namespace WinfoMain.Desktop;

using System.Drawing;
using WinfoMain.Common;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Result of a spatial query on the desktop.
/// Contains everything known about a given screen position or rectangle:
/// which window, which region, which text, which line, which word.
/// </summary>
public class DesktopQueryResult
{
    /// <summary>The window at the queried position.</summary>
    public DesktopWindow Window { get; init; } = null!;

    /// <summary>Process name of the window (e.g. "notepad").</summary>
    public string ProcessName => Window.ProcessName;

    /// <summary>Title of the window.</summary>
    public string WindowTitle => Window.Title;

    /// <summary>
    /// The deepest UIA element at the queried position, or null if no UIA tree available.
    /// </summary>
    public UIATreeNode? UiaElement { get; init; }

    /// <summary>True if a UIA element was found at the position.</summary>
    public bool HasUiaElement => UiaElement != null;

    /// <summary>
    /// The layout region at the queried position, or null if no layout available.
    /// </summary>
    public LayoutRegionResult? Region { get; init; }

    /// <summary>Region type (e.g. "Content", "NavPanel", "TopBar"), or null.</summary>
    public string? RegionType => Region?.Type;

    /// <summary>Region name if available (e.g. "Navigationsbereich"), or null.</summary>
    public string? RegionName => Region?.Name;

    /// <summary>
    /// Complete text of the region (all texts joined), or null.
    /// </summary>
    public string? RegionText { get; init; }

    /// <summary>
    /// The text element directly at the queried position, or null.
    /// </summary>
    public RegionText? TextElement { get; init; }

    /// <summary>The single word/text at the queried position, or null.</summary>
    public string? Word => TextElement?.Text;

    /// <summary>
    /// The full line of text at the queried position (all texts in the same row), or null.
    /// </summary>
    public string? Line { get; init; }

    /// <summary>
    /// The screen-absolute bounds of the matched text element, or Empty if none.
    /// </summary>
    public Rectangle WordScreenBounds { get; init; }

    /// <summary>True if a layout is available for the window.</summary>
    public bool HasLayout => Region != null;

    /// <summary>True if a specific word/text was found at the position.</summary>
    public bool HasWord => TextElement != null;
}
