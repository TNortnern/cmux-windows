namespace Cmux.Core.Services;

/// <summary>
/// Minimal diagnostic file logger (%LOCALAPPDATA%\cmux\logs\cmux.log, rotated at 1 MB).
/// Debug lines are written only when CMUX_DEBUG=1. Never pass terminal content or command lines.
/// </summary>
public static class Log
{
    public static void Debug(string message) { } // STUB: package E implements
    public static void Info(string message) { }
    public static void Error(string message, Exception? ex = null) { }
}
