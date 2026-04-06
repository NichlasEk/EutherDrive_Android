using System;

namespace EutherDrive.Core.GbEmu
{
    internal class Timer
    {
        [NonSerialized]
        private readonly Cpu _cpu;

        // Internal counters to track cycles for DIV and TIMA registers.
        // The Game Boy hardware timers increment at specific cycle intervals, not every cycle.
        private int _divCounter;
        private int _timaCounter;

        // Timer registers
        private byte _div;   // 0xFF04 - Divider Register
        private byte _tima;  // 0xFF05 - Timer Counter
        private byte _tma;   // 0xFF06 - Timer Modulo
        private byte _tac;   // 0xFF07 - Timer Control

        public Timer(Cpu cpu)
        {
            _cpu = cpu;
            _divCounter = 0;
            _timaCounter = 0;
            
            // Initialize timer registers to default values
            _div = 0x00;
            _tima = 0x00;
            _tma = 0x00;
            _tac = 0x00;
        }

        // Timer register read methods
        public byte ReadDIV() => _div;
        public byte ReadTIMA() => _tima;
        public byte ReadTMA() => _tma;
        public byte ReadTAC() => _tac;

        public string GetDebugState()
        {
            return $"div=0x{_div:X2} tima=0x{_tima:X2} tma=0x{_tma:X2} tac=0x{_tac:X2} divCnt={_divCounter} timaCnt={_timaCounter}";
        }

        // Timer register write methods
        public void WriteDIV(byte value)
        {
            // Writing to DIV resets it to 0x00
            _div = 0x00;
            _divCounter = 0; // Reset the divider counter as well
        }

        public void WriteTIMA(byte value)
        {
            _tima = value;
        }

        public void WriteTMA(byte value)
        {
            _tma = value;
        }

        public void WriteTAC(byte value)
        {
            _tac = value;
        }

        /// <summary>
        /// This method should be called for every T-cycle (4 M-cycles).
        /// It updates the internal state of the timers.
        /// </summary>
        public void Tick(int cycles)
        {
            // --- DIV Register (Divider) ---
            // The DIV register increments at a fixed frequency of 16384 Hz.
            // The Game Boy CPU clock is 4194304 Hz.
            // So, DIV increments every 4194304 / 16384 = 256 T-cycles.
            _divCounter += cycles;
            while (_divCounter >= 256)
            {
                _divCounter -= 256;
                _div++; // Increment the internal DIV register
            }

            // --- TIMA Register (Timer Counter) ---
            // First, check if the timer is enabled in the TAC register (bit 2).
            if ((_tac & 0b0000_0100) == 0) // Is bit 2 zero?
            {
                // Timer is disabled.
                return;
            }

            _timaCounter += cycles;

            // Determine the frequency for TIMA increment from the TAC register (bits 1-0).
            int threshold = GetTimaThreshold(_tac);

            while (_timaCounter >= threshold)
            {
                _timaCounter -= threshold;

                if (_tima == 0xFF) // Is TIMA about to overflow?
                {
                    // Overflow occurred. Reset TIMA to the value in TMA.
                    _tima = _tma;

                    // Request a Timer Interrupt.
                    _cpu.RequestInterrupt(Cpu.Interrupt.Timer);
                }
                else
                {
                    // No overflow, just increment TIMA.
                    _tima++;
                }
            }
        }

        /// <summary>
        /// Gets the number of T-cycles required for one TIMA increment
        /// based on the value of the TAC register.
        /// </summary>
        private int GetTimaThreshold(byte tac)
        {
            switch (tac & 0b0000_0011) // Check bits 1-0
            {
                case 0b00: return 1024; //  4096 Hz (4194304 / 4096)
                case 0b01: return 16;   // 262144 Hz (4194304 / 262144)
                case 0b10: return 64;   //  65536 Hz (4194304 / 65536)
                case 0b11: return 256;  //  16384 Hz (4194304 / 16384)
                default: return 1024; // Should not happen
            }
        }
    }
}
