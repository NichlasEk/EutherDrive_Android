using System;
using EutherDrive.Core.Cpu.Z80Emu;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.Arcade.System32;

// Sega System 32 sound map and RF5C68 behavior are translated from MAME's
// BSD-3-Clause Sega System 32/RF5C68 devices.
internal sealed class System32Sound : IOpcodeBusInterface
{
    private const int MasterClock = 32_215_900;
    private const int Z80Clock = MasterClock / 4;
    private const int Z80CyclesPerFrame = Z80Clock / 60;
    private const int OutputSampleRate = 44_100;
    private const int OutputFramesPerFrame = OutputSampleRate / 60;
    private const double YmTickRate = (MasterClock / 4.0) / 6.0;
    private const double RfSampleRate = (50_000_000.0 / 4.0) / 384.0;

    private readonly byte[] _sharedRam;
    private readonly byte[] _soundRam = new byte[0x1_0000];
    private readonly Z80 _cpu = new();
    private readonly Ym2612 _ym1 = new(new bool[6], quantizeOutput: true, emulateLadderEffect: true, Opn2BusyBehavior.Ym3438);
    private readonly Ym2612 _ym2 = new(new bool[6], quantizeOutput: true, emulateLadderEffect: true, Opn2BusyBehavior.Ym3438);
    private readonly Rf5C68 _rf5c68 = new();
    private byte[] _soundRom = Array.Empty<byte>();
    private int _soundBank;
    private readonly byte[] _soundIrqControl = { 0xff, 0xff, 0xff, 0xff };
    private byte _soundIrqInput;
    private byte _soundDummy;
    private bool _resetAsserted = true;
    private bool _trace;
    private double _ymTickAccumulator;
    private double _rfAccumulator;
    private double _rfPrevLeft;
    private double _rfPrevRight;
    private double _rfNextLeft;
    private double _rfNextRight;
    private double _ym1Left;
    private double _ym1Right;
    private double _ym2Left;
    private double _ym2Right;

    public System32Sound(byte[] sharedRam)
    {
        _sharedRam = sharedRam;
    }

    public void Load(System32RomSet roms)
    {
        _soundRom = roms.SoundCpu;
        _trace = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SYSTEM32_TRACE_SOUND"), "1", StringComparison.Ordinal);
        ResetSound();
    }

    public void ResetSound()
    {
        Array.Clear(_soundRam);
        _cpu.ApplyResetLine();
        _ym1.Reset();
        _ym2.Reset();
        _rf5c68.Reset();
        _soundBank = 0;
        Array.Fill(_soundIrqControl, (byte)0xff);
        _soundIrqInput = 0;
        _soundDummy = 0;
        _resetAsserted = true;
        _ymTickAccumulator = 0;
        _rfAccumulator = 0;
        _rfPrevLeft = _rfPrevRight = _rfNextLeft = _rfNextRight = 0;
        _ym1Left = _ym1Right = _ym2Left = _ym2Right = 0;
    }

    public void SetResetAsserted(bool asserted)
    {
        if (asserted == _resetAsserted)
            return;

        _resetAsserted = asserted;
        if (asserted)
            _cpu.ApplyResetLine();
    }

    public void SignalV60SoundIrq()
    {
        SignalSoundIrq(1);
    }

    public void RunFrame(short[] audioBuffer)
    {
        if (_soundRom.Length == 0)
        {
            Array.Clear(audioBuffer);
            return;
        }

        int cycles = 0;
        while (cycles < Z80CyclesPerFrame)
        {
            uint elapsed = _cpu.ExecuteInstruction(this);
            cycles += (int)elapsed;
        }

        RenderAudio(audioBuffer);
    }

    public byte ReadMemory(ushort address)
    {
        if (address <= 0x9fff)
            return ReadSoundRom(address);
        if (address is >= 0xa000 and <= 0xbfff)
            return ReadSoundRom((_soundBank * 0x2000) + (address - 0xa000));
        if (address is >= 0xc000 and <= 0xdfff)
            return _rf5c68.Read(address - 0xc000);
        if (address >= 0xe000)
            return _sharedRam[address - 0xe000];

        return 0xff;
    }

    public byte ReadOpcode(ushort address) => ReadMemory(address);

    public void WriteMemory(ushort address, byte value)
    {
        if (address is >= 0xc000 and <= 0xdfff)
        {
            _rf5c68.Write(address - 0xc000, value);
            return;
        }

        if (address >= 0xe000)
            _sharedRam[address - 0xe000] = value;
    }

    public byte ReadIo(ushort address)
    {
        byte port = (byte)address;
        if ((port & 0xf0) == 0x80)
            return _ym1.ReadRegister((ushort)(port & 0x03));
        if ((port & 0xf0) == 0x90)
            return _ym2.ReadRegister((ushort)(port & 0x03));
        if (port == 0xf1)
            return _soundDummy;

        return 0xff;
    }

