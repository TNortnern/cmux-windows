using System.Text;

namespace Cmux.Core.Services;

/// <summary>
/// Minimal diagnostic file logger (%LOCALAPPDATA%\cmux\logs\cmux.log, rotated at 1 MB).
/// Debug lines are written only when CMUX_DEBUG=1. Never pass terminal content or command lines.
/// Logging never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private const string FileName = "cmux.log";
    private const string OldFileName = "cmux.log.old";

    private static readonly object Gate = new();
    private static readonly bool DebugEnabled = Environment.GetEnvironmentVariable("CMUX_DEBUG") == "1";

    public static void Debug(string message)
    {
        if (DebugEnabled)
            Write("DEBUG", message, null);
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder(128)
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);

            if (ex != null)
                sb.Append(" (").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(')');

            sb.AppendLine();

            lock (Gate)
            {
                var dir = CmuxPaths.LogsDirectory;
                var path = Path.Combine(dir, FileName);

                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    var old = Path.Combine(dir, OldFileName);
                    File.Move(path, old, overwrite: true);
                }

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
