namespace InkCanvas.Features.Ppt;

public enum NavigationCorner { BottomLeft, BottomRight, SideLeft, SideRight }

public readonly record struct ScreenBox(int X, int Y, int Width, int Height);

public readonly record struct NavigationPlacementResult(
    int X, int Y, int Width, int Height, NavigationCorner Corner);

/// <summary>
/// Places the measured navigation client rectangle in physical pixels, keeping bottom
/// navigation aligned with the main toolbar and avoiding overlap on narrow displays.
/// </summary>
public static class NavigationLayout
{
    public static NavigationPlacementResult Place(
        NavigationCorner corner, ScreenBox showBounds, int width, int height, int gap, ScreenBox toolbarBounds = default)
    {
        int boundsWidth = Math.Max(0, showBounds.Width);
        int boundsHeight = Math.Max(0, showBounds.Height);
        width = Math.Min(Math.Max(0, width), boundsWidth);
        height = Math.Min(Math.Max(0, height), boundsHeight);

        int x = corner switch
        {
            NavigationCorner.BottomRight or NavigationCorner.SideRight
                => showBounds.X + boundsWidth - width - gap,
            _ => showBounds.X + gap,
        };
        int y = corner switch
        {
            NavigationCorner.SideLeft or NavigationCorner.SideRight
                => showBounds.Y + (boundsHeight - height) / 2,
            _ => (toolbarBounds.Height > 0 ? toolbarBounds.Y + toolbarBounds.Height : showBounds.Y + boundsHeight - gap) - height,
        };

        x = Math.Clamp(x, showBounds.X, showBounds.X + boundsWidth - width);
        y = Math.Clamp(y, showBounds.Y, showBounds.Y + boundsHeight - height);

        if (toolbarBounds.Width > 0 && toolbarBounds.Height > 0
            && x < toolbarBounds.X + toolbarBounds.Width && x + width > toolbarBounds.X
            && y < toolbarBounds.Y + toolbarBounds.Height && y + height > toolbarBounds.Y)
        {
            int above = toolbarBounds.Y - gap - height;
            int below = toolbarBounds.Y + toolbarBounds.Height + gap;
            y = above >= showBounds.Y ? above : below + height <= showBounds.Y + boundsHeight ? below : y;
        }

        return new NavigationPlacementResult(x, y, width, height, corner);
    }

    public static NavigationCorner NextCorner(NavigationCorner corner) => corner switch
    {
        NavigationCorner.BottomLeft => NavigationCorner.BottomRight,
        NavigationCorner.BottomRight => NavigationCorner.SideRight,
        NavigationCorner.SideRight => NavigationCorner.SideLeft,
        _ => NavigationCorner.BottomLeft,
    };
}
