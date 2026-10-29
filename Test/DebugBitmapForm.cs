namespace WinfoMain.Test;

using System.Drawing;
using System.Drawing.Imaging;
using WinfoMain.Common;
using WinfoMain.Desktop.OcrEngine;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Debug window with layered rendering and zoom:
///   Layer 0: Screenshot (original bitmap)
///   Layer 1: Edges (Sobel edge detection overlay)
///   Layer 2: BSP Cells (raw leaf cells before merging, amber dotted)
///   Layer 3: Scene Regions (colored rectangles with labels)
///   Layer 4: OCR Bounds (yellow rectangles)
///   Layer 5: OCR Text (rendered words)
/// Each layer toggleable via checkbox. Ctrl+MouseWheel to zoom.
/// </summary>
public class DebugBitmapForm : Form
{
    private static readonly Color ThemeBg = Color.FromArgb(30, 30, 30);
    private static readonly Color ThemePanel = Color.FromArgb(45, 45, 45);
    private static readonly Color ThemeButton = Color.FromArgb(60, 60, 60);
    private static readonly Color ThemeText = Color.FromArgb(220, 220, 220);

    // Persist checkbox states + settings to file (survives app restart)
    private static readonly string _settingsPath = Path.Combine(
        Path.GetDirectoryName(Application.ExecutablePath)!, "debug-bitmap-settings.json");
    private static Dictionary<string, string> _savedSettings = LoadSettings();

    private readonly Bitmap? _screenshotLayer;
    private readonly Bitmap? _edgeLayer;
    private readonly Bitmap? _ocrBoundsLayer;
    private readonly WinContent _content;
    private BspRegionDetector.BspResult? _bspDebug;
    private readonly LayoutResult? _layout;
    private readonly List<UiaProbe.ProbeResult>? _probeResults;
    private readonly Rectangle _windowBounds; // for screen→local coord conversion
    private readonly ProbeOcrMatcher.MatchResult? _probeOcrMatch;
    private readonly int _autoThreshold;
    private readonly int _width;
    private readonly int _height;

    private readonly CheckBox _chkScreenshot;
    private readonly CheckBox _chkEdges;
    private readonly CheckBox _chkBspCells;
    private readonly CheckBox _chkOcrBounds;
    private readonly CheckBox _chkOcrText;
    private readonly CheckBox _chkFitText;
    private readonly CheckBox _chkScene;
    private readonly CheckBox _chkLayoutText;
    private readonly CheckBox _chkProbe;
    private readonly CheckBox _chkLabels;
    private readonly ComboBox _cboTextColor;
    private readonly TrackBar _thresholdSlider;
    private readonly Label _thresholdLabel;
    private readonly Label _zoomLabel;
    private readonly Panel _scrollPanel;
    private readonly PictureBox _pictureBox;

    private Bitmap? _composite;
    private float _zoom = 1.0f;
    private Color _ocrTextColor = ThemeText;

    private static readonly (string Name, Color Color)[] TextColors =
    [
        ("White", Color.FromArgb(220, 220, 220)),
        ("Yellow", Color.FromArgb(255, 255, 80)),
        ("Cyan", Color.FromArgb(80, 255, 255)),
        ("Green", Color.FromArgb(80, 255, 80)),
        ("Orange", Color.FromArgb(255, 180, 50)),
        ("Pink", Color.FromArgb(255, 120, 200)),
        ("Red", Color.FromArgb(255, 80, 80)),
        ("Blue", Color.FromArgb(100, 160, 255)),
    ];

    private static readonly float[] ZoomSteps =
        [0.125f, 0.25f, 0.5f, 0.75f, 1.0f, 1.5f, 2.0f, 3.0f, 4.0f];

