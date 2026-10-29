namespace WinfoMain.Test;

using System.Drawing;
using System.Windows.Forms;
using WinfoMain.Common;
using WinfoMain.Desktop;

/// <summary>
/// Inspector Tab — live desktop context under the cursor.
///
/// Shows:
///   - All open windows (foreground highlighted)
///   - Live query result for the current cursor position:
///     Process, Window, Region, full region text, line, word
///
/// Uses DesktopQueryService — no own logic, pure display.
/// </summary>
public class InspectorTab : UserControl
{
    // Dark Mode Colors (same as MainForm)
    private static readonly Color ThemeBg     = Color.FromArgb(30, 30, 30);
    private static readonly Color ThemeText   = Color.FromArgb(220, 220, 220);
    private static readonly Color ThemeBgPanel = Color.FromArgb(40, 40, 40);
    private static readonly Color LabelColor  = Color.FromArgb(140, 140, 140);
    private static readonly Color ValueColor  = Color.FromArgb(100, 180, 255);
    private static readonly Color ActiveColor = Color.FromArgb(80, 220, 80);

    private readonly ListView _windowList;
    private readonly RichTextBox _cursorDetail;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly Splitter _splitter;
    private readonly Panel _leftPanel;

    private DesktopModel? _model;
    private Point _lastCursorPos;
    private int _settledTicks;

    public InspectorTab()
    {
        BackColor = ThemeBg;
        Dock = DockStyle.Fill;

        // Left panel: resizable, window list
        _leftPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 350,
            BackColor = ThemeBg,
            MinimumSize = new Size(150, 0),
        };

        _splitter = new Splitter
        {
            Dock = DockStyle.Left,
            Width = 4,
            BackColor = Color.FromArgb(60, 60, 60),
            MinSize = 150,
        };

        var listLabel = new Label
        {
            Text = "Open windows",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = LabelColor,
            Font = new Font("Cascadia Mono", 8f),
            Padding = new Padding(4, 4, 0, 0),
        };

