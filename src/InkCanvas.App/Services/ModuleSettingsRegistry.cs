using InkCanvas.Core.Settings;
using InkCanvas.Host.Contracts;

namespace InkCanvas.App.Services;

/// <summary>Settings contributions live only as long as their module registration.</summary>
public sealed class ModuleSettingsRegistry : IModuleSettingsRegistry
{
    private readonly List<ModuleSettingsPage> _pages = new();
    public IReadOnlyList<ModuleSettingsPage> Pages => _pages.ToArray();
    public event Action Changed;

    public IDisposable Register(ModuleSettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (string.IsNullOrWhiteSpace(page.Id) || string.IsNullOrWhiteSpace(page.Name) || page.Build is null)
            throw new ArgumentException("Settings pages require an ID, name and builder.", nameof(page));
        if (_pages.Any(existing => existing.Id == page.Id || existing.Name == page.Name)
            || SettingsCategoryCatalog.Pages.Any(existing => existing.Name == page.Name))
            throw new ArgumentException("Settings page identity is already registered.", nameof(page));
        _pages.Add(page);
        Notify();
        return new Registration(this, page);
    }

    private void Notify()
    {
        foreach (Action observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { observer(); } catch { }
    }

    private sealed class Registration(ModuleSettingsRegistry registry, ModuleSettingsPage page) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (registry._pages.Remove(page)) registry.Notify();
        }
    }
}
