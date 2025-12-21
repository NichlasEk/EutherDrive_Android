using System;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core;

/// <summary>
/// Steg 1: “riktig” core-koppling utan DMA/CPU.
/// Bara framebuffer + input via headless VDP-test.
/// </summary>
public sealed class MdTracerStage1Core : IEmulatorCore
{
    private readonly MdVdpHeadlessTest _vdp = new();
    private bool _inited;

    public void LoadRom(string path)
    {
        // Steg 1: ROM ignoreras – bara resetta för nu
        Reset();
    }

    public void Reset()
    {
        _vdp.Reset(320, 224);
        _inited = true;
    }

    public void RunFrame()
    {
        if (!_inited) Reset();
        _vdp.RunFrame();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = _vdp.Width;
        height = _vdp.Height;
        stride = _vdp.Stride;
        return _vdp.GetFrame();
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 1;
        return ReadOnlySpan<short>.Empty;
    }

    public void SetInputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start)
    {
        _vdp.SetInput(up, down, left, right, a, b, c, start);
    }
}
