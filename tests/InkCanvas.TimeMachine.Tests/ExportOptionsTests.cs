using System.Reflection;
using InkCanvas.Modules.TimeMachine;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>导出参数校验、帧采样、倍速换算、磁盘估算与音轨字段剥离的测试。</summary>
public sealed class ExportOptionsTests
{
    private static ExportOptions Options(long start = 0, long end = 1000, int fps = 30,
        int width = 1920, int height = 1080, double speed = 1, bool boardOnly = false)
        => new("test.mp4", start, end, fps, 4_000_000, width, height, speed,
            boardOnly, false, false);

    [Fact]
    public void Validate_RejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => TimeMachineExporter.Validate(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyOutputPath(string path)
    {
        var options = Options() with { OutputPath = path };
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(options));
    }

    [Fact]
    public void Validate_RejectsNonMp4Extension()
    {
        var options = Options() with { OutputPath = "test.mkv" };
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(options));
    }

    [Fact]
    public void Validate_RejectsNegativeStart()
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(start: -10)));
    }

    [Fact]
    public void Validate_RejectsEndBeforeStart()
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(start: 500, end: 500)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Validate_RejectsOutOfRangeFps(int fps)
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(fps: fps)));
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000_000)]
    public void Validate_RejectsOutOfRangeBitrate(int bitRate)
    {
        var options = Options();
        options = options with { BitRate = bitRate };
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(options));
    }

    [Theory]
    [InlineData(160, 1080)]
    [InlineData(1920, 160)]
    [InlineData(5000, 1080)]
    public void Validate_RejectsOutOfRangeResolution(int width, int height)
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(width: width, height: height)));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(10)]
    public void Validate_RejectsOutOfRangeSpeed(double speed)
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(speed: speed)));
    }

    [Fact]
    public void FrameCount_RespectsSpeed()
    {
        Assert.Equal(30, TimeMachineExporter.EstimateFrameCount(Options(end: 1000)));
        Assert.Equal(15, TimeMachineExporter.EstimateFrameCount(Options(end: 1000, speed: 2)));
        Assert.Equal(60, TimeMachineExporter.EstimateFrameCount(Options(end: 1000, speed: 0.5)));
    }

    [Fact]
    public void SampleFrameTimes_AreMonotonicAndWithinRange()
    {
        var options = Options(end: 1000, fps: 30, speed: 2);
        var times = TimeMachineExporter.SampleFrameTimes(options);

        Assert.Equal(15, times.Count);
        Assert.Equal(0, times[0]);
        Assert.All(times, t => Assert.True(t <= options.EndMs));
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] > times[i - 1]);
    }

    [Fact]
    public void EffectiveDurationMs_DividedBySpeed()
    {
        Assert.Equal(1000, TimeMachineExporter.SourceDurationMs(Options(end: 1000)));
        Assert.Equal(500, TimeMachineExporter.EffectiveDurationMs(Options(end: 1000, speed: 2)), 6);
    }

    [Fact]
    public void BoardOnly_DisablesPptBackground()
    {
        Assert.False(TimeMachineExporter.ShouldDrawPptBackground(Options(boardOnly: true)));
        Assert.True(TimeMachineExporter.ShouldDrawPptBackground(Options(boardOnly: false)));
    }

    [Fact]
    public void EstimateRequiredBytes_IsVideoOnly()
    {
        long bytes = TimeMachineExporter.EstimateRequiredBytes(Options(end: 1000, fps: 30));
        Assert.Equal(30L * 1920 * 1080 * 4, bytes);
    }

    [Fact]
    public void ExportContract_HasNoAudioFields()
    {
        AssertNoAudioMembers(typeof(ExportOptions));
        AssertNoAudioMembers(typeof(ExportResult));
    }

    private static void AssertNoAudioMembers(Type type)
    {
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(members, m =>
            m.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("IncludeAudio", StringComparison.OrdinalIgnoreCase));
    }
}
