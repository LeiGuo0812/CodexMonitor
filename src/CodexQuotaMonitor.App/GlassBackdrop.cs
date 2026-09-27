using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexQuotaMonitor.App;

// One controller per window. The XAML configuration tracks activation, theme and accessibility.
internal sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private Color _tint;
    private float _opacity = 0.5f;
    private ICompositionSupportsSystemBackdrop? _target;

    public static bool IsAvailable => DesktopAcrylicController.IsSupported() &&
        TaskbarAppearance.AdvancedEffects && !TaskbarAppearance.Read().HighContrast;

    public void Update(Color tint, double opacity)
    {
        _tint = tint;
        // The preference controls acrylic tint, never whole-window/text opacity.
        _opacity = (float)(0.12 + Math.Clamp(opacity, 0.4, 1) * 0.42);
        ApplyParameters();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _target = target;
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        _controller.AddSystemBackdropTarget(target);
        ApplyParameters();
    }

    private void ApplyParameters()
    {
        if (_controller is null) return;
        _controller.TintColor = _tint;
        _controller.FallbackColor = _tint;
        _controller.TintOpacity = _opacity;
        _controller.LuminosityOpacity = 0.94f;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // Handle this override explicitly: the inherited WinRT override can throw E_INVALIDARG
        // during live theme changes. Ignore notifications after target disconnection.
        if (_target is null || _controller is null) return;
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        ApplyParameters();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        _target = null;
        var controller = _controller;
        _controller = null;
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose();
        base.OnTargetDisconnected(target);
    }
}
