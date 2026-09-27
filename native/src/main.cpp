#include "application.h"
#include <shellapi.h>
#include <fstream>
using namespace cqm;
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    int result = 0;
    try
    {
        int count = 0;
        auto args = CommandLineToArgvW(GetCommandLineW(), &count);
        bool verify = false, queryOnly = false;
        fs::path report;
        for (int i = 1; i < count; i++)
        {
            if (wcscmp(args[i], L"--verify-native") == 0)
                verify = true;
            else if (wcscmp(args[i], L"--query-report") == 0)
                queryOnly = true;
            else if (wcscmp(args[i], L"--report") == 0 && i + 1 < count)
                report = args[++i];
        }
        LocalFree(args);
        if (queryOnly)
        {
            std::atomic_bool stop = false;
            auto q = queryCodex(loadSettings().codex, stop);
            Json j = {{"success", q.success},
                      {"error", q.error},
                      {"windows", q.windows.size()},
                      {"creditDetails", q.creditDetails},
                      {"hasCreditCount", q.creditCount.has_value()},
                      {"retrieved", q.retrieved}};
            if (!report.empty())
            {
                std::ofstream out(report);
                out << j.dump(2);
            }
            result = q.success ? 0 : 2;
        }
        else
        {
            Handle mutex(CreateMutexW(nullptr, FALSE,
                                      verify ? L"Local\\CodexQuotaMonitor.NativeVerification"
                                             : L"Local\\CodexQuotaMonitor.SingleInstance"));
            if (!mutex)
                throw std::runtime_error("SINGLE_INSTANCE_FAILED");
            DWORD acquired = WaitForSingleObject(mutex.get(), 0);
            if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
            {
                Handle activate(OpenEventW(EVENT_MODIFY_STATE, FALSE,
                                           verify ? L"Local\\CodexQuotaMonitor.NativeVerifyActivate"
                                                  : L"Local\\CodexQuotaMonitor.Activate"));
                if (activate)
                    SetEvent(activate.get());
            }
            else
            {
                {
                    Application app(instance, verify, report);
                    result = app.run();
                }
                ReleaseMutex(mutex.get());
            }
        }
    }
    catch (const std::exception &e)
    {
        writeDiagnostic(e.what());
        MessageBoxW(nullptr, L"CodexMonitor 无法启动。诊断已写入本程序设置目录。", L"CodexMonitor",
                    MB_ICONERROR);
        result = 1;
    }
    if (SUCCEEDED(com))
        CoUninitialize();
    return result;
}