        _windowList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = ThemeBgPanel,
            ForeColor = ThemeText,
            Font = new Font("Cascadia Mono", 9f),
            BorderStyle = BorderStyle.None,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            HideSelection = false,
        };
        _windowList.Columns.Add("", 18);
        _windowList.Columns.Add("Process", 120);
        _windowList.Columns.Add("Title", 190);

        _leftPanel.Controls.Add(_windowList);
        _leftPanel.Controls.Add(listLabel);

        // Right panel: fills remaining space, cursor detail
        var detailLabel = new Label
        {
            Text = "Cursor position",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = LabelColor,
            Font = new Font("Cascadia Mono", 8f),
            Padding = new Padding(4, 4, 0, 0),
        };

        _cursorDetail = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = ThemeBgPanel,
            ForeColor = ThemeText,
            Font = new Font("Cascadia Mono", 10f),
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };

        var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = ThemeBg };
        rightPanel.Controls.Add(_cursorDetail);
        rightPanel.Controls.Add(detailLabel);

        // Dock order: rightPanel (Fill) → splitter → leftPanel (Left)
        Controls.Add(rightPanel);
        Controls.Add(_splitter);
        Controls.Add(_leftPanel);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _refreshTimer.Tick += OnTimerTick;
    }

    // --- Public API ---

    public void AttachModel(DesktopModel model)
    {
        DetachModel();
        _model = model;
        _model.WindowChanged += OnWindowChanged;
        _model.WindowAdded += OnWindowChanged;
        _model.WindowRemoved += OnWindowRemoved;
        _model.ActiveWindowChanged += OnActiveWindowChanged;
        RefreshWindowList();
    }

    public void DetachModel()
    {
        if (_model == null) return;
        _model.WindowChanged -= OnWindowChanged;
        _model.WindowAdded -= OnWindowChanged;
        _model.WindowRemoved -= OnWindowRemoved;
        _model.ActiveWindowChanged -= OnActiveWindowChanged;
        _model = null;
    }

    /// <summary>Width of the left panel (for settings persistence).</summary>
    public int LeftPanelWidth
    {
        get => _leftPanel.Width;
        set { if (value >= 150) _leftPanel.Width = value; }
    }

    public void StartPolling() => _refreshTimer.Start();
    public void StopPolling()  => _refreshTimer.Stop();

    public void RefreshWindowList()
    {
        if (InvokeRequired) { BeginInvoke(RefreshWindowList); return; }

        var activeHwnd = _model?.ActiveWindow?.Hwnd ?? 0;
        var windows = DesktopWindowList.Instance.All
            .Where(w => !w.IsMinimized)
            .OrderBy(w => w.ZOrder)
            .ToList();

        _windowList.BeginUpdate();
        _windowList.Items.Clear();

        foreach (var w in windows)
        {
            var item = new ListViewItem("");
            item.SubItems.Add(w.ProcessName);
            var title = w.Title.Length > 40 ? w.Title[..40] + "…" : w.Title;
            item.SubItems.Add(title);
            item.Tag = w.Hwnd;

            if (w.Hwnd == activeHwnd)
            {
                item.SubItems[0].Text = "▶";
                item.ForeColor = ActiveColor;
                item.Font = new Font("Cascadia Mono", 9f, FontStyle.Bold);
            }

            _windowList.Items.Add(item);
        }

        _windowList.EndUpdate();
    }

    // --- Timer + Cursor ---

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var pos = Cursor.Position;
        if (pos != _lastCursorPos)
        {
            _lastCursorPos = pos;
            _settledTicks = 0;
            return;
        }
        _settledTicks++;
        if (_settledTicks == 1)
            UpdateCursorDetail(pos);
    }

    private void UpdateCursorDetail(Point screenPos)
    {
        var result = DesktopQueryService.QueryAt(screenPos, (uint)Environment.ProcessId);
        RenderDetail(result, screenPos);
    }

    private void RenderDetail(DesktopQueryResult? result, Point screenPos)
    {
        _cursorDetail.Clear();

        AppendLabel("Position:  ");
        AppendValue($"{screenPos.X}, {screenPos.Y}\n");

        if (result == null)
        {
            AppendLabel("\nNo window at this position.\n");
            return;
        }

        AppendLabel("Process:   ");
        AppendValue($"{result.ProcessName}\n");

        AppendLabel("Window:    ");
        AppendValue($"{result.WindowTitle}\n");

        if (!result.HasLayout)
        {
            AppendLabel("\n(No layout — run OCR/Live first)\n");
            return;
        }

        AppendLabel("Word:      ");
        AppendValue($"{result.Word ?? ""}\n", bold: result.Word != null);

        AppendLabel("Line:      ");
        AppendValue($"{result.Line ?? ""}\n");

        AppendLabel("Region:    ");
        AppendValue($"{result.RegionType ?? "–"}");
        if (result.RegionName != null)
            AppendValue($"  [{result.RegionName}]");
        AppendValue("\n");

        if (result.RegionText != null)
        {
            AppendLabel("\nRegion text:\n");
            _cursorDetail.SelectionStart = _cursorDetail.TextLength;
            _cursorDetail.SelectionColor = Color.FromArgb(170, 200, 230);
            _cursorDetail.SelectionFont = new Font("Cascadia Mono", 9f);
            _cursorDetail.AppendText(result.RegionText + "\n");
        }
    }

    // --- Model Events ---

    private void OnWindowChanged(DesktopWindow _)        => RefreshWindowList();
    private void OnWindowRemoved(nint _)                 => RefreshWindowList();
    private void OnActiveWindowChanged(DesktopWindow _)  => RefreshWindowList();

    // --- Helpers ---

    private void AppendLabel(string text)
    {
        _cursorDetail.SelectionStart = _cursorDetail.TextLength;
        _cursorDetail.SelectionColor = LabelColor;
        _cursorDetail.SelectionFont = new Font("Cascadia Mono", 10f);
        _cursorDetail.AppendText(text);
    }

    private void AppendValue(string text, bool bold = false)
    {
        _cursorDetail.SelectionStart = _cursorDetail.TextLength;
        _cursorDetail.SelectionColor = ValueColor;
        _cursorDetail.SelectionFont = new Font("Cascadia Mono", 10f,
            bold ? FontStyle.Bold : FontStyle.Regular);
        _cursorDetail.AppendText(text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachModel();
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