    public void WriteIo(ushort address, byte value)
    {
        byte port = (byte)address;
        if ((port & 0xf0) == 0x80)
        {
            WriteYm(_ym1, port & 0x03, value);
            return;
        }
        if ((port & 0xf0) == 0x90)
        {
            WriteYm(_ym2, port & 0x03, value);
            return;
        }
        if ((port & 0xf0) == 0xa0)
        {
            _soundBank = (_soundBank & ~0x3f) | (value & 0x3f);
            return;
        }
        if ((port & 0xf0) == 0xb0)
        {
            _soundBank = (_soundBank & 0x3f) | ((value & 0x04) << 4) | ((value & 0x03) << 7);
            return;
        }
        if ((port & 0xf0) == 0xc0)
        {
            int offset = port & 0x0f;
            if ((offset & 1) != 0)
            {
                _soundIrqInput &= value;
            }
            if ((offset & 4) != 0)
            {
                // TODO: wire sound-to-main IRQ when a game needs it.
            }
            return;
        }
        if ((port & 0xf8) == 0xd0)
        {
            _soundIrqControl[port & 0x03] = value;
            return;
        }
        if (port == 0xf1)
            _soundDummy = value;
    }

    public InterruptLine Nmi() => InterruptLine.High;

    public InterruptLine Int()
    {
        byte effective = (byte)(_soundIrqInput & ~_soundIrqControl[3] & 0x07);
        return effective != 0 ? InterruptLine.Low : InterruptLine.High;
    }

    public bool BusReq() => false;

    public bool Reset() => _resetAsserted;

    private void SignalSoundIrq(int which)
    {
        for (int i = 0; i < 3; i++)
        {
            if (_soundIrqControl[i] == which)
                _soundIrqInput |= (byte)(1 << i);
        }
    }

    private byte ReadSoundRom(int offset)
    {
        return (uint)offset < _soundRom.Length ? _soundRom[offset] : (byte)0xff;
    }

    private static void WriteYm(Ym2612 ym, int port, byte value)
    {
        switch (port)
        {
            case 0:
                ym.WriteAddress1(value);
                break;
            case 1:
                ym.WriteData1(value);
                break;
            case 2:
                ym.WriteAddress2(value);
                break;
            case 3:
                ym.WriteData2(value);
                break;
        }
    }

    private void RenderAudio(short[] destination)
    {
        if (destination.Length != OutputFramesPerFrame * 2)
            Array.Resize(ref destination, OutputFramesPerFrame * 2);

        double ymTicksPerOutput = YmTickRate / OutputSampleRate;
        double rfSamplesPerOutput = RfSampleRate / OutputSampleRate;
        int write = 0;
        int peak = 0;
        for (int i = 0; i < OutputFramesPerFrame; i++)
        {
            _ymTickAccumulator += ymTicksPerOutput;
            int ymTicks = (int)_ymTickAccumulator;
            if (ymTicks > 0)
            {
                _ymTickAccumulator -= ymTicks;
                _ym1.Tick(ymTicks, (left, right) =>
                {
                    _ym1Left = left;
                    _ym1Right = right;
                });
                _ym2.Tick(ymTicks, (left, right) =>
                {
                    _ym2Left = left;
                    _ym2Right = right;
                });
            }

            _rfAccumulator += rfSamplesPerOutput;
            while (_rfAccumulator >= 1.0)
            {
                _rfPrevLeft = _rfNextLeft;
                _rfPrevRight = _rfNextRight;
                (_rfNextLeft, _rfNextRight) = _rf5c68.RenderSample();
                _rfAccumulator -= 1.0;
            }

            double rfLeft = Lerp(_rfPrevLeft, _rfNextLeft, _rfAccumulator);
            double rfRight = Lerp(_rfPrevRight, _rfNextRight, _rfAccumulator);
            double leftMix = ((_ym1Left + _ym2Left) * 0.30) + (rfLeft * 0.40);
            double rightMix = ((_ym1Right + _ym2Right) * 0.30) + (rfRight * 0.40);
            short left = ToSample(leftMix);
            short right = ToSample(rightMix);
            destination[write++] = left;
            destination[write++] = right;
            peak = Math.Max(peak, Math.Abs(left));
            peak = Math.Max(peak, Math.Abs(right));
        }

        if (_trace)
            Console.WriteLine($"[System32 Sound] pc=0x{_cpu.Pc:X4} bank={_soundBank} irqIn=0x{_soundIrqInput:X2} irqCtl={_soundIrqControl[0]:X2}/{_soundIrqControl[1]:X2}/{_soundIrqControl[2]:X2}/{_soundIrqControl[3]:X2} rfOn=0x{_rf5c68.DebugEnabledMask:X2} peak={peak}");
    }

