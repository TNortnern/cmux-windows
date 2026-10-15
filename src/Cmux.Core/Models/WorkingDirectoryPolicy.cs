namespace Cmux.Core.Models;

/// <summary>
/// Rules for which directories may be used as the start directory of a new tab, split or pane.
/// Only local, fully-qualified, existing folders qualify (no UNC or relative paths).
/// </summary>
public static class WorkingDirectoryPolicy
{
    public static bool IsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;

        if (!Path.IsPathFullyQualified(trimmed))
            return false;

        try
        {
            return Directory.Exists(trimmed);
        }
        catch (Exception)
        {
            // Malformed or inaccessible path: treat as unusable.
            return false;
        }
    }

    /// <summary>Returns the trimmed path when usable, otherwise null.</summary>
    public static string? Sanitize(string? path) => IsUsable(path) ? path!.Trim() : null;

    /// <summary>Picks the first usable candidate, in priority order.</summary>
    public static string? ChooseForNewPane(string? live, string? cached, string? fallback)
    {
        return Sanitize(live) ?? Sanitize(cached) ?? Sanitize(fallback);
    }
}
