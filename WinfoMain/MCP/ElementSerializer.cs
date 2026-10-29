namespace WinfoMain.MCP;

using System.Drawing;
using System.Text;
using WinfoMain.Common;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Serializes UIA nodes, layout regions, and search results to compact LLM-readable text.
/// All output uses the standard format conventions:
///   Window:  [processName] "Title" WxH @X,Y
///   Element: [ControlType] "Name" (AutomationId) @[X,Y WxH] eid:XXXX
///   Region:  [RegionType] "Name" WxH (AreaRatio%) @X,Y eid:XXXX
/// </summary>
public static class ElementSerializer
{
    // --- Single Node ---

    /// <summary>Format a UIA node as a one-liner with element ID.</summary>
    public static string FormatNode(UIATreeNode node)
    {
        var sb = new StringBuilder();
        sb.Append($"[{node.ControlType}]");

        if (!string.IsNullOrEmpty(node.Name))
            sb.Append($" \"{Truncate(node.Name, 60)}\"");

        if (!string.IsNullOrEmpty(node.AutomationId) && node.AutomationId != node.Name)
            sb.Append($" ({node.AutomationId})");

        var r = node.BoundingRectangle;
        if (!r.IsEmpty)
            sb.Append($" @[{r.X},{r.Y} {r.Width}x{r.Height}]");

        if (node.RuntimeId.Length > 0)
            sb.Append($" {ElementId.FromUia(node.RuntimeId)}");

        return sb.ToString();
    }

    /// <summary>Format a UIA node with full detail (all properties).</summary>
    public static string FormatNodeDetail(UIATreeNode node, Rectangle windowBounds = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormatNode(node));

        foreach (var (name, display, _) in node.GetPropertyList(windowBounds))
            sb.AppendLine($"  {name}: {display}");

        var visibleText = node.GetVisibleText();
        if (!string.IsNullOrWhiteSpace(visibleText))
            sb.AppendLine($"  VisibleText: {Truncate(visibleText, 200)}");

        if (node.Children.Count > 0)
            sb.AppendLine($"  Children: {node.Children.Count}");

