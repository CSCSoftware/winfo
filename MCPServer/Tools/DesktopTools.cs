namespace WinfoMain.MCPServer.Tools;

using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using ModelContextProtocol.Server;
using WinfoMain.MCP;

/// <summary>
/// Desktop tools for window management: move, resize, snap, show/hide, close, and monitor listing.
/// </summary>
[McpServerToolType]
public static class DesktopTools
{
    [McpServerTool(Name = "move_window")]
    [Description("Move and/or resize a window. Restores the window first if minimized or maximized.")]
    public static string MoveWindow(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd.")] string window,
        [Description("X position (screen coordinates)")] int x,
        [Description("Y position (screen coordinates)")] int y,
        [Description("Optional: new width. Omit to keep current size.")] int? width = null,
        [Description("Optional: new height. Omit to keep current size.")] int? height = null)
    {
        PipelineHelper.Wake();
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        if (!IsWindow(win.Hwnd))
            return "Cannot move: invalid window handle.";

        // Restore if maximized or minimized
        if (IsIconic(win.Hwnd) || IsZoomed(win.Hwnd))
            ShowWindowNative(win.Hwnd, SW_RESTORE);

        GetWindowRect(win.Hwnd, out var rect);
        int w = width ?? rect.Width;
        int h = height ?? rect.Height;

        MoveWindowNative(win.Hwnd, x, y, w, h, true);
        return $"Moved {ElementSerializer.FormatWindowHeader(win)} to ({x}, {y}) {w}x{h}";
    }

    [McpServerTool(Name = "show_window")]
    [Description("Change window state: minimize, maximize, restore, hide, or show.")]
    public static string ShowWindowState(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd.")] string window,
        [Description("State: 'minimize', 'maximize', 'restore', 'hide', 'show'")] string state)
    {
        PipelineHelper.Wake();
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        if (!IsWindow(win.Hwnd))
            return "Cannot change state: invalid window handle.";

        var cmd = state.ToLowerInvariant() switch
        {
            "minimize" or "min" => SW_MINIMIZE,
            "maximize" or "max" => SW_MAXIMIZE,
            "restore" => SW_RESTORE,
            "hide" => SW_HIDE,
            "show" => SW_SHOW,
            _ => -1
        };

        if (cmd == -1)
            return $"Unknown state '{state}'. Use: minimize, maximize, restore, hide, show.";

        ShowWindowNative(win.Hwnd, cmd);
        return $"{char.ToUpper(state[0])}{state[1..].ToLower()}d {ElementSerializer.FormatWindowHeader(win)}";
    }

