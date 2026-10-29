namespace WinfoMain.Common;

using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;
/// <summary>
/// Represents a single window on the desktop.
/// Central data structure shared across all modules.
/// </summary>
public class DesktopWindow
{
    public nint Hwnd { get; }
    public string ProcessName { get; internal set; } = "";
    public string Title { get; internal set; } = "";
    public Rectangle Bounds { get; internal set; }
    public uint ProcessId { get; internal set; }
    public int ZOrder { get; internal set; }
    public bool IsMinimized { get; internal set; }

    /// <summary>XxHash64 of the last captured bitmap. Used for change detection.</summary>
    public ulong ContentHash { get; set; }

    /// <summary>Window content from OCR (pixel-based text recognition).</summary>
    public WinContent ContentOcr { get; } = new();

    /// <summary>Unified layout result (UIA or OCR path). Primary output for consumers.</summary>
    public LayoutResult? Layout { get; set; }

    /// <summary>BSP debug data for DebugBitmapForm (threshold slider, cells visualization).</summary>
    public BspRegionDetector.BspResult? BspDebug { get; set; }

    /// <summary>UIA TextPattern probe results (text from editors/terminals).</summary>
    public List<UiaProbe.ProbeResult>? ProbeResults { get; set; }

    /// <summary>Probe-OCR matched words for pixel-accurate rendering.</summary>
    public ProbeOcrMatcher.MatchResult? ProbeOcrMatch { get; set; }

    /// <summary>
    /// When true, OCR pipeline uses 3x upscale + 16 colors for this window.
    /// Set automatically when average word height falls below the upscale threshold.
    /// Persists for the session — subsequent pipeline runs use upscale directly.
    /// </summary>
    public bool NeedsUpscale { get; set; }

    /// <summary>
    /// Last captured bitmap (for debug visualization).
    /// Use SetLastCapture() to safely swap — it disposes the old bitmap atomically.
    /// </summary>
    private Bitmap? _lastCapture;
    public Bitmap? LastCapture => _lastCapture;

    /// <summary>
    /// Thread-safe swap: atomically replaces LastCapture and disposes the old bitmap.
    /// Prevents leaks when BackgroundWorker and PipelineHelper run concurrently.
    /// </summary>
    public void SetLastCapture(Bitmap? newCapture)
    {
        var old = Interlocked.Exchange(ref _lastCapture, newCapture);
        if (old != null && !ReferenceEquals(old, newCapture))
            old.Dispose();
    }

    public DesktopWindow(nint hwnd)
    {
        Hwnd = hwnd;
    }
}

/// <summary>
/// The single source of truth for all visible windows on the desktop.
/// Enumerates top-level windows via Win32 API. All modules read from this list
/// instead of querying the OS independently.
/// </summary>
public class DesktopWindowList
{
    public static DesktopWindowList Instance { get; } = new();
    private DesktopWindowList() { }

    private readonly ConcurrentDictionary<nint, DesktopWindow> _windows = new();

    /// <summary>All tracked windows.</summary>
    public IReadOnlyList<DesktopWindow> All => _windows.Values.ToList();

    /// <summary>Find a window by its native handle.</summary>
    public DesktopWindow? FindByHwnd(nint hwnd) =>
        _windows.TryGetValue(hwnd, out var w) ? w : null;

    /// <summary>Find a window by process name (first match, case-insensitive).</summary>
    public DesktopWindow? FindByProcessName(string name) =>
        _windows.Values.FirstOrDefault(w =>
            w.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Update only the Bounds (and IsMinimized) of a single known window in-place.
    /// Much cheaper than a full Refresh() — no EnumWindows, no process queries.
    /// </summary>
    public void UpdateBounds(nint hwnd)
    {
        if (_windows.TryGetValue(hwnd, out var window))
        {
            window.Bounds = GetWindowBounds(hwnd);
            window.IsMinimized = IsIconic(hwnd);
        }
    }

    /// <summary>
    /// Refresh the window list from the OS. Adds new windows, updates existing,
    /// removes windows that no longer exist.
    /// </summary>
    public void Refresh()
    {
        var current = new HashSet<nint>();
        int zOrder = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (!HasTitle(hwnd) && !IsAppWindow(hwnd)) return true;

            current.Add(hwnd);

            var window = _windows.GetOrAdd(hwnd, h => new DesktopWindow(h));
            window.ZOrder = zOrder++;
            window.IsMinimized = IsIconic(hwnd);
            window.Title = GetWindowTitle(hwnd);
            window.Bounds = GetWindowBounds(hwnd);

            if (string.IsNullOrEmpty(window.ProcessName))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                window.ProcessId = pid;
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    window.ProcessName = proc.ProcessName;
                }
                catch (Exception ex) { WinfoLog.Write($"[WinfoMain] GetProcessById({pid}) failed: {ex}"); window.ProcessName = $"pid:{pid}"; }
            }

            return true;
        }, 0);

        // Remove windows that no longer exist — dispose bitmaps to prevent memory leaks
        foreach (var hwnd in _windows.Keys)
        {
            if (!current.Contains(hwnd))
            {
                if (_windows.TryRemove(hwnd, out var removed))
                    removed.SetLastCapture(null);
            }
        }
    }

    // --- Win32 Interop ---

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder sb, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static string GetWindowTitle(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static Rectangle GetWindowBounds(nint hwnd)
    {
        GetWindowRect(hwnd, out RECT r);
        return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private static bool HasTitle(nint hwnd) => GetWindowTextLength(hwnd) > 0;

    private static bool IsAppWindow(nint hwnd)
    {
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        return (exStyle & WS_EX_APPWINDOW) != 0 && (exStyle & WS_EX_TOOLWINDOW) == 0;
    }
}
