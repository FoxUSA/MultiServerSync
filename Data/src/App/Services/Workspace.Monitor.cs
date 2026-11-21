using System.IO;
using System.Reflection;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>«Включить сбор» раздела «Мониторинг» — строкой в терминале его скрытой вкладки, как «Архивировать» в «Проводнике».</summary>
public sealed partial class Workspace
{
    /// <summary>Куда встаёт сборщик и что ещё появляется на сервере — одни и те же слова в вопросе, выводе и журнале.</summary>
    public const string MonitorScript = "/usr/local/lib/mss-monitor/collect.sh";

    public const string MonitorUnit = "/etc/systemd/system/mss-monitor.service";

    /// <summary>Как убрать сборщик руками — одной строкой в журнал.</summary>
    public const string MonitorRemoveHint =
        "systemctl disable --now mss-monitor; rm -rf /usr/local/lib/mss-monitor " + MonitorProbe.DataDir + " " + MonitorUnit + "; systemctl daemon-reload";

    private MonitorRange _monitorRange;

    /// <summary>«Мониторинг»: сервер вкладки — отмеченный в её панели слева (только один); null — не выбран.</summary>
    public ServerEntry? MonitorServer => CopyTargets.FirstOrDefault();

    /// <summary>«Мониторинг»: период вкладки, свой у каждой, помнится между запусками.</summary>
    public MonitorRange MonitorRange
    {
        get => _monitorRange;
        set
        {
            if (Set(ref _monitorRange, value))
            {
                AppState.Current.SaveWorkspaces();
            }
        }
    }

