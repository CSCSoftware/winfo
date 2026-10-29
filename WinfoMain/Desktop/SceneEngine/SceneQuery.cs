namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;

/// <summary>
/// Query API for searching within a LayoutResult.
/// Supplements the query methods already on LayoutResult itself.
/// </summary>
public static class SceneQuery
{
    /// <summary>
    /// Find the region at a given window-relative position.
    /// Returns the smallest (most specific) matching region.
    /// </summary>
    public static LayoutRegionResult? FindByBounds(LayoutResult layout, Point windowPoint)
    {
        LayoutRegionResult? best = null;
        int bestArea = int.MaxValue;

        foreach (var region in layout.Regions)
        {
            if (region.Bounds.Contains(windowPoint))
            {
                int area = region.Bounds.Width * region.Bounds.Height;
                if (area < bestArea)
                {
                    best = region;
                    bestArea = area;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Find the text element at a given window-relative position.
    /// First finds the region, then the closest text within it.
    /// </summary>
    public static (LayoutRegionResult? Region, RegionText? Text) FindTextByBounds(
        LayoutResult layout, Point windowPoint, int tolerance = 10)
    {
        var region = FindByBounds(layout, windowPoint);
        if (region == null) return (null, null);

        // Convert to region-relative coordinates
        int rx = windowPoint.X - region.Bounds.X;
        int ry = windowPoint.Y - region.Bounds.Y;

        var text = region.HitTest(rx, ry, tolerance);
        return (region, text);
    }

    /// <summary>
    /// Get all texts across all regions that match a query (case-insensitive substring).
    /// Delegates to LayoutResult.SearchText.
    /// </summary>
    public static List<(LayoutRegionResult Region, RegionText Text)> SearchByText(
        LayoutResult layout, string query)
        => layout.SearchText(query);

    /// <summary>
    /// Find all regions of a given type.
    /// Delegates to LayoutResult.FindAllByType.
    /// </summary>
    public static List<LayoutRegionResult> FindByType(LayoutResult layout, string type)
        => layout.FindAllByType(type);
}
