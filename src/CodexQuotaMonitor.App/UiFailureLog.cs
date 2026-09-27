using System.Text.Json;

namespace CodexQuotaMonitor.App;

internal static class UiFailureLog
{
    public static void Record(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaMonitor");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "ui-failure.json"), JsonSerializer.Serialize(new
            {
                At = DateTimeOffset.UtcNow,
                Type = exception.GetType().Name,
                exception.HResult,
                LayoutCycle = exception.Message.Contains("layout cycle", StringComparison.OrdinalIgnoreCase),
                ClosedObject = exception.HResult == unchecked((int)0x80000013),
                exception.StackTrace,
                InnerType = exception.InnerException?.GetType().Name,
                InnerHResult = exception.InnerException?.HResult,
                InnerStack = exception.InnerException?.StackTrace
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
