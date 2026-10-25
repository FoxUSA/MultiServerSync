using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace MultiServerSync.Launcher;

/// <summary>
/// Единственный файл в корне. Запускает Data\MultiServerSync.App.exe.
///
/// Он же устанавливает обновление. Программа сама себя заменить не может — она
/// скачивает и складывает готовую замену в Update\staged, а лаунчер дожидается,
/// пока она закроется, и подменяет ровно две вещи: папку Data целиком и себя.
/// Ход установки виден в окне (<see cref="InstallWindow"/>). UserData не трогается никогда.
/// </summary>
internal static class Program
{
    private const string Product = "MultiServerSync";
    private const string LauncherName = "MultiServerSync.exe";
    private const string AppExeName = "MultiServerSync.App.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var dataDir = Path.Combine(root, "Data");

        CleanLeftovers(root);

        var (pid, rest) = SplitArgs(args);
        var staged = Path.Combine(root, "Update", "staged");
        string? oldLauncher = null;

        // обновление применяется и без ключа: если прошлый раз прервали, оно доедет при обычном запуске
        if (File.Exists(Path.Combine(staged, "update.json")))
        {
            // оформление то же, что у программы: WPF UI и тема из настроек пользователя
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            ApplyTheme(root);

            var window = new InstallWindow(f => ApplyUpdate(f, root, staged, pid, out oldLauncher));
            app.Run(window);
        }

        var exe = Path.Combine(dataDir, AppExeName);

        if (!File.Exists(exe))
        {
            Message(
                $"Не найден файл программы:\n{exe}\n\n" +
                "Проверьте, что рядом с этим лаунчером есть папка Data с содержимым программы.");
            return 2;
        }

        try
        {
            var info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = dataDir,
                UseShellExecute = false,
            };

            // момент старта лаунчера: программа пишет в журнал, сколько занял запуск целиком
            info.Environment["MSS_LAUNCHER_START"] = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString();

            foreach (var arg in rest)
            {
                info.ArgumentList.Add(arg);
            }

