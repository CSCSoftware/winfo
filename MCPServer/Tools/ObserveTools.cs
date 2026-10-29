namespace WinfoMain.MCPServer.Tools;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using WinfoMain.Common;
using WinfoMain.Desktop;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;
using WinfoMain.MCP;

/// <summary>
/// Observe tools: wait for elements and detect changes.
/// </summary>
[McpServerToolType]
public static class ObserveTools
{
    // Previous layout snapshots for delta computation (per hwnd)
    private static readonly ConcurrentDictionary<nint, LayoutResult> _previousLayouts = new();

    [McpServerTool(Name = "wait_for_element")]
    [Description("Wait for a UI element or text to appear. Polls every 500ms until found or timeout. Useful after clicking buttons that open dialogs, or waiting for content to load. Searches both UIA tree and OCR layout text.")]
    public static async Task<string> WaitForElement(
        [Description("Text to search for (case-insensitive, matches element Name, AutomationId, or OCR text)")] string search,
        [Description("Maximum wait time in milliseconds (default: 5000, max: 30000)")] int timeout_ms = 5000,
        [Description("Optional: restrict search to a specific window. Default: search all windows.")] string? window = null)
    {
        if (string.IsNullOrWhiteSpace(search))
            return "Search text must not be empty.";

        PipelineHelper.Wake();
        timeout_ms = Math.Clamp(timeout_ms, 500, 30000);
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeout_ms)
        {
            // Refresh window list
            DesktopWindowList.Instance.Refresh();
            var windows = DesktopWindowList.Instance.All
                .Where(w => !w.IsMinimized)
                .ToList();

            // Filter by window if specified
            if (!string.IsNullOrEmpty(window))
            {
                var (resolved, _) = WindowResolver.Resolve(window);
                if (resolved != null)
                    windows = [resolved];
                else
                    windows = [];
            }

            foreach (var win in windows)
            {
                // Search in UIA tree (cached)
                var uiaRoot = SceneBuilder.Instance.GetCachedUiaRoot(win);
                if (uiaRoot != null)
                {
                    var uiaHits = UIATreeSearch.SearchByText(uiaRoot, search);
                    if (uiaHits.Count > 0)
                    {
                        var node = uiaHits[0];
                        return $"Found after {sw.ElapsedMilliseconds}ms:\n"
                             + $"  Window: {ElementSerializer.FormatWindowHeader(win)}\n"
                             + $"  Element: {ElementSerializer.FormatNode(node)}";
                    }
                }

                // Search in layout text (OCR)
                if (win.Layout != null)
                {
                    var hit = SearchLayout(win);
                    if (hit != null) return $"Found after {sw.ElapsedMilliseconds}ms:\n{hit}";
                }

                // If no layout, try a fresh pipeline run
                if (win.Layout == null)
                {
                    await PipelineHelper.EnsureLayoutAsync(win);
                    if (win.Layout != null)
                    {
                        var hit = SearchLayout(win);
                        if (hit != null) return $"Found after {sw.ElapsedMilliseconds}ms:\n{hit}";
                    }
                }
            }

            await Task.Delay(500);
        }

        return $"Timeout after {timeout_ms}ms: \"{search}\" not found.";

        // Local helper: search layout text and format result
        string? SearchLayout(DesktopWindow w)
        {
            var textHits = w.Layout!.SearchText(search);
            if (textHits.Count == 0) return null;

            var (region, text) = textHits[0];
            int regionIdx = w.Layout.Regions.IndexOf(region);
            int textIdx = region.Texts.IndexOf(text);
            return $"  Window: {ElementSerializer.FormatWindowHeader(w)}\n"
                 + $"  Region: [{region.Type}] \"{text.Text}\" {ElementId.FromOcr(w.Hwnd, regionIdx, textIdx)}";
        }
    }

    [McpServerTool(Name = "get_delta")]
    [Description("Detect what changed in a window since the last call. Runs the pipeline (capture + OCR + layout) and compares with the previous snapshot. Returns added, removed, and changed regions. First call returns all regions as 'added'.")]
    public static async Task<string> GetDelta(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd. Default: active window.")] string window = "active")
    {
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        // Get the previous layout (if any)
        _previousLayouts.TryGetValue(win.Hwnd, out var oldLayout);

        // Run fresh pipeline (always, even if layout exists)
        var pipelineError = await PipelineHelper.RunPipelineAsync(win);
        if (pipelineError != null)
            return pipelineError;

        var newLayout = win.Layout;
        if (newLayout == null)
            return $"No layout available for [{win.ProcessName}].";

        // Store current as previous for next call
        _previousLayouts[win.Hwnd] = newLayout;

        // Compute and render delta
        var delta = SceneDelta.Compute(oldLayout, newLayout);

        var header = ElementSerializer.FormatWindowHeader(win);

        if (delta.IsEmpty)
            return $"{header}\nNo changes detected.";

        return $"{header}\n{delta.Render()}";
    }

    [McpServerTool(Name = "wait_for_delta")]
    [Description("Wait for changes in a window. Polls capture + OCR + layout and compares with previous snapshot. Returns when changes are detected or timeout. Useful after triggering actions to wait for UI updates.")]
    public static async Task<string> WaitForDelta(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd. Default: active window.")] string window = "active",
        [Description("Maximum wait time in milliseconds (default: 10000, max: 30000)")] int timeout_ms = 10000)
    {
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        timeout_ms = Math.Clamp(timeout_ms, 500, 30000);

        // Get the previous layout (if any)
        _previousLayouts.TryGetValue(win.Hwnd, out var oldLayout);

        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeout_ms)
        {
            var pipelineError = await PipelineHelper.RunPipelineAsync(win);
            if (pipelineError != null)
                return pipelineError;

            if (win.Layout == null)
            {
                await Task.Delay(500);
                continue;
            }

            var delta = SceneDelta.Compute(oldLayout, win.Layout);

            if (!delta.IsEmpty)
            {
                // Store current as previous for next call
                _previousLayouts[win.Hwnd] = win.Layout;

                var header = ElementSerializer.FormatWindowHeader(win);
                return $"{header}\nChanges detected after {sw.ElapsedMilliseconds}ms:\n{delta.Render()}";
            }

            await Task.Delay(500);
        }

        // Timeout — store current layout anyway for next call
        if (win.Layout != null)
            _previousLayouts[win.Hwnd] = win.Layout;

        var timeoutHeader = ElementSerializer.FormatWindowHeader(win);
        return $"{timeoutHeader}\nNo changes detected after {timeout_ms}ms.";
    }
}
