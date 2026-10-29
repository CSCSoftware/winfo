namespace WinfoMain.Desktop.UIA;

using System.Drawing;
using WinfoMain.Common;
using System.Windows.Automation;
using System.Windows.Automation.Text;

/// <summary>
/// Experimental: Extract visible text from a window via UIA TextPattern.
/// Uses GetVisibleRanges() to get text lines with screen bounds,
/// then optionally iterates word-by-word with MoveEndpointByUnit.
///
/// Fully self-contained — no dependencies on other WinfoMain modules.
/// Can be removed without side effects.
/// </summary>
public static class UiaProbe
{
    /// <summary>A visible text line extracted via TextPattern.</summary>
    public sealed class ProbeLine
    {
        /// <summary>Screen-absolute bounds of the text line.</summary>
        public Rectangle Bounds { get; set; }

        /// <summary>The text content of this line.</summary>
        public string Text { get; set; } = "";

        public override string ToString()
        {
            var preview = Text.TrimEnd();
            return $"@{Bounds.X},{Bounds.Y} {Bounds.Width}x{Bounds.Height} \"{preview}\"";
        }
    }

    /// <summary>Result of a probe operation on a window.</summary>
    public sealed class ProbeResult
    {
        /// <summary>Name of the element that provided the TextPattern.</summary>
        public string ElementName { get; set; } = "";

        /// <summary>ControlType of the element.</summary>
        public string ControlType { get; set; } = "";

        /// <summary>ClassName of the element (e.g. "TermControl").</summary>
        public string ClassName { get; set; } = "";

        /// <summary>Screen-absolute bounds of the text element.</summary>
        public Rectangle Bounds { get; set; }

        /// <summary>Visible text lines with their bounds.</summary>
        public List<ProbeLine> Lines { get; set; } = [];

        /// <summary>Full visible text (all lines joined).</summary>
        public string FullText => string.Join("\n", Lines.Select(l => l.Text));
    }

    /// <summary>
    /// Probe a window for visible text via TextPattern.
    /// Walks the UIA tree to find elements with TextPattern support,
    /// then extracts visible lines with their screen bounds.
    /// </summary>
    /// <param name="hwnd">Window handle.</param>
    /// <returns>List of ProbeResults, one per TextPattern element found.</returns>
    public static List<ProbeResult> Probe(nint hwnd)
    {
        var results = new List<ProbeResult>();

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return results;

            // Find all elements that support TextPattern
            var condition = new PropertyCondition(
                AutomationElement.IsTextPatternAvailableProperty, true);
            var elements = root.FindAll(TreeScope.Descendants, condition);

            foreach (AutomationElement element in elements)
            {
                try
                {
                    var result = ExtractVisibleText(element);
                    if (result != null && result.Lines.Count > 0)
                        results.Add(result);
                }
                catch (Exception ex) { WinfoLog.Write($"[WinfoMain] UiaProbe element extraction failed: {ex}"); }
            }
        }
        catch (Exception ex) { WinfoLog.Write($"[WinfoMain] UiaProbe.Probe failed: {ex}"); }

        return results;
    }

    /// <summary>Async version of Probe.</summary>
    public static Task<List<ProbeResult>> ProbeAsync(nint hwnd)
        => Task.Run(() => Probe(hwnd));

    // ── Internals ──────────────────────────────────────────────────

    private static ProbeResult? ExtractVisibleText(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            return null;

        var textPattern = (TextPattern)pattern;
        var current = element.Current;

        var bounds = current.BoundingRectangle;
        var result = new ProbeResult
        {
            ElementName = current.Name ?? "",
            ControlType = current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            ClassName = current.ClassName ?? "",
            Bounds = bounds.IsEmpty
                ? Rectangle.Empty
                : new Rectangle((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height),
        };

        // Get visible text ranges (one per visible line or contiguous block)
        var visibleRanges = textPattern.GetVisibleRanges();
        if (visibleRanges == null || visibleRanges.Length == 0)
            return result;

        foreach (var range in visibleRanges)
        {
            try
            {
                var text = range.GetText(50000);
                if (string.IsNullOrEmpty(text)) continue;

                var rects = range.GetBoundingRectangles();
                var rangeBounds = rects.Length > 0
                    ? UnionRects(rects)
                    : Rectangle.Empty;

                // Split multi-line text (e.g. Terminal returns all visible text in one range)
                // Order matters: check \r\n first, then \n, then \r alone
                var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

                if (lines.Length <= 1)
                {
                    // Single line — use range bounds directly
                    var trimmed = text.TrimEnd();
                    if (trimmed.Length > 0)
                    {
                        result.Lines.Add(new ProbeLine
                        {
                            Text = trimmed,
                            Bounds = rangeBounds,
                        });
                    }
                }
                else
                {
                    // Multiple lines — approximate per-line bounds from total range bounds
                    // Use total line count (including empty) since all lines occupy space
                    var lineHeight = lines.Length > 0 && !rangeBounds.IsEmpty
                        ? rangeBounds.Height / (double)lines.Length
                        : 0;
                    var y = (double)rangeBounds.Y;

                    // Strip trailing empty lines (unused terminal rows etc.)
                    var lastNonEmpty = Array.FindLastIndex(lines, l => !string.IsNullOrWhiteSpace(l));
                    var effectiveCount = lastNonEmpty + 1;

                    for (var i = 0; i < effectiveCount; i++)
                    {
                        var trimmed = lines[i].TrimEnd();

                        result.Lines.Add(new ProbeLine
                        {
                            Text = trimmed,
                            Bounds = rangeBounds.IsEmpty
                                ? Rectangle.Empty
                                : new Rectangle(rangeBounds.X, (int)y, rangeBounds.Width, (int)Math.Max(lineHeight, 1)),
                        });
                        y += lineHeight;
                    }
                }
            }
            catch (Exception ex) { WinfoLog.Write($"[WinfoMain] UiaProbe range extraction failed: {ex}"); }
        }

        return result;
    }

    /// <summary>Union multiple Rects (from GetBoundingRectangles) into one Rectangle.</summary>
    private static Rectangle UnionRects(System.Windows.Rect[] rects)
    {
        if (rects.Length == 0) return Rectangle.Empty;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var r in rects)
        {
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;

            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.Right > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
        }

        if (minX >= maxX || minY >= maxY) return Rectangle.Empty;

        return new Rectangle((int)minX, (int)minY, (int)(maxX - minX), (int)(maxY - minY));
    }
}
