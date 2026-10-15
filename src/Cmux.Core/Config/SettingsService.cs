using System.Text.Json;
using Cmux.Core.Services;

namespace Cmux.Core.Config;

/// <summary>
/// Manages reading, writing, and caching of <see cref="CmuxSettings"/>.
/// Settings are stored at <c>%LOCALAPPDATA%/cmux/settings.json</c>.
/// </summary>
public static class SettingsService
{
    public const int MinScrollbackLines = 100;
    public const int MaxScrollbackLines = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Hand-edited files often use the C# property names; don't silently ignore them.
        PropertyNameCaseInsensitive = true,
    };

    private static CmuxSettings? _current;

    private static string SettingsPath => Path.Combine(CmuxPaths.DataDirectory, "settings.json");

    /// <summary>
    /// The current in-memory settings instance (loaded on first access).
    /// </summary>
    public static CmuxSettings Current => _current ??= Load();

    /// <summary>
    /// Raised after <see cref="NotifyChanged"/> is called to signal that settings have been modified.
    /// </summary>
    public static event Action? SettingsChanged;

    /// <summary>
    /// Reads settings from disk. Returns a fresh default instance on any failure.
    /// </summary>
    public static CmuxSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
                return new CmuxSettings();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<CmuxSettings>(json, JsonOptions) ?? new CmuxSettings();
            settings.ScrollbackLines = Math.Clamp(settings.ScrollbackLines, MinScrollbackLines, MaxScrollbackLines);
            return settings;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings.json; using defaults", ex);
            return new CmuxSettings();
        }
    }

    /// <summary>
    /// Persists the given settings to disk atomically (write to .tmp, then move).
    /// </summary>
    public static void Save(CmuxSettings? settings = null)
    {
        settings ??= Current;

        try
        {
            var path = SettingsPath;
            var tmpPath = path + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Don't crash on write failures (permission issues, disk full, etc.).
            Log.Error("Failed to save settings.json", ex);
        }
    }

    /// <summary>
    /// Resets settings to defaults and persists the result.
    /// </summary>
    public static CmuxSettings Reset()
    {
        _current = new CmuxSettings();
        Save(_current);
        return _current;
    }

    /// <summary>
    /// Raises the <see cref="SettingsChanged"/> event.
    /// Call after modifying <see cref="Current"/> properties.
    /// </summary>
    public static void NotifyChanged() => SettingsChanged?.Invoke();
}
