using System.Security.Cryptography;
using System.Text.Json;
using Cmux.Core;

namespace Cmux.Core.Services;

/// <summary>
/// Stores secrets encrypted with Windows DPAPI in %LOCALAPPDATA%/cmux/secrets.json.
/// Writes are atomic and keep the previous version as secrets.json.bak. If the existing file
/// cannot be read or parsed, writes fail instead of replacing it (which would lose every other secret).
/// </summary>
public static class SecretStoreService
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string SecretsPath => Path.Combine(CmuxPaths.DataDirectory, "secrets.json");

    public static string? GetSecret(string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
            return null;

        try
        {
            lock (Gate)
            {
                var map = LoadRawSecrets();
                if (!map.TryGetValue(secretName, out var encoded) || string.IsNullOrWhiteSpace(encoded))
                    return null;

                var encrypted = Convert.FromBase64String(encoded);
                var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(plain);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read a stored secret", ex);
            return null;
        }
    }

    /// <summary>
    /// Stores or (when <paramref name="value"/> is empty) removes a secret.
    /// Returns false if the secrets file could not be read or written; nothing is changed in that case.
    /// </summary>
    public static bool SetSecret(string secretName, string? value)
    {
        if (string.IsNullOrWhiteSpace(secretName))
            return false;

        try
        {
            lock (Gate)
            {
                var map = LoadRawSecrets();

                if (string.IsNullOrWhiteSpace(value))
                {
                    map.Remove(secretName);
                }
                else
                {
                    var plain = System.Text.Encoding.UTF8.GetBytes(value);
                    var encrypted = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
                    map[secretName] = Convert.ToBase64String(encrypted);
                }

                SaveRawSecrets(map);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save a secret", ex);
            return false;
        }
    }

    public static bool RemoveSecret(string secretName) => SetSecret(secretName, null);

    /// <summary>
    /// Returns an empty map when no file exists. Throws when the file exists but cannot be read or parsed.
    /// </summary>
    private static Dictionary<string, string> LoadRawSecrets()
    {
        var path = SecretsPath;
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        return map ?? throw new InvalidDataException("secrets.json contains no object");
    }

    private static void SaveRawSecrets(Dictionary<string, string> map)
    {
        var path = SecretsPath;
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(map, JsonOptions);
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }
}
