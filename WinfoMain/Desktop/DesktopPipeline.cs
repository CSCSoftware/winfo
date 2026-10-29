namespace WinfoMain.Desktop;

using WinfoMain.Common;
using WinfoMain.Desktop.OcrEngine;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Headless pipeline: Capture → OCR → Layout (UIA+OCR+Probe).
/// Encapsulates the full 3-stage processing for a single window.
///
/// Used by: MCP Server tools, Test-App buttons, BackgroundWorker.
/// No GUI dependency — fully headless.
/// </summary>
public class DesktopPipeline
{
    /// <summary>
    /// Minimum average word height in pixels. If the normal OCR result falls below this,
    /// the pipeline retries with 3x upscale + 16 colors and remembers it for the window.
    /// </summary>
    public static int UpscaleThreshold { get; set; } = 15;

    /// <summary>
    /// Keep LastCapture bitmap on the window after processing.
    /// True for Test-App (debug visualization), false for MCP Server (saves ~20-90MB per window).
    /// </summary>
    public bool KeepLastCapture { get; set; } = true;

    /// <summary>
    /// Run the full pipeline for a window: Capture → OCR → Layout.
    /// Updates window.LastCapture, window.ContentOcr, window.Layout, window.BspDebug.
    /// Auto-upscale: if average word height is below UpscaleThreshold, retries with 3x + 16 colors.
    /// </summary>
    public async Task<PipelineResult> ProcessAsync(DesktopWindow window)
    {
        // Stage 1: Capture
        var bitmap = WindowCapture.Capture(window.Hwnd);
        if (bitmap == null)
            return PipelineResult.Failed(window, "Capture failed");

        window.SetLastCapture(bitmap);

        // Stage 2: OCR
        if (window.NeedsUpscale)
        {
            // Already known to need upscale — go directly
            await OcrEngine.OcrEngine.Instance.ProcessBitmapAsync(bitmap, window.ContentOcr, scaleFactor: 3, colorCount: 16);
        }
        else
        {
            // Normal OCR first
            await OcrEngine.OcrEngine.Instance.ProcessBitmapAsync(bitmap, window.ContentOcr);

            // Check average word height — if too small, retry with upscale
            // Skip if no words at all (empty windows don't benefit from upscale)
            if (UpscaleThreshold > 0 && window.ContentOcr.Words.Count > 0)
            {
                double avgHeight = window.ContentOcr.Words.Average(w => w.Bounds.Height);
                if (avgHeight < UpscaleThreshold)
                {
                    window.NeedsUpscale = true;
                    await OcrEngine.OcrEngine.Instance.ProcessBitmapAsync(bitmap, window.ContentOcr, scaleFactor: 3, colorCount: 16);
                }
            }
        }

        // Stage 3: Layout (UIA → OCR-merge → Probe-merge, all automatic)
        window.Layout = await SceneBuilder.Instance.ProcessAsync(window.ContentOcr, window);

        // BSP debug data (only for OCR path)
        if (window.Layout.DataSource.StartsWith("OCR"))
            window.BspDebug = BspRegionDetector.DetectFull(window.ContentOcr);
        else
            window.BspDebug = null;

        // Release capture bitmap if nobody needs it (MCP Server mode)
        if (!KeepLastCapture)
        {
            window.SetLastCapture(null);
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }

        return PipelineResult.Success(window);
    }

    /// <summary>
    /// Run the full pipeline for a window identified by hwnd.
    /// Returns null if the window is not found.
    /// </summary>
    public async Task<PipelineResult?> ProcessAsync(nint hwnd)
    {
        var window = DesktopWindowList.Instance.FindByHwnd(hwnd);
        if (window == null) return null;

        return await ProcessAsync(window);
    }
}

/// <summary>
/// Result of a DesktopPipeline run.
/// </summary>
public class PipelineResult
{
    public DesktopWindow Window { get; init; } = null!;
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }

    public string ProcessName => Window.ProcessName;
    public int WordCount => Window.ContentOcr.Words.Count;
    public int LineCount => Window.ContentOcr.Lines.Count;
    public int RegionCount => Window.Layout?.Regions.Count ?? 0;
    public string DataSource => Window.Layout?.DataSource ?? "";

    public ProbeStats? Probe { get; init; }

    internal static PipelineResult Success(DesktopWindow window)
    {
        ProbeStats? probe = null;
        if (window.ProbeResults != null)
        {
            probe = new ProbeStats(
                window.ProbeResults.Count,
                window.ProbeOcrMatch?.PerfectMatches ?? 0,
                window.ProbeOcrMatch?.CorrectedMatches ?? 0,
                window.ProbeOcrMatch?.ProbeOnlyWords ?? 0);
        }

        return new PipelineResult
        {
            Window = window,
            IsSuccess = true,
            Probe = probe,
        };
    }

    internal static PipelineResult Failed(DesktopWindow window, string error)
        => new() { Window = window, IsSuccess = false, ErrorMessage = error };
}

/// <summary>Probe (TextPattern) statistics from a pipeline run.</summary>
public record ProbeStats(int ElementCount, int PerfectMatches, int CorrectedMatches, int ProbeOnlyWords);
