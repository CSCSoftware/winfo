namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using System.Text;
using WinfoMain.Desktop.UIA;
using static WinfoMain.Desktop.SceneEngine.SceneVisualizer;

/// <summary>
/// Renders LayoutAnalyzer results as compact, token-efficient text for LLM consumption.
/// Each region gets its visible texts with relative positions.
/// Also builds structured LayoutResult DTOs for programmatic use (OCR, etc.).
/// </summary>
public static class LayoutRenderer
{
    /// <summary>
    /// Build a structured LayoutResult with all regions and their texts.
    /// This is the primary API for programmatic consumers (OCR, code integration).
    /// </summary>
    public static LayoutResult BuildResult(List<LayoutRect> rects, UIATreeNode root, Rectangle windowBounds)
    {
        var allWords = CollectVisibleWords(root, windowBounds);

        var result = new LayoutResult
        {
            WindowWidth = windowBounds.Width,
            WindowHeight = windowBounds.Height,
        };

        foreach (var rect in rects)
        {
            int relX = rect.Bounds.X - windowBounds.X;
            int relY = rect.Bounds.Y - windowBounds.Y;

            var region = new LayoutRegionResult
            {
                Type = rect.Label,
                Name = ExtractName(rect.Source),
                ControlType = rect.ControlType,
                Bounds = new Rectangle(relX, relY, rect.Bounds.Width, rect.Bounds.Height),
                AreaRatio = rect.AreaRatio,
            };

            foreach (var w in allWords)
            {
                if (!rect.Bounds.IntersectsWith(w.Bounds)) continue;
                if (IsScrollbarText(w.Text)) continue;

                region.Texts.Add(new RegionText
                {
                    Text = w.Text,
                    X = w.Bounds.X - rect.Bounds.X,
                    Y = w.Bounds.Y - rect.Bounds.Y,
                    Width = w.Bounds.Width,
                    Height = w.Bounds.Height,
                    ControlType = w.ControlType,
                });
            }

            result.Regions.Add(region);
        }

        return result;
    }

    /// <summary>
    /// Render layout regions with their visible texts — optimized for LLM prompts.
    /// </summary>
    public static string Render(List<LayoutRect> rects, UIATreeNode root, Rectangle windowBounds)
    {
        var result = BuildResult(rects, root, windowBounds);
        return RenderResult(result, rects);
    }

