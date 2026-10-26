#include "core.h"
#include <iostream>
using namespace cqm;
static unsigned checks = 0;
static void require(bool condition, const char *name)
{
    checks++;
    if (!condition)
        throw std::runtime_error(name);
}
static Query query(std::string account, std::vector<Quota> quotas)
{
    Query q;
    q.success = true;
    q.account = account;
    q.windows = std::move(quotas);
    q.retrieved = now();
    return q;
}
static Quota window(Kind kind, double used)
{
    Quota q;
    q.key = kind == Kind::FiveHour ? "five" : "week";
    q.name = kind == Kind::FiveHour ? L"5 小时" : L"周额度";
    q.kind = kind;
    q.used = used;
    q.reset = now() + 3600;
    q.minutes = kind == Kind::FiveHour ? 300 : 10080;
    return q;
}
int main()
{
    try
    {
        const auto five = window(Kind::FiveHour, 100), week = window(Kind::Week, 20);
        RECT bar{0, 1000, 1920, 1048};
        for (auto name :
             {L"Explorer.EXE", L"EXPLORER.exe", L"ShellExperienceHost.exe", L"startmenuexperiencehost.EXE",
              L"SearchHost.exe", L"SearchApp.exe", L"TextInputHost.exe"})
            require(isShellExecutableName(name), "Shell executable names are case insensitive");
        for (auto name : {L"chrome.exe", L"explorer.exe.bak", L"myexplorer.exe"})
            require(!isShellExecutableName(name), "Ordinary apps are not shell hosts");
        for (int left : {0, 700, 1650})
            require(shouldSnapToTaskbar({left, 968, left + 200, 1008}, bar, 1),
                    "Eight DIP depth snaps at left, centre and right");
        require(!shouldSnapToTaskbar({0, 967, 200, 1007}, bar, 1), "Shallow overlap does not snap");
        require(!shouldSnapToTaskbar({1930, 980, 2130, 1020}, bar, 1), "Outside taskbar does not snap");
        require(shouldSnapToTaskbar({0, 946, 350, 1014}, bar, 1.75f), "Snap depth scales with DPI");
        require(!shouldSnapToTaskbar({0, 946, 350, 1013}, bar, 1.75f), "Scaled depth threshold respected");
        State state;
        state.apply(query("a", {week, five}));
        require(state.selected && state.selected->kind == Kind::FiveHour,
                "Prefer five hours independent of order");
        require(state.selected->label() == L"剩余 0%", "Exhausted five hours remains selected");
        state.apply(query("a", {week}));
        require(state.missingFive && state.stale && state.selected->kind == Kind::FiveHour,
                "Missing five hours retained as stale");
        Query fail;
        fail.error = "TIMEOUT";
        state.apply(fail);
        require(state.selected && state.stale, "Request failure preserves known account data");
        state.apply(query("b", {week}));
        require(state.selected->kind == Kind::Week && !state.stale && !state.rememberedFive,
                "Account switch clears five hours");
        fail.error = "CHATGPT_AUTH_REQUIRED";
        state.apply(fail);
        require(!state.selected && state.data.windows.empty() && !state.updated,
                "Logout clears all previous data");
        state.apply(query("a", {five}));
        state.apply(query("", {week}));
        require(state.selected->kind == Kind::Week && state.error == "ACCOUNT_IDENTITY_UNAVAILABLE",
                "Unknown identity does not inherit previous selection");
        state.apply(fail);
        require(!state.selected, "Failure after unknown identity clears cache");
        state.apply(query("a", {five}));
        fail.account = "b";
        fail.error = "RATE_LIMIT_UNSUPPORTED";
        state.apply(fail);
        require(!state.selected && !state.updated && state.data.credits.empty(),
                "Changed account with failed limits clears cached data");
        auto tiny = window(Kind::FiveHour, 99.5);
        require(tiny.label() == L"剩余 <1%", "Small nonzero quota distinguished");
        tiny.used.reset();
        require(tiny.label() == L"剩余 --", "Unknown quota distinct from zero");
        Seconds time = 1790900000;
        for (auto [delta, label] : std::vector<std::pair<Seconds, std::wstring>>{{-1, L"等待确认重置"},
                                                                                 {0, L"等待确认重置"},
                                                                                 {30, L"<1分钟"},
                                                                                 {60, L"1分钟"},
                                                                                 {1380, L"23分钟"},
                                                                                 {8040, L"2小时14分"},
                                                                                 {280800, L"3天6小时"}})
            require(countdown(time + delta, time) == label, "Countdown boundary");
        require(countdown({}, time) == L"重置时间未知", "Unknown reset not inferred");
        tiny.reset = now() - 1;
        tiny.minutes = 300;
        require(tiny.timePercent() == 0, "Expired reset progress stays zero");
        tiny.reset = now() + 18001;
        require(!tiny.timePercent(), "Out of period countdown is unknown");
        tiny.minutes.reset();
        require(!tiny.timePercent(), "Missing duration cannot create progress");
        Json account = {
            {"result",
             {{"account", {{"type", "chatgpt"}, {"email", "test@example.invalid"}, {"planType", "pro"}}}}}};
        Json limits = Json::parse(
            R"({"result":{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":1791073118},"secondary":{"usedPercent":99.5,"windowDurationMins":300,"resetsAt":1790900000}},"other":{"primary":{"usedPercent":101,"windowDurationMins":60,"resetsAt":1791073118000}}},"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300}},"rateLimitResetCredits":{"availableCount":4,"credits":[{"id":"a","status":"available","expiresAt":1991000000},{"id":"b","status":"available","expiresAt":1991000000},{"id":"c","status":"active","expiresAt":null}]}}})");
        auto parsed = parseReplies(account, limits);
        State creditState;
        creditState.refreshing = true;
        creditState.apply(parsed);
        require(!creditState.refreshing, "Successful completion clears refreshing state");
        auto countOnly = parsed;
        countOnly.credits.clear();
        countOnly.creditDetails = Query::CountOnly;
        creditState.apply(countOnly);
        require(creditState.data.credits.size() == 3 && creditState.creditDetailsStale,
                "Count-only response preserves same-account same-count details as stale");
        countOnly.creditCount = 2;
        creditState.apply(countOnly);
        require(creditState.data.credits.empty() && !creditState.creditDetailsStale,
                "Changed count does not retain potentially spent credits");
        creditState.apply(parsed);
        countOnly.account = "different";
        countOnly.creditCount = parsed.creditCount;
        creditState.apply(countOnly);
        require(creditState.data.credits.empty() && !creditState.creditsUpdated,
                "Changed account does not inherit credit details");
        creditState.apply(parsed);
        auto emptyDetails = parsed;
        emptyDetails.credits.clear();
        emptyDetails.creditDetails = Query::Partial;
        creditState.apply(emptyDetails);
        require(creditState.data.credits.empty() && !creditState.creditDetailsStale,
                "Explicit empty credit list clears cached details");
        creditState.refreshing = true;
        creditState.apply(fail);
        require(!creditState.refreshing, "Failed completion also clears refreshing state");
        require(parsed.success && parsed.windows.size() == 3, "Prefer complete bucket map over legacy");
        require(parsed.windows[0].kind == Kind::Week && parsed.windows[1].kind == Kind::FiveHour,
                "Duration determines kind");
        require(!parsed.windows[2].used && !parsed.windows[2].reset,
                "Invalid percent and milliseconds rejected");
        require(parsed.creditCount == 4 && parsed.credits.size() == 3 &&
                    parsed.creditDetails == Query::Partial,
                "Credit count independent of details length");
        require(parsed.credits[0].expires == parsed.credits[1].expires && !parsed.credits[2].expires,
                "Duplicate expiry retained and unknown sorted last");
        require(parsed.credits[0].validity().find(L"前可用") != std::wstring::npos,
                "Credit wording retains pre-expiry availability");
        require(parsed.credits[2].validity().find(L"到期时间未知") != std::wstring::npos,
                "Unknown expiry not permanent");
        for (auto value : {Json(-1), Json(101), Json("50"), Json()})
        {
            auto changed = limits;
            changed["result"]["rateLimitsByLimitId"]["codex"]["primary"]["usedPercent"] = value;
            require(!parseReplies(account, changed).windows[0].used, "Invalid percent is unknown");
        }
        auto changed = limits;
        changed["result"]["rateLimitsByLimitId"]["codex"]["primary"]["resetsAt"] = UINT64_MAX;
        require(!parseReplies(account, changed).windows[0].reset, "Unsigned timestamp overflow rejected");
        changed = limits;
        changed["result"]["rateLimitResetCredits"] = {{"availableCount", 0}, {"credits", Json::array()}};
        parsed = parseReplies(account, changed);
        require(parsed.creditCount == 0 && parsed.creditDetails == Query::Complete, "Zero credits supported");
        changed["result"]["rateLimitResetCredits"] = {{"availableCount", 3}};
        parsed = parseReplies(account, changed);
        require(parsed.creditCount == 3 && parsed.creditDetails == Query::CountOnly,
                "Count without details supported");
        changed["result"]["rateLimitResetCredits"] = {{"availableCount", -1}, {"credits", nullptr}};
        parsed = parseReplies(account, changed);
        require(!parsed.creditCount, "Negative count unknown");
        changed["result"]["rateLimitResetCredits"] = {{"availableCount", UINT64_MAX}};
        require(!parseReplies(account, changed).creditCount, "Credit count overflow rejected");
        require(!parseReplies(account, Json{{"error", {{"code", -1}}}}).success,
                "RPC error not empty success");
        require(!parseReplies(account, Json{{"result", Json::object()}}).success,
                "Missing rate fields not account absence");
        auto missingAccount = account;
        missingAccount["result"]["account"] = nullptr;
        require(parseReplies(missingAccount, limits).error == "CHATGPT_AUTH_REQUIRED",
                "Null account requires login");
        auto modern = limits;
        modern["result"]["accountId"] = "Account-A";
        modern["result"]["rateLimits"]["planType"] = "pro";
        auto snapshot = parseReplies(Json(), modern);
        require(snapshot.success && !snapshot.account.empty() && snapshot.plan == "pro" &&
                    snapshot.windows.size() == 3 && snapshot.creditCount == 4,
                "Authenticated usage snapshot works without a profile request");
        Json profileError = {
            {"error", {{"code", -32603}, {"message", "workspace routing discovery timed out"}}}};
        require(parseReplies(profileError, modern).success,
                "Workspace routing profile failure does not discard authenticated usage");
        require(parseReplies(account, modern).account == snapshot.account,
                "Snapshot identity is consistent with and without a profile");
        auto nullableProfile = account;
        nullableProfile["result"]["account"]["email"] = nullptr;
        nullableProfile["result"]["account"]["planType"] = nullptr;
        auto nullable = parseReplies(nullableProfile, modern);
        require(nullable.success && nullable.account == snapshot.account && nullable.plan == "pro",
                "Nullable profile email and plan use snapshot identity and plan");
        modern["result"]["rateLimits"]["planType"] = nullptr;
        nullable = parseReplies(Json(), modern);
        require(nullable.success && nullable.plan.empty(), "Nullable snapshot plan is safe");
        require(parseReplies(missingAccount, modern).error == "CHATGPT_AUTH_REQUIRED",
                "Explicit logout overrides a usage snapshot");
        auto apiKey = account;
        apiKey["result"]["account"] = {{"type", "apiKey"}};
        require(parseReplies(apiKey, modern).error == "CHATGPT_AUTH_REQUIRED",
                "Explicit non-subscription auth cannot reuse subscription usage");
        require(parseReplies(Json(), limits).error == "ACCOUNT_IDENTITY_UNAVAILABLE",
                "Legacy usage requires the separate profile fallback");
        require(parseReplies(account, limits).account == fingerprint("chatgpt\ntest@example.invalid"),
                "Legacy email-based account identity remains compatible");
        require(parseReplies(profileError, limits).error == "ACCOUNT_ERROR",
                "Profile error without snapshot identity remains a failed query");
        auto failedUsage = Json{{"error", {{"code", -32603}}}};
        auto identifiedFailure = parseReplies(account, failedUsage);
        require(!identifiedFailure.success &&
                    identifiedFailure.account == fingerprint("chatgpt\ntest@example.invalid"),
                "Usage error preserves a known profile identity for account isolation");
        require(parseReplies(missingAccount, failedUsage).error == "CHATGPT_AUTH_REQUIRED",
                "Usage failure still honors an explicit signed-out profile");
        for (auto id : {Json(), Json(42), Json::object(), Json(""), Json(" \t\r\n")})
        {
            auto invalid = modern;
            invalid["result"]["accountId"] = id;
            require(parseReplies(Json(), invalid).error == "ACCOUNT_IDENTITY_UNAVAILABLE",
                    "Invalid snapshot identity requires profile fallback");
        }
        State snapshotState;
        snapshotState.apply(snapshot);
        auto anotherAccount = modern;
        anotherAccount["result"]["accountId"] = "account-a";
        anotherAccount["result"]["rateLimitResetCredits"]["credits"] = nullptr;
        auto switchedSnapshot = parseReplies(Json(), anotherAccount);
        require(switchedSnapshot.account != snapshot.account, "Opaque snapshot identities preserve case");
        snapshotState.apply(switchedSnapshot);
        require(snapshotState.data.credits.empty() && !snapshotState.creditDetailsStale,
                "A changed snapshot account never inherits reset credit details");
        Settings s;
        require(s.refresh == 60, "Default refresh is one minute");
        s.position = 1;
        s.preset = 4;
        s.refresh = 90;
        s.font = 18;
        s.codex = L"C:\\测试 路径\\codex.exe";
        auto restored = Settings::fromJson(s.toJson());
        require(restored.toJson() == s.toJson(), "Existing PascalCase JSON settings round trip");
        for (auto value : {Json(0), Json(100000), Json(-10)})
        {
            auto j = s.toJson();
            j["RefreshIntervalSeconds"] = value;
            auto output = Settings::fromJson(j);
            require(output.refresh >= 30 && output.refresh <= 3600, "Refresh bounded");
        }
        auto settingsJson = s.toJson();
        settingsJson["FontSize"] = 1e100;
        settingsJson["PositionMode"] = 1e100;
        auto normalized = Settings::fromJson(settingsJson);
        require(normalized.font == 15 && normalized.position == 0, "Unrepresentable numeric settings safe");
        require(validColor(L"#123aBC") && !validColor(L"#123") && !validColor(L"#GG1234"),
                "Color validation");
        require(wide(utf8(L"额度 · 周 / 中文路径")) == L"额度 · 周 / 中文路径", "UTF-8 round trip");
        require(fingerprint("chatgpt\nunit@example.invalid").size() == 64,
                "Account identity hashed in memory");
        require(fullDate(time, true).find(L"UTC+00:00") != std::wstring::npos, "UTC copy formatting");
        std::cout << checks << " native core checks passed\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << "FAILED: " << e.what() << "\n";
        return 1;
    }
}
