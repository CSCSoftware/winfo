namespace WinfoMain.Test;

using System.Drawing;
using System.Drawing.Drawing2D;

/// <summary>
/// Fullscreen transparent overlay for selecting a screen region by rubber-banding.
/// Shows a semi-transparent dark overlay with the selected rectangle cut out.
/// ESC cancels, mouse release confirms.
/// </summary>
public class RegionSelectForm : Form
{
    private Point _startPoint;
    private Rectangle _selection;
    private bool _dragging;

    /// <summary>
    /// The selected region in screen coordinates, or Rectangle.Empty if cancelled.
    /// </summary>
    public Rectangle SelectedRegion { get; private set; }

    public RegionSelectForm()
    {
        // Cover all screens
        var bounds = SystemInformation.VirtualScreen;
        Location = bounds.Location;
        Size = bounds.Size;

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Opacity = 0.4;

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                SelectedRegion = Rectangle.Empty;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _startPoint = e.Location;
            _selection = Rectangle.Empty;
            _dragging = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;

        int x = Math.Min(_startPoint.X, e.X);
        int y = Math.Min(_startPoint.Y, e.Y);
        int w = Math.Abs(e.X - _startPoint.X);
        int h = Math.Abs(e.Y - _startPoint.Y);
        _selection = new Rectangle(x, y, w, h);

        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        if (_selection.Width < 5 || _selection.Height < 5)
        {
            SelectedRegion = Rectangle.Empty;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        // Convert from form-local to screen coordinates
        var screenOrigin = SystemInformation.VirtualScreen.Location;
        SelectedRegion = new Rectangle(
            _selection.X + screenOrigin.X,
            _selection.Y + screenOrigin.Y,
            _selection.Width,
            _selection.Height);

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_selection.Width <= 0 || _selection.Height <= 0) return;

        var g = e.Graphics;

        // Draw bright rectangle border for the selection
        using var pen = new Pen(Color.FromArgb(0, 180, 255), 2);
        g.DrawRectangle(pen, _selection);

        // Light fill to show selected area clearly against the dark overlay
        using var fillBrush = new SolidBrush(Color.FromArgb(40, 0, 180, 255));
        g.FillRectangle(fillBrush, _selection);

        // Size label
        var label = $"{_selection.Width} x {_selection.Height}";
        using var font = new Font("Segoe UI", 10f);
        using var labelBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        var labelPos = new PointF(_selection.Left, _selection.Bottom + 4);
        g.DrawString(label, font, labelBrush, labelPos);
    }
}
