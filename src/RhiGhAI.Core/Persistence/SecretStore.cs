using System.Security.Cryptography;
using System.Text;

namespace RhiGhAI.Core.Persistence;

/// <summary>
/// Stores the provider API key encrypted for the current Windows user (DPAPI).
/// ponytail: one key at a time — switching provider means re-entering the key.
/// </summary>
public sealed class SecretStore(string? rootDirectory = null)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RhiGhAI.ApiKey.v1");

    private readonly string _rootDirectory = rootDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductInfo.Name);

    private string SecretPath => Path.Combine(_rootDirectory, "provider.key");

    public string? LoadApiKey()
    {
        try
        {
            if (!File.Exists(SecretPath))
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(SecretPath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // A key written by another Windows user (or a corrupt file) is simply not available.
            return null;
        }
    }

    public void SaveApiKey(string? apiKey)
    {
        Directory.CreateDirectory(_rootDirectory);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(SecretPath))
            {
                File.Delete(SecretPath);
            }

            return;
        }

        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey.Trim()),
            Entropy,
            DataProtectionScope.CurrentUser);
        string temporary = $"{SecretPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, cipher);
            File.Move(temporary, SecretPath, true);
        }
        finally
        {
            // A failed move would otherwise leave the encrypted key lying around under a second name.
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Nothing further to do: the key itself is already either written or unchanged.
                }
            }
        }
    }
}
