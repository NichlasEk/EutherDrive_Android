using System;
using System.IO;

namespace ePceCD
{
    [Serializable]
    public class ADPCM
    {
        private static readonly bool TraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_ADPCM_TRACE"), "1", StringComparison.Ordinal);
        private static readonly string? TraceFile =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_ADPCM_TRACE_FILE");
        private static readonly object TraceFileLock = new object();
        private const uint RAM_SIZE = 0x10000; // 64KB ADPCM RAM
        private const int AdpcmRamMask = 0xFFFF;
        private const int AdpcmLengthMask = 0x1FFFF;
        private const double BusClockHz = 7159090.0;
        private const int SlotPeriodCycles = 12;
        private const int SlotWidthCycles = 3;
        private const int DmaTransferCycles = 12;
        private static readonly int[] s_ReadLatency = BuildLatencyTable(read: true);
        private static readonly int[] s_WriteLatency = BuildLatencyTable(read: false);
        private static readonly short[] s_StepDelta = BuildStepDeltaTable();
        private static readonly sbyte[] s_IndexShift = { -1, -1, -1, -1, 2, 4, 6, 8 };
        private byte[] _ram = new byte[RAM_SIZE];
        [NonSerialized]
        private CDRom _cdRom;

        // 寄存器状态
        private ushort _addressPort;      // 地址端口（0x08/0x09）
        private byte _dmaControl;         // DMA控制（0x0B）
        private byte _control;            // 控制寄存器（0x0D）
        private byte _playbackRate;        // 播放速率（0x0E）

        // 内部状态
        private byte _readValue;
        private byte _writeValue;
        private int _readCycles;
        private int _writeCycles;
        private int _dmaCycles;
        private uint _readAddress;         // 当前读取地址
        private uint _writeAddress;        // 当前写入地址
        private uint _adpcmLength;         // 剩余播放长度
        private bool _isPlaying;          // 是否正在播放
        private bool _playPending;
        private bool _endReached;         // 播放结束标志
        private bool _halfReached;        // 半缓冲区标志
        private double _clocksPerSample;  // 每个样本的时钟周期
        private double _adpcmCycleCounter;
        private double _audioCycleCounter;
        private int _currentPredictor;    // ADPCM解码预测值
        private int _currentStepIndex;    // ADPCM步长索引
        private bool _nibbleToggle;
        private int _currentOutputSample;
        [NonSerialized]
        private short[] _audioQueue = Array.Empty<short>();
        [NonSerialized]
        private int _audioQueueRead;
        [NonSerialized]
        private int _audioQueueWrite;
        [NonSerialized]
        private int _audioQueueCount;
        private float _filterState;
        private float _dcPrevX;
        private float _dcPrevY;
        private float _gainSmooth;
        [NonSerialized]
        private bool _traceLastPlaying;
        [NonSerialized]
        private bool _traceLastPending;
        [NonSerialized]
        private uint _traceLastLength;
        [NonSerialized]
        private byte _traceLastControl;
        [NonSerialized]
        private byte _traceLastRate;

        // 中断标志位掩码
        private const byte STATUS_END_FLAG = 0x01;
        private const byte STATUS_PLAYING_FLAG = 0x08;

        public ADPCM(CDRom cdRom)
        {
            _cdRom = cdRom;
            UpdatePlaybackRate();
            ResetDecoderState();
        }

        public ADPCM()
        {
            _cdRom = null!;
            UpdatePlaybackRate();
            ResetDecoderState();
        }

        public void BindCdRom(CDRom cdRom)
        {
            _cdRom = cdRom;
        }

        internal string GetDebugSummary()
        {
            return
                $"play={(_isPlaying ? 1 : 0)} pend={(_playPending ? 1 : 0)} len=0x{_adpcmLength:X5} ctl=0x{_control:X2} dma=0x{_dmaControl:X2} rate=0x{_playbackRate:X2} q={_audioQueueCount} out={_currentOutputSample} end={(_endReached ? 1 : 0)} half={(_halfReached ? 1 : 0)}";
        }

        internal bool HasDebugActivity()
        {
            return _isPlaying ||
                   _playPending ||
                   _audioQueueCount > 0 ||
                   _currentOutputSample != 0 ||
                   (_dmaControl & 0x03) != 0 ||
                   _adpcmLength != 0;
        }

