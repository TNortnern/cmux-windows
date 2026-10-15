namespace Cmux.Core.Terminal;

/// <summary>
/// Reads another process's current directory from its PEB.
/// </summary>
public static class ProcessCwdReader
{
    public static bool TryGetCurrentDirectory(int pid, out string? cwd)
    {
        cwd = null; // STUB: package B implements
        return false;
    }
}
