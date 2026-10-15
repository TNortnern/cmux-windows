using System.Windows;

namespace Cmux.Views;

public enum ToolApprovalDecision
{
    Deny,
    RunOnce,
    AlwaysAllowSession,
}

/// <summary>What the user is asked to approve. Details is shown verbatim, read-only.</summary>
public sealed record ToolApprovalRequest(string ToolName, string Target, string Details);

public partial class ToolApprovalWindow : Window
{
    // One prompt at a time: parallel tool calls queue instead of stacking dialogs.
    private static readonly SemaphoreSlim PromptGate = new(1, 1);

    private ToolApprovalWindow(ToolApprovalRequest request)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);

        ToolNameText.Text = request.ToolName;
        TargetText.Text = request.Target;
        DetailsBox.Text = request.Details;
        AlwaysButton.Content = $"Always allow {request.ToolName} this session";

        Loaded += (_, _) => DenyButton.Focus();
    }

    public ToolApprovalDecision Decision { get; private set; } = ToolApprovalDecision.Deny;

    /// <summary>
    /// Shows the approval prompt on the UI thread and returns the user's decision.
    /// Closing the window, Esc, or having no UI counts as Deny. Throws OperationCanceledException if
    /// the run is canceled while waiting for the prompt.
    /// </summary>
    public static async Task<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var app = Application.Current;
        if (app == null)
            return ToolApprovalDecision.Deny;

        await PromptGate.WaitAsync(ct);
        try
        {
            return await app.Dispatcher.InvokeAsync(() => ShowModal(app, request, ct)).Task;
        }
        finally
        {
            PromptGate.Release();
        }
    }

    private static ToolApprovalDecision ShowModal(Application app, ToolApprovalRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ToolApprovalDecision.Deny;

        var window = new ToolApprovalWindow(request);
        var owner = app.MainWindow;
        if (owner != null && owner.IsLoaded)
            window.Owner = owner;

        // If the run is canceled while the prompt is open, close it so the run can unwind.
        using var registration = ct.Register(() => app.Dispatcher.BeginInvoke(new Action(window.Close)));
        window.ShowDialog();
        return window.Decision;
    }

    private void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        Decision = ToolApprovalDecision.RunOnce;
        Close();
    }

    private void AlwaysAllow_Click(object sender, RoutedEventArgs e)
    {
        Decision = ToolApprovalDecision.AlwaysAllowSession;
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        Decision = ToolApprovalDecision.Deny;
        Close();
    }
}
