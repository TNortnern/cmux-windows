using Cmux.Core.IPC;
using Cmux.Core.Services;
using Cmux.Daemon;

// Single-instance check: one daemon per user session (mutex name includes the user SID)
var mutexName = $"Local\\CmuxDaemon-{PipeSecurityFactory.CurrentUserSid}";
using var mutex = new Mutex(true, mutexName, out bool createdNew);
if (!createdNew)
{
    Log.Info("cmux-daemon is already running for this user. Exiting.");
    return 1;
}

Log.Info($"[cmux-daemon] Starting (PID {Environment.ProcessId})...");

var sessionManager = new DaemonSessionManager();
var pipeServer = new DaemonPipeServer(sessionManager);

using var cts = new CancellationTokenSource();

// Idle timeout: exit after 24h with no connected clients and no active sessions
var idleTimeout = TimeSpan.FromHours(24);
DateTime lastActivity = DateTime.UtcNow;

pipeServer.ClientConnected += () =>
{
    lastActivity = DateTime.UtcNow;
    Log.Info($"[cmux-daemon] Client connected (total: {pipeServer.ConnectedClients})");
};

pipeServer.ClientDisconnected += () =>
{
    lastActivity = DateTime.UtcNow;
    Log.Info($"[cmux-daemon] Client disconnected (total: {pipeServer.ConnectedClients}, sessions: {sessionManager.ActiveSessionCount})");
};

sessionManager.SessionCreated += paneId =>
{
    lastActivity = DateTime.UtcNow;
    Log.Info($"[cmux-daemon] Session created: {paneId} (total: {sessionManager.ActiveSessionCount})");
};

sessionManager.SessionExited += (paneId, exitCode) =>
{
    lastActivity = DateTime.UtcNow;
    Log.Info($"[cmux-daemon] Session exited: {paneId} code={exitCode} (total: {sessionManager.ActiveSessionCount})");
};

Log.Info("[cmux-daemon] Starting pipe server...");
// Pipe server runs on a dedicated background thread (synchronous I/O)
var serverThread = new Thread(() =>
{
    try { pipeServer.Run(cts.Token); }
    catch (OperationCanceledException) { } // Shutdown requested: the thread simply ends
})
{
    IsBackground = true,
    Name = "PipeServer-Accept",
};
serverThread.Start();
Log.Info("[cmux-daemon] Pipe server started, waiting for connections...");

// Idle monitor: wakes once a minute and stops the daemon once it has been idle for 24h
try
{
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), cts.Token);

        if (pipeServer.ConnectedClients == 0
            && sessionManager.ActiveSessionCount == 0
            && DateTime.UtcNow - lastActivity > idleTimeout)
        {
            Log.Info("[cmux-daemon] Idle timeout reached. Shutting down.");
            cts.Cancel();
        }
    }
}
catch (OperationCanceledException) { } // Cancelled by the idle monitor's own Cancel() path or shutdown

Log.Info($"[cmux-daemon] Shutting down (sessions: {sessionManager.ActiveSessionCount})...");
sessionManager.Dispose();
Log.Info("[cmux-daemon] Stopped.");
return 0;