    /// <summary>
    /// Render a LayoutResult as LLM-optimized text.
    /// </summary>
    public static string RenderResult(LayoutResult result, List<LayoutRect>? rectsForLimits = null)
    {
        if (result.Regions.Count == 0)
            return "(no layout)";

        var sb = new StringBuilder();
        sb.AppendLine($"Window: {result.WindowWidth}x{result.WindowHeight}");
        sb.AppendLine();

        for (int i = 0; i < result.Regions.Count; i++)
        {
            var region = result.Regions[i];

            // Header: [Type] "Name" WxH (%) @x,y
            sb.Append($"[{region.Type}]");
            if (!string.IsNullOrEmpty(region.Name))
                sb.Append($" \"{region.Name}\"");
            sb.Append($" {region.Bounds.Width}x{region.Bounds.Height}");
            sb.Append($" ({region.AreaRatio:P0})");
            sb.Append($" @{region.Bounds.X},{region.Bounds.Y}");
            sb.AppendLine();

            if (region.Texts.Count == 0)
            {
                sb.AppendLine();
                continue;
            }

            // Convert to tuples for row-grouping
            var words = region.Texts
                .Select(t => (text: TruncateText(t.Text, 50), t.X, t.Y, ct: t.ControlType))
                .ToList();

            var rows = GroupIntoRows(words, toleranceY: 8);
            RemoveRepeatingTexts(rows);

            // Row limit per region type
            int maxRows = rectsForLimits != null && i < rectsForLimits.Count
                ? MaxRowsForRegion(rectsForLimits[i])
                : 10;
            int rendered = 0;
            foreach (var row in rows)
            {
                if (row.Count == 0) continue;

                if (rendered >= maxRows)
                {
                    int remaining = rows.Count(r => r.Count > 0) - rendered;
                    if (remaining > 0)
                        sb.AppendLine($"  ... +{remaining} rows");
                    break;
                }

                sb.Append("  ");
                bool first = true;
                foreach (var (text, rx, ry, ct) in row)
                {
                    if (!first) sb.Append("  |  ");
                    sb.Append($"\"{text}\"");
                    first = false;
                }
                sb.AppendLine();
                rendered++;
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Minimal render — one line per region, no word details. For overview prompts.
    /// </summary>
    public static string RenderOverview(List<LayoutRect> rects, Rectangle windowBounds)
    {
        if (rects.Count == 0)
            return "(no layout)";

        var sb = new StringBuilder();
        foreach (var rect in rects)
        {
            int relX = rect.Bounds.X - windowBounds.X;
            int relY = rect.Bounds.Y - windowBounds.Y;
            sb.Append($"[{rect.Label}]");

            var name = ExtractName(rect.Source);
            if (!string.IsNullOrEmpty(name))
                sb.Append($" \"{name}\"");

            sb.Append($" {rect.Bounds.Width}x{rect.Bounds.Height}");
            sb.Append($" ({rect.AreaRatio:P0})");
            sb.Append($" @{relX},{relY}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Group words into rows by Y-position. Words within toleranceY pixels
    /// of each other are on the same row. Within a row, sort left-to-right.
    /// </summary>
    private static List<List<(string text, int rx, int ry, string ct)>> GroupIntoRows(
        List<(string text, int rx, int ry, string controlType)> words, int toleranceY)
    {
        if (words.Count == 0) return [];

        // Sort by Y then X
        words.Sort((a, b) =>
        {
            int dy = a.ry.CompareTo(b.ry);
            return dy != 0 ? dy : a.rx.CompareTo(b.rx);
        });

        var rows = new List<List<(string text, int rx, int ry, string ct)>>();
        var currentRow = new List<(string text, int rx, int ry, string ct)> { words[0] };
        int rowY = words[0].ry;

        for (int i = 1; i < words.Count; i++)
        {
            if (Math.Abs(words[i].ry - rowY) <= toleranceY)
            {
                currentRow.Add(words[i]);
            }
            else
            {
                rows.Add(currentRow);
                currentRow = [words[i]];
                rowY = words[i].ry;
            }
        }
        rows.Add(currentRow);

        return rows;
    }

    /// <summary>
    /// Max rows to render per region type — keeps output compact.
    /// </summary>
    private static int MaxRowsForRegion(LayoutRect rect)
    {
        return rect.Label switch
        {
            "TitleBar" => 1,
            "MenuBar" => 1,
            "TopBar" => 2,
            "Header" => 2,
            "Toolbar" => 2,
            "TabBar" => 2,
            "StatusBar" => 1,
            "BottomBar" => 1,
            "AppRail" => 10,
            "NavPanel" => 15,
            "SidePanel" => 15,
            "Content" => 20,
            _ => 10, // Region, etc.
        };
    }

    /// <summary>
    /// Remove texts that appear on >50% of rows — these are typically column header
    /// labels repeated on every data row (e.g. "Name" in DataGrid rows).
    /// </summary>
    private static void RemoveRepeatingTexts(List<List<(string text, int rx, int ry, string ct)>> rows)
    {
        if (rows.Count < 3) return; // need enough rows to detect pattern

        // Count how often each text appears across rows
        var textCounts = new Dictionary<string, int>();
        foreach (var row in rows)
            foreach (var (text, _, _, _) in row)
                textCounts[text] = textCounts.GetValueOrDefault(text) + 1;

        // Texts appearing in >50% of rows are likely column labels
        int threshold = rows.Count / 2;
        var repeating = new HashSet<string>();
        foreach (var (text, count) in textCounts)
        {
            if (count > threshold && text.Length < 20)
                repeating.Add(text);
        }

        if (repeating.Count == 0) return;

        // Remove repeating texts from all rows
        foreach (var row in rows)
            row.RemoveAll(item => repeating.Contains(item.text));
    }

    private static bool IsScrollbarText(string text)
    {
        // Scrollbar button names as exposed by UIA on German and English Windows.
        return text is "Bildlauf nach oben" or "Bildlauf nach unten"
            or "Seite nach oben" or "Seite nach unten"
            or "Vorwärts um kleine Menge" or "Vorwärts um große Menge"
            or "Zurück um kleine Menge" or "Zurück um große Menge"
            or "Scroll up" or "Scroll down"
            or "Page up" or "Page down"
            or "Position";
    }

    private static string TruncateText(string text, int maxLen)
    {
        // Strip common UIA tooltip suffixes (German and English Windows)
        int dotIdx = text.IndexOf(". Drücken Sie", StringComparison.Ordinal);
        if (dotIdx < 0) dotIdx = text.IndexOf(". Press ", StringComparison.Ordinal);
        if (dotIdx > 0) text = text[..dotIdx];

        if (text.Length <= maxLen) return text;
        // Try to cut at a natural point
        int cutAt = text.LastIndexOf('.', maxLen - 3);
        if (cutAt < maxLen / 2) cutAt = maxLen - 3;
        return text[..cutAt] + "...";
    }

    /// <summary>
    /// Extract a meaningful name from the Source string (DisplayText).
    /// Handles formats like: [ControlType] "Name" (ClassName)
    /// </summary>
    private static string? ExtractName(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;

        // Try to extract quoted name
        int q1 = source.IndexOf('"');
        int q2 = source.LastIndexOf('"');
        if (q1 >= 0 && q2 > q1)
        {
            var name = source[(q1 + 1)..q2];
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= 60)
                return name;
        }

        // Try AutomationId in parentheses
        int p1 = source.IndexOf('(');
        int p2 = source.LastIndexOf(')');
        if (p1 >= 0 && p2 > p1)
        {
            var aid = source[(p1 + 1)..p2];
            // Skip CSS class names (contain spaces + random chars)
            if (!aid.Contains(' ') && aid.Length <= 40)
                return aid;
        }

        return null;
    }
}
