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
/// ещё и в саму сборку ресурсами. Архиваторы, синхронизация и переносы между
/// компьютерами регулярно съедают и desktop.ini, и .ico — поэтому при каждом
/// запуске раскладка проверяется и молча восстанавливается: и файлы иконок,
/// и desktop.ini, и атрибуты, без которых проводник иконку не покажет.
/// </summary>
public static class FolderIcons
{
    public const string AppIconName = "app.ico";
    public const string DataIconName = "data.ico";
    public const string UserDataIconName = "user_data.ico";

    /// <summary>
    /// Проверить и при необходимости восстановить иконки всех трёх папок.
    /// Ошибки только в журнал: без иконок программа работает как обычно.
    /// </summary>
    public static void Restore()
    {
        try
        {
            var restoredIcons = new List<string>();

            foreach (var name in new[] { AppIconName, DataIconName, UserDataIconName })
            {
                if (EnsureIconFile(name))
                {
                    restoredIcons.Add(name);
                }
            }

            var changed = new List<string>();

            // путь в desktop.ini относительный: папку можно перенести куда угодно, иконка не отвяжется
            if (ApplyFolderIcon(AppPaths.RootDir, $"Data\\{AppIconName}"))
            {
                changed.Add(Path.GetFileName(AppPaths.RootDir));
            }

            if (ApplyFolderIcon(AppPaths.DataDir, DataIconName))
            {
                changed.Add("Data");
            }

            if (ApplyFolderIcon(AppPaths.UserDataDir, $"..\\Data\\{UserDataIconName}"))
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
                RefreshShell();
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Не удалось восстановить иконки папок: " + ex.Message);
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

        // сравниваем по размеру: битый или обрезанный архиватором файл так и ловится
        if (File.Exists(target) && new FileInfo(target).Length == bytes.Length)
        {
            return false;
        }

        if (File.Exists(target))
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }

        File.WriteAllBytes(target, bytes);
        return true;
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

        return !iniOk || !fileAttributesOk || !dirAttributesOk;
    }

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

    /// <summary>Сказать проводнику перечитать папки: иначе иконка сменится когда-нибудь потом.</summary>
    private static void RefreshShell()
    {
        foreach (var folder in new[] { AppPaths.RootDir, AppPaths.DataDir, AppPaths.UserDataDir })
        {
            var pointer = Marshal.StringToHGlobalUni(folder);

            try
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, pointer, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
