using InkCanvas.Features.Board;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>时光机回放背景的叠加网格类型。</summary>
public enum ReplayGridStyle
{
    /// <summary>纯色背景，不绘制网格。</summary>
    None,

    /// <summary>等距网格线。</summary>
    Grid,

    /// <summary>等距网格线 + 中心横纵坐标轴。</summary>
    Coordinate,
}

/// <summary>
/// 背景枚举名到回放背景样式的映射结果。纯逻辑、无 UI 依赖，供导出与测试共用。
/// <para><see cref="IsLight"/> 用于选择网格线颜色：浅色背景用深线，深色背景用浅线。</para>
/// </summary>
public readonly record struct ReplayBackgroundStyle(string Name, ReplayGridStyle Grid, bool IsLight)
{
    public bool HasGrid => Grid != ReplayGridStyle.None;

    public bool HasAxes => Grid == ReplayGridStyle.Coordinate;
}

/// <summary>把会话里记录的背景枚举名（或大小写变体）解析为回放背景样式。</summary>
public static class ReplayBackgrounds
{
    /// <summary>无背景事件时的回退名，与 <see cref="InkTimeline.BackgroundAt"/> 一致。</summary>
    public const string Fallback = "Black";

    public static ReplayBackgroundStyle Resolve(string name)
    {
        if (!Enum.TryParse<BoardBackground>(name, ignoreCase: true, out BoardBackground background))
            return new ReplayBackgroundStyle(Fallback, ReplayGridStyle.None, false);

        ReplayGridStyle grid = background switch
        {
            BoardBackground.Grid => ReplayGridStyle.Grid,
            BoardBackground.Coordinate => ReplayGridStyle.Coordinate,
            _ => ReplayGridStyle.None,
        };

        bool isLight = background is BoardBackground.White or BoardBackground.Grid
            or BoardBackground.Lined or BoardBackground.Cream or BoardBackground.Coordinate;

        return new ReplayBackgroundStyle(background.ToString(), grid, isLight);
    }
}
