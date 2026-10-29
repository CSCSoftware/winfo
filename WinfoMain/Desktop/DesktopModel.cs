namespace WinfoMain.Desktop;

using System.Runtime.InteropServices;
using WinfoMain.Common;

/// <summary>
/// Live Desktop Model — maintains an always-up-to-date collection of window snapshots.
/// Orchestrates DesktopEventHook + BackgroundWorker with event-driven background updates.
///
/// Usage:
///   var model = new DesktopModel();
///   model.Start();   // on UI thread (for WinEvent hooks)
///   model.Stop();
///   model.Dispose();
/// </summary>
public class DesktopModel : IDisposable
{
    private volatile DesktopEventHook? _hook;
    private volatile BackgroundWorker? _worker;
    private Timer? _fullScanTimer;
    private Timer? _activeWindowTimer;
    private Timer? _autoSleepTimer;
    private bool _disposed;
    private volatile bool _sleeping;

    /// <summary>Currently active (foreground) window.</summary>
    public volatile DesktopWindow? ActiveWindow;

    /// <summary>Whether the model is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Whether the model is sleeping (timers paused, queue paused).</summary>
    public bool IsSleeping => _sleeping;

    /// <summary>Skip windows from our own process.</summary>
    public bool IgnoreOwnProcess { get; set; } = true;

    /// <summary>
    /// Keep LastCapture bitmap on windows after processing.
    /// False for MCP Server (saves ~20-90MB per window). Must be set before Start().
    /// </summary>
    public bool KeepLastCapture { get; set; } = true;

    /// <summary>Process names to ignore (case-insensitive). Managed via GUI toggle.</summary>
    public HashSet<string> IgnoredProcessNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Interval for periodic hash-check of all visible windows.</summary>
    public TimeSpan FullScanInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval for polling the active window (fallback for apps without scroll events).
    /// Set to 1s because Electron/Chromium apps don't fire UIA scroll events.</summary>
    public TimeSpan ActiveWindowPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Time of inactivity before auto-sleeping. Set to Zero to disable auto-sleep.</summary>
    public TimeSpan AutoSleepTimeout { get; set; } = TimeSpan.FromMinutes(2);

    // --- Events ---

    /// <summary>A window's content was updated (OCR pipeline complete).</summary>
    public event Action<DesktopWindow>? WindowChanged;

    /// <summary>A new window appeared.</summary>
    public event Action<DesktopWindow>? WindowAdded;

    /// <summary>A window was destroyed.</summary>
    public event Action<nint>? WindowRemoved;

    /// <summary>The active (foreground) window changed.</summary>
    public event Action<DesktopWindow>? ActiveWindowChanged;

    /// <summary>Diagnostic log messages.</summary>
    public event Action<string>? Log;

    // --- Lifecycle ---

    /// <summary>
    /// Start the live desktop model. MUST be called on the UI thread (for WinEvent hooks).
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        // Initial window list
        DesktopWindowList.Instance.Refresh();
        RaiseLog($"Start: {DesktopWindowList.Instance.All.Count} windows");

        // Detect initial foreground window (skip if it's our own process)
        var fgHwnd = GetForegroundWindow();
        var fgWindow = fgHwnd != IntPtr.Zero ? DesktopWindowList.Instance.FindByHwnd(fgHwnd) : null;
        if (fgWindow != null && ShouldIgnore(fgWindow))
        {
            fgWindow = null;
            fgHwnd = IntPtr.Zero;
        }
        ActiveWindow = fgWindow;

        // BackgroundWorker
        _worker = new BackgroundWorker();
        _worker.ActiveHwnd = fgHwnd;
        _worker.KeepLastCapture = KeepLastCapture;
        _worker.WindowUpdated += OnWindowUpdated;
        _worker.Log += RaiseLog;
        _worker.Start();

        // EventHook (must be on UI thread)
        _hook = new DesktopEventHook();
        _hook.ForegroundChanged += OnForegroundChanged;
        _hook.WindowCreated += OnWindowCreated;
        _hook.WindowDestroyed += OnWindowDestroyed;
        _hook.WindowMoved += OnWindowMoved;
        _hook.WindowRestored += OnWindowRestored;
        _hook.ContentScrolled += OnContentChanged;
        _hook.ValueChanged += OnContentChanged;
        _hook.Start();

        // Full-scan timer: periodic hash check of all visible windows
        _fullScanTimer = new Timer(_ => EnqueueFullScan(),
            null, FullScanInterval, FullScanInterval);

        // Active-window poll timer: fallback for apps without scroll events
        _activeWindowTimer = new Timer(_ => PollActiveWindow(),
            null, ActiveWindowPollInterval, ActiveWindowPollInterval);

        // Auto-sleep timer: sleeps after inactivity
        if (AutoSleepTimeout > TimeSpan.Zero)
        {
            _autoSleepTimer = new Timer(_ => Sleep(),
                null, AutoSleepTimeout, Timeout.InfiniteTimeSpan);
        }

