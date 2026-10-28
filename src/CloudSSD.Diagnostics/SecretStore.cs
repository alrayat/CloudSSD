using System.Security.Cryptography;
using System.Text;

namespace CloudSSD.Diagnostics;

/// <summary>
/// Stores small secrets with Windows DPAPI (CurrentUser scope). The protected blob can only be decrypted by the same
/// Windows user on the same machine, so copying the file elsewhere (backups, cloud sync) does not leak the secret.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudSSD.v1.secret");

    public static void Save(string path, string secret)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    public static string? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Cryptographically random URL-safe token.</summary>
    public static string RandomToken(int bytes = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
