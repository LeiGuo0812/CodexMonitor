#include "core.h"
#include <bcrypt.h>
#include <cwctype>
#include <iomanip>
#include <sstream>
namespace cqm
{
std::wstring wide(const std::string &s)
{
    if (s.empty())
        return {};
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0);
    if (!n)
        return {};
    std::wstring r(n, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), r.data(), n);
    return r;
}
std::string utf8(const std::wstring &s)
{
    if (s.empty())
        return {};
    int n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0, nullptr,
                                nullptr);
    if (!n)
        return {};
    std::string r(n, 0);
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, s.data(), (int)s.size(), r.data(), n, nullptr,
                        nullptr);
    return r;
}
Seconds now()
{
    return std::chrono::duration_cast<std::chrono::seconds>(
               std::chrono::system_clock::now().time_since_epoch())
        .count();
}
static std::optional<Seconds> timestamp(const Json &j, const char *k)
{
    if (!j.is_object() || !j.contains(k) || !j[k].is_number_integer())
        return {};
    if (j[k].is_number_unsigned() && j[k].get<uint64_t>() > 253402300799ULL)
        return {};
    auto n = j[k].get<Seconds>();
    if (n < -62135596800LL || n > 253402300799LL)
        return {};
    return n;
}
static std::optional<int> integer(const Json &j, const char *k)
{
    if (!j.is_object() || !j.contains(k) || !j[k].is_number_integer())
        return {};
    if (j[k].is_number_unsigned() && j[k].get<uint64_t>() > INT_MAX)
        return {};
    auto n = j[k].get<int64_t>();
    if (n < INT_MIN || n > INT_MAX)
        return {};
    return (int)n;
}
static std::string str(const Json &j, const char *k)
{
    return j.is_object() && j.contains(k) && j[k].is_string() ? j[k].get<std::string>() : "";
}
std::wstring countdown(std::optional<Seconds> t, Seconds current)
{
    if (!t)
        return L"重置时间未知";
    auto d = *t - current;
    if (d <= 0)
        return L"等待确认重置";
    if (d < 60)
        return L"<1分钟";
    if (d < 3600)
        return std::to_wstring((d + 59) / 60) + L"分钟";
    if (d < 86400)
        return std::to_wstring(d / 3600) + L"小时" +
               (d % 3600 / 60 ? std::to_wstring(d % 3600 / 60) + L"分" : L"");
    return std::to_wstring(d / 86400) + L"天" +
           (d % 86400 / 3600 ? std::to_wstring(d % 86400 / 3600) + L"小时" : L"");
}
static bool calendar(Seconds t, SYSTEMTIME &s, bool utc)
{
    if (t < -11644473600LL || t > 253402300799LL)
        return false;
    ULARGE_INTEGER n{};
    n.QuadPart = (t + 11644473600LL) * 10000000ULL;
    FILETIME f{n.LowPart, n.HighPart};
    SYSTEMTIME u{};
    if (!FileTimeToSystemTime(&f, &u))
        return false;
    if (utc)
    {
        s = u;
        return true;
    }
    return SystemTimeToTzSpecificLocalTime(nullptr, &u, &s) != FALSE;
}
std::wstring readable(std::optional<Seconds> t, bool utc)
{
    if (!t)
        return L"时间未知";
    SYSTEMTIME s{}, today{};
    if (!calendar(*t, s, utc))
        return L"时间未知";
    calendar(now(), today, utc);
    const wchar_t *days[] = {L"周日", L"周一", L"周二", L"周三", L"周四", L"周五", L"周六"};
    auto midnight = [](SYSTEMTIME value)
    {
        value.wHour = value.wMinute = value.wSecond = value.wMilliseconds = 0;
        FILETIME ft{};
        SystemTimeToFileTime(&value, &ft);
        ULARGE_INTEGER n{};
        n.LowPart = ft.dwLowDateTime;
        n.HighPart = ft.dwHighDateTime;
        return (int64_t)n.QuadPart;
    };
    auto delta = (midnight(s) - midnight(today)) / 864000000000LL;
    std::wstring d;
    if (delta == 0)
        d = L"今天";
    else if (delta == 1)
        d = L"明天";
    else if (delta == 2)
        d = L"后天";
    else if (delta == -1)
        d = L"昨天";
    else
    {
        if (s.wYear != today.wYear)
            d = std::to_wstring(s.wYear) + L"年";
        d += std::to_wstring(s.wMonth) + L"月" + std::to_wstring(s.wDay) + L"日";
    }
    wchar_t b[30]{};
    swprintf_s(b, L"（%s）%02u:%02u", days[s.wDayOfWeek], s.wHour, s.wMinute);
    return d + b + (utc ? L" UTC" : L"");
}
std::wstring fullDate(std::optional<Seconds> t, bool utc)
{
    if (!t)
        return L"时间未知";
    SYSTEMTIME s{}, u{};
    if (!calendar(*t, s, utc) || !calendar(*t, u, true))
        return L"时间未知";
    FILETIME a{}, b{};
    SystemTimeToFileTime(&s, &a);
    SystemTimeToFileTime(&u, &b);
    ULARGE_INTEGER x{}, y{};
    x.LowPart = a.dwLowDateTime;
    x.HighPart = a.dwHighDateTime;
    y.LowPart = b.dwLowDateTime;
    y.HighPart = b.dwHighDateTime;
    int bias = (int)(((int64_t)x.QuadPart - (int64_t)y.QuadPart) / 600000000LL);
    wchar_t text[90]{};
    swprintf_s(text, L"%04u-%02u-%02u %02u:%02u:%02u（UTC%c%02d:%02d）", s.wYear, s.wMonth, s.wDay, s.wHour,
               s.wMinute, s.wSecond, bias < 0 ? L'-' : L'+', abs(bias) / 60, abs(bias) % 60);
    return text;
}
std::optional<double> Quota::remaining() const
{
    return used && std::isfinite(*used) && *used >= 0 && *used <= 100 ? std::optional<double>(100 - *used)
                                                                      : std::nullopt;
}
std::optional<double> Quota::timePercent() const
{
    if (!reset || !minutes || *minutes <= 0)
        return {};
    double v = 100.0 * (*reset - now()) / (*minutes * 60.0);
    if (v > 100)
        return {};
    return std::max(0.0, v);
}
std::wstring Quota::label() const
{
    auto v = remaining();
    if (!v)
        return L"剩余 --";
    if (*v > 0 && *v < 1)
        return L"剩余 <1%";
    return L"剩余 " + std::to_wstring((int)std::round(*v)) + L"%";
}
std::wstring Credit::validity() const
{
    auto st = status;
    std::transform(st.begin(), st.end(), st.begin(), towlower);
    std::wstring label = st == L"available" || st == L"active" ? L"可用"
                         : st == L"expired"                    ? L"已到期"
                         : st == L"used" || st == L"consumed"  ? L"已使用"
                         : st.empty()                          ? L"状态未知"
                                                               : status;
    if (!expires)
        return L"到期时间未知 · " + label;
    if (label == L"已使用")
        return L"已使用 · " + readable(expires);
    if (*expires <= now() || label == L"已到期")
        return L"已到期 · " + readable(expires);
    return countdown(expires) + L"后到期\n" + readable(expires) +
           (label == L"可用" ? L" 前可用" : L" · " + label);
}
std::string fingerprint(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(),
                   [](unsigned char c) { return (char)tolower(c); });
    unsigned char bytes[32]{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, (PUCHAR)value.data(), (ULONG)value.size(), bytes,
                   32) < 0)
        return {};
    const char *hex = "0123456789abcdef";
    std::string out;
    for (auto c : bytes)
    {
        out += hex[c >> 4];
        out += hex[c & 15];
    }
    return out;
}
Query parseReplies(const Json &accountReply, const Json &limits)
{
    Query q;
    q.retrieved = now();
    if (accountReply.contains("error"))
    {
        q.error = "ACCOUNT_ERROR";
        return q;
    }
    auto a = accountReply.value("result", Json::object()).value("account", Json());
    if (!a.is_object() || str(a, "type") != "chatgpt")
    {
        q.error = "CHATGPT_AUTH_REQUIRED";
        return q;
    }
    auto email = str(a, "email");
    if (!email.empty())
        q.account = fingerprint("chatgpt\n" + email);
    q.plan = str(a, "planType");
    if (limits.contains("error"))
    {
        q.error = "RATE_LIMIT_UNSUPPORTED";
        return q;
    }
    auto r = limits.value("result", Json());
    if (!r.is_object())
    {
        q.error = "RATE_LIMIT_RESPONSE_MISSING";
        return q;
    }
    Json buckets;
    if (r.contains("rateLimitsByLimitId") && r["rateLimitsByLimitId"].is_object())
        buckets = r["rateLimitsByLimitId"];
    else if (r.contains("rateLimits") && r["rateLimits"].is_object())
        buckets = Json{{"codex", r["rateLimits"]}};
    else
    {
        q.error = "RATE_LIMIT_FIELDS_UNAVAILABLE";
        return q;
    }
    for (auto &[id, bucket] : buckets.items())
        for (auto field : {"primary", "secondary"})
        {
            if (!bucket.is_object() || !bucket.contains(field) || !bucket[field].is_object())
                continue;
            auto &w = bucket[field];
            Quota v;
            v.key = id + "." + field;
            v.minutes = integer(w, "windowDurationMins");
            v.reset = timestamp(w, "resetsAt");
            v.restriction = str(bucket, "rateLimitReachedType");
            std::string semantics = id + " " + str(bucket, "limitName");
            std::transform(semantics.begin(), semantics.end(), semantics.begin(),
                           [](unsigned char c) { return (char)tolower(c); });
            if (v.minutes == 300)
                v.kind = Kind::FiveHour;
            else if (v.minutes == 10080)
                v.kind = Kind::Week;
            else if (semantics.find("5h") != std::string::npos ||
                     semantics.find("five_hour") != std::string::npos ||
                     semantics.find("five-hour") != std::string::npos)
                v.kind = Kind::FiveHour;
            else if (semantics.find("week") != std::string::npos || semantics.find("周") != std::string::npos)
                v.kind = Kind::Week;
            else
                v.kind = v.minutes ? Kind::Other : Kind::Unknown;
            v.name = v.kind == Kind::FiveHour ? L"5 小时"
                     : v.kind == Kind::Week   ? L"周额度"
                                              : wide(id + " · " + field);
            if (w.contains("usedPercent") && w["usedPercent"].is_number())
            {
                double p = w["usedPercent"].get<double>();
                if (std::isfinite(p) && p >= 0 && p <= 100)
                    v.used = p;
            }
            q.windows.push_back(v);
        }
    if (r.contains("rateLimitResetCredits") && r["rateLimitResetCredits"].is_object())
    {
        auto &c = r["rateLimitResetCredits"];
        q.creditCount = integer(c, "availableCount");
        if (q.creditCount && *q.creditCount < 0)
            q.creditCount.reset();
        q.creditDetails = Query::CountOnly;
        if (c.contains("credits") && !c["credits"].is_null())
        {
            if (c["credits"].is_array())
            {
                for (auto &row : c["credits"])
                {
                    if (!row.is_object())
                        continue;
                    Credit v;
                    v.id = wide(str(row, "id"));
                    v.title = wide(str(row, "title"));
                    v.description = wide(str(row, "description"));
                    v.type = wide(str(row, "resetType"));
                    v.status = wide(str(row, "status"));
                    v.granted = timestamp(row, "grantedAt");
                    v.expires = timestamp(row, "expiresAt");
                    q.credits.push_back(v);
                }
                q.creditDetails = q.creditCount && (int)q.credits.size() < *q.creditCount ? Query::Partial
                                                                                          : Query::Complete;
                std::stable_sort(q.credits.begin(), q.credits.end(), [](auto &l, auto &r)
                                 { return l.expires.value_or(INT64_MAX) < r.expires.value_or(INT64_MAX); });
            }
            else
                q.creditDetails = Query::Unavailable;
        }
    }
    q.success = true;
    return q;
}
void State::apply(Query q)
{
    bool changed = !account.empty() && !q.account.empty() && account != q.account;
    if (changed || (q.success && q.account.empty()) || q.error == "CHATGPT_AUTH_REQUIRED" ||
        (!q.success && account.empty()))
    {
        selected.reset();
        rememberedFive.reset();
        data = {};
        updated = 0;
        creditsUpdated = 0;
        creditDetailsStale = false;
        account.clear();
    }
    if (!q.account.empty())
        account = q.account;
    error = q.success && q.account.empty() ? "ACCOUNT_IDENTITY_UNAVAILABLE" : q.error;
    refreshing = false;
    missingFive = false;
    if (!q.success)
    {
        stale = selected.has_value();
        creditDetailsStale = !data.credits.empty();
        return;
    }
    // A null detail list is not a failed read or an authoritative empty list.
    // Reuse only the same known account's details with an unchanged known count.
    bool reuseCredits = q.creditDetails == Query::CountOnly && !q.account.empty() &&
                        q.account == data.account && q.creditCount && q.creditCount == data.creditCount &&
                        !data.credits.empty();
    creditDetailsStale = reuseCredits;
    if (reuseCredits)
        q.credits = data.credits;
    else
        creditsUpdated =
            (q.creditDetails == Query::Complete || q.creditDetails == Query::Partial) ? q.retrieved : 0;
    data = std::move(q);
    updated = data.retrieved;
    stale = false;
    auto five = find(Kind::FiveHour);
    if (five)
    {
        rememberedFive = five;
        selected = five;
        return;
    }
    if (rememberedFive)
    {
        selected = rememberedFive;
        stale = true;
        missingFive = true;
        return;
    }
    selected = find(Kind::Week);
}
std::optional<Quota> State::find(Kind k) const
{
    for (auto &q : data.windows)
        if (q.kind == k)
            return q;
    return {};
}
bool shouldSnapToTaskbar(RECT widget, RECT bar, float scale)
{
    int width = std::min(widget.right, bar.right) - std::max(widget.left, bar.left);
    int height = std::min(widget.bottom, bar.bottom) - std::max(widget.top, bar.top);
    if (width <= 0 || height <= 0 || !std::isfinite(scale) || scale <= 0)
        return false;
    bool horizontal = bar.right - bar.left >= bar.bottom - bar.top;
    int barDepth = horizontal ? bar.bottom - bar.top : bar.right - bar.left;
    int widgetDepth = horizontal ? widget.bottom - widget.top : widget.right - widget.left;
    float threshold = std::min({8.f * scale, barDepth / 2.f, widgetDepth / 2.f});
    return (horizontal ? height : width) >= threshold;
}
bool validColor(const std::wstring &v)
{
    return v.size() == 7 && v[0] == L'#' &&
           std::all_of(v.begin() + 1, v.end(), [](wchar_t c) { return iswxdigit(c) != 0; });
}
void Settings::normalize()
{
    if (position < 0 || position > 2)
        position = 0;
    if (mode < 0 || mode > 2)
        mode = 0;
    if (preset < 0 || preset > 4)
        preset = 0;
    widgetOpacity = std::isfinite(widgetOpacity) ? std::clamp(widgetOpacity, .4, 1.) : .92;
    detailsOpacity = std::isfinite(detailsOpacity) ? std::clamp(detailsOpacity, .4, 1.) : .96;
    font = std::isfinite(font) ? std::clamp(font, 13., 18.) : 15.;
    horizontal = std::clamp(horizontal, -4000, 0);
    vertical = std::clamp(vertical, 0, 160);
    refresh = std::clamp(refresh, 30, 3600);
    for (auto value : {&background, &primary, &secondary, &accent})
        if (value->size() == 6 &&
            std::all_of(value->begin(), value->end(), [](wchar_t c) { return iswxdigit(c) != 0; }))
            *value = L"#" + *value;
    if (!validColor(background))
        background = L"#E8EDF3";
    if (!validColor(primary))
        primary = L"#17212D";
    if (!validColor(secondary))
        secondary = L"#526174";
    if (!validColor(accent))
        accent = L"#3B73C5";
}
Settings Settings::fromJson(const Json &j)
{
    Settings s;
    if (!j.is_object())
        return s;
    auto number = [&](const char *k, double d)
    {
        if (!j.contains(k) || !j[k].is_number())
            return d;
        double v = j[k].get<double>();
        return std::isfinite(v) && v >= INT_MIN && v <= INT_MAX ? v : d;
    };
    auto text = [&](const char *k, std::wstring d)
    { return j.contains(k) && j[k].is_string() ? wide(j[k].get<std::string>()) : d; };
    s.codex = text("CodexExecutablePath", L"");
    s.position = (int)number("PositionMode", 0);
    s.mode = (int)number("ThemeMode", 0);
    s.preset = (int)number("ThemePreset", 0);
    s.background = text("CustomBackground", s.background);
    s.primary = text("CustomPrimaryText", s.primary);
    s.secondary = text("CustomSecondaryText", s.secondary);
    s.accent = text("CustomAccent", s.accent);
    s.widgetOpacity = number("WidgetOpacity", .92);
    s.detailsOpacity = number("DetailsOpacity", .96);
    s.font = number("FontSize", 15);
    s.horizontal = (int)number("HorizontalOffsetDip", -360);
    s.vertical = (int)number("VerticalOffsetDip", 12);
    s.refresh = (int)number("RefreshIntervalSeconds", 60);
    s.startup = j.contains("StartWithWindows") && j["StartWithWindows"].is_boolean()
                    ? j["StartWithWindows"].get<bool>()
                    : false;
    s.normalize();
    return s;
}
Json Settings::toJson() const
{
    return {{"CodexExecutablePath", codex.empty() ? Json() : Json(utf8(codex))},
            {"PositionMode", position},
            {"ThemeMode", mode},
            {"ThemePreset", preset},
            {"CustomBackground", utf8(background)},
            {"CustomPrimaryText", utf8(primary)},
            {"CustomSecondaryText", utf8(secondary)},
            {"CustomAccent", utf8(accent)},
            {"WidgetOpacity", widgetOpacity},
            {"DetailsOpacity", detailsOpacity},
            {"FontSize", font},
            {"HorizontalOffsetDip", horizontal},
            {"VerticalOffsetDip", vertical},
            {"RefreshIntervalSeconds", refresh},
            {"StartWithWindows", startup}};
}
std::wstring errorLabel(const std::string &e)
{
    if (e.empty())
        return L"已连接 Codex";
    if (e == "ACCOUNT_IDENTITY_UNAVAILABLE")
        return L"账户身份未确认，不复用旧账户缓存";
    if (e == "CHATGPT_AUTH_REQUIRED")
        return L"请在 Codex 中登录订阅账户";
    if (e == "CODEX_NOT_FOUND")
        return L"未找到 Codex CLI，请在设置中选择";
    if (e == "CODEX_NOT_A_CLI")
        return L"所选文件不是有效的 Codex CLI";
    if (e == "TIMEOUT")
        return L"查询超时，稍后重试";
    return L"连接异常 · " + wide(e);
}
} // namespace cqm
