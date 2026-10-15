using System.Text;

namespace Cmux.Core.Terminal;

/// <summary>
/// Adds a prompt hook to known shells so they report their cwd via OSC 9;9.
/// PowerShell gets its hook through -EncodedCommand (no quoting issues); cmd.exe gets it through PROMPT.
/// </summary>
public static class ShellIntegration
{
    // Runs after the profile has loaded. Wraps the existing prompt function so every prompt
    // emits ESC ] 9 ; 9 ; "<path>" ESC \ when the current location is a file system path.
    // The guard keeps a re-run of this script from wrapping our own prompt (infinite recursion).
    private const string PowerShellScript =
        @"if (-not $global:__cmuxWrapped) { $global:__cmuxWrapped = $true; $global:__cmuxPrompt = $function:prompt }; " +
        @"function global:prompt { $l = $executionContext.SessionState.Path.CurrentLocation; $o = ''; " +
        @"if ($l.Provider.Name -eq 'FileSystem') { $o = ""$([char]27)]9;9;`""$($l.ProviderPath)`""$([char]27)\"" }; " +
        @"$p = if ($global:__cmuxPrompt) { & $global:__cmuxPrompt } else { ""PS $($l)> "" }; return $o + $p }";

    private static readonly string EncodedPowerShellScript =
        Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellScript));

    /// <summary>
    /// Returns the command line to launch and any extra environment variables needed for the
    /// shell to report its working directory. Unknown shells, and PowerShell command lines that
    /// already specify their own behaviour, are returned unchanged.
    /// </summary>
    public static (string CommandLine, Dictionary<string, string>? ExtraEnv) Apply(string shellCommandLine)
    {
        var (exe, args) = SplitCommandLine(shellCommandLine);
        string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();

        switch (name)
        {
            case "pwsh":
            case "powershell":
                if (HasPowerShellBehaviourArg(args))
                    return (shellCommandLine, null);
                return (shellCommandLine.TrimEnd() + " -NoExit -EncodedCommand " + EncodedPowerShellScript, null);

            case "cmd":
                var existing = Environment.GetEnvironmentVariable("PROMPT");
                var prompt = "$E]9;9;$P$E\\" + (string.IsNullOrEmpty(existing) ? "$P$G" : existing);
                return (shellCommandLine, new Dictionary<string, string> { ["PROMPT"] = prompt });

            default:
                return (shellCommandLine, null);
        }
    }

    private static (string Exe, string Args) SplitCommandLine(string commandLine)
    {
        string s = commandLine.TrimStart();
        if (s.Length == 0)
            return (string.Empty, string.Empty);

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end < 0 ? (s[1..], string.Empty) : (s[1..end], s[(end + 1)..]);
        }

        // Unquoted path that may contain spaces (e.g. C:\Program Files\...\pwsh.exe):
        // the executable ends at the first ".exe" that is followed by whitespace or the end.
        for (int i = 0; i + 4 <= s.Length; i++)
        {
            if (string.Compare(s, i, ".exe", 0, 4, StringComparison.OrdinalIgnoreCase) == 0 &&
                (i + 4 == s.Length || char.IsWhiteSpace(s[i + 4])))
                return (s[..(i + 4)], s[(i + 4)..]);
        }

        int space = s.IndexOfAny([' ', '\t']);
        return space < 0 ? (s, string.Empty) : (s[..space], s[space..]);
    }

    /// <summary>
    /// True when the arguments already choose what PowerShell runs (-Command, -File,
    /// -EncodedCommand) or already set -NoExit, so the integration must not be appended.
    /// Accepts PowerShell's unambiguous prefixes (for example -c, -f, -enc).
    /// </summary>
    private static bool HasPowerShellBehaviourArg(string args)
    {
        foreach (var token in args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2 || (token[0] != '-' && token[0] != '/'))
                continue;

            int colon = token.IndexOf(':');
            string name = (colon > 0 ? token[1..colon] : token[1..]).ToLowerInvariant();

            if ("command".StartsWith(name, StringComparison.Ordinal) ||
                "file".StartsWith(name, StringComparison.Ordinal) ||
                "encodedcommand".StartsWith(name, StringComparison.Ordinal) ||
                "noexit".StartsWith(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
