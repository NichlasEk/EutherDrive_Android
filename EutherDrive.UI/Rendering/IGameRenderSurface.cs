using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace EutherDrive.Rendering;

public interface IGameRenderSurface
{
    Control View { get; }
    int PixelWidth { get; }
    int PixelHeight { get; }

    bool EnsureSize(int width, int height);
    FrameBlitMetrics Present(ReadOnlySpan<byte> source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf);
    void Reset();
}