    private static double Lerp(double previous, double next, double fraction)
        => previous + ((next - previous) * fraction);

    private static short ToSample(double value)
    {
        value = Math.Clamp(value, -1.0, 1.0);
        return (short)Math.Round(value * short.MaxValue);
    }

    private sealed class Rf5C68
    {
        private const int AddressFractionBits = 11;
        private readonly byte[] _ram = new byte[0x1_0000];
        private readonly Channel[] _channels =
        {
            new(), new(), new(), new(), new(), new(), new(), new()
        };
        private byte _currentBank;
        private ushort _writeBank;
        private bool _enabled;

        public int DebugEnabledMask
        {
            get
            {
                int mask = 0;
                for (int i = 0; i < _channels.Length; i++)
                    if (_channels[i].Enabled)
                        mask |= 1 << i;
                return mask;
            }
        }

        public void Reset()
        {
            Array.Clear(_ram);
            foreach (Channel channel in _channels)
                channel.Reset();
            _currentBank = 0;
            _writeBank = 0;
            _enabled = false;
        }

        public byte Read(int offset)
        {
            offset &= 0x1fff;
            if ((offset & 0x1000) != 0)
                return _ram[_writeBank | (offset & 0x0fff)];
            return 0xff;
        }

        public void Write(int offset, byte value)
        {
            offset &= 0x1fff;
            if ((offset & 0x1000) != 0)
            {
                _ram[_writeBank | (offset & 0x0fff)] = value;
                return;
            }

            int register = offset & 0x0f;
            if (register > 8)
                return;

            Channel channel = _channels[_currentBank & 7];
            switch (register)
            {
                case 0:
                    channel.Envelope = value;
                    break;
                case 1:
                    channel.Pan = value;
                    break;
                case 2:
                    channel.Step = (ushort)((channel.Step & 0xff00) | value);
                    break;
                case 3:
                    channel.Step = (ushort)((channel.Step & 0x00ff) | (value << 8));
                    break;
                case 4:
                    channel.LoopStart = (ushort)((channel.LoopStart & 0xff00) | value);
                    break;
                case 5:
                    channel.LoopStart = (ushort)((channel.LoopStart & 0x00ff) | (value << 8));
                    break;
                case 6:
                    channel.Start = value;
                    if (!channel.Enabled)
                        channel.Address = (uint)(channel.Start << (8 + AddressFractionBits));
                    break;
                case 7:
                    _enabled = (value & 0x80) != 0;
                    if ((value & 0x40) != 0)
                        _currentBank = (byte)(value & 7);
                    else
                        _writeBank = (ushort)((value & 0x0f) << 12);
                    break;
                case 8:
                    for (int i = 0; i < _channels.Length; i++)
                    {
                        bool enabled = ((~value >> i) & 1) != 0;
                        _channels[i].Enabled = enabled;
                        if (!enabled)
                            _channels[i].Address = (uint)(_channels[i].Start << (8 + AddressFractionBits));
                    }
                    break;
            }
        }

        public (double Left, double Right) RenderSample()
        {
            if (!_enabled)
                return (0, 0);

            int left = 0;
            int right = 0;
            foreach (Channel channel in _channels)
            {
                if (!channel.Enabled)
                    continue;

                int sample = _ram[(channel.Address >> AddressFractionBits) & 0xffff];
                if (sample == 0xff)
                {
                    channel.Address = (uint)(channel.LoopStart << AddressFractionBits);
                    sample = _ram[(channel.Address >> AddressFractionBits) & 0xffff];
                    if (sample == 0xff)
                        continue;
                }

                channel.Address += channel.Step;
                int lv = (channel.Pan & 0x0f) * channel.Envelope;
                int rv = ((channel.Pan >> 4) & 0x0f) * channel.Envelope;
                if ((sample & 0x80) != 0)
                {
                    sample &= 0x7f;
                    left += (sample * lv) >> 5;
                    right += (sample * rv) >> 5;
                }
                else
                {
                    left -= (sample * lv) >> 5;
                    right -= (sample * rv) >> 5;
                }
            }

            return (Math.Clamp(left, -32768, 32767) / 32768.0, Math.Clamp(right, -32768, 32767) / 32768.0);
        }

        private sealed class Channel
        {
            public bool Enabled;
            public byte Envelope;
            public byte Pan;
            public byte Start;
            public uint Address;
            public ushort Step;
            public ushort LoopStart;

            public void Reset()
            {
                Enabled = false;
                Envelope = 0;
                Pan = 0;
                Start = 0;
                Address = 0;
                Step = 0;
                LoopStart = 0;
            }
        }
    }
}
