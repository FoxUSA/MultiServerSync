using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Режим хранения паролей серверов: настройки, ключ в памяти и пересохранение servers.json.
/// Порядок записи файлов подобран так, чтобы оборванная на полпути смена режима
/// ничего не теряла: в любой момент настройки и серверы расшифровываются вместе.
/// </summary>
public static class PasswordVault
{
    private static AppSettings Settings => AppState.Current.Settings;

    public static bool IsMaster => Settings.PasswordProtection == nameof(SecretMode.Master)
        && Settings.MasterSalt.Length > 0
        && Settings.MasterCheck.Length > 0;

    /// <summary>После загрузки настроек: режим из них, мастер-пароль ещё не введён.</summary>
    public static void Init()
    {
        if (IsMaster)
        {
            Secrets.UseMaster(null);
        }
        else
        {
            Secrets.UseWindows();
        }
    }

    /// <summary>Проверка мастер-пароля при запуске. Верный — ключ остаётся в памяти до выхода.</summary>
    public static bool TryUnlock(string master)
    {
        var key = Secrets.DeriveKey(master, Convert.FromBase64String(Settings.MasterSalt));

        if (!Secrets.Verify(key, Settings.MasterCheck))
        {
            return false;
        }

        Secrets.UseMaster(key);
        return true;
    }

    /// <summary>Расшифровка паролей после загрузки; открытый текст и чужой режим тут же перешифровываются.</summary>
    public static void OpenAll()
    {
        var locked = new List<ServerEntry>();
        var resave = false;

        foreach (var server in AppState.Current.Servers)
        {
            if (!server.OpenPassword(out var stale))
            {
                locked.Add(server);
            }

            resave |= stale;
        }

        if (resave)
        {
            AppState.Current.SaveServers();
            AppLog.Info(Secrets.Mode == SecretMode.Master
                ? "Пароли серверов перешифрованы мастер-паролем."
                : "Пароли серверов зашифрованы под учётной записью Windows.");
        }

        if (locked.Count > 0)
        {
            AppLog.Warn(
                $"Не удалось расшифровать пароли серверов ({locked.Count}): "
                + string.Join(", ", locked.Select(s => s.PaneLabel))
                + ". Они зашифрованы на другом компьютере или под другой учётной записью Windows — "
                + "введите их заново или включите мастер-пароль на том компьютере, где они открываются.");
        }
    }

    /// <summary>Включить или сменить мастер-пароль: все пароли перешифровываются новым ключом.</summary>
    public static void EnableMaster(string master)
    {
        var salt = Secrets.NewSalt();
        var key = Secrets.DeriveKey(master, salt);
        var wasMaster = IsMaster;

        // через промежуточный вид Windows: его можно расшифровать при любых настройках. Оборвись
        // запись после серверов — спросят старый мастер-пароль, после настроек — новый, и в обоих
        // случаях пароли откроются. Напрямую старый ключ → новый так не страхуется
        Secrets.UseWindows();
        AppState.Current.SaveServers();

        Settings.PasswordProtection = nameof(SecretMode.Master);
        Settings.MasterSalt = Convert.ToBase64String(salt);
        Settings.MasterCheck = Secrets.MakeCheck(key);
        AppState.Current.SaveSettings();

        Secrets.UseMaster(key);
        AppState.Current.SaveServers();

        AppLog.Info(wasMaster
            ? "Мастер-пароль сменён, пароли серверов перешифрованы."
            : "Включён мастер-пароль: пароли серверов перешифрованы, при запуске он будет спрошен.");
    }

    /// <summary>Вернуться к шифрованию учётной записью Windows.</summary>
    public static void DisableMaster()
    {
        // сначала серверы: если запись настроек оборвётся, мастер-пароль спросят, а пароли
        // уже в виде Windows — расшифруются и так
        Secrets.UseWindows();
        AppState.Current.SaveServers();

        ClearMasterSettings();
        AppLog.Info("Мастер-пароль отключён: пароли серверов зашифрованы под учётной записью Windows.");
    }

    /// <summary>
    /// «Не помню мастер-пароль»: без него пароли не расшифровать, поэтому они стираются,
    /// серверы остаются. Дальше пароли шифруются учётной записью Windows.
    /// </summary>
    public static void Forget()
    {
        Secrets.UseWindows();

        foreach (var server in AppState.Current.Servers)
        {
            server.ForgetPassword();
        }

        UndoJournal.ForgetServers();
        AppState.Current.SaveServers();
        ClearMasterSettings();
        AppLog.Warn("Мастер-пароль сброшен: пароли серверов стёрты, введите их заново.");
    }

    private static void ClearMasterSettings()
    {
        Settings.PasswordProtection = nameof(SecretMode.Windows);
        Settings.MasterSalt = string.Empty;
        Settings.MasterCheck = string.Empty;
        AppState.Current.SaveSettings();
    }
}
