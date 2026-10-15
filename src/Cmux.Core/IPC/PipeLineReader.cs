using System.IO;
using System.Text;

namespace Cmux.Core.IPC;

internal static class PipeLineReader
{
    /// <summary>
    /// Reads one '\n'-terminated UTF-8 line with a hard size cap. Trailing '\r' is stripped.
    /// Returns null on EOF, on an empty line, or when the line exceeds <paramref name="maxBytes"/>
    /// (the caller then drops the connection without replying).
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var chunk = new byte[4096];
        using var line = new MemoryStream();
        while (true)
        {
            int n = await stream.ReadAsync(chunk, ct);
            if (n == 0) break;

            int newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
            int take = newline >= 0 ? newline : n;
            if (line.Length + take > maxBytes) return null;

            line.Write(chunk, 0, take);
            if (newline >= 0) break;
        }

        if (line.Length == 0) return null;
        return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
    }
}