        private void Trace(string message)
        {
            if (!TraceEnabled)
                return;

            string line = $"ADPCM: {message}";
            if (!string.IsNullOrWhiteSpace(TraceFile))
            {
                lock (TraceFileLock)
                    File.AppendAllText(TraceFile, line + Environment.NewLine);
            }
            else
            {
                Console.WriteLine(line);
            }
        }

        private void TraceState(string reason)
        {
            Trace(
                $"{reason} pc=0x{HuC6280.CurrentPC:X4} ctl=0x{_control:X2} rate=0x{_playbackRate:X2} len=0x{_adpcmLength:X5} addr=0x{_addressPort:X4} r=0x{_readAddress:X4} w=0x{_writeAddress:X4} play={(_isPlaying ? 1 : 0)} pend={(_playPending ? 1 : 0)} end={(_endReached ? 1 : 0)} half={(_halfReached ? 1 : 0)} dma=0x{_dmaControl:X2}");
        }

        private void TraceStateChanges(string reason)
        {
            if (!TraceEnabled)
                return;

            if (_traceLastPlaying != _isPlaying ||
                _traceLastPending != _playPending ||
                _traceLastLength != _adpcmLength ||
                _traceLastControl != _control ||
                _traceLastRate != _playbackRate)
            {
                _traceLastPlaying = _isPlaying;
                _traceLastPending = _playPending;
                _traceLastLength = _adpcmLength;
                _traceLastControl = _control;
                _traceLastRate = _playbackRate;
                TraceState(reason);
            }
        }

        private void ResetDecoderState()
        {
            _currentPredictor = 2048;
            _currentStepIndex = 0;
            _nibbleToggle = false;
            _currentOutputSample = 0;
            _adpcmCycleCounter = 0;
            _audioCycleCounter = 0;
            _filterState = 0.0f;
            _dcPrevX = 0.0f;
            _dcPrevY = 0.0f;
            _gainSmooth = 1.0f;
            EnsureAudioQueue();
            _audioQueueRead = 0;
            _audioQueueWrite = 0;
            _audioQueueCount = 0;
        }

        public byte ReadData(int addr)
        {
            switch (addr & 0x0F)
            {
                case 0x08: // 地址端口低8位
                    return (byte)(_addressPort & 0x00FF);

                case 0x09: // 地址端口高8位
                    return (byte)((_addressPort >> 8) & 0x00FF);

                case 0x0A: // 读取数据端口
                    _readCycles = NextSlotCycles(read: true);
                    return _readValue;

                case 0x0B: // DMA控制
                    if (_cdRom.dataBuffer == null || _cdRom.dataBuffer.Length == 0)
                        _dmaControl &= unchecked((byte)~0x01);
                    return _dmaControl;

                case 0x0C: // 状态寄存器
                    byte status = 0;
                    status |= (byte)(_endReached ? STATUS_END_FLAG : 0);
                    status |= (byte)(_isPlaying ? STATUS_PLAYING_FLAG : 0);
                    status |= (byte)(_readCycles > 0 ? 0x80 : 0);
                    status |= (byte)(_writeCycles > 0 ? 0x04 : 0);
                    return status;

                case 0x0D: // 控制寄存器
                    return _control;

                case 0x0E: // 播放速率
                    return _playbackRate;

                default:
                    Console.WriteLine($"ADPCM Read Unknown Register: 0x{addr:X2}");
                    return 0;
            }
        }

        public void WriteData(int addr, byte value)
        {
            //Console.WriteLine($"ADPCM Write Register: 0x{addr:X2}");
            switch (addr & 0x0F)
            {
                case 0x08: // 地址端口低8位
                    _addressPort = (ushort)((_addressPort & 0xFF00) | value);
                    break;

                case 0x09: // 地址端口高8位
                    _addressPort = (ushort)((_addressPort & 0x00FF) | (value << 8));
                    break;

                case 0x0A: // 数据写入端口
                    _writeCycles = NextSlotCycles(read: false);
                    _writeValue = value;
                    break;

                case 0x0B: // DMA控制
                    if (_cdRom.dataBuffer == null || _cdRom.dataBuffer.Length == 0)
                        value &= unchecked((byte)~0x01);
                    _dmaControl = value;
                    break;

                case 0x0D: // 控制寄存器
                    UpdateControlState(value);
                    break;

                case 0x0E: // 播放速率
                    _playbackRate = value;
                    UpdatePlaybackRate();
                    break;

                default:
                    Console.WriteLine($"ADPCM Write Unknown Register: 0x{addr:X2}");
                    break;
            }
        }

