namespace InkCanvas.Core.AiAssistant;

public readonly record struct AssistantPlacement(double X, double Y, bool FlippedX, bool FlippedY, int Side);

public static class AssistantLayout
{
    public static AssistantPlacement Place(
        AssistantRect anchor,
        double cardWidth,
        double cardHeight,
        double viewportWidth,
        double viewportHeight,
        double gap = 12,
        double margin = 12)
    {
        cardWidth = Math.Max(0, cardWidth);
        cardHeight = Math.Max(0, cardHeight);
        gap = Math.Max(0, gap);
        margin = Math.Max(0, margin);

        double maxRight = Math.Max(margin, viewportWidth - margin);
        double maxBottom = Math.Max(margin, viewportHeight - margin);

        double left = anchor.Left;
        double top = anchor.Bottom + gap;
        bool flippedX = false;
        bool flippedY = false;
        int side = 0;

        if (top + cardHeight > maxBottom)
        {
            top = anchor.Top - gap - cardHeight;
            flippedY = true;
            side = 1;
        }

        if (top < margin || top + cardHeight > maxBottom)
            top = Clamp(top, margin, maxBottom - cardHeight);

        if (left + cardWidth > maxRight)
        {
            double rightSide = anchor.Right + gap;
            double leftSide = anchor.Left - gap - cardWidth;

            if (rightSide + cardWidth <= maxRight)
            {
                left = rightSide;
                side = 2;
                flippedX = true;
            }
            else if (leftSide >= margin)
            {
                left = leftSide;
                side = 3;
                flippedX = true;
            }
            else
            {
                side = 2;
                flippedX = true;
            }
        }

        if (left < margin || left + cardWidth > maxRight)
            left = Clamp(left, margin, maxRight - cardWidth);

        return new AssistantPlacement(left, top, flippedX, flippedY, side);
    }

    /// <summary>物理屏幕像素矩形换算为 DIP，使用目标显示器的缩放比例。</summary>
    public static AssistantRect ToDips(AssistantRect physical, double dpiScale)
    {
        double dpi = dpiScale > 0 ? dpiScale : 1.0;
        return new AssistantRect(physical.Left / dpi, physical.Top / dpi,
            physical.Right / dpi, physical.Bottom / dpi);
    }

    /// <summary>把画布客户区 DIP 锚点平移到屏幕绝对 DIP（板窗口客户区原点非 (0,0) 时）。</summary>
    public static AssistantRect Offset(AssistantRect rect, double dx, double dy)
        => new(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);

    /// <summary>
    /// 在屏幕绝对 DIP 的目标工作区里放置浮窗：先平移到以工作区左上为原点计算，
    /// 再把结果平移回屏幕坐标，副屏工作区不在 (0,0) 时不会被 clamp 回主屏。
    /// </summary>
    public static AssistantPlacement PlaceInWorkArea(
        AssistantRect anchor, double cardWidth, double cardHeight, AssistantRect workArea,
        double gap = 12, double margin = 12)
    {
        AssistantRect local = Offset(anchor, -workArea.Left, -workArea.Top);
        AssistantPlacement placement = Place(local, cardWidth, cardHeight,
            workArea.Width, workArea.Height, gap, margin);
        return placement with { X = placement.X + workArea.Left, Y = placement.Y + workArea.Top };
    }

    /// <summary>锚点铺满视口时固定在目标工作区右下角，并按同一工作区做边界 clamp。</summary>
    public static AssistantPlacement DockBottomRight(
        AssistantRect workArea, double cardWidth, double cardHeight, double inset = 16, double margin = 12)
    {
        double x = workArea.Right - cardWidth - inset;
        double y = workArea.Bottom - cardHeight - inset;
        x = Clamp(x, workArea.Left + margin, workArea.Right - cardWidth - margin);
        y = Clamp(y, workArea.Top + margin, workArea.Bottom - cardHeight - margin);
        return new AssistantPlacement(x, y, false, false, 0);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max <= min) return min;
        return Math.Min(Math.Max(value, min), max);
    }
}