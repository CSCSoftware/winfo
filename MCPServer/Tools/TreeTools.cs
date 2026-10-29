namespace WinfoMain.MCPServer.Tools;

using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ModelContextProtocol.Server;
using WinfoMain.Common;
using WinfoMain.Desktop;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;
using WinfoMain.MCP;

[McpServerToolType]
public static class TreeTools
{
    [McpServerTool(Name = "get_tree")]
    [Description("Get the UI Automation tree of a window. Shows the hierarchical structure of all UI elements with their types, names, and element IDs.")]
    public static async Task<string> GetTree(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd. Default: active window.")] string window = "active",
        [Description("Maximum tree depth to show. Default: 5.")] int depth = 5)
    {
        PipelineHelper.Wake();
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        var root = await SceneBuilder.Instance.GetOrLoadUiaRootAsync(win);
        if (root == null)
            return $"Could not load UIA tree for [{win.ProcessName}] \"{win.Title}\".";

        var header = ElementSerializer.FormatWindowHeader(win);
        var tree = ElementSerializer.FormatTree(root, depth);
        return $"{header}\n{UIATreeSearch.CountNodes(root)} nodes:\n\n{tree}";
    }

    [McpServerTool(Name = "find_element")]
    [Description("Find UI elements by text query. Searches Name, AutomationId, ClassName, and Value (case-insensitive). Returns matching elements with their element IDs.")]
    public static async Task<string> FindElement(
        [Description("Text to search for in element names, automation IDs, class names, and values.")] string query,
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd. Default: active window.")] string window = "active")
    {
        PipelineHelper.Wake();
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        var root = await SceneBuilder.Instance.GetOrLoadUiaRootAsync(win);
        if (root == null)
            return $"Could not load UIA tree for [{win.ProcessName}].";

        var matches = UIATreeSearch.SearchByText(root, query);
        if (matches.Count == 0)
            return $"No elements matching \"{query}\" in [{win.ProcessName}].";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {matches.Count} element(s) matching \"{query}\" in [{win.ProcessName}]:");
        foreach (var node in matches)
            sb.AppendLine($"  {ElementSerializer.FormatNode(node)}");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_element_details")]
    [Description("Get full details of a UI element by its element ID. Shows all properties, patterns, and children count.")]
    public static string GetElementDetails(
        [Description("Element ID (eid:u:... for UIA, eid:r:... for region, eid:o:... for OCR text).")] string eid)
    {
        PipelineHelper.Wake();
        if (!ElementId.IsValid(eid))
            return $"Invalid element ID: {eid}";

        if (ElementId.IsUia(eid))
        {
            var result = ElementResolver.ResolveUia(eid);
            if (!result.Success)
                return result.Error!;

            var windowBounds = result.Window?.Bounds ?? Rectangle.Empty;
            return ElementSerializer.FormatNodeDetail(result.Value!, windowBounds);
        }

        if (ElementId.IsRegion(eid))
        {
            var result = ElementResolver.ResolveRegion(eid);
            if (!result.Success)
                return result.Error!;

            var win = result.Window!;
            var layout = win.Layout!;
            int regionIdx = layout.Regions.IndexOf(result.Value!);
            return ElementSerializer.FormatRegionDetail(result.Value!, win.Hwnd, regionIdx >= 0 ? regionIdx : 0);
        }

        if (ElementId.IsOcr(eid))
        {
            var parsed = ElementId.ParseOcr(eid);
            if (parsed == null)
                return $"Invalid OCR element ID: {eid}";

            var (hwnd, regionIndex, textIndex) = parsed.Value;
            var win = DesktopWindowList.Instance.FindByHwnd(hwnd);
            if (win?.Layout == null)
                return "Window or layout not found.";

            var layout = win.Layout;
            if (regionIndex < 0 || regionIndex >= layout.Regions.Count)
                return $"Region index {regionIndex} out of range.";

            var region = layout.Regions[regionIndex];
            if (textIndex < 0 || textIndex >= region.Texts.Count)
                return $"Text index {textIndex} out of range.";

            var text = region.Texts[textIndex];
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"OCR Text Element {eid}");
            sb.AppendLine($"  Text: \"{text.Text}\"");
            sb.AppendLine($"  ControlType: {text.ControlType}");
            sb.AppendLine($"  Position: @{text.X},{text.Y} {text.Width}x{text.Height} (region-relative)");
            sb.AppendLine($"  Region: [{region.Type}] \"{region.Name}\" {ElementId.FromRegion(hwnd, regionIndex)}");
            sb.AppendLine($"  Window: {ElementSerializer.FormatWindowHeader(win)}");
            return sb.ToString().TrimEnd();
        }

        return $"Unknown element ID type: {eid}";
    }

    [McpServerTool(Name = "get_focused")]
    [Description("Get the currently focused UI element and its parent window.")]
    public static string GetFocused()
    {
        PipelineHelper.Wake();
        try
        {
            var fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero)
                return "No foreground window.";

            var win = DesktopWindowList.Instance.FindByHwnd(fgHwnd);
            if (win == null)
            {
                DesktopWindowList.Instance.Refresh();
                win = DesktopWindowList.Instance.FindByHwnd(fgHwnd);
            }

            var sb = new System.Text.StringBuilder();
            if (win != null)
                sb.AppendLine($"Window: {ElementSerializer.FormatWindowHeader(win)}");
            else
                sb.AppendLine($"Window: hwnd 0x{fgHwnd:X} (not in window list)");

            // Get focused element via UIA (with CacheRequest for property access)
            var cr = UIATreeWalker.CreateCacheRequest(UIATreeView.ControlView);
            using (cr.Activate())
            {
                var focused = AutomationElement.FocusedElement;
                if (focused != null)
                {
                    var node = UIATreeBuilder.ConvertToNode(focused, UIATreeView.ControlView);
                    sb.AppendLine($"Focused: {ElementSerializer.FormatNode(node)}");
                }
                else
                {
                    sb.AppendLine("Focused: (none)");
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error getting focused element: {ex.Message}";
        }
    }

    [McpServerTool(Name = "hittest")]
    [Description("Find the UI element at a specific screen position. Returns the deepest (most specific) UIA element, layout region, and OCR text at that point.")]
    public static async Task<string> HitTest(
        [Description("Screen X coordinate")] int x,
        [Description("Screen Y coordinate")] int y)
    {
        PipelineHelper.Wake();
        var screenPos = new Point(x, y);

        var win = DesktopQueryService.FindWindowAt(screenPos);
        if (win == null)
            return $"No window at ({x}, {y}).";

        await PipelineHelper.EnsureLayoutAsync(win);
        var result = await DesktopQueryService.QueryWindowAtAsync(win, screenPos);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Position: ({x}, {y})");
        sb.AppendLine($"Window: {ElementSerializer.FormatWindowHeader(result.Window)}");

        if (result.UiaElement != null)
            sb.AppendLine($"UIA Element: {ElementSerializer.FormatNode(result.UiaElement)}");

        if (result.Region != null)
        {
            int regionIdx = win.Layout!.Regions.IndexOf(result.Region);
            sb.AppendLine($"Region: {ElementSerializer.FormatRegion(result.Region, win.Hwnd, regionIdx)}");
        }
        if (result.TextElement != null)
            sb.AppendLine($"Text: \"{result.TextElement.Text}\"");

        return sb.ToString().TrimEnd();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
