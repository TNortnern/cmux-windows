using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cmux.Core.IPC;
using Cmux.Core.Services;

namespace Cmux.Daemon;

public sealed class DaemonPipeServer
{
    private const string PipeName = "cmux-daemon";
    private const int OutboxCapacity = 4096;
    private const int MaxRequestChars = 16 * 1024 * 1024;
    private const int ReadBufferBytes = 65536;

    private readonly DaemonSessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private int _connectedClients;

    public int ConnectedClients => _connectedClients;

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;

    private sealed record ClientConnection(string Id, Channel<string> Outbox, NamedPipeServerStream Pipe);

    public DaemonPipeServer(DaemonSessionManager sessionManager)
    {
        _sessionManager = sessionManager;

        _sessionManager.RawOutput += (paneId, data) =>
            BroadcastEvent(new DaemonEvent
            {
                Type = DaemonMessageTypes.EventOutput,
                PaneId = paneId,
                Data = Convert.ToBase64String(data),
            });

        _sessionManager.SessionExited += (paneId, exitCode) =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventExited, PaneId = paneId, Data = exitCode.ToString() });

        _sessionManager.TitleChanged += (paneId, title) =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventTitleChanged, PaneId = paneId, Data = title });

        _sessionManager.CwdChanged += (paneId, dir) =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventCwdChanged, PaneId = paneId, Data = dir });

        _sessionManager.BellReceived += paneId =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventBell, PaneId = paneId });
    }

    public void Run(CancellationToken ct)
    {
        Log.Info($"[PipeServer] Listening on \\\\.\\pipe\\{PipeName}");

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // Per-user ACL + CurrentUserOnly; overlapped I/O so reads and writes proceed concurrently.
                pipe = PipeSecurityFactory.CreateServer(PipeName);
                pipe.WaitForConnection();

                // Ownership of the stream moves to the connection thread.
                var connected = pipe;
                pipe = null;
                var thread = new Thread(() => HandleConnection(connected))
                {
                    IsBackground = true,
                    Name = "PipeServer-Client",
                };
                thread.Start();
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Error("[PipeServer] Accept failed, retrying", ex);
                Thread.Sleep(100);
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream pipe)
    {
        var clientId = Guid.NewGuid().ToString("N")[..8];
        // Bounded outbox: a client that falls 4096 messages behind is disconnected rather than
        // letting the daemon buffer without limit.
        var outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboxCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var client = new ClientConnection(clientId, outbox, pipe);

        _clients[clientId] = client;
        Interlocked.Increment(ref _connectedClients);
        ClientConnected?.Invoke();
        LogDaemon($"[PipeServer] Client {clientId} connected (total: {_connectedClients}).");

        try
        {
            using (pipe)
            {
                var writerThread = new Thread(() => WriteLoop(pipe, outbox.Reader))
                {
                    IsBackground = true,
                    Name = $"PipeServer-Writer-{clientId}",
                };
                writerThread.Start();

                ReadLoop(client);

                outbox.Writer.TryComplete();
                writerThread.Join(TimeSpan.FromSeconds(5));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            outbox.Writer.TryComplete();
            _clients.TryRemove(clientId, out _);
            Interlocked.Decrement(ref _connectedClients);
            ClientDisconnected?.Invoke();
            LogDaemon($"[PipeServer] Client {clientId} disconnected (remaining: {_connectedClients}, sessions: {_sessionManager.ActiveSessionCount}).");
        }
    }

    /// <summary>Drains the outbox to the pipe. Exits when the outbox completes or the pipe breaks.</summary>
    private static void WriteLoop(NamedPipeServerStream pipe, ChannelReader<string> reader)
    {
        try
        {
            foreach (var json in reader.ReadAllAsync().ToBlockingEnumerable())
            {
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                pipe.Write(bytes, 0, bytes.Length);
            }
        }
        catch (IOException) { } // Client vanished mid-write: the read side handles cleanup
        catch (ObjectDisposedException) { } // Pipe disposed by disconnect: expected
    }

    /// <summary>
    /// Reads raw bytes, decodes UTF-8 incrementally (a multi-byte character split across reads is
    /// preserved by the Decoder), and splits on '\n'. A request line over 16 MB drops the client.
    /// </summary>
    private void ReadLoop(ClientConnection client)
    {
        var bytes = new byte[ReadBufferBytes];
        // Decoder may emit up to the byte count plus up to 3 bytes carried over from the previous read.
        var chars = new char[bytes.Length + 4];
        var decoder = Encoding.UTF8.GetDecoder();
        var line = new StringBuilder();

        while (true)
        {
            int read;
            try
            {
                read = client.Pipe.Read(bytes, 0, bytes.Length);
            }
            catch (IOException) { return; } // Client disconnected
            catch (ObjectDisposedException) { return; } // Disconnected from another thread (slow client)

            if (read == 0)
            {
                LogDaemon($"[PipeServer:{client.Id}] EOF");
                return;
            }

            int charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            Span<char> rest = chars.AsSpan(0, charCount);

            while (true)
            {
                int newline = rest.IndexOf('\n');
                var segment = newline < 0 ? rest : rest[..newline];

                if (line.Length + segment.Length > MaxRequestChars)
                {
                    Log.Info($"[PipeServer:{client.Id}] Request exceeded 16 MB; disconnecting client.");
                    return;
                }
                line.Append(segment);

                if (newline < 0) break;
                rest = rest[(newline + 1)..];

                int length = line.Length;
                if (length > 0 && line[length - 1] == '\r') length--;
                if (length > 0)
                {
                    var response = ProcessRequest(line.ToString(0, length));
                    if (!client.Outbox.Writer.TryWrite(response))
                    {
                        DisconnectSlowClient(client);
                        return;
                    }
                }
                line.Clear();
            }
        }
    }

    private string ProcessRequest(string requestJson)
    {
        DaemonRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<DaemonRequest>(requestJson);
        }
        catch (JsonException)
        {
            return Serialize(Fail("Malformed request"));
        }

        if (request == null)
            return Serialize(Fail("Invalid request"));

        // Writes are too frequent to log; request bodies are never logged
        if (request.Type != DaemonMessageTypes.SessionWrite)
            LogDaemon($"[PipeServer] Request: {request.Type} pane={request.PaneId}");

        DaemonResponse response;
        try
        {
            response = request.Type switch
            {
                DaemonMessageTypes.SessionCreate => HandleSessionCreate(request),
                DaemonMessageTypes.SessionWrite => HandleSessionWrite(request),
                DaemonMessageTypes.SessionResize => HandleSessionResize(request),
                DaemonMessageTypes.SessionClose => HandleSessionClose(request),
                DaemonMessageTypes.SessionList => HandleSessionList(),
                DaemonMessageTypes.SessionSnapshot => HandleSessionSnapshot(request),
                DaemonMessageTypes.SessionCwd => HandleSessionCwd(request),
                DaemonMessageTypes.Ping => Ok("pong"),
                _ => Fail($"Unknown command: {request.Type}"),
            };
        }
        catch (Exception ex)
        {
            response = Fail(ex.Message);
        }

        // Echo the correlation Id so the client can match this response to its request
        response.Id = request.Id;
        return Serialize(response);
    }

    private DaemonResponse HandleSessionCreate(DaemonRequest request)
    {
        var paneId = RequirePaneId(request);

        // Never echo the command line into the error or logs
        if (!DaemonInputValidation.IsCommandAllowed(request.Command))
            return Fail("Command program is not an allowed executable.");

        var info = _sessionManager.CreateSession(
            paneId,
            DaemonInputValidation.ClampCols(request.Cols ?? 120),
            DaemonInputValidation.ClampRows(request.Rows ?? 30),
            DaemonInputValidation.ValidateWorkingDirectory(request.WorkingDirectory),
            string.IsNullOrWhiteSpace(request.Command) ? null : request.Command);

        return Ok(JsonSerializer.Serialize(info));
    }

    private DaemonResponse HandleSessionWrite(DaemonRequest request)
    {
        var paneId = RequirePaneId(request);
        var data = request.Data != null ? Convert.FromBase64String(request.Data) : Array.Empty<byte>();
        _sessionManager.WriteToSession(paneId, data);
        return Ok();
    }

    private DaemonResponse HandleSessionResize(DaemonRequest request)
    {
        var paneId = RequirePaneId(request);
        _sessionManager.ResizeSession(
            paneId,
            DaemonInputValidation.ClampCols(request.Cols ?? 120),
            DaemonInputValidation.ClampRows(request.Rows ?? 30));
        return Ok();
    }

    private DaemonResponse HandleSessionClose(DaemonRequest request)
    {
        // Closing a pane that is already gone is a success
        _sessionManager.CloseSession(RequirePaneId(request));
        return Ok();
    }

    private DaemonResponse HandleSessionList()
    {
        return Ok(JsonSerializer.Serialize(_sessionManager.ListSessions()));
    }

    private DaemonResponse HandleSessionSnapshot(DaemonRequest request)
    {
        return Ok(_sessionManager.GetSnapshot(RequirePaneId(request)));
    }

    private DaemonResponse HandleSessionCwd(DaemonRequest request)
    {
        var session = _sessionManager.GetSession(RequirePaneId(request));
        if (session == null)
            return Fail("Session not found.");

        return Ok(session.GetLiveWorkingDirectory());
    }

    private static string RequirePaneId(DaemonRequest request) =>
        request.PaneId ?? throw new ArgumentException("PaneId required");

    private static DaemonResponse Ok(string? data = null) => new() { Success = true, Data = data };

    private static DaemonResponse Fail(string error) => new() { Success = false, Error = error };

    private static string Serialize(DaemonResponse response) => JsonSerializer.Serialize(response);

    private void BroadcastEvent(DaemonEvent evt)
    {
        var json = JsonSerializer.Serialize(evt);
        foreach (var client in _clients.Values)
        {
            if (!client.Outbox.Writer.TryWrite(json))
                DisconnectSlowClient(client);
        }
    }

    private void DisconnectSlowClient(ClientConnection client)
    {
        Log.Info($"[PipeServer:{client.Id}] Outbox full; disconnecting slow client.");
        client.Outbox.Writer.TryComplete();
        // Disposing the pipe unblocks the read loop and the writer thread
        client.Pipe.Dispose();
    }

    private static void LogDaemon(string message) => Log.Debug(message);
}
