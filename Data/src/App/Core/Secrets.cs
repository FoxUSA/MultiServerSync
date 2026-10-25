using System.Security.Cryptography;
using System.Text;

namespace MultiServerSync.Core;

public enum SecretMode
{
    /// <summary>Шифрует Windows под текущей учётной записью — ничего спрашивать не нужно.</summary>
    Windows,

    /// <summary>Шифрует ключ из мастер-пароля — подходит на любом компьютере.</summary>
    Master,
}

/// <summary>
/// Шифрование паролей серверов в servers.json. Значение несёт свою метку: «dpapi:…» — Windows,
/// «mp:…» — мастер-пароль, без метки — открытый текст старых версий. Расшифровывается любая
/// метка, шифруется текущим режимом, так что переход между режимами — просто пересохранение.
/// </summary>
public static class Secrets
{
    private const string WindowsPrefix = "dpapi:";
    private const string MasterPrefix = "mp:";

    /// <summary>Подбор мастер-пароля перебором: каждая попытка — доли секунды.</summary>
    private const int Iterations = 300_000;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Привязка к программе: чужая программа под той же учётной записью так просто не расшифрует.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MultiServerSync.ServerPassword");

    /// <summary>Чем проверяется мастер-пароль: этот текст, зашифрованный ключом.</summary>
    private const string CheckText = "MultiServerSync";

    private static byte[]? _key;

    public static SecretMode Mode { get; private set; } = SecretMode.Windows;

    public static void UseWindows()
    {
        Mode = SecretMode.Windows;
        _key = null;
    }

    /// <summary>Режим мастер-пароля. Без ключа — пароль ещё не введён, шифровать нечем.</summary>
    public static void UseMaster(byte[]? key)
    {
        Mode = SecretMode.Master;
        _key = key;
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(16);

    public static byte[] DeriveKey(string master, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(master, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static string MakeCheck(byte[] key) => SealAes(CheckText, key);

    public static bool Verify(byte[] key, string check) => OpenAes(check, key) == CheckText;

    /// <summary>Шифрует текущим режимом. Пустой пароль так и остаётся пустым.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        if (Mode == SecretMode.Master)
        {
            // без ключа лучше не сохранить вовсе, чем записать пароль открытым текстом
            return MasterPrefix + SealAes(plain, _key ?? throw new InvalidOperationException("мастер-пароль не введён"));
        }

        var sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return WindowsPrefix + Convert.ToBase64String(sealedBytes);
    }

    /// <summary>Расшифровка. null — не вышло: другая учётная запись Windows или мастер-пароль не введён.</summary>
    public static string? Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        try
        {
            if (stored.StartsWith(WindowsPrefix, StringComparison.Ordinal))
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored[WindowsPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }

            if (stored.StartsWith(MasterPrefix, StringComparison.Ordinal))
            {
                return _key is null ? null : OpenAes(stored[MasterPrefix.Length..], _key);
            }
        }
        catch (Exception)
        {
            return null;
        }

        // открытый текст из старой версии
        return stored;
    }

    /// <summary>Значение уже зашифровано текущим режимом — пересохранять незачем.</summary>
    public static bool IsCurrent(string stored) =>
        string.IsNullOrEmpty(stored)
        || stored.StartsWith(Mode == SecretMode.Master ? MasterPrefix : WindowsPrefix, StringComparison.Ordinal);

    /// <summary>AES-GCM: одноразовое число, метка подлинности и шифртекст одной строкой base64.</summary>
    private static string SealAes(string plain, byte[] key)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[data.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, data, cipher, tag);
        }

        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private static string? OpenAes(string sealedText, byte[] key)
    {
        try
        {
            var all = Convert.FromBase64String(sealedText);

            if (all.Length < NonceSize + TagSize)
            {
                return null;
            }

            var nonce = all.AsSpan(0, NonceSize);
            var tag = all.AsSpan(NonceSize, TagSize);
            var cipher = all.AsSpan(NonceSize + TagSize);
            var data = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, data);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