    /// <summary>Файл, вшитый в программу: скрипт сборщика и служба systemd (Assets\Monitor).</summary>
    private static string MonitorAsset(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().First(n => n.EndsWith(".Monitor." + name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Ставит сборщик истории на сервер: скрипт на sh + awk, служба mss-monitor.service, история в /var/lib/mss-monitor.
    /// Без systemd (роутеры) — не ставит, а говорит почему: «Сейчас» работает и без сборщика.
    /// Не root — через sudo, как установка архиватора. Каждый шаг — в вывод строки, итог — в журнал.
    /// </summary>
    /// <returns>Сбор включён.</returns>
    public async Task<bool> RunMonitorInstallAsync(ServerEntry server)
    {
        var settings = AppState.Current.Settings;
        var feed = MonitorFeed.For(server);
        var installed = false;

        SetScope();
        AppState.Current.RequestTerminal();
        ShowResults(CommandBatch);
        feed.SetInstalling(true);

        // ход и итог — на ярлыке вкладки, как у проверки связи и прочих действий с сервером
        ShowStatus("сбор истории", string.Empty, WorkspaceStatus.Running);

        try
        {
            await CommandBatch.RunAsync(
                "Включить сбор истории",
                [server],
                async (target, result, ct) =>
                {
                    result.Append(BlockParams);
                    result.Append("Сборщик: " + MonitorScript);
                    result.Append("Служба: mss-monitor.service — замер каждые 10 с");
                    result.Append("История: " + MonitorProbe.DataDir + " — минуты за 48 ч, часы за 180 дней");
                    result.Message = "подключение…";

                    // 1. есть ли systemd, awk и под кем вошли
                    result.Append("── проверка ──");
                    var probe = await SshService.RunCommandAsync(target, settings,
                        "[ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1 && echo SYSTEMD:1; "
                        + "command -v awk >/dev/null 2>&1 && echo AWK:1; "
                        + "echo \"UID:$(id -u)\"; "
                        + "df -Pk " + MonitorProbe.DataDir.Replace("/mss-monitor", string.Empty, StringComparison.Ordinal) + " 2>/dev/null | awk 'NR==2 {print \"FREE:\" $4}'",
                        ct);

                    if (probe.Interruption is { } lost)
                    {
                        Fail(result, lost);
                        return;
                    }

                    string Field(string key) => probe.StdOut.Split('\n')
                        .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..].Trim() ?? string.Empty;

                    var systemd = Field("SYSTEMD") == "1";
                    var awk = Field("AWK") == "1";
                    var root = Field("UID") == "0";
                    var free = long.TryParse(Field("FREE"), out var kb) ? kb * 1024 : -1;

                    result.Append("systemd: " + (systemd ? "есть" : "нет")
                        + " · awk: " + (awk ? "есть" : "нет")
                        + " · вход: " + (root ? "root" : "не root, через sudo")
                        + (free >= 0 ? " · свободно на диске " + MonitorFormat.Bytes(free) : string.Empty));

                    // роутер и прочие системы без systemd: ничего не ставим — это не сбой, а ответ
                    if (!systemd)
                    {
                        result.Append("── итог ──");
                        result.Append("На сервере нет systemd — сбор истории на нём не включить. «Сейчас» работает и без него.");
                        result.Message = "нет systemd — сбор не включён";
                        result.Warning = true;
                        result.Status = OperationStatus.Success;
                        return;
                    }

                    if (!awk)
                    {
                        result.Append("── итог ──");
                        result.Append("На сервере нет awk — сборщику нечем считать. «Сейчас» работает и без него.");
                        result.Message = "нет awk — сбор не включён";
                        result.Warning = true;
                        result.Status = OperationStatus.Success;
                        return;
                    }

                    // 2. скрипт, служба, папка истории — одним заходом; служба перезапускается, если уже была
                    var script =
                        "set -e\n"
                        + "mkdir -p /usr/local/lib/mss-monitor " + MonitorProbe.DataDir + "\n"
                        + "cat > " + MonitorScript + " <<'MSS_COLLECT_EOF'\n" + MonitorAsset("collect.sh").TrimEnd('\n') + "\nMSS_COLLECT_EOF\n"
                        + "chmod 755 " + MonitorScript + "\n"
                        + "cat > " + MonitorUnit + " <<'MSS_UNIT_EOF'\n" + MonitorAsset("mss-monitor.service").TrimEnd('\n') + "\nMSS_UNIT_EOF\n"
                        + "systemctl daemon-reload\n"
                        + "systemctl enable mss-monitor.service >/dev/null 2>&1\n"
                        + "systemctl restart mss-monitor.service\n"
                        + "sleep 2\n"
                        + "echo \"STATE:$(systemctl is-active mss-monitor.service 2>/dev/null)\"\n";

                    result.Append("── установка ──");
                    result.Append((root ? "$ " : "$ sudo ") + "mkdir -p /usr/local/lib/mss-monitor " + MonitorProbe.DataDir);
                    result.Append("$ cat > " + MonitorScript + " · " + MonitorUnit);
                    result.Append("$ systemctl daemon-reload && systemctl enable mss-monitor && systemctl restart mss-monitor");
                    result.Message = "ставлю сборщик…";

                    var setup = await SshService.RunScriptAsync(target, settings, script, sudo: !root, live: null, ct,
                        limit: TimeSpan.FromMinutes(2));

                    var output = setup.StdOut.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

                    foreach (var line in output.Where(l => l.Trim().Length > 0 && !l.StartsWith("STATE:", StringComparison.Ordinal)))
                    {
                        result.Append(line);
                    }

                    if (!string.IsNullOrWhiteSpace(setup.StdErr))
                    {
                        result.Append(setup.StdErr.TrimEnd());
                    }

                    if (setup.Interruption is { } why)
                    {
                        Fail(result, "установка не прошла: " + why);
                        return;
                    }

                    var state = output.FirstOrDefault(l => l.StartsWith("STATE:", StringComparison.Ordinal))?[6..].Trim() ?? string.Empty;

                    if (setup.ExitCode is int code && code != 0 || state != "active")
                    {
                        Fail(result, state.Length > 0 && state != "active"
                            ? $"служба не запустилась: {state}"
                            : "не удалось поставить сборщик" + (root ? string.Empty : " — нужен sudo"));
                        return;
                    }

                    result.Append("── итог ──");
                    result.Append("Служба mss-monitor запущена — история копится с этой минуты.");
                    result.Append("Убрать: " + MonitorRemoveHint);
                    result.Message = "сбор истории включён";
                    result.Status = OperationStatus.Success;
                    installed = true;

                    ActionLog.Success("На сервер поставлен сбор истории мониторинга", target, AppSection.Monitor, this,
                        [
                            "сборщик: " + MonitorScript,
                            "служба: mss-monitor.service — замер каждые 10 с",
                            "история: " + MonitorProbe.DataDir + " — минуты за 48 ч, часы за 180 дней, файлы обрезаются сами",
                            "убрать: " + MonitorRemoveHint,
                        ]);
                },
                settings.MaxParallel,
                concurrent: true);
        }
        finally
        {
            feed.SetInstalling(false);
        }

        FinishStatus(CommandBatch, "сбор истории", installed ? "сбор включён" : "сбор не включён");

        if (!CommandBatch.IsRunning)
        {
            AppState.Current.NotifyFinished(CommandBatch);
        }

        return installed;

        static void Fail(OperationResult result, string why)
        {
            result.Append("— " + why + " —");
            result.Message = why;
            result.Status = OperationStatus.Failed;
        }
    }
}