        IsRunning = true;
    }

    /// <summary>Stop the live desktop model.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _fullScanTimer?.Dispose();
        _fullScanTimer = null;

        _activeWindowTimer?.Dispose();
        _activeWindowTimer = null;

        _autoSleepTimer?.Dispose();
        _autoSleepTimer = null;

        _hook?.Stop();
        _hook?.Dispose();
        _hook = null;

        _worker?.Stop();
        _worker?.Dispose();
        _worker = null;

        RaiseLog("Stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Enter sleep mode: pause timers and background processing. EventHooks stay active
    /// to track window list changes. Near-zero CPU usage while sleeping.
    /// </summary>
    public void Sleep()
    {
        if (!IsRunning || _sleeping) return;
        _sleeping = true;

        _fullScanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _activeWindowTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _autoSleepTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        _worker?.Pause();

        RaiseLog("Sleep — timers paused, worker paused");
    }

    /// <summary>
    /// Wake from sleep: resume timers and background processing.
    /// Resets auto-sleep countdown.
    /// </summary>
    public void Wake()
    {
        if (!IsRunning || !_sleeping)
        {
            // Even if not sleeping, reset auto-sleep timer on activity
            ResetAutoSleepTimer();
            return;
        }
        _sleeping = false;

        _worker?.Resume();

        _fullScanTimer?.Change(FullScanInterval, FullScanInterval);
        _activeWindowTimer?.Change(ActiveWindowPollInterval, ActiveWindowPollInterval);
        ResetAutoSleepTimer();

        // Refresh window list and enqueue ALL windows immediately
        DesktopWindowList.Instance.Refresh();
        EnqueueFullScan();

        RaiseLog("Wake — timers resumed, worker resumed, full scan enqueued");
    }

    /// <summary>Force a full refresh of the window list and enqueue all for update.</summary>
    public void ForceRefresh()
    {
        DesktopWindowList.Instance.Refresh();
        EnqueueFullScan();
    }

    /// <summary>Force re-processing of a specific window (resets hash to trigger full pipeline).</summary>
    public void ForceUpdate(DesktopWindow window)
    {
        window.ContentHash = 0;
        _worker?.Enqueue(window.Hwnd);
    }

    /// <summary>
    /// Ensures a window has been processed (has OCR content).
    /// If not, triggers the pipeline and waits for completion.
    /// Used by MCP Server tools and cursor hit-test for on-demand updates.
    /// </summary>
    public async Task EnsureUpdatedAsync(DesktopWindow window, TimeSpan? timeout = null)
    {
        if (window.ContentOcr.Words.Count > 0)
            return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnDone(DesktopWindow w)
        {
            if (w.Hwnd == window.Hwnd)
                tcs.TrySetResult(true);
        }

        WindowChanged += OnDone;
        try
        {
            ForceUpdate(window);
            var timeoutMs = (int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
            if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task)
                RaiseLog($"EnsureUpdated timeout [{window.ProcessName}]");
        }
        finally
        {
            WindowChanged -= OnDone;
        }
    }

    // --- Event Handlers ---

    private void OnForegroundChanged(nint hwnd)
    {
        // Ensure window exists in our list
        var list = DesktopWindowList.Instance;
        var window = list.FindByHwnd(hwnd);
        if (window == null)
        {
            // New window we haven't seen yet (elevated apps, etc.)
            list.Refresh();
            window = list.FindByHwnd(hwnd);
        }

        if (window == null) return;
        if (ShouldIgnore(window)) return;

        ActiveWindow = window;
        if (_worker != null) _worker.ActiveHwnd = hwnd;
        ActiveWindowChanged?.Invoke(window);

        // Enqueue for immediate update
        _worker?.Enqueue(hwnd);
    }

    private void OnWindowCreated(nint hwnd)
    {
        // Brief delay — window might not be fully initialized yet
        Task.Delay(200).ContinueWith(_ =>
        {
            var list = DesktopWindowList.Instance;
            list.Refresh();
            var window = list.FindByHwnd(hwnd);
            if (window == null) return;
            if (ShouldIgnore(window)) return;

            WindowAdded?.Invoke(window);
            _worker?.Enqueue(hwnd);
        }, TaskScheduler.Default);
    }

    private void OnWindowDestroyed(nint hwnd)
    {
        WindowRemoved?.Invoke(hwnd);
        DesktopWindowList.Instance.Refresh();
    }

    private void OnWindowMoved(nint hwnd)
    {
        // Only update bounds in-place — no full EnumWindows scan needed for move/resize
        DesktopWindowList.Instance.UpdateBounds(hwnd);
        _worker?.Enqueue(hwnd);
    }

    private void OnWindowRestored(nint hwnd)
    {
        DesktopWindowList.Instance.Refresh();
        var window = DesktopWindowList.Instance.FindByHwnd(hwnd);
        if (window != null)
        {
            window.ContentHash = 0; // Force re-capture after restore
            _worker?.Enqueue(hwnd);
        }
    }

    private void OnContentChanged(nint hwnd)
    {
        _worker?.Enqueue(hwnd);
    }

    private void OnWindowUpdated(DesktopWindow window)
    {
        WindowChanged?.Invoke(window);
    }

    // --- Timer Callbacks ---

    private void EnqueueFullScan()
    {
        if (_sleeping) return;

        foreach (var window in DesktopWindowList.Instance.All)
        {
            if (window.IsMinimized) continue;
            if (ShouldIgnore(window)) continue;

            _worker?.Enqueue(window.Hwnd);
        }
    }

    private void PollActiveWindow()
    {
        if (_sleeping) return;

        var window = ActiveWindow;
        if (window == null || ShouldIgnore(window)) return;
        _worker?.Enqueue(window.Hwnd);
    }

    private void ResetAutoSleepTimer()
    {
        if (AutoSleepTimeout > TimeSpan.Zero)
            _autoSleepTimer?.Change(AutoSleepTimeout, Timeout.InfiniteTimeSpan);
    }

    // --- Helpers ---

    /// <summary>Central filter: should this window be ignored?</summary>
    private bool ShouldIgnore(DesktopWindow window)
    {
        if (IgnoreOwnProcess && window.ProcessId == (uint)Environment.ProcessId) return true;
        if (IgnoredProcessNames.Contains(window.ProcessName)) return true;
        return false;
    }

    private void RaiseLog(string message)
    {
        Log?.Invoke(message);
    }

    // --- P/Invoke ---

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
