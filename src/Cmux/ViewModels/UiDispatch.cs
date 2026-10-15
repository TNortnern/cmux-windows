namespace Cmux.ViewModels;

/// <summary>Runs work on the WPF dispatcher when called from another thread.</summary>
internal static class UiDispatch
{
    public static void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
