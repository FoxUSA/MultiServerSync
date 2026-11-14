using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiServerSync.Services;

/// <summary>
/// Запуск процесса с правами пользователя из программы, запущенной от администратора: берётся токен Проводника
/// Windows (рабочий стол пользователя всегда работает без повышения) и процесс стартует с ним. Так делает и
/// пример Microsoft «запуск без повышения из повышенного процесса».
/// </summary>
internal static class UserLaunch
{
    /// <summary>Пользователь рабочего стола — ему программа открывает канал помощника. Null — Проводник не найден.</summary>
    public static SecurityIdentifier? DesktopUser()
    {
        using var token = ShellToken();

        if (token is null)
        {
            return null;
        }

        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User;
    }

    /// <summary>Запустить <paramref name="exe"/> с правами пользователя рабочего стола, без окна. Возвращает номер процесса.</summary>
    public static int Start(string exe, string arguments)
    {
        using var shell = ShellToken() ?? throw new InvalidOperationException("не найден Проводник Windows — не у кого взять права пользователя");

        if (!DuplicateTokenEx(shell, TokenAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (primary)
        {
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                dwFlags = StartfUseShowWindow,
                wShowWindow = SwHide,
            };

            var commandLine = new StringBuilder($"\"{exe}\" {arguments}");

            if (!CreateProcessWithTokenW(primary, 0, exe, commandLine, CreateUnicodeEnvironment, IntPtr.Zero,
                    Path.GetDirectoryName(exe), ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
    }

    private static SafeAccessTokenHandle? ShellToken()
    {
        var window = GetShellWindow();

        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);

        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return OpenProcessToken(process, TokenDuplicate | TokenQuery, out var token) ? token : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;

    // TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID
    private const uint TokenAccess = 0x0008 | 0x0001 | 0x0002 | 0x0080 | 0x0100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int StartfUseShowWindow = 0x1;
    private const short SwHide = 0;
    private const uint CreateUnicodeEnvironment = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, int logonFlags, string? application, StringBuilder commandLine,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
}
