namespace WinfoMain.Desktop.SceneEngine;

using System.Collections.Concurrent;
using System.Drawing;
using WinfoMain.Common;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Orchestrator: probes UIA, decides path, produces LayoutResult.
///
/// Three-stage pipeline:
///   1. UIA path or OCR path → base LayoutResult with regions
///   2. OcrTextMerger → fills sparse UIA regions with OCR texts
///   3. ProbeTextMerger → replaces texts with Probe (TextPattern) for editors/terminals
///
/// The resulting LayoutResult is the single source of truth.
/// </summary>
public class SceneBuilder
{
    public static SceneBuilder Instance { get; } = new();

    /// <summary>Minimum visible UIA nodes to use the UIA path.</summary>
    public int MinUiaNodes { get; set; } = 5;

    /// <summary>Timeout for loading the UIA tree in ms.</summary>
    public int UiaTimeoutMs { get; set; } = 3000;

    // Cache: last UIA root per window (for ElementResolver lookups)
    private readonly ConcurrentDictionary<nint, UIATreeNode> _uiaRootCache = new();

    /// <summary>Get the cached UIA root for a window (from last ProcessAsync). May be null.</summary>
    public UIATreeNode? GetCachedUiaRoot(DesktopWindow window)
        => _uiaRootCache.TryGetValue(window.Hwnd, out var root) ? root : null;

    /// <summary>
    /// Main API: analyze a window and produce a LayoutResult.
    /// Automatically decides between UIA and OCR path.
    /// OCR content must already be available (from BackgroundWorker pipeline).
    /// </summary>
    public async Task<LayoutResult> ProcessAsync(
        WinContent ocrContent, DesktopWindow window)
    {
        // 1. UIA probe: try to load tree, count visible nodes
        var uiaRoot = await ProbeUiaAsync(window);
        int visibleNodes = uiaRoot != null ? UIATreeSearch.CountNodes(uiaRoot) : 0;

        // Cache UIA root for ElementResolver lookups
        if (uiaRoot != null)
            _uiaRootCache[window.Hwnd] = uiaRoot;
        else
            _uiaRootCache.TryRemove(window.Hwnd, out _);

        // 2. Path decision: try UIA first, fall back to OCR if result is empty
        LayoutResult result;
        if (visibleNodes >= MinUiaNodes)
        {
            var uiaResult = UIALayoutBuilder.Build(uiaRoot!, window.Bounds);
            if (uiaResult.Regions.Count > 0)
            {
                // Stage 2: merge OCR texts into UIA regions where UIA texts are sparse
                OcrTextMerger.Merge(uiaResult, ocrContent, window.Bounds);
                result = uiaResult;
            }
            else
            {
                // UIA tree present but no usable regions (e.g. Blender) → fall back
                result = OcrLayoutBuilder.Build(ocrContent, window.Bounds);
            }
        }
        else
        {
            result = OcrLayoutBuilder.Build(ocrContent, window.Bounds);
        }

        // 3. Probe: if window has 1-2 TextPattern elements (editor/terminal),
        //    replace OCR texts with higher-quality Probe texts in affected regions.
        await ProbeTextMerger.ProbeAndMergeAsync(result, window.Hwnd, ocrContent, window.Bounds, window);

        return result;
    }

    /// <summary>
    /// Get UIA root from cache, or load fresh if not cached.
    /// Returns null if UIA is unavailable or times out.
    /// Caches the result for subsequent lookups.
    /// </summary>
    public async Task<UIATreeNode?> GetOrLoadUiaRootAsync(DesktopWindow window)
    {
        var cached = GetCachedUiaRoot(window);
        if (cached != null)
            return cached;

        var root = await ProbeUiaAsync(window);
        if (root != null)
            _uiaRootCache[window.Hwnd] = root;

        return root;
    }

    /// <summary>
    /// Try to load the full UIA tree for a window.
    /// Returns null if UIA is unavailable or times out.
    /// </summary>
    private async Task<UIATreeNode?> ProbeUiaAsync(DesktopWindow window)
    {
        try
        {
            var walker = new UIATreeWalker
            {
                LazyLoading = false,
                TimeoutMs = UiaTimeoutMs,
            };

            var windowInfo = new WindowInfo
            {
                Hwnd = window.Hwnd,
                ProcessName = window.ProcessName,
                Title = window.Title,
                Bounds = window.Bounds,
            };

            var root = await walker.LoadRootAsync(windowInfo);
            if (root == null) return null;

            await walker.LoadFullTreeAsync(root);
            return root;
        }
        catch (Exception ex)
        {
            WinfoLog.Write($"[WinfoMain] ProbeUiaAsync failed: {ex}");
            return null;
        }
    }
}
