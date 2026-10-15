namespace Cmux.Core.Terminal;

/// <summary>
/// Adds a prompt hook to known shells so they report their cwd via OSC 9;9.
/// </summary>
public static class ShellIntegration
{
    public static (string CommandLine, Dictionary<string, string>? ExtraEnv) Apply(string shellCommandLine)
        => (shellCommandLine, null); // STUB: package B implements
}
