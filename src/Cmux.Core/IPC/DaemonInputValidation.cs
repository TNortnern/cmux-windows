using System.IO;

namespace Cmux.Core.IPC;

/// <summary>
/// Pure validation helpers for daemon session requests. Shared by the daemon and tests.
/// </summary>
public static class DaemonInputValidation
{
    public const int MaxCols = 1000;
    public const int MaxRows = 500;

    private static readonly string[] s_executableExtensions = [".exe", ".cmd", ".bat", ".com"];

    public static int ClampCols(int cols) => Math.Clamp(cols, 1, MaxCols);

    public static int ClampRows(int rows) => Math.Clamp(rows, 1, MaxRows);

    /// <summary>
    /// Returns the path when it is fully qualified, local (not UNC) and an existing directory; otherwise null.
    /// </summary>
    public static string? ValidateWorkingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path) || IsUnc(path)) return null;
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// True when no command is given (default shell), or when the command's program resolves to an
    /// existing local .exe/.cmd/.bat/.com. Arguments after the program are not inspected.
    /// </summary>
    public static bool IsCommandAllowed(string? command) =>
        string.IsNullOrWhiteSpace(command) || ResolveExecutable(command) != null;

    /// <summary>
    /// Resolves the program named by the first token of a command line (quoted or bare) to an
    /// existing absolute executable path. Bare names are searched on PATH. Returns null if not allowed.
    /// </summary>
    public static string? ResolveExecutable(string commandLine)
    {
        var token = FirstToken(commandLine);
        if (token.Length == 0 || token.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        if (Path.IsPathRooted(token) || token.Contains('\\') || token.Contains('/'))
        {
            if (!Path.IsPathFullyQualified(token) || IsUnc(token)) return null;
            return FindExecutable(token);
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var rawDir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim('"');
            if (!Path.IsPathFullyQualified(dir) || IsUnc(dir)) continue;

            var found = FindExecutable(Path.Combine(dir, token));
            if (found != null) return found;
        }
        return null;
    }

    private static string FirstToken(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0) return "";

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end < 0 ? s[1..] : s[1..end];
        }

        int space = s.IndexOfAny([' ', '\t']);
        return space < 0 ? s : s[..space];
    }

    private static string? FindExecutable(string path)
    {
        IEnumerable<string> candidates = Path.HasExtension(path)
            ? [path]
            : s_executableExtensions.Select(ext => path + ext);

        foreach (var candidate in candidates)
        {
            var ext = Path.GetExtension(candidate);
            if (!s_executableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}
