#pragma once
#include "platform.h"
#include "render.h"
#include <commctrl.h>
#include <thread>
#include <mutex>
namespace cqm
{
enum Action
{
    Details = 100,
    Refresh,
    SettingsPage,
    Startup,
    ClearCache,
    Quit,
    PositionAuto = 120,
    PositionTaskbar,
    PositionAbove,
    ThemeMist = 130,
    ThemeSand,
    ThemeGraphite,
    ThemeMidnight,
    ThemeCustom,
    Save = 150,
    Cancel,
    Defaults,
    Browse,
    CopyFive,
    CopyWeek,
    ToggleUtc,
    Minimize,
    Close
};
struct Hit
{
    int action;
    D2D1_RECT_F rect;
    std::wstring label;
};
struct View
{
    HWND hwnd = nullptr;
    HWND tooltip = nullptr;
    std::unique_ptr<Canvas> canvas;
    float scale = 1, scroll = 0, total = 0, animation = 1;
    ULONGLONG opened = 0;
    std::vector<Hit> hits;
    std::map<int, std::wstring> tips;
    int focus = -1, hover = -1;
    bool utc = false, initialFit = false, scrollDragging = false;
};
class Application
{
    HINSTANCE instance_;
    Drawing drawing_;
    Settings saved_, settings_;
    Palette colors_;
    State state_;
    HWND widget_ = nullptr, tooltip_ = nullptr;
    Canvas widgetCanvas_;
    View details_, editor_;
    std::map<int, HWND> controls_;
    std::map<int, D2D1_RECT_F> controlRects_;
    HFONT controlFont_ = nullptr;
    HBRUSH editBrush_ = nullptr;
    HICON icon_ = nullptr;
    bool trayAdded_ = false, hidden_ = false, docked_ = false, drag_ = false, down_ = false, hover_ = false,
         editorSync_ = false, closing_ = false, cacheBusy_ = false, saving_ = false;
    bool taskbarDark_ = false, contrast_ = false, animate_ = true;
    POINT press_{}, cursor_{};
    RECT dragBounds_{}, candidate_{};
    int candidateHits_ = 0;
    float hoverAmount_ = 0;
    std::wstring widgetSignature_, tooltipText_, editorError_;
    std::wstring measuredText_, detailClock_;
    int measuredWidth_ = 148, measuredHeight_ = 42;
    Seconds nextRefresh_ = 0;
    int failures_ = 0;
    UINT taskbarCreated_ = 0;
    std::atomic_bool stop_ = false;
    std::thread queryThread_, cleanupThread_, validationThread_;
    std::mutex resultMutex_;
    std::optional<Query> queryResult_;
    std::optional<CleanupResult> cleanupResult_;
    std::optional<bool> validationResult_;
    Settings pendingSave_;
    HWINEVENTHOOK foregroundHook_ = nullptr, shellHook_ = nullptr;
    HANDLE networkNotification_ = nullptr;
    Seconds lastNetworkRefresh_ = 0;
    Handle activateEvent_;
    bool verify_ = false;
    fs::path report_;
    ULONGLONG verifyStarted_ = 0;
    int verifyStep_ = 0;
    Json verification_ = Json::object();
    static Application *active_;
    static LRESULT CALLBACK widgetProc(HWND, UINT, WPARAM, LPARAM);
    static LRESULT CALLBACK viewProc(HWND, UINT, WPARAM, LPARAM);
    static void CALLBACK shellEvent(HWINEVENTHOOK, DWORD, HWND, LONG, LONG, DWORD, DWORD);
    LRESULT widgetMessage(HWND, UINT, WPARAM, LPARAM);
    LRESULT viewMessage(View &, bool, HWND, UINT, WPARAM, LPARAM);
    void tick();
    void refresh();
    void receiveQuery();
    void appearance();
    void place(bool immediate = false);
    void drawWidget();
    void updateTooltip();
    void addTray();
    void removeTray();
    void notify(const std::wstring &text);
    HMENU buildMenu();
    void menu();
    void command(int action);
    void openDetails();
    void openSettings();
    void closeView(View &view, bool editor);
    void fitDetails();
    void drawDetails();
    void drawSettings();
    void chrome(View &v, const std::wstring &title, float width);
    void button(View &v, int id, const std::wstring &text, D2D1_RECT_F rect, bool accent = false);
    void tip(View &view, int id, const std::wstring &text, D2D1_RECT_F rect);
    void card(Canvas &c, D2D1_RECT_F r, Color tint);
    void fillControls();
    void readControls(bool save);
    void layoutControls();
    void commitSettings(const Settings &s);
    void createControl(int id, const wchar_t *klass, const std::wstring &text, float x, float y, float width,
                       float height, DWORD style);
    void redrawViews();
    void finishVerification();

  public:
    Application(HINSTANCE instance, bool verify = false, fs::path report = {})
        : instance_(instance), widgetCanvas_(drawing_), verify_(verify), report_(std::move(report))
    {
    }
    ~Application();
    int run();
};
} // namespace cqm
