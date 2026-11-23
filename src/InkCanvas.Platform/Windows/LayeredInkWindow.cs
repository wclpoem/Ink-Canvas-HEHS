using InkCanvas.Platform.Interop;
using InkCanvas.Platform.System;
using System.Runtime.InteropServices;

namespace InkCanvas.Platform.Windows;

public enum OverlayPointerKind { Pen, Touch, Mouse }

public readonly record struct OverlayPointer(
    uint Id,
    float X,
    float Y,
    float Pressure,
    float ContactWidth,
    float ContactHeight,
    OverlayPointerKind Kind,
    uint TimeMs,
    bool Shift = false,
    bool Control = false)
{
    public long InputSurface { get; init; }
    public bool HasPressure { get; init; }
    public float TiltX { get; init; }
    public float TiltY { get; init; }
    public bool IsIntermediate { get; init; }
}

/// <summary>
/// A borderless, topmost, <b>per-pixel transparent</b> overlay that receives pen/touch input.
/// <para>
/// Why not a WinUI 3 window: a WinUI 3 XAML window always paints an opaque backdrop. We
/// measured five techniques (null backdrop + transparent root, <c>WS_EX_NOREDIRECTIONBITMAP</c>
/// applied both before and after XAML init, DWM blur-behind, and two accent policies) and all
/// produced pixel-identical output to an opaque control window. A Win32 layered window fed
/// with premultiplied-alpha pixels does work, including partial alpha, so the ink surface
/// lives here while every piece of chrome is a WinUI 3 window.
/// </para>
/// <para>
/// The window is created on the UI thread, so it shares the app's existing message pump.
/// </para>
/// </summary>
public sealed class LayeredInkWindow : IDisposable
{
    private const string ClassName = "InkCanvasOverlayWindow";
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    // Shared system cursor follows the user's cursor scheme and is owned by Windows.
    private static readonly IntPtr ArrowCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(NativeMethods.IDC_ARROW));
    private static bool _classRegistered;
    private static bool _mouseInPointerEnabled;
    private static bool _mouseAsPointer;
    private static readonly object _classLock = new();

    // Keep the delegate alive for the lifetime of the window; the native side holds a raw pointer.
    private readonly WndProcDelegate _wndProc;
    private IntPtr _hwnd;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private IntPtr _screenDc;
    private bool _disposed;
    private readonly Dictionary<uint, OverlayPointer> _activePointers = new();
    private NativeMethods.POINTER_PEN_INFO[] _penHistory;
    private NativeMethods.POINTER_TOUCH_INFO[] _touchHistory;
    private OverlayPointer? _fallbackMouse;
    private bool _trackingMouseLeave;
    private ScreenRect[] _inputAreas;
    private readonly Dictionary<(IntPtr Device, uint Contact), uint> _legacyContacts = new();
    private uint _nextLegacyId = 0x10000;
    private ScreenInfo[] _screens = ScreenService.All().ToArray();

    private delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);

    // ---- touch latency / feedback interop (kept private to this window) ----
    // TWF_FINETOUCH: prefer noncoalesced touch input; TWF_WANTPALM: disable the system's
    // palm-rejection buffering so packets arrive immediately (RegisterTouchWindow docs).
    // We do our own palm classification (PalmRejector), so palm contacts must reach us.
    private const uint TWF_FINETOUCH = 0x00000001;
    private const uint TWF_WANTPALM = 0x00000002;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint PM_NOYIELD = 0x0002;

    // FEEDBACK_TYPE (winuser.h): touch/pen visuals and press-and-hold right-click gestures.
    private const uint FEEDBACK_TOUCH_CONTACTVISUALIZATION = 1;
    private const uint FEEDBACK_PEN_TAP = 3;
    private const uint FEEDBACK_PEN_DOUBLETAP = 4;
    private const uint FEEDBACK_PEN_PRESSANDHOLD = 5;
    private const uint FEEDBACK_PEN_RIGHTTAP = 6;
    private const uint FEEDBACK_TOUCH_TAP = 7;
    private const uint FEEDBACK_TOUCH_DOUBLETAP = 8;
    private const uint FEEDBACK_TOUCH_PRESSANDHOLD = 9;
    private const uint FEEDBACK_TOUCH_RIGHTTAP = 10;
    private const uint FEEDBACK_GESTURE_PRESSANDHOLD = 11;

    [DllImport("user32.dll")] private static extern bool RegisterTouchWindow(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    internal struct TouchInput
    {
        public int X, Y;
        public IntPtr Source;
        public uint Id, Flags, Mask, Time;
        public UIntPtr Extra;
        public uint Width, Height;
    }
    [DllImport("user32.dll")] private static extern bool GetTouchInputInfo(IntPtr input, uint count, [Out] TouchInput[] contacts, int size);
    [DllImport("user32.dll")] private static extern bool CloseTouchInputHandle(IntPtr input);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int operation);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern bool SetWindowFeedbackSetting(
        IntPtr h, uint feedback, uint flags, uint size, ref int configuration);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessageW(out NativeMethods.MSG msg, IntPtr h,
        uint filterMin, uint filterMax, uint remove);

    public LayeredInkWindow(int x, int y, int width, int height)
    {
        X = x; Y = y; Width = width; Height = height;
        _wndProc = WndProc;

        lock (_classLock)
        {
            if (!_mouseInPointerEnabled)
            {
                // Without this the mouse only produces WM_MOUSE* messages, never WM_POINTER*,
                // so a WndProc that handles pointer input alone cannot draw with a mouse.
                // Pen and touch already arrive as pointer messages; this brings the mouse in line.
                NativeMethods.EnableMouseInPointer(true);
                _mouseInPointerEnabled = true;
                _mouseAsPointer = NativeMethods.IsMouseInPointerEnabled();
            }

            if (!_classRegistered)
            {
                var wc = new NativeMethods.WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = Marshal.GetHINSTANCE(typeof(LayeredInkWindow).Module),
                    lpszClassName = ClassName,
                    hCursor = ArrowCursor,
                    hbrBackground = IntPtr.Zero,
                };
                if (NativeMethods.RegisterClassW(ref wc) == 0)
                    throw new InvalidOperationException($"RegisterClass failed: {Marshal.GetLastWin32Error()}");
                _classRegistered = true;
            }
        }

        // No WS_EX_TRANSPARENT initially: we need input while writing.
        int ex = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW
               | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE;
        _hwnd = NativeMethods.CreateWindowExW(ex, ClassName, "Ink Canvas Overlay",
            NativeMethods.WS_POPUP, x, y, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        DpiScale = dpi == 0 ? 1.0f : dpi / 96.0f;

        // Touch optimisation (WPF parity: MainWindow.xaml:26 Stylus.IsPressAndHoldEnabled=False,
        // and WPF's own touch stack delivers every contact unbuffered for custom classification):
        // - TWF_FINETOUCH|TWF_WANTPALM removes the system's coalescing/palm-detection buffering
        //   so first-contact ink is laid down with minimal delay; pen input is unaffected.
        // - Feedback suppression kills the press-and-hold right-click ring, tap ripples and
        //   contact dots on this surface (WM_POINTERDOWN is consumed, so no WM_GESTURE/flicks
        //   are generated either; this removes the DWM-drawn visuals that remain).
        // Edge swipes are handled separately by ShellController.ApplyEdgeGestureSetting, which
        // honours the user's IsEnableEdgeGestureUtil setting — not forced here.
        RegisterTouchWindow(_hwnd, TWF_FINETOUCH | TWF_WANTPALM);
        SuppressSystemInputFeedback();

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        _memDc = NativeMethods.CreateCompatibleDC(_screenDc);
        EnsurePixelBuffer();
    }

    private void CreatePixelBuffer(int width, int height)
    {
        var bi = new NativeMethods.BITMAPINFO
        {
            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFO>(),
            biWidth = width,
            biHeight = -height,   // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        var dib = NativeMethods.CreateDIBSection(_screenDc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            throw new InvalidOperationException("CreateDIBSection failed");
        }
        var previous = NativeMethods.SelectObject(_memDc, dib);
        if (_oldBitmap == IntPtr.Zero) _oldBitmap = previous;
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
        _dib = dib;
        _bits = bits;
    }

    public long PixelBufferBytes => _dib == IntPtr.Zero ? 0 : (long)Width * Height * 4;

    public void EnsurePixelBuffer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dib == IntPtr.Zero) CreatePixelBuffer(Width, Height);
    }

    /// <summary>Release the DIB of an inactive surface while Windows retains its last presented frame.</summary>
    public bool ReleasePixelBuffer()
    {
        if (_disposed || _dib == IntPtr.Zero || IsVisible && !ClickThrough) return false;
        NativeMethods.SelectObject(_memDc, _oldBitmap);
        NativeMethods.DeleteObject(_dib);
        _dib = _bits = IntPtr.Zero;
        return true;
    }

    private void SuppressSystemInputFeedback()
    {
        int disabled = 0; // BOOL FALSE
        uint[] kinds =
        {
            FEEDBACK_TOUCH_CONTACTVISUALIZATION, FEEDBACK_PEN_TAP, FEEDBACK_PEN_DOUBLETAP,
            FEEDBACK_PEN_PRESSANDHOLD, FEEDBACK_PEN_RIGHTTAP, FEEDBACK_TOUCH_TAP,
            FEEDBACK_TOUCH_DOUBLETAP, FEEDBACK_TOUCH_PRESSANDHOLD, FEEDBACK_TOUCH_RIGHTTAP,
            FEEDBACK_GESTURE_PRESSANDHOLD,
        };
        foreach (var kind in kinds)
            SetWindowFeedbackSetting(_hwnd, kind, 0, sizeof(int), ref disabled);
    }

    /// <summary>
    /// True when another pointer message for this window is already queued. The presenter uses
    /// this to skip a stale present: the newer message will present the latest state, so no
    /// latency is added — work is only dropped when the pump is behind the digitizer.
    /// </summary>
    public bool HasQueuedPointerMessage()
        => PeekMessageW(out _, _hwnd, NativeMethods.WM_POINTERUPDATE, NativeMethods.WM_POINTERUP,
            PM_NOREMOVE | PM_NOYIELD);

    public IntPtr Handle => _hwnd;
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

