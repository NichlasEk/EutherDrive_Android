using System;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Channel 4: Noise channel.
    /// Generates pseudo-random noise using a linear feedback shift register.
    /// </summary>
    internal class NoiseChannel : ChannelBase
    {
        // --- Noise Generator ---
        private ushort _lfsr; // Linear Feedback Shift Register
        private int _frequencyTimer;
        private int _frequencyTimerPeriod;
        private int _clockShift;
        private int _widthMode;
        private int _divisor;

        // --- Length Value ---
        private int _lengthValue;

        public NoiseChannel(): base()
        {
            _lengthMax = 64; // Channel 4 length counter max value
            _lfsr = 0x0000; // Initialize LFSR to all 0s (silent)
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _clockShift = 0;
            _widthMode = 0;
            _divisor = 0;
            _dacEnabled = false; // Start with DAC disabled
            _lengthValue = 0;
        }

        public override void Trigger(bool isNextStepNotLength)
        {
            base.Trigger(isNextStepNotLength);
            _frequencyTimer = _frequencyTimerPeriod;
            _lfsr = 0x7FFF; // Reset LFSR
        }

        public override float GetSample()
        {
            if (!IsEnabled || !_dacEnabled) return 0.0f;

            // Get noise sample (inverted bit 0 of LFSR)
            int noiseSample = (~_lfsr) & 0x01;

            // Apply envelope and convert to bipolar [-1, 1]
            float sample = noiseSample * _envelopeVolume / 15.0f;

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

                // Update LFSR
                bool xorResult = ((_lfsr & 0x01) ^ ((_lfsr >> 1) & 0x01)) != 0;
                _lfsr >>= 1;
                _lfsr |= (ushort)((xorResult ? 1 : 0) << 14);

                // Apply width mode
                if (_widthMode == 1)
                {
                    _lfsr &= 0x7FBF; // Clear bit 6
                    _lfsr |= (ushort)((xorResult ? 1 : 0) << 6);
                }
            }
        }

        public override byte ReadRegister(ushort address)
        {
            switch (address)
            {
                case 0xFF20: // NR41 - Length timer
                    return 0xFF; // Write-only
                case 0xFF21: // NR42 - Envelope
                    return (byte)((_envelopeInitialVolume << 4) | (_envelopeDirection << 3) | _envelopePeriod);
                case 0xFF22: // NR43 - Polynomial counter
                    return (byte)((_clockShift << 4) | (_widthMode << 3) | _divisor);
                case 0xFF23: // NR44 - Counter/consecutive; initial
                    return (byte)((_lengthEnabled ? 0x40 : 0) | 0xBF);
                default:
                    return 0xFF;
            }
        }

        public override void WriteRegister(ushort address, byte value, bool isApuEnabled, bool IsLengthClockStep)
        {
            switch (address)
            {
                case 0xFF20: // NR41 - Length timer
                    // Store length value for use when channel is triggered
                    _lengthValue = value & 0x3F;
                    _lengthCounter = _lengthMax - _lengthValue;
                    break;

                case 0xFF21: // NR42 - Envelope
                    _envelopeInitialVolume = (value >> 4) & 0x0F;
                    _envelopeDirection = (value >> 3) & 0x01;
                    _envelopePeriod = value & 0x07;
                    _dacEnabled = (value & 0xF8) != 0;
                    if (!_dacEnabled)
                    {
                        _enabled =false;
                    }
                    _envelopeEnabled = _envelopePeriod > 0;
                    break;

                case 0xFF22: // NR43 - Polynomial counter
                    _clockShift = (value >> 4) & 0x0F;
                    _widthMode = (value >> 3) & 0x01;
                    _divisor = value & 0x07;
                    _frequencyTimerPeriod = CalculateFrequencyTimerPeriod();
                    break;

                case 0xFF23: // NR44 - Counter/consecutive; initial
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
            int divisorValue = _divisor == 0 ? 8 : _divisor * 16;
            return divisorValue << _clockShift;
        }

        public override void PowerOff()
        {
            base.PowerOff();
            _lfsr = 0;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _clockShift = 0;
            _widthMode = 0;
            _divisor = 0;
            _lengthValue = 0;
        }

        public override void Reset()
        {
            base.Reset();
            _lfsr = 0x7FFF;
            _frequencyTimer = 0;
            _frequencyTimerPeriod = 0;
            _clockShift = 0;
            _widthMode = 0;
            _divisor = 0;
        }
    }
}
