// license:BSD-3-Clause
// YMZ774 command, sequencer, and mixer behavior is based on MAME's
// src/devices/sound/ymz770.cpp and ymz770.h.

namespace EutherDrive.Core.Arcade.Igs;

internal sealed class Pgm2Ymz774
{
    private const int ChannelCount = 16;
    private const int SequenceCount = 8;
    private const int MaxBlockSamples = 1152;
    private const int DefaultSampleRate = 44_100;

    private readonly Channel[] _channels = new Channel[ChannelCount];
    private readonly Sequence[] _sequences = new Sequence[SequenceCount];
    private readonly Sqc[] _sqcs = new Sqc[SequenceCount];
    private readonly int[] _volinc = new int[256];

    private byte[] _rom = Array.Empty<byte>();
    private int _romBytes;
    private byte _curReg;
    private int _bank;
    private int _vlma;
    private int _vlma1;
    private int _clipLimit;
    private int _boostShift;

    public Pgm2Ymz774()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new Channel();
        for (int i = 0; i < _sequences.Length; i++)
            _sequences[i] = new Sequence();
        for (int i = 0; i < _sqcs.Length; i++)
            _sqcs[i] = new Sqc();

        for (int i = 1; i < _volinc.Length; i++)
            _volinc[i] = Math.Max(1, (128 << 17) / (i * 32));

