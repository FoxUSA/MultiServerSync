using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using MultiServerSync.Core;

namespace MultiServerSync.Services;

/// <summary>
/// Родные иконки папок: MultiServerSync, Data и UserData.
///
/// Иконки живут в Data и обновляются вместе с программой, а сами .ico зашиты
/// ещё и в саму сборку ресурсами. Архиваторы, облака и переносы между компьютерами
/// регулярно съедают и desktop.ini, и .ico, и атрибуты — поэтому при каждом запуске
/// иконки назначаются заново, где бы папка ни лежала: файлы иконок, desktop.ini,
/// атрибуты, пометка «скачано из интернета», и проводнику говорится перерисовать папки.
/// </summary>
public static class FolderIcons
{
    public const string AppIconName = "app.ico";
    public const string DataIconName = "data.ico";
    public const string UserDataIconName = "user_data.ico";

    /// <summary>
    /// Проверить и назначить иконки всех трёх папок заново.
    /// Ошибки только в журнал: без иконок программа работает как обычно.
    /// </summary>
    public static void Restore()
    {
        try
        {
            var restoredIcons = new List<string>();

            foreach (var name in new[] { AppIconName, DataIconName, UserDataIconName })
            {
                if (Try(() => EnsureIconFile(name), name))
                {
                    restoredIcons.Add(name);
                }
            }

            var changed = new List<string>();

            // путь в desktop.ini относительный: папку можно перенести куда угодно, иконка не отвяжется
            if (Try(() => ApplyFolderIcon(AppPaths.RootDir, $"Data\\{AppIconName}"), AppPaths.RootDir))
            {
                changed.Add(Path.GetFileName(AppPaths.RootDir));
            }

            if (Try(() => ApplyFolderIcon(AppPaths.DataDir, DataIconName), AppPaths.DataDir))
            {
                changed.Add("Data");
            }

            if (Try(() => ApplyFolderIcon(AppPaths.UserDataDir, $"..\\Data\\{UserDataIconName}"), AppPaths.UserDataDir))
            {
                changed.Add("UserData");
            }

            if (restoredIcons.Count > 0)
            {
                AppLog.Debug("Восстановлены файлы иконок: " + string.Join(", ", restoredIcons) + ".");
            }

            if (changed.Count > 0)
            {
                AppLog.Debug("Восстановлены иконки папок: " + string.Join(", ", changed) + ".");
            }

            // проводник держит иконки в кэше: на новом ПК он успевает показать папки голыми
            // ещё до первого запуска — перерисовать их просим каждый раз, а не только после починки
            RefreshShell(restoredIcons.Count > 0 || changed.Count > 0);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Не удалось восстановить иконки папок: " + ex.Message);
        }
    }

    /// <summary>Одна папка или иконка не далась — остальные всё равно чинятся.</summary>
    private static bool Try(Func<bool> action, string what)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Иконка папки: {what} — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Вернуть .ico в Data, если он пропал или побит. Эталон — тот же файл,
    /// зашитый в сборку ресурсом, поэтому восстановление не зависит от сети.
    /// </summary>
    private static bool EnsureIconFile(string name)
    {
        var target = AppPaths.IconFile(name);

        var resource = Application.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute));

        if (resource?.Stream is null)
        {
            return false;
        }

        byte[] bytes;

        using (var stream = resource.Stream)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        // сравниваем содержимое целиком: заодно облако (Dropbox, OneDrive) докачивает файл,
        // который лежал на диске только ссылкой, — из такого проводник иконку не берёт
        var same = File.Exists(target) && SafeReadBytes(target).AsSpan().SequenceEqual(bytes);

        if (!same)
        {
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            File.WriteAllBytes(target, bytes);
        }

        var unblocked = DropInternetMark(target);
        return !same || unblocked;
    }

    /// <summary>Прописать папке иконку. true — если что-то пришлось починить.</summary>
    private static bool ApplyFolderIcon(string folder, string iconRelativePath)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var iniPath = Path.Combine(folder, "desktop.ini");

        // IconResource понимает новый проводник, IconFile/IconIndex — старый
        var expected =
            "[.ShellClassInfo]\r\n" +
            "ConfirmFileOp=0\r\n" +
            $"IconResource={iconRelativePath},0\r\n" +
            $"IconFile={iconRelativePath}\r\n" +
            "IconIndex=0\r\n";

        var iniOk = File.Exists(iniPath)
            && string.Equals(SafeRead(iniPath), expected, StringComparison.Ordinal);

        if (!iniOk)
        {
            if (File.Exists(iniPath))
            {
                File.SetAttributes(iniPath, FileAttributes.Normal);
            }

            // только ASCII: пути внутри относительные, кодировка проводнику не важна
            File.WriteAllText(iniPath, expected, new UTF8Encoding(false));
        }

        // распакованный из скачанного архива desktop.ini несёт пометку «из интернета»
        var unblocked = DropInternetMark(iniPath);

        // desktop.ini обязан быть скрытым и системным, иначе он просто мозолит глаза
        var wantedFileAttributes = FileAttributes.Hidden | FileAttributes.System;
        var fileAttributesOk = (File.GetAttributes(iniPath) & wantedFileAttributes) == wantedFileAttributes;

        if (!fileAttributesOk)
        {
            File.SetAttributes(iniPath, wantedFileAttributes | FileAttributes.Archive);
        }

        // без «только чтение» у самой папки проводник в desktop.ini даже не заглянет
        var dir = new DirectoryInfo(folder);
        var dirAttributesOk = dir.Attributes.HasFlag(FileAttributes.ReadOnly);

        if (!dirAttributesOk)
        {
            dir.Attributes |= FileAttributes.ReadOnly;
        }

        return !iniOk || unblocked || !fileAttributesOk || !dirAttributesOk;
    }

    /// <summary>Снять с файла пометку «скачано из интернета». true — если она была.</summary>
    private static bool DropInternetMark(string path) => DeleteFile(path + ":Zone.Identifier");

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] SafeReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Сказать проводнику перерисовать иконки самих папок (в окне, где они лежат) и их содержимое.
    /// Иначе иконка сменится когда-нибудь потом — или так и останется голой до перезагрузки.
    /// </summary>
    private static void RefreshShell(bool changed)
    {
        var folders = new[] { AppPaths.RootDir, AppPaths.DataDir, AppPaths.UserDataDir };
        var parents = new List<string>();

        foreach (var folder in folders)
        {
            Notify(SHCNE_UPDATEITEM, folder);
            Notify(SHCNE_UPDATEDIR, folder);

            var parent = Path.GetDirectoryName(folder);

            if (!string.IsNullOrEmpty(parent) && !parents.Contains(parent, StringComparer.OrdinalIgnoreCase))
            {
                parents.Add(parent);
            }
        }

        foreach (var parent in parents)
        {
            Notify(SHCNE_UPDATEDIR, parent);
        }

        // общий сброс кэша иконок — только когда на диске правда что-то поменялось
        if (changed)
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void Notify(int eventId, string path)
    {
        var pointer = Marshal.StringToHGlobalUni(path);

        try
        {
            SHChangeNotify(eventId, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, pointer, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSHNOWAIT = 0x3000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeleteFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFile(string path);
}
