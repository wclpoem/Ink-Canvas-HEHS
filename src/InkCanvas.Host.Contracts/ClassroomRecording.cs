using InkCanvas.Features.Board;
using InkCanvas.Features.Ppt;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

/// <summary>
/// 时光机插件私有课堂录制文件的格式标记。文件扩展名仍为 .icink（ZIP 容器），
/// 本体凭 <see cref="ManifestEntry"/> 内的 Format 字段识别；未安装/未启用插件时本体拒绝读取并给出中文提示。
/// 该契约已冻结：宿主与插件两侧均不得更改常量值。
/// </summary>
public static class ClassroomRecordingFormat
{
    public const string Format = "inkcanvas-classroom";
    public const int Version = 1;
    public const string ManifestEntry = "classroom.json";
    public const string Extension = ".icink";
}

/// <summary>课堂工具信号：宿主在倒计时/点名/练习收尾等既有打点处触发，插件据此生成时间轴事件与章节书签。</summary>
public enum ClassroomSignalKind { TimerStarted, TimerStopped, TimerEnded, RosterDrawn, PracticeFinished }

public sealed record ClassroomSignal(ClassroomSignalKind Kind, string Label = null);

/// <summary>宿主向时光机录制模块暴露的能力面（进程内受信模块模型）。</summary>
public interface ITimeMachineHost
{
    Window Owner { get; }
    int Theme { get; }
    BoardService Board { get; }
    InkController ActiveController { get; }
    bool BoardOpen { get; }
    PptInkSession Ppt { get; }
    string DataDirectory { get; }
    event Action ActiveControllerChanged;
    event Action BoardOpenChanged;
    event Action<ClassroomSignal> ClassroomSignal;
    /// <summary>截取当前课堂可视区域并保存为 PNG，返回文件绝对路径。</summary>
    Task<string> CaptureScreenshotAsync();
    Task NotifyAsync(string title, string message);
    void ConfigureWindow(Window window, int width = 1120, int height = 820);
}

/// <summary>时光机模块导出的服务；宿主据此接线主工具栏命令与新格式文件路由。不得提供交互式回放窗口。</summary>
public interface ITimeMachineService
{
    bool IsRecording { get; }
    event Action RecordingStateChanged;
    void StartRecording();
    void StopRecording();
    /// <summary>打开录制管理窗口：录制列表、章节书签查看、MP4 导出设置。</summary>
    Task OpenRecordingsAsync();
    /// <summary>打开一个 inkcanvas-classroom 格式的 .icink 录制文件（宿主已完成格式探测与插件在位校验）。</summary>
    Task OpenFileAsync(string path);
}