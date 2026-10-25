using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiServerSync.Services;

/// <summary>
/// Значки и названия типов — те же, что в проводнике Windows: их отдаёт сама система по
/// расширению, сам файл для этого не нужен. Поэтому и файлы сервера получают значок той
/// программы, которая открыла бы их здесь. Всё кэшируется: система спрашивается раз на расширение.
/// </summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>У этих файлов значок свой у каждого (программы, ярлыки), а не общий на расширение.</summary>
    private static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".cur", ".ani", ".msc", ".scr",
    };

    public static ImageSource? Folder => Stock(SiidFolder);

    public static ImageSource? LinkOverlay => Stock(SiidLink);

    public static ImageSource? Computer => Stock(SiidDesktopPc);

    public static ImageSource? Server => Stock(SiidServer);

    public static ImageSource? Program => Stock(SiidApplication);

    public static ImageSource? Unknown => Stock(SiidDocNoAssoc);

    /// <summary>Значок объекта на этом компьютере: у дисков и программ — их собственный.</summary>
    public static ImageSource? ForLocal(string path, bool isDirectory, bool isDrive)
    {
        if (isDrive)
        {
            return Icons.GetOrAdd("drive:" + path, _ => FromShell(path, 0, useAttributes: false));
        }

        if (isDirectory)
        {
            return Folder;
        }

        var ext = Path.GetExtension(path);

        return OwnIcon.Contains(ext)
            ? Icons.GetOrAdd("file:" + path, _ => FromShell(path, 0, useAttributes: false) ?? ByExtension(ext))
            : ByExtension(ext);
    }

    /// <summary>Значок по одному имени — для файлов сервера. Без расширения: с правом запуска — программа.</summary>
    public static ImageSource? ForName(string name, bool isDirectory, bool isExecutable)
    {
        if (isDirectory)
        {
            return Folder;
        }

        var ext = Path.GetExtension(name);

        if (ext.Length == 0 || ext.Length > 12)
        {
            return isExecutable ? Program : Unknown;
        }

        return ByExtension(ext);
    }

    public static ImageSource? ByExtension(string ext) =>
        Icons.GetOrAdd("ext:" + ext, e => FromShell("x" + ext, FileAttributeNormal, useAttributes: true));

    /// <summary>Тип файла словами, как в столбце «Тип» проводника: «Документ XML», «Папка с файлами».</summary>
    public static string TypeName(string name, bool isDirectory, bool isExecutable = false)
    {
        if (isDirectory)
        {
            return Types.GetOrAdd("<dir>", _ => Describe("folder", FileAttributeDirectory) ?? "Папка с файлами");
        }

        var ext = Path.GetExtension(name);

        if (ext.Length == 0 || ext.Length > 12)
        {
            return isExecutable ? "Программа" : "Файл";
        }

        return Types.GetOrAdd(ext, e => Describe("x" + e, FileAttributeNormal) ?? "Файл " + e.TrimStart('.').ToUpperInvariant());
    }

    private static ImageSource? Stock(uint id) => Icons.GetOrAdd("stock:" + id, _ =>
    {
        var info = new StockIconInfo { cbSize = (uint)Marshal.SizeOf<StockIconInfo>() };

        if (SHGetStockIconInfo(id, ShgsiIcon | ShgsiSmallIcon, ref info) != 0 || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        return ToImage(info.hIcon);
    });

    private static ImageSource? FromShell(string path, uint attributes, bool useAttributes)
    {
        try
        {
            var info = new FileInfoW();
            var flags = ShgfiIcon | ShgfiSmallIcon | (useAttributes ? ShgfiUseFileAttributes : 0);

            if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<FileInfoW>(), flags) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            return ToImage(info.hIcon);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Describe(string path, uint attributes)
    {
        try
        {
            var info = new FileInfoW();

            return SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<FileInfoW>(), ShgfiTypeName | ShgfiUseFileAttributes) == IntPtr.Zero
                || string.IsNullOrWhiteSpace(info.szTypeName)
                ? null
                : info.szTypeName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? ToImage(IntPtr icon)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiTypeName = 0x400;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;
    private const uint ShgsiIcon = 0x100;
    private const uint ShgsiSmallIcon = 0x1;

    private const uint SiidDocNoAssoc = 0;
    private const uint SiidApplication = 2;
    private const uint SiidFolder = 3;
    private const uint SiidServer = 15;
    private const uint SiidLink = 29;
    private const uint SiidDesktopPc = 94;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref FileInfoW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref StockIconInfo psii);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// Копирование, перенос и удаление на этом компьютере — средствами самой Windows: её окно
/// с ходом операции, её вопрос «заменить или пропустить», удаление в корзину. Ровно то,
/// что человек видит в проводнике, поэтому без своих правил и сюрпризов.
/// </summary>
public static class ShellFileOps
{
    public static Task<bool> CopyAsync(Window? owner, IReadOnlyList<string> from, string toFolder, bool renameOnCollision) =>
        RunAsync(owner, FoCopy, from, toFolder, (ushort)(FofAllowUndo | FofNoConfirmMkdir | (renameOnCollision ? FofRenameOnCollision : 0)));

    public static Task<bool> MoveAsync(Window? owner, IReadOnlyList<string> from, string toFolder) =>
        RunAsync(owner, FoMove, from, toFolder, FofAllowUndo | FofNoConfirmMkdir);

    /// <summary>В корзину; вопрос «удалить?» уже задан нашим окном, второй раз Windows не спрашивает.</summary>
    public static Task<bool> RecycleAsync(Window? owner, IReadOnlyList<string> paths) =>
        RunAsync(owner, FoDelete, paths, null, FofAllowUndo | FofNoConfirmation | FofWantNukeWarning);

    private static Task<bool> RunAsync(Window? owner, uint func, IReadOnlyList<string> from, string? to, ushort flags)
    {
        var hwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
        var done = new TaskCompletionSource<bool>();

        // своё окно хода Windows рисует сама — в отдельном потоке, чтобы наше окно не замирало
        var thread = new Thread(() =>
        {
            try
            {
                var op = new FileOp
                {
                    hwnd = hwnd,
                    wFunc = func,
                    pFrom = string.Join('\0', from) + "\0",
                    pTo = to is null ? null : to + "\0",
                    fFlags = flags,
                };

                var code = SHFileOperation(ref op);
                done.SetResult(code == 0 && !op.fAnyOperationsAborted);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    private const uint FoMove = 1;
    private const uint FoCopy = 2;
    private const uint FoDelete = 3;
    private const ushort FofRenameOnCollision = 0x8;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoConfirmMkdir = 0x200;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOp
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref FileOp lpFileOp);
}
