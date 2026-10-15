using System.IO;
using System.Windows;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Services;
using Cmux.Services;

namespace Cmux;

public partial class App : Application
{
    private NamedPipeServer? _pipeServer;

    public static NotificationService NotificationService { get; } = new();
    public static NamedPipeServer? PipeServer { get; private set; }
    public static SnippetService SnippetService { get; } = new();
    public static CommandLogService CommandLogService { get; } = new();
    // Agent services are created on first use so a disabled agent costs nothing.
    private static readonly Lazy<AgentConversationStoreService> _agentConversationStore = new(() => new AgentConversationStoreService());
    private static readonly Lazy<AgentRuntimeService> _agentRuntime = new(() => new AgentRuntimeService());
    public static AgentConversationStoreService AgentConversationStore => _agentConversationStore.Value;
    public static AgentRuntimeService AgentRuntime => _agentRuntime.Value;
    public static bool IsAgentRuntimeCreated => _agentRuntime.IsValueCreated;
    // Created on first use. With UseSessionDaemon off, nothing here ever constructs or touches it.
    private static readonly Lazy<DaemonClient> _daemonClient = new(() => new DaemonClient());
    public static DaemonClient DaemonClient => _daemonClient.Value;
    public static bool IsDaemonClientCreated => _daemonClient.IsValueCreated;
    public static Task<bool> DaemonConnectTask { get; private set; } = Task.FromResult(false);
    /// <summary>Whether this run uses the session daemon (read once at startup; changing it needs a restart).</summary>
    public static bool IsDaemonEnabled { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Must be set before the first window is created.
        if (SettingsService.Current.SoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Global exception handlers: log the exception, then show it
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error("[CRASH] DispatcherUnhandledException", args.Exception);
            System.Windows.MessageBox.Show($"Unexpected error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Error("[CRASH] UnhandledException", ex);
            System.Windows.MessageBox.Show($"Fatal error: {ex?.Message}\n\n{ex?.StackTrace}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Start the named pipe server for CLI communication
        _pipeServer = new NamedPipeServer();
        PipeServer = _pipeServer;
        _pipeServer.Start();

        IsDaemonEnabled = SettingsService.Current.UseSessionDaemon;
        if (IsDaemonEnabled)
        {
            // Opt-in daemon: try an existing daemon first, then start one if needed.
            // Sessions wait for this task before deciding local vs daemon mode.
            DaemonConnectTask = Task.Run(ConnectDaemon);
        }
        else
        {
            DaemonConnectTask = Task.FromResult(false);
        }

        // Wire up Windows toast notifications
        NotificationService.NotificationAdded += notification =>
        {
            // Only show toast when the app window is not focused
            var mainWindow = Current.MainWindow;
            if (mainWindow != null && !mainWindow.IsActive)
            {
                var workspaceName = "Terminal"; // Will be enriched by MainViewModel
                Services.ToastNotificationHelper.ShowToast(notification, workspaceName);
            }
        };
    }

    private static bool ConnectDaemon()
    {
        var client = DaemonClient;
        DaemonLog("[App] Phase 1: Quick daemon check (300ms)...");
        if (client.TryConnect(300))
        {
            DaemonLog("[App] Phase 1: Daemon connected!");
            client.RaiseConnected();
            return true;
        }
        DaemonLog("[App] Phase 1: Daemon not available, starting daemon...");

        var connected = client.StartDaemonAndConnect();
        DaemonLog(connected
            ? "[App] Phase 2: Daemon started and connected"
            : "[App] Phase 2: Daemon failed to start");
        if (connected) client.RaiseConnected();
        return connected;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        if (_daemonClient.IsValueCreated) _daemonClient.Value.Dispose();
        if (IsAgentRuntimeCreated) AgentRuntime.Dispose();
        base.OnExit(e);
    }

    internal static void DaemonLog(string message) => Log.Debug(message);
}
