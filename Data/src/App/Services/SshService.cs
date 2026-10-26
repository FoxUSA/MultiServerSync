using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MultiServerSync.Core;
using MultiServerSync.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MultiServerSync.Services;

/// <param name="Permissions">Права строкой, как в ls и WinSCP: rwxr-xr-x.</param>
/// <param name="IsLink">Символическая ссылка. IsDirectory у неё — про то, куда она указывает.</param>
/// <param name="Owner">Владелец именем; где имени нет — числом.</param>
/// <param name="Mode">Права числом, как в chmod, вместе с setuid/setgid/sticky; -1 — неизвестно.</param>
/// <param name="LinkTarget">Куда ведёт ссылка — как записано в ней самой.</param>
public sealed record RemoteEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime Modified,
    string Permissions = "",
    bool IsLink = false,
    string Owner = "",
    string Group = "",
    int Mode = -1,
    string LinkTarget = "");

/// <param name="MatchByDate">
/// Заменять файлы с другой датой изменения: расхождение в любую сторону — повод отправить файл
/// заново. Размер сверяется всегда, независимо от этого флага: он ловит обрывы связи даром.
/// </param>
/// <param name="MatchByHash">
/// Заменять файлы с другой контрольной суммой: перед копированием на сервере считается
/// sha256sum по всем файлам назначения, и решение принимается по содержимому, а не по дате.
/// </param>
public sealed record UploadOptions(
    bool Overwrite,
    bool PreserveTimestamps,
    bool MatchByDate,
    bool MatchByHash,
    string ExcludeMasks = "")
{
    /// <summary>Надо ли вообще решать, отправлять файл: иначе он отправляется всегда.</summary>
    public bool Matching => MatchByDate || MatchByHash;
}

public sealed record UploadedFile(string LocalPath, string RemotePath, long Size);

/// <summary>Что делать с файлами, которые копирование перезапишет в назначении.</summary>
public enum ReplaceChoice
{
    Replace,
    Skip,
    Cancel,
}

/// <summary>
/// Вопрос перед заменой. Зовётся из потока копирования, когда уже ясно, какие файлы назначения
/// будут перезаписаны (с учётом сверки по дате или сумме), и до того, как что-то поехало.
/// </summary>
public delegate ReplaceChoice ConfirmReplace(ServerEntry server, IReadOnlyList<string> paths);

/// <summary>
/// Чем копирование занято прямо сейчас — это и пишется на ярлыке вкладки и в строке
/// сервера. Порядок значений — порядок шагов, назад они не идут.
/// </summary>
public enum UploadStep
{
    Connecting = 1,

    /// <summary>Сервер считает суммы файлов назначения — одной пачкой, до копирования.</summary>
    CheckingHashes = 2,

    /// <summary>Сверяются даты и размеры: решается, какие файлы вообще надо отправлять.</summary>
    CheckingDates = 3,

    Copying = 4,
}

public sealed record UploadStats(
    int FilesSent,
    int FilesSkipped,
    int FilesUpToDate,
    int FilesRedated,
    int DirsCreated,
    long BytesSent,
    IReadOnlyList<UploadedFile> Files,
    IReadOnlyList<string> CreatedDirs,
    StageClock Clock);

/// <summary>Время этапов копирования — строкой в вывод сервера: видно, на что уходят секунды.</summary>
public sealed class StageClock
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly List<(string Stage, TimeSpan Time)> _stages = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Закрывает этап: ему уходит время с прошлой отметки. Одноимённые этапы складываются.</summary>
    public void Mark(string stage)
    {
        var spent = _clock.Elapsed;
        _clock.Restart();

        var i = _stages.FindIndex(s => s.Stage == stage);

        if (i >= 0)
        {
            _stages[i] = (stage, _stages[i].Time + spent);
        }
        else
        {
            _stages.Add((stage, spent));
        }
    }

    /// <summary>Отсчёт заново — после паузы, которая ни к одному этапу не относится.</summary>
    public void Restart() => _clock.Restart();

    public override string ToString()
    {
        var total = _stages.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Time);

        // доли секунды — шум, они только удлиняют строку
        return string.Join(
            " · ",
            _stages
                .Where(s => s.Time.TotalSeconds >= 0.05)
                .Select(s => $"{s.Stage} {Seconds(s.Time)}")
                .Append($"всего {Seconds(total)}"));
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", Ru) + " с";
}

/// <summary>Ответ сервера на команду целиком: оба потока вывода и чем всё закончилось.</summary>
public sealed record CommandOutcome(int? ExitCode, string StdOut, string StdErr, bool TimedOut, string? ExitSignal)
{
    /// <summary>
    /// Почему команду не удалось выполнить — человеческим языком. Пусто, если команда
    /// доехала до сервера и отработала: что она при этом написала в вывод и с каким
    /// итогом завершилась — дело самой команды, а не программы.
    /// </summary>
    public string? Interruption
    {
        get
        {
            if (TimedOut)
            {
                return "команда не успела завершиться — ждать перестали";
            }

            if (!string.IsNullOrWhiteSpace(ExitSignal))
            {
                return $"команду оборвало на сервере (сигнал {ExitSignal})";
            }

            return null;
        }
    }
}

/// <summary>Файл ключа не найден или не читается — текст уже объясняет, что делать.</summary>
public sealed class SshKeyException(string message) : Exception(message);

