using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.System;
using InkCanvas.Platform.Windows;

namespace InkCanvas.App.Services;

/// <summary>
/// Owns the transparent screen-annotation surface: the layered window, its presenter and the
/// global hotkeys registered against it.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private Action _undo, _redo, _togglePen, _toggleEraser, _clear;
    private Action _togglePassThrough, _toggleBoard;
    private Action _pptNext, _pptPrevious, _pptExit, _toggleBlackScreen;
    private Action _screenshot, _fold, _select, _line, _draw, _ai;
    private Action _sideBoard;
    private string _sideBoardModifiers, _sideBoardKey;
    private Func<bool> _editingEnabled, _overlayEditingEnabled, _presentationEnabled;
    private bool _presentationKeysEnabled;

    private Core.Settings.DisplaySettings _displaySettings = new();
    private long _idleSince;
    private bool _idleResourcesReleased;

    public void ConfigureDisplays(Core.Settings.DisplaySettings settings)
    {
        _displaySettings = settings ?? new();
        RefreshDisplayBounds();
    }
    public OverlayHost(InkController controller)
    {
        var screens = ScreenService.All();
        var screen = screens.Count == 0 ? ScreenService.Virtual : ScreenService.GetVirtualBounds(screens);
        Window = new LayeredInkWindow(screen.X, screen.Y, screen.Width, screen.Height);
        Window.SetScreenLayout(screens);
        Presenter = new OverlayInkPresenter(Window, controller);
        Hotkeys = new HotkeyService(Window.Handle);
        Controller = controller;
        controller.ViewCenter = new System.Numerics.Vector2(
            screen.Width / Window.DpiScale / 2f, screen.Height / Window.DpiScale / 2f);

        Window.HotkeyPressed += id => Hotkeys.Dispatch(id);
        Presenter.Attach();
    }

    public LayeredInkWindow Window { get; }

    public OverlayInkPresenter Presenter { get; }

    public HotkeyService Hotkeys { get; }

    public InkController Controller { get; }
    public event Action InteractionChanged;

    /// <summary>Keep saved ink at the same desktop pixel when the virtual desktop origin changes.</summary>
    public bool RefreshDisplayBounds() => RefreshDisplayBounds(ScreenService.All());

    internal bool RefreshDisplayBounds(IReadOnlyList<ScreenInfo> screens)
    {
        // Enumeration may be empty briefly while Windows applies a projection mode.
        var screen = ScreenService.GetVirtualBounds(screens);
        if (screen.Width <= 0 || screen.Height <= 0) return false;
        Window.SetScreenLayout(screens);
        bool resized = SetDisplayBounds(screen);
        // A monitor split, DPI or enabled area can change without changing the outer bounds.
        Window.SetInputAreas(_displaySettings.AllDisplays ? null : screens
            .Where(item => (_displaySettings.EnabledDevices ?? new()).Contains(item.DeviceName, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.Bounds));
        return resized;
    }

    internal bool SetDisplayBounds(ScreenRect screen)
    {
        if (screen == new ScreenRect(Window.X, Window.Y, Window.Width, Window.Height)) return false;
        bool visible = Window.IsVisible;
        Window.CancelInput();
        Presenter.Suspend();
        int oldX = Window.X, oldY = Window.Y;
        try
        {
            Window.SetBounds(screen.X, screen.Y, screen.Width, screen.Height);
            Controller.ViewTransform *= System.Numerics.Matrix3x2.CreateTranslation(
                (oldX - screen.X) / Window.DpiScale, (oldY - screen.Y) / Window.DpiScale);
            Controller.ViewCenter = new(screen.Width / Window.DpiScale / 2, screen.Height / Window.DpiScale / 2);
            Presenter.SetScreenTrackingClip(null, false);
        }
        finally { if (visible) Presenter.Attach(); }
        return true;
    }

    public bool ClickThrough
    {
        get => Window.ClickThrough;
        set => SetInteractionMode(value, inkVisible: !value);
    }

    public void SetInteractionMode(bool clickThrough, bool inkVisible)
    {
        if (inkVisible && !clickThrough) { _idleResourcesReleased = false; _idleSince = 0; }
        if (clickThrough || !inkVisible) Controller.CancelLassoErase();
        if (clickThrough || !inkVisible) Controller.HideEraserPreview();
        Window.ClickThrough = clickThrough;
        Presenter.InkVisible = inkVisible;
        InteractionChanged?.Invoke();
    }

    public void Show()
    {
        _idleResourcesReleased = false;
        _idleSince = 0;
        Window.Show();
        Presenter.Attach();
        InteractionChanged?.Invoke();
    }

    public void Hide()
    {
        Controller.CancelLassoErase();
        Presenter.Suspend();
        Window.Hide();
        InteractionChanged?.Invoke();
    }

    /// <summary>
    /// Re-anchors the ink surface to a slideshow window. While a show runs the visible
    /// picture can sit on a different monitor than the primary one, so both the bounds and
    /// the world centre have to follow it.
    /// </summary>
    public void AlignTo(ShowWindowInfo bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        Window.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Controller.ViewCenter = new System.Numerics.Vector2(
            Window.Width / Window.DpiScale / 2f, Window.Height / Window.DpiScale / 2f);
        if (Window.IsVisible) Presenter.Attach();
    }

    /// <summary>Returns the ink surface to the primary screen once the show ends.</summary>
    public void ResetToPrimary()
    {
        var screen = ScreenService.Primary;
        Window.SetBounds(screen.X, screen.Y, screen.Width, screen.Height);
        Controller.ViewCenter = new System.Numerics.Vector2(
            Window.Width / Window.DpiScale / 2f, Window.Height / Window.DpiScale / 2f);
        if (Window.IsVisible) Presenter.Attach();
    }

    internal bool TrimIdleResources(bool force = false)
    {
        if (Window.IsVisible && Presenter.InkVisible)
        {
            _idleSince = 0;
            _idleResourcesReleased = false;
            return false;
        }
        if (_idleResourcesReleased || Controller.HasActiveInteraction) return false;
        if (_idleSince == 0) _idleSince = Environment.TickCount64;
        if (!force && Environment.TickCount64 - _idleSince < 15_000) return false;
        Presenter.ReleaseIdleResources();
        Window.ReleasePixelBuffer();
        _idleResourcesReleased = true;
        return true;
    }

    /// <summary>
    /// Registers the union of the local teaching shortcuts and the WPF guide's set
    /// (OperatingGuideWindow rows): Ctrl+Z/Y undo/redo, Ctrl+Shift+C clear, Alt+C screenshot,
    /// Alt+V fold, Alt+D draw/palette, Alt+Q (and the local Alt+P) mouse mode, Alt+B board,
    /// Alt+S select, Alt+E eraser, Alt+L line, Alt+1 pen, Alt+A AI 助教, Alt+N 板中板,
    /// F9 black screen.
    /// PageUp/PageDown/Esc/B are registered separately while a slideshow runs.
    /// Failures are non-fatal (key may be taken).
    /// </summary>
    public void RegisterHotkeys(Action undo, Action redo, Action toggleEraser, Action clear,
        Action togglePassThrough = null, Action toggleBoard = null,
        Action pptNext = null, Action pptPrevious = null, Action pptExit = null,
        Action screenshot = null, Action fold = null, Action select = null, Action line = null, Action draw = null,
        Func<bool> editingEnabled = null, Func<bool> overlayEditingEnabled = null, Func<bool> presentationEnabled = null,
        Action togglePen = null, Action toggleBlackScreen = null, Action ai = null,
        Action sideBoard = null, string sideBoardModifiers = null, string sideBoardKey = null)
    {
        _undo = undo;
        _redo = redo;
        _togglePen = togglePen;
        _toggleEraser = toggleEraser;
        _clear = clear;
        _togglePassThrough = togglePassThrough;
        _toggleBoard = toggleBoard;
        _pptNext = pptNext;
        _pptPrevious = pptPrevious;
        _pptExit = pptExit;
        _toggleBlackScreen = toggleBlackScreen;
        _screenshot = screenshot;
        _fold = fold;
        _select = select;
        _line = line;
        _draw = draw;
        _ai = ai;
        _sideBoard = sideBoard;
        _sideBoardModifiers = sideBoardModifiers;
        _sideBoardKey = sideBoardKey;
        _editingEnabled = editingEnabled;
        _overlayEditingEnabled = overlayEditingEnabled;
        _presentationEnabled = presentationEnabled;
        // The Win32 hotkey service has no per-key removal; rebuilding avoids stale bindings.
        Hotkeys.UnregisterAll();
        RegisterBaselineHotkeys();
        if (_presentationKeysEnabled) RegisterPresentationHotkeys();
    }

    /// <summary>
    /// Registers only the keys that are valid during a slide show (Page Up/Down, Esc, B). They
    /// are deliberately absent outside a show so a browser or video player keeps its keys.
    /// Re-registering the baseline set is required because the Win32 hotkey service has no
    /// per-key removal; this runs only on show start/end.
    /// </summary>
    public bool SetPresentationKeysEnabled(bool enabled)
    {
        if (_presentationKeysEnabled == enabled) return true;
        _presentationKeysEnabled = enabled;
        Hotkeys.UnregisterAll();
        RegisterBaselineHotkeys();
        return enabled ? RegisterPresentationHotkeys() : true;
    }

    private void RegisterBaselineHotkeys()
    {
        Hotkeys.Register(HotkeyModifiers.Control, 0x5A /* Z */, _undo, _overlayEditingEnabled);
        Hotkeys.Register(HotkeyModifiers.Control, 0x59 /* Y */, _redo, _overlayEditingEnabled);
        if (_togglePen is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x31 /* 1 */, _togglePen, _editingEnabled);
        Hotkeys.Register(HotkeyModifiers.Alt, 0x45 /* E */, _toggleEraser, _editingEnabled);
        // Local teaching loop keeps Ctrl+Shift+C clear (Ctrl+C stays with the foreground app).
        Hotkeys.Register(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x43 /* C */, _clear, _overlayEditingEnabled);
        if (_select is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x53 /* S */, _select, _editingEnabled);
        if (_line is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x4C /* L */, _line, _editingEnabled);
        if (_draw is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x44 /* D */, _draw, _editingEnabled);
        if (_fold is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x56 /* V */, _fold);
        if (_screenshot is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x43 /* C */, _screenshot);
        // Escape hatches: the overlay captures the whole screen while writing, so these must
        // work even if the bar is unreachable. Alt+P is the local hotkey, Alt+Q the guide's.
        if (_togglePassThrough is not null)
        {
            Hotkeys.Register(HotkeyModifiers.Alt, 0x50 /* P */, _togglePassThrough);
            Hotkeys.Register(HotkeyModifiers.Alt, 0x51 /* Q */, _togglePassThrough);
        }
        if (_toggleBoard is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x42 /* B */, _toggleBoard);
        // AI 助教 works from the overlay and from the board (BoardWindow also handles it locally
        // as a fallback when this registration is rejected by another application).
        if (_ai is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x41 /* A */, _ai);
        // 板中板：默认 Alt+N，可在 Settings.hotkey.sideBoard 中改写；无效配置回退默认。
        if (_sideBoard is not null)
        {
            if (!SideBoardHotkey.TryParseModifiers(_sideBoardModifiers, out var sideBoardModifiers))
                sideBoardModifiers = SideBoardHotkeyModifiers.Alt;
            Hotkeys.Register((HotkeyModifiers)(uint)sideBoardModifiers,
                SideBoardHotkey.ParseVirtualKey(_sideBoardKey), _sideBoard);
        }
        // F9 is the global black-screen toggle and stays available outside a show.
        if (_toggleBlackScreen is not null) Hotkeys.Register(HotkeyModifiers.None, 0x78 /* F9 */, _toggleBlackScreen);
    }

    private bool RegisterPresentationHotkeys()
    {
        Func<bool> enabled = _presentationEnabled is null
            ? () => _presentationKeysEnabled
            : () => _presentationKeysEnabled && _presentationEnabled();
        bool ok = true;
        if (_pptNext is not null) ok &= Hotkeys.Register(HotkeyModifiers.None, 0x22 /* PageDown */, _pptNext, enabled);
        if (_pptPrevious is not null) ok &= Hotkeys.Register(HotkeyModifiers.None, 0x21 /* PageUp */, _pptPrevious, enabled);
        if (_pptExit is not null) ok &= Hotkeys.Register(HotkeyModifiers.None, 0x1B /* Escape */, _pptExit, enabled);
        if (_toggleBlackScreen is not null) ok &= Hotkeys.Register(HotkeyModifiers.None, 0x42 /* B */, _toggleBlackScreen, enabled);
        return ok;
    }


    public void Dispose()
    {
        Hotkeys.Dispose();
        Presenter.Dispose();
        Window.Dispose();
    }
}
