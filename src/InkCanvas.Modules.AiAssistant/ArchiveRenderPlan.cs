using InkCanvas.Core.Ink;

namespace InkCanvas.Modules.AiAssistant;

public sealed record ArchiveRenderRegion(Rect Bounds, int Width, int Height, string Role);

public static class ArchiveRenderPlan
{
    public const int MaximumEdge = 3840;
    public const int MaximumPixels = 3840 * 2160;

    public static IReadOnlyList<ArchiveRenderRegion> Create(Rect content, bool screen, float pageWidth, float pageHeight, IReadOnlyList<(Rect Bounds, int Points)> ink)
    {
        if (content.IsEmpty || !float.IsFinite(content.Width) || !float.IsFinite(content.Height)) throw new InvalidDataException("页面范围无效。");
        var result = new List<ArchiveRenderRegion>();
        if (screen)
        {
            float pixelLimitScale = (float)Math.Sqrt(MaximumPixels / (content.Width * (double)content.Height));
            float scale = Math.Min(1, Math.Min(MaximumEdge / Math.Max(content.Width, content.Height), pixelLimitScale));
            result.Add(Region(content, scale, scale < 1 ? "overview" : "original"));
            if (scale < 1)
            {
                var cells = new Dictionary<(int X, int Y), long>();
                foreach (var stroke in ink)
                {
                    var key = ((int)MathF.Floor((stroke.Bounds.Left - content.Left) / 1536), (int)MathF.Floor((stroke.Bounds.Top - content.Top) / 1024));
                    cells[key] = cells.GetValueOrDefault(key) + Math.Max(1, stroke.Points);
                }
                foreach (var cell in cells.OrderByDescending(pair => pair.Value).Take(2))
                {
                    float left = content.Left + cell.Key.X * 1536, top = content.Top + cell.Key.Y * 1024;
                    result.Add(Region(new(left, top, Math.Min(content.Right, left + 1536), Math.Min(content.Bottom, top + 1024)), 1, "detail"));
                }
                if (result.Count == 1) result.Add(Region(new(content.Left, content.Top, Math.Min(content.Right, content.Left + 1536), Math.Min(content.Bottom, content.Top + 1024)), 1, "detail"));
            }
            return result;
        }
        float width = Math.Clamp(pageWidth, 512, MaximumEdge), height = Math.Clamp(pageHeight, 512, 2160);
        if (content.Width <= width + 64 && content.Height <= height + 64 && content.Width <= MaximumEdge && content.Height <= MaximumEdge && (double)content.Width * content.Height <= MaximumPixels)
            return new[] { Region(content, 1, "page") };
        int columns = Math.Max(1, (int)Math.Ceiling((content.Width - 64) / (width - 64)));
        int rows = Math.Max(1, (int)Math.Ceiling((content.Height - 64) / (height - 64)));
        if ((long)columns * rows > 1024) throw new InvalidDataException("长页面超过 1024 个分段，请拆分存档后重试。");
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                float left = content.Left + col * (width - 64), top = content.Top + row * (height - 64);
                result.Add(Region(new(left, top, Math.Min(content.Right, left + width), Math.Min(content.Bottom, top + height)), 1, "segment"));
            }
        return result;
    }

    private static ArchiveRenderRegion Region(Rect bounds, float scale, string role) => new(bounds,
        Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)), role);
}
