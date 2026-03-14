using System;

namespace KSNES.AudioProcessing;

public class APU : IAPU
{
    [NonSerialized]
    private readonly bool _tracePorts =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_APU_PORTS"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private int _tracePortsCount;
    [NonSerialized]
    private readonly int _tracePortsLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_APU_PORTS_LIMIT", 256);
    [NonSerialized]
    private readonly bool _traceSpcPortReads =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_APU_SPC_READS"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly byte[] _lastTracedSpcPortRead = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

    [NonSerialized]
    private readonly ISPC700 _spc;
    [NonSerialized]
    private readonly IDSP _dsp;

    [JsonIgnore] private readonly byte[] _bootRom =
    {
        0xcd, 0xef, 0xbd, 0xe8, 0x00, 0xc6, 0x1d, 0xd0, 0xfc, 0x8f, 0xaa, 0xf4, 0x8f, 0xbb, 0xf5, 0x78,
        0xcc, 0xf4, 0xd0, 0xfb, 0x2f, 0x19, 0xeb, 0xf4, 0xd0, 0xfc, 0x7e, 0xf4, 0xd0, 0x0b, 0xe4, 0xf5,
        0xcb, 0xf4, 0xd7, 0x00, 0xfc, 0xd0, 0xf3, 0xab, 0x01, 0x10, 0xef, 0x7e, 0xf4, 0x10, 0xeb, 0xba,
        0xf6, 0xda, 0x00, 0xba, 0xf4, 0xc4, 0xf4, 0xdd, 0x5d, 0xd0, 0xdb, 0x1f, 0x00, 0x00, 0xc0, 0xff
    };

    public byte[] RAM { get; private set; } = new byte[0x10000];

    public byte[] SpcWritePorts { get; private set; } = new byte[4];
    public byte[] SpcReadPorts { get; set; } = new byte[6];
    [NonSerialized]
    private byte[] _pendingMainCpuPorts = new byte[4];
    [NonSerialized]
    private bool[] _pendingMainCpuPortDirty = new bool[4];
    [NonSerialized]
    private byte _auxIo4;
    [NonSerialized]
    private byte _auxIo5;
    private byte _dspAdr;
    private bool _dspRomReadable = true;
    private bool _port01ResetThisCycle;
    private bool _port23ResetThisCycle;

    private int _cycles;

    private const int OutputFrequency = 32040;
    private const int TargetFrequency = 44100;
    private const int ResampleRingSize = 8192;
    private readonly float[] _resampleRingL = new float[ResampleRingSize];
    private readonly float[] _resampleRingR = new float[ResampleRingSize];
    private int _resampleRead;
    private int _resampleWrite;
    private int _resampleCount;
    private double _resamplePos;

    private int _timer1int;
    private int _timer1div;
    private int _timer1target;
    private byte _timer1counter;
    private bool _timer1enabled;
    private int _timer2int;
    private int _timer2div;
    private int _timer2target;
    private byte _timer2counter;
    private bool _timer2enabled;
    private int _timer3int;
    private int _timer3div;
    private int _timer3target;
    private byte _timer3counter;
    private bool _timer3enabled;

    public APU(ISPC700 spc, IDSP dsp)
    {
        _spc = spc;
        _dsp = dsp;
        Attach();
    }

    public ISPC700 Spc => _spc;
    public IDSP Dsp => _dsp;
    public void Attach()
    {
        _spc?.SetAPU(this);
        _dsp?.SetAPU(this);
    }

    public void Reset()
    {
        RAM = new byte[0x10000];
        SpcWritePorts = new byte[4];
        SpcReadPorts = new byte[6];
        _pendingMainCpuPorts = new byte[4];
        _pendingMainCpuPortDirty = new bool[4];
        _auxIo4 = 0;
        _auxIo5 = 0;
        _dspAdr = 0;
        _dspRomReadable = true;
        _port01ResetThisCycle = false;
        _port23ResetThisCycle = false;
        _spc.Reset();
        _dsp.Reset();
        _cycles = 0;
        _timer1int = 128;
        _timer1div = 0;
        _timer1target = 0;
        _timer1counter = 0;
        _timer1enabled = false;
        _timer2int = 128;
        _timer2div = 0;
        _timer2target = 0;
        _timer2counter = 0;
        _timer2enabled = false;
        _timer3int = 16;
        _timer3div = 0;
        _timer3target = 0;
        _timer3counter = 0;
        _timer3enabled = false;
        _resampleRead = 0;
        _resampleWrite = 0;
        _resampleCount = 0;
        _resamplePos = 0;
    }

