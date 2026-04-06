using System;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Channel 3: Wave channel.
    /// Generates arbitrary waveforms using a 32-byte wave table.
    /// </summary>
    internal class WaveChannel : ChannelBase
    {
        private int _lengthValue;

        // --- Wave Table ---
        private readonly byte[] _waveTable = new byte[32];
        private int _wavePosition;

        // --- Frequency Generator ---
        private int _frequency;
        private int _frequencyTimer;
        private int _frequencyTimerPeriod;

        // --- Volume Control ---
        private int _volumeShift;

        public WaveChannel(): base()
        {
            _lengthMax = 256; // Channel 3 length counter max value
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _wavePosition = 0;
            _volumeShift = 0;
            _dacEnabled = false; // Channel 3 DAC is always enabled when channel is enabled
        }

        public override void Trigger(bool isNextStepNotLength)
        {
            base.Trigger(isNextStepNotLength);
            _frequencyTimer = _frequencyTimerPeriod;
            _wavePosition = 0;
        }

        public override float GetSample()
        {
            if (!IsEnabled || !_dacEnabled) return 0.0f;

            // Get wave sample (4-bit value from wave table)
            byte waveSample = _waveTable[_wavePosition];

            // Apply volume shift
            if (_volumeShift > 0)
            {
                waveSample >>= (_volumeShift - 1);
            }
            else
            {
                waveSample = 0; // Mute
            }

            // Convert 4-bit sample to bipolar [-1, 1]
            float sample = waveSample / 15.0f;

            return sample * 2.0f - 1.0f;
        }

        /// <summary>
        /// Updates the frequency timer based on CPU cycles.
        /// </summary>
        public void UpdateFrequencyTimer(int cycles)
        {
            if (!IsEnabled || !_dacEnabled || _frequencyTimerPeriod == 0) return;

            _frequencyTimer -= cycles;
            while (_frequencyTimer <= 0)
            {
                _frequencyTimer += _frequencyTimerPeriod;
                _wavePosition = (_wavePosition + 1) % 32;
            }
        }

        public override byte ReadRegister(ushort address)
        {
            switch (address)
            {
                case 0xFF1A: // NR30 - DAC enable
                    return (byte)((_dacEnabled ? 0x80 : 0x00) | 0x7F);
                case 0xFF1B: // NR31 - Length timer
                    return 0xFF; // Write-only
                case 0xFF1C: // NR32 - Volume
                    return (byte)((_volumeShift << 5) | 0x9F);
                case 0xFF1D: // NR33 - Frequency low
                    return 0xFF; // Write-only
                case 0xFF1E: // NR34 - Frequency high & control
                    return (byte)((_lengthEnabled ? 0x40 : 0) | 0xBF);
                default:
                    return 0xFF;
            }
        }

        public override void WriteRegister(ushort address, byte value, bool isApuEnabled, bool IsLengthClockStep)
        {
            switch (address)
            {
                case 0xFF1A: // NR30 - DAC enable
                    _dacEnabled = (value & 0x80) != 0;
                    if (!_dacEnabled) _enabled=false;
                    break;

                case 0xFF1B: // NR31 - Length timer
                    _lengthValue = value; // Store the 8-bit length value
                    _lengthCounter = _lengthMax - _lengthValue; // immediate reload on write
                    break;

                case 0xFF1C: // NR32 - Volume
                    _volumeShift = (value >> 5) & 0x03;
                    break;

                case 0xFF1D: // NR33 - Frequency low
                    _frequency = (_frequency & 0x0700) | value;
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();
                    break;

                case 0xFF1E: // NR34 - Frequency high & control
                    _frequency = (_frequency & 0x00FF) | ((value & 0x07) << 8);
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();
                    //_lengthEnabled = (value & 0x40) != 0;
                    OnLengthEnableWritten((value & 0x40) != 0, IsLengthClockStep);

                    if ((value & 0x80) != 0)
                    {
                        Trigger(IsLengthClockStep);
                    }
                    break;
            }
        }

        /// <summary>
        /// Writes to the wave table (0xFF30-0xFF3F).
        /// </summary>
        public void WriteWaveTable(ushort address, byte value)
        {
            if (address >= 0xFF30 && address <= 0xFF3F)
            {
                int index = (address - 0xFF30) * 2;
        
                // Each byte in the wave RAM address space (0xFF30-0xFF3F) holds two
                // 4-bit samples. The first sample is in the high nibble, and the
                // second is in the low nibble.
                _waveTable[index] = (byte)((value >> 4) & 0x0F);
                _waveTable[index + 1] = (byte)(value & 0x0F);
            }
        }

        /// <summary>
        /// Reads from the wave table (0xFF30-0xFF3F).
        /// </summary>
        public byte ReadWaveTable(ushort address)
        {
            if (address >= 0xFF30 && address <= 0xFF3F)
            {
                int index = address - 0xFF30;
                return (byte)((_waveTable[index * 2] << 4) | _waveTable[index * 2 + 1]);
            }
            return 0xFF;
        }

        private int CalculateFrequencyTimerPeriod()
        {
            return (2048 - _frequency) * 2; // Wave channel uses 2x frequency
        }

        public override void PowerOff()
        {
            base.PowerOff();
            _lengthValue = 0;
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _wavePosition = 0;
            _volumeShift = 0;
            // IMPORTANT: Per hardware specs, Wave RAM is NOT cleared on power-off.
            // So, we do not touch _waveTable here.
        }

        public override void Reset()
        {
            base.Reset();
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _wavePosition = 0;
            _volumeShift = 0;
            _dacEnabled = false;
        }
    }
}
