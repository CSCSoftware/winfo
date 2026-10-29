namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using WinfoMain.Desktop.UIA;

/// <summary>
/// A named rectangle representing a layout region of a window.
/// </summary>
public class LayoutRect
{
    public string Label { get; set; } = "";
    public Rectangle Bounds { get; set; }
    public double AreaRatio { get; set; }
    public string ControlType { get; set; } = "";
    public string Source { get; set; } = "";  // DisplayText of the source node

    public override string ToString()
        => $"{Label}: {Bounds.X},{Bounds.Y} {Bounds.Width}x{Bounds.Height} ({AreaRatio:P0})";
}

/// <summary>
/// Analyzes a UIATreeNode tree to find the top-level layout rectangles of a window.
/// Unlike SceneDetectors (semantic classification), this uses pure geometry:
/// find the depth where children partition the window into non-overlapping regions.
/// </summary>
public static class LayoutAnalyzer
{
    private const double MinAreaRatio = 0.005;  // 0.5% of window
    private const double OverlapThreshold = 0.85;
    private const int MaxDrillDepth = 20;

    /// <summary>
    /// Analyze the window tree and return the major layout rectangles.
    /// </summary>
    public static List<LayoutRect> Analyze(UIATreeNode root, Rectangle windowBounds, List<string>? debugLog = null)
    {
        if (windowBounds.IsEmpty || windowBounds.Width <= 0 || windowBounds.Height <= 0)
            return [];

        double windowArea = windowBounds.Width * (double)windowBounds.Height;

        debugLog?.Add($"Root: {root.DisplayText}, Children={root.Children.Count}, ChildrenLoaded={root.ChildrenLoaded}");
        debugLog?.Add($"Window: {windowBounds}  Area={windowArea:N0}");

        // Step 1: Drill down to find the layout level
        var layoutNodes = FindLayoutLevel(root, windowBounds, debugLog);
        debugLog?.Add($"Layout level found: {layoutNodes.Count} nodes");

        if (layoutNodes.Count == 0)
            return [];

        // Step 2: Convert to LayoutRects
        var results = new List<LayoutRect>();
        foreach (var node in layoutNodes)
        {
            var bounds = node.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;

            double areaRatio = (bounds.Width * (double)bounds.Height) / windowArea;
            if (areaRatio < MinAreaRatio) continue;

            results.Add(new LayoutRect
            {
                Label = ClassifyByGeometry(bounds, windowBounds, node),
                Bounds = bounds,
                AreaRatio = areaRatio,
                ControlType = node.ControlType,
                Source = node.DisplayText,
            });
        }

        // Step 3: Split large content regions into sub-regions
        results = SplitLargeRegions(results, layoutNodes, root, windowBounds, windowArea, debugLog);

        // Step 4: Merge near-identical bounds (wrapper duplicates in result)
        results = MergeOverlappingRects(results);

        // Sort: top-to-bottom, then left-to-right
        results.Sort((a, b) =>
        {
            int dy = a.Bounds.Y.CompareTo(b.Bounds.Y);
            return dy != 0 ? dy : a.Bounds.X.CompareTo(b.Bounds.X);
        });

        return results;
    }

