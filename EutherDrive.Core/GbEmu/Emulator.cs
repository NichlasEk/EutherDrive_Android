// Emulator.cs
using System.Text;
using Serilog;

namespace EutherDrive.Core.GbEmu
{
    public static class GameboyConstants
    {
        public const int ScreenWidth = 160;
        public const int ScreenHeight = 144;

        public const double CpuClockSpeed = 4194304; // 4.19 MHz
        public const int CyclesPerFrame = 70224; // 154 scanlines × 456 T-cycles
        public const double FramesPerSecond = CpuClockSpeed / CyclesPerFrame; // ~59.7275
    }

    internal sealed class Emulator : IDisposable
    {
        public readonly Cpu Cpu;
        public readonly Mmu Mmu;
        public readonly Ppu Ppu;
        public readonly Timer Timer;
        public readonly Joypad Joypad;

        public readonly Apu Apu;
        
        private bool _disposed = false;
        private Action<char> _serialDataHandler;
        private short[] _audioBuffer = Array.Empty<short>();
        private int _audioBufferCount;

        public readonly StringBuilder SerialLog = new();
        public bool IsPaused => Cpu.IsPaused;

        public Emulator()
        {
            // Initialize components in the correct order
            Cpu = new Cpu(null); // MMU is set later
            Apu = new Apu(); 
            Joypad = new Joypad(Cpu);
            Timer = new Timer(Cpu);
            Ppu = new Ppu(null, Cpu); // MMU is set later
            Mmu = new Mmu(Joypad, Ppu, Timer, Cpu, Apu);

             // Link components
            Cpu.SetMmu(Mmu);
            Ppu.SetMmu(Mmu);
            Apu.AudioBufferReady += OnAudioBufferReady;

            // Subscribe to events
            _serialDataHandler = c => SerialLog.Append(c);
            Mmu.OnSerialData += _serialDataHandler;            
        }

        public void LoadRom(string path)
        {
            var romData = File.ReadAllBytes(path);
            Mmu.LoadCartridge(romData);

             // Ensure APU is properly initialized after ROM loading
            Apu.EnsureInitialized();

            // Initialize CPU and hardware registers based on ROM type
            if (Mmu.IsGameBoyColor)
            {
                Cpu.InitializeForGbc();
                InitializeHardwareForGbc();
                Ppu.SetPostBootState(0x90, Ppu.PpuMode.VBlank);
            }
            else
            {
                Cpu.InitializeForDmg();
                InitializeHardwareForDmg();
                Ppu.SetPostBootState(0x00, Ppu.PpuMode.OamScan);
            }

            Cpu.Continue();
           
            Log.Information($"APU Status: {Apu.GetStatus()}");
        }

        public void SetInputState(bool up, bool down, bool left, bool right, bool a, bool b, bool start, bool select)
        {
            Joypad.SetState(up, down, left, right, a, b, start, select);
        }

        /// <summary>
        /// Gets comprehensive audio system status for debugging.
        /// </summary>
        public string GetAudioStatus()
        {
            var apuStatus = Apu.GetStatus();
            return $"APU: {apuStatus} | Pending Samples: {_audioBufferCount / 2}";
        }

        public void RunFrame()
        {
            int cyclesThisFrame = 0;
            while (cyclesThisFrame < GameboyConstants.CyclesPerFrame)
            {
                if (Cpu.IsPaused)
                {
                    // Don't burn CPU cycles if the emulator is paused
                    Thread.Sleep(16);
                    return;
                }

                int cycles = Cpu.Step();

                // PPU and Timer cycles are based on CPU speed mode
                int machineCycles = Mmu.IsDoubleSpeedMode ? cycles / 2 : cycles;

                Ppu.Step(machineCycles);
                Timer.Tick(machineCycles);
                Apu.Step(machineCycles);
                
                cyclesThisFrame += machineCycles;
            }
        }

        public ReadOnlySpan<short> ConsumeAudioBuffer()
        {
            ReadOnlySpan<short> result = _audioBuffer.AsSpan(0, _audioBufferCount);
            _audioBufferCount = 0;
            return result;
        }

        public void ResetRuntimeBuffers()
        {
            _audioBufferCount = 0;
            if (_audioBuffer.Length > 0)
                Array.Clear(_audioBuffer, 0, _audioBuffer.Length);
        }

        public string GetDebugState()
        {
            return $"{Cpu.GetDebugState()} | {Ppu.GetDebugState()} | {Timer.GetDebugState()} | {Joypad.GetDebugState()}";
        }

