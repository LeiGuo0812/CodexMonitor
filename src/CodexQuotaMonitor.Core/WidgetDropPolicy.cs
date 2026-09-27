namespace CodexQuotaMonitor.Core;

public static class WidgetDropPolicy
{
    public static bool IsNearTaskbar(double centerX, double centerY, double left, double top,
        double right, double bottom, double scale) =>
        centerX >= left && centerX <= right && centerY >= top - 12 * scale && centerY <= bottom + 12 * scale;
}
