namespace WinfoMain.Common;

using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Hashing;
using System.Runtime.InteropServices;

/// <summary>
/// Static utility for capturing window bitmaps and computing content hashes.
/// Used by OcrEngine (capture for OCR) and BackgroundWorker (change detection).
/// </summary>
public static class WindowCapture
{
    /// <summary>
    /// Capture a window's bitmap via PrintWindow (PW_RENDERFULLCONTENT).
    /// Falls back to BitBlt from screen if PrintWindow fails.
    /// Returns null if the window is gone or has zero size.
    /// </summary>
    public static Bitmap? Capture(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();

        // PW_RENDERFULLCONTENT (2) captures even DWM-composed content
        bool ok = PrintWindow(hwnd, hdc, 2);
        graphics.ReleaseHdc(hdc);

        if (!ok)
        {
            // Fallback: BitBlt from screen (works for elevated windows)
            using var screenDc = Graphics.FromHwnd(IntPtr.Zero);
            var screenHdc = screenDc.GetHdc();
            var bmpHdc = graphics.GetHdc();
            BitBlt(bmpHdc, 0, 0, width, height, screenHdc, rect.Left, rect.Top, SRCCOPY);
            graphics.ReleaseHdc(bmpHdc);
            screenDc.ReleaseHdc(screenHdc);
        }

        return bitmap;
    }

    /// <summary>
    /// Compute XxHash64 over the raw pixel data of a bitmap.
    /// Used for fast change detection (same hash = same visual content).
    /// </summary>
    public static unsafe ulong ComputeHash(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            int byteCount = Math.Abs(data.Stride) * data.Height;
            var span = new ReadOnlySpan<byte>((void*)data.Scan0, byteCount);
            return XxHash64.HashToUInt64(span);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // --- Win32 Interop ---

    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint hdcDest, int x, int y, int w, int h,
        nint hdcSrc, int srcX, int srcY, uint rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