    [McpServerTool(Name = "snap_window")]
    [Description("Snap a window to a screen position: left, right, top, bottom, topleft, topright, bottomleft, bottomright, center, or full.")]
    public static string SnapWindow(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd.")] string window,
        [Description("Position: left, right, top, bottom, topleft, topright, bottomleft, bottomright, center, full")] string position,
        [Description("Optional: monitor index (0-based). Default: current monitor.")] int? monitor = null)
    {
        PipelineHelper.Wake();
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        if (!IsWindow(win.Hwnd))
            return "Cannot snap: invalid window handle.";

        // Get target monitor work area
        var monitors = GetMonitorList();
        Rectangle workArea;

        if (monitor.HasValue)
        {
            if (monitor.Value < 0 || monitor.Value >= monitors.Count)
                return $"Monitor #{monitor.Value} not found. Available: 0-{monitors.Count - 1}";
            workArea = monitors[monitor.Value].WorkArea;
        }
        else
        {
            // Use the monitor the window is currently on
            var hMon = MonitorFromWindow(win.Hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = MONITORINFOEX.Create();
            GetMonitorInfo(hMon, ref mi);
            workArea = new Rectangle(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height);
        }

        var (sx, sy, sw, sh) = CalculateSnapPosition(position.ToLowerInvariant(), workArea);
        if (sw == 0)
            return $"Unknown position '{position}'. Use: left, right, top, bottom, topleft, topright, bottomleft, bottomright, center, full.";

        if (IsIconic(win.Hwnd) || IsZoomed(win.Hwnd))
            ShowWindowNative(win.Hwnd, SW_RESTORE);

        MoveWindowNative(win.Hwnd, sx, sy, sw, sh, true);

        var monLabel = monitor.HasValue ? $" on monitor #{monitor.Value}" : "";
        return $"Snapped {ElementSerializer.FormatWindowHeader(win)} to {position}{monLabel} ({sx},{sy} {sw}x{sh})";
    }

    [McpServerTool(Name = "list_monitors")]
    [Description("List all connected monitors with resolution, position, and work area.")]
    public static string ListMonitors()
    {
        var monitors = GetMonitorList();

        if (monitors.Count == 0)
            return "No monitors found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{monitors.Count} monitor(s):");

        foreach (var mon in monitors)
        {
            var primary = mon.IsPrimary ? " (primary)" : "";
            sb.AppendLine($"  #{mon.Index}{primary} {mon.Bounds.Width}x{mon.Bounds.Height} @{mon.Bounds.X},{mon.Bounds.Y} WorkArea: {mon.WorkArea.Width}x{mon.WorkArea.Height} @{mon.WorkArea.X},{mon.WorkArea.Y} \"{mon.DeviceName}\"");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "close_window")]
    [Description("Close a window by sending WM_CLOSE. The application may prompt to save unsaved work.")]
    public static string CloseWindow(
        [Description("Window specifier: 'active', process name, title substring, or hex hwnd.")] string window)
    {
        PipelineHelper.Wake();
        var (win, error) = WindowResolver.Resolve(window);
        if (win == null)
            return error!;

        if (!IsWindow(win.Hwnd))
            return "Cannot close: invalid window handle.";

        PostMessage(win.Hwnd, WM_CLOSE, 0, 0);
        return $"Sent WM_CLOSE to {ElementSerializer.FormatWindowHeader(win)}";
    }

    // === Helpers ===

    private static List<(int Index, bool IsPrimary, Rectangle Bounds, Rectangle WorkArea, string DeviceName)> GetMonitorList()
    {
        var monitors = new List<(int Index, bool IsPrimary, Rectangle Bounds, Rectangle WorkArea, string DeviceName)>();
        int index = 0;

        EnumDisplayMonitors(0, 0, (nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData) =>
        {
            var mi = MONITORINFOEX.Create();
            GetMonitorInfo(hMonitor, ref mi);

            bool isPrimary = (mi.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
            var bounds = new Rectangle(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height);
            var workArea = new Rectangle(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height);

            monitors.Add((index++, isPrimary, bounds, workArea, mi.szDevice?.Trim('\0') ?? ""));
            return true;
        }, 0);

        return monitors;
    }

    private static (int x, int y, int w, int h) CalculateSnapPosition(string position, Rectangle work)
    {
        int halfW = work.Width / 2;
        int halfH = work.Height / 2;

        return position switch
        {
            "left"        => (work.X, work.Y, halfW, work.Height),
            "right"       => (work.X + halfW, work.Y, halfW, work.Height),
            "top"         => (work.X, work.Y, work.Width, halfH),
            "bottom"      => (work.X, work.Y + halfH, work.Width, halfH),
            "topleft"     => (work.X, work.Y, halfW, halfH),
            "topright"    => (work.X + halfW, work.Y, halfW, halfH),
            "bottomleft"  => (work.X, work.Y + halfH, halfW, halfH),
            "bottomright" => (work.X + halfW, work.Y + halfH, halfW, halfH),
            "center"      => (work.X + work.Width / 4, work.Y + work.Height / 4, halfW, halfH),
            "full"        => (work.X, work.Y, work.Width, work.Height),
            _ => (0, 0, 0, 0)
        };
    }

    // === Constants ===

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE  = 9;
    private const int SW_HIDE     = 0;
    private const int SW_SHOW     = 5;
    private const uint WM_CLOSE   = 0x0010;

    // === Structs ===

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;

        public static MONITORINFOEX Create()
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            return mi;
        }
    }

    // === P/Invoke ===

    [DllImport("user32.dll", EntryPoint = "MoveWindow")]
    private static extern bool MoveWindowNative(nint hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindowNative(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);
}
