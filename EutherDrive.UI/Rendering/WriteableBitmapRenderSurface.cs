using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EutherDrive.Rendering;

public sealed class WriteableBitmapRenderSurface : IGameRenderSurface, IDisposable
{
    private const int AdvancedFilterStrongEdgeThreshold = 84;
    private const int AdvancedFilterMediumEdgeThreshold = 40;
    private const int AdvancedFilterStrongGain256 = 208;
    private const int AdvancedFilterMediumGain256 = 152;
    private const int AdvancedFilterBaseGain256 = 96;
    private const int AdvancedFilterClampSlack = 10;
    private const int InterlacedTextSafeStrongGain256 = 160;
    private const int InterlacedTextSafeMediumGain256 = 120;
    private const int InterlacedTextSafeBaseGain256 = 64;
    private const int InterlacedTextSafeClampSlack = 4;
    private const int InterlacedTextProtectRangeThreshold = 72;
    private const int InterlacedTextProtectExtremeThreshold = 28;
    private const int InterlacedTextSupportThreshold = 28;
    private const int InterlacedTextOppositeThreshold = 40;
    private const int InterlacedTextPushToExtreme256 = 176;

    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };

    private WriteableBitmap? _bitmap;

    public Control View => _image;
    public WriteableBitmap? Bitmap => _bitmap;
    public int PixelWidth => _bitmap?.PixelSize.Width ?? 0;
    public int PixelHeight => _bitmap?.PixelSize.Height ?? 0;

    public bool EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Render surface received invalid size {width}x{height}.");

        if (_bitmap != null && _bitmap.PixelSize.Width == width && _bitmap.PixelSize.Height == height)
            return false;

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        _image.Source = _bitmap;
        return true;
    }

    public unsafe FrameBlitMetrics Present(ReadOnlySpan<byte> source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf)
    {
        if (source.IsEmpty || width <= 0 || height <= 0 || srcStride <= 0)
            return FrameBlitMetrics.None;

        EnsureSize(width, height);
        if (_bitmap == null)
            return FrameBlitMetrics.None;

        long lockStart = measurePerf ? Stopwatch.GetTimestamp() : 0;
        using var fb = _bitmap.Lock();
        long lockTicks = measurePerf ? Stopwatch.GetTimestamp() - lockStart : 0;

        int dstStride = fb.RowBytes;
        int rowBytes = Math.Min(width * 4, Math.Min(srcStride, dstStride));
        if (rowBytes <= 0)
            return new FrameBlitMetrics(lockTicks, 0);

        fixed (byte* pSrc0 = source)
        {
            byte* pDst0 = (byte*)fb.Address.ToPointer();
            long blitStart = measurePerf ? Stopwatch.GetTimestamp() : 0;

            if (rowBytes == srcStride && rowBytes == dstStride && !options.ForceOpaque && !options.ApplyScanlines && !options.ApplyAdvancedPixelFilter)
            {
                long totalBytes = (long)rowBytes * height;
                Buffer.MemoryCopy(pSrc0, pDst0, totalBytes, totalBytes);
            }
            else if (options.ApplyAdvancedPixelFilter)
            {
                BlitAdvancedPixelFilter(
                    pSrc0,
                    pDst0,
                    height,
                    srcStride,
                    dstStride,
                    rowBytes,
                    options.ForceOpaque,
                    options.ApplyScanlines,
                    options.ScanlineDarkenFactor,
                    options.AdvancedFilterProfile);
            }
            else
            {
                BlitFrameRows(
                    pSrc0,
                    pDst0,
                    height,
                    srcStride,
                    dstStride,
                    rowBytes,
                    options.ForceOpaque,
                    options.ApplyScanlines,
                    options.ScanlineDarkenFactor);
            }

            long blitTicks = measurePerf ? Stopwatch.GetTimestamp() - blitStart : 0;
            return new FrameBlitMetrics(lockTicks, blitTicks);
        }
    }

    public void Reset()
    {
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public void Dispose() => Reset();

    private static unsafe void BlitFrameRows(
        byte* pSrc0,
        byte* pDst0,
        int height,
        int srcStride,
        int dstStride,
        int copyBytesPerRow,
        bool forceOpaque,
        bool applyScanlines,
        int scanlineDarkenFactor)
    {
        for (int y = 0; y < height; y++)
        {
            byte* pSrcRow = pSrc0 + (y * srcStride);
            byte* pDstRow = pDst0 + (y * dstStride);

            bool darkenRow = applyScanlines && ((y & 1) == 1);
            if (forceOpaque || darkenRow)
            {
                for (int x = 0; x < copyBytesPerRow; x += 4)
                {
                    byte b = pSrcRow[x + 0];
                    byte g = pSrcRow[x + 1];
                    byte r = pSrcRow[x + 2];
                    byte a = pSrcRow[x + 3];

                    if (darkenRow)
                    {
                        b = (byte)((b * scanlineDarkenFactor) >> 8);
                        g = (byte)((g * scanlineDarkenFactor) >> 8);
                        r = (byte)((r * scanlineDarkenFactor) >> 8);
                    }

                    pDstRow[x + 0] = b;
                    pDstRow[x + 1] = g;
                    pDstRow[x + 2] = r;
                    pDstRow[x + 3] = forceOpaque ? (byte)0xFF : a;
                }
            }
            else
            {
                Buffer.MemoryCopy(pSrcRow, pDstRow, dstStride, copyBytesPerRow);
            }
        }
    }

    private static unsafe void BlitAdvancedPixelFilter(
        byte* pSrc0,
        byte* pDst0,
        int height,
        int srcStride,
        int dstStride,
        int copyBytesPerRow,
        bool forceOpaque,
        bool applyScanlines,
        int scanlineDarkenFactor,
        AdvancedPixelFilterProfile profile)
    {
        for (int y = 0; y < height; y++)
        {
            byte* pSrcRow = pSrc0 + (y * srcStride);
            byte* pSrcRowUp = pSrc0 + ((y > 0 ? (y - 1) : y) * srcStride);
            byte* pSrcRowDown = pSrc0 + ((y + 1 < height ? (y + 1) : y) * srcStride);
            byte* pDstRow = pDst0 + (y * dstStride);
            bool darkenRow = applyScanlines && ((y & 1) == 1);

            for (int x = 0; x < copyBytesPerRow; x += 4)
            {
                int xLeft = x > 0 ? x - 4 : x;
                int xRight = x + 4 < copyBytesPerRow ? x + 4 : x;

                byte cb = pSrcRow[x + 0];
                byte cg = pSrcRow[x + 1];
                byte cr = pSrcRow[x + 2];
                byte ca = pSrcRow[x + 3];

                byte lb = pSrcRow[xLeft + 0];
                byte lg = pSrcRow[xLeft + 1];
                byte lr = pSrcRow[xLeft + 2];

                byte rb = pSrcRow[xRight + 0];
                byte rg = pSrcRow[xRight + 1];
                byte rr = pSrcRow[xRight + 2];

                byte ub = pSrcRowUp[x + 0];
                byte ug = pSrcRowUp[x + 1];
                byte ur = pSrcRowUp[x + 2];

                byte db = pSrcRowDown[x + 0];
                byte dg = pSrcRowDown[x + 1];
                byte dr = pSrcRowDown[x + 2];

                int cY = Luma(cr, cg, cb);
                int lY = Luma(lr, lg, lb);
                int rY = Luma(rr, rg, rb);
                int uY = Luma(ur, ug, ub);
                int dY = Luma(dr, dg, db);
                int edge = Math.Abs(cY - lY)
                    + Math.Abs(cY - rY)
                    + Math.Abs(cY - uY)
                    + Math.Abs(cY - dY);

                bool textSafeInterlaced = profile == AdvancedPixelFilterProfile.PsxInterlacedTextSafe;
                int textEdgePolarity = textSafeInterlaced ? ClassifyTextEdge(cY, lY, rY, uY, dY) : 0;
                int gain256 = textSafeInterlaced
                        ? edge > AdvancedFilterStrongEdgeThreshold
                            ? InterlacedTextSafeStrongGain256
                            : edge > AdvancedFilterMediumEdgeThreshold
                                ? InterlacedTextSafeMediumGain256
                                : InterlacedTextSafeBaseGain256
                        : edge > AdvancedFilterStrongEdgeThreshold
                            ? AdvancedFilterStrongGain256
                            : edge > AdvancedFilterMediumEdgeThreshold
                                ? AdvancedFilterMediumGain256
                                : AdvancedFilterBaseGain256;
                int clampSlack = textSafeInterlaced ? InterlacedTextSafeClampSlack : AdvancedFilterClampSlack;

                byte b = AdaptiveSharpenChannel(cb, lb, rb, ub, db, gain256, clampSlack, textSafeInterlaced, textEdgePolarity);
                byte g = AdaptiveSharpenChannel(cg, lg, rg, ug, dg, gain256, clampSlack, textSafeInterlaced, textEdgePolarity);
                byte r = AdaptiveSharpenChannel(cr, lr, rr, ur, dr, gain256, clampSlack, textSafeInterlaced, textEdgePolarity);

                if (darkenRow)
                {
                    b = (byte)((b * scanlineDarkenFactor) >> 8);
                    g = (byte)((g * scanlineDarkenFactor) >> 8);
                    r = (byte)((r * scanlineDarkenFactor) >> 8);
                }

                pDstRow[x + 0] = b;
                pDstRow[x + 1] = g;
                pDstRow[x + 2] = r;
                pDstRow[x + 3] = forceOpaque ? (byte)0xFF : ca;
            }
        }
    }

    private static byte AdaptiveSharpenChannel(byte c, byte l, byte r, byte u, byte d, int gain256, int clampSlack, bool textSafeInterlaced, int textEdgePolarity)
    {
        int center = c;
        int minN = Math.Min(center, Math.Min(Math.Min(l, r), Math.Min(u, d)));
        int maxN = Math.Max(center, Math.Max(Math.Max(l, r), Math.Max(u, d)));
        int low = Math.Max(0, minN - clampSlack);
        int high = Math.Min(255, maxN + clampSlack);
        int sharpened;
        if (textEdgePolarity != 0)
        {
            int target = textEdgePolarity > 0 ? high : low;
            int distance = textEdgePolarity > 0 ? target - center : center - target;
            sharpened = textEdgePolarity > 0
                ? center + ((distance * InterlacedTextPushToExtreme256) >> 8)
                : center - ((distance * InterlacedTextPushToExtreme256) >> 8);
        }
        else
        {
            int blur = textSafeInterlaced
                ? ((center * 3) + ((l + r) * 2) + u + d) / 9
                : ((center * 2) + l + r + u + d) / 6;
            int detail = center - blur;
            sharpened = center + ((detail * gain256) >> 8);
        }

        if (sharpened < low) sharpened = low;
        if (sharpened > high) sharpened = high;
        return (byte)sharpened;
    }

    private static int ClassifyTextEdge(int center, int left, int right, int up, int down)
    {
        int minY = Math.Min(center, Math.Min(Math.Min(left, right), Math.Min(up, down)));
        int maxY = Math.Max(center, Math.Max(Math.Max(left, right), Math.Max(up, down)));
        if ((maxY - minY) < InterlacedTextProtectRangeThreshold)
            return 0;

        bool nearBrightExtreme = (maxY - center) <= InterlacedTextProtectExtremeThreshold;
        bool nearDarkExtreme = (center - minY) <= InterlacedTextProtectExtremeThreshold;
        if (!nearBrightExtreme && !nearDarkExtreme)
            return 0;

        int supportNeighbors = 0;
        int oppositeNeighbors = 0;
        AccumulateTextEdgeNeighbor(left, minY, maxY, nearBrightExtreme, ref supportNeighbors, ref oppositeNeighbors);
        AccumulateTextEdgeNeighbor(right, minY, maxY, nearBrightExtreme, ref supportNeighbors, ref oppositeNeighbors);
        AccumulateTextEdgeNeighbor(up, minY, maxY, nearBrightExtreme, ref supportNeighbors, ref oppositeNeighbors);
        AccumulateTextEdgeNeighbor(down, minY, maxY, nearBrightExtreme, ref supportNeighbors, ref oppositeNeighbors);
        if (supportNeighbors < 1 || oppositeNeighbors < 1)
            return 0;

        return nearBrightExtreme ? 1 : -1;
    }

    private static void AccumulateTextEdgeNeighbor(int neighbor, int minY, int maxY, bool brightEdge, ref int supportNeighbors, ref int oppositeNeighbors)
    {
        if (brightEdge)
        {
            if ((maxY - neighbor) <= InterlacedTextSupportThreshold)
                supportNeighbors++;
            if ((neighbor - minY) <= InterlacedTextOppositeThreshold)
                oppositeNeighbors++;
            return;
        }

        if ((neighbor - minY) <= InterlacedTextSupportThreshold)
            supportNeighbors++;
        if ((maxY - neighbor) <= InterlacedTextOppositeThreshold)
            oppositeNeighbors++;
    }

    private static int Luma(byte r, byte g, byte b)
        => ((77 * r) + (150 * g) + (29 * b)) >> 8;
}