            Process.Start(info);
        }
        catch (Exception ex)
        {
            Message("Не удалось запустить программу:\n" + ex.Message);
            return 1;
        }

        // прежний лаунчер — это мы сами: пока работаем, его не удалить, добивает его тихий cmd после выхода
        if (oldLauncher is not null)
        {
            DeleteAfterExit(oldLauncher);
        }

        return 0;
    }

    /// <summary>Тема как в программе: Light | Dark | System из UserData\settings.json, по умолчанию тёмная.</summary>
    private static void ApplyTheme(string root)
    {
        var theme = "Dark";

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "UserData", "settings.json")));

            if (doc.RootElement.TryGetProperty("Theme", out var value) && value.ValueKind == JsonValueKind.String)
            {
                theme = value.GetString() ?? theme;
            }
        }
        catch
        {
            // настроек ещё нет или не читаются — остаётся тёмная, как у программы по умолчанию
        }

        switch (theme)
        {
            case "Light":
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                break;
            case "System":
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                break;
            default:
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                break;
        }
    }

    // ── установка обновления ─────────────────────────────────────────────────

    /// <summary>
    /// Шаги 3–7 общего списка в <see cref="InstallWindow"/>. Старое удаляется не сразу,
    /// а сперва отодвигается в Data.old и MultiServerSync.exe.old: если новое не встанет,
    /// прежняя версия возвращается на место и программа запускается как раньше.
    /// </summary>
    private static bool ApplyUpdate(InstallWindow form, string root, string staged, int pid, out string? oldLauncher)
    {
        oldLauncher = null;

        var dataDir = Path.Combine(root, "Data");
        var newData = Path.Combine(staged, "Data");
        var backup = Path.Combine(root, "Data.old");
        var self = Path.Combine(root, LauncherName);
        var newLauncher = Path.Combine(staged, LauncherName);

        // ── 3. закрыть программу
        form.Step(3);
        form.Progress("Ждём, пока программа закроется…");

        if (pid > 0)
        {
            WaitForExit(pid);
        }

        if (!WaitUntilFree(Path.Combine(dataDir, AppExeName), TimeSpan.FromSeconds(30)))
        {
            return Fail(form, "программа не закрылась за 30 секунд — папка Data занята");
        }

        if (!File.Exists(Path.Combine(newData, AppExeName)))
        {
            return Fail(form, $"в подготовленной папке Data нет {AppExeName}");
        }

        // ── 4. убрать старое
        form.Step(4, "Убираем старую папку Data…");
        DeleteDirectory(backup);

        if (Directory.Exists(dataDir))
        {
            try
            {
                ClearReadOnly(new DirectoryInfo(dataDir));
                Directory.Move(dataDir, backup);
            }
            catch (Exception ex)
            {
                return Fail(form, "не удалось убрать прежнюю папку Data: " + ex.Message);
            }
        }

        // работающий exe удалить нельзя, а переименовать — можно
        var replaceLauncher = File.Exists(newLauncher) && !SameFile(newLauncher, self);

        if (replaceLauncher)
        {
            form.Progress("Убираем старый MultiServerSync.exe…");

            try
            {
                var old = self + ".old";

                if (File.Exists(old))
                {
                    File.Delete(old);
                }

                File.Move(self, old);
                oldLauncher = old;
            }
            catch
            {
                // не страшно: Data обновится, лаунчер заменится в следующий раз
                replaceLauncher = false;
            }
        }

        // ── 5. поставить новое
        form.Step(5, "Ставим новую папку Data…");

        try
        {
            Directory.Move(newData, dataDir);
        }
        catch (Exception ex)
        {
            Rollback(dataDir, backup, self, oldLauncher);
            oldLauncher = null;
            return Fail(form, "не удалось поставить новую папку Data: " + ex.Message);
        }

        if (replaceLauncher)
        {
            form.Progress("Ставим новый MultiServerSync.exe…");

            try
            {
                File.Copy(newLauncher, self, true);
            }
            catch
            {
                // без лаунчера в корне программу не запустить ярлыком — возвращаем прежний
                try
                {
                    File.Move(oldLauncher!, self);
                }
                catch
                {
                    // хуже уже не сделать
                }

                oldLauncher = null;
            }
        }

        // ── 6. уборка
        form.Step(6, "Удаляем старые файлы…");
        DeleteWithProgress(backup, form);

        form.Progress("Удаляем временную папку Update…");
        DeleteDirectory(Path.Combine(root, "Update"));

        // ── 7. сам запуск — в Main, после закрытия окна
        form.Step(7, "Запускаем MultiServerSync…");
        Thread.Sleep(600);

        return true;
    }

    private static bool Fail(InstallWindow form, string reason)
    {
        form.Fail(
            "Обновление не установилось: " + reason + ".\n\n"
            + "Программа запустится в прежней версии, скачанное обновление осталось "
            + "в папке Update и установится при следующем запуске.");
        return false;
    }

    /// <summary>Новое не встало: вернуть прежние Data и лаунчер, чтобы программа запустилась.</summary>
    private static void Rollback(string dataDir, string backup, string self, string? oldLauncher)
    {
        try
        {
            if (!Directory.Exists(dataDir) && Directory.Exists(backup))
            {
                Directory.Move(backup, dataDir);
            }
        }
        catch
        {
            // хуже уже не сделать
        }

        try
        {
            if (oldLauncher is not null && !File.Exists(self))
            {
                File.Move(oldLauncher, self);
            }
        }
        catch
        {
            // хуже уже не сделать
        }
    }

    /// <summary>Удаление старой Data по файлу — чтобы полоса двигалась, а не стояла.</summary>
    private static void DeleteWithProgress(string path, InstallWindow form)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            ClearReadOnly(new DirectoryInfo(path));

            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);

            for (var i = 0; i < files.Length; i++)
            {
                try
                {
                    File.Delete(files[i]);
                }
                catch
                {
                    // занятый файл добьёт CleanLeftovers при следующем запуске
                }

                if (i % 10 == 0 || i == files.Length - 1)
                {
                    form.Progress(
                        $"Удаляем старые файлы: {i + 1} из {files.Length}",
                        (i + 1) * 100.0 / files.Length);
                }
            }
        }
        catch
        {
            // ниже всё равно попробуем удалить целиком
        }

        DeleteDirectory(path);
    }

    /// <summary>Удалить файл, когда этот процесс завершится: свой exe при жизни не удалить.</summary>
    private static void DeleteAfterExit(string path)
    {
        try
        {
            var info = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{path}\"",
            };

            Process.Start(info);
        }
        catch
        {
            // не вышло — удалится при следующем запуске (CleanLeftovers)
        }
    }

    /// <summary>Дождаться, пока файл перестанет быть занятым: программа закрывается не мгновенно.</summary>
    private static bool WaitUntilFree(string probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (!File.Exists(probe))
            {
                return true;
            }

            try
            {
                using var stream = File.Open(probe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                Thread.Sleep(300);
            }
        }
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(30000);
        }
        catch
        {
            // процесса уже нет — этого и ждали
        }
    }

    /// <summary>Разобрать «--apply-update &lt;pid&gt;»; остальные аргументы уходят программе.</summary>
    private static (int Pid, List<string> Passthrough) SplitArgs(string[] args)
    {
        var pid = 0;
        var rest = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--apply-update", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed))
                {
                    pid = parsed;
                    i++;
                }

                continue;
            }

            rest.Add(args[i]);
        }

        return (pid, rest);
    }

    /// <summary>Убрать хвосты прошлого обновления.</summary>
    private static void CleanLeftovers(string root)
    {
        DeleteDirectory(Path.Combine(root, "Data.old"));

        try
        {
            foreach (var file in Directory.GetFiles(root, "*.old"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // ещё занят — удалится в другой раз
                }
            }
        }
        catch
        {
            // корень недоступен — не наша забота
        }
    }

    private static bool SameFile(string first, string second)
    {
        try
        {
            if (!File.Exists(first) || !File.Exists(second))
            {
                return false;
            }

            if (new FileInfo(first).Length != new FileInfo(second).Length)
            {
                return false;
            }

            return File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second));
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                ClearReadOnly(new DirectoryInfo(path));
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // остаётся до следующего запуска
        }
    }

    /// <summary>Папкам программы проставлено «только чтение» ради иконок — иначе они не удаляются.</summary>
    private static void ClearReadOnly(DirectoryInfo dir)
    {
        dir.Attributes = FileAttributes.Directory;

        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }

        foreach (var sub in dir.GetDirectories())
        {
            ClearReadOnly(sub);
        }
    }

    private static void Message(string text) =>
        MessageBoxW(IntPtr.Zero, text, Product, 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
