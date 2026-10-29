namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Builds LayoutResult from UIA tree.
/// Uses LayoutAnalyzer (geometric partition) + SceneVisualizer (text extraction).
/// Used when UIA provides enough structure (>= 5 visible nodes).
/// </summary>
public static class UIALayoutBuilder
{
    public static LayoutResult Build(UIATreeNode root, Rectangle windowBounds)
    {
        // 1. Find layout regions (geometric partition analysis)
        var rects = LayoutAnalyzer.Analyze(root, windowBounds);

        // 2. Extract texts and assign to regions
        var result = LayoutRenderer.BuildResult(rects, root, windowBounds);
        result.DataSource = "UIA";

        return result;
    }
}
