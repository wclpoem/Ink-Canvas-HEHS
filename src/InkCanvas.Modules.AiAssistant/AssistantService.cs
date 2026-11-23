using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Host.Contracts;
using InkCanvas.Modules.AiAssistant.Providers;
using InkCanvas.Platform.System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.AiAssistant;

internal sealed partial class AssistantService : IAiAssistantService, IAiConnectionSettings, IDisposable
{
    private const string StorageId = "inkcanvas.ai-assistant";
    private readonly IAiAssistantHost _host;
    private readonly ICanvasToolsHost _canvasHost;
    private readonly AiConnectionStore _connections;
    private readonly string _preferencesPath;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly AiProviderClient _client = new();
    private AssistantPreferences _preferences = new();
    private AssistantWorkspace _workspace;
    private AssistantWindow _window;
    private CancellationTokenSource _cancellation;
    private AssistantChatTurn _activeTurn;
    private CanvasToolTarget _activeTarget;
    private (int Id, int Index, int Animation) _lastSlide;
    private Action _flushActiveTurn;
    private bool _disposed, _dirty, _executingTool;
    private string _loadError;
    private int _generation;

    public AssistantService(IAiAssistantHost host, ICanvasToolsHost canvasHost, AiConnectionStore connections, string directory)
    {
        _host = host; _canvasHost = canvasHost; _connections = connections;
        _preferencesPath = Path.Combine(directory, "assistant.json");
        try { if (File.Exists(_preferencesPath)) _preferences = AssistantPreferences.Read(File.ReadAllBytes(_preferencesPath)); }
        catch (Exception exception) { Core.Diagnostics.FileLog.WriteLogToFile("助手设置读取失败 " + exception.Message); }
        _saveTimer = _dispatcher.CreateTimer(); _saveTimer.Interval = TimeSpan.FromMilliseconds(750); _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Flush();
        _host.Board.DocumentChanged += OnDocumentChanged;
        _host.Board.Snapshotting += Flush;
        _canvasHost.TargetChanged += OnTargetChanged;
    }

    public bool IsOpen => _window is not null;
    public FrameworkElement CreateSettingsView() => _connections is null
        ? new TextBlock { Text = "AI 连接配置读取失败，请检查配置文件后重新启动。", TextWrapping = TextWrapping.Wrap }
        : new AiConnectionSettingsView(_connections);

    public FrameworkElement CreateBehaviorSettingsView()
    {
        var view = new StackPanel { Spacing = 12 };
        var tools = new ToggleSwitch { Header = "允许工具调用", OnContent = "开启", OffContent = "关闭", IsOn = _preferences.ToolsEnabled };
        var budget = new NumberBox { Header = "模型上下文窗口（Token，默认 256k）", Minimum = 8192, Maximum = 1048576, Value = _preferences.ContextTokens,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var threshold = new NumberBox { Header = "自动压缩触发比例（%）", Minimum = 50, Maximum = 95, Value = _preferences.CompactAtPercent,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var instructions = new TextBox { Header = "教师偏好与补充指令", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 100, MaxHeight = 240, Text = _preferences.Instructions };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Save()
        {
            try
            {
                _preferences.ToolsEnabled = tools.IsOn;
                _preferences.ContextTokens = double.IsFinite(budget.Value) ? Math.Clamp((int)budget.Value, 8192, 1048576) : AssistantPreferences.DefaultContextTokens;
                _preferences.CompactAtPercent = double.IsFinite(threshold.Value) ? Math.Clamp((int)threshold.Value, 50, 95) : 80;
                _preferences.Instructions = instructions.Text;
                Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
                string temporary = _preferencesPath + ".tmp";
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_preferences));
                File.Move(temporary, _preferencesPath, true); error.Text = "";
            }
            catch (Exception exception) { error.Text = exception.Message; }
        }
        tools.Toggled += (_, _) => { if (!tools.IsOn) CancelActive(); Save(); };
        budget.ValueChanged += (_, _) => Save(); threshold.ValueChanged += (_, _) => Save(); instructions.LostFocus += (_, _) => Save();
        view.Children.Add(tools); view.Children.Add(budget); view.Children.Add(threshold); view.Children.Add(instructions); view.Children.Add(error); return view;
    }