/// <summary>Logical DIPs per physical screen pixel on the monitor this overlay covers.</summary>
    public float DpiScale { get; private set; } = 1f;

    /// <summary>Writable premultiplied-BGRA surface, <c>Width*Height*4</c> bytes, top-down.</summary>
    public IntPtr PixelBuffer => _bits;

    public bool IsVisible { get; private set; }

    /// <summary>Diagnostics: how many input messages actually reached this window.</summary>
    public long PointerMessageCount { get; private set; }

    public long PointerDownCount { get; private set; }

    public long PointerMoveCount { get; private set; }

    public long PointerUpCount { get; private set; }

    public long MouseMessageCount { get; private set; }

    /// <summary>Whether the mouse is being delivered as WM_POINTER (vs WM_MOUSE fallback).</summary>
    public static bool MouseAsPointer => _mouseAsPointer;

    /// <summary>When true, clicks fall through to whatever is underneath (screen-pen idle mode).</summary>
    public bool ClickThrough
    {
        get => NativeMethods.HasExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT);
        set
        {
            if (value) NativeMethods.AddExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT);
            else NativeMethods.RemoveExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT);
        }
    }

    public bool Topmost
    {
        get => NativeMethods.HasExStyle(_hwnd, NativeMethods.WS_EX_TOPMOST);
        set => NativeMethods.SetWindowPos(_hwnd,
            value ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Repair ordering only when the presentation has moved above the ink surface.</summary>
    public void EnsureAbove(nint presentation)
    {
        if (!IsVisible || presentation == 0 || !NativeMethods.IsWindowVisible(presentation)) return;
        for (var above = NativeMethods.GetWindow(_hwnd, 3 /* GW_HWNDPREV */); above != 0;
            above = NativeMethods.GetWindow(above, 3))
        {
            if (above != presentation) continue;
            Topmost = true;
            return;
        }
    }

    public event Action<OverlayPointer> PointerDown;
    public event Action<OverlayPointer> PointerMove;
    public event Action<OverlayPointer> PointerUp;
    public event Action<OverlayPointer> PointerCanceled;
    public event Action<OverlayPointer> PointerHover;
    public event Action<uint> PointerExited;

    /// <summary>
    /// Raised on the UI thread whenever input lands on the overlay. Receiving a click moves a
    /// topmost window to the top of the topmost Z-band, which would put this full-screen overlay
    /// above every chrome window; the shell re-asserts the chrome above it when this fires.
    /// </summary>
    public event Action InputBegan;

    /// <summary>Raised for WM_HOTKEY; the argument is the registration id.</summary>
    public event Action<int> HotkeyPressed;

    public event Action DisplaySettingsChanged;

    public void CancelInput()
    {
        foreach (var pointer in _activePointers.Values.ToArray()) EndPointer(pointer.Id, false, canceled: true);
        if (_fallbackMouse is { } mouse) { _fallbackMouse = null; RaiseCanceled(mouse); }
        if (NativeMethods.GetCapture() == Handle) NativeMethods.ReleaseCapture();
        _legacyContacts.Clear();
    }

    public void SetScreenLayout(IEnumerable<ScreenInfo> screens)
    {
        var updated = screens.ToArray();
        if (_screens.SequenceEqual(updated)) return;
        CancelInput();
        _screens = updated;
    }

    public void SetInputAreas(IEnumerable<ScreenRect> areas, bool force = false)
    {
        var updated = areas?.ToArray();
        if (!force && (_inputAreas is null && updated is null || _inputAreas is not null && updated is not null && _inputAreas.SequenceEqual(updated))) return;
        CancelInput();
        IntPtr region = IntPtr.Zero;
        if (updated is not null)
        {
            region = CreateRectRgn(0, 0, 0, 0);
            foreach (var area in updated)
            {
                var part = CreateRectRgn(area.X - X, area.Y - Y, area.X + area.Width - X, area.Y + area.Height - Y);
                CombineRgn(region, region, part, 2 /* RGN_OR */);
                NativeMethods.DeleteObject(part);
            }
        }
        if (SetWindowRgn(Handle, region, true) == 0)
        {
            if (region != IntPtr.Zero) NativeMethods.DeleteObject(region);
            throw new InvalidOperationException("无法设置书写显示器范围。");
        }
        _inputAreas = updated;
    }

    public bool AllowsInput(float screenX, float screenY) => _inputAreas is null || _inputAreas.Any(area =>
        screenX >= area.X && screenY >= area.Y && screenX < area.X + area.Width && screenY < area.Y + area.Height);


    public void Show()
    {
        if (IsVisible) return;
        EnsurePixelBuffer();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        IsVisible = true;
    }

    public void Hide()
    {
        if (!IsVisible) return;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        IsVisible = false;
    }

/// <summary>
    /// Repositions and/or resizes the overlay without touching click-through, topmost or
    /// visibility. The DIB surface is only reallocated when the size actually changes, and
    /// <see cref="PixelBuffer"/> always points at the current allocation afterwards.
    /// </summary>
    public void SetBounds(int x, int y, int width, int height)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        if (width <= 0 || height <= 0) return;
        _ = checked(width * height * 4);

        bool sizeChanged = width != Width || height != Height;
        if (sizeChanged && !RebuildSurface(width, height)) return;

        bool boundsChanged = x != X || y != Y || sizeChanged;
        if (boundsChanged)
        {
            CancelInput();
            X = x; Y = y; Width = width; Height = height;
            if (_inputAreas is not null) SetInputAreas(_inputAreas, force: true);
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        DpiScale = dpi == 0 ? 1.0f : dpi / 96.0f;
    }

    /// <summary>Replaces the bitmap-backed drawing surface; leaves state untouched on failure.</summary>
    private bool RebuildSurface(int width, int height)
    {
        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            if (_memDc == IntPtr.Zero)
                _memDc = NativeMethods.CreateCompatibleDC(screen);

            if (_memDc == IntPtr.Zero) return false;

            var bi = new NativeMethods.BITMAPINFO
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFO>(),
                biWidth = width,
                biHeight = -height,   // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };

            // Allocate the replacement first so a failure keeps the current surface usable.
            IntPtr newDib = NativeMethods.CreateDIBSection(screen, ref bi, 0, out IntPtr newBits, IntPtr.Zero, 0);
            if (newDib == IntPtr.Zero || newBits == IntPtr.Zero)
            {
                if (newDib != IntPtr.Zero) NativeMethods.DeleteObject(newDib);
                return false;
            }

            if (_oldBitmap != IntPtr.Zero)
                NativeMethods.SelectObject(_memDc, _oldBitmap);
            if (_dib != IntPtr.Zero)
                NativeMethods.DeleteObject(_dib);

            _dib = newDib;
            _bits = newBits;
            IntPtr previous = NativeMethods.SelectObject(_memDc, _dib);
            if (previous != IntPtr.Zero) _oldBitmap = previous;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }


    /// <summary>Push the current pixel buffer to the screen with per-pixel alpha.</summary>
    public void Present()
    {
        if (_disposed || _hwnd == IntPtr.Zero || _bits == IntPtr.Zero) return;
        var dst = new NativeMethods.POINT { X = X, Y = Y };
        var src = new NativeMethods.POINT { X = 0, Y = 0 };
        var size = new NativeMethods.SIZE { cx = Width, cy = Height };
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = 0,               // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,           // AC_SRC_ALPHA -> honour per-pixel alpha
        };
        // Screen DC is cached for the window lifetime: one less P/Invoke pair per presented frame.
        NativeMethods.UpdateLayeredWindow(_hwnd, _screenDc, ref dst, ref size, _memDc, ref src, 0,
            ref blend, NativeMethods.ULW_ALPHA);
        if (!IsVisible) Show();
    }

    // Latency contract: pointer messages are handled synchronously on the UI thread — Raise
    // calls the presenter, which renders and calls UpdateLayeredWindow before this WndProc
    // returns. There is deliberately no dispatcher hop or timer batching on this path.
    private IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case NativeMethods.WM_SETCURSOR when (l.ToInt64() & 0xFFFF) == NativeMethods.HTCLIENT && !ClickThrough:
                // A no-activate overlay must replace the cursor inherited from the window
                // beneath it, which may still be displaying an application-starting spinner.
                NativeMethods.SetCursor(ArrowCursor);
                return new IntPtr(1);
            case NativeMethods.WM_DISPLAYCHANGE:
            case 0x02E0: // WM_DPICHANGED. The shared ink viewport deliberately keeps a stable scale.
            case 0x001A: // WM_SETTINGCHANGE, including taskbar / work-area changes.
            case 0x0219 when w.ToInt64() is 0x0007 or 0x0018: // WM_DEVICECHANGE, device nodes / dock configuration.
                DisplaySettingsChanged?.Invoke();
                break;
            case NativeMethods.WM_MOUSEACTIVATE:
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            case NativeMethods.WM_POINTERACTIVATE:
                return new IntPtr(NativeMethods.PA_NOACTIVATE);
            case NativeMethods.WM_POINTERDOWN:
                PointerMessageCount++; PointerDownCount++;
                BeginPointer(w);
                return IntPtr.Zero;
            case 0x0240: // WM_TOUCH, supplied by legacy infrared digitizers.
                try
                {
                    uint count = (uint)(w.ToInt64() & 0xFFFF);
                    var contacts = new TouchInput[count];
                    if (GetTouchInputInfo(l, count, contacts, Marshal.SizeOf<TouchInput>()))
                        foreach (var contact in contacts) ProcessTouch(contact);
                }
                finally { CloseTouchInputHandle(l); }
                return IntPtr.Zero;
            case NativeMethods.WM_POINTERUPDATE:
                PointerMessageCount++; PointerMoveCount++;
                MovePointer(w);
                return IntPtr.Zero;
            case 0x0249: // WM_POINTERENTER
                if (TryReadPointer((uint)(w.ToInt64() & 0xFFFF), out var entered)
                    && entered.Kind != OverlayPointerKind.Touch) PointerHover?.Invoke(entered);
                return IntPtr.Zero;
            case 0x024A: // WM_POINTERLEAVE
                PointerExited?.Invoke((uint)(w.ToInt64() & 0xFFFF));
                return IntPtr.Zero;
            case NativeMethods.WM_POINTERUP:
                PointerMessageCount++;
                EndPointer((uint)(w.ToInt64() & 0xFFFF), readCurrent: true);
                return IntPtr.Zero;
            case NativeMethods.WM_POINTERCAPTURECHANGED:
                // GetPointerInfo can fail once capture has changed. Finish from the last
                // valid sample rather than leaving a live stroke attached to the old pointer.
                EndPointer((uint)(w.ToInt64() & 0xFFFF), readCurrent: false, canceled: true);
                return IntPtr.Zero;
            case 0x0215: // WM_CAPTURECHANGED (mouse capture)
            case 0x001F: // WM_CANCELMODE
                foreach (var pointer in _activePointers.Values.ToArray())
                    if (pointer.Kind == OverlayPointerKind.Mouse || msg == 0x001F)
                        EndPointer(pointer.Id, readCurrent: false, canceled: true);
                if (_fallbackMouse is { } mouse)
                {
                    _fallbackMouse = null;
                    RaiseCanceled(mouse);
                }
                return IntPtr.Zero;
            case NativeMethods.WM_HOTKEY:
                HotkeyPressed?.Invoke(w.ToInt32());
                return IntPtr.Zero;

            // Fallback for the case where mouse-in-pointer could not be enabled: without it the
            // mouse never produces pointer messages and drawing would silently do nothing.
            case NativeMethods.WM_LBUTTONDOWN when !_mouseAsPointer:
                InputBegan?.Invoke();
                RaiseMouse(PointerDown, l, mouseDown: true);
                return IntPtr.Zero;
            case NativeMethods.WM_MOUSEMOVE when !_mouseAsPointer:
                TrackMouseLeave();
                RaiseMouse(_fallbackMouse.HasValue ? PointerMove : PointerHover, l, mouseDown: false);
                return IntPtr.Zero;
            case 0x02A3: // WM_MOUSELEAVE
                _trackingMouseLeave = false;
                PointerExited?.Invoke(1);
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONUP when !_mouseAsPointer:
                RaiseMouse(PointerUp, l, mouseDown: false, mouseUp: true);
                return IntPtr.Zero;
            case NativeMethods.WM_DESTROY:
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(h, msg, w, l);
    }

    private void RaiseMouse(Action<OverlayPointer> handler, IntPtr lParam, bool mouseDown, bool mouseUp = false)
    {
        if ((GetMessageExtraInfo().ToInt64() & 0xFFFFFF00L) == 0xFF515700L) return;
        MouseMessageCount++;
        if (handler is null) return;
        int raw = lParam.ToInt32();
        short x = (short)(raw & 0xFFFF);
        short y = (short)((raw >> 16) & 0xFFFF);
        if (!AllowsInput(X + x, Y + y))
        {
            if (_fallbackMouse is { } last)
            {
                _fallbackMouse = null; PointerUp?.Invoke(last);
                if (NativeMethods.GetCapture() == Handle) NativeMethods.ReleaseCapture();
            }
            return;
        }
        var sample = new OverlayPointer(1, x / DpiScale, y / DpiScale, 0.5f, 0, 0,
            OverlayPointerKind.Mouse, (uint)Environment.TickCount,
            NativeMethods.GetKeyState(0x10) < 0, NativeMethods.GetKeyState(0x11) < 0);
        if (mouseUp)
        {
            if (!_fallbackMouse.HasValue) return;
            _fallbackMouse = null;
            handler(sample);
            if (NativeMethods.GetCapture() == Handle) NativeMethods.ReleaseCapture();
        }
        else
        {
            if (mouseDown || _fallbackMouse.HasValue) _fallbackMouse = sample;
            if (mouseDown) NativeMethods.SetCapture(Handle);
            handler(sample);
        }
    }

    private void BeginPointer(IntPtr wParam)
    {
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!TryReadPointer(id, out var sample)) return;
        if (!AllowsInput(X + sample.X * DpiScale, Y + sample.Y * DpiScale)) return;
        if (_activePointers.ContainsKey(id)) EndPointer(id, readCurrent: false, canceled: true);
        _activePointers[id] = sample;
        // Establish controller state before callbacks that can synchronously change focus
        // or capture (closing a popup, presenting ink, or raising another window).
        PointerDown?.Invoke(sample);
        InputBegan?.Invoke();
        // Touch/pen have implicit pointer capture. Mouse-in-pointer still needs mouse
        // capture so dragging over a toolbar HWND keeps delivering its final up here.
        if (_activePointers.ContainsKey(id) && sample.Kind == OverlayPointerKind.Mouse)
            NativeMethods.SetCapture(Handle);
    }

    private void MovePointer(IntPtr wParam)
    {
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!TryReadPointer(id, out var sample)) return;
        if (_activePointers.TryGetValue(id, out var previous)) sample = sample with { InputSurface = previous.InputSurface };
        if (!AllowsInput(X + sample.X * DpiScale, Y + sample.Y * DpiScale))
        { EndPointer(id, readCurrent: false); return; }
        if (!_activePointers.ContainsKey(id))
        {
            if (sample.Kind != OverlayPointerKind.Touch) PointerHover?.Invoke(sample);
            return;
        }
        _activePointers[id] = sample;
        if (NativeMethods.GetPointerInfo(id, out var info)
            && ((info.pointerFlags & 0x8000 /* CANCELED */) != 0
                || (info.pointerFlags & 0x0004 /* INCONTACT */) == 0))
        {
            EndPointer(id, readCurrent: false, canceled: (info.pointerFlags & 0x8000) != 0);
            return;
        }
        if (info.historyCount > 1)
        {
            const int maximumHistory = 256;
            uint count = Math.Min(info.historyCount, maximumHistory);
            if (sample.Kind == OverlayPointerKind.Pen)
            {
                _penHistory ??= new NativeMethods.POINTER_PEN_INFO[maximumHistory];
                if (NativeMethods.GetPointerPenInfoHistory(id, ref count, _penHistory))
                for (int i = (int)Math.Min(count, maximumHistory) - 1; i > 0; i--)
                {
                    var pen = _penHistory[i];
                    if (!AllowsInput(pen.pointerInfo.ptPixelLocation.X, pen.pointerInfo.ptPixelLocation.Y)) continue;
                    var point = TranslateHistory(sample, pen.pointerInfo) with
                    {
                        Pressure = (pen.penMask & 1) != 0 ? Math.Clamp(pen.pressure / 1024f, 0, 1) : .5f,
                        HasPressure = (pen.penMask & 1) != 0,
                        TiltX = (pen.penMask & 4) != 0 ? Math.Clamp(pen.tiltX, -89, 89) : 0,
                        TiltY = (pen.penMask & 8) != 0 ? Math.Clamp(pen.tiltY, -89, 89) : 0,
                    };
                    PointerMove?.Invoke(point);
                }
            }
            else if (sample.Kind == OverlayPointerKind.Touch)
            {
                _touchHistory ??= new NativeMethods.POINTER_TOUCH_INFO[maximumHistory];
                if (NativeMethods.GetPointerTouchInfoHistory(id, ref count, _touchHistory))
                for (int i = (int)Math.Min(count, maximumHistory) - 1; i > 0; i--)
                {
                    var touch = _touchHistory[i];
                    if (!AllowsInput(touch.pointerInfo.ptPixelLocation.X, touch.pointerInfo.ptPixelLocation.Y)) continue;
                    var screen = ScreenAt(touch.pointerInfo.ptPixelLocation.X, touch.pointerInfo.ptPixelLocation.Y);
                    var point = TranslateHistory(sample, touch.pointerInfo) with
                    {
                        ContactWidth = (float)((touch.rcContact.Right - touch.rcContact.Left) / screen.DpiScale),
                        ContactHeight = (float)((touch.rcContact.Bottom - touch.rcContact.Top) / screen.DpiScale),
                    };
                    PointerMove?.Invoke(point);
                }
            }
        }
        PointerMove?.Invoke(sample);
    }

    private OverlayPointer TranslateHistory(OverlayPointer current, NativeMethods.POINTER_INFO info)
        => current with { X = (info.ptPixelLocation.X - X) / DpiScale,
            Y = (info.ptPixelLocation.Y - Y) / DpiScale, TimeMs = info.dwTime, IsIntermediate = true };

    private void TrackMouseLeave()
    {
        if (_trackingMouseLeave) return;
        var tracking = new TRACKMOUSEEVENT { Size = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = 2, Window = Handle };
        _trackingMouseLeave = TrackMouseEvent(ref tracking);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public uint Size, Flags; public IntPtr Window; public uint HoverTime; }
    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tracking);

    private void EndPointer(uint id, bool readCurrent, bool canceled = false)
    {
        if (!_activePointers.Remove(id, out var sample)) return;
        if (readCurrent && TryReadPointer(id, out var current) && AllowsInput(X + current.X * DpiScale, Y + current.Y * DpiScale))
            sample = current with { InputSurface = sample.InputSurface };
        if (readCurrent && NativeMethods.GetPointerInfo(id, out var info) && (info.pointerFlags & 0x8000) != 0) canceled = true;
        PointerUpCount++;
        if (canceled) RaiseCanceled(sample);
        else PointerUp?.Invoke(sample);
        if (sample.Kind == OverlayPointerKind.Mouse && NativeMethods.GetCapture() == Handle)
            NativeMethods.ReleaseCapture();
    }

    private void RaiseCanceled(OverlayPointer sample)
    {
        if (PointerCanceled is not null) PointerCanceled.Invoke(sample);
        else PointerUp?.Invoke(sample);
    }

    private bool TryReadPointer(uint id, out OverlayPointer sample)
    {
        sample = default;
        if (!NativeMethods.GetPointerInfo(id, out var info)) return false;
        var screen = ScreenAt(info.ptPixelLocation.X, info.ptPixelLocation.Y);

        float pressure = 0.5f, cw = 0, ch = 0, tiltX = 0, tiltY = 0;
        bool hasPressure = false;
        var kind = OverlayPointerKind.Mouse;

        if (info.pointerType == NativeMethods.POINTER_INPUT_TYPE_PEN)
        {
            kind = OverlayPointerKind.Pen;
            if (NativeMethods.GetPointerPenInfo(id, out var pen))
            {
                hasPressure = (pen.penMask & 1) != 0;
                pressure = hasPressure ? Math.Clamp(pen.pressure / 1024f, 0f, 1f) : .5f;
                if ((pen.penMask & 4) != 0) tiltX = Math.Clamp(pen.tiltX, -89, 89);
                if ((pen.penMask & 8) != 0) tiltY = Math.Clamp(pen.tiltY, -89, 89);
            }
        }
        else if (info.pointerType == NativeMethods.POINTER_INPUT_TYPE_TOUCH)
        {
            kind = OverlayPointerKind.Touch;
            if (NativeMethods.GetPointerTouchInfo(id, out var touch))
            {
                cw = (float)((touch.rcContact.Right - touch.rcContact.Left) / screen.DpiScale);
                ch = (float)((touch.rcContact.Bottom - touch.rcContact.Top) / screen.DpiScale);
                pressure = touch.pressure == 0 ? 0.5f : Math.Clamp(touch.pressure / 1024f, 0f, 1f);
            }
        }

        var pt = info.ptPixelLocation;
        sample = new OverlayPointer(id, (pt.X - X) / DpiScale, (pt.Y - Y) / DpiScale,
            pressure, cw, ch, kind, info.dwTime, NativeMethods.GetKeyState(0x10) < 0, NativeMethods.GetKeyState(0x11) < 0)
            { InputSurface = screen.Handle.ToInt64(), HasPressure = hasPressure, TiltX = tiltX, TiltY = tiltY };
        return true;
    }

    internal void ProcessTouch(TouchInput contact)
    {
        var key = (contact.Source, contact.Id);
        float x = contact.X / 100f, y = contact.Y / 100f;
        bool down = (contact.Flags & 2) != 0, up = (contact.Flags & 4) != 0;
        if (down)
        {
            if (ClickThrough || !AllowsInput(x, y)) return;
            if (_legacyContacts.Remove(key, out uint old)) EndPointer(old, false, canceled: true);
            _legacyContacts[key] = ++_nextLegacyId;
        }
        if (!_legacyContacts.TryGetValue(key, out uint id)) return;
        var screen = ScreenAt(x, y);
        var sample = new OverlayPointer(id, (x - X) / DpiScale, (y - Y) / DpiScale, .5f,
            (float)(contact.Width / 100d / screen.DpiScale), (float)(contact.Height / 100d / screen.DpiScale),
            OverlayPointerKind.Touch, contact.Time) { InputSurface = screen.Handle.ToInt64() };
        if (!down && _activePointers.TryGetValue(id, out var previous)) sample = sample with { InputSurface = previous.InputSurface };
        if (up || !AllowsInput(x, y))
        {
            if (AllowsInput(x, y)) _activePointers[id] = sample;
            _legacyContacts.Remove(key); EndPointer(id, false);
        }
        else
        {
            _activePointers[id] = sample;
            if (down) { PointerDownCount++; PointerDown?.Invoke(sample); InputBegan?.Invoke(); }
            else PointerMove?.Invoke(sample);
        }
        PointerMessageCount++;
    }

    private ScreenInfo ScreenAt(float x, float y)
    {
        foreach (var screen in _screens)
            if (x >= screen.Bounds.X && y >= screen.Bounds.Y && x < screen.Bounds.X + screen.Bounds.Width
                && y < screen.Bounds.Y + screen.Bounds.Height) return screen;
        return ScreenService.FromRect(new((int)x, (int)y, 1, 1));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_memDc != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memDc, _oldBitmap);
            NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { NativeMethods.DeleteObject(_dib); _dib = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        if (_screenDc != IntPtr.Zero) { NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { NativeMethods.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }
}
