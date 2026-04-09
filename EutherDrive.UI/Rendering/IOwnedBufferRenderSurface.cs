using System;

namespace EutherDrive.Rendering;

public interface IOwnedBufferRenderSurface
{
    FrameBlitMetrics PresentOwnedBuffer(byte[] source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf);
}
