namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using System.Drawing.Imaging;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Extracts all visible text elements from a UIATreeNode tree and renders
/// them as a debug bitmap — each word drawn at its screen position,
/// offset to window-relative coordinates.
/// </summary>
public static class SceneVisualizer
{
    /// <summary>A visible text element with its bounds.</summary>
    public record struct VisibleWord(string Text, Rectangle Bounds, string ControlType);

    /// <summary>ControlTypes that always act as clip containers.</summary>
    private static readonly HashSet<string> ClipContainerTypes =
        ["List", "Tree", "DataGrid", "Table", "Document"];

    /// <summary>
    /// Recursively collect all visible text elements from the UIA tree.
    /// - Only leaf-level text (skips parent if child carries same text)
    /// - Truncates multi-line text to first line
    /// - Clips to window bounds AND scroll container bounds
    /// - Skips elements flagged IsOffscreen
    /// </summary>
    public static List<VisibleWord> CollectVisibleWords(UIATreeNode root, Rectangle windowBounds)
    {
        var words = new List<VisibleWord>();
        Collect(root, words, windowBounds, windowBounds);
        return Deduplicate(words);
    }

    /// <param name="clipRect">
    /// The visible clip region — starts as windowBounds, tightens when entering
    /// scroll containers (List, Tree, DataGrid, Document).
    /// </param>
    private static void Collect(UIATreeNode node, List<VisibleWord> words,
        Rectangle windowBounds, Rectangle clipRect)
    {
        if (node.IsOffscreen) return;

        var b = node.BoundingRectangle;
        if (b.Width <= 0 || b.Height <= 0) return;

        // Element must be at least partially inside the clip region
        if (!b.IntersectsWith(clipRect)) return;

        // Skip elements with 1x1 bounds (Chromium's "clipped offscreen" marker)
        if (b.Width <= 1 && b.Height <= 1) return;

        // If element is much taller/wider than the clip region, treat it as a
        // container (e.g. email body, long document) — don't extract its text,
        // only recurse into children which may have proper bounds
        bool isOversized = b.Height > clipRect.Height * 1.5 || b.Width > clipRect.Width * 1.5;

        var text = isOversized ? null : node.GetVisibleText();

        if (!string.IsNullOrWhiteSpace(text))
        {
            // Skip parent text if any child carries the same (or starting) text
            bool childHasSameText = false;
            if (node.Children.Count > 0)
            {
                foreach (var child in node.Children)
                {
                    var childText = child.GetVisibleText();
                    if (childText != null && text.StartsWith(childText))
                    {
                        childHasSameText = true;
                        break;
                    }
                }
            }

            if (!childHasSameText)
            {
                // Truncate to first line
                var firstLine = text;
                int nlPos = text.IndexOf('\n');
                if (nlPos >= 0)
                    firstLine = text[..nlPos].TrimEnd();

                if (!string.IsNullOrWhiteSpace(firstLine))
                    words.Add(new VisibleWord(firstLine, b, node.ControlType));
            }
        }

        // Tighten clip region when entering a scroll/clip container.
        // Detect scroll containers by: known ControlType, ScrollPattern, or
        // any node whose children extend beyond its own bounds (implicit scroll).
        var childClip = clipRect;
        bool isClipContainer = ClipContainerTypes.Contains(node.ControlType)
            || node.SupportedPatterns.Contains("ScrollPattern");

        if (!isClipContainer && node.Children.Count > 2)
        {
            // Heuristic: if any child extends below this node's bottom → implicit scroll
            int nodeBottom = b.Y + b.Height;
            foreach (var child in node.Children)
            {
                if (child.BoundingRectangle.Y + child.BoundingRectangle.Height > nodeBottom + 5)
                {
                    isClipContainer = true;
                    break;
                }
            }
        }

        if (isClipContainer)
        {
            childClip = Rectangle.Intersect(clipRect, b);
            if (childClip.IsEmpty) return;
        }

        foreach (var child in node.Children)
            Collect(child, words, windowBounds, childClip);
    }

    /// <summary>
    /// Remove duplicates: same text at overlapping positions.
    /// Keep the one with smaller bounds (more specific).
    /// </summary>
    private static List<VisibleWord> Deduplicate(List<VisibleWord> words)
    {
        var result = new List<VisibleWord>(words.Count);
        var skip = new HashSet<int>();

        for (int i = 0; i < words.Count; i++)
        {
            if (skip.Contains(i)) continue;

            for (int j = i + 1; j < words.Count; j++)
            {
                if (skip.Contains(j)) continue;
                if (words[i].Text != words[j].Text) continue;

                if (words[i].Bounds.IntersectsWith(words[j].Bounds))
                {
                    int areaI = words[i].Bounds.Width * words[i].Bounds.Height;
                    int areaJ = words[j].Bounds.Width * words[j].Bounds.Height;
                    skip.Add(areaI <= areaJ ? j : i);
                }
            }
        }

        for (int i = 0; i < words.Count; i++)
        {
            if (!skip.Contains(i))
                result.Add(words[i]);
        }

        return result;
    }

    /// <summary>
    /// Render all visible text elements as a debug bitmap.
    /// Dark background, words drawn at their window-relative positions.
    /// Font size capped at 14px for readable UI text.
    /// </summary>
    public static Bitmap? RenderBitmap(List<VisibleWord> words, Rectangle windowBounds)
    {
        if (words.Count == 0) return null;

        int w = Math.Max(windowBounds.Width, 100);
        int h = Math.Max(windowBounds.Height, 100);
        int offsetX = windowBounds.X;
        int offsetY = windowBounds.Y;

        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.FromArgb(30, 30, 30));

        using var textBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
        using var boundsPen = new Pen(Color.FromArgb(40, 80, 80, 80), 1);
        using var buttonBrush = new SolidBrush(Color.FromArgb(180, 220, 180));
        using var treeBrush = new SolidBrush(Color.FromArgb(180, 180, 220));

        foreach (var word in words)
        {
            var b = word.Bounds;
            int x = b.X - offsetX;
            int y = b.Y - offsetY;

            if (x + b.Width < 0 || y + b.Height < 0 || x >= w || y >= h) continue;

            g.DrawRectangle(boundsPen, x, y, b.Width, b.Height);

            var brush = word.ControlType switch
            {
                "Button" or "SplitButton" => buttonBrush,
                "TreeItem" or "ListItem" or "TabItem" => treeBrush,
                _ => textBrush
            };

            float fs = Math.Min(b.Height * 0.7f, 14f);
            if (fs < 7) fs = 7;

            try
            {
                using var font = new Font("Segoe UI", fs, GraphicsUnit.Pixel);
                g.DrawString(word.Text, font, brush, x, y, StringFormat.GenericTypographic);
            }
            catch
            {
                // GDI+ can fail — skip
            }
        }

        return bitmap;
    }
}
