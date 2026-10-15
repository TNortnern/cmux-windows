using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Cmux.Core.IPC;

/// <summary>
/// Client for the cmux control pipe (used by the CLI).
/// Sends "COMMAND {json}" and reads one response line. The client-side CurrentUserOnly option
/// makes the OS verify the server is owned by the current user (anti pipe-squatting).
/// </summary>
public static class NamedPipeClient
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<string> SendCommand(string command, Dictionary<string, string>? args = null, string? tag = null, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var pipe = new NamedPipeClientStream(
            ".",
            NamedPipeServer.PipeNameFor(tag),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await pipe.ConnectAsync(cts.Token);

        var json = JsonSerializer.Serialize(args ?? new Dictionary<string, string>());
        var payload = s_utf8.GetBytes($"{command} {json}\n");
        await pipe.WriteAsync(payload, cts.Token);

        var response = await PipeLineReader.ReadLineAsync(pipe, MaxResponseBytes, cts.Token);
        return response ?? "";
    }
}
