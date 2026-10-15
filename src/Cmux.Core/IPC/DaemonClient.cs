using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Cmux.Core.Services;

namespace Cmux.Core.IPC;

public sealed class DaemonClient : IDisposable
{
    private const string PipeName = "cmux-daemon";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private volatile bool _connected; // set after pipe + reader are ready
    private CancellationTokenSource? _listenCts;
    private volatile bool _disposed;

    // Requests awaiting a response, keyed by DaemonRequest.Id. The listen loop completes them.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DaemonResponse?>> _pending = new();
    private readonly object _writeLock = new();

    public bool IsConnected => _pipe?.IsConnected == true && _connected;

    public event Action<string, byte[]>? RawOutputReceived;  // paneId, VT bytes
    public event Action<string, int>? SessionExited;          // paneId, exitCode
    public event Action<string, string>? TitleChanged;        // paneId, title
    public event Action<string, string>? CwdChanged;          // paneId, directory
    public event Action<string>? BellReceived;                // paneId
    public event Action? Connected;
    public event Action? Disconnected;

    /// <summary>
    /// Tries to connect to the daemon pipe synchronously.
    /// Must be called from a background thread (not the UI thread).
    /// Returns true if connected.
    /// </summary>
    public bool TryConnect(int timeoutMs = 300)
    {
        if (IsConnected) return true;

        NamedPipeClientStream? pipe = null;
        try
        {
            // Asynchronous (overlapped) so reads and writes can proceed concurrently on one handle.
            // CurrentUserOnly makes the OS verify the server pipe is owned by this user.
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);

            var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _pipe = pipe;
            _reader = reader;
            _connected = true;
            pipe = null; // now owned by this client
            StartListening(reader);
            LogDaemon("[Connect] Pipe connected, reader ready.");
            return true;
        }
        catch (TimeoutException)
        {
            LogDaemon($"[Connect] Timeout after {timeoutMs}ms");
            return false;
        }
        catch (Exception ex)
        {
            LogDaemon($"[Connect] Error: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    /// <summary>Fires the Connected event. Call after TryConnect succeeds.</summary>
    public void RaiseConnected() => Connected?.Invoke();

    /// <summary>
    /// Starts the daemon process and waits for it to become available.
    /// Must be called from a background thread.
    /// </summary>
    public bool StartDaemonAndConnect(int maxRetries = 20, int retryDelayMs = 500)
    {
        try
        {
            var exePath = FindDaemonExecutable();
            if (exePath == null)
            {
                LogDaemon("[TryStart] Daemon executable not found");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                LogDaemon("[TryStart] Process.Start returned null");
                return false;
            }

            // Retry connecting until daemon is ready
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (TryConnect(1000))
                {
                    LogDaemon($"[TryStart] Connected on attempt {attempt + 1}");
                    return true;
                }

                if (proc.HasExited)
                {
                    LogDaemon($"[TryStart] Daemon process exited with code {proc.ExitCode}");
                    return false;
                }

                Thread.Sleep(retryDelayMs);
            }

            LogDaemon("[TryStart] All attempts failed");
            return false;
        }
        catch (Exception ex)
        {
            LogDaemon($"[TryStart] Exception: {ex.GetType().Name}");
            return false;
        }
    }

    private static string? FindDaemonExecutable()
    {
        // Published layout: the daemon sits next to the app executable.
        var candidate = Path.Combine(AppContext.BaseDirectory, "cmux-daemon.exe");
        if (File.Exists(candidate)) return candidate;

#if DEBUG
        // Dev-tree fallback: walk up to the repo's src folder and search the daemon build output.
        //   appDir: .../src/Cmux/bin/Debug/net10.0-windows10.0.17763.0/
        //   daemon: .../src/Cmux.Daemon/bin/Debug/net10.0-windows/cmux-daemon.exe
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !string.Equals(dir.Name, "src", StringComparison.OrdinalIgnoreCase))
                dir = dir.Parent;

            if (dir != null)
            {
                var daemonBin = Path.Combine(dir.FullName, "Cmux.Daemon", "bin");
                if (Directory.Exists(daemonBin))
                {
                    var config = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
                    var configDir = Path.Combine(daemonBin, config);
                    if (Directory.Exists(configDir))
                    {
                        foreach (var tfmDir in Directory.GetDirectories(configDir))
                        {
                            candidate = Path.Combine(tfmDir, "cmux-daemon.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                    }

                    foreach (var exe in Directory.GetFiles(daemonBin, "cmux-daemon.exe", SearchOption.AllDirectories))
                        return exe;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDaemon($"[FindDaemon] Dev-tree search failed: {ex.GetType().Name}");
        }
#endif

        return null;
    }

    private void StartListening(StreamReader reader)
    {
        _listenCts = new CancellationTokenSource();
        var ct = _listenCts.Token;
        // Dedicated thread: ReadLine blocks.
        var thread = new Thread(() => ListenLoop(reader, ct))
        {
            IsBackground = true,
            Name = "DaemonClient-Listen",
        };
        thread.Start();
    }

    private void ListenLoop(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                HandleLine(line);
            }
        }
        catch (IOException) { } // Daemon went away: the finally block reports the disconnect
        catch (ObjectDisposedException) { } // Dispose() closed the pipe underneath us: expected
        finally
        {
            _connected = false; // Prevent new requests from entering SendRequestAsync
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(null);
            }
            Disconnected?.Invoke();
        }
    }

    /// <summary>
    /// Parses each line once. A line with a non-empty "Id" is a response to that request;
    /// otherwise it is an event (which carries "Type").
    /// </summary>
    private void HandleLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var id = GetString(root, "Id");
            if (!string.IsNullOrEmpty(id))
            {
                if (_pending.TryRemove(id, out var tcs))
                {
                    DaemonResponse? response;
                    try { response = root.Deserialize<DaemonResponse>(); }
                    catch (JsonException) { response = null; }
                    tcs.TrySetResult(response);
                }
                return;
            }

            var type = GetString(root, "Type");
            if (!string.IsNullOrEmpty(type))
                DispatchEvent(root, type);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // Malformed line or a failing subscriber: drop it without logging its contents
            LogDaemon($"[Listen] Dropped line: {ex.GetType().Name}");
        }
    }

    private void DispatchEvent(JsonElement evt, string type)
    {
        var paneId = GetString(evt, "PaneId");
        switch (type)
        {
            case DaemonMessageTypes.EventOutput:
                if (paneId != null && evt.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.String)
                    RawOutputReceived?.Invoke(paneId, data.GetBytesFromBase64());
                break;
            case DaemonMessageTypes.EventExited:
                if (paneId != null)
                    SessionExited?.Invoke(paneId, int.TryParse(GetString(evt, "Data"), out var code) ? code : -1);
                break;
            case DaemonMessageTypes.EventTitleChanged:
                if (paneId != null && GetString(evt, "Data") is { } title)
                    TitleChanged?.Invoke(paneId, title);
                break;
            case DaemonMessageTypes.EventCwdChanged:
                if (paneId != null && GetString(evt, "Data") is { } cwd)
                    CwdChanged?.Invoke(paneId, cwd);
                break;
            case DaemonMessageTypes.EventBell:
                if (paneId != null)
                    BellReceived?.Invoke(paneId);
                break;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Writes one JSON line as raw bytes. Stream.Write avoids StreamWriter.Flush, which blocks on
    /// synchronous pipes. Writes are serialized with a lock so concurrent requests never interleave.
    /// </summary>
    private void WriteToPipe(string line)
    {
        var pipe = _pipe ?? throw new IOException("Not connected to the daemon.");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_writeLock)
        {
            pipe.Write(bytes, 0, bytes.Length);
        }
    }

    private async Task<DaemonResponse?> SendRequestAsync(DaemonRequest request)
    {
        if (!IsConnected) return null;

        var id = Guid.NewGuid().ToString("N");
        request.Id = id;
        var tcs = new TaskCompletionSource<DaemonResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            WriteToPipe(JsonSerializer.Serialize(request));
            return await tcs.Task.WaitAsync(RequestTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogDaemon($"[Request] {request.Type} timed out");
            return null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            LogDaemon($"[Request] {request.Type} failed: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Debug diagnostics, written only when CMUX_DEBUG=1. Never pass request bodies.</summary>
    public static void LogDaemon(string message) => Log.Debug(message);

    public async Task<DaemonSessionInfo?> CreateSessionAsync(string paneId, int cols, int rows, string? workingDirectory = null, string? command = null)
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionCreate,
            PaneId = paneId,
            Cols = cols,
            Rows = rows,
            WorkingDirectory = workingDirectory,
            Command = command,
        });

        if (response?.Success == true && response.Data != null)
            return JsonSerializer.Deserialize<DaemonSessionInfo>(response.Data);
        return null;
    }

    public async Task WriteAsync(string paneId, byte[] data)
    {
        await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionWrite,
            PaneId = paneId,
            Data = Convert.ToBase64String(data),
        });
    }

    public async Task WriteAsync(string paneId, string text)
    {
        await WriteAsync(paneId, Encoding.UTF8.GetBytes(text));
    }

    public async Task ResizeAsync(string paneId, int cols, int rows)
    {
        await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionResize,
            PaneId = paneId,
            Cols = cols,
            Rows = rows,
        });
    }

    public async Task CloseSessionAsync(string paneId)
    {
        await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionClose,
            PaneId = paneId,
        });
    }

    /// <summary>Returns the session's live working directory (OSC 7 / process cwd), or null if unavailable.</summary>
    public async Task<string?> GetLiveWorkingDirectoryAsync(string paneId)
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionCwd,
            PaneId = paneId,
        });
        return response?.Success == true ? response.Data : null;
    }

    public async Task<List<DaemonSessionInfo>> ListSessionsAsync()
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionList,
        });

        if (response?.Success == true && response.Data != null)
            return JsonSerializer.Deserialize<List<DaemonSessionInfo>>(response.Data) ?? [];
        return [];
    }

    public async Task<string?> GetSnapshotAsync(string paneId)
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionSnapshot,
            PaneId = paneId,
        });

        return response?.Success == true ? response.Data : null;
    }

    public async Task<bool> PingAsync()
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.Ping,
        });
        return response?.Success == true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        _listenCts?.Cancel();
        _reader?.Dispose();
        _pipe?.Dispose();
        _listenCts?.Dispose();

        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(null);
        }
    }
}