    public void ResyncAfterLoad()
    {
        _pendingMainCpuPorts ??= new byte[4];
        _pendingMainCpuPortDirty ??= new bool[4];
        Array.Clear(_pendingMainCpuPorts, 0, _pendingMainCpuPorts.Length);
        Array.Clear(_pendingMainCpuPortDirty, 0, _pendingMainCpuPortDirty.Length);
        _auxIo4 = 0;
        _auxIo5 = 0;
        _port01ResetThisCycle = false;
        _port23ResetThisCycle = false;

        if (_timer1int <= 0 || _timer1int > 128)
            _timer1int = 128;
        if (_timer2int <= 0 || _timer2int > 128)
            _timer2int = 128;
        if (_timer3int <= 0 || _timer3int > 16)
            _timer3int = 16;

        if (_resampleRead < 0 || _resampleRead >= ResampleRingSize
            || _resampleWrite < 0 || _resampleWrite >= ResampleRingSize
            || _resampleCount < 0 || _resampleCount > ResampleRingSize)
        {
            _resampleRead = 0;
            _resampleWrite = 0;
            _resampleCount = 0;
            _resamplePos = 0;
            Array.Clear(_resampleRingL, 0, _resampleRingL.Length);
            Array.Clear(_resampleRingR, 0, _resampleRingR.Length);
        }

        _tracePortsCount = 0;
        Array.Fill(_lastTracedSpcPortRead, (byte)0xFF);
    }

    public void Cycle()
    {
        _port01ResetThisCycle = false;
        _port23ResetThisCycle = false;
        for (int i = 0; i < 4; i++)
        {
            if (_pendingMainCpuPortDirty[i])
            {
                SpcReadPorts[i] = _pendingMainCpuPorts[i];
                _pendingMainCpuPortDirty[i] = false;
                TracePort($"[APU-PORT-CPU-LATCH] port={i} val=0x{SpcReadPorts[i]:X2} cycle={_cycles}");
            }
        }
        _spc.Cycle();
        _timer1int--;
        if (_timer1int == 0)
        {
            _timer1int = 128;
            if (_timer1enabled)
            {
                _timer1div++;
                int divider = _timer1target == 0 ? 256 : _timer1target;
                if (_timer1div >= divider)
                {
                    _timer1div = 0;
                    _timer1counter = (byte)((_timer1counter + 1) & 0x0f);
                }
            }
        }
        _timer2int--;
        if (_timer2int == 0)
        {
            _timer2int = 128;
            if (_timer2enabled)
            {
                _timer2div++;
                int divider = _timer2target == 0 ? 256 : _timer2target;
                if (_timer2div >= divider)
                {
                    _timer2div = 0;
                    _timer2counter = (byte)((_timer2counter + 1) & 0x0f);
                }
            }
        }
        _timer3int--;
        if (_timer3int == 0)
        {
            _timer3int = 16;
            if (_timer3enabled)
            {
                _timer3div++;
                int divider = _timer3target == 0 ? 256 : _timer3target;
                if (_timer3div >= divider)
                {
                    _timer3div = 0;
                    _timer3counter = (byte)((_timer3counter + 1) & 0x0f);
                }
            }
        }
        _cycles++;
        if ((_cycles & 0x1f) == 0)
        {
            _dsp.Cycle();
            if (_dsp.SampleOffset > 0)
            {
                int sampleIndex = _dsp.SampleOffset - 1;
                AppendResampleSample(_dsp.SamplesL[sampleIndex], _dsp.SamplesR[sampleIndex]);
                _dsp.SampleOffset = 0;
            }
        }
    }

    public bool TryWriteMainCpuPort(int portIndex, byte value)
    {
        if ((portIndex <= 1 && _port01ResetThisCycle) ||
            (portIndex >= 2 && _port23ResetThisCycle))
        {
            TracePort($"[APU-PORT-CPU-DROP] port={portIndex} val=0x{value:X2} cycle={_cycles}");
            return false;
        }

        SpcReadPorts[portIndex] = value;
        TracePort($"[APU-PORT-CPU-WR] port={portIndex} val=0x{value:X2} cycle={_cycles}");
        return true;
    }

    public byte Read(int adr)
    {
        adr &= 0xffff;
        switch (adr)
        {
            case 0xf0:
            case 0xf1:
            case 0xfa:
            case 0xfb:
            case 0xfc:
                return 0;
            case 0xf2:
                return _dspAdr;
            case 0xf3:
                return _dsp.Read(_dspAdr & 0x7f);
            case 0xf4:
            case 0xf5:
            case 0xf6:
            case 0xf7:
                TraceSpcRead(adr - 0xf4, SpcReadPorts[adr - 0xf4]);
                return SpcReadPorts[adr - 0xf4];
            case 0xf8:
                return _auxIo4;
            case 0xf9:
                return _auxIo5;
            case 0xfd:
                byte val = _timer1counter;
                _timer1counter = 0;
                return val;
            case 0xfe:
                byte val2 = _timer2counter;
                _timer2counter = 0;
                return val2;
            case 0xff:
                byte val3 = _timer3counter;
                _timer3counter = 0;
                return val3;
        }
        if (adr >= 0xffc0 && _dspRomReadable)
        {
            return _bootRom[adr & 0x3f];
        }
        return RAM[adr];
    }

