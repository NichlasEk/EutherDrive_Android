using System;
using System.Linq;

namespace ePceCD
{
    [Serializable]
    public class APU //PSG , HuC6260
    {
        private const int MainClockHz = 7159090;
        public int m_SampleRate = 44100;
        public short m_BaseLine = 0;

        [Serializable]
        private class PSG_Channel
        {
            public int m_Frequency;
            public float m_RealFrequency;
            public bool m_Enabled, m_DDA, m_Noise;
            public byte m_Control;
            public byte m_Amplitude;
            public int m_Left_Volume, m_Right_Volume, m_Volume, m_DDA_Output, m_NoiseFrequency;
            public float m_RealNoiseFrequency, m_NoiseIndex, m_LeftOutput, m_RightOutput;
            public int[] m_Buffer = new int[32];
            public int m_BufferIndex;
            public float m_OutputIndex;
        }
        private int m_Left_Volume, m_Right_Volume;
        private float m_RealLFOFrequency;
        private int m_LFO_Frequency;
        private bool m_LFO_Enabled, m_LFO_Active;
        private int m_LFO_Shift;
        private PSG_Channel[] m_Channels = new PSG_Channel[8];
        [NonSerialized]
        private PSG_Channel m_Selected;
        private int m_SelectedIndex;
        private const int PsgChannelCount = 6;
        private const int NoiseBufferSize = 0x8000;
        private const int DcOffset = 16;
        private static int[] m_NoiseBuffer = new int[NoiseBufferSize];
        private static float[] m_VolumeTable = new float[32];
        [NonSerialized]
        private CDRom m_CDRom;

        public bool MixADPCM, MixFADE;
        private long m_ElapsedMainCycles;
        private long m_SampleAccumulator;
        [NonSerialized]
        private short[] _psgSampleQueue = new short[16384];
        [NonSerialized]
        private int _psgQueueRead;
        [NonSerialized]
        private int _psgQueueWrite;
        [NonSerialized]
        private short _lastRightSample;
        [NonSerialized]
        private short _lastLeftSample;

        //private short[] m_AudioBuffer = new short[1052];
        //private int m_BufferPos = 0;

        [NonSerialized]
        private float _hpfPrevInputLeft;
        [NonSerialized]
        private float _hpfPrevInputRight;
        [NonSerialized]
        private float _hpfPrevOutputLeft;
        [NonSerialized]
        private float _hpfPrevOutputRight;

        public IAudioHandler host;

        public APU(IAudioHandler audio, CDRom cdrom)
        {
            host = audio;
            Init(cdrom);
        }

        public APU()
        {
            Init(null);
        }

        private void Init(CDRom? cdrom)
        {
            MixADPCM = true;
            MixFADE = true;

            uint lfsr = 1;
            for (int i = 0; i < m_NoiseBuffer.Length; i++)
            {
                m_NoiseBuffer[i] = (lfsr & 1) != 0 ? 0x1F : 0x00;
                lfsr = (uint)((lfsr >> 1) | ((((lfsr & 1) ^ ((lfsr >> 1) & 1) ^ ((lfsr >> 11) & 1) ^ ((lfsr >> 12) & 1) ^ ((lfsr >> 17) & 1)) & 1) << 17));
            }
            double amplitude = 65535.0 / 6.0 / 32.0;
            double step = 48.0 / 32.0;
            for (int i = 0; i < 30; i++)
            {
                m_VolumeTable[i] = (float)amplitude;
                amplitude /= Math.Pow(10.0, step / 20.0);
            }
            m_VolumeTable[30] = 0;
            m_VolumeTable[31] = 0;

            for (int i = 0; i < m_Channels.Length; i++)
            {
                m_Channels[i] = new PSG_Channel();
            }
            m_SelectedIndex = 0;
            m_Selected = m_Channels[m_SelectedIndex];
            m_CDRom = cdrom!;
            ResetAudioTiming();
        }