    /// <summary>
    /// Render the layout as ASCII art for quick visualization.
    /// </summary>
    public static string RenderAscii(List<LayoutRect> rects, Rectangle windowBounds, int width = 80, int height = 24)
    {
        if (rects.Count == 0 || windowBounds.IsEmpty)
            return "(no layout)";

        var canvas = new char[height, width];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                canvas[y, x] = ' ';

        // Collect all drawing operations with priority: corners > walls > lines > labels
        // This prevents later rects from overwriting structural chars of earlier ones
        // Priority: 3=corner, 2=wall, 1=line, 0=label
        var priority = new int[height, width];

        void Plot(int py, int px, char c, int prio)
        {
            if (py < 0 || py >= height || px < 0 || px >= width) return;
            if (prio >= priority[py, px])
            {
                canvas[py, px] = c;
                priority[py, px] = prio;
            }
        }

        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            char ch = (char)('A' + i % 26);

            int cx = (int)((r.Bounds.X - windowBounds.X) / (double)windowBounds.Width * (width - 1));
            int cy = (int)((r.Bounds.Y - windowBounds.Y) / (double)windowBounds.Height * (height - 1));
            int cw = Math.Max(1, (int)(r.Bounds.Width / (double)windowBounds.Width * (width - 1)));
            int ch2 = Math.Max(1, (int)(r.Bounds.Height / (double)windowBounds.Height * (height - 1)));

            cx = Math.Clamp(cx, 0, width - 1);
            cy = Math.Clamp(cy, 0, height - 1);
            cw = Math.Min(cw, width - cx);
            ch2 = Math.Min(ch2, height - cy);

            // Horizontal lines (top + bottom)
            for (int x = cx; x < cx + cw; x++)
            {
                Plot(cy, x, '-', 1);
                Plot(cy + ch2 - 1, x, '-', 1);
            }
            // Vertical walls (left + right)
            for (int y = cy; y < cy + ch2; y++)
            {
                Plot(y, cx, '|', 2);
                Plot(y, cx + cw - 1, '|', 2);
            }
            // Corners
            Plot(cy, cx, '+', 3);
            Plot(cy, cx + cw - 1, '+', 3);
            Plot(cy + ch2 - 1, cx, '+', 3);
            Plot(cy + ch2 - 1, cx + cw - 1, '+', 3);

            // Label inside
            var label = $"{ch}:{r.Label}";
            if (label.Length > cw - 2) label = label[..Math.Max(0, cw - 2)];
            int labelY = cy + ch2 / 2;
            int labelX = cx + 1;
            if (label.Length > 0)
            {
                for (int j = 0; j < label.Length; j++)
                    Plot(labelY, labelX + j, label[j], 0);
            }
        }

        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                sb.Append(canvas[y, x]);
            sb.AppendLine();
        }

        sb.AppendLine();
        for (int i = 0; i < rects.Count; i++)
        {
            char ch = (char)('A' + i % 26);
            sb.AppendLine($"  {ch} = {rects[i].Label} ({rects[i].AreaRatio:P0}) [{rects[i].Source}]");
        }

        return sb.ToString();
    }

    // ── Core: Find the layout level ─────────────────────────────────

    /// <summary>
    /// Drill through the tree to find the level where children partition
    /// the parent area into distinct, non-overlapping regions.
    /// Handles Chromium/Electron apps with many wrapper layers.
    /// </summary>
    private static List<UIATreeNode> FindLayoutLevel(UIATreeNode root, Rectangle windowBounds, List<string>? log)
    {
        double windowArea = windowBounds.Width * (double)windowBounds.Height;
        var best = new List<UIATreeNode>();
        int bestDepth = -1;
        FindLayoutLevelRecursive(root, windowBounds, windowArea, 0, ref best, ref bestDepth, log);
        return best;
    }

    private static void FindLayoutLevelRecursive(
        UIATreeNode node, Rectangle windowBounds, double windowArea,
        int depth, ref List<UIATreeNode> best, ref int bestDepth, List<string>? log)
    {
        if (depth > MaxDrillDepth || node.Children.Count == 0)
            return;

        var indent = new string(' ', depth * 2);

        // Collect visible children with real bounds
        var visible = new List<UIATreeNode>();
        foreach (var c in node.Children)
        {
            if (c.IsOffscreen) continue;
            var b = c.BoundingRectangle;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) continue;
            visible.Add(c);
        }

        log?.Add($"{indent}D{depth} [{node.ControlType}] \"{Trunc(node.Name, 30)}\" → {node.Children.Count} children, {visible.Count} visible");

        if (visible.Count == 0)
            return;

        // Deduplicate stacked nodes (same/similar bounds) — keep the best one
        var deduped = DeduplicateStacked(visible, log);
        if (deduped.Count != visible.Count)
            log?.Add($"{indent}  Dedup: {visible.Count} → {deduped.Count}");

        // Filter to significant area
        var significant = new List<UIATreeNode>();
        foreach (var c in deduped)
        {
            double area = c.BoundingRectangle.Width * (double)c.BoundingRectangle.Height;
            if (area / windowArea >= MinAreaRatio)
                significant.Add(c);
        }

        // Evaluate this level: do the significant children partition the space?
        if (significant.Count >= 2)
        {
            double score = ScorePartition(significant, windowBounds);
            double bestScore = best.Count >= 2 ? ScorePartition(best, windowBounds) : 0;

            log?.Add($"{indent}  Candidates: {significant.Count}, Score={score:F3} (best={bestScore:F3})");

            if (score > bestScore ||
                (score > 0.3 && score >= bestScore * 0.9 && significant.Count > best.Count))
            {
                best = new List<UIATreeNode>(significant);
                bestDepth = depth;
                log?.Add($"{indent}  ★ NEW BEST at depth {depth}: {significant.Count} regions, score={score:F3}");
                foreach (var s in significant)
                {
                    var b = s.BoundingRectangle;
                    double ar = (b.Width * (double)b.Height) / windowArea;
                    log?.Add($"{indent}    [{s.ControlType}] \"{Trunc(s.Name, 25)}\" {b.X},{b.Y} {b.Width}x{b.Height} ({ar:P1})");
                }
            }
        }

        // Drill deeper into large children — use original visible list, not deduped,
        // so wrapper nodes that were deduped away still get explored (they may contain
        // important sub-layouts like NavigationMenu → SplitViewPane)
        foreach (var child in visible)
        {
            double childArea = child.BoundingRectangle.Width * (double)child.BoundingRectangle.Height;
            if (childArea / windowArea > 0.3)
                FindLayoutLevelRecursive(child, windowBounds, windowArea, depth + 1, ref best, ref bestDepth, log);
        }
    }

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Score how well a set of nodes partitions a window.
    /// High score = good coverage, low overlap, multiple distinct regions.
    /// </summary>
    private static double ScorePartition(List<UIATreeNode> nodes, Rectangle windowBounds)
    {
        double windowArea = windowBounds.Width * (double)windowBounds.Height;
        if (windowArea <= 0 || nodes.Count < 2)
            return 0;

        // Coverage: use clipped areas (intersection with window) for scoring
        double totalArea = 0;
        double totalRawArea = 0;
        foreach (var n in nodes)
        {
            var clipped = Rectangle.Intersect(n.BoundingRectangle, windowBounds);
            totalArea += clipped.Width * (double)clipped.Height;
            totalRawArea += n.BoundingRectangle.Width * (double)n.BoundingRectangle.Height;
        }
        double coverage = Math.Min(totalArea / windowArea, 1.0);

        // Penalty if nodes extend far beyond the window (scrollable content)
        double overflowRatio = totalRawArea > 0 ? totalArea / totalRawArea : 1.0;
        if (overflowRatio < 0.5) // >50% of total area is offscreen
            coverage *= overflowRatio * 2; // heavy penalty

        // Overlap penalty: pairwise overlap
        double overlapArea = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                var inter = Rectangle.Intersect(nodes[i].BoundingRectangle, nodes[j].BoundingRectangle);
                if (!inter.IsEmpty)
                    overlapArea += inter.Width * (double)inter.Height;
            }
        }
        double overlapPenalty = totalArea > 0 ? overlapArea / totalArea : 0;

        // Diversity: how many distinct position clusters (using center points)
        // Reward layouts with both horizontal and vertical separation
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            double cx = n.BoundingRectangle.X + n.BoundingRectangle.Width / 2.0;
            double cy = n.BoundingRectangle.Y + n.BoundingRectangle.Height / 2.0;
            minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
            minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
        }
        double spread = 0;
        if (windowBounds.Width > 0) spread += (maxX - minX) / windowBounds.Width;
        if (windowBounds.Height > 0) spread += (maxY - minY) / windowBounds.Height;
        spread = Math.Min(spread / 2.0, 1.0); // normalize to 0..1

        // Final score: coverage * (1 - overlap) * spread_bonus
        double score = coverage * (1.0 - overlapPenalty) * (0.3 + 0.7 * spread);

        return score;
    }

    /// <summary>
    /// Remove truly stacked/duplicate nodes: both rects must overlap heavily
    /// AND be similar in size. A small TitleBar inside a large Pane is NOT a duplicate.
    /// Common in Chromium: multiple Chrome_WidgetWin_0 Panes at the same position/size.
    /// </summary>
    private static List<UIATreeNode> DeduplicateStacked(List<UIATreeNode> nodes, List<string>? log = null)
    {
        var result = new List<UIATreeNode>(nodes);

        for (int i = result.Count - 1; i >= 0; i--)
        {
            bool removed = false;
            for (int j = 0; j < i; j++)
            {
                double areaI = result[i].BoundingRectangle.Width * (double)result[i].BoundingRectangle.Height;
                double areaJ = result[j].BoundingRectangle.Width * (double)result[j].BoundingRectangle.Height;

                // Both must be similar size (within 3x of each other)
                double sizeRatio = Math.Min(areaI, areaJ) / Math.Max(areaI, areaJ);
                if (sizeRatio < 0.33) continue;

                double overlap = OverlapRatio(result[i].BoundingRectangle, result[j].BoundingRectangle);
                if (overlap > OverlapThreshold)
                {
                    // Don't dedup nodes that contain semantic landmarks as children —
                    // they may be wrappers for important sub-layouts (e.g. NavigationMenu with SplitViewPane)
                    var smaller = areaI < areaJ ? result[i] : result[j];
                    if (HasSemanticChildren(smaller))
                    {
                            continue;
                    }

                    // Keep the more descriptive one
                    if (DescriptiveScore(result[i]) > DescriptiveScore(result[j]))
                        result[j] = result[i];
                    result.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
            if (removed) continue;
        }

        return result;
    }

    /// <summary>
    /// Check if a node or its immediate children have semantic landmarks
    /// (SplitViewPane, PaneRoot, NavigationMenu, etc.) that indicate real layout structure.
    /// </summary>
    private static bool HasSemanticChildren(UIATreeNode node)
    {
        if (IsSemanticLandmark(node)) return true;
        foreach (var child in node.Children)
            if (IsSemanticLandmark(child)) return true;
        return false;
    }

    private static bool IsSemanticLandmark(UIATreeNode node)
    {
        var cls = node.ClassName ?? "";
        var aid = node.AutomationId ?? "";
        return cls.Contains("SplitViewPane") ||
               cls.Contains("LandmarkTarget") ||
               aid.Contains("PaneRoot") ||
               aid.Contains("NavigationMenu") ||
               aid.Contains("ContentFrame") ||
               aid.Contains("ContentRoot") ||
               aid.Contains("MenuItemsHost");
    }

    // ── Content splitting ──────────────────────────────────────────

    /// <summary>
    /// If a layout region covers >50% of the window, try to split it into sub-regions
    /// by analyzing the children of the corresponding UIATreeNode.
    /// This handles cases like Explorer where a large "Content" pane actually contains
    /// a NavPanel + main content area.
    /// </summary>
    private static List<LayoutRect> SplitLargeRegions(
        List<LayoutRect> rects, List<UIATreeNode> layoutNodes, UIATreeNode root,
        Rectangle windowBounds, double windowArea, List<string>? log)
    {
        const double SplitThreshold = 0.50; // only split regions >50% of window
        const double MinSubScore = 0.25;    // sub-partition must be decent

        var result = new List<LayoutRect>();

        foreach (var rect in rects)
        {
            if (rect.AreaRatio <= SplitThreshold)
            {
                result.Add(rect);
                continue;
            }

            // Find the corresponding UIATreeNode
            var sourceNode = layoutNodes.FirstOrDefault(n =>
                n.BoundingRectangle == rect.Bounds);

            if (sourceNode == null || sourceNode.Children.Count == 0)
            {
                result.Add(rect);
                continue;
            }

            // Try to find a sub-partition within this large region
            var subNodes = FindSubPartition(sourceNode, windowBounds, windowArea, log);

            // Also search for semantic landmarks in the entire window area — they may be
            // siblings lost during dedup (e.g. Task Manager: NavigationMenu/SplitViewPane
            // is a sibling of TaskManagerMain, both wrapped in anonymous Panes)
            // Use window bounds, not region bounds, because the landmark may be adjacent
            var landmarks = FindSemanticLandmarksInBounds(root, windowBounds, windowArea);
            // Filter: only landmarks NOT already covered by other (non-large) result rects
            var otherRects = rects.Where(r => r.AreaRatio <= SplitThreshold).ToList();
            landmarks.RemoveAll(l =>
                otherRects.Any(r => OverlapRatio(l.BoundingRectangle, r.Bounds) > 0.5));
            if (landmarks.Count >= 1)
            {
                // Combine: landmarks + existing sub-nodes (or sourceNode if no sub-nodes)
                var combined = new List<UIATreeNode>(landmarks);
                var baseNodes = (subNodes != null && subNodes.Count >= 2) ? subNodes : [sourceNode];
                foreach (var bn in baseNodes)
                {
                    // Only add if it doesn't overlap heavily with any landmark
                    bool overlaps = landmarks.Any(l =>
                        OverlapRatio(l.BoundingRectangle, bn.BoundingRectangle) > 0.5);
                    if (!overlaps)
                        combined.Add(bn);
                }

                if (combined.Count >= 2)
                {
                    double combinedScore = ScorePartition(combined, windowBounds);
                    double existingScore = (subNodes != null && subNodes.Count >= 2) ? ScorePartition(subNodes, windowBounds) : 0;
                    log?.Add($"  SplitLarge (semantic): {rect.Label} ({rect.AreaRatio:P0}) → {combined.Count} nodes (landmarks={landmarks.Count}), score={combinedScore:F3} vs existing={existingScore:F3}");
                    if (combinedScore > existingScore)
                        subNodes = combined;
                }
            }

            if (subNodes == null || subNodes.Count < 2)
            {
                result.Add(rect);
                continue;
            }

            double subScore = ScorePartition(subNodes, windowBounds);
            log?.Add($"  SplitLarge: {rect.Label} ({rect.AreaRatio:P0}) → {subNodes.Count} sub-regions, score={subScore:F3}");

            if (subScore < MinSubScore)
            {
                result.Add(rect);
                continue;
            }

            // Replace the large region with its sub-regions
            log?.Add($"  ★ SPLIT {rect.Label} into {subNodes.Count} sub-regions");
            foreach (var sub in subNodes)
            {
                var b = sub.BoundingRectangle;
                if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) continue;
                double areaRatio = (b.Width * (double)b.Height) / windowArea;
                if (areaRatio < MinAreaRatio) continue;

                result.Add(new LayoutRect
                {
                    Label = ClassifyByGeometry(b, windowBounds, sub),
                    Bounds = b,
                    AreaRatio = areaRatio,
                    ControlType = sub.ControlType,
                    Source = sub.DisplayText,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Search the entire tree for semantic landmark nodes (SplitViewPane, PaneRoot, etc.)
    /// whose bounds fall within the given region. Returns nodes that represent real layout
    /// structure hidden deep in wrapper hierarchies.
    /// </summary>
    private static List<UIATreeNode> FindSemanticLandmarksInBounds(
        UIATreeNode root, Rectangle regionBounds, double windowArea)
    {
        var result = new List<UIATreeNode>();
        FindSemanticLandmarksRecursive(root, regionBounds, windowArea, result, 0);
        return result;
    }

    private static void FindSemanticLandmarksRecursive(
        UIATreeNode node, Rectangle regionBounds, double windowArea,
        List<UIATreeNode> result, int depth)
    {
        if (depth > MaxDrillDepth) return;

        foreach (var child in node.Children)
        {
            var b = child.BoundingRectangle;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) continue;
            if (child.IsOffscreen) continue;

            double area = b.Width * (double)b.Height;
            if (area / windowArea < MinAreaRatio) continue;

            // Must be within the region bounds
            if (!regionBounds.Contains(b) && OverlapRatio(b, regionBounds) < 0.8) continue;

            // Is this a semantic landmark with distinct bounds (not full-size wrapper)?
            if (IsSemanticLandmark(child))
            {
                double sizeRatio = area / (regionBounds.Width * (double)regionBounds.Height);
                // Only keep landmarks that are a real sub-region, not full-size wrappers
                if (sizeRatio < 0.8)
                {
                    result.Add(child);
                    // Don't recurse into this landmark's children — avoid nested duplicates
                    // (e.g. PaneRoot containing MenuItemsHost)
                    continue;
                }
            }

            // Recurse into children
            FindSemanticLandmarksRecursive(child, regionBounds, windowArea, result, depth + 1);
        }
    }

    /// <summary>
    /// Drill into a large node to find meaningful sub-regions.
    /// Uses BFS to explore all paths through wrapper nodes, not just one linear drill.
    /// This handles apps like Windows Settings where the layout is buried deep:
    /// Window → Window → Custom(NavigationView) → SplitViewPane + LandmarkTarget
    /// </summary>
    private static List<UIATreeNode>? FindSubPartition(
        UIATreeNode node, Rectangle windowBounds, double windowArea, List<string>? log)
    {
        List<UIATreeNode>? best = null;
        double bestScore = 0;

        // BFS queue: explore nodes that are large enough to contain sub-layouts
        var queue = new Queue<(UIATreeNode node, int depth)>();
        queue.Enqueue((node, 0));
        var visited = new HashSet<UIATreeNode>();

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth > MaxDrillDepth || !visited.Add(current)) continue;
            if (current.Children.Count == 0) continue;

            var visible = new List<UIATreeNode>();
            foreach (var c in current.Children)
            {
                if (c.IsOffscreen) continue;
                var b = c.BoundingRectangle;
                if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) continue;
                double area = b.Width * (double)b.Height;
                if (area / windowArea >= MinAreaRatio)
                    visible.Add(c);
            }

            if (visible.Count >= 2)
            {
                var deduped = DeduplicateStacked(visible);
                double score = ScorePartition(deduped, windowBounds);

                // Bonus for semantic landmarks: reward partitions where multiple children
                // have structural hints (pane, landmark, toolbar, etc.)
                int semanticCount = deduped.Count(n =>
                    (n.ClassName ?? "").Contains("SplitViewPane") ||
                    (n.ClassName ?? "").Contains("LandmarkTarget") ||
                    (n.AutomationId ?? "").Contains("PaneRoot") ||
                    (n.AutomationId ?? "").Contains("ContentFrame") ||
                    (n.AutomationId ?? "").Contains("ContentRoot") ||
                    n.ControlType is "Tab" or "ToolBar" or "StatusBar" or "MenuBar");
                if (semanticCount >= 2)
                    score *= 1.4; // 40% bonus: multiple semantic landmarks = real layout
                else if (semanticCount == 1 && deduped.Count <= 3)
                    score *= 1.2; // 20% bonus: at least one landmark

                log?.Add($"    SubScan D{depth}: {deduped.Count} regions, score={score:F3}{(semanticCount > 0 ? " (semantic)" : "")} best={bestScore:F3}");
                if (score > bestScore && deduped.Count >= 2)
                {
                    best = deduped;
                    bestScore = score;
                    log?.Add($"    ★ SubPartition D{depth}: {deduped.Count} regions, score={score:F3}");
                }
            }

            // Enqueue children that cover >20% of window — they might contain the real layout
            foreach (var c in visible)
            {
                double a = c.BoundingRectangle.Width * (double)c.BoundingRectangle.Height;
                if (a / windowArea > 0.20)
                    queue.Enqueue((c, depth + 1));
            }
        }

        return best;
    }

    // ── Geometry classification ──────────────────────────────────────

    private static string ClassifyByGeometry(Rectangle bounds, Rectangle window, UIATreeNode node)
    {
        double relX = (bounds.X - window.X) / (double)window.Width;
        double relY = (bounds.Y - window.Y) / (double)window.Height;
        double relW = bounds.Width / (double)window.Width;
        double relH = bounds.Height / (double)window.Height;

        // Semantic hints from ControlType
        if (node.ControlType == "TitleBar") return "TitleBar";
        if (node.ControlType == "MenuBar") return "MenuBar";
        if (node.ControlType == "StatusBar") return "StatusBar";
        if (node.ControlType == "ToolBar") return "Toolbar";
        if (node.ControlType == "Tab") return "TabBar";

        // Semantic hints from ClassName/AutomationId (XAML/WinUI landmarks)
        var cls = node.ClassName ?? "";
        var aid = node.AutomationId ?? "";
        if (cls.Contains("SplitViewPane") || aid.Contains("NavigationMenu") ||
            aid.Contains("MenuItemsHost") || aid.Contains("PaneRoot"))
            return "NavPanel";
        if (cls.Contains("LandmarkTarget") || aid.Contains("ContentArea") ||
            aid.Contains("ContentFrame") || aid.Contains("ContentRoot"))
            return "Content";
        if (cls.Contains("NavigationView") && !cls.Contains("NavigationViewItem"))
            return relW < 0.35 ? "NavPanel" : "Content";

        // Full width + short + at top → horizontal bar
        if (relW > 0.7 && relH < 0.12 && relY < 0.15)
            return "TopBar";

        // Full width + short + at bottom
        if (relW > 0.7 && relH < 0.08 && relY > 0.85)
            return "BottomBar";

        // Narrow + tall + at left edge → vertical app rail
        if (relW < 0.08 && relH > 0.35 && relX < 0.05)
            return "AppRail";

        // Medium width + tall + at left → navigation/list panel
        if (relW < 0.35 && relH > 0.4 && relX < 0.15)
            return "NavPanel";

        // Medium width + tall + at right → side panel
        if (relW < 0.35 && relH > 0.4 && relX > 0.55)
            return "SidePanel";

        // Large area → main content
        if (relW * relH > 0.25)
            return "Content";

        // Short horizontal band
        if (relH < 0.1 && relW > 0.3)
            return "Header";

        return "Region";
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static double OverlapRatio(Rectangle a, Rectangle b)
    {
        var intersection = Rectangle.Intersect(a, b);
        if (intersection.IsEmpty) return 0;

        double intersectArea = intersection.Width * (double)intersection.Height;
        double smallerArea = Math.Min(
            a.Width * (double)a.Height,
            b.Width * (double)b.Height);

        return smallerArea > 0 ? intersectArea / smallerArea : 0;
    }

    private static int DescriptiveScore(UIATreeNode node)
    {
        int score = 0;
        if (!string.IsNullOrEmpty(node.Name)) score += 2;
        if (!string.IsNullOrEmpty(node.AutomationId)) score += 1;
        if (node.ControlType is not ("Pane" or "Group")) score += 1;
        if (node.Children.Count > 0) score += 1;
        return score;
    }

    private static List<LayoutRect> MergeOverlappingRects(List<LayoutRect> rects)
    {
        var result = new List<LayoutRect>(rects);
        for (int i = result.Count - 1; i >= 0; i--)
        {
            for (int j = 0; j < i; j++)
            {
                double areaI = result[i].Bounds.Width * (double)result[i].Bounds.Height;
                double areaJ = result[j].Bounds.Width * (double)result[j].Bounds.Height;
                double sizeRatio = Math.Min(areaI, areaJ) / Math.Max(areaI, areaJ);
                if (sizeRatio < 0.33) continue;

                if (OverlapRatio(result[i].Bounds, result[j].Bounds) > OverlapThreshold)
                {
                    if (result[i].Source.Length > result[j].Source.Length)
                        result[j] = result[i];
                    result.RemoveAt(i);
                    break;
                }
            }
        }
        return result;
    }
}
