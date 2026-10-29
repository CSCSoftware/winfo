namespace WinfoMain.MCPServer.Tools;

using System.Collections.Concurrent;
using WinfoMain.Common;
using WinfoMain.Desktop;
using WinfoMain.MCPServer.Services;

/// <summary>
/// Shared pipeline infrastructure for MCP tools.
/// Per-window locking prevents concurrent pipeline runs on the same window.
/// Automatically wakes the DesktopModel from sleep on every tool call.
/// </summary>
internal static class PipelineHelper
{
    private static readonly ConcurrentDictionary<nint, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Wake the DesktopModel from sleep (if sleeping).
    /// Called by tools that need live desktop data.
    /// Resets the auto-sleep countdown.
    /// </summary>
    internal static void Wake()
    {
        DesktopModelHostedService.Instance?.Model.Wake();
    }

    /// <summary>
    /// Ensure layout is available (skip if already present).
    /// Returns null on success, error message on failure.
    /// </summary>
    internal static async Task<string?> EnsureLayoutAsync(DesktopWindow win)
    {
        Wake();

        if (win.Layout != null)
            return null;

        return await RunPipelineAsync(win);
    }

    /// <summary>
    /// Always run a fresh pipeline (capture + OCR + layout).
    /// Returns null on success, error message on failure.
    /// </summary>
    internal static async Task<string?> RunPipelineAsync(DesktopWindow win)
    {
        Wake();

        var semaphore = _locks.GetOrAdd(win.Hwnd, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            var pipeline = new DesktopPipeline { KeepLastCapture = false };
            var result = await pipeline.ProcessAsync(win);
            if (!result.IsSuccess)
                return $"Pipeline failed for [{win.ProcessName}]: {result.ErrorMessage}";

            return null;
        }
        catch (Exception ex)
        {
            return $"Pipeline error for [{win.ProcessName}]: {ex.Message}";
        }
        finally
        {
            semaphore.Release();
        }
    }
}
