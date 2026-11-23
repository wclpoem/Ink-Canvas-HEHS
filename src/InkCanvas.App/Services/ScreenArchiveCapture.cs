using System.Runtime.InteropServices;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.System;

namespace InkCanvas.App.Services;

internal static class ScreenArchiveCapture
{
    /// <summary>Read the underlying desktop while keeping our ink and tools out of the background image.</summary>
    public static (byte[] Pixels, int Width, int Height) Capture(ScreenRect screen)
    {
        var affinity = new List<(nint Window, uint Previous)>();
        var hidden = new List<nint>();
        try
        {
            EnumWindows((window, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(window, out uint process);
                if (process != Environment.ProcessId || !NativeMethods.IsWindowVisible(window)) return true;
                GetWindowDisplayAffinity(window, out uint previous);
                if (SetWindowDisplayAffinity(window, 0x11 /* WDA_EXCLUDEFROMCAPTURE */)) affinity.Add((window, previous));
                else { NativeMethods.ShowWindow(window, 0 /* SW_HIDE */); hidden.Add(window); }
                return true;
            }, 0);
            DwmFlush();
            return ScreenshotService.Capture(screen.X, screen.Y, screen.Width, screen.Height);
        }
        finally
        {
            foreach (var item in affinity) SetWindowDisplayAffinity(item.Window, item.Previous);
            foreach (var window in hidden) NativeMethods.ShowWindow(window, 4 /* SW_SHOWNOACTIVATE */);
        }
    }

    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
