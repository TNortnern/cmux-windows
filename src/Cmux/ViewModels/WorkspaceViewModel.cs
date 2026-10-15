using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Models;
using Cmux.Core.Services;

namespace Cmux.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan InfoRefreshInterval = TimeSpan.FromSeconds(5);

    public Workspace Workspace { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _iconGlyph;

    [ObservableProperty]
    private string _accentColor;

    [ObservableProperty]
    private ObservableCollection<SurfaceViewModel> _surfaces = [];

    [ObservableProperty]
    private SurfaceViewModel? _selectedSurface;

    [ObservableProperty]
    private string? _gitBranch;

    [ObservableProperty]
    private string? _workingDirectory;

    [ObservableProperty]
    private string? _latestNotificationText;

    [ObservableProperty]
    private int _unreadNotificationCount;

    [ObservableProperty]
    private string _portsDisplay = "";

    [ObservableProperty]
    private bool _hasNotification;

    [ObservableProperty]
    private AgentType _detectedAgent;

    public string AgentLabel => AgentDetector.GetLabel(DetectedAgent);
    public string AgentIcon => AgentDetector.GetIcon(DetectedAgent);
    public string IconFontFamily => IsPrivateUseGlyph(IconGlyph) ? "Segoe MDL2 Assets" : "Segoe UI Emoji";

    private readonly NotificationService _notificationService;
    private System.Threading.Timer? _infoRefreshTimer;
    private volatile bool _isActive;
    private int _refreshing;

    /// <summary>Raised when the user asks to rename a tab (the tab bar shows its inline editor).</summary>
    public event Action<SurfaceViewModel>? SurfaceRenameRequested;

    public WorkspaceViewModel(Workspace workspace, NotificationService notificationService, string? initialWorkingDirectory = null)
    {
        Workspace = workspace;
        _name = workspace.Name;
        _iconGlyph = workspace.IconGlyph;
        _accentColor = workspace.AccentColor;
        _notificationService = notificationService;

        // Surfaces start lazily: sessions only start once the workspace is active and the tab is selected.
        for (int i = 0; i < workspace.Surfaces.Count; i++)
        {
            var surface = workspace.Surfaces[i];
            Surfaces.Add(CreateSurfaceViewModel(surface, i == 0 ? initialWorkingDirectory : null));
        }

        if (workspace.SelectedSurface != null)
        {
            SelectedSurface = Surfaces.FirstOrDefault(s => s.Surface.Id == workspace.SelectedSurface.Id);
        }
        else if (Surfaces.Count > 0)
        {
            SelectedSurface = Surfaces[0];
        }

        _infoRefreshTimer = new System.Threading.Timer(_ => RefreshInfo(), null, Timeout.InfiniteTimeSpan, InfoRefreshInterval);
    }

    /// <summary>
    /// Marks this workspace as the selected one. Only an active workspace starts terminal sessions
    /// and runs the (expensive) git and agent detection.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        if (!active)
        {
            _infoRefreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        SelectedSurface?.EnsureSessionsStarted();
        _infoRefreshTimer?.Change(TimeSpan.Zero, InfoRefreshInterval);
    }

    private SurfaceViewModel CreateSurfaceViewModel(Surface surface, string? initialWorkingDirectory)
    {
        var vm = new SurfaceViewModel(surface, Workspace.Id, _notificationService, initialWorkingDirectory);
        vm.WorkingDirectoryChanged += OnSurfaceWorkingDirectoryChanged;
        vm.LastPaneClosed += () => OnSurfaceLastPaneClosed(vm);
        return vm;
    }

    /// <summary>
    /// Adds a tab in the given directory and selects it. Pass null to start in the default directory.
    /// </summary>
    public SurfaceViewModel AddSurface(string? workingDirectory = null, string? name = null)
    {
        var surface = new Surface { Name = name ?? $"Terminal {Surfaces.Count + 1}" };
        Workspace.Surfaces.Add(surface);

        var surfaceVm = CreateSurfaceViewModel(surface, workingDirectory);
        Surfaces.Add(surfaceVm);
        SelectedSurface = surfaceVm;
        return surfaceVm;
    }

    /// <summary>New tab in the focused pane's current directory.</summary>
    [RelayCommand]
    public void CreateNewSurface()
    {
        AddSurface(SelectedSurface?.GetFocusedWorkingDirectory());
    }

    /// <summary>Opens a tab in the same directory as the given tab's focused pane.</summary>
    public void DuplicateSurface(SurfaceViewModel original)
    {
        AddSurface(original.GetFocusedWorkingDirectory(), original.Name + " (copy)");
    }

    [RelayCommand]
    public void CloseSurface(SurfaceViewModel? surface)
    {
        if (surface == null || !Surfaces.Contains(surface)) return;

        // A workspace always keeps one tab: open a fresh one in the same directory first.
        if (Surfaces.Count <= 1)
            AddSurface(surface.GetFocusedWorkingDirectory());

        int index = Surfaces.IndexOf(surface);
        surface.CaptureAllPaneTranscripts("surface-close");
        surface.CloseDaemonSessions();
        surface.Dispose();
        Surfaces.Remove(surface);
        Workspace.Surfaces.Remove(surface.Surface);

        if (SelectedSurface == surface)
        {
            SelectedSurface = Surfaces[Math.Min(index, Surfaces.Count - 1)];
        }
    }

    private void OnSurfaceLastPaneClosed(SurfaceViewModel surface)
    {
        if (Surfaces.Contains(surface))
            CloseSurface(surface);
    }

    public void SelectSurface(int index)
    {
        if (index >= 0 && index < Surfaces.Count)
            SelectedSurface = Surfaces[index];
    }

    /// <summary>Moves a tab to a new position (keeps the workspace model's order in sync).</summary>
    public void MoveSurface(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex) return;
        if (oldIndex < 0 || newIndex < 0 || oldIndex >= Surfaces.Count || newIndex >= Surfaces.Count)
            return;
        if (oldIndex >= Workspace.Surfaces.Count || newIndex >= Workspace.Surfaces.Count)
            return;

        Surfaces.Move(oldIndex, newIndex);
        Workspace.Surfaces.Move(oldIndex, newIndex);
    }

    [RelayCommand]
    public void NextSurface()
    {
        if (Surfaces.Count == 0) return;
        int index = SelectedSurface != null ? Surfaces.IndexOf(SelectedSurface) : -1;
        SelectedSurface = Surfaces[(index + 1) % Surfaces.Count];
    }

    [RelayCommand]
    public void PreviousSurface()
    {
        if (Surfaces.Count == 0) return;
        int index = SelectedSurface != null ? Surfaces.IndexOf(SelectedSurface) : 0;
        SelectedSurface = Surfaces[(index - 1 + Surfaces.Count) % Surfaces.Count];
    }

    /// <summary>Starts inline rename of the selected tab.</summary>
    [RelayCommand]
    public void Rename()
    {
        if (SelectedSurface != null)
            SurfaceRenameRequested?.Invoke(SelectedSurface);
    }

    /// <summary>Closes the daemon-side sessions of every tab (used when the user closes the workspace).</summary>
    public void CloseDaemonSessions()
    {
        foreach (var surface in Surfaces)
            surface.CloseDaemonSessions();
    }

    partial void OnSelectedSurfaceChanged(SurfaceViewModel? value)
    {
        Workspace.SelectedSurface = value?.Surface;
        if (_isActive)
            value?.EnsureSessionsStarted();
    }

    private void OnSurfaceWorkingDirectoryChanged(string directory)
    {
        WorkingDirectory = directory;
        Workspace.WorkingDirectory = directory;
    }

    /// <summary>
    /// Periodic git and agent detection. Only runs for the selected workspace; others refresh when
    /// they are selected. A reentrancy guard stops overlapping runs.
    /// </summary>
    private void RefreshInfo()
    {
        if (!_isActive) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        try
        {
            var dir = WorkingDirectory ?? Workspace.WorkingDirectory;
            string? branch = string.IsNullOrEmpty(dir) ? null : GitService.GetBranch(dir);
            bool hasBranch = !string.IsNullOrEmpty(dir);

            AgentType? agent = null;
            var pid = SelectedSurface?.ShellPid;
            if (pid is int shellPid and > 0)
                agent = AgentDetector.DetectFromProcessId(shellPid);

            UiDispatch.Post(() =>
            {
                if (hasBranch && branch != GitBranch)
                {
                    GitBranch = branch;
                    Workspace.GitBranch = branch;
                }

                if (agent is { } detected && detected != DetectedAgent)
                {
                    DetectedAgent = detected;
                    OnPropertyChanged(nameof(AgentLabel));
                    OnPropertyChanged(nameof(AgentIcon));
                }
            });
        }
        catch (Exception ex)
        {
            // Non-critical: detection can fail while a shell is starting or exiting.
            Log.Debug($"Workspace info refresh skipped: {ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    partial void OnUnreadNotificationCountChanged(int value)
    {
        HasNotification = value > 0;
    }

    partial void OnNameChanged(string value)
    {
        Workspace.Name = value;
    }

    partial void OnIconGlyphChanged(string value)
    {
        Workspace.IconGlyph = value;
        OnPropertyChanged(nameof(IconFontFamily));
    }

    partial void OnAccentColorChanged(string value)
    {
        Workspace.AccentColor = value;
    }

    private static bool IsPrivateUseGlyph(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int codePoint = char.ConvertToUtf32(value, 0);
        return codePoint is >= 0xE000 and <= 0xF8FF;
    }

    public int CaptureAllSurfaceTranscripts(string reason)
    {
        int captured = 0;
        foreach (var surface in Surfaces)
            captured += surface.CaptureAllPaneTranscripts(reason);

        return captured;
    }

    public void Dispose()
    {
        _infoRefreshTimer?.Dispose();
        _infoRefreshTimer = null;
        foreach (var surface in Surfaces)
            surface.Dispose();
    }
}