        // 更新播放速率计算
        private void UpdatePlaybackRate()
        {
            int rateCode = _playbackRate & 0x0F;
            double freq = 32000.0 / (16 - rateCode); // 实际采样率
            _clocksPerSample = BusClockHz / freq;
        }

        // 处理控制寄存器写入
        private void UpdateControlState(byte value)
        {
            if ((value & 0x02) != 0 && (_control & 0x02) == 0)
                _writeAddress = (uint)((_addressPort - ((value & 0x01) != 0 ? 0 : 1)) & AdpcmRamMask);

            if ((value & 0x08) != 0 && (_control & 0x08) == 0)
                _readAddress = (uint)((_addressPort - ((value & 0x04) != 0 ? 0 : 1)) & AdpcmRamMask);

            if ((value & 0x20) != 0 && !_isPlaying)
                _playPending = true;

            _control = value;
        }

        private void SoftReset()
        {
            _readValue = 0;
            _writeValue = 0;
            _readCycles = 0;
            _writeCycles = 0;
            _readAddress = 0;
            _writeAddress = 0;
            _addressPort = 0;
            _adpcmLength = 0;
            _playPending = false;
            SetEndReached(false);
            SetHalfReached(false);
            _isPlaying = (_control & 0x20) != 0;
            ResetDecoderState();
        }

        private int NextSlotCycles(bool read)
        {
            long cycles = _cdRom?.Bus?.GetMasterClockCycles() ?? 0;
            int offset = (int)(cycles % SlotPeriodCycles);
            return read ? s_ReadLatency[offset] : s_WriteLatency[offset];
        }

        public void Clock(int cycles)
        {
            if (cycles <= 0)
                return;

            CheckReset();
            CheckLength();
            RunAdpcm(cycles);
            UpdateReadWriteEvents(cycles);
            UpdateDma(cycles);
            UpdateAudio(cycles);
            CheckLength();
            CheckReset();
        }

        private void UpdateReadWriteEvents(int cycles)
        {
            if (_readCycles > 0)
            {
                _readCycles -= cycles;
                if (_readCycles <= 0)
                {
                    _readCycles = 0;
                    _readValue = _ram[_readAddress & AdpcmRamMask];
                    _readAddress = (_readAddress + 1) & AdpcmRamMask;

                    if (!IsLengthLatched())
                    {
                        if (_adpcmLength > 0)
                        {
                            _adpcmLength--;
                            SetHalfReached(_adpcmLength < 0x8000);
                        }
                        else
                        {
                            SetHalfReached(false);
                            SetEndReached(true);
                        }
                    }
                }
            }

            if (_writeCycles > 0)
            {
                _writeCycles -= cycles;
                if (_writeCycles <= 0)
                {
                    _writeCycles = 0;
                    _ram[_writeAddress & AdpcmRamMask] = _writeValue;
                    _writeAddress = (_writeAddress + 1) & AdpcmRamMask;

                    if (!IsLengthLatched())
                        _adpcmLength = (_adpcmLength + 1) & AdpcmLengthMask;

                    SetHalfReached(_adpcmLength < 0x8000);
                    SetEndReached(_adpcmLength == 0);
                }
            }
        }

        private void UpdateDma(int cycles)
        {
            if ((_dmaControl & 0x03) == 0)
                return;

            if (_cdRom.dataBuffer == null || _cdRom.dataBuffer.Length == 0 || _cdRom.dataBuffer.Position >= _cdRom.dataBuffer.Length)
            {
                _dmaControl &= unchecked((byte)~0x01);
                return;
            }

            if (_dmaCycles > 0)
            {
                _dmaCycles -= cycles;
                if (_dmaCycles <= 0)
                {
                    _dmaCycles = 0;
                    if (_writeCycles == 0)
                    {
                        _writeCycles = NextSlotCycles(read: false);
                        _writeValue = _cdRom.ReadDataPort();
                        if (_cdRom.dataBuffer == null || _cdRom.dataBuffer.Position >= _cdRom.dataBuffer.Length)
                            _dmaControl &= unchecked((byte)~0x01);
                    }
                    else
                    {
                        _dmaCycles = 1;
                    }
                }
                return;
            }

            _dmaCycles = DmaTransferCycles;
        }

