namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using WinfoMain.Common;

/// <summary>
/// BSP region detector with Otsu-based gap threshold.
///
/// Algorithm:
/// 1. Collect ALL gaps along both axes globally (for threshold computation).
/// 2. Otsu's method on gap sizes → threshold separating text-flow from structural.
/// 3. Recursive XY-Cut: at each level, find the largest LOCAL gap >= threshold
///    among the current word set, split there. Local gaps are recomputed per recursion
///    so that column splits work (global X-projection merges sidebar + content).
/// </summary>
public static class BspRegionDetector
{
    private const int MaxDepth = 20;

    /// <summary>A single BSP region: bounding box + contained OCR words.</summary>
    public sealed class BspRegion
    {
        public Rectangle Bounds { get; set; }
        public List<WinWordBase> Words { get; set; } = [];
    }

    public sealed class BspResult
    {
        public List<BspRegion> Regions = new();
        public List<Rectangle> Cells = new();
        public int GapThreshold;
        public int TotalGaps;
        public int StructuralGaps;
    }

    // ────────────────────────────────────────────────────────────
    //  Public API
    // ────────────────────────────────────────────────────────────

    public static List<BspRegion> Detect(WinContent content) => DetectFull(content).Regions;

    /// <summary>Full detection with auto threshold (Otsu).</summary>
    public static BspResult DetectFull(WinContent content) => DetectFull(content, -1);

    /// <summary>
    /// Full detection with optional manual threshold override.
    /// Pass thresholdOverride >= 0 to use a fixed threshold instead of Otsu.
    /// </summary>
    public static BspResult DetectFull(WinContent content, int thresholdOverride)
    {
        var result = new BspResult();
        if (content.Words.Count == 0)
            return result;

        var allWords = content.Words.ToList();

        // Step 1: Collect ALL gaps globally (both axes) for threshold calibration
        var globalGaps = CollectGaps(allWords);
        result.TotalGaps = globalGaps.Count;

        if (globalGaps.Count == 0)
        {
            result.Regions.Add(new BspRegion { Bounds = ComputeBounds(allWords), Words = allWords });
            return result;
        }

        // Step 2: Threshold — manual override or Otsu
        int threshold = thresholdOverride >= 0
            ? thresholdOverride
            : ComputeOtsuThreshold(globalGaps, allWords);
        result.GapThreshold = threshold;

        // Step 3: Recursive split — gaps computed locally at each level
        var regions = new List<BspRegion>();
        SplitRecursive(allWords, threshold, 0, regions, result.Cells);

        // Count how many of the global gaps were structural (debug info)
        result.StructuralGaps = globalGaps.Count(g => g >= threshold);

        regions.Sort((a, b) =>
        {
            int dy = a.Bounds.Y - b.Bounds.Y;
            return dy != 0 ? dy : a.Bounds.X - b.Bounds.X;
        });

        result.Regions = regions;
        return result;
    }

    // ────────────────────────────────────────────────────────────
    //  Step 1: Collect gap sizes (for Otsu calibration)
    // ────────────────────────────────────────────────────────────

    private static List<int> CollectGaps(List<WinWordBase> words)
    {
        var gaps = new List<int>();

        // Y-axis gaps (horizontal bands between text rows)
        var yIntervals = words
            .Select(w => (start: w.Bounds.Top, end: w.Bounds.Bottom))
            .OrderBy(i => i.start).ToList();
        foreach (var g in GapsFromIntervals(yIntervals))
            gaps.Add(g);

        // X-axis gaps (vertical bands between columns)
        var xIntervals = words
            .Select(w => (start: w.Bounds.Left, end: w.Bounds.Right))
            .OrderBy(i => i.start).ToList();
        foreach (var g in GapsFromIntervals(xIntervals))
            gaps.Add(g);

        return gaps;
    }

