namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using WinfoMain.Common;

/// <summary>
/// Builds LayoutResult from OCR data + BspRegionDetector.
/// Used when UIA is not available (Blender, games, some Electron apps).
/// </summary>
public static class OcrLayoutBuilder
{
    public static LayoutResult Build(WinContent content, Rectangle windowBounds)
    {
        var bsp = BspRegionDetector.DetectFull(content);

        var result = new LayoutResult
        {
            WindowWidth = windowBounds.Width,
            WindowHeight = windowBounds.Height,
            DataSource = "OCR",
        };

        foreach (var bspRegion in bsp.Regions)
        {
            var layoutRegion = new LayoutRegionResult
            {
                Type = ClassifyByPosition(bspRegion.Bounds, windowBounds),
                ControlType = "OCR",
                Bounds = bspRegion.Bounds,
                AreaRatio = windowBounds.Width > 0 && windowBounds.Height > 0
                    ? (bspRegion.Bounds.Width * (double)bspRegion.Bounds.Height)
                      / (windowBounds.Width * (double)windowBounds.Height)
                    : 0,
            };

            foreach (var word in bspRegion.Words)
            {
                layoutRegion.Texts.Add(new RegionText
                {
                    Text = word.Text,
                    X = word.Bounds.X - bspRegion.Bounds.X,
                    Y = word.Bounds.Y - bspRegion.Bounds.Y,
                    Width = word.Bounds.Width,
                    Height = word.Bounds.Height,
                    ControlType = "OCR",
                });
            }

            result.Regions.Add(layoutRegion);
        }

        return result;
    }

    /// <summary>
    /// Geometric classification without UIA (position + size only).
    /// Simpler than LayoutAnalyzer but better than "Unknown" for everything.
    /// </summary>
    private static string ClassifyByPosition(Rectangle bounds, Rectangle window)
    {
        if (window.Width <= 0 || window.Height <= 0) return "Region";

        double relY = (double)bounds.Y / window.Height;
        double relW = (double)bounds.Width / window.Width;
        double relH = (double)bounds.Height / window.Height;

        if (relW > 0.7 && relH < 0.1 && relY < 0.1) return "TopBar";
        if (relW > 0.7 && relH < 0.08 && relY > 0.9) return "BottomBar";
        if (relW < 0.3 && relH > 0.4 && bounds.X < window.Width * 0.1) return "NavPanel";
        if (relW * relH > 0.25) return "Content";
        return "Region";
    }
}
