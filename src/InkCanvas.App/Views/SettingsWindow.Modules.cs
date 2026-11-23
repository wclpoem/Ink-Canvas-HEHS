using InkCanvas.Core.Settings;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private IReadOnlyList<ModuleSettingsPage> _moduleSettingsPages = Array.Empty<ModuleSettingsPage>();
    private IEnumerable<string> Categories => SettingsCategoryCatalog.Pages.Select(page => page.Name)
        .Concat(GuidePages).Concat(_moduleSettingsPages.Select(page => page.Name));

    private void RefreshModuleSettings() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        _searchIndexed = false;
        _searchEntries.Clear();
        QueueRebuild();
    });

    private string ResolveCategory(string category)
    {
        string resolved = SettingsCategoryCatalog.Resolve(category);
        return _moduleSettingsPages.FirstOrDefault(page => page.Aliases.Contains(resolved))?.Name ?? resolved;
    }

    private void BuildModuleSettings(ModuleSettingsPage page)
    {
        try { page.Build(new ModuleSettingsPageBuilder(this)); }
        catch (Exception error)
        {
            _refreshControls.Clear();
            _content.Children.Clear();
            _content.Children.Add(InfoRow("插件设置加载失败 · " + error.GetBaseException().Message));
        }
    }

    private sealed class ModuleSettingsPageBuilder(SettingsWindow window) : IModuleSettingsPageBuilder
    {
        public void AddSection(string title) => window.AddSection(title);
        public void AddInfo(string text) => window._content.Children.Add(window.InfoRow(text));

        public void AddToggle(string title, Func<bool> get, Action<bool> set, string note = null,
            Func<string> unavailableReason = null)
        {
            UIElement row = window.ToggleRow(title, get, set, note: note);
            if (unavailableReason is not null) row = window.Require(row, unavailableReason);
            window._content.Children.Add(row);
        }

        public void AddButton(string title, string description, Action action, Func<string> unavailableReason = null)
        {
            UIElement row = window.ButtonRow(title, description, action);
            if (unavailableReason is not null) row = window.Require(row, unavailableReason);
            window._content.Children.Add(row);
        }

        public void AddStatus(string title, Func<string> get)
        {
            var text = new TextBlock { Text = get(), TextWrapping = TextWrapping.Wrap };
            window._content.Children.Add(window.Searchable(window.Card(text), title));
            window._refreshControls.Add(() => text.Text = get());
        }

        public void AddView(string title, Func<FrameworkElement> createView, string keywords = null)
        {
            // Index metadata without creating plugin controls or opening their data stores.
            if (window._indexingSearch) window.Searchable(new Border(), title, keywords);
            else window._content.Children.Add(window.Searchable(window.Card(createView()), title, keywords));
        }
    }
}
