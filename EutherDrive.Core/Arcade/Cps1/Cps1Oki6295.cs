using System;
using System.IO;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Arcade.Cps1;

// OKIM6295/MSM6295 ADPCM core translated from MAME's BSD-3-Clause OKI devices
// by Mirko Buffoni and Aaron Giles.
internal sealed class Cps1Oki6295
{
    private const int VoiceCount = 4;
    private const int DefaultClockHz = 16_000_000 / 4 / 4;
    private static readonly float[] VolumeTable =
    {
        0x20 / 32.0f,
        0x16 / 32.0f,
        0x10 / 32.0f,
        0x0b / 32.0f,
        0x08 / 32.0f,
        0x06 / 32.0f,
        0x04 / 32.0f,
        0x03 / 32.0f,
        0x02 / 32.0f,
        0.0f,
        0.0f,
        0.0f,
        0.0f,
        0.0f,
        0.0f,
        0.0f
    };

    private readonly OkiVoice[] _voices =
    {
        new(),
        new(),
        new(),
        new()
    };

    [NonSerialized]
    private byte[] _rom = Array.Empty<byte>();
    private int _pendingCommand = -1;
    private int _clockHz = DefaultClockHz;
    private bool _pin7High = true;
    private double _sourcePhase;
    private short _lastSourceSample;
    private short _nextSourceSample;

    public void Load(byte[] rom)
    {
        _rom = rom ?? Array.Empty<byte>();
        Reset();
    }

    public void ReplaceRom(byte[] rom)
    {
        _rom = rom ?? Array.Empty<byte>();
    }

    public void Reset()
    {
        _pendingCommand = -1;
        _sourcePhase = 0.0;
        _lastSourceSample = 0;
        _nextSourceSample = 0;
        foreach (OkiVoice voice in _voices)
            voice.Reset();
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
    }

    public void SetPin7(bool high)
    {
        if (_pin7High == high)
            return;

        _pin7High = high;
        _sourcePhase = 0.0;
    }

    public void SetClock(int clockHz)
    {
        clockHz = Math.Max(1, clockHz);
        if (_clockHz == clockHz)
            return;

        _clockHz = clockHz;
        _sourcePhase = 0.0;
    }

    public byte ReadStatus()
    {
        byte result = 0xf0;
        for (int i = 0; i < _voices.Length; i++)
        {
            if (_voices[i].Playing)
                result |= (byte)(1 << i);
        }

        return result;
    }

    public void Write(byte command)
    {
        if (_pendingCommand >= 0)
        {
            int voiceMask = command >> 4;
            for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++, voiceMask >>= 1)
            {
                if ((voiceMask & 1) == 0)
                    continue;

                OkiVoice voice = _voices[voiceIndex];
                if (voice.Playing)
                    continue;

                int baseOffset = _pendingCommand * 8;
                int start = ((ReadRomByte(baseOffset) << 16) | (ReadRomByte(baseOffset + 1) << 8) | ReadRomByte(baseOffset + 2)) & 0x3ffff;
                int stop = ((ReadRomByte(baseOffset + 3) << 16) | (ReadRomByte(baseOffset + 4) << 8) | ReadRomByte(baseOffset + 5)) & 0x3ffff;
                if (start < stop)
                    voice.Start(start, 2 * (stop - start + 1), VolumeTable[command & 0x0f]);
            }

            _pendingCommand = -1;
            return;
        }

        if ((command & 0x80) != 0)
        {
            _pendingCommand = command & 0x7f;
            return;
        }

