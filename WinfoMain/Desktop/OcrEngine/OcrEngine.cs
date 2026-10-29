namespace WinfoMain.Desktop.OcrEngine;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinfoMain.Common;

/// <summary>
/// OCR Engine — singleton that captures a window's bitmap and fills its ContentOcr.
/// Uses WinRT Windows.Media.Ocr (on-device, no network).
///
/// Usage: await OcrEngine.Instance.ProcessAsync(window);
/// Result lands in window.ContentOcr (Words + Lines).
/// </summary>
public class OcrEngine : IDisposable
{
    public static OcrEngine Instance { get; } = new();
    private OcrEngine()
    {
        _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "WinRT OCR engine could not be created. No language pack installed?");
    }

    private readonly Windows.Media.Ocr.OcrEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    /// Capture the window's bitmap and run OCR. Results are stored in window.ContentOcr.
    /// Thread-safe — only one OCR operation runs at a time.
    /// </summary>
    public async Task ProcessAsync(DesktopWindow window)
    {
        await _gate.WaitAsync();
        try
        {
            using var bitmap = WindowCapture.Capture(window.Hwnd);
            if (bitmap == null) return;

            var content = window.ContentOcr;
            content.Clear();

            await RecognizeIntoContent(bitmap, content);
            GroupWordsIntoLines(content);
            MergePunctuation(content);
            InsertEmptyLines(content);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Run OCR on an arbitrary bitmap. Results go into the provided WinContent.
    /// Thread-safe — shares the same gate as ProcessAsync.
    /// scaleFactor: 1 = original, 2 = 2x upscale, 3 = 3x upscale (HighQualityBicubic).
    /// colorCount: 0 = no reduction, 2 = black/white (Otsu), 4/16/256 = grayscale quantization.
    /// </summary>
    public async Task ProcessBitmapAsync(Bitmap bitmap, WinContent content, int scaleFactor = 1, int colorCount = 0)
    {
        await _gate.WaitAsync();
        try
        {
            content.Clear();

            // Memory optimization: ReduceColors BEFORE ScaleBitmap.
            // ReduceColors on the original (e.g. 1920x1320 = 10 MB) is much cheaper than
            // on the 3x-scaled bitmap (5760x3960 = 91 MB). The scaled bitmap is then also
            // smaller in practice because it's grayscale data in 32bpp container.
            //
            // NOTE: If OCR quality degrades, check if this order matters. The original code
            // scaled first, then reduced colors. Reducing first means we lose color info before
            // upscaling, but since WinRT OCR works on grayscale internally anyway, this should
            // be fine. If issues arise: swap the two blocks back (scale first, then reduce).
            Bitmap? processed = null;
            try
            {
                var current = bitmap;

                if (colorCount > 0)
                {
                    processed = ReduceColors(current, colorCount);
                    current = processed;
                }

                if (scaleFactor > 1)
                {
                    var scaled = ScaleBitmap(current, scaleFactor);
                    processed?.Dispose();
                    processed = scaled;
                    current = processed;
                }

                await RecognizeIntoContent(current, content, scaleFactor);
            }
            finally
            {
                processed?.Dispose();
            }

            GroupWordsIntoLines(content);
            MergePunctuation(content);
            InsertEmptyLines(content);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Core OCR: Bitmap → WinRT SoftwareBitmap → RecognizeAsync → collect words into content.
    /// When scaleFactor > 1, all bounds are divided back to original bitmap coordinates.
    /// </summary>
    private async Task RecognizeIntoContent(Bitmap bitmap, WinContent content, int scaleFactor = 1)
    {
        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var result = await _engine.RecognizeAsync(softwareBitmap);

        foreach (var ocrLine in result.Lines)
        {
            foreach (var ocrWord in ocrLine.Words)
            {
                // WinRT OCR bounds are systematically offset (X too far right, Y too low)
                // and too short (descenders clipped). Correct here for all downstream consumers.
                int rawX = (int)ocrWord.BoundingRect.X - 1;
                int rawY = (int)ocrWord.BoundingRect.Y - 2;
                int rawW = (int)ocrWord.BoundingRect.Width;
                int rawH = (int)(ocrWord.BoundingRect.Height * 1.25);
                int ocrH = (int)ocrWord.BoundingRect.Height;

                if (scaleFactor > 1)
                {
                    rawX /= scaleFactor;
                    rawY /= scaleFactor;
                    rawW /= scaleFactor;
                    rawH /= scaleFactor;
                    ocrH /= scaleFactor;
                }

                var word = new WinWordOcr
                {
                    Text = ocrWord.Text,
                    Bounds = new Rectangle(rawX, rawY, rawW, rawH),
                    OcrHeight = ocrH,
                };
                content.Words.Add(word);
            }
        }
    }

    /// <summary>
    /// Groups words into lines based on vertical overlap of their bounds.
    /// Two words are on the same line if their Y-ranges overlap.
    /// Handles mixed font sizes within a line (e.g. heading + small text).
    /// Words within each line are sorted left-to-right by X position.
    /// </summary>
    private static void GroupWordsIntoLines(WinContent content)
    {
        if (content.Words.Count == 0) return;

        // Sort all words top-to-bottom, then left-to-right
        var sorted = content.Words.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).ToList();

        var currentLine = new WinLineOcr();
        currentLine.Words.Add(sorted[0]);

        for (int i = 1; i < sorted.Count; i++)
        {
            var word = sorted[i];

            if (OverlapsVertically(currentLine, word))
            {
                currentLine.Words.Add(word);
            }
            else
            {
                SortLineByX(currentLine);
                content.Lines.Add(currentLine);

                currentLine = new WinLineOcr();
                currentLine.Words.Add(word);
            }
        }

        SortLineByX(currentLine);
        content.Lines.Add(currentLine);
    }

    /// <summary>
    /// Merge single-character punctuation (comma, period, exclamation mark, etc.)
    /// into the preceding word on the same line. Updates Text and Bounds.
    /// </summary>
    private static void MergePunctuation(WinContent content)
    {
        for (int lineIdx = 0; lineIdx < content.Lines.Count; lineIdx++)
        {
            var line = content.Lines[lineIdx];

            for (int i = line.Words.Count - 1; i >= 0; i--)
            {
                var word = line.Words[i];
                if (word.Text.Length == 1 && IsPunctuation(word.Text[0]))
                {
                    // Find merge target: previous word in this line, or last word of previous line
                    WinWordBase? prev = null;
                    if (i > 0)
                    {
                        prev = line.Words[i - 1];
                    }
                    else if (lineIdx > 0)
                    {
                        var prevLine = content.Lines[lineIdx - 1];
                        if (prevLine.Words.Count > 0)
                            prev = prevLine.Words[^1];
                    }

                    if (prev == null) continue;

                    // Merge: append text, extend width conservatively
                    prev.Text += word.Text;
                    int charWidth = prev is WinWordOcr ocrPrev && ocrPrev.CharWidth > 0
                        ? (int)Math.Ceiling(ocrPrev.CharWidth * 0.5f)
                        : Math.Max(2, prev.Bounds.Height / 4);
                    prev.Bounds = new Rectangle(
                        prev.Bounds.X, prev.Bounds.Y,
                        prev.Bounds.Width + charWidth, prev.Bounds.Height);

                    // Remove from line and from global word list
                    line.Words.RemoveAt(i);
                    content.Words.Remove(word);
                }
            }

            // Remove empty lines left after merging all their words away
            if (line.Words.Count == 0)
            {
                content.Lines.RemoveAt(lineIdx);
                lineIdx--;
            }
        }
    }

    private static bool IsPunctuation(char c) =>
        c is ',' or '.' or '!' or '?' or ';' or ':';

    /// <summary>
    /// Detect gaps between consecutive lines that are larger than the typical
    /// line spacing. Insert at most ONE empty WinLine per gap as a separator.
    /// </summary>
    private static void InsertEmptyLines(WinContent content)
    {
        if (content.Lines.Count < 2) return;

        // Calculate typical line step (median of top-to-top distance between consecutive lines)
        var steps = new List<int>();
        for (int i = 1; i < content.Lines.Count; i++)
        {
            int step = content.Lines[i].Bounds.Top - content.Lines[i - 1].Bounds.Top;
            if (step > 0) steps.Add(step);
        }

        if (steps.Count == 0) return;

        steps.Sort();
        int medianStep = steps[steps.Count / 2];

        // Threshold: gap bigger than 1.8x the typical line step → insert one empty line
        int threshold = (int)(medianStep * 1.8);

        for (int i = content.Lines.Count - 1; i >= 1; i--)
        {
            int step = content.Lines[i].Bounds.Top - content.Lines[i - 1].Bounds.Top;
            if (step > threshold)
            {
                content.Lines.Insert(i, new WinLineOcr());
            }
        }
    }

    /// <summary>
    /// Check if a word belongs to the current line.
    /// Uses the median height of existing words as the reference zone to prevent
    /// the line from growing unbounded and swallowing the next line.
    /// A word belongs to the line if its vertical midpoint falls within the
    /// median-based zone of the line.
    /// </summary>
    private static bool OverlapsVertically(WinLineBase line, WinWordBase word)
    {
        int lineTop = line.Words.Min(w => w.Bounds.Top);

        // Use median word height as reference (robust against outliers)
        var heights = line.Words.Select(w => w.Bounds.Height).OrderBy(h => h).ToList();
        int medianH = heights[heights.Count / 2];

        // The line zone extends from lineTop to lineTop + medianH (with small tolerance)
        int zoneBottom = lineTop + medianH + Math.Max(2, medianH / 3);

        // Word's midpoint must fall within the zone
        int wordMid = word.Bounds.Top + word.Bounds.Height / 2;
        return wordMid >= lineTop && wordMid <= zoneBottom;
    }

    private static void SortLineByX(WinLineBase line)
    {
        var sorted = line.Words.OrderBy(w => w.Bounds.X).ToList();
        line.Words.Clear();
        line.Words.AddRange(sorted);
    }

    // --- Debug Visualization ---

    /// <summary>
    /// Render a WinContent to a bitmap with all words drawn at their bounds positions.
    /// Optional background bitmap (e.g. original screenshot) for visual comparison.
    /// </summary>
    public static Bitmap? RenderDebugBitmap(WinContent content, Bitmap? background = null)
    {
        if (content.Words.Count == 0) return null;

        int width = content.Words.Max(w => w.Bounds.Right) + 10;
        int height = content.Words.Max(w => w.Bounds.Bottom) + 10;
        if (background != null)
        {
            width = Math.Max(width, background.Width);
            height = Math.Max(height, background.Height);
        }
        if (width <= 0 || height <= 0) return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (background != null)
            g.DrawImage(background, 0, 0);
        else
            g.Clear(Color.FromArgb(30, 30, 30));

        using var linePen = new Pen(Color.FromArgb(50, 80, 80, 80), 1);
        foreach (var line in content.Lines)
        {
            if (line.Words.Count == 0) continue;
            g.DrawRectangle(linePen, line.Bounds);
        }

        using var wordBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
        using var boundsPen = new Pen(Color.FromArgb(180, 255, 255, 80), 1);

        // Internal Leading offset: GDI+ reserves space above glyphs for diacritics.
        // We subtract it so rendered text aligns with the actual glyph top.
        static float InternalLeadingPixels(Font f)
        {
            var ff = f.FontFamily;
            int em = ff.GetEmHeight(f.Style);
            int leading = ff.GetCellAscent(f.Style) + ff.GetCellDescent(f.Style) - em;
            return f.Size * leading / em;
        }

        foreach (var word in content.Words.OfType<WinWordOcr>())
        {
            var b = word.Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;

            float fs = word.OcrHeight > 0 ? word.OcrHeight : b.Height;
            if (fs < 4) fs = 4;
            using var font = new Font("Segoe UI", fs, GraphicsUnit.Pixel);
            g.DrawRectangle(boundsPen, b);
            g.DrawString(word.Text, font, wordBrush, b.X, b.Y - InternalLeadingPixels(font), StringFormat.GenericTypographic);
        }

        return bitmap;
    }

    // --- Bitmap Scaling ---

    /// <summary>
    /// Scale a bitmap by an integer factor using NearestNeighbor interpolation.
    /// Ideal for text upscaling — preserves sharp edges, no blurring.
    /// </summary>
    private static Bitmap ScaleBitmap(Bitmap source, int factor)
    {
        int w = source.Width * factor;
        int h = source.Height * factor;
        var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(source, 0, 0, w, h);
        return scaled;
    }

    // --- Color Reduction ---

    /// <summary>
    /// Reduce colors in a bitmap. 2 = black/white (Otsu threshold),
    /// other values = grayscale quantization to N levels.
    /// </summary>
    private static Bitmap ReduceColors(Bitmap source, int colorCount)
    {
        int w = source.Width, h = source.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var srcData = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            int byteCount = srcData.Stride * h;
            var srcPixels = new byte[byteCount];
            var dstPixels = new byte[byteCount];
            Marshal.Copy(srcData.Scan0, srcPixels, 0, byteCount);

            // Convert to grayscale values first
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * srcData.Stride;
                for (int x = 0; x < w; x++)
                {
                    int off = rowOff + x * 4;
                    // Luminance: 0.299R + 0.587G + 0.114B
                    gray[y * w + x] = (byte)(srcPixels[off + 2] * 77 + srcPixels[off + 1] * 150 + srcPixels[off] * 29 >> 8);
                }
            }

            byte threshold = 128;
            if (colorCount == 2)
            {
                // Otsu's method
                var histogram = new int[256];
                foreach (var g in gray) histogram[g]++;

                int total = w * h;
                float sum = 0;
                for (int i = 0; i < 256; i++) sum += i * histogram[i];

                float sumB = 0, wB = 0;
                float maxVariance = 0;
                for (int t = 0; t < 256; t++)
                {
                    wB += histogram[t];
                    if (wB == 0) continue;
                    float wF = total - wB;
                    if (wF == 0) break;
                    sumB += t * histogram[t];
                    float mB = sumB / wB;
                    float mF = (sum - sumB) / wF;
                    float variance = wB * wF * (mB - mF) * (mB - mF);
                    if (variance > maxVariance)
                    {
                        maxVariance = variance;
                        threshold = (byte)t;
                    }
                }
            }

            // Quantize
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * dstData.Stride;
                for (int x = 0; x < w; x++)
                {
                    int off = rowOff + x * 4;
                    byte g = gray[y * w + x];
                    byte q;
                    if (colorCount == 2)
                    {
                        q = g > threshold ? (byte)255 : (byte)0;
                    }
                    else
                    {
                        // Quantize to N levels: map 0-255 → 0..(N-1) → back to 0-255
                        int level = g * (colorCount - 1) / 255;
                        q = (byte)(level * 255 / (colorCount - 1));
                    }
                    dstPixels[off] = q;     // B
                    dstPixels[off + 1] = q; // G
                    dstPixels[off + 2] = q; // R
                    dstPixels[off + 3] = 255; // A
                }
            }

            Marshal.Copy(dstPixels, 0, dstData.Scan0, byteCount);
        }
        finally
        {
            source.UnlockBits(srcData);
            result.UnlockBits(dstData);
        }

        return result;
    }

    // --- Bitmap Conversion ---

    private static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap bitmap)
    {
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var byteCount = bitmapData.Stride * bitmap.Height;
            var pixels = new byte[byteCount];
            Marshal.Copy(bitmapData.Scan0, pixels, 0, byteCount);

            // GDI+ Format32bppArgb is already BGRA — matches WinRT Bgra8
            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                bitmap.Width,
                bitmap.Height,
                BitmapAlphaMode.Premultiplied);

            softwareBitmap.CopyFromBuffer(pixels.AsBuffer());
            return softwareBitmap;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

}