    private static IEnumerable<int> GapsFromIntervals(List<(int start, int end)> sorted)
    {
        var merged = MergeIntervals(sorted);
        for (int i = 1; i < merged.Count; i++)
        {
            int gap = merged[i].start - merged[i - 1].end;
            if (gap > 0) yield return gap;
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Step 2: Otsu threshold
    // ────────────────────────────────────────────────────────────

    private static int ComputeOtsuThreshold(List<int> gapSizes, List<WinWordBase> words)
    {
        var heights = words.Select(w => w.Bounds.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        int medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 20;

        var sizes = gapSizes.OrderBy(s => s).ToList();

        if (sizes.Count < 2)
            return medianHeight;

        // All gaps very similar → no structural boundaries
        if (sizes[^1] - sizes[0] < medianHeight / 2)
            return sizes[^1] + 1;

        // Otsu's method
        double totalSum = 0;
        foreach (var s in sizes) totalSum += s;
        double totalCount = sizes.Count;

        double bestVariance = 0;
        int bestThreshold = medianHeight;

        double sumLeft = 0;
        int countLeft = 0;

        for (int i = 0; i < sizes.Count - 1; i++)
        {
            sumLeft += sizes[i];
            countLeft++;

            if (sizes[i] == sizes[i + 1]) continue;

            double countRight = totalCount - countLeft;
            double sumRight = totalSum - sumLeft;

            double meanLeft = sumLeft / countLeft;
            double meanRight = sumRight / countRight;
            double wL = countLeft / totalCount;
            double wR = countRight / totalCount;

            double variance = wL * wR * (meanLeft - meanRight) * (meanLeft - meanRight);

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = (sizes[i] + sizes[i + 1]) / 2;
            }
        }

        // Floor: word-spacing should never be structural
        return Math.Max(bestThreshold, medianHeight);
    }

    // ────────────────────────────────────────────────────────────
    //  Step 3: Recursive split with LOCAL gap detection
    // ────────────────────────────────────────────────────────────

    private static void SplitRecursive(
        List<WinWordBase> words, int threshold, int depth,
        List<BspRegion> regions, List<Rectangle> debugCells)
    {
        if (depth >= MaxDepth || words.Count <= 1)
        {
            AddLeaf(words, regions, debugCells);
            return;
        }

        // Find the largest gap >= threshold in THIS word set (both axes)
        var (splitPos, splitSize, isHorizontal) = FindBestLocalGap(words, threshold);

        if (splitPos < 0)
        {
            AddLeaf(words, regions, debugCells);
            return;
        }

        // Partition words
        List<WinWordBase> groupA, groupB;

        if (isHorizontal)
        {
            groupA = new List<WinWordBase>();
            groupB = new List<WinWordBase>();
            foreach (var w in words)
            {
                if (w.Bounds.Bottom <= splitPos) groupA.Add(w);
                else if (w.Bounds.Top >= splitPos) groupB.Add(w);
                else
                {
                    // Spanning word → nearest side
                    int mid = w.Bounds.Y + w.Bounds.Height / 2;
                    if (mid < splitPos) groupA.Add(w); else groupB.Add(w);
                }
            }
        }
        else
        {
            groupA = new List<WinWordBase>();
            groupB = new List<WinWordBase>();
            foreach (var w in words)
            {
                if (w.Bounds.Right <= splitPos) groupA.Add(w);
                else if (w.Bounds.Left >= splitPos) groupB.Add(w);
                else
                {
                    int mid = w.Bounds.X + w.Bounds.Width / 2;
                    if (mid < splitPos) groupA.Add(w); else groupB.Add(w);
                }
            }
        }

        if (groupA.Count == 0 || groupB.Count == 0)
        {
            AddLeaf(words, regions, debugCells);
            return;
        }

        SplitRecursive(groupA, threshold, depth + 1, regions, debugCells);
        SplitRecursive(groupB, threshold, depth + 1, regions, debugCells);
    }

    /// <summary>
    /// Find the largest gap >= threshold among the given words.
    /// Checks both axes, returns the overall best.
    /// </summary>
    private static (int position, int size, bool isHorizontal) FindBestLocalGap(
        List<WinWordBase> words, int threshold)
    {
        int bestPos = -1;
        int bestSize = 0;
        bool bestHorizontal = true;

        // Check Y-axis (horizontal gaps between rows)
        var yIntervals = words
            .Select(w => (start: w.Bounds.Top, end: w.Bounds.Bottom))
            .OrderBy(i => i.start).ToList();
        var yMerged = MergeIntervals(yIntervals);

        for (int i = 1; i < yMerged.Count; i++)
        {
            int gap = yMerged[i].start - yMerged[i - 1].end;
            if (gap >= threshold && gap > bestSize)
            {
                bestSize = gap;
                bestPos = yMerged[i - 1].end + gap / 2;
                bestHorizontal = true;
            }
        }

        // Check X-axis (vertical gaps between columns)
        var xIntervals = words
            .Select(w => (start: w.Bounds.Left, end: w.Bounds.Right))
            .OrderBy(i => i.start).ToList();
        var xMerged = MergeIntervals(xIntervals);

        for (int i = 1; i < xMerged.Count; i++)
        {
            int gap = xMerged[i].start - xMerged[i - 1].end;
            if (gap >= threshold && gap > bestSize)
            {
                bestSize = gap;
                bestPos = xMerged[i - 1].end + gap / 2;
                bestHorizontal = false;
            }
        }

        return (bestPos, bestSize, bestHorizontal);
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

    private static void AddLeaf(List<WinWordBase> words, List<BspRegion> regions, List<Rectangle> debugCells)
    {
        if (words.Count == 0) return;

        var region = new BspRegion { Bounds = ComputeBounds(words), Words = words.ToList() };
        regions.Add(region);
        debugCells.Add(region.Bounds);
    }

    private static Rectangle ComputeBounds(List<WinWordBase> words)
    {
        int x = words.Min(w => w.Bounds.Left);
        int y = words.Min(w => w.Bounds.Top);
        int right = words.Max(w => w.Bounds.Right);
        int bottom = words.Max(w => w.Bounds.Bottom);
        return new Rectangle(x, y, right - x, bottom - y);
    }

    private static List<(int start, int end)> MergeIntervals(List<(int start, int end)> sorted)
    {
        if (sorted.Count == 0) return new();

        var merged = new List<(int start, int end)> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            if (sorted[i].start <= last.end)
                merged[^1] = (last.start, Math.Max(last.end, sorted[i].end));
            else
                merged.Add(sorted[i]);
        }
        return merged;
    }
}