    public void Open()
    {
        if (_disposed) return;
        if (_window is not null) { _window.ShowPanel(); return; }
        EnsureWorkspace();
        var window = new AssistantWindow(_host.Settings.Appearance.Theme); _window = window;
        window.SetWorkspace(_workspace);
        window.SetModel(_connections?.Active is { } connection ? connection.Name + " · " + connection.Model : "请在插件设置中配置 AI 连接");
        window.Changed += MarkDirty; window.StopRequested += CancelActive; window.ConversationChanging += CancelActive;
        window.SendRequested += chat => _ = SendAsync(chat);
        window.CaptureRequested += () => _ = CaptureAsync();
        window.RetryRequested += id =>
        {
            var chat = _workspace.Active; var turn = chat?.Turns.LastOrDefault();
            if (turn?.Id != id || _cancellation is not null || turn.Tools.Count > 0) return;
            chat.Draft = turn.Question; chat.DraftAttachments = turn.Attachments.ToList(); chat.Turns.RemoveAt(chat.Turns.Count - 1);
            chat.SummarizedTurns = Math.Min(chat.SummarizedTurns, chat.Turns.Count);
            window.SetWorkspace(_workspace); _ = SendAsync(chat);
        };
        window.Closed += () => { CancelActive(); Flush(); if (ReferenceEquals(_window, window)) _window = null; };
        _host.ConfigureWindow(window); window.UseNativeFrame(); window.Activate();
        if (!string.IsNullOrEmpty(_loadError)) window.ReportError(_loadError);
    }

    private void EnsureWorkspace()
    {
        if (_workspace is not null) return;
        try { _workspace = AssistantWorkspace.Load(_host.Board.ReadExtension(StorageId)); _loadError = null; }
        catch (Exception exception) { _workspace = new(); _loadError = "无法读取此文件的对话记录，原始记录已保留。" + exception.Message; }
    }
    private void MarkDirty() { _dirty = true; if (!_saveTimer.IsRunning) _saveTimer.Start(); }
    private void Flush()
    {
        _flushActiveTurn?.Invoke();
        _saveTimer.Stop();
        if (!_dirty || _workspace is null || !string.IsNullOrEmpty(_loadError)) return;
        _host.Board.WriteExtension(StorageId, _workspace.Save()); _dirty = false;
    }
    private void OnDocumentChanged()
    {
        CancelActive(false); _saveTimer.Stop(); _dirty = false; _workspace = null;
        EnsureWorkspace(); _window?.SetWorkspace(_workspace); _window?.ReportError(_loadError);
    }
    private void OnTargetChanged()
    {
        if (_executingTool || _cancellation is null || _activeTarget is not null && _canvasHost.IsCurrent(_activeTarget)) return;
        CancelActive();
    }
    public void NotifyBoardChanged() => OnTargetChanged();
    public void NotifySlideChanged(int slideId, int slideIndex, int animationStep)
    {
        var source = (slideId, slideIndex, animationStep);
        if (source == _lastSlide) return;
        _lastSlide = source;
        if (!_executingTool && _activeTarget?.Kind == "screen") CancelActive();
    }
    private void CancelActive() => CancelActive(true);
    private void CancelActive(bool persist)
    {
        _flushActiveTurn?.Invoke(); _flushActiveTurn = null;
        _generation++; _cancellation?.Cancel(); _cancellation = null;
        if (_activeTurn is { State: "running" } turn) { turn.State = "stopped"; turn.Activity = ""; turn.Refresh(); }
        _activeTurn = null; _activeTarget = null; _window?.SetBusy(false);
        if (persist && _workspace is not null) { MarkDirty(); Flush(); }
    }

    public void Review(int slideId, int slideIndex, int animationStep, string text)
    {
        Open(); CancelActive();
        var chat = _workspace.CreateChat(); chat.Title = "课件记录";
        chat.Turns.Add(new() { Answer = text ?? "", State = "done" }); _window.SetWorkspace(_workspace); MarkDirty();
    }
    public async Task OpenRegionAsync(ScreenRect rect)
    {
        Open(); var chat = _workspace.Active; int generation = _generation;
        try
        {
            string image = await Features.AiAssistant.AssistantCapture.CapturePngBase64Async(rect.X, rect.Y, rect.Width, rect.Height);
            var attachment = await AssistantAttachments.FromImageAsync(Convert.FromBase64String(image), "选区截图");
            if (_disposed || generation != _generation || !ReferenceEquals(chat, _workspace.Active)) return;
            _window?.AddAttachment(attachment);
        }
        catch (Exception exception) { _window?.ReportError("截图失败。" + exception.Message); }
    }
    private async Task CaptureAsync()
    {
        var chat = _workspace.Active;
        try
        {
            var png = await Features.Board.BoardPreview.RenderAsync(_host.Board);
            var attachment = await AssistantAttachments.FromImageAsync(png, "白板第 " + (_host.Board.CurrentIndex + 1) + " 页");
            if (!_disposed && ReferenceEquals(chat, _workspace.Active)) _window?.AddAttachment(attachment);
        }
        catch (Exception exception) { _window?.ReportError("获取画布失败。" + exception.Message); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        CancelActive(); Flush(); _disposed = true;
        _host.Board.DocumentChanged -= OnDocumentChanged; _host.Board.Snapshotting -= Flush;
        _canvasHost.TargetChanged -= OnTargetChanged; _saveTimer.Stop();
        _window?.Close(); _window = null; _client.Dispose();
    }
}
