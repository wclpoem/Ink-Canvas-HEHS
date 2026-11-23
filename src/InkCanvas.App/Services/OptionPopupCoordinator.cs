using InkCanvas.Controls.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

/// <summary>Opening an option pane dismisses other unpinned panes, including other tool families.</summary>
internal static class OptionPopupCoordinator
{
    private sealed class Pane(Window window, PopupHeader header)
    {
        public Window Window { get; } = window;
        public PopupHeader Header { get; } = header;
        public bool Visible, Closed;
    }

    // All registrations and window events run on the application's UI thread.
    private static readonly List<Pane> Panes = new();

    public static void DismissUnpinned()
    {
        foreach (var pane in Panes.ToArray())
            if (!pane.Closed && !pane.Header.IsPinned && pane.Window.AppWindow.IsVisible)
                pane.Window.Close();
    }

    public static void Attach(Window window, PopupHeader header)
    {
        var pane = new Pane(window, header);
        var appWindow = window.AppWindow;
        Panes.Add(pane);

        void CollapseOthers()
        {
            foreach (var other in Panes.ToArray())
                if (other != pane && !other.Closed && !other.Header.IsPinned && other.Window.AppWindow.IsVisible)
                    other.Window.Close();
        }

        void Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (pane.Closed) return;
            bool visible = sender.IsVisible;
            bool opened = visible && !pane.Visible;
            pane.Visible = visible;
            if (opened) CollapseOthers();
        }

        void PinChanged()
        {
            if (!pane.Closed && pane.Visible && !header.IsPinned) CollapseOthers();
        }

        appWindow.Changed += Changed;
        header.PinChanged += PinChanged;
        window.Closed += (_, _) =>
        {
            pane.Closed = true;
            appWindow.Changed -= Changed;
            header.PinChanged -= PinChanged;
            Panes.Remove(pane);
            // Unregister first so resetting a closed pane cannot dismiss another open pane.
            header.IsPinned = false;
        };
    }
}
