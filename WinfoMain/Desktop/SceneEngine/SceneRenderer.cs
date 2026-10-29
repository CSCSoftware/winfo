namespace WinfoMain.Desktop.SceneEngine;

using System.Text;

/// <summary>
/// Render a LayoutResult as compact LLM-consumable text.
/// Three modes: Overview (~150 tokens), RegionDetail (one region expanded), FullDetail (all regions).
/// Works uniformly for both UIA and OCR paths.
/// </summary>
public static class SceneRenderer
{
    /// <summary>
    /// Render complete layout overview — compact one-liner per region.
    /// Target: ~150 tokens for a typical window.
    /// </summary>
    public static string RenderOverview(LayoutResult layout, string? processName = null, string? title = null)
    {
        var sb = new StringBuilder();

        sb.Append("Window:");
        if (!string.IsNullOrEmpty(processName))
            sb.Append($" [{processName}]");
        if (!string.IsNullOrEmpty(title))
            sb.Append($" \"{title}\"");
        sb.Append($" {layout.WindowWidth}x{layout.WindowHeight}");
        sb.Append($" [{layout.DataSource}]");
        sb.Append($" {layout.Regions.Count} regions");
        sb.AppendLine();
        sb.AppendLine();

        foreach (var region in layout.Regions)
            sb.AppendLine(RenderRegionOneLiner(region));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Render a single region in detail — shows all texts grouped by row.
    /// </summary>
    public static string RenderRegionDetail(LayoutRegionResult region)
    {
        var sb = new StringBuilder();
        sb.Append($"[{region.Type}]");
        if (!string.IsNullOrEmpty(region.Name))
            sb.Append($" \"{region.Name}\"");
        sb.Append($" {region.Bounds.Width}x{region.Bounds.Height}");
        sb.Append($" ({region.AreaRatio:P0})");
        sb.Append($" @{region.Bounds.X},{region.Bounds.Y}");
        sb.AppendLine();

        var rows = region.GetRows();
        foreach (var row in rows)
        {
            sb.Append("  ");
            bool first = true;
            foreach (var text in row)
            {
                if (!first) sb.Append("  |  ");
                sb.Append($"\"{text.Text}\"");
                first = false;
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Render all regions with full text detail.
    /// </summary>
    public static string RenderFullDetail(LayoutResult layout, string? processName = null, string? title = null)
    {
        var sb = new StringBuilder();

        sb.Append("Window:");
        if (!string.IsNullOrEmpty(processName))
            sb.Append($" [{processName}]");
        if (!string.IsNullOrEmpty(title))
            sb.Append($" \"{title}\"");
        sb.Append($" {layout.WindowWidth}x{layout.WindowHeight}");
        sb.Append($" [{layout.DataSource}]");
        sb.AppendLine();
        sb.AppendLine();

        foreach (var region in layout.Regions)
        {
            sb.AppendLine(RenderRegionDetail(region));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // --- Region one-liner rendering ---

    private static string RenderRegionOneLiner(LayoutRegionResult region)
    {
        var sb = new StringBuilder();
        sb.Append($"[{region.Type}]");

        if (!string.IsNullOrEmpty(region.Name))
            sb.Append($" \"{region.Name}\"");

        sb.Append($" {region.Bounds.Width}x{region.Bounds.Height}");
        sb.Append($" ({region.AreaRatio:P0})");
        sb.Append($" @{region.Bounds.X},{region.Bounds.Y}");

        // Show first few texts as preview
        var preview = region.Texts
            .Take(5)
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (preview.Count > 0)
        {
            sb.Append($": {string.Join(", ", preview)}");
            if (region.Texts.Count > 5)
                sb.Append($" (+{region.Texts.Count - 5})");
        }

        return sb.ToString();
    }
}
