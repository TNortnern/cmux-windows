using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Linq;
using Cmux.ViewModels;

namespace Cmux.Controls;

public partial class SurfaceTabBar : UserControl
{
    private SurfaceViewModel? _renamingSurface;
    private Point _tabDragStart;

    public event Action<string>? SearchTextChanged;
    public event Action? NextMatchRequested;
    public event Action? PreviousMatchRequested;

    public SurfaceTabBar()
    {
        InitializeComponent();
        DataContextChanged += OnWorkspaceDataContextChanged;
    }

    private void OnWorkspaceDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is WorkspaceViewModel oldWorkspace)
            oldWorkspace.SurfaceRenameRequested -= BeginRename;
        if (e.NewValue is WorkspaceViewModel newWorkspace)
            newWorkspace.SurfaceRenameRequested += BeginRename;
    }

    public void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    public void UpdateMatchCount(int current, int total)
    {
        MatchCount.Text = total > 0 ? $"{current + 1}/{total}" : "";
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        => SearchTextChanged?.Invoke(SearchInput.Text);

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                PreviousMatchRequested?.Invoke();
            else
                NextMatchRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchInput.Text = "";
            var window = Window.GetWindow(this);
            window?.Focus();
            e.Handled = true;
        }
    }

    private void PrevMatch_Click(object sender, RoutedEventArgs e) => PreviousMatchRequested?.Invoke();
    private void NextMatch_Click(object sender, RoutedEventArgs e) => NextMatchRequested?.Invoke();

    private SurfaceViewModel? GetSurfaceFromMenu(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu ctx)
            return ctx.Tag as SurfaceViewModel;
        return null;
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SurfaceViewModel surface)
        {
            if (e.ClickCount == 2)
            {
                BeginRename(surface);
                e.Handled = true;
                return;
            }
            if (DataContext is WorkspaceViewModel workspace)
                workspace.SelectedSurface = surface;
        }
    }

    // --- Tab drag-and-drop reordering (mirrors the workspace sidebar) ---

    private void Tab_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(null);
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _tabDragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is FrameworkElement fe && fe.DataContext is SurfaceViewModel surface)
            DragDrop.DoDragDrop(fe, surface, DragDropEffects.Move);
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not SurfaceViewModel target) return;
        if (DataContext is not WorkspaceViewModel workspace) return;

        var source = e.Data.GetData(typeof(SurfaceViewModel)) as SurfaceViewModel;
        if (source == null || source == target) return;

        int sourceIndex = workspace.Surfaces.IndexOf(source);
        int targetIndex = workspace.Surfaces.IndexOf(target);
        workspace.MoveSurface(sourceIndex, targetIndex);
    }

    // --- Rename ---

    private void BeginRename(SurfaceViewModel surface)
    {
        _renamingSurface = surface;
        TabRenameBox.Text = surface.Name;
        TabRenameBox.Visibility = Visibility.Visible;
        TabRenameBox.SelectAll();
        TabRenameBox.Focus();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SurfaceViewModel surface)
        {
            if (DataContext is WorkspaceViewModel workspace)
                workspace.CloseSurface(surface);
        }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel workspace)
            workspace.CreateNewSurface();
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface == null) return;
        BeginRename(surface);
    }

    private void FinishTabRename(bool save)
    {
        if (_renamingSurface != null && save && !string.IsNullOrWhiteSpace(TabRenameBox.Text))
            _renamingSurface.Name = TabRenameBox.Text.Trim();
        _renamingSurface = null;
        TabRenameBox.Visibility = Visibility.Collapsed;
    }

    private void TabRenameBox_LostFocus(object sender, RoutedEventArgs e) => FinishTabRename(true);

    private void TabRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FinishTabRename(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { FinishTabRename(false); e.Handled = true; }
    }

    private void DuplicateTab_Click(object sender, RoutedEventArgs e)
    {
        var original = GetSurfaceFromMenu(sender);
        if (original != null && DataContext is WorkspaceViewModel ws)
            ws.DuplicateSurface(original);
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            ws.SelectedSurface = surface;
            surface.SplitRight();
        }
    }

    private void SplitDown_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            ws.SelectedSurface = surface;
            surface.SplitDown();
        }
    }

    private void CloseThisTab_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
            ws.CloseSurface(surface);
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            var others = ws.Surfaces.Where(s => s != surface).ToList();
            foreach (var other in others)
                ws.CloseSurface(other);
        }
    }
}
