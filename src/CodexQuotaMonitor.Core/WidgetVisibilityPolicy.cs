namespace CodexQuotaMonitor.Core;

public static class WidgetVisibilityPolicy
{
    public static bool ShouldHide(string foregroundClass, bool visible, bool minimized,
        bool ownProcess, bool maximizedWithCaption, bool coversMonitor)
    {
        if (!visible || minimized || ownProcess || maximizedWithCaption) return false;
        // Explorer's desktop covers the entire monitor but is not a fullscreen application.
        if (foregroundClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        return coversMonitor;
    }
}
