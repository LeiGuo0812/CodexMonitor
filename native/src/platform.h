#pragma once
#include "core.h"
#include <atomic>
#include <utility>
namespace cqm
{
class Handle
{
    HANDLE value_ = nullptr;

  public:
    Handle() = default;
    explicit Handle(HANDLE h) : value_(h)
    {
    }
    ~Handle()
    {
        reset();
    }
    Handle(const Handle &) = delete;
    Handle &operator=(const Handle &) = delete;
    Handle(Handle &&h) noexcept : value_(h.release())
    {
    }
    Handle &operator=(Handle &&h) noexcept
    {
        reset(h.release());
        return *this;
    }
    HANDLE get() const
    {
        return value_;
    }
    operator bool() const
    {
        return value_ && value_ != INVALID_HANDLE_VALUE;
    }
    HANDLE release()
    {
        return std::exchange(value_, nullptr);
    }
    void reset(HANDLE h = nullptr)
    {
        if (*this)
            CloseHandle(value_);
        value_ = h;
    }
};
fs::path settingsDirectory();
fs::path executablePath();
Settings loadSettings();
void saveSettings(const Settings &settings);
bool startupEnabled();
void setStartup(bool enabled);
struct CleanupResult
{
    unsigned removed = 0, failed = 0;
};
CleanupResult clearCaches();
Query queryCodex(const std::wstring &configured, const std::atomic_bool &stop);
bool verifyCodex(const std::wstring &configured, const std::atomic_bool &stop);
std::vector<fs::path> locateCodex(const std::wstring &configured);
struct Placement
{
    RECT rect{};
    bool docked = false;
};
Placement placeWidget(const Settings &settings, int width, int height, float scale);
bool fullscreenForeground(HWND own);
bool systemDark(bool apps = false);
bool highContrast();
bool animationsEnabled();
bool transparencyEnabled();
void copyText(HWND hwnd, const std::wstring &text);
std::wstring chooseExecutable(HWND owner);
bool applyGlass(HWND hwnd, bool dark, bool enabled);
void writeDiagnostic(const std::string &code) noexcept;
} // namespace cqm
