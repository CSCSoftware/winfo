namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;

/// <summary>
/// Structured layout analysis result — machine-readable, for programmatic use.
/// Use LayoutRenderer.Render() for LLM text, this for OCR/code integration.
/// </summary>
public class LayoutResult
{
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    /// <summary>"UIA", "OCR", or "UIA+OCR"</summary>
    public string DataSource { get; set; } = "";
    public List<LayoutRegionResult> Regions { get; set; } = [];

    /// <summary>
    /// Find all regions that contain the given point (window-relative).
    /// </summary>
    public List<LayoutRegionResult> HitTest(int x, int y)
        => Regions.FindAll(r => r.Bounds.Contains(x, y));

    /// <summary>
    /// Find region by type (e.g. "NavPanel", "Content").
    /// </summary>
    public LayoutRegionResult? FindByType(string type)
        => Regions.Find(r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Find all regions of a given type.
    /// </summary>
    public List<LayoutRegionResult> FindAllByType(string type)
        => Regions.FindAll(r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Search for a text across all regions. Returns matching regions + texts.
    /// </summary>
    public List<(LayoutRegionResult region, RegionText text)> SearchText(string query)
    {
        var results = new List<(LayoutRegionResult, RegionText)>();
        foreach (var r in Regions)
            foreach (var t in r.Texts)
                if (t.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add((r, t));
        return results;
    }
}

/// <summary>
/// A layout region with its texts and positions.
/// </summary>
public class LayoutRegionResult
{
    public string Type { get; set; } = "";          // "NavPanel", "Content", "TopBar", etc.
    public string? Name { get; set; }               // e.g. "Prozesse", "Navigationsbereich"
    public string ControlType { get; set; } = "";   // UIA ControlType: "DataGrid", "Tab", etc.
    public Rectangle Bounds { get; set; }           // window-relative
    public double AreaRatio { get; set; }
    public List<RegionText> Texts { get; set; } = [];

    /// <summary>
    /// Find text at a position within this region (region-relative coordinates).
    /// Returns the closest text within tolerance pixels.
    /// </summary>
    public RegionText? HitTest(int x, int y, int tolerance = 10)
    {
        RegionText? best = null;
        double bestDist = double.MaxValue;
        foreach (var t in Texts)
        {
            double dx = x - (t.X + t.Width / 2.0);
            double dy = y - (t.Y + t.Height / 2.0);
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist && dist <= tolerance + Math.Max(t.Width, t.Height) / 2.0)
            {
                best = t;
                bestDist = dist;
            }
        }
        return best;
    }

    /// <summary>
    /// Get texts grouped by row (same Y within tolerance).
    /// </summary>
    public List<List<RegionText>> GetRows(int toleranceY = 8)
    {
        if (Texts.Count == 0) return [];

        var sorted = Texts.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        var rows = new List<List<RegionText>>();
        var currentRow = new List<RegionText> { sorted[0] };
        int rowY = sorted[0].Y;

        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].Y - rowY) <= toleranceY)
            {
                currentRow.Add(sorted[i]);
            }
            else
            {
                rows.Add(currentRow);
                currentRow = [sorted[i]];
                rowY = sorted[i].Y;
            }
        }
        rows.Add(currentRow);
        return rows;
    }
}

/// <summary>
/// A text element within a region. Coordinates are region-relative.
/// </summary>
public class RegionText
{
    public string Text { get; set; } = "";
    public int X { get; set; }             // region-relative
    public int Y { get; set; }             // region-relative
    public int Width { get; set; }
    public int Height { get; set; }
    public string ControlType { get; set; } = "";  // "Text", "Button", "Edit", etc.
}