    public DebugBitmapForm(string title, WinContent content, Bitmap? screenshot = null,
        BspRegionDetector.BspResult? bspDebug = null, LayoutResult? layout = null,
        List<UiaProbe.ProbeResult>? probeResults = null, Rectangle windowBounds = default,
        ProbeOcrMatcher.MatchResult? probeOcrMatch = null)
    {
        // Determine canvas size from all available data
        _width = 100;
        _height = 100;

        if (screenshot != null)
        {
            _width = Math.Max(_width, screenshot.Width);
            _height = Math.Max(_height, screenshot.Height);
        }
        if (content.Words.Count > 0)
        {
            _width = Math.Max(_width, content.Words.Max(w => w.Bounds.Right) + 10);
            _height = Math.Max(_height, content.Words.Max(w => w.Bounds.Bottom) + 10);
        }

        // Render individual layers
        _screenshotLayer = RenderScreenshotLayer(screenshot);
        _edgeLayer = EdgeDetector.Detect(screenshot, _width, _height);
        _ocrBoundsLayer = RenderOcrBoundsLayer(content);
        _content = content;
        _bspDebug = bspDebug;
        _layout = layout;
        _probeResults = probeResults;
        _windowBounds = windowBounds;
        _probeOcrMatch = probeOcrMatch;
        _autoThreshold = bspDebug?.GapThreshold ?? 20;

        // --- Build UI ---
        Text = title;
        BackColor = ThemeBg;
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        Width = Math.Min(_width + 40, workArea.Width - 100);
        Height = Math.Min(_height + 120, workArea.Height - 100);
        StartPosition = FormStartPosition.CenterScreen;

        // Control panel (top)
        var controlPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = ThemePanel,
            Padding = new Padding(8, 6, 8, 6),
        };

        int x = 0;
        _chkScreenshot = MakeCheckBox("Screenshot", ref x, screenshot != null);
        _chkScreenshot.Enabled = screenshot != null;
        _chkEdges = MakeCheckBox("Edges", ref x, false);
        _chkEdges.Enabled = _edgeLayer != null;
        _chkBspCells = MakeCheckBox("BSP Cells", ref x, false);
        _chkBspCells.Enabled = bspDebug?.Cells.Count > 0;
        _chkOcrBounds = MakeCheckBox("OCR Bounds", ref x, true);
        _chkOcrText = MakeCheckBox("OCR Text", ref x, true);
        _chkFitText = MakeCheckBox("Fit Text", ref x, false);
        _chkScene = MakeCheckBox("Scene Regions", ref x, layout != null);
        _chkScene.Enabled = layout != null;
        bool hasLayoutTexts = layout?.Regions.Any(r => r.Texts.Count > 0) == true;
        _chkLayoutText = MakeCheckBox("Layout", ref x, hasLayoutTexts);
        _chkLayoutText.Enabled = hasLayoutTexts;
        bool hasProbe = probeOcrMatch?.Words.Count > 0 || probeResults?.Any(r => r.Lines.Count > 0) == true;
        _chkProbe = MakeCheckBox("Probe", ref x, hasProbe);
        _chkProbe.Enabled = hasProbe;
        _chkLabels = MakeCheckBox("Labels", ref x, true);

