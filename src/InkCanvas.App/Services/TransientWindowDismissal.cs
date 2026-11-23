using System.Runtime.InteropServices;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace InkCanvas.App.Services;

/// <summary>Dismiss transient tools even while the presentation retains foreground focus.</summary>
internal static class TransientWindowDismissal
{
    public static void Attach(Window window, Func<IntPtr> owner, Func<bool> enabled = null, Func<RectInt32> anchor = null,
        Func<RectInt32> interactionRegion = null, Action dismiss = null)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        IntPtr popup = WindowChrome.GetHwnd(window);
        bool mouseDown = IsDown(1), escapeDown = IsDown(0x1B);
        bool closed = false, ownerGesture = false;
        bool InInteractionRegion(NativeMethods.POINT point)
        {
            if (interactionRegion is not null)
            {
                var region = interactionRegion();
                if (point.X >= region.X && point.Y >= region.Y && point.X < region.X + region.Width && point.Y < region.Y + region.Height) return true;
            }
            if (anchor is not null)
            {
                var bounds = anchor();
                if (point.X >= bounds.X && point.Y >= bounds.Y && point.X < bounds.X + bounds.Width && point.Y < bounds.Y + bounds.Height) return true;
            }
            return false;
        }
        void DismissAt(NativeMethods.POINT point)
        {
            if (closed || enabled?.Invoke() == false || !window.AppWindow.IsVisible || InInteractionRegion(point)) return;
            if (!BelongsTo(WindowFromPoint(point), WindowChrome.GetHwnd(window))) (dismiss ?? window.Close)();
        }
        HookProc callback = (code, message, data) =>
        {
            if (code >= 0 && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
            {
                var point = Marshal.PtrToStructure<NativeMethods.POINT>(data);
                ownerGesture = !closed && InInteractionRegion(point);
                // A combo's popup can disappear before the queued callback runs.
                // Classify its HWND while the press still targets that child popup.
                // Snapshot toolbar hit testing before the drag moves it away from this press.
                if (!closed && enabled?.Invoke() != false && !ownerGesture && !BelongsTo(WindowFromPoint(point), popup))
                    dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => DismissAt(point));
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        IntPtr hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, callback, IntPtr.Zero, 0);
        ((FrameworkElement)window.Content).Loaded += (_, _) => foreground = NativeMethods.GetForegroundWindow();
        timer.Tick += (_, _) =>
        {
            bool mouse = IsDown(1), escape = IsDown(0x1B);
            bool click = mouse && !mouseDown, dismissKey = escape && !escapeDown;
            mouseDown = mouse; escapeDown = escape;
            if (closed || !window.AppWindow.IsVisible) return;
            if (dismiss is not null && enabled?.Invoke() == false) return;
            if (dismissKey && (dismiss is null || enabled?.Invoke() != false)) { (dismiss ?? window.Close)(); return; }
            var current = NativeMethods.GetForegroundWindow();
            // A toolbar drag can temporarily change the foreground HWND. Remember where the
            // press began, including the first poll after release, rather than dismiss mid-drag.
            bool interactingWithOwner = ownerGesture;
            if (!mouse) ownerGesture = false;
            bool movedAway = !interactingWithOwner && current != IntPtr.Zero && current != foreground
                && !BelongsTo(current, owner())
                && !BelongsTo(current, WindowChrome.GetHwnd(window));
            foreground = current;
            if (enabled?.Invoke() != false && movedAway) { (dismiss ?? window.Close)(); return; }
            if (hook == IntPtr.Zero && click && NativeMethods.GetCursorPos(out var point)) DismissAt(point);
        };
        timer.Start();
        window.Closed += (_, _) =>
        {
            closed = true; timer.Stop();
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            GC.KeepAlive(callback);
        };
    }

    private static bool BelongsTo(IntPtr target, IntPtr window)
    {
        if (target == IntPtr.Zero) return false;
        var root = GetAncestor(target, 2 /* GA_ROOT */);
        if (root == window) return true;
        for (var next = NativeMethods.GetWindow(root, 4 /* GW_OWNER */); next != IntPtr.Zero; next = NativeMethods.GetWindow(next, 4))
            if (next == window) return true;
        return false;
    }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
}
