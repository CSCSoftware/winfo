namespace WinfoMain.MCPServer.Tools;

using System.ComponentModel;
using System.Drawing;
using ModelContextProtocol.Server;
using WinfoMain.Common;
using WinfoMain.Desktop;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.MCP;

[McpServerToolType]
public static class ReadTools
{
    [McpServerTool(Name = "list_windows")]
    [Description("List all visible windows on the desktop with process name, title, size, and position.")]
    public static string ListWindows()
    {
        PipelineHelper.Wake();
        DesktopWindowList.Instance.Refresh();
        var windows = DesktopWindowList.Instance.All
            .Where(w => !w.IsMinimized)
            .OrderBy(w => w.ZOrder)
            .ToList();

        return ElementSerializer.FormatWindowList(windows);
    }

    [McpServerTool(Name = "describe_screen")]
    [Description("Describe what is at a specific screen position (x, y). Returns the window, region, and text at that point.")]
    public static string DescribeScreen(
        [Description("Screen X coordinate")] int x,
        [Description("Screen Y coordinate")] int y)
    {
        PipelineHelper.Wake();
        var result = DesktopQueryService.QueryAt(new Point(x, y));
        if (result == null)
            return $"Nothing found at screen position ({x}, {y}).";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Position: ({x}, {y})");
        sb.AppendLine($"Window: {ElementSerializer.FormatWindowHeader(result.Window)}");

        if (result.Region != null)
        {
            var layout = result.Window.Layout!;
            int regionIdx = layout.Regions.IndexOf(result.Region);
            sb.AppendLine($"Region: {ElementSerializer.FormatRegion(result.Region, result.Window.Hwnd, regionIdx)}");
        }

        if (result.TextElement != null)
            sb.AppendLine($"Text: \"{result.TextElement.Text}\"");

        if (result.Line != null)
            sb.AppendLine($"Line: {result.Line}");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "describe_window")]
    [Description("Describe a window's layout: regions with types, sizes, and text previews. Use 'active' for the foreground window, or specify a process name or title substring.")]
    public static async Task<string> DescribeWindow(
        [Description("Window specifier: 'active', process name (e.g. 'notepad'), title substring, or hex hwnd (e.g. '0x1A2B'). Default: active window.")] string window = "active",
        [Description("Detail level: 'overview' (region list with previews) or 'full' (all texts in all regions). Default: overview.")] string detail = "overview")
    {
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        var pipelineError = await PipelineHelper.EnsureLayoutAsync(win);
        if (pipelineError != null)
            return pipelineError;

        return detail.Equals("full", StringComparison.OrdinalIgnoreCase)
            ? ElementSerializer.FormatWindowDetail(win)
            : ElementSerializer.FormatWindowOverview(win);
    }

    [McpServerTool(Name = "read_text")]
    [Description("Read text content from a window or a specific region. Returns all text grouped by rows.")]
    public static async Task<string> ReadText(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd. Default: active window.")] string window = "active",
        [Description("Region element ID (eid:r:...) to read a specific region. Omit to read all text.")] string? region = null)
    {
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        var pipelineError = await PipelineHelper.EnsureLayoutAsync(win);
        if (pipelineError != null)
            return pipelineError;

        var layout = win.Layout!;

        // Specific region requested
        if (!string.IsNullOrEmpty(region))
        {
            if (ElementId.IsRegion(region))
            {
                var resolved = ElementResolver.ResolveRegion(region);
                if (!resolved.Success)
                    return resolved.Error!;

                int regionIdx = layout.Regions.IndexOf(resolved.Value!);
                return ElementSerializer.FormatRegionDetail(resolved.Value!, win.Hwnd, regionIdx >= 0 ? regionIdx : 0);
            }
            return $"Invalid region ID: {region}. Expected format: eid:r:HWND:INDEX";
        }

        // All text from all regions
        return ElementSerializer.FormatWindowDetail(win);
    }

    [McpServerTool(Name = "search_text")]
    [Description("Search for text across all windows (or a specific window). Returns matching text elements with their positions and element IDs.")]
    public static async Task<string> SearchText(
        [Description("Text to search for (case-insensitive substring match)")] string query,
        [Description("Optional: restrict search to a specific window. Default: search all windows.")] string? window = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Query must not be empty.";

        PipelineHelper.Wake();

        // Search specific window
        if (!string.IsNullOrEmpty(window))
        {
            var (win, error) = WindowResolver.Resolve(window);
            if (win == null)
                return error!;

            // Auto-trigger pipeline if no layout data yet
            var pipelineError = await PipelineHelper.EnsureLayoutAsync(win);
            if (pipelineError != null)
                return pipelineError;

            var hits = win.Layout!.SearchText(query);
            return ElementSerializer.FormatSearchResults(win, hits);
        }

        // Search across all windows
        var allHits = DesktopQueryService.SearchText(query);
        if (allHits.Count == 0)
            return $"No matches for \"{query}\" across {DesktopWindowList.Instance.All.Count} windows.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {allHits.Count} match(es) for \"{query}\":");

        // Group by window
        foreach (var group in allHits.GroupBy(h => h.Window.Hwnd))
        {
            var w = group.First().Window;
            var layout = w.Layout!;
            sb.AppendLine();
            sb.AppendLine($"  {ElementSerializer.FormatWindowHeader(w)}");

            foreach (var (_, region, text) in group)
            {
                int regionIdx = layout.Regions.IndexOf(region);
                int textIdx = region.Texts.IndexOf(text);
                sb.AppendLine($"    [{region.Type}] \"{text.Text}\" {ElementId.FromOcr(w.Hwnd, regionIdx, textIdx)}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
