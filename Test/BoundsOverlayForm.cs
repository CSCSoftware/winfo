namespace WinfoMain.Test;

using System.Drawing;

/// <summary>
/// Transparent overlay form that draws a rectangle around a word's bounds.
/// Used by cursor hit-test to visualize which word the cursor is hovering over.
/// </summary>
public class BoundsOverlayForm : Form
{
    private Rectangle _bounds;

    public BoundsOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        StartPosition = FormStartPosition.Manual;
    }

    public void ShowBounds(Rectangle screenBounds)
    {
        const int pad = 3;
        _bounds = screenBounds;
        SetBounds(
            screenBounds.X - pad,
            screenBounds.Y - pad,
            screenBounds.Width + pad * 2,
            screenBounds.Height + pad * 2);
        Invalidate();
        if (!Visible) Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(255, 80, 80), 2f);
        e.Graphics.DrawRectangle(pen, 2, 2, Width - 5, Height - 5);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT — click-through
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW — no taskbar entry
            return cp;
        }
    }
}
