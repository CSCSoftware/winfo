namespace WinfoMain.Desktop;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

/// <summary>
/// Monitors Windows desktop events via SetWinEventHook.
/// Fires debounced events for foreground changes, window create/destroy,
/// move/resize, minimize/restore, scroll, and value changes.
/// Must be started on a thread with a message pump (WinForms UI thread).
/// </summary>
public class DesktopEventHook : IDisposable
{
    // --- Public events ---

    public event Action<nint>? ForegroundChanged;
    public event Action<nint>? WindowCreated;
    public event Action<nint>? WindowDestroyed;
    public event Action<nint>? WindowMoved;
    public event Action<nint>? WindowRestored;
    public event Action<nint>? ContentScrolled;
    public event Action<nint>? ValueChanged;

    // --- Win32 constants ---

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_SCROLLINGEND = 0x0013;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_VALUECHANGE = 0x800E;
    private const uint EVENT_OBJECT_CONTENTSCROLLED = 0x8015;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private const int DEBOUNCE_LOCATION_MS = 150;
    private const int DEBOUNCE_SCROLL_MS = 100;
    private const int DEBOUNCE_VALUE_MS = 100;

    // --- State ---

    private readonly uint _ownPid;
    private readonly ConcurrentDictionary<(nint hwnd, uint eventType), CancellationTokenSource> _debounceTimers = new();
    private readonly List<nint> _hooks = [];
    private WinEventDelegate? _callback;
    private volatile bool _running;
    private bool _disposed;

    public DesktopEventHook()
    {
        _ownPid = (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Start listening for desktop events.
    /// MUST be called on a thread with a message pump (WinForms UI thread).
    /// </summary>
    public void Start()
    {
        if (_running) return;

        _callback = OnWinEvent;

        _hooks.Add(SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _hooks.Add(SetWinEventHook(
            EVENT_SYSTEM_SCROLLINGEND, EVENT_SYSTEM_SCROLLINGEND,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _hooks.Add(SetWinEventHook(
            EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _hooks.Add(SetWinEventHook(
            EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _hooks.Add(SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _hooks.Add(SetWinEventHook(
            EVENT_OBJECT_VALUECHANGE, EVENT_OBJECT_VALUECHANGE,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _hooks.Add(SetWinEventHook(
            EVENT_OBJECT_CONTENTSCROLLED, EVENT_OBJECT_CONTENTSCROLLED,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _hooks.RemoveAll(h => h == IntPtr.Zero);
        _running = true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        foreach (var hook in _hooks)
            UnhookWinEvent(hook);
        _hooks.Clear();

        foreach (var kvp in _debounceTimers)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _debounceTimers.Clear();

        _callback = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    // --- Core callback ---

    private void OnWinEvent(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_running || hwnd == IntPtr.Zero) return;

        // Only top-level window events (exception: VALUE_CHANGE from child elements)
        if (eventType != EVENT_OBJECT_VALUECHANGE)
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        }

        // For VALUE_CHANGE on child elements, resolve to top-level window
        var topHwnd = hwnd;
        if (eventType == EVENT_OBJECT_VALUECHANGE && (idObject != OBJID_WINDOW || idChild != CHILDID_SELF))
        {
            topHwnd = GetAncestor(hwnd, GA_ROOT);
            if (topHwnd == IntPtr.Zero) return;
        }

        // Skip own process
        GetWindowThreadProcessId(topHwnd, out var pid);
        if (pid == _ownPid) return;

        // Filter: only relevant top-level windows
        if (!IsRelevantWindow(topHwnd, eventType)) return;

        switch (eventType)
        {
            case EVENT_SYSTEM_FOREGROUND:
                ForegroundChanged?.Invoke(topHwnd);
                break;

            case EVENT_OBJECT_CREATE:
                WindowCreated?.Invoke(topHwnd);
                break;

            case EVENT_OBJECT_DESTROY:
                WindowDestroyed?.Invoke(topHwnd);
                break;

            case EVENT_OBJECT_LOCATIONCHANGE:
                Debounce(topHwnd, eventType, DEBOUNCE_LOCATION_MS,
                    () => WindowMoved?.Invoke(topHwnd));
                break;

            case EVENT_SYSTEM_MINIMIZESTART:
                break; // Minimized: no subscriber — handled by IsMinimized checks elsewhere

            case EVENT_SYSTEM_MINIMIZEEND:
                WindowRestored?.Invoke(topHwnd);
                break;

            case EVENT_SYSTEM_SCROLLINGEND:
            case EVENT_OBJECT_CONTENTSCROLLED:
                Debounce(topHwnd, eventType, DEBOUNCE_SCROLL_MS,
                    () => ContentScrolled?.Invoke(topHwnd));
                break;

            case EVENT_OBJECT_VALUECHANGE:
                Debounce(topHwnd, eventType, DEBOUNCE_VALUE_MS,
                    () => ValueChanged?.Invoke(topHwnd));
                break;
        }
    }

    // --- Debouncing ---

    private void Debounce(nint hwnd, uint eventType, int delayMs, Action action)
    {
        var key = (hwnd, eventType);

        if (_debounceTimers.TryRemove(key, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceTimers[key] = cts;

        _ = Task.Delay(delayMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (_debounceTimers.TryRemove(key, out var completed))
                completed.Dispose();
            action();
        }, TaskScheduler.Default);
    }

    // --- Window filtering ---

    private static bool IsRelevantWindow(nint hwnd, uint eventType)
    {
        // CREATE/DESTROY: don't filter heavily — window might not have styles set yet
        if (eventType == EVENT_OBJECT_CREATE || eventType == EVENT_OBJECT_DESTROY)
        {
            var style = (uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE);
            return (style & WS_CHILD) == 0;
        }

        // FOREGROUND: trust the OS, only reject child windows
        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            var style = (uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE);
            return (style & WS_CHILD) == 0;
        }

        // Other events: must be visible, not child, not tool window
        {
            var style = (uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_CHILD) != 0) return false;
            if ((style & WS_VISIBLE) == 0) return false;

            var exStyle = (uint)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
        }

        return true;
    }

    // --- P/Invoke ---

    private delegate void WinEventDelegate(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess,
        uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
