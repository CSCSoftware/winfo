namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using WinfoMain.Common;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Merges Probe (TextPattern) results into an existing LayoutResult.
/// Only used when a window has 1-2 TextPattern elements (editors/terminals).
/// Probe text is higher quality than OCR — replaces OCR texts in affected regions.
/// </summary>
public static class ProbeTextMerger
{
    /// <summary>
    /// Maximum number of TextPattern elements to consider this a "Probe-worthy" window.
    /// More than this typically means a structured app (Explorer, Settings) where Probe is unreliable.
    /// </summary>
    public const int MaxProbeElements = 2;

    /// <summary>
    /// Run Probe, match with OCR, and merge into LayoutResult if applicable.
    /// Returns true if Probe texts were merged.
    /// </summary>
    public static async Task<bool> ProbeAndMergeAsync(
        LayoutResult layout,
        nint hwnd,
        WinContent ocrContent,
        Rectangle windowBounds,
        DesktopWindow window)
    {
        // 1. Run Probe
        List<UiaProbe.ProbeResult> probeResults;
        try
        {
            probeResults = await UiaProbe.ProbeAsync(hwnd);
        }
        catch (Exception ex)
        {
            WinfoLog.Write($"[WinfoMain] ProbeAndMergeAsync failed: {ex}");
            return false;
        }

        if (probeResults.Count == 0 || probeResults.Count > MaxProbeElements)
        {
            window.ProbeResults = probeResults.Count > 0 ? probeResults : null;
            window.ProbeOcrMatch = null;
            return false;
        }

        window.ProbeResults = probeResults;

        // 2. Match Probe words with OCR for pixel-accurate bounds
        if (ocrContent.Words.Count == 0)
            return false;

        var matchResult = ProbeOcrMatcher.Match(probeResults, ocrContent, windowBounds);
        window.ProbeOcrMatch = matchResult;

        if (matchResult.Words.Count == 0)
            return false;

        // 3. Merge matched words into the LayoutResult regions
        return MergeMatchedWords(layout, matchResult, probeResults, windowBounds);
    }

    /// <summary>
    /// Merge ProbeOcrMatcher results into LayoutResult regions.
    /// Replaces texts in regions that overlap with Probe element bounds.
    /// </summary>
    private static bool MergeMatchedWords(
        LayoutResult layout,
        ProbeOcrMatcher.MatchResult matchResult,
        List<UiaProbe.ProbeResult> probeResults,
        Rectangle windowBounds)
    {
        // Compute the window-local bounds of all Probe elements
        var probeBounds = new List<Rectangle>();
        foreach (var pr in probeResults)
        {
            var local = new Rectangle(
                pr.Bounds.X - windowBounds.X,
                pr.Bounds.Y - windowBounds.Y,
                pr.Bounds.Width,
                pr.Bounds.Height);
            probeBounds.Add(local);
        }

        bool anyMerged = false;

        foreach (var region in layout.Regions)
        {
            // Check if any Probe element overlaps significantly with this region
            bool overlapsProbe = false;
            foreach (var pb in probeBounds)
            {
                if (!region.Bounds.IntersectsWith(pb)) continue;

                // Compute overlap area
                var overlap = Rectangle.Intersect(region.Bounds, pb);
                double overlapArea = overlap.Width * (double)overlap.Height;
                double probeArea = pb.Width * (double)pb.Height;

                // Probe element must be mostly inside this region (>30% of Probe area)
                if (probeArea > 0 && overlapArea / probeArea > 0.3)
                {
                    overlapsProbe = true;
                    break;
                }
            }

            if (!overlapsProbe) continue;

            // Collect matched words that fall inside this region
            var wordsInRegion = new List<ProbeOcrMatcher.MatchedWord>();
            foreach (var word in matchResult.Words)
            {
                if (word.Bounds.IsEmpty) continue;
                if (region.Bounds.IntersectsWith(word.Bounds))
                    wordsInRegion.Add(word);
            }

            if (wordsInRegion.Count == 0) continue;

            // Replace region texts with Probe-matched words (higher quality)
            region.Texts.Clear();
            foreach (var word in wordsInRegion)
            {
                region.Texts.Add(new RegionText
                {
                    Text = word.Text,
                    X = word.Bounds.X - region.Bounds.X,
                    Y = word.Bounds.Y - region.Bounds.Y,
                    Width = word.Bounds.Width,
                    Height = word.Bounds.Height,
                    ControlType = "Probe",
                });
            }

            anyMerged = true;
        }

        if (anyMerged && !layout.DataSource.Contains("Probe"))
            layout.DataSource += "+Probe";

        return anyMerged;
    }
}