        private void OnAudioBufferReady(short[] leftChannel, short[] rightChannel)
        {
            int samples = Math.Min(leftChannel.Length, rightChannel.Length);
            if (samples <= 0)
                return;

            int required = _audioBufferCount + (samples * 2);
            if (_audioBuffer.Length < required)
                Array.Resize(ref _audioBuffer, Math.Max(required, _audioBuffer.Length == 0 ? 2048 : _audioBuffer.Length * 2));

            int dst = _audioBufferCount;
            for (int i = 0; i < samples; i++)
            {
                _audioBuffer[dst++] = leftChannel[i];
                _audioBuffer[dst++] = rightChannel[i];
            }

            _audioBufferCount = dst;
        }

        private void InitializeHardwareForGbc()
        {
            // This function writes the initial values to I/O registers
            // as if the GBC boot ROM had just finished running.
            // Using the IORegisters class makes this much more readable.
            Mmu.WriteByte(IORegisters.LCDC, 0x91); // LCD On, BG/Win On, OBJ On
            Mmu.WriteByte(IORegisters.STAT, 0x85); // Initial mode is V-Blank or H-Blank
            Mmu.WriteByte(IORegisters.LY, 0x90);   // The boot ROM finishes with LY at 0x90 (144)
            Mmu.WriteByte(IORegisters.BGP, 0xFC);  // BG Palette Data for DMG mode
            Mmu.WriteByte(IORegisters.OBP0, 0xFF);
            Mmu.WriteByte(IORegisters.OBP1, 0xFF);
            Mmu.WriteByte(IORegisters.VBK, 0xFF);  // VRAM Bank Select - Bank 0
            Mmu.WriteByte(IORegisters.SVBK, 0xFF); // WRAM Bank Select - Bank 1

            // Sound registers
            Mmu.WriteByte(0xFF10, 0x80); // NR10
            Mmu.WriteByte(0xFF11, 0xBF); // NR11
            Mmu.WriteByte(0xFF12, 0xF3); // NR12
            Mmu.WriteByte(0xFF14, 0xBF); // NR14
            Mmu.WriteByte(0xFF26, 0xF1); // NR52

            Mmu.WriteByte(IORegisters.P1_JOYP, 0xCF);
            Mmu.WriteByte(IORegisters.IF, 0xE1);
            Mmu.WriteByte(IORegisters.IE, 0x00);
        }

        private void InitializeHardwareForDmg()
        {
            // Approximate DMG post-boot register state when no boot ROM is executed.
            Mmu.WriteByte(IORegisters.LCDC, 0x91);
            Mmu.WriteByte(IORegisters.STAT, 0x85);
            Mmu.WriteByte(IORegisters.SCY, 0x00);
            Mmu.WriteByte(IORegisters.SCX, 0x00);
            Mmu.WriteByte(IORegisters.BGP, 0xFC);
            Mmu.WriteByte(IORegisters.OBP0, 0xFF);
            Mmu.WriteByte(IORegisters.OBP1, 0xFF);
            Mmu.WriteByte(IORegisters.WY, 0x00);
            Mmu.WriteByte(IORegisters.WX, 0x00);

            // Sound registers roughly matching DMG boot ROM handoff.
            Mmu.WriteByte(0xFF10, 0x80);
            Mmu.WriteByte(0xFF11, 0xBF);
            Mmu.WriteByte(0xFF12, 0xF3);
            Mmu.WriteByte(0xFF14, 0xBF);
            Mmu.WriteByte(0xFF16, 0x3F);
            Mmu.WriteByte(0xFF17, 0x00);
            Mmu.WriteByte(0xFF19, 0xBF);
            Mmu.WriteByte(0xFF1A, 0x7F);
            Mmu.WriteByte(0xFF1B, 0xFF);
            Mmu.WriteByte(0xFF1C, 0x9F);
            Mmu.WriteByte(0xFF1E, 0xBF);
            Mmu.WriteByte(0xFF20, 0xFF);
            Mmu.WriteByte(0xFF21, 0x00);
            Mmu.WriteByte(0xFF22, 0x00);
            Mmu.WriteByte(0xFF23, 0xBF);
            Mmu.WriteByte(0xFF24, 0x77);
            Mmu.WriteByte(0xFF25, 0xF3);
            Mmu.WriteByte(0xFF26, 0xF1);

            Mmu.WriteByte(IORegisters.P1_JOYP, 0xCF);
            Mmu.WriteByte(IORegisters.IF, 0xE1);
            Mmu.WriteByte(IORegisters.IE, 0x00);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Unsubscribe from events
                    if (Mmu != null && _serialDataHandler != null)
                    {
                        Mmu.OnSerialData -= _serialDataHandler;
                    }
                    
                    Apu.AudioBufferReady -= OnAudioBufferReady;
                    
                    // Dispose managed resources
                    SerialLog?.Clear();
                }
                _disposed = true;
            }
        }
    }
}
