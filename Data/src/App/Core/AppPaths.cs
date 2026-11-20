using System.IO;

namespace MultiServerSync.Core;

/// <summary>
/// Раскладка на диске:
///   &lt;корень&gt;\Multi Server Sync.exe — лаунчер (в старых установках — MultiServerSync.exe, так и остаётся)
///   &lt;корень&gt;\Data\                — программа, библиотеки, иконки и manifest.json (при обновлении затирается целиком)
///   &lt;корень&gt;\UserData\            — всё пользовательское: серверы, вкладки, настройки, логи
///   &lt;корень&gt;\Update\              — только во время обновления: скачанный архив и подготовленная замена
/// </summary>
public static class AppPaths
{
    /// <summary>Имя лаунчера в корне. Обновление кладёт на его место файл из архива.</summary>
    public const string LauncherName = "Multi Server Sync.exe";

    /// <summary>
    /// Прежнее имя лаунчера. Установки старых версий его не меняют: на него смотрят ярлыки, а старый
    /// лаунчер при обновлении ищет замену себе только под этим именем.
    /// </summary>
    public const string OldLauncherName = "MultiServerSync.exe";

    /// <summary>Имя основной программы внутри Data. По нему проверяется, что архив годный.</summary>
    public const string AppExeName = "MultiServerSync.App.exe";

    static AppPaths()
    {
        DataDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var parent = Directory.GetParent(DataDir)?.FullName;
        var isPublishedLayout = string.Equals(
            new DirectoryInfo(DataDir).Name, "Data", StringComparison.OrdinalIgnoreCase);

        RootDir = isPublishedLayout && parent is not null ? parent : DataDir;
        UserDataDir = Path.Combine(RootDir, "UserData");
        LogsDir = Path.Combine(UserDataDir, "logs");

        Directory.CreateDirectory(UserDataDir);
        Directory.CreateDirectory(LogsDir);
    }

    public static string RootDir { get; }

    public static string DataDir { get; }

    public static string UserDataDir { get; }

    public static string LogsDir { get; }

    /// <summary>Лаунчер в корне: он же применяет подготовленное обновление. Новое имя, а нет его — прежнее.</summary>
    public static string LauncherFile
    {
        get
        {
            var current = Path.Combine(RootDir, LauncherName);
            var old = Path.Combine(RootDir, OldLauncherName);
            return !File.Exists(current) && File.Exists(old) ? old : current;
        }
    }

    /// <summary>Рабочая папка обновления. Лежит в корне: Data при обновлении заменяется целиком.</summary>
    public static string UpdateDir => Path.Combine(RootDir, "Update");

    /// <summary>Скачанное и проверенное обновление, готовое к подмене лаунчером.</summary>
    public static string StagedDir => Path.Combine(UpdateDir, "staged");

    /// <summary>Отметка готовности: пока файла нет, лаунчер обновление не применяет.</summary>
    public static string StagedMarkerFile => Path.Combine(StagedDir, "update.json");

    public static string ServersFile => Path.Combine(UserDataDir, "servers.json");

    /// <summary>Вкладки раздела «Серверы» — наборы серверов. Сами серверы лежат в servers.json.</summary>
    public static string ServerSetsFile => Path.Combine(UserDataDir, "serversets.json");

    public static string SettingsFile =>Path.Combine(UserDataDir, "settings.json");

    /// <summary>Вкладки-рабочие столы: у каждой свои серверы, файлы и команда.</summary>
    public static string WorkspacesFile => Path.Combine(UserDataDir, "workspaces.json");

    /// <summary>Где запускались команды и копирование — по ней умный режим замечает непривычный сервер.</summary>
    public static string RunHistoryFile => Path.Combine(UserDataDir, "runhistory.json");

    /// <summary>Строки и вывод терминалов всех вкладок — переживают перезапуск.</summary>
    public static string TerminalFile => Path.Combine(UserDataDir, "terminal.json");

    /// <summary>Записи журнала — переживают перезапуск.</summary>
    public static string JournalFile => Path.Combine(UserDataDir, "journal.json");

    /// <summary>Отмена действий с самими вкладками — закрытых и переименованных: переживает перезапуск.</summary>
    public static string TabActionsFile => Path.Combine(UserDataDir, "tab-actions.json");

    /// <summary>Запомненные ключи серверов, как known_hosts у ssh.</summary>
    public static string KnownHostsFile => Path.Combine(UserDataDir, "known_hosts.json");

    /// <summary>Версия программы. Кладётся сборкой в Data и обновляется вместе с программой.</summary>
    public static string ManifestFile => Path.Combine(DataDir, "manifest.json");

    /// <summary>Иконка папки: все три лежат в Data и обновляются вместе с программой.</summary>
    public static string IconFile(string name) => Path.Combine(DataDir, name);

    public static string LogFileForToday(DateTime now) =>
        Path.Combine(LogsDir, $"{now:yyyy-MM-dd}.log");
}
