using System;
using System.IO;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Manages the Game Boy's Audio Processing Unit (APU).
    /// The APU is responsible for generating all sound effects and music.
    /// </summary>
    internal class Apu
    {
        // --- APU Timing and Constants ---
        private const int SampleRate = 44100;

        // It's correct to always use base clock speed and not double speed for this.
        private const double CyclesPerSample = GameboyConstants.CpuClockSpeed / SampleRate;

        // --- Frame Sequencer ---
        // The frame sequencer generates clocks for length, envelope, and sweep units.
        // It runs at 512 Hz.
        private const int FrameSequencerCycles = (int)(GameboyConstants.CpuClockSpeed / 512);
        private int _frameSequencerCounter = 0;
        private int _frameSequencerStep = 0;

        // --- Audio Buffer (16-bit stereo) ---
        public Action<short[], short[]> AudioBufferReady; // Left and Right channels as 16-bit
        private const int BufferSize = 512; // or 256
        private short[] _leftChannelBuffer = new short[BufferSize];
        private short[] _rightChannelBuffer = new short[BufferSize];
        private int _bufferIndex = 0;
        private double _apuCycleCounter = 0;

        // Audio Processing State (DC blocking filter) ---
        private float _dcBlockPrevXLeft = 0.0f;
        private float _dcBlockPrevYLeft = 0.0f;
        private float _dcBlockPrevXRight = 0.0f;
        private float _dcBlockPrevYRight = 0.0f;

        // --- APU Sound Channels ---
        private PulseWithSweepChannel _channel1; // Pulse A (with sweep)
        private PulseChannel _channel2; // Pulse B
        private WaveChannel _channel3; // Wave
        private NoiseChannel _channel4; // Noise

        internal bool IsLengthClockStep => (_frameSequencerStep % 2 == 0);


        // --- APU Master Control Registers ---
        private byte _nr50; // 0xFF24 - Channel control / On-Off / Volume
        private byte _nr51; // 0xFF25 - Sound output terminal selection
        private byte _nr52; // 0xFF26 - Sound on/off

        public Apu()
        {
            _channel1 = new PulseWithSweepChannel();
            _channel2 = new PulseChannel();
            _channel3 = new WaveChannel();
            _channel4 = new NoiseChannel();
            
            // Initialize APU as disabled by default (Game Boy behavior)
            _nr52 = 0x00; // APU disabled initially
            _nr50 = 0x00; // Master volume at minimum
            _nr51 = 0x00; // All channels disabled
        }

        /// <summary>
        /// Steps the APU by the given number of CPU cycles.
        /// </summary>
        public void Step(int cpuCycles)
        {
            // --- Frame Sequencer Logic ---
            _frameSequencerCounter += cpuCycles;
            while (_frameSequencerCounter >= FrameSequencerCycles)
            {
                _frameSequencerCounter -= FrameSequencerCycles;

                // Step the sequencer (0 through 7)
                _frameSequencerStep = (_frameSequencerStep + 1) % 8;

                // Clock Length Counters on steps 0, 2, 4, 6
                if (_frameSequencerStep % 2 == 0)
                {
                    _channel1.TickLength();
                    _channel2.TickLength();
                    _channel3.TickLength();
                    _channel4.TickLength();
                }

                // Clock Volume Envelopes on step 7
                if (_frameSequencerStep == 7)
                {
                    _channel1.TickEnvelope();
                    _channel2.TickEnvelope();
                    _channel4.TickEnvelope();
                }

                // Clock Sweep on step 2 and 6 (ONLY Channel 1)
                if (_frameSequencerStep == 2 || _frameSequencerStep == 6)
                {
                    _channel1.TickSweep();
                }
            }

            // --- Update Channel Frequency Timers ---
            _channel1.UpdateFrequencyTimer(cpuCycles);
            _channel2.UpdateFrequencyTimer(cpuCycles);
            _channel3.UpdateFrequencyTimer(cpuCycles);
            _channel4.UpdateFrequencyTimer(cpuCycles);

            // --- Sound Generation and Buffering ---
            _apuCycleCounter += cpuCycles;
            while (_apuCycleCounter >= CyclesPerSample)
            {
                _apuCycleCounter -= CyclesPerSample;

                // Only generate audio if APU is enabled and we have an audio callback
                if ((_nr52 & 0x80) == 0 || AudioBufferReady == null)
                {
                    // APU is disabled or no audio callback - generate silence
                    _leftChannelBuffer[_bufferIndex] = 0;
                    _rightChannelBuffer[_bufferIndex] = 0;
                    _bufferIndex++;
                    
                    if (_bufferIndex >= BufferSize)
                    {
                        AudioBufferReady?.Invoke(_leftChannelBuffer, _rightChannelBuffer);
                        _bufferIndex = 0;
                    }
                    return;
                }

                // 1. Get sample from each channel
                float sample1 = _channel1.GetSample();
                float sample2 = _channel2.GetSample();
                float sample3 = _channel3.GetSample();
                float sample4 = _channel4.GetSample();

                // 2. Mix samples for left and right outputs based on NR51 register
                float mixedLeft = 0.0f;
                float mixedRight = 0.0f;

                // Channel 1 (Pulse A)
                if ((_nr51 & 0b00010000) != 0) { mixedLeft += sample1; }
                if ((_nr51 & 0b00000001) != 0) { mixedRight += sample1; }

                // Channel 2 (Pulse B)
                if ((_nr51 & 0b00100000) != 0) { mixedLeft += sample2; }
                if ((_nr51 & 0b00000010) != 0) { mixedRight += sample2; }

                // Channel 3 (Wave)
                if ((_nr51 & 0b01000000) != 0) { mixedLeft += sample3; }
                if ((_nr51 & 0b00000100) != 0) { mixedRight += sample3; }

                // Channel 4 (Noise)
                if ((_nr51 & 0b10000000) != 0) { mixedLeft += sample4; }
                if ((_nr51 & 0b00001000) != 0) { mixedRight += sample4; }

                // 3. Divide by fixed channel count (4) like real hardware
                float avgLeft = mixedLeft / 4.0f;
                float avgRight = mixedRight / 4.0f;

                // 4. Apply master volume (NR50)
                // Hardware maps volume 0-7 to multipliers 1-8
                int leftVolume = ((_nr50 >> 4) & 0b00000111);
                int rightVolume = (_nr50 & 0b00000111);

                float processedLeft = avgLeft * ((leftVolume + 1) / 8.0f);
                float processedRight = avgRight * ((rightVolume + 1) / 8.0f);

                // 5. Apply DC blocking high-pass filter
                processedLeft = DcBlock(processedLeft, ref _dcBlockPrevXLeft, ref _dcBlockPrevYLeft);
                processedRight = DcBlock(processedRight, ref _dcBlockPrevXRight, ref _dcBlockPrevYRight);
                
                // 6. Apply soft clipping to prevent harsh distortion
                processedLeft = SoftClip(processedLeft);
                processedRight = SoftClip(processedRight);

                // 7. Convert float sample to 16-bit integer and buffer it
                _leftChannelBuffer[_bufferIndex] = FloatToI16(processedLeft);
                _rightChannelBuffer[_bufferIndex] = FloatToI16(processedRight);

                _bufferIndex++;

                // 8. If buffer is full, send it to the audio device
                if (_bufferIndex >= BufferSize)
                {
                    AudioBufferReady?.Invoke(_leftChannelBuffer, _rightChannelBuffer);
                    _bufferIndex = 0;
                }
            }
        }
        
        /// <summary>
        /// Reads from an APU I/O register.
        /// </summary>
        public byte ReadRegister(ushort address)
        {
            // Handle master control registers
            switch (address)
            {
                case 0xFF24: return _nr50;
                case 0xFF25: return _nr51;
                case 0xFF26: 
                    // Reading NR52 returns the on/off status in the top bit,
                    // and the status of each channel in the lower 4 bits.
                    byte status = (byte)(_nr52 & 0b1000_0000);
                    if (_channel1.IsActiveForStatus) status |= 0b0000_0001;
                    if (_channel2.IsActiveForStatus) status |= 0b0000_0010;
                    if (_channel3.IsActiveForStatus) status |= 0b0000_0100;
                    if (_channel4.IsActiveForStatus) status |= 0b0000_1000;

                    // Set unused bits (4, 5, 6) to 1, which is what the hardware does.
                    return (byte)(status | 0b0111_0000);
            }

            // Delegate to channel-specific read methods
            if (address >= 0xFF10 && address <= 0xFF14)
                return _channel1.ReadRegister(address);
            if (address >= 0xFF15 && address <= 0xFF19)
                return _channel2.ReadRegister(address);
            if (address >= 0xFF1A && address <= 0xFF1E)
                return _channel3.ReadRegister(address);
            if (address >= 0xFF20 && address <= 0xFF23)
                return _channel4.ReadRegister(address);
            if (address >= 0xFF30 && address <= 0xFF3F)
                return _channel3.ReadWaveTable(address);

            return 0xFF;
        }

        /// <summary>
        /// Writes to an APU I/O register.
        /// </summary>
        public void WriteRegister(ushort address, byte value)
        {
            // NR52 is always writable
            if (address == 0xFF26)
            {
                bool wasApuEnabled = (_nr52 & 0x80) != 0;
                _nr52 = (byte)(value & 0x80); // Only bit 7 is writable
                bool isApuNowEnabled = (_nr52 & 0x80) != 0;

                // When APU is turned OFF, all registers are cleared.
                if (wasApuEnabled && !isApuNowEnabled)
                {
                    // Reset all registers and channels
                    // Note: This logic seems to be slightly wrong in the original code.
                    // When the APU is turned on, registers are not cleared.
                    // When it is turned off, they are.
                    _nr50 = 0;
                    _nr51 = 0;
                    _channel1.PowerOff();
                    _channel2.PowerOff();
                    _channel3.PowerOff();
                    _channel4.PowerOff();
                }
                // When turning ON: next FS step must be 0; clear CH3 sample buffer
                if (!wasApuEnabled && isApuNowEnabled)
                {
                    _frameSequencerStep = 7;   // so the next 512 Hz tick enters step 0
                    _frameSequencerCounter = 0;
                    //_channel3.OnApuPowerOn();
                }
                return;
            }

             // Wave RAM is always writable regardless of APU power state
            if (address >= 0xFF30 && address <= 0xFF3F)
            {
                _channel3.WriteWaveTable(address, value);
                return;
            }

            bool isApuEnabled = (_nr52 & 0x80) != 0;

            // The length register portion of NR11, NR21, NR31, NR41 is always writable.
            // For simplicity, we can allow the whole register to be written.
            bool isLengthRegister = address == 0xFF11 || address == 0xFF16 || address == 0xFF1B || address == 0xFF20;

            if (!isApuEnabled && !isLengthRegister)
            {
                // Don't allow writing to most registers if APU is off.
                return;
            }

            // Delegate writes to channel-specific methods
            if (address >= 0xFF10 && address <= 0xFF14)
            {
                _channel1.WriteRegister(address, value, isApuEnabled, IsLengthClockStep);
                return;
            }
            if (address >= 0xFF15 && address <= 0xFF19)
            {
                _channel2.WriteRegister(address, value, isApuEnabled, IsLengthClockStep);
                return;
            }
            if (address >= 0xFF1A && address <= 0xFF1E)
            {
                _channel3.WriteRegister(address, value, isApuEnabled, IsLengthClockStep);
                return;
            }
            if (address >= 0xFF20 && address <= 0xFF23)
            {
                _channel4.WriteRegister(address, value, isApuEnabled, IsLengthClockStep);
                return;
            }

            switch (address)
            {
                // --- Master Control ---
                case 0xFF24: // NR50 - Master volume and VIN panning
                    _nr50 = value;
                    break;
                case 0xFF25: // NR51 - Channel panning
                    _nr51 = value;
                    break;
            }
        }

        /// <summary>
        /// A high-pass filter to remove DC offset from the signal.
        /// Standard DC blocker: y[n] = x[n] - x[n-1] + R * y[n-1]
        /// </summary>
        internal static float DcBlock(float x, ref float prevX, ref float prevY)
        {
            // R ≈ 0.997 gives ~20 Hz cutoff at 44.1 kHz
            const float R = 0.997f;
            float y = x - prevX + R * prevY;
            prevX = x;
            prevY = y;
            return y;
        }

        /// <summary>
        /// A gentle saturation function to prevent harsh digital clipping.
        /// It smoothly compresses peaks that exceed the [-1, 1] range.
        /// </summary>
        private static float SoftClip(float x)
        {
            const float a = 0.95f;
            if (x > a) return a + (x - a) / (1f + (x - a) * (x - a));
            if (x < -a) return -a + (x + a) / (1f + (x + a) * (x + a));
            return x;
        }

        /// <summary>
        /// Converts a float sample from [-1, 1] to a 16-bit signed integer.
        /// </summary>
        private static short FloatToI16(float x)
        {
            // Clamp and scale to the full range of short.
            if (x > 1.0f) x = 1.0f;
            else if (x < -1.0f) x = -1.0f;
            return (short)Math.Round(x * 32767.0f);
        }

        /// <summary>
        /// Ensures the APU is properly initialized and enabled.
        /// This should be called after the emulator is fully set up.
        /// </summary>
        public void EnsureInitialized()
        {
            // If APU is not enabled, enable it with default settings
            if ((_nr52 & 0x80) == 0)
            {
                _nr52 = 0x80; // Enable APU
                _nr50 = 0x77; // Set reasonable volume levels
                _nr51 = 0xFF; // Enable all channels
            }
        }

        /// <summary>
        /// Gets the current APU status for debugging.
        /// </summary>
        public string GetStatus()
        {
            return $"APU Enabled: {(_nr52 & 0x80) != 0}, " +
                   $"Master Volume: L={(_nr50 >> 4) & 0x07}, R={_nr50 & 0x07}, " +
                   $"Channel Panning: {_nr51:X2}, " +
                   $"Channels: 1={_channel1.IsEnabled}, 2={_channel2.IsEnabled}, 3={_channel3.IsEnabled}, 4={_channel4.IsEnabled}";
        }
    }
}