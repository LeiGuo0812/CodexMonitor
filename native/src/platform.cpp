#include "platform.h"
#include <shellapi.h>
#include <shlobj.h>
#include <dwmapi.h>
#include <commdlg.h>
#include <fstream>
#include <set>
#include <thread>
namespace cqm
{
static std::wstring env(const wchar_t *name)
{
    DWORD n = GetEnvironmentVariableW(name, nullptr, 0);
    if (!n)
        return {};
    std::wstring s(n, 0);
    s.resize(GetEnvironmentVariableW(name, s.data(), n));
    return s;
}
static fs::path folder(REFKNOWNFOLDERID id)
{
    PWSTR p = nullptr;
    if (FAILED(SHGetKnownFolderPath(id, 0, nullptr, &p)))
        throw std::runtime_error("KNOWN_FOLDER_FAILED");
    fs::path r(p);
    CoTaskMemFree(p);
    return r;
}
fs::path executablePath()
{
    std::wstring s(32768, 0);
    DWORD n = GetModuleFileNameW(nullptr, s.data(), (DWORD)s.size());
    if (!n || n == s.size())
        throw std::runtime_error("EXECUTABLE_PATH_FAILED");
    s.resize(n);
    return fs::path(s);
}
fs::path settingsDirectory()
{
    return folder(FOLDERID_LocalAppData) / L"CodexQuotaMonitor";
}
Settings loadSettings()
{
    try
    {
        std::ifstream file(settingsDirectory() / L"settings.json", std::ios::binary);
        if (file)
            return Settings::fromJson(Json::parse(file));
    }
    catch (...)
    {
    }
    return {};
}
void saveSettings(const Settings &s)
{
    auto directory = settingsDirectory();
    fs::create_directories(directory);
    auto file = directory / L"settings.json", tmp = directory / L"settings.json.tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out || !(out << s.toJson().dump(2)))
            throw std::runtime_error("SETTINGS_WRITE_FAILED");
        out.flush();
        if (!out)
            throw std::runtime_error("SETTINGS_WRITE_FAILED");
    }
    if (!MoveFileExW(tmp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        throw std::runtime_error("SETTINGS_REPLACE_FAILED");
}
static constexpr auto RunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
bool startupEnabled()
{
    DWORD bytes = 0;
    return RegGetValueW(HKEY_CURRENT_USER, RunKey, L"CodexQuotaMonitor", RRF_RT_REG_SZ, nullptr, nullptr,
                        &bytes) == ERROR_SUCCESS &&
           bytes > sizeof(wchar_t);
}
void setStartup(bool enabled)
{
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, RunKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) !=
        ERROR_SUCCESS)
        throw std::runtime_error("STARTUP_ACCESS_DENIED");
    LSTATUS status = ERROR_SUCCESS;
    if (enabled)
    {
        std::wstring cmd = L"\"" + executablePath().wstring() + L"\" --background";
        status = RegSetValueExW(key, L"CodexQuotaMonitor", 0, REG_SZ, (const BYTE *)cmd.c_str(),
                                (DWORD)((cmd.size() + 1) * sizeof(wchar_t)));
    }
    else
    {
        status = RegDeleteValueW(key, L"CodexQuotaMonitor");
        if (status == ERROR_FILE_NOT_FOUND)
            status = ERROR_SUCCESS;
    }
    RegCloseKey(key);
    if (status != ERROR_SUCCESS)
        throw std::runtime_error("STARTUP_WRITE_FAILED");
}
static bool link(const fs::path &p)
{
    auto a = GetFileAttributesW(p.c_str());
    return a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
}
static bool safeTree(const fs::path &path)
{
    if (link(path))
        return false;
    std::error_code ec;
    for (fs::recursive_directory_iterator i(path, ec), end; i != end && !ec; i.increment(ec))
    {
        if (link(i->path()))
            return false;
    }
    return !ec;
}
CleanupResult clearCaches()
{
    CleanupResult result;
    std::set<fs::path> roots;
    wchar_t temp[32768]{};
    if (GetTempPathW(32768, temp))
        roots.insert(fs::path(temp) / L".net");
    auto custom = env(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR");
    if (!custom.empty())
        roots.insert(fs::absolute(custom).lexically_normal());
    for (auto root : roots)
    {
        root = fs::absolute(root).lexically_normal();
        if (!fs::is_directory(root) || link(root))
            continue;
        std::error_code ec;
        for (fs::directory_iterator app(root, ec), end; app != end && !ec; app.increment(ec))
        {
            if (link(app->path()) || !app->is_directory(ec))
                continue;
            for (fs::directory_iterator bundle(app->path(), ec), bend; bundle != bend && !ec;
                 bundle.increment(ec))
            {
                auto p = fs::absolute(bundle->path()).lexically_normal();
                if (p.parent_path().parent_path() != root || link(p) || !bundle->is_directory(ec))
                    continue;
                auto marker = p / L"codex-monitor-cache-owner.txt";
                if (link(marker) || !fs::is_regular_file(p / L"CodexQuotaMonitor.dll", ec))
                    continue;
                std::ifstream in(marker, std::ios::binary);
                std::string owner((std::istreambuf_iterator<char>(in)), {});
                while (!owner.empty() && isspace((unsigned char)owner.back()))
                    owner.pop_back();
                if (owner != "CodexMonitor:8e992948-6bc8-4b72-8f26-4e2e7254a364")
                    continue;
                if (!safeTree(p))
                {
                    result.failed++;
                    continue;
                }
                // Only a verified, marked version directory can reach recursive removal.
                std::error_code removed;
                fs::remove_all(p, removed);
                if (removed)
                    result.failed++;
                else
                    result.removed++;
            }
        }
    }
    for (auto name : {L"ui-failure.json", L"native-failure.json"})
    {
        auto p = settingsDirectory() / name;
        std::error_code ec;
        if (fs::exists(p) && !link(p))
        {
            fs::remove(p, ec);
            if (ec)
                result.failed++;
        }
    }
    return result;
}
std::vector<fs::path> locateCodex(const std::wstring &configured)
{
    std::vector<fs::path> found;
    auto add = [&](fs::path p)
    {
        std::error_code ec;
        if (!fs::is_regular_file(p, ec))
            return;
        p = fs::absolute(p, ec).lexically_normal();
        if (!ec && std::find(found.begin(), found.end(), p) == found.end())
            found.push_back(p);
    };
    auto pathSearch = [&](const std::wstring &name)
    {
        auto path = env(L"PATH");
        size_t start = 0;
        while (start <= path.size())
        {
            auto end = path.find(L';', start);
            auto part = path.substr(start, end == std::wstring::npos ? end : end - start);
            if (part.size() > 1 && part.front() == L'"' && part.back() == L'"')
                part = part.substr(1, part.size() - 2);
            if (!part.empty())
                add(fs::path(part) / name);
            if (end == std::wstring::npos)
                break;
            start = end + 1;
        }
    };
    if (!configured.empty())
    {
        std::wstring expanded(32768, 0);
        DWORD n = ExpandEnvironmentStringsW(configured.c_str(), expanded.data(), (DWORD)expanded.size());
        if (n && n <= expanded.size())
            expanded.resize(n - 1);
        else
            expanded = configured;
        if (expanded.size() > 1 && expanded.front() == L'"' && expanded.back() == L'"')
            expanded = expanded.substr(1, expanded.size() - 2);
        add(expanded);
        if (fs::path(expanded).parent_path().empty())
            pathSearch(expanded);
        return found;
    }
    pathSearch(L"codex.exe");
    add(folder(FOLDERID_Profile) / L".local" / L"bin" / L"codex.exe");
    auto desktop = folder(FOLDERID_LocalAppData) / L"OpenAI" / L"Codex";
    std::vector<fs::directory_entry> dirs;
    std::error_code ec;
    for (fs::directory_iterator i(desktop / L"bin", ec), end; i != end && !ec; i.increment(ec))
        if (i->is_directory(ec))
            dirs.push_back(*i);
    std::sort(dirs.begin(), dirs.end(),
              [](const auto &a, const auto &b)
              {
                  std::error_code x, y;
                  return a.last_write_time(x) > b.last_write_time(y);
              });
    for (auto &d : dirs)
        add(d.path() / L"codex.exe");
    add(desktop / L"bin" / L"codex.exe");
    add(desktop / L"resources" / L"codex.exe");
    return found;
}
class Process
{
    Handle process_, input_, output_, job_;
    std::string pending_;
    const std::atomic_bool &stop_;
    ULONGLONG deadline_;

  public:
    Process(const fs::path &path, const wchar_t *argument, const std::atomic_bool &stop, unsigned timeout)
        : stop_(stop), deadline_(GetTickCount64() + timeout)
    {
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        HANDLE r = nullptr, w = nullptr;
        if (!CreatePipe(&r, &w, &security, 0))
            throw std::runtime_error("PIPE_FAILED");
        Handle childInput(r);
        input_.reset(w);
        SetHandleInformation(input_.get(), HANDLE_FLAG_INHERIT, 0);
        if (!CreatePipe(&r, &w, &security, 0))
            throw std::runtime_error("PIPE_FAILED");
        output_.reset(r);
        Handle childOutput(w);
        SetHandleInformation(output_.get(), HANDLE_FLAG_INHERIT, 0);
        Handle null(CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &security,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        STARTUPINFOEXW start{};
        start.StartupInfo.cb = sizeof(start);
        start.StartupInfo.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
        start.StartupInfo.wShowWindow = SW_HIDE;
        start.StartupInfo.hStdInput = childInput.get();
        start.StartupInfo.hStdOutput = childOutput.get();
        start.StartupInfo.hStdError = null.get();
        SIZE_T bytes = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
        std::vector<BYTE> attributes(bytes);
        start.lpAttributeList = (LPPROC_THREAD_ATTRIBUTE_LIST)attributes.data();
        if (!InitializeProcThreadAttributeList(start.lpAttributeList, 1, 0, &bytes))
            throw std::runtime_error("PROCESS_ATTRIBUTES_FAILED");
        HANDLE inherited[] = {childInput.get(), childOutput.get(), null.get()};
        if (!UpdateProcThreadAttribute(start.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited,
                                       sizeof(inherited), nullptr, nullptr))
        {
            DeleteProcThreadAttributeList(start.lpAttributeList);
            throw std::runtime_error("PROCESS_HANDLES_FAILED");
        }
        job_.reset(CreateJobObjectW(nullptr, nullptr));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!job_ ||
            !SetInformationJobObject(job_.get(), JobObjectExtendedLimitInformation, &info, sizeof(info)))
        {
            DeleteProcThreadAttributeList(start.lpAttributeList);
            throw std::runtime_error("JOB_FAILED");
        }
        PROCESS_INFORMATION pi{};
        std::wstring command = L"\"" + path.wstring() + L"\" " + argument;
        auto cwd = folder(FOLDERID_Profile).wstring();
        BOOL ok = CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, TRUE,
                                 CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr,
                                 cwd.c_str(), &start.StartupInfo, &pi);
        DeleteProcThreadAttributeList(start.lpAttributeList);
        if (!ok)
            throw std::runtime_error("CODEX_START_FAILED");
        process_.reset(pi.hProcess);
        Handle thread(pi.hThread);
        if (!AssignProcessToJobObject(job_.get(), process_.get()))
        {
            TerminateProcess(process_.get(), 1);
            throw std::runtime_error("JOB_ASSIGN_FAILED");
        }
        ResumeThread(thread.get());
    }
    ~Process()
    {
        input_.reset();
        if (process_ && WaitForSingleObject(process_.get(), 150) != WAIT_OBJECT_0)
            job_.reset();
    }
    void check()
    {
        if (stop_)
            throw std::runtime_error("CANCELLED");
        if (GetTickCount64() > deadline_)
            throw std::runtime_error("TIMEOUT");
    }
    void write(const Json &j)
    {
        check();
        std::string s = j.dump() + "\n";
        DWORD n = 0;
        if (!WriteFile(input_.get(), s.data(), (DWORD)s.size(), &n, nullptr) || n != s.size())
            throw std::runtime_error("APP_SERVER_IO_ERROR");
    }
    std::string line()
    {
        for (;;)
        {
            check();
            auto end = pending_.find('\n');
            if (end != std::string::npos)
            {
                auto s = pending_.substr(0, end);
                pending_.erase(0, end + 1);
                return s;
            }
            DWORD available = 0;
            if (!PeekNamedPipe(output_.get(), nullptr, 0, nullptr, &available, nullptr))
                throw std::runtime_error("APP_SERVER_EXITED");
            if (available)
            {
                char buffer[4096];
                DWORD n = 0;
                if (!ReadFile(output_.get(), buffer, std::min<DWORD>(available, sizeof(buffer)), &n,
                              nullptr) ||
                    !n)
                    throw std::runtime_error("APP_SERVER_IO_ERROR");
                pending_.append(buffer, n);
                if (pending_.size() > 8 * 1024 * 1024)
                    throw std::runtime_error("RESPONSE_TOO_LARGE");
            }
            else if (WaitForSingleObject(process_.get(), 0) == WAIT_OBJECT_0)
            {
                if (!pending_.empty())
                    return std::exchange(pending_, {});
                throw std::runtime_error("APP_SERVER_EXITED");
            }
            else
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    }
    Json request(int id, const char *method, Json params = nullptr)
    {
        Json q = {{"id", id}, {"method", method}};
        if (!params.is_null())
            q["params"] = params;
        write(q);
        for (;;)
        {
            auto reply = Json::parse(line());
            if (!reply.is_object())
                throw std::runtime_error("INVALID_JSON_LINE");
            if (reply.contains("id") && reply.contains("method"))
            {
                write({{"id", reply["id"]},
                       {"error",
                        {{"code", -32601}, {"message", "Quota monitor only supports read methods."}}}});
                continue;
            }
            if (reply.contains("id") && reply["id"].is_number_integer() && reply["id"].get<int>() == id)
                return reply;
        }
    }
};
bool verifyCodex(const std::wstring &configured, const std::atomic_bool &stop)
{
    try
    {
        auto paths = locateCodex(configured);
        for (auto &path : paths)
        {
            Process cli(path, L"--version", stop, 5000);
            if (cli.line().starts_with("codex-cli "))
                return true;
        }
    }
    catch (...)
    {
    }
    return false;
}
Query queryCodex(const std::wstring &configured, const std::atomic_bool &stop)
{
    Query q;
    q.retrieved = now();
    const auto deadline = GetTickCount64() + 30000;
    auto budget = [&](ULONGLONG maximum)
    {
        auto current = GetTickCount64();
        if (stop)
            throw std::runtime_error("CANCELLED");
        if (current >= deadline)
            throw std::runtime_error("TIMEOUT");
        return (DWORD)std::min(maximum, deadline - current);
    };
    try
    {
        static fs::path verified;
        static Seconds verifiedAt = 0;
        auto candidates = locateCodex(configured);
        if (candidates.empty())
        {
            q.error = "CODEX_NOT_FOUND";
            return q;
        }
        fs::path chosen;
        for (auto &p : candidates)
        {
            budget(5000);
            if (stop)
                break;
            if (p == verified && now() - verifiedAt < 900)
            {
                chosen = p;
                break;
            }
            try
            {
                Process version(p, L"--version", stop, budget(5000));
                if (version.line().starts_with("codex-cli "))
                {
                    chosen = verified = p;
                    verifiedAt = now();
                    break;
                }
            }
            catch (const std::exception &)
            {
            }
        }
        if (chosen.empty())
        {
            q.error = "CODEX_NOT_A_CLI";
            return q;
        }
        Process process(chosen, L"app-server", stop, budget(20000));
        auto init = process.request(
            1, "initialize",
            {{"clientInfo",
              {{"name", "codex_quota_monitor"}, {"title", "Codex Quota Monitor"}, {"version", "2.0.0"}}}});
        if (init.contains("error"))
            throw std::runtime_error("INITIALIZE_FAILED");
        process.write({{"method", "initialized"}});
        auto account = process.request(2, "account/read", {{"refreshToken", false}});
        if (account.contains("error"))
        {
            q.error = "ACCOUNT_ERROR";
            return q;
        }
        auto result = account.value("result", Json::object()).value("account", Json());
        if (!result.is_object() || result.value("type", "") != "chatgpt")
        {
            q.error = "CHATGPT_AUTH_REQUIRED";
            return q;
        }
        auto email = result.value("email", std::string{});
        if (!email.empty())
            q.account = fingerprint("chatgpt\n" + email);
        q.plan = result.value("planType", std::string{});
        return parseReplies(account, process.request(3, "account/rateLimits/read"));
    }
    catch (const Json::exception &)
    {
        q.error = "INVALID_RESPONSE";
    }
    catch (const std::exception &e)
    {
        q.error = e.what();
    }
    return q;
}
static std::optional<RECT> childRect(HWND parent, std::initializer_list<const wchar_t *> names)
{
    for (auto name : names)
    {
        HWND h = nullptr;
        while ((h = FindWindowExW(parent, h, name, nullptr)))
        {
            RECT r{};
            if (IsWindowVisible(h) && GetWindowRect(h, &r) && r.right > r.left && r.bottom > r.top)
                return r;
        }
    }
    struct Context
    {
        std::initializer_list<const wchar_t *> names;
        std::optional<RECT> rect;
    };
    Context c{names, {}};
    EnumChildWindows(
        parent,
        [](HWND h, LPARAM l) -> BOOL
        {
            auto &ctx = *(Context *)l;
            wchar_t name[128]{};
            GetClassNameW(h, name, 128);
            for (auto n : ctx.names)
                if (wcscmp(n, name) == 0)
                {
                    RECT r{};
                    if (IsWindowVisible(h) && GetWindowRect(h, &r) && r.right > r.left && r.bottom > r.top)
                    {
                        ctx.rect = r;
                        return FALSE;
                    }
                }
            return TRUE;
        },
        (LPARAM)&c);
    return c.rect;
}
Placement placeWidget(const Settings &s, int width, int height, float scale)
{
    HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
    APPBARDATA data{sizeof(data)};
    SHAppBarMessage(ABM_GETTASKBARPOS, &data);
    RECT bar = data.rc;
    if (bar.right <= bar.left || bar.bottom <= bar.top)
        GetWindowRect(taskbar, &bar);
    MONITORINFO mi{sizeof(mi)};
    GetMonitorInfoW(MonitorFromWindow(taskbar, MONITOR_DEFAULTTOPRIMARY), &mi);
    if (bar.bottom <= bar.top)
        bar = {mi.rcMonitor.left, mi.rcWork.bottom, mi.rcMonitor.right, mi.rcMonitor.bottom};
    APPBARDATA state{sizeof(state)};
    auto flags = SHAppBarMessage(ABM_GETSTATE, &state);
    if (s.position != 2 && taskbar && !(flags & ABS_AUTOHIDE) && height <= bar.bottom - bar.top &&
        (data.uEdge == ABE_TOP || data.uEdge == ABE_BOTTOM))
    {
        auto list = childRect(taskbar, {L"MSTaskSwWClass", L"MSTaskListWClass", L"TaskListThumbnailWnd"});
        auto tray = childRect(taskbar, {L"TrayNotifyWnd"});
        if (list && tray)
        {
            int padding = (int)ceil(8 * scale);
            int x = tray->left - padding - width + (int)round((s.horizontal + 360) * scale);
            int y = bar.top + (bar.bottom - bar.top - height) / 2;
            // Docking is accepted anywhere along the taskbar. Clamp the drop to
            // the nearest safe slot instead of reverting to floating for an x mismatch.
            int first = list->right + padding, last = tray->left - padding - width;
            if (s.position == 1 && first <= last)
                x = std::clamp(x, first, last);
            if (x >= list->right + padding && x + width <= tray->left - padding)
                return {{x, y, x + width, y + height}, true};
        }
    }
    int x = std::clamp(mi.rcWork.right - width + (int)round(s.horizontal * scale), mi.rcWork.left,
                       std::max(mi.rcWork.left, mi.rcWork.right - width));
    int y = data.uEdge == ABE_TOP ? bar.bottom + (int)round(s.vertical * scale)
                                  : bar.top - height - (int)round(s.vertical * scale);
    y = std::max((int)mi.rcWork.top, y);
    return {{x, y, x + width, y + height}, false};
}
bool fullscreenForeground(HWND own)
{
    auto h = GetForegroundWindow();
    if (!h || h == own || !IsWindowVisible(h) || IsIconic(h))
        return false;
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid == GetCurrentProcessId())
        return false;
    wchar_t c[128]{};
    GetClassNameW(h, c, 128);
    for (auto skip : {L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd", L"Progman", L"WorkerW",
                      L"ControlCenterWindow", L"NotifyIconOverflowWindow"})
        if (wcscmp(c, skip) == 0)
            return false;
    // Quick Settings can use a screen-sized shell host during its animation.
    // Identify the system executable as well as the public window classes.
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (process)
    {
        wchar_t path[32768]{};
        DWORD length = (DWORD)std::size(path);
        if (QueryFullProcessImageNameW(process.get(), 0, path, &length))
        {
            auto name = fs::path(path).filename().wstring();
            auto full = fs::path(path).wstring();
            wchar_t windows[32768]{};
            GetWindowsDirectoryW(windows, (UINT)std::size(windows));
            std::wstring systemRoot = std::wstring(windows) + L"\\";
            bool system = full.size() > systemRoot.size() &&
                          _wcsnicmp(full.c_str(), systemRoot.c_str(), systemRoot.size()) == 0;
            if (system && (name == L"ShellExperienceHost.exe" || name == L"ShellHost.exe" ||
                           name == L"StartMenuExperienceHost.exe" || name == L"SearchHost.exe" ||
                           name == L"explorer.exe"))
                return false;
        }
    }
    if (IsZoomed(h) && (GetWindowLongPtrW(h, GWL_STYLE) & WS_CAPTION))
        return false;
    RECT r{};
    MONITORINFO mi{sizeof(mi)};
    if (!GetWindowRect(h, &r) || !GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTOPRIMARY), &mi))
        return false;
    return abs(r.left - mi.rcMonitor.left) <= 2 && abs(r.top - mi.rcMonitor.top) <= 2 &&
           abs(r.right - mi.rcMonitor.right) <= 2 && abs(r.bottom - mi.rcMonitor.bottom) <= 2;
}
static DWORD regValue(const wchar_t *path, const wchar_t *name, DWORD fallback)
{
    DWORD v = fallback, n = sizeof(v);
    RegGetValueW(HKEY_CURRENT_USER, path, name, RRF_RT_REG_DWORD, nullptr, &v, &n);
    return v;
}
bool systemDark(bool apps)
{
    return regValue(L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                    apps ? L"AppsUseLightTheme" : L"SystemUsesLightTheme", 1) == 0;
}
bool transparencyEnabled()
{
    return regValue(L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                    L"EnableTransparency", 1) != 0;
}
bool highContrast()
{
    HIGHCONTRASTW h{sizeof(h)};
    return SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(h), &h, 0) && (h.dwFlags & HCF_HIGHCONTRASTON);
}
bool animationsEnabled()
{
    BOOL v = TRUE;
    SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &v, 0);
    return v != FALSE;
}
bool applyGlass(HWND h, bool dark, bool enabled)
{
    BOOL d = dark;
    DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, &d, sizeof(d));
    DWM_WINDOW_CORNER_PREFERENCE corner = DWMWCP_ROUND;
    DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(corner));
    DWM_SYSTEMBACKDROP_TYPE type = enabled ? DWMSBT_TRANSIENTWINDOW : DWMSBT_NONE;
    bool available =
        enabled && SUCCEEDED(DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, &type, sizeof(type)));
    MARGINS margins = available ? MARGINS{-1, -1, -1, -1} : MARGINS{};
    return SUCCEEDED(DwmExtendFrameIntoClientArea(h, &margins)) && available;
}
void copyText(HWND h, const std::wstring &s)
{
    if (!OpenClipboard(h))
        return;
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, (s.size() + 1) * sizeof(wchar_t));
    if (memory)
    {
        void *p = GlobalLock(memory);
        if (p)
        {
            memcpy(p, s.c_str(), (s.size() + 1) * sizeof(wchar_t));
            GlobalUnlock(memory);
            EmptyClipboard();
            if (!SetClipboardData(CF_UNICODETEXT, memory))
                GlobalFree(memory);
        }
        else
            GlobalFree(memory);
    }
    CloseClipboard();
}
std::wstring chooseExecutable(HWND owner)
{
    wchar_t path[32768]{};
    OPENFILENAMEW ofn{sizeof(ofn)};
    ofn.hwndOwner = owner;
    ofn.lpstrFilter = L"Codex CLI (codex.exe)\0*.exe\0\0";
    ofn.lpstrFile = path;
    ofn.nMaxFile = 32768;
    ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    return GetOpenFileNameW(&ofn) ? path : L"";
}
void writeDiagnostic(const std::string &code) noexcept
{
    try
    {
        auto dir = settingsDirectory();
        fs::create_directories(dir);
        std::ofstream out(dir / L"native-failure.json");
        out << Json{{"time", now()}, {"code", code.substr(0, 128)}, {"version", "2.0.0-native-preview.2"}}
                   .dump(2);
    }
    catch (...)
    {
    }
}
} // namespace cqm