        _cboTextColor = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Left = x + 4,
            Top = 3,
            Width = 80,
            Height = 24,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = ThemeText,
            FlatStyle = FlatStyle.Flat,
        };
        foreach (var (name, _) in TextColors)
            _cboTextColor.Items.Add(name);
        _cboTextColor.SelectedIndex = RestoreColorIndex();
        _ocrTextColor = TextColors[_cboTextColor.SelectedIndex].Color;
        x += 88;

        _thresholdLabel = new Label
        {
            Text = $"Thr: {_autoThreshold}px",
            AutoSize = true,
            Left = x + 8,
            Top = 6,
            ForeColor = Color.FromArgb(220, 200, 80),
            Font = new Font("Segoe UI", 8.5f),
        };
        x += TextRenderer.MeasureText(_thresholdLabel.Text, _thresholdLabel.Font).Width + 12;

        _thresholdSlider = new TrackBar
        {
            Minimum = 1,
            Maximum = 500,
            Value = Math.Clamp(_autoThreshold, 1, 500),
            TickStyle = TickStyle.None,
            AutoSize = false,
            Left = x,
            Top = 2,
            Width = 160,
            Height = 28,
            SmallChange = 1,
            LargeChange = 10,
            BackColor = ThemePanel,
        };
        x += 168;

        _zoomLabel = new Label
        {
            Text = "100%",
            AutoSize = true,
            Left = x + 8,
            Top = 6,
            ForeColor = Color.FromArgb(160, 160, 160),
            Font = new Font("Segoe UI", 8.5f),
        };

        // Restore saved checkbox states before registering handlers
        RestoreCheckState(_chkScreenshot);
        RestoreCheckState(_chkEdges);
        RestoreCheckState(_chkBspCells);
        RestoreCheckState(_chkOcrBounds);
        RestoreCheckState(_chkOcrText);
        RestoreCheckState(_chkFitText);
        RestoreCheckState(_chkScene);
        RestoreCheckState(_chkLayoutText);
        RestoreCheckState(_chkProbe);
        RestoreCheckState(_chkLabels);

        controlPanel.Controls.AddRange([_chkScreenshot, _chkEdges, _chkBspCells, _chkOcrBounds, _chkOcrText, _chkFitText, _chkScene, _chkLayoutText, _chkProbe, _chkLabels, _cboTextColor, _thresholdLabel, _thresholdSlider, _zoomLabel]);

        // Picture box in scroll panel
        _pictureBox = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(0, 0),
        };

        _scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = ThemeBg,
        };
        _scrollPanel.Controls.Add(_pictureBox);
        _scrollPanel.MouseWheel += OnMouseWheel;
        _pictureBox.MouseWheel += OnMouseWheel;

        Controls.Add(_scrollPanel);
        Controls.Add(controlPanel);

        // Register handlers AFTER UI is built (UpdateComposite needs _pictureBox)
        _chkScreenshot.CheckedChanged += (_, _) => { SaveCheckState(_chkScreenshot); UpdateComposite(); };
        _chkEdges.CheckedChanged += (_, _) => { SaveCheckState(_chkEdges); UpdateComposite(); };
        _chkBspCells.CheckedChanged += (_, _) => { SaveCheckState(_chkBspCells); UpdateComposite(); };
        _chkOcrBounds.CheckedChanged += (_, _) => { SaveCheckState(_chkOcrBounds); UpdateComposite(); };
        _chkOcrText.CheckedChanged += (_, _) => { SaveCheckState(_chkOcrText); UpdateComposite(); };
        _chkFitText.CheckedChanged += (_, _) => { SaveCheckState(_chkFitText); UpdateComposite(); };
        _chkScene.CheckedChanged += (_, _) => { SaveCheckState(_chkScene); UpdateComposite(); };
        _chkLayoutText.CheckedChanged += (_, _) => { SaveCheckState(_chkLayoutText); UpdateComposite(); };
        _chkProbe.CheckedChanged += (_, _) => { SaveCheckState(_chkProbe); UpdateComposite(); };
        _chkLabels.CheckedChanged += (_, _) => { SaveCheckState(_chkLabels); UpdateComposite(); };

        _cboTextColor.SelectedIndexChanged += (_, _) =>
        {
            int idx = _cboTextColor.SelectedIndex;
            if (idx >= 0 && idx < TextColors.Length)
            {
                _ocrTextColor = TextColors[idx].Color;
                SaveColorIndex(idx);
                UpdateComposite();
            }
        };

        _thresholdSlider.ValueChanged += (_, _) =>
        {
            int thr = _thresholdSlider.Value;
            _thresholdLabel.Text = $"Thr: {thr}px";
            RecalculateBsp(thr);
            UpdateComposite();
        };

        UpdateComposite();

        FormClosed += (_, _) =>
        {
            _composite?.Dispose();
            _screenshotLayer?.Dispose();
            _edgeLayer?.Dispose();
            _ocrBoundsLayer?.Dispose();
        };
    }

    private static CheckBox MakeCheckBox(string text, ref int x, bool isChecked)
    {
        var chk = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Left = x,
            Top = 4,
            Checked = isChecked,
            ForeColor = ThemeText,
            FlatStyle = FlatStyle.Flat,
        };
        // Estimate width for next control positioning
        x += TextRenderer.MeasureText(text, chk.Font).Width + 28;
        return chk;
    }

    // ──────────────────────────────────────────────────────────────
    //  BSP recalculation (live slider)
    // ──────────────────────────────────────────────────────────────

    private void RecalculateBsp(int threshold)
    {
        if (_content.Words.Count == 0) return;

        _bspDebug = BspRegionDetector.DetectFull(_content, threshold);

        _chkBspCells.Enabled = _bspDebug.Cells.Count > 0;
    }

    // ──────────────────────────────────────────────────────────────
    //  Zoom
    // ──────────────────────────────────────────────────────────────

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0) return;

        // Find current step index
        int idx = 0;
        for (int i = 0; i < ZoomSteps.Length; i++)
        {
            if (Math.Abs(ZoomSteps[i] - _zoom) < 0.01f) { idx = i; break; }
            if (ZoomSteps[i] > _zoom) { idx = Math.Max(0, i - 1); break; }
            idx = i;
        }

        // Step up or down
        if (e.Delta > 0 && idx < ZoomSteps.Length - 1)
            idx++;
        else if (e.Delta < 0 && idx > 0)
            idx--;
        else
            return;

        _zoom = ZoomSteps[idx];
        _zoomLabel.Text = $"{(int)(_zoom * 100)}%";

        // Resize picture box to zoomed size
        _pictureBox.Size = new Size((int)(_width * _zoom), (int)(_height * _zoom));
        _pictureBox.Invalidate();

        // Suppress default scroll
        if (e is HandledMouseEventArgs hme)
            hme.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────
    //  Compositing
    // ──────────────────────────────────────────────────────────────

    private void UpdateComposite()
    {
        var old = _composite;
        _composite = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(_composite);
        g.Clear(ThemeBg);

        if (_chkScreenshot.Checked && _screenshotLayer != null)
            g.DrawImage(_screenshotLayer, 0, 0);

        if (_chkEdges.Checked && _edgeLayer != null)
            g.DrawImage(_edgeLayer, 0, 0);

        if (_chkBspCells.Checked && _bspDebug?.Cells.Count > 0)
            DrawBspCellsOnto(g, _bspDebug);

        if (_chkScene.Checked && _layout != null)
            DrawLayoutOnto(g, _layout, _chkLabels.Checked);

        if (_chkLayoutText.Checked && _layout != null)
            DrawLayoutTextOnto(g, _layout, _ocrTextColor);

        if (_chkProbe.Checked)
        {
            if (_probeOcrMatch?.Words.Count > 0)
                DrawProbeMatchedOnto(g, _probeOcrMatch);
            else if (_probeResults != null)
                DrawProbeOnto(g, _probeResults, _windowBounds, _ocrTextColor);
        }

        if (_chkOcrBounds.Checked && _ocrBoundsLayer != null)
            g.DrawImage(_ocrBoundsLayer, 0, 0);

        if (_chkOcrText.Checked && _content.Words.Count > 0)
            DrawOcrTextOnto(g, _content, _ocrTextColor, _chkFitText.Checked);

        _pictureBox.Image = _composite;
        _pictureBox.Size = new Size((int)(_width * _zoom), (int)(_height * _zoom));
        old?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────
    //  Layer Renderers
    // ──────────────────────────────────────────────────────────────

    private Bitmap? RenderScreenshotLayer(Bitmap? screenshot)
    {
        if (screenshot == null) return null;

        var layer = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(layer);
        g.DrawImage(screenshot, 0, 0);
        return layer;
    }

    /// <summary>Layer 1: OCR word bounds (yellow rectangles) + line bounds (subtle).</summary>
    private Bitmap? RenderOcrBoundsLayer(WinContent content)
    {
        if (content.Words.Count == 0) return null;

        var layer = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(layer);

        using var boundsPen = new Pen(Color.FromArgb(180, 255, 255, 80), 1);
        foreach (var word in content.Words)
        {
            var b = word.Bounds;
            if (IsValidBounds(b.Width, b.Height))
                g.DrawRectangle(boundsPen, b);
        }

        using var linePen = new Pen(Color.FromArgb(50, 80, 80, 80), 1);
        foreach (var line in content.Lines)
        {
            if (line.Words.Count > 0)
                g.DrawRectangle(linePen, line.Bounds);
        }

        return layer;
    }

    /// <summary>Draw OCR text directly onto composite graphics with the given color.</summary>
    private static void DrawOcrTextOnto(Graphics g, WinContent content, Color textColor, bool fitText)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var wordBrush = new SolidBrush(textColor);

        if (!fitText)
        {
            // Simple mode: use OcrHeight directly
            foreach (var word in content.Words.OfType<WinWordOcr>())
            {
                var b = word.Bounds;
                if (!IsValidBounds(b.Width, b.Height) || string.IsNullOrEmpty(word.Text)) continue;

                float fs = word.OcrHeight > 0 ? word.OcrHeight : b.Height;
                if (fs < 4) fs = 4;
                using var font = new Font("Segoe UI", fs, GraphicsUnit.Pixel);
                g.DrawString(word.Text, font, wordBrush,
                    b.X, b.Y - InternalLeadingPixels(font),
                    StringFormat.GenericTypographic);
            }
            return;
        }

        // Fit mode: per line — fit each word, then normalize similar sizes to median
        const float FitTolerance = 2f;
        const float OutlierThreshold = 0.30f; // 30% deviation = real size difference
        float lastFitSize = 0f;

        foreach (var line in content.Lines)
        {
            var ocrWords = line.Words.OfType<WinWordOcr>()
                .Where(w => w.Bounds.Width > 0 && w.Bounds.Height > 0 && !string.IsNullOrEmpty(w.Text))
                .ToList();
            if (ocrWords.Count == 0) continue;

            // Phase 1: Fit each word individually
            var fittedSizes = new float[ocrWords.Count];
            for (int i = 0; i < ocrWords.Count; i++)
            {
                var w = ocrWords[i];
                float startFs = lastFitSize > 0 ? lastFitSize : (w.OcrHeight > 0 ? w.OcrHeight : w.Bounds.Height);
                float targetWidth = w.Bounds.Width * 0.95f; // OCR bounds include small padding
                fittedSizes[i] = FitFontSize(g, w.Text, targetWidth, startFs, FitTolerance);
                lastFitSize = fittedSizes[i];
            }

            // Phase 2: Compute median, normalize non-outliers
            var sorted = fittedSizes.OrderBy(s => s).ToArray();
            float median = sorted[sorted.Length / 2];

            for (int i = 0; i < fittedSizes.Length; i++)
            {
                float deviation = Math.Abs(fittedSizes[i] - median) / median;
                if (deviation <= OutlierThreshold)
                    fittedSizes[i] = median; // normalize to line median
                // else: keep — genuinely different size
            }

            // Phase 3: Render
            for (int i = 0; i < ocrWords.Count; i++)
            {
                var w = ocrWords[i];
                float fs = Math.Max(fittedSizes[i], 4f);
                using var font = new Font("Segoe UI", fs, GraphicsUnit.Pixel);
                g.DrawString(w.Text, font, wordBrush,
                    w.Bounds.X, w.Bounds.Y - InternalLeadingPixels(font),
                    StringFormat.GenericTypographic);
            }
        }
    }

    /// <summary>
    /// Find the font size (in pixels) so that MeasureString width ≈ targetWidth.
    /// Starts from startSize, adjusts proportionally, max 3 iterations.
    /// </summary>
    private static float FitFontSize(Graphics g, string text, float targetWidth, float startSize, float tolerance)
    {
        float fs = Math.Max(startSize, 4f);

        for (int i = 0; i < 3; i++)
        {
            using var font = new Font("Segoe UI", fs, GraphicsUnit.Pixel);
            float measured = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

            if (measured <= 0) return fs;
            if (Math.Abs(measured - targetWidth) <= tolerance) return fs;

            // Scale proportionally
            fs = fs * (targetWidth / measured);
            if (fs < 4f) fs = 4f;
        }

        return fs;
    }

    /// <summary>Draw layout regions (from LayoutResult) onto composite graphics.</summary>
    private static void DrawLayoutOnto(Graphics g, LayoutResult layout, bool showLabels)
    {
        if (layout.Regions.Count == 0) return;

        for (int i = 0; i < layout.Regions.Count; i++)
            DrawLayoutRegion(g, layout.Regions[i], i, showLabels);
    }

    // Border colors per region index (cycle through)
    private static readonly Color[] RegionColors =
    [
        Color.FromArgb(220, 255, 80, 80),    // red
        Color.FromArgb(200, 80, 200, 255),    // blue
        Color.FromArgb(180, 100, 255, 100),   // green
        Color.FromArgb(160, 255, 200, 80),    // orange
        Color.FromArgb(140, 200, 100, 255),   // purple
        Color.FromArgb(120, 100, 255, 255),   // cyan
    ];

    private static void DrawLayoutRegion(Graphics g, LayoutRegionResult region, int index, bool showLabels)
    {
        var color = RegionColors[index % RegionColors.Length];
        using var borderPen = new Pen(color, 2);
        g.DrawRectangle(borderPen, region.Bounds);

        if (showLabels)
        {
            var label = region.Type;
            if (!string.IsNullOrEmpty(region.Name))
                label += $" ({region.Name})";
            if (region.Texts.Count > 0)
                label += $" [{region.Texts.Count}t]";
            using var labelFont = new Font("Segoe UI", 8f);
            var labelSize = g.MeasureString(label, labelFont);
            using var labelBgBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            using var labelTextBrush = new SolidBrush(color);
            g.FillRectangle(labelBgBrush,
                region.Bounds.X + 1, region.Bounds.Y + 1,
                labelSize.Width + 4, labelSize.Height + 1);
            g.DrawString(label, labelFont, labelTextBrush,
                region.Bounds.X + 3, region.Bounds.Y + 1);
        }
    }

    /// <summary>
    /// Draw UIA texts from LayoutResult regions onto composite graphics.
    /// Unlike OCR words, UIA texts can be multi-word labels, sentences, or paragraphs.
    /// UIA bounds often don't match actual text size, so we use a fixed base font
    /// and clip to the element bounds.
    /// </summary>
    private static void DrawLayoutTextOnto(Graphics g, LayoutResult layout, Color textColor)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var textBrush = new SolidBrush(textColor);
        const float BaseFontSize = 12f; // px — reasonable default for UI text

        foreach (var region in layout.Regions)
        {
            if (region.Texts.Count == 0) continue;

            foreach (var t in region.Texts)
            {
                if (string.IsNullOrEmpty(t.Text) || !IsValidBounds(t.Width, t.Height)) continue;

                // Absolute position (RegionText coords are region-relative)
                int absX = region.Bounds.X + t.X;
                int absY = region.Bounds.Y + t.Y;

                // Y-Shift: OCR bounds are systematically too low — shift up by 15%
                float yShift = YShift(t.Height);
                float drawY = absY - yShift;

                // Use base font, but if bounds height is small use that as ceiling
                float fs = ClampFontSize(Math.Min(BaseFontSize, t.Height * 0.8f));

                using var font = new Font("Segoe UI", fs, GraphicsUnit.Pixel);

                // Clip to element bounds — prevents overflow
                var clipRect = new RectangleF(absX, drawY, t.Width, t.Height + yShift);
                g.SetClip(clipRect);
                g.DrawString(t.Text, font, textBrush,
                    absX, drawY,
                    StringFormat.GenericTypographic);
                g.ResetClip();
            }
        }
    }

    /// <summary>
    /// Draw UIA TextPattern probe results onto composite graphics.
    /// Converts screen-absolute bounds to window-local coordinates.
    /// Uses a distinct color (cyan) to distinguish from OCR text.
    /// </summary>
    private static void DrawProbeOnto(Graphics g, List<UiaProbe.ProbeResult> probeResults,
        Rectangle windowBounds, Color textColor)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var textBrush = new SolidBrush(Color.FromArgb(0, 220, 220)); // cyan — distinct from OCR
        using var boundsPen = new Pen(Color.FromArgb(80, 0, 200, 200), 1);
        boundsPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;

        foreach (var result in probeResults)
        {
            foreach (var line in result.Lines)
            {
                if (line.Bounds.IsEmpty || !IsValidBounds(line.Bounds.Width, line.Bounds.Height))
                    continue;

                // Convert screen-absolute → window-local
                int localX = line.Bounds.X - windowBounds.X;
                int localY = line.Bounds.Y - windowBounds.Y;
                int w = line.Bounds.Width;
                int h = line.Bounds.Height;

                // Draw bounds rect (dotted cyan)
                g.DrawRectangle(boundsPen, localX, localY, w, h);

                if (string.IsNullOrEmpty(line.Text)) continue;

                // Font size from line height
                float fs = ClampFontSize(h * 0.8f);
                if (fs > 20f) fs = 20f;

                using var font = new Font("Consolas", fs, GraphicsUnit.Pixel);
                var clipRect = new RectangleF(localX, localY, w, h);
                g.SetClip(clipRect);
                g.DrawString(line.Text, font, textBrush, localX, localY, StringFormat.GenericTypographic);
                g.ResetClip();
            }
        }
    }

    /// <summary>
    /// Draw Probe-OCR matched words with color-coding by match kind.
    /// Cyan = Perfect, Yellow = Corrected, Orange = ProbeOnly.
    /// Bounds are already window-local.
    /// </summary>
    private static void DrawProbeMatchedOnto(Graphics g, ProbeOcrMatcher.MatchResult match)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var perfectBrush = new SolidBrush(Color.FromArgb(0, 220, 220));       // cyan
        using var correctedBrush = new SolidBrush(Color.FromArgb(255, 220, 50));     // yellow
        using var probeOnlyBrush = new SolidBrush(Color.FromArgb(255, 140, 40));     // orange

        using var probeOnlyPen = new Pen(Color.FromArgb(120, 255, 140, 40), 1);
        probeOnlyPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;

        foreach (var word in match.Words)
        {
            if (word.Bounds.IsEmpty || !IsValidBounds(word.Bounds.Width, word.Bounds.Height))
                continue;

            var brush = word.Kind switch
            {
                ProbeOcrMatcher.MatchKind.Perfect => perfectBrush,
                ProbeOcrMatcher.MatchKind.Corrected => correctedBrush,
                ProbeOcrMatcher.MatchKind.ProbeOnly => probeOnlyBrush,
                _ => perfectBrush,
            };

            // Draw dotted bounds for ProbeOnly words
            if (word.Kind == ProbeOcrMatcher.MatchKind.ProbeOnly)
                g.DrawRectangle(probeOnlyPen, word.Bounds.X, word.Bounds.Y, word.Bounds.Width, word.Bounds.Height);

            float fs = ClampFontSize(word.Bounds.Height * 0.8f);
            if (fs > 20f) fs = 20f;

            using var font = new Font("Segoe UI", fs, GraphicsUnit.Pixel);
            // Probe bounds are consistently a few pixels lower than OCR — shift up by 15% of height
            float yShift = YShift(word.Bounds.Height);
            float drawY = word.Bounds.Y - yShift;
            var clipRect = new RectangleF(word.Bounds.X, drawY, word.Bounds.Width, word.Bounds.Height + yShift);
            g.SetClip(clipRect);
            g.DrawString(word.Text, font, brush, word.Bounds.X, drawY, StringFormat.GenericTypographic);
            g.ResetClip();
        }
    }

    /// <summary>Draw raw BSP leaf cells as semi-transparent grid.</summary>
    private static void DrawBspCellsOnto(Graphics g, BspRegionDetector.BspResult bsp)
    {
        using var cellPen = new Pen(Color.FromArgb(120, 255, 180, 0), 1); // amber, dashed
        cellPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
        using var fillBrush = new SolidBrush(Color.FromArgb(20, 255, 200, 0));

        foreach (var cell in bsp.Cells)
        {
            if (IsValidBounds(cell.Width, cell.Height))
            {
                g.FillRectangle(fillBrush, cell);
                g.DrawRectangle(cellPen, cell);
            }
        }

        // Draw threshold info in top-right corner
        if (bsp.Cells.Count > 0)
        {
            var info = $"BSP: {bsp.Cells.Count} cells, thr={bsp.GapThreshold}px ({bsp.StructuralGaps}/{bsp.TotalGaps} structural)";
            using var font = new Font("Segoe UI", 8f);
            var sz = g.MeasureString(info, font);
            float x = g.ClipBounds.Width > 0 ? Math.Max(0, g.VisibleClipBounds.Width - sz.Width - 8) : 4;
            using var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            using var textBrush = new SolidBrush(Color.FromArgb(220, 255, 200, 0));
            g.FillRectangle(bgBrush, x, 2, sz.Width + 6, sz.Height + 2);
            g.DrawString(info, font, textBrush, x + 3, 3);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────

    private static void SaveCheckState(CheckBox chk)
    {
        _savedSettings[chk.Text] = chk.Checked.ToString();
        SaveSettings();
    }

    private static void RestoreCheckState(CheckBox chk)
    {
        if (_savedSettings.TryGetValue(chk.Text, out var saved) && bool.TryParse(saved, out bool val))
            chk.Checked = val && chk.Enabled;
    }

    private static void SaveColorIndex(int index)
    {
        _savedSettings["__OcrTextColor"] = index.ToString();
        SaveSettings();
    }

    private static int RestoreColorIndex()
    {
        if (_savedSettings.TryGetValue("__OcrTextColor", out var val) && int.TryParse(val, out int idx))
            return Math.Clamp(idx, 0, TextColors.Length - 1);
        return 0;
    }

    private static void SaveSettings()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_savedSettings);
            File.WriteAllText(_settingsPath, json);
        }
        catch { /* ignore write errors */ }
    }

    private static Dictionary<string, string> LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                // Support both old (bool) and new (string) format
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                        ?? new();
                }
                catch
                {
                    // Migrate from old bool format
                    var old = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                    if (old != null)
                    {
                        var migrated = new Dictionary<string, string>();
                        foreach (var kvp in old)
                            migrated[kvp.Key] = kvp.Value.ToString();
                        return migrated;
                    }
                }
            }
        }
        catch { /* ignore read errors */ }
        return new();
    }

    /// <summary>
    /// GDI+ Internal Leading: space reserved above glyphs for diacritics.
    /// Subtract from Y to align rendered text with actual glyph top.
    /// </summary>
    private static float InternalLeadingPixels(Font f)
    {
        var ff = f.FontFamily;
        int em = ff.GetEmHeight(f.Style);
        int leading = ff.GetCellAscent(f.Style) + ff.GetCellDescent(f.Style) - em;
        return f.Size * leading / em;
    }

    // ──────────────────────────────────────────────────────────────
    //  Drawing Helpers (extracted from repeated inline patterns)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Y-shift: move text up by 15% of height to correct systematic low-placement.</summary>
    private static float YShift(float height) => height * 0.15f;

    /// <summary>Clamp font size to a minimum of 6px to stay readable.</summary>
    private static float ClampFontSize(float size) => Math.Max(size, 6f);

    /// <summary>Returns true when width and height are both positive (valid render target).</summary>
    private static bool IsValidBounds(int w, int h) => w > 0 && h > 0;
}
