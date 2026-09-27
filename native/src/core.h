#pragma once
#include <windows.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>
#include "json.hpp"
namespace cqm
{
using Json = nlohmann::json;
using Seconds = int64_t;
namespace fs = std::filesystem;
std::wstring wide(const std::string &value);
std::string utf8(const std::wstring &value);
Seconds now();
std::wstring countdown(std::optional<Seconds> date, Seconds current = now());
std::wstring readable(std::optional<Seconds> date, bool utc = false);
std::wstring fullDate(std::optional<Seconds> date, bool utc = false);
enum class Kind
{
    Unknown,
    FiveHour,
    Week,
    Other
};
struct Quota
{
    std::string key;
    std::wstring name;
    Kind kind = Kind::Unknown;
    std::optional<double> used;
    std::optional<Seconds> reset;
    std::optional<int> minutes;
    std::string restriction;
    std::optional<double> remaining() const;
    std::optional<double> timePercent() const;
    std::wstring label() const;
};
struct Credit
{
    std::wstring id, title, description, status, type;
    std::optional<Seconds> granted, expires;
    std::wstring validity() const;
};
struct Query
{
    bool success = false;
    std::string error, account, plan;
    std::vector<Quota> windows;
    std::vector<Credit> credits;
    std::optional<int> creditCount;
    enum Details
    {
        Unavailable,
        CountOnly,
        Complete,
        Partial
    } creditDetails = Unavailable;
    Seconds retrieved = 0;
};
struct State
{
    Query data;
    std::optional<Quota> selected;
    std::optional<Quota> rememberedFive;
    std::string account, error;
    Seconds updated = 0;
    bool stale = false, refreshing = false, missingFive = false;
    void apply(Query query);
    std::optional<Quota> find(Kind kind) const;
};
struct Settings
{
    std::wstring codex;
    int position = 0, mode = 0, preset = 0;
    std::wstring background = L"#E8EDF3", primary = L"#17212D", secondary = L"#526174", accent = L"#3B73C5";
    double widgetOpacity = .92, detailsOpacity = .96, font = 15;
    int horizontal = -360, vertical = 12, refresh = 60;
    bool startup = false;
    void normalize();
    static Settings fromJson(const Json &j);
    Json toJson() const;
};
bool validColor(const std::wstring &value);
Query parseReplies(const Json &account, const Json &limits);
std::string fingerprint(std::string value);
std::wstring errorLabel(const std::string &error);
} // namespace cqm
