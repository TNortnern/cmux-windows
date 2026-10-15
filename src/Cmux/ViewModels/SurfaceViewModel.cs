using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.Core.Terminal;

namespace Cmux.ViewModels;

/// <summary>
/// One tab. Owns the split layout and one terminal session per pane. Terminal sessions are
/// started lazily (<see cref="EnsureSessionsStarted"/>) so restored tabs cost nothing until shown.
/// </summary>
public partial class SurfaceViewModel : ObservableObject, IDisposable
{
    public Surface Surface { get; }
    private readonly string _workspaceId;
    private readonly NotificationService _notificationService;
    private readonly string? _initialWorkingDirectory;
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly Dictionary<string, List<string>> _paneCommandHistory = [];
    private readonly ConcurrentDictionary<string, string?> _paneShells = new();
    // Panes whose session currently lives in the daemon.
    private readonly ConcurrentDictionary<string, byte> _daemonPanes = new();
    // Panes that were ever daemon-backed; they need an explicit CLOSE when the user closes them.
    private readonly ConcurrentDictionary<string, byte> _everDaemonPanes = new();
    private readonly ConcurrentDictionary<string, string?> _daemonCwd = new();
    private readonly ConcurrentDictionary<string, int> _daemonPids = new();
    private bool _sessionsStarted;
    private bool _disposed;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private SplitNode _rootNode;

    [ObservableProperty]
    private string? _focusedPaneId;

    [ObservableProperty]
    private bool _isZoomed;

    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>Raised when the last pane of this tab has closed; the owner should close the tab.</summary>
    public event Action? LastPaneClosed;

    public bool SessionsStarted => _sessionsStarted;

    /// <summary>Gets the shell process PID of the focused pane (local process or daemon-reported).</summary>
    public int? ShellPid
    {
        get
        {
            var paneId = FocusedPaneId;
            if (paneId == null) return null;
            if (_sessions.TryGetValue(paneId, out var session) && session.ProcessId is int localPid)
                return localPid;
            return _daemonPids.TryGetValue(paneId, out var daemonPid) ? daemonPid : null;
        }
    }

    public SurfaceViewModel(Surface surface, string workspaceId, NotificationService notificationService, string? initialWorkingDirectory = null)
    {
        Surface = surface;
        _workspaceId = workspaceId;
        _notificationService = notificationService;
        _initialWorkingDirectory = WorkingDirectoryPolicy.Sanitize(initialWorkingDirectory);
        _name = surface.Name;
        _rootNode = surface.RootSplitNode;
        _focusedPaneId = surface.FocusedPaneId;

        // Wire daemon events for session persistence (only when the daemon is enabled)
        if (App.IsDaemonEnabled)
        {
            var daemon = App.DaemonClient;
            daemon.RawOutputReceived += OnDaemonRawOutput;
            daemon.CwdChanged += OnDaemonCwdChanged;
            daemon.TitleChanged += OnDaemonTitleChanged;
            daemon.SessionExited += OnDaemonSessionExited;
            daemon.BellReceived += OnDaemonBellReceived;
            daemon.Disconnected += OnDaemonDisconnected;
        }

        // Command history is small text; load it now so the history UI works before sessions start.
        foreach (var leaf in _rootNode.GetLeaves())
        {
            if (leaf.PaneId == null) continue;
            if (Surface.PaneSnapshots.TryGetValue(leaf.PaneId, out var snapshot) && snapshot.CommandHistory is { Count: > 0 })
            {
                _paneCommandHistory[leaf.PaneId] = snapshot.CommandHistory
                    .Select(App.CommandLogService.SanitizeCommandForStorage)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Cast<string>()
                    .ToList();
            }
        }

        if (_focusedPaneId == null)
        {
            var firstLeaf = _rootNode.GetLeaves().FirstOrDefault();
            if (firstLeaf?.PaneId != null)
                FocusedPaneId = firstLeaf.PaneId;
        }
    }

