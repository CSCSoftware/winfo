namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using WinfoMain.Common;

/// <summary>
/// Merges OCR words into an existing UIA LayoutResult.
/// UIA provides structure (regions, bounds, types), OCR provides text content.
/// Only fills regions that have fewer OCR-relevant texts than OCR can provide.
/// Sets DataSource to "UIA+OCR" when OCR texts were added.
/// </summary>
public static class OcrTextMerger
{
    /// <summary>
    /// Merge OCR words into UIA regions where UIA texts are sparse.
    /// OCR word bounds are window-relative (from OcrEngine pipeline).
    /// </summary>
    public static void Merge(LayoutResult uiaResult, WinContent ocrContent, Rectangle windowBounds)
    {
        if (uiaResult.Regions.Count == 0 || ocrContent.Words.Count == 0)
            return;

        bool anyMerged = false;

        foreach (var region in uiaResult.Regions)
        {
            // Both region.Bounds and OCR word bounds are window-relative — compare directly
            var regionBounds = region.Bounds;

            // Collect OCR words that fall inside this region
            var ocrWordsInRegion = new List<WinWordBase>();
            foreach (var word in ocrContent.Words)
            {
                if (regionBounds.IntersectsWith(word.Bounds))
                    ocrWordsInRegion.Add(word);
            }

            if (ocrWordsInRegion.Count == 0)
                continue;

            // Only merge if OCR provides significantly more text than UIA
            int uiaTextCount = region.Texts.Count;
            if (uiaTextCount >= ocrWordsInRegion.Count)
                continue;

            // Replace UIA texts with OCR texts (OCR has more content)
            region.Texts.Clear();

            foreach (var word in ocrWordsInRegion)
            {
                region.Texts.Add(new RegionText
                {
                    Text = word.Text,
                    X = word.Bounds.X - regionBounds.X,
                    Y = word.Bounds.Y - regionBounds.Y,
                    Width = word.Bounds.Width,
                    Height = word.Bounds.Height,
                    ControlType = "OCR",
                });
            }

            anyMerged = true;
        }

        if (anyMerged)
            uiaResult.DataSource = "UIA+OCR";
    }
}
