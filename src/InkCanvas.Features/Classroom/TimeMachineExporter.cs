using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Classroom;

/// <summary>导出阶段，用于 UI 区分「录制音频 / 编码音频 / 编码视频」。</summary>
public enum ExportStage
{
    Preparing,
    RecordingAudio,
    EncodingAudio,
    EncodingVideo,
    Completed,
}

/// <summary>带阶段说明的导出进度（0–1）。</summary>
public sealed record ExportProgress(ExportStage Stage, double Value, string Message);

/// <summary>
/// 视频导出参数。<see cref="StartMs"/> / <see cref="EndMs"/> 为相对录制开始的时间，
/// <see cref="Speed"/> 为回放倍速（输出时长 = 区间长度 / Speed），<see cref="BoardOnly"/>
/// 为 true 时跳过 PPT 截图（只保留背景 / 网格 / 笔画）。
/// </summary>
public sealed record ExportOptions(
    string OutputPath,
    long StartMs,
    long EndMs,
    int Fps = 30,
    int BitRate = 4_000_000,
    bool IncludeAudio = false,
    int Width = 1920,
    int Height = 1080,
    double Speed = 1.0,
    bool BoardOnly = false,
    bool ShowCursor = false,
    bool ShowChapterInfo = false);

/// <summary>导出结果（含音频状态说明，避免把「只有视频」静默当成成功）。</summary>
public sealed record ExportResult(
    string OutputPath,
    int FrameCount,
    bool HasAudio,
    string AudioNote,
    string AudioPath = null,
    string Encoder = "Media Foundation H.264");

/// <summary>
/// 把板书会话导出为 MP4：按帧率采样 <see cref="InkTimeline"/>，用 Win2D 离屏渲染，
/// 再通过 Media Foundation（<c>IMFSinkWriter</c>）编码 H.264；可选把 WASAPI loopback
/// 录制的讲解音频编码为 AAC 合并进同一 MP4。
/// <para>
/// 音频为可选：勾选 <see cref="ExportOptions.IncludeAudio"/> 时先在后台实时录制系统声音，
/// 成功后写入 AAC 音频流。录制 / AAC 初始化失败会降级为仅视频，并在
/// <see cref="ExportResult.AudioNote"/> 写明中文原因，而不是静默忽略。
/// </para>
/// <para>
/// 编码在后台线程执行；取消通过 <see cref="CancellationToken"/>。失败抛出带中文说明的异常
/// （主线程不崩溃），并删除 MP4 / WAV 半成品。
/// </para>
/// </summary>
public static class TimeMachineExporter
{
    public const int FrameWidth = 1920;
    public const int FrameHeight = 1080;
    public const int AudioSampleRate = WasapiAudioRecorder.SampleRate;
    public const int AudioChannels = WasapiAudioRecorder.Channels;
    public const int AudioBitRate = 128_000;

