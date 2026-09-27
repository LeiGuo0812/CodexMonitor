using CodexQuotaMonitor.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace CodexQuotaMonitor.App;

public sealed partial class DashboardWindow : Window
{
    private readonly AppRuntime _runtime;
    private MonitorSettings _settings;
    private MonitorViewState _state;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _compact;
    private bool _fiveHourUnavailable;
    private bool _fitted;
    private bool _fittedForData;
    private bool _closed;
    private readonly HashSet<ProgressBar> _animated = new();
    private readonly Dictionary<ProgressBar, Storyboard> _animations = new();
    private bool _observedLoading;
    private double _preferredHeightDip = 740;
    private readonly List<CreditRow> _creditRows = new();
    private readonly List<QuotaRow> _quotaRows = new();
    private ThemeColors? _colors;
    private double _glassOpacity;
    private bool _glassAvailable;
    private Brush? _creditBackground;
    private Brush? _otherBackground;
    private Brush? _otherBorder;
    private bool _suspended;
    private bool _timesDirty = true;
    private (long Five, long Week, long Updated, DateTime Day, TimeZoneInfo Zone)? _timeKey;
    private int _cardCreations;
    private MonitorViewState? _renderedState;

    public DashboardWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _settings = runtime.Settings;
        _state = runtime.State;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.IsMaximizable = false;
        Page.KeyDown += Page_KeyDown;
        ApplySettings(_settings);
        Update(_state);
        WindowPlacement.AboveTaskbar(this, 480, 740, _runtime.WidgetHandle);
        Page.Loaded += Page_Loaded;
        _clock.Tick += Clock_Tick;
        AppWindow.Changed += Window_Changed;
        _clock.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _clock.Stop();
            _clock.Tick -= Clock_Tick;
            Page.Loaded -= Page_Loaded;
            Page.KeyDown -= Page_KeyDown;
            AppWindow.Changed -= Window_Changed;
            foreach (var animation in _animations.Values) animation.Stop();
            _animations.Clear();
            _animated.Clear();
            _creditRows.Clear();
            _quotaRows.Clear();
            CreditsList.Children.Clear();
            OtherWindowsList.Children.Clear();
            SystemBackdrop = null;
        };
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) { _renderedState = null; Update(_state); FitInitialContent(); }
    private void Clock_Tick(object? sender, object e) => UpdateTimes();
    private void Window_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs e)
    {
        if (_closed) return;
        var minimized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
        if (_suspended == minimized) return;
        _suspended = minimized;
        if (minimized) _clock.Stop();
        else
        {
            ApplySettings(_settings);
            _timesDirty = true;
            Update(_state);
            _clock.Start();
        }
    }

    public void ApplySettings(MonitorSettings settings)
    {
        if (_closed) return;
        _settings = settings;
        if (_suspended) return;
        var colors = WindowAppearance.Colors(settings);
        var glassAvailable = GlassBackdrop.IsAvailable;
        if (_colors == colors && _glassOpacity == settings.DetailsOpacity && _glassAvailable == glassAvailable) return;
        _colors = colors;
        _glassOpacity = settings.DetailsOpacity;
        _glassAvailable = glassAvailable;
        WindowAppearance.ApplyTheme(WindowRoot, settings);
        WindowAppearance.ApplyGlass(this, settings);
        var background = WindowAppearance.ParseColor(colors.Background, Color.FromArgb(255, 232, 237, 243));
        var primary = new SolidColorBrush(WindowAppearance.ParseColor(colors.PrimaryText, Colors.Black));
        var secondary = new SolidColorBrush(WindowAppearance.ParseColor(colors.SecondaryText, Colors.Gray));
        WindowRoot.Background = WindowAppearance.Translucent(colors.Background, SystemBackdrop is GlassBackdrop ? 0.12 : 1);
        var edge = WindowAppearance.Translucent(colors.PrimaryText, 0.13);
        foreach (var card in new[] { MainQuotaCard, ResetTimingCard, WeekTimingCard, CreditsCard })
        {
            card.BorderBrush = edge;
        }
        MainQuotaCard.Background = WindowAppearance.PanelSurface(colors, colors.Accent);
        ResetTimingCard.Background = WindowAppearance.PanelSurface(colors, "#39A992");
        WeekTimingCard.Background = WindowAppearance.PanelSurface(colors, colors.WeeklyAccent);
        // A muted tint from the active palette keeps the credit panel distinct
        // without introducing a fixed amber cast into cool or custom themes.
        CreditsCard.Background = WindowAppearance.PanelSurface(colors, colors.SecondaryText);
        CreditsCard.BorderBrush = WindowAppearance.Translucent(colors.SecondaryText, 0.20);
        CreditsCountText.Foreground = primary;
        CreditsStatusText.Foreground = secondary;
        CreditsStatusText.Opacity = 1;
        // Set inherited text color on the root as well as on primary and auxiliary labels.
        WindowRoot.SetValue(TextBlock.ForegroundProperty, primary);
        RemainingText.Foreground = primary;
        ResetLocalText.Foreground = secondary;
        WeeklyResetDateText.Foreground = secondary;
        ConnectionText.Foreground = secondary;
        QuotaProgress.Foreground = WindowAppearance.Translucent(colors.Accent, 1);
        QuotaProgress.Background = WindowAppearance.Translucent("#172337", 0.4);
        MainResetProgress.Foreground = WindowAppearance.Translucent(colors.Accent, 1);
        MainResetProgress.Background = WindowAppearance.Translucent("#142E2D", 0.4);
        WeeklyResetProgress.Foreground = WindowAppearance.Translucent(colors.WeeklyAccent, 1);
        WeeklyResetProgress.Background = WindowAppearance.Translucent("#291B3B", 0.4);
        ResetCountdownText.Foreground = MainResetProgress.Foreground;
        WeeklyCountdownText.Foreground = WeeklyResetProgress.Foreground;
        OtherWindowsList.Opacity = 1;
        _creditBackground = WindowAppearance.Translucent(colors.Background, 0.32);
        _otherBackground = WindowAppearance.GlassSurface(colors, 0.28);
        _otherBorder = new SolidColorBrush(Color.FromArgb(38, background.R, background.G, background.B));
        foreach (var row in _creditRows) row.Root.Background = _creditBackground;
        foreach (var row in _quotaRows) { row.Root.Background = _otherBackground; row.Root.BorderBrush = _otherBorder; }
        Update(_state);
    }

    public void Update(MonitorViewState state)
    {
        if (_closed) return;
        _timesDirty |= _state.Selection != state.Selection || _state.LastSuccessfulUpdate != state.LastSuccessfulUpdate ||
            _state.QueryError != state.QueryError || !_state.AvailableWindows.SequenceEqual(state.AvailableWindows);
        _state = state;
        if (_suspended) return;
        if (RefreshButton.IsEnabled == state.IsRefreshing) RefreshButton.IsEnabled = !state.IsRefreshing;
        var buttonText = state.IsRefreshing ? "刷新中…" : "刷新";
        if (!Equals(RefreshButton.Content, buttonText)) RefreshButton.Content = buttonText;
        var previous = _renderedState;
        _renderedState = state;
        if (previous is not null && previous.Selection == state.Selection && previous.QueryError == state.QueryError &&
            previous.PlanType == state.PlanType && previous.AvailableWindows.SequenceEqual(state.AvailableWindows) &&
            previous.ResetCredits.AvailableCount == state.ResetCredits.AvailableCount &&
            previous.ResetCredits.DetailsStatus == state.ResetCredits.DetailsStatus &&
            previous.ResetCredits.Credits.SequenceEqual(state.ResetCredits.Credits))
        {
            UpdateTimes();
            return;
        }
        var selection = state.Selection;
        var main = selection.MainWindow;
        MainWindowName.Text = main?.Name ?? "当前额度窗口未知";
        RemainingText.Text = main?.RemainingLabel ?? "剩余 --";
        SetProgress(QuotaProgress, main?.RemainingPercent);
        PlanText.Text = PlanLabel(state.PlanType);
        QuotaStateText.Text = selection.Status switch
        {
            QuotaSelectionStatus.Current => selection.IsStale ? "数据陈旧" : "当前窗口",
            QuotaSelectionStatus.AccountChanged => "账户已切换",
            QuotaSelectionStatus.MissingPreviouslyKnownWindow => "5 小时窗口字段暂缺",
            QuotaSelectionStatus.QueryFailed => "查询失败",
            _ => "等待额度数据"
        };

        var restricted = state.AvailableWindows.FirstOrDefault(window => window.RateLimitReachedType is not null)?.RateLimitReachedType;
        RestrictionText.Visibility = restricted is null ? Visibility.Collapsed : Visibility.Visible;
        RestrictionText.Text = restricted is null ? string.Empty : RestrictionLabel(restricted);

        UpdateOtherWindows(state.AvailableWindows, main);

        UpdateResetCredits(state.ResetCredits);
        UpdateTimes();
        ConnectionText.Text = ConnectionStatus(selection, state.QueryError);
        if (Page.IsLoaded && !_fittedForData && state.LastSuccessfulUpdate is not null)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitInitialContent);
    }

    private void UpdateTimes()
    {
        if (_closed || _suspended) return;
        var now = DateTimeOffset.Now;
        var fiveHour = _state.AvailableWindows.FirstOrDefault(window => window.Kind == QuotaWindowKind.FiveHour);
        if (_fiveHourUnavailable != (fiveHour is null))
        {
            _fiveHourUnavailable = fiveHour is null;
            UpdateTimeCardsLayout();
        }
        var week = _state.AvailableWindows.FirstOrDefault(window => window.Kind == QuotaWindowKind.Week)
            ?? (_state.Selection.MainWindow?.Kind == QuotaWindowKind.Week ? _state.Selection.MainWindow : null);
        if (fiveHour is null) MainResetProgress.Visibility = Visibility.Collapsed;
        else SetTimeProgress(MainResetProgress, fiveHour, now);
        SetTimeProgress(WeeklyResetProgress, week, now);
        foreach (var row in _creditRows) row.UpdateTime(now);
        foreach (var row in _quotaRows) row.UpdateTime(now);
        var updatedKey = _state.LastSuccessfulUpdate is { } updated ? (long)((now - updated).TotalMinutes) : long.MinValue;
        var timeKey = (QuotaFormatting.CountdownKey(fiveHour?.ResetsAt, now), QuotaFormatting.CountdownKey(week?.ResetsAt, now),
            updatedKey, now.Date, TimeZoneInfo.Local);
        if (!_timesDirty && _timeKey == timeKey) return;
        _timesDirty = false;
        _timeKey = timeKey;
        MainResetTitle.Text = "5 小时重置";
        MainResetTitle.Visibility = fiveHour is null ? Visibility.Collapsed : Visibility.Visible;
        ResetCountdownText.Text = fiveHour is null ? "5小时额度不可用" : QuotaFormatting.FormatTimeRemaining(fiveHour.ResetsAt, now);
        ResetLocalText.Text = fiveHour?.ResetsAt is null ? "自然重置时间未知" : $"{QuotaFormatting.FormatReadableDate(fiveHour.ResetsAt, now)} 重置";
        ResetLocalText.Visibility = fiveHour is null ? Visibility.Collapsed : Visibility.Visible;
        CopyResetButton.Visibility = fiveHour is null ? Visibility.Collapsed : Visibility.Visible;
        CopyResetButton.IsEnabled = fiveHour?.ResetsAt is not null;
        WeeklyCountdownText.Text = week is null ? "周额度数据暂不可用" : QuotaFormatting.FormatTimeRemaining(week.ResetsAt, now);
        WeeklyResetDateText.Text = week?.ResetsAt is null ? "等待服务端提供周重置时间" : $"{QuotaFormatting.FormatReadableDate(week.ResetsAt, now)} 重置";
        WeeklyRemainingText.Text = week?.RemainingLabel ?? "";
        CopyWeeklyResetButton.IsEnabled = week?.ResetsAt is not null;
        UiUpdates.Tooltip(ResetLocalText, ExactDateTooltip(fiveHour?.ResetsAt));
        UiUpdates.Tooltip(WeeklyResetDateText, ExactDateTooltip(week?.ResetsAt));
        LastUpdateText.Text = QuotaFormatting.FormatUpdatedAgo(_state.LastSuccessfulUpdate, now);
        UiUpdates.Tooltip(LastUpdateText, QuotaFormatting.FormatFullLocalDate(_state.LastSuccessfulUpdate));
    }

    private void SetTimeProgress(ProgressBar bar, QuotaWindow? window, DateTimeOffset now)
    {
        var remaining = QuotaFormatting.ResetTimeRemainingPercent(window, now);
        // A tenth of a percent is already sub-pixel at this window width.
        SetProgress(bar, remaining is { } value ? Math.Round(value, 1) : null);
        UiUpdates.Tooltip(bar, "当前周期剩余时间比例；与可用额度比例独立计算");
    }

    private void SetProgress(ProgressBar bar, double? value)
    {
        var loading = value is null && _state.LastSuccessfulUpdate is null && _state.QueryError is null;
        _observedLoading |= loading;
        if (bar.IsIndeterminate != loading) bar.IsIndeterminate = loading;
        var visibility = loading || value is not null ? Visibility.Visible : Visibility.Collapsed;
        if (bar.Visibility != visibility) bar.Visibility = visibility;
        if (value is null) return;
        if (!Page.IsLoaded) { bar.Value = 0; return; }
        if (_animations.ContainsKey(bar)) return;
        if (!_animated.Add(bar)) { if (bar.Value != value.Value) bar.Value = value.Value; return; }
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 0, To = value.Value, Duration = new Duration(TimeSpan.FromMilliseconds(650)),
            EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, bar);
        Storyboard.SetTargetProperty(animation, "Value");
        storyboard.Children.Add(animation);
        _animations[bar] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (_closed) return;
            storyboard.Stop();
            bar.Value = value.Value;
            _animations.Remove(bar);
        };
        storyboard.Begin();
    }

    public void ShowAtTaskbar()
    {
        if (_closed) return;
        WindowPlacement.AboveTaskbar(this, 480, _preferredHeightDip, _runtime.WidgetHandle);
    }

    internal static string ExactDateTooltip(DateTimeOffset? date) => date is { } value
        ? $"{QuotaFormatting.FormatFullLocalDate(value)}\nUTC：{value.UtcDateTime:yyyy-MM-dd HH:mm:ss}"
        : "时间未知";

    private void FitInitialContent()
    {
        if (_closed) return;
        var hasData = _state.LastSuccessfulUpdate is not null;
        if (_fitted && (_fittedForData || !hasData)) return;
        _fitted = true;
        _fittedForData = hasData;
        DashboardColumns.UpdateLayout();
        DashboardColumns.Measure(new Windows.Foundation.Size(Math.Max(1, Page.ActualWidth - 36), double.PositiveInfinity));
        _preferredHeightDip = Math.Clamp(DashboardColumns.DesiredSize.Height + 172, 620, 880);
        ShowAtTaskbar();
    }

    private void DashboardColumns_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WeekTimingCard is null) return;
        var compact = e.NewSize.Width < 390;
        if (compact == _compact) return;
        _compact = compact;
        UpdateTimeCardsLayout();
    }

    private void UpdateTimeCardsLayout()
    {
        var stacked = _compact || _fiveHourUnavailable;
        TimeCards.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        TimeCards.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TimeCards.ColumnSpacing = stacked ? 0 : 10;
        Grid.SetColumn(WeekTimingCard, stacked ? 0 : 1);
        Grid.SetRow(WeekTimingCard, stacked ? 1 : 0);
        ResetTimingCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        ResetTimingCard.VerticalAlignment = _fiveHourUnavailable ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        ResetTimingCard.Padding = _fiveHourUnavailable ? new Thickness(10, 8, 10, 8) : new Thickness(13);
        ResetCountdownText.FontSize = _fiveHourUnavailable ? 12 : 16;
    }

    private void UpdateOtherWindows(IReadOnlyList<QuotaWindow> windows, QuotaWindow? main)
    {
        var index = 0;
        var now = DateTimeOffset.Now;
        foreach (var window in windows)
        {
            if (window.Kind == QuotaWindowKind.Week || (main is not null && window.Key == main.Key)) continue;
            if (index == _quotaRows.Count)
            {
                var row = new QuotaRow();
                row.Root.Background = _otherBackground;
                row.Root.BorderBrush = _otherBorder;
                _quotaRows.Add(row);
                OtherWindowsList.Children.Add(row.Root);
                _cardCreations++;
            }
            _quotaRows[index++].Update(window, now);
        }
        while (_quotaRows.Count > index)
        {
            OtherWindowsList.Children.RemoveAt(_quotaRows.Count - 1);
            _quotaRows.RemoveAt(_quotaRows.Count - 1);
        }
        OtherWindowsSection.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateResetCredits(ResetCreditsSummary summary)
    {
        UiUpdates.Text(CreditsCountText, summary.AvailableCount is { } count ? $"可用 {count} 张" : "数量未知");
        UiUpdates.Text(CreditsStatusText, summary.DetailsStatus switch
        {
            ResetCreditDetailsStatus.Unavailable => "服务端未提供重置卡状态。",
            ResetCreditDetailsStatus.CountOnly => "已知卡片数量；到期详情暂不可用。",
            ResetCreditDetailsStatus.Partial => $"服务端报告可用 {summary.AvailableCount} 张，返回 {summary.Credits.Count} 条详情；列表可能不完整。",
            ResetCreditDetailsStatus.Complete when summary.AvailableCount == 0 => "服务端确认当前没有可用重置卡。",
            _ => "按到期时间排序；每张卡单独保留。"
        });
        var index = 0;
        var now = DateTimeOffset.Now;
        foreach (var credit in summary.OrderedCredits)
        {
            if (index == _creditRows.Count)
            {
                var row = new CreditRow();
                row.Root.Background = _creditBackground;
                _creditRows.Add(row);
                CreditsList.Children.Add(row.Root);
                _cardCreations++;
            }
            _creditRows[index].Update(credit, index + 1, now);
            index++;
        }
        while (_creditRows.Count > index)
        {
            CreditsList.Children.RemoveAt(_creditRows.Count - 1);
            _creditRows.RemoveAt(_creditRows.Count - 1);
        }
    }
    private static string ConnectionStatus(QuotaSelection selection, string? queryError)
    {
        if (selection.Status == QuotaSelectionStatus.MissingPreviouslyKnownWindow)
            return "Codex 已返回额度，但此前识别到的 5 小时窗口字段暂缺。已保留该窗口的上次数据并标记陈旧，后续刷新确认。";
        if (queryError is null && selection.Status is QuotaSelectionStatus.Current or QuotaSelectionStatus.AccountChanged)
            return selection.Status == QuotaSelectionStatus.AccountChanged ? "账户已变化；已清除上一账户的缓存。" : "Codex app-server 连接正常。";
        return queryError switch
        {
            "CODEX_NOT_FOUND" => "找不到 Codex CLI。请在设置中选择 Codex 可执行文件。",
            "CODEX_NOT_A_CLI" => "找到的程序不是可用的 Codex CLI。请在设置中选择正确的 codex.exe，或清空路径以自动发现桌面版自带 CLI。",
            "CODEX_START_FAILED" => "无法启动所选 Codex CLI。请检查路径与权限。",
            "CHATGPT_AUTH_REQUIRED" or "ACCOUNT_UNAVAILABLE" => "当前 Codex 账户未通过 ChatGPT 登录。请在 Codex 中恢复登录。",
            "TIMEOUT" => "Codex app-server 响应超时；将按退避间隔重试。",
            "APP_SERVER_EXITED" => "Codex app-server 提前退出；下次刷新会重新启动。",
            null => "等待 Codex 额度数据。",
            _ => $"查询未完成（{queryError}）；没有使用演示额度替代真实数据。"
        };
    }

    internal static string RestrictionLabel(string value) => value switch
    {
        "rate_limit_reached" => "Codex 服务端报告当前额度窗口已达到使用限制。",
        "workspace_owner_credits_depleted" => "工作区所有者额度已耗尽，当前请求可能受限。",
        "workspace_member_credits_depleted" => "工作区成员额度已耗尽，当前请求可能受限。",
        "workspace_owner_usage_limit_reached" => "工作区所有者使用上限已达到。",
        "workspace_member_usage_limit_reached" => "工作区成员使用上限已达到。",
        _ => "Codex 服务端报告当前账户受限。"
    };

    private static string PlanLabel(string? planType) => planType switch
    {
        "pro" => "ChatGPT Pro",
        "plus" => "ChatGPT Plus",
        "prolite" => "ChatGPT Pro Lite",
        "team" => "ChatGPT Team",
        "business" or "self_serve_business_usage_based" => "ChatGPT Business",
        "enterprise" or "enterprise_cbp_usage_based" => "ChatGPT Enterprise",
        "edu" => "ChatGPT Edu",
        "free" => "ChatGPT Free",
        null => "正在读取 Codex 账户",
        _ => $"ChatGPT {planType}"
    };

    private void CopyResetButton_Click(object sender, RoutedEventArgs e)
    {
        var reset = _state.AvailableWindows.FirstOrDefault(window => window.Kind == QuotaWindowKind.FiveHour)?.ResetsAt;
        CopyResetTime(reset);
    }

    private void CopyWeeklyResetButton_Click(object sender, RoutedEventArgs e)
    {
        var week = _state.AvailableWindows.FirstOrDefault(window => window.Kind == QuotaWindowKind.Week)
            ?? (_state.Selection.MainWindow?.Kind == QuotaWindowKind.Week ? _state.Selection.MainWindow : null);
        CopyResetTime(week?.ResetsAt);
    }

    private static void CopyResetTime(DateTimeOffset? reset)
    {
        if (reset is null) return;
        var data = new DataPackage();
        data.SetText($"{QuotaFormatting.FormatFullLocalDate(reset)}\nUTC：{reset.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss}（UTC+00:00）");
        Clipboard.SetContent(data);
    }

    internal object ReadVerification() => new
    {
        HasRemainingText = RemainingText.Text != "剩余 --",
        HasResetText = _state.AvailableWindows.Any(window => window.ResetsAt is not null),
        HasCreditCount = CreditsCountText.Text != "数量未知",
        HasCreditDetails = CreditsList.Children.Count > 0,
        MainWindowKind = _state.Selection.MainWindow?.Kind.ToString(),
        FiveHourAvailable = _state.AvailableWindows.Any(window => window.Kind == QuotaWindowKind.FiveHour),
        FiveHourUnavailableOnly = ResetCountdownText.Text == "5小时额度不可用" &&
            MainResetTitle.Visibility == Visibility.Collapsed && ResetLocalText.Visibility == Visibility.Collapsed &&
            MainResetProgress.Visibility == Visibility.Collapsed && CopyResetButton.Visibility == Visibility.Collapsed,
        FiveHourPanelTitle = MainResetTitle.Text,
        FiveHourPanelWidth = ResetTimingCard.ActualWidth,
        FiveHourPanelHeight = ResetTimingCard.ActualHeight,
        FiveHourTextWidth = ResetCountdownText.ActualWidth,
        FiveHourTextHeight = ResetCountdownText.ActualHeight,
        FiveHourUnavailableStacked = !_fiveHourUnavailable ||
            (Grid.GetRow(WeekTimingCard) == 1 && Grid.GetColumn(WeekTimingCard) == 0 &&
             Math.Abs(ResetTimingCard.ActualWidth - TimeCards.ActualWidth) < 2 &&
             Math.Abs(WeekTimingCard.ActualWidth - TimeCards.ActualWidth) < 2 &&
             Math.Abs(ResetTimingCard.ActualHeight - ResetCountdownText.ActualHeight - 18) < 2),
        RefreshEnabled = RefreshButton.IsEnabled,
        MainTimeBarVisible = MainResetProgress.Visibility == Visibility.Visible,
        WeeklyTimeBarVisible = WeeklyResetProgress.Visibility == Visibility.Visible,
        TimeBarColorsDiffer = ((SolidColorBrush)MainResetProgress.Foreground).Color != ((SolidColorBrush)WeeklyResetProgress.Foreground).Color,
        ThickProgressBars = new[] { QuotaProgress, MainResetProgress, WeeklyResetProgress }
            .Where(bar => bar.Visibility == Visibility.Visible).All(bar =>
                bar.ActualHeight >= 10 && TemplatePartHeight(bar, "ProgressBarTrack") >= 9.5 &&
                TemplatePartHeight(bar, "DeterminateProgressBarIndicator") >= 9.5),
        CreditsUseThemeTint = ((LinearGradientBrush)CreditsCard.Background).GradientStops[0].Color ==
            WindowAppearance.Translucent(WindowAppearance.Colors(_settings).SecondaryText, 0.22).Color,
        ReadableDate = new[] { ResetLocalText.Text, WeeklyResetDateText.Text }.Any(text => text.Contains('月') || text.Contains('天')),
        GlassBackdrop = SystemBackdrop is GlassBackdrop,
        Bounds = WindowPlacement.ReadVerification(this),
        FooterVisible = SettingsButton.ActualHeight > 0,
        ContentFitsWidth = DashboardColumns.ActualWidth <= DetailsScroll.ViewportWidth + 1,
        ScrollableHeight = DetailsScroll.ScrollableHeight,
        CompactLayout = _compact,
        UnifiedTitleBar = ExtendsContentIntoTitleBar && AppWindow.TitleBar.ButtonBackgroundColor == Colors.Transparent,
        InitialAnimationsStarted = _animated.Count,
        InitialLoadingObserved = _observedLoading
    };

    private static double? TemplatePartHeight(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element.ActualHeight;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (TemplatePartHeight(VisualTreeHelper.GetChild(root, i), name) is { } height) return height;
        return null;
    }

    internal bool ClockSuspended => _suspended && !_clock.IsEnabled;
    internal bool ClockRunning => !_suspended && _clock.IsEnabled;

    internal object VerifyResourceReuse()
    {
        var original = _state;
        var now = DateTimeOffset.Now;
        var credit = new ResetCredit("duplicate", null, "available", now, now.AddDays(2), "自检卡片", null);
        var other = new QuotaWindow("verification", "自检额度", QuotaWindowKind.Other, 20, now.AddHours(2), 300, null);
        var sample = original with { AvailableWindows = original.AvailableWindows.Concat(new[] { other, other }).ToArray(),
            ResetCredits = new ResetCreditsSummary(2, new[] { credit, credit }, ResetCreditDetailsStatus.Complete) };
        try
        {
            Update(sample);
            var first = _creditRows[0].Root;
            var second = _creditRows[1].Root;
            var creations = _cardCreations;
            Update(sample with { IsRefreshing = true });
            ApplySettings(_settings);
            Update(sample with { AvailableWindows = sample.AvailableWindows.Select(w => w with { }).ToArray(),
                ResetCredits = sample.ResetCredits with { Credits = sample.ResetCredits.Credits.Select(c => c with { }).ToArray() } });
            var unchangedReuses = creations == _cardCreations && ReferenceEquals(first, _creditRows[0].Root) && ReferenceEquals(second, _creditRows[1].Root);
            var changed = sample with { ResetCredits = sample.ResetCredits with { Credits = new[] { credit with { Title = "已更新" }, credit } } };
            Update(changed);
            var changedReuses = creations == _cardCreations && ReferenceEquals(first, _creditRows[0].Root) &&
                ((TextBlock)((StackPanel)first.Child).Children[0]).Text.StartsWith("已更新");
            Update(changed with { ResetCredits = new ResetCreditsSummary(1, new[] { credit }, ResetCreditDetailsStatus.Complete) });
            var removed = _creditRows.Count == 1 && CreditsList.Children.Count == 1;
            return new { UnchangedReuses = unchangedReuses, ChangedReuses = changedReuses, RemovedRowsReleased = removed,
                DuplicateCardsPreserved = !ReferenceEquals(first, second) };
        }
        finally { Update(original); }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _runtime.RefreshNow();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _runtime.OpenSettings();

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