        Reset();
    }

    public string DebugSummary => $"rom=0x{_romBytes:X} reg=0x{_curReg:X2} bank={_bank} play=0x{PlayingMask():X4}";

    public void LoadRom(byte[] rom, int romBytes)
    {
        _rom = rom;
        _romBytes = Math.Max(0, Math.Min(romBytes, rom.Length));
        Reset();
    }

    public void Reset()
    {
        _curReg = 0;
        _bank = 0;
        _vlma = 128;
        _vlma1 = 128;
        _clipLimit = 0;
        _boostShift = 0;

        foreach (Channel channel in _channels)
            channel.Reset(_rom);
        foreach (Sequence sequence in _sequences)
            sequence.Reset();
        foreach (Sqc sqc in _sqcs)
            sqc.Reset();
    }

    public byte Read(int offset)
    {
        if ((offset & 1) != 0 && (_curReg == 0xe3 || _curReg == 0xe4))
        {
            byte result = 0;
            int bank = _curReg == 0xe3 ? 8 : 0;
            for (int i = 0; i < 8; i++)
                if (_channels[i + bank].IsPlaying)
                    result |= (byte)(1 << i);
            return result;
        }

        return 0;
    }

    public void Write(int offset, byte value)
    {
        if ((offset & 1) == 0)
            _curReg = value;
        else
            InternalRegWrite(_curReg, value);
    }

    public void Render(short[] output)
    {
        Array.Clear(output);
        if (_romBytes <= 0)
            return;

        int samples = output.Length / 2;
        short[] decodeScratch = new short[MaxBlockSamples * 2];

        for (int i = 0; i < samples; i++)
        {
            Sequencer();

            int mixL = 0;
            int mixR = 0;
            foreach (Channel channel in _channels)
            {
                if (channel.OutputRemaining == 0 && channel.IsPlaying && !channel.IsPaused)
                    DecodeNextBlock(channel, decodeScratch);

                if (channel.OutputRemaining <= 0)
                    continue;

                int sample = channel.OutputData[channel.OutputPtr++];
                sample = (sample * (channel.Volume >> 17)) >> 7;
                sample = (sample * channel.Current.Volume2) >> 7;
                mixR += (sample * channel.Current.Pan) >> 7;
                mixL += (sample * (128 - channel.Current.Pan)) >> 7;
                channel.OutputRemaining--;
                if (channel.OutputRemaining == 0 && !channel.IsPlaying)
                    channel.Decoder.Clear();
            }

            mixR *= _vlma;
            mixL *= _vlma;
            int shift = 7 - _boostShift;
            if (shift >= 0)
            {
                mixR >>= shift;
                mixL >>= shift;
            }
            else
            {
                mixR <<= -shift;
                mixL <<= -shift;
            }

            ApplyClip(ref mixL, ref mixR);
            output[i * 2] = Clamp16(mixL);
            output[(i * 2) + 1] = Clamp16(mixR);
        }
    }

    private void DecodeNextBlock(Channel channel, short[] decodeScratch)
    {
        while (channel.OutputRemaining == 0 && channel.IsPlaying && !channel.IsPaused)
        {
            if (channel.LastBlock)
            {
                if (channel.Loop != 0)
                {
                    if (channel.Loop != 255)
                        channel.Loop--;
                    channel.Pending = true;
                }
                else
                {
                    channel.IsPlaying = false;
                    channel.OutputRemaining = 0;
                    channel.Decoder.Clear();
                    return;
                }
            }

            if (channel.Pending)
            {
                int phrase = channel.Phrase;
                if (phrase < 0 || (phrase * 4) >= _romBytes)
                {
                    channel.IsPlaying = false;
                    return;
                }

                channel.Atbl = (_rom[4 * phrase] >> 4) & 7;
                channel.PtrBits = 8 * GetPhraseOffset(phrase);
                if (!channel.LastBlock && channel.Latch.Volume2 != channel.Current.Volume2)
                    channel.Decoder.Clear();
                channel.Pending = false;
            }

            int outputSamples = 0;
            int sampleRate = DefaultSampleRate;
            int channels = 2;
            Array.Clear(decodeScratch);
            bool decoded = channel.Decoder.DecodeBuffer(
                ref channel.PtrBits,
                _romBytes * 8,
                decodeScratch,
                ref outputSamples,
                ref sampleRate,
                ref channels,
                channel.Atbl);
            if (!decoded || outputSamples == 0)
            {
                channel.IsPlaying = !channel.LastBlock;
                channel.LastBlock = true;
                channel.OutputRemaining = 0;
                continue;
            }

            MixDecodeBlock(channel, decodeScratch, outputSamples, channels);
            channel.LastBlock = outputSamples < MaxBlockSamples;
            channel.OutputPtr = 0;
            channel.ApplyParams();
        }
    }

    private void MixDecodeBlock(Channel channel, short[] source, int samples, int sourceChannels)
    {
        int count = Math.Min(samples, MaxBlockSamples);
        for (int i = 0; i < count; i++)
        {
            if (sourceChannels == 1)
                channel.OutputData[i] = source[i];
            else
                channel.OutputData[i] = (short)((source[i * 2] + source[(i * 2) + 1]) / 2);
        }

        channel.OutputRemaining = count;
    }

    private void InternalRegWrite(byte reg, byte data)
    {
        if (reg < 0x10)
        {
            int ch = ((reg >> 1) & 7) + (_bank * 8);
            if ((reg & 1) != 0)
                _channels[ch].Phrase = (_channels[ch].Phrase & 0xff00) | data;
            else
                _channels[ch].Phrase = (_channels[ch].Phrase & 0x00ff) | ((data & 7) << 8);
        }
        else if (reg < 0x60)
        {
            int ch = (reg & 7) + (_bank * 8);
            Channel channel = _channels[ch];
            switch (reg & 0xf8)
            {
                case 0x10:
                    channel.Latch.VolumeTarget = data;
                    break;
                case 0x18:
                    channel.Latch.VolumeDelay = data;
                    break;
                case 0x20:
                    channel.Latch.Volume2 = data;
                    break;
                case 0x28:
                    channel.Latch.Pan = data;
                    break;
                case 0x30:
                    channel.Latch.PanDelay = data;
                    break;
                case 0x38:
                    channel.Latch.Pan1 = data;
                    break;
                case 0x40:
                    channel.Latch.Pan1Delay = data;
                    break;
                case 0x48:
                    channel.Loop = data;
                    break;
                case 0x50:
                    if (data != 0)
                    {
                        channel.Pending = true;
                        channel.IsPlaying = true;
                        channel.IsPaused = false;
                        channel.LastBlock = false;
                    }
                    else
                    {
                        channel.IsPlaying = false;
                    }
                    break;
                case 0x58:
                    channel.IsPaused = data != 0;
                    break;
            }
        }
        else if (reg < 0xd0)
        {
            if (_bank == 0)
                WriteSequenceReg(reg, data);
        }
        else
        {
            switch (reg)
            {
                case 0xd0:
                    _vlma = data;
                    break;
                case 0xd1:
                    _vlma1 = data;
                    break;
                case 0xd2:
                    _clipLimit = data;
                    break;
                case 0xf0:
                    _bank = data & 1;
                    break;
            }
        }
    }

    private void WriteSequenceReg(int reg, byte data)
    {
        int sq = reg & 7;
        switch (reg & 0xf8)
        {
            case 0x60:
            case 0x68:
                sq = (reg >> 1) & 7;
                if ((reg & 1) != 0)
                    _sequences[sq].SequenceNumber = (_sequences[sq].SequenceNumber & 0xff00) | data;
                else
                    _sequences[sq].SequenceNumber = (_sequences[sq].SequenceNumber & 0x00ff) | ((data & 7) << 8);
                break;
            case 0x70:
                if (data != 0)
                {
                    _sequences[sq].Offset = GetSequenceOffset(_sequences[sq].SequenceNumber);
                    _sequences[sq].Delay = 0;
                    _sequences[sq].IsPlaying = true;
                    _sequences[sq].IsPaused = false;
                }
                else
                {
                    StopSequenceChannels(_sequences[sq]);
                    _sequences[sq].IsPlaying = false;
                }
                break;
            case 0x78:
                _sequences[sq].IsPaused = data != 0;
                break;
            case 0x80:
                _sequences[sq].Loop = data;
                break;
            case 0x88:
            case 0x90:
                sq = (reg - 0x88) >> 1;
                if ((reg & 1) != 0)
                    _sequences[sq].Timer = (_sequences[sq].Timer & 0xff00) | data;
                else
                    _sequences[sq].Timer = (_sequences[sq].Timer & 0x00ff) | (data << 8);
                break;
            case 0xa0:
            case 0xa8:
                sq = (reg >> 1) & 7;
                if ((reg & 1) != 0)
                    _sequences[sq].StopChannelMask = (_sequences[sq].StopChannelMask & 0xff00) | data;
                else
                    _sequences[sq].StopChannelMask = (_sequences[sq].StopChannelMask & 0x00ff) | (data << 8);
                break;
            case 0xb0:
                _sqcs[sq].SqcNumber = data;
                break;
            case 0xb8:
                if (data != 0)
                {
                    _sqcs[sq].Offset = GetSqcOffset(_sqcs[sq].SqcNumber);
                    _sqcs[sq].IsPlaying = true;
                    _sqcs[sq].IsWaiting = false;
                }
                else
                {
                    _sqcs[sq].IsPlaying = false;
                    StopSequenceChannels(_sequences[sq]);
                    _sequences[sq].IsPlaying = false;
                }
                break;
            case 0xc0:
                _sqcs[sq].Loop = data;
                break;
        }
    }

    private void Sequencer()
    {
        foreach (Channel channel in _channels)
        {
            if (!channel.IsPlaying || channel.IsPaused || (channel.Volume >> 17) == channel.Current.VolumeTarget)
                continue;

            if (channel.Current.VolumeDelay != 0)
            {
                if ((channel.Volume >> 17) < channel.Current.VolumeTarget)
                    channel.Volume += _volinc[channel.Current.VolumeDelay];
                else
                    channel.Volume -= _volinc[channel.Current.VolumeDelay];
            }
            else
            {
                channel.Volume = channel.Current.VolumeTarget << 17;
            }
        }

        for (int i = 0; i < SequenceCount; i++)
        {
            Sqc sqc = _sqcs[i];
            Sequence sequence = _sequences[i];

            if (sqc.IsPlaying && !sqc.IsWaiting)
            {
                sequence.SequenceNumber = ((ReadRomByte(sqc.Offset) << 8) | ReadRomByte(sqc.Offset + 1)) & 0x7ff;
                sqc.Offset += 2;
                sequence.Loop = ReadRomByte(sqc.Offset++);
                sequence.Offset = GetSequenceOffset(sequence.SequenceNumber);
                sequence.Delay = 0;
                sequence.IsPlaying = true;
                sequence.IsPaused = false;
                sqc.IsWaiting = true;
                if (ReadRomByte(sqc.Offset++) == 0xff)
                {
                    if (sqc.Loop != 0)
                    {
                        if (sqc.Loop != 255)
                            sqc.Loop--;
                        sqc.Offset = GetSqcOffset(sqc.SqcNumber);
                    }
                    else
                    {
                        sqc.IsPlaying = false;
                    }
                }
            }

            if (!sequence.IsPlaying || sequence.IsPaused)
                continue;

            if (sequence.Delay > 0)
            {
                sequence.Delay--;
                continue;
            }

            int reg = ReadRomByte(sequence.Offset++);
            byte data = ReadRomByte(sequence.Offset++);
            switch (reg)
            {
                case 0xff:
                    StopSequenceChannels(sequence);
                    if (sequence.Loop != 0)
                    {
                        if (sequence.Loop != 255)
                            sequence.Loop--;
                        sequence.Offset = GetSequenceOffset(sequence.SequenceNumber);
                    }
                    else
                    {
                        sequence.IsPlaying = false;
                        sqc.IsWaiting = false;
                    }
                    break;
                case 0xfe:
                    sequence.Delay = (sequence.Timer * 32) + 32 - 1;
                    break;
                case 0xf0:
                    sequence.Bank = data & 1;
                    break;
                default:
                {
                    int savedBank = _bank;
                    _bank = sequence.Bank;
                    if (_bank == 0 && reg >= 0x60 && reg < 0xb0)
                    {
                        int sqn = i;
                        if (reg < 0x70 || (reg >= 0x88 && reg < 0x98) || reg >= 0xa0)
                            sqn = i * 2;
                        InternalRegWrite((byte)(reg + sqn), data);
                    }
                    else
                    {
                        InternalRegWrite((byte)reg, data);
                    }
                    _bank = savedBank;
                    break;
                }
            }
        }
    }

    private void StopSequenceChannels(Sequence sequence)
    {
        if (!sequence.IsPlaying)
            return;

        for (int ch = 0; ch < ChannelCount; ch++)
            if ((sequence.StopChannelMask & (1 << ch)) != 0)
                _channels[ch].IsPlaying = false;
    }

    private int GetPhraseOffset(int phrase)
    {
        int ph = phrase * 4;
        if (ph + 3 >= _romBytes)
            return _romBytes;
        return (((_rom[ph] & 0x0f) << 24) | (_rom[ph + 1] << 16) | (_rom[ph + 2] << 8) | _rom[ph + 3]) * 2;
    }

    private int GetSequenceOffset(int sequence)
    {
        int sq = (sequence * 4) + 0x2000;
        if (sq + 3 >= _romBytes)
            return _romBytes;
        return (((_rom[sq] & 0x0f) << 24) | (_rom[sq + 1] << 16) | (_rom[sq + 2] << 8) | _rom[sq + 3]) * 2;
    }

    private int GetSqcOffset(int sqc)
    {
        int sq = (sqc * 4) + 0x6000;
        if (sq + 3 >= _romBytes)
            return _romBytes;
        return (((_rom[sq] & 0x0f) << 24) | (_rom[sq + 1] << 16) | (_rom[sq + 2] << 8) | _rom[sq + 3]) * 2;
    }

    private byte ReadRomByte(int offset)
    {
        if ((uint)offset >= (uint)_romBytes)
            return 0xff;
        return _rom[offset];
    }

    private int PlayingMask()
    {
        int mask = 0;
        for (int i = 0; i < _channels.Length; i++)
            if (_channels[i].IsPlaying)
                mask |= 1 << i;
        return mask;
    }

    private void ApplyClip(ref int left, ref int right)
    {
        switch (_clipLimit)
        {
            case 3:
                left = Math.Clamp(left, -32768 * 75 / 100, 32768 * 75 / 100);
                right = Math.Clamp(right, -32768 * 75 / 100, 32768 * 75 / 100);
                break;
            case 2:
                left = Math.Clamp(left, -32768 * 875 / 1000, 32768 * 875 / 1000);
                right = Math.Clamp(right, -32768 * 875 / 1000, 32768 * 875 / 1000);
                break;
            case 1:
                left = Math.Clamp(left, -32768, 32767);
                right = Math.Clamp(right, -32768, 32767);
                break;
        }
    }

    private static short Clamp16(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private sealed class Channel
    {
        public readonly short[] OutputData = new short[MaxBlockSamples];
        public Pgm2MpegAudio Decoder = new(Array.Empty<byte>(), Pgm2MpegAudio.AMM, false, 8);
        public ChannelParams Current;
        public ChannelParams Latch;
        public int Phrase;
        public int Volume;
        public int Loop;
        public int Atbl;
        public int PtrBits;
        public int OutputPtr;
        public int OutputRemaining;
        public bool Pending;
        public bool IsPlaying;
        public bool IsPaused;
        public bool LastBlock;

        public void Reset(byte[] rom)
        {
            Latch.Pan = 64;
            Latch.PanDelay = 0;
            Latch.Pan1 = 64;
            Latch.Pan1Delay = 0;
            Latch.VolumeTarget = 0;
            Latch.VolumeDelay = 0;
            Latch.Volume2 = 0;
            ApplyParams();
            Phrase = 0;
            Volume = 0;
            Loop = 0;
            Atbl = 0;
            PtrBits = 0;
            OutputPtr = 0;
            OutputRemaining = 0;
            Pending = false;
            IsPlaying = false;
            IsPaused = false;
            LastBlock = false;
            Decoder = new Pgm2MpegAudio(rom, Pgm2MpegAudio.AMM, false, 8);
        }

        public void ApplyParams()
        {
            Current = Latch;
        }
    }

    private struct ChannelParams
    {
        public int Pan;
        public int PanDelay;
        public int Pan1;
        public int Pan1Delay;
        public int VolumeTarget;
        public int VolumeDelay;
        public int Volume2;
    }

    private sealed class Sequence
    {
        public int Delay;
        public int SequenceNumber;
        public int Timer;
        public int StopChannelMask;
        public int Loop;
        public int Offset;
        public int Bank;
        public bool IsPlaying;
        public bool IsPaused;

        public void Reset()
        {
            Delay = 0;
            SequenceNumber = 0;
            Timer = 0;
            StopChannelMask = 0;
            Loop = 0;
            Offset = 0;
            Bank = 0;
            IsPlaying = false;
            IsPaused = false;
        }
    }

    private sealed class Sqc
    {
        public int SqcNumber;
        public int Loop;
        public int Offset;
        public bool IsPlaying;
        public bool IsWaiting;

        public void Reset()
        {
            SqcNumber = 0;
            Loop = 0;
            Offset = 0;
            IsPlaying = false;
            IsWaiting = false;
        }
    }
}
