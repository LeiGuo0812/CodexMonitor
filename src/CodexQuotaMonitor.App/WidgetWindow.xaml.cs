using CodexQuotaMonitor.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using Windows.Foundation;
using Windows.UI;

namespace CodexQuotaMonitor.App;

public sealed partial class WidgetWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly TaskbarPositioner _positioner = new();
    private readonly DispatcherTimer _positionTimer = new();
    private readonly WidgetPresentationCache _presentationCache = new();
    private WidgetPresentation? _presentation;
    private bool _measureNeeded = true;
    private int _appearanceReadCount;
    private int _measurements;
    private MonitorViewState _state;
    private MonitorSettings _settings;
    private Point _lastCursor;
    private NativeWindowInterop.Rect _dragRect;
    private bool _pointerDown;
    private bool _dragging;
    private readonly ShellWindowEvents _shellEvents;
    private readonly WidgetClickTracker _clickTracker = new();
    private TaskbarAppearance? _appearance;
    private bool? _clearBackground;
    private readonly TransparentWindowSurface _nativeSurface;
    private bool _hovered;
    private bool _closed;
    private Storyboard? _hoverAnimation;

    public WidgetWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _settings = runtime.Settings;
        _state = runtime.State;
        InitializeComponent();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        _positioner.Attach(WindowNative.GetWindowHandle(this));
        _nativeSurface = new TransparentWindowSurface(WindowNative.GetWindowHandle(this));
        SystemBackdrop = new TaskbarBackdrop();

        Surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Surface_PointerPressed), true);
        Surface.PointerMoved += Surface_PointerMoved;
        Surface.PointerEntered += (_, _) => SetHovered(true);
        Surface.PointerExited += (_, _) => SetHovered(false);
        Surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Surface_PointerReleased), true);
        Surface.PointerCanceled += Surface_PointerCanceled;
        Surface.PointerCaptureLost += Surface_PointerCanceled;
        Surface.IsDoubleTapEnabled = false; // Use Windows double-click timing for mouse releases.
        Surface.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(Surface_RightTapped), true);

        _shellEvents = new ShellWindowEvents(() =>
        {
            if (!_dragging) _positioner.Update(_settings);
        });
        Closed += (_, _) =>
        {
            _closed = true;
            _hoverAnimation?.Stop();
            _shellEvents.Dispose();
            _positionTimer.Stop();
            SystemBackdrop = null;
            _nativeSurface.Dispose();
        };

        _positionTimer.Interval = TimeSpan.FromSeconds(1);
        _positionTimer.Tick += (_, _) =>
        {
            if (!_dragging) _positioner.Update(_settings);
            if (_positioner.HiddenForFullscreen) SetHovered(false);
            TaskbarAppearance.Read();
            if (_appearanceReadCount != TaskbarAppearance.ReadCount)
            {
                _appearanceReadCount = TaskbarAppearance.ReadCount;
                _runtime.RefreshSystemAppearance();
            }
            if (!_positioner.HiddenForFullscreen) UpdatePresentation();
            UpdateTaskbarAppearance();
        };
        _positionTimer.Start();

        ApplySettings(_settings);
        UpdatePresentation();
    }

    public void ShowWithoutActivation()
    {
        AppWindow.Show(activateWindow: false);
        _positioner.Update(_settings);
    }

    public void Update(MonitorViewState state)
    {
        _state = state;
        if (!_positioner.HiddenForFullscreen) UpdatePresentation();
    }

    internal object ReadVerification()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeWindowInterop.GetWindowRect(hwnd, out var bounds);
        var scale = Math.Max(96, NativeWindowInterop.GetDpiForWindow(hwnd)) / 96.0;
        return new
        {
            Visible = NativeWindowInterop.IsWindowVisible(hwnd),
            Placement = _positioner.Diagnostics(),
            WidthDip = bounds.Width / scale,
            HeightDip = bounds.Height / scale,
            ContentWidthDip = WidgetContent.ActualWidth,
            ContentHeightDip = WidgetContent.ActualHeight,
            UnusedWidthDip = bounds.Width / scale - WidgetContent.ActualWidth - 22,
            HasRemainingText = MainLine.Text != "剩余 --",
            HasResetText = !ResetLine.Text.Contains("未知"),
            ShellEventsActive = _shellEvents.IsActive,
            ShellEventsReceived = _shellEvents.Notifications,
            AboveTaskbar = !_positioner.IsBehindTaskbar(),
            TextRoutesToSurface = !WidgetContent.IsHitTestVisible && Surface.IsHitTestVisible,
            DoubleClickMilliseconds = NativeWindowInterop.GetDoubleClickTime(),
            RightTapEnabled = Surface.IsRightTapEnabled,
            UsesTaskbarPalette = _appearance is not null,
            TransparentTaskbarSurface = _clearBackground,
            NativeTransparencyAttached = _nativeSurface.Attached,
            NativeTransparencyResult = _nativeSurface.DwmResult,
            NativeBackgroundClears = _nativeSurface.BackgroundClears,
            SurfaceAlpha = ((SolidColorBrush)Surface.Background).Color.A,
            SideEdgesVisible = LeftEdge.Visibility == Visibility.Visible && RightEdge.Visibility == Visibility.Visible,
            SideEdgeAlpha = ((SolidColorBrush)LeftEdge.Background).Color.A,
            TagUsesThemeAccent = ((SolidColorBrush)WindowTag.Foreground).Color ==
                WindowAppearance.ParseColor(WindowAppearance.Colors(_settings).Accent, Colors.Blue),
            TagColor = ((SolidColorBrush)WindowTag.Foreground).Color.ToString(),
            StatusHasIndependentColor = ((SolidColorBrush)StateMarker.Foreground).Color != ((SolidColorBrush)MainLine.Foreground).Color,
            SystemTaskbarDark = _appearance?.Dark,
            SystemHighContrast = _appearance?.HighContrast,
            PaletteSignature = $"{_appearance?.Background}/{_appearance?.Foreground}/{_appearance?.Secondary}",
            TextMeasurements = _measurements,
            _positioner.IsTaskbarSlot
        };
    }

    public void ApplySettings(MonitorSettings settings)
    {
        if (_settings.PositionMode != settings.PositionMode || _settings.HorizontalOffsetDip != settings.HorizontalOffsetDip ||
            _settings.VerticalOffsetDip != settings.VerticalOffsetDip) _positioner.ResetPlacement();
        _settings = settings;
        _measureNeeded |= MainLine.FontSize != settings.FontSize;
        MainLine.FontSize = settings.FontSize;
        UpdatePresentation();
        if (!_dragging) _positioner.Update(_settings);
        UpdateTaskbarAppearance();
    }

    private void UpdateTaskbarAppearance()
    {
        var appearance = TaskbarAppearance.Read();
        // A clear overlay reveals the taskbar itself even when shell acrylic is disabled.
        var clear = _positioner.IsTaskbarSlot && !appearance.HighContrast && _nativeSurface.DwmResult >= 0;
        var accent = appearance.HighContrast ? appearance.Foreground :
            WindowAppearance.ParseColor(WindowAppearance.Colors(_settings).Accent, Colors.Blue);
        if (WindowTag.Foreground is not SolidColorBrush tag || tag.Color != accent)
            WindowTag.Foreground = new SolidColorBrush(accent);
        // Uniform, lower-intensity light along the spindle. Geometry tapers the ends;
        // horizontal low-alpha skirts supply the glow without a harsh white hotspot.
        var glow = Color.FromArgb(140, (byte)((accent.R + 7 * 255) / 8),
            (byte)((accent.G + 7 * 255) / 8), (byte)((accent.B + 7 * 255) / 8));
        if (LeftHoverEdge.Fill is not SolidColorBrush edge || edge.Color != glow)
        {
            LeftHoverEdge.Fill = RightHoverEdge.Fill = new SolidColorBrush(glow);
            LeftOuterGlow.Fill = RightOuterGlow.Fill = SoftEdgeGlow(glow, 20);
            LeftInnerGlow.Fill = RightInnerGlow.Fill = SoftEdgeGlow(glow, 32);
        }
        var state = _presentation?.StateMarker;
        var stateColor = appearance.HighContrast ? appearance.Foreground : state switch
        {
            "已连接" => Color.FromArgb(255, 49, 150, 108),
            "数据陈旧" => Color.FromArgb(255, 201, 139, 46),
            _ => Color.FromArgb(255, 188, 72, 68)
        };
        if (StateMarker.Foreground is not SolidColorBrush marker || marker.Color != stateColor)
            StateMarker.Foreground = new SolidColorBrush(stateColor);
        if (appearance == _appearance && clear == _clearBackground) return;
        _appearance = appearance;
        _clearBackground = clear;
        Surface.RequestedTheme = appearance.Dark ? ElementTheme.Dark : ElementTheme.Light;
        Surface.Background = new SolidColorBrush(clear ? Colors.Transparent : appearance.Background);
        Surface.BorderBrush = new SolidColorBrush(Colors.Transparent);
        Surface.CornerRadius = new CornerRadius(clear ? 0 : 12);
        LeftEdge.Visibility = RightEdge.Visibility = clear ? Visibility.Visible : Visibility.Collapsed;
        LeftEdge.Background = RightEdge.Background = new SolidColorBrush(Color.FromArgb(64,
            appearance.Foreground.R, appearance.Foreground.G, appearance.Foreground.B));
        MainLine.Foreground = new SolidColorBrush(appearance.Foreground);
        ResetLine.Foreground = new SolidColorBrush(appearance.Secondary);
        HoverEdges.Visibility = clear ? Visibility.Visible : Visibility.Collapsed;
        // Hover adds light in both shell modes. A dark translucent wash would dim a light taskbar.
        HoverFill.Background = new SolidColorBrush(Color.FromArgb(appearance.Dark ? (byte)48 : (byte)96, 255, 255, 255));
    }

    private static LinearGradientBrush SoftEdgeGlow(Color tint, byte peakAlpha)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (offset, alpha) in new[] { (0.0, 0), (0.25, peakAlpha / 3), (0.5, (int)peakAlpha), (0.75, peakAlpha / 3), (1.0, 0) })
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = Color.FromArgb((byte)alpha, tint.R, tint.G, tint.B) });
        return brush;
    }

    private void ApplyHoverOpacity(double value)
    {
        HoverFill.Opacity = HoverEdges.Opacity = value;
        LeftEdge.Opacity = RightEdge.Opacity = 1 - value;
    }

    private void SetHovered(bool hovered)
    {
        if (_closed || _hovered == hovered) return;
        _hovered = hovered;
        var from = HoverFill.Opacity;
        _hoverAnimation?.Stop();
        var target = hovered ? 1.0 : 0.0;
        ApplyHoverOpacity(from);
        if (!TaskbarAppearance.Animations)
        {
            ApplyHoverOpacity(target);
            _hoverAnimation = null;
            return;
        }
        var storyboard = new Storyboard();
        foreach (var element in new UIElement[] { HoverFill, HoverEdges, LeftEdge, RightEdge })
        {
            var idle = ReferenceEquals(element, LeftEdge) || ReferenceEquals(element, RightEdge);
            var animation = new DoubleAnimation { From = idle ? 1 - from : from, To = idle ? 1 - target : target,
                Duration = new Duration(TimeSpan.FromMilliseconds(hovered ? 180 : 240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
        }
        _hoverAnimation = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (_closed || _hoverAnimation != storyboard) return;
            storyboard.Stop();
            ApplyHoverOpacity(target);
            _hoverAnimation = null;
        };
        storyboard.Begin();
    }

    internal async Task<object> VerifyHoverAsync()
    {
        SetHovered(true);
        await Task.Delay(300);
        var highlighted = HoverFill.Opacity > 0.99 && HoverEdges.Opacity > 0.99;
        var idleLinesReplaced = LeftEdge.Opacity < 0.01 && RightEdge.Opacity < 0.01;
        SetHovered(false);
        await Task.Delay(350);
        return new { Highlighted = highlighted, Restored = HoverFill.Opacity < 0.01 && HoverEdges.Opacity < 0.01 && LeftEdge.Opacity > 0.99 && RightEdge.Opacity > 0.99,
            SpindleEdges = LeftHoverEdge.Data is not null && RightHoverEdge.Data is not null && LeftHoverEdge.Width == 2 && RightHoverEdge.Width == 2,
            SoftGlow = LeftOuterGlow.Fill is LinearGradientBrush { GradientStops.Count: 5 } && LeftInnerGlow.Fill is LinearGradientBrush { GradientStops.Count: 5 },
            UniformReducedLight = LeftHoverEdge.Fill is SolidColorBrush { Color.A: 140 } && RightHoverEdge.Fill is SolidColorBrush { Color.A: 140 },
            IdleLinesReplaced = idleLinesReplaced,
            LightOverlay = HoverFill.Background is SolidColorBrush { Color.R: 255, Color.G: 255, Color.B: 255 },
            HitTestingPreserved = !HoverFill.IsHitTestVisible && !HoverEdges.IsHitTestVisible && Surface.IsHitTestVisible };
    }

    private void UpdatePresentation()
    {
        if (_closed) return;
        var presentation = _presentationCache.Get(_state.Selection, DateTimeOffset.Now,
            _state.LastSuccessfulUpdate, _state.QueryError);
        if (!ReferenceEquals(presentation, _presentation))
        {
            _measureNeeded |= _presentation is null || presentation.MainLine != _presentation.MainLine ||
                presentation.WindowTag != _presentation.WindowTag || presentation.ResetLine != _presentation.ResetLine ||
                presentation.StateMarker != _presentation.StateMarker;
            UiUpdates.Text(MainLine, presentation.MainLine);
            UiUpdates.Text(WindowTag, presentation.WindowTag);
            WindowTag.Visibility = string.IsNullOrEmpty(presentation.WindowTag) ? Visibility.Collapsed : Visibility.Visible;
            UiUpdates.Text(ResetLine, presentation.ResetLine);
            UiUpdates.Text(StateMarker, presentation.StateMarker == "已连接" ? "●" : "!");
            if (_presentation?.Tooltip != presentation.Tooltip || _presentation?.StateMarker != presentation.StateMarker)
                UiUpdates.Tooltip(Surface, presentation.Tooltip + $"\n状态：{presentation.StateMarker}");
            _presentation = presentation;
            UpdateTaskbarAppearance();
        }
        if (!_measureNeeded) return;
        _measureNeeded = false;
        _measurements++;
        // Measure the two lines instead of reserving a fixed 198 DIP and a star column.
        WidgetContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _positioner.SetSize((int)Math.Ceiling(WidgetContent.DesiredSize.Width) + 22,
            (int)Math.Ceiling(WidgetContent.DesiredSize.Height) + 6);
        if (!_dragging) _positioner.Update(_settings);
        UpdateTaskbarAppearance();
    }

    private void Surface_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        args.Handled = true;
        _runtime.ShowContextMenu();
    }
    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Surface);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (!NativeWindowInterop.GetCursorPos(out var cursor)) return;
        _lastCursor = new Point(cursor.X, cursor.Y);
        NativeWindowInterop.GetWindowRect(WindowNative.GetWindowHandle(this), out _dragRect);
        _pointerDown = Surface.CapturePointer(args.Pointer);
        _dragging = false;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_pointerDown || !args.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed ||
            !NativeWindowInterop.GetCursorPos(out var cursor)) return;
        var current = new Point(cursor.X, cursor.Y);
        var dx = (int)Math.Round(current.X - _lastCursor.X);
        var dy = (int)Math.Round(current.Y - _lastCursor.Y);
        var threshold = 6 * Math.Max(96, NativeWindowInterop.GetDpiForWindow(WindowNative.GetWindowHandle(this))) / 96.0;
        if (!_dragging && Math.Abs(dx) < threshold && Math.Abs(dy) < threshold) return;
        _clickTracker.Reset();
        _dragging = true;
        _dragRect.Left += dx;
        _dragRect.Right += dx;
        _dragRect.Top += dy;
        _dragRect.Bottom += dy;
        _lastCursor = current;
        NativeWindowInterop.SetWindowPos(WindowNative.GetWindowHandle(this), new IntPtr(-1),
            _dragRect.Left, _dragRect.Top, _dragRect.Width, _dragRect.Height,
            NativeWindowInterop.SwpNoActivate | NativeWindowInterop.SwpShowWindow);
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_pointerDown) return;
        var dragged = _dragging;
        _pointerDown = false;
        _dragging = false;
        Surface.ReleasePointerCapture(args.Pointer);
        if (dragged)
        {
            _runtime.UpdateDraggedPosition(_dragRect.Left, _dragRect.Top, _positioner.IsTaskbarSlot);
            return;
        }
        if (NativeWindowInterop.GetCursorPos(out var cursor) && _clickTracker.Release(cursor.X, cursor.Y,
            Environment.TickCount64, (int)NativeWindowInterop.GetDoubleClickTime(),
            Math.Max(2, NativeWindowInterop.GetSystemMetrics(36) / 2),
            Math.Max(2, NativeWindowInterop.GetSystemMetrics(37) / 2)))
            _runtime.OpenDetails();
    }

    internal IntPtr Handle => WindowNative.GetWindowHandle(this);
    internal object VerifyResourceReuse()
    {
        UpdatePresentation();
        UpdateTaskbarAppearance();
        var tag = WindowTag.Foreground;
        var marker = StateMarker.Foreground;
        var background = Surface.Background;
        var measurements = _measurements;
        var reads = TaskbarAppearance.ReadCount;
        for (var i = 0; i < 100; i++) { UpdatePresentation(); UpdateTaskbarAppearance(); }
        return new { BrushesReused = ReferenceEquals(tag, WindowTag.Foreground) && ReferenceEquals(marker, StateMarker.Foreground) && ReferenceEquals(background, Surface.Background),
            ExtraMeasurements = _measurements - measurements, ExtraSystemReads = TaskbarAppearance.ReadCount - reads };
    }
    internal void InvalidateMetrics() => _measureNeeded = true;
    internal void RefreshPlacement() { if (!_dragging) _positioner.Update(_settings); }

    internal MonitorSettings SettingsForDrop(int left, int top)
    {
        NativeWindowInterop.GetWindowRect(Handle, out var bounds);
        var scale = Math.Max(96, NativeWindowInterop.GetDpiForWindow(Handle)) / 96.0;
        return TaskbarPositioner.SettingsForDrop(_settings, left, top, bounds.Width, bounds.Height, scale);
    }

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        _pointerDown = false;
        _dragging = false;
    }

}