        public void BindCdRom(CDRom cdrom)
        {
            m_CDRom = cdrom;
        }

        private void ResetAudioTiming()
        {
            m_ElapsedMainCycles = 0;
            m_SampleAccumulator = 0;
            _psgQueueRead = 0;
            _psgQueueWrite = 0;
            _lastRightSample = 0;
            _lastLeftSample = 0;
            _hpfPrevInputLeft = 0.0f;
            _hpfPrevInputRight = 0.0f;
            _hpfPrevOutputLeft = 0.0f;
            _hpfPrevOutputRight = 0.0f;
        }

        public void RebindSelectedChannel()
        {
            if (m_SelectedIndex < 0 || m_SelectedIndex >= m_Channels.Length)
                m_SelectedIndex = 0;
            m_Selected = m_Channels[m_SelectedIndex];
        }

        private short SoftClip(int sample)
        {
            const int threshold = short.MaxValue - 1000;
            if (sample > threshold) return (short)(threshold + (sample - threshold) * 0.5f);
            if (sample < -threshold) return (short)(-threshold + (sample + threshold) * 0.5f);
            return (short)sample;
        }

        public unsafe void GetSamples(short[] buffer, int len, int offset = 0)
        {
            ProducePendingSamples();

            for (int i = 0; i < len / 2; i++)
            {
                short psgRight = _lastRightSample;
                short psgLeft = _lastLeftSample;
                if (TryDequeuePsgSample(out short queuedRight, out short queuedLeft))
                {
                    psgRight = queuedRight;
                    psgLeft = queuedLeft;
                }

                int left = psgLeft;
                int right = psgRight;
                int adpcmSample = m_CDRom._ADPCM.GetSample();
                if (MixADPCM)
                {
                    left += adpcmSample;
                    right += adpcmSample;
                }
                buffer[offset + i * 2] = SoftClip(right + m_BaseLine);
                buffer[offset + i * 2 + 1] = SoftClip(left + m_BaseLine);
            }

            m_CDRom.MixCD(buffer, len, offset);
        }

        public void Clock(int cycles)
        {
            if (cycles <= 0)
                return;

            m_ElapsedMainCycles += cycles;
            ProducePendingSamples();
        }

        private void ProducePendingSamples()
        {
            if (m_ElapsedMainCycles <= 0)
                return;

            m_SampleAccumulator += m_ElapsedMainCycles * m_SampleRate;
            m_ElapsedMainCycles = 0;

            while (m_SampleAccumulator >= MainClockHz)
            {
                m_SampleAccumulator -= MainClockHz;
                GeneratePsgSample();
            }
        }

        private void GeneratePsgSample()
        {
            int left = 0;
            int right = 0;

            if (m_LFO_Enabled && m_LFO_Active)
            {
                var lfoSource = m_Channels[1];
                int lfoFreq = lfoSource.m_Buffer[(int)lfoSource.m_OutputIndex];
                lfoSource.m_OutputIndex += m_RealLFOFrequency;
                lfoSource.m_OutputIndex %= 32;
                if ((lfoFreq & 0x10) != 0)
                    lfoFreq |= -16;

                var lfoTarget = m_Channels[0];
                lfoTarget.m_RealFrequency = 3584160.0f / m_SampleRate / (lfoTarget.m_Frequency + (lfoFreq << m_LFO_Shift) + 1);
            }

            foreach (var channel in m_Channels.Take(PsgChannelCount))
            {
                if (!channel.m_Enabled || (m_LFO_Enabled && channel == m_Channels[1]))
                    continue;

                int channelSample = GetChannelSample(channel);
                left += (int)((channelSample - DcOffset) * channel.m_LeftOutput);
                right += (int)((channelSample - DcOffset) * channel.m_RightOutput);
            }

            const float hpfR = 0.9985f;
            float filteredLeft = left - _hpfPrevInputLeft + hpfR * _hpfPrevOutputLeft;
            float filteredRight = right - _hpfPrevInputRight + hpfR * _hpfPrevOutputRight;
            _hpfPrevInputLeft = left;
            _hpfPrevInputRight = right;
            _hpfPrevOutputLeft = filteredLeft;
            _hpfPrevOutputRight = filteredRight;

            _lastRightSample = SoftClip((int)filteredRight + m_BaseLine);
            _lastLeftSample = SoftClip((int)filteredLeft + m_BaseLine);
            EnqueuePsgSample(_lastRightSample, _lastLeftSample);
        }

