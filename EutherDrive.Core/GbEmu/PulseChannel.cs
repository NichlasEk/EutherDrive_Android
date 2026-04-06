using System;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Channel 2: Pulse B.
    /// Generates square waves without sweep functionality.
    /// </summary>
    internal class PulseChannel : ChannelBase
    {
        // --- Frequency Generator ---
        private int _frequency;
        private int _frequencyTimer;
        private int _frequencyTimerPeriod;

        // --- Waveform Generator ---
        private int _dutyCycle;
        private int _dutyPosition;
        private readonly int[] _dutyPatterns = { 0, 0, 0, 0, 0, 0, 0, 1, // 12.5%
                                                 1, 0, 0, 0, 0, 0, 0, 1, // 25%
                                                 1, 0, 0, 0, 0, 1, 1, 1, // 50%
                                                 0, 1, 1, 1, 1, 1, 1, 0 }; // 75%

        // --- Length Value ---
        private int _lengthValue;

        public PulseChannel(): base()
        {
            _lengthMax = 64; // Channel 2 length counter max value
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _dutyCycle = 0;
            _dutyPosition = 0;
            _dacEnabled = false; // Start with DAC disabled
            _lengthValue = 0;
        }

        public override void Trigger(bool isNextStepNotLength)
        {
            base.Trigger(isNextStepNotLength);
            _frequencyTimer = _frequencyTimerPeriod;
            _dutyPosition = 0;
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
                case 0xFF15: // NR15 - Unused register
                    return 0xFF;
                case 0xFF16: // NR21 - Length timer & duty cycle
                    return (byte)((_dutyCycle << 6) | 0x3F);
                case 0xFF17: // NR22 - Envelope
                    return (byte)((_envelopeInitialVolume << 4) | (_envelopeDirection << 3) | _envelopePeriod);
                case 0xFF18: // NR23 - Frequency low
                    return 0xFF; // Write-only
                case 0xFF19: // NR24 - Frequency high & control
                    return (byte)((_lengthEnabled ? 0x40 : 0) | 0xBF);
                default:
                    return 0xFF;
            }
        }

        public override void WriteRegister(ushort address, byte value, bool isApuEnabled, bool IsLengthClockStep)
        {
            switch (address)
            {
                case 0xFF15: // NR15 - Unused register
                    break;
                case 0xFF16: // NR21 - Length timer & duty cycle
                    // Duty cycle is only writable when the APU is enabled.
                    if (isApuEnabled)
                    {
                        _dutyCycle = (value >> 6) & 0x03;
                    }
                    // Length is always writable.
                    _lengthValue = value & 0x3F;
                    _lengthCounter = _lengthMax - _lengthValue; // immediate reload on write
                    break;

                case 0xFF17: // NR22 - Envelope
                    _envelopeInitialVolume = (value >> 4) & 0x0F;
                    _envelopeDirection = (value >> 3) & 0x01;
                    _envelopePeriod = value & 0x07;
                    _dacEnabled = (value & 0xF8) != 0;
                    if (!_dacEnabled) _enabled=false;
                    _envelopeEnabled = _envelopePeriod > 0;
                    break;

                case 0xFF18: // NR23 - Frequency low
                    _frequency = (_frequency & 0x0700) | value;
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();
                    break;

                case 0xFF19: // NR24 - Frequency high & control
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

        private int CalculateFrequencyTimerPeriod()
        {
            return (2048 - _frequency) * 4;
        }

        public override void PowerOff()
        {
            base.PowerOff();
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
            _frequency = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _dutyCycle = 0;
            _dutyPosition = 0;
        }
    }
}