        return sb.ToString().TrimEnd();
    }

    // --- Tree ---

    /// <summary>
    /// Format a UIA tree as indented text. Each node is a one-liner with eid.
    /// </summary>
    public static string FormatTree(UIATreeNode root, int maxDepth = -1)
    {
        var sb = new StringBuilder();
        FormatTreeRecursive(sb, root, 0, maxDepth);
        return sb.ToString().TrimEnd();
    }

    private static void FormatTreeRecursive(StringBuilder sb, UIATreeNode node, int depth, int maxDepth)
    {
        sb.Append(new string(' ', depth * 2));
        sb.AppendLine(FormatNode(node));

        if (maxDepth >= 0 && depth >= maxDepth)
        {
            if (node.Children.Count > 0)
            {
                sb.Append(new string(' ', (depth + 1) * 2));
                sb.AppendLine($"... ({node.Children.Count} children)");
            }
            return;
        }

        foreach (var child in node.Children)
            FormatTreeRecursive(sb, child, depth + 1, maxDepth);
    }

    // --- Layout Regions with Element IDs ---

    /// <summary>Format a layout region with element ID.</summary>
    public static string FormatRegion(LayoutRegionResult region, nint hwnd, int regionIndex)
    {
        var sb = new StringBuilder();
        sb.Append($"[{region.Type}]");

        if (!string.IsNullOrEmpty(region.Name))
            sb.Append($" \"{region.Name}\"");

        sb.Append($" {region.Bounds.Width}x{region.Bounds.Height}");
        sb.Append($" ({region.AreaRatio:P0})");
        sb.Append($" @{region.Bounds.X},{region.Bounds.Y}");
        sb.Append($" {ElementId.FromRegion(hwnd, regionIndex)}");

        return sb.ToString();
    }

    /// <summary>Format a layout region with full text detail and element IDs per text.</summary>
    public static string FormatRegionDetail(LayoutRegionResult region, nint hwnd, int regionIndex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormatRegion(region, hwnd, regionIndex));

        var rows = region.GetRows();
        foreach (var row in rows)
        {
            sb.Append("  ");
            bool first = true;
            foreach (var text in row)
            {
                if (!first) sb.Append("  |  ");
                int textIdx = region.Texts.IndexOf(text);
                sb.Append($"\"{text.Text}\" {ElementId.FromOcr(hwnd, regionIndex, textIdx)}");
                first = false;
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // --- Window overview with element IDs ---

    /// <summary>Format a window's layout overview — regions with element IDs.</summary>
    public static string FormatWindowOverview(DesktopWindow window)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormatWindowHeader(window));

        var layout = window.Layout;
        if (layout == null)
        {
            sb.AppendLine("  (no layout data)");
            return sb.ToString().TrimEnd();
        }

        for (int i = 0; i < layout.Regions.Count; i++)
        {
            var region = layout.Regions[i];
            sb.Append("  ");
            sb.Append(FormatRegion(region, window.Hwnd, i));

            // Text preview
            var preview = region.Texts
                .Take(5)
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (preview.Count > 0)
            {
                sb.Append($": {string.Join(", ", preview)}");
                if (region.Texts.Count > 5)
                    sb.Append($" (+{region.Texts.Count - 5})");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Format a window's full detail — all regions with all texts and element IDs.</summary>
    public static string FormatWindowDetail(DesktopWindow window)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormatWindowHeader(window));
        sb.AppendLine();

        var layout = window.Layout;
        if (layout == null)
        {
            sb.AppendLine("  (no layout data)");
            return sb.ToString().TrimEnd();
        }

        for (int i = 0; i < layout.Regions.Count; i++)
        {
            sb.AppendLine(FormatRegionDetail(layout.Regions[i], window.Hwnd, i));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // --- Search results ---

    /// <summary>Format search results with element IDs.</summary>
    public static string FormatSearchResults(
        DesktopWindow window,
        List<(LayoutRegionResult Region, RegionText Text)> hits)
    {
        if (hits.Count == 0)
            return "No matches found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {hits.Count} match(es) in [{window.ProcessName}] \"{Truncate(window.Title, 40)}\":");

        var layout = window.Layout!;
        foreach (var (region, text) in hits)
        {
            int regionIdx = layout.Regions.IndexOf(region);
            int textIdx = region.Texts.IndexOf(text);
            sb.Append($"  [{region.Type}] \"{text.Text}\"");
            sb.Append($" @{region.Bounds.X + text.X},{region.Bounds.Y + text.Y}");
            sb.Append($" {ElementId.FromOcr(window.Hwnd, regionIdx, textIdx)}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // --- Window list ---

    /// <summary>Format a list of windows as compact one-liners.</summary>
    public static string FormatWindowList(IReadOnlyList<DesktopWindow> windows)
    {
        if (windows.Count == 0)
            return "No windows found.";

        var sb = new StringBuilder();
        sb.AppendLine($"{windows.Count} window(s):");

        foreach (var w in windows)
            sb.AppendLine($"  {FormatWindowHeader(w)}");

        return sb.ToString().TrimEnd();
    }

    // --- Helpers ---

    /// <summary>Standard window header format.</summary>
    public static string FormatWindowHeader(DesktopWindow window)
    {
        var sb = new StringBuilder();
        sb.Append($"[{window.ProcessName}]");
        sb.Append($" \"{Truncate(window.Title, 50)}\"");
        sb.Append($" {window.Bounds.Width}x{window.Bounds.Height}");
        sb.Append($" @{window.Bounds.X},{window.Bounds.Y}");

        var layout = window.Layout;
        if (layout != null)
            sb.Append($" [{layout.DataSource}]");

        return sb.ToString();
    }

    private static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;

        // Find last space before maxLen for clean truncation
        int cut = text.LastIndexOf(' ', maxLen - 4);
        if (cut < maxLen / 2) cut = maxLen - 3;
        return text[..cut] + "...";
    }
}