        int stopMask = command >> 3;
        for (int voiceIndex = 0; voiceIndex < VoiceCount; voiceIndex++, stopMask >>= 1)
        {
            if ((stopMask & 1) != 0)
                _voices[voiceIndex].Stop();
        }
    }

    public void RenderStereo(short[] destination, ref int sampleFrameIndex, int targetSampleFrames, float gain = 0.30f, int outputSampleRate = 44_100)
    {
        if (destination.Length == 0)
            return;

        int maxFrames = destination.Length / 2;
        targetSampleFrames = Math.Clamp(targetSampleFrames, sampleFrameIndex, maxFrames);
        if (targetSampleFrames <= sampleFrameIndex)
            return;

        double phaseStep = GetSampleRate() / (double)outputSampleRate;
        while (sampleFrameIndex < targetSampleFrames)
        {
            _sourcePhase += phaseStep;
            while (_sourcePhase >= 1.0)
            {
                _sourcePhase -= 1.0;
                _lastSourceSample = _nextSourceSample;
                _nextSourceSample = GenerateSourceSample();
            }

            double interp = _lastSourceSample + (_nextSourceSample - _lastSourceSample) * _sourcePhase;
            int sample = (int)Math.Round(interp * gain);
            int offset = sampleFrameIndex * 2;
            destination[offset] = Mix(destination[offset], sample);
            destination[offset + 1] = Mix(destination[offset + 1], sample);
            sampleFrameIndex++;
        }
    }

    private int GetSampleRate()
        => _clockHz / (_pin7High ? 132 : 165);

    private short GenerateSourceSample()
    {
        float mixed = 0.0f;
        foreach (OkiVoice voice in _voices)
            mixed += voice.Generate(_rom);

        int pcm = (int)MathF.Round(mixed * 16.0f);
        return (short)Math.Clamp(pcm, short.MinValue, short.MaxValue);
    }

    private byte ReadRomByte(int address)
        => (uint)address < (uint)_rom.Length ? _rom[address] : (byte)0xff;

    private static short Mix(short current, int add)
        => (short)Math.Clamp(current + add, short.MinValue, short.MaxValue);

    private sealed class OkiVoice
    {
        private readonly OkiAdpcmState _adpcm = new();
        private int _baseOffset;
        private int _sample;
        private int _count;
        private float _volume;

        public bool Playing { get; private set; }

        public void Reset()
        {
            Playing = false;
            _baseOffset = 0;
            _sample = 0;
            _count = 0;
            _volume = 0.0f;
            _adpcm.Reset();
        }

        public void Start(int baseOffset, int count, float volume)
        {
            Playing = true;
            _baseOffset = baseOffset;
            _sample = 0;
            _count = count;
            _volume = volume;
            _adpcm.Reset();
        }

        public void Stop()
            => Playing = false;

        public float Generate(byte[] rom)
        {
            if (!Playing)
                return 0.0f;

            int address = _baseOffset + (_sample / 2);
            byte encoded = (uint)address < (uint)rom.Length ? rom[address] : (byte)0xff;
            int shift = ((_sample & 1) << 2) ^ 4;
            int nibble = (encoded >> shift) & 0x0f;
            short signal = _adpcm.Clock(nibble);

            _sample++;
            if (_sample >= _count)
                Playing = false;

            return signal * _volume;
        }
    }

    private sealed class OkiAdpcmState
    {
        private static readonly int[] IndexShift = { -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] DiffLookup = BuildDiffLookup();

        private int _signal;
        private int _step;

        public void Reset()
        {
            _signal = 0;
            _step = 0;
        }

        public short Clock(int nibble)
        {
            nibble &= 0x0f;
            _signal += DiffLookup[_step * 16 + nibble];
            _signal = Math.Clamp(_signal, -2048, 2047);

            _step += IndexShift[nibble & 7];
            _step = Math.Clamp(_step, 0, 48);
            return (short)_signal;
        }

        private static int[] BuildDiffLookup()
        {
            int[,] nibbleBits =
            {
                { 1, 0, 0, 0 }, { 1, 0, 0, 1 }, { 1, 0, 1, 0 }, { 1, 0, 1, 1 },
                { 1, 1, 0, 0 }, { 1, 1, 0, 1 }, { 1, 1, 1, 0 }, { 1, 1, 1, 1 },
                { -1, 0, 0, 0 }, { -1, 0, 0, 1 }, { -1, 0, 1, 0 }, { -1, 0, 1, 1 },
                { -1, 1, 0, 0 }, { -1, 1, 0, 1 }, { -1, 1, 1, 0 }, { -1, 1, 1, 1 }
            };

            int[] table = new int[49 * 16];
            for (int step = 0; step <= 48; step++)
            {
                int stepValue = (int)Math.Floor(16.0 * Math.Pow(11.0 / 10.0, step));
                for (int nibble = 0; nibble < 16; nibble++)
                {
                    table[step * 16 + nibble] = nibbleBits[nibble, 0]
                        * (stepValue * nibbleBits[nibble, 1]
                            + stepValue / 2 * nibbleBits[nibble, 2]
                            + stepValue / 4 * nibbleBits[nibble, 3]
                            + stepValue / 8);
                }
            }

            return table;
        }
    }
}
