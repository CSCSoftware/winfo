namespace WinfoMain.Test;

using System.Text.Json;
using WinfoMain.Common;
using WinfoMain.Desktop;
using WinfoMain.Desktop.OcrEngine;
using WinfoMain.Desktop.SceneEngine;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Main window — pure GUI shell, no model data or services.
/// </summary>
public class MainForm : Form
{
    // Dark Mode Colors
    private static readonly Color ThemeBg = Color.FromArgb(30, 30, 30);
    private static readonly Color ThemePanel = Color.FromArgb(45, 45, 45);
    private static readonly Color ThemeButton = Color.FromArgb(60, 60, 60);
    private static readonly Color ThemeText = Color.FromArgb(220, 220, 220);
    private static readonly Color ThemeBgPanel = Color.FromArgb(40, 40, 40);

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinfoMain");

    private static readonly string SettingsPath = Path.Combine(AppDataDir, "MainForm.json");
    private static readonly string LogPath = Path.Combine(AppDataDir, "log.txt");

    private readonly RichTextBox _log;
    private readonly CheckedListBox _windowList;
    private readonly CheckBox _chkDrawBounds;
    private readonly RichTextBox _cursorInfo;
    private readonly Button _btnRefresh;
    private readonly Button _btnOcr;
    private readonly Button _btnRegion;
    private readonly ComboBox _scaleCombo;
    private readonly ComboBox _colorCombo;
    private readonly NumericUpDown _upscaleThreshold;
    private readonly Button _btnLive;
    private readonly ComboBox _windowCombo;
    private readonly StreamWriter _logFile;
    private readonly System.Windows.Forms.Timer _cursorTimer;
    private readonly InspectorTab _inspectorTab;
    private readonly Label _perfLabel;
    private DesktopModel? _model;
    private BoundsOverlayForm? _boundsOverlay;
    private Point _lastCursorPos;
    private int _settledTicks;

    public MainForm()
    {
        Text = "WinfoMain Test";
        Width = 1100;
        Height = 750;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemeBg;
        ForeColor = ThemeText;

        // Toolbar panel (two rows: buttons + cursor info)
        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 70,
            BackColor = ThemeBg,
            Padding = new Padding(4),
        };

        var btnClear = new Button
        {
            Text = "Clear",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
        };
        btnClear.Click += (_, _) => _log.Clear();

        _btnRefresh = new Button
        {
            Text = "Refresh",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            Left = 70,
        };
        _btnRefresh.Click += (_, _) => RefreshWindowList();

        _windowCombo = new ComboBox
        {
            Width = 400,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = ThemeText,
            FlatStyle = FlatStyle.Flat,
            Left = 160,
        };

        _btnOcr = new Button
        {
            Text = "OCR",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            Left = 570,
        };
        _btnOcr.Click += (_, _) => RunOcr();

        _btnRegion = new Button
        {
            Text = "Region",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            Left = 640,
        };
        _btnRegion.Click += (_, _) => RunRegionOcr();

