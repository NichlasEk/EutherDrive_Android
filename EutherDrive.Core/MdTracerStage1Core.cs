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
        Console.WriteLine("[MdTracerStage1Core] Reset 320x224");
    }

    public void RunFrame()
    {
        if (!_inited) Reset();
        if (_vdp.Width <= 0 || _vdp.Height <= 0)
            _vdp.Reset(320, 224);
        if (_vdp.GetFrame().Length == 0)
            _vdp.Reset(320, 224);
        _vdp.RunFrame();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        if (!_inited) Reset();
        width = _vdp.Width;
        height = _vdp.Height;
        stride = _vdp.Stride;
        Console.WriteLine($"[MdTracerStage1Core] GetFrameBuffer {width}x{height} stride={stride}");
        return _vdp.GetFrame();
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 1;
        return ReadOnlySpan<short>.Empty;
    }

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        _vdp.SetInput(up, down, left, right, a, b, c, start, x, y, z, mode);
    }
}
