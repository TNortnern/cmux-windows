using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cmux.Core.Models;
using Cmux.Core.Config;
using Cmux.Core.Terminal;
using Cmux.ViewModels;
using Cmux.Views;

namespace Cmux.Controls;

/// <summary>
/// Recursively renders a SplitNode tree as nested Grid panels with
/// GridSplitters for resizable dividers. Leaf nodes contain TerminalControl instances.
/// Terminal controls are kept per pane while shown; controls for panes that are no longer
/// shown (closed, zoomed away, other surface, unloaded) are detached so they stop working.
/// </summary>
public class SplitPaneContainer : ContentControl
{
    private SurfaceViewModel? _surface;
    private readonly Dictionary<string, TerminalControl> _terminalCache = [];
    private readonly HashSet<string> _shownPaneIds = [];
    private readonly Dictionary<string, (TerminalSession Session, Action<string> Handler)> _titleSubscriptions = [];
    private bool _needsRebuildOnLoad;

    public event Action? SearchRequested;

    private static SolidColorBrush GetThemeBrush(string key) =>
        Application.Current.Resources[key] as SolidColorBrush ?? Brushes.Transparent;

    private static Color GetThemeColor(string key) =>
        Application.Current.Resources[key] is Color c ? c : Colors.Transparent;

    public SplitPaneContainer()
    {
        Background = Brushes.Transparent;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SurfaceViewModel oldSurface)
        {
            oldSurface.PropertyChanged -= OnSurfacePropertyChanged;
        }

        // Switching surfaces/workspaces: release the previous surface's terminals so they are never reused
        DetachAll();

        _surface = e.NewValue as SurfaceViewModel;