        _scaleCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 45,
            Left = 720,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            FlatStyle = FlatStyle.Flat,
        };
        _scaleCombo.Items.AddRange(new object[] { "1x", "2x", "3x" });
        _scaleCombo.SelectedIndex = 0;

        _colorCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 50,
            Left = 770,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            FlatStyle = FlatStyle.Flat,
        };
        _colorCombo.Items.AddRange(new object[] { "Full", "2", "4", "16", "256" });
        _colorCombo.SelectedIndex = 0;

        _upscaleThreshold = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 50,
            Value = DesktopPipeline.UpscaleThreshold,
            Width = 50,
            Left = 830,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
        };
        _upscaleThreshold.ValueChanged += (_, _) =>
        {
            DesktopPipeline.UpscaleThreshold = (int)_upscaleThreshold.Value;
            bool manual = _upscaleThreshold.Value == 0;
            _scaleCombo.Enabled = manual;
            _colorCombo.Enabled = manual;
        };
        _scaleCombo.Enabled = _upscaleThreshold.Value == 0;
        _colorCombo.Enabled = _upscaleThreshold.Value == 0;

        var btnDebug = new Button
        {
            Text = "Debug",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            Left = 895,
        };
        btnDebug.Click += (_, _) => ShowDebugBitmap();

        var btnProbe = new Button
        {
            Text = "Probe",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            Left = 975,
        };
        btnProbe.Click += (_, _) => RunProbe();

        var btnAll = new Button
        {
            Text = "All",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            Left = 1055,
        };
        btnAll.Click += (_, _) => RunAll();

        _btnLive = new Button
        {
            Text = "Live",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeButton,
            ForeColor = ThemeText,
            Left = 1125,
        };
        _btnLive.Click += (_, _) => ToggleLive();

        _chkDrawBounds = new CheckBox
        {
            Text = "Draw Bounds",
            AutoSize = true,
            ForeColor = ThemeText,
            FlatStyle = FlatStyle.Flat,
            Left = 4,
            Top = 40,
            Enabled = false,
        };
        _chkDrawBounds.CheckedChanged += (_, _) => OnDrawBoundsChanged();

        _cursorInfo = new RichTextBox
        {
            Left = 130,
            Top = 40,
            Width = 900,
            Height = 24,
            ReadOnly = true,
            BackColor = ThemeBgPanel,
            ForeColor = ThemeText,
            Font = new Font("Cascadia Mono", 9f),
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.None,
            Multiline = false,
        };

        _cursorTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _cursorTimer.Tick += OnCursorTimerTick;

        toolbar.Controls.AddRange([btnClear, _btnRefresh, _windowCombo, _btnOcr, _btnRegion, _scaleCombo, _colorCombo, _upscaleThreshold, btnDebug, btnProbe, btnAll, _btnLive, _chkDrawBounds, _cursorInfo]);

        _windowList = new CheckedListBox
        {
            Dock = DockStyle.Left,
            Width = 220,
            Visible = false,
            BackColor = ThemeBgPanel,
            ForeColor = ThemeText,
            Font = new Font("Cascadia Mono", 9f),
            BorderStyle = BorderStyle.None,
            CheckOnClick = true,
        };
        _windowList.ItemCheck += OnWindowListItemCheck;

        _log = new RichTextBox
        {
            Name = "rtbLog",
            AccessibleName = "LogOutput",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = ThemeBgPanel,
            ForeColor = ThemeText,
            Font = new Font("Cascadia Mono", 10f),
            BorderStyle = BorderStyle.None,
        };

        // Tab: Log
        var tabLog = new TabPage("Log")
        {
            BackColor = ThemeBg,
            ForeColor = ThemeText,
        };
        var logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(_windowList);
        tabLog.Controls.Add(logPanel);

        // Tab: Inspector
        _inspectorTab = new InspectorTab();
        var tabInspector = new TabPage("Inspector")
        {
            BackColor = ThemeBg,
            ForeColor = ThemeText,
        };
        tabInspector.Controls.Add(_inspectorTab);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            BackColor = ThemeBg,
            ForeColor = ThemeText,
        };
        tabs.TabPages.Add(tabLog);
        tabs.TabPages.Add(tabInspector);
        tabs.SelectedIndex = 1; // Inspector as default
        tabs.SelectedIndexChanged += OnTabChanged;

        _perfLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            BackColor = ThemeBg,
            ForeColor = Color.FromArgb(120, 120, 120),
            Font = new Font("Cascadia Mono", 8f),
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 8, 0),
            Text = "CPU: –  RAM: –",
        };

        Controls.Add(tabs);
        Controls.Add(_perfLabel);
        Controls.Add(toolbar);

        // Log file — new file each start (overwrites previous)
        if (!Directory.Exists(AppDataDir)) Directory.CreateDirectory(AppDataDir);
        _logFile = new StreamWriter(LogPath, append: false) { AutoFlush = true };


        FormClosing += (_, _) =>
        {
            PerformanceMonitor.Instance.Stop();
            _cursorTimer.Stop();
            _boundsOverlay?.Close();
            _model?.Dispose();
            _model = null;
            SaveSettings();
            _logFile.Dispose();
        };

        Log("WinfoMain Test started.");
        RefreshWindowList();
        RestoreSelectedWindow();
        RestoreInspectorSettings();
        _inspectorTab.StartPolling();

        PerformanceMonitor.Instance.Sampled += (cpu, mem) =>
        {
            if (InvokeRequired)
                BeginInvoke(() => _perfLabel.Text = $"CPU: {cpu:F1}%  RAM: {mem:F0} MB");
            else
                _perfLabel.Text = $"CPU: {cpu:F1}%  RAM: {mem:F0} MB";
        };
        PerformanceMonitor.Instance.Start();
    }

    private void RefreshWindowList()
    {
        var list = DesktopWindowList.Instance;
        list.Refresh();

        _windowCombo.Items.Clear();
        foreach (var w in list.All.OrderBy(w => w.ZOrder))
        {
            if (w.IsMinimized) continue;
            var label = $"[{w.ProcessName}] {w.Title}";
            if (label.Length > 80) label = label[..80] + "...";
            _windowCombo.Items.Add(new WindowItem(w, label));
        }

        if (_windowCombo.Items.Count > 0)
            _windowCombo.SelectedIndex = 0;

        Log($"Windows: {_windowCombo.Items.Count} found.");
    }

    private async void RunOcr()
    {
        if (_windowCombo.SelectedItem is not WindowItem item) return;

        var window = item.Window;
        Log($"OCR running for [{window.ProcessName}] \"{window.Title}\"...");

        try
        {
            var result = await new DesktopPipeline().ProcessAsync(window);
            if (!result.IsSuccess) { Log($"Error: {result.ErrorMessage}"); return; }

            Log($"OCR done: {result.WordCount} words, {result.LineCount} lines.");
            Log($"Layout: {result.DataSource}, {result.RegionCount} regions.");

            foreach (var line in window.ContentOcr.Lines)
                Log($"  {line.Text}");
        }
        catch (Exception ex)
        {
            Log($"OCR error: {ex.Message}");
        }
    }

    private async void RunRegionOcr()
    {
        // Hide ourselves briefly so we don't capture our own window
        var prevOpacity = Opacity;
        Opacity = 0;
        Application.DoEvents();

        Rectangle region;
        using (var selector = new RegionSelectForm())
        {
            var result = selector.ShowDialog();
            Opacity = prevOpacity;

            if (result != DialogResult.OK || selector.SelectedRegion.IsEmpty)
            {
                Log("Region OCR cancelled.");
                return;
            }
            region = selector.SelectedRegion;
        }

        int threshold = (int)_upscaleThreshold.Value;
        int scaleFactor;
        int colorCount;
        string colorLabel;

        if (threshold > 0)
        {
            // Threshold mode — ComboBoxes are disabled, values come from auto-detection
            scaleFactor = 1;
            colorCount = 0;
            colorLabel = "Full";
        }
        else
        {
            // Manual mode — use ComboBox values
            scaleFactor = _scaleCombo.SelectedIndex + 1;
            colorCount = _colorCombo.SelectedIndex == 0 ? 0 : int.Parse((string)_colorCombo.SelectedItem!);
            colorLabel = colorCount == 0 ? "Full" : $"{colorCount}";
        }

        Log($"Region: {region.X},{region.Y} {region.Width}x{region.Height} (Scale: {scaleFactor}x, Colors: {colorLabel}, Threshold: {threshold}px)");

        try
        {
            // Capture screen region
            using var bitmap = new Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(region.Location, Point.Empty, region.Size);
            }

            // Run OCR (normal first)
            var content = new WinContent();
            await OcrEngine.Instance.ProcessBitmapAsync(bitmap, content, scaleFactor, colorCount);

            // Threshold mode: check avg height, retry with 3x + 16 colors if too small or no words
            if (threshold > 0)
            {
                bool retry = content.Words.Count == 0;
                if (!retry && content.Words.Count > 0)
                {
                    double avgH = content.Words.Average(w => w.Bounds.Height);
                    retry = avgH < threshold;
                    if (retry) Log($"AvgH {avgH:F1}px < {threshold}px — retrying with 3x + 16 colors...");
                }
                if (retry)
                {
                    if (content.Words.Count == 0) Log("0 words — retrying with 3x + 16 colors...");
                    content = new WinContent();
                    await OcrEngine.Instance.ProcessBitmapAsync(bitmap, content, scaleFactor: 3, colorCount: 16);
                    scaleFactor = 3;
                    colorCount = 16;
                    colorLabel = "16";
                }
            }

            Log($"Region OCR: {content.Words.Count} words, {content.Lines.Count} lines (Scale: {scaleFactor}x, Colors: {colorLabel}).");

            // Log each word with raw bounds
            foreach (var word in content.Words)
            {
                var b = word.Bounds;
                Log($"  [{b.X},{b.Y} {b.Width}x{b.Height}] \"{word.Text}\"");
            }

            // Log lines
            Log("--- Lines ---");
            foreach (var line in content.Lines)
            {
                Log($"  {line.Text}");
            }

            double avgHeight = content.Words.Count > 0 ? content.Words.Average(w => w.Bounds.Height) : 0;
            ShowDebugBitmapWindow($"Region OCR ({region.Width}x{region.Height}) — {content.Words.Count} words, {content.Lines.Count} lines, AvgH: {avgHeight:F1}px (Scale: {scaleFactor}x, Colors: {colorLabel})", content);
        }
        catch (Exception ex)
        {
            Log($"Region OCR error: {ex.Message}");
        }
    }

    private void ShowDebugBitmap()
    {
        if (_windowCombo.SelectedItem is not WindowItem item) return;

        var window = item.Window;
        if (window.ContentOcr.Words.Count == 0)
        {
            Log("No content. Run OCR first.");
            return;
        }

        ShowDebugBitmapWindow(
            $"Debug — [{window.ProcessName}]",
            window.ContentOcr,
            screenshot: window.LastCapture,
            bspDebug: window.BspDebug,
            layout: window.Layout,
            probeResults: window.ProbeResults,
            windowBounds: window.Bounds,
            probeOcrMatch: window.ProbeOcrMatch);
    }

    /// <summary>
    /// Opens a layered debug window showing OCR content and optional layout/BSP data.
    /// </summary>
    private void ShowDebugBitmapWindow(string title, WinContent content, Bitmap? screenshot = null,
        BspRegionDetector.BspResult? bspDebug = null, LayoutResult? layout = null,
        List<UiaProbe.ProbeResult>? probeResults = null, Rectangle windowBounds = default,
        ProbeOcrMatcher.MatchResult? probeOcrMatch = null)
    {
        if (content.Words.Count == 0)
        {
            Log("No debug bitmap created.");
            return;
        }

        var form = new DebugBitmapForm(title, content, screenshot, bspDebug, layout, probeResults, windowBounds, probeOcrMatch);
        form.Show();
        Log($"Debug window opened: {title}");
    }

    private async void RunProbe()
    {
        if (_windowCombo.SelectedItem is not WindowItem item) return;

        var window = item.Window;
        Log($"--- UIA Probe [{window.ProcessName}] ---");

        try
        {
            var results = await UiaProbe.ProbeAsync(window.Hwnd);
            window.ProbeResults = results;
            Log($"Found {results.Count} TextPattern element(s):");

            foreach (var r in results)
            {
                Log($"  [{r.ControlType}] \"{r.ElementName}\" Class={r.ClassName} Bounds={r.Bounds.X},{r.Bounds.Y} {r.Bounds.Width}x{r.Bounds.Height}");
                Log($"  {r.Lines.Count} visible line(s):");

                foreach (var line in r.Lines)
                {
                    Log($"    {line}");
                }
            }

            if (results.Count == 0)
                Log("No TextPattern elements found.");

            // Match probe words with OCR for pixel-accurate bounds
            if (results.Count > 0 && window.ContentOcr.Words.Count > 0)
            {
                window.ProbeOcrMatch = ProbeOcrMatcher.Match(results, window.ContentOcr, window.Bounds);
                var m = window.ProbeOcrMatch;
                Log($"Probe-OCR: {m.PerfectMatches} perfect, {m.CorrectedMatches} corrected, {m.ProbeOnlyWords} probe-only");
                foreach (var word in m.Words.Where(w => w.Kind == ProbeOcrMatcher.MatchKind.ProbeOnly))
                    Log($"  ProbeOnly: \"{word.Text}\"");
            }
        }
        catch (Exception ex)
        {
            Log($"Probe error: {ex.Message}");
        }
    }

    private async void RunAll()
    {
        if (_windowCombo.SelectedItem is not WindowItem item) return;

        var window = item.Window;
        Log($"=== ALL [{window.ProcessName}] ===");

        try
        {
            var result = await new DesktopPipeline().ProcessAsync(window);
            if (!result.IsSuccess) { Log($"Error: {result.ErrorMessage}"); return; }

            Log($"OCR: {result.WordCount} words, {result.LineCount} lines.");
            Log($"Layout: {result.DataSource}, {result.RegionCount} regions.");

            if (result.Probe != null)
            {
                Log($"Probe: {result.Probe.ElementCount} TextPattern element(s).");
                Log($"Match: {result.Probe.PerfectMatches} perfect, {result.Probe.CorrectedMatches} corrected, {result.Probe.ProbeOnlyWords} probe-only.");
            }

            ShowDebugBitmapWindow(
                $"Debug — [{window.ProcessName}]",
                window.ContentOcr,
                screenshot: window.LastCapture,
                bspDebug: window.BspDebug,
                layout: window.Layout,
                probeResults: window.ProbeResults,
                windowBounds: window.Bounds,
                probeOcrMatch: window.ProbeOcrMatch);

            Log($"=== ALL done ===");
        }
        catch (Exception ex)
        {
            Log($"ALL error: {ex.Message}");
        }
    }

    private void ToggleLive()
    {
        if (_model != null)
        {
            // Stop — unsubscribe all event handlers before disposing
            _model.WindowChanged -= OnLiveWindowChanged;
            _model.WindowAdded -= OnLiveWindowAdded;
            _model.WindowRemoved -= OnLiveWindowRemoved;
            _model.ActiveWindowChanged -= OnLiveActiveWindowChanged;
            _model.Log -= OnLiveLog;
            _cursorTimer.Stop();
            _boundsOverlay?.Close();
            _boundsOverlay = null;
            _chkDrawBounds.Checked = false;
            _chkDrawBounds.Enabled = false;
            _cursorInfo.Text = "";
            _inspectorTab.DetachModel();
            _model.Dispose();
            _model = null;
            _windowList.Visible = false;
            _btnLive.BackColor = ThemeButton;
            _btnLive.Text = "Live";
            Log("Live OFF");
            return;
        }

        // Start
        _model = new DesktopModel();
        LoadIgnoredProcessNames();

        _model.WindowChanged += OnLiveWindowChanged;
        _model.WindowAdded += OnLiveWindowAdded;
        _model.WindowRemoved += OnLiveWindowRemoved;
        _model.ActiveWindowChanged += OnLiveActiveWindowChanged;
        _model.Log += OnLiveLog;
        _model.Start();

        _inspectorTab.AttachModel(_model);
        PopulateWindowList();
        _windowList.Visible = true;
        _chkDrawBounds.Enabled = true;
        LoadDrawBoundsSetting();
        _btnLive.BackColor = Color.FromArgb(0, 120, 0);
        _btnLive.Text = "Live ON";
        Log("Live ON");
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        if (sender is not TabControl tabs) return;
        var isInspector = tabs.SelectedTab?.Text == "Inspector";
        if (isInspector)
            _inspectorTab.StartPolling();
        else
            _inspectorTab.StopPolling();
    }

    private void PopulateWindowList()
    {
        _windowList.Items.Clear();
        foreach (var w in DesktopWindowList.Instance.All.OrderBy(w => w.ZOrder))
        {
            if (w.IsMinimized) continue;
            if (w.ProcessId == (uint)Environment.ProcessId) continue;
            var label = FormatWindowLabel(w);
            bool isIgnored = _model?.IgnoredProcessNames.Contains(w.ProcessName) ?? false;
            _windowList.Items.Add(label, isChecked: !isIgnored);
        }
    }

    private void OnWindowListItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_model == null) return;
        var label = _windowList.Items[e.Index]?.ToString() ?? "";
        var processName = ExtractProcessName(label);
        if (string.IsNullOrEmpty(processName)) return;

        if (e.NewValue == CheckState.Unchecked)
            _model.IgnoredProcessNames.Add(processName);
        else
            _model.IgnoredProcessNames.Remove(processName);
    }

    private void OnLiveWindowChanged(DesktopWindow w)
    {
        if (InvokeRequired) { BeginInvoke(() => OnLiveWindowChanged(w)); return; }
        Log($"Updated [{w.ProcessName}] OCR:{w.ContentOcr.Words.Count}");
    }

    private void OnLiveActiveWindowChanged(DesktopWindow w)
    {
        if (InvokeRequired) { BeginInvoke(() => OnLiveActiveWindowChanged(w)); return; }
        Log($"Active [{w.ProcessName}] \"{w.Title}\"");
    }

    private void OnLiveLog(string msg)
    {
        Log($"[Live] {msg}");
    }

    private void OnLiveWindowAdded(DesktopWindow w)
    {
        if (InvokeRequired) { BeginInvoke(() => OnLiveWindowAdded(w)); return; }
        Log($"Added [{w.ProcessName}] \"{w.Title}\"");

        if (w.ProcessId == (uint)Environment.ProcessId) return;
        var label = FormatWindowLabel(w);
        // Don't add duplicates
        if (_windowList.Items.Cast<string>().Any(s => s == label)) return;
        bool isIgnored = _model?.IgnoredProcessNames.Contains(w.ProcessName) ?? false;
        _windowList.Items.Add(label, isChecked: !isIgnored);
    }

    private void OnLiveWindowRemoved(nint hwnd)
    {
        if (InvokeRequired) { BeginInvoke(() => OnLiveWindowRemoved(hwnd)); return; }
        Log($"Removed 0x{hwnd:X}");
        // We can't match by hwnd since the window is gone — leave list as-is.
        // It will be cleaned up on next PopulateWindowList (ForceRefresh or restart).
    }

    private static string FormatWindowLabel(DesktopWindow w)
    {
        var label = $"[{w.ProcessName}] {w.Title}";
        return label.Length > 45 ? label[..45] + "..." : label;
    }

    private static string ExtractProcessName(string label)
    {
        // Format: "[processName] title..."
        if (!label.StartsWith('[')) return "";
        int end = label.IndexOf(']');
        return end > 1 ? label[1..end] : "";
    }

    public void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendText(line + "\n");
        _log.ScrollToCaret();
        _logFile.WriteLine(line);
    }

    // ──────────────────────────────────────────────────────────────
    //  Settings Persistence
    // ──────────────────────────────────────────────────────────────

    private void RestoreSelectedWindow()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<MainFormSettings>(json);
            if (s?.SelectedWindow == null) return;

            for (int i = 0; i < _windowCombo.Items.Count; i++)
            {
                if (_windowCombo.Items[i] is WindowItem item && item.Label == s.SelectedWindow)
                {
                    _windowCombo.SelectedIndex = i;
                    return;
                }
            }
        }
        catch { /* ignore corrupt settings */ }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var s = new MainFormSettings
            {
                SelectedWindow = (_windowCombo.SelectedItem as WindowItem)?.Label,
                IgnoredProcessNames = _model?.IgnoredProcessNames.Count > 0
                    ? _model.IgnoredProcessNames.ToList()
                    : CollectUncheckedProcessNames(),
                DrawBounds = _chkDrawBounds.Checked,
                InspectorSplitter = _inspectorTab.LeftPanelWidth,
            };

            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* ignore save errors */ }
    }

    private class MainFormSettings
    {
        public string? SelectedWindow { get; set; }
        public List<string>? IgnoredProcessNames { get; set; }
        public bool DrawBounds { get; set; }
        public int InspectorSplitter { get; set; } = 350;
    }

    private void RestoreInspectorSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<MainFormSettings>(json);
            if (s == null) return;
            _inspectorTab.LeftPanelWidth = s.InspectorSplitter;
        }
        catch { /* ignore corrupt settings */ }
    }

    private void LoadDrawBoundsSetting()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<MainFormSettings>(json);
            if (s == null) return;
            _chkDrawBounds.Checked = s.DrawBounds;
        }
        catch { /* ignore corrupt settings */ }
    }

    private void LoadIgnoredProcessNames()
    {
        if (_model == null) return;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<MainFormSettings>(json);
            if (s?.IgnoredProcessNames == null) return;

            foreach (var name in s.IgnoredProcessNames)
                _model.IgnoredProcessNames.Add(name);
        }
        catch { /* ignore corrupt settings */ }
    }

    private List<string>? CollectUncheckedProcessNames()
    {
        var names = new List<string>();
        for (int i = 0; i < _windowList.Items.Count; i++)
        {
            if (!_windowList.GetItemChecked(i))
            {
                var proc = ExtractProcessName(_windowList.Items[i]?.ToString() ?? "");
                if (!string.IsNullOrEmpty(proc) && !names.Contains(proc))
                    names.Add(proc);
            }
        }
        return names.Count > 0 ? names : null;
    }

    // ──────────────────────────────────────────────────────────────
    //  Draw Bounds + Cursor Hit-Test
    // ──────────────────────────────────────────────────────────────

    private void OnDrawBoundsChanged()
    {
        if (_chkDrawBounds.Checked)
        {
            _lastCursorPos = Cursor.Position;
            _settledTicks = 0;
            _cursorTimer.Start();
        }
        else
        {
            _cursorTimer.Stop();
            _boundsOverlay?.Close();
            _boundsOverlay = null;
            _cursorInfo.Text = "";
        }
    }

    private void OnCursorTimerTick(object? sender, EventArgs e)
    {
        var pos = Cursor.Position;

        if (pos != _lastCursorPos)
        {
            _lastCursorPos = pos;
            _settledTicks = 0;
            return;
        }

        _settledTicks++;
        if (_settledTicks == 1) // 100ms settled
            HitTestAtCursor(pos);
    }

    private void HitTestAtCursor(Point screenPos)
    {
        if (_model == null) return;

        var queryResult = DesktopQueryService.QueryAt(screenPos, (uint)Environment.ProcessId);

        if (queryResult == null)
        {
            _boundsOverlay?.Hide();
            return;
        }

        // Trigger pipeline if window has no content yet — data arrives on next tick
        if (queryResult.Window.ContentOcr.Words.Count == 0)
        {
            _model.ForceUpdate(queryResult.Window);
            return;
        }

        if (!queryResult.HasWord)
        {
            _boundsOverlay?.Hide();
            _cursorInfo.Text = "";
            return;
        }

        if (_boundsOverlay == null || _boundsOverlay.IsDisposed)
        {
            _boundsOverlay = new BoundsOverlayForm();
            _boundsOverlay.Show();
        }
        _boundsOverlay.ShowBounds(queryResult.WordScreenBounds);

        ShowCursorInfo(queryResult);
    }

    private static readonly Color OcrColor = Color.FromArgb(100, 180, 255);
    private static readonly Color LabelColor = Color.FromArgb(140, 140, 140);

    private void ShowCursorInfo(DesktopQueryResult result)
    {
        _cursorInfo.Text = "";
        AppendColored("Word: ", LabelColor, false);
        AppendColored(result.Word ?? "", OcrColor, true);

        if (result.Line != null && result.Line != result.Word)
        {
            AppendColored("\nLine: ", LabelColor, false);
            AppendColored(result.Line, OcrColor, false);
        }

        if (result.RegionType != null)
        {
            AppendColored("\nRegion: ", LabelColor, false);
            AppendColored(result.RegionType, OcrColor, false);
        }
    }

    private void AppendColored(string text, Color color, bool bold)
    {
        _cursorInfo.SelectionStart = _cursorInfo.TextLength;
        _cursorInfo.SelectionLength = 0;
        _cursorInfo.SelectionColor = color;
        _cursorInfo.SelectionFont = bold
            ? new Font(_cursorInfo.Font, FontStyle.Bold)
            : _cursorInfo.Font;
        _cursorInfo.AppendText(text);
    }

    // Helper for ComboBox items
    private record WindowItem(DesktopWindow Window, string Label)
    {
        public override string ToString() => Label;
    }
}
