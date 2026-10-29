namespace WinfoMain.Overlay;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// Visual click/point indicator overlay — the pointer finger.
/// Three modes:
///   1. PointAt(x,y)  — Finger slides in from offscreen, LED blinks, stays at target
///   2. ClickAt(x,y)  — Quick flash with red LED at click position
///   3. LetUserPoint() — User drags finger with mouse, clicks to confirm position
///
/// Uses UpdateLayeredWindow for true per-pixel alpha transparency.
/// Runs on any STA thread with a message pump.
/// </summary>
public sealed class FingerOverlay : IDisposable
{
    private readonly Bitmap _fingerOriginal;
    private enum LedMode { Off, Green, Red }
    private readonly Dictionary<(Quadrant, LedMode), Bitmap> _fingerCache = new();

    private OverlayForm? _form;
    private System.Windows.Forms.Timer? _animTimer;

    // Animation state
    private int _animTargetX, _animTargetY;
    private double _animStartX, _animStartY;
    private int _animStep, _animTotalSteps;
    private Quadrant _animQuadrant;
    private bool _animLedState;
    private int _animLedBlinks;

    // User-point mode
    private TaskCompletionSource<Point?>? _userPointTcs;
    private bool _isUserPointMode;


    // Settings
    private float _scale = 0.3f;
    private int _slideInMs = 400;

    // LED position = fingernail center in original 270x270 image
    private static readonly PointF LedRelative = new(0.197f, 0.330f);

    // Hotspot = fingertip (leftmost pixel) in original 270x270 image (x=33,y=83)
    private static readonly PointF HotspotRelative = new(0.122f, 0.307f);

    public static FingerOverlay? Instance { get; private set; }

    /// <summary>
    /// Fires whenever the user drops the finger at a new position.
    /// Works in ALL modes — point_at, click, let_user_point.
    /// Subscribers (MCP Server, custom apps) use this to react to user pointing.
    /// </summary>
    public event Action<FingerDropResult>? OnDropped;

    public float Scale
    {
        get => _scale;
        set { _scale = Math.Clamp(value, 0.1f, 1.0f); _fingerCache.Clear(); }
    }

    public int SlideInMs
    {
        get => _slideInMs;
        set => _slideInMs = Math.Clamp(value, 100, 2000);
    }

    public FingerOverlay()
    {
        _fingerOriginal = LoadEmbeddedFinger();
        Instance = this;
    }

    public void Initialize()
    {
        if (_form != null) return;

        _form = new OverlayForm();
        _form.CreateControl();
        var _ = _form.Handle;

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
        _animTimer.Tick += OnAnimTick;
    }

    /// <summary>Slide finger in from offscreen to target, green LED blinking. Stays visible for DisplayMs.</summary>
    public void PointAt(int x, int y)
    {
        if (_form == null) return;
        _form.Invoke(() => StartSlideIn(x, y, LedMode.Green));
    }

    /// <summary>Quick flash with red LED at position. For click feedback (shorter display).</summary>
    public void ClickAt(int x, int y)
    {
        if (_form == null) return;
        _form.Invoke(() => ShowFingerImmediate(x, y, LedMode.Red));
    }

    public Task<Point?> LetUserPointAsync(CancellationToken ct = default)
    {
        if (_form == null) return Task.FromResult<Point?>(null);

        _userPointTcs = new TaskCompletionSource<Point?>();
        ct.Register(() => CancelUserPoint());

        _form.Invoke(() => StartUserPointMode());

        return _userPointTcs.Task;
    }

    public void Hide()
    {
        if (_form == null) return;
        _form.Invoke(() =>
        {


            _animTimer?.Stop();
            StopBlink();

            _form.Hide();
        });
    }

    public void Dispose()
    {

        StopBlink();

        _blinkTimer?.Dispose();

        _animTimer?.Dispose();
        _form?.Dispose();
        _fingerOriginal.Dispose();
        foreach (var bmp in _fingerCache.Values) bmp.Dispose();
        _fingerCache.Clear();
        if (Instance == this) Instance = null;
    }

