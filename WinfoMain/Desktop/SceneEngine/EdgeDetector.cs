namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using System.Drawing.Imaging;

/// <summary>
/// Sobel edge detection on bitmaps.
/// Produces a transparent overlay with white edges (alpha = magnitude).
/// </summary>
public static class EdgeDetector
{
    // Sobel kernels (3x3)
    private static readonly int[] Gx = [-1, 0, 1, -2, 0, 2, -1, 0, 1];
    private static readonly int[] Gy = [-1, -2, -1, 0, 0, 0, 1, 2, 1];

    /// <summary>
    /// Detect edges using Sobel operator.
    /// Returns a bitmap (width x height) with white pixels on transparent background,
    /// alpha proportional to edge magnitude.
    /// </summary>
    public static unsafe Bitmap? Detect(Bitmap? source, int width, int height)
    {
        if (source == null) return null;

        int srcW = source.Width;
        int srcH = source.Height;
        if (srcW < 3 || srcH < 3) return null;

        var srcRect = new Rectangle(0, 0, srcW, srcH);
        var srcData = source.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* srcPtr = (byte*)srcData.Scan0;
            int srcStride = srcData.Stride;

            // Step 1: Convert to grayscale
            var gray = new byte[srcW * srcH];
            for (int y = 0; y < srcH; y++)
            {
                byte* row = srcPtr + y * srcStride;
                int offset = y * srcW;
                for (int x = 0; x < srcW; x++)
                {
                    int px = x * 4; // BGRA
                    gray[offset + x] = (byte)(0.114 * row[px] + 0.587 * row[px + 1] + 0.299 * row[px + 2]);
                }
            }

            // Step 2: Apply Sobel and build result
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var dstRect = new Rectangle(0, 0, width, height);
            var dstData = result.LockBits(dstRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* dstPtr = (byte*)dstData.Scan0;
                int dstStride = dstData.Stride;

                // Clear to fully transparent
                for (int y = 0; y < height; y++)
                {
                    byte* dstRow = dstPtr + y * dstStride;
                    for (int x = 0; x < width * 4; x++)
                        dstRow[x] = 0;
                }

                // Sobel convolution (skip 1px border)
                for (int y = 1; y < srcH - 1; y++)
                {
                    if (y >= height) break;
                    byte* dstRow = dstPtr + y * dstStride;

                    for (int x = 1; x < srcW - 1; x++)
                    {
                        if (x >= width) break;

                        int sumX = 0, sumY = 0;
                        int ki = 0;
                        for (int ky = -1; ky <= 1; ky++)
                        {
                            int gy = (y + ky) * srcW + (x - 1);
                            for (int kx = -1; kx <= 1; kx++)
                            {
                                int g = gray[gy + kx + 1];
                                sumX += Gx[ki] * g;
                                sumY += Gy[ki] * g;
                                ki++;
                            }
                        }

                        int mag = (int)Math.Sqrt(sumX * sumX + sumY * sumY);
                        if (mag > 255) mag = 255;
                        if (mag < 10) continue; // skip noise

                        int px = x * 4;
                        dstRow[px] = 255;     // B
                        dstRow[px + 1] = 255; // G
                        dstRow[px + 2] = 255; // R
                        dstRow[px + 3] = (byte)mag; // A = magnitude
                    }
                }
            }
            finally
            {
                result.UnlockBits(dstData);
            }

            return result;
        }
        finally
        {
            source.UnlockBits(srcData);
        }
    }
}
