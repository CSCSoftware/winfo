namespace WinfoMain.Desktop;

using System.Collections.Concurrent;
using WinfoMain.Common;
using OcrEngineClass = WinfoMain.Desktop.OcrEngine.OcrEngine;
using WinfoMain.Desktop.SceneEngine;

/// <summary>
/// Background worker that processes window updates with FIFO queue + dedup.
/// Pipeline: Capture → Hash → Settle → Capture stable → OCR → Layout → Fire Event.
/// </summary>
public class BackgroundWorker : IDisposable
{
    private readonly ConcurrentQueue<nint> _queue = new();
    private readonly ConcurrentDictionary<nint, bool> _pending = new();
    private readonly ConcurrentDictionary<nint, DateTime> _lastUpdateTime = new();

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private bool _disposed;
    private volatile bool _paused;
    private readonly ManualResetEventSlim _pauseGate = new(true);

    /// <summary>Fired when a window's content has been updated (after full pipeline).</summary>
    public event Action<DesktopWindow>? WindowUpdated;

    /// <summary>Diagnostic log messages from pipeline stages.</summary>
    public event Action<string>? Log;

    /// <summary>Handle of the active (foreground) window — bypasses throttling.</summary>
    public nint ActiveHwnd { get; set; }

    /// <summary>Minimum interval between updates for the same non-active window.</summary>
    public TimeSpan ThrottlePerWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Keep LastCapture bitmap on the window after processing.
    /// True for Test-App (debug visualization), false for MCP Server (saves ~20-90MB per window).
    /// </summary>
    public bool KeepLastCapture { get; set; } = true;

    /// <summary>Pause processing — worker loop blocks until Resume() is called. Drains the queue.</summary>
    public void Pause()
    {
        _paused = true;
        _pauseGate.Reset();

        // Drain queue — stale events from Sleep are useless, Wake triggers fresh scans
        while (_queue.TryDequeue(out _)) { }
        _pending.Clear();
    }

    /// <summary>Resume processing after Pause().</summary>
    public void Resume()
    {
        _paused = false;
        _pauseGate.Set();
    }

    /// <summary>Add a window to the update queue. Deduplicates: same hwnd is only queued once.</summary>
    public void Enqueue(nint hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;

        // Dedup: only enqueue if not already pending
        if (_pending.TryAdd(hwnd, true))
        {
            _queue.Enqueue(hwnd);
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _workerTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }

        _cts?.Dispose();
        _cts = null;
        _workerTask = null;

        _pending.Clear();
        while (_queue.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Resume(); // Unblock worker loop if paused
        Stop();
        _pauseGate.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Worker loop ---

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Block while paused (Sleep mode)
            if (_paused)
            {
                try { await Task.Run(() => _pauseGate.Wait(ct), ct); }
                catch (OperationCanceledException) { break; }
            }

            if (!_queue.TryDequeue(out var hwnd))
            {
                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _pending.TryRemove(hwnd, out _);

            // Throttle: skip if updated recently (unless active window)
            if (hwnd != ActiveHwnd && _lastUpdateTime.TryGetValue(hwnd, out var lastTime))
            {
                if (DateTime.UtcNow - lastTime < ThrottlePerWindow)
                    continue;
            }

            // Resolve to DesktopWindow
            var window = DesktopWindowList.Instance.FindByHwnd(hwnd);
            if (window == null || window.IsMinimized) continue;

            try
            {
                await ProcessWindowAsync(window, ct);

                // Force GC to reclaim large bitmaps from LOH (OCR pipeline allocates
                // 3x-scaled + color-reduced + SoftwareBitmap byte[] per window).
                // Without this, LOH fragments accumulate across 12+ windows and spike to 2+ GB.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Worker error [{window.ProcessName}]: {ex.ToString()}");
            }
        }
    }

    // --- Pipeline ---

    private async Task ProcessWindowAsync(DesktopWindow window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Stage 1: Capture + Hash
        using var bitmap = WindowCapture.Capture(window.Hwnd);
        if (bitmap == null) return;

        var newHash = WindowCapture.ComputeHash(bitmap);
        if (newHash == window.ContentHash && window.ContentHash != 0)
            return; // Content unchanged

        window.ContentHash = newHash;
        Log?.Invoke($"Changed [{window.ProcessName}] hash={newHash:X8}");

        // Stage 2: Settle — wait for content to stop changing (max 3x150ms = 450ms)
        await SettleAsync(window, ct);

        // Stage 3: Capture stable bitmap (after Settle) for all downstream processing
        ct.ThrowIfCancellationRequested();
        var stableBitmap = WindowCapture.Capture(window.Hwnd);
        if (stableBitmap == null) return;

        // Store on window — replaces previous capture
        window.SetLastCapture(stableBitmap);

        // Stage 4: OCR (with upscale support for small text)
        try
        {
            if (window.NeedsUpscale)
            {
                await OcrEngineClass.Instance.ProcessBitmapAsync(stableBitmap, window.ContentOcr, scaleFactor: 3, colorCount: 16);
            }
            else
            {
                await OcrEngineClass.Instance.ProcessBitmapAsync(stableBitmap, window.ContentOcr);

                if (DesktopPipeline.UpscaleThreshold > 0 && window.ContentOcr.Words.Count > 0)
                {
                    double avgHeight = window.ContentOcr.Words.Average(w => w.Bounds.Height);
                    if (avgHeight < DesktopPipeline.UpscaleThreshold)
                    {
                        window.NeedsUpscale = true;
                        await OcrEngineClass.Instance.ProcessBitmapAsync(stableBitmap, window.ContentOcr, scaleFactor: 3, colorCount: 16);
                        Log?.Invoke($"Upscale [{window.ProcessName}]: 3x + 16 colors");
                    }
                }
            }
            Log?.Invoke($"OCR [{window.ProcessName}]: {window.ContentOcr.Words.Count} words");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"OCR error [{window.ProcessName}]: {ex.ToString()}");
        }

        // Stage 5: Layout Analysis (UIA probe → UIA or OCR path)
        try
        {
            window.Layout = await SceneBuilder.Instance.ProcessAsync(window.ContentOcr, window);
            Log?.Invoke($"Layout [{window.ProcessName}]: {window.Layout.DataSource}, {window.Layout.Regions.Count} regions");

            // Store BSP debug data for DebugBitmapForm (threshold slider, cells)
            if (window.Layout.DataSource == "OCR")
            {
                window.BspDebug = BspRegionDetector.DetectFull(window.ContentOcr);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Layout error [{window.ProcessName}]: {ex.ToString()}");
        }

        // Done — release LastCapture if nobody needs it (MCP Server mode)
        if (!KeepLastCapture)
            window.SetLastCapture(null);

        _lastUpdateTime[window.Hwnd] = DateTime.UtcNow;
        WindowUpdated?.Invoke(window);
    }

    /// <summary>
    /// Wait for content to stop changing. Prevents stale processing during rapid scrolling.
    /// Max 3 iterations (~450ms).
    /// </summary>
    private async Task SettleAsync(DesktopWindow window, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct);

            using var checkBmp = WindowCapture.Capture(window.Hwnd);
            if (checkBmp == null) return;

            var checkHash = WindowCapture.ComputeHash(checkBmp);
            if (checkHash == window.ContentHash)
            {
                Log?.Invoke($"Settled [{window.ProcessName}] after {i + 1} iteration(s)");
                return;
            }

            // Still changing — update hash and loop
            window.ContentHash = checkHash;
        }

        Log?.Invoke($"Settle timeout [{window.ProcessName}] — proceeding anyway");
    }
}
