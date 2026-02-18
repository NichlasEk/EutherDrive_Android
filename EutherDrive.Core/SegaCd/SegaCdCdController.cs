using System;
using System.Collections.Generic;

namespace EutherDrive.Core.SegaCd;

internal sealed class SegaCdCdController
{
    private const ulong SegaCdMclkFrequency = 50_000_000;
    private const ulong CdDaFrequency = 44_100;

    private readonly SegaCdCddStub _cdd;
    private readonly SegaCdCdcStub _cdc;

    private ulong _driveCycleProduct;
    private readonly List<short> _audioBuffer = new();

    public SegaCdCdController(SegaCdCddStub cdd, SegaCdCdcStub cdc)
    {
        _cdd = cdd;
        _cdc = cdc;
    }

    public void SetDisc(CdRom? disc)
    {
        _cdd.SetDisc(disc);
    }

    public void Reset()
    {
        _cdd.Reset();
        _cdc.Reset();
        _driveCycleProduct = 0;
        _audioBuffer.Clear();
    }

    public void Tick(ulong mclkCycles, WordRam wordRam, byte[] prgRam, bool prgRamAccessible, SegaCdPcmStub pcm)
    {
        _driveCycleProduct += mclkCycles * CdDaFrequency;
        while (_driveCycleProduct >= SegaCdMclkFrequency)
        {
            _driveCycleProduct -= SegaCdMclkFrequency;
            _cdd.Clock44100Hz(_cdc, wordRam, prgRam, prgRamAccessible, pcm);
            var sample = _cdd.LastAudioSample;
            _audioBuffer.Add(ToSample(sample.Left));
            _audioBuffer.Add(ToSample(sample.Right));
        }
    }

    public short[] ConsumeAudioBuffer()
    {
        if (_audioBuffer.Count == 0)
            return Array.Empty<short>();

        short[] data = _audioBuffer.ToArray();
        _audioBuffer.Clear();
        return data;
    }

    private static short ToSample(double value)
    {
        if (value > 1.0) value = 1.0;
        if (value < -1.0) value = -1.0;
        return (short)Math.Round(value * short.MaxValue);
    }
}