    // === Slide-In Animation ===

    private LedMode _animLedMode;

    private void StartSlideIn(int targetX, int targetY, LedMode finalLed)
    {

        _animTimer!.Stop();
        StopBlink();

        _isUserPointMode = false;

        _animTargetX = targetX;
        _animTargetY = targetY;
        _animQuadrant = GetQuadrant(targetX, targetY);
        _animLedState = false;
        _animLedBlinks = 0;
        _animLedMode = finalLed;

        // Calculate start position (offscreen, from the direction the finger body extends)
        var screen = Screen.FromPoint(new Point(targetX, targetY));
        int slideDistance = (int)(200 * _scale) + 150;

        (_animStartX, _animStartY) = _animQuadrant switch
        {
            Quadrant.TopLeft => (targetX + slideDistance, targetY + slideDistance),
            Quadrant.TopRight => (targetX - slideDistance, targetY + slideDistance),
            Quadrant.BottomLeft => (targetX + slideDistance, targetY - slideDistance),
            Quadrant.BottomRight => (targetX - slideDistance, targetY - slideDistance),
            _ => (targetX + slideDistance, targetY + slideDistance)
        };

        _animStep = 0;
        _animTotalSteps = Math.Max(1, _slideInMs / 16);

        _form!.Show();
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_form == null) return;

        _animStep++;
        float t = Math.Min(1.0f, (float)_animStep / _animTotalSteps);

        // Ease-out cubic for smooth deceleration
        float ease = 1.0f - (1.0f - t) * (1.0f - t) * (1.0f - t);

        int currentX = (int)(_animStartX + (_animTargetX - _animStartX) * ease);
        int currentY = (int)(_animStartY + (_animTargetY - _animStartY) * ease);

        // Blink LED every 4 frames during animation
        if (_animStep % 4 == 0)
        {
            _animLedState = !_animLedState;
            _animLedBlinks++;
        }

        var ledNow = _animLedState ? _animLedMode : LedMode.Off;
        var finger = GetOrCreateFinger(_animQuadrant, ledNow);
        var hotspot = GetHotspot(_animQuadrant, finger.Size);

        UpdateOverlay(finger, currentX - hotspot.X, currentY - hotspot.Y);

