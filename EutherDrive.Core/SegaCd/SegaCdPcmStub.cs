using System;
using System.Collections.Generic;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdPcmStub
{
    // Ricoh RF5C164 PCM sound chip (port from jgenesis)
    private const int Rf5c164Divider = 384; // sub-CPU cycles per PCM sample
    private const int AddressFractBits = 11;
    private const int AddressFractMask = (1 << AddressFractBits) - 1;
    private const int WaveformRamLen = 64 * 1024;
    private const int WaveformAddressMask = WaveformRamLen - 1;
    private const int AddressFixedPointMask = (1 << (16 + AddressFractBits)) - 1;

    private readonly byte[] _waveformRam = new byte[WaveformRamLen];
    private readonly Channel[] _channels = new Channel[8];
    private byte _waveformRamBank;
    private byte _selectedChannel;
    private int _divider = Rf5c164Divider;
    private bool _enabled;

    private readonly List<short> _audioBuffer = new();
    private static readonly bool TracePcmReg =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_PCM_REG"), "1", StringComparison.Ordinal);
    private static readonly bool TracePcmDma =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_PCM_DMA"), "1", StringComparison.Ordinal);
    private long _traceLastTicks;
    private int _dmaWriteCount;

    private enum PcmInterpolation
    {
        None,
        Linear
    }

    private PcmInterpolation _interpolation = PcmInterpolation.Linear;

    private sealed class InterpolationBuffer
    {
        private readonly sbyte[] _buffer = new sbyte[6];

        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public void Push(sbyte sample)
        {
            for (int i = 0; i < 5; i++)
                _buffer[i] = _buffer[i + 1];
            _buffer[5] = sample;
        }

        public double Sample(PcmInterpolation interpolation, uint currentAddress)
        {
            return interpolation switch
            {
                PcmInterpolation.None => _buffer[5],
                PcmInterpolation.Linear => InterpolateLinear(_buffer[4], _buffer[5], InterpolationX(currentAddress)),
                _ => _buffer[5]
            };
        }

        private static double InterpolationX(uint address)
        {
            return (address & AddressFractMask) / (double)(1 << AddressFractBits);
        }

        private static double InterpolateLinear(sbyte y0, sbyte y1, double x)
        {
            return y0 * (1.0 - x) + y1 * x;
        }
    }

    private sealed class Channel
    {
        public bool Enabled;
        public ushort StartAddress;
        public ushort LoopAddress;
        public byte MasterVolume;
        public byte LeftVolume;
        public byte RightVolume;
        public uint CurrentAddress;
        public ushort AddressIncrement;
        public readonly InterpolationBuffer Interp = new();

        public void Enable(byte[] waveformRam)
        {
            if (Enabled)
                return;

            CurrentAddress = (uint)StartAddress << AddressFractBits;
            Interp.Clear();
            Enabled = true;

            byte firstSample = waveformRam[StartAddress];
            if (firstSample != 0xFF)
                Interp.Push(SignMagnitudeToPcm(firstSample));
        }

        public void Disable()
        {
            Enabled = false;
        }

        public void Clock(byte[] waveformRam)
        {
            if (!Enabled)
                return;

            uint increment = AddressIncrement;
            uint incremented = CurrentAddress + increment;

            uint address = CurrentAddress >> AddressFractBits;
            uint steps = (incremented >> AddressFractBits) - address;
            if (steps == 0)
            {
                CurrentAddress = (incremented & (uint)AddressFixedPointMask);
                return;
            }

            for (uint i = 0; i < steps - 1; i++)
            {
                address = (address + 1) & (uint)WaveformAddressMask;
                byte sample = waveformRam[address];
                if (sample == 0xFF)
                    continue;
                Interp.Push(SignMagnitudeToPcm(sample));
            }

            address = (address + 1) & (uint)WaveformAddressMask;
            byte lastSample = waveformRam[address];
            if (lastSample == 0xFF)
            {
                address = LoopAddress;
                byte loopSample = waveformRam[LoopAddress];
                if (loopSample == 0xFF)
                    Interp.Push(0);
                else
                    Interp.Push(SignMagnitudeToPcm(loopSample));
            }
            else
            {
                Interp.Push(SignMagnitudeToPcm(lastSample));
            }

            uint newAddrInt = address & (uint)WaveformAddressMask;
            uint newAddrFract = incremented & AddressFractMask;
            CurrentAddress = (newAddrInt << AddressFractBits) | newAddrFract;
        }

        public (int Left, int Right) Sample(PcmInterpolation interpolation)
        {
            if (!Enabled)
                return (0, 0);

            double sample = Interp.Sample(interpolation, CurrentAddress);
            int sign = Math.Sign(sample);
            double magnitude = Math.Abs(sample);

            double amplified = magnitude * MasterVolume;
            double panL = amplified * LeftVolume;
            double panR = amplified * RightVolume;

            int outputL = sign * ((int)Math.Round(panL) >> 5);
            int outputR = sign * ((int)Math.Round(panR) >> 5);
            return (outputL, outputR);
        }
    }

    public SegaCdPcmStub()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new Channel();
    }

    public byte Read(uint address)
    {
        address &= 0x1FFF;
        if (address is 0x0008)
            return ReadChannelOnRegister();
        if (address is >= 0x0010 and <= 0x001F)
            return ReadChannelAddress(address);
        if (address is >= 0x1000 and <= 0x1FFF)
        {
            if (!_enabled)
            {
                uint ramAddr = ((uint)_waveformRamBank << 12) | (address & 0x0FFF);
                return _waveformRam[ramAddr];
            }
            return 0x00;
        }
        return 0x00;
    }

    public void Write(uint address, byte value)
    {
        address &= 0x1FFF;
        if (address <= 0x0008)
        {
            WriteRegister(address, value);
            return;
        }
        if (address is >= 0x1000 and <= 0x1FFF)
        {
            uint ramAddr = ((uint)_waveformRamBank << 12) | (address & 0x0FFF);
            _waveformRam[ramAddr] = value;
        }
    }

    public void DmaWrite(uint address, byte value)
    {
        uint ramAddr = ((uint)_waveformRamBank << 12) | (address & 0x0FFF);
        _waveformRam[ramAddr] = value;
        if (TracePcmDma)
            _dmaWriteCount++;
    }

    public void Tick(uint subCpuCycles)
    {
        int cycles = (int)subCpuCycles;
        while (cycles >= _divider)
        {
            cycles -= _divider;
            _divider = Rf5c164Divider;

            if (_enabled)
                Clock();

            var (l, r) = Sample();
            _audioBuffer.Add(ToSample(l));
            _audioBuffer.Add(ToSample(r));
        }
        _divider -= cycles;

        if (TracePcmDma)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _traceLastTicks >= System.Diagnostics.Stopwatch.Frequency)
            {
                _traceLastTicks = now;
                Console.Error.WriteLine($"[SCD-PCM] dmaWrites={_dmaWriteCount} bank={_waveformRamBank} enabled={(_enabled ? 1 : 0)}");
                _dmaWriteCount = 0;
            }
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

    public void Disable()
    {
        _enabled = false;
    }

    private void Clock()
    {
        foreach (var channel in _channels)
            channel.Clock(_waveformRam);
    }

    private (double Left, double Right) Sample()
    {
        if (!_enabled)
            return (0.0, 0.0);

        int sumL = 0;
        int sumR = 0;
        for (int i = 0; i < _channels.Length; i++)
        {
            var (l, r) = _channels[i].Sample(_interpolation);
            sumL += l;
            sumR += r;
        }

        sumL = Math.Clamp(sumL, short.MinValue, short.MaxValue);
        sumR = Math.Clamp(sumR, short.MinValue, short.MaxValue);
        double outL = sumL / -(double)short.MinValue;
        double outR = sumR / -(double)short.MinValue;
        return (outL, outR);
    }

    private byte ReadChannelOnRegister()
    {
        byte value = 0;
        for (int i = 0; i < _channels.Length; i++)
            if (_channels[i].Enabled)
                value |= (byte)(1 << i);
        return value;
    }

    private byte ReadChannelAddress(uint address)
    {
        int channelIdx = (int)((address & 0xF) >> 1);
        Channel channel = _channels[channelIdx];
        ushort channelAddress = channel.Enabled
            ? (ushort)(channel.CurrentAddress >> AddressFractBits)
            : channel.StartAddress;
        return (address & 1) != 0 ? (byte)(channelAddress >> 8) : (byte)(channelAddress & 0xFF);
    }

    private void WriteRegister(uint address, byte value)
    {
        Channel channel = _channels[_selectedChannel];
        switch (address)
        {
            case 0x0000:
                channel.MasterVolume = value;
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ch={_selectedChannel} master=0x{value:X2}");
                break;
            case 0x0001:
                channel.LeftVolume = (byte)(value & 0x0F);
                channel.RightVolume = (byte)(value >> 4);
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ch={_selectedChannel} pan L=0x{channel.LeftVolume:X2} R=0x{channel.RightVolume:X2}");
                break;
            case 0x0002:
                channel.AddressIncrement = SetLsb(channel.AddressIncrement, value);
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ch={_selectedChannel} inc=0x{channel.AddressIncrement:X4}");
                break;
            case 0x0003:
                channel.AddressIncrement = SetMsb(channel.AddressIncrement, value);
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ch={_selectedChannel} inc=0x{channel.AddressIncrement:X4}");
                break;
            case 0x0004:
                channel.LoopAddress = SetLsb(channel.LoopAddress, value);
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ch={_selectedChannel} loop=0x{channel.LoopAddress:X4}");
                break;
            case 0x0005:
                channel.LoopAddress = SetMsb(channel.LoopAddress, value);
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ch={_selectedChannel} loop=0x{channel.LoopAddress:X4}");
                break;
            case 0x0006:
                channel.StartAddress = (ushort)(value << 8);
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ch={_selectedChannel} start=0x{channel.StartAddress:X4}");
                break;
            case 0x0007:
                _enabled = (value & 0x80) != 0;
                if ((value & 0x40) != 0)
                    _selectedChannel = (byte)(value & 0x07);
                else
                    _waveformRamBank = (byte)(value & 0x0F);
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] ctrl=0x{value:X2} enabled={(_enabled ? 1 : 0)} sel={_selectedChannel} bank=0x{_waveformRamBank:X2}");
                break;
            case 0x0008:
                for (int i = 0; i < _channels.Length; i++)
                {
                    if (((value >> i) & 1) != 0)
                        _channels[i].Disable();
                    else
                        _channels[i].Enable(_waveformRam);
                }
                if (TracePcmReg)
                    Console.Error.WriteLine($"[SCD-PCM] chOn=0x{value:X2}");
                break;
        }
    }

    private static short ToSample(double value)
    {
        if (value > 1.0) value = 1.0;
        if (value < -1.0) value = -1.0;
        return (short)Math.Round(value * short.MaxValue);
    }

    private static sbyte SignMagnitudeToPcm(byte sample)
    {
        int magnitude = sample & 0x7F;
        return (sample & 0x80) != 0 ? (sbyte)magnitude : (sbyte)-magnitude;
    }

    private static ushort SetLsb(ushort value, byte lsb)
    {
        return (ushort)((value & 0xFF00) | lsb);
    }

    private static ushort SetMsb(ushort value, byte msb)
    {
        return (ushort)((value & 0x00FF) | (msb << 8));
    }
}
