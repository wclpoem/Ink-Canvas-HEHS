using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>
/// 时光机插件服务：在进程内管理录制生命周期、新格式存储与录制管理窗口。
/// 对宿主的依赖仅经 <see cref="ITimeMachineHost"/> 契约，不触碰宿主内部类型。
/// </summary>
public sealed partial class TimeMachineService : ITimeMachineService, IDisposable
{
    private readonly ITimeMachineHost _host;
    private readonly Func<Action, Action> _protect;
    private TimeMachineRecorder _recorder;
    private RecordingsWindow _window;
    private bool _disposed;

    public TimeMachineService(ITimeMachineHost host, Func<Action, Action> protect)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _protect = protect ?? (Func<Action, Action>)(a => a);
    }

    public bool IsRecording => _recorder is { IsRecording: true };
    internal string LastRecordingSaveError { get; private set; }

    public event Action RecordingStateChanged;

    public void StartRecording()
    {
        if (_disposed || IsRecording) return;

        _recorder = new TimeMachineRecorder(_host);
        _recorder.BoardClosed += _protect(OnBoardClosed);
        _recorder.Start();
        RaiseState();
    }

    public void StopRecording()
    {
        if (_disposed || _recorder is null) return;

        TimeMachineRecorder recorder = _recorder;
        LastRecordingSaveError = null;
        _recorder = null;
        ClassroomSession session = recorder.Stop();
        recorder.Dispose();

        if (session is not null && session.Events.Count > 0)
        {
            try
            {
                RecordingPackage.Save(session, RecordingsDirectory());
            }
            catch (Exception error)
            {
                LastRecordingSaveError = error.Message;
            }
        }

        RaiseState();
    }

    public Task OpenRecordingsAsync()
    {
        if (_disposed) return Task.CompletedTask;
        ShowWindow(initial: null, session: null);
        return Task.CompletedTask;
    }

    public async Task OpenFileAsync(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;

        string extractDir = Path.Combine(RecordingsDirectory(), ".cache",
            Path.GetFileNameWithoutExtension(path));
        ClassroomSession session = await Task.Run(() => RecordingPackage.Load(path, extractDir));
        ShowWindow(initial: null, session);
    }

    // ---- 供管理窗口调用 ---------------------------------------------------

    public string RecordingsDirectory() => RecordingPackage.DefaultDirectory(_host.DataDirectory);

    public IReadOnlyList<RecordingInfo> ListRecordings() => RecordingPackage.List(RecordingsDirectory());

    public ClassroomSession OpenSession(RecordingInfo info)
    {
        if (info is null) return null;
        string extractDir = Path.Combine(RecordingsDirectory(), ".cache", info.Id);
        return RecordingPackage.Load(info.FilePath, extractDir);
    }

    public bool RenameRecording(RecordingInfo info, string newName)
        => info is not null && RecordingPackage.Rename(info.FilePath, newName);

    public bool DeleteRecording(RecordingInfo info)
        => info is not null && RecordingPackage.Delete(info.FilePath);

    public void CopyRecording(RecordingInfo info, string targetPath)
    {
        if (info is null) return;
        RecordingPackage.CopyTo(info.FilePath, targetPath);
    }

    public Task<ExportResult> ExportAsync(ClassroomSession session, ExportOptions options,
        IProgress<ExportProgress> progress, CancellationToken ct)
        => TimeMachineExporter.ExportWithResultAsync(session, options, progress, ct);

    public ITimeMachineHost Host => _host;

    public void Dispose()
    {
        if (_disposed) return;
        try { StopRecording(); } catch { }
        _disposed = true;
        _window?.Close();
        _window = null;
    }

    // ---- 内部 -------------------------------------------------------------

    private void OnBoardClosed()
    {
        StopRecording();
        _ = _host.NotifyAsync("时光机", "画板已关闭，本次录制已自动保存。");
    }

    private void ShowWindow(RecordingInfo initial, ClassroomSession session)
    {
        if (_window is { IsClosed: false })
        {
            _window.Activate();
            return;
        }

        _window = new RecordingsWindow(_host, this, initial, session);
        _host.ConfigureWindow(_window, 1120, 820);
        _window.Closed += (_, _) => _window = null;
        _window.Activate();
    }

    private void RaiseState() => RecordingStateChanged?.Invoke();
}
