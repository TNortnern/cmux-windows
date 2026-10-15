namespace Cmux.Core;

/// <summary>
/// Well-known on-disk locations. Directories are created on first access.
/// </summary>
public static class CmuxPaths
{
    public static string DataDirectory => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux"));

    public static string LogsDirectory => Ensure(Path.Combine(DataDirectory, "logs"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