        private void EnqueuePsgSample(short right, short left)
        {
            int nextWrite = (_psgQueueWrite + 2) % _psgSampleQueue.Length;
            if (nextWrite == _psgQueueRead)
            {
                _psgQueueRead = (_psgQueueRead + 2) % _psgSampleQueue.Length;
            }

            _psgSampleQueue[_psgQueueWrite] = right;
            _psgSampleQueue[(_psgQueueWrite + 1) % _psgSampleQueue.Length] = left;
            _psgQueueWrite = nextWrite;
        }

        private bool TryDequeuePsgSample(out short right, out short left)
        {
            if (_psgQueueRead == _psgQueueWrite)
            {
                right = 0;
                left = 0;
                return false;
            }

            right = _psgSampleQueue[_psgQueueRead];
            left = _psgSampleQueue[(_psgQueueRead + 1) % _psgSampleQueue.Length];
            _psgQueueRead = (_psgQueueRead + 2) % _psgSampleQueue.Length;
            return true;
        }

        private int GetChannelSample(PSG_Channel channel)
        {
            int sample;
            if (channel.m_DDA)
            {
                sample = channel.m_DDA_Output;
            }
            else if (channel.m_Noise)
            {
                sample = m_NoiseBuffer[(int)channel.m_NoiseIndex];
                channel.m_NoiseIndex += channel.m_RealNoiseFrequency;
                channel.m_NoiseIndex %= NoiseBufferSize;
            }
            else
            {
                sample = channel.m_Buffer[(int)channel.m_OutputIndex];
                channel.m_OutputIndex += channel.m_RealFrequency;
                channel.m_OutputIndex %= 32;
            }
            return sample;
        }

        private bool AreAllBuffersFull()
        {
            return m_Channels.Take(6)
                   .Where(c => c.m_Enabled && !c.m_Noise)
                   .All(c => c.m_BufferIndex == 0);
        }

