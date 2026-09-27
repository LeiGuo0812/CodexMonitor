using CodexQuotaMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexQuotaMonitor.App;

// Each row owns its controls for the lifetime of the open details window.
// Rows are indexed, so duplicate IDs and equal expiry times remain separate cards.
internal sealed class CreditRow
{
    public Border Root { get; }
    private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _validity = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.78, FontSize = 12 };
    private ResetCredit? _credit;
    private int _number;
    private (long Countdown, DateTime Day, TimeZoneInfo Zone)? _timeKey;

    public CreditRow()
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(_title);
        content.Children.Add(_validity);
        Root = new Border { Child = content, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10) };
    }

    public void Update(ResetCredit credit, int number, DateTimeOffset now)
    {
        if (_credit != credit || _number != number)
        {
            _credit = credit;
            _number = number;
            _timeKey = null;
            UiUpdates.Text(_title, string.IsNullOrWhiteSpace(credit.Title) ? $"重置卡 {number}" : $"{credit.Title} · {number}");
        }
        UpdateTime(now);
    }

    public void UpdateTime(DateTimeOffset now)
    {
        if (_credit is null) return;
        var key = (QuotaFormatting.CountdownKey(_credit.ExpiresAt, now), now.Date, TimeZoneInfo.Local);
        if (_timeKey == key) return;
        _timeKey = key;
        UiUpdates.Text(_validity, QuotaFormatting.FormatCreditValidity(_credit, now));
        UiUpdates.Tooltip(Root, $"到期：{DashboardWindow.ExactDateTooltip(_credit.ExpiresAt)}\n发放：{DashboardWindow.ExactDateTooltip(_credit.GrantedAt)}");
    }
}

internal sealed class QuotaRow
{
    public Border Root { get; }
    private readonly TextBlock _name = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _remaining = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _time = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
    private readonly TextBlock _restriction = new() { TextWrapping = TextWrapping.Wrap };
    private QuotaWindow? _window;
    private (long Countdown, DateTime Day, TimeZoneInfo Zone)? _timeKey;

    public QuotaRow()
    {
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(_remaining, 1);
        header.Children.Add(_name);
        header.Children.Add(_remaining);
        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(header);
        content.Children.Add(_time);
        content.Children.Add(_restriction);
        Root = new Border { Child = content, Padding = new Thickness(14, 11, 14, 11), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1) };
    }

    public void Update(QuotaWindow window, DateTimeOffset now)
    {
        if (_window != window)
        {
            _window = window;
            _timeKey = null;
            UiUpdates.Text(_name, window.Name);
            UiUpdates.Text(_remaining, window.RemainingLabel);
            UiUpdates.Text(_restriction, window.RateLimitReachedType is null ? "" : DashboardWindow.RestrictionLabel(window.RateLimitReachedType));
            _restriction.Visibility = window.RateLimitReachedType is null ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateTime(now);
    }

    public void UpdateTime(DateTimeOffset now)
    {
        if (_window is null) return;
        var key = (QuotaFormatting.CountdownKey(_window.ResetsAt, now), now.Date, TimeZoneInfo.Local);
        if (_timeKey == key) return;
        _timeKey = key;
        UiUpdates.Text(_time, $"{QuotaFormatting.FormatTimeRemaining(_window.ResetsAt, now)} · {QuotaFormatting.FormatReadableDate(_window.ResetsAt, now)} 重置");
    }
}
