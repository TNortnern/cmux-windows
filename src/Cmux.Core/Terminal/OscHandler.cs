namespace Cmux.Core.Terminal;

/// <summary>
/// Handles OSC (Operating System Command) terminal sequences.
/// Specifically detects notification sequences (OSC 9, 99, 777)
/// used by AI coding agents like Claude Code and Codex.
/// </summary>
public class OscHandler
{
    public event Action<string>? TitleChanged;
    public event Action<string>? WorkingDirectoryChanged;
    public event Action<string, string?, string>? NotificationReceived; // title, subtitle, body
    public event Action<char, string?>? ShellPromptMarker;

    /// <summary>
    /// Processes an OSC string (without the ESC ] prefix and BEL/ST terminator).
    /// </summary>
    public void Handle(string oscString)
    {
        if (string.IsNullOrEmpty(oscString)) return;

        // Split on first ';' to get the OSC code
        int semicolonIndex = oscString.IndexOf(';');
        string codeStr;
        string payload;

        if (semicolonIndex >= 0)
        {
            codeStr = oscString[..semicolonIndex];
            payload = oscString[(semicolonIndex + 1)..];
        }
        else
        {
            codeStr = oscString;
            payload = "";
        }

        if (!int.TryParse(codeStr, out int code))
            return;

        switch (code)
        {
            case 0: // Set icon name and window title
            case 2: // Set window title
                TitleChanged?.Invoke(payload);
                break;

            case 7: // Set working directory (file://host/path)
                HandleWorkingDirectory(payload);
                break;

            case 9: // OSC 9;9;<cwd> (ConEmu/Windows Terminal cwd) or OSC 9;<body> notification
                HandleOsc9(payload);
                break;

            case 99: // Extended notification (key=value pairs)
                HandleOsc99(payload);
                break;

            case 777: // Custom notification (notify;title;body)
                HandleOsc777(payload);
                break;

            case 133: // Shell integration (prompt markers)
                if (payload.Length > 0)
                {
                    var marker = payload[0];
                    string? markerPayload = null;

                    if (payload.Length > 1)
                    {
                        markerPayload = payload[1] == ';'
                            ? payload[2..]
                            : payload[1..];
                    }

                    ShellPromptMarker?.Invoke(marker, string.IsNullOrWhiteSpace(markerPayload) ? null : markerPayload);
                }
                break;
        }
    }

    private void HandleOsc9(string payload)
    {
        // OSC 9;9;<path> — cwd report (path may be quoted)
        if (payload.StartsWith("9;", StringComparison.Ordinal))
        {
            HandleWorkingDirectory(payload[2..].Trim('"'));
            return;
        }

        // OSC 9;4;... — ConEmu progress indicator, not a notification
        if (payload == "4" || payload.StartsWith("4;", StringComparison.Ordinal))
            return;

        // OSC 9;<body> — simple notification
        NotificationReceived?.Invoke("Terminal", null, payload);
    }

    private void HandleWorkingDirectory(string payload)
    {
        if (TryGetLocalPath(payload, out var path))
            WorkingDirectoryChanged?.Invoke(path);
    }

    /// <summary>
    /// Accepts a file:// URI (empty, localhost or this machine as host) or a plain path,
    /// and returns a fully-qualified local path. Rejects UNC and remote hosts.
    /// </summary>
    private static bool TryGetLocalPath(string payload, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(payload))
            return false;

        try
        {
            string candidate = payload;

            if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                string rest = candidate["file://".Length..];
                int slash = rest.IndexOf('/');
                if (slash < 0)
                    return false;

                string host = rest[..slash];
                if (host.Length > 0 &&
                    !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                    !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    return false;

                candidate = Uri.UnescapeDataString(rest[slash..]);

                // "/C:/dir" -> "C:/dir"
                if (candidate.Length >= 3 && candidate[0] == '/' && char.IsAsciiLetter(candidate[1]) && candidate[2] == ':')
                    candidate = candidate[1..];
            }

            candidate = candidate.Replace('/', '\\');

            if (candidate.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            if (!Path.IsPathFullyQualified(candidate))
                return false;

            path = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// OSC 99: Extended notification format.
    /// Format: key=value;key=value
    /// Keys: i (id), d (icon), t (title), b (body), p (progress)
    /// Some implementations use: OSC 99 ; title ; body
    /// </summary>
    private void HandleOsc99(string payload)
    {
        // Try key=value format first
        if (payload.Contains('='))
        {
            string? title = null;
            string? body = null;
            string? subtitle = null;

            foreach (var pair in payload.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();

                switch (key)
                {
                    case "t": title = value; break;
                    case "b": body = value; break;
                    case "s": subtitle = value; break;
                }
            }

            if (title != null || body != null)
            {
                NotificationReceived?.Invoke(
                    title ?? "Terminal",
                    subtitle,
                    body ?? title ?? "");
            }
        }
        else
        {
            // Simpler format: OSC 99 ; body
            NotificationReceived?.Invoke("Terminal", null, payload);
        }
    }

    /// <summary>
    /// OSC 777: notify;title;body format.
    /// Used by rxvt-unicode and adopted by other terminals.
    /// </summary>
    private void HandleOsc777(string payload)
    {
        var parts = payload.Split(';', 3);
        if (parts.Length >= 1 && parts[0] == "notify")
        {
            string title = parts.Length >= 2 ? parts[1] : "Terminal";
            string body = parts.Length >= 3 ? parts[2] : "";
            NotificationReceived?.Invoke(title, null, body);
        }
        else
        {
            NotificationReceived?.Invoke("Terminal", null, payload);
        }
    }
}
