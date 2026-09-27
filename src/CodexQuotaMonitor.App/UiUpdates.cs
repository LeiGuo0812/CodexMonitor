using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexQuotaMonitor.App;

internal static class UiUpdates
{
    public static void Text(TextBlock element, string value)
    {
        if (element.Text != value) element.Text = value;
    }

    public static void Tooltip(DependencyObject element, string value)
    {
        if (ToolTipService.GetToolTip(element) is not string current || current != value)
            ToolTipService.SetToolTip(element, value);
    }
}
