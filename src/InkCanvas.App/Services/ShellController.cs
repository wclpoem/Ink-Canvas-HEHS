using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Ui;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Classroom;
using InkCanvas.Features.Ppt;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Tools;
using InkCanvas.Ink;

using InkCanvas.Platform.Ai;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.System;
using InkCanvas.Platform.Windows;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using InkCanvas.Host.Contracts;
using InkCanvas.Extensibility;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

using ClassroomRoster = InkCanvas.Features.Classroom.ClassroomRoster;

namespace InkCanvas.App.Services;

/// <summary>
/// Owns the windows and implements the shell actions the floating bar invokes. Keeps App.xaml.cs
/// down to startup wiring.
/// </summary>
public sealed partial class ShellController : IShellActions, IExampleLibraryHost, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly PptInkFileStore _pptStore;
    private readonly PptInkSession _pptSession;
    private RosterWindow _roster;
    private SpotlightWindow _spotlight;
    private MagnifierWindow _magnifier;
    private ToolkitWindow _toolkit;
    private readonly NavigationGuard _pptGuard = new();
    private readonly SlideSwitchGate _pptGate = new();
    private readonly Dictionary<PptNavigationPlacement, PptNavigationWindow> _pptNavigations = new();
    private SlideNavigatorWindow _slideNav;
    private SlideContentIndex _slideIndex;
    private DateTime _lastSlideIndexRefreshUtc = DateTime.MinValue;
    private bool _pptNavCollapsed;
    private bool _manualPptNav;
    private PptSnapshot _pptShowSnapshot;
    private int _pptCaptureSession;
    private int _pptCaptureGeneration;
    private string _pptScreenshotPath;
    private int _pptScreenshotSlideIndex;
    private int _pptScreenshotSlideId;
    private readonly PresentationSourceSelector _pptSources = new();
    private readonly ComPresentationSource _pptComSource;
    private readonly WindowPresentationSource _pptWindowSource = new();
    private readonly KeyPresentationSource _pptKeySource = new();
    private BlackScreenWindow _blackScreen;
    private DateTime _lastPptRpcBusyLogUtc = DateTime.MinValue;
    private TeachingSession _teachingSession;
    private PracticePlan _activePractice;
    private GroupScoreService _groupScore;
    private GroupScoreWindow _groupScoreWindow;
    private ExampleLibraryWindow _exampleLibrary;
    private ResourceBundleWindow _resourceBundle;
    private ClassroomPreset _preset;
    private FocusRegionStore _focusRegions;
    private PresetWindow _presetWindow;

    private FloatingBarWindow _bar;
    private SelectionToolbarWindow _selectionToolbar;
    private bool _selectionToolbarCollapsed;
    private BoardWindow _board;
    private BoardWindow _cachedBoard;
    private bool _boardSwitching;
    private SettingsWindow _settings;
    private bool _settingsOpening;
    private IntPtr _settingsDrawingSurface;
    private bool _settingsSurfaceWasTopmost;
    private LotteryWindow _settingsRosterSource;
    private string _requestedSettingsCategory;
    internal string RosterDirectory { get; set; } = LotteryWindow.DataDir;
    private bool _imagePicking;
    private CancellationTokenSource _imageImportCancellation;
    private CountdownWindow _countdown;
    private LotteryWindow _lottery;
    private ReplayWindow _replay;
    private readonly List<NoticeWindow> _notices = new();
    private PenPaletteWindow _palette;
    private readonly List<PenPaletteWindow> _detachedPalettes = new();
    private ShapePickerWindow _shapePicker;
    private DispatcherQueueTimer _pptRetryTimer;
    private bool _pptRetryQueued;
    private bool _pptRetryNext;
    private bool _blackScreenEscapeHotkey;
    private bool _blackScreenEscapeFailureLogged;
    private bool _hotkeysReady;
    private bool _presentationKeyFailureLogged;
    private bool _exiting;
    private InkRecorder _recorder;
    /// <summary>子板录制器：与主录制器同一时钟起点，停止时事件并入主会话（BoardId=1）。</summary>
    private InkRecorder _sideBoardRecorder;
    private InfoWindow _guide;
    private AssistantWindow _aiAssistant;
    private AiStreamHandle _aiStream;
    private AiStreamHandle _aiRegionStream;
    private AssistantSession _aiSession;
    private AssistantTarget _aiTarget;
    private bool _aiHiddenWithSideBoard;
    private bool _aiAssistantFromSideBoard;
    /// <summary>例题「再讲一次」待发送追问；标准识别流完成后自动提交，不新建教学模式。</summary>
    private string _aiPendingExamplePrompt = "";
    private AssistantRect _aiAnchor;
    private AiClient _aiClient;
    private string _aiThumb = "";
    private ProblemContext _aiProblem;
    private List<AssistantStroke> _aiStrokes = new();
    private double _aiScale = 1.0;
    private int _aiSlideId;
    private int _aiSlideIndex;
    private bool _aiTwoStage;
    private string _aiStageFirstToken;
    private bool _aiStageReset;
    private string _aiPreStageText = "";
    private List<AssistantStep> _aiPreStageSteps = new();
    private string _aiPreStageFormula = "";
    private bool _aiSecondStageStarted;
    private readonly AiResponseGate _aiGate = new();
    private ExerciseDifficulty _variantDifficulty;
    private bool _variantPending;
    private string _reRecognizeRegionId;
    private AssistantBoardPlan _aiBoardPlan;
    private string _aiBoardInstruction = "";
    private string _aiBoardIntent = "";
    /// <summary>发起 AI 请求时的统一课堂上下文快照；Flag 关闭时仅记录、不参与判定。</summary>
    private ClassroomContext _aiContext;
    private bool _aiBoardRequestPending;
    private bool _lastSlideShowState;
    private bool _lastAutoFoldRequested;
    private bool? _manualFoldOverride;
    private bool _exitPromptOpen;
    private bool _paletteHighlighter;
    private bool _disposed;
    private InkController _boundBoardController;
    private IReadOnlyList<ScreenInfo> _screens = Array.Empty<ScreenInfo>();
    private bool _displayRefreshPending;
    private long _lastDisplayCheck;
    internal event Action ActiveControllerChanged;
    internal event Action BoardOpenChanged;
    internal event Action<ClassroomSignal> ClassroomSignal;

    public ShellController(AppServices services)
    {
        Services = services;
        services.SettingsApplied += ApplyDocumentSettings;
        services.SettingsApplied += RefreshIconFonts;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Overlay = new OverlayHost(services.OverlayInk);
        _pptStore = new PptInkFileStore(services.Settings.Automation.AutoSavedStrokesLocation);
        Overlay.InteractionChanged += RefreshShortcutRegistration;
        Overlay.ConfigureDisplays(services.Settings.Display);
        services.SettingsApplied += ApplyDisplaySettings;
        services.SettingsApplied += ApplySideBoardDisplaySettings;
        services.SettingsApplied += RefreshPaletteNibModes;
        Overlay.Window.DisplaySettingsChanged += QueueDisplayRefresh;
        _screens = ScreenService.All();
        _documentAi = new DocumentAiHost(this);
        RegisterAssistantTools();
        services.StartModules(new ScreenTrackingHost(services, Overlay, () => _board is not null || DocumentActive), new DocumentHost(this), new CanvasToolsHost(this), new AssistantHost(this), new LibraryHost(this), new TimeMachineHost(this), _documentAi);
        services.SettingsApplied += ApplyArchivePaths;
        _pptSession = new PptInkSession(services.Ppt, _pptStore)
        {
            InkManagedExternally = () => Services.Modules.HasExport<IScreenTrackingService>() && Services.Settings.Experimental.ScreenContentTracking && Services.Settings.Experimental.PowerPoint,
            CaptureCurrentInk = () => Services.OverlayInk.Document.Strokes,
            ApplyCurrentInk = strokes =>
            {
                var controller = Services.OverlayInk;
                Overlay.Presenter.BeginSceneUpdate();
                try
                {
                    Overlay.Window.CancelInput();
                    controller.EndPlayback();
                    controller.FinishDotAnimations();
                    controller.ClearSelection();
                    controller.Document.ReplaceAll(strokes);
                    controller.History.Clear();
                    _bar?.Bar.RefreshSelection();
                }
                finally { Overlay.Presenter.EndSceneUpdate(); }
            },
            AutoSavePresentationInk = () => Services.Settings.PowerPoint.IsAutoSaveStrokesInPowerPoint,
            AutoSavePptScreenshots = () => Services.Settings.PowerPoint.IsAutoSaveScreenShotInPowerPoint,
            NotifyPreviousPage = () => Services.Settings.PowerPoint.IsNotifyPreviousPage,
            NotifyHiddenSlides = () => Services.Settings.PowerPoint.IsNotifyHiddenPage,
            NotifySlideTimings = () => Services.Settings.PowerPoint.IsNotifyAutoPlayPresentation,
            AutoSaveRoot = () => Services.Settings.Automation.AutoSavedStrokesLocation,
            UseScreenshotDateFolders = () => Services.Settings.Automation.IsSaveScreenshotsInDateFolders,
            SaveInkWithScreenshot = () => Services.Settings.Automation.IsAutoSaveStrokesAtScreenshot,
            MinimumScreenshotStrokeCount = () => Services.Settings.Automation.MinimumAutomationStrokeNumber,
        };
        _pptSession.PresentationNotice += OnPptPresentationNotice;
        _pptSession.ScreenshotRequested += OnPptScreenshotRequested;
        _pptSession.InkTransition += OnPptInkTransition;
        _pptSession.Failed += LogPptFailure;
        _pptComSource = new ComPresentationSource(
            () => _pptSession.Snapshot.IsAttached,
            () => _pptSession.Next(),
            () => _pptSession.Previous());

        if (services.Settings.PowerPoint.PowerPointSupport) _pptSession.Poll();
        Updater = new UpdateCoordinator(services)
        {
            Confirm = ConfirmAsync,
            ConfirmRelease = ConfirmUpdateReleaseAsync,
            Notify = ShowUpdateNoticeAsync,
            ExitForInstall = () => { _installingUpdate = true; Exit(); },
            CanInstallSilently = CanInstallUpdateSilently,
            PrepareForInstall = PrepareForUpdateAsync,
            ChooseRelease = ChooseUpdateReleaseAsync,
        };

        _pollTimer = _dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(900);
        _pollTimer.Tick += (_, _) => Poll();

        // A flip that lands inside the navigation debounce is delivered once here instead of
        // being dropped; the latest requested direction wins and the timer never repeats.
        _pptRetryTimer = _dispatcher.CreateTimer();
        _pptRetryTimer.IsRepeating = false;
        _pptRetryTimer.Tick += (_, _) =>
        {
            _pptRetryTimer.Stop();
            _pptRetryQueued = false;
            NavigatePpt(_pptRetryNext);
        };

        _teachingSession = TeachingSession.Create("课堂", Environment.TickCount64);
        _preset = ClassroomPresetLibrary.ForId(Services.Settings.Classroom.PresetId)
            ?? ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Teach);
        _focusRegions = FocusRegionStore.Deserialize(Services.Settings.Classroom.SpotlightRegions);
        services.Ppt.RefreshRequested += QueuePresentationRefresh;
    }

    /// <summary>Raised whenever a chrome window is created (for the --shot self-check hook).</summary>
    public event Action<Window> WindowOpened;

    private void NotifyWindowOpened(Window w)
    {
        w.Activated += (_, _) => RefreshShortcutRegistration();
        if (w.Content is FrameworkElement root)
            root.GotFocus += (_, _) => RefreshShortcutRegistration();
        if (w is SettingsWindow or CountdownWindow or LotteryWindow or InfoWindow)
        {
            var owner = _documentWorkspace?.Window ?? (Window)_board ?? _bar;
            if (owner is not null) WindowChrome.CenterOnOwner(w, owner);
        }
        WindowOpened?.Invoke(w);
    }

    private void RefreshIconFonts()
    {
        foreach (var window in OpenWindows)
        {
            IconFontStyle.Refresh(window.Content);
            if (window.Content is FrameworkElement { XamlRoot: { } root })
                foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                    IconFontStyle.Refresh(popup.Child);
        }
    }

    private void QueueDisplayRefresh()
    {
        if (_displayRefreshPending || _disposed) return;
        _displayRefreshPending = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _displayRefreshPending = false;
            if (!_disposed) RefreshDisplays();
        });
    }

    private void RefreshDisplays() => RefreshDisplays(ScreenService.All());

    internal void RefreshDisplays(IReadOnlyList<ScreenInfo> screens)
    {
        _lastDisplayCheck = Environment.TickCount64;
        if (screens.Count == 0) return;
        if (_screens.SequenceEqual(screens)) return;
        try
        {
            Overlay.RefreshDisplayBounds(screens);
            _bar?.RefreshDisplayPlacement();
            _palette?.RefreshDisplayPlacement();
            foreach (var palette in _detachedPalettes) palette.RefreshDisplayPlacement();
            _shapePicker?.RefreshDisplayPlacement();
            if (_board is not null) foreach (var panel in _board.ToolPanels) panel.Fit();
            _replay?.RefreshDisplayPlacement();
            EnsureSideBoardOnScreen();
            foreach (var window in OpenWindows)
                if (window is SettingsWindow or CountdownWindow or LotteryWindow or InfoWindow or ReplayWindow or NoticeWindow or UpdateWindow
                    || _documentWindows.Contains(window))
                    WindowChrome.KeepVisible(window);
            QueueDesktopSelection();
            Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
            _screens = screens.ToArray();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"Display layout: {ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>All live chrome windows (for the --shot headless visual self-check).</summary>
    public IEnumerable<Window> OpenWindows
    {
        get
        {
            if (_bar is not null) yield return _bar;
            foreach (var navigation in _pptNavigations.Values) yield return navigation;
            if (_selectionToolbar is not null) yield return _selectionToolbar;
            if (_board is not null) yield return _board;
            if (_documentWorkspace is not null) yield return _documentWorkspace.Window;
            foreach (var window in _documentWindows) if (!ReferenceEquals(window, _documentWorkspace?.Window)) yield return window;
            if (_settings is not null) yield return _settings;
            if (_countdown is not null) yield return _countdown;
            if (_lottery is not null) yield return _lottery;
            if (_replay is not null) yield return _replay;
            if (_updateWindow is not null) yield return _updateWindow;
            foreach (var notice in _notices) yield return notice;
            if (_palette is not null) yield return _palette;
            foreach (var palette in _detachedPalettes) yield return palette;
            if (_aiAssistant is not null) yield return _aiAssistant;
            if (_shapePicker is not null) yield return _shapePicker;
        }
    }

    public AppServices Services { get; }

    public OverlayHost Overlay { get; }

    public UpdateCoordinator Updater { get; }

    /// <summary>Diagnostics for the floating bar's window-vs-content sizing.</summary>
    public string BarDiagnostics
    {
        get
        {
            if (_bar is null) return "no bar";
            var size = _bar.AppWindow.Size;
            var client = _bar.AppWindow.ClientSize;
            var desired = _bar.Bar.DesiredSize;
            return $"window={size.Width}x{size.Height}; client={client.Width}x{client.Height}; "
                 + $"dpi={WindowChrome.GetDpiScale(_bar):0.##}; desired={desired.Width:0.#}x{desired.Height:0.#}; "
                 + $"backdrop={(_bar.SystemBackdrop as ChromeBackdrop)?.State}; "
                 + $"primaryChildren={_bar.Bar.PrimaryChildCount}; computedRow={_bar.Bar.ComputedPrimaryWidth:0.#}";
        }
    }

    public void Start()
    {
        _bar = new FloatingBarWindow();
        NotifyWindowOpened(_bar);
        _bar.Bar.Settings = Services.Settings;
        _bar.Configure(Services.OverlayInk, this);
        _displayedExtensionCommands = ExtensionCommands.ToArray();
        Services.Plugins.Changed += OnExtensionCommandsChanged;
        Services.Modules.Changed += OnExtensionCommandsChanged;
        _ = StartPluginsAsync();
        _bar.Bar.LayoutChanged += OnBarLayoutChanged;
        _bar.Bar.SettingsEdited += () => { Services.ApplySettings(); Services.SaveSettings(); };
        _bar.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                        Services.Settings.Appearance.FloatingBarScale);
        _bar.ApplyTheme(Services.Settings.Appearance.Theme);
        WindowChrome.ShowWithoutActivation(_bar);

        Services.OverlayInk.Invalidated += QueueDesktopSelection;
        Services.OverlayInk.Invalidated += QueueDesktopMedia;
        Services.ScreenImages.ImagesChanged += QueueDesktopMedia;
        Services.ScreenImages.PageChanged += QueueDesktopMedia;
        Services.ScreenImages.MediaPlacementChanged += OnDesktopMediaPlacement;
        Services.OverlayInk.SelectionChanged += QueueDesktopSelection;
        _bar.AppWindow.Changed += OnBarWindowChanged;

        Overlay.Show();
        // A click on the overlay raises it to the top of the topmost Z-band, above every chrome
        // window. Re-assert the chrome immediately so close buttons and title bars stay clickable.
        Overlay.Window.InputBegan += () =>
        {
            RaiseChromeAboveOverlay();
            _bar?.DismissPanelsOnInput();
            // WPF HideSubPanels: laying ink dismisses the pen/shape popups (pin only survives picks).
            _palette?.DismissOnOverlayInput();
            _shapePicker?.DismissOnOverlayInput();
        };
        if (Services.Settings.Startup.IsFoldAtStartup) _bar.SetFolded(true);

        RegisterOverlayHotkeys();

        // A show may already be running when the bar is created; page keys are only
        // registered during a show so they are not stolen from other apps.
        _hotkeysReady = true;
        if (_lastSlideShowState && !Overlay.SetPresentationKeysEnabled(true))
            Core.Diagnostics.FileLog.WriteLogToFile("PPT 翻页热键注册失败（可能被其他程序占用）", Core.Diagnostics.LogType.Info);

        // F9 is already registered by RegisterOverlayHotkeys; keep B/Esc as show-time conveniences.
        Overlay.Hotkeys.Register(HotkeyModifiers.None, 0x42 /* B */, ToggleBlackScreen, () => PresentationShortcutsEnabled);
        Overlay.Hotkeys.Register(HotkeyModifiers.None, 0x1B /* Escape */, CloseBlackScreen, () => _blackScreen is not null);
        StartShortcutTracking();
        _pollTimer.Start();

        // Delete-selection changes the button count; FloatingBar raises LayoutChanged for this.
        Services.OverlayInk.SlideFlipRequested += next => { if (next) PptNext(); else PptPrevious(); };
        Services.OverlayInk.ToolChanged += OnControllerToolChanged;

        Core.Diagnostics.FileLog.NewLog($"Ink Canvas 启动，版本 {Updater.CurrentVersion}");

        ApplyEdgeGestureSetting();

        if (Services.Settings.Startup.IsAutoUpdate)
            _ = Updater.CheckAsync(interactive: false);

        _ = RestorePendingRecordingsAsync();
    }

    /// <summary>
    /// The default hotkey set. Extracted so it can be replayed after the Win32 hotkey service
    /// drops a temporary key (the service only supports unregistering everything).
    /// </summary>
    private void RegisterOverlayHotkeys()
    {
        Overlay.RegisterHotkeys(
            undo: () => Services.OverlayInk.Undo(),
            redo: () => Services.OverlayInk.Redo(),
            togglePen: () => { Services.OverlayInk.Tool = InkTool.Pen; _bar.Bar.RefreshSelection(); },
            toggleEraser: () => { Services.OverlayInk.Tool = InkTool.Eraser; _bar.Bar.RefreshSelection(); },
            clear: () => Services.OverlayInk.Clear(),
            togglePassThrough: ToggleClickThrough,
            toggleBoard: ToggleBoard,
            pptNext: () => HandlePresentationKey(PresentationKeyMap.VkPageDown),
            pptPrevious: () => HandlePresentationKey(PresentationKeyMap.VkPageUp),
            pptExit: () => { if (_lastSlideShowState) PptExit(); },
            toggleBlackScreen: ToggleBlackScreen,
            screenshot: () => _ = ScreenshotAsync(),
            fold: ToggleFold,
            select: () =>
            {
                if (_board is not null) _board.SelectOrSelectAll();
                else Services.OverlayInk.Tool = InkTool.Select;
                _bar.Bar.RefreshSelection();
            },
            line: () =>
            {
                Services.OverlayInk.SelectShape(Core.Shapes.ShapeKind.Line);
                _bar?.Bar.RefreshSelection();
            },
            draw: () =>
            {
                bool active = Services.OverlayInk.Tool == InkTool.Pen && (DocumentActive || _board is not null || !Overlay.ClickThrough);
                if (!DocumentActive && _board is null && Overlay.ClickThrough) ToggleClickThrough();
                Services.OverlayInk.Tool = InkTool.Pen;
                if (active) OpenPenPalette(false);
            },
            editingEnabled: () => CanvasShortcutsEnabled,
            overlayEditingEnabled: () => OverlayShortcutsEnabled,
            presentationEnabled: () => PresentationShortcutsEnabled,
            ai: OpenAiAssistant,
            sideBoard: ToggleSideBoard,
            sideBoardModifiers: Services.Settings.Hotkey.SideBoard.Modifiers,
            sideBoardKey: Services.Settings.Hotkey.SideBoard.Key);
    }

    private void OnBarLayoutChanged()
    {
        _bar?.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                         Services.Settings.Appearance.FloatingBarScale, recenter: false);
        QueueDesktopSelection();
    }

    private void OnBarWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) QueueDesktopSelection();
    }

    private void QueueDesktopSelection()
    {
        if (_disposed || _exiting) return;
        var ink = Services.OverlayInk;
        if (_selectionToolbar is null && DesktopSelectionAvailable && ink.HasSelection && ink.Playback is null
            && !ink.IsMarqueeSelecting && !ink.IsRotatingSelection)
        {
            var toolbar = new SelectionToolbarWindow(this);
            _selectionToolbar = toolbar;
            if (_selectionToolbarCollapsed) toolbar.Toolbar.ToggleCollapsed();
            toolbar.Closed += (_, _) =>
            {
                _selectionToolbarCollapsed = toolbar.Toolbar.IsCollapsed;
                if (ReferenceEquals(_selectionToolbar, toolbar)) _selectionToolbar = null;
            };
            NotifyWindowOpened(toolbar);
        }
        if (ink.HasSelection || _selectionToolbar?.IsShown == true) _selectionToolbar?.QueueUpdate();
    }

    internal bool DesktopSelectionAvailable => _board is null && !DocumentActive && !_exiting && !Overlay.ClickThrough
        && Services.OverlayInk.Tool == InkTool.Select;

    internal Core.Ink.Rect DesktopToolbarBounds
    {
        get
        {
            if (_bar is null || _bar.IsFolded) return Core.Ink.Rect.Empty;
            var pos = _bar.AppWindow.Position;
            var size = _bar.AppWindow.Size;
            return new Core.Ink.Rect(pos.X, pos.Y, pos.X + size.Width, pos.Y + size.Height);
        }
    }

    private InkController ActiveController => _board is not null
        ? (_sideBoard is { IsShown: true } ? _sideBoard.CurrentController : Services.Board.Current)
        : DocumentActive ? _documentWorkspace.Controller : Services.OverlayInk;

    /// <summary>
    /// WPF GetMainWindowTheme: in board mode the aux windows follow the black/white board
    /// choice (light iff whiteboard), otherwise the application theme.
    /// </summary>
    /// <summary>Auto-revert to pen changes Tool outside bar clicks; keep the bar selection in sync.</summary>
    private void OnControllerToolChanged()
    {
        RefreshShortcutRegistration();
        if (DocumentActive && ActiveController.Tool == InkTool.Pan) OptionPopupCoordinator.DismissUnpinned();
        if (_bar is not null) _bar.Bar.ClickThroughActive = DocumentActive ? ActiveController.Tool == InkTool.Pan : Overlay.ClickThrough;
        _bar?.Bar.RefreshSelection();
        QueueDesktopSelection();
    }

    private void BindBoardController(InkController controller)
    {
        if (!ReferenceEquals(controller, _boundBoardController)) CloseDetachedPalettes();
        if (_boundBoardController is not null)
        {
            _boundBoardController.ToolChanged -= OnControllerToolChanged;
        }
        _boundBoardController = controller;
        if (controller is not null)
        {
            controller.ToolChanged += OnControllerToolChanged;
        }
        ActiveControllerChanged?.Invoke();
    }

    private int EffectiveTheme => _board is not null
        ? (Services.Settings.Canvas.UsingWhiteboard ? 1 : 2)
        : Services.Settings.Appearance.Theme;

    private void CloseDetachedPalettes()
    {
        foreach (var palette in _detachedPalettes.ToArray()) palette.Close();
    }

    private void ApplyEdgeGestureSetting()
        => Platform.System.EdgeGestureService.SetEdgeGesturesDisabled(
            Overlay.Window.Handle, Services.Settings.Advanced.IsEnableEdgeGestureUtil);

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = DocumentActive ? _documentWorkspace.Window : (Window)_board ?? _bar;
        return await YesOrNoWindow.AskAsync(owner, message, title);
    }

    private Task NotifyAsync(string title, string message)
        => ShowNoticeAsync(title, message);

    /// <summary>
    /// 创建/复用置顶通知窗口：按标题+正文去重，随后居中到 owner、登记并置顶。
    /// 传入 <paramref name="actions"/> 时窗口底部额外渲染动作按钮（点击后先关闭窗口再执行）；
    /// actions 为空时与旧 <c>NotifyAsync</c> 行为完全一致。
    /// </summary>
    private Task<bool> ShowNoticeAsync(string title, string message, IReadOnlyList<NoticeAction> actions = null)
    {
        if (_disposed || _exiting) return Task.FromResult(false);
        _bar?.ClosePanels();
        var notice = actions is null
            ? _notices.FirstOrDefault(window => window.Title == title && window.Message == message)
            : null;
        if (notice is not null) { notice.Activate(); return AwaitNoticeCompletionAsync(notice.Completion); }
        notice = new NoticeWindow(title, message, EffectiveTheme, actions);
        _notices.Add(notice);
        notice.Closed += (_, _) => _notices.Remove(notice);
        var owner = DocumentActive ? _documentWorkspace.Window : (Window)_board ?? _bar;
        if (owner is not null) WindowChrome.CenterOnOwner(notice, owner);
        NotifyWindowOpened(notice);
        notice.Activate();
        WindowChrome.EnsureAbove(notice, Overlay.Window.Handle);
        return AwaitNoticeCompletionAsync(notice.Completion);
    }

    private static async Task<bool> AwaitNoticeCompletionAsync(Task completion)
    {
        await completion;
        return true;
    }

    private async Task<bool> AskAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 启动期扫描未完成录制的增量日志：存在时提示「恢复 / 放弃」，并顺带按保留天数清理旧会话。
    /// 恢复走 <see cref="ClassroomSessionRecovery"/>，把结果登记到课堂会话目录（复用既有 JSON 格式）。
    /// </summary>
    private async Task RestorePendingRecordingsAsync()
    {
        IReadOnlyList<PendingRecording> pending;
        try
        {
            pending = await Task.Run(() => ClassroomSessionRecovery.ScanPending());
        }
        catch (Exception error)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 课堂录制恢复扫描失败：{error.Message}", Core.Diagnostics.LogType.Error);
            return;
        }

        // 启动期按保留天数清理旧会话：最近 20 条永不自动删除，只删超龄且不在最近范围内的。
        try
        {
            int pruned = await Task.Run(() => ClassroomSessionStore.DeleteExpiredSessions());
            if (pruned > 0)
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[AI] 课堂会话按保留天数清理：removed={pruned}", Core.Diagnostics.LogType.Trace);
        }
        catch (Exception error)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 课堂会话保留清理失败：{error.Message}", Core.Diagnostics.LogType.Trace);
        }

        if (pending.Count == 0) return;
        if (_disposed || _exiting) return;

        // 让浮动栏先完成首帧，再弹恢复提示，避免启动瞬间被模态窗遮挡。
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (_disposed || _exiting) return;

        List<string> ids = pending.Select(item => item.Id).ToList();
        var lines = pending.Take(3).Select(item =>
            "· " + item.Name + "（" + item.EventCount + " 个事件，约 " + FormatDuration(item.PlaybackLength) + "）").ToList();
        if (pending.Count > 3) lines.Add("· 以及另外 " + (pending.Count - 3) + " 个录制");
        string message = "检测到 " + pending.Count + " 个未完成的课堂录制（上次异常退出）：\n"
            + string.Join("\n", lines)
            + "\n\n恢复会合并最后的快照与增量日志，并加入课堂会话列表；放弃会删除这些恢复残留，不影响已保存的录制。";

        Core.Diagnostics.FileLog.WriteLogToFile(
            "[AI] 课堂录制恢复提示：count=" + pending.Count + ",ids=" + string.Join(",", ids),
            Core.Diagnostics.LogType.Info);

        await ShowNoticeAsync("课堂录制恢复", message, new[]
        {
            new NoticeAction
            {
                Label = "恢复",
                Invoke = async () =>
                {
                    int recovered = await Task.Run(() => ClassroomSessionRecovery.RecoverAll(ids));
                    await NotifyAsync("课堂录制", recovered > 0
                        ? "已恢复 " + recovered + " 个未完成录制，可在课堂会话列表中查看。"
                        : "没有可恢复的录制内容，已清理恢复残留。");
                },
            },
            new NoticeAction
            {
                Label = "放弃",
                Invoke = async () =>
                {
                    int discarded = await Task.Run(() => ClassroomSessionRecovery.DiscardAll(ids));
                    await NotifyAsync("课堂录制", discarded > 0
                        ? "已放弃 " + discarded + " 个未完成录制，删除的只是恢复残留。"
                        : "没有需要清理的恢复残留。");
                },
            },
        });
    }

    private static string FormatDuration(TimeSpan length)
    {
        if (length <= TimeSpan.Zero) return "未知时长";
        if (length.TotalMinutes < 1) return Math.Max(1, (int)length.TotalSeconds) + " 秒";
        if (length.TotalHours < 1) return (int)length.TotalMinutes + " 分钟";
        return (int)length.TotalHours + " 小时 " + length.Minutes + " 分钟";
    }

    private void OnPptPresentationNotice(PptPresentationNotice notice)
    {
        _dispatcher.TryEnqueue(async () => await ShowPptPresentationNoticeAsync(notice));
    }

    private async Task ShowPptPresentationNoticeAsync(PptPresentationNotice notice)
    {
        // WPF YesOrNoNotificationWindow parity: exact strings, topmost 450x160 window that also
        // works while the floating bar is folded (the old ContentDialog hung on a hidden XamlRoot).
        var owner = (Window)_board ?? _bar;
        string resumeHint = notice.ResumeIndexDegraded ? "（按旧页码恢复，页序可能已变化）" : "";
        if (notice.PreviousSlideIndex is > 0
            && await YesOrNoWindow.AskAsync(owner, $"上次播放到了第 {notice.PreviousSlideIndex} 页，是否立即跳转？{resumeHint}"))
            _pptSession.GoToSlide(notice.PreviousSlideIndex.Value);

        if (notice.OfferRestoreHiddenSlides
            && await YesOrNoWindow.AskAsync(owner, "检测到此演示文档中包含隐藏的幻灯片，是否取消隐藏？"))
            _pptSession.RestoreHiddenSlides();

        if (notice.OfferDisableTimings
            && await YesOrNoWindow.AskAsync(owner, "检测到此演示文档中自动播放或排练计时已经启用，可能导致幻灯片自动翻页，是否取消？"))
            _pptSession.DisableSlideTimings();

        if (notice.LegacyInkFilesDetected || notice.ResumeIndexDegraded)
            await ShowInkRecoveryWizardAsync(notice);
    }

    private async Task ShowInkRecoveryWizardAsync(PptPresentationNotice notice)
    {
        try
        {

            string summary = notice.InkMappingSummary ?? "课件页序与墨迹存档不一致。";
            string backupHint = string.IsNullOrWhiteSpace(notice.BackupDirectory)
                ? "选择清空前会先备份整个墨迹目录。"
                : $"迁移备份：{notice.BackupDirectory}";
            var dialog = new ContentDialog
            {
                Title = "课件批注与当前页面不一致",
                Content = new TextBlock
                {
                    Text = $"{summary}\n{backupHint}\n\n保留现有批注不会修改任何数据。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "保留现有批注",
                SecondaryButtonText = "备份并导出",
                CloseButtonText = "稍后处理",
                DefaultButton = ContentDialogButton.Primary,
            };
            ContentDialogResult result = await ShowDialogAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    "PPT ink recovery: user kept existing ink after mapping mismatch", Core.Diagnostics.LogType.Info);
                return;
            }
            if (result != ContentDialogResult.Secondary)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    "PPT ink recovery: user postponed the mapping decision", Core.Diagnostics.LogType.Info);
                return;
            }

            string directory = _pptStore.GetPresentationDirectory(notice.Details.Presentation);
            string backup = SlideInkMigrator.Backup(directory);
            if (string.IsNullOrWhiteSpace(backup))
            {
                await NotifyAsync("备份失败", "无法备份该课件的墨迹目录，已取消操作，未删除任何数据。");
                Core.Diagnostics.FileLog.WriteLogToFile(
                    "PPT ink recovery: backup failed, nothing cleared", Core.Diagnostics.LogType.Error);
                return;
            }
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"PPT ink recovery: backup exported to {backup}", Core.Diagnostics.LogType.Info);
            if (!await AskAsync("清空该课件的批注？",
                    $"批注已备份到：\n{backup}\n\n仅在确认后清空此课件的批注，其他数据不受影响。", "清空批注"))
            {
                await NotifyAsync("已导出备份", $"未清空任何批注。备份位置：{backup}");
                return;
            }
            _pptSession.ClearPresentationInk();
            await NotifyAsync("批注已清空", $"已备份到 {backup}，随后清空了该课件的批注。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"PPT ink recovery: user cleared ink after backup {backup}", Core.Diagnostics.LogType.Info);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"PPT ink recovery wizard: {ex}", Core.Diagnostics.LogType.Error);
        }
    }


    private void OnPptScreenshotRequested(PptScreenshotRequest request)
    {
        if (!_pptGate.IsCurrent(_pptCaptureSession, _pptCaptureGeneration)) return;
        // Capture screen pixels synchronously before the COM navigation mutates the slideshow.
        try
        {
            var area = ShowWindowTracker.TryGet(request.Snapshot.WindowHandle, out var measured)
                ? new ScreenRect(measured.X, measured.Y, measured.Width, measured.Height)
                : request.Snapshot.WindowHandle != IntPtr.Zero
                    ? ScreenService.FromWindow(request.Snapshot.WindowHandle).Bounds : GetScreenshotBounds();
            var (pixels, width, height) = ScreenshotService.Capture(area.X, area.Y, area.Width, area.Height);
            if (width <= 0 || height <= 0) return;
            string path = request.SuggestedPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                string root = Services.Settings.Automation.AutoSavedStrokesLocation;
                path = Path.Combine(root, "Auto Saved - PPT Screenshots", DateTime.Now.ToString("yyyy-MM-dd"),
                    $"{request.Snapshot.ShowPosition}_{DateTime.Now:HH-mm-ss}.png");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RememberPptScreenshot(path, request.Snapshot.Slide?.Index ?? request.InkSlide?.Index ?? 0,
                request.Snapshot.Slide?.SlideId ?? request.InkSlide?.SlideId ?? 0);
            _ = ScreenshotService.SavePngAsync(path, pixels, width, height);
            SaveSlideNavigatorThumbnail(request.Snapshot.Presentation, _pptScreenshotSlideId, pixels, width, height);
            if (request.SaveInkAlongside)
            {
                string ink = Path.ChangeExtension(path, ".icink");
                InkFileStore.Save(ink, request.Strokes);
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"PPT screenshot: {ex}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>Periodic mode maintenance: slideshow detection, auto-fold, layering.</summary>
    private void Poll()
    {
        if (_exiting) return;
        RefreshDocumentTarget();
        MaintainAutomation();
        Overlay.TrimIdleResources();
        if (_selectionToolbar is { IsShown: false } toolbar && Environment.TickCount64 - toolbar.HiddenSince >= 15_000)
            toolbar.Close();
        if (Environment.TickCount64 - _lastDisplayCheck >= 1000) RefreshDisplays();
        RefreshPresentation();

        // Always evaluate auto-fold so disabling its settings also unfolds a bar that was
        // previously folded automatically.
        if (_documentWorkspace is null) ApplyAutoFold();
        RaiseChromeAboveOverlay();
        RefreshShortcutRegistration();
    }

    private bool _presentationRefreshQueued, _refreshingPresentation;
    private void QueuePresentationRefresh()
    {
        if (_exiting || _disposed || _presentationRefreshQueued) return;
        _presentationRefreshQueued = true;
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _presentationRefreshQueued = false;
            if (_exiting || _disposed) return;
            RefreshPresentation();
            RaiseChromeAboveOverlay();
        })) _presentationRefreshQueued = false;
    }

    private void RefreshPresentation()
    {
        if (_refreshingPresentation) return;
        _refreshingPresentation = true;
        try
        {
            try
            {
                if (Services.Settings.PowerPoint.PowerPointSupport && _documentWorkspace is null)
                {
                    // Each poll opens a new slide generation. Screenshot requests raised synchronously
                    // from inside PptInkSession.Poll() are validated against it on the handler side.
                    int generation = _pptGate.NextGeneration();
                    _pptCaptureSession = _pptGate.Session;
                    _pptCaptureGeneration = generation;

                    var current = _pptSession.Poll();

                    // COM unavailable and no show visible through automation: WPS/PowerPoint may still
                    // be running a full-screen viewer. Treat its window as a degraded slideshow so the
                    // pager stays usable (page numbers stay 0/0, the snapshot has no slide data).
                    bool windowAvailable = false;
                    if (!current.IsAttached && !current.IsSlideShowRunning)
                    {
                        FallbackShowWindow fallback = PresentationFallbackDetector.Detect();
                        if (fallback is not null)
                        {
                            windowAvailable = true;
                            current = new PptSnapshot
                            {
                                State = PptConnectionState.SlideShow,
                                WindowHandle = fallback.Handle,
                            };
                        }
                    }
                    _pptSources.Select(current.IsAttached, windowAvailable);

                    bool isShow = current.IsSlideShowRunning;
                    if (isShow != _lastSlideShowState)
                    {
                        _lastSlideShowState = isShow;
                        Services.PresentationActive = isShow;
                        Services.ApplySettings();
                        if (isShow)
                        {
                            // Capture the pre-show desktop spot before SetPptModeVisible/Reposition
                            // reflow the bar; otherwise the restore target becomes the reflowed
                            // spot and the toolbar never returns where the user left it.
                            _bar?.SetPresentationMode(true, current.WindowHandle, current.IsWindowedShow);
                            _pptGate.BeginSession();
                            // Re-anchor the capture generation: BeginSession bumped the session, so the
                            // values captured at the top of the poll are no longer current.
                            _pptCaptureSession = _pptGate.Session;
                            _pptCaptureGeneration = _pptGate.Generation;
                            // MW_PPT.cs:283-286: slideshow begin leaves board mode.
                            if (_board is not null) ToggleBoard();
                        }
                        else
                        {
                            // A new show starts from the top of the matrix; the black screen is a
                            // presentation aid and must not survive into editing mode.
                            _pptSources.Reset();
                            CloseBlackScreen();
                            // MW_PPT.cs:505-508: slideshow end leaves board mode.
                            if (_board is not null) ToggleBoard();
                        }
                        if (_hotkeysReady)
                        {
                            bool keysReady = Overlay.SetPresentationKeysEnabled(isShow);
                            if (isShow && !keysReady && !_presentationKeyFailureLogged)
                            {
                                _presentationKeyFailureLogged = true;
                                Core.Diagnostics.FileLog.WriteLogToFile(
                                    "PPT 翻页热键注册失败（可能被其他程序占用）", Core.Diagnostics.LogType.Info);
                            }
                            if (!isShow) _presentationKeyFailureLogged = false;

                            // Toggling presentation keys rebuilds the whole group, which drops the
                            // temporary black-screen Esc; put it back while the window is still up.
                            if (_blackScreen is not null)
                            {
                                _blackScreenEscapeHotkey = false;
                                TryRegisterBlackScreenEscape();
                            }
                        }
                        _bar?.Bar.SetPptModeVisible(isShow);
                        _bar?.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                                         Services.Settings.Appearance.FloatingBarScale);
                        if (isShow && Services.Settings.PowerPoint.IsShowCanvasAtNewSlideShow)
                        {
                            Overlay.Show();
                            // MW_PPT.cs:302,397-400: lastDesktopInkColor=1 -> red pen on show begin.
                            Services.OverlayInk.CurrentAttributes.Color = Core.Ink.InkColor.Red;
                            _bar?.Bar.RefreshSelection();
                        }
                    }
                    // The show-start path already applied presentation mode above (before the
                    // reflow), so only the exit path is handled here, after Reposition.
                    if (!isShow) _bar?.SetPresentationMode(false, current.WindowHandle, current.IsWindowedShow);
                    if (isShow && _board is null) Overlay.Window.EnsureAbove(current.WindowHandle);
                    UpdatePptNavigation(current);
                }
                else
                {
                    if (_lastSlideShowState)
                    {
                        _lastSlideShowState = false;
                        Services.PresentationActive = false;
                        Services.ApplySettings();
                        _bar?.Bar.SetPptModeVisible(false);
                    }
                    _pptSources.Reset();
                    if (_hotkeysReady) Overlay.SetPresentationKeysEnabled(false);
                    CloseBlackScreen();
                    _bar?.SetPresentationMode(false);
                    UpdatePptNavigation(PptSnapshot.Detached);
                }

                // 原条件：放映中 + 尚无墨迹 + 允许手指手势控制放映。未验证。
                Services.OverlayInk.Palm.PptGestureEligible =
                    _lastSlideShowState
                    && Services.OverlayInk.Document.Count == 0
                    && Services.Settings.PowerPoint.IsEnableFingerGestureSlideShowControl;
            }
            catch (Exception ex)
            {
                // The poll must never take the app down: COM can fail while PowerPoint is busy.
                LogPptFailure(new PptFailure("Poll", ex.Message, Marshal.GetHRForException(ex)));
            }

            // Slideshow polling must be responsive; idle polling only watches for a show starting.
            _pollTimer.Interval = _lastSlideShowState
                ? TimeSpan.FromMilliseconds(250)
                : Services.Ppt.LastFailure is null ? _pptSession.RecommendedPollInterval : TimeSpan.FromMilliseconds(80);
        }
        finally { _refreshingPresentation = false; }
    }

    /// <summary>
    /// Keep floating tools above the annotation surface after overlay input and placement.
    /// </summary>
    private void RaiseChromeAboveOverlay()
    {
        ImageFilePicker.EnsureAbove(_board is not null ? WindowChrome.GetHwnd(_board) : Overlay.Window.Handle);
        if (_bar is not null && _board is null) WindowChrome.EnsureAbove(_bar, Overlay.Window.Handle);
        if (_selectionToolbar?.IsShown == true) WindowChrome.EnsureAbove(_selectionToolbar, Overlay.Window.Handle);
        _bar?.EnsureHandlesAbove(Overlay.Window.Handle);
        if (_board is not null && _settingsDrawingSurface != WindowChrome.GetHwnd(_board)) WindowChrome.EnsureAbove(_board, Overlay.Window.Handle);
        if (_countdown is not null) WindowChrome.EnsureAbove(_countdown, Overlay.Window.Handle);
        if (_lottery is not null) WindowChrome.EnsureAbove(_lottery, Overlay.Window.Handle);
        if (_replay is not null) WindowChrome.EnsureAbove(_replay, Overlay.Window.Handle);
        if (_updateWindow is not null) WindowChrome.EnsureAbove(_updateWindow, Overlay.Window.Handle);
        foreach (var notice in _notices) WindowChrome.EnsureAbove(notice, Overlay.Window.Handle);
        foreach (var navigation in _pptNavigations.Values)
            if (navigation.AppWindow.IsVisible) WindowChrome.EnsureAbove(navigation, Overlay.Window.Handle);
        if (_slideNav is not null) WindowChrome.EnsureAbove(_slideNav, Overlay.Window.Handle);
        if (_toolkit is not null) WindowChrome.EnsureAbove(_toolkit, Overlay.Window.Handle);

        if (_palette is not null) WindowChrome.EnsureAbove(_palette, Overlay.Window.Handle);
        foreach (var palette in _detachedPalettes) WindowChrome.EnsureAbove(palette, Overlay.Window.Handle);
        if (_shapePicker is not null) WindowChrome.EnsureAbove(_shapePicker, Overlay.Window.Handle);
        if (_board is not null)
            foreach (var panel in _board.ToolPanels) WindowChrome.EnsureAbove(panel, Overlay.Window.Handle);
        foreach (var window in _documentWindows) if (!ReferenceEquals(window, _documentWorkspace?.Window) && window.AppWindow.IsVisible) WindowChrome.EnsureAbove(window, Overlay.Window.Handle);
        UpdateSettingsDrawingOrder();
        RefreshDesktopMedia();
        if (_aiAssistant is not null)
        {
            if (_aiHiddenWithSideBoard) return;
            IntPtr anchor = _board is not null ? WindowChrome.GetHwnd(_board) : Overlay.Window.Handle;
            if (AssistantBelongsToSideBoard && _sideBoard is not null)
                anchor = WindowChrome.GetHwnd(_sideBoard);
            WindowChrome.EnsureAbove(_aiAssistant, anchor);
        }
    }

    private const int RpcCallRejected = unchecked((int)0x80010001);
    private const int RpcServerCallRetryLater = unchecked((int)0x8001010A);
    private static readonly TimeSpan PptRpcBusyLogInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// PowerPoint 忙（模态对话框、正在启动/退出放映）时会拒绝 RPC 调用。这类错误是预期内的
    /// 瞬时状态，降级为 Trace 并最多每 10 秒记录一次；其它 PPT 失败仍记为 Error。
    /// </summary>
    private void LogPptFailure(PptFailure failure)
    {
        if (failure is null) return;
        if (IsPptRpcBusy(failure.ErrorCode, failure.Message))
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastPptRpcBusyLogUtc < PptRpcBusyLogInterval) return;
            _lastPptRpcBusyLogUtc = now;
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"PPT 忙，已忽略 RPC 拒绝呼叫：{failure.Operation}: {failure.Message} (0x{failure.ErrorCode:X8})",
                Core.Diagnostics.LogType.Trace);
            return;
        }

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"PPT/{failure.Operation}: {failure.Message} (0x{failure.ErrorCode:X8})", Core.Diagnostics.LogType.Error);
    }

    private static bool IsPptRpcBusy(int errorCode, string message)
    {
        if (errorCode == RpcCallRejected || errorCode == RpcServerCallRetryLater) return true;
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("被呼叫方拒绝接收呼叫", StringComparison.Ordinal)
            || message.Contains("Could not get dispatch ID", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdatePptNavigation(PptSnapshot snapshot)
    {
        NotifyAiSlideChange(snapshot);

        bool inShow = snapshot?.IsSlideShowRunning == true;
        _pptShowSnapshot = inShow ? snapshot : null;
        if (inShow) RefreshSlideIndex(snapshot);
        if (!inShow && !_manualPptNav)
        {
            ClosePptNavigationWindows();
            _pptNavCollapsed = false;
            return;
        }
        bool visible = _board is null && _bar?.IsFolded != true && !_pptNavCollapsed;
        var settings = Services.Settings.PowerPoint;
        foreach (var placement in Enum.GetValues<PptNavigationPlacement>())
        {
            bool side = placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight;
            bool enabled = side ? settings.IsShowSidePPTNavigationPanel : settings.IsShowBottomPPTNavigationPanel;
            if (!_pptNavigations.TryGetValue(placement, out var window) && enabled && visible)
            {
                window = CreatePptNavigation(placement);
                _pptNavigations.Add(placement, window);
            }
            window?.Update(snapshot, visible && enabled,
                inShow && (side ? settings.IsShowPPTNavigationSides : settings.IsShowPPTNavigationBottom));
        }
    }

    /// <summary>
    /// 翻页后只提示「题目来自旧页」，不自动清空或多选区；保留课堂上的题目上下文。
    /// </summary>
    private void NotifyAiSlideChange(PptSnapshot snapshot)
    {
        if (Assistant is null) return;
        Assistant.NotifySlideChanged(snapshot?.Slide?.SlideId ?? 0, snapshot?.Slide?.Index ?? 0,
            snapshot?.IsSlideShowRunning == true ? Services.Ppt.GetAnimationStep() : 0);
    }

    private PptNavigationWindow CreatePptNavigation(PptNavigationPlacement placement)
    {
        var window = new PptNavigationWindow(placement, this, Services.Settings.Appearance.Theme, _bar, CreatePptPreviewContext());
        return window;
    }

    private void ClosePptNavigationWindows()
    {
        foreach (var window in _pptNavigations.Values) window.Close();
        _pptNavigations.Clear();
        _pptPreviewSource?.Dispose();
        _pptPreviewSource = null;
    }

    private void ApplyAutoFold()
    {
        if (_bar is null) return;
        var foreground = ForegroundWindowService.DescribeForeground();
        var a = Services.Settings.Automation;
        bool fold = Core.Settings.AutomationPolicy.ShouldFold(a, foreground.Process, foreground.Title, foreground.Height, _lastSlideShowState);
        if (!fold && a.IsAutoFoldInOldZyBoard)
            fold = WindowEnumerator.WindowExists("WhiteBoard - DrawingWindow") || WindowEnumerator.WindowExists("InstantAnnotationWindow");

        if (fold != _lastAutoFoldRequested)
        {
            _lastAutoFoldRequested = fold;
            _manualFoldOverride = null;
        }
        bool desiredFold = _manualFoldOverride ?? fold;

        if (desiredFold && !_bar.IsFolded)
        {
            // WPF FoldFloatingBar_Click always invokes CursorIcon_Click before hiding.
            if (!Overlay.ClickThrough) ToggleClickThrough();
            _bar.SetFolded(true);
        }
        else if (!desiredFold && _bar.IsFolded)
        {
            // WPF unfold restores only the toolbar; the overlay remains in mouse mode until
            // the user selects an ink tool.
            _bar.SetFolded(false);
        }
        if (desiredFold) Overlay.ClickThrough = true;
    }

    // ---- IShellActions ----------------------------------------------------

    public void ToggleBoard()
    {
        if (_boardSwitching) return;
        CloseDetachedPalettes();
        _imageImportCancellation?.Cancel();
        _videoImportCancellation?.Cancel();
        _selectionToolbar?.Hide();
        _replay?.Close();
        if (_board is null)
        {
            Services.BoardModeActive = true;
            Services.ConfigureController(Services.Board.Current);
            _documentWorkspace?.Window.AppWindow.Hide();
            _palette?.Close(); _shapePicker?.Close();
            bool restoring = _cachedBoard is not null;
            _board = _cachedBoard ?? new BoardWindow(Services, this, _bar);
            _cachedBoard = null;
            if (!restoring)
            {
                NotifyWindowOpened(_board);
                _board.Closed += (_, _) => OnBoardClosed(_board);
            }
            _board.CanvasInputBegan += OnBoardInputBegan;
            _board.CurrentPageChanged += OnBoardPageChanged;
            _recorder = new InkRecorder(Services.Board);
            _recorder.Start();
            // 子板与主展板同一时钟起点录制，停止时把子板事件并入主会话（BoardId=1）。
            if (!ReferenceEquals(Services.SideBoard, Services.Board))
            {
                _sideBoardRecorder = new InkRecorder(Services.SideBoard, boardId: 1, persistOnStop: false);
                _sideBoardRecorder.Start();
            }
            SyncRecorderContext();
            if (_bar is not null) _bar.Bar.Controller = _board.CurrentController;
            WindowChrome.ApplyTheme(_board, Services.Settings.Appearance.Theme);

            BindBoardController(_board.CurrentController);
            BoardOpenChanged?.Invoke();
            BindToolbar(_board.CurrentController);
            _board.RestoreFromAnnotation();
            var boardWindow = _board;
            boardWindow.Activated += (_, _) => RaiseAssistantAboveBoard(boardWindow);
            boardWindow.AppWindow.Changed += (_, args) =>
            {
                if (args.DidSizeChange || args.DidPositionChange) RaiseAssistantAboveBoard(boardWindow);
            };
            _board.Activate();
            // The board is opaque and full-screen; the screen overlay would draw over it.
            Overlay.Hide();
            // WPF slides the floating bar off-edge in board mode; hide it here (handles stay).
            if (_bar is not null && !_bar.IsFolded) _bar.AppWindow.Hide();
        }
        else
        {
            _ = ReturnToAnnotationAsync(_board);
        }
    }

    private async Task ReturnToAnnotationAsync(BoardWindow board)
    {
        _boardSwitching = true;
        try
        {
            if (!await board.HideForAnnotationAsync() || !ReferenceEquals(_board, board)) return;
            _cachedBoard = board;
            OnBoardClosed(board);
        }
        finally { _boardSwitching = false; }
    }

    private void OnBoardPageChanged()
    {
        _replay?.Close();
        if (_board is null || _bar is null) return;
        BindToolbar(_board.CurrentController);
        BindBoardController(_board.CurrentController);
        _palette?.Close();
        _palette = null;

        if (_aiProblem is { Count: > 0 } && _aiAssistant is not null)
        {
            _aiAssistant.ShowNotice("画板已换页，当前拼题仍来自旧页；可继续讲题，或重新框选并替换选区。");
        }

        _teachingSession?.SetCurrentSlide(
            _aiSlideId, _aiSlideIndex, 0, Environment.TickCount64);
        if (_teachingSession is not null)
            _teachingSession.CurrentBoardPageIndex = Services.Board.CurrentIndex;
        _shapePicker?.Close(); _shapePicker = null;
        Assistant?.NotifyBoardChanged();
    }

    private void OnBoardInputBegan()
    {
        if (_board is not null) ImageFilePicker.EnsureAbove(WindowChrome.GetHwnd(_board));
        _palette?.DismissOnOverlayInput();
        _shapePicker?.DismissOnOverlayInput();
    }

    private void OnBoardClosed(BoardWindow board)
    {
        if (board.IsClosed && ReferenceEquals(_cachedBoard, board)) _cachedBoard = null;
        if (!ReferenceEquals(_board, board)) return;
        _board.CurrentPageChanged -= OnBoardPageChanged;
        StopRecording();
        CloseSideBoardWithBoard();
        if ((_aiTarget?.Kind == AssistantCanvasKind.MainBoard
             || (UnifiedContextEnabled && _aiContext?.Kind == BoardCanvasKind.MainBoard))
            && _aiAssistant is not null)
        {
            // 主板已关闭：作废本代响应，停掉主流与局部纠错流，再关浮窗（Close 会触发 OnAiWindowClosed 完成清理）。
            _aiGate.Invalidate();
            _aiStream?.Abort();
            _aiStream = null;
            _aiRegionStream?.Abort();
            _aiRegionStream = null;
            _aiAssistant.Close();
            Core.Diagnostics.FileLog.WriteLogToFile(
                "[AI] 主板已关闭，终止主板 AI 流并关闭浮窗",
                Core.Diagnostics.LogType.Info);
        }
        _board = null;
        Services.BoardModeActive = false;
        Services.ConfigureController(Services.OverlayInk);
        board.CurrentPageChanged -= OnBoardPageChanged;
        board.CanvasInputBegan -= OnBoardInputBegan;
        BoardOpenChanged?.Invoke();
        if (_disposed || _exiting) return;
        _replay?.Close();
        _palette?.Close(); _shapePicker?.Close();
        // BindToolbar 会写 FloatingBar.Controller，其 setter 立即抛出 LayoutChanged。
        // 关闭板时先解绑重排，避免「板已关 / 浮条仍在收尾」时在 Closed 回调链里重排；
        // 绑定完成后在下面浮条重新显示时再做一次显式重排。
        var bar = _bar;
        if (bar is not null) bar.Bar.LayoutChanged -= OnBarLayoutChanged;
        try
        {
            BindBoardController(null);
            BindToolbar(ActiveController);
        }
        finally
        {
            if (bar is not null) bar.Bar.LayoutChanged += OnBarLayoutChanged;
        }
        Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
        if (_documentWorkspace is not null) { _documentWorkspace.Window.Activate(); OnDocumentPageChanged(); }
        else Overlay.Show();
        if (bar is not null && !bar.IsFolded)
        {
            WindowChrome.ShowWithoutActivation(bar);
            bar.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                           Services.Settings.Appearance.FloatingBarScale, recenter: false);
        }
        QueueDesktopSelection();
    }

    /// <summary>板窗口激活或尺寸变化后重新把 AI 浮窗提到板上方，避免被全屏置顶板压住。</summary>
    private void RaiseAssistantAboveBoard(BoardWindow board)
    {
        if (_aiAssistant is null || AssistantBelongsToSideBoard) return;
        WindowChrome.EnsureAbove(_aiAssistant, WindowChrome.GetHwnd(board));
    }

    private void StopRecording()
    {
        InkRecorder sideRecorder = _sideBoardRecorder;
        _sideBoardRecorder = null;
        InkRecorder recorder = _recorder;
        _recorder = null;

        // 子板先停：其事件按 BoardId=1 并入主会话，回放与总结仍只读主会话。
        if (sideRecorder is not null)
        {
            try
            {
                sideRecorder.Stop();
                int sideEvents = sideRecorder.CapturedEvents.Count;
                int mainEvents = recorder?.CapturedEvents.Count ?? 0;
                if (recorder?.Session is not null && sideEvents > 0)
                    recorder.MergeEvents(sideRecorder.CapturedEvents);
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[AI] 子板录制合并：recordedSideEvents={sideEvents}，recordedMainEvents={mainEvents}",
                    Core.Diagnostics.LogType.Info);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[AI] 子板录制合并失败：{ex.Message}", Core.Diagnostics.LogType.Info);
            }
            finally { sideRecorder.Dispose(); }
        }

        if (recorder is null) return;
        recorder.Stop();
        try { recorder.Save(); }
        catch { /* 磁盘问题不应影响关闭流程 */ }
        recorder.Dispose();
    }

    /// <summary>暂停录制（仅录制中有效）；暂停期间事件不入时间轴，计时扣除暂停时长。</summary>
    public void PauseRecording()
    {
        _recorder?.Pause();
        _sideBoardRecorder?.Pause();
    }

    /// <summary>继续录制（仅录制中有效）。</summary>
    public void ResumeRecording()
    {
        _recorder?.Resume();
        _sideBoardRecorder?.Resume();
    }

    /// <summary>浮条入口：在暂停 / 继续之间切换；未在录制时静默忽略。以主录制器状态为准，同时切换子板。</summary>
    public void ToggleRecordingPause()
    {
        var recorder = _recorder;
        if (recorder is null || !recorder.IsRecording) return;
        if (recorder.IsPaused)
        {
            recorder.Resume();
            _sideBoardRecorder?.Resume();
        }
        else
        {
            recorder.Pause();
            _sideBoardRecorder?.Pause();
        }
    }

    public void OpenSettings() => OpenSettingsPage(null);

    private void OpenSettingsPage(string category)
    {
        if (category is not null) _requestedSettingsCategory = category;
        if (_settings is not null)
        {
            ShowRequestedSettingsPage(_settings);
            ActivateSettingsWindow(_settings); return;
        }
        if (_settingsOpening || _exiting) return;
        _settingsOpening = true;
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_exiting) return;
                _bar?.ClosePanels();
                _palette?.DismissOnOverlayInput();
                _shapePicker?.DismissOnOverlayInput();
                var window = new SettingsWindow(Services, Updater, this, RosterDirectory);
                _settings = window;
                ShowRequestedSettingsPage(window);
                NotifyWindowOpened(window);
                window.SettingsChanged += OnSettingsChanged;
                window.Activated += (_, _) => UpdateSettingsDrawingOrder();
                window.Closed += (_, _) =>
                {
                    window.SettingsChanged -= OnSettingsChanged;
                    _settings = null;
                    UpdateSettingsDrawingOrder();
                    _settingsRosterSource?.AppWindow.Show(activateWindow: false);
                    _settingsRosterSource = null;
                };
                ActivateSettingsWindow(window);
            }
            finally { _settingsOpening = false; }
        });
    }

    private void ActivateSettingsWindow(SettingsWindow window)
    {
        _bar?.ClosePanels();
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        window.Activate();
        // Settings is opened by an explicit user action from nonactivating tool chrome.
        // Request native foreground focus as well as XAML activation, then reorder ink.
        NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(window));
        UpdateSettingsDrawingOrder();
    }

    private void ShowRequestedSettingsPage(SettingsWindow window)
    {
        if (_requestedSettingsCategory is null) return;
        window.SelectCategory(_requestedSettingsCategory);
        if (_requestedSettingsCategory == "抽奖与点名") window.RevealRosterEditor();
        _requestedSettingsCategory = null;
    }

    private void UpdateSettingsDrawingOrder()
    {
        if (_disposed) return;
        var settings = _settings is null ? IntPtr.Zero : WindowChrome.GetHwnd(_settings);
        var surface = IntPtr.Zero;
        if (settings != IntPtr.Zero && NativeMethods.GetForegroundWindow() == settings)
        {
            if (_board is not null) surface = WindowChrome.GetHwnd(_board);
            else if (!DocumentActive) surface = Overlay.Window.Handle;
        }
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (surface != _settingsDrawingSurface)
        {
            if (_settingsDrawingSurface != IntPtr.Zero)
                NativeMethods.SetWindowPos(_settingsDrawingSurface,
                    _settingsSurfaceWasTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            _settingsDrawingSurface = surface;
            _settingsSurfaceWasTopmost = surface != IntPtr.Zero && NativeMethods.HasExStyle(surface, NativeMethods.WS_EX_TOPMOST);
        }
        // A normal settings window must receive input while the drawing surface stays visible.
        // Inserting the drawing HWND after it also removes that surface from the topmost band.
        if (surface != IntPtr.Zero) NativeMethods.SetWindowPos(surface, settings, 0, 0, 0, 0, flags);
    }

    private void OpenRosterSettings(LotteryWindow source)
    {
        if (!ReferenceEquals(_settingsRosterSource, source))
        {
            _settingsRosterSource?.AppWindow.Show(activateWindow: false);
            _settingsRosterSource = source;
            source.Closed += (_, _) => { if (ReferenceEquals(_settingsRosterSource, source)) _settingsRosterSource = null; };
        }
        source.AppWindow.Hide();
        OpenSettingsPage("抽奖与点名");
    }

    private void OnSettingsChanged()
    {
        QueueDesktopSelection();
        _bar?.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                         Services.Settings.Appearance.FloatingBarScale);
        _bar?.ApplyTheme(Services.Settings.Appearance.Theme);
        if (_board is not null) WindowChrome.ApplyTheme(_board, Services.Settings.Appearance.Theme);

        // WPF ApplyScaling: menus live inside the scaled bar/board Viewbox, so scale them live.
        double menuScale = _board is not null
            ? Services.Settings.Appearance.BlackboardScale
            : Services.Settings.Appearance.FloatingBarScale;
        _palette?.ApplyScale(menuScale);
        foreach (var palette in _detachedPalettes) palette.ApplyScale(menuScale);
        _shapePicker?.ApplyScale(menuScale);
        _board?.RefreshUiScale();
        _bar?.Bar.RefreshSettingsVisuals();
        if (_lastSlideShowState) UpdatePptNavigation(_pptShowSnapshot ?? _pptSession.Snapshot);
        ApplyEdgeGestureSetting();
    }

    private void ApplyDisplaySettings() => Overlay.ConfigureDisplays(Services.Settings.Display);

    private void ApplySideBoardDisplaySettings() => EnsureSideBoardOnScreen();

    private void RefreshPaletteNibModes()
    {
        _board?.RefreshToolSettings();
        if (_palette is not null) { ConfigurePaletteNibMode(_palette); _palette.RefreshFromController(); }
        foreach (var palette in _detachedPalettes) { ConfigurePaletteNibMode(palette); palette.RefreshFromController(); }
    }

    private void ConfigurePaletteNibMode(PenPaletteWindow palette) => palette.ConfigureNibMode(
        Services.Settings.Appearance.IsEnableDisPlayNibModeToggler, Services.Settings.Startup.IsEnableNibMode,
        value => { Services.Settings.Startup.IsEnableNibMode = value; Services.ApplySettings(); Services.SaveSettings(); });

    public void OpenTimer()
    {
        if (_countdown is not null) { _countdown.Activate(); return; }
        var window = new CountdownWindow(EffectiveTheme, Services.Settings.Classroom.CountdownMinutes);
        window.TimerStarted += OnTimerStarted;
        window.TimerStopped += OnTimerStopped;
        window.TimerEnded += OnTimerEnded;
        _countdown = window;
        NotifyWindowOpened(window);
        WindowChrome.ApplyTheme(window, EffectiveTheme);
        window.Closed += (_, _) =>
        {
            window.TimerStarted -= OnTimerStarted;
            window.TimerStopped -= OnTimerStopped;
            window.TimerEnded -= OnTimerEnded;
            if (ReferenceEquals(_countdown, window)) _countdown = null;
        };
        window.Activate();
        WindowChrome.EnsureAbove(window);
    }

    private void OnTimerStarted(TimeSpan remaining)
        => RaiseClassroomSignal(ClassroomSignalKind.TimerStarted,
            ((int)Math.Max(0, remaining.TotalMilliseconds)).ToString());

    private void OnTimerStopped(TimeSpan remaining)
        => RaiseClassroomSignal(ClassroomSignalKind.TimerStopped,
            ((int)Math.Max(0, remaining.TotalMilliseconds)).ToString());

    private void OnTimerEnded() => _ = CaptureTimerEndedAsync();

    /// <summary>
    /// 倒计时归零：练习会话先生成板书快照（未录制也保存），录制中再写 TimerEnded
    /// 并追加「练习结束」章节书签；同时向时光机插件转发 TimerEnded / PracticeFinished 信号。
    /// </summary>
    private async Task CaptureTimerEndedAsync()
    {
        int pageIndex = Services.Board.CurrentIndex;

        string screenshot = null;
        try
        {
            screenshot = await CaptureScreenshotToFileAsync();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"倒计时结束快照失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }

        PracticePlan practice = _activePractice;
        if (practice is not null)
        {
            PracticeFinishRecord record = PracticeFinishPolicy.Plan(
                practice,
                _recorder?.IsRecording == true,
                !string.IsNullOrEmpty(screenshot),
                screenshot ?? "",
                Environment.TickCount64);
            long finishedAtMs = record.FinishedAtMs;

            _teachingSession?.Link(
                "practice", practice.Id, _aiSlideId, _aiSlideIndex, pageIndex,
                PracticeLinkNote.Build(
                    practice.AiSessionId, practice.BoardPageId, practice.GroupId, record.SnapshotPath),
                finishedAtMs);
            if (record.SnapshotSaved)
                _teachingSession?.RecordSnapshot(record.SnapshotPath, finishedAtMs);
            SaveTeachingSession();

            _activePractice = null;
            RaiseClassroomSignal(ClassroomSignalKind.PracticeFinished, "练习结束");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 练习结束：exercise={practice.Id}，snapshotSaved={record.SnapshotSaved}，"
                + $"group={practice.GroupId}，recording={_recorder?.IsRecording == true}",
                Core.Diagnostics.LogType.Event);
            _ = ShowPracticeFinishMenuAsync(record, practice);
        }

        var recorder = _recorder;
        if (recorder is not null && recorder.IsRecording && recorder.Session is not null)
        {
            long timeMs = recorder.ElapsedMs;
            string payload = practice is not null
                ? JsonSerializer.Serialize(new
                {
                    screenshot,
                    practice = true,
                    exerciseId = practice.ExerciseId,
                })
                : string.IsNullOrEmpty(screenshot)
                    ? "倒计时结束"
                    : JsonSerializer.Serialize(new { screenshot });

            try
            {
                recorder.Append(new TimelineEvent
                {
                    TimeMs = timeMs,
                    PageIndex = pageIndex,
                    Kind = TimelineEventKind.TimerEnded,
                    Payload = payload,
                });

                if (practice is not null)
                    recorder.Timeline.AddBookmark(timeMs, practice.BookmarkName, SessionBookmarkKind.Practice);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"Timeline/TimerEnded: {ex.Message}", Core.Diagnostics.LogType.Error);
            }
        }
        RaiseClassroomSignal(ClassroomSignalKind.TimerEnded, practice is not null ? "练习结束" : "倒计时结束");
    }

    /// <summary>
    /// 练习结束动作浮窗：先按快照结果给文案，再按纯逻辑策略清单渲染
    /// 「再来一题（变式）/ 收题（返回画板）/ 导出本次练习」三项；只有变式需要联网。
    /// </summary>
    private async Task ShowPracticeFinishMenuAsync(PracticeFinishRecord record, PracticePlan plan)
    {
        if (_disposed || _exiting) return;

        string message = record.SnapshotSaved
            ? "练习结束，已保存板书快照。"
            : "练习结束，板书快照保存失败，可用「导出本次练习」手动保存。";
        bool aiAvailable = !string.IsNullOrWhiteSpace(AiConfig.ReadFromEnvironment().Endpoint);

        var actions = new List<NoticeAction>();
        foreach (PracticeFinishAction action in PracticeFinishActionPolicy.Build(aiAvailable, record.SnapshotSaved))
        {
            PracticeFinishActionKind kind = action.Kind;
            actions.Add(new NoticeAction
            {
                Label = action.Label,
                IsEnabled = true,
                Invoke = () => RunPracticeFinishActionAsync(kind, record, plan),
            });
        }

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 练习结束浮窗：snapshotSaved={record.SnapshotSaved}，aiAvailable={aiAvailable}，"
            + $"actions={actions.Count}，group={plan?.GroupId}",
            Core.Diagnostics.LogType.Event);

        await ShowNoticeAsync("随堂练习", message, actions);
    }

    /// <summary>执行练习结束动作；动作在通知窗口关闭后触发，失败统一兜底提示。</summary>
    private async Task RunPracticeFinishActionAsync(
        PracticeFinishActionKind kind, PracticeFinishRecord record, PracticePlan plan)
    {
        try
        {
            switch (kind)
            {
                case PracticeFinishActionKind.NextVariant:
                    await RunNextVariantFromFinishAsync();
                    break;

                case PracticeFinishActionKind.Collect:
                    await CollectPracticeBoardAsync(plan);
                    break;

                case PracticeFinishActionKind.Export:
                    await ExportPracticeFinishAsync();
                    break;

                default:
                    Core.Diagnostics.FileLog.WriteLogToFile(
                        $"[AI] 未知的练习结束动作：kind={kind}", Core.Diagnostics.LogType.Info);
                    break;
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 练习结束动作失败：kind={kind}，error={ex.GetBaseException().Message}",
                Core.Diagnostics.LogType.Error);
            await NotifyAsync("随堂练习", "动作执行失败：" + ex.GetBaseException().Message);
        }
    }

    /// <summary>「再来一题（变式）」：离线直接提示；无框选原题时先要求框选，不复制生成逻辑。</summary>
    private async Task RunNextVariantFromFinishAsync()
    {
        if (string.IsNullOrWhiteSpace(AiConfig.ReadFromEnvironment().Endpoint))
        {
            await NotifyAsync("随堂练习", "离线不可生成变式题，请联网后再试。");
            return;
        }

        if (_aiProblem is null || _aiProblem.IsEmpty)
        {
            await NotifyAsync("随堂练习", "请先在画布上框选原题，再生成变式题。");
            return;
        }

        StartVariantRequest();
        await Task.CompletedTask;
    }

    /// <summary>「收题（返回画板）」：停止倒计时并回到练习页；不改变课堂场景 preset。</summary>
    private async Task CollectPracticeBoardAsync(PracticePlan plan)
    {
        _countdown?.StopCountdown();

        if (plan is not null
            && plan.BoardPageIndex >= 0
            && plan.BoardPageIndex < Services.Board.PageCount)
            Services.Board.GoTo(plan.BoardPageIndex);

        if (_board is not null) _board.Activate();
        else ToggleBoard();

        await Task.CompletedTask;
    }

    /// <summary>「导出本次练习」：整屏截图落盘并与课堂会话导出记录关联。</summary>
    private async Task ExportPracticeFinishAsync()
    {
        string path = await CaptureScreenshotToFileAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            await NotifyAsync("随堂练习", "导出失败，请稍后重试。");
            return;
        }

        _teachingSession?.RecordExport(path, Environment.TickCount64);
        SaveTeachingSession();
        await NotifyAsync("随堂练习", "本次练习已导出：" + path);
    }

    private void OnLotteryPicked(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _ = PasteTextCardAsync("抽奖结果", name);
    }

    public void OpenLottery()
    {
        if (_lottery is not null) { _lottery.Activate(); return; }
        var window = new LotteryWindow(Services.Lottery, autoClose: false, EffectiveTheme, RosterDirectory, OpenRosterSettings);
        window.Picked += OnLotteryPicked;
        window.OptionsChanged += () =>
        {
            Services.Settings.Rand.PeopleCount = Services.Lottery.PeopleCount;
            Services.Settings.Rand.SamplingMode = Services.Lottery.SamplingMode;
            Services.Settings.Rand.CooldownHalfLifeDays = Services.Lottery.CooldownHalfLifeDays;
            Services.Settings.Rand.CooldownPenaltyStrength = Services.Lottery.CooldownPenaltyStrength;
            Services.SaveSettings();
        };
        _lottery = window;
        NotifyWindowOpened(_lottery);
        WindowChrome.ApplyTheme(_lottery, EffectiveTheme);
        _lottery.Closed += (_, _) =>
        {
            window.Picked -= OnLotteryPicked;
            if (ReferenceEquals(_lottery, window)) _lottery = null;
        };
        _lottery.Activate();
        WindowChrome.EnsureAbove(_lottery);
    }

    /// <summary>WPF 单次抽选: quick-pick lottery that auto-rolls and auto-closes after results.</summary>
    public void OpenLotteryQuick()
    {
        var quick = new LotteryWindow(Services.Lottery, autoClose: true, EffectiveTheme, RosterDirectory, OpenRosterSettings);
        WindowChrome.ApplyTheme(quick, EffectiveTheme);
        quick.Activate();
        WindowChrome.EnsureAbove(quick);
    }

    public void OpenRoster()
    {
        if (_roster is not null) { _roster.Activate(); return; }
        var window = new RosterWindow(PasteRosterNameToBoard);
        window.NameDrawn += OnRosterNameDrawn;
        _roster = window;
        WindowChrome.ApplyTheme(window, Services.Settings.Appearance.Theme);
        window.Closed += (_, _) =>
        {
            window.NameDrawn -= OnRosterNameDrawn;
            if (ReferenceEquals(_roster, window)) _roster = null;
        };
        window.Activate();
    }

    private void OnRosterNameDrawn(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        RaiseClassroomSignal(ClassroomSignalKind.RosterDrawn, name);
    }

    private void PasteRosterNameToBoard(string name) => _ = PasteRosterNameAsync(name);

    /// <summary>把点名结果渲染成卡片贴到当前白板页，画板未开时先打开。</summary>
    private async Task PasteRosterNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            const int width = 220;
            const int height = 72;
            byte[] png = await PasteCardRenderer.RenderPngAsync(
                "点名结果", new[] { name }, width, height);
            if (png is null || png.Length == 0)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"点名贴板：渲染 PNG 失败（{name}）", Core.Diagnostics.LogType.Error);
                return;
            }

            var work = ScreenService.WorkArea;
            double dpi = CurrentDpi();
            double viewWidth = work.Width / dpi;
            double viewHeight = work.Height / dpi;
            double left = Math.Clamp(viewWidth / 2.0 - width / 2.0, 8, Math.Max(8, viewWidth - width - 8));
            double top = Math.Clamp(viewHeight / 2.0 - height / 2.0, 8, Math.Max(8, viewHeight - height - 8));

            string metadata = new AiPasteMetadata
            {
                Kind = AiPasteKind.Steps,
                Text = name,
                Lines = new List<string> { name },
            }.Serialize();

            bool added = Services.Board.AddImage(new BoardImageAsset
            {
                Name = "点名结果",
                Data = png,
                ContentType = "image/png",
                Left = (float)left,
                Top = (float)top,
                Width = width,
                Height = height,
                Metadata = metadata,
            });

            if (!added)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"点名贴板：AddImage 失败（{name}）", Core.Diagnostics.LogType.Error);
                return;
            }

            if (_board is null) ToggleBoard();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"点名贴板失败（{name}）：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    public void OpenSpotlight()
    {
        if (_spotlight is not null) { _spotlight.Activate(); return; }
        var window = new SpotlightWindow(AiAssistantAvailable);
        window.CaptureRequested += askAi => _ = CaptureFocusAsync(askAi);
        window.SaveRegionRequested += OnSaveFocusRegion;
        window.CycleRegionRequested += OnCycleFocusRegion;
        _spotlight = window;
        _spotlight.Closed += (_, _) => _spotlight = null;
        _spotlight.Activate();
    }

    public void OpenMagnifier()
    {
        if (_magnifier is not null) { _magnifier.Activate(); return; }
        var window = new MagnifierWindow(AiAssistantAvailable);
        window.CaptureRequested += askAi => _ = CaptureFocusAsync(askAi);
        RestoreWindowPosition(
            window.MoveTo, Services.Settings.Classroom.MagnifierX, Services.Settings.Classroom.MagnifierY);
        _magnifier = window;
        _magnifier.Closed += (_, _) =>
        {
            try
            {
                Services.Settings.Classroom.MagnifierX = window.PositionX;
                Services.Settings.Classroom.MagnifierY = window.PositionY;
                Services.SaveSettings();
            }
            catch (Exception) { }

            if (ReferenceEquals(_magnifier, window)) _magnifier = null;
        };
        _magnifier.Activate();
    }

    public void OpenToolkit()
    {
        if (_toolkit is not null) { _toolkit.ShowWithoutActivating(); return; }
        var window = new ToolkitWindow(this, Services.Settings.Appearance.Theme,
            OpenExampleLibrary, OpenResourceBundle);
        window.SetCollapsed(Services.Settings.Classroom.ToolkitCollapsed);
        RestoreWindowPosition(
            window.MoveTo, Services.Settings.Classroom.ToolkitX, Services.Settings.Classroom.ToolkitY);
        _toolkit = window;
        _toolkit.Closed += (_, _) =>
        {
            Services.Settings.Classroom.ToolkitCollapsed = window.IsCollapsed;
            try
            {
                Services.Settings.Classroom.ToolkitX = window.PositionX;
                Services.Settings.Classroom.ToolkitY = window.PositionY;
            }
            catch (Exception) { }
            Services.SaveSettings();
            if (ReferenceEquals(_toolkit, window)) _toolkit = null;
        };
        _toolkit.ShowWithoutActivating();
    }

    private static void RestoreWindowPosition(Action<int, int> move, int x, int y)
    {
        if (move is null || x == int.MinValue || y == int.MinValue) return;
        var work = ScreenService.WorkArea;
        int clampedX = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - 120));
        int clampedY = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - 80));
        move(clampedX, clampedY);
    }

    /// <summary>
    /// 打开本地例题库窗口。窗口采用单实例：已创建（包括通过关闭按钮隐藏）时重新显示并置前，
    /// 不重复创建，避免丢失当前筛选与选中状态。
    /// </summary>
    public void OpenExampleLibrary()
    {
        if (_exampleLibrary is not null)
        {
            _exampleLibrary.ShowWindow();
            return;
        }

        var window = new ExampleLibraryWindow(Services.Settings, Services.Settings.Appearance.Theme, this);
        _exampleLibrary = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_exampleLibrary, window)) _exampleLibrary = null;
        };
        window.ShowWindow();
    }

    /// <summary>
    /// 打开课堂资源包窗口。与例题库一致采用单实例：已创建（包括关闭按钮隐藏）时重新显示并置前，
    /// 不重复创建，避免丢失当前文件列表与操作结果提示。打包受
    /// <see cref="InkCanvas.Core.Settings.FeatureSettings.EnableResourceBundle"/> 控制。
    /// </summary>
    public void OpenResourceBundle()
    {
        if (_resourceBundle is not null)
        {
            _resourceBundle.ShowWindow();
            return;
        }

        var window = new ResourceBundleWindow(Services.Settings.Features, Services.Settings.Appearance.Theme,
            Core.Settings.ArchivePolicy.ResolveDirectory(Services.Settings.Archive.DefaultDirectory),
            pptAnnotationRoot: Services.Settings.Automation.AutoSavedStrokesLocation,
            exampleLibraryDirectory: ExampleLibraryStore.DefaultDirectory,
            recordingDirectory: Path.Combine(Services.Store.Directory, "PluginData",
                "inkcanvas.time-machine", "Recordings"));
        _resourceBundle = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_resourceBundle, window)) _resourceBundle = null;
        };
        window.ShowWindow();
    }

    // ---- 课堂场景 / 分组积分 / 讲义导出 / 聚焦贴图 -----------------------

    /// <summary>
    /// 例题「再讲一次」：切回记录的画板页/课件页，复用 <see cref="OpenAiAssistant"/> 打开浮窗，
    /// 预填追问并在首次识别完成后自动提交；不新建教学模式、不修改原例题。
    /// </summary>
    public void ReExplainExample(ExampleEntry entry)
    {
        if (entry is null) return;

        try
        {
            RestoreExampleBoardPage(entry);
            OpenAiAssistant();
            if (_aiAssistant is null) return;

            ApplyExampleSlide(entry);
            ApplyFavoriteContext(_aiAssistant, entry.SourcePresentation, entry.SourceSlideId,
                entry.BoardPage);

            string prompt = ExampleReExplainPrompt.Build(entry);
            if (string.IsNullOrWhiteSpace(prompt)) return;

            _aiPendingExamplePrompt = prompt;
            _aiAssistant.PrefillFollowUp(prompt);
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 再讲一次：title={entry.Title}，slide={entry.SourceSlideId}，boardPage={entry.BoardPage}，"
                + $"session={entry.AiSessionId}，objectIds={entry.ObjectIds?.Count ?? 0}",
                Core.Diagnostics.LogType.Event);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 再讲一次失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            _aiAssistant?.ShowNotice("再讲失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 例题「贴回白板」：优先用记录步骤生成原生文本/公式对象（<see cref="BoardPlanWriter"/>），
    /// 无步骤时退化为旧 AI 卡片 PNG（<see cref="AiPasteMetadata"/> 兼容路径）。
    /// 不修改原例题，不触发 AI 请求。
    /// </summary>
    public async Task<bool> PasteExampleToBoardAsync(ExampleEntry entry)
    {
        if (entry is null) return false;

        try
        {
            BoardService board = ExampleTargetBoard(entry);
            if (board is null)
            {
                if (_board is null) ToggleBoard();
                board = Services.Board;
            }
            if (board is null) return false;

            AssistantBoardPlan plan = ExampleBoardPlanFactory.Create(entry);
            if (!plan.IsEmpty)
                return PasteExampleNativeObjects(board, entry, plan);

            List<string> lines = ExampleBoardPlanFactory.LegacyLines(entry);
            if (lines.Count == 0) return false;

            string metadata = new AiPasteMetadata
            {
                Kind = AiPasteKind.BoardPlan,
                Text = string.Join("\n", lines),
                Lines = new List<string>(lines),
                Instruction = "[例题贴回] " + (entry.Title ?? ""),
                Title = entry.Title ?? "",
            }.Serialize();

            bool pasted = await PasteCardToBoardAsync(board, "例题贴回", lines, metadata);
            if (pasted && ReferenceEquals(board, Services.Board) && _board is null) ToggleBoard();
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 例题贴回（旧卡片）：title={entry.Title}，lines={lines.Count}，success={pasted}",
                Core.Diagnostics.LogType.Event);
            return pasted;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 例题贴回失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            return false;
        }
    }

    private bool PasteExampleNativeObjects(BoardService board, ExampleEntry entry, AssistantBoardPlan plan)
    {
        BoardPlanNativeResult created = BoardPlanWriter.Create(plan, "[例题贴回] " + (entry.Title ?? ""));
        if (!created.Success)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 例题贴回原生对象失败：{created.Error}", Core.Diagnostics.LogType.Error);
            return false;
        }

        var sizes = new List<BoardCardSize>(created.Count);
        foreach (var text in created.Texts) sizes.Add(new BoardCardSize(text.Width, text.Height));
        foreach (var formula in created.Formulas) sizes.Add(new BoardCardSize(formula.Width, formula.Height));

        var content = board.GetContentBounds();
        var contentBounds = new AssistantRect(content.Left, content.Top, content.Right, content.Bottom);
        BoardLayoutPlan layout = BoardContentLayoutPlanner.Plan(
            contentBounds, sizes, BoardViewportRect(board), safeInsets: BoardSafeInsetsFor(board));
        if (!layout.IsComplete)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 例题贴回未写入：status={layout.Status}，message={layout.Message}",
                Core.Diagnostics.LogType.Info);
            return false;
        }

        for (int index = 0; index < created.Texts.Count; index++)
        {
            BoardCardPlacement placement = layout.Placements[index];
            created.Texts[index].Left = (float)placement.Left;
            created.Texts[index].Top = (float)placement.Top;
            created.Texts[index].Width = (float)placement.Width;
            created.Texts[index].Height = (float)placement.Height;
        }
        for (int index = 0; index < created.Formulas.Count; index++)
        {
            BoardCardPlacement placement = layout.Placements[created.Texts.Count + index];
            created.Formulas[index].Left = (float)placement.Left;
            created.Formulas[index].Top = (float)placement.Top;
            created.Formulas[index].Width = (float)placement.Width;
            created.Formulas[index].Height = (float)placement.Height;
        }

        string groupId = Guid.NewGuid().ToString("N");
        IReadOnlyList<Guid> objectIds = board.AddAiObjectsAtomic(
            created.Texts, created.Formulas, "例题贴回", groupId);
        if (objectIds.Count == 0) return false;

        SelectAiBoardGroup(board, groupId);
        if (ReferenceEquals(board, Services.Board) && _board is null) ToggleBoard();
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 例题贴回（原生对象）：title={entry.Title}，texts={created.Texts.Count}，"
            + $"formulas={created.Formulas.Count}，group={groupId}，ids={string.Join(",", objectIds)}",
            Core.Diagnostics.LogType.Event);
        return true;
    }

    /// <summary>例题所属画板：side 且子板存在时优先子板，其余回退主展板。</summary>
    private BoardService ExampleTargetBoard(ExampleEntry entry)
        => string.Equals(entry?.BoardId, "side", StringComparison.OrdinalIgnoreCase)
           && _sideBoard is not null
            ? Services.SideBoard
            : Services.Board;

    /// <summary>例题再讲/贴回时切到记录的画板页；页序号越界时保持当前页。</summary>
    private void RestoreExampleBoardPage(ExampleEntry entry)
    {
        if (entry is null || entry.BoardPage < 0) return;

        BoardService board = ExampleTargetBoard(entry);
        if (board is null)
        {
            if (_board is null) ToggleBoard();
            board = Services.Board;
        }
        if (board is null || entry.BoardPage >= board.Pages.Count) return;
        board.GoTo(entry.BoardPage);
    }

    /// <summary>设置再讲使用的幻灯片身份；当前放映课件与记录不一致时保留现状，仅提示来源在追问里。</summary>
    private void ApplyExampleSlide(ExampleEntry entry)
    {
        if (entry is null || entry.SourceSlideId <= 0) return;

        string current = CurrentPresentationName();
        if (!string.IsNullOrWhiteSpace(entry.SourcePresentation)
            && !string.IsNullOrWhiteSpace(current)
            && !string.Equals(current, entry.SourcePresentation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _aiSlideId = entry.SourceSlideId;
        if (_slideIndex?.Slides is { Count: > 0 })
        {
            SlideContentEntry slide = _slideIndex.Slides
                .FirstOrDefault(item => item is not null && item.SlideId == entry.SourceSlideId);
            if (slide is not null) _aiSlideIndex = slide.Index;
        }
    }

    public void CyclePreset()
    {
        var presets = new List<ClassroomPreset>(ClassroomPresetLibrary.BuiltIns);
        presets.AddRange(LoadCustomPresets());

        int current = 0;
        for (int i = 0; i < presets.Count; i++)
        {
            if (string.Equals(presets[i].Id, _preset?.Id, StringComparison.Ordinal))
            {
                current = i;
                break;
            }
        }

        ApplyPreset(presets[(current + 1) % presets.Count]);
    }

    public void OpenPresetEditor()
    {
        if (_presetWindow is not null)
        {
            _presetWindow.Activate();
            return;
        }

        var window = new PresetWindow(_preset, Services.Settings.Appearance.Theme, AiAssistantAvailable);
        window.Saved += preset =>
        {
            SaveCustomPreset(preset);
            ApplyPreset(preset);
        };
        _presetWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_presetWindow, window)) _presetWindow = null;
        };
        window.Activate();
    }

    private IReadOnlyList<ClassroomPreset> LoadCustomPresets()
        => ClassroomPresetLibrary.Deserialize(Services.Settings.Classroom.CustomPresets);

    private void SaveCustomPreset(ClassroomPreset preset)
    {
        if (preset is null || preset.IsBuiltIn) return;

        var presets = new List<ClassroomPreset>(LoadCustomPresets());
        int index = presets.FindIndex(item =>
            string.Equals(item.Id, preset.Id, StringComparison.Ordinal));
        if (index >= 0) presets[index] = preset;
        else presets.Add(preset);

        Services.Settings.Classroom.CustomPresets =
            ClassroomPresetLibrary.Serialize(presets);
        Services.Settings.Classroom.PresetsVersion = ClassroomPreset.CurrentVersion;
        Services.SaveSettings();
    }

    public void ApplyPreset(ClassroomPreset preset)
    {
        if (preset is null) return;

        ClassroomPreset normalized = ClassroomPresetLibrary.Normalize(preset.Clone(), preset.Id);
        _preset = normalized;
        Services.Settings.Classroom.PresetId = normalized.Id;
        Services.SaveSettings();

        PresetAction action = ClassroomPresetLibrary.Plan(normalized);
        foreach (ClassroomTool tool in action.Open) OpenClassroomTool(tool);
        foreach (ClassroomTool tool in action.Close) CloseClassroomTool(tool);
        if (action.CollapseToolkit && _toolkit is not null) _toolkit.SetCollapsed(true);

        if (_teachingSession is not null)
        {
            _teachingSession.PresetId = normalized.Id;
            _teachingSession.UpdatedAtMs = Environment.TickCount64;
        }

        if (!normalized.IsBuiltIn)
        {
            SaveCustomPreset(normalized);
        }

        _ = NotifyAsync("课堂场景", "已切换到「" + normalized.Name + "」。");
    }

    private void OpenClassroomTool(ClassroomTool tool)
    {
        switch (tool)
        {
            case ClassroomTool.SlideNavigator:
                if (_slideNav?.IsVisible != true) ToggleSlideNavigator();
                break;
            case ClassroomTool.AiAssistant:
            case ClassroomTool.AiStepGuide:
                OpenAiAssistant();
                break;
            case ClassroomTool.Spotlight:
                OpenSpotlight();
                break;
            case ClassroomTool.Magnifier:
                OpenMagnifier();
                break;
            case ClassroomTool.Board:
                if (_board is null) ToggleBoard();
                break;
            case ClassroomTool.Countdown:
                OpenTimer();
                break;
            case ClassroomTool.GroupScore:
                OpenGroupScore();
                break;
            case ClassroomTool.HandoutExport:
                OpenHandout();
                break;
            case ClassroomTool.Toolkit:
                OpenToolkit();
                break;
        }
    }

    private void CloseClassroomTool(ClassroomTool tool)
    {
        switch (tool)
        {
            case ClassroomTool.Countdown:
                _countdown?.Close();
                break;
            case ClassroomTool.Spotlight:
                _spotlight?.Close();
                break;
            case ClassroomTool.Magnifier:
                _magnifier?.Close();
                break;
            case ClassroomTool.GroupScore:
                _groupScoreWindow?.Close();
                break;
            case ClassroomTool.SlideNavigator:
                _slideNav?.HidePanel();
                break;
        }
    }

    public void OpenGroupScore()
    {
        EnsureGroupScore();

        if (_groupScoreWindow is not null)
        {
            _groupScoreWindow.Activate();
            return;
        }

        var window = new GroupScoreWindow(
            _groupScore,
            LoadRosterNames(),
            ParseGroupMode(Services.Settings.Classroom.GroupSplitMode),
            Services.Settings.Classroom.GroupSplitValue,
            Services.Settings.Appearance.Theme,
            text => _ = PasteTextCardAsync("小组积分", text));
        window.StateChanged += _ =>
        {
            Services.Settings.Classroom.GroupSplitMode =
                window.Mode == GroupSplitMode.BySize ? "size" : "groupCount";
            Services.Settings.Classroom.GroupSplitValue = window.SplitValue;
            SaveGroupScore();
            Services.SaveSettings();
        };
        window.Closed += (_, _) =>
        {
            SaveGroupScore();
            if (ReferenceEquals(_groupScoreWindow, window)) _groupScoreWindow = null;
        };

        _groupScoreWindow = window;
        WindowChrome.ApplyTheme(window, Services.Settings.Appearance.Theme);
        window.Activate();
    }

    private void EnsureGroupScore()
    {
        if (_groupScore is not null) return;
        _groupScore = GroupScoreService.Deserialize(TryRead(GroupScorePath()));
    }

    private static GroupSplitMode ParseGroupMode(string value)
        => string.Equals(value, "size", StringComparison.OrdinalIgnoreCase)
            ? GroupSplitMode.BySize
            : GroupSplitMode.ByGroupCount;

    private static string GroupScorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "InkCanvas", "groups.json");

    private void SaveGroupScore()
    {
        try
        {
            if (_groupScore is null) return;
            string path = GroupScorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _groupScore.Serialize());
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"分组积分保存失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    private IReadOnlyList<string> LoadRosterNames()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "InkCanvas", "roster.json");
            if (!File.Exists(path)) return Array.Empty<string>();

            var roster = new InkCanvas.Features.Classroom.ClassroomRoster();
            return roster.Load(path) ? roster.Names : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void OpenHandout() => _ = ShowHandoutAsync();

    private async Task ShowHandoutAsync()
    {
        SlideContentIndex index = EnsureSlideIndexForExport();
        if (index is null || index.Count == 0)
        {
            await NotifyAsync("课后讲义", "请先在课件放映中翻几页，让页索引建立后再导出。");
            return;
        }

        var mode = new RadioButtons();
        mode.Items.Add("批注 + 教师解析");
        mode.Items.Add("仅批注");
        mode.SelectedIndex = 0;

        var selectionOnly = new CheckBox { Content = "仅导出勾选页（默认全部）" };
        var pageChecks = new List<(int SlideId, CheckBox Box)>();
        var pageList = new StackPanel { Spacing = 2 };
        foreach (SlideContentEntry entry in index.Slides)
        {
            var box = new CheckBox
            {
                Content = "第 " + entry.Index + " 页"
                    + (entry.HasInk ? " · 有批注" : "")
                    + (entry.Favorite ? " · 收藏" : "")
                    + (entry.HasAi ? " · AI" : ""),
                IsChecked = true,
                MinHeight = 30,
            };
            pageChecks.Add((entry.SlideId, box));
            pageList.Children.Add(box);
        }

        var preview = new Image
        {
            Height = 132,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
        SlideContentEntry first = index.Slides[0];
        if (!string.IsNullOrWhiteSpace(first.ThumbnailPath) && File.Exists(first.ThumbnailPath))
        {
            try { preview.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri(first.ThumbnailPath)); }
            catch (Exception) { }
        }

        var content = new StackPanel { Spacing = 8 };
        string summaryPageName = SummaryBoardPolicy.ResolvePageName(
            Services.Settings.Classroom.SummaryPageName);
        var boardPageInfos = new List<HandoutComposer.BoardPageInfo>();
        for (int pageIndex = 0; pageIndex < Services.Board.PageCount; pageIndex++)
        {
            boardPageInfos.Add(new HandoutComposer.BoardPageInfo(
                pageIndex,
                Services.Board.PageHasContent(pageIndex),
                string.Equals(Services.Board.PageName(pageIndex), summaryPageName, StringComparison.Ordinal)));
        }
        HandoutComposer.PreviewSummary summary = HandoutComposer.Summarize(
            index,
            new HandoutOptions { Content = HandoutContentMode.AnnotationsWithTeacherNotes },
            boardPageInfos);
        content.Children.Add(new TextBlock
        {
            Text = "预计导出：" + summary.Describe() + "（原课件不会被修改）",
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock { Text = "导出前预览（第一页）", FontSize = 12, Opacity = 0.75 });
        content.Children.Add(preview);
        content.Children.Add(mode);
        content.Children.Add(selectionOnly);
        content.Children.Add(new ScrollViewer
        {
            Content = pageList,
            Height = 170,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var dialog = new ContentDialog
        {
            Title = "导出课后讲义",
            Content = content,
            PrimaryButtonText = "导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        var options = new HandoutOptions
        {
            Content = mode.SelectedIndex == 1
                ? HandoutContentMode.AnnotationsOnly
                : HandoutContentMode.AnnotationsWithTeacherNotes,
        };

        if (selectionOnly.IsChecked == true)
        {
            options.SelectedSlideIds = pageChecks
                .Where(pair => pair.Box.IsChecked == true)
                .Select(pair => pair.SlideId)
                .ToList();
        }

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "InkCanvas",
            "handout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        HandoutExportResult result = await HandoutExportService.ExportAsync(
            new HandoutRenderRequest { Index = index, Options = options, Board = Services.Board }, directory);

        if (!result.Success)
        {
            string reason = result.Warnings.Count > 0 ? string.Join("；", result.Warnings) : "没有生成任何页面。";
            await NotifyAsync("讲义导出失败", reason + "（原课件未被修改）");
            return;
        }

        _teachingSession?.RecordExport(result.PdfPath, Environment.TickCount64);
        SaveTeachingSession();
        await NotifyAsync("讲义已导出",
            "共 " + result.Pages.Count + " 页，目录：" + directory
            + (string.IsNullOrEmpty(result.PdfPath) ? "（仅 PNG）" : "（含 PDF）"));
    }

    private SlideContentIndex EnsureSlideIndexForExport()
    {
        if (_slideIndex is { Count: > 0 }) return _slideIndex;

        PptSnapshot snapshot = _pptSession.Snapshot;
        PptPresentationInfo presentation = snapshot?.Presentation;

        try
        {
            if (presentation is not null)
            {
                SlideContentIndex sidecar = LoadSlideIndexSidecar(presentation);
                if (sidecar is { Count: > 0 }) return _slideIndex = sidecar;
            }

            // 不在放映时仍允许导出最近一次课件的讲义（课后沉淀场景）。
            SlideContentIndex latest = LoadLatestSlideIndexSidecar();
            if (latest is not null) return _slideIndex = latest;
            if (presentation is null) return _slideIndex;

            PptPresentationDetails details = Services.Ppt.GetPresentationDetails();
            if (details is null) return _slideIndex;

            _slideIndex = SlideContentIndex.Build(
                details, _pptSession.CreateArchive(), null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string directory = _pptStore.GetPresentationDirectory(presentation);
            foreach (SlideContentEntry entry in _slideIndex.Slides)
            {
                if (entry.SlideId <= 0 || !string.IsNullOrEmpty(entry.ThumbnailPath)) continue;
                string thumb = Path.Combine(directory, "thumb-" + entry.SlideId + ".png");
                if (File.Exists(thumb)) entry.ThumbnailPath = thumb;
            }

            return _slideIndex;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"讲义导出索引失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            return _slideIndex;
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (dialog is null) return ContentDialogResult.None;

        DialogHost host = ResolveDialogHost();
        if (!DialogHostPolicy.IsUsable(host.Surface) || host.Root is null)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[UI] ContentDialog 无可用宿主：surface={host.Surface}，"
                + $"bar={_bar is not null}，board={_board is not null}，assistant={_aiAssistant is not null}",
                Core.Diagnostics.LogType.Error);
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = host.Root;
        bool suspendedAssistant = SuspendAssistantForDialog(host.Surface);
        if (host.RestoreFoldedBar)
        {
            try { _bar?.SetFolded(false); }
            catch (Exception ex)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[UI] 弹窗临时展开浮条失败：{ex.Message}", Core.Diagnostics.LogType.Trace);
            }
        }
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[UI] ContentDialog 宿主：surface={host.Surface}，suspendedAssistant={suspendedAssistant}，"
            + $"restoreFoldedBar={host.RestoreFoldedBar}，rootSet={(dialog.XamlRoot is not null)}",
            Core.Diagnostics.LogType.Info);
        try
        {
            PrepareDialogHost(host.Window, host.Surface);
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[UI] ContentDialog 显示失败：surface={host.Surface}，{ex.Message}",
                Core.Diagnostics.LogType.Error);
            return ContentDialogResult.None;
        }
        finally
        {
            RestoreAssistantAfterDialog(suspendedAssistant);
            if (host.RestoreFoldedBar)
            {
                try { _bar?.SetFolded(true); }
                catch (Exception ex)
                {
                    Core.Diagnostics.FileLog.WriteLogToFile(
                        $"[UI] 弹窗结束恢复浮条折叠失败：{ex.Message}", Core.Diagnostics.LogType.Trace);
                }
            }
        }
    }

    /// <summary>
    /// 对话框宿主解析：当前目标窗口优先（侧板/主展板），其次文档、浮条，最后 AI 浮窗兜底。
    /// 只读取窗口可见性与 XamlRoot，不修改任何 UI。
    /// </summary>
    private DialogHost ResolveDialogHost()
    {
        bool sideBoardVisible = _sideBoard is { IsShown: true } && TryGetXamlRoot(_sideBoard) is not null;
        bool sideBoardPreferred = _aiTarget?.Kind == AssistantCanvasKind.SideBoard
                                  || (_sideBoard is not null
                                      && ReferenceEquals(ActiveController, _sideBoard.CurrentController));
        bool mainBoardVisible = _board is not null && IsWindowVisible(_board)
                                && TryGetXamlRoot(_board) is not null;
        bool documentVisible = DocumentActive && _documentWorkspace?.Window is not null
                               && IsWindowVisible(_documentWorkspace.Window)
                               && TryGetXamlRoot(_documentWorkspace.Window) is not null;
        bool barVisible = _bar is not null && !_bar.IsFolded && IsWindowVisible(_bar);
        XamlRoot barRoot = TryGetXamlRoot(_bar);
        XamlRoot assistantRoot = TryGetXamlRoot(_aiAssistant);
        bool assistantVisible = assistantRoot is not null && !_aiHiddenWithSideBoard
                                && IsWindowVisible(_aiAssistant);

        DialogHostSurface surface = DialogHostPolicy.Select(new DialogHostContext(
            sideBoardVisible,
            sideBoardPreferred,
            mainBoardVisible,
            documentVisible,
            barRoot is not null && barVisible,
            assistantVisible));

        Window window = surface switch
        {
            DialogHostSurface.SideBoard => _sideBoard,
            DialogHostSurface.MainBoard => _board,
            DialogHostSurface.Document => _documentWorkspace?.Window,
            DialogHostSurface.FloatingBar => _bar,
            DialogHostSurface.Assistant => _aiAssistant,
            _ => null,
        };

        XamlRoot root = TryGetXamlRoot(window);
        if (root is not null) return new DialogHost(window, root, surface, RestoreFoldedBar: false);

        // 最后兜底：浮条折叠时窗口被 Hide，但 XamlRoot 仍在——直接显示会得到不可见对话框。
        // 此时临时展开浮条承载，关闭后恢复折叠状态。
        if (barRoot is not null && _bar is not null)
            return new DialogHost(_bar, barRoot, DialogHostSurface.FloatingBar, RestoreFoldedBar: _bar.IsFolded);

        return new DialogHost(null, null, DialogHostSurface.None, RestoreFoldedBar: false);
    }

    /// <summary>激活宿主并把它提到浮条/AI 浮窗之上，避免 ContentDialog 被常驻 topmost 子窗口遮挡。</summary>
    private void PrepareDialogHost(Window window, DialogHostSurface surface)
    {
        if (window is null) return;

        try { window.Activate(); }
        catch (Exception) { /* 激活失败不阻断弹窗，XamlRoot 仍可承载对话框 */ }

        WindowChrome.EnsureAbove(window, Overlay.Window.Handle);
        RaiseAboveAllDialogSiblings(window, surface);
    }

    private void RaiseAboveAllDialogSiblings(Window window, DialogHostSurface surface)
    {
        if (surface is DialogHostSurface.FloatingBar or DialogHostSurface.Assistant) return;

        if (_bar is not null && !ReferenceEquals(window, _bar))
        {
            IntPtr bar = WindowChrome.GetHwnd(_bar);
            if (bar != IntPtr.Zero) WindowChrome.EnsureAbove(window, bar);
        }

        if (_aiAssistant is not null && !ReferenceEquals(window, _aiAssistant))
        {
            IntPtr assistant = WindowChrome.GetHwnd(_aiAssistant);
            if (assistant != IntPtr.Zero) WindowChrome.EnsureAbove(window, assistant);
        }
    }

    /// <summary>板/文档宿主内的模态对话框会被拥有的 AI 浮窗盖住；弹窗期间暂时收起，结束后恢复。</summary>
    private bool SuspendAssistantForDialog(DialogHostSurface surface)
    {
        if (!DialogHostPolicy.RequiresAssistantSuspension(surface)) return false;
        if (_aiAssistant is null || _aiHiddenWithSideBoard) return false;

        try
        {
            _aiAssistant.AppWindow.Hide();
            return true;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[UI] 弹窗期间收起 AI 浮窗失败：{ex.Message}", Core.Diagnostics.LogType.Trace);
            return false;
        }
    }

    private void RestoreAssistantAfterDialog(bool suspended)
    {
        if (!suspended || _aiAssistant is null) return;

        try
        {
            _aiAssistant.AppWindow.Show();
            _aiAssistant.Activate();
            IntPtr anchor = _board is not null ? WindowChrome.GetHwnd(_board) : Overlay.Window.Handle;
            if (AssistantBelongsToSideBoard && _sideBoard is not null)
                anchor = WindowChrome.GetHwnd(_sideBoard);
            WindowChrome.EnsureAbove(_aiAssistant, anchor);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[UI] 弹窗结束恢复 AI 浮窗失败：{ex.Message}", Core.Diagnostics.LogType.Trace);
        }
    }

    private static XamlRoot TryGetXamlRoot(Window window)
    {
        if (window is null) return null;
        try
        {
            return (window.Content as FrameworkElement)?.XamlRoot;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsWindowVisible(Window window)
    {
        if (window is null) return false;
        try
        {
            return window.AppWindow?.IsVisible == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private readonly record struct DialogHost(
        Window Window, XamlRoot Root, DialogHostSurface Surface, bool RestoreFoldedBar);

    public void PasteSpotlightToBoard() => _ = CaptureFocusAsync(askAi: false);

    public void AskAiFromSpotlight() => _ = CaptureFocusAsync(askAi: true);

    private async Task CaptureFocusAsync(bool askAi)
    {
        ScreenRect rect;
        bool fromSpotlight = _spotlight is not null;

        if (_spotlight is not null)
        {
            rect = _spotlight.CurrentScreenRect;
            _spotlight.SetMaskVisible(false);
        }
        else if (_magnifier is not null)
        {
            rect = _magnifier.CurrentScreenRect;
            _magnifier.HideForCapture();
        }
        else
        {
            await NotifyAsync("聚焦贴图", "请先打开聚光灯或放大镜，调整好聚焦区域后再贴图。");
            return;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            RestoreFocusWindow(fromSpotlight);
            await NotifyAsync("聚焦贴图", "聚焦区域尚未确定，请先拖动聚光灯或移动放大镜取景。");
            return;
        }

        try
        {
            await Task.Delay(90);

            if (askAi)
            {
                await StartAssistantFromRegionAsync(rect);
                return;
            }

            string base64 = await AssistantCapture.CapturePngBase64Async(
                rect.X, rect.Y, rect.Width, rect.Height);
            byte[] png = string.IsNullOrEmpty(base64)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(base64);
            if (png.Length == 0)
            {
                await NotifyAsync("聚焦贴图", "截图失败，请重试。");
                return;
            }

            await PasteImageCardAsync("聚焦区域", png, 320, 220, "聚光灯/放大镜截图");
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"聚焦贴图失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
        finally
        {
            RestoreFocusWindow(fromSpotlight);
        }
        QueueDesktopSelection();
    }

    private void RestoreFocusWindow(bool fromSpotlight)
    {
        if (fromSpotlight) _spotlight?.SetMaskVisible(true);
        else _magnifier?.ShowAfterCapture();
    }

    private void OnSaveFocusRegion(ScreenRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _ = NotifyAsync("聚焦区域", "当前聚焦区无效，请先拖动聚光灯。");
            return;
        }

        FocusRegionItem saved = _focusRegions.Add(new FocusRegionItem
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
            SlideId = _pptSession.Snapshot?.Slide?.SlideId ?? 0,
            SlideIndex = _pptSession.Snapshot?.Slide?.Index ?? 0,
            AnimationStep = _pptSession.Snapshot?.IsSlideShowRunning == true
                ? Services.Ppt.GetAnimationStep()
                : 0,
            BoardPageIndex = _board is null ? -1 : Services.Board.CurrentIndex,
        }, Environment.TickCount64);

        if (saved is null)
        {
            _ = NotifyAsync("聚焦区域", "保存失败（最多保存 " + FocusRegionStore.MaximumRegions + " 个区域）。");
            return;
        }

        PersistFocusRegions();
        _ = NotifyAsync("聚焦区域", "已保存「" + saved.Name + "」，共 " + _focusRegions.Count + " 个。");
    }

    private void OnCycleFocusRegion(int delta)
    {
        if (_focusRegions.Count == 0)
        {
            _ = NotifyAsync("聚焦区域", "还没有保存的聚焦区域；点「保存区」先保存当前区域。");
            return;
        }

        ScreenRect currentRect = _spotlight?.CurrentScreenRect ?? default;
        FocusRegionItem current = currentRect.Width > 0
            ? _focusRegions.FindMatching(
                currentRect.X, currentRect.Y, currentRect.Width, currentRect.Height)
            : null;

        FocusRegionItem next = _focusRegions.Next(current, delta);
        if (next is null) return;

        _spotlight?.SetRegion(new ScreenRect(next.X, next.Y, next.Width, next.Height));
        _ = NotifyAsync("聚焦区域", "切换到「" + next.Name + "」（" + next.Order + "/" + _focusRegions.Count + "）。");
    }

    private void PersistFocusRegions()
    {
        Services.Settings.Classroom.SpotlightRegions = _focusRegions.Serialize();
        Services.SaveSettings();
    }

    private async Task StartAssistantFromRegionAsync(ScreenRect rect)
    {
        _aiTarget = CaptureAssistantTarget();
        _aiContext = CaptureClassroomContext();
        SyncRecorderContext();
        string thumb = await AssistantCapture.CapturePngBase64Async(
            rect.X, rect.Y, rect.Width, rect.Height);
        double dpi = CurrentDpi() > 0 ? CurrentDpi() : 1.0;

        _aiAnchor = new AssistantRect(
            rect.X / dpi, rect.Y / dpi,
            (rect.X + rect.Width) / dpi, (rect.Y + rect.Height) / dpi);
        _aiThumb = thumb;
        _aiStrokes = new List<AssistantStroke>();
        _aiScale = 1.0;
        _aiSlideId = _pptSession.Snapshot?.Slide?.SlideId ?? 0;
        _aiSlideIndex = _pptSession.Snapshot?.Slide?.Index ?? 0;

        var problem = new ProblemContext
        {
            SlideId = _aiSlideId,
            SlideIndex = _aiSlideIndex,
            AnimationStep = _pptSession.Snapshot?.IsSlideShowRunning == true
                ? Services.Ppt.GetAnimationStep()
                : 0,
        };
        ProblemContextPolicy.Add(problem, new ProblemRegion
        {
            ImageBase64 = thumb,
            Anchor = _aiAnchor,
            Source = "focus",
        });
        _aiProblem = problem;

        PrepareAssistantWindow();
        StartAssistantStream(BuildProblemRequest(), followUp: false, question: "");
    }

    private async Task PasteTextCardAsync(string title, string text)
    {
        var lines = new List<string>();
        foreach (string line in (text ?? "").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) lines.Add(trimmed);
        }

        if (lines.Count == 0) return;

        var (width, height) = PasteCardLayout.Estimate(lines);
        byte[] png = await PasteCardRenderer.RenderPngAsync(title, lines, width, height);
        if (png.Length == 0) return;

        await PasteImageCardAsync(title, png, width, height, text);
    }

    private async Task PasteImageCardAsync(
        string title, byte[] png, int width, int height, string metadataText)
    {
        string source = "";
        if (_pptSession.Snapshot?.Presentation is not null)
            source = "课件第 " + (_pptSession.Snapshot?.Slide?.Index ?? 0) + " 页";
        else if (_board is not null)
            source = "画板第 " + (Services.Board.CurrentIndex + 1) + " 页";

        if (!string.IsNullOrEmpty(source))
            metadataText = (metadataText ?? "") + "\n来源：" + source;

        var work = ScreenService.WorkArea;
        double dpi = CurrentDpi() > 0 ? CurrentDpi() : 1.0;
        double viewWidth = work.Width / dpi;
        double viewHeight = work.Height / dpi;
        double left = Math.Clamp(viewWidth / 2.0 - width / 2.0, 8, Math.Max(8, viewWidth - width - 8));
        double top = Math.Clamp(viewHeight / 2.0 - height / 2.0, 8, Math.Max(8, viewHeight - height - 8));

        string metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.Steps,
            Text = metadataText ?? "",
            Lines = (metadataText ?? "").Split('\n').Where(line => line.Trim().Length > 0).ToList(),
        }.Serialize();

        bool added = Services.Board.AddImage(new BoardImageAsset
        {
            Name = title,
            Data = png,
            ContentType = "image/png",
            Left = (float)left,
            Top = (float)top,
            Width = width,
            Height = height,
            Metadata = metadata,
        });

        if (added && _board is null) ToggleBoard();
        if (added)
        {
            _teachingSession?.Link(
                "tool", Guid.NewGuid().ToString("N"), _pptSession.Snapshot?.Slide?.SlideId ?? 0, _pptSession.Snapshot?.Slide?.Index ?? 0,
                Services.Board.CurrentIndex, title, Environment.TickCount64);
        }
    }

    /// <summary>WPF 保存墨迹: save the active surface (board page or overlay) as .icink.</summary>
    public void SaveStrokes() => _ = SaveStrokesAsync();

    public void InsertImage() => _ = InsertImageAsync();

    internal async Task InsertImageAsync()
    {
        if (DocumentActive) { await NotifyAsync("PDF 批注", "PDF 文档中可使用笔、荧光笔和形状批注。图片可插入画板页。"); return; }
        if (_imagePicking) return;
        _imagePicking = true;
        var cancellation = new CancellationTokenSource();
        _imageImportCancellation = cancellation;
        try
        {
            if (_board is not null) { await _board.InsertImageAsync(); return; }
            var controller = Services.OverlayInk;
            bool IsCurrent() => !_exiting && !_disposed && _board is null && !DocumentActive;
            var viewport = new System.Numerics.Vector2(Overlay.Window.Width / Overlay.Window.DpiScale,
                Overlay.Window.Height / Overlay.Window.DpiScale);
            var asset = await ImageImport.PickAsync(_bar, Services.Settings.Canvas.IsCompressPicturesUploaded,
                viewport, controller.ViewTransform, cancellation.Token);
            if (asset is not null && await ImageImport.WaitForCanvasAsync(controller, IsCurrent, cancellation.Token))
            {
                viewport = new(Overlay.Window.Width / Overlay.Window.DpiScale, Overlay.Window.Height / Overlay.Window.DpiScale);
                ImageImport.PlaceInView(asset, viewport, controller.ViewTransform);
                InsertDesktopImage(asset);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_exiting && !_disposed) await NotifyAsync("插入图片", $"图片插入失败：{ex.Message}"); }
        finally
        {
            _imagePicking = false;
            if (ReferenceEquals(_imageImportCancellation, cancellation)) _imageImportCancellation = null;
            cancellation.Dispose();
        }
    }

    internal void InsertDesktopImage(BoardImageAsset asset)
    {
        if (!Services.ScreenImages.AddImage(asset)) return;
        Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
        Services.OverlayInk.SelectElement(Services.ScreenImages.GetSelectionElements().Single(element => element.Id == asset.Id));
        if (_bar is not null) { _bar.Bar.ClickThroughActive = false; _bar.Bar.RefreshSelection(); }
    }

    internal void CloneDesktopSelectionToBoard()
    {
        var strokes = Core.Ink.InkStroke.CloneObjects(Services.OverlayInk.Selection);
        var images = Services.OverlayInk.SelectedElements.OfType<BoardSelectionElement>()
            .Where(element => element.Image is not null).Select(element => element.Image.Copy(preserveId: false, copyData: false)).ToList();
        if (strokes.Count == 0 && images.Count == 0) return;
        var center = Services.OverlayInk.SelectionBounds.Center;
        if (Services.Board.Current.Document.Count > 0 || Services.Board.CurrentImages.Count > 0 || Services.Board.CurrentMedia.Count > 0)
        {
            if (!Services.Board.InsertPageAfterCurrent())
            {
                _ = NotifyAsync("衍至画板", "已达到画板页数上限。");
                return;
            }
        }
        ToggleBoard();
        var target = Services.Board.Current;
        var delta = System.Numerics.Matrix3x2.CreateTranslation(target.ViewCenter - center);
        foreach (var stroke in strokes) stroke.Transform(delta, scaleWidth: false);
        foreach (var image in images) image.Transform = BoardElementMath.Append(image.Transform, delta);
        target.Document.AddRange(strokes);
        foreach (var image in images) Services.Board.SetImagePresent(target, image, true);
        target.History.Push(new Core.History.DelegateAction("clone to board",
            () => { target.Document.AddRange(strokes); foreach (var image in images) Services.Board.SetImagePresent(target, image, true); },
            () => { target.Document.RemoveAll(strokes); foreach (var image in images) Services.Board.SetImagePresent(target, image, false); }));
        target.Tool = InkTool.Select;
        target.SelectAll();
    }

    internal async Task ExportDesktopSelectionAsync()
    {
        var strokes = Services.OverlayInk.Selection.Select(stroke => stroke.Clone()).ToList();
        var images = Services.OverlayInk.SelectedElements.OfType<BoardSelectionElement>()
            .Where(element => element.Image is not null).Select(element => element.Image.Copy(copyData: false)).ToList();
        var bounds = Services.OverlayInk.SelectionBounds;
        if (strokes.Count == 0 && images.Count == 0 || bounds.IsEmpty) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"选中墨迹 {DateTime.Now:yyyy-MM-dd HH-mm-ss}" };
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd((Window)_selectionToolbar ?? _bar));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            using var controller = new InkController();
            controller.Document.AddRange(strokes);
            var imageStore = new BoardService(controller);
            foreach (var image in images) imageStore.SetImagePresent(controller, image, true);
            controller.GetSelectableElements = imageStore.GetSelectionElements;
            controller.ViewTransform = System.Numerics.Matrix3x2.CreateTranslation(-bounds.Left + 5, -bounds.Top + 5);
            using var renderer = new Ink.Rendering.InkSceneRenderer { ShowSelectionOutline = false };
            await renderer.Images.PrepareAsync(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), controller);
            using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(),
                Math.Clamp(MathF.Ceiling(bounds.Width + 10), 1, 8192), Math.Clamp(MathF.Ceiling(bounds.Height + 10), 1, 8192), 96);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                renderer.DrawCommitted(drawing, controller);
            }
            await target.SaveAsync(file.Path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"export selection: {ex}", Core.Diagnostics.LogType.Error);
            await NotifyAsync("存为图片", $"存为图片失败：{ex.Message}");
        }
    }

    private async Task SaveStrokesAsync()
    {
        if (DocumentActive) { await _documentWorkspace.SaveAsync(); return; }
        try
        {
            var target = _board is not null ? Services.Board : Services.ScreenImages;
            var owner = (Window)_board ?? _bar;
            bool entireBoard = _board is not null;
            string name = Core.Settings.ArchivePolicy.FileName(Services.Settings.Archive, DateTimeOffset.Now, entireBoard ? target.PageCount : 1, entireBoard ? "整板" : "批注");
            var library = Services.Modules.GetExport<IBoardLibraryService>();
            string path = library is not null ? await library.PickSaveAsync(name) : await ImageFilePicker.SaveArchiveAsync(WindowChrome.GetWorkArea(owner),
                Core.Settings.ArchivePolicy.ResolveDirectory(Services.Settings.Archive.DefaultDirectory), name);
            if (path is null || _disposed) return;
            await Services.Archives.SaveManualAsync(target, path, entireBoard);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"save strokes: {ex}", Core.Diagnostics.LogType.Error);
            if (!_disposed) await NotifyAsync("存档保存失败", ex.GetBaseException().Message);
        }
    }

    /// <summary>Opens the available document browser or the archive file picker.</summary>
    public void OpenStrokes() => _ = OpenStrokesAsync();

    private async Task OpenStrokesAsync()
    {
        try
        {
            if (Services.Modules.GetExport<IBoardLibraryService>() is { } library)
            {
                library.Open();
                return;
            }
            var owner = (Window)_board ?? _bar;
            var store = _board is not null ? Services.Board : Services.ScreenImages;
            var extensions = new[] { ".icink" }.Concat(Services.Plugins.ImportExtensions).ToList();
            if (Services.Modules.HasExport<IPdfWorkspaceService>()) extensions.Add(".pdf");
            string path = await ImageFilePicker.OpenArchiveAsync(WindowChrome.GetWorkArea(owner),
                Core.Settings.ArchivePolicy.ResolveDirectory(Services.Settings.Archive.DefaultDirectory), extensions.Distinct().ToArray());
            if (path is null || _disposed) return;
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) { await OpenPdfAsync(path); return; }
            var page = store.Current;
            bool entireBoard = ReferenceEquals(store, Services.Board);
            var apply = await Services.PrepareInkOpenAsync(path, store, entireBoard);
            if (_disposed || owner != ((Window)_board ?? _bar) || page != store.Current) return;
            if (entireBoard ? store.HasContent : store.Current.Document.Count > 0 || store.CurrentImages.Count > 0 || store.CurrentMedia.Count > 0)
            {
                if (!await YesOrNoWindow.AskAsync(owner, "打开将替换当前墨迹、图片和视频，是否继续？")) return;
            }
            if (_disposed || owner != ((Window)_board ?? _bar) || page != store.Current) return;
            apply();
            _bar?.Bar.RefreshSelection();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"open strokes: {ex}", Core.Diagnostics.LogType.Error);
            if (!_disposed) await NotifyAsync("文件打开失败", ex.GetBaseException().Message);
        }
    }

    public void OpenReplay()
    {
        if (_replay is not null) { _replay.Activate(); return; }
        // Replay whichever surface the user is looking at: the board page when open, else the overlay.
        var controller = ActiveController;
        if (!controller.BeginPlayback()) return;
        bool restoreOverlay = _board is null && !DocumentActive && !Overlay.Presenter.InkVisible;
        if (_board is null && !DocumentActive) Overlay.Presenter.InkVisible = true;
        _replay = new ReplayWindow(controller, EffectiveTheme,
            _board is not null ? Services.Settings.Appearance.BlackboardScale : Services.Settings.Appearance.FloatingBarScale,
            _documentWorkspace?.Window ?? (Window)_board ?? _bar);
        NotifyWindowOpened(_replay);
        _replay.Closed += (_, _) => { _replay = null; if (restoreOverlay) Overlay.Presenter.InkVisible = false; };
        _replay.Activate();
        WindowChrome.EnsureAbove(_replay);
    }

    public void OpenTimeMachine()
    {
        if (Services.Modules.Invoke<ITimeMachineService>(service => _ = service.OpenRecordingsAsync())) return;
        OpenLocalTimeMachine();
    }

    /// <summary>
    /// 时光机模块不可用（未安装/未启用/调用异常）时的本地回退：仍保留的
    /// <see cref="TimeMachineWindow"/> 基于当前课堂录制的 <see cref="ClassroomSession"/> 回放；
    /// 没有可回放会话时给出明确提示，避免入口点击后无反馈。
    /// </summary>
    private void OpenLocalTimeMachine()
    {
        if (_timeMachine is not null) { _timeMachine.Activate(); return; }
        var session = _recorder?.Session;
        if (session is null)
        {
            _ = NotifyAsync("板书时光机",
                "时光机模块未安装或未启用，且当前没有可回放的课堂录制。请先在「设置 → 模块」启用时光机，或进入画板开始一次课堂录制。");
            return;
        }
        try
        {
            var window = new TimeMachineWindow(session, Services.Board, Services.Settings.Appearance.Theme);
            window.Closed += (_, _) => _timeMachine = null;
            _timeMachine = window;
            window.Activate();
        }
        catch (Exception ex)
        {
            _ = NotifyAsync("板书时光机", "打开本地时光机失败：" + ex.GetBaseException().Message);
        }
    }

    public void OpenGuide()
    {
        if (_guide is not null) { _guide.Activate(); return; }
        _guide = InfoWindow.OpenGuide(Services.Settings.Appearance.Theme);
        _guide.Closed += (_, _) => _guide = null;
        _guide.Activate();
    }

    public void OpenPenPalette(bool highlighter)
    {
        if (_palette is not null) { _palette.Close(); _palette = null; }
        _paletteHighlighter = highlighter;
        var controller = _board is not null ? Services.Board.Current : Services.OverlayInk;
        _palette = new PenPaletteWindow(controller, highlighter, Services.Settings.Appearance.Theme)
        {
            HighlighterAlpha = Services.Settings.Canvas.InkAlpha,
            LightColorScheme = Services.Settings.Appearance.Theme == 1,
            InkWidthChanged = width =>
            {
                if (highlighter) Services.Settings.Canvas.HighlighterWidth = width;
                else if (controller.CurrentAttributes.TaperKind == Core.Ink.TaperKind.InkWash) Services.Settings.Canvas.InkWashWidth = width;
                else Services.Settings.Canvas.InkWidth = width;
                Services.ApplySettings();
                Services.SaveSettings();
            },
            InkStyleChanged = style =>
            {
                Services.Settings.Canvas.InkStyle = style;
                Services.ApplySettings();
                Services.SaveSettings();
            },
            InkAlphaChanged = alpha =>
            {
                Services.Settings.Canvas.InkAlpha = alpha;
                Services.ApplySettings();
                Services.SaveSettings();
            },
            AttributesChanged = () => _bar?.Bar.RefreshSelection(),
        };
        _palette.Closed += (_, _) => _palette = null;
        _palette.Activate();
        if (_bar is not null)
        {
            var tool = highlighter ? InkTool.Highlighter : InkTool.Pen;
            _palette.PlaceAboveBar(
                () => _bar.GetToolAnchor(tool),
                WindowChrome.GetHwnd(_bar),
                () => WindowChrome.GetScreenBounds(_bar, _bar.Bar));
        }
        WindowChrome.EnsureAbove(_palette);
    }

    public void OpenShapePicker()
    {
        if (_shapePicker is not null) { _shapePicker.Close(); _shapePicker = null; }
        _shapePicker = new ShapePickerWindow(ActiveController,
            () => _bar?.Bar.RefreshSelection(), Services.Settings.Appearance.Theme);
        _shapePicker.Closed += (_, _) => _shapePicker = null;
        _shapePicker.Activate();
        if (_bar is not null)
        {
            _shapePicker.PlaceAboveBar(
                () => _bar.GetToolAnchor(InkTool.Shape),
                WindowChrome.GetHwnd(_bar),
                () => WindowChrome.GetScreenBounds(_bar, _bar.Bar));
        }
        WindowChrome.EnsureAbove(_shapePicker);
    }

    /// <summary>云端截图取景：有文档/画板窗口时截该窗口，否则截虚拟桌面。</summary>
    internal ScreenRect GetScreenshotBounds()
    {
        var workspace = _documentWorkspace?.Window ?? (Window)_board;
        return workspace is not null ? ScreenService.FromWindow(WindowChrome.GetHwnd(workspace)).Bounds : ScreenService.Virtual;
    }

    public async Task ScreenshotAsync() => await CaptureScreenshotToFileAsync();

    /// <summary>整屏截图并保存；返回文件路径，失败返回 null。</summary>
    private async Task<string> CaptureScreenshotToFileAsync()
    {
        try
        {
            var screen = GetScreenshotBounds();
            string dir = Path.Combine(Services.Settings.Automation.AutoSavedStrokesLocation, "Screenshots");
            string path = await ScreenshotService.CaptureToFileAsync(dir, screen.X, screen.Y, screen.Width, screen.Height,
                Services.Settings.Automation.IsSaveScreenshotsInDateFolders);

            if (Services.Settings.Automation.IsAutoSaveStrokesAtScreenshot)
                await Services.AutoSave.SaveSnapshotAsync(ActiveController.Document.Strokes, "screenshot");
            return path;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"Screenshot: {ex.Message}", Core.Diagnostics.LogType.Error);
            return null;
        }
    }

    public bool ClearCanvas()
    {
        var controller = ActiveController;
        if (Services.Settings.Archive.ScreenCapture.SaveOnClear) _ = SaveScreenArchiveAsync("清屏前存档");
        var automation = Services.Settings.Automation;
        if (automation.IsAutoSaveStrokesAtClear && controller.Document.Count > automation.MinimumAutomationStrokeNumber)
        {
            _ = Services.AutoSave.SaveSnapshotAsync(controller.Document.Strokes, "clear");
            _ = ScreenshotAsync();
        }
        if (DocumentActive) controller.Clear();
        else (_board is not null ? Services.Board : Services.ScreenImages).ClearCurrentPage();
        return true;
    }

    public void ToggleFold()
    {
        if (_bar is null) return;
        bool fold = !_bar.IsFolded;
        _manualFoldOverride = fold;
        if (fold)
        {
            // WPF FoldFloatingBar_Click invokes CursorIcon_Click before hiding, in desktop and PPT modes.
            if (!Overlay.ClickThrough) ToggleClickThrough();
            _bar.SetFolded(true);
        }
        else
        {
            _bar.SetFolded(false);
        }
    }

    public void ToggleClickThrough()
    {
        if (DocumentActive) { ActiveController.Tool = ActiveController.Tool == InkTool.Pan ? InkTool.Pen : InkTool.Pan; return; }
        if (_board is not null)
        {
            _ = CloseBoardToMouseModeAsync();
            return;
        }
        bool enableMouseMode = !Overlay.ClickThrough;
        if (enableMouseMode)
        {
            OptionPopupCoordinator.DismissUnpinned();
            SaveBeforeMouseMode();
            bool showInk = _lastSlideShowState
                ? !Services.Settings.PowerPoint.HideStrokesAfterLeavingAnnotation
                : !Services.Settings.Canvas.HideStrokeWhenSelecting;
            Overlay.SetInteractionMode(clickThrough: true, inkVisible: showInk);
        }
        else
        {
            // CursorIcon_Click in the WPF source restores drawing without clearing strokes.
            Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
        }

        if (_bar is not null)
        {
            _bar.Bar.ClickThroughActive = Overlay.ClickThrough;
            _bar.Bar.RefreshSelection();
        }
    }

    private void SaveBeforeMouseMode()
    {
        if (Services.Settings.Archive.ScreenCapture.SaveOnMouseMode) _ = SaveScreenArchiveAsync("切换鼠标模式");
        var strokes = ActiveController.Document.Strokes;
        if (strokes.Count <= Services.Settings.Automation.MinimumAutomationStrokeNumber) return;
        if (_lastSlideShowState) _pptSession.RequestScreenshot();
        else _ = ScreenshotAsync();
        _ = Services.AutoSave.SaveSnapshotAsync(strokes, _lastSlideShowState ? "ppt-mouse-mode" : "mouse-mode");
    }

    /// <summary>Launch in pass-through mode so the overlay does not capture the desktop's input.</summary>
    public void StartInClickThrough()
    {
        if (Overlay.ClickThrough) return;
        ToggleClickThrough();
    }

    public void PptNext() => NavigatePpt(next: true);

    public void PptPrevious() => NavigatePpt(next: false);

    /// <summary>Diagnostics only: which navigation source the active show currently resolves to.</summary>
    internal PresentationSourceLevel PptNavigationSource => _pptSources.Active;

    /// <summary>Diagnostics only: tick of the last completed <see cref="NavigatePpt"/> call.</summary>
    internal long PptNavigationLastExitMs => _pptGuard.LastExitMs;

    private void NavigatePpt(bool next)
    {
        long now = Environment.TickCount64;
        if (!_pptGuard.TryEnter(now, out long retryAfterMs))
        {
            // Explicit flips that land inside the debounce window must not vanish: queue exactly
            // one retry for the remaining interval. Keyboard repeat and toolbar clicks converge
            // on the latest direction instead of turning into two page flips.
            if (retryAfterMs > 0) QueuePptNavigationRetry(next, retryAfterMs);
            return;
        }
        try
        {
            // The generation guards the screenshot request raised synchronously by the session.
            int generation = _pptGate.NextGeneration();
            _pptCaptureSession = _pptGate.Session;
            _pptCaptureGeneration = generation;

            if (_board is not null) ToggleBoard();
            if (_lastSlideShowState) _ = SaveScreenArchiveAsync("PPT 翻页前存档");
            bool moved = NavigateThroughSources(next);
            QueuePresentationRefresh();

            // The host receives its own simulated navigation back as a slide change; remember
            // that it was ours so the poll loop does not treat it as an external event.
            if (moved) _pptGuard.SuppressExternal(now);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"PPT navigation: {ex}", Core.Diagnostics.LogType.Error);
        }
        finally
        {
            _pptGuard.Exit(now);
        }
    }

    /// <summary>
    /// Delivers a flip that arrived inside <see cref="NavigationGuard"/>'s debounce window.
    /// Only the latest direction is kept and the timer fires once, so a rapid double click ends
    /// in a single additional flip instead of being swallowed.
    /// </summary>
    private void QueuePptNavigationRetry(bool next, long retryAfterMs)
    {
        _pptRetryNext = next;
        if (_pptRetryQueued) return;
        _pptRetryQueued = true;
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[PPT] 导航请求落在 {retryAfterMs}ms 节流窗口内，排队补发 {(next ? "Next" : "Previous")}",
            Core.Diagnostics.LogType.Info);
        _pptRetryTimer.Stop();
        _pptRetryTimer.Interval = TimeSpan.FromMilliseconds(retryAfterMs + 10);
        _pptRetryTimer.Start();
    }

    /// <summary>
    /// COM → full-screen window → raw keys. Outside a slide show only COM is attempted, which
    /// preserves the previous behaviour and avoids injecting Page Down into unrelated apps.
    /// Each failure is recorded once per show via <see cref="PresentationSourceSelector"/>.
    /// </summary>
    private bool NavigateThroughSources(bool next)
    {
        PresentationSourceLevel level = _pptSources.Active;
        while (level != PresentationSourceLevel.None)
        {
            bool moved;
            switch (level)
            {
                case PresentationSourceLevel.Com:
                    moved = next ? _pptComSource.Next() : _pptComSource.Previous();
                    if (moved) return true;
                    if (!_lastSlideShowState) return false;
                    DegradePptSource(level);
                    break;
                case PresentationSourceLevel.Window:
                    if (!_pptWindowSource.IsAvailable)
                    {
                        DegradePptSource(level);
                        break;
                    }
                    moved = next ? _pptWindowSource.Next() : _pptWindowSource.Previous();
                    if (moved) return true;
                    DegradePptSource(level);
                    break;
                case PresentationSourceLevel.Keys:
                    moved = next ? _pptKeySource.Next() : _pptKeySource.Previous();
                    if (moved) return true;
                    DegradePptSource(level);
                    return false;
                default:
                    return false;
            }
            level = _pptSources.Active;
        }
        return false;
    }

    private void DegradePptSource(PresentationSourceLevel failed)
    {
        PresentationSourceLevel previous = _pptSources.Active;
        _pptSources.ReportFailure(failed);
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"PPT 翻页降级：{previous} → {_pptSources.Active}（{_pptSources.LastReason}）",
            Core.Diagnostics.LogType.Info);
    }

    private void HandlePresentationKey(int virtualKey)
    {
        if (!_lastSlideShowState) return;
        switch (PresentationKeyMap.Map(virtualKey))
        {
            case PresentationKeyAction.Next:
                PptNext();
                break;
            case PresentationKeyAction.Previous:
                PptPrevious();
                break;
            case PresentationKeyAction.ToggleBlackScreen:
                ToggleBlackScreen();
                break;
        }
    }

    public void ToggleBlackScreen()
    {
        if (_blackScreen is not null)
        {
            CloseBlackScreen();
            return;
        }
        _blackScreen = new BlackScreenWindow();
        _blackScreen.Closed += (_, _) =>
        {
            _blackScreen = null;
            ReleaseBlackScreenEscape();
        };
        _blackScreen.Activate();
        // The window can show without owning focus, so cover that case with a global Esc that
        // only exists while the black screen is up. The window also handles Esc itself.
        TryRegisterBlackScreenEscape();
        Core.Diagnostics.FileLog.WriteLogToFile("黑屏已开启（F9/B 或点击/Esc 恢复）", Core.Diagnostics.LogType.Info);
    }

    /// <summary>
    /// Escape is reserved only while the black screen is visible. Registration fails when the
    /// key is already taken (e.g. Esc exits a running slideshow), in which case the window's own
    /// KeyDown still closes it.
    /// </summary>
    private void TryRegisterBlackScreenEscape()
    {
        if (_blackScreen is null || _blackScreenEscapeHotkey) return;
        if (Overlay.Hotkeys.Register(HotkeyModifiers.None, 0x1B /* Escape */, ToggleBlackScreen))
        {
            _blackScreenEscapeHotkey = true;
            _blackScreenEscapeFailureLogged = false;
            return;
        }
        if (_blackScreenEscapeFailureLogged) return;
        _blackScreenEscapeFailureLogged = true;
        Core.Diagnostics.FileLog.WriteLogToFile(
            "黑屏 Esc 全局热键注册失败，改用窗口内按键关闭", Core.Diagnostics.LogType.Info);
    }

    private void ReleaseBlackScreenEscape()
    {
        if (!_blackScreenEscapeHotkey) return;
        _blackScreenEscapeHotkey = false;
        // HotkeyService can only drop the whole group: rebuild it so Esc is not held after close.
        Overlay.Hotkeys.UnregisterAll();
        RegisterOverlayHotkeys();
    }

    private void CloseBlackScreen()
    {
        BlackScreenWindow window = _blackScreen;
        if (window is null) return;
        _blackScreen = null;
        try { window.Close(); }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"关闭黑屏失败：{ex}", Core.Diagnostics.LogType.Error);
        }
        finally { ReleaseBlackScreenEscape(); }
    }

    public void RotatePptNavigationCorner()
    {
        // Kept for shell action compatibility. All enabled corners are displayed together.
        UpdatePptNavigation(_pptShowSnapshot ?? PptSnapshot.Detached);
    }

    public void CollapsePptNavigation()
    {
        _pptNavCollapsed = !_pptNavCollapsed;
        var snapshot = _pptShowSnapshot;
        UpdatePptNavigation(snapshot ?? PptSnapshot.Detached);
    }

    public void TogglePptNavigationWindow()
    {
        if (_manualPptNav)
        {
            _manualPptNav = false;
            ClosePptNavigationWindows();
            _pptNavCollapsed = false;
            return;
        }

        _manualPptNav = true;
        _pptNavCollapsed = false;
        UpdatePptNavigation(_pptSession.Snapshot ?? PptSnapshot.Detached);
    }

    // ---- 课件页导航（批注/收藏索引） ------------------------------------

    public void ToggleSlideNavigator()
    {
        _slideNav ??= CreateSlideNavigator();
        _slideNav.Toggle();
        if (_slideNav.IsVisible) RefreshSlideIndex(_pptSession.Snapshot);
    }

    public void PptGotoSlide(int oneBasedIndex)
    {
        if (oneBasedIndex <= 0) return;
        if (_pptSession.GoToSlide(oneBasedIndex)) return;
        _ = NotifyAsync("无法跳页", "当前放映源不支持直接跳页，请用上一页/下一页；PowerPoint 放映时可正常跳转。");
    }

    private SlideNavigatorWindow CreateSlideNavigator()
    {
        var window = new SlideNavigatorWindow(Services.Settings.Appearance.Theme);
        window.SlideActivated += index => PptGotoSlide(index);
        window.FavoriteToggled += OnSlideFavoriteToggled;
        window.AiReviewRequested += OnSlideAiReviewRequested;
        return window;
    }

    private void OnSlideFavoriteToggled(int slideId)
    {
        if (_slideIndex is null || slideId <= 0) return;
        if (!_slideIndex.ToggleFavorite(slideId)) return;

        SaveSlideIndex();
        _slideNav?.Update(_slideIndex, _pptSession.Snapshot?.Slide?.SlideId ?? 0);
    }

    /// <summary>翻到有 AI 讲解的页时，从页索引取出已保存讲解并重新显示在浮窗里。</summary>
    private void OnSlideAiReviewRequested(int slideId)
    {
        SlideContentEntry entry = _slideIndex?.Find(slideId);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Text)) return;

        _aiStream?.Abort();
        _aiStream = null;
        _aiTarget = null; // 课件回看保持原有“贴回主展板”行为，不参与板中板路由
        _aiGate.Invalidate();
        _aiSlideId = slideId;
        _aiSlideIndex = entry.Index;
        _aiThumb = "";

        if (_aiAssistant is null)
        {
            _aiProblem = null;
            PrepareAssistantWindow();
        }

        AssistantSession review = AssistantMachine.CreateSession(
            AssistantMode.Chat, "review-" + slideId + "-" + entry.AnimationStep);
        review.State = AssistantState.Done;
        review.TeachingMode = AssistantTeachingMode.Full;
        review.RevealedSteps = 0;
        review.Text = entry.Text;
        _aiSession = review;

        _aiAssistant?.Apply(review, "");
        _aiAssistant?.ShowNotice("已调出第 " + entry.Index + " 页保存的 AI 讲解");
    }

    /// <summary>Keep the current slide current, reading a full inventory only while the legacy index is visible.</summary>
    private void RefreshSlideIndex(PptSnapshot snapshot)
    {
        if (snapshot?.IsSlideShowRunning != true || snapshot.Presentation is null) return;

        if (_slideIndex?.Identity != snapshot.Presentation.Identity)
        {
            _slideIndex = LoadSlideIndexSidecar(snapshot.Presentation) ?? new SlideContentIndex();
            _slideIndex.Identity = snapshot.Presentation.Identity;
            _slideIndex.Name = snapshot.Presentation.Name;
        }
        int currentSlideId = snapshot.Slide?.SlideId ?? 0;
        int animationStep = Services.Ppt.GetAnimationStep();
        _teachingSession?.SetCurrentSlide(currentSlideId, snapshot.Slide?.Index ?? 0, animationStep, Environment.TickCount64);
        if (snapshot.Slide is { } activeSlide)
        {
            var active = _slideIndex.Find(activeSlide.SlideId);
            if (active is null)
            {
                active = new SlideContentEntry { SlideId = activeSlide.SlideId, Index = activeSlide.Index, Name = activeSlide.Name };
                _slideIndex.Slides.Add(active);
            }
            active.Index = activeSlide.Index;
            active.HasInk = Services.OverlayInk.Document.Count > 0;
            active.InkStrokeCount = Services.OverlayInk.Document.Count;
            active.AnimationStep = animationStep;
        }
        if (_slideNav?.IsVisible != true) return;

        DateTime now = DateTime.UtcNow;
        if (now - _lastSlideIndexRefreshUtc < TimeSpan.FromSeconds(2)) return;
        _lastSlideIndexRefreshUtc = now;

        try
        {
            PptPresentationDetails details = Services.Ppt.GetPresentationDetails();
            if (details?.Presentation is null) return;

            SlideContentIndex sidecar = LoadSlideIndexSidecar(details.Presentation);
            SlideContentIndex previous = sidecar ?? _slideIndex;
            _slideIndex = SlideContentIndex.Build(
                details, _pptSession.CreateIndexArchive(), previous,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string directory = _pptStore.GetPresentationDirectory(details.Presentation);
            foreach (SlideContentEntry entry in _slideIndex.Slides)
            {
                if (entry.SlideId <= 0 || !string.IsNullOrEmpty(entry.ThumbnailPath)) continue;
                string thumb = Path.Combine(directory, "thumb-" + entry.SlideId + ".png");
                if (File.Exists(thumb)) entry.ThumbnailPath = thumb;
            }

            SlideContentEntry current = _slideIndex.Find(currentSlideId);
            if (current is not null && string.IsNullOrEmpty(current.Text))
            {
                string text = SafeSlideText(currentSlideId);
                if (!string.IsNullOrWhiteSpace(text)) current.Text = text;
            }

            if (current is not null) current.AnimationStep = animationStep;

            _slideNav?.Update(_slideIndex, currentSlideId);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件页索引刷新失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    private string SlideIndexPath(PptPresentationInfo presentation)
        => Path.Combine(_pptStore.GetPresentationDirectory(presentation), "SlideIndex.json");

    private SlideContentIndex LoadSlideIndexSidecar(PptPresentationInfo presentation)
    {
        try
        {
            string path = SlideIndexPath(presentation);
            return File.Exists(path) ? SlideContentIndex.Deserialize(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>无放映时读取最近一次课件的 SlideIndex.json，并把缺失缩略图按同目录 thumb-{slideId}.png 回填。</summary>
    private SlideContentIndex LoadLatestSlideIndexSidecar()
    {
        try
        {
            string root = Path.Combine(
                Services.Settings.Automation.AutoSavedStrokesLocation,
                "Auto Saved - Presentations");
            if (!Directory.Exists(root)) return null;

            var files = new DirectoryInfo(root)
                .GetFiles("SlideIndex.json", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(3)
                .ToList();

            foreach (FileInfo file in files)
            {
                SlideContentIndex index = SlideContentIndex.Deserialize(File.ReadAllText(file.FullName));
                if (index is null || index.Count == 0) continue;

                string directory = file.DirectoryName ?? root;
                foreach (SlideContentEntry entry in index.Slides)
                {
                    if (!string.IsNullOrEmpty(entry.ThumbnailPath) && File.Exists(entry.ThumbnailPath))
                        continue;

                    string thumb = Path.Combine(directory, "thumb-" + entry.SlideId + ".png");
                    if (File.Exists(thumb)) entry.ThumbnailPath = thumb;
                }

                return index;
            }

            return null;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"读取最近课件索引失败：{ex.Message}", Core.Diagnostics.LogType.Info);
            return null;
        }
    }

    private void SaveSlideIndex()
    {
        PptPresentationInfo presentation = _pptSession.Snapshot?.Presentation;
        if (_slideIndex is null || presentation is null) return;

        try
        {
            string path = SlideIndexPath(presentation);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _slideIndex.Serialize());
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件页索引保存失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>把当页截图另存为固定缩略图（thumb-{slideId}.png），供课件页导航复用。</summary>
    private void SaveSlideNavigatorThumbnail(
        PptPresentationInfo presentation, int slideId, byte[] pixels, int width, int height)
    {
        if (presentation is null || slideId <= 0) return;

        try
        {
            string directory = _pptStore.GetPresentationDirectory(presentation);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "thumb-" + slideId + ".png");
            if (!File.Exists(path)) _ = ScreenshotService.SavePngAsync(path, pixels, width, height);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件页缩略图保存失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    private void OnPptInkTransition(PptInkTransition transition)
    {
        if (transition is null) return;

        // A slide change invalidates any selection made on the previous slide.
        if (transition.Kind != PptInkTransitionKind.Slide) return;
        Services.OverlayInk.Selection.Clear();
        _bar?.Bar.RefreshSelection();
    }

    private void RememberPptScreenshot(string path, int slideIndex, int slideId)
    {
        _pptScreenshotPath = path;
        _pptScreenshotSlideIndex = slideIndex;
        _pptScreenshotSlideId = slideId;
    }

    private static ScreenRect ResolveSlideShowBounds(PptInkTransition transition)
    {
        nint hwnd = transition.Current?.WindowHandle ?? 0;
        if (hwnd != 0 && ShowWindowTracker.TryGet(hwnd, out ShowWindowInfo info)
            && info.IsFullScreen && info.Width > 0 && info.Height > 0)
        {
            return new ScreenRect(info.X, info.Y, info.Width, info.Height);
        }
        return ScreenService.Primary;
    }

    /// <summary>
    /// 向录制中的课堂时间轴追加一个非墨迹事件；未录制时静默忽略，异常只记录日志。
    /// </summary>
    private void RecordClassroomEvent(TimelineEventKind kind, string payload = null, int? pageIndex = null)
    {
        var recorder = _recorder;
        if (recorder is null || !recorder.IsRecording || recorder.Session is null) return;

        try
        {
            recorder.Append(new TimelineEvent
            {
                TimeMs = recorder.ElapsedMs,
                PageIndex = pageIndex ?? Services.Board.CurrentIndex,
                Kind = kind,
                Payload = payload,
            });
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"Timeline/{kind}: {ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    public void PptExit()
    {
        _pptSession.ExitSlideShow();
        QueuePresentationRefresh();
        // MW_PPT.cs:685 HideSubPanels("cursor") after View.Exit.
        _bar?.ClosePanels();
        _palette?.Close();
        _shapePicker?.Close();
    }

    public void PptOpenNavigation()
    {
        if (!_lastSlideShowState) return;
        var window = _pptNavigations.GetValueOrDefault(PptNavigationPlacement.BottomLeft)
            ?? _pptNavigations.Values.FirstOrDefault();
        window?.SetPreviewOpen(!window.PreviewOpen);
    }

    // ---- AI 助教 ---------------------------------------------------------

    /// <summary>板工具栏/选中态连续修改入口：复用预览编辑策略与选中 AI 组批量更新。</summary>
    public void ApplyAiBoardEdit(string instruction)
        => OnAiBoardPlanEditRequested(instruction);

    public void OpenAiAssistant()
    {
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] OpenAiAssistant 入口：board={_board is not null}，assistantExists={_aiAssistant is not null}",
            Core.Diagnostics.LogType.Info);
        try
        {
            OpenAiAssistantCore();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"AI 助教打开失败：{ex}", Core.Diagnostics.LogType.Error);
            try
            {
                ShowAssistantMessage("AI 助教打开失败，请稍后重试。");
            }
            catch (Exception inner)
            {
                Core.Diagnostics.FileLog.WriteLogToFile($"AI 助教错误提示失败：{inner}", Core.Diagnostics.LogType.Error);
                _ = NotifyAsync("AI 助教", "打开失败：" + ex.GetBaseException().Message);
            }
        }
    }

    private void OpenAiAssistantCore()
    {
        _aiTarget = CaptureAssistantTarget();
        _aiContext = CaptureClassroomContext();
        SyncRecorderContext();
        var controller = ActiveController;
        IReadOnlyList<InkStroke> sourceStrokes = controller.Selection;
        var bounds = controller.SelectionBounds;
        string source = "selection";
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] Core 入口：controller={controller?.GetType().Name ?? "null"}，"
            + $"selection={sourceStrokes.Count}，"
            + $"bounds=({bounds.Left:0.0},{bounds.Top:0.0},{bounds.Width:0.0}x{bounds.Height:0.0})，source={source}"
            + $"，target={AssistantTargetPolicy.Describe(_aiTarget)}",
            Core.Diagnostics.LogType.Info);

        if (sourceStrokes.Count == 0 || bounds.Width < 16 || bounds.Height < 16)
        {
            source = "canvas";
            // Friendly fallback: the selection tool is tap-only today. When nothing is
            // selected we recognize every stroke on the canvas; when the canvas is empty
            // we recognize the whole overlay viewport (screenshot-only chat).
            sourceStrokes = controller.Document.Strokes;
            if (sourceStrokes.Count == 0)
            {
                sourceStrokes = Array.Empty<InkStroke>();
                var (targetWork, targetDpi, _, _) = AssistantTarget();
                double viewWidth = targetWork.Width / targetDpi;
                double viewHeight = targetWork.Height / targetDpi;
                Window canvasWindow = TargetCanvasWindow();
                if (canvasWindow is not null)
                {
                    ScreenRect client = WindowChrome.GetClientBounds(WindowChrome.GetHwnd(canvasWindow));
                    if (client.Width > 0 && client.Height > 0)
                    {
                        viewWidth = client.Width / targetDpi;
                        viewHeight = client.Height / targetDpi;
                    }
                }
                bounds = new Rect(0, 0, (float)viewWidth, (float)viewHeight);
            }
            else
            {
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (InkStroke stroke in sourceStrokes)
                {
                    foreach (var point in stroke.Points)
                    {
                        minX = MathF.Min(minX, point.X);
                        minY = MathF.Min(minY, point.Y);
                        maxX = MathF.Max(maxX, point.X);
                        maxY = MathF.Max(maxY, point.Y);
                    }
                }
                bounds = new Rect(minX - 8, minY - 8, maxX - minX + 16, maxY - minY + 16);
                if (bounds.Width < 16 || bounds.Height < 16)
                {
                    ShowAssistantMessage("画布上还没有可识别的内容，请先书写或用「选择」工具点选。");
                    return;
                }
            }
        }

        Matrix3x2 matrix = controller.ViewTransform;
        Vector2 topLeft = Vector2.Transform(new Vector2(bounds.Left, bounds.Top), matrix);
        Vector2 bottomRight = Vector2.Transform(new Vector2(bounds.Right, bounds.Bottom), matrix);

        var viewRect = new AssistantRect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
        _aiAnchor = viewRect;

        List<AssistantStroke> strokes = BuildAssistantStrokes(sourceStrokes, matrix);
        double scale = Math.Sqrt(Math.Abs(matrix.GetDeterminant()));
        var (_, dpi, originXDip, originYDip) = AssistantTarget();
        // controller.ViewTransform 给的是画布客户区 DIP，截图 API 要屏幕绝对物理像素：
        // 板窗口客户区原点不在 (0,0)，必须先加客户区原点再乘目标 DPI。
        int px = (int)Math.Round((viewRect.Left + originXDip) * dpi);
        int py = (int)Math.Round((viewRect.Top + originYDip) * dpi);
        int pw = (int)Math.Round(viewRect.Width * dpi);
        int ph = (int)Math.Round(viewRect.Height * dpi);

        _aiSlideId = _pptSession.Snapshot?.Slide?.SlideId ?? 0;
        _aiSlideIndex = _pptSession.Snapshot?.Slide?.Index ?? 0;

        _ = StartAssistantAsync(strokes, scale, px, py, pw, ph, source);
    }

    private void ShowAssistantMessage(string message)
    {
        try
        {
            _aiTarget = CaptureAssistantTarget();
            _aiContext = CaptureClassroomContext();
            SyncRecorderContext();
            _aiThumb = "";
            _aiAnchor = CenterAnchor();
            _aiSession = AssistantMachine.CreateSession(AssistantMode.Auto, "");
            AssistantMachine.Fail(_aiSession, message);
            PrepareAssistantWindow().Apply(_aiSession, _aiThumb);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"AI 助教提示浮窗失败：{ex}", Core.Diagnostics.LogType.Error);
            _ = NotifyAsync("AI 助教", message);
        }
    }

    private async Task StartAssistantAsync(
        List<AssistantStroke> strokes, double scale, int px, int py, int pw, int ph, string source)
    {
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 截图开始：px={px},py={py},pw={pw},ph={ph},source={source}",
            Core.Diagnostics.LogType.Info);
        string thumb;
        try
        {
            thumb = await AssistantCapture.CapturePngBase64Async(px, py, pw, ph);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 截图失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            thumb = "";
        }

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 截图完成：thumb.Length={thumb.Length}",
            Core.Diagnostics.LogType.Info);
        _aiThumb = thumb;

        if (!AiTargetCanvasAvailable())
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 截图期间目标画布已关闭，丢弃本次请求：target={AssistantTargetPolicy.Describe(_aiTarget)}",
                Core.Diagnostics.LogType.Info);
            return;
        }

        var problem = new ProblemContext
        {
            SlideId = _aiSlideId,
            SlideIndex = _aiSlideIndex,
            AnimationStep = _pptSession.Snapshot?.IsSlideShowRunning == true
                ? Services.Ppt.GetAnimationStep()
                : 0,
        };
        ProblemContextPolicy.Add(problem, new ProblemRegion
        {
            ImageBase64 = _aiThumb,
            Anchor = _aiAnchor,
            Source = source ?? "",
        });

        _aiProblem = problem;
        _aiStrokes = strokes;
        _aiScale = scale;

        try
        {
            PrepareAssistantWindow();
            StartAssistantStream(BuildProblemRequest(), followUp: false, question: "");
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 浮窗启动失败：{ex}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>
    /// 用当前拼题上下文构造请求。多选区时不发墨迹（先截图为准），并让外接矩形决定浮窗位置。
    /// </summary>
    private AssistantRequest BuildProblemRequest(AssistantTeachingMode? teachingOverride = null)
    {
        IReadOnlyList<ProblemRegion> ordered = ProblemContextPolicy.Ordered(_aiProblem);
        AssistantRect combined = ProblemContextPolicy.CombinedAnchor(_aiProblem);
        if (!combined.IsEmpty) _aiAnchor = combined;

        _aiThumb = ProblemContextPolicy.PrimaryImage(_aiProblem);
        bool multi = ordered.Count > 1;

        AssistantTeachingMode teaching = teachingOverride ?? AssistantTeachingPolicy.Parse(
            Services.Settings.Ai.TeachingMode);
        // 课堂/投屏默认不直接公布完整答案；教师仍可在浮窗内手动切到完整解答。
        if (teachingOverride is null && teaching == AssistantTeachingMode.Full && IsClassroomPresentation())
            teaching = AssistantTeachingMode.StepByStep;

        return new AssistantRequest
        {
            ImageBase64 = _aiThumb,
            Images = ordered,
            InkStrokes = multi ? Array.Empty<AssistantStroke>() : _aiStrokes,
            Viewport = new AssistantViewport { Scale = _aiScale, Rect = _aiAnchor },
            Mode = AssistantMode.Auto,
            Teaching = teaching,
            SessionId = _aiProblem?.SessionId ?? "",
            SlideContext = BuildSlideContext(),
        };
    }

    private bool IsClassroomPresentation()
        => _pptSession.Snapshot?.IsSlideShowRunning == true
           || string.Equals(_preset?.Id, "teach", StringComparison.Ordinal);

    /// <summary>
    /// 课件上下文只作辅助：标题、当前页文本、必要的前后页文本；COM 读取失败时返回 null，
    /// 退化为纯框选识别，不阻塞也不影响基础讲题。
    /// </summary>
    private SlideContext BuildSlideContext()
    {
        PptSnapshot snapshot = _pptSession.Snapshot;
        if (snapshot?.Presentation is null) return null;

        try
        {
            string currentText = SafeSlideText(_aiSlideId);
            string previousText = "";
            string nextText = "";
            PptPresentationDetails details = Services.Ppt.GetPresentationDetails();
            if (details?.Slides is { Count: > 0 })
            {
                int position = -1;
                for (int i = 0; i < details.Slides.Count; i++)
                {
                    if (details.Slides[i].SlideId != _aiSlideId) continue;
                    position = i;
                    break;
                }

                if (position > 0)
                    previousText = SafeSlideText(details.Slides[position - 1].SlideId);
                if (position >= 0 && position + 1 < details.Slides.Count)
                    nextText = SafeSlideText(details.Slides[position + 1].SlideId);
            }

            return SlideContextPolicy.Build(
                snapshot.Presentation.Name, _aiSlideId, _aiSlideIndex,
                currentText, previousText, nextText,
                _teachingSession?.CurrentAnimationStep ?? 0);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件上下文读取失败：{ex.Message}", Core.Diagnostics.LogType.Info);
            return null;
        }
    }

    private string SafeSlideText(int slideId)
    {
        if (slideId <= 0) return "";
        try
        {
            return Services.Ppt.GetSlideText(slideId) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>当前放映课件的显示名；未连接或读取失败时返回空。</summary>
    private string CurrentPresentationName()
    {
        try
        {
            return _pptSession.Snapshot?.Presentation?.Name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private AssistantWindow PrepareAssistantWindow()
    {
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] Prepare 入口：anchor=({_aiAnchor.Left:0.0},{_aiAnchor.Top:0.0})-({_aiAnchor.Right:0.0},{_aiAnchor.Bottom:0.0})，"
            + $"regions={_aiProblem?.Count ?? 0}，board={_board is not null}",
            Core.Diagnostics.LogType.Info);
        if (_aiAssistant is not null) _aiAssistant.Close();

        Core.Diagnostics.FileLog.WriteLogToFile("[AI] 即将构造 AssistantWindow", Core.Diagnostics.LogType.Info);
        var window = new AssistantWindow(Services.Settings.Appearance.Theme, Services.Settings.Features);
        _aiAssistant = window;
        _aiAssistantFromSideBoard = _aiTarget?.Kind == AssistantCanvasKind.SideBoard;
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] AssistantWindow 构造完成：hwnd=0x{WindowChrome.GetHwnd(window).ToInt64():X}",
            Core.Diagnostics.LogType.Info);
        window.StopRequested += OnAiStopRequested;
        window.RetryRequested += OnAiRetryRequested;
        window.CopyRequested += OnAiCopyRequested;
        window.PasteRequested += OnAiPasteRequested;
        window.FollowUpRequested += OnAiFollowUpRequested;
        window.BoardWriteRequested += OnAiBoardWriteRequested;
        window.BoardWriteConfirmedSelection += (ids, replace) => _ = ConfirmBoardWriteAsync(ids, replace);
        window.BoardPlanEditRequested += OnAiBoardPlanEditRequested;
        window.BoardWriteCancelled += OnAiBoardWriteCancelled;
        window.TeachingModeRequested += OnAiTeachingModeChanged;
        window.RevealNextRequested += OnAiRevealNext;
        window.VariantRequested += OnAiVariantRequested;
        window.AddRegionRequested += () => _ = AddAiRegionAsync();
        window.RemoveRegionRequested += OnAiRemoveRegion;
        window.ReRecognizeRegionRequested += OnAiReRecognizeRegion;
        window.MoveRegionRequested += OnAiMoveRegion;
        window.RegionSelected += OnAiRegionSelected;
        window.Closed += () => OnAiWindowClosed(window);
        window.SetAnchor(_aiAnchor);
        window.RegionSelectionEnabled =
            Services.Settings.Ai.EnableLocalCorrection && !(_aiProblem is { Count: > 1 });
        window.ApplyProblem(_aiProblem);
        ApplyFavoriteContext(window,
            CurrentPresentationName(),
            _aiSlideId > 0 ? _aiSlideId : _aiProblem?.SlideId ?? 0,
            AiFavoriteBoardPage());

        var (work, dpi, originXDip, originYDip) = AssistantTarget();
        var workDip = new AssistantRect(work.X / dpi, work.Y / dpi,
            (work.X + work.Width) / dpi, (work.Y + work.Height) / dpi);
        double workWidth = workDip.Width;
        double workHeight = workDip.Height;
        double cardWidth = AssistantWindow.CardWidth;
        double cardHeight = Math.Max(200, Math.Min(460, workHeight * AssistantWindow.MaxHeightRatio));

        AssistantRect anchor = _aiAnchor.IsEmpty ? CenterAnchor() : _aiAnchor;
        // 锚点是客户区 DIP；定位用窗口需要屏幕绝对 DIP 与目标工作区。
        AssistantRect screenAnchor = AssistantLayout.Offset(anchor, originXDip, originYDip);

        AssistantPlacement placement;
        if (anchor.Width > workWidth * 0.6 || anchor.Height > workHeight * 0.6)
        {
            // 整屏/大范围识别时锚点几乎铺满视口，跟随锚点会把浮窗顶到左上角；
            // 改为固定在目标工作区右下角，并按同一工作区做边界 clamp。
            placement = AssistantLayout.DockBottomRight(workDip, cardWidth, cardHeight);
        }
        else
        {
            placement = AssistantLayout.PlaceInWorkArea(screenAnchor, cardWidth, cardHeight, workDip);
        }

        // 目标画布窗口作为 owner：子板/主展板时浮窗随板走；overlay 模式不设 Win32 owner，
        // 避免此前把 WinUI 浮窗挂到独立 Win32 分层窗口后不可见。
        Window canvasWindow = TargetCanvasWindow();
        IntPtr owner = canvasWindow is not null ? WindowChrome.GetHwnd(canvasWindow) : IntPtr.Zero;
        IntPtr anchorHwnd = owner != IntPtr.Zero ? owner : Overlay.Window.Handle;
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] SetOwner 前：target={AssistantTargetPolicy.Describe(_aiTarget)}，"
            + $"owner=0x{owner.ToInt64():X}，currentOwner=0x{WindowChrome.GetOwner(window).ToInt64():X}",
            Core.Diagnostics.LogType.Info);
        WindowChrome.SetOwner(window, owner);
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] SetOwner 后：currentOwner=0x{WindowChrome.GetOwner(window).ToInt64():X}",
            Core.Diagnostics.LogType.Info);
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] ShowAt 前：placement=({placement.X:0.0},{placement.Y:0.0})，dpi={dpi:0.###}，workHeight={workHeight:0.0}",
            Core.Diagnostics.LogType.Info);
        window.ShowAt(placement, dpi, workHeight);
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] ShowAt 后：appWindowId={window.AppWindow.Id.Value}，isVisible={window.AppWindow.IsVisible}，"
            + $"position=({window.AppWindow.Position.X},{window.AppWindow.Position.Y})，"
            + $"size={window.AppWindow.Size.Width}x{window.AppWindow.Size.Height}",
            Core.Diagnostics.LogType.Info);
        WindowChrome.EnsureAbove(window, anchorHwnd);
        _aiHiddenWithSideBoard = false;
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] EnsureAbove 后：isVisible={window.AppWindow.IsVisible}，anchor=0x{anchorHwnd.ToInt64():X}",
            Core.Diagnostics.LogType.Info);
        ShowLassoAiHintOnce(window);
        return window;
    }

    /// <summary>
    /// 首次使用套索 / AI 识别时的一次性轻提示：提示内容走浮窗现有 notice 行，
    /// 约 2.6 秒自动消失；写入设置后不再出现，避免反复打扰。
    /// </summary>
    private void ShowLassoAiHintOnce(AssistantWindow window)
    {
        if (window is null || Services.Settings.Ai.HasSeenLassoHint) return;

        try
        {
            Services.Settings.Ai.HasSeenLassoHint = true;
            Services.SaveSettings();
        }
        catch
        {
            // 设置写入失败不应阻断 AI 功能；下次启动会再次提示一次。
        }

        window.ShowNotice("框选内容后，可使用 AI 识别公式和图形。");
    }

    private void StartAssistantStream(AssistantRequest request, bool followUp, string question)
    {
        _aiClient ??= new AiClient(AiConfig.ReadFromEnvironment());
        _aiStream?.Abort();
        _aiStream = null;

        long now = Environment.TickCount64;
        if (followUp)
        {
            _aiSession ??= AssistantMachine.CreateSession(AssistantMode.Chat, request.SessionId);
            AssistantMachine.BeginFollowUp(_aiSession, question, now);
            _aiSession.Mode = AssistantMode.Chat;
        }
        else
        {
            _aiSession = AssistantMachine.CreateSession(request.Mode, request.SessionId);
            if (request.Purpose == "board_write")
            {
                // 板书指令只驱动服务端生成，不追加到正文，避免与讲题/追问内容混排。
                AssistantMachine.BeginCommand(_aiSession, request.Instruction, now);
            }
            else
            {
                AssistantMachine.BeginCapture(_aiSession, now);
                AssistantMachine.BeginRecognize(_aiSession);
            }

            _aiSession.TeachingMode = request.Teaching;
            _aiSession.RevealedSteps = 0;
        }

        if (_aiTarget is not null) _aiTarget.SessionId = _aiSession?.SessionId ?? "";

        _aiAssistant?.Apply(_aiSession, _aiThumb);
        _aiAssistant?.SetBusy(true);

        // 翻页后复用同一浮窗时刷新收藏来源，避免把新页讲评记到旧 slideId。
        if (_aiAssistant is not null)
            ApplyFavoriteContext(_aiAssistant,
                CurrentPresentationName(),
                _aiSlideId > 0 ? _aiSlideId : _aiProblem?.SlideId ?? 0,
                AiFavoriteBoardPage());

        _aiTwoStage = request.Purpose == "board_write"
            ? false
            : Services.Settings.Ai.EnableTwoStage
              && (_aiProblem is null || _aiProblem.Count <= 1);
        _aiStageFirstToken = null;
        _aiStageReset = false;
        ResetAiPreStage();

        if (_aiTwoStage)
        {
            try
            {
                _aiSession.LocalHint = LocalFormulaTranscriber.Hint(request.InkStrokes, _aiSession);
                _aiAssistant?.Apply(_aiSession, _aiThumb);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"AI 本地提示失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            }
        }

        int generation = _aiGate.Begin(request.SessionId);

        if (UnifiedContextEnabled)
        {
            // 请求发起时补齐会话/请求/代数，后续显示与写回都以此快照为准。
            // 每次请求都按当前画板/页/课件重新采集，避免沿用上一轮残留的 PageId/RequestId 导致变式预览被误判过期。
            _aiContext = CaptureClassroomContext() with
            {
                SessionId = _aiSession?.SessionId ?? "",
                RequestId = "req-" + Guid.NewGuid().ToString("N"),
                Generation = generation,
                CreatedAtMs = Environment.TickCount64,
            };
            SyncRecorderContext();
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"{ClassroomContextPolicy.Describe(_aiContext)} 请求发起",
                Core.Diagnostics.LogType.Info);
        }

        try
        {
            if (_aiTwoStage)
            {
                AiStreamHandle handle = _aiClient.StartTwoStage(
                    request,
                    ev => _dispatcher.TryEnqueue(() =>
                    {
                        if (_aiGate.IsCurrent(generation)) OnAiEvent(ev);
                    }),
                    err => _dispatcher.TryEnqueue(() =>
                    {
                        if (_aiGate.IsCurrent(generation)) OnAiError(err);
                    }),
                    () => _dispatcher.TryEnqueue(() =>
                    {
                        if (_aiGate.IsCurrent(generation)) OnAiDone();
                    }));
                _aiStream = handle;
                handle.SecondStageStarted += () => _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_aiStream, handle) && _aiGate.IsCurrent(generation))
                        OnAiSecondStageStarted();
                });
            }
            else
            {
                _aiStream = _aiClient.Start(
                    request,
                    ev => _dispatcher.TryEnqueue(() =>
                    {
                        if (_aiGate.IsCurrent(generation)) OnAiEvent(ev);
                    }),
                    err => _dispatcher.TryEnqueue(() =>
                    {
                        if (_aiGate.IsCurrent(generation)) OnAiError(err);
                    }),
                    () => _dispatcher.TryEnqueue(() =>
                    {
                        if (_aiGate.IsCurrent(generation)) OnAiDone();
                    }));
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 请求启动失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            OnAiError("AI 请求启动失败");
        }
    }

    private void OnAiEvent(AssistantEvent ev)
    {
        if (_aiSession is null) return;
        if (!AiTargetStillCurrent(writeBack: false)) return;
        try
        {
            if (ev.Kind == AssistantEventKind.BoardPlan)
            {
                _ = PreviewBoardPlanAsync(ev);
                return;
            }

            if (_aiTwoStage) ApplyTwoStageEvent(ev);
            else AssistantMachine.ApplyEvent(_aiSession, ev, Environment.TickCount64);

            if (Services.Settings.Ai.EnableVerification && ev.Kind == AssistantEventKind.Formula)
                VerifySessionFormula(_aiSession);
            _aiAssistant?.Apply(_aiSession, _aiThumb);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 事件处理失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>
    /// 两阶段去重：InkOnly 只作首屏预览，WithImage 通常从头重放最终结果。
    /// 检测到首 token 重放时清空草稿，避免两份内容叠加。
    /// </summary>
    private void ApplyTwoStageEvent(AssistantEvent ev)
    {
        if (ev is null || _aiSession is null) return;

        if (ev.Kind == AssistantEventKind.Token)
        {
            bool replay = !_aiStageReset
                && _aiStageFirstToken is not null
                && _aiSession.Text.Length > 0
                && string.Equals(ev.Text, _aiStageFirstToken, StringComparison.Ordinal);

            if (replay)
            {
                _aiSession.Text = "";
                _aiSession.Formula = "";
                _aiSession.Steps.Clear();
                _aiSession.Annotations.Clear();
                _aiSession.TokenCount = 0;
                _aiSession.FirstTokenAtMs = 0;
                _aiSession.LocalHint = "";
                _aiStageReset = true;
            }

            _aiStageFirstToken ??= ev.Text ?? "";
        }

        AssistantMachine.ApplyEvent(_aiSession, ev, Environment.TickCount64);
    }

    /// <summary>
    /// WithImage 阶段启动前，把 InkOnly 阶段产生的草稿暂存并清空会话；
    /// 两段是同一份正文的重放/续写，完成后按语义合并去重。
    /// </summary>
    private void OnAiSecondStageStarted()
    {
        if (_aiSession is null || !_aiTwoStage) return;
        if (!AiTargetStillCurrent(writeBack: false)) return;

        _aiPreStageText = _aiSession.Text;
        _aiPreStageSteps = _aiSession.Steps
            .Select(step => CloneStep(step))
            .ToList();
        _aiPreStageFormula = _aiSession.Formula;
        _aiSecondStageStarted = true;

        _aiSession.Text = "";
        _aiSession.Steps.Clear();
        _aiSession.Formula = "";
        _aiSession.TokenCount = 0;
        _aiSession.State = AssistantState.Streaming;
        _aiAssistant?.Apply(_aiSession, _aiThumb, preserveScroll: true);
    }

    /// <summary>合并两阶段结果：新文本已包含旧文本时取较长的，否则拼接；步骤按内容去重。</summary>
    private void MergeAiPreStage()
    {
        if (!_aiSecondStageStarted || _aiSession is null) return;

        string newText = _aiSession.Text ?? "";
        string preText = _aiPreStageText ?? "";
        if (string.IsNullOrEmpty(newText))
        {
            _aiSession.Text = preText;
        }
        else if (!string.IsNullOrEmpty(preText))
        {
            if (newText.Contains(preText, StringComparison.Ordinal)
                || preText.Contains(newText, StringComparison.Ordinal))
            {
                _aiSession.Text = newText.Length >= preText.Length ? newText : preText;
            }
            else
            {
                _aiSession.Text = preText + newText;
            }
        }

        if (_aiPreStageSteps.Count > 0)
        {
            List<AssistantStep> current = _aiSession.Steps;
            if (current.Count == 0)
            {
                ReplaceSteps(current, _aiPreStageSteps);
            }
            else if (current.Count != _aiPreStageSteps.Count || !StepsEquivalent(current, _aiPreStageSteps))
            {
                var merged = new List<AssistantStep>(current.Count + _aiPreStageSteps.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (AssistantStep step in _aiPreStageSteps)
                {
                    if (seen.Add(StepKey(step))) merged.Add(CloneStep(step));
                }

                foreach (AssistantStep step in current)
                {
                    if (seen.Add(StepKey(step))) merged.Add(CloneStep(step));
                }

                ReplaceSteps(current, merged);
            }
        }

        if (string.IsNullOrEmpty(_aiSession.Formula))
            _aiSession.Formula = _aiPreStageFormula;

        ResetAiPreStage();
    }

    private void ResetAiPreStage()
    {
        _aiPreStageText = "";
        _aiPreStageSteps.Clear();
        _aiPreStageFormula = "";
        _aiSecondStageStarted = false;
    }

    private static AssistantStep CloneStep(AssistantStep step) => new()
    {
        Index = step.Index,
        Text = step.Text ?? "",
        Latex = step.Latex ?? "",
    };

    private static string StepKey(AssistantStep step) =>
        step.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "\u0001" + (step.Text ?? "")
        + "\u0001" + (step.Latex ?? "");

    private static bool StepsEquivalent(List<AssistantStep> left, List<AssistantStep> right)
    {
        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Text ?? "", right[i].Text ?? "", StringComparison.Ordinal)
                || !string.Equals(left[i].Latex ?? "", right[i].Latex ?? "", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void ReplaceSteps(List<AssistantStep> target, IReadOnlyList<AssistantStep> source)
    {
        target.Clear();
        foreach (AssistantStep step in source) target.Add(CloneStep(step));
    }

    private static void VerifySessionFormula(AssistantSession session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.Formula)) return;

        try
        {
            VerificationResult result = EquationVerifier.Verify(session.Formula, session.Confidence);
            session.Verified = result.Status == VerificationStatus.Verified;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 自检失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    private void OnAiError(string message)
    {
        // 失败即放弃待预览标记，避免错误之后的下一次正常完成误触发变式预览。
        if (_variantPending)
        {
            _variantPending = false;
            Core.Diagnostics.FileLog.WriteLogToFile(
                "[AI] 变式请求失败：已清除待预览标记", Core.Diagnostics.LogType.Info);
        }

        if (!AiTargetStillCurrent(writeBack: false)) { _aiAssistant?.SetBusy(false); return; }
        if (_aiSession is null) { _aiAssistant?.SetBusy(false); return; }
        ResetAiPreStage();
        AssistantMachine.Fail(_aiSession, message);
        _aiAssistant?.Apply(_aiSession, _aiThumb);
        _aiAssistant?.SetBusy(false);
        _aiStream = null;

        if (_aiBoardRequestPending)
        {
            _aiBoardRequestPending = false;
            _aiAssistant?.ShowNotice("板书生成失败，已保留指令，可重试。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书请求失败：intent={_aiBoardIntent}，instruction={_aiBoardInstruction}，error={message}",
                Core.Diagnostics.LogType.Error);
        }
    }

    private void OnAiDone()
    {
        if (!AiTargetStillCurrent(writeBack: false)) { _aiAssistant?.SetBusy(false); return; }
        if (_aiSession is null) { _aiAssistant?.SetBusy(false); return; }
        try
        {
            MergeAiPreStage();
            AssistantMachine.Finish(_aiSession);
            if (Services.Settings.Ai.EnableVerification) VerifySessionFormula(_aiSession);
            _aiSession.LocalHint = "";
            _aiAssistant?.Apply(_aiSession, _aiThumb);
            _aiAssistant?.SetPasted(false);
            _aiAssistant?.SetBusy(false);

            if (_aiBoardRequestPending)
            {
                _aiBoardRequestPending = false;
                if (_aiBoardPlan is null && _aiSession.BoardPlan is null)
                {
                    _aiAssistant?.ShowNotice("AI 未返回可写入的板书计划，已保留指令，可重试。");
                    Core.Diagnostics.FileLog.WriteLogToFile(
                        $"[AI] 板书计划缺失：intent={_aiBoardIntent}，instruction={_aiBoardInstruction}",
                        Core.Diagnostics.LogType.Info);
                }
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 完成处理失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            _aiAssistant?.SetBusy(false);
        }

        RecordAiExplanation();

        if (!string.IsNullOrEmpty(_reRecognizeRegionId))
        {
            string id = _reRecognizeRegionId;
            _reRecognizeRegionId = null;
            // 重新识别结果只更新会话内区域文本并再次求解，不写画布，选区差异不应丢弃结果。
            if (!AiTargetStillCurrent(continueSession: true))
            {
                _reRecognizeRegionId = null;
            }
            else if (_aiProblem is not null)
            {
                ProblemRegion region = _aiProblem.Regions.Find(item => item.Id == id);
                if (region is not null && !string.IsNullOrWhiteSpace(_aiSession?.Text))
                    region.Text = _aiSession.Text.Trim();
                _aiAssistant?.ApplyProblem(_aiProblem);
                if (!_aiProblem.IsEmpty)
                    StartAssistantStream(BuildProblemRequest(), followUp: false, question: "");
            }
        }

        _aiStream = null;

        if (_variantPending)
        {
            _variantPending = false;
            // 变式结果只进入预览对话框，不直接写画布，因此只要求会话可继续（不比较选区）。
            if (AiTargetStillCurrent(continueSession: true))
            {
                _ = PreviewVariantSafelyAsync(_aiSession);
            }
            else
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    "[AI] 变式预览被丢弃：会话目标已变化（画板/页/课件/代数），未弹出练习预览。"
                    + $"requested={ClassroomContextPolicy.Describe(_aiContext)}",
                    Core.Diagnostics.LogType.Info);
                // 具体原因提示已由 ClassroomContextStillCurrent 的 ContinueSessionNotice 弹出；此处不再重复 ShowNotice。
            }
        }

        FlushPendingExamplePrompt();
    }

    /// <summary>
    /// 例题「再讲一次」：标准识别流完成后自动提交预置追问，复用同一会话与浮窗，
    /// 不创建新的教学模式；未完成或已关闭时保持预填，等教师手动发送。
    /// </summary>
    private void FlushPendingExamplePrompt()
    {
        if (string.IsNullOrEmpty(_aiPendingExamplePrompt)) return;
        if (_aiSession?.State != AssistantState.Done || _aiAssistant is null) return;

        // 追问沿用原会话，不写画布，只要求会话可继续（选区变化不影响追问）。
        if (!AiTargetStillCurrent(continueSession: true))
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                "[AI] 再讲追问被丢弃：会话目标已变化，未自动提交追问。", Core.Diagnostics.LogType.Info);
            // 清空预填避免残留，提示由判定内部弹出。
            _aiPendingExamplePrompt = "";
            return;
        }

        string prompt = _aiPendingExamplePrompt;
        _aiPendingExamplePrompt = "";
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 再讲一次：自动提交追问，promptChars={prompt.Length}，session={_aiSession.SessionId}",
            Core.Diagnostics.LogType.Event);
        _aiAssistant.SubmitFollowUp(prompt);
    }

    /// <summary>把 AI 讲解按 slideId 归档到课件页索引，翻回该页可回查并导出讲义。</summary>
    private void RecordAiExplanation()
    {
        if (_aiSession is null || _aiSlideId <= 0) return;
        if (string.IsNullOrWhiteSpace(_aiSession.Text)) return;

        try
        {
            if (_slideIndex is not null)
            {
                _slideIndex.SetText(_aiSlideId, _aiSession.Text);
                _slideIndex.MarkAi(_aiSlideId);
                SaveSlideIndex();
                _slideNav?.Update(_slideIndex, _pptSession.Snapshot?.Slide?.SlideId ?? 0);
            }

            _teachingSession?.Link(
                "ai", _aiSession.SessionId, _aiSlideId, _aiSlideIndex,
                AiFavoriteBoardPage(), "", Environment.TickCount64);
            SaveTeachingSession();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 讲解归档失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>统一课堂会话落盘：课件、画板、AI、练习、工具结果与导出都关联到同一 ID。</summary>
    private void SaveTeachingSession()
    {
        if (_teachingSession is null || string.IsNullOrEmpty(_teachingSession.Id)) return;

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "InkCanvas", "teaching");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "session-" + _teachingSession.Id + ".json"),
                _teachingSession.Serialize());
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课堂会话保存失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    private void OnAiStopRequested()
    {
        // 先作废本代响应并停掉局部纠错流，避免停止后仍有迟到回调写回。
        _aiGate.Invalidate();
        _aiRegionStream?.Abort();
        _aiRegionStream = null;
        if (!AiTargetStillCurrent(writeBack: false)) return;
        _aiStream?.Abort();
        _aiStream = null;
        ResetAiPreStage();
        if (_aiSession is null) return;
        AssistantMachine.Finish(_aiSession);
        _aiAssistant?.Apply(_aiSession, _aiThumb);
        _aiAssistant?.SetBusy(false);
    }

    private void OnAiRetryRequested() => OpenAiAssistant();

    /// <summary>
    /// 切换讲题模式只调整展示裁剪，不重新识别或重发请求；已生成内容按新模式重排。
    /// </summary>
    private void OnAiTeachingModeChanged(AssistantTeachingMode mode)
    {
        if (_aiSession is null) return;
        // 只重排已生成内容的展示，不写画布。
        if (!AiTargetStillCurrent(continueSession: true)) return;

        AssistantMachine.SetTeachingMode(_aiSession, mode);
        Services.Settings.Ai.TeachingMode = AssistantTeachingPolicy.Wire(mode);
        Services.SaveSettings();
        _aiAssistant?.Apply(_aiSession, _aiThumb, preserveScroll: true);

        // 若当前会话由「只给提示」裁剪而来（没有步骤/公式），切到需要更多内容的模式时，
        // 复用同一识别结果重新生成，而不是重新截图或重新识别。
        bool lacksDetail = _aiSession.Steps.Count == 0
            && string.IsNullOrEmpty(_aiSession.Formula);
        if (mode != AssistantTeachingMode.Hint && lacksDetail && !string.IsNullOrEmpty(_aiThumb))
        {
            StartAssistantStream(BuildProblemRequest(mode), followUp: false, question: "");
        }
    }

    private void OnAiRevealNext()
    {
        if (_aiSession is null) return;
        if (!AssistantMachine.RevealNextStep(_aiSession)) return;
        _aiAssistant?.Apply(_aiSession, _aiThumb, preserveScroll: true);
    }

    /// <summary>基于当前题生成同知识点变式题；结果先预览编辑，不直接贴入。</summary>
    private void OnAiVariantRequested(bool advanced)
    {
        // 发起变式请求属于原会话上的继续追问，不写画布，不要求选区未变化。
        if (!AiTargetStillCurrent(continueSession: true)) return;
        if (_aiProblem is null || _aiProblem.IsEmpty)
        {
            _aiAssistant?.ShowNotice("先框选一道题，再生成变式题");
            return;
        }

        _variantDifficulty = advanced ? ExerciseDifficulty.Advanced : ExerciseDifficulty.Basic;
        StartVariantRequest();
    }

    /// <summary>
    /// 变式题请求的唯一构造点：AI 浮窗入口与练习结束浮窗共用，
    /// 只负责置位待预览标记并发出请求，不复制生成/贴回实现。
    /// </summary>
    private void StartVariantRequest()
    {
        if (_aiProblem is null) return;

        _variantPending = true;

        AssistantRequest request = ExerciseFlowPolicy.BuildRequest(
            _aiProblem,
            _variantDifficulty,
            _aiSession?.SessionId ?? _aiProblem.SessionId,
            AssistantTeachingMode.Full);
        request.SlideContext = BuildSlideContext();

        StartAssistantStream(request, followUp: false, question: "");
    }

    private async Task PreviewVariantSafelyAsync(AssistantSession session)
    {
        try
        {
            await PreviewVariantAsync(session);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 变式预览异常：{ex.GetType().Name}: {ex.Message}", Core.Diagnostics.LogType.Error);
            _aiAssistant?.ShowNotice("变式预览打开失败，已保留变式文本，可复制或重试。");
        }
    }

    private async Task PreviewVariantAsync(AssistantSession session)
    {
        if (session is null)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                "[AI] 变式预览跳过：会话为空", Core.Diagnostics.LogType.Info);
            return;
        }
        if (session.State != AssistantState.Done)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 变式预览跳过：会话状态 {session.State} 非 Done", Core.Diagnostics.LogType.Info);
            return;
        }

        VariantQuestionDraft draft = ExerciseFlowPolicy.Parse(
            session.Text, _variantDifficulty,
            sourceSessionId: session.SessionId,
            slideId: _aiSlideId,
            slideIndex: _aiSlideIndex,
            nowMs: Environment.TickCount64);

        if (!draft.IsValid)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 变式预览失败：AI 未返回可用变式题（difficulty={_variantDifficulty}，textChars={session.Text?.Length ?? 0}）",
                Core.Diagnostics.LogType.Info);
            await NotifyAsync("变式题", "AI 没有返回可用题目，请重试或补充框选。");
            return;
        }

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 变式预览弹出：difficulty={_variantDifficulty}，textChars={draft.Text?.Length ?? 0}，session={session.SessionId}",
            Core.Diagnostics.LogType.Event);

        var textBox = new TextBox
        {
            Text = draft.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            FontSize = 13,
        };
        var knowledgeBox = new TextBox
        {
            Text = ExerciseFlowPolicy.NormalizeKnowledgePoints(draft.KnowledgePoint),
            Height = 34,
            FontSize = 13,
            PlaceholderText = "多个知识点用逗号分隔",
        };
        var answerBox = new TextBox
        {
            Text = draft.Answer,
            Height = 34,
            FontSize = 13,
            PlaceholderText = "参考答案（可留空）",
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "预览并编辑后再贴入新画板页；原题不会被覆盖。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock { Text = "题目", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(textBox);
        content.Children.Add(new TextBlock { Text = "知识点", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(knowledgeBox);
        content.Children.Add(new TextBlock { Text = "参考答案", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(answerBox);

        var dialog = new ContentDialog
        {
            Title = (_variantDifficulty == ExerciseDifficulty.Advanced ? "提高变式" : "基础变式") + " · 随堂练习",
            Content = content,
            PrimaryButtonText = "开始练习",
            SecondaryButtonText = "仅贴到新页",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.None) return;

        draft.Text = textBox.Text?.Trim() ?? "";
        draft.KnowledgePoint = ExerciseFlowPolicy.NormalizeKnowledgePoints(knowledgeBox.Text);
        draft.Answer = answerBox.Text?.Trim() ?? "";
        if (!ExerciseFlowPolicy.CanStart(draft))
        {
            await NotifyAsync("随堂练习", "题目内容不能为空。");
            return;
        }

        await StartPracticeAsync(draft, startTimer: result == ContentDialogResult.Primary);
    }

    private async Task StartPracticeAsync(VariantQuestionDraft draft, bool startTimer)
    {
        if (_board is null) ToggleBoard();
        if (_board is null)
        {
            await NotifyAsync("随堂练习", "画板未能打开，请稍后重试。");
            return;
        }

        if (!Services.Board.InsertPageAfterCurrent())
        {
            await NotifyAsync("随堂练习", "无法新建画板页（可能已达页数上限）。");
            return;
        }

        int pageIndex = Services.Board.CurrentIndex;
        Services.Board.RenamePage(pageIndex, Services.Settings.Classroom.PracticePageName);

        Guid pageId = Services.Board.CurrentPageId;
        string practiceGroupId = Guid.NewGuid().ToString("N");
        string aiSessionId = _aiSession?.SessionId ?? draft.SourceSessionId ?? "";

        AssistantBoardPlan boardPlan = ExerciseFlowPolicy.BuildPracticeBoardPlan(
            draft, Services.Settings.Classroom.PracticePageName);
        BoardPlanNativeResult created = BoardPlanWriter.Create(
            boardPlan, "随堂练习", Environment.TickCount64);

        int objectCount = 0;
        if (created.Success)
        {
            ExerciseFlowPolicy.LayoutPracticeObjects(created.Texts);
            IReadOnlyList<Guid> objectIds = Services.Board.AddAiObjectsAtomic(
                created.Texts, created.Formulas, "随堂练习", pageId, practiceGroupId);
            objectCount = objectIds.Count;
        }
        else
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 随堂练习原生对象不可用：error={created.Error}，回退旧卡片",
                Core.Diagnostics.LogType.Info);
        }

        bool nativeWritten = objectCount > 0;
        if (!nativeWritten)
        {
            bool cardAdded = await AddPracticeCardPngAsync(draft);
            if (!cardAdded) return;
        }

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 随堂练习：page={pageIndex}，pageId={pageId}，texts={created.Texts.Count}，"
            + $"formulas={created.Formulas.Count}，objects={objectCount}，"
            + $"fallbackPng={!nativeWritten}，group={practiceGroupId}",
            Core.Diagnostics.LogType.Event);

        PracticePlan plan = ExerciseFlowPolicy.Plan(
            draft, pageIndex, Services.Settings.Classroom.CountdownMinutes,
            aiSessionId: aiSessionId, boardPageId: pageId, groupId: practiceGroupId);
        _activePractice = plan;

        ApplyPreset(ClassroomPresetLibrary.ForId("practice"));
        if (startTimer)
        {
            OpenTimer();
            _countdown?.StartCountdown(TimeSpan.FromMinutes(plan.CountdownMinutes));
        }

        string note = PracticeLinkNote.Build(aiSessionId, pageId, practiceGroupId);
        _teachingSession?.Link(
            "practice", draft.Id, _aiSlideId, _aiSlideIndex, pageIndex, note, Environment.TickCount64);
        if (_teachingSession is not null && !_teachingSession.ExerciseIds.Contains(draft.Id))
            _teachingSession.ExerciseIds.Add(draft.Id);
        SaveTeachingSession();

        string practiceContent = nativeWritten
            ? "已写入可编辑练习（题目/知识点/答案）到第 " + (pageIndex + 1) + " 页"
            : "已贴入第 " + (pageIndex + 1) + " 页";
        await NotifyAsync("随堂练习",
            practiceContent
            + (startTimer ? "，倒计时已开始（" + plan.CountdownMinutes + " 分钟）。" : "。"));
    }

    /// <summary>
    /// 旧卡片路径（原生对象写入不可用时的回退）：渲染「随堂练习」PNG 并按 DPI 居中贴入当前新页。
    /// 返回 false 时已向教师提示失败原因，调用方应停止后续倒计时与关联写入。
    /// </summary>
    private async Task<bool> AddPracticeCardPngAsync(VariantQuestionDraft draft)
    {
        IReadOnlyList<string> lines = ExerciseFlowPolicy.BuildPasteLines(draft);
        var (width, height) = PasteCardLayout.Estimate(lines);
        byte[] png = await PasteCardRenderer.RenderPngAsync("随堂练习", lines, width, height);
        if (png.Length == 0)
        {
            await NotifyAsync("随堂练习", "练习卡片渲染失败，已保留新页。");
            return false;
        }

        var work = ScreenService.WorkArea;
        double dpi = CurrentDpi() > 0 ? CurrentDpi() : 1.0;
        double left = Math.Clamp(
            (work.Width / dpi) / 2.0 - width / 2.0, 8, Math.Max(8, work.Width / dpi - width - 8));
        double top = Math.Clamp(
            (work.Height / dpi) / 2.0 - height / 2.0, 8, Math.Max(8, work.Height / dpi - height - 8));

        string metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.Steps,
            Text = draft.Text,
            Lines = new List<string>(lines),
        }.Serialize();

        bool added = Services.Board.AddImage(new BoardImageAsset
        {
            Name = "随堂练习",
            Data = png,
            ContentType = "image/png",
            Left = (float)left,
            Top = (float)top,
            Width = width,
            Height = height,
            Metadata = metadata,
        });

        if (!added)
        {
            await NotifyAsync("随堂练习", "练习卡片贴入失败，已保留空的新页。");
            return false;
        }

        return true;
    }


    /// <summary>把画布当前选区截图追加为拼题的又一个区域。</summary>
    private async Task AddAiRegionAsync()
    {
        if (_aiProblem is null || _aiAssistant is null) return;
        if (!AiTargetStillCurrent(writeBack: true)) return;

        InkController controller = _aiTarget?.Kind switch
        {
            AssistantCanvasKind.SideBoard => Services.SideBoard.Current,
            AssistantCanvasKind.MainBoard => Services.Board.Current,
            _ => ActiveController,
        };
        IReadOnlyList<InkStroke> selection = controller.Selection;
        Rect bounds = controller.SelectionBounds;
        if (selection.Count == 0 || bounds.Width < 16 || bounds.Height < 16)
        {
            _aiAssistant.ShowNotice("先在画布上用选择工具框选题干/图形/条件，再点「追加选区」");
            return;
        }

        var window = _aiAssistant;
        Matrix3x2 matrix = controller.ViewTransform;
        Vector2 topLeft = Vector2.Transform(new Vector2(bounds.Left, bounds.Top), matrix);
        Vector2 bottomRight = Vector2.Transform(new Vector2(bounds.Right, bounds.Bottom), matrix);
        var viewRect = new AssistantRect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));

        var (_, dpi, originXDip, originYDip) = AssistantTarget();
        string thumb = await AssistantCapture.CapturePngBase64Async(
            (int)Math.Round((viewRect.Left + originXDip) * dpi),
            (int)Math.Round((viewRect.Top + originYDip) * dpi),
            (int)Math.Round(viewRect.Width * dpi),
            (int)Math.Round(viewRect.Height * dpi));

        if (!ReferenceEquals(_aiAssistant, window) || _aiProblem is null) return;

        ProblemRegion added = ProblemContextPolicy.Add(_aiProblem, new ProblemRegion
        {
            ImageBase64 = thumb,
            Anchor = viewRect,
            Source = "selection",
        });
        if (added is null)
        {
            window.ShowNotice("该选区没有可识别内容");
            return;
        }

        _aiStrokes.AddRange(BuildAssistantStrokes(selection, matrix));
        AssistantRect combined = ProblemContextPolicy.CombinedAnchor(_aiProblem);
        if (!combined.IsEmpty)
        {
            _aiAnchor = combined;
            window.SetAnchor(_aiAnchor);
        }

        window.ApplyProblem(_aiProblem);
        window.ShowNotice("已追加第 " + added.Order + " 个选区，共 " + _aiProblem.Count + " 块");
        StartAssistantStream(BuildProblemRequest(), followUp: false, question: "");
    }

    private void OnAiRemoveRegion(string id)
    {
        if (_aiProblem is null || _aiAssistant is null) return;
        if (!ProblemContextPolicy.Remove(_aiProblem, id)) return;

        _aiAssistant.ApplyProblem(_aiProblem);
        if (_aiProblem.IsEmpty)
        {
            _aiAssistant.ShowNotice("已移除全部选区，可回画布重新框选");
            return;
        }

        StartAssistantStream(BuildProblemRequest(), followUp: false, question: "");
    }

    /// <summary>只重新识别一个选区：识别完成后写回该区文字，再携带完整拼题上下文重新求解。</summary>
    private void OnAiReRecognizeRegion(string id)
    {
        if (_aiProblem is null || _aiAssistant is null) return;
        // 重新识别只更新该区文本，不直接写画布。
        if (!AiTargetStillCurrent(continueSession: true)) return;

        ProblemRegion region = _aiProblem.Regions.Find(item => item.Id == id);
        if (region is null || string.IsNullOrEmpty(region.ImageBase64))
        {
            _aiAssistant.ShowNotice("该选区没有可重新识别的图片");
            return;
        }

        _reRecognizeRegionId = id;
        var request = new AssistantRequest
        {
            ImageBase64 = region.ImageBase64,
            Images = new[] { region },
            InkStrokes = Array.Empty<AssistantStroke>(),
            Viewport = new AssistantViewport { Scale = 1.0, Rect = region.Anchor },
            Mode = AssistantMode.Auto,
            SessionId = _aiProblem.SessionId,
            Teaching = AssistantTeachingMode.Full,
            Purpose = "region",
            SlideContext = BuildSlideContext(),
        };

        _aiAssistant.ShowNotice("正在重新识别第 " + region.Order + " 个选区…");
        StartAssistantStream(request, followUp: false, question: "");
    }

    private void OnAiMoveRegion(string id, int delta)
    {
        if (_aiProblem is null || _aiAssistant is null) return;
        if (!ProblemContextPolicy.Move(_aiProblem, id, delta)) return;

        _aiAssistant.ApplyProblem(_aiProblem);
        StartAssistantStream(BuildProblemRequest(), followUp: false, question: "");
    }

    private void OnAiCopyRequested()
    {
        if (_aiSession is null) return;
        string text = string.IsNullOrEmpty(_aiSession.Formula) ? _aiSession.Text : _aiSession.Formula;
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"AI 复制失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>
    /// 「整理板书 / AI 书写」入口：复用追问文本（为空由浮窗填默认指令），
    /// 以 board_write 用途单独发起一次请求；不写入会话正文，也不复用讲题渲染。
    /// </summary>
    private void OnAiBoardWriteRequested(string instruction, string intent)
    {
        if (_aiSession is null || _aiAssistant is null) return;
        // 板书请求本身不写画布（写入发生在 ConfirmBoardWriteAsync），只要求会话可继续。
        if (!AiTargetStillCurrent(continueSession: true)) return;

        string normalizedIntent = BoardWriteIntentPolicy.Normalize(intent);
        string instructionText = (instruction ?? "").Trim();
        if (instructionText.Length == 0)
            instructionText = BoardWriteIntentPolicy.DefaultInstruction(normalizedIntent);

        BoardService targetBoard = AiTargetBoard();
        bool requiresContent = BoardWriteIntentPolicy.RequiresExistingContent(normalizedIntent);
        if (targetBoard is null && requiresContent)
        {
            _aiAssistant.ShowNotice("目标画板不可用，无法整理板书。");
            return;
        }

        bool pageHasContent = targetBoard is not null && !targetBoard.GetContentBounds().IsEmpty;
        if (!BoardWriteIntentPolicy.CanRequest(normalizedIntent, pageHasContent))
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                "[AI] 整理板书取消：目标页没有可整理内容（GetContentBounds 为空）",
                Core.Diagnostics.LogType.Info);
            _aiAssistant.ShowNotice(BoardWriteIntentPolicy.EmptyPageNotice(normalizedIntent));
            return;
        }

        string requestInstruction = instructionText;
        if (requiresContent)
            requestInstruction += "\n请同时返回 originals（识别原文逐行）以及 confidence 与 warnings。";

        _aiBoardInstruction = instructionText;
        _aiBoardIntent = normalizedIntent;
        _aiBoardPlan = null;
        _aiBoardRequestPending = true;
        _aiAssistant.ClearBoardPlanPreview();

        int targetPage = _aiTarget?.PageIndex ?? targetBoard?.CurrentIndex ?? 0;
        var request = new AssistantRequest
        {
            ImageBase64 = BoardWriteIntentPolicy.ShouldAttachScreenshot(normalizedIntent, _aiThumb)
                ? _aiThumb ?? "" : "",
            InkStrokes = Array.Empty<AssistantStroke>(),
            Viewport = new AssistantViewport { Scale = 1.0, Rect = _aiAnchor },
            Mode = requiresContent ? AssistantMode.Auto : AssistantMode.Chat,
            SessionId = _aiSession.SessionId ?? "",
            Purpose = "board_write",
            Instruction = requestInstruction,
            TargetPageIndex = targetPage,
            SlideContext = BuildSlideContext(),
        };

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 板书请求：intent={normalizedIntent}，pageHasContent={pageHasContent}，"
            + $"instruction={instructionText}，target={AssistantTargetPolicy.Describe(_aiTarget)}，"
            + $"image={(request.ImageBase64.Length > 0 ? "yes" : "no")}",
            Core.Diagnostics.LogType.Info);
        StartAssistantStream(request, followUp: false, question: "");
    }

    private void OnAiBoardWriteCancelled()
    {
        _aiBoardRequestPending = false;
        _aiAssistant?.ShowNotice("已取消写入，指令已保留，可重新生成。");
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 板书预览取消：intent={_aiBoardIntent}，instruction={_aiBoardInstruction}",
            Core.Diagnostics.LogType.Info);
    }

    /// <summary>
    /// 阶段 5：本地应用“连续修改指令”（字号/列/过滤/移动/行距），
    /// 不调用 AI、不重新生成内容，只刷新预览。
    /// </summary>
    private void OnAiBoardPlanEditRequested(string instruction)
    {
        if (_aiSession is null || _aiAssistant is null) return;
        if (!AiTargetStillCurrent(writeBack: false)) return;

        // 选中 AI 组时优先本地修改现有原生对象，不新增、不调用模型。
        if (TryApplySelectedAiObjectEdit(instruction)) return;

        AssistantBoardPlan plan = _aiBoardPlan ?? _aiSession.BoardPlan;
        if (plan is null || plan.IsEmpty)
        {
            _aiAssistant.ShowNotice("没有可调整的板书预览，请先生成。");
            return;
        }

        if (!BoardPlanEditPolicy.TryApply(plan, instruction, out AssistantBoardPlan updated, out string message))
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书本地编辑未识别：instruction={instruction}，reason={message}",
                Core.Diagnostics.LogType.Info);
            _aiAssistant.ShowNotice(message);
            return;
        }

        bool targetChanged = !string.Equals(
            updated.TargetBoard, plan.TargetBoard, StringComparison.OrdinalIgnoreCase);
        if (targetChanged && !ApplyBoardPlanTarget(updated.TargetBoard)) return;

        _aiBoardPlan = updated;
        _aiSession.BoardPlan = updated;
        _aiAssistant.ApplyBoardPlanPreview(updated, _aiBoardInstruction);
        _aiAssistant.ShowNotice(message);

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 板书本地编辑：instruction={instruction}，target={updated.TargetBoard}，"
            + $"page={updated.TargetPageIndex}，blocks={updated.Blocks.Count}，{message}",
            Core.Diagnostics.LogType.Event);
    }

    /// <summary>把写入目标切到主展板或板中板；窗口未打开时不切换并提示。</summary>
    private bool ApplyBoardPlanTarget(string targetBoard)
    {
        bool side = string.Equals(targetBoard, "side", StringComparison.OrdinalIgnoreCase);
        if (side)
        {
            if (_sideBoard is not { IsShown: true })
            {
                _aiAssistant?.ShowNotice("板中板窗口未打开，未切换目标；请先打开板中板。");
                return false;
            }
        }
        else if (_board is null)
        {
            _aiAssistant?.ShowNotice("主展板窗口未打开，未切换目标；请先打开主展板。");
            return false;
        }

        BoardService board = side ? Services.SideBoard : Services.Board;
        if (board is null)
        {
            _aiAssistant?.ShowNotice("目标画板不可用，未切换。");
            return false;
        }

        int pageIndex = Math.Clamp(_aiTarget?.PageIndex ?? board.CurrentIndex, 0, Math.Max(0, board.Pages.Count - 1));
        object page = board.Pages.Count > 0 && pageIndex < board.Pages.Count ? board.Pages[pageIndex] : null;

        _aiTarget = new AssistantTarget
        {
            Canvas = board,
            Page = page,
            Kind = side ? AssistantCanvasKind.SideBoard : AssistantCanvasKind.MainBoard,
            PageIndex = pageIndex,
            SessionId = _aiSession?.SessionId ?? "",
        };
        return true;
    }

    /// <summary>解析 board_plan 并交给浮窗预览；失败保留指令与旧计划，不覆盖正文渲染。</summary>
    private async Task PreviewBoardPlanAsync(AssistantEvent ev)
    {
        if (ev is null || _aiAssistant is null || _aiSession is null) return;
        if (!AiTargetStillCurrent(writeBack: false)) return;

        AssistantWindow window = _aiAssistant;
        string sessionId = _aiSession.SessionId ?? "";

        if (!AssistantBoardPlan.TryParse(ev.Text, out AssistantBoardPlan plan, out string error))
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] board_plan 解析失败：{error}", Core.Diagnostics.LogType.Error);
            window.ShowNotice("AI 返回的板书计划无法解析，已保留指令，可重试。");
            return;
        }

        await Task.CompletedTask;
        if (!ReferenceEquals(_aiAssistant, window)) return;
        if (!string.Equals(_aiSession?.SessionId ?? "", sessionId, StringComparison.Ordinal)) return;
        if (!AiTargetStillCurrent(writeBack: false)) return;

        // 预览目标与真实写入目标对齐，便于“移到主展板/板中板”的本地切换。
        plan.TargetBoard = _aiTarget?.Kind == AssistantCanvasKind.SideBoard ? "side" : "main";
        _aiBoardPlan = plan;
        _aiBoardRequestPending = false;
        _aiSession.BoardPlan = plan;
        window.ApplyBoardPlanPreview(plan, _aiBoardInstruction);

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] board_plan 就绪：schema={plan.Schema}，intent={plan.Intent}，blocks={plan.Blocks.Count}，"
            + $"planTarget={plan.TargetBoard}/{plan.TargetPageIndex}，actual={AssistantTargetPolicy.Describe(_aiTarget)}",
            Core.Diagnostics.LogType.Event);
    }

    /// <summary>
    /// 确认写入：先校验目标板/页仍有效，再渲染多张卡片、自动落点，最后用一次
    /// <see cref="BoardService.AddImagesAtomic"/> 完成整体写入与一次撤销。
    /// </summary>
    /// <summary>
    /// 确认写入：按 <paramref name="selectedIds"/> 过滤块（部分采用），渲染后一次写入；
    /// <paramref name="replaceOriginal"/> 为 true 时在同一撤销步骤内删除原笔画并插入新卡片。
    /// </summary>
    private async Task ConfirmBoardWriteAsync(IReadOnlyList<string> selectedIds, bool replaceOriginal)
    {
        AssistantWindow window = _aiAssistant;
        if (window is null) return;
        if (!AiTargetStillCurrent(writeBack: true)) { window.SetBoardPlanBusy(false); return; }

        AssistantBoardPlan plan = _aiBoardPlan ?? _aiSession?.BoardPlan;
        if (plan is null || plan.IsEmpty)
        {
            window.ShowNotice("没有可写入的板书计划，请重新生成。");
            return;
        }

        AssistantBoardPlan selectedPlan = selectedIds is { Count: > 0 }
            ? plan.SelectBlocks(selectedIds)
            : plan;
        if (selectedPlan.IsEmpty)
        {
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice("未选择任何板书块，请勾选后再采用。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书写入取消：没有匹配的选中块（selectedIds={selectedIds?.Count ?? 0}）",
                Core.Diagnostics.LogType.Info);
            return;
        }

        BoardService board = AiTargetBoard();
        if (board is null)
        {
            window.ShowNotice("目标画板不可用，已保留计划，可重试。");
            return;
        }

        int pageIndex = _aiTarget?.PageIndex ?? board.CurrentIndex;
        object targetPage = _aiTarget?.Page;
        string instruction = _aiBoardInstruction;

        window.SetBusy(true);
        window.SetBoardPlanBusy(true);

        BoardPlanNativeResult created;
        try
        {
            created = BoardPlanWriter.Create(selectedPlan, instruction);
        }
        catch (Exception ex)
        {
            created = new BoardPlanNativeResult { Error = ex.Message };
        }

        if (!ReferenceEquals(_aiAssistant, window)) return;
        if (!AiTargetStillCurrent(writeBack: true))
        {
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            return;
        }

        if (!created.Success)
        {
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice($"板书对象创建失败：{created.Error}，已保留计划，可重试。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书对象创建失败：intent={_aiBoardIntent}，error={created.Error}",
                Core.Diagnostics.LogType.Error);
            return;
        }

        if (!ReferenceEquals(board.Current, targetPage) || board.CurrentIndex != pageIndex)
        {
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice("板书目标页已切换，已保留计划；请回到原页后重试。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书写入目标已变：expected=pageIndex {pageIndex}，actual=pageIndex {board.CurrentIndex}",
                Core.Diagnostics.LogType.Error);
            return;
        }

        var sizes = new List<BoardCardSize>(created.Count);
        foreach (var text in created.Texts)
            sizes.Add(new BoardCardSize(text.Width, text.Height));
        foreach (var formula in created.Formulas)
            sizes.Add(new BoardCardSize(formula.Width, formula.Height));

        bool selectionOnly = board.Current.HasSelection;
        bool hasStrokeSelection = selectionOnly && board.Current.Selection.Count > 0;
        IReadOnlyList<InkCanvas.Core.Ink.InkStroke> strokesToRemove =
            Array.Empty<InkCanvas.Core.Ink.InkStroke>();
        if (replaceOriginal && selectionOnly && hasStrokeSelection)
            strokesToRemove = board.Current.Selection.ToList();
        else if (replaceOriginal && !selectionOnly)
            strokesToRemove = board.Current.Document.Strokes.ToList();

        Rect content = replaceOriginal
            ? board.GetContentBoundsExcept(strokesToRemove)
            : board.GetContentBounds();
        var contentBounds = new AssistantRect(content.Left, content.Top, content.Right, content.Bottom);
        AssistantRect viewport = BoardViewportRect(board);
        BoardSafeInsets safeInsets = BoardSafeInsetsFor(board);
        BoardLayoutPlan layout = BoardContentLayoutPlanner.Plan(
            contentBounds, sizes, viewport, safeInsets: safeInsets);

        int writePageIndex = pageIndex;
        bool newPage = false;
        BoardLayoutPlan writeLayout = layout;

        // 课堂总结固定写到新页：InsertPageAfterCurrent + RenamePage，再写入原生对象。
        // 与随堂练习页一致：对象可一次撤销，页本身保留（页级撤销单独记录）。
        bool summaryWrite = SummaryBoardPolicy.IsSummary(selectedPlan, instruction);
        if (summaryWrite)
        {
            if (!board.InsertPageAfterCurrent())
            {
                window.SetBoardPlanBusy(false);
                window.SetBusy(false);
                window.ShowNotice("新建总结页失败，已保留计划，可重试。");
                return;
            }

            newPage = true;
            writePageIndex = board.CurrentIndex;
            string summaryPageName = SummaryBoardPolicy.ResolvePageName(
                Services.Settings.Classroom.SummaryPageName);
            board.RenamePage(writePageIndex, summaryPageName);
            writeLayout = BoardContentLayoutPlanner.Plan(
                new AssistantRect(0, 0, 0, 0), sizes, BoardViewportRect(board),
                safeInsets: BoardSafeInsetsFor(board));

            if (!writeLayout.IsComplete)
            {
                board.RemovePage(writePageIndex);
                window.SetBoardPlanBusy(false);
                window.SetBusy(false);
                window.ShowNotice("总结页也无法排下当前内容，已保留计划。");
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[AI] 总结写入取消：总结页仍无法排下，status={writeLayout.Status}，"
                    + $"message={writeLayout.Message}",
                    Core.Diagnostics.LogType.Info);
                return;
            }

            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 总结页就绪：page={writePageIndex}，name={summaryPageName}，"
                + $"blocks={selectedPlan.Blocks.Count}，blocksKind="
                + $"{string.Join(",", selectedPlan.Blocks.Select(block => block.Kind))}",
                Core.Diagnostics.LogType.Event);
        }
        else if (layout.Status is BoardLayoutStatus.NeedsShrink
            or BoardLayoutStatus.NeedsMove or BoardLayoutStatus.NeedsNewPage)
        {
            BoardLayoutChoice choice = await ShowBoardLayoutDialogAsync(window, layout);
            if (choice == BoardLayoutChoice.Cancel)
            {
                window.SetBoardPlanBusy(false);
                window.SetBusy(false);
                window.ShowNotice("当前页空间不足，已保留预览，未写入。");
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[AI] 板书写入取消：当前页空间不足，status={layout.Status}，message={layout.Message}",
                    Core.Diagnostics.LogType.Info);
                return;
            }

            if (choice == BoardLayoutChoice.NewPage)
            {
                if (!board.InsertPageAfterCurrent())
                {
                    window.SetBoardPlanBusy(false);
                    window.SetBusy(false);
                    window.ShowNotice("新建页面失败，已保留计划，可重试。");
                    return;
                }

                newPage = true;
                writePageIndex = board.CurrentIndex;
                writeLayout = BoardContentLayoutPlanner.Plan(
                    new AssistantRect(0, 0, 0, 0), sizes, BoardViewportRect(board),
                    safeInsets: BoardSafeInsetsFor(board));

                if (!writeLayout.IsComplete)
                {
                    board.RemovePage(writePageIndex);
                    window.SetBoardPlanBusy(false);
                    window.SetBusy(false);
                    window.ShowNotice("新页面也无法排下当前板书，已保留计划。");
                    Core.Diagnostics.FileLog.WriteLogToFile(
                        $"[AI] 板书写入取消：新页面仍无法排下，status={writeLayout.Status}，"
                        + $"message={writeLayout.Message}",
                        Core.Diagnostics.LogType.Info);
                    return;
                }
            }
        }

        // 弹窗期间用户可能切板/切页/翻课件；写入前统一复检，新页选择也按当前上下文校验。
        // 总结写入会主动新建页，页身份必然变化：只要求会话与画板未变。
        bool contextStillCurrent = summaryWrite
            ? ClassroomContextBoardStillCurrent()
            : ClassroomContextStillCurrent(writeBack: true);
        if (UnifiedContextEnabled && !contextStillCurrent)
        {
            if (newPage) board.RemovePage(writePageIndex);
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice("板书目标在弹窗期间已变化（画板/页/课件页已切换），本次未写入；请回到原页重试。");
            return;
        }

        if (!writeLayout.IsComplete)
        {
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice(writeLayout.Message);
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书空间不足：blocks={created.Count}，status={writeLayout.Status}，"
                + $"message={writeLayout.Message}",
                Core.Diagnostics.LogType.Info);
            return;
        }

        if (!newPage && HasSameInstructionOnPage(board, instruction))
        {
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice("目标页已存在相同指令的 AI 板书对象，请先选中修改或清空后再写入；未重复插入。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书写入已阻止：同页已有相同 Instruction，instruction={instruction}",
                Core.Diagnostics.LogType.Info);
            return;
        }

        // 写入前最后确认目标板当前页仍是请求快照里的那一页（新页分支已在上面按新页写入，跳过）。
        // 文档/PPT 的 PageId 是文档页/课件页身份，不是主展板页 Id，不能直接比，交给上面的上下文复检。
        if (UnifiedContextEnabled && !newPage && _aiContext is { } writeContext
            && writeContext.HasPageIdentity
            && writeContext.Kind is (BoardCanvasKind.MainBoard
                or BoardCanvasKind.SideBoard or BoardCanvasKind.Overlay)
            && board.CurrentPageId != writeContext.PageId)
        {
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice("板书目标页已变化，本次未写入；请回到原页重试。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书写入已阻止：上下文页标识不匹配，expected={writeContext.PageId}，actual={board.CurrentPageId}",
                Core.Diagnostics.LogType.Info);
            return;
        }

        string groupId = Guid.NewGuid().ToString("N");
        for (int index = 0; index < created.Texts.Count; index++)
        {
            BoardCardPlacement placement = writeLayout.Placements[index];
            created.Texts[index].Left = (float)placement.Left;
            created.Texts[index].Top = (float)placement.Top;
            created.Texts[index].Width = (float)placement.Width;
            created.Texts[index].Height = (float)placement.Height;
        }
        for (int index = 0; index < created.Formulas.Count; index++)
        {
            BoardCardPlacement placement = writeLayout.Placements[created.Texts.Count + index];
            created.Formulas[index].Left = (float)placement.Left;
            created.Formulas[index].Top = (float)placement.Top;
            created.Formulas[index].Width = (float)placement.Width;
            created.Formulas[index].Height = (float)placement.Height;
        }

        bool effectiveReplace = replaceOriginal && !newPage;
        int removedStrokes = 0;
        IReadOnlyList<Guid> objectIds;

        if (effectiveReplace)
        {
            if (selectionOnly && !hasStrokeSelection)
            {
                window.ShowNotice("选区中没有笔画，已仅插入原生对象（未删除原迹）。");
                objectIds = board.AddAiObjectsAtomic(created.Texts, created.Formulas, "AI 板书", groupId);
            }
            else
            {
                removedStrokes = strokesToRemove.Count;
                objectIds = board.ReplaceStrokesWithAiObjectsAtomic(
                    strokesToRemove, created.Texts, created.Formulas, "AI 板书替换", groupId);
                if (objectIds.Count == 0)
                {
                    removedStrokes = 0;
                    objectIds = board.AddAiObjectsAtomic(created.Texts, created.Formulas, "AI 板书", groupId);
                    if (objectIds.Count > 0)
                        window.ShowNotice("当前页没有可替换的原笔画，已仅插入原生对象。");
                }
            }
        }
        else
        {
            objectIds = board.AddAiObjectsAtomic(created.Texts, created.Formulas,
                newPage ? "AI 板书（新页）" : "AI 板书", groupId);
        }

        if (objectIds.Count == 0)
        {
            if (newPage) board.RemovePage(writePageIndex);
            window.SetBoardPlanBusy(false);
            window.SetBusy(false);
            window.ShowNotice("板书写入失败，已保留计划，可重试。");
            return;
        }

        SelectAiBoardGroup(board, groupId);
        // 收藏为例题时带上本次原生对象分组，便于再讲/回放定位。
        window.SetFavoriteObjects(groupId, objectIds);

        if (newPage)
        {
            _aiTarget = new AssistantTarget
            {
                Canvas = board,
                Page = board.Current,
                Kind = _aiTarget?.Kind ?? (board == Services.SideBoard
                    ? AssistantCanvasKind.SideBoard : AssistantCanvasKind.MainBoard),
                PageIndex = board.CurrentIndex,
                SessionId = _aiSession?.SessionId ?? "",
            };
            if (_aiContext is not null)
            {
                // 新页写入成功后把上下文页身份迁移到新页，避免后续复检仍指向旧页。
                _aiContext = _aiContext with
                {
                    PageId = board.CurrentPageId,
                    PageIndex = board.CurrentIndex,
                    Generation = _aiGate.Generation,
                };
                SyncRecorderContext();
            }
        }

        if (board == Services.Board && _board is null) ToggleBoard();

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 板书写入完成：board={(_aiTarget?.Kind == AssistantCanvasKind.SideBoard ? "side" : "main")}，"
            + $"page={writePageIndex}，newPage={newPage}，selected={selectedPlan.Blocks.Count}/{plan.Blocks.Count}，"
            + $"replace={effectiveReplace}，removedStrokes={removedStrokes}，"
            + $"nativeTexts={created.Texts.Count}，nativeFormulas={created.Formulas.Count}，"
            + $"objects={objectIds.Count}，images=0，layout={writeLayout.Status}，"
            + $"scale={writeLayout.Scale:0.00}，ids={string.Join(",", objectIds)}",
            Core.Diagnostics.LogType.Event);

        _aiBoardPlan = null;
        _aiBoardInstruction = "";
        _aiBoardIntent = "";
        _aiBoardRequestPending = false;
        window.ClearBoardPlanPreview();
        window.SetBoardPlanBusy(false);
        window.SetBusy(false);
        window.SetPasted(true);

        string scaleNote = string.IsNullOrEmpty(writeLayout.Message) ? "" : writeLayout.Message;
        string actionNote = summaryWrite
            ? "已写入「" + SummaryBoardPolicy.ResolvePageName(Services.Settings.Classroom.SummaryPageName)
              + "」新页（原生对象）；总结页保留，对象可 Ctrl+Z 一次撤销。"
            : newPage
                ? "已写入新的一页（原生对象）；页面保留，可用 Ctrl+Z 撤销。"
                : removedStrokes > 0
                    ? $"已替换原板书（删除 {removedStrokes} 笔），原生对象可 Ctrl+Z 一次撤销。"
                    : "已写入展板（原生对象），可 Ctrl+Z 整体撤销。";
        ClassroomContext noticeContext = _aiContext;
        if (UnifiedContextEnabled && noticeContext is not null
            && noticeContext.Kind is (BoardCanvasKind.Overlay
                or BoardCanvasKind.PptOverlay or BoardCanvasKind.Document))
        {
            // 覆层/课件/文档请求实际写入主展板，提示里点明目标画布，避免教师误以为是当前页。
            string targetNote = noticeContext.Kind switch
            {
                BoardCanvasKind.PptOverlay => "PPT",
                BoardCanvasKind.Document => "文档",
                _ => "悬浮层",
            };
            window.ShowNotice("已写入主展板第 " + (writePageIndex + 1)
                + " 页（目标画布：" + targetNote + "）。" + scaleNote + actionNote);
        }
        else
        {
            window.ShowNotice(scaleNote + actionNote);
        }
        // 写回成功后记录一次上下文与目标的对应关系，便于排查串写。
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 写回完成：boardId={AiTargetBoardId()}，kind={AiTargetContextKind()}，page={writePageIndex}，"
            + $"pageId={board.CurrentPageId}，context=({ClassroomContextPolicy.Describe(_aiContext)})",
            Core.Diagnostics.LogType.Info);
    }

    /// <summary>写入后选中同一次 AI 写入的全部原生对象，便于整体移动/缩放与取消分组。</summary>
    private static void SelectAiBoardGroup(BoardService board, string groupId)
    {
        if (board is null || string.IsNullOrWhiteSpace(groupId)) return;

        var first = board.GetSelectionElements()
            .FirstOrDefault(element => element.IsPresent
                && element is IGroupedSelectionElement grouped
                && string.Equals(grouped.SelectionGroupId, groupId, StringComparison.Ordinal));
        if (first is not null) board.Current.SelectElement(first);
    }

    /// <summary>
    /// 连续修改指令优先作用于当前选中的 AI 对象组：批量更新样式/过滤删除，
    /// 一次撤销、不新增对象、不调用 AI。无选中 AI 组时返回 false，交给预览 plan 路径。
    /// </summary>
    private bool TryApplySelectedAiObjectEdit(string instruction)
    {
        BoardService board = AiTargetBoard();
        if (board?.Current is null) return false;

        var groups = board.Current.SelectedElements
            .OfType<IGroupedSelectionElement>()
            .Select(element => element.SelectionGroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (groups.Count == 0) return false;

        var targets = board.CurrentTextObjects
            .Where(text => groups.Contains(text.GroupId))
            .Cast<ISelectionElement>()
            .Concat(board.CurrentFormulaObjects.Where(formula => groups.Contains(formula.GroupId)))
            .ToList();
        if (targets.Count == 0) return false;

        if (!BoardPlanEditPolicy.TryParseObjectEdit(
                instruction, out BoardPlanEditPolicy.BoardObjectEdit edit, out _))
            return false;

        if (!board.ApplyAiObjectEdit(targets, edit, "连续修改 AI 板书"))
        {
            _aiAssistant?.ShowNotice("选中 AI 对象已是最新状态，或过滤后没有可保留对象。");
            return true;
        }

        string primaryGroup = groups.First();
        board.Current.ClearSelection();
        SelectAiBoardGroup(board, primaryGroup);
        board.Current.NotifyElementsChanged();
        _aiAssistant?.ShowNotice($"已更新选中 AI 组（{targets.Count} 个对象），未新增、未调用 AI。");
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 连续修改现有 AI 对象：groups={groups.Count}，targets={targets.Count}，"
            + $"fontScale={edit.FontScale:0.00}，columns={edit.Columns?.ToString() ?? "-"}，"
            + $"keep={string.Join(",", edit.KeepKinds)}，remove={string.Join(",", edit.RemoveKinds)}",
            Core.Diagnostics.LogType.Event);
        return true;
    }

    private static bool HasSameInstructionOnPage(BoardService board, string instruction)
    {
        if (board is null || string.IsNullOrWhiteSpace(instruction)) return false;
        return board.CurrentTextObjects.Any(text =>
                   string.Equals(text.Instruction, instruction, StringComparison.Ordinal))
            || board.CurrentFormulaObjects.Any(formula =>
                   string.Equals(formula.Instruction, instruction, StringComparison.Ordinal));
    }


    /// <summary>空间不足时让用户选择：缩小排版写入、写入新的一页或取消。</summary>
    private async Task<BoardLayoutChoice> ShowBoardLayoutDialogAsync(
        AssistantWindow window, BoardLayoutPlan layout)
    {
        BoardLayoutDialogSpec spec = BoardLayoutDecisionPolicy.Describe(layout);
        var dialog = new ContentDialog
        {
            Title = spec.Title,
            Content = new TextBlock { Text = spec.Message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = spec.PrimaryText,
            SecondaryButtonText = spec.SecondaryText,
            CloseButtonText = spec.CloseText,
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await ShowDialogAsync(dialog);

        if (result == ContentDialogResult.Primary) return spec.PrimaryChoice;
        if (result == ContentDialogResult.Secondary) return spec.SecondaryChoice;
        return BoardLayoutChoice.Cancel;
    }

    /// <summary>把目标窗口工具栏高度换算为 world DIP 安全区（视图缩放时按比例放大）。</summary>
    private BoardSafeInsets BoardSafeInsetsFor(BoardService board)
    {
        BoardSafeInsets screenInsets = board == Services.SideBoard
            ? _sideBoard?.GetContentSafeInsets() ?? BoardContentLayoutPlanner.DefaultSideBoardInsets
            : _board?.GetContentSafeInsets() ?? BoardContentLayoutPlanner.DefaultMainBoardInsets;

        Matrix3x2 transform = board.Current.ViewTransform;
        double scale = Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
        if (!(scale > 0.05) || !double.IsFinite(scale)) scale = 1.0;

        return new BoardSafeInsets(
            screenInsets.Top / scale,
            screenInsets.Bottom / scale,
            screenInsets.Left / scale,
            screenInsets.Right / scale);
    }

    /// <summary>当前板视口（客户区）反变换到世界坐标，供自动落点使用。</summary>
    private AssistantRect BoardViewportRect(BoardService board)
    {
        var (work, dpi, _, _) = AssistantTarget();
        double width = Math.Max(1, work.Width / dpi);
        double height = Math.Max(1, work.Height / dpi);

        if (_aiTarget?.Kind == AssistantCanvasKind.SideBoard && _sideBoard is not null)
        {
            ScreenRect client = WindowChrome.GetClientBounds(WindowChrome.GetHwnd(_sideBoard));
            if (client.Width > 0 && client.Height > 0)
            {
                width = client.Width / dpi;
                height = client.Height / dpi;
            }
        }

        Matrix3x2 transform = board.Current.ViewTransform;
        if (!Matrix3x2.Invert(transform, out Matrix3x2 inverse)) inverse = Matrix3x2.Identity;

        Vector2 topLeft = Vector2.Transform(Vector2.Zero, inverse);
        Vector2 bottomRight = Vector2.Transform(new Vector2((float)width, (float)height), inverse);
        return new AssistantRect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
    }

    private void OnAiPasteRequested() => _ = PasteAssistantAsync();

    private async Task PasteAssistantAsync()
    {
        if (!AiTargetStillCurrent(writeBack: true)) { _aiAssistant?.SetBusy(false); return; }
        if (_aiSession is null || _aiAssistant is null) return;
        if (_aiSession.State != AssistantState.Done) return;

        var window = _aiAssistant;
        AssistantSession session = _aiSession;
        List<string> lines = BuildPasteLines(session);

        window.SetBusy(true);
        string metadata = "";
        if (Services.Settings.Ai.EnableEditablePaste)
        {
            try
            {
                metadata = BuildPasteMetadata(session).Serialize();
            }
            catch (Exception ex)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    $"AI 贴回元数据生成失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            }
        }

        BoardService targetBoard = AiTargetBoard();
        var (rendered, added) = await RenderAndPlacePasteCardAsync(
            targetBoard, "AI 助教", lines, metadata, _aiAnchor);
        if (!ReferenceEquals(_aiAssistant, window)) return;
        if (!AiTargetStillCurrent(writeBack: true))
        {
            window.SetBusy(false);
            return;
        }

        if (!rendered)
        {
            window.SetBusy(false);
            AssistantMachine.Fail(session, "贴回失败");
            window.Apply(session, _aiThumb);
            return;
        }

        if (!added)
        {
            window.SetBusy(false);
            window.ShowNotice("贴回失败：目标画板当前页已失效，请重新框选后再试");
            return;
        }

        if (targetBoard == Services.Board && _board is null) ToggleBoard();

        window.SetBusy(false);
        window.SetPasted(true);
    }

    /// <summary>
    /// AI 卡片贴回的共用实现：渲染 PNG、按浮窗锚点换算落点并写入目标画板。
    /// 返回 (是否渲染成功, 是否写入成功)，供浮窗贴回与例题旧卡片兼容路径复用。
    /// </summary>
    private async Task<(bool Rendered, bool Added)> RenderAndPlacePasteCardAsync(
        BoardService targetBoard, string name, IReadOnlyList<string> lines,
        string metadata, AssistantRect anchor)
    {
        if (targetBoard is null) return (false, false);

        var (width, height) = PasteCardLayout.Estimate(lines);
        byte[] png = await PasteCardRenderer.RenderPngAsync(name, lines, width, height);
        if (png is null || png.Length == 0) return (false, false);

        var (work, dpi, _, _) = AssistantTarget();
        double viewWidth = work.Width / dpi;
        double viewHeight = work.Height / dpi;
        if (ReferenceEquals(targetBoard, Services.SideBoard) && _sideBoard is not null)
        {
            ScreenRect client = WindowChrome.GetClientBounds(WindowChrome.GetHwnd(_sideBoard));
            if (client.Width > 0 && client.Height > 0)
            {
                viewWidth = client.Width / dpi;
                viewHeight = client.Height / dpi;
            }
        }
        var (left, top, w, h) = PasteCardLayout.Place(anchor, width, height, viewWidth, viewHeight);

        bool added = targetBoard.AddImage(new BoardImageAsset
        {
            Name = name,
            Data = png,
            ContentType = "image/png",
            Left = (float)left,
            Top = (float)top,
            Width = (float)w,
            Height = (float)h,
            Metadata = metadata,
        });
        return (true, added);
    }

    private async Task<bool> PasteCardToBoardAsync(
        BoardService targetBoard, string name, IReadOnlyList<string> lines, string metadata)
    {
        var (rendered, added) = await RenderAndPlacePasteCardAsync(
            targetBoard, name, lines, metadata, CenterAnchor());
        return rendered && added;
    }

    private void OnAiFollowUpRequested(string question)
    {
        if (_aiSession is null) return;
        // 追问沿用原会话，不写画布，只要求会话可继续（选区变化不影响追问）。
        if (!AiTargetStillCurrent(continueSession: true)) return;

        var request = new AssistantRequest
        {
            ImageBase64 = _aiThumb,
            InkStrokes = Array.Empty<AssistantStroke>(),
            Viewport = new AssistantViewport { Scale = 1.0, Rect = _aiAnchor },
            Mode = AssistantMode.Chat,
            SessionId = _aiSession.SessionId,
            Question = question ?? "",
            SlideContext = BuildSlideContext(),
        };
        StartAssistantStream(request, followUp: true, question: question);
    }

    private void OnAiWindowClosed(AssistantWindow window)
    {
        if (!ReferenceEquals(_aiAssistant, window)) return;
        _aiGate.Invalidate();
        _variantPending = false;
        _aiStream?.Abort();
        _aiStream = null;
        _aiRegionStream?.Abort();
        _aiRegionStream = null;
        ResetAiPreStage();
        // 浮窗关闭即会话结束：统一上下文、旧目标与会话缓存一并清空，避免下一轮误用旧身份。
        _aiContext = null;
        SyncRecorderContext();
        _aiTarget = null;
        _aiSession = null;
        _aiProblem = null;
        _aiThumb = "";
        _aiBoardPlan = null;
        _aiBoardInstruction = "";
        _aiAssistantFromSideBoard = false;
        _aiHiddenWithSideBoard = false;
        _aiPendingExamplePrompt = "";
        _aiAssistant = null;
    }

    private void OnAiRegionSelected(AssistantRect rect)
    {
        AssistantSession receivedSession = _aiSession;
        AssistantWindow receivedWindow = _aiAssistant;
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"AI 局部纠错入口：recv rect=({rect.Left:0.0},{rect.Top:0.0})-({rect.Right:0.0},{rect.Bottom:0.0})，"
            + $"EnableLocalCorrection={Services.Settings.Ai.EnableLocalCorrection}，"
            + $"会话={(receivedSession is null ? "null" : receivedSession.State.ToString())}，"
            + $"窗口={(receivedWindow is null ? "null" : "ok")}",
            Core.Diagnostics.LogType.Trace);

        if (!AiTargetStillCurrent(continueSession: true)) return;
        if (!Services.Settings.Ai.EnableLocalCorrection) return;
        if (rect.IsEmpty) return;

        AssistantSession session = _aiSession;
        AssistantWindow window = _aiAssistant;
        if (session is null || window is null) return;
        if (session.State != AssistantState.Done)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 局部纠错入口：会话状态 {session.State} 非 Done，提示后返回",
                Core.Diagnostics.LogType.Trace);
            window.ShowNotice("生成完成后才能局部纠错");
            return;
        }

        bool formulaTarget = !string.IsNullOrWhiteSpace(session.Formula);
        string target = formulaTarget ? session.Formula ?? "" : session.Text ?? "";
        AssistantRect anchor = _aiAnchor;
        var (start, length) = RegionTextMapper.Map(anchor, rect, target);
        AssistantRegion region = new()
        {
            Rect = rect,
            TextStart = start,
            TextLength = length,
            Source = "region",
        };

        _aiRegionStream?.Abort();
        _aiRegionStream = null;

        try
        {
            _aiClient ??= new AiClient(AiConfig.ReadFromEnvironment());

            var request = new AssistantRequest
            {
                ImageBase64 = _aiThumb,
                InkStrokes = Array.Empty<AssistantStroke>(),
                Viewport = new AssistantViewport { Scale = 1.0, Rect = _aiAnchor },
                Mode = session.Mode,
                SessionId = session.SessionId ?? "",
                Stage = AssistantStage.WithImage,
                Region = region,
            };

            var buffer = new StringBuilder();
            string formula = null;
            bool failure = false;

            window.ShowNotice("局部纠错中…");

            _aiRegionStream = _aiClient.Start(
                request,
                ev => _dispatcher.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_aiAssistant, window)) return;
                    if (ev.Kind == AssistantEventKind.Token)
                    {
                        buffer.Append(ev.Text ?? "");
                    }
                    else if (ev.Kind == AssistantEventKind.Formula && !string.IsNullOrEmpty(ev.Latex))
                    {
                        formula = ev.Latex;
                    }
                }),
                err => _dispatcher.TryEnqueue(() =>
                {
                    failure = true;
                    Core.Diagnostics.FileLog.WriteLogToFile(
                        $"AI 局部纠错失败：{err}", Core.Diagnostics.LogType.Error);
                    if (!ReferenceEquals(_aiAssistant, window)) return;
                    window.ShowNotice("局部纠错失败，已保留原结果");
                }),
                () => _dispatcher.TryEnqueue(() =>
                {
                    _aiRegionStream = null;
                    if (failure || !ReferenceEquals(_aiAssistant, window)) return;
                    if (!ReferenceEquals(_aiSession, session)) return;
                    // 局部纠错完成只更新会话文本，不写画布，不要求选区未变化。
                    if (!AiTargetStillCurrent(continueSession: true)) return;

                    string replacement = string.IsNullOrWhiteSpace(formula) ? buffer.ToString() : formula;
                    if (string.IsNullOrWhiteSpace(replacement))
                    {
                        Core.Diagnostics.FileLog.WriteLogToFile(
                            "AI 局部纠错结果：返回内容为空，保留原结果",
                            Core.Diagnostics.LogType.Trace);
                        window.ShowNotice("局部纠错未返回内容");
                        return;
                    }

                    try
                    {
                        string trimmed = replacement.Trim();
                        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
                            session, anchor, rect, trimmed);
                        AssistantSession updated = result.Session;

                        if (updated is null)
                        {
                            Core.Diagnostics.FileLog.WriteLogToFile(
                                $"AI 局部纠错结果应用失败：流程返回 null（Field={result.Field}，替换长度 {trimmed.Length}）",
                                Core.Diagnostics.LogType.Error);
                            window.ShowNotice("局部纠错结果应用失败");
                            return;
                        }

                        int previousLength = result.Field == nameof(AssistantSession.Formula)
                            ? (session.Formula ?? "").Length
                            : (session.Text ?? "").Length;
                        int updatedLength = result.Field == nameof(AssistantSession.Formula)
                            ? (updated.Formula ?? "").Length
                            : (updated.Text ?? "").Length;

                        updated.Regions.Add(region);
                        _aiSession = updated;
                        window.Apply(updated, _aiThumb, preserveScroll: true);

                        bool appended = result.Field == nameof(AssistantSession.Text)
                            && string.IsNullOrEmpty(session.Text)
                            && !string.IsNullOrEmpty(updated.Text);
                        Core.Diagnostics.FileLog.WriteLogToFile(
                            $"AI 局部纠错结果应用成功：Field={result.Field}，"
                            + $"start={result.Start}，length={result.Length}，长度 {previousLength}->{updatedLength}，"
                            + $"替换长度 {trimmed.Length}，追加={appended}，"
                            + $"region=({region.Rect.Left:0.0},{region.Rect.Top:0.0})-({region.Rect.Right:0.0},{region.Rect.Bottom:0.0})",
                            Core.Diagnostics.LogType.Event);
                        window.ShowNotice(appended ? "已将局部结果追加到正文" : "已应用局部纠错");
                    }
                    catch (Exception ex)
                    {
                        Core.Diagnostics.FileLog.WriteLogToFile(
                            $"AI 局部纠错结果应用失败：{ex.Message}", Core.Diagnostics.LogType.Error);
                        window.ShowNotice("局部纠错结果应用失败");
                    }
                }));
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 局部纠错启动失败：{ex.Message}", Core.Diagnostics.LogType.Error);
            window.ShowNotice("局部纠错失败，已保留原结果");
        }
    }

    /// <summary>统一课堂上下文开关；关闭时沿用旧 <see cref="AssistantTarget"/> 路由与文案。</summary>
    private bool UnifiedContextEnabled => Services.Settings.Features.UnifiedContext;

    /// <summary>把当前 AI 请求上下文按画板归属分发给主/子板录制器（BoardId 0/1）。</summary>
    private void SyncRecorderContext()
    {
        if (!UnifiedContextEnabled || _aiContext is null)
        {
            _recorder?.SetAiContext(null);
            _sideBoardRecorder?.SetAiContext(null);
            return;
        }
        _recorder?.SetAiContext(_aiContext.Kind == BoardCanvasKind.SideBoard ? null : _aiContext);
        _sideBoardRecorder?.SetAiContext(_aiContext.Kind == BoardCanvasKind.SideBoard ? _aiContext : null);
    }

    /// <summary>
    /// 按当前真实状态构建统一课堂上下文。这里不写 SessionId/RequestId/Generation，
    /// 由请求发起处补全；纯读取状态，不修改画板内容。
    /// </summary>
    private ClassroomContext CaptureClassroomContext(bool includeSelection = true)
    {
        long now = Environment.TickCount64;
        ClassroomContext context;

        if (_sideBoard is { IsShown: true } && ReferenceEquals(ActiveController, _sideBoard.CurrentController))
        {
            BoardService board = Services.SideBoard;
            InkController controller = board.Current;
            context = new ClassroomContext
            {
                Kind = BoardCanvasKind.SideBoard,
                BoardId = "side",
                PageId = board.CurrentPageId,
                PageIndex = board.CurrentIndex,
                SelectionId = includeSelection ? DescribeSelectionId(controller) : "",
                StrokeIds = includeSelection ? CaptureSelectedStrokeIds(controller) : Array.Empty<string>(),
                GroupId = includeSelection ? CaptureSelectedGroupId(controller) : "",
                ObjectIds = includeSelection ? CaptureSelectedObjectIds(controller) : Array.Empty<string>(),
                Source = ContentSource.UserInk,
                CreatedAtMs = now,
            };
        }
        else if (_board is not null && ReferenceEquals(ActiveController, _board.CurrentController))
        {
            BoardService board = Services.Board;
            InkController controller = board.Current;
            context = new ClassroomContext
            {
                Kind = BoardCanvasKind.MainBoard,
                BoardId = "main",
                PageId = board.CurrentPageId,
                PageIndex = board.CurrentIndex,
                SelectionId = includeSelection ? DescribeSelectionId(controller) : "",
                StrokeIds = includeSelection ? CaptureSelectedStrokeIds(controller) : Array.Empty<string>(),
                GroupId = includeSelection ? CaptureSelectedGroupId(controller) : "",
                ObjectIds = includeSelection ? CaptureSelectedObjectIds(controller) : Array.Empty<string>(),
                Source = ContentSource.UserInk,
                CreatedAtMs = now,
            };
        }
        else if (DocumentActive)
        {
            // 文档工作区没有对外暴露页索引，页身份用文档页 Guid 承载；索引按 0 记录。
            if (_documentPageId == Guid.Empty) _documentPageId = Guid.NewGuid();
            context = new ClassroomContext
            {
                Kind = BoardCanvasKind.Document,
                BoardId = "document",
                PageId = _documentPageId,
                PageIndex = 0,
                PresentationKey = null,
                Source = ContentSource.UserInk,
                CreatedAtMs = now,
            };
        }
        else if (_pptSession?.Snapshot?.IsSlideShowRunning == true
                 && _pptSession.Snapshot.Presentation is not null)
        {
            PptSnapshot snapshot = _pptSession.Snapshot;
            string presentationKey = snapshot.Presentation.Identity;
            int slideId = snapshot.Slide?.SlideId ?? _aiSlideId;
            int? slideIndex = snapshot.Slide?.Index;
            if (!slideIndex.HasValue && _aiSlideIndex > 0) slideIndex = _aiSlideIndex;
            Guid pageId = ClassroomContextFactory.DeterministicPageId(presentationKey, slideId);
            if (pageId == Guid.Empty) pageId = Services.Board.CurrentPageId;
            // 课件放映写在主展板页上（素材/批注落点仍是主展板），但身份按课件页区分。
            context = new ClassroomContext
            {
                Kind = BoardCanvasKind.PptOverlay,
                BoardId = "ppt",
                PresentationKey = presentationKey,
                SlideId = snapshot.Slide?.SlideId ?? _aiSlideId,
                SlideIndex = slideIndex,
                AnimationStep = snapshot.IsSlideShowRunning ? Services.Ppt.GetAnimationStep() : 0,
                PageId = pageId,
                PageIndex = slideIndex ?? Services.Board.CurrentIndex,
                Source = ContentSource.UserInk,
                CreatedAtMs = now,
            };
        }
        else
        {
            // 桌面/窗口覆层：没有独立页面身份，写回落到主展板当前页。
            context = new ClassroomContext
            {
                Kind = BoardCanvasKind.Overlay,
                BoardId = "overlay",
                PageId = Services.Board.CurrentPageId,
                PageIndex = Services.Board.CurrentIndex,
                Source = ContentSource.UserInk,
                CreatedAtMs = now,
            };
        }

        return ClassroomContextFactory.Normalize(context);
    }

    /// <summary>
    /// 按已记录的上下文重建当前快照：画布/页/课件/选区按真实状态重取，
    /// 会话、请求与代数沿用发起请求时的记录（显示/写回校验都看这份快照）。
    /// </summary>
    private ClassroomContext SnapshotClassroomContext(bool includeSelection = true)
    {
        if (_aiContext is null) return null;
        ClassroomContext current = CaptureClassroomContext(includeSelection);
        if (current is null) return null;
        return current with
        {
            SessionId = _aiGate.SessionId,
            RequestId = _aiContext.RequestId,
            Generation = _aiGate.Generation,
            CreatedAtMs = Environment.TickCount64,
        };
    }

    /// <summary>
    /// 统一上下文过期校验。writeBack=true（贴回/局部纠错）要求同板、同页、同课件页、
    /// 同选区与同请求代数；continueSession=true（变式预览、再讲追问、教学模式切换、局部纠错、
    /// 板书请求）只要求同板、同页、同课件页与同代数，不约束选区；两者都不传时为流式显示校验
    /// （只要求同板同页）。未记录上下文时保持旧路径行为（视为有效）。
    /// </summary>
    private bool ClassroomContextStillCurrent(bool writeBack, bool continueSession = false)
    {
        if (_aiContext is null) return true;
        // continueSession 不比较选区，快照无需采集选区（把“选区变化”从判定链路中彻底移除）。
        ClassroomContext current = SnapshotClassroomContext(includeSelection: writeBack);
        bool ok = continueSession
            ? ClassroomContextPolicy.CanContinueSession(_aiContext, current)
            : writeBack
                ? ClassroomContextPolicy.CanWriteBack(_aiContext, current)
                : ClassroomContextPolicy.IsCurrentForDisplay(_aiContext, current);
        if (ok) return true;

        if (continueSession)
        {
            // 会话继续失败只按画板/页面/课件页/代数说明原因，不出现“选区已变化”的写回提示。
            string continueNotice = ClassroomContextPolicy.ContinueSessionNotice(_aiContext, current);
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 会话继续被丢弃：requested={ClassroomContextPolicy.Describe(_aiContext)}，"
                + $"current={ClassroomContextPolicy.Describe(current)}，notice={continueNotice}",
                Core.Diagnostics.LogType.Info);
            _aiAssistant?.ShowNotice(continueNotice);
            return false;
        }

        string notice = ClassroomContextPolicy.StaleNotice(_aiContext, current);
        bool crossBoard = !ClassroomContextPolicy.SameBoard(_aiContext, current);
        Core.Diagnostics.FileLog.WriteLogToFile(
            (writeBack ? "[AI] 丢弃过期写回：" : "[AI] 流式显示丢弃过期上下文：")
            + $"requested={ClassroomContextPolicy.Describe(_aiContext)}，"
            + $"current={ClassroomContextPolicy.Describe(current)}，notice={notice}",
            Core.Diagnostics.LogType.Info);
        if (writeBack || crossBoard) _aiAssistant?.ShowNotice(notice);
        return false;
    }

    /// <summary>
    /// 总结写入专用复检：主动新建页后页身份必然变化，只校验会话与画板未切换，
    /// 避免把「按设计新建总结页」误判为目标过期。
    /// </summary>
    private bool ClassroomContextBoardStillCurrent()
    {
        if (_aiContext is null) return true;
        ClassroomContext current = SnapshotClassroomContext(includeSelection: false);
        if (current is null) return false;

        bool ok = ClassroomContextPolicy.SameSession(_aiContext, current)
                  && ClassroomContextPolicy.SameBoard(_aiContext, current);
        if (ok) return true;

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"{ClassroomContextPolicy.Describe(_aiContext)} 总结写入丢弃过期上下文："
            + $"current={ClassroomContextPolicy.Describe(current)}",
            Core.Diagnostics.LogType.Info);
        return false;
    }

    /// <summary>选区稳定短描述：笔画与元素 Id 排序后取 SHA256 前 8 字节，仅用于判定选区是否变化。</summary>
    private static string DescribeSelectionId(InkController controller)
    {
        if (controller is null || !controller.HasSelection) return "";
        var ids = controller.Selection.Select(stroke => stroke.Id.ToString())
            .Concat(controller.SelectedElements.Select(element => element.Id.ToString()))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return "";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join(",", ids)));
        return "sel-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>选中笔画 Id；元素选区（图片/AI 对象）不把整页笔画当作选区内容，由 ObjectIds 表达。</summary>
    private static IReadOnlyList<string> CaptureSelectedStrokeIds(InkController controller)
    {
        if (controller is null) return Array.Empty<string>();
        if (controller.Selection.Count > 0)
            return controller.Selection.Select(stroke => stroke.Id.ToString()).ToList();
        return Array.Empty<string>();
    }

    /// <summary>选中元素里第一个非空分组 Id；没有分组时返回空字符串。</summary>
    private static string CaptureSelectedGroupId(InkController controller)
    {
        if (controller is null) return "";
        foreach (ISelectionElement element in controller.SelectedElements)
        {
            if (element is IGroupedSelectionElement grouped
                && !string.IsNullOrWhiteSpace(grouped.SelectionGroupId))
            {
                return grouped.SelectionGroupId;
            }
        }

        return "";
    }

    /// <summary>当前选中元素 Id（含 AI 原生对象与图片）；无元素选区时为空集合。</summary>
    private static IReadOnlyList<string> CaptureSelectedObjectIds(InkController controller)
        => controller?.SelectedElements.Select(element => element.Id.ToString()).ToList()
           ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>按当前激活画布（子板优先 → 主展板 → overlay）记录本次 AI 请求目标。</summary>
    private AssistantTarget CaptureAssistantTarget()
    {
        AssistantCanvasKind kind = AssistantCanvasKind.Overlay;
        object canvas = null, page = null;
        int pageIndex = 0;
        if (_sideBoard is { IsShown: true } && ReferenceEquals(ActiveController, _sideBoard.CurrentController))
        {
            kind = AssistantCanvasKind.SideBoard;
            canvas = Services.SideBoard;
            pageIndex = Services.SideBoard.CurrentIndex;
        }
        else if (_board is not null && ReferenceEquals(ActiveController, _board.CurrentController))
        {
            kind = AssistantCanvasKind.MainBoard;
            canvas = Services.Board;
            pageIndex = Services.Board.CurrentIndex;
        }
        else if (DocumentActive)
        {
            kind = AssistantCanvasKind.Document;
            canvas = null;
            page = _documentWorkspace?.Controller;
            pageIndex = 0;
        }
        else if (_pptSession?.Snapshot?.IsSlideShowRunning == true
                 && _pptSession.Snapshot.Presentation is not null)
        {
            kind = AssistantCanvasKind.PptOverlay;
            canvas = Services.Board;
            page = Services.Board.Current;
            pageIndex = Services.Board.CurrentIndex;
        }
        if (page is null && canvas is BoardService board && pageIndex >= 0 && pageIndex < board.Pages.Count)
            page = board.Pages[pageIndex];
        return new AssistantTarget
        {
            Canvas = canvas, Page = page, Kind = kind, PageIndex = pageIndex,
            SessionId = _aiSession?.SessionId ?? "",
        };
    }

    /// <summary>按已记录目标重建当前快照，用于过期判定；未记录目标时返回 null。</summary>
    private AssistantTarget SnapshotAssistantTarget()
    {
        if (_aiTarget is null) return null;
        BoardService board = _aiTarget.Kind switch
        {
            AssistantCanvasKind.SideBoard => Services.SideBoard,
            AssistantCanvasKind.MainBoard => Services.Board,
            AssistantCanvasKind.PptOverlay => Services.Board,
            _ => null,
        };
        int index = _aiTarget.Kind == AssistantCanvasKind.Document ? 0 : board?.CurrentIndex ?? 0;
        object page = _aiTarget.Kind switch
        {
            AssistantCanvasKind.Document => _documentWorkspace?.Controller,
            AssistantCanvasKind.PptOverlay => Services.Board.Current,
            _ => board is not null && index >= 0 && index < board.Pages.Count ? board.Pages[index] : null,
        };
        return new AssistantTarget
        {
            Canvas = board, Page = page, Kind = _aiTarget.Kind, PageIndex = index,
            SessionId = _aiSession?.SessionId ?? "",
        };
    }

    /// <summary>
    /// 目标校验。writeBack=true 用于真正写入画布（贴回/板书写入/替换），要求同板/页/课件/选区/代数；
    /// continueSession=true 用于在原会话上继续（变式预览、再讲追问、教学模式切换、局部纠错、板书请求），
    /// 只要求同板/页/课件/代数，不约束选区；两者都不传时为流式显示校验。
    /// Flag 开启且已记录统一上下文时走 <see cref="ClassroomContextPolicy"/>；
    /// 否则沿用旧 <see cref="AssistantTargetPolicy"/> 逻辑（不再回写 requested.SessionId，
    /// 会话身份统一由当前快照提供，避免把过期会话伪装成当前会话）。
    /// 失败一律写日志，写回失败、会话继续失败与跨板失败额外用浮窗提示用户。
    /// </summary>
    private bool AiTargetStillCurrent(bool writeBack = false, bool continueSession = false)
    {
        if (UnifiedContextEnabled && _aiContext is not null)
            return ClassroomContextStillCurrent(writeBack, continueSession);

        AssistantTarget requested = _aiTarget;
        if (requested is null) return true; // 聚焦区域/课件回看等旧路径保持原行为
        AssistantTarget current = SnapshotAssistantTarget();
        bool sameCanvas = AssistantTargetPolicy.SameCanvas(requested, current);
        bool sameSession = AssistantTargetPolicy.SameSession(requested, current);
        bool ok = writeBack || continueSession
            ? AssistantTargetPolicy.CanWriteBack(requested, current)
            : sameCanvas && sameSession;
        if (ok) return true;

        string notice = AssistantTargetPolicy.StaleNotice(requested, current);
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 丢弃过期响应：writeBack={writeBack}，continueSession={continueSession}，"
            + $"requested=({AssistantTargetPolicy.Describe(requested)})，"
            + $"current=({AssistantTargetPolicy.Describe(current)})，notice={notice}",
            Core.Diagnostics.LogType.Info);
        if (writeBack || continueSession || !sameCanvas) _aiAssistant?.ShowNotice(notice);
        return false;
    }

    /// <summary>发起请求的目标画布窗口是否仍然存在（截图 await 期间子板/主展板可能已关闭）。</summary>
    private bool AiTargetCanvasAvailable()
    {
        if (_aiTarget is null) return true;
        return _aiTarget.Kind switch
        {
            AssistantCanvasKind.SideBoard => _sideBoard is not null,
            AssistantCanvasKind.MainBoard => _board is not null,
            AssistantCanvasKind.Document => _documentWorkspace is not null,
            AssistantCanvasKind.PptOverlay => _pptSession?.Snapshot?.IsSlideShowRunning == true,
            _ => true,
        };
    }

    /// <summary>本次 AI 请求是否锚定板中板。</summary>
    private bool AssistantBelongsToSideBoard
        => _aiAssistantFromSideBoard
           || (_aiTarget?.Kind == AssistantCanvasKind.SideBoard
               && ReferenceEquals(_aiTarget.Canvas, Services.SideBoard));

    /// <summary>收藏来源页：按目标板取页索引；overlay 维持旧语义（主展板未打开为 -1）。</summary>
    private int AiFavoriteBoardPage()
        => _aiTarget?.Kind switch
        {
            AssistantCanvasKind.SideBoard => Services.SideBoard.CurrentIndex,
            AssistantCanvasKind.MainBoard => Services.Board.CurrentIndex,
            _ => _board is null ? -1 : Services.Board.CurrentIndex,
        };

    /// <summary>
    /// 刷新浮窗的收藏来源：课件页 + 画板页 + 统一课堂上下文（会话/请求/画板/页标识）。
    /// 未记录上下文时降级为空值，收藏仍可进行，只是缺少 AI 关联。
    /// </summary>
    private void ApplyFavoriteContext(
        AssistantWindow window, string presentation, int slideId, int boardPage)
    {
        if (window is null) return;

        window.SetFavoriteSource(presentation, slideId, boardPage);
        ClassroomContext context = _aiContext;
        window.SetFavoriteContext(
            _aiSession?.SessionId ?? context?.SessionId ?? "",
            context?.RequestId ?? "",
            context?.BoardId ?? AiTargetBoardId(),
            context?.PageId ?? Guid.Empty,
            context?.GroupId ?? "",
            context?.ObjectIds);
    }

    /// <summary>贴回等写操作的目标板；overlay/未记录目标回退主展板，保持既有行为。</summary>
    private BoardService AiTargetBoard()
        => _aiTarget?.Kind == AssistantCanvasKind.SideBoard ? Services.SideBoard : Services.Board;

    /// <summary>供日志与提示使用的画板标识：Flag 开启时取统一上下文，否则按旧目标推断。</summary>
    private string AiTargetBoardId()
        => UnifiedContextEnabled && _aiContext is not null
            ? _aiContext.BoardId
            : _aiTarget?.Kind switch
            {
                AssistantCanvasKind.SideBoard => "side",
                AssistantCanvasKind.MainBoard => "main",
                AssistantCanvasKind.Document => "document",
                AssistantCanvasKind.PptOverlay => "ppt",
                _ => "overlay",
            };

    /// <summary>供日志与提示使用的画布种类：Flag 开启时取统一上下文，否则按旧目标推断。</summary>
    private BoardCanvasKind AiTargetContextKind()
        => UnifiedContextEnabled && _aiContext is not null
            ? _aiContext.Kind
            : _aiTarget?.Kind switch
            {
                AssistantCanvasKind.SideBoard => BoardCanvasKind.SideBoard,
                AssistantCanvasKind.MainBoard => BoardCanvasKind.MainBoard,
                AssistantCanvasKind.Document => BoardCanvasKind.Document,
                AssistantCanvasKind.PptOverlay => BoardCanvasKind.PptOverlay,
                _ => BoardCanvasKind.Overlay,
            };

    /// <summary>目标画布窗口：子板优先，其次主展板，overlay 返回 null。</summary>
    private Window TargetCanvasWindow()
        => _aiTarget?.Kind switch
        {
            AssistantCanvasKind.SideBoard => _sideBoard,
            AssistantCanvasKind.MainBoard => _board,
            AssistantCanvasKind.Document => _documentWorkspace?.Window,
            AssistantCanvasKind.PptOverlay => null,
            _ => null,
        };

    /// <summary>
    /// AI 助教定位与截图的目标几何。板模式取板窗口所在显示器的工作区、DPI 与客户区左上
    /// 屏幕坐标（换算为 DIP）；overlay 模式保持主屏工作区、(0,0) 客户区原点与 overlay DPI，
    /// 不改变现有全屏 overlay 行为。
    /// </summary>
    private (ScreenRect Work, double Dpi, double OriginXDip, double OriginYDip) AssistantTarget()
    {
        Window canvasWindow = TargetCanvasWindow();
        if (canvasWindow is not null)
        {
            IntPtr hwnd = WindowChrome.GetHwnd(canvasWindow);
            if (hwnd != IntPtr.Zero)
            {
                ScreenInfo info = ScreenService.FromWindow(hwnd);
                if (info.WorkArea.Width > 0 && info.WorkArea.Height > 0)
                {
                    double boardDpi = info.DpiScale > 0 ? info.DpiScale : 1.0;
                    ScreenRect client = WindowChrome.GetClientBounds(hwnd);
                    return (info.WorkArea, boardDpi, client.X / boardDpi, client.Y / boardDpi);
                }
            }
        }

        return (ScreenService.WorkArea, CurrentDpi(), 0, 0);
    }

    private double CurrentDpi()
    {
        double dpi = Overlay.Window.DpiScale;
        return dpi > 0 ? dpi : 1.0;
    }

    private AssistantRect CenterAnchor()
    {
        var (work, dpi, originXDip, originYDip) = AssistantTarget();
        var workDip = new AssistantRect(work.X / dpi, work.Y / dpi,
            (work.X + work.Width) / dpi, (work.Y + work.Height) / dpi);
        // 锚点语义保持为客户区 DIP；板模式减去客户区原点，overlay 模式原点为 (0,0)。
        double centerX = workDip.CenterX - originXDip;
        double centerY = workDip.CenterY - originYDip;
        return new AssistantRect(centerX - 8, centerY - 8, centerX + 8, centerY + 8);
    }

    public void Exit() => _ = ExitAsync(restart: false);
    public void Restart() => _ = ExitAsync(restart: true);

    private static List<AssistantStroke> BuildAssistantStrokes(IReadOnlyList<InkStroke> selection, Matrix3x2 matrix)
    {
        var strokes = new List<AssistantStroke>(selection.Count);
        foreach (InkStroke stroke in selection)
        {
            var points = new List<AssistantPoint>(stroke.Points.Count);
            foreach (InkPoint point in stroke.Points)
            {
                Vector2 view = Vector2.Transform(new Vector2(point.X, point.Y), matrix);
                points.Add(new AssistantPoint
                {
                    X = view.X,
                    Y = view.Y,
                    Pressure = point.Pressure,
                    TimeMs = point.TimeMs,
                });
            }

            strokes.Add(new AssistantStroke
            {
                BaseWidth = stroke.Attributes.BaseWidth,
                ColorArgb = stroke.Attributes.EffectiveColor.PackedArgb,
                Points = points,
            });
        }

        return strokes;
    }

    private static List<string> BuildPasteLines(AssistantSession session)
    {
        var lines = new List<string>();
        string text = (session.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) lines.Add(line);
        }

        int number = 1;
        foreach (AssistantStep step in session.Steps)
        {
            string detail = step.Text ?? "";
            string latex = AssistantText.LatexToPlain(step.Latex ?? "");
            if (!string.IsNullOrEmpty(latex))
                detail = string.IsNullOrEmpty(detail) ? latex : detail + " / " + latex;
            if (string.IsNullOrEmpty(detail)) continue;

            int index = step.Index > 0 ? step.Index : number;
            lines.Add(index + ". " + detail);
            number = index + 1;
        }

        if (!string.IsNullOrEmpty(session.Formula))
            lines.Add(AssistantText.LatexToPlain(session.Formula));

        if (lines.Count == 0) lines.Add("AI 助教暂无返回内容");
        return lines;
    }

    /// <summary>贴回卡片的可编辑源数据：公式优先，其次步骤，再看图形批注。</summary>
    private static AiPasteMetadata BuildPasteMetadata(AssistantSession session)
    {
        var metadata = new AiPasteMetadata
        {
            Kind = DerivePasteKind(session),
            Latex = session.Formula ?? "",
            Text = session.Text ?? "",
            Lines = BuildPasteLines(session),
        };

        foreach (var annotation in session.Annotations ?? new List<IReadOnlyList<IReadOnlyList<double>>>())
        {
            var points = new List<double>();
            foreach (var element in annotation ?? Array.Empty<IReadOnlyList<double>>())
            {
                if (element is null) continue;
                foreach (double value in element) points.Add(value);
            }

            if (points.Count > 0) metadata.Annotations.Add(points);
        }

        return metadata;
    }

    private static AiPasteKind DerivePasteKind(AssistantSession session)
    {
        if (!string.IsNullOrEmpty(session.Formula)) return AiPasteKind.Formula;
        if (session.Steps is { Count: > 0 }) return AiPasteKind.Steps;
        if (session.Annotations is { Count: > 0 }) return AiPasteKind.Shape;
        return AiPasteKind.Steps;
    }

    private async Task ExitAsync(bool restart)
    {
        if (_exiting || _exitPromptOpen) return;
        _exitPromptOpen = true;
        try
        {
            if (Services.Settings.Advanced.IsSecondConfimeWhenShutdownApp && !_installingUpdate
                && !await ConfirmAsync(restart ? "重启 Ink Canvas" : "退出 Ink Canvas", restart ? "确认保存设置并重启 Ink Canvas？" : "确认关闭 Ink Canvas？")) return;
            if (Services.Settings.Archive.ScreenCapture.SaveOnExit)
            {
                await SaveScreenArchiveAsync(restart ? "重启前屏幕存档" : "退出前屏幕存档");
                await WaitForScreenArchivesAsync();
                if (Services.ScreenArchives.LastError is { } captureError && Services.ScreenArchives.IsEnabled(_lastSlideShowState)
                    && Services.OverlayInk.Document.Count > 0 && _board is null)
                { await NotifyAsync("截图白板存档失败", captureError); return; }
            }
            if (_documentWorkspace is not null && !await _documentWorkspace.CloseAsync()) return;
            if (Services.Settings.Archive.SaveOnExit)
            {
                try
                {
                    if (_board is not null) await _board.SaveArchiveBeforeCloseAsync(restart ? "重启程序" : "退出程序");
                    else await Services.Archives.SaveAutomaticAsync(restart ? "重启程序" : "退出程序");
                }
                catch (Exception ex)
                {
                    await NotifyAsync("白板存档失败", "白板内容已保留，请检查保存设置后再退出。\n" + ex.GetBaseException().Message);
                    return;
                }
            }
            if (restart)
            {
                Services.SaveSettings();
                try
                {
                    if (Services.ExtensionInstalls.Pending.Count > 0) { using var helper = Features.Update.UpdateService.LaunchRestart(); }
                    else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath, "-m") { UseShellExecute = true });
                }
                catch (Exception ex) { await NotifyAsync("重启失败", ex.Message); return; }
            }
            _exiting = true;
            _pollTimer.Stop();
            _pptRetryTimer?.Stop();

            if (Services.Settings.Automation.IsAutoSaveStrokesAtClear)
                await Services.AutoSave.SaveSnapshotAsync(Services.OverlayInk.Document.Strokes, "exit");
            await Services.AutoSave.WaitForPendingAsync();
            await Services.Archives.WaitForPendingAsync();
            await WaitForScreenArchivesAsync();
            Services.SaveSettings();

            Dispose();
            Application.Current.Exit();
        }
        finally { _exitPromptOpen = false; }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        CloseSideBoardForExit();
        _pptPreviewSource?.Dispose();
        StopRecording();
        ResetAiPreStage();
        _aiStream?.Abort();
        _aiRegionStream?.Abort();
        _aiAssistant?.Close();
        _board?.CloseForShutdown();
        _cachedBoard?.CloseForShutdown();
        _cachedBoard = null;
        _documentAi?.Dispose();
        _spotlight?.Close();
        _magnifier?.Close();
        _roster?.Close();
        _toolkit?.Close();
        _groupScoreWindow?.Close();
        _exampleLibrary?.Close();
        _resourceBundle?.Close();
        _presetWindow?.Close();
        _slideNav?.Close();
        CloseBlackScreen();
        _pptSession.InkTransition -= OnPptInkTransition;
        StopShortcutTracking();
        Overlay.InteractionChanged -= RefreshShortcutRegistration;
        StopDocumentForegroundTracking();
        foreach (var notice in _notices.ToArray()) notice.Close();
        _updateWindow?.Close();
        foreach (var palette in _detachedPalettes.ToArray()) palette.Close();
        _imageImportCancellation?.Cancel();
        _videoImportCancellation?.Cancel();
        Services.Plugins.Changed -= OnExtensionCommandsChanged;
        Services.Modules.Changed -= OnExtensionCommandsChanged;
        Services.SettingsApplied -= ApplyDocumentSettings;
        Services.SettingsApplied -= RefreshIconFonts;
        Services.SettingsApplied -= ApplyArchivePaths;
        Services.SettingsApplied -= ApplyDisplaySettings;
        Services.SettingsApplied -= ApplySideBoardDisplaySettings;
        Services.SettingsApplied -= RefreshPaletteNibModes;
        Services.Plugins.Dispose();
        Services.Modules.Dispose();
        Services.OverlayInk.Invalidated -= QueueDesktopSelection;
        Services.OverlayInk.Invalidated -= QueueDesktopMedia;
        Services.ScreenImages.ImagesChanged -= QueueDesktopMedia;
        Services.ScreenImages.PageChanged -= QueueDesktopMedia;
        Services.ScreenImages.MediaPlacementChanged -= OnDesktopMediaPlacement;
        foreach (var video in _desktopVideos.Values) video.Close();
        _desktopVideos.Clear();
        Services.OverlayInk.SelectionChanged -= QueueDesktopSelection;
        if (_bar is not null) _bar.AppWindow.Changed -= OnBarWindowChanged;
        if (_bar is not null) _bar.Bar.LayoutChanged -= OnBarLayoutChanged;
        _selectionToolbar?.Close();
        _selectionToolbar = null;
        _replay?.Close();
        _pollTimer.Stop();
        _countdown?.Dispose();
        ClosePptNavigationWindows();
        _pptSession.Dispose();
        Services.Ppt.RefreshRequested -= QueuePresentationRefresh;
        Services.Ppt.Dispose();
        Updater.Dispose();
        Overlay.Window.DisplaySettingsChanged -= QueueDisplayRefresh;
        Overlay.Dispose();
        Services.Dispose();
    }
}