    /// <summary>
    /// Starts a terminal session for every pane that has none yet. Restored panes get their stored
    /// snapshot applied, after which the snapshot is dropped from the surface.
    /// </summary>
    public void EnsureSessionsStarted()
    {
        if (_sessionsStarted || _disposed) return;
        _sessionsStarted = true;

        var panes = RootNode.GetLeaves().Select(l => l.PaneId).Where(id => id != null).Cast<string>().ToList();
        var firstPaneId = panes.FirstOrDefault();

        foreach (var paneId in panes)
        {
            Surface.PaneSnapshots.TryGetValue(paneId, out var snapshot);
            var cwd = paneId == firstPaneId ? _initialWorkingDirectory : null;

            try
            {
                StartSession(paneId, cwd, snapshot, snapshot?.Shell);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not start terminal session for pane {paneId}", ex);
                continue;
            }

            if (snapshot != null)
                Surface.PaneSnapshots.Remove(paneId);
        }
    }

    private void OnDaemonRawOutput(string paneId, byte[] data)
    {
        if (!_daemonPanes.ContainsKey(paneId)) return;
        if (_sessions.TryGetValue(paneId, out var session))
            session.FeedOutput(data);
    }

    private void OnDaemonCwdChanged(string paneId, string dir)
    {
        if (!_daemonPanes.ContainsKey(paneId)) return;
        _daemonCwd[paneId] = dir;
        if (_sessions.TryGetValue(paneId, out var session))
            session.WorkingDirectory = dir;
        if (paneId == FocusedPaneId)
            WorkingDirectoryChanged?.Invoke(dir);
    }

    private void OnDaemonTitleChanged(string paneId, string title)
    {
        if (!_daemonPanes.ContainsKey(paneId)) return;
    }

    private void OnDaemonSessionExited(string paneId, int exitCode)
    {
        if (!_daemonPanes.ContainsKey(paneId)) return;
        // The daemon already dropped this session, so there is nothing to CLOSE.
        _daemonPanes.TryRemove(paneId, out _);
        _everDaemonPanes.TryRemove(paneId, out _);
        if (_sessions.TryGetValue(paneId, out var session))
        {
            session.DaemonWrite = null;
            session.DaemonResize = null;
            OnPaneProcessExited(paneId, session);
        }
    }

    private void OnDaemonBellReceived(string paneId)
    {
        if (!_daemonPanes.ContainsKey(paneId)) return;
    }

    private void OnDaemonDisconnected()
    {
        // Daemon died — fall back all daemon sessions to local ConPTY
        var paneIds = _daemonPanes.Keys.ToList();
        if (paneIds.Count == 0) return;

        DaemonLog($"[DaemonDisconnected] Falling back {paneIds.Count} sessions to local ConPTY");

        foreach (var paneId in paneIds)
        {
            if (!_sessions.TryGetValue(paneId, out var session)) continue;

            var cwd = session.WorkingDirectory;
            session.DaemonWrite = null;
            session.DaemonResize = null;

            try
            {
                session.Start(command: _paneShells.GetValueOrDefault(paneId) ?? GetConfiguredShell(), workingDirectory: cwd);
                DaemonLog($"[DaemonDisconnected] {paneId} → local session started");
            }
            catch (Exception ex)
            {
                DaemonLog($"[DaemonDisconnected] {paneId} → local start failed: {ex.Message}");
            }
        }

        _daemonPanes.Clear();
    }

    public TerminalSession? GetSession(string paneId)
    {
        return _sessions.GetValueOrDefault(paneId);
    }

    /// <summary>
    /// Best-known current directory of the focused pane, used as the start directory for a new
    /// tab or split. Returns null when no usable directory is known.
    /// </summary>
    public string? GetFocusedWorkingDirectory()
    {
        var paneId = FocusedPaneId;
        if (paneId == null) return null;

        if (!_sessions.TryGetValue(paneId, out var session))
            return WorkingDirectoryPolicy.Sanitize(Surface.PaneSnapshots.GetValueOrDefault(paneId)?.WorkingDirectory);

        if (_daemonPanes.ContainsKey(paneId))
        {
            var cached = _daemonCwd.GetValueOrDefault(paneId);
            return WorkingDirectoryPolicy.ChooseForNewPane(cached, session.WorkingDirectory, null);
        }

        return WorkingDirectoryPolicy.ChooseForNewPane(session.GetLiveWorkingDirectory(), session.WorkingDirectory, null);
    }

