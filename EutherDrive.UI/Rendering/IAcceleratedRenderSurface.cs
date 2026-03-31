using System;

namespace EutherDrive.Rendering;

public interface IAcceleratedRenderSurface : IGameRenderSurface
{
    bool ShouldFallbackToBitmap(out string reason);
    bool TryGetDebugSummary(out string summary);
    void SetInterlaceBlend(bool enabled, int fieldParity);
}