        private void RunAdpcm(int cycles)
        {
            if ((_control & 0x80) != 0)
            {
                _isPlaying = (_control & 0x20) != 0;
                _playPending = false;
                return;
            }

            if (!_isPlaying && !_playPending)
                return;

            if ((_control & 0x20) == 0 || (((_control & 0x40) != 0) && _adpcmLength == 0))
            {
                _playPending = false;
                _isPlaying = false;
                _currentOutputSample = 0;
                return;
            }

            _adpcmCycleCounter += cycles;
            while (_adpcmCycleCounter >= _clocksPerSample)
            {
                _adpcmCycleCounter -= _clocksPerSample;

                if (_playPending)
                {
                    _playPending = false;
                    _isPlaying = true;
                    _currentPredictor = 2048;
                    _currentStepIndex = 0;
                    _nibbleToggle = false;
                }

                byte ramByte = _ram[_readAddress & AdpcmRamMask];
                byte nibble;
                _nibbleToggle = !_nibbleToggle;

                if (_nibbleToggle)
                {
                    nibble = (byte)((ramByte >> 4) & 0x0F);
                }
                else
                {
                    nibble = (byte)(ramByte & 0x0F);
                    _readAddress = (_readAddress + 1) & AdpcmRamMask;
                    _adpcmLength = (_adpcmLength - 1) & AdpcmLengthMask;

                    SetHalfReached(_adpcmLength <= 0x8000);
                    if (_adpcmLength == 0)
                        SetEndReached(true);
                }

                int predictor = DecodeAdpcmSample(nibble);
                _currentOutputSample = (predictor - 2048) * 10;
            }
        }

        // 检查是否启用长度锁存
        private bool IsLengthLatched() => (_control & 0x10) != 0;

        private bool CheckReset()
        {
            if ((_control & 0x80) == 0)
                return false;
            SoftReset();
            return true;
        }

        private void CheckLength()
        {
            if (IsLengthLatched())
            {
                _adpcmLength = _addressPort;
                SetEndReached(false);
            }
        }

        private void SetEndReached(bool value)
        {
            if (_endReached != value)
            {
                _endReached = value;
                if (value)
                {
                    _cdRom.ActiveIrqs |= (byte)CDRom.CdRomIrqSource.Stop;
                }
                else
                {
                    _cdRom.ActiveIrqs &= unchecked((byte)~(byte)CDRom.CdRomIrqSource.Stop);
                }
            }
        }

        private void SetHalfReached(bool value)
        {
            if (_halfReached != value)
            {
                _halfReached = value;

                if (value)
                {
                    _cdRom.ActiveIrqs |= (byte)CDRom.CdRomIrqSource.Adpcm;
                }
                else
                {
                    _cdRom.ActiveIrqs &= unchecked((byte)~(byte)CDRom.CdRomIrqSource.Adpcm);
                }
            }
        }

        private void UpdateAudio(int cycles)
        {
            _audioCycleCounter += cycles;
            const double audioCyclesPerSample = BusClockHz / 44100.0;

            while (_audioCycleCounter >= audioCyclesPerSample)
            {
                _audioCycleCounter -= audioCyclesPerSample;

                float x = _currentOutputSample;
                const float dcBlockR = 0.997f;
                float y = x - _dcPrevX + dcBlockR * _dcPrevY;
                _dcPrevX = x;
                _dcPrevY = y;

                const float alphaLpf = 0.4f;
                _filterState += alphaLpf * (y - _filterState);

                const float gainSmoothing = 0.003f;
                _gainSmooth += (1.0f - _gainSmooth) * gainSmoothing;

                int out32 = (int)(_filterState * _gainSmooth);
                short sample = (short)Math.Clamp(out32, short.MinValue, short.MaxValue);
                EnqueueAudioSample(sample);
            }
        }

        private void EnsureAudioQueue()
        {
            if (_audioQueue.Length == 0)
                _audioQueue = new short[8192];
        }

