namespace WinfoMain.Desktop.UIA;

using System.Drawing;

/// <summary>
/// Minimal window descriptor — replaces DesktopWindow for standalone use.
/// Only the properties needed by UIATreeWalker and TreeViewerForm.
/// </summary>
public class WindowInfo
{
    public nint Hwnd { get; init; }
    public string ProcessName { get; init; } = "";
    public string Title { get; init; } = "";
    public Rectangle Bounds { get; init; }
}