    /// <summary>校验导出参数；不合法时抛出中文 <see cref="ArgumentException"/>。</summary>
    public static void Validate(ExportOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options), "导出参数不能为空。");

        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new ArgumentException("导出路径不能为空。", nameof(options));

        string extension = Path.GetExtension(options.OutputPath);
        if (!string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件必须使用 .mp4 扩展名。", nameof(options));

        if (options.StartMs < 0)
            throw new ArgumentException("导出的起始时间不能为负。", nameof(options));

        if (options.EndMs <= options.StartMs)
            throw new ArgumentException("导出的结束时间必须大于起始时间。", nameof(options));

        if (options.Fps <= 0 || options.Fps > 120)
            throw new ArgumentException("帧率必须在 1–120 之间。", nameof(options));

        if (options.BitRate < 100_000 || options.BitRate > 100_000_000)
            throw new ArgumentException("码率必须在 100 kbps–100 Mbps 之间。", nameof(options));

        if (options.Width < 320 || options.Width > 3840)
            throw new ArgumentException("导出宽度必须在 320–3840 之间。", nameof(options));

        if (options.Height < 320 || options.Height > 3840)
            throw new ArgumentException("导出高度必须在 320–3840 之间。", nameof(options));

        if (!double.IsFinite(options.Speed) || options.Speed < 0.5 || options.Speed > 8)
            throw new ArgumentException("导出倍速必须在 0.5–8 之间。", nameof(options));

        double seconds = EffectiveDurationMs(options) / 1000.0;
        if (seconds <= 0)
            throw new ArgumentException("导出的结束时间必须大于起始时间。", nameof(options));

        if (seconds * options.Fps > 1_000_000)
            throw new ArgumentException("导出时长过长（超过 100 万帧），请缩短时间段或降低帧率。", nameof(options));
    }

    /// <summary>原始会话区间时长（毫秒）：<c>EndMs - StartMs</c>，不受倍速影响。</summary>
    public static long SourceDurationMs(ExportOptions options)
        => options is null ? 0 : Math.Max(0, options.EndMs - options.StartMs);

    /// <summary>倍速换算后的输出时长（毫秒）：<c>(EndMs - StartMs) / Speed</c>。</summary>
    public static double EffectiveDurationMs(ExportOptions options)
        => (options.EndMs - options.StartMs) / Math.Max(0.5, Math.Min(8, options.Speed));

    /// <summary>预计输出帧数：<c>ceil(输出时长 × Fps / 1000)</c>；倍速越高帧数越少，至少 1 帧。</summary>
    public static int EstimateFrameCount(ExportOptions options)
    {
        Validate(options);
        double frames = EffectiveDurationMs(options) * options.Fps / 1000.0;
        if (frames < 1) return 1;
        if (frames > 1_000_000) throw new ArgumentException(
            "导出时长过长（超过 100 万帧），请缩短时间段或降低帧率。", nameof(options));
        return (int)Math.Ceiling(frames);
    }

    /// <summary>
    /// 生成每一帧对应的时刻（毫秒，递增且不超过 <see cref="ExportOptions.EndMs"/>）。
    /// 帧 i 的时间为 <c>StartMs + i * (1000/Fps) * Speed</c>，与导出倍速一致。纯函数，可单独测试。
    /// </summary>
    public static IReadOnlyList<long> SampleFrameTimes(ExportOptions options)
    {
        Validate(options);

        int count = EstimateFrameCount(options);
        double stepMs = (1000.0 / options.Fps) * options.Speed;

        var times = new List<long>(count);
        for (int i = 0; i < count; i++)
        {
            long offset = (long)Math.Round(i * stepMs, MidpointRounding.AwayFromZero);
            long time = options.StartMs + offset;
            if (time > options.EndMs) break;
            times.Add(time);
        }
        if (times.Count == 0) times.Add(options.StartMs);
        return times;
    }

    /// <summary>
    /// 讲解音频应与输出视频等长：按所选时间段 / 倍速计算 48 kHz PCM 帧数（纯函数，可测试）。
    /// </summary>
    public static long AudioSampleFrames(ExportOptions options, int sampleRate = AudioSampleRate)
    {
        Validate(options);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        return Pcm16Wav.SampleFrames(
            TimeSpan.FromMilliseconds(EffectiveDurationMs(options)), sampleRate);
    }

    /// <summary>BoardOnly 时跳过 PPT 截图；返回 true 表示需要渲染 PPT 背景。</summary>
    public static bool ShouldDrawPptBackground(ExportOptions options)
        => options is not null && !options.BoardOnly;

    /// <summary>
    /// 估算导出占用的磁盘字节数：<c>帧数 × 宽 × 高 × 4</c>，含音频时再加上
    /// <c>采样率 × 输出时长（秒）× 4</c>（48 kHz / 2 声道 / 16-bit 原始 PCM）。
    /// </summary>
    public static long EstimateRequiredBytes(ExportOptions options)
    {
        Validate(options);

        long frames = EstimateFrameCount(options);
        long video = frames * (long)options.Width * options.Height * 4;

        long audio = 0;
        if (options.IncludeAudio)
        {
            // 录制按原始时长进行，倍速 > 1 时会先录下更长的 WAV 再变速；
            // 倍速 < 1 时变速后的输出反而更长，取两者较大值估算峰值占用。
            double outputSeconds = EffectiveDurationMs(options) / 1000.0;
            double recordedSeconds = SourceDurationMs(options) / 1000.0;
            double seconds = Math.Max(outputSeconds, recordedSeconds);
            audio = (long)Math.Ceiling(AudioSampleRate * seconds * 4);
        }
        return video + audio;
    }

    /// <summary>
    /// 按可用空间校验导出所需磁盘；不足预估的 1.2 倍时抛出中文 <see cref="InvalidOperationException"/>，
    /// 调用方应在启动编码前调用，避免浪费录制 / 渲染时间。
    /// </summary>
    public static void EnsureFreeDiskSpace(long availableBytes, ExportOptions options)
    {
        long required = EstimateRequiredBytes(options);
        double needed = required * 1.2;
        if (availableBytes < needed)
        {
            double needMb = needed / (1024.0 * 1024.0);
            double freeMb = Math.Max(0, availableBytes) / (1024.0 * 1024.0);
            throw new InvalidOperationException(
                $"磁盘可用空间不足：预计需要 {needMb:0.0} MB（含 20% 余量），当前仅 {freeMb:0.0} MB。" +
                "请清理磁盘、选择其它位置或缩短导出时间段。");
        }
    }

    /// <summary>读取输出目录所在卷的可用空间并校验；无法读取时跳过检查（不阻断导出）。</summary>
    public static void EnsureDiskSpace(ExportOptions options)
    {
        Validate(options);
        string fullPath = Path.GetFullPath(options.OutputPath);
        string directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string root;
        try
        {
            root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return;
            long available = new DriveInfo(root).AvailableFreeSpace;
            EnsureFreeDiskSpace(available, options);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // 驱动器不存在 / 权限受限：无法判断时保持原有行为，交由编码阶段报错。
        }
    }

    /// <summary>
    /// 清理导出失败 / 中断留下的半成品（同名 <c>.tmp</c> / <c>.part</c>）。
    /// 成功的 MP4（以及作为结果的 WAV）不会被删除，便于调用方在成功后也安全调用。
    /// </summary>
    public static void CleanupArtifacts(ExportOptions options)
    {
        if (options is null || string.IsNullOrWhiteSpace(options.OutputPath)) return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(options.OutputPath);
        }
        catch
        {
            return;
        }

        TryDelete(fullPath + ".tmp");
        TryDelete(fullPath + ".part");
    }


    /// <summary>导出 MP4；返回结果以便调用方提示音频状态。</summary>
    public static Task<ExportResult> ExportWithResultAsync(
        ClassroomSession session, ExportOptions options,
        IProgress<double> progress, CancellationToken ct)
        => ExportWithResultAsync(session, options, progress, ct, null);

    /// <summary>导出 MP4（可注入音频录制器，便于测试合成 PCM 的合并路径）。</summary>
    public static Task<ExportResult> ExportWithResultAsync(
        ClassroomSession session, ExportOptions options,
        IProgress<double> progress, CancellationToken ct, WasapiAudioRecorder audioRecorder)
        => ExportWithResultAsync(session, options,
            progress is null ? null : new DoubleProgressAdapter(progress), ct, audioRecorder);

    /// <summary>导出 MP4；进度包含「录制音频 / 编码音频 / 编码视频」阶段。</summary>
    public static Task<ExportResult> ExportWithResultAsync(
        ClassroomSession session, ExportOptions options,
        IProgress<ExportProgress> progress, CancellationToken ct)
        => ExportWithResultAsync(session, options, progress, ct, null);

    /// <summary>导出 MP4；进度包含阶段，可注入音频录制器。</summary>
    public static async Task<ExportResult> ExportWithResultAsync(
        ClassroomSession session, ExportOptions options,
        IProgress<ExportProgress> progress, CancellationToken ct, WasapiAudioRecorder audioRecorder)
    {
        if (session is null) throw new ArgumentNullException(nameof(session), "会话不能为空。");
        Validate(options);

        return await Task.Run(
            () => ExportCore(session, options, progress, audioRecorder, ct), ct).ConfigureAwait(false);
    }

    /// <summary>导出 MP4（不返回音频说明，使用 <see cref="ExportWithResultAsync"/> 获取）。</summary>
    public static async Task ExportAsync(
        ClassroomSession session, ExportOptions options,
        IProgress<double> progress, CancellationToken ct)
    {
        await ExportWithResultAsync(session, options, progress, ct).ConfigureAwait(false);
    }

    private static ExportResult ExportCore(
        ClassroomSession session, ExportOptions options,
        IProgress<ExportProgress> progress, WasapiAudioRecorder audioRecorder, CancellationToken ct)
    {
        var times = SampleFrameTimes(options);
        if (times.Count == 0)
            throw new InvalidOperationException("视频导出失败：所选时间段没有可采样的帧。");

        string outputPath = Path.GetFullPath(options.OutputPath);
        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 磁盘空间不足时在录制 / 渲染之前就失败，避免留下半成品。
        EnsureDiskSpace(options);

        // 先删掉同名残留，避免编码失败时留下旧文件被误认为本次结果。
        try
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"视频导出失败：无法覆盖目标文件（{ex.Message}）。", ex);
        }

        string wavPath = null;
        bool hasAudio = false;
        string audioNote = "未包含音频。";

        if (options.IncludeAudio)
        {
            ct.ThrowIfCancellationRequested();
            WasapiAudioRecorder recorder = audioRecorder ?? new WasapiAudioRecorder();
            string tempWav = TempWavPath();
            long sourceMs = SourceDurationMs(options);

            Report(progress, ExportStage.RecordingAudio, 0,
                $"正在录制讲解音频（原始时长 {sourceMs / 1000.0:0.##}s，倍速 {options.Speed:0.##}×）…");
            try
            {
                wavPath = recorder.RecordAsync(
                    tempWav,
                    TimeSpan.FromMilliseconds(sourceMs),
                    new InlineProgress(value =>
                        Report(progress, ExportStage.RecordingAudio, value,
                            $"正在录制讲解音频… {value * 100:0}%")),
                    ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempWav);
                TryDelete(outputPath);
                throw;
            }

            if (wavPath is null)
            {
                hasAudio = false;
                audioNote = VideoOnlyNote(recorder.LastError);
            }
            else
            {
                hasAudio = true;
                audioNote = "已同步录制系统讲解音频。";

                // 音频按原始会话时长录制；倍速导出时在编码前把 WAV 变速到输出时长，
                // 从而与视频帧数 / fps 对齐（±1 帧）。
                if (Math.Abs(options.Speed - 1.0) > 1e-9)
                {
                    ct.ThrowIfCancellationRequested();
                    string resampled = TempWavPath();
                    try
                    {
                        WavResampler.ChangeSpeedToFile(wavPath, resampled, options.Speed);
                        TryDelete(wavPath);
                        wavPath = resampled;
                        audioNote =
                            $"已同步录制讲解音频（{options.Speed:0.##}× 变速，输出 {EffectiveDurationMs(options) / 1000.0:0.##}s）。";
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(resampled);
                        TryDelete(wavPath);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        TryDelete(resampled);
                        TryDelete(wavPath);
                        wavPath = null;
                        hasAudio = false;
                        audioNote = "音频变速失败（" + ex.Message + "），已仅导出视频。";
                    }
                }
            }
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            Mf.EnsureStarted();
            try
            {
                using var renderer = new StrokeRenderer();
                using var aiTextRenderer = new AiTextSceneRenderer();
                using var aiFormulaRenderer = new FormulaSceneRenderer();
                using var device = CanvasDevice.GetSharedDevice();
                using var target = new CanvasRenderTarget(device, options.Width, options.Height, 96);
                var timeline = new InkTimeline(session);
                var sceneCursor = new SceneCursor(session);
                CanvasBitmap pptBitmap = null;
                string pptBitmapPath = "";
                var aiBitmaps = new Dictionary<string, CanvasBitmap>(StringComparer.Ordinal);

                Mf.SinkWriter writer = null;
                if (hasAudio)
                {
                    try
                    {
                        writer = new Mf.SinkWriter(outputPath, options.Width, options.Height,
                            options.Fps, options.BitRate, includeAudio: true);
                        Report(progress, ExportStage.EncodingAudio, 0, "正在编码讲解音频…");
                        writer.WriteAudioWav(wavPath,
                            value => Report(progress, ExportStage.EncodingAudio, value,
                                $"正在编码讲解音频… {value * 100:0}%"),
                            ct);
                    }
                    catch (OperationCanceledException)
                    {
                        writer?.Dispose();
                        writer = null;
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // AAC 编码器缺失 / 格式不受支持：降级为仅视频，并删除这份 WAV。
                        writer?.Dispose();
                        writer = null;
                        TryDelete(outputPath);
                        TryDelete(wavPath);
                        wavPath = null;
                        hasAudio = false;
                        audioNote = "音频编码失败（" + ex.Message + "），已仅导出视频。";
                    }
                }

                if (writer is null)
                {
                    writer = new Mf.SinkWriter(outputPath, options.Width, options.Height,
                        options.Fps, options.BitRate, includeAudio: false);
                }

                try
                {
                    long frameDuration = 10_000_000L / options.Fps;
                    for (int i = 0; i < times.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        long timeMs = times[i];
                        var scene = sceneCursor.At(timeMs);
                        LoadPptBitmap(device, scene.PptScreenshotPath, options, ref pptBitmap, ref pptBitmapPath);
                        using (var drawing = target.CreateDrawingSession())
                        {
                            drawing.Clear(BackgroundColor(scene.Background));
                            if (pptBitmap is not null)
                                drawing.DrawImage(pptBitmap,
                                    new Windows.Foundation.Rect(0, 0, options.Width, options.Height),
                                    new Windows.Foundation.Rect(0, 0, pptBitmap.SizeInPixels.Width,
                                        pptBitmap.SizeInPixels.Height));
                            DrawGrid(drawing, scene.Background, options.Width, options.Height);
                            foreach (var stroke in timeline.BuildFrame(timeMs))
                                renderer.Draw(drawing, stroke, Matrix3x2.Identity);

                            // AI 板书对象：结构化原生文本/公式走矢量渲染；旧 PNG 卡片走图片路径。
                            // 导出不请求模型/网络，也不重新生成内容。
                            foreach (TimelineAiObject aiObject in timeline.BuildObjectFrame(timeMs))
                                DrawAiObject(drawing, device, aiObject, aiBitmaps,
                                    aiTextRenderer, aiFormulaRenderer);

                            // 可选叠加：章节信息与书写光标。二者都不改变笔画集合，
                            // 只影响像素外观，因此 BoardOnly / 区间语义不受影响。
                            if (options.ShowChapterInfo)
                            {
                                string chapter = ChapterLabelAt(timeline, timeMs);
                                if (!string.IsNullOrEmpty(chapter))
                                {
                                    using var chapterFormat = new CanvasTextFormat
                                    {
                                        FontSize = 26,
                                        WordWrapping = CanvasWordWrapping.NoWrap,
                                    };
                                    drawing.FillRectangle(
                                        new Windows.Foundation.Rect(20, 16, 560, 46),
                                        Windows.UI.Color.FromArgb(0x99, 0, 0, 0));
                                    drawing.DrawText(chapter, 32, 24,
                                        Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), chapterFormat);
                                }
                            }

                            if (options.ShowCursor && timeline.CursorPointAt(timeMs) is { } cursor)
                            {
                                drawing.FillCircle(cursor.X, cursor.Y, 6,
                                    Windows.UI.Color.FromArgb(0xCC, 0x00, 0x67, 0xC0));
                                drawing.DrawCircle(cursor.X, cursor.Y, 10,
                                    Windows.UI.Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF), 1.5f);
                            }
                        }

                        byte[] pixels = target.GetPixelBytes();
                        writer.WriteFrame(pixels, i * frameDuration, frameDuration);

                        double value = (i + 1) / (double)times.Count;
                        Report(progress, ExportStage.EncodingVideo, value,
                            $"正在编码视频… {value * 100:0}%");
                    }

                    writer.Finish();
                }
                finally
                {
                    writer?.Dispose();
                    pptBitmap?.Dispose();
                    pptBitmap = null;
                    foreach (CanvasBitmap bitmap in aiBitmaps.Values) bitmap.Dispose();
                    aiBitmaps.Clear();
                }
            }
            finally
            {
                Mf.Release();
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            TryDelete(wavPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            TryDelete(wavPath);
            throw new InvalidOperationException(
                "视频导出失败：Media Foundation H.264/AAC 编码不可用或渲染出错（" + ex.Message + "）。", ex);
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            TryDelete(wavPath);
            throw new InvalidOperationException("视频导出失败：编码器没有生成有效的 MP4 文件。");
        }

        if (hasAudio && (wavPath is null || !File.Exists(wavPath)))
            audioNote = "已写入讲解音频轨，但临时 WAV 已丢失。";

        Report(progress, ExportStage.Completed, 1,
            hasAudio ? "导出完成（含讲解音频）。" : "导出完成（仅视频）。");
        return new ExportResult(outputPath, times.Count, hasAudio, audioNote,
            hasAudio ? wavPath : null,
            hasAudio ? "Media Foundation H.264 + AAC" : "Media Foundation H.264");
    }

    private static string TempWavPath()
        => Path.Combine(Path.GetTempPath(),
            "inkcanvas-audio-" + Guid.NewGuid().ToString("N") + ".wav");

    private static string VideoOnlyNote(string reason)
    {
        string text = string.IsNullOrWhiteSpace(reason) ? "录制讲解音频失败。" : reason.Trim();
        if (text.IndexOf("已仅导出视频", StringComparison.Ordinal) < 0) text += "已仅导出视频。";
        return text;
    }

    private static void Report(IProgress<ExportProgress> progress, ExportStage stage, double value, string message)
        => progress?.Report(new ExportProgress(stage, Math.Clamp(value, 0, 1), message));

    /// <summary>导出叠加的章节标题：取目标时刻之前最近的书签，没有书签时返回空串。</summary>
    private static string ChapterLabelAt(InkTimeline timeline, long timeMs)
    {
        var bookmarks = timeline.Session?.Bookmarks;
        if (bookmarks is null || bookmarks.Count == 0) return "";

        SessionBookmark best = null;
        foreach (var bookmark in bookmarks)
        {
            if (bookmark is null) continue;
            if (bookmark.TimeMs > timeMs) break;
            best = bookmark;
        }
        return best is null ? "" : timeline.DescribeBookmark(best, 24);
    }

    private static Windows.UI.Color BackgroundColor(string name)
    {
        if (Enum.TryParse<BoardBackground>(name, ignoreCase: true, out var background))
        {
            return background switch
            {
                BoardBackground.Black => Windows.UI.Color.FromArgb(255, 0, 0, 0),
                BoardBackground.Green => Windows.UI.Color.FromArgb(255, 0x1E, 0x3B, 0x2F),
                BoardBackground.Cream => Windows.UI.Color.FromArgb(255, 0xF5, 0xEF, 0xDC),
                _ => Windows.UI.Color.FromArgb(255, 0xFA, 0xFA, 0xFA),
            };
        }
        return Windows.UI.Color.FromArgb(255, 0xFA, 0xFA, 0xFA);
    }


    /// <summary>
    /// 按需加载 PPT 截图。BoardOnly（或截图不存在 / 已失效）时释放缓存并回退纯色背景，
    /// 避免把上一页的图片带进后续帧。
    /// </summary>
    private static void LoadPptBitmap(CanvasDevice device, string path, ExportOptions options,
        ref CanvasBitmap bitmap, ref string loadedPath)
    {
        bool shouldDraw = ShouldDrawPptBackground(options)
            && !string.IsNullOrWhiteSpace(path)
            && File.Exists(path);

        if (!shouldDraw)
        {
            bitmap?.Dispose();
            bitmap = null;
            loadedPath = "";
            return;
        }

        if (bitmap is not null && string.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase)) return;

        bitmap?.Dispose();
        bitmap = null;
        loadedPath = "";
        try
        {
            bitmap = CanvasBitmap.LoadAsync(device, path).AsTask().GetAwaiter().GetResult();
            loadedPath = path;
        }
        catch
        {
            // 截图损坏 / 格式不支持：静默回退纯色，不中断导出。
            bitmap = null;
            loadedPath = "";
        }
    }

    /// <summary>
    /// 绘制一个 AI 板书对象：PNG 直接来自录制事件；缺失或解码失败时画占位框。
    /// 导出过程不调用模型/网络，也不重新生成内容。
    /// </summary>
    private static void DrawAiObject(CanvasDrawingSession drawing, CanvasDevice device, TimelineAiObject item,
        Dictionary<string, CanvasBitmap> cache,
        AiTextSceneRenderer textRenderer, FormulaSceneRenderer formulaRenderer)
    {
        if (item.IsNative)
        {
            if (item.IsFormula)
                formulaRenderer.Draw(drawing, new TimelineNativeFormulaElement(item), Matrix3x2.Identity);
            else
                textRenderer.Draw(drawing, new TimelineNativeTextElement(item), Matrix3x2.Identity);
            return;
        }

        var rect = new Windows.Foundation.Rect(
            item.Left, item.Top, Math.Max(1, item.Width), Math.Max(1, item.Height));
        // 旧 PNG 卡片同样应用录制的 6 元变换；schema=2 缺失时 identity。
        Matrix3x2 transform = BoardElementMath.Read(item.HasTransform ? item.Transform : null);
        Matrix3x2 previous = drawing.Transform;
        try
        {
            if (!transform.IsIdentity) drawing.Transform = transform * previous;

            CanvasBitmap bitmap = LoadAiBitmap(device, item, cache, out bool failed);
            if (bitmap is not null)
            {
                drawing.DrawImage(bitmap, rect,
                    new Windows.Foundation.Rect(0, 0, bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height));
                return;
            }

            DrawAiPlaceholder(drawing, item, rect, failed ? "解码失败" : "回放缺失");
        }
        finally
        {
            drawing.Transform = previous;
        }
    }

    private static CanvasBitmap LoadAiBitmap(CanvasDevice device, TimelineAiObject item,
        Dictionary<string, CanvasBitmap> cache, out bool failed)
    {
        failed = false;
        if (!item.HasRenderPng) return null;

        string key = item.Id + ":" + item.RenderPngBase64.Length + ":"
            + (item.RenderPngBase64.Length > 0 ? item.RenderPngBase64[0] : '\0');
        if (cache.TryGetValue(key, out CanvasBitmap cached)) return cached;

        try
        {
            byte[] bytes = Convert.FromBase64String(item.RenderPngBase64);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }

            stream.Seek(0);
            CanvasBitmap bitmap = CanvasBitmap.LoadAsync(device, stream).AsTask().GetAwaiter().GetResult();
            cache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            failed = true;
            return null;
        }
    }

    private static void DrawAiPlaceholder(CanvasDrawingSession drawing, TimelineAiObject item,
        Windows.Foundation.Rect rect, string reason)
    {
        drawing.FillRectangle(rect, Windows.UI.Color.FromArgb(0x33, 0x00, 0x67, 0xC0));
        drawing.DrawRectangle(rect, Windows.UI.Color.FromArgb(0xCC, 0x00, 0x67, 0xC0), 1.5f);

        string label = (string.IsNullOrWhiteSpace(item.Kind) ? "AI 对象" : item.Kind) + " · " + reason;
        using var format = new CanvasTextFormat { FontSize = 18, WordWrapping = CanvasWordWrapping.NoWrap };
        drawing.DrawText(label, (float)rect.X + 10, (float)rect.Y + 8,
            Windows.UI.Color.FromArgb(0xFF, 0x00, 0x67, 0xC0), format);
    }

    /// <summary>按回放背景样式绘制网格 / 坐标轴（与时光机窗口 40 px 间距一致）。</summary>
    private static void DrawGrid(CanvasDrawingSession drawing, string background, int width, int height)
    {
        ReplayBackgroundStyle style = ReplayBackgrounds.Resolve(background);
        if (!style.HasGrid) return;

        const float spacing = 40;
        var gridColor = style.IsLight
            ? Windows.UI.Color.FromArgb(0x2E, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        for (float x = spacing; x < width; x += spacing)
            drawing.DrawLine(x, 0, x, height, gridColor, 0.5f);
        for (float y = spacing; y < height; y += spacing)
            drawing.DrawLine(0, y, width, y, gridColor, 0.5f);

        if (!style.HasAxes) return;

        var axisColor = style.IsLight
            ? Windows.UI.Color.FromArgb(0x88, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF);
        float centerX = width / 2f;
        float centerY = height / 2f;
        drawing.DrawLine(centerX, 0, centerX, height, axisColor, 1.4f);
        drawing.DrawLine(0, centerY, width, centerY, axisColor, 1.4f);
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 清理失败不覆盖原始异常 */ }
    }

    /// <summary>把旧的 <see cref="IProgress{T}"/>（0–1）映射到带阶段的进度。</summary>
    private sealed class DoubleProgressAdapter : IProgress<ExportProgress>
    {
        private readonly IProgress<double> _inner;

        public DoubleProgressAdapter(IProgress<double> inner) => _inner = inner;

        public void Report(ExportProgress value)
        {
            double mapped = value.Stage switch
            {
                ExportStage.Preparing => 0,
                ExportStage.RecordingAudio => value.Value * 0.2,
                ExportStage.EncodingAudio => 0.2 + value.Value * 0.1,
                ExportStage.Completed => 1,
                _ => 0.3 + value.Value * 0.7,
            };
            _inner.Report(Math.Clamp(mapped, 0, 1));
        }
    }

    private sealed class InlineProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public InlineProgress(Action<double> report) => _report = report;

        public void Report(double value) => _report(value);
    }

    /// <summary>逐帧推进课堂场景，避免每帧从头扫描事件。</summary>
    private sealed class SceneCursor
    {
        private readonly List<TimelineEvent> _events;
        private readonly string _initialBackground = "Black";
        private int _index;
        private int _page;
        private string _background;
        private int _pptSlide;
        private string _pptScreenshot = "";
        private string _roster = "";
        private int _timer;

        public SceneCursor(ClassroomSession session)
        {
            _events = session.Events ?? new List<TimelineEvent>();
            if (_events.Count > 0) _page = _events[0].PageIndex;
            foreach (var e in _events)
            {
                if (e.Kind == TimelineEventKind.BackgroundChanged) { _initialBackground = e.Payload; break; }
            }
            _background = _initialBackground;
        }

        public ClassroomScene At(long timeMs)
        {
            while (_index < _events.Count && _events[_index].TimeMs <= timeMs)
            {
                var e = _events[_index++];
                switch (e.Kind)
                {
                    case TimelineEventKind.PageChanged:
                        _page = e.PageIndex;
                        break;
                    case TimelineEventKind.BackgroundChanged:
                        _background = e.Payload;
                        break;
                    case TimelineEventKind.PptSlideChanged:
                        PptSlideChange change = InkTimeline.DecodePptSlideChanged(e.Payload);
                        if (change.IsValid)
                        {
                            if (change.Index > 0) _pptSlide = change.Index;
                            _pptScreenshot = change.Screenshot ?? "";
                        }
                        break;
                    case TimelineEventKind.RosterDrawn:
                        _roster = e.Payload ?? _roster;
                        break;
                    case TimelineEventKind.TimerStarted:
                    case TimelineEventKind.TimerStopped:
                        _timer = Math.Max(0, int.TryParse(e.Payload, out int remaining) ? remaining : _timer);
                        break;
                    case TimelineEventKind.TimerEnded:
                        _timer = 0;
                        break;
                }
            }
            return new ClassroomScene(_page, _background ?? _initialBackground, _pptSlide, _roster ?? "", _timer,
                _pptScreenshot ?? "");
        }
    }

    // ---- Media Foundation -------------------------------------------------

    private static class Mf
    {
        private const int MfVersion = 0x00020070;
        private const int MfStartupFull = 0;
        private const int MfVideoInterlaceProgressive = 2;

        private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid AudioFormatAac = new("00001610-0000-0010-8000-00aa00389b71");
        private static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid AudioNumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid AudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid AudioPreferWaveFormatEx = new("a901aaba-e037-458a-bdf6-545be2074042");
        private static readonly Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        private static readonly Guid AacProfileLevelIndication = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
        private static readonly Guid AudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");

        private static readonly object Gate = new();
        private static int _refCount;

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IMFAttributes mediaType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateSample(out IMFSample sample);

        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string outputUrl,
            IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter sinkWriter);

        public static void EnsureStarted()
        {
            lock (Gate)
            {
                if (_refCount == 0)
                {
                    int hr = MFStartup(MfVersion, MfStartupFull);
                    if (hr < 0) throw new COMException("Media Foundation 初始化失败。", hr);
                }
                _refCount++;
            }
        }

        public static void Release()
        {
            lock (Gate)
            {
                if (_refCount == 0) return;
                _refCount--;
                if (_refCount == 0) MFShutdown();
            }
        }

        private static void Check(int hr, string message)
        {
            if (hr < 0)
                throw new COMException(message + "（HRESULT 0x" + hr.ToString("X8") + "）", hr);
        }

        private static IMFAttributes CreateVideoType(Guid subtype, int width, int height, int fps, int bitRate)
        {
            Check(MFCreateMediaType(out var type), "创建视频媒体类型失败。");

            Guid key = MajorType;
            Guid value = MediaTypeVideo;
            type.SetGUID(ref key, ref value);

            key = Subtype;
            value = subtype;
            type.SetGUID(ref key, ref value);

            if (bitRate > 0)
            {
                key = AvgBitrate;
                type.SetUINT32(ref key, (uint)bitRate);
            }

            key = FrameSize;
            Check(type.SetUINT64(ref key, ((ulong)(uint)width << 32) | (uint)height), "设置视频尺寸失败。");

            key = FrameRate;
            Check(type.SetUINT64(ref key, ((ulong)(uint)fps << 32) | 1), "设置视频帧率失败。");

            key = PixelAspectRatio;
            Check(type.SetUINT64(ref key, (1UL << 32) | 1), "设置像素宽高比失败。");

            key = InterlaceMode;
            type.SetUINT32(ref key, MfVideoInterlaceProgressive);
            return type;
        }

        private static IMFAttributes CreateAudioType(
            Guid subtype, int sampleRate, int channels, int bitsPerSample,
            int avgBytesPerSecond, bool allSamplesIndependent, bool isAac = false)
        {
            Check(MFCreateMediaType(out var type), "创建音频媒体类型失败。");

            Guid key = MajorType;
            Guid value = MediaTypeAudio;
            type.SetGUID(ref key, ref value);

            key = Subtype;
            value = subtype;
            type.SetGUID(ref key, ref value);

            key = AudioSamplesPerSecond;
            type.SetUINT32(ref key, (uint)sampleRate);

            key = AudioNumChannels;
            type.SetUINT32(ref key, (uint)channels);

            key = AudioBitsPerSample;
            type.SetUINT32(ref key, (uint)bitsPerSample);

            if (!isAac)
            {
                key = AudioBlockAlignment;
                type.SetUINT32(ref key, (uint)(channels * bitsPerSample / 8));

                key = AudioPreferWaveFormatEx;
                type.SetUINT32(ref key, 1);
            }

            if (avgBytesPerSecond > 0)
            {
                key = AudioAvgBytesPerSecond;
                type.SetUINT32(ref key, (uint)avgBytesPerSecond);
            }

            if (allSamplesIndependent)
            {
                key = AllSamplesIndependent;
                type.SetUINT32(ref key, 1);
            }

            if (isAac)
            {
                key = AacPayloadType;
                type.SetUINT32(ref key, 0);
                key = AacProfileLevelIndication;
                type.SetUINT32(ref key, 0x29);
            }

            return type;
        }

        public sealed class SinkWriter : IDisposable
        {
            private IMFSinkWriter _writer;
            private int _streamIndex;
            private int _audioStreamIndex = -1;
            private int _audioSampleRate;

            public SinkWriter(string path, int width, int height, int fps, int bitRate,
                bool includeAudio = false, int audioSampleRate = AudioSampleRate,
                int audioChannels = AudioChannels, int audioBitRate = AudioBitRate)
            {
                Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, null, out _writer), "创建 MP4 编码器失败。");

                var outputType = CreateVideoType(VideoFormatH264, width, height, fps, bitRate);
                try
                {
                    Check(_writer.AddStream(outputType, out _streamIndex), "添加 H.264 视频流失败。");
                }
                finally
                {
                    Marshal.ReleaseComObject(outputType);
                }

                var inputType = CreateVideoType(VideoFormatRgb32, width, height, fps, 0);
                try
                {
                    Check(_writer.SetInputMediaType(_streamIndex, inputType, null),
                        "设置 BGRA 输入格式失败（本机可能缺少 H.264 编码器）。");
                }
                finally
                {
                    Marshal.ReleaseComObject(inputType);
                }

                if (includeAudio)
                {
                    _audioSampleRate = audioSampleRate;

                    var audioOutput = CreateAudioType(AudioFormatAac, audioSampleRate, audioChannels,
                        16, audioBitRate / 8, allSamplesIndependent: false, isAac: true);
                    try
                    {
                        Check(_writer.AddStream(audioOutput, out _audioStreamIndex),
                            "添加 AAC 音频流失败（本机可能缺少 AAC 编码器）。");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(audioOutput);
                    }

                    var audioInput = CreateAudioType(AudioFormatPcm, audioSampleRate, audioChannels,
                        16, audioSampleRate * audioChannels * 2, allSamplesIndependent: true);
                    try
                    {
                        Check(_writer.SetInputMediaType(_audioStreamIndex, audioInput, null),
                            "设置 PCM 音频输入格式失败。");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(audioInput);
                    }
                }

                Check(_writer.BeginWriting(), "启动编码写入失败。");
            }

            public void WriteFrame(byte[] bgra, long sampleTime, long duration)
            {
                int byteCount = bgra.Length;
                Check(MFCreateMemoryBuffer(byteCount, out var buffer), "分配帧缓冲失败。");
                try
                {
                    CopyToBuffer(buffer, bgra, byteCount);
                    Check(MFCreateSample(out var sample), "创建视频采样失败。");
                    try
                    {
                        Check(sample.AddBuffer(buffer), "挂接帧缓冲失败。");
                        sample.SetSampleTime(sampleTime);
                        sample.SetSampleDuration(duration);
                        Check(_writer.WriteSample(_streamIndex, sample), "写入视频帧失败。");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                }
            }

            /// <summary>把 WAV（48 kHz / 2ch / 16-bit PCM）分块写入 AAC 音频流。</summary>
            public void WriteAudioWav(string wavPath, Action<double> progress, CancellationToken ct)
            {
                if (_audioStreamIndex < 0)
                    throw new InvalidOperationException("当前编码器未启用音频流。");

                WavInfo info = Pcm16Wav.ReadInfo(wavPath);
                if (!info.IsPcm16 || info.Channels != AudioChannels || info.SampleRate != AudioSampleRate)
                    throw new InvalidOperationException("讲解音频不是 48 kHz / 2 声道 / 16-bit PCM。");

                int frameBytes = info.Channels * info.BitsPerSample / 8;
                long totalFrames = info.DataBytes / frameBytes;
                if (totalFrames <= 0)
                    throw new InvalidOperationException("讲解音频为空，无法写入音轨。");

                const int framesPerSample = 1024;
                byte[] buffer = new byte[framesPerSample * frameBytes];
                long framesRead = 0;

                using (var stream = File.OpenRead(wavPath))
                {
                    stream.Position = info.DataOffset;

                    while (framesRead < totalFrames)
                    {
                        ct.ThrowIfCancellationRequested();

                        int frames = (int)Math.Min(framesPerSample, totalFrames - framesRead);
                        int bytes = frames * frameBytes;
                        int read = 0;
                        while (read < bytes)
                        {
                            int count = stream.Read(buffer, read, bytes - read);
                            if (count <= 0) break;
                            read += count;
                        }
                        if (read < frameBytes) break;

                        int actualFrames = read / frameBytes;
                        long sampleTime = framesRead * 10_000_000L / _audioSampleRate;
                        long nextTime = (framesRead + actualFrames) * 10_000_000L / _audioSampleRate;
                        WriteAudioSample(buffer, actualFrames * frameBytes, sampleTime, nextTime - sampleTime);

                        framesRead += actualFrames;
                        progress?.Invoke(Math.Clamp(framesRead / (double)totalFrames, 0, 1));
                    }
                }
            }

            private void WriteAudioSample(byte[] pcm, int byteCount, long sampleTime, long duration)
            {
                Check(MFCreateMemoryBuffer(byteCount, out var buffer), "分配音频缓冲失败。");
                try
                {
                    CopyToBuffer(buffer, pcm, byteCount);
                    Check(MFCreateSample(out var sample), "创建音频采样失败。");
                    try
                    {
                        Check(sample.AddBuffer(buffer), "挂接音频缓冲失败。");
                        sample.SetSampleTime(sampleTime);
                        sample.SetSampleDuration(duration);
                        Check(_writer.WriteSample(_audioStreamIndex, sample), "写入音频帧失败。");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                }
            }

            private static void CopyToBuffer(IMFMediaBuffer buffer, byte[] bytes, int byteCount)
            {
                Check(buffer.Lock(out IntPtr pointer, out _, out _), "锁定媒体缓冲失败。");
                try
                {
                    if (bytes != null && byteCount > 0)
                    {
                        if (byteCount > bytes.Length) byteCount = bytes.Length;
                        Marshal.Copy(bytes, 0, pointer, byteCount);
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
                buffer.SetCurrentLength(byteCount);
            }

            public void Finish() => Check(_writer.DoFinalize(), "完成 MP4 封装失败。");

            public void Dispose()
            {
                if (_writer is not null)
                {
                    Marshal.ReleaseComObject(_writer);
                    _writer = null;
                }
            }
        }

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType([In] ref Guid key, out int type);
            [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IMFAttributes other, int matchType, out int result);
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
            [PreserveSig] int GetDouble([In] ref Guid key, out double value);
            [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
            [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
            [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
            [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
            [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint size, IntPtr blobSize);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
            [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem([In] ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
            [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
            [PreserveSig] int SetDouble([In] ref Guid key, double value);
            [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
            [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
            [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IMFAttributes destination);
        }

        [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out int currentLength);
            [PreserveSig] int SetCurrentLength(int currentLength);
            [PreserveSig] int GetMaxLength(out int maxLength);
        }

        // IMFSample 的 vtable 先包含 IMFAttributes 的 30 个槽位，再是自身的 14 个方法。
        // 这里显式列出前置槽位（不会被调用），确保 AddBuffer 等方法落在正确的槽位上。
        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            [PreserveSig] int Slot_GetItem();
            [PreserveSig] int Slot_GetItemType();
            [PreserveSig] int Slot_CompareItem();
            [PreserveSig] int Slot_Compare();
            [PreserveSig] int Slot_GetUINT32();
            [PreserveSig] int Slot_GetUINT64();
            [PreserveSig] int Slot_GetDouble();
            [PreserveSig] int Slot_GetGUID();
            [PreserveSig] int Slot_GetStringLength();
            [PreserveSig] int Slot_GetString();
            [PreserveSig] int Slot_GetAllocatedString();
            [PreserveSig] int Slot_GetBlobSize();
            [PreserveSig] int Slot_GetBlob();
            [PreserveSig] int Slot_GetAllocatedBlob();
            [PreserveSig] int Slot_GetUnknown();
            [PreserveSig] int Slot_SetItem();
            [PreserveSig] int Slot_DeleteItem();
            [PreserveSig] int Slot_DeleteAllItems();
            [PreserveSig] int Slot_SetUINT32();
            [PreserveSig] int Slot_SetUINT64();
            [PreserveSig] int Slot_SetDouble();
            [PreserveSig] int Slot_SetGUID();
            [PreserveSig] int Slot_SetString();
            [PreserveSig] int Slot_SetBlob();
            [PreserveSig] int Slot_SetUnknown();
            [PreserveSig] int Slot_LockStore();
            [PreserveSig] int Slot_UnlockStore();
            [PreserveSig] int Slot_GetCount();
            [PreserveSig] int Slot_GetItemByIndex();
            [PreserveSig] int Slot_CopyAllItems();

            [PreserveSig] int GetSampleFlags(out uint flags);
            [PreserveSig] int SetSampleFlags(uint flags);
            [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration(out long duration);
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(out uint count);
            [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
            [PreserveSig] int RemoveBufferByIndex(uint index);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out uint length);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IMFAttributes targetMediaType, out int streamIndex);
            [PreserveSig] int SetInputMediaType(int streamIndex, IMFAttributes inputMediaType, IMFAttributes encodingParameters);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
            [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
            [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
            [PreserveSig] int NotifyEndOfSegment(int streamIndex);
            [PreserveSig] int Flush(int streamIndex);
            [PreserveSig] int DoFinalize();
            [PreserveSig] int GetServiceForStream(int streamIndex, [In] ref Guid service, [In] ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetStatistics(int streamIndex, IntPtr stats);
        }
    }
}