/// <summary>Работа с сервером по SSH и SFTP. Все вызовы синхронной библиотеки уводятся в пул потоков.</summary>
public static class SshService
{
    /// <summary>
    /// Клиент SSH с проверкой ключа сервера — все подключения создаются так.
    /// Через промежуточный сервер — по туннелю, который живёт, пока жив клиент.
    /// </summary>
    public static SshClient NewSsh(ServerEntry server, AppSettings settings)
    {
        if (Jump.Open(server, settings) is not { } tunnel)
        {
            return HostKeys.Watch(new SshClient(BuildConnectionInfo(server, settings)), server);
        }

        // ключ не прочитался — туннель уже открыт и сам не закроется
        try
        {
            return HostKeys.Watch(new TunneledSshClient(BuildConnectionInfo(server, settings, "127.0.0.1", tunnel.Port), tunnel), server);
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
    }

    /// <summary>Клиент SFTP с проверкой ключа сервера.</summary>
    public static SftpClient NewSftp(ServerEntry server, AppSettings settings)
    {
        if (Jump.Open(server, settings) is not { } tunnel)
        {
            return HostKeys.Watch(new SftpClient(BuildConnectionInfo(server, settings)), server);
        }

        try
        {
            return HostKeys.Watch(new TunneledSftpClient(BuildConnectionInfo(server, settings, "127.0.0.1", tunnel.Port), tunnel), server);
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
    }

    public static ConnectionInfo BuildConnectionInfo(ServerEntry server, AppSettings settings) =>
        BuildConnectionInfo(server, settings, server.Host.Trim(), server.Port <= 0 ? 22 : server.Port);

    /// <summary>То же, но подключаться по другому адресу — к концу туннеля через промежуточный сервер.</summary>
    public static ConnectionInfo BuildConnectionInfo(ServerEntry server, AppSettings settings, string host, int port)
    {
        var user = string.IsNullOrWhiteSpace(server.User) ? "root" : server.User.Trim();
        var password = server.Password ?? string.Empty;

        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = password;
            }
        };

        // ключ первым, как у ssh; пароль — запасным путём, если он есть.
        // Ни ключа, ни пароля — пробуем стандартные ключи из %USERPROFILE%\.ssh, тоже как ssh
        var keys = LoadKeys(server);
        var methods = new List<AuthenticationMethod>();

        if (keys.Count > 0)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(user, [.. keys]));
        }

        if (password.Length > 0 || keys.Count == 0)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            methods.Add(keyboard);
        }

        var info = new ConnectionInfo(
            host,
            port,
            user,
            [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.ConnectTimeoutSeconds)),
            Encoding = Encoding.UTF8,
        };

        return info;
    }

    /// <summary>Стандартные имена ключей в %USERPROFILE%\.ssh — в том порядке, в каком их пробует ssh.</summary>
    private static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    /// <summary>Папка ключей пользователя — с неё открывается выбор файла ключа.</summary>
    public static string UserSshFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>
    /// Ключи для входа. Указанный ключ, который не читается, — ошибка с объяснением: молча
    /// уйти на пароль значило бы получить «сервер отклонил вход» и гадать почему.
    /// Стандартные ключи без пароля к ним пробуются только когда больше пробовать нечего.
    /// </summary>
    private static List<IPrivateKeySource> LoadKeys(ServerEntry server)
    {
        if (server.HasKey)
        {
            return [OpenKey(server.KeyPath, server.KeyPassphrase)];
        }

        if (!string.IsNullOrEmpty(server.Password))
        {
            return [];
        }

        var found = new List<IPrivateKeySource>();

        foreach (var name in DefaultKeyNames)
        {
            var path = Path.Combine(UserSshFolder, name);

            try
            {
                if (File.Exists(path))
                {
                    found.Add(new PrivateKeyFile(path));
                }
            }
            catch (Exception)
            {
                // защищён паролем или чужой формат — стандартный ключ просто пропускаем
            }
        }

        return found;
    }

    /// <summary>Открывает файл ключа. Не вышло — <see cref="SshKeyException"/> с понятной причиной.</summary>
    public static PrivateKeyFile OpenKey(string keyPath, string? passphrase)
    {
        var path = Environment.ExpandEnvironmentVariables(keyPath.Trim().Trim('"'));

        if (!File.Exists(path))
        {
            throw new SshKeyException($"Файл ключа не найден: {path}");
        }

        try
        {
            return new PrivateKeyFile(path, string.IsNullOrEmpty(passphrase) ? null : passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            throw new SshKeyException("Ключ защищён паролем — укажите пароль ключа в настройках сервера.");
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException or FormatException
                                       or System.Security.Cryptography.CryptographicException)
        {
            throw new SshKeyException(string.IsNullOrEmpty(passphrase)
                ? $"Ключ не читается: это не закрытый ключ SSH или формат не поддерживается ({ex.Message})."
                : $"Ключ не читается: неверный пароль ключа или формат не поддерживается ({ex.Message}).");
        }
    }

    /// <summary>
    /// Дополняется PATH: при входе без терминала он часто урезан, и тогда
    /// systemctl, service, ip и прочее из /sbin «не найдены», хотя на сервере есть.
    /// </summary>
    private const string Prologue =
        "export PATH=\"$PATH:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\"\n"
        // чем идёт команда на сервере: по этим номерам ей потом посылается Ctrl+C.
        // Строка служебная — из вывода она вырезается и до пользователя не доходит
        + "__mss_pgid=$(ps -o pgid= -p $$ 2>/dev/null | tr -d ' ')\n"
        + "echo \"" + JobMarker + "$$ ${__mss_pgid:-$$}\"\n";

    /// <summary>Метка служебной строки пролога: номер процесса команды и номер его группы.</summary>
    internal const string JobMarker = "MSS-JOB ";

    /// <summary>Одна команда без sudo — для проверки связи и служебных вызовов.</summary>
    public static Task<CommandOutcome> RunCommandAsync(
        ServerEntry server,
        AppSettings settings,
        string command,
        CancellationToken ct) =>
        RunScriptAsync(server, settings, command, sudo: false, live: null, ct);

    /// <summary>
    /// Выполняет команду или целый скрипт.
    ///
    /// Текст уходит на сервер через stdin интерпретатора (<c>bash -s</c>), а не командной строкой:
    /// длина ничем не ограничена — можно вставлять портянки любого размера, — и не нужно
    /// экранировать кавычки и переводы строк.
    ///
    /// Оба потока (обычный вывод и вывод ошибок) читаются целиком и параллельно, построчно,
    /// по мере поступления: вывод виден сразу и ничего не теряется, даже если команда много
    /// пишет в оба. Строки идут как есть, без пометок — ровно то же, что видно в терминале.
    /// </summary>
    public static Task<CommandOutcome> RunScriptAsync(
        ServerEntry server,
        AppSettings settings,
        string script,
        bool sudo,
        Action<string>? live,
        CancellationToken ct,
        Action<int, int>? onJob = null,
        Action<CommandInput?>? onInput = null,
        Action<bool>? onWaiting = null,
        Action<string>? onTail = null)
    {
        return Task.Run(
            () =>
            {
                using var client = NewSsh(server, settings);
                client.Connect();

                // Скрипт идёт через stdin, но сервер забирает ровно его байты во временный файл
                // и запускает уже оттуда — ввод после этого свободен для ответов команде
                // ([Y/n], «продолжить?»). Временный файл удаляется сразу после открытия.
                //
                // Перед скриптом — метка: sudo -S читает пароль первой строкой, а если пароль
                // не спросил (NOPASSWD), строка с паролем просто пропускается до метки
                // и в скрипт не попадает.
                var payload = new UTF8Encoding(false).GetBytes(
                    Prologue + script.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

                var marker = "MSS-BEGIN-" + Guid.NewGuid().ToString("N");

                var loader =
                    "while IFS= read -r __mss_l; do [ \"$__mss_l\" = " + marker + " ] && break; done; "
                    + "__mss_f=$(mktemp) || exit 1; "
                    + "head -c " + payload.Length + " > \"$__mss_f\" || exit 1; "
                    + "exec 3<\"$__mss_f\"; rm -f \"$__mss_f\"; exec bash /dev/fd/3";

                // -p '' убирает приглашение sudo, чтобы оно не попало в вывод
                var runner = (sudo ? "sudo -S -p '' -- " : string.Empty) + "bash -c " + ShellQuote(loader);

                using var cmd = client.CreateCommand(runner);

                // ждём завершения сами, чтобы после таймаута успеть дочитать уже пришедший вывод
                cmd.CommandTimeout = Timeout.InfiniteTimeSpan;

                var timeout = TimeSpan.FromSeconds(Math.Max(5, settings.CommandTimeoutSeconds));
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var timedOut = false;

                var outPump = new LinePump(stdout, live, onJob, onTail);
                var errPump = new LinePump(stderr, live, null, onTail);

                var async = cmd.BeginExecute();
                using var registration = ct.Register(() => TryCancel(cmd));

                // читать оба потока надо сразу: иначе сервер упрётся в переполненное окно и встанет
                var outReader = Task.Run(() => outPump.Run(cmd.OutputStream), CancellationToken.None);
                var errReader = Task.Run(() => errPump.Run(cmd.ExtendedOutputStream), CancellationToken.None);

                var stream = cmd.CreateInputStream();
                CommandInput? input = null;

                try
                {
                    var head = new StringBuilder();

                    if (sudo)
                    {
                        head.Append(server.Password ?? string.Empty).Append('\n');
                    }

                    head.Append(marker).Append('\n');

                    stream.Write(new UTF8Encoding(false).GetBytes(head.ToString()));
                    stream.Write(payload);
                    stream.Flush();

                    if (onInput is null)
                    {
                        // служебным командам отвечать некому: ввод закрыт, как раньше
                        stream.Dispose();
                    }
                    else
                    {
                        input = new CommandInput(stream, outPump, errPump);
                        onInput(input);
                    }

                    // Время ожидания ответа человеком в таймаут не идёт: команда не зависла,
                    // она ждёт, пока ей ответят.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var paused = TimeSpan.Zero;
                    var waiting = false;
                    var tick = TimeSpan.FromMilliseconds(250);

                    while (!async.AsyncWaitHandle.WaitOne(tick))
                    {
                        outPump.Tick();
                        errPump.Tick();

                        var nowWaiting = input is { IsOpen: true } && (outPump.Waiting || errPump.Waiting);

                        if (nowWaiting != waiting)
                        {
                            waiting = nowWaiting;
                            onWaiting?.Invoke(waiting);
                        }

                        if (waiting)
                        {
                            paused += tick;
                        }

                        if (clock.Elapsed - paused > timeout)
                        {
                            timedOut = true;
                            TryCancel(cmd);
                            async.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                            break;
                        }
                    }

                    if (waiting)
                    {
                        onWaiting?.Invoke(false);
                    }
                }
                finally
                {
                    input?.Close();

                    if (onInput is not null)
                    {
                        onInput(null);
                    }
                }

                // хвост вывода дочитываем уже после завершения команды
                Task.WaitAll([outReader, errReader], TimeSpan.FromSeconds(20));

                // EndExecute намеренно не вызывается: он ещё раз вычитывает поток вывода,
                // а мы уже прочитали его сами. Код возврата приходит от сервера отдельно
                // и доступен сразу, как канал закрылся.
                ct.ThrowIfCancellationRequested();

                client.Disconnect();

                return new CommandOutcome(
                    cmd.ExitStatus,
                    Read(stdout),
                    Read(stderr),
                    timedOut,
                    cmd.ExitSignal);
            },
            ct);
    }

    /// <summary>
    /// Прерывает команду на сервере: сигнал уходит всей группе её процессов отдельным
    /// подключением. В канал самой команды «нажать Ctrl+C» нельзя — её стандартный ввод
    /// закрыт сразу после передачи скрипта, да и без терминала 0x03 не сигнал, а обычный
    /// байт, поэтому прерывание делается так же, как со второй консоли: kill по группе.
    /// </summary>
    /// <param name="signal">INT — то же, что Ctrl+C; TERM — просьба завершиться; KILL — снятие.</param>
    /// <param name="sudo">Команда шла через sudo: её процессы под root, kill тоже нужен через sudo.</param>
    public static async Task<bool> SignalJobAsync(
        ServerEntry server,
        AppSettings settings,
        int pid,
        int group,
        string signal,
        bool sudo)
    {
        // сначала всей группе — вместе с потомками команды; если группы нет, хотя бы самому процессу
        var script = $"kill -s {signal} -- -{group} 2>/dev/null || kill -s {signal} -- {pid}";

        var outcome = await RunScriptAsync(server, settings, script, sudo, live: null, CancellationToken.None);

        return outcome.ExitCode == 0;
    }

    /// <summary>Разбирает служебную строку: номер процесса команды и номер его группы.</summary>
    internal static void ReportJob(string line, Action<int, int>? onJob)
    {
        if (onJob is null)
        {
            return;
        }

        var parts = line[JobMarker.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var group))
        {
            onJob(pid, group);
        }
        else if (parts.Length == 1 && int.TryParse(parts[0], out var only))
        {
            onJob(only, only);
        }
    }

    private static string Read(StringBuilder sink)
    {
        lock (sink)
        {
            return sink.ToString();
        }
    }

    private static void TryCancel(SshCommand cmd)
    {
        try
        {
            cmd.CancelAsync(true, 500);
        }
        catch
        {
            // команда уже завершилась
        }
    }

    /// <summary>
    /// Сколько файлов одного сервера идут одновременно. Каждый запрос SFTP ждёт ответа,
    /// и на тысячах мелких файлов время уходило на ожидание, а не на данные. Подключение
    /// одно: запросы разных файлов идут по нему вперемешку, каждый со своим номером.
    /// </summary>
    private const int FileWorkers = 8;

    static SshService()
    {
        // каждый поток отправки ждёт сеть синхронно: восемь серверов по восемь потоков —
        // пул по умолчанию набирал бы их по одному в полсекунды, и разгон съедал бы выигрыш
        ThreadPool.GetMinThreads(out var workers, out var io);

        if (workers < 96)
        {
            ThreadPool.SetMinThreads(96, io);
        }
    }

    public static Task<UploadStats> UploadAsync(
        ServerEntry server,
        AppSettings settings,
        IReadOnlyList<TransferItem> items,
        string remoteRoot,
        UploadOptions options,
        ConcurrentDictionary<string, string> localHashes,
        Action<string> report,
        CancellationToken ct,
        Action<UploadStep>? step = null,
        Action<long, long>? progress = null,
        ConfirmReplace? confirmReplace = null)
    {
        return Task.Run(
            () =>
            {
                var stats = new Counters();
                var clock = new StageClock();
                var meter = new TransferMeter(progress, ct);

                step?.Invoke(UploadStep.Connecting);

                var root = NormalizeRemote(remoteRoot);
                var plan = BuildPlan(items, root, ExcludeRules.Parse(options.ExcludeMasks));

                if (plan.Excluded > 0)
                {
                    report($"исключено по маскам: {plan.Excluded}");
                }

                using var ssh = NewSsh(server, settings);
                using var sftp = NewSftp(server, settings);
                ConnectBoth(ssh, sftp);
                clock.Mark("подключение");

                if (options.MatchByDate && !options.MatchByHash)
                {
                    step?.Invoke(UploadStep.CheckingDates);
                }

                // что уже лежит на сервере — одной командой на всё сразу, а не запросом на файл:
                // на десятке тысяч файлов это секунды вместо минут ожидания ответов
                var inventory = TakeInventory(ssh, plan, report, ct);
                var ctx = new UploadContext(sftp, options, localHashes, inventory, stats, report) { Meter = meter };
                clock.Mark("опись");

                if (options.MatchByHash && options.Overwrite)
                {
                    ctx.RemoteHashes = FetchRemoteHashes(ssh, ctx, plan.Files, report, ct, step);
                    clock.Mark("суммы до");
                }

                // сначала решаем по каждому файлу, потом везём: так видно, сколько и чего
                // отправлять, и под это выбирается способ доставки. Решение — до создания папок:
                // если замену не подтвердят, на сервере не должно остаться ничего нового
                var redate = new ConcurrentBag<Retouch>();
                var picked = new ConcurrentBag<SendJob>();

                RunParallel(plan.Files, ct, job =>
                {
                    if (Decide(ctx, job, redate) is { } s)
                    {
                        picked.Add(s);
                    }
                });

                clock.Mark("разбор");

                var send = picked.ToList();
                var replacing = send.Where(s => s.Remote is not null).Select(s => s.Job.RemotePath).Order(StringComparer.Ordinal).ToList();

                if (confirmReplace is not null && replacing.Count > 0)
                {
                    switch (confirmReplace(server, replacing))
                    {
                        case ReplaceChoice.Cancel:
                            report("копирование отменено: замену файлов не подтвердили");
                            throw new OperationCanceledException();

                        case ReplaceChoice.Skip:
                            send.RemoveAll(s => s.Remote is not null);
                            stats.Skipped += replacing.Count;
                            report($"не заменены, оставлены как есть: {replacing.Count}");
                            break;
                    }
                }

                EnsureDirectory(sftp, root, stats, report);
                ctx.MarkKnown(root);

                // папки — заранее и по порядку, родитель раньше детей: потоки отправки
                // потом только кладут файлы и не спорят, кому создавать общую папку.
                // Новые — одной командой на все, по одной остаются только те, что так не вышли
                MakeDirs(ssh, ctx, plan.Dirs, report, ct);

                foreach (var dir in plan.Dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    ctx.EnsureDir(dir.RemotePath);
                }

                clock.Mark("папки");
                ApplyDates(ssh, sftp, redate.ToList(), report, ct);
                clock.Mark("даты");

                var dates = new List<Retouch>();

                if (send.Count > 0)
                {
                    step?.Invoke(UploadStep.Copying);

                    var rootStamp = TryGetAttributes(sftp, root)?.LastWriteTimeUtc;
                    var jobs = send.OrderBy(s => s.Job.RemotePath, StringComparer.Ordinal).ToList();

                    meter.Total = jobs.Sum(s => s.Info.Length);
                    meter.Finish(); // 0 из N — полоса появляется сразу, а не с первым куском

                    // временная папка пачки лежит в корне назначения и сбивает ему дату —
                    // возвращаем прежнюю, если ничего своего в корень не легло
                    if (Send(ssh, ctx, jobs, server, settings, root, ct)
                        && rootStamp is { } stamp
                        && !jobs.Any(s => ParentRemote(s.Job.RemotePath) == root)
                        && !plan.Dirs.Any(d => ParentRemote(d.RemotePath) == root && ctx.Known(d.RemotePath) is null))
                    {
                        dates.Add(new Retouch(root, stamp));
                    }

                    meter.Finish();
                    clock.Mark("отправка");
                }

                // время папки правится последним: запись файлов внутрь всё равно его сбивает.
                // Папку, в которой ничего не менялось и дата которой и так совпадает, не трогаем
                if (options.PreserveTimestamps)
                {
                    foreach (var dir in plan.Dirs)
                    {
                        var info = new DirectoryInfo(dir.LocalPath);

                        if (!ctx.IsTouched(dir.RemotePath)
                            && ctx.Known(dir.RemotePath) is { IsDirectory: true } known
                            && Math.Abs((info.LastWriteTimeUtc - known.WriteUtc).TotalSeconds) <= DateTolerance)
                        {
                            continue;
                        }

                        dates.Add(new Retouch(dir.RemotePath, info.LastWriteTimeUtc));
                    }
                }

                ApplyDates(ssh, sftp, dates, report, ct);
                clock.Mark("даты");

                ssh.Disconnect();
                sftp.Disconnect();

                // потоки заканчивают вразнобой — список отправленного выводим по алфавиту
                var uploaded = stats.Uploaded.OrderBy(f => f.RemotePath, StringComparer.Ordinal).ToList();

                return new UploadStats(
                    stats.Files, stats.Skipped, stats.UpToDate, stats.Redated, stats.Dirs, stats.Bytes, uploaded,
                    stats.CreatedDirs.Order(StringComparer.Ordinal).ToList(), clock);
            },
            ct);
    }

    /// <summary>
    /// Параллельный обход. Сбой одного файла останавливает остальные и выходит наружу
    /// сам по себе, а не обёрткой «произошла одна или несколько ошибок»: строке сервера
    /// нужна настоящая причина.
    /// </summary>
    private static void RunParallel<T>(IEnumerable<T> source, CancellationToken ct, Action<T> body, int workers = FileWorkers)
    {
        try
        {
            Parallel.ForEach(
                source,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = ct },
                body);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            ThrowFirst(ae);
        }
    }

    /// <summary>
    /// Два входа на сервер — одновременно: каждый ждёт своего обмена ключами,
    /// и по очереди подключение шло вдвое дольше.
    /// </summary>
    internal static void ConnectBoth(SshClient ssh, SftpClient sftp)
    {
        try
        {
            Parallel.Invoke(ssh.Connect, sftp.Connect);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            ThrowFirst(ae);
        }
    }

    /// <summary>Наружу — настоящая причина сбоя, а не обёртка «одна или несколько ошибок».</summary>
    private static void ThrowFirst(AggregateException ae)
    {
        var all = ae.Flatten().InnerExceptions;
        var first = all.FirstOrDefault(e => e is not OperationCanceledException) ?? all[0];

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    /// <summary>
    /// Папка одной командой find: её содержимое (тип, куда ведёт ссылка, размер, дата, права)
    /// и заодно — у каких подпапок внутри есть свои папки, чтобы дерево не рисовало ложных стрелок.
    /// По SFTP на то же уходил запрос на папку и ещё по запросу на каждую ссылку.
    /// null — на сервере find без -printf (урезанный busybox): читать по SFTP.
    /// </summary>
    internal static (IReadOnlyList<RemoteEntry> Items, HashSet<string> WithSubdirs)? ReadDirectoryFast(SshClient ssh, string path)
    {
        var target = NormalizeRemote(path);
        var q = ShellQuote(target);

        // метки в stderr вместо текста ошибок: он бывает на языке сервера
        var script =
            "export LC_ALL=C\n"
            + $"[ -e {q} ] || {{ echo MSS:NOENT >&2; exit 3; }}\n"
            + $"[ -r {q} ] && [ -x {q} ] || {{ echo MSS:EACCES >&2; exit 4; }}\n"
            + $"cd {q} || exit 4\n"
            + "find . -mindepth 1 -maxdepth 1 -printf '1\\t" + EntryFormat + "\\t%P\\0'\n"
            + "find -L . -mindepth 2 -maxdepth 2 -type d -printf '2\\t%h\\0' 2>/dev/null\n";

        var (output, error) = RunInput(ssh, script, CancellationToken.None);

        if (error.Contains("MSS:NOENT", StringComparison.Ordinal))
        {
            throw new SftpPathNotFoundException($"Папки {target} нет на сервере.");
        }

        if (error.Contains("MSS:EACCES", StringComparison.Ordinal))
        {
            throw new SftpPermissionDeniedException($"Нет прав на чтение {target}.");
        }

        // find ругнулся и ничего не вывел — это не тот find
        if (output.Length == 0 && error.Trim().Length > 0)
        {
            return null;
        }

        var items = new List<RemoteEntry>();
        var withSubdirs = new HashSet<string>(StringComparer.Ordinal);
        var prefix = target == "/" ? "/" : target + "/";

        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.StartsWith("2\t", StringComparison.Ordinal))
            {
                // «./имя» — у этой подпапки есть свои папки
                var parent = record[2..];
                withSubdirs.Add(parent.StartsWith("./", StringComparison.Ordinal) ? parent[2..] : parent);
                continue;
            }

            if (record.Length < 2 || record[0] != '1' || record[1] != '\t'
                || ParseEntry(record[2..], prefix) is not { } entry)
            {
                continue;
            }

            items.Add(entry);
        }

        return (items, withSubdirs);
    }

    /// <summary>
    /// Поля записи find о файле: тип, тип цели ссылки, размер, дата, права строкой, владелец,
    /// группа, права числом, цель ссылки. Имя всегда последним — в нём может быть что угодно.
    /// </summary>
    private const string EntryFormat = "%y\\t%Y\\t%s\\t%T@\\t%M\\t%u\\t%g\\t%m\\t%l";

    /// <summary>Разбирает запись <see cref="EntryFormat"/> + «\t имя». null — запись битая.</summary>
    private static RemoteEntry? ParseEntry(string record, string prefix)
    {
        var parts = record.Split('\t', 10);

        if (parts.Length < 10)
        {
            return null;
        }

        var isLink = parts[0] == "l";
        var name = parts[9];

        return new RemoteEntry(
            name,
            prefix + name,
            parts[0] == "d" || (isLink && parts[1] == "d"),
            long.TryParse(parts[2], out var size) ? size : 0,
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(time * 1000)).LocalDateTime
                : default,
            parts[4].Length == 10 ? parts[4][1..] : parts[4],
            isLink,
            parts[5],
            parts[6],
            TryOctal(parts[7]),
            parts[8]);
    }

    /// <summary>
    /// Несколько папок одной командой — для чтения на уровень вперёд: зашли в папку, и все её
    /// подпапки уже лежат в кэше, щелчок по любой показывает её сразу. В ответе только те папки,
    /// которые удалось прочитать: непрочитанная пустой не притворяется. Записей больше
    /// <paramref name="limit"/> — остальное не разбирается.
    /// </summary>
    internal static Dictionary<string, List<RemoteEntry>> ReadDirectoriesFast(
        SshClient ssh,
        IReadOnlyList<string> paths,
        int limit,
        CancellationToken ct)
    {
        var result = new Dictionary<string, List<RemoteEntry>>(StringComparer.Ordinal);

        if (paths.Count == 0)
        {
            return result;
        }

        var quoted = string.Join(" ", paths.Select(p => ShellQuote(NormalizeRemote(p))));

        // метка R — папку можно читать: только такие встают в кэш, даже если они пустые
        var script =
            "export LC_ALL=C\n"
            + $"for d in {quoted}; do [ -r \"$d\" ] && [ -x \"$d\" ] && printf 'R\\t%s\\0' \"$d\"; done\n"
            + $"find {quoted} -mindepth 1 -maxdepth 1 -printf '1\\t%H\\t" + EntryFormat + "\\t%f\\0' 2>/dev/null"
            + $" | head -c {64L * 1024 * 1024}\n";

        var (output, _) = RunInput(ssh, script, ct);
        var count = 0;

        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.StartsWith("R\t", StringComparison.Ordinal))
            {
                result.TryAdd(record[2..], []);
                continue;
            }

            if (!record.StartsWith("1\t", StringComparison.Ordinal))
            {
                continue;
            }

            var tab = record.IndexOf('\t', 2);

            if (tab < 0)
            {
                continue;
            }

            var dir = record[2..tab];

            if (!result.TryGetValue(dir, out var list)
                || ParseEntry(record[(tab + 1)..], dir == "/" ? "/" : dir + "/") is not { } entry)
            {
                continue;
            }

            list.Add(entry);

            if (++count >= limit)
            {
                break;
            }
        }

        // разбор оборвался на пределе — папки, дочитанные не до конца, в кэш не кладём
        if (count >= limit)
        {
            return new Dictionary<string, List<RemoteEntry>>(StringComparer.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Читает папку на уже открытом подключении. Ссылки на папки (в /var/www их полно)
    /// раскрываются: без этого в них нельзя было бы зайти.
    /// </summary>
    public static IReadOnlyList<RemoteEntry> ReadDirectory(SftpClient sftp, string path)
    {
        var target = NormalizeRemote(path);
        var files = sftp.ListDirectory(target).Where(f => f.Name is not ("." or "..")).ToList();

        // куда ведёт ссылка — отдельный запрос на каждую; в /etc и /usr/lib их сотни, и по одной
        // папка открывалась секундами. Запросы на одном подключении идут параллельно
        var linkIsDir = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();

        Parallel.ForEach(
            files.Where(f => f.IsSymbolicLink),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            file =>
            {
                try
                {
                    linkIsDir[file.FullName] = sftp.GetAttributes(file.FullName).IsDirectory;
                }
                catch (SshException)
                {
                    // битая ссылка — показываем как файл
                }
            });

        return files
            .Select(file => new RemoteEntry(
                file.Name,
                NormalizeRemote(file.FullName),
                file.IsSymbolicLink ? linkIsDir.GetValueOrDefault(file.FullName) : file.IsDirectory,
                file.Length,
                file.LastWriteTime,
                Permissions(file),
                file.IsSymbolicLink,
                file.Attributes.UserId.ToString(CultureInfo.InvariantCulture),
                file.Attributes.GroupId.ToString(CultureInfo.InvariantCulture),
                ModeOf(file.Attributes)))
            .ToList();
    }

    /// <summary>
    /// Пустой файл с правами и владельцем папки, в которую он ложится, — без права запуска,
    /// как у новых файлов при заливке. Файл с таким именем уже есть — ошибка, ничего не трогаем.
    /// </summary>
    public static void CreateEmptyFile(SftpClient sftp, string path, Action<string> report)
    {
        var target = NormalizeRemote(path);

        if (sftp.Exists(target))
        {
            throw new IOException($"{Path.GetFileName(target)} уже есть в этой папке.");
        }

        var parent = TryGetAttributes(sftp, ParentRemote(target));

        using (sftp.Create(target))
        {
            // пустой — писать нечего
        }

        if (parent is null)
        {
            return;
        }

        try
        {
            var attrs = sftp.GetAttributes(target);
            CopyPermissions(parent, attrs);
            attrs.OwnerCanExecute = false;
            attrs.GroupCanExecute = false;
            attrs.OthersCanExecute = false;
            sftp.SetAttributes(target, attrs);
        }
        catch (Exception ex)
        {
            report($"не удалось перенести права на {target}: {ex.Message}");
        }

        CopyOwner(sftp, target, parent, report);
    }

    /// <summary>Создаёт папку (и недостающих родителей) на уже открытом подключении.</summary>
    public static void CreateDirectory(SftpClient sftp, string path) =>
        EnsureDirectory(sftp, NormalizeRemote(path), new Counters(), _ => { });

    private static string Permissions(ISftpFile file)
    {
        var bits = new[]
        {
            file.OwnerCanRead, file.OwnerCanWrite, file.OwnerCanExecute,
            file.GroupCanRead, file.GroupCanWrite, file.GroupCanExecute,
            file.OthersCanRead, file.OthersCanWrite, file.OthersCanExecute,
        };

        var text = new StringBuilder(9);

        for (var i = 0; i < bits.Length; i++)
        {
            text.Append(bits[i] ? "rwx"[i % 3] : '-');
        }

        return text.ToString();
    }

    public static Task CreateRemoteDirectoryAsync(
        ServerEntry server,
        AppSettings settings,
        string path,
        CancellationToken ct)
    {
        return Task.Run(
            () =>
            {
                using var sftp = NewSftp(server, settings);
                sftp.Connect();
                EnsureDirectory(sftp, NormalizeRemote(path), new Counters(), _ => { });
                sftp.Disconnect();
            },
            ct);
    }

    /// <summary>
    /// Спрашивает у сервера, какие способы аутентификации он разрешает.
    /// Нужно, когда пароль отклонён: сразу видно, разрешён ли вообще вход по паролю.
    /// </summary>
    public static Task<IReadOnlyList<string>> ProbeAllowedAuthenticationsAsync(
        ServerEntry server,
        AppSettings settings,
        CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<string>>(
            () =>
            {
                var user = string.IsNullOrWhiteSpace(server.User) ? "root" : server.User.Trim();
                var none = new NoneAuthenticationMethod(user);

                using var tunnel = Jump.Open(server, settings);
                var (host, port) = tunnel is null
                    ? (server.Host.Trim(), server.Port <= 0 ? 22 : server.Port)
                    : ("127.0.0.1", tunnel.Port);

                var info = new ConnectionInfo(host, port, user, none)
                {
                    Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.ConnectTimeoutSeconds)),
                };

                using var client = HostKeys.Watch(new SshClient(info), server);

                try
                {
                    client.Connect();
                    client.Disconnect();
                }
                catch (SshAuthenticationException)
                {
                    // ожидаемо: способ «none» сервер не примет, но список разрешённых уже получен
                }

                return none.AllowedAuthentications?.ToList() ?? [];
            },
            ct);
    }

    /// <summary>Та же проверка, но без исключений — для показа подсказки после неудачного входа.</summary>
    public static async Task<IReadOnlyList<string>> TryProbeAuthMethodsAsync(ServerEntry server, AppSettings settings)
    {
        try
        {
            return await ProbeAllowedAuthenticationsAsync(server, settings, CancellationToken.None);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Человеческое объяснение сбоя вместо сухого текста библиотеки.</summary>
    public static string Describe(Exception ex, ServerEntry server)
    {
        // подключение оборвали мы сами: сервер показал не тот ключ, что запомнен
        if (ex is SshException && HostKeys.Mismatch(server) is { } fresh)
        {
            return $"Ключ сервера изменился: запомнен {HostKeys.Get(server)?.Fingerprint}, сейчас {fresh.Fingerprint}. "
                + "Вход не выполнялся. Если сервер переустанавливали — примите новый ключ в окне сервера, "
                + "иначе кто-то подменяет сервер.";
        }

        switch (ex)
        {
            case SshKeyException:
                return ex.Message;

            case SshAuthenticationException when server.HasKey:
                return $"Сервер не принял ключ для пользователя «{server.User}». "
                    + "Открытая часть ключа (.pub) должна быть в ~/.ssh/authorized_keys этого пользователя на сервере"
                    + (string.IsNullOrEmpty(server.Password) ? "." : "; пароль сервер тоже не принял.");

            case SshAuthenticationException:
                return $"Сервер отклонил вход для пользователя «{server.User}». "
                    + "Проверьте логин и пароль. Если они верны — на сервере может быть выключен вход по паролю "
                    + "(в /etc/ssh/sshd_config: PasswordAuthentication no) или запрещён вход root "
                    + "(PermitRootLogin prohibit-password) — тогда нужен вход по ключу или другой пользователь.";

            case SocketException socket:
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused =>
                        $"Порт {server.Port} на {server.Host} закрыт: SSH-сервер не слушает этот порт.",
                    SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                        $"{server.Host}:{server.Port} не отвечает: хост недоступен или закрыт фаерволом.",
                    SocketError.HostNotFound =>
                        $"Имя «{server.Host}» не разрешается в адрес (DNS).",
                    _ => $"Сеть: {socket.Message}",
                };

            case SshOperationTimeoutException:
                return $"Истёк таймаут подключения к {server.Host}:{server.Port}. "
                    + "Увеличьте таймаут в настройках или проверьте доступность хоста.";

            // соединение открылось, но SSH-приветствия не пришло. Так бывает, когда на компьютере
            // VPN или прокси в режиме туннеля (Clash, mihomo и т. п.) сам принимает соединение
            // и не пропускает его дальше, — тогда так отвечают сразу все серверы
            case SshConnectionException when ex.Message.Contains("identification string", StringComparison.OrdinalIgnoreCase):
                return $"{server.Host}:{server.Port} принял соединение, но не ответил как SSH-сервер и сразу закрыл его. "
                    + "Если так со всеми серверами — соединение режет VPN, прокси или фаервол на этом компьютере "
                    + "(например, Clash/mihomo в режиме TUN): пустите эти адреса или порт напрямую. "
                    + "Если только с этим — на порту не SSH или сервер не пускает ваш адрес.";

            case SshConnectionException:
                return "Соединение разорвано сервером: " + ex.Message;

            case SftpPermissionDeniedException:
                return $"Недостаточно прав на сервере: пользователю «{server.User}» "
                    + "запрещена запись по указанному пути.";

            case SftpPathNotFoundException:
                return "Путь на сервере не найден: " + ex.Message;

            case ProxyException:
                return "Ошибка прокси: " + ex.Message;

            default:
                return ex.Message;
        }
    }

    public static string NormalizeRemote(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var p = path.Trim().Replace('\\', '/');

        while (p.Contains("//", StringComparison.Ordinal))
        {
            p = p.Replace("//", "/", StringComparison.Ordinal);
        }

        if (p.Length > 1)
        {
            p = p.TrimEnd('/');
        }

        return p.Length == 0 ? "/" : p;
    }

    public static string CombineRemote(string basePath, string name)
    {
        var b = NormalizeRemote(basePath);
        var n = name.Replace('\\', '/').Trim('/');

        if (n.Length == 0)
        {
            return b;
        }

        return b == "/" ? "/" + n : b + "/" + n;
    }

    public static string ParentRemote(string path)
    {
        var p = NormalizeRemote(path);

        if (p == "/")
        {
            return "/";
        }

        var idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    /// <summary>Что отправить: все файлы и папки выбранного, с путями на сервере.</summary>
    private static Plan BuildPlan(IReadOnlyList<TransferItem> items, string root, ExcludeRules exclude)
    {
        var plan = new Plan();

        foreach (var item in items)
        {
            if (exclude.Matches(item.Name, item.IsDirectory))
            {
                plan.Excluded++;
                continue;
            }

            // папка едет целиком, вместе со своим именем — как при копировании
            // в проводнике: перетащил папку в каталог, там появилась папка
            var target = CombineRemote(root, item.Name);
            plan.Roots.Add(target);

            if (item.IsDirectory)
            {
                AddDirectory(plan, item.FullPath, target, item.Name, exclude);
            }
            else
            {
                plan.Files.Add(new FileJob(item.FullPath, target));
            }
        }

        return plan;
    }

    /// <param name="relative">Путь от копируемого объекта включительно — по нему сверяются маски.</param>
    private static void AddDirectory(Plan plan, string localDir, string remoteDir, string relative, ExcludeRules exclude)
    {
        plan.Dirs.Add(new DirJob(localDir, remoteDir));

        foreach (var file in Directory.EnumerateFiles(localDir))
        {
            var name = Path.GetFileName(file);

            if (exclude.Matches(relative + "/" + name, isDirectory: false))
            {
                plan.Excluded++;
                continue;
            }

            plan.Files.Add(new FileJob(file, CombineRemote(remoteDir, name)));
        }

        foreach (var dir in Directory.EnumerateDirectories(localDir))
        {
            var name = Path.GetFileName(dir);

            // исключённая папка не обходится вовсе: в node_modules десятки тысяч файлов
            if (exclude.Matches(relative + "/" + name, isDirectory: true))
            {
                plan.Excluded++;
                continue;
            }

            AddDirectory(plan, dir, CombineRemote(remoteDir, name), relative + "/" + name, exclude);
        }
    }

    /// <summary>
    /// Размер, дата и тип всего, что лежит в местах назначения, — одной командой find.
    /// null — сервер так не умеет (урезанный find без -printf): тогда о каждом файле
    /// спрашиваем отдельно, как раньше. Медленнее, но работает везде.
    /// </summary>
    private static Inventory? TakeInventory(SshClient ssh, Plan plan, Action<string> report, CancellationToken ct)
    {
        if (plan.Roots.Count == 0)
        {
            return null;
        }

        // -H: если само место назначения — ссылка на папку, заходим в неё.
        // Ссылки глубже не раскрываем (там бывают петли и выход на весь диск) — о путях
        // за ними find ничего не скажет, их спросим по одному
        // первой записью — кто мы на сервере и есть ли чем принять пачку файлов одним потоком;
        // права, владелец и группа нужны, чтобы проставить их файлам прямо в пачке
        var script =
            "find / -maxdepth 0 -printf '' >/dev/null 2>&1 || exit 97\n"
            + "printf '" + CapsMarker + "\\t%s\\t%s\\t%s\\t%s\\0' \"$(id -u 2>/dev/null)\" \"$(id -g 2>/dev/null)\""
            + " \"$(tar --version 2>/dev/null | head -n 1)\" \"$(command -v gzip 2>/dev/null)\"\n"
            + "find -H " + string.Join(" ", plan.Roots.Select(ShellQuote))
            + " -printf '%y\\t%s\\t%T@\\t%m\\t%U\\t%G\\t%p\\0' 2>/dev/null\n"
            + "exit 0";

        string output;

        try
        {
            using var cmd = ssh.CreateCommand(script);
            cmd.ExecuteAsync(ct).GetAwaiter().GetResult();

            if (cmd.ExitStatus == 97)
            {
                report("find на сервере без -printf — размеры и даты спрашиваются по одному файлу");
                return null;
            }

            output = cmd.Result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report("опись сервера одной командой не удалась, спрашиваю по одному файлу: " + ex.Message);
            return null;
        }

        var inventory = new Inventory();

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var integer = System.Globalization.NumberStyles.Integer;

        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.StartsWith(CapsMarker + "\t", StringComparison.Ordinal))
            {
                var caps = record.Split('\t');

                if (caps.Length >= 5)
                {
                    inventory.Uid = int.TryParse(caps[1], integer, invariant, out var uid) ? uid : null;
                    inventory.Gid = int.TryParse(caps[2], integer, invariant, out var gid) ? gid : null;
                    inventory.GnuTar = caps[3].Contains("GNU tar", StringComparison.Ordinal);
                    inventory.Gzip = caps[4].Trim().Length > 0;
                }

                continue;
            }

            var parts = record.Split('\t', 7);

            if (parts.Length < 7)
            {
                continue;
            }

            var path = NormalizeRemote(parts[6].TrimStart('\n', '\r'));

            if (parts[0] == "l")
            {
                inventory.Links.Add(path);
                continue;
            }

            long.TryParse(parts[1], integer, invariant, out var size);

            var seconds = parts[2].Split('.')[0];
            long.TryParse(seconds, integer, invariant, out var unix);

            var mode = TryOctal(parts[3]);
            var owner = int.TryParse(parts[4], integer, invariant, out var u) ? u : -1;
            var group = int.TryParse(parts[5], integer, invariant, out var g) ? g : -1;

            inventory.Entries[path] = new RemoteStat(
                parts[0] == "d", size, DateTime.UnixEpoch.AddSeconds(unix), null, mode, owner, group);
        }

        var fileCount = inventory.Entries.Values.Count(e => !e.IsDirectory);
        report($"на сервере уже есть: файлов {fileCount}, папок {inventory.Entries.Count - fileCount} — одним запросом");

        return inventory;
    }

    /// <summary>Файл, который решено отправить, и что о нём известно на сервере.</summary>
    private sealed record SendJob(FileJob Job, FileInfo Info, RemoteStat? Remote);

    /// <summary>Дата изменения, которую надо проставить объекту на сервере.</summary>
    private sealed record Retouch(string Path, DateTime WriteUtc);

    /// <summary>Права, владелец и группа файла в пачке — числами, как их понимает tar.</summary>
    private readonly record struct TarOwner(int Mode, int Uid, int Gid);

    /// <summary>
    /// Решение по одному файлу: пропустить, поправить дату или отправить (тогда возвращается
    /// задание). Пропуски построчно не пишем: на тысячах файлов они заваливают вывод,
    /// а сказать о них нечего — их число есть в итоге.
    /// </summary>
    private static SendJob? Decide(UploadContext ctx, FileJob job, ConcurrentBag<Retouch> redate)
    {
        var stats = ctx.Stats;
        var info = new FileInfo(job.LocalPath);
        var remote = ctx.Stat(job.RemotePath);

        if (remote is not null && !ctx.Options.Overwrite)
        {
            Interlocked.Increment(ref stats.Skipped);
            return null;
        }

        if (remote is not null && ctx.Options.Matching)
        {
            var reason = Difference(info, remote, job.RemotePath, ctx);

            if (reason is null)
            {
                Interlocked.Increment(ref stats.UpToDate);

                // Сверка по сумме на дату не смотрит: содержимое то же, а дата на сервере может
                // остаться от прошлой заливки. Без поправки она так и висела бы чужой — и сверка
                // по дате потом гоняла бы эти файлы заново. Дату переносим, файл не отправляем.
                // Сами даты ставятся потом, одной командой на все файлы сразу
                if (ctx.Options.PreserveTimestamps && Math.Abs(DriftSeconds(info, remote)) > DateTolerance)
                {
                    redate.Add(new Retouch(job.RemotePath, info.LastWriteTimeUtc));
                    Interlocked.Increment(ref stats.Redated);
                }

                return null;
            }

            ctx.Report($"замена ({reason}): {job.RemotePath}");
        }

        return new SendJob(job, info, remote);
    }

    /// <summary>Файлы меньше этого едут пачкой; крупные — каждый своим потоком.</summary>
    private const long SmallFile = 8L * 1024 * 1024;

    /// <summary>С какого числа мелких файлов пачка окупается: один-два файла SFTP довезёт не медленнее.</summary>
    private const int TarMinFiles = 4;

    /// <summary>Предел одной пачки: сбой не тянет за собой всё, а временная папка не разрастается.</summary>
    private const int TarBatchFiles = 4000;

    private const long TarBatchBytes = 256L * 1024 * 1024;

    /// <summary>Сколько подключений SFTP возят крупные несжимаемые файлы одновременно.</summary>
    private const int BigFileConnections = 4;

    /// <summary>Метка строки «не получилось» в выводе служебных скриптов: за ней номер строки скрипта.</summary>
    private const string FailMarker = "MSS-FAIL ";

    /// <summary>Метка первой записи описи: кто мы на сервере и чем он умеет принимать пачки.</summary>
    private const string CapsMarker = "MSS-CAPS";

    /// <summary>
    /// Везёт отобранное. Способ выбирается по тому, что едет:
    /// мелочь от четырёх файлов — пачкой, одним сжатым потоком tar, без запросов на каждый файл;
    /// крупный файл, который жмётся (текст, дампы), — своим сжатым потоком;
    /// крупный, который не жмётся (видео, архивы), — по SFTP, несколькими подключениями сразу;
    /// один-три мелких файла, сервер без GNU tar, путь за ссылкой — по SFTP, как раньше.
    /// Права, владелец и дата во всех случаях одни и те же. Пачка, которая не прошла,
    /// досылается по одному. Вернёт true, если пачки ставились через временную папку в корне.
    /// </summary>
    private static bool Send(
        SshClient ssh,
        UploadContext ctx,
        List<SendJob> jobs,
        ServerEntry server,
        AppSettings settings,
        string root,
        CancellationToken ct)
    {
        var inventory = ctx.Inventory;
        var tarReady = inventory is { GnuTar: true, Uid: not null, Gid: not null };

        var packed = new List<SendJob>();
        var streamed = new List<SendJob>();
        var big = new List<SendJob>();
        var small = new List<SendJob>();

        foreach (var s in jobs)
        {
            // за ссылкой опись не была: ни прав, ни владельца для пачки там не знаем
            var tarOk = tarReady && !inventory!.IsBlind(s.Job.RemotePath);

            if (s.Info.Length >= SmallFile)
            {
                (tarOk && inventory!.Gzip && Compressible(s.Info.FullName) ? streamed : big).Add(s);
            }
            else
            {
                (tarOk ? packed : small).Add(s);
            }
        }

        if (packed.Count < TarMinFiles)
        {
            small.AddRange(packed);
            packed.Clear();
        }

        var batches = Batches(packed).Concat(streamed.Select(s => new List<SendJob> { s })).ToList();
        var fallback = new ConcurrentBag<SendJob>();

        if (small.Count > 0)
        {
            ctx.Report($"по одному через SFTP: {small.Count} файл(ов), {TransferItem.Format(small.Sum(s => s.Info.Length))}");
        }

        // пачки и крупное — каждое своими подключениями, мелочь — основным SFTP:
        // друг другу они не мешают и едут одновременно
        var tasks = new[]
        {
            Task.Run(() => SendBatches(ssh, ctx, root, batches, fallback, server, settings, ct), ct),
            Task.Run(() => SendBig(ctx, big, server, settings, ct), ct),
            Task.Run(() => RunParallel(small, ct, s => SendViaSftp(ctx, ctx.Sftp, s, ct)), ct),
        };

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ae)
        {
            ThrowFirst(ae);
        }

        if (!fallback.IsEmpty)
        {
            RunParallel(
                fallback.OrderBy(s => s.Job.RemotePath, StringComparer.Ordinal),
                ct,
                s => SendViaSftp(ctx, ctx.Sftp, s, ct));
        }

        return batches.Count > 0;
    }

    /// <summary>Мелочь — пачками с пределом по числу файлов и по объёму.</summary>
    private static List<List<SendJob>> Batches(List<SendJob> files)
    {
        var result = new List<List<SendJob>>();
        var current = new List<SendJob>();
        long bytes = 0;

        foreach (var s in files)
        {
            if (current.Count > 0 && (current.Count >= TarBatchFiles || bytes + s.Info.Length > TarBatchBytes))
            {
                result.Add(current);
                current = [];
                bytes = 0;
            }

            current.Add(s);
            bytes += s.Info.Length;
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    /// <summary>
    /// Жмётся ли файл: пробуем сжать его начало. Видео, картинки и архивы уже сжаты —
    /// гонять их через gzip только тратить время.
    /// </summary>
    private static bool Compressible(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var buffer = new byte[128 * 1024];
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            if (read == 0)
            {
                return false;
            }

            using var sink = new MemoryStream();

            using (var gzip = new GZipStream(sink, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(buffer, 0, read);
            }

            return sink.Length < read * 0.85;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Пачки — по нескольку сразу, каждая по своему подключению: одно соединение
    /// до далёкого сервера упирается в свой предел скорости, несколько едут вместе.
    /// </summary>
    private static void SendBatches(
        SshClient ssh,
        UploadContext ctx,
        string root,
        List<List<SendJob>> batches,
        ConcurrentBag<SendJob> fallback,
        ServerEntry server,
        AppSettings settings,
        CancellationToken ct)
    {
        if (batches.Count == 0)
        {
            return;
        }

        var own = batches.Count > 1 ? ExtraSsh(server, settings, Math.Min(TarConnections, batches.Count) - 1) : [];
        var pool = new ConcurrentBag<SshClient>(own) { ssh };

        if (batches.Count > 1)
        {
            ctx.Report($"пачек: {batches.Count}, идут по {pool.Count} сразу, каждая своим подключением");
        }

        var done = 0;

        try
        {
            RunParallel(
                batches,
                ct,
                batch =>
                {
                    if (!pool.TryTake(out var client))
                    {
                        client = ssh;
                    }

                    try
                    {
                        SendTar(client, ctx, root, batch, fallback, ct, () => $"пачка {Interlocked.Increment(ref done)} из {batches.Count}");
                    }
                    finally
                    {
                        pool.Add(client);
                    }
                },
                pool.Count);
        }
        finally
        {
            CloseAll(own);
        }
    }

    /// <summary>Ещё подключения SSH к тому же серверу. Не пустил — обходимся теми, что вышли.</summary>
    private static List<SshClient> ExtraSsh(ServerEntry server, AppSettings settings, int count)
    {
        var opened = new ConcurrentBag<SshClient>();

        Parallel.For(0, Math.Max(count, 0), _ =>
        {
            SshClient? client = null;

            try
            {
                // через промежуточный сервер ошибка бывает уже здесь — не повод валить всё копирование
                client = NewSsh(server, settings);
                client.Connect();
                opened.Add(client);
            }
            catch (Exception)
            {
                client?.Dispose();
            }
        });

        return [.. opened];
    }

    private static void CloseAll(IEnumerable<BaseClient> clients)
    {
        foreach (var client in clients)
        {
            try
            {
                client.Disconnect();
            }
            catch (Exception)
            {
                // подключение уже оборвано
            }

            client.Dispose();
        }
    }

    /// <summary>
    /// Крупные несжимаемые файлы. Где на сервере есть оболочка — кусками по нескольким
    /// подключениям сразу, даже один файл: одно соединение до далёкого сервера быстрее
    /// не едет, а каналу команды SSH.NET ещё и отдаёт данные быстрее, чем SFTP.
    /// </summary>
    private static void SendBig(UploadContext ctx, List<SendJob> big, ServerEntry server, AppSettings settings, CancellationToken ct)
    {
        if (big.Count == 0)
        {
            return;
        }

        if (ctx.Inventory is not null)
        {
            var own = ExtraSsh(server, settings, BigFileConnections);

            if (own.Count > 0)
            {
                try
                {
                    SendPieces(ctx, big, own, ct);
                    return;
                }
                finally
                {
                    CloseAll(own);
                }
            }
        }

        SendBigSftp(ctx, big, server, settings, ct);
    }

    /// <summary>Сколько байт в одном куске крупного файла: целое число мегабайт — dd отсчитывает место ими.</summary>
    private const long PieceBytes = 32L * 1024 * 1024;

    private const long Mebibyte = 1024L * 1024;

    /// <summary>Сколько пачек мелочи едут одновременно, каждая по своему подключению.</summary>
    private const int TarConnections = 3;

    /// <summary>Крупный файл, который едет кусками: сколько кусков ещё не дошло и куда они пишутся.</summary>
    private sealed class PiecedFile(SendJob job, string temp, SftpFileAttributes? previous, SftpFileAttributes? parent, int pieces)
    {
        public SendJob Job { get; } = job;

        public string Temp { get; } = temp;

        public SftpFileAttributes? Previous { get; } = previous;

        public SftpFileAttributes? Parent { get; } = parent;

        public int Pieces { get; } = pieces;

        public int Left = pieces;

        public bool Done;
    }

    /// <summary>
    /// Куски всех крупных файлов — одной очередью на все подключения. Каждый кусок пишет на
    /// своё место во временный файл dd на сервере; последний дошедший ставит права, владельца
    /// и подменяет прежний файл — так же, как SFTP, целиком или никак.
    /// </summary>
    private static void SendPieces(UploadContext ctx, List<SendJob> big, List<SshClient> own, CancellationToken ct)
    {
        ctx.Report($"крупные файлы кусками, в {own.Count} подключения сразу: {big.Count}, {TransferItem.Format(big.Sum(s => s.Info.Length))}");

        var files = new List<PiecedFile>();

        foreach (var s in big.OrderByDescending(s => s.Info.Length))
        {
            var parentDir = ParentRemote(s.Job.RemotePath);
            ctx.EnsureDir(parentDir);

            // образец прав и владельца — как у SFTP: прежний файл, а для нового — папка
            var previous = s.Remote is null ? null : s.Remote.Attributes ?? TryGetAttributes(ctx.Sftp, s.Job.RemotePath);
            var parent = s.Remote is null ? ctx.DirAttributes(parentDir) : null;
            var pieces = (int)Math.Max(1, (s.Info.Length + PieceBytes - 1) / PieceBytes);

            files.Add(new PiecedFile(s, TempName(s.Job.RemotePath), previous, parent, pieces));
        }

        var parts = files.SelectMany(f => Enumerable.Range(0, f.Pieces).Select(i => (File: f, Index: i))).ToList();
        var pool = new ConcurrentBag<SshClient>(own);

        try
        {
            RunParallel(
                parts,
                ct,
                p =>
                {
                    if (!pool.TryTake(out var client))
                    {
                        client = own[0];
                    }

                    try
                    {
                        var offset = p.Index * PieceBytes;
                        var count = Math.Min(PieceBytes, p.File.Job.Info.Length - offset);
                        WritePiece(client, ctx.Meter, p.File.Job.Job.LocalPath, p.File.Temp, offset, count, ct);
                    }
                    finally
                    {
                        pool.Add(client);
                    }

                    if (Interlocked.Decrement(ref p.File.Left) == 0)
                    {
                        var f = p.File;
                        ApplyAttributes(ctx.Sftp, f.Temp, f.Job.Info, f.Previous, f.Parent, ctx.Options, ctx.Report);
                        Replace(ctx.Sftp, f.Temp, f.Job.Job.RemotePath);
                        f.Done = true;
                        Delivered(ctx, f.Job);
                    }
                },
                own.Count);
        }
        catch
        {
            foreach (var f in files.Where(f => !f.Done))
            {
                TryDeleteRemote(ctx.Sftp, f.Temp);
            }

            throw;
        }
    }

    /// <summary>Кусок файла — в его место во временном файле на сервере, через dd по каналу команды.</summary>
    private static void WritePiece(SshClient ssh, TransferMeter meter, string localPath, string temp, long offset, long count, CancellationToken ct)
    {
        using var cmd = ssh.CreateCommand(
            "dd of=" + ShellQuote(temp) + " bs=1048576 seek=" + (offset / Mebibyte).ToString(CultureInfo.InvariantCulture) + " conv=notrunc");
        var run = cmd.ExecuteAsync(ct);

        using (var channel = new BufferedStream(cmd.CreateInputStream(), 256 * 1024))
        using (var file = File.OpenRead(localPath))
        {
            file.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[256 * 1024];

            while (count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, count));

                if (read == 0)
                {
                    throw new IOException("файл стал короче, пока отправлялся");
                }

                channel.Write(buffer, 0, read);
                meter.Add(read);
                count -= read;
            }
        }

        run.GetAwaiter().GetResult();

        if (cmd.ExitStatus is not 0)
        {
            throw new InvalidOperationException("кусок файла не записался: " + (FirstLine(cmd.Error) ?? "dd на сервере не сработал"));
        }
    }

    /// <summary>
    /// Крупные файлы по SFTP, каждый на своём подключении — где оболочки на сервере нет
    /// или лишних подключений он не пустил.
    /// </summary>
    private static void SendBigSftp(UploadContext ctx, List<SendJob> big, ServerEntry server, AppSettings settings, CancellationToken ct)
    {
        ctx.Report($"крупные файлы по SFTP: {big.Count}, {TransferItem.Format(big.Sum(s => s.Info.Length))}");

        var pool = new ConcurrentBag<SftpClient> { ctx.Sftp };
        var own = new ConcurrentBag<SftpClient>();

        Parallel.For(0, Math.Min(BigFileConnections, big.Count) - 1, _ =>
        {
            SftpClient? client = null;

            try
            {
                // через промежуточный сервер ошибка бывает уже здесь — не повод валить всё копирование
                client = NewSftp(server, settings);
                client.Connect();
                own.Add(client);
                pool.Add(client);
            }
            catch (Exception)
            {
                // не пустил ещё одно подключение — обойдёмся теми, что есть
                client?.Dispose();
            }
        });

        try
        {
            RunParallel(
                big.OrderByDescending(s => s.Info.Length),
                ct,
                s =>
                {
                    if (!pool.TryTake(out var client))
                    {
                        client = ctx.Sftp;
                    }

                    try
                    {
                        SendViaSftp(ctx, client, s, ct);
                    }
                    finally
                    {
                        pool.Add(client);
                    }
                },
                pool.Count);
        }
        finally
        {
            foreach (var client in own)
            {
                try
                {
                    client.Disconnect();
                }
                catch (Exception)
                {
                    // подключение уже оборвано
                }

                client.Dispose();
            }
        }
    }

    /// <summary>Один файл по SFTP: во временный файл рядом, права и владелец, подмена прежнего.</summary>
    private static void SendViaSftp(UploadContext ctx, SftpClient sftp, SendJob s, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var remoteFile = s.Job.RemotePath;
        var parentDir = ParentRemote(remoteFile);
        ctx.EnsureDir(parentDir);

        // Образец прав и владельца: прежний файл, а для нового — папка, в которую он ложится.
        // Права из описи SFTP не годятся как есть, поэтому атрибуты заменяемого файла
        // берём запросом — только для тех файлов, что действительно отправляются
        var previous = s.Remote is null ? null : s.Remote.Attributes ?? TryGetAttributes(sftp, remoteFile);
        var parent = s.Remote is null ? ctx.DirAttributes(parentDir) : null;

        // пишем рядом во временный файл и подменяем им прежний одним действием: оборвётся
        // связь — на месте останется целый старый файл, а не обрезок нового. Именно так
        // раньше и появлялся неполный файл, который потом никакая сверка не заменяла.
        var temp = TempName(remoteFile);

        try
        {
            using (var stream = ctx.Meter.Wrap(File.OpenRead(s.Job.LocalPath)))
            {
                sftp.UploadFile(stream, temp, canOverride: true);
            }

            ApplyAttributes(sftp, temp, s.Info, previous, parent, ctx.Options, ctx.Report);
            Replace(sftp, temp, remoteFile);
        }
        catch
        {
            TryDeleteRemote(sftp, temp);
            throw;
        }

        Delivered(ctx, s);
    }

    /// <summary>
    /// Файл встал на место: в счёт и в список отправленного. Построчно в вывод не пишем —
    /// список целиком, по алфавиту, выводится в итоге, и строки шли бы дважды.
    /// </summary>
    private static void Delivered(UploadContext ctx, SendJob s)
    {
        var stats = ctx.Stats;

        ctx.Touch(ParentRemote(s.Job.RemotePath));
        Interlocked.Increment(ref stats.Files);
        Interlocked.Add(ref stats.Bytes, s.Info.Length);

        lock (stats.Uploaded)
        {
            stats.Uploaded.Add(new UploadedFile(s.Job.LocalPath, s.Job.RemotePath, s.Info.Length));
        }
    }

    /// <summary>
    /// Пачка файлов одним потоком: tar (сжатый, если на сервере есть gzip) распаковывается
    /// во временную папку в корне назначения — на той же файловой системе, — и оттуда файлы
    /// переставляются на место через mv. Перестановка — то же переименование, что и у SFTP:
    /// оборвётся связь — на месте останутся целые старые файлы, а не обрезки новых.
    ///
    /// Права, владелец и группа прописаны каждому файлу в самой пачке — те же, что ставит
    /// SFTP: у заменяемого файла его собственные, у нового — от папки, без права запуска.
    /// Владельца tar ставит, только если мы root, — как и SFTP.
    /// </summary>
    private static void SendTar(
        SshClient ssh,
        UploadContext ctx,
        string root,
        List<SendJob> batch,
        ConcurrentBag<SendJob> fallback,
        CancellationToken ct,
        Func<string> label)
    {
        ct.ThrowIfCancellationRequested();

        var inventory = ctx.Inventory!;
        var compress = inventory.Gzip;
        var stage = ".mss-upload-" + Guid.NewGuid().ToString("N")[..8];

        foreach (var s in batch)
        {
            ctx.EnsureDir(ParentRemote(s.Job.RemotePath));
        }

        var entries = batch
            .Select(s => (Job: s, Rel: Relative(root, s.Job.RemotePath), Owner: OwnerFor(ctx, s)))
            .ToList();

        string? problem;

        try
        {
            // папку — отдельной командой до потока: если её не создать, поток уходил бы в никуда
            using (var prepare = ssh.CreateCommand(
                "cd -- " + ShellQuote(root) + " && mkdir -m 700 -- " + ShellQuote(stage)))
            {
                prepare.ExecuteAsync(ct).GetAwaiter().GetResult();
                problem = prepare.ExitStatus is 0 ? null : FirstLine(prepare.Error) ?? "не создать временную папку";
            }

            problem ??= Unpack(
                ssh,
                "cd -- " + ShellQuote(root + "/" + stage)
                    + " && exec tar -x" + (compress ? "z" : string.Empty) + "pf - --numeric-owner "
                    + (inventory.Uid == 0 ? "--same-owner" : "--no-same-owner"),
                entries,
                compress,
                ctx.Options.PreserveTimestamps,
                ctx.Meter,
                ct);
        }
        catch (OperationCanceledException)
        {
            RemoveStage(ssh, root, stage);
            throw;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            RemoveStage(ssh, root, stage);
            ctx.Report($"пачкой не вышло, файлы ({batch.Count}) пойдут по одному: {problem}");

            foreach (var s in batch)
            {
                fallback.Add(s);
            }

            return;
        }

        // на место — по команде на папку назначения, а не на файл
        var chunks = entries
            .GroupBy(e => ParentRemote(e.Job.Job.RemotePath), StringComparer.Ordinal)
            .SelectMany(g => g.Chunk(200))
            .ToList();

        var script = new StringBuilder();
        script.Append("cd -- ").Append(ShellQuote(root)).Append(" || exit 1\n");

        for (var i = 0; i < chunks.Count; i++)
        {
            var dest = Relative(root, ParentRemote(chunks[i][0].Job.Job.RemotePath));

            script.Append("mv -f --");

            foreach (var e in chunks[i])
            {
                script.Append(' ').Append(ShellQuote(stage + "/" + e.Rel));
            }

            script.Append(' ').Append(ShellQuote((dest.Length == 0 ? "." : dest) + "/"));
            script.Append(" || echo '").Append(FailMarker).Append(i).Append("'\n");
        }

        script.Append("rm -rf -- ").Append(ShellQuote(stage)).Append('\n');

        HashSet<int> failed;
        string complaint;

        try
        {
            (var output, complaint) = RunInput(ssh, script.ToString(), ct);
            failed = FailedLines(output);
        }
        catch (OperationCanceledException)
        {
            RemoveStage(ssh, root, stage);
            throw;
        }
        catch (Exception ex)
        {
            // какие-то файлы могли уже встать — дослать их заново безвредно
            RemoveStage(ssh, root, stage);
            ctx.Report($"пачка не встала на место, файлы ({batch.Count}) пойдут по одному: {ex.Message}");

            foreach (var s in batch)
            {
                fallback.Add(s);
            }

            return;
        }

        if (failed.Count > 0)
        {
            var count = failed.Sum(i => chunks[i].Length);
            ctx.Report($"не встали на место, пойдут по одному: {count} файл(ов)"
                + (FirstLine(complaint) is { } why ? " — " + why : string.Empty));
        }

        var placed = chunks.Where((_, i) => !failed.Contains(i)).SelectMany(c => c).ToList();

        if (placed.Count > 0)
        {
            ctx.Report($"{label()} дошла{(compress ? " со сжатием" : string.Empty)}: {placed.Count} файл(ов), "
                + TransferItem.Format(placed.Sum(e => e.Job.Info.Length)));
        }

        var foreign = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            foreach (var e in chunks[i])
            {
                if (failed.Contains(i))
                {
                    fallback.Add(e.Job);
                    continue;
                }

                if (inventory.Uid != 0 && (e.Owner.Uid != inventory.Uid || e.Owner.Gid != inventory.Gid))
                {
                    foreign++;
                }

                Delivered(ctx, e.Job);
            }
        }

        if (foreign > 0)
        {
            ctx.Report($"владелец не перенесён на {foreign} файл(ов): сменить его может только root");
        }
    }

    /// <summary>
    /// Пишет пачку в tar на сервере. null — распаковалось; иначе — что пошло не так.
    /// </summary>
    private static string? Unpack(
        SshClient ssh,
        string command,
        IReadOnlyList<(SendJob Job, string Rel, TarOwner Owner)> entries,
        bool compress,
        bool preserveTimestamps,
        TransferMeter meter,
        CancellationToken ct)
    {
        using var cmd = ssh.CreateCommand(command);
        var run = cmd.ExecuteAsync(ct);
        string? broken = null;

        try
        {
            using var channel = new BufferedStream(cmd.CreateInputStream(), 256 * 1024);
            using var gzip = compress ? new GZipStream(channel, CompressionLevel.Fastest, leaveOpen: true) : null;
            using var writer = new TarWriter(gzip ?? (Stream)channel, TarEntryFormat.Pax, leaveOpen: true);

            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();

                using var data = meter.Wrap(File.OpenRead(e.Job.Job.LocalPath));

                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, e.Rel)
                {
                    Mode = (UnixFileMode)e.Owner.Mode,
                    Uid = e.Owner.Uid,
                    Gid = e.Owner.Gid,

                    // без сохранения дат — как у SFTP: файл получает время, когда лёг
                    ModificationTime = preserveTimestamps ? e.Job.Info.LastWriteTimeUtc : DateTime.UtcNow,
                    DataStream = data,
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            broken = ex.Message;
        }

        run.GetAwaiter().GetResult();

        if (cmd.ExitStatus is not 0)
        {
            return FirstLine(cmd.Error) ?? broken ?? "архив на сервере не распаковался";
        }

        return broken;
    }

    /// <summary>Убирает временную папку пачки. Не вышло — не беда: в ней только копии.</summary>
    private static void RemoveStage(SshClient ssh, string root, string stage)
    {
        try
        {
            using var cmd = ssh.CreateCommand("cd -- " + ShellQuote(root) + " && rm -rf -- " + ShellQuote(stage));
            cmd.Execute();
        }
        catch (Exception)
        {
            // связь оборвана — папка с точкой останется, но целевые файлы целы
        }
    }

    /// <summary>
    /// Права, владелец и группа для файла в пачке — по тем же правилам, что у SFTP:
    /// у заменяемого файла его собственные, у нового — от папки, без права запуска.
    /// </summary>
    private static TarOwner OwnerFor(UploadContext ctx, SendJob s)
    {
        const int Rwx = 0x1FF; // 0777
        const int Rw = 0x1B6;  // 0666

        if (s.Remote is { Mode: >= 0, Uid: >= 0, Gid: >= 0 } previous)
        {
            return new TarOwner(previous.Mode & Rwx, previous.Uid, previous.Gid);
        }

        var parent = ParentRemote(s.Job.RemotePath);

        if (ctx.Known(parent) is { IsDirectory: true, Mode: >= 0, Uid: >= 0, Gid: >= 0 } dir)
        {
            return new TarOwner(dir.Mode & Rw, dir.Uid, dir.Gid);
        }

        if (ctx.DirAttributes(parent) is { } attrs)
        {
            return new TarOwner(ModeOf(attrs) & Rw, attrs.UserId, attrs.GroupId);
        }

        var inventory = ctx.Inventory!;
        return new TarOwner(0x1A4, inventory.Uid ?? 0, inventory.Gid ?? 0); // 0644
    }

    /// <summary>Права из атрибутов SFTP — числом, как в chmod.</summary>
    private static int ModeOf(SftpFileAttributes a)
    {
        var bits = new[]
        {
            a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute,
            a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute,
            a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute,
        };

        var mode = 0;

        foreach (var bit in bits)
        {
            mode = (mode << 1) | (bit ? 1 : 0);
        }

        return mode;
    }

    private static int TryOctal(string text)
    {
        try
        {
            return Convert.ToInt32(text.Trim(), 8);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>Путь относительно корня назначения; сам корень — пустая строка.</summary>
    private static string Relative(string root, string path)
    {
        if (path == root)
        {
            return string.Empty;
        }

        return root == "/" ? path[1..] : path[(root.Length + 1)..];
    }

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    /// <summary>
    /// Служебный скрипт через stdin обычного sh: длина не ограничена, кавычки не мешают.
    /// Вернёт вывод и вывод ошибок.
    /// </summary>
    internal static (string Output, string Error) RunInput(SshClient ssh, string script, CancellationToken ct)
    {
        using var cmd = ssh.CreateCommand("sh -s");
        var run = cmd.ExecuteAsync(ct);

        using (var input = new BufferedStream(cmd.CreateInputStream(), 64 * 1024))
        {
            input.Write(new UTF8Encoding(false).GetBytes(script));
        }

        run.GetAwaiter().GetResult();

        return (cmd.Result, cmd.Error ?? string.Empty);
    }

    /// <summary>Номера строк скрипта, на которых он сказал «не получилось».</summary>
    private static HashSet<int> FailedLines(string output)
    {
        var failed = new HashSet<int>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(FailMarker, StringComparison.Ordinal)
                && int.TryParse(line[FailMarker.Length..], out var index))
            {
                failed.Add(index);
            }
        }

        return failed;
    }

    /// <summary>
    /// Ставит даты изменения одной командой на все сразу: touch по файлам с одинаковой
    /// датой — их после сборки обычно большинство. Что не вышло (старый touch без «@секунд»,
    /// нет прав) — по одному через SFTP, как раньше: там же и скажется причина.
    /// </summary>
    private static void ApplyDates(SshClient ssh, SftpClient sftp, IReadOnlyList<Retouch> items, Action<string> report, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var groups = items
            .GroupBy(i => i.WriteUtc.Ticks)
            .SelectMany(g => g.Chunk(200))
            .ToList();

        var left = new List<Retouch>();

        try
        {
            var script = new StringBuilder();

            for (var i = 0; i < groups.Count; i++)
            {
                script.Append("touch -c -m -d ").Append(UnixStamp(groups[i][0].WriteUtc)).Append(" --");

                foreach (var r in groups[i])
                {
                    script.Append(' ').Append(ShellQuote(r.Path));
                }

                script.Append(" 2>/dev/null || echo '").Append(FailMarker).Append(i).Append("'\n");
            }

            var (output, _) = RunInput(ssh, script.ToString(), ct);

            foreach (var i in FailedLines(output))
            {
                left.AddRange(groups[i]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            left = [.. items];
        }

        RunParallel(left, ct, r => PreserveTimestamps(sftp, r.Path, r.WriteUtc, r.WriteUtc, report));
    }

    /// <summary>Время для touch -d: «@секунды.доли» от начала эпохи.</summary>
    private static string UnixStamp(DateTime utc)
    {
        var ticks = utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks;
        return "@" + (ticks / TimeSpan.TicksPerSecond).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + (ticks % TimeSpan.TicksPerSecond).ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Запас при сравнении дат: у SFTP время с точностью до секунды.</summary>
    private const double DateTolerance = 2;

    /// <summary>На сколько секунд локальный файл новее серверного (минус — серверный новее).</summary>
    private static double DriftSeconds(FileInfo local, RemoteStat remote) =>
        (local.LastWriteTimeUtc - remote.WriteUtc).TotalSeconds;

    /// <summary>
    /// Чем локальный файл отличается от серверного — человеческим языком, или null,
    /// если отличий нет и отправлять нечего. Размер сверяется всегда: он ничего не стоит
    /// (уже известен из описи) и ловит недокачанный файл даже там, где дата совпала.
    /// </summary>
    private static string? Difference(FileInfo local, RemoteStat remote, string remoteFile, UploadContext ctx)
    {
        if (local.Length != remote.Size)
        {
            return $"другой размер · локально {TransferItem.Format(local.Length)}"
                + $", на сервере {TransferItem.Format(remote.Size)}";
        }

        if (ctx.Options.MatchByHash)
        {
            if (ctx.RemoteHashes is null || !ctx.RemoteHashes.TryGetValue(remoteFile, out var actual))
            {
                return "сумма с сервера не получена";
            }

            // сумма сошлась — на дату уже не смотрим: содержимое и так совпадает
            return string.Equals(ctx.LocalHash(local.FullName), actual, StringComparison.OrdinalIgnoreCase)
                ? null
                : "другая контрольная сумма";
        }

        // расхождение в любую сторону — повод отправить файл: на сервере должно лежать то же,
        // что и локально, в том числе когда локальный откатили на версию постарше
        var drift = DriftSeconds(local, remote);

        if (Math.Abs(drift) <= DateTolerance)
        {
            return null;
        }

        return (drift > 0 ? "локальный свежее" : "на сервере свежее")
            + $" · локально {local.LastWriteTime:dd.MM.yyyy HH:mm:ss}"
            + $", на сервере {remote.WriteUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
    }

    /// <summary>Имя временного файла рядом с целевым: точка спереди, чтобы не мозолил глаза.</summary>
    private static string TempName(string remoteFile) => CombineRemote(
        ParentRemote(remoteFile),
        "." + Path.GetFileName(remoteFile) + ".part-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Ставит временный файл на место целевого.</summary>
    private static void Replace(SftpClient sftp, string temp, string target)
    {
        try
        {
            // posix-rename@openssh.com заменяет существующий файл одним действием
            sftp.RenameFile(temp, target, isPosix: true);
            return;
        }
        catch (SshException)
        {
            // сервер без этого расширения: убираем прежний файл и переименовываем обычным способом
        }

        if (sftp.Exists(target))
        {
            sftp.DeleteFile(target);
        }

        sftp.RenameFile(temp, target);
    }

    private static void TryDeleteRemote(SftpClient sftp, string remotePath)
    {
        try
        {
            if (sftp.IsConnected && sftp.Exists(remotePath))
            {
                sftp.DeleteFile(remotePath);
            }
        }
        catch (Exception)
        {
            // связь уже оборвана — временный файл останется на сервере, но целевой при этом цел
        }
    }

    /// <summary>
    /// Суммы файлов назначения до копирования. Нужны только там, где размер совпал:
    /// другой размер — и так повод отправить. Свои суммы считаем тут же, параллельно
    /// с серверными и на всех ядрах. Чего на сервере нет — того нет в ответе, и это
    /// обычное дело: такой файл просто отправится.
    /// </summary>
    private static Dictionary<string, string> FetchRemoteHashes(
        SshClient ssh,
        UploadContext ctx,
        IReadOnlyList<FileJob> files,
        Action<string> report,
        CancellationToken ct,
        Action<UploadStep>? step)
    {
        var candidates = files.Where(ctx.NeedsHash).ToList();

        if (candidates.Count == 0)
        {
            report("сверять суммы не с чем: таких файлов на сервере нет или у них другой размер");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        step?.Invoke(UploadStep.CheckingHashes);

        var local = Task.Run(
            () => Parallel.ForEach(
                candidates,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                f => ctx.LocalHash(f.LocalPath)),
            ct);

        var map = RemoteSums(ssh, candidates.Select(f => f.RemotePath).ToList(), ct, out var complaint);

        local.GetAwaiter().GetResult();

        if (map.Count == 0 && complaint.Length > 0)
        {
            // сверять нечем — не беда: значит, отправится всё. Но сказать об этом надо
            report("суммы с сервера получить не удалось, файлы будут отправлены все: " + complaint);
        }
        else
        {
            report($"получено сумм с сервера: {map.Count} из {candidates.Count} файл(ов) того же размера");
        }

        return map;
    }

    /// <summary>
    /// SHA-256 файлов на сервере одной командой: пути уходят на вход списком, а sha256sum
    /// работает сразу на всех ядрах сервера. Где xargs без -0/-P — пачками по сто, как раньше.
    /// </summary>
    internal static Dictionary<string, string> RemoteSums(
        SshClient ssh,
        IReadOnlyList<string> paths,
        CancellationToken ct,
        out string complaint)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        complaint = string.Empty;

        if (paths.Count == 0)
        {
            return map;
        }

        try
        {
            // по 16 путей на запуск: вывод одного запуска укладывается в одну запись в канал,
            // и строки соседних потоков не перемешиваются
            using var cmd = ssh.CreateCommand(
                "n=$(nproc 2>/dev/null || echo 2); xargs -0 -n 16 -P \"$n\" sha256sum --");

            var run = cmd.ExecuteAsync(ct);

            using (var input = new BufferedStream(cmd.CreateInputStream(), 64 * 1024))
            {
                foreach (var path in paths)
                {
                    input.Write(Encoding.UTF8.GetBytes(path));
                    input.WriteByte(0);
                }
            }

            run.GetAwaiter().GetResult();

            ParseSums(cmd.Result, map);
            complaint = cmd.Error?.Trim() ?? string.Empty;

            if (map.Count > 0 || !complaint.Contains("xargs", StringComparison.OrdinalIgnoreCase))
            {
                return map;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            complaint = ex.Message;
        }

        // запасной путь: пачками в командной строке
        map.Clear();
        complaint = string.Empty;

        foreach (var batch in paths.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();

            using var cmd = ssh.CreateCommand("sha256sum -- " + string.Join(" ", batch.Select(ShellQuote)));
            cmd.ExecuteAsync(ct).GetAwaiter().GetResult();

            ParseSums(cmd.Result, map);

            if (complaint.Length == 0 && !string.IsNullOrWhiteSpace(cmd.Error))
            {
                complaint = cmd.Error.Trim();
            }
        }

        return map;
    }

    /// <summary>Разбирает вывод sha256sum: «хеш␠␠путь» построчно.</summary>
    private static void ParseSums(string output, Dictionary<string, string> into)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            var sep = text.IndexOf("  ", StringComparison.Ordinal);

            if (sep is > 0)
            {
                into[text[(sep + 2)..].Trim()] = text[..sep].Trim();
            }
        }
    }

    /// <summary>
    /// Сверяет SHA-256 залитых файлов: локальный хеш считаем сами, удалённый берём
    /// через sha256sum на сервере. Возвращает число расхождений.
    /// </summary>
    public static Task<int> VerifyChecksumsAsync(
        ServerEntry server,
        AppSettings settings,
        IReadOnlyList<UploadedFile> files,
        ConcurrentDictionary<string, string> localHashes,
        Action<string> report,
        CancellationToken ct)
    {
        return Task.Run(
            () =>
            {
                // свои суммы считаем, пока сервер считает свои, — на всех ядрах. Обычно большая
                // часть уже в кэше со сверки перед копированием, остальное дочитывается тут
                var local = Task.Run(
                    () => Parallel.ForEach(
                        files,
                        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                        f => LocalSha256(localHashes, f.LocalPath)),
                    ct);

                using var client = NewSsh(server, settings);
                client.Connect();

                var remote = RemoteSums(client, files.Select(f => f.RemotePath).ToList(), ct, out var complaint);

                client.Disconnect();
                local.GetAwaiter().GetResult();

                if (remote.Count == 0 && complaint.Length > 0)
                {
                    throw new InvalidOperationException(
                        "Не удалось посчитать контрольные суммы на сервере: " + complaint);
                }

                var mismatches = 0;

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var expected = LocalSha256(localHashes, file.LocalPath);

                    if (!remote.TryGetValue(file.RemotePath, out var actual))
                    {
                        mismatches++;
                        report($"НЕ СВЕРЕНО (нет ответа сервера): {file.RemotePath}");
                    }
                    else if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches++;
                        report($"РАСХОЖДЕНИЕ: {file.RemotePath}");
                        report($"    локально: {expected}");
                        report($"    на сервере: {actual}");
                    }
                }

                return mismatches;
            },
            ct);
    }

    /// <summary>По замку на файл, пока его сумма считается: серверы стартуют разом.</summary>
    private static readonly ConcurrentDictionary<string, object> HashLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Сумма локального файла из общего кэша операции. Серверы просят её одновременно —
    /// без замка каждый читал бы тот же файл с диска заново.
    /// </summary>
    private static string LocalSha256(ConcurrentDictionary<string, string> cache, string path)
    {
        // сумма помнится вместе с размером и временем записи: файл, пересобранный по тому же
        // пути, считается заново — иначе сверка сравнивала сервер с суммой прежней версии
        var key = HashKey(path);

        if (cache.TryGetValue(key, out var known))
        {
            return known;
        }

        lock (HashLocks.GetOrAdd(key, _ => new object()))
        {
            var hash = cache.GetOrAdd(key, _ => ComputeLocalSha256(path));
            HashLocks.TryRemove(key, out _);
            return hash;
        }
    }

    /// <summary>Ключ кэша сумм: путь, за ним через перевод строки размер и время записи.</summary>
    internal static string HashKey(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? $"{path}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}"
            : path;
    }

    internal static string ComputeLocalSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Оборачивает строку в одинарные кавычки для безопасной передачи в sh.</summary>
    public static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Доводит только что залитый временный файл до вида, в котором он встанет на место
    /// прежнего: права и владельца берём у заменяемого файла, а если файл новый — у папки,
    /// в которую он ложится; дату — у локального файла.
    ///
    /// Новый файл иначе достаётся тому, под кем мы зашли (обычно root), с правами по умолчанию,
    /// и в чужом дереве — например, в сайте под www-data — оказывается не той принадлежности.
    /// </summary>
    private static void ApplyAttributes(
        SftpClient sftp,
        string remotePath,
        FileInfo local,
        SftpFileAttributes? previous,
        SftpFileAttributes? parent,
        UploadOptions options,
        Action<string> report)
    {
        // образец: прежний файл, а для нового — папка, в которую он ложится
        var sample = previous ?? parent;

        if (sample is null && !options.PreserveTimestamps)
        {
            return;
        }

        SftpFileAttributes? attrs = null;

        try
        {
            attrs = sftp.GetAttributes(remotePath);

            if (sample is not null)
            {
                CopyPermissions(sample, attrs);

                // права папки переносятся на файл без «входа»: 755 у папки — это 644 у файла
                if (previous is null)
                {
                    attrs.OwnerCanExecute = false;
                    attrs.GroupCanExecute = false;
                    attrs.OthersCanExecute = false;
                }
            }

            if (options.PreserveTimestamps)
            {
                // SFTP создаёт файл заново, дата изменения стала текущей — возвращаем исходную
                attrs.LastWriteTimeUtc = local.LastWriteTimeUtc;
                attrs.LastAccessTimeUtc = local.LastAccessTimeUtc;
            }

            sftp.SetAttributes(remotePath, attrs);
        }
        catch (Exception ex)
        {
            attrs = null;
            report($"не удалось перенести права и дату на {remotePath}: {ex.Message}");
        }

        // атрибуты только что проставлены — владельца ставим поверх них, не перечитывая
        if (sample is not null)
        {
            CopyOwner(sftp, remotePath, sample, report, attrs);
        }
    }

    /// <summary>
    /// Переносит права один в один, флаг за флагом.
    ///
    /// Через SetPermissions этого делать нельзя: SSH.NET читает переданное число как три
    /// восьмеричные цифры («644» — это цифры 6, 4 и 4), а не как набор битов. Набор битов
    /// маски 644 — это число 420, и оно принималось за цифры 4, 2, 0, то есть за права 0420:
    /// после копирования файл читался только владельцем. Так и «умирали» сайты — веб-сервер
    /// переставал читать собственные файлы.
    /// </summary>
    private static void CopyPermissions(SftpFileAttributes from, SftpFileAttributes to)
    {
        to.OwnerCanRead = from.OwnerCanRead;
        to.OwnerCanWrite = from.OwnerCanWrite;
        to.OwnerCanExecute = from.OwnerCanExecute;
        to.GroupCanRead = from.GroupCanRead;
        to.GroupCanWrite = from.GroupCanWrite;
        to.GroupCanExecute = from.GroupCanExecute;
        to.OthersCanRead = from.OthersCanRead;
        to.OthersCanWrite = from.OthersCanWrite;
        to.OthersCanExecute = from.OthersCanExecute;
    }

    /// <summary>
    /// Переносит владельца и группу — отдельным запросом: сменить владельца может только root,
    /// и отказ в этом не должен уносить с собой права, которые уже проставлены.
    /// </summary>
    private static void CopyOwner(
        SftpClient sftp,
        string remotePath,
        SftpFileAttributes sample,
        Action<string> report,
        SftpFileAttributes? current = null)
    {
        try
        {
            var attrs = current ?? sftp.GetAttributes(remotePath);

            if (attrs.UserId == sample.UserId && attrs.GroupId == sample.GroupId)
            {
                return;
            }

            attrs.UserId = sample.UserId;
            attrs.GroupId = sample.GroupId;

            sftp.SetAttributes(remotePath, attrs);
        }
        catch (Exception ex)
        {
            report($"владелец не перенесён на {remotePath} (сменить его может только root): {ex.Message}");
        }
    }

    /// <summary>Проставляет удалённому объекту дату изменения и доступа локального оригинала.</summary>
    private static void PreserveTimestamps(
        SftpClient sftp,
        string remotePath,
        DateTime writeUtc,
        DateTime accessUtc,
        Action<string> report)
    {
        try
        {
            var attrs = sftp.GetAttributes(remotePath);
            attrs.LastWriteTimeUtc = writeUtc;
            attrs.LastAccessTimeUtc = accessUtc;
            sftp.SetAttributes(remotePath, attrs);
        }
        catch (Exception ex)
        {
            report($"не удалось перенести дату изменения на {remotePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Новые папки — одной командой на все: запросами SFTP на каждую, с переносом прав
    /// и владельца, тысячи папок создавались часами. Права и владелец — как у ближайшей
    /// папки выше, которая была до нас, так же как по одной. Что не вышло, досоздаст
    /// EnsureDir по одной — там же скажется и причина.
    /// </summary>
    private static void MakeDirs(SshClient ssh, UploadContext ctx, IReadOnlyList<DirJob> dirs, Action<string> report, CancellationToken ct)
    {
        if (ctx.Inventory is not { } inventory)
        {
            return;
        }

        var missing = dirs
            .Select(d => NormalizeRemote(d.RemotePath))
            .Where(p => !inventory.IsBlind(p) && ctx.Known(p) is not { IsDirectory: true })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        var pending = missing.ToHashSet(StringComparer.Ordinal);

        var chunks = missing
            .GroupBy(p => DirSample(ctx, pending, p))
            .SelectMany(g => g.Chunk(200).Select(paths => (Sample: g.Key, Paths: paths)))
            .ToList();

        // mkdir -p досоздаёт родителей сам, так что порядок пачек не важен:
        // права и владельца каждой папке всё равно проставит её собственная пачка
        var script = new StringBuilder();

        for (var i = 0; i < chunks.Count; i++)
        {
            var (sample, paths) = chunks[i];
            var list = string.Join(" ", paths.Select(ShellQuote));

            script.Append("mkdir -p -- ").Append(list).Append(" 2>/dev/null || echo '").Append(FailMarker).Append(i).Append("'\n");

            if (sample.Mode >= 0)
            {
                script.Append("chmod ").Append(Convert.ToString(sample.Mode, 8)).Append(" -- ").Append(list)
                    .Append(" 2>/dev/null || echo '").Append(FailMarker).Append("-1'\n");
            }

            if (sample.Uid >= 0 && sample.Gid >= 0 && (sample.Uid != inventory.Uid || sample.Gid != inventory.Gid))
            {
                script.Append("chown ").Append(sample.Uid).Append(':').Append(sample.Gid).Append(" -- ").Append(list)
                    .Append(" 2>/dev/null || echo '").Append(FailMarker).Append("-1'\n");
            }
        }

        HashSet<int> failed;

        try
        {
            failed = FailedLines(RunInput(ssh, script.ToString(), ct).Output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return;
        }

        var created = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            if (failed.Contains(i))
            {
                continue;
            }

            foreach (var path in chunks[i].Paths)
            {
                ctx.MarkCreated(path);
                created++;
            }
        }

        ctx.Stats.Dirs += created;

        lock (ctx.Stats.CreatedDirs)
        {
            ctx.Stats.CreatedDirs.AddRange(chunks.Where((_, i) => !failed.Contains(i)).SelectMany(c => c.Paths));
        }

        report($"создано папок: {created} — одной командой");

        if (failed.Contains(-1))
        {
            report("права или владелец перенесены не на все новые папки (владельца может сменить только root)");
        }
    }

    /// <summary>Права и владелец ближайшей папки выше, которая была на сервере до нас; -1 — неизвестно.</summary>
    private static (int Mode, int Uid, int Gid) DirSample(UploadContext ctx, HashSet<string> pending, string path)
    {
        var parent = ParentRemote(path);

        while (pending.Contains(parent))
        {
            parent = ParentRemote(parent);
        }

        if (ctx.Known(parent) is { IsDirectory: true, Mode: >= 0, Uid: >= 0, Gid: >= 0 } known)
        {
            return (known.Mode, known.Uid, known.Gid);
        }

        return ctx.DirAttributes(parent) is { } a ? (ModeOf(a), a.UserId, a.GroupId) : (-1, -1, -1);
    }

    private static void EnsureDirectory(SftpClient sftp, string remoteDir, Counters stats, Action<string> report)
    {
        var path = NormalizeRemote(remoteDir);

        if (path == "/" || sftp.Exists(path))
        {
            return;
        }

        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;

        foreach (var part in parts)
        {
            current += "/" + part;

            if (sftp.Exists(current))
            {
                continue;
            }

            // образец берём до создания: это ближайшая существующая папка выше
            var parent = TryGetAttributes(sftp, ParentRemote(current));

            sftp.CreateDirectory(current);
            stats.Dirs++;

            lock (stats.CreatedDirs)
            {
                stats.CreatedDirs.Add(current);
            }

            report($"создана папка: {current}");

            // иначе папка достаётся тому, под кем мы зашли, с правами по умолчанию —
            // в чужом дереве (сайт под www-data) это ломает работу с ней
            if (parent is not null)
            {
                try
                {
                    var attrs = sftp.GetAttributes(current);
                    CopyPermissions(parent, attrs);
                    sftp.SetAttributes(current, attrs);
                }
                catch (Exception ex)
                {
                    report($"не удалось перенести права на {current}: {ex.Message}");
                }

                CopyOwner(sftp, current, parent, report);
            }
        }
    }

    /// <summary>Атрибуты объекта на сервере или null, если его там нет.</summary>
    private static SftpFileAttributes? TryGetAttributes(SftpClient sftp, string remotePath)
    {
        try
        {
            return sftp.GetAttributes(remotePath);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Что известно об объекте на сервере. Attributes — если уже спрашивали SFTP;
    /// Mode, Uid, Gid — из описи (права числом, как в chmod), -1 — неизвестно.
    /// </summary>
    private sealed record RemoteStat(
        bool IsDirectory,
        long Size,
        DateTime WriteUtc,
        SftpFileAttributes? Attributes = null,
        int Mode = -1,
        int Uid = -1,
        int Gid = -1);

    private sealed record FileJob(string LocalPath, string RemotePath);

    private sealed record DirJob(string LocalPath, string RemotePath);

    /// <summary>Всё, что отправляется: места назначения выбранного, папки (родитель раньше детей) и файлы.</summary>
    private sealed class Plan
    {
        public List<string> Roots { get; } = [];

        public List<DirJob> Dirs { get; } = [];

        public List<FileJob> Files { get; } = [];

        /// <summary>Сколько файлов и папок отсеяно масками (папка — одна, со всем содержимым).</summary>
        public int Excluded { get; set; }
    }

    /// <summary>Опись мест назначения, снятая одной командой find.</summary>
    private sealed class Inventory
    {
        public Dictionary<string, RemoteStat> Entries { get; } = new(StringComparer.Ordinal);

        /// <summary>Символические ссылки: find в них не заходил.</summary>
        public HashSet<string> Links { get; } = new(StringComparer.Ordinal);

        /// <summary>Под кем мы на сервере: 0 — root, ему можно ставить файлам владельца.</summary>
        public int? Uid { get; set; }

        public int? Gid { get; set; }

        /// <summary>GNU tar — им принимается пачка файлов одним потоком.</summary>
        public bool GnuTar { get; set; }

        /// <summary>gzip — пачка едет сжатой.</summary>
        public bool Gzip { get; set; }

        /// <summary>Путь лежит за ссылкой — о нём опись ничего не знает, спрашивать отдельно.</summary>
        public bool IsBlind(string path)
        {
            if (Links.Count == 0)
            {
                return false;
            }

            for (var p = path; ; p = ParentRemote(p))
            {
                if (Links.Contains(p))
                {
                    return true;
                }

                if (p == "/")
                {
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Всё, что нужно потокам отправки одного сервера: подключение, настройки, опись,
    /// суммы, снятые до начала, и кэш локальных сумм — один на всю операцию,
    /// чтобы один и тот же файл не пересчитывался для каждого сервера заново.
    /// </summary>
    private sealed class UploadContext(
        SftpClient sftp,
        UploadOptions options,
        ConcurrentDictionary<string, string> localHashes,
        Inventory? inventory,
        Counters stats,
        Action<string> report)
    {
        private readonly ConcurrentDictionary<string, byte> _knownDirs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _touched = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SftpFileAttributes?> _dirAttrs = new(StringComparer.Ordinal);
        private readonly object _dirLock = new();

        public SftpClient Sftp { get; } = sftp;

        public UploadOptions Options { get; } = options;

        public Counters Stats { get; } = stats;

        public Action<string> Report { get; } = report;

        public IReadOnlyDictionary<string, string>? RemoteHashes { get; set; }

        /// <summary>Счётчик отправленного — для полосы под выводом.</summary>
        public TransferMeter Meter { get; init; } = new(null);

        /// <summary>Опись сервера; null — её снять не удалось, и всё едет по SFTP.</summary>
        public Inventory? Inventory => inventory;

        public string LocalHash(string path) => LocalSha256(localHashes, path);

        /// <summary>Что о пути сказала опись. null — нет на сервере или описи нет вовсе.</summary>
        public RemoteStat? Known(string path) =>
            inventory is not null && !inventory.IsBlind(path) ? inventory.Entries.GetValueOrDefault(path) : null;

        /// <summary>Есть ли объект на сервере и какой он: из описи, а без неё — запросом.</summary>
        public RemoteStat? Stat(string path)
        {
            if (inventory is not null && !inventory.IsBlind(path))
            {
                return inventory.Entries.GetValueOrDefault(path);
            }

            var attrs = TryGetAttributes(Sftp, path);
            return attrs is null ? null : new RemoteStat(attrs.IsDirectory, attrs.Size, attrs.LastWriteTimeUtc, attrs);
        }

        /// <summary>Сумму стоит спрашивать, только если файл есть и размер у него тот же.</summary>
        public bool NeedsHash(FileJob job)
        {
            if (inventory is null || inventory.IsBlind(job.RemotePath))
            {
                return true;
            }

            return inventory.Entries.TryGetValue(job.RemotePath, out var remote)
                && !remote.IsDirectory
                && remote.Size == new FileInfo(job.LocalPath).Length;
        }

        public void MarkKnown(string path) => _knownDirs[NormalizeRemote(path)] = 0;

        /// <summary>Папка на месте: из описи — даром, иначе создаётся с правами и владельцем родителя.</summary>
        public void EnsureDir(string path)
        {
            path = NormalizeRemote(path);

            if (_knownDirs.ContainsKey(path))
            {
                return;
            }

            if (Known(path) is { IsDirectory: true })
            {
                _knownDirs[path] = 0;
                return;
            }

            lock (_dirLock)
            {
                if (_knownDirs.ContainsKey(path))
                {
                    return;
                }

                var before = Stats.Dirs;
                EnsureDirectory(Sftp, path, Stats, Report);

                // новая папка меняет время и самой себе, и тем, кто выше
                if (Stats.Dirs != before)
                {
                    for (var p = path; ; p = ParentRemote(p))
                    {
                        _touched[p] = 0;

                        if (p == "/")
                        {
                            break;
                        }
                    }
                }

                _knownDirs[path] = 0;
            }
        }

        /// <summary>Папку создали одной командой со всеми: запомнить её и сбитое время у неё и у всех выше.</summary>
        public void MarkCreated(string path)
        {
            path = NormalizeRemote(path);
            _knownDirs[path] = 0;

            for (var p = path; ; p = ParentRemote(p))
            {
                _touched[p] = 0;

                if (p == "/")
                {
                    break;
                }
            }
        }

        /// <summary>В папку что-то записали — её время сбито и его надо вернуть.</summary>
        public void Touch(string dir) => _touched[NormalizeRemote(dir)] = 0;

        public bool IsTouched(string dir) => _touched.ContainsKey(NormalizeRemote(dir));

        /// <summary>Права и владелец папки — образец для новых файлов в ней. Спрашиваются раз на папку.</summary>
        public SftpFileAttributes? DirAttributes(string dir) =>
            _dirAttrs.GetOrAdd(NormalizeRemote(dir), d => TryGetAttributes(Sftp, d));
    }

    private sealed class Counters
    {
        public int Files;
        public int Skipped;
        public int UpToDate;
        public int Redated;
        public int Dirs;
        public long Bytes;
        public readonly List<UploadedFile> Uploaded = [];

        /// <summary>Созданные папки — списком в конце вывода, как и отправленные файлы.</summary>
        public readonly List<string> CreatedDirs = [];
    }
}

/// <summary>
/// Вывод команды по мере поступления. Читается посимвольно, а не строками: вопрос вроде
/// «Continue? [Y/n]» приходит без перевода строки, и построчное чтение его не показало бы,
/// пока на него не ответят. Незаконченная строка показывается через долю секунды тишины,
/// а через секунду считается, что команда ждёт ответа.
/// </summary>
internal sealed class LinePump(
    StringBuilder sink,
    Action<string>? live,
    Action<int, int>? onJob,
    Action<string>? onTail)
{
    private static readonly TimeSpan ShowTailAfter = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WaitingAfter = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly StringBuilder _partial = new();
    private DateTime _lastData = DateTime.UtcNow;
    private bool _tailShown;
    private bool _afterCr;

    /// <summary>Незаконченная строка висит секунду и больше — похоже на вопрос.</summary>
    public bool Waiting
    {
        get
        {
            lock (_lock)
            {
                return _partial.Length > 0 && !LooksLikeJob() && DateTime.UtcNow - _lastData >= WaitingAfter;
            }
        }
    }

    public void Run(Stream? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            var buffer = new char[4096];
            int read;

            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                Feed(buffer, read);
            }
        }
        catch (Exception ex)
        {
            lock (sink)
            {
                sink.AppendLine("(чтение вывода прервано: " + ex.Message + ")");
            }
        }

        lock (_lock)
        {
            if (_partial.Length > 0)
            {
                EmitLine();
            }
        }
    }

    /// <summary>Показывает незаконченную строку, если на ней всё затихло.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (_tailShown || _partial.Length == 0 || LooksLikeJob() || DateTime.UtcNow - _lastData < ShowTailAfter)
            {
                return;
            }

            _tailShown = true;
            onTail?.Invoke(_partial.ToString());
        }
    }

    /// <summary>
    /// Дописывает ответ к строке-вопросу и закрывает её: без терминала сервер набранное
    /// не повторяет, и в выводе вопрос висел бы без ответа. Пароль не показываем.
    /// </summary>
    public bool Commit(string answer)
    {
        lock (_lock)
        {
            if (_partial.Length == 0)
            {
                return false;
            }

            var prompt = _partial.ToString();
            var secret = prompt.Contains("pass", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("пароль", StringComparison.OrdinalIgnoreCase);

            _partial.Append(secret && answer.Length > 0 ? "********" : answer);
            _lastData = DateTime.UtcNow;
            EmitLine();
            return true;
        }
    }

    /// <summary>Ответ без висящего вопроса — отдельной строкой.</summary>
    public void Echo(string answer)
    {
        lock (_lock)
        {
            if (answer.Length > 0)
            {
                lock (sink)
                {
                    sink.AppendLine(answer);
                }

                live?.Invoke(answer);
            }
        }
    }

    private void Feed(char[] buffer, int count)
    {
        lock (_lock)
        {
            _lastData = DateTime.UtcNow;

            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];

                if (_afterCr)
                {
                    _afterCr = false;

                    if (c == '\n')
                    {
                        continue;
                    }
                }

                if (c == '\n')
                {
                    EmitLine();
                }
                else if (c == '\r')
                {
                    EmitLine();
                    _afterCr = true;
                }
                else
                {
                    _partial.Append(c);
                }
            }
        }
    }

    private void EmitLine()
    {
        var line = _partial.ToString();
        _partial.Clear();

        if (_tailShown)
        {
            _tailShown = false;
            onTail?.Invoke(string.Empty);
        }

        // служебная строка пролога: в вывод она не идёт ни в каком виде
        if (line.StartsWith(SshService.JobMarker, StringComparison.Ordinal))
        {
            SshService.ReportJob(line, onJob);
            return;
        }

        lock (sink)
        {
            sink.AppendLine(line);
        }

        live?.Invoke(line);
    }

    /// <summary>Начало служебной строки пролога — её не показываем и вопросом не считаем.</summary>
    private bool LooksLikeJob()
    {
        var text = _partial.ToString();

        return text.Length < SshService.JobMarker.Length
            ? SshService.JobMarker.StartsWith(text, StringComparison.Ordinal)
            : text.StartsWith(SshService.JobMarker, StringComparison.Ordinal);
    }
}

/// <summary>
/// Ввод идущей команды: строка ответа под выводом пишет сюда. Живёт, пока команда идёт;
/// по её окончании закрывается сам.
/// </summary>
public sealed class CommandInput
{
    private readonly Stream _stream;
    private readonly LinePump _out;
    private readonly LinePump _err;
    private readonly Lock _lock = new();
    private bool _closed;

    internal CommandInput(Stream stream, LinePump output, LinePump errors)
    {
        _stream = stream;
        _out = output;
        _err = errors;
    }

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return !_closed;
            }
        }
    }

    /// <summary>Отправляет строку ответа с переводом строки — как Enter в терминале.</summary>
    public bool Send(string text)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            try
            {
                _stream.Write(new UTF8Encoding(false).GetBytes(text + "\n"));
                _stream.Flush();
            }
            catch (Exception)
            {
                _closed = true;
                return false;
            }
        }

        // вопрос мог прийти и в обычный вывод, и в вывод ошибок (read -p пишет туда)
        if (!_out.Commit(text) && !_err.Commit(text))
        {
            _out.Echo(text);
        }

        return true;
    }

    /// <summary>Конец ввода — то же, что Ctrl+D в терминале.</summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
                // канал уже закрыт
            }
        }
    }
}
