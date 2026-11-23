using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

/// <summary>Modules register settings during startup and release them through RegisterCleanup.</summary>
public interface IModuleSettingsRegistry
{
    IDisposable Register(ModuleSettingsPage page);
}

public sealed record ModuleSettingsPage(string Id, string Name, string Glyph, string SearchTerms,
    Action<IModuleSettingsPageBuilder> Build)
{
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

/// <summary>The host supplies shared settings controls, persistence and search indexing.</summary>
public interface IModuleSettingsPageBuilder
{
    void AddSection(string title);
    void AddInfo(string text);
    void AddToggle(string title, Func<bool> get, Action<bool> set, string note = null,
        Func<string> unavailableReason = null);
    void AddButton(string title, string description, Action action, Func<string> unavailableReason = null);
    void AddStatus(string title, Func<string> get);
    void AddView(string title, Func<FrameworkElement> createView, string keywords = null);
}