    public void Write(int adr, byte value)
    {
        adr &= 0xffff;
        switch (adr)
        {
            case 0xf0:
                break;
            case 0xf1:
                TracePort($"[APU-F1-WR] val=0x{value:X2} cycle={_cycles}");
                bool newTimer1Enabled = (value & 0x01) > 0;
                bool newTimer2Enabled = (value & 0x02) > 0;
                bool newTimer3Enabled = (value & 0x04) > 0;

                if (!newTimer1Enabled)
                {
                    _timer1div = 0;
                    _timer1counter = 0;
                }

                if (!newTimer2Enabled)
                {
                    _timer2div = 0;
                    _timer2counter = 0;
                }

                if (!newTimer3Enabled)
                {
                    _timer3div = 0;
                    _timer3counter = 0;
                }

                _timer1enabled = newTimer1Enabled;
                _timer2enabled = newTimer2Enabled;
                _timer3enabled = newTimer3Enabled;
                _dspRomReadable = (value & 0x80) > 0;
                if ((value & 0x10) > 0)
                {
                    SpcReadPorts[0] = 0;
                    SpcReadPorts[1] = 0;
                    _port01ResetThisCycle = true;
                    TracePort($"[APU-PORT-SPC-RST] group=01 cycle={_cycles}");
                }

                if ((value & 0x20) > 0)
                {
                    SpcReadPorts[2] = 0;
                    SpcReadPorts[3] = 0;
                    _port23ResetThisCycle = true;
                    TracePort($"[APU-PORT-SPC-RST] group=23 cycle={_cycles}");
                }
                break;
            case 0xf2:
                _dspAdr = value;
                break;
            case 0xf3:
                if (_dspAdr < 0x80)
                {
                    _dsp.Write(_dspAdr, value);
                }
                break;
            case 0xf4:
            case 0xf5:
            case 0xf6:
            case 0xf7:
                SpcWritePorts[adr - 0xf4] = value;
                TracePort($"[APU-PORT-SPC-WR] port={adr - 0xf4} val=0x{value:X2} cycle={_cycles}");
                break;
            case 0xf8:
                _auxIo4 = value;
                break;
            case 0xf9:
                _auxIo5 = value;
                break;
            case 0xfa:
                _timer1target = value;
                break;
            case 0xfb:
                _timer2target = value;
                break;
            case 0xfc:
                _timer3target = value;
                break;
        }
        RAM[adr] = value;
    }

    public void SetSamples(float[] left, float[] right)
    {
        int outCount = Math.Min(left.Length, right.Length);
        if (outCount <= 0)
            return;

        double step = OutputFrequency / (double)TargetFrequency;
        for (int i = 0; i < outCount; i++)
        {
            if (_resampleCount <= 1)
            {
                left[i] = 0f;
                right[i] = 0f;
                continue;
            }

            int idx = (int)_resamplePos;
            if (idx + 1 >= _resampleCount)
                idx = _resampleCount - 2;
            double frac = _resamplePos - idx;

            float l0 = PeekResample(_resampleRingL, idx);
            float l1 = PeekResample(_resampleRingL, idx + 1);
            float r0 = PeekResample(_resampleRingR, idx);
            float r1 = PeekResample(_resampleRingR, idx + 1);

            left[i] = (float)(l0 + (l1 - l0) * frac);
            right[i] = (float)(r0 + (r1 - r0) * frac);

            _resamplePos += step;
            int advance = (int)_resamplePos;
            if (advance > 0)
            {
                _resamplePos -= advance;
                AdvanceResampleRead(advance);
            }
        }
    }

    private void AppendResampleSample(float left, float right)
    {
        if (_resampleCount == ResampleRingSize)
            AdvanceResampleRead(1);

        _resampleRingL[_resampleWrite] = left;
        _resampleRingR[_resampleWrite] = right;
        _resampleWrite = (_resampleWrite + 1) % ResampleRingSize;
        _resampleCount = Math.Min(ResampleRingSize, _resampleCount + 1);
    }

    private float PeekResample(float[] ring, int index)
    {
        int pos = _resampleRead + index;
        if (pos >= ResampleRingSize)
            pos -= ResampleRingSize;
        return ring[pos];
    }

    private void AdvanceResampleRead(int count)
    {
        if (count <= 0 || _resampleCount == 0)
            return;
        if (count >= _resampleCount)
        {
            _resampleRead = _resampleWrite;
            _resampleCount = 0;
            _resamplePos = 0;
            return;
        }
        _resampleRead = (_resampleRead + count) % ResampleRingSize;
        _resampleCount -= count;
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(envName), out int limit) && limit > 0
            ? limit
            : defaultValue;
    }

    private void TracePort(string message)
    {
        if (!_tracePorts || _tracePortsCount >= _tracePortsLimit)
            return;

        _tracePortsCount++;
        Console.WriteLine(message);
    }

    private void TraceSpcRead(int port, byte value)
    {
        if (!_traceSpcPortReads)
            return;

        if (_lastTracedSpcPortRead[port] == value)
            return;

        _lastTracedSpcPortRead[port] = value;
        TracePort($"[APU-PORT-SPC-RD] port={port} val=0x{value:X2} cycle={_cycles}");
    }
}
