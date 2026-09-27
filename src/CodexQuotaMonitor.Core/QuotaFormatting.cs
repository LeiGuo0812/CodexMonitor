using System.Globalization;

namespace CodexQuotaMonitor.Core;

public static class QuotaFormatting
{
    public static string FormatCreditValidity(ResetCredit credit, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var status = credit.Status?.ToLowerInvariant() switch
        {
            "available" or "active" => "可用", "expired" => "已到期",
            "used" or "consumed" => "已使用", null or "" => "状态未知", _ => credit.Status
        };
        if (credit.ExpiresAt is not { } expiry) return $"到期时间未知 · {status}";
        var date = FormatReadableDate(expiry, now, zone);
        if (status == "已使用") return $"已使用 · 到期时间：{date}";
        if (expiry <= now || status == "已到期") return $"已到期 · {date}";
        return status == "可用"
            ? $"{FormatTimeRemaining(expiry, now)}到期\n{date} 前可用"
            : $"{FormatTimeRemaining(expiry, now)}到期\n{date} · {status}";
    }

    public static string FormatReadableDate(DateTimeOffset? value, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        if (value is null) return "时间未知";
        var zone = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(value.Value, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var days = (local.Date - today).Days;
        var day = days switch
        {
            0 => "今天", 1 => "明天", 2 => "后天", -1 => "昨天",
            _ => local.Year == today.Year ? $"{local.Month}月{local.Day}日" : $"{local.Year}年{local.Month}月{local.Day}日"
        };
        string[] weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
        return $"{day}（{weekdays[(int)local.DayOfWeek]}）{local:HH:mm}";
    }

    public static string FormatTimeRemaining(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null) return "重置时间未知";
        var remaining = resetAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "等待确认重置";
        if (remaining.TotalMinutes < 1) return "不到 1 分钟";
        if (remaining.TotalHours < 1) return $"还剩 {Math.Ceiling(remaining.TotalMinutes):0} 分钟";
        if (remaining.TotalDays < 1)
            return remaining.Minutes == 0 ? $"还剩 {(int)remaining.TotalHours} 小时" : $"还剩 {(int)remaining.TotalHours} 小时 {remaining.Minutes} 分钟";
        return remaining.Hours == 0 ? $"还剩 {(int)remaining.TotalDays} 天" : $"还剩 {(int)remaining.TotalDays} 天 {remaining.Hours} 小时";
    }

    public static string FormatUpdatedAgo(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return "尚未成功更新";
        var elapsed = now - value.Value;
        if (elapsed.TotalSeconds < 60) return "刚刚更新";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} 分钟前更新";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} 小时前更新";
        return $"更新于 {FormatReadableDate(value, now)}";
    }

    // Time remaining as a fraction of the server's declared period, independent of quota usage.
    public static double? ResetTimeRemainingPercent(QuotaWindow? window, DateTimeOffset now)
    {
        if (window?.ResetsAt is not { } resetAt || window.WindowDurationMinutes is not > 0) return null;
        var minutes = (resetAt - now).TotalMinutes;
        if (minutes > window.WindowDurationMinutes.Value) return null;
        return Math.Max(0, minutes / window.WindowDurationMinutes.Value * 100);
    }

    public static string FormatCountdown(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null) return "重置时间未知";
        var remaining = resetAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "等待确认重置";
        if (remaining < TimeSpan.FromMinutes(1)) return "<1分钟";
        if (remaining < TimeSpan.FromHours(1))
        {
            var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            return $"{minutes}分钟";
        }
        if (remaining < TimeSpan.FromDays(1))
        {
            var hours = (int)Math.Floor(remaining.TotalHours);
            var minutes = remaining.Minutes;
            return minutes == 0 ? $"{hours}小时" : $"{hours}小时{minutes}分";
        }

        var days = (int)Math.Floor(remaining.TotalDays);
        var hoursLeft = remaining.Hours;
        return hoursLeft == 0 ? $"{days}天" : $"{days}天{hoursLeft}小时";
    }

    public static string FormatFullLocalDate(DateTimeOffset? value, CultureInfo? culture = null)
        => FormatFullDate(value, TimeZoneInfo.Local, culture);

    public static string FormatFullDate(
        DateTimeOffset? value,
        TimeZoneInfo timeZone,
        CultureInfo? culture = null)
    {
        if (value is null) return "重置时间未知";
        var local = TimeZoneInfo.ConvertTime(value.Value, timeZone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return $"{local.ToString("yyyy-MM-dd HH:mm:ss", culture ?? CultureInfo.InvariantCulture)}（UTC{sign}{(int)absolute.TotalHours:00}:{absolute.Minutes:00}）";
    }
}
