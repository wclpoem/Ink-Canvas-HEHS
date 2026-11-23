using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

/// <summary>Optional AI plugin owns connection settings and its credential storage.</summary>
public interface IAiConnectionSettings
{
    FrameworkElement CreateSettingsView();
}