    public string GetPaneTitle(string paneId, string? fallbackTitle)
    {
        if (Surface.PaneCustomNames.TryGetValue(paneId, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;

        return fallbackTitle ?? "Terminal";
    }

    public void SetPaneCustomName(string paneId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            Surface.PaneCustomNames.Remove(paneId);
        else
            Surface.PaneCustomNames[paneId] = name.Trim();

        OnPropertyChanged(nameof(RootNode));
    }

    public IReadOnlyList<string> GetCommandHistory(string paneId)
    {
        return _paneCommandHistory.TryGetValue(paneId, out var history)
            ? history.AsReadOnly()
            : [];
    }

    private static bool ShouldCaptureTranscript(string reason)
    {
        var settings = SettingsService.Current;

        if (string.Equals(reason, "clear-terminal", StringComparison.OrdinalIgnoreCase))
            return settings.CaptureTranscriptsOnClear;

        return settings.CaptureTranscriptsOnClose;
    }

    public string? CapturePaneTranscript(string paneId, string reason)
    {
        if (!ShouldCaptureTranscript(reason))
            return null;

        if (!_sessions.TryGetValue(paneId, out var session))
            return null;

        var text = session.Buffer.ExportPlainText(maxScrollbackLines: 20000);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return App.CommandLogService.SaveTerminalTranscript(
            _workspaceId,
            Surface.Id,
            paneId,
            session.WorkingDirectory,
            text,
            reason);
    }

    public int CaptureAllPaneTranscripts(string reason)
    {
        if (!ShouldCaptureTranscript(reason))
            return 0;

        int captured = 0;

        var paneIds = RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var paneId in paneIds)
        {
            if (CapturePaneTranscript(paneId, reason) != null)
                captured++;
        }

        return captured;
    }

    public void CapturePaneSnapshotsForPersistence()
    {
        var activePaneIds = RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet();

        foreach (var paneId in activePaneIds)
        {
            // Panes whose sessions have not started keep their stored snapshot untouched.
            if (!_sessions.TryGetValue(paneId, out var session))
                continue;

            var state = Surface.PaneSnapshots.TryGetValue(paneId, out var existing)
                ? existing
                : new PaneStateSnapshot();

            state.CapturedAt = DateTime.UtcNow;
            state.WorkingDirectory = session.WorkingDirectory;
            state.Shell = _paneShells.GetValueOrDefault(paneId);
            state.BufferSnapshot = session.CreateBufferSnapshot(maxScrollbackLines: 3000);

            if (_paneCommandHistory.TryGetValue(paneId, out var history))
                state.CommandHistory = history.TakeLast(500).ToList();

            Surface.PaneSnapshots[paneId] = state;
        }

        var stalePaneIds = Surface.PaneSnapshots.Keys.Where(id => !activePaneIds.Contains(id)).ToList();
        foreach (var paneId in stalePaneIds)
            Surface.PaneSnapshots.Remove(paneId);
    }

    public void RegisterCommandSubmission(string paneId, string command)
    {
        var sanitized = App.CommandLogService.SanitizeCommandForStorage(command);
        if (string.IsNullOrWhiteSpace(sanitized))
            return;

        AppendToCommandHistory(paneId, sanitized);

        var cwd = _sessions.TryGetValue(paneId, out var session)
            ? session.WorkingDirectory
            : null;

        App.CommandLogService.RecordManualCommandSubmission(
            paneId,
            _workspaceId,
            Surface.Id,
            sanitized,
            cwd);
    }

    public bool TryHandlePaneCommand(string paneId, string command)
    {
        // Agent is opt-in: never touch (and so never create) the runtime when disabled.
        if (!SettingsService.Current.Agent.Enabled)
            return false;

        if (!_sessions.TryGetValue(paneId, out var session))
            return false;

        return App.AgentRuntime.TryHandlePaneCommand(
            command,
            new Cmux.Services.AgentPaneContext
            {
                WorkspaceId = _workspaceId,
                SurfaceId = Surface.Id,
                PaneId = paneId,
                WorkingDirectory = session.WorkingDirectory,
                WriteToPane = text =>
                {
                    if (!string.IsNullOrEmpty(text))
                        session.Write(text);
                },
            });
    }

    private void AppendToCommandHistory(string paneId, string command)
    {
        if (!_paneCommandHistory.TryGetValue(paneId, out var history))
        {
            history = [];
            _paneCommandHistory[paneId] = history;
        }

        var trimmed = command.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        if (history.Count == 0 || !string.Equals(history[^1], trimmed, StringComparison.Ordinal))
            history.Add(trimmed);

        while (history.Count > 500)
            history.RemoveAt(0);
    }

    private TerminalSession StartSession(string paneId, string? workingDirectory = null, PaneStateSnapshot? restoredState = null, string? shell = null)
    {
        var effectiveShell = shell ?? GetConfiguredShell();
        // Store the explicit override (null = use default shell from settings)
        _paneShells[paneId] = shell;

        try
        {
            // Never block the UI thread waiting for the daemon. The daemon is opt-in and only
            // used when it is already connected; otherwise panes run locally.
            var connect = App.DaemonConnectTask;
            bool daemonReady = connect.IsCompletedSuccessfully && connect.Result;

            DaemonLog($"[StartSession:{paneId}] daemonReady={daemonReady}, TaskStatus={connect.Status}");

            if (daemonReady)
            {
                try
                {
                    return StartDaemonSession(paneId, workingDirectory, restoredState, effectiveShell);
                }
                catch (Exception ex)
                {
                    DaemonLog($"[StartSession:{paneId}] Daemon session failed: {ex.Message}");
                }
            }

            DaemonLog($"[StartSession:{paneId}] Using LOCAL session");
            return StartLocalSession(paneId, workingDirectory, restoredState, effectiveShell);
        }
        catch
        {
            // Do not leave a half-registered session behind.
            if (_sessions.TryRemove(paneId, out var failed))
                failed.Dispose();
            _paneShells.TryRemove(paneId, out _);
            throw;
        }
    }

    private static void DaemonLog(string message) => App.DaemonLog(message);

    private static int GetScrollbackLines() =>
        Math.Clamp(SettingsService.Current.ScrollbackLines, 100, 100_000);

    private TerminalSession StartDaemonSession(string paneId, string? workingDirectory, PaneStateSnapshot? restoredState, string? shell)
    {
        // Use saved snapshot dimensions if available (avoids spurious resize on reconnect)
        var initCols = restoredState?.BufferSnapshot?.Cols ?? 120;
        var initRows = restoredState?.BufferSnapshot?.Rows ?? 30;
        var session = new TerminalSession(paneId, initCols, initRows, GetScrollbackLines());
        WireSessionEvents(session, paneId);

        // Set daemon delegates so Write/Resize route through daemon
        var daemon = App.DaemonClient;
        session.DaemonWrite = data => daemon.WriteAsync(paneId, data);
        session.DaemonResize = (cols, rows) => daemon.ResizeAsync(paneId, cols, rows);

        _sessions[paneId] = session;
        _daemonPanes[paneId] = 0;
        _everDaemonPanes[paneId] = 0;

        var effectiveCwd = workingDirectory ?? restoredState?.WorkingDirectory;

        // Create/attach session on daemon asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                DaemonLog($"[DaemonSession:{paneId}] Calling CreateSessionAsync ({initCols}x{initRows})...");
                var result = await daemon.CreateSessionAsync(
                    paneId, initCols, initRows, effectiveCwd);

                if (result == null)
                {
                    DaemonLog($"[DaemonSession:{paneId}] CreateSessionAsync returned NULL — falling back to local");
                    _daemonPanes.TryRemove(paneId, out _);
                    session.DaemonWrite = null;
                    session.DaemonResize = null;
                    session.Start(command: shell, workingDirectory: effectiveCwd);
                    return;
                }

                DaemonLog($"[DaemonSession:{paneId}] CreateSessionAsync OK: IsExisting={result.IsExisting}, IsRunning={result.IsRunning}");

                if (result.Pid is int pid)
                    _daemonPids[paneId] = pid;

                // Set working directory from daemon response
                if (!string.IsNullOrEmpty(result.WorkingDirectory))
                {
                    session.WorkingDirectory = result.WorkingDirectory;
                    _daemonCwd[paneId] = result.WorkingDirectory;
                }

                // If reconnecting to an existing daemon session, get the live buffer snapshot
                if (result.IsExisting && result.IsRunning)
                {
                    DaemonLog($"[DaemonSession:{paneId}] Reconnecting — fetching daemon snapshot...");
                    var snapshotJson = await daemon.GetSnapshotAsync(paneId);
                    if (snapshotJson != null)
                    {
                        try
                        {
                            var snapshot = System.Text.Json.JsonSerializer.Deserialize<TerminalBufferSnapshot>(snapshotJson);
                            if (snapshot != null)
                            {
                                session.RestoreBufferSnapshot(snapshot);
                                DaemonLog($"[DaemonSession:{paneId}] Snapshot restored ({snapshotJson.Length} chars)");
                            }
                        }
                        catch (Exception ex)
                        {
                            DaemonLog($"[DaemonSession:{paneId}] Snapshot restore error: {ex.Message}");
                        }
                    }
                    else
                    {
                        DaemonLog($"[DaemonSession:{paneId}] GetSnapshotAsync returned null");
                    }
                }
            }
            catch (Exception ex)
            {
                DaemonLog($"[DaemonSession:{paneId}] Exception — falling back to local: {ex.Message}");
                _daemonPanes.TryRemove(paneId, out _);
                session.DaemonWrite = null;
                session.DaemonResize = null;
                session.Start(command: shell, workingDirectory: effectiveCwd);
            }
        });

        if (restoredState?.BufferSnapshot != null)
            session.RestoreBufferSnapshot(restoredState.BufferSnapshot);

        return session;
    }

    private static string? GetConfiguredShell()
    {
        var shell = SettingsService.Current.DefaultShell;
        return string.IsNullOrWhiteSpace(shell) ? null : shell;
    }

    private TerminalSession StartLocalSession(string paneId, string? workingDirectory, PaneStateSnapshot? restoredState, string? shell)
    {
        var session = new TerminalSession(paneId, scrollbackLines: GetScrollbackLines());
        WireSessionEvents(session, paneId);

        _sessions[paneId] = session;
        session.Start(command: shell, workingDirectory: workingDirectory ?? restoredState?.WorkingDirectory);

        if (restoredState?.BufferSnapshot != null)
            session.RestoreBufferSnapshot(restoredState.BufferSnapshot);

        return session;
    }

    private void WireSessionEvents(TerminalSession session, string paneId)
    {
        session.WorkingDirectoryChanged += dir =>
        {
            if (paneId == FocusedPaneId)
                WorkingDirectoryChanged?.Invoke(dir);
        };

        session.ProcessExited += () => OnPaneProcessExited(paneId, session);

        session.NotificationReceived += (title, subtitle, body) =>
        {
            var source = NotificationSource.Osc9; // Default
            _notificationService.AddNotification(
                _workspaceId, Surface.Id, paneId,
                title, subtitle, body, source);
        };

        session.ShellPromptMarker += (marker, payload) =>
        {
            App.CommandLogService.HandlePromptMarker(
                paneId,
                _workspaceId,
                Surface.Id,
                marker,
                payload,
                session.WorkingDirectory);

            if (marker == 'B')
            {
                var sanitized = App.CommandLogService.SanitizeCommandForStorage(payload);
                if (!string.IsNullOrWhiteSpace(sanitized))
                    AppendToCommandHistory(paneId, sanitized);
            }
        };
    }

    /// <summary>A pane's shell exited: like Windows Terminal, the pane closes.</summary>
    private void OnPaneProcessExited(string paneId, TerminalSession session)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed) return;
            // Ignore events from sessions that were already replaced or closed.
            if (!_sessions.TryGetValue(paneId, out var current) || !ReferenceEquals(current, session))
                return;
            ClosePane(paneId);
        });
    }

    [RelayCommand]
    public void SplitRight()
    {
        SplitFocused(SplitDirection.Vertical);
    }

    [RelayCommand]
    public void SplitDown()
    {
        SplitFocused(SplitDirection.Horizontal);
    }

    /// <summary>
    /// Splits the focused pane. The new pane starts in the focused pane's current directory. Its
    /// shell is started before the layout changes, so a failure leaves the layout untouched.
    /// </summary>
    public void SplitFocused(SplitDirection direction, string? shell = null)
    {
        var paneId = FocusedPaneId;
        if (paneId == null || _disposed) return;

        var node = RootNode.FindNode(paneId);
        if (node == null || !node.IsLeaf) return;

        var cwd = GetFocusedWorkingDirectory();
        var effectiveShell = shell ?? _paneShells.GetValueOrDefault(paneId);
        var newPaneId = Guid.NewGuid().ToString();

        try
        {
            StartSession(newPaneId, cwd, null, effectiveShell);
        }
        catch (Exception ex)
        {
            Log.Error("Split cancelled: the new pane's shell could not start", ex);
            return;
        }

        node.Split(direction, newPaneId);
        FocusedPaneId = newPaneId;

        OnPropertyChanged(nameof(RootNode));
    }

    /// <summary>Splits the focused pane along its longer side (pixel size of the pane).</summary>
    public void SplitFocusedInLongerDirection(double containerWidth, double containerHeight)
    {
        var rect = RootNode.GetLeafRects().FirstOrDefault(r => r.PaneId == FocusedPaneId);
        double width = rect.Width * containerWidth;
        double height = rect.Height * containerHeight;
        SplitFocused(width >= height ? SplitDirection.Vertical : SplitDirection.Horizontal);
    }

    public void OpenPaneWithShell(string shellPath)
    {
        SplitFocused(SplitDirection.Vertical, shellPath);
    }

    [RelayCommand]
    public void ClosePane()
    {
        ClosePane(FocusedPaneId);
    }

    /// <summary>
    /// Closes a pane and its session. Closing the last pane raises <see cref="LastPaneClosed"/>
    /// so the owner can close the tab.
    /// </summary>
    public void ClosePane(string? paneId)
    {
        if (paneId == null || _disposed) return;

        var leaves = RootNode.GetLeaves().ToList();
        if (!leaves.Any(l => l.PaneId == paneId)) return;

        CapturePaneTranscript(paneId, "pane-close");

        // Adjacent pane to focus after removal (computed before the tree changes).
        string? nextPaneId = leaves.Count > 1
            ? (RootNode.GetNextLeaf(paneId) ?? RootNode.GetPreviousLeaf(paneId))?.PaneId
            : null;

        DisposePaneSession(paneId, closeDaemon: true);

        Surface.PaneCustomNames.Remove(paneId);
        Surface.PaneSnapshots.Remove(paneId);
        _paneCommandHistory.Remove(paneId);

        if (leaves.Count <= 1)
        {
            FocusedPaneId = null;
            LastPaneClosed?.Invoke();
            return;
        }

        RootNode.Remove(paneId);

        if (paneId == FocusedPaneId)
            FocusedPaneId = nextPaneId;

        OnPropertyChanged(nameof(RootNode));
    }

    private void DisposePaneSession(string paneId, bool closeDaemon)
    {
        bool wasDaemon = _everDaemonPanes.TryRemove(paneId, out _);
        _daemonPanes.TryRemove(paneId, out _);
        _daemonCwd.TryRemove(paneId, out _);
        _daemonPids.TryRemove(paneId, out _);
        _paneShells.TryRemove(paneId, out _);

        if (_sessions.TryRemove(paneId, out var session))
        {
            session.DaemonWrite = null;
            session.DaemonResize = null;
            session.Dispose();
        }

        if (closeDaemon && wasDaemon)
            _ = SendDaemonCloseAsync(paneId);
    }

    /// <summary>
    /// Asks the daemon to end the sessions of every daemon-backed pane in this tab. Call before
    /// <see cref="Dispose"/> when the user closes the tab (Dispose itself keeps daemon sessions alive).
    /// </summary>
    public void CloseDaemonSessions()
    {
        foreach (var paneId in _everDaemonPanes.Keys.ToList())
        {
            _everDaemonPanes.TryRemove(paneId, out _);
            _ = SendDaemonCloseAsync(paneId);
        }
    }

    private static async Task SendDaemonCloseAsync(string paneId)
    {
        try
        {
            await App.DaemonClient.CloseSessionAsync(paneId);
        }
        catch (Exception ex)
        {
            DaemonLog($"[CloseSession:{paneId}] failed: {ex.Message}");
        }
    }

    public void FocusPane(string paneId)
    {
        FocusedPaneId = paneId;
        Surface.FocusedPaneId = paneId;
    }

    [RelayCommand]
    public void FocusNextPane()
    {
        if (FocusedPaneId == null) return;
        var next = RootNode.GetNextLeaf(FocusedPaneId);
        if (next?.PaneId != null)
            FocusPane(next.PaneId);
    }

    [RelayCommand]
    public void FocusPreviousPane()
    {
        if (FocusedPaneId == null) return;
        var prev = RootNode.GetPreviousLeaf(FocusedPaneId);
        if (prev?.PaneId != null)
            FocusPane(prev.PaneId);
    }

    /// <summary>Moves focus to the nearest pane in the given direction. Returns false if there is none.</summary>
    public bool FocusInDirection(PaneDirection direction)
    {
        if (FocusedPaneId == null) return false;
        var neighbor = RootNode.FindNeighborPaneId(FocusedPaneId, direction);
        if (neighbor == null) return false;
        FocusPane(neighbor);
        return true;
    }

    /// <summary>Grows the focused pane toward <paramref name="direction"/>.</summary>
    public bool ResizeFocusedPane(PaneDirection direction, double delta = 0.05)
    {
        if (FocusedPaneId == null) return false;
        if (!RootNode.ResizePane(FocusedPaneId, direction, delta)) return false;
        OnPropertyChanged(nameof(RootNode));
        return true;
    }

    [RelayCommand]
    public void ToggleZoom() => IsZoomed = !IsZoomed;

    public void EqualizePanes()
    {
        RootNode.Equalize();
        OnPropertyChanged(nameof(RootNode));
    }

    partial void OnFocusedPaneIdChanged(string? value)
    {
        Surface.FocusedPaneId = value;
    }

    partial void OnNameChanged(string value)
    {
        Surface.Name = value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unwire daemon events (only when the daemon is enabled)
        if (App.IsDaemonEnabled)
        {
            var daemon = App.DaemonClient;
            daemon.RawOutputReceived -= OnDaemonRawOutput;
            daemon.CwdChanged -= OnDaemonCwdChanged;
            daemon.TitleChanged -= OnDaemonTitleChanged;
            daemon.SessionExited -= OnDaemonSessionExited;
            daemon.BellReceived -= OnDaemonBellReceived;
            daemon.Disconnected -= OnDaemonDisconnected;
        }

        // Local sessions are stopped; daemon sessions are deliberately left running.
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        _daemonPanes.Clear();
        _paneShells.Clear();
    }
}
