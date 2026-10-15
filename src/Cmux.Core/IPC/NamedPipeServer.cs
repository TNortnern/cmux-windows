using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Cmux.Core.Services;

namespace Cmux.Core.IPC;

/// <summary>
/// Named pipe server for cmux CLI/API communication.
/// Windows equivalent of the Unix domain socket used by cmux on macOS.
/// Pipe name: \\.\pipe\cmux (or \\.\pipe\cmux-{tag} for tagged instances).
/// One request per connection: "COMMAND {json object}". The legacy "COMMAND key=value ..." form is also accepted.
/// </summary>
public sealed class NamedPipeServer : IDisposable
{
    internal const int MaxRequestBytes = 1024 * 1024;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public string PipeName => _pipeName;

    /// <summary>
    /// Invoked when a command is received. Args: (command, args dictionary).
    /// Returns the response JSON string.
    /// </summary>
    public Func<string, Dictionary<string, string>, Task<string>>? OnCommand { get; set; }

    public NamedPipeServer(string? tag = null)
    {
        _pipeName = PipeNameFor(tag);
    }

    internal static string PipeNameFor(string? tag) => string.IsNullOrEmpty(tag) ? "cmux" : $"cmux-{tag}";

    public void Start()
    {
        if (_cts != null) return;

        // Create the first instance synchronously so clients can connect as soon as Start returns.
        var first = PipeSecurityFactory.CreateServer(_pipeName);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _listenTask = Task.Run(() => ListenLoop(first, ct));
    }

    private async Task ListenLoop(NamedPipeServerStream? nextPipe, CancellationToken ct)
    {
        NamedPipeServerStream? pipe = nextPipe;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                pipe ??= PipeSecurityFactory.CreateServer(_pipeName);
                await pipe.WaitForConnectionAsync(ct);

                // Ownership of the stream moves to the connection handler.
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleConnection(connected, ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                pipe = null;
                Log.Error("[NamedPipe] Listen error, retrying", ex);
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        pipe?.Dispose();
    }

    private async Task HandleConnection(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(RequestReadTimeout);

                var requestLine = await PipeLineReader.ReadLineAsync(pipe, MaxRequestBytes, readCts.Token);
                if (requestLine == null) return; // EOF, empty, or over the size cap: drop without replying

                // Parse: COMMAND [json | key=value ...]
                var parts = requestLine.Split(' ', 2);
                var command = parts[0].ToUpperInvariant();
                var args = new Dictionary<string, string>();
                if (parts.Length > 1)
                {
                    ParseArgs(parts[1], args);
                }

                var response = await DispatchAsync(command, args);

                using var writer = new StreamWriter(pipe, s_utf8, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(response);
            }
            catch (IOException)
            {
                // Client disconnected mid-request
            }
            catch (OperationCanceledException)
            {
                // Read timeout or server shutting down
            }
        }
    }

    private async Task<string> DispatchAsync(string command, Dictionary<string, string> args)
    {
        if (OnCommand == null)
            return JsonSerializer.Serialize(new { error = "No handler registered" });

        try
        {
            return await OnCommand(command, args);
        }
        catch (Exception ex)
        {
            Log.Error($"[NamedPipe] Command {command} failed", ex);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    internal static void ParseArgs(string argsString, Dictionary<string, string> args)
    {
        var trimmed = argsString.Trim();
        if (trimmed.StartsWith('{') && TryParseJsonArgs(trimmed, args))
            return;

        // Legacy form: key=value (quotes group values with spaces); bare words become positional args
        foreach (var part in SplitRespectingQuotes(trimmed))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                var key = part[..eq];
                var value = part[(eq + 1)..].Trim('"', '\'');
                args[key] = value;
            }
            else
            {
                args.TryAdd("_arg" + args.Count, part);
            }
        }
    }

    private static bool TryParseJsonArgs(string json, Dictionary<string, string> args)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                    continue;

                args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
            }
            return true;
        }
        catch (JsonException)
        {
            // Not JSON after all: caller falls back to key=value parsing
            return false;
        }
    }

    private static IEnumerable<string> SplitRespectingQuotes(string input)
    {
        var current = new StringBuilder();
        bool inQuote = false;
        char quoteChar = '\0';

        foreach (var c in input)
        {
            if (inQuote)
            {
                if (c == quoteChar)
                {
                    inQuote = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == ' ')
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listenTask?.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
