using System;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Channel 1: Pulse A with sweep functionality.
    /// Generates square waves with frequency sweep capability.
    /// </summary>
    internal class PulseWithSweepChannel : ChannelBase
    {
        // --- Sweep Generator ---
        private int _sweepPeriod;
        private int _sweepCounter;
        private int _sweepShift;
        private int _sweepDirection; // 0 = increase, 1 = decrease
        private bool _sweepEnabled;

        // --- Frequency Generator ---
        private int _frequency;
        private int _frequencyTimer;
        private int _frequencyTimerPeriod;
        private int _shadowFrequency;


        // --- Waveform Generator ---
        private int _dutyCycle;
        private int _dutyPosition;
        private readonly int[] _dutyPatterns = { 0, 0, 0, 0, 0, 0, 0, 1, // 12.5%
                                                 1, 0, 0, 0, 0, 0, 0, 1, // 25%
                                                 1, 0, 0, 0, 0, 1, 1, 1, // 50%
                                                 0, 1, 1, 1, 1, 1, 1, 0 }; // 75%

        // --- Length Value ---
        private int _lengthValue;

        public PulseWithSweepChannel(): base()
        {
            _lengthMax = 64; // Channel 1 length counter max value
            _sweepPeriod = 0;
            _sweepCounter = 0;
            _sweepShift = 0;
            _sweepDirection = 0;
            _sweepEnabled = false;
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _dutyCycle = 0;
            _dutyPosition = 0;
            _dacEnabled = false; // Start with DAC disabled
            _lengthValue = 0;
        }

        public override void TickSweep()
        {
            if (!_sweepEnabled || _sweepPeriod == 0) return;

            _sweepCounter--;
            if (_sweepCounter <= 0)
            {
                _sweepCounter = (_sweepPeriod != 0) ? _sweepPeriod : 8;

                int newFreq = CalculateSweepFrequency(_shadowFrequency);
                if (newFreq > 2047)
                {
                    _enabled = false;
                }
                else if (_sweepShift > 0)
                {
                    _frequency = newFreq;
                    _shadowFrequency = newFreq;
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();

                    // Second overflow check
                    if (CalculateSweepFrequency(_shadowFrequency) > 2047)
                    {
                        _enabled = false;
                    }
                }
            }
        }

        private int CalculateSweepFrequency(int baseFrequency)
        {
            int offset = baseFrequency >> _sweepShift;
            int newFreq;
            
            if (_sweepDirection == 1) // decrease
            {
                newFreq = baseFrequency - offset;
            }
            else // increase
            {
                newFreq = baseFrequency + offset;
            }

            return newFreq;
        }

        private int CalculateFrequencyTimerPeriod()
        {
            // Game Boy frequency formula: 131072 / (2048 - frequency)
            // Timer period = CPU cycles per waveform step
            return (2048 - _frequency) * 4;
        }

        public override void Trigger(bool isNextStepNotLength)
        {
            base.Trigger(isNextStepNotLength);
            _frequencyTimer = _frequencyTimerPeriod;
            _dutyPosition = 0;

            _shadowFrequency = _frequency;
            // If sweep period or shift is non-zero, sweep is enabled
            _sweepEnabled = _sweepPeriod > 0 || _sweepShift > 0;
            _sweepCounter = (_sweepPeriod != 0) ? _sweepPeriod : 8;

             // Immediate sweep write-back and second overflow check
            if (_sweepShift > 0)
            {
                int newFreq = CalculateSweepFrequency(_shadowFrequency);
                if (newFreq <= 2047)
                {
                    _frequency = newFreq;
                    _shadowFrequency = newFreq;
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();

                    // Second (non-write-back) overflow check
                    if (CalculateSweepFrequency(_shadowFrequency) > 2047)
                        _enabled = false;
                }
                else
                {
                    _enabled = false;
                }
            }
        }

        public override float GetSample()
        {
            if (!IsEnabled || !_dacEnabled) return 0.0f;

            // Get duty pattern value
            int dutyValue = _dutyPatterns[_dutyCycle * 8 + _dutyPosition];

            // Apply envelope and convert to bipolar [-1, 1]
            float sample = dutyValue * _envelopeVolume / 15.0f;

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
                _dutyPosition = (_dutyPosition + 1) % 8;
            }
        }

        public override byte ReadRegister(ushort address)
        {
            switch (address)
            {
                case 0xFF10: // NR10 - Sweep
                    return (byte)(0x80 | (_sweepPeriod << 4) | (_sweepDirection << 3) | _sweepShift);
                case 0xFF11: // NR11 - Length timer & duty cycle
                    return (byte)((_dutyCycle << 6) | 0x3F);
                case 0xFF12: // NR12 - Envelope
                    return (byte)((_envelopeInitialVolume << 4) | (_envelopeDirection << 3) | _envelopePeriod);
                case 0xFF13: // NR13 - Frequency low
                    return 0xFF; // Write-only
                case 0xFF14: /// NR14 - Frequency high & control
                    // Unused bits in APU registers often read back as 1.
                    // 0xBF is 0b10111111. This preserves the length-enable bit (bit 6)
                    // while setting the unused bits high.
                    return (byte)((_lengthEnabled ? 0b0100_0000 : 0) | 0b1011_1111);
                default:
                    return 0xFF;
            }
        }

        public override void WriteRegister(ushort address, byte value, bool isApuEnabled, bool IsLengthClockStep) 
        {
            switch (address)
            {
                case 0xFF10: // NR10 - Sweep
                    _sweepPeriod = (value >> 4) & 0x07;
                    _sweepDirection = (value >> 3) & 0x01;
                    _sweepShift = value & 0x07;
                    break;

                case 0xFF11: // NR11 - Length timer & duty cycle
                    // Duty cycle is only writable when the APU is enabled.
                    if (isApuEnabled)
                    {
                        _dutyCycle = (value >> 6) & 0x03;
                    }
                    // Length is always writable.
                    _lengthValue = value & 0x3F;
                    _lengthCounter = _lengthMax - _lengthValue; // immediate reload on write
                    break;

                case 0xFF12: // NR12 - Envelope
                    _envelopeInitialVolume = (value >> 4) & 0x0F;
                    _envelopeDirection = (value >> 3) & 0x01;
                    _envelopePeriod = value & 0x07;
                    _dacEnabled = (value & 0xF8) != 0;
                    if (!_dacEnabled) _enabled=false;
                    _envelopeEnabled = _envelopePeriod > 0;
                    break;

                case 0xFF13: // NR13 - Frequency low
                    _frequency = (_frequency & 0x0700) | value;
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();
                    break;

                case 0xFF14: // NR14 - Frequency high & control
                    _frequency = (_frequency & 0x00FF) | ((value & 0x07) << 8);
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();
                    OnLengthEnableWritten((value & 0x40) != 0, IsLengthClockStep);

                    if ((value & 0x80) != 0)
                    {
                        Trigger(IsLengthClockStep);
                    }
                    break;
            }
        }

        public override void PowerOff()
        {
            base.PowerOff();
            _sweepPeriod = 0;
            _sweepCounter = 0;
            _sweepShift = 0;
            _sweepDirection = 0;
            _sweepEnabled = false;
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _dutyCycle = 0;
            _dutyPosition = 0;
            _lengthValue = 0;
        }

        public override void Reset()
        {
            base.Reset();
            _sweepPeriod = 0;
            _sweepCounter = 0;
            _sweepShift = 0;
            _sweepDirection = 0;
            _sweepEnabled = false;
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _dutyCycle = 0;
            _dutyPosition = 0;
        }
    }
}
