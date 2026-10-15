using System.Text;
using System.Text.RegularExpressions;

namespace Cmux.Core.Services;

/// <summary>
/// Pure, UI-free helpers used by the agent runtime to keep model-facing data safe:
/// untrusted-data fencing, secret redaction, base URL validation, custom tool template
/// rendering, scaffold path containment and bounded output buffers.
/// </summary>
public static class AgentSafety
{
    public const int MaxBashOutputChars = 256 * 1024;
    public const int MaxMcpFrameBytes = 8 * 1024 * 1024;
    public const int MaxMcpHeaderLineBytes = 4 * 1024;
    public const int MaxMcpStderrChars = 64 * 1024;
    public const int MaxMcpDescriptionChars = 1024;
    public const int MaxTemplateArgChars = 16 * 1024;

    private const string CwdEnvName = "CMUX_CWD";
    private const string ArgEnvPrefix = "CMUX_ARG_";
    private const string CmdMetacharacters = "&|<>^()\"%!\r\n";

    private static readonly Regex PlaceholderRegex = new(@"\{\{([A-Za-z_][A-Za-z0-9_\-]{0,63})\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (Regex Pattern, string Replacement)[] RedactionRules =
    [
        (new Regex(@"(?i)\bAuthorization\s*:\s*[^\r\n]+", RegexOptions.Compiled), "Authorization: [REDACTED]"),
        (new Regex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled), "Bearer [REDACTED]"),
        (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled), "[REDACTED PRIVATE KEY]"),
        (new Regex(@"\bsk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "sk-[REDACTED]"),
        (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "gh_[REDACTED]"),
        (new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled), "github_pat_[REDACTED]"),
        (new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled), "AKIA[REDACTED]"),
        (new Regex(@"(?i)\b([A-Za-z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|PASSWD))(\s*=\s*)(""[^""\r\n]*""|'[^'\r\n]*'|[^\s""'`]+)", RegexOptions.Compiled), "$1$2[REDACTED]"),
    ];

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var result = text;
        foreach (var (pattern, replacement) in RedactionRules)
            result = pattern.Replace(result, replacement);
        return result;
    }

    /// <summary>
    /// Redacts secrets and wraps model-facing content in an untrusted_data block.
    /// The closing tag is escaped inside the content so it cannot break out of the fence.
    /// </summary>
    public static string Fence(string source, string? content)
    {
        var safeSource = new StringBuilder(source.Length);
        foreach (var ch in source)
            safeSource.Append(ch is '"' or '<' or '>' or '\r' or '\n' ? '_' : ch);

        var body = Redact(content).Replace("</untrusted_data", "<\\/untrusted_data", StringComparison.OrdinalIgnoreCase);
        return $"<untrusted_data source=\"{safeSource}\">\n{body}\n</untrusted_data>";
    }

    public sealed record BaseUrlResult(string? Url, string? Error);

    /// <summary>
    /// Accepts https:// base URLs. http:// is accepted only for loopback hosts.
    /// A missing scheme is treated as https://.
    /// </summary>
    public static BaseUrlResult ValidateBaseUrl(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = "https://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return new BaseUrlResult(null, "Agent base URL is not a valid absolute URL.");

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return new BaseUrlResult(candidate.TrimEnd('/'), null);

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && IsLoopbackHost(uri.Host))
            return new BaseUrlResult(candidate.TrimEnd('/'), null);

        return new BaseUrlResult(null, "Agent base URL must use https:// (http:// is allowed only for localhost, 127.0.0.1 or ::1).");
    }

    private static bool IsLoopbackHost(string host)
    {
        var h = host.Trim('[', ']');
        return string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase)
            || h == "127.0.0.1"
            || h == "::1";
    }

    public enum ShellKind
    {
        Cmd,
        PowerShell,
        Posix,
    }

    public static ShellKind ResolveShellKind(string shellPath)
    {
        var fileName = Path.GetFileName(shellPath ?? "").ToLowerInvariant();
        return fileName switch
        {
            "cmd.exe" => ShellKind.Cmd,
            "pwsh.exe" or "powershell.exe" => ShellKind.PowerShell,
            _ => ShellKind.Posix,
        };
    }

    /// <summary>Environment variable name that carries a template argument, e.g. "file-name" -> CMUX_ARG_FILE_NAME.</summary>
    public static string ArgEnvName(string argName)
    {
        var chars = (argName ?? "").ToUpperInvariant()
            .Select(c => c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' ? c : '_')
            .ToArray();
        return ArgEnvPrefix + new string(chars);
    }

    public static string CwdEnvVariableName => CwdEnvName;

    public sealed record RenderedCommand(string Command, IReadOnlyDictionary<string, string> Env, string? Error);

    /// <summary>
    /// Renders a custom tool command template without splicing argument values into the command text.
    /// Each {{name}} becomes a shell-appropriate reference to an environment variable that carries the value.
    /// {{cwd}} refers to the validated working directory.
    /// </summary>
    public static RenderedCommand RenderCommandTemplate(
        string template,
        IReadOnlyDictionary<string, string> args,
        string cwd,
        ShellKind shell)
    {
        if (!IsSafeLocalDirectory(cwd))
            return new RenderedCommand("", new Dictionary<string, string>(), "Working directory must be an existing fully-qualified local directory (not UNC).");

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CwdEnvName] = cwd,
        };

        foreach (var (name, value) in args)
        {
            if (value.Length > MaxTemplateArgChars)
                return new RenderedCommand("", new Dictionary<string, string>(), $"Argument '{name}' is too long.");
            if (value.Contains('\0'))
                return new RenderedCommand("", new Dictionary<string, string>(), $"Argument '{name}' contains a NUL character.");
            env[ArgEnvName(name)] = value;
        }

        string? error = null;
        var rendered = PlaceholderRegex.Replace(template ?? "", match =>
        {
            if (error != null)
                return "";

            var key = match.Groups[1].Value;
            var envName = string.Equals(key, "cwd", StringComparison.OrdinalIgnoreCase) ? CwdEnvName : ArgEnvName(key);
            if (!env.TryGetValue(envName, out var value))
            {
                value = "";
                env[envName] = value;
            }

            if (shell == ShellKind.Cmd && value.IndexOfAny(CmdMetacharacters.ToCharArray()) >= 0)
            {
                error = $"Argument '{key}' contains characters that are not allowed in cmd.exe templates.";
                return "";
            }

            return ShellReference(shell, envName);
        });

        if (error != null)
            return new RenderedCommand("", new Dictionary<string, string>(), error);

        return new RenderedCommand(rendered, env, null);
    }

    public static string ShellReference(ShellKind shell, string envName)
    {
        return shell switch
        {
            ShellKind.Cmd => $"%{envName}%",
            ShellKind.PowerShell => $"$env:{envName}",
            _ => $"\"${envName}\"",
        };
    }

    /// <summary>True for an existing, fully-qualified, non-UNC local directory.</summary>
    public static bool IsSafeLocalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!Path.IsPathFullyQualified(path))
            return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return false;
        return Directory.Exists(path);
    }

    /// <summary>
    /// Resolves a requested scaffold root against the project root. Returns null when the
    /// resolved path is not the project root or inside it. Relative paths resolve against the project root.
    /// </summary>
    public static string? ResolveScaffoldRoot(string projectRoot, string? requested)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (string.IsNullOrWhiteSpace(requested))
            return root;

        var trimmed = requested.Trim();
        var combined = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(root, trimmed);
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));

        return IsWithinDirectory(root, candidate) ? candidate : null;
    }

    public static bool IsWithinDirectory(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tools that run shell commands, write to panes, write files or call external servers.</summary>
    public static bool RequiresApproval(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return false;

        return toolName is "bash_run" or "cmux_pane_write" or "cmux_pane_run_command" or "cmux_scaffold_agents_files"
            || toolName.StartsWith("custom_", StringComparison.Ordinal)
            || (toolName.StartsWith("mcp_", StringComparison.Ordinal) && !toolName.EndsWith("_error", StringComparison.Ordinal));
    }

    /// <summary>Text buffer that stops growing at a character cap and records that output was dropped.</summary>
    public sealed class CappedTextBuffer
    {
        private readonly StringBuilder _sb = new();
        private readonly int _maxChars;

        public CappedTextBuffer(int maxChars)
        {
            _maxChars = Math.Max(1, maxChars);
        }

        public bool IsTruncated { get; private set; }

        public void AppendLine(string? line)
        {
            if (IsTruncated || line == null)
                return;

            var room = _maxChars - _sb.Length;
            if (line.Length + 1 <= room)
            {
                _sb.Append(line).Append('\n');
                return;
            }

            if (room > 0)
                _sb.Append(line, 0, Math.Min(line.Length, room));
            IsTruncated = true;
        }

        public override string ToString()
        {
            return IsTruncated ? _sb.ToString() + "\n[output truncated]" : _sb.ToString();
        }
    }
}