        if (_surface != null)
        {
            _surface.PropertyChanged += OnSurfacePropertyChanged;
            Rebuild();
        }
        else
        {
            Content = null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_needsRebuildOnLoad)
            Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Removed from the window: stop all terminal work; rebuild when shown again.
        DetachAll();
        _needsRebuildOnLoad = _surface != null;
    }

    private void OnSurfacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurfaceViewModel.RootNode)
            or nameof(SurfaceViewModel.IsZoomed))
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
        else if (e.PropertyName is nameof(SurfaceViewModel.FocusedPaneId))
        {
            Dispatcher.BeginInvoke(UpdateFocusState);
        }
    }

    /// <summary>
    /// Updates only focus-related visual state on cached terminals without
    /// rebuilding the entire UI tree.
    /// </summary>
    private void UpdateFocusState()
    {
        if (_surface == null) return;

        // In zoom mode, focus change may require rebuild if the zoomed pane changed
        if (_surface.IsZoomed)
        {
            Rebuild();
            return;
        }

        foreach (var (paneId, terminal) in _terminalCache)
        {
            terminal.IsPaneFocused = paneId == _surface.FocusedPaneId;
        }
    }

    private void Rebuild()
    {
        if (_surface == null) return;

        _needsRebuildOnLoad = false;
        UnsubscribeTitles();
        _shownPaneIds.Clear();

        // Zoom mode: show only the focused pane full-size
        if (_surface.IsZoomed && _surface.FocusedPaneId != null)
        {
            var focusedNode = _surface.RootNode.FindNode(_surface.FocusedPaneId);
            if (focusedNode != null)
            {
                Content = BuildLeaf(focusedNode);
                PruneTerminalCache();
                return;
            }
        }

        Content = BuildNode(_surface.RootNode);
        PruneTerminalCache();
    }

    /// <summary>Detaches and drops cached terminals for panes that the rebuilt tree no longer shows.</summary>
    private void PruneTerminalCache()
    {
        foreach (var paneId in _terminalCache.Keys.Where(id => !_shownPaneIds.Contains(id)).ToList())
        {
            _terminalCache[paneId].Detach();
            _terminalCache.Remove(paneId);
        }
    }

    /// <summary>Detaches every cached terminal and forgets all pane state.</summary>
    private void DetachAll()
    {
        UnsubscribeTitles();

        foreach (var terminal in _terminalCache.Values)
            terminal.Detach();

        _terminalCache.Clear();
        _shownPaneIds.Clear();
    }

    private void UnsubscribeTitles()
    {
        foreach (var (session, handler) in _titleSubscriptions.Values)
            session.TitleChanged -= handler;

        _titleSubscriptions.Clear();
    }

    private void SubscribeTitle(string paneId, TerminalSession session, TextBlock titleText)
    {
        Action<string> handler = sessionTitle =>
        {
            // The session raises TitleChanged on its reader thread
            Dispatcher.BeginInvoke(() =>
            {
                titleText.Text = _surface?.GetPaneTitle(paneId, sessionTitle) ?? "Terminal";
            });
        };

        session.TitleChanged += handler;
        _titleSubscriptions[paneId] = (session, handler);
    }

    private UIElement BuildNode(SplitNode node)
    {
        if (node.IsLeaf)
        {
            return BuildLeaf(node);
        }

        return BuildSplit(node);
    }

    private UIElement BuildLeaf(SplitNode node)
    {
        if (node.PaneId == null)
            return new Border { Background = Brushes.Transparent };

        var paneId = node.PaneId; // Capture for closures
        _shownPaneIds.Add(paneId);

        // Reuse cached terminal if available (preserves session and scroll position)
        if (!_terminalCache.TryGetValue(paneId, out var terminal))
        {
            terminal = new TerminalControl();
            ApplyCurrentSettings(terminal);
            _terminalCache[paneId] = terminal;
        }
        else
        {
            // Detach from old parent before reusing
            // Terminal could be inside DockPanel (with header) or Border
            var oldParent = System.Windows.Media.VisualTreeHelper.GetParent(terminal) as FrameworkElement;

            if (oldParent is DockPanel dockPanel)
            {
                dockPanel.Children.Remove(terminal);
            }
            else if (oldParent is Border border)
            {
                border.Child = null;
            }

            // Clear old event handlers to prevent memory leaks and wrong callbacks
            terminal.ClearEventHandlers();
        }

        // Wire up event handlers with closures capturing the current pane ID
        terminal.FocusRequested += () => _surface?.FocusPane(paneId);
        terminal.CommandInterceptRequested += command => _surface?.TryHandlePaneCommand(paneId, command) == true;
        terminal.CommandSubmitted += command => _surface?.RegisterCommandSubmission(paneId, command);
        terminal.ClearRequested += () => _surface?.CapturePaneTranscript(paneId, "clear-terminal");
        terminal.SplitRequested += dir =>
        {
            _surface?.FocusPane(paneId);
            _surface?.SplitFocused(dir);
        };
        terminal.ZoomRequested += () => _surface?.ToggleZoom();
        terminal.ClosePaneRequested += () => _surface?.ClosePane(paneId);
        terminal.SearchRequested += () => SearchRequested?.Invoke();
        terminal.IsPaneFocused = paneId == _surface?.FocusedPaneId;
        terminal.IsSurfaceZoomed = _surface?.IsZoomed == true;

        // Attach the terminal session
        var session = _surface?.GetSession(paneId);
        if (session != null)
            terminal.AttachSession(session);
        else
            terminal.Detach();

        // Get pane title (custom name takes precedence over shell title)
        var title = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";

        // Create panel with header
        var panel = new DockPanel { LastChildFill = true };

        // Header bar with title and close button
        var header = new Border
        {
            Background = GetThemeBrush("SidebarItemHoverBrush"),
            Height = 22,
            Padding = new Thickness(8, 2, 8, 2),
        };

        var headerMenu = new ContextMenu();
        var renamePane = new MenuItem { Header = "Rename Pane" };
        renamePane.Click += (_, _) =>
        {
            var currentName = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";
            var prompt = new TextPromptWindow(
                title: "Rename Pane",
                message: "Set a custom name for this pane.",
                defaultValue: currentName)
            {
                Owner = Window.GetWindow(this),
            };

            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.ResponseText))
                _surface?.SetPaneCustomName(paneId, prompt.ResponseText);
        };
        headerMenu.Items.Add(renamePane);

        var resetPaneName = new MenuItem { Header = "Reset Pane Name" };
        resetPaneName.Click += (_, _) => _surface?.SetPaneCustomName(paneId, string.Empty);
        headerMenu.Items.Add(resetPaneName);

        header.ContextMenu = headerMenu;

        DockPanel.SetDock(header, Dock.Top);

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Focus indicator
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Title
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Close button

        // Focus indicator (shows which pane is focused)
        var focusIndicator = new Border
        {
            Width = 3,
            Height = 12,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = terminal.IsPaneFocused
                ? GetThemeBrush("AccentBrush")
                : GetThemeBrush("DividerBrush"),
        };
        Grid.SetColumn(focusIndicator, 0);

        // Title text (updated live from the session's TitleChanged)
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = GetThemeBrush("ForegroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleText, 1);

        if (session != null)
            SubscribeTitle(paneId, session, titleText);

        // Close button
        var closeButton = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            Foreground = GetThemeBrush("ForegroundDimBrush"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Close pane",
        };
        closeButton.Click += (s, e) => _surface?.ClosePane(paneId);
        Grid.SetColumn(closeButton, 2);

        headerGrid.Children.Add(focusIndicator);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(closeButton);
        header.Child = headerGrid;

        panel.Children.Add(header);
        panel.Children.Add(terminal);

        var focusedAccent = GetThemeColor("AccentColor");
        return new Border
        {
            Child = panel,
            BorderBrush = terminal.IsPaneFocused
                ? new SolidColorBrush(Color.FromArgb(153, focusedAccent.R, focusedAccent.G, focusedAccent.B))
                : GetThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>
    /// New terminal controls start from the Ghostty config; apply the app's current theme and font
    /// so a control recreated after zoom or a surface switch looks like the others.
    /// </summary>
    private static void ApplyCurrentSettings(TerminalControl terminal)
    {
        var settings = SettingsService.Current;
        terminal.UpdateSettings(TerminalThemes.GetEffective(settings), settings.FontFamily, settings.FontSize);
    }

    private UIElement BuildSplit(SplitNode node)
    {
        var grid = new Grid();

        if (node.Direction == SplitDirection.Vertical)
        {
            // Left | Right
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(4, GridUnitType.Pixel),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetColumn(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeWE,
            };
            splitter.DragCompleted += (_, _) =>
                node.SplitRatio = RatioFromSizes(grid.ColumnDefinitions[0].ActualWidth, grid.ColumnDefinitions[2].ActualWidth, node.SplitRatio);
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetColumn(second, 2);
                grid.Children.Add(second);
            }
        }
        else
        {
            // Top / Bottom
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(4, GridUnitType.Pixel),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetRow(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeNS,
            };
            splitter.DragCompleted += (_, _) =>
                node.SplitRatio = RatioFromSizes(grid.RowDefinitions[0].ActualHeight, grid.RowDefinitions[2].ActualHeight, node.SplitRatio);
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetRow(second, 2);
                grid.Children.Add(second);
            }
        }

        return grid;
    }

    /// <summary>
    /// Converts the measured sizes of the two panes after a drag into a split ratio, so the
    /// layout survives the next rebuild. Bounded the same way as <see cref="SplitNode.SplitRatio"/> changes.
    /// </summary>
    private static double RatioFromSizes(double first, double second, double fallback)
    {
        double total = first + second;
        return total > 0 ? Math.Clamp(first / total, 0.1, 0.9) : fallback;
    }

    /// <summary>
    /// Updates settings for all cached terminal controls.
    /// </summary>
    public void UpdateAllTerminals(TerminalTheme theme, string fontFamily, int fontSize)
    {
        foreach (var terminal in _terminalCache.Values)
        {
            terminal.UpdateSettings(theme, fontFamily, fontSize);
        }
    }
}
