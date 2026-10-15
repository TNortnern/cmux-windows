using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Cmux.Tests")]

namespace Cmux.Core;

/// <summary>
/// Well-known on-disk locations. Directories are created on first access.
/// </summary>
public static class CmuxPaths
{
    /// <summary>Test hook: redirects every cmux path under this directory.</summary>
    internal static string? RootOverride { get; set; }

    public static string DataDirectory => Ensure(RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux"));

    public static string LogsDirectory => Ensure(Path.Combine(DataDirectory, "logs"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
