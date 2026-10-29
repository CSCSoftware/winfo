namespace WinfoMain.Desktop;

using System.Drawing;
using WinfoMain.Common;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Desktop-wide spatial query service.
/// Answers "what is at position X/Y (or inside rectangle R) on the desktop?"
///
/// Works at two levels:
///   1. Desktop level  — which window is at the given screen coordinates?
///   2. Window level   — which region/text is at that position within the window?
///
/// Used by: MCP Server tools, Test-App Inspector tab, automation scripts.
/// No GUI dependency — fully headless.
/// </summary>
public static class DesktopQueryService
{
    // --- Window lookup ---

    /// <summary>
    /// Find the topmost visible window at a screen position.
    /// Excludes minimized windows. Respects Z-order (lower = topmost).
    /// </summary>
    public static DesktopWindow? FindWindowAt(Point screenPos, uint excludeProcessId = 0)
    {
        DesktopWindow? best = null;

        foreach (var w in DesktopWindowList.Instance.All)
        {
            if (w.IsMinimized) continue;
            if (excludeProcessId != 0 && w.ProcessId == excludeProcessId) continue;
            if (!w.Bounds.Contains(screenPos)) continue;

            if (best == null || w.ZOrder < best.ZOrder)
                best = w;
        }

        return best;
    }

    /// <summary>
    /// Find all visible windows that overlap with a screen rectangle.
    /// Sorted by Z-order (topmost first).
    /// </summary>
    public static List<DesktopWindow> FindWindowsInRect(Rectangle screenRect, uint excludeProcessId = 0)
    {
        var result = new List<DesktopWindow>();

        foreach (var w in DesktopWindowList.Instance.All)
        {
            if (w.IsMinimized) continue;
            if (excludeProcessId != 0 && w.ProcessId == excludeProcessId) continue;
            if (!w.Bounds.IntersectsWith(screenRect)) continue;

            result.Add(w);
        }

        result.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));
        return result;
    }

    // --- Spatial queries ---

    /// <summary>
    /// Query everything at a screen position: window, region, line, word.
    /// Returns null if no window found at the position.
    /// </summary>
    public static DesktopQueryResult? QueryAt(Point screenPos, uint excludeProcessId = 0)
    {
        var window = FindWindowAt(screenPos, excludeProcessId);
        if (window == null) return null;

        return QueryWindowAt(window, screenPos);
    }

    /// <summary>
    /// Query everything within a screen rectangle in the topmost window.
    /// Returns null if no window found.
    /// </summary>
    public static DesktopQueryResult? QueryAt(Rectangle screenRect, uint excludeProcessId = 0)
    {
        var center = new Point(
            screenRect.X + screenRect.Width / 2,
            screenRect.Y + screenRect.Height / 2);

        var window = FindWindowAt(center, excludeProcessId);
        if (window == null) return null;

        return QueryWindowAt(window, center);
    }

    /// <summary>
    /// Query a specific window at a given screen position.
    /// Use this when the window is already known (e.g. from a list selection).
    /// UIA uses cached tree only (fast, no I/O).
    /// </summary>
    public static DesktopQueryResult QueryWindowAt(DesktopWindow window, Point screenPos)
    {
        // UIA hit-test (screen coords, cache only)
        UIATreeNode? uiaElement = null;
        var uiaRoot = SceneBuilder.Instance.GetCachedUiaRoot(window);
        if (uiaRoot != null)
            uiaElement = UIATreeSearch.FindByBounds(uiaRoot, screenPos);

        return BuildResult(window, screenPos, uiaElement);
    }

    /// <summary>
    /// Async version: loads UIA tree from cache or fresh if needed.
    /// Use when guaranteed UIA results are required even if no pipeline has run yet.
    /// </summary>
    public static async Task<DesktopQueryResult> QueryWindowAtAsync(DesktopWindow window, Point screenPos)
    {
        // UIA hit-test (with fresh load fallback)
        UIATreeNode? uiaElement = null;
        var uiaRoot = await SceneBuilder.Instance.GetOrLoadUiaRootAsync(window);
        if (uiaRoot != null)
            uiaElement = UIATreeSearch.FindByBounds(uiaRoot, screenPos);

        return BuildResult(window, screenPos, uiaElement);
    }

    private static DesktopQueryResult BuildResult(DesktopWindow window, Point screenPos, UIATreeNode? uiaElement)
    {
        if (window.Layout == null || window.Layout.Regions.Count == 0)
            return new DesktopQueryResult { Window = window, UiaElement = uiaElement };

        // Convert screen coords to window-relative
        var winPos = new Point(
            screenPos.X - window.Bounds.X,
            screenPos.Y - window.Bounds.Y);

        var (region, textElement) = SceneQuery.FindTextByBounds(window.Layout, winPos);

        string? line = null;
        Rectangle wordScreenBounds = Rectangle.Empty;

        if (region != null && textElement != null)
        {
            // Build line: all texts in the same row as the matched text element
            line = BuildLine(region, textElement);

            // Convert text bounds back to screen coords
            // RegionText coords are region-relative → add region bounds → add window bounds
            wordScreenBounds = new Rectangle(
                textElement.X + region.Bounds.X + window.Bounds.X,
                textElement.Y + region.Bounds.Y + window.Bounds.Y,
                textElement.Width,
                textElement.Height);
        }

        string? regionText = region != null ? BuildRegionText(region) : null;

        return new DesktopQueryResult
        {
            Window = window,
            UiaElement = uiaElement,
            Region = region,
            RegionText = regionText,
            TextElement = textElement,
            Line = line,
            WordScreenBounds = wordScreenBounds,
        };
    }

    // --- Text search across all windows ---

    /// <summary>
    /// Search for text across all windows that have a layout.
    /// Returns all matches with their window + region + text context.
    /// </summary>
    public static List<(DesktopWindow Window, LayoutRegionResult Region, RegionText Text)>
        SearchText(string query)
    {
        var results = new List<(DesktopWindow, LayoutRegionResult, RegionText)>();

        foreach (var window in DesktopWindowList.Instance.All)
        {
            if (window.Layout == null) continue;

            foreach (var (region, text) in SceneQuery.SearchByText(window.Layout, query))
                results.Add((window, region, text));
        }

        return results;
    }

    // --- Coordinate helpers ---

    /// <summary>Convert screen coordinates to window-relative coordinates.</summary>
    public static Point ToWindowRelative(Point screenPos, DesktopWindow window)
        => new(screenPos.X - window.Bounds.X, screenPos.Y - window.Bounds.Y);

    /// <summary>Convert window-relative coordinates to screen coordinates.</summary>
    public static Point ToScreenCoords(Point windowPos, DesktopWindow window)
        => new(windowPos.X + window.Bounds.X, windowPos.Y + window.Bounds.Y);

    // --- Private helpers ---

    private static string BuildLine(LayoutRegionResult region, RegionText target)
    {
        var rows = region.GetRows();
        foreach (var row in rows)
        {
            if (row.Any(t => ReferenceEquals(t, target)))
                return string.Join(" ", row.OrderBy(t => t.X).Select(t => t.Text));
        }
        return target.Text;
    }

    private static string BuildRegionText(LayoutRegionResult region)
    {
        if (region.Texts.Count == 0) return "";

        var rows = region.GetRows();
        return string.Join("\n", rows.Select(row =>
            string.Join(" ", row.OrderBy(t => t.X).Select(t => t.Text))));
    }
}
