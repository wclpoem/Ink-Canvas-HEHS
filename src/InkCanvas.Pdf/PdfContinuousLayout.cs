using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Pdf;

/// <summary>Continuous document coordinates with page-local annotation coordinates.</summary>
public sealed class PdfContinuousLayout
{
    private readonly Rect[] _pages;
    public float Width { get; }
    public float Height { get; }
    public int Count => _pages.Length;
    public Rect this[int index] => _pages[index];

    public PdfContinuousLayout(IReadOnlyList<PdfPageGeometry> pages, float gap = 24)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF must contain at least one page.", nameof(pages));
        Width = pages.Max(page => page.DipWidth);
        _pages = new Rect[pages.Count];
        float y = 0;
        for (int index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            float x = (Width - page.DipWidth) / 2;
            _pages[index] = new(x, y, x + page.DipWidth, y + page.DipHeight);
            y += page.DipHeight + gap;
        }
        Height = y - gap;
    }

    public Matrix3x2 PageView(int index, Matrix3x2 documentView)
        => Matrix3x2.CreateTranslation(_pages[index].Left, _pages[index].Top) * documentView;

    public Matrix3x2 DocumentView(int index, Matrix3x2 pageView)
        => Matrix3x2.CreateTranslation(-_pages[index].Left, -_pages[index].Top) * pageView;

    public int NearestPage(float y)
    {
        int low = 0, high = Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (y > (_pages[middle].Bottom + _pages[middle + 1].Top) / 2) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public int HitTest(Vector2 point)
    {
        int index = NearestPage(point.Y);
        return _pages[index].Contains(point) ? index : -1;
    }

    public IEnumerable<int> VisiblePages(float top, float bottom)
    {
        for (int index = NearestPage(top); index < Count && _pages[index].Top <= bottom; index++)
            if (_pages[index].Bottom >= top) yield return index;
    }

    /// <summary>Visible pages first, followed by bounded look-ahead in the direction of travel.</summary>
    public IEnumerable<int> PreviewPages(float top, float bottom, int direction)
    {
        int first = NearestPage(top), last = NearestPage(bottom);
        foreach (int index in VisiblePages(top, bottom)) yield return index;
        for (int distance = 1; distance <= (direction == 0 ? 1 : 3); distance++)
        {
            int ahead = direction < 0 ? first - distance : last + distance;
            int behind = direction < 0 ? last + distance : first - distance;
            if (ahead >= 0 && ahead < Count) yield return ahead;
            if (distance == 1 && behind >= 0 && behind < Count) yield return behind;
        }
    }
}
