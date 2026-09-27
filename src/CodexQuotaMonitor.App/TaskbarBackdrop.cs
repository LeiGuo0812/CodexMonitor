using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CodexQuotaMonitor.App;

// The shell already supplies the material behind a docked widget. Do not tint or blur it again.
internal sealed class TaskbarBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
        _compositor = new Windows.UI.Composition.Compositor();
        _brush = _compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        target.SystemBackdrop = _brush;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // The widget updates its opaque fallback and text from Windows shell settings.
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        _compositor?.Dispose();
        _compositor = null;
        base.OnTargetDisconnected(target);
    }
}