        private void EnqueueAudioSample(short value)
        {
            EnsureAudioQueue();

            if (_audioQueueCount >= _audioQueue.Length)
            {
                _audioQueueRead = (_audioQueueRead + 1) % _audioQueue.Length;
                _audioQueueCount--;
            }

            _audioQueue[_audioQueueWrite] = value;
            _audioQueueWrite = (_audioQueueWrite + 1) % _audioQueue.Length;
            _audioQueueCount++;
        }

        // 生成音频样本
        public int GetSample()
        {
            EnsureAudioQueue();

            if (_audioQueueCount <= 0)
                return 0;

            short sample = _audioQueue[_audioQueueRead];
            _audioQueueRead = (_audioQueueRead + 1) % _audioQueue.Length;
            _audioQueueCount--;
            return sample;
        }

        //public int Clamp(int value, int min, int max)
        //{
        //    if (value < min)
        //        return min;
        //    else if (value > max)
        //        return max;
        //    else
        //        return value;
        //}

        public int AddClamped(int num1, int num2, int min, int max)
        {
            int result = num1 + num2;
            if (result < min) return min;
            if (result > max) return max;
            return result;
        }

        private static int[] BuildLatencyTable(bool read)
        {
            int[] table = new int[SlotPeriodCycles];
            for (int i = 0; i < table.Length; i++)
                table[i] = ComputeLatency(i, read);
            return table;
        }

        private static int ComputeLatency(int offset, bool read)
        {
            for (int d = 1; d <= SlotPeriodCycles; d++)
            {
                int slot = ((offset + d) / SlotWidthCycles) & 0x03; // 0=refresh, 1=write, 2=write, 3=read
                if (read)
                {
                    if (slot == 3)
                        return d;
                }
                else
                {
                    if (slot == 1 || slot == 2)
                        return d;
                }
            }

            return SlotPeriodCycles;
        }

        //public int DecodeSample(byte nibble)
        //{
        //    nibble &= 0x0F; // 确保4位数据
        //    int sign = nibble & 0x08;
        //    int magnitude = nibble & 0x07;

        //    // 计算步长和delta
        //    int step = _stepSize[_currentStepIndex];
        //    int delta = (step * magnitude) >> 2; // 等价于除以4

        //    if (sign != 0)
        //        delta = -delta;

        //    // 更新预测值
        //    _currentPredictor += delta;
        //    _currentPredictor = Clamp(_currentPredictor, -32768, 32767);

        //    // 更新步长索引
        //    _currentStepIndex += _stepFactor[magnitude];
        //    _currentStepIndex = Clamp(_currentStepIndex, 0, _stepSize.Length - 1);

        //    return _currentPredictor;
        //}

        private int DecodeAdpcmSample(byte nibble)
        {
            int sign = (nibble & 0x08) != 0 ? -1 : 1;
            int value = nibble & 0x07;
            int delta = s_StepDelta[(_currentStepIndex << 3) + value] * sign;
            _currentPredictor = (_currentPredictor + delta) & 0x0FFF;
            _currentStepIndex = AddClamped(_currentStepIndex, s_IndexShift[value], 0, 48);
            return _currentPredictor;
        }

        private static short[] BuildStepDeltaTable()
        {
            short[] table = new short[49 * 8];
            for (int step = 0; step < 49; step++)
            {
                int stepValue = (int)Math.Floor(16.0 * Math.Pow(11.0 / 10.0, step));
                for (int nibble = 0; nibble < 8; nibble++)
                {
                    table[(step << 3) + nibble] = (short)(
                        (stepValue / 8) +
                        (((nibble & 0x01) != 0) ? (stepValue / 4) : 0) +
                        (((nibble & 0x02) != 0) ? (stepValue / 2) : 0) +
                        (((nibble & 0x04) != 0) ? stepValue : 0));
                }
            }
            return table;
        }
    }

    [Serializable]
    public class AUDIOFADE
    {
        public byte fade;

        public byte ReadData(int addr)
        {
            //Console.WriteLine($"AUDIOFADE ReadData: 0x{addr:X4}");
            return fade;
        }

        public void WriteData(int addr, byte value)
        {
            fade = value;
            //Console.WriteLine($"AUDIOFADE WriteData: 0x{addr:X4} 0x{value:X4}");
        }

    }
}