        public void Write(int address, byte data)
        {
            ProducePendingSamples();

            switch (address)
            {
                case 0x800:
                    m_SelectedIndex = data & 0x07;
                    m_Selected = m_Channels[m_SelectedIndex];
                    break;
                case 0x801:
                    m_Left_Volume = (data >> 4) & 0x0F;
                    m_Right_Volume = data & 0x0F;
                    break;
                case 0x808:
                    m_LFO_Frequency = data;
                    break;
                case 0x809:
                    m_LFO_Enabled = (data & 0x80) != 0;
                    switch (data & 0x3)
                    {
                        case 0: m_LFO_Active = false; break;
                        case 1: m_LFO_Active = true; m_LFO_Shift = 0; break;
                        case 2: m_LFO_Active = true; m_LFO_Shift = 4; break;
                        case 3: m_LFO_Active = true; m_LFO_Shift = 8; break;
                    }
                    if (m_LFO_Enabled && m_LFO_Active)
                        Console.WriteLine("LFO MODE HAS BEEN ACTIVATED");
                    break;
                case 0x802:
                    m_Selected.m_Frequency = (m_Selected.m_Frequency & 0x0F00) | data;
                    break;
                case 0x803:
                    m_Selected.m_Frequency = (m_Selected.m_Frequency & 0x00FF) | ((data << 8) & 0x0F00);
                    break;
                case 0x804:
                    bool wasEnabled = m_Selected.m_Enabled;
                    bool wasDda = m_Selected.m_DDA;
                    m_Selected.m_Control = data;
                    m_Selected.m_Enabled = (data & 0x80) != 0;
                    m_Selected.m_DDA = (data & 0x40) != 0;
                    m_Selected.m_Volume = (data >> 1) & 0x0F;
                    if (wasEnabled != m_Selected.m_Enabled)
                    {
                        m_Selected.m_OutputIndex = 0.0f;
                    }
                    if (wasDda && !m_Selected.m_Enabled)
                    {
                        m_Selected.m_BufferIndex = 0;
                    }
                    break;
                case 0x805:
                    m_Selected.m_Amplitude = data;
                    m_Selected.m_Left_Volume = (data >> 4);
                    m_Selected.m_Right_Volume = (data & 0x0F);
                    break;
                case 0x806:
                    if (m_Selected.m_DDA)
                    {
                        m_Selected.m_DDA_Output = data & 0x1F;
                    }
                    else if (!m_Selected.m_Enabled)
                    {
                        m_Selected.m_Buffer[m_Selected.m_BufferIndex] = data & 0x1F;
                        m_Selected.m_BufferIndex = (m_Selected.m_BufferIndex + 1) & 0x1F;
                    }
                    break;
                case 0x807:
                    if (m_SelectedIndex >= 4 && m_SelectedIndex < 6)
                    {
                        m_Selected.m_Noise = (data & 0x80) != 0;
                        int freq = data & 0x1F;
                        m_Selected.m_NoiseFrequency = freq == 0x1F ? 32 : (((~freq) & 0x1F) << 6);
                        m_Selected.m_RealNoiseFrequency = 3584160.0f / m_SampleRate / Math.Max(1, m_Selected.m_NoiseFrequency);
                    }
                    break;
                default:
                    Console.WriteLine($"PSG Access at {address:X} -> {data:X}");
                    break;
            }

            UpdateChannelParameters();

            //if (AreAllBuffersFull())
            //{
            //    int remainingSpace = 1052 - m_BufferPos;
            //    int samplesToGenerate = Math.Min(remainingSpace, 64);

            //    GetSamples(m_AudioBuffer, samplesToGenerate, m_BufferPos);
            //    m_BufferPos += samplesToGenerate;

            //    if (m_BufferPos >= 1052)
            //    {
            //        host.PlaySamples(m_AudioBuffer);
            //        m_BufferPos = 0;
            //    }
            //}
        }

        private void UpdateChannelParameters()
        {
            for (int i = 0; i < PsgChannelCount; i++)
            {
                var channel = m_Channels[i];
                int frequency = channel.m_Frequency != 0 ? channel.m_Frequency : 0x1000;
                channel.m_RealFrequency = 3584160.0f / m_SampleRate / frequency;

                int tempLeft = Math.Min(0x0F, (0x0F - m_Left_Volume) + (0x0F - channel.m_Left_Volume) + (0x0F - channel.m_Volume));
                int tempRight = Math.Min(0x0F, (0x0F - m_Right_Volume) + (0x0F - channel.m_Right_Volume) + (0x0F - channel.m_Volume));
                int volumeIndexLeft = (tempLeft << 1) | ((~channel.m_Control) & 0x01);
                int volumeIndexRight = (tempRight << 1) | ((~channel.m_Control) & 0x01);
                channel.m_LeftOutput = m_VolumeTable[volumeIndexLeft];
                channel.m_RightOutput = m_VolumeTable[volumeIndexRight];

                if (i < 4)
                    channel.m_Noise = false;
            }

            int lfoSourceFrequency = m_Channels[1].m_Frequency != 0 ? m_Channels[1].m_Frequency : 0x1000;
            int lfoDivider = m_LFO_Frequency != 0 ? m_LFO_Frequency : 0x100;
            m_RealLFOFrequency = 3584160.0f / m_SampleRate / (lfoSourceFrequency * lfoDivider);
        }

    }
}
