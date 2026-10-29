namespace WinfoMain.MCP;

using System.Drawing;
using WinfoMain.Common;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Resolves an element ID (eid:) to concrete objects.
/// - eid:u: → UIATreeNode (from cached UIA tree in SceneBuilder)
/// - eid:o: → screen position (center of the OCR text element)
/// - eid:r: → LayoutRegionResult
/// </summary>
public static class ElementResolver
{
    /// <summary>
    /// Resolve a UIA element ID to its tree node.
    /// Searches the UIA tree cached on the window's Layout.
    /// </summary>
    public static ResolveResult<UIATreeNode> ResolveUia(string eid)
    {
        var runtimeId = ElementId.ParseUia(eid);
        if (runtimeId == null)
            return ResolveResult<UIATreeNode>.Fail($"Invalid UIA element ID: {eid}");

        // Search all windows for a UIA tree containing this RuntimeId
        foreach (var window in DesktopWindowList.Instance.All)
        {
            var root = SceneBuilder.Instance.GetCachedUiaRoot(window);
            if (root == null) continue;

            var node = UIATreeSearch.FindByRuntimeId(root, runtimeId);
            if (node != null)
                return ResolveResult<UIATreeNode>.Ok(node, window);
        }

        return ResolveResult<UIATreeNode>.Fail($"UIA element not found: {eid}. Tree may have changed.");
    }

    /// <summary>
    /// Resolve an OCR element ID to a screen position (center of the text).
    /// </summary>
    public static ResolveResult<Point> ResolveOcr(string eid)
    {
        var parsed = ElementId.ParseOcr(eid);
        if (parsed == null)
            return ResolveResult<Point>.Fail($"Invalid OCR element ID: {eid}");

        var (hwnd, regionIndex, textIndex) = parsed.Value;
        var window = DesktopWindowList.Instance.FindByHwnd(hwnd);
        if (window == null)
            return ResolveResult<Point>.Fail($"Window 0x{hwnd:X} not found");

        var layout = window.Layout;
        if (layout == null)
            return ResolveResult<Point>.Fail($"Window has no layout data");

        if (regionIndex < 0 || regionIndex >= layout.Regions.Count)
            return ResolveResult<Point>.Fail($"Region index {regionIndex} out of range (0..{layout.Regions.Count - 1})");

        var region = layout.Regions[regionIndex];
        if (textIndex < 0 || textIndex >= region.Texts.Count)
            return ResolveResult<Point>.Fail($"Text index {textIndex} out of range (0..{region.Texts.Count - 1})");

        var text = region.Texts[textIndex];

        // Convert region-relative to screen coordinates
        int screenX = window.Bounds.X + region.Bounds.X + text.X + text.Width / 2;
        int screenY = window.Bounds.Y + region.Bounds.Y + text.Y + text.Height / 2;

        return ResolveResult<Point>.Ok(new Point(screenX, screenY), window);
    }

    /// <summary>
    /// Resolve a region element ID to a LayoutRegionResult.
    /// </summary>
    public static ResolveResult<LayoutRegionResult> ResolveRegion(string eid)
    {
        var parsed = ElementId.ParseRegion(eid);
        if (parsed == null)
            return ResolveResult<LayoutRegionResult>.Fail($"Invalid region element ID: {eid}");

        var (hwnd, regionIndex) = parsed.Value;
        var window = DesktopWindowList.Instance.FindByHwnd(hwnd);
        if (window == null)
            return ResolveResult<LayoutRegionResult>.Fail($"Window 0x{hwnd:X} not found");

        var layout = window.Layout;
        if (layout == null)
            return ResolveResult<LayoutRegionResult>.Fail($"Window has no layout data");

        if (regionIndex < 0 || regionIndex >= layout.Regions.Count)
            return ResolveResult<LayoutRegionResult>.Fail($"Region index {regionIndex} out of range (0..{layout.Regions.Count - 1})");

        return ResolveResult<LayoutRegionResult>.Ok(layout.Regions[regionIndex], window);
    }

    /// <summary>
    /// Resolve any element ID type — returns the screen position for clicking.
    /// UIA → center of BoundingRectangle, OCR → center of text, Region → center of region bounds.
    /// </summary>
    public static ResolveResult<Point> ResolveToScreenPoint(string eid)
    {
        if (ElementId.IsUia(eid))
        {
            var result = ResolveUia(eid);
            if (!result.Success) return ResolveResult<Point>.Fail(result.Error!);
            var r = result.Value!.BoundingRectangle;
            return r.IsEmpty
                ? ResolveResult<Point>.Fail("UIA element has no bounds")
                : ResolveResult<Point>.Ok(new Point(r.X + r.Width / 2, r.Y + r.Height / 2), result.Window);
        }

        if (ElementId.IsOcr(eid))
            return ResolveOcr(eid);

        if (ElementId.IsRegion(eid))
        {
            var result = ResolveRegion(eid);
            if (!result.Success) return ResolveResult<Point>.Fail(result.Error!);
            var window = result.Window!;
            var bounds = result.Value!.Bounds;
            int screenX = window.Bounds.X + bounds.X + bounds.Width / 2;
            int screenY = window.Bounds.Y + bounds.Y + bounds.Height / 2;
            return ResolveResult<Point>.Ok(new Point(screenX, screenY), window);
        }

        return ResolveResult<Point>.Fail($"Unknown element ID format: {eid}");
    }
}

/// <summary>
/// Result of an element resolve operation.
/// </summary>
public class ResolveResult<T>
{
    public bool Success { get; private init; }
    public T? Value { get; private init; }
    public DesktopWindow? Window { get; private init; }
    public string? Error { get; private init; }

    public static ResolveResult<T> Ok(T value, DesktopWindow? window = null)
        => new() { Success = true, Value = value, Window = window };

    public static ResolveResult<T> Fail(string error)
        => new() { Success = false, Error = error };
}