        if (t >= 1.0f)
        {
            _animTimer!.Stop();

            // Show final state with LED on
            var finalFinger = GetOrCreateFinger(_animQuadrant, _animLedMode);
            UpdateOverlay(finalFinger, _animTargetX - hotspot.X, _animTargetY - hotspot.Y);

            // After slide-in: always enable drag so user can grab the finger
            _lastOverlayX = _animTargetX - hotspot.X;
            _lastOverlayY = _animTargetY - hotspot.Y;
            _userPointQuadrant = _animQuadrant;
            EnableDragMode();
            StartBlink(_animQuadrant);
        }
    }

    // === Immediate show (for click feedback) ===

    private void ShowFingerImmediate(int screenX, int screenY, LedMode led)
    {

        _animTimer!.Stop();
        StopBlink();


        var quadrant = GetQuadrant(screenX, screenY);
        var finger = GetOrCreateFinger(quadrant, led);
        var hotspot = GetHotspot(quadrant, finger.Size);

        _lastOverlayX = screenX - hotspot.X;
        _lastOverlayY = screenY - hotspot.Y;
        _userPointQuadrant = quadrant;

        UpdateOverlay(finger, _lastOverlayX, _lastOverlayY);
        _form!.Show();

        // Always stay visible and draggable
        _isUserPointMode = true;
        EnableDragMode();
        StartBlink(quadrant);
    }

    // === User-Point Mode (Drag & Drop) ===

    private Quadrant _userPointQuadrant;
    private int _lastOverlayX, _lastOverlayY; // track form position during drag
    private System.Windows.Forms.Timer? _blinkTimer;
    private bool _blinkState;

    private void StartUserPointMode()
    {

        _animTimer!.Stop();
        StopBlink();

        _isUserPointMode = true;

        // Slide-in to screen center, then enable drag
        var screen = Screen.PrimaryScreen!.Bounds;
        int cx = screen.X + screen.Width / 2;
        int cy = screen.Y + screen.Height / 2;

        _userPointQuadrant = GetQuadrant(cx, cy);

        // Use slide-in animation to center — after arrival, enable drag mode
        _animTargetX = cx;
        _animTargetY = cy;
        _animQuadrant = _userPointQuadrant;
        _animLedState = false;
        _animLedBlinks = 0;
        _animLedMode = LedMode.Green;

        var slideDistance = (int)(200 * _scale) + 150;
        (_animStartX, _animStartY) = _animQuadrant switch
        {
            Quadrant.TopLeft => (cx + slideDistance, cy + slideDistance),
            Quadrant.TopRight => (cx - slideDistance, cy + slideDistance),
            Quadrant.BottomLeft => (cx + slideDistance, cy - slideDistance),
            Quadrant.BottomRight => (cx - slideDistance, cy - slideDistance),
            _ => (cx + slideDistance, cy + slideDistance)
        };

        _animStep = 0;
        _animTotalSteps = Math.Max(1, _slideInMs / 16);

        _form!.Show();
        _animTimer!.Start();
        // Drag mode gets enabled in OnAnimTick when animation completes
    }

    private void EnableDragMode()
    {
        _isUserPointMode = true;
        _form!.SetClickThrough(false);
        _form.UserDragMove = OnUserDragMove;
        _form.UserDropped = OnUserDropped;
        _form.UserCancelled = OnUserCancelled;

        // Update tracked position
        var finger = GetOrCreateFinger(_userPointQuadrant, LedMode.Green);
        var hotspot = GetHotspot(_userPointQuadrant, finger.Size);
        _lastOverlayX = _animTargetX - hotspot.X;
        _lastOverlayY = _animTargetY - hotspot.Y;
    }

    private void OnUserDragMove(Point formPos)
    {
        if (!_isUserPointMode || _form == null) return;

        StopBlink(); // stop blinking while dragging
        var finger = GetOrCreateFinger(_userPointQuadrant, LedMode.Green);
        _lastOverlayX = formPos.X;
        _lastOverlayY = formPos.Y;
        UpdateOverlay(finger, _lastOverlayX, _lastOverlayY);
    }

    private void OnUserDropped(Point screenPos)
    {
        if (!_isUserPointMode) return;

        // Compute fingertip position from tracked overlay position
        var finger = GetOrCreateFinger(_userPointQuadrant, LedMode.Green);
        var hotspot = GetHotspot(_userPointQuadrant, finger.Size);
        int tipX = _lastOverlayX + hotspot.X;
        int tipY = _lastOverlayY + hotspot.Y;

        // Stay permanently with blinking LED — keep draggable for repositioning
        StartBlink(_userPointQuadrant);

        // Fire event for all subscribers (MCP Server, custom apps)
        var dropResult = new FingerDropResult(new Point(tipX, tipY));
        OnDropped?.Invoke(dropResult);

        // Also complete the TCS if let_user_point is waiting
        _userPointTcs?.TrySetResult(new Point(tipX, tipY));
    }

    private void OnUserCancelled()
    {
        CancelUserPoint();
    }

    private void CancelUserPoint()
    {
        if (_form == null) return;
        _form.Invoke(() =>
        {
            _animTimer!.Stop();

            _isUserPointMode = false;
            _form.SetClickThrough(true);
            _form.UserDragMove = null;
            _form.UserDropped = null;
            _form.UserClicked = null;
            _form.UserCancelled = null;
            _form.Hide();
        });
        _userPointTcs?.TrySetResult(null);
    }

    // === LED Blink (permanent, for let_user_point drop position) ===

    private Quadrant _blinkQuadrant;

    private void StartBlink(Quadrant quadrant)
    {
        StopBlink();
        _blinkQuadrant = quadrant;
        _blinkState = true;

        _blinkTimer ??= new System.Windows.Forms.Timer { Interval = 500 };
        _blinkTimer.Tick += OnBlinkTick;
        _blinkTimer.Start();
    }

    private void StopBlink()
    {
        if (_blinkTimer != null)
        {
            _blinkTimer.Stop();
            _blinkTimer.Tick -= OnBlinkTick;
        }
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        if (_form == null) return;
        _blinkState = !_blinkState;
        var led = _blinkState ? LedMode.Green : LedMode.Off;
        var finger = GetOrCreateFinger(_blinkQuadrant, led);
        UpdateOverlay(finger, _lastOverlayX, _lastOverlayY);
    }

    // === Rendering ===

    private Bitmap GetOrCreateFinger(Quadrant quadrant, LedMode led)
    {
        var key = (quadrant, led);
        if (_fingerCache.TryGetValue(key, out var cached))
            return cached;

        int w = (int)(_fingerOriginal.Width * _scale);
        int h = (int)(_fingerOriginal.Height * _scale);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_fingerOriginal, 0, 0, w, h);
        }

        switch (quadrant)
        {
            case Quadrant.TopLeft: break;
            case Quadrant.TopRight: bmp.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
            case Quadrant.BottomLeft: bmp.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
            case Quadrant.BottomRight: bmp.RotateFlip(RotateFlipType.RotateNoneFlipXY); break;
        }

        if (led != LedMode.Off)
            DrawLed(bmp, quadrant, led);

        if (_fingerCache.TryGetValue(key, out var old))
            old.Dispose();
        _fingerCache[key] = bmp;
        return bmp;
    }

    private void DrawLed(Bitmap bmp, Quadrant quadrant, LedMode led)
    {
        var ledPos = GetLedPosition(quadrant, bmp.Size);
        int ledRadius = Math.Max(2, (int)(bmp.Width * 0.035f));

        Color coreColor, glowCenter;
        if (led == LedMode.Red)
        {
            coreColor = Color.FromArgb(240, 255, 30, 30);
            glowCenter = Color.FromArgb(140, 255, 0, 0);
        }
        else // Green
        {
            coreColor = Color.FromArgb(240, 30, 220, 30);
            glowCenter = Color.FromArgb(140, 0, 200, 0);
        }

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Glow
        int glowR = ledRadius * 3;
        using (var glowBrush = new PathGradientBrush(CreateCirclePath(ledPos.X, ledPos.Y, glowR)))
        {
            glowBrush.CenterColor = glowCenter;
            glowBrush.SurroundColors = new[] { Color.FromArgb(0, glowCenter.R, glowCenter.G, glowCenter.B) };
            g.FillEllipse(glowBrush, ledPos.X - glowR, ledPos.Y - glowR, glowR * 2, glowR * 2);
        }

        // Core
        using var ledBrush = new SolidBrush(coreColor);
        g.FillEllipse(ledBrush, ledPos.X - ledRadius, ledPos.Y - ledRadius, ledRadius * 2, ledRadius * 2);

        // Highlight
        int hlR = Math.Max(1, ledRadius / 3);
        using var hlBrush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
        g.FillEllipse(hlBrush, ledPos.X - hlR - 1, ledPos.Y - hlR - 1, hlR * 2, hlR * 2);
    }

    private static GraphicsPath CreateCirclePath(int cx, int cy, int radius)
    {
        var path = new GraphicsPath();
        path.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
        return path;
    }

    // === Coordinate helpers ===

    private enum Quadrant { TopLeft, TopRight, BottomLeft, BottomRight }

    private static Quadrant GetQuadrant(int screenX, int screenY)
    {
        var screen = Screen.FromPoint(new Point(screenX, screenY));
        var bounds = screen.Bounds;

        float relX = (float)(screenX - bounds.X) / bounds.Width;
        float relY = (float)(screenY - bounds.Y) / bounds.Height;

        return (relX > 0.75f, relY > 0.75f) switch
        {
            (false, false) => Quadrant.TopLeft,
            (true, false) => Quadrant.TopRight,
            (false, true) => Quadrant.BottomLeft,
            (true, true) => Quadrant.BottomRight
        };
    }

    private Point GetHotspot(Quadrant quadrant, Size imageSize)
    {
        int hx = (int)(HotspotRelative.X * imageSize.Width);
        int hy = (int)(HotspotRelative.Y * imageSize.Height);

        return quadrant switch
        {
            Quadrant.TopLeft => new Point(hx, hy),
            Quadrant.TopRight => new Point(imageSize.Width - hx, hy),
            Quadrant.BottomLeft => new Point(hx, imageSize.Height - hy),
            Quadrant.BottomRight => new Point(imageSize.Width - hx, imageSize.Height - hy),
            _ => new Point(hx, hy)
        };
    }

    private static Point GetLedPosition(Quadrant quadrant, Size imageSize)
    {
        int lx = (int)(LedRelative.X * imageSize.Width);
        int ly = (int)(LedRelative.Y * imageSize.Height);

        return quadrant switch
        {
            Quadrant.TopLeft => new Point(lx, ly),
            Quadrant.TopRight => new Point(imageSize.Width - lx, ly),
            Quadrant.BottomLeft => new Point(lx, imageSize.Height - ly),
            Quadrant.BottomRight => new Point(imageSize.Width - lx, imageSize.Height - ly),
            _ => new Point(lx, ly)
        };
    }

    // === UpdateLayeredWindow ===

    private void UpdateOverlay(Bitmap bmp, int x, int y)
    {
        if (_form == null) return;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memDc, hBitmap);

        var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
        var sourceLocation = new POINT { x = 0, y = 0 };
        var topPos = new POINT { x = x, y = y };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };

        UpdateLayeredWindow(_form.Handle, screenDc, ref topPos, ref size,
            memDc, ref sourceLocation, 0, ref blend, ULW_ALPHA);

        SelectObject(memDc, oldBitmap);
        DeleteObject(hBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static Bitmap LoadEmbeddedFinger()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("finger.png", StringComparison.OrdinalIgnoreCase));

        if (name == null)
            throw new FileNotFoundException("Embedded resource finger.png not found");

        using var stream = asm.GetManifestResourceStream(name)!;
        return new Bitmap(stream);
    }

    // === Overlay Form ===

    private sealed class OverlayForm : Form
    {
        private bool _clickThrough = true;
        private bool _isDragging;
        private Point _dragOffset;

        public Action<Point>? UserClicked { get; set; }
        public Action<Point>? UserDragMove { get; set; }
        public Action<Point>? UserDropped { get; set; }
        public Action? UserCancelled { get; set; }

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(1, 1);
        }

        public void SetClickThrough(bool clickThrough)
        {
            if (_clickThrough == clickThrough) return;
            _clickThrough = clickThrough;

            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            if (clickThrough)
                exStyle |= WS_EX_TRANSPARENT;
            else
                exStyle &= ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!_clickThrough)
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (UserDragMove != null)
                    {
                        // Drag mode: start dragging
                        _isDragging = true;
                        _dragOffset = new Point(e.X, e.Y);
                        Capture = true;
                    }
                    else
                    {
                        UserClicked?.Invoke(Cursor.Position);
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    _isDragging = false;
                    UserCancelled?.Invoke();
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isDragging)
            {
                var screen = Cursor.Position;
                UserDragMove?.Invoke(new Point(screen.X - _dragOffset.X, screen.Y - _dragOffset.Y));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isDragging && e.Button == MouseButtons.Left)
            {
                _isDragging = false;
                Capture = false;
                UserDropped?.Invoke(Cursor.Position);
            }
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_clickThrough && e.KeyCode == Keys.Escape)
            {
                _isDragging = false;
                UserCancelled?.Invoke();
            }
            base.OnKeyDown(e);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                cp.ExStyle |= WS_EX_TRANSPARENT;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                cp.ExStyle |= WS_EX_TOPMOST;
                return cp;
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }

    // === P/Invoke ===

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

}
