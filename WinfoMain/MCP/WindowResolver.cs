namespace WinfoMain.MCP;

using WinfoMain.Common;
using System.Runtime.InteropServices;

/// <summary>
/// Resolves a window specifier string to a DesktopWindow.
/// Supported formats:
///   "active"          — current foreground window
///   "notepad"         — match by process name (case-insensitive)
///   "My Document"     — match by title substring (case-insensitive)
///   "0x1A2B3C"        — match by hwnd (hex)
/// </summary>
public static class WindowResolver
{
    /// <summary>
    /// Resolve a window specifier to a DesktopWindow.
    /// Returns (window, null) on success, or (null, errorMessage) on failure.
    /// </summary>
    public static (DesktopWindow? Window, string? Error) Resolve(string? specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier) || specifier.Equals("active", StringComparison.OrdinalIgnoreCase))
            return ResolveActive();

        // Hex hwnd: "0x1A2B3C"
        if (specifier.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(specifier[2..], System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            {
                var w = DesktopWindowList.Instance.FindByHwnd((nint)parsed);
                return w != null
                    ? (w, null)
                    : (null, $"No window with hwnd {specifier}");
            }
            return (null, $"Invalid hwnd format: {specifier}");
        }

        // Try process name first (exact match)
        var byProcess = DesktopWindowList.Instance.FindByProcessName(specifier);
        if (byProcess != null)
            return (byProcess, null);

        // Try title substring (case-insensitive)
        var byTitle = DesktopWindowList.Instance.All
            .FirstOrDefault(w => w.Title.Contains(specifier, StringComparison.OrdinalIgnoreCase));
        if (byTitle != null)
            return (byTitle, null);

        return (null, $"No window matching \"{specifier}\". Use \"active\", a process name, or a title substring.");
    }

    private static (DesktopWindow? Window, string? Error) ResolveActive()
    {
        var fgHwnd = GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero)
            return (null, "No foreground window");

        var window = DesktopWindowList.Instance.FindByHwnd(fgHwnd);
        if (window != null)
            return (window, null);

        // Foreground window might not be in our list yet — refresh and retry
        DesktopWindowList.Instance.Refresh();
        window = DesktopWindowList.Instance.FindByHwnd(fgHwnd);
        return window != null
            ? (window, null)
            : (null, "Foreground window not found in window list");
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
