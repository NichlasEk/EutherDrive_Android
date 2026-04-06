using System;
using System.Text;
using Serilog;

namespace EutherDrive.Core.GbEmu
{
    public class WatchedMemory
    {
        private byte[] _memory;
        public event Action<int, byte, byte> ValueChanged;
        // args: index, oldValue, newValue

        public WatchedMemory(int size)
        {
            _memory = new byte[size];
        }

        public byte this[int index]
        {
            get => _memory[index];
            set
            {
                var oldValue = _memory[index];
                if (oldValue != value)
                {
                    _memory[index] = value;
                    ValueChanged?.Invoke(index, oldValue, value);
                }
            }
        }
    
       // Length property
    public int Length => _memory.Length;

    // Expose underlying array for Array.Copy
    public byte[] ToArray() => _memory;
}

    internal class Mmu
    {
        //internal byte[] _memory = new byte[0x10000]; // 64KB address space

        internal WatchedMemory _memory;
        [NonSerialized]
        private Joypad _joypad;
        [NonSerialized]
        private Ppu _ppu;
        [NonSerialized]
        private Timer _timer;
        [NonSerialized]
        private Cpu _cpu;
        private IMbc _mbc;
        [NonSerialized]
        private Apu _apu;


        // Game Boy Color specific fields
        private bool _isGameBoyColor = false;
        private byte _wramBank = 1; // Current WRAM bank (1-7, bank 0 is fixed)
        private byte _vramBank = 0; // Current VRAM bank (0-1)
        private bool _doubleSpeedMode = false;
        private bool _speedSwitchRequested = false;
        
        // GBC WRAM banks (8 banks of 4KB each = 32KB total)
        private byte[][] _wramBanks = new byte[8][];
        
        // GBC VRAM banks (2 banks of 8KB each = 16KB total)
        private byte[][] _vramBanks = new byte[2][];
        
        // GBC color palettes are now handled by the PPU
        
        // GBC HDMA registers
        private byte _hdmaSourceHigh = 0;    // 0xFF51
        private byte _hdmaSourceLow = 0;     // 0xFF52
        private byte _hdmaDestHigh = 0;      // 0xFF53
        private byte _hdmaDestLow = 0;       // 0xFF54
        private byte _hdmaLengthMode = 0;    // 0xFF55

        private bool _hdmaActive = false;
        private ushort _hdmaSource;
        private ushort _hdmaDestination;
        private int _hdmaRemainingBytes;
        
        // GBC infrared register
        private byte _infraredPort = 0;      // 0xFF56

        // An event to notify subscribers (like our UI) when a character is "printed".
        public event Action<char> OnSerialData;

        /// <summary>
        /// Gets information about the current MBC for debugging
        /// </summary>
        public string GetMbcInfo()
        {
            if (_mbc == null)
            {
                return "No MBC loaded";
            }

            // Use detailed info if available (for MBC3 with RTC)
            if (_mbc is Mbc3 mbc3)
            {
                return mbc3.GetDetailedInfo();
            }

            // Show rumble status for MBC5
            if (_mbc is Mbc5 mbc5)
            {
                var info = $"{_mbc.GetType().Name}: ROM Bank {_mbc.CurrentRomBank}, RAM Bank {_mbc.CurrentRamBank}, RAM {( _mbc.IsRamEnabled ? "Enabled" : "Disabled")}";
                if (mbc5.IsRumbleEnabled)
                {
                    info += ", Rumble ON";
                }
                return info;
            }

            return $"{_mbc.GetType().Name}: ROM Bank {_mbc.CurrentRomBank}, RAM Bank {_mbc.CurrentRamBank}, RAM {( _mbc.IsRamEnabled ? "Enabled" : "Disabled")}";
        }

        // Expose speed switch properties for the CPU
        public bool IsSpeedSwitchRequested() => _speedSwitchRequested;
        public void PerformSpeedSwitch()
        {
            if (_speedSwitchRequested)
            {
                _doubleSpeedMode = !_doubleSpeedMode;
                _speedSwitchRequested = false;
                Log.Information($"CPU speed switched. Double Speed: {_doubleSpeedMode}");
            }
        }

        private void PerformDmaChunk()
        {
            // Transfer one 16-byte block
            for (int i = 0; i < 0x10; i++)
            {
                // Read from source and write to VRAM destination
                // Using ReadByte/WriteByte ensures MBCs and other memory behaviors are respected
                WriteByte(_hdmaDestination, ReadByte(_hdmaSource));
                _hdmaSource++;
                _hdmaDestination++;
            }

            _hdmaRemainingBytes -= 0x10;

            // Check if the transfer is complete
            if (_hdmaRemainingBytes <= 0)
            {
                _hdmaActive = false;
                _hdmaLengthMode = 0xFF; // Set to 0xFF on completion
                _hdmaRemainingBytes = 0;
                Log.Debug("HDMA transfer finished.");
            }
            else
            {
                // Update the length register with remaining blocks
                // The value is (remaining blocks - 1)
                _hdmaLengthMode = (byte)((_hdmaRemainingBytes / 0x10) - 1);
            }
        }

        /// <summary>
        /// Gets whether the emulator is running in Game Boy Color mode
        /// </summary>
        public bool IsGameBoyColor => _isGameBoyColor;

        public void ForceGBC()
        {
            _isGameBoyColor = true;
             _ppu.SetGbcMode(true);
        }

        /// <summary>
        /// Gets the current WRAM bank (GBC only)
        /// </summary>
        public byte CurrentWramBank => _wramBank;

        /// <summary>
        /// Gets the current VRAM bank (GBC only)
        /// </summary>
        public byte CurrentVramBank => _vramBank;

        /// <summary>
        /// Gets whether double-speed mode is active (GBC only)
        /// </summary>
        public bool IsDoubleSpeedMode => _doubleSpeedMode;

        /// <summary>
        /// Reads a byte from WRAM with proper banking support (GBC)
        /// </summary>
        private byte ReadWramByte(ushort address)
        {
            if (_isGameBoyColor)
            {
                if (address >= 0xC000 && address <= 0xCFFF)
                {
                    // Bank 0 (fixed) - 0xC000-0xCFFF
                    return _memory[address];
                }
                else if (address >= 0xD000 && address <= 0xDFFF)
                {
                    // Current bank - 0xD000-0xDFFF
                    int bankIndex = _wramBank;
                    int offset = address - 0xD000;
                    return _wramBanks[bankIndex][offset];
                }
            }
            
            // Fallback to regular memory access
            return _memory[address];
        }

        /// <summary>
        /// Writes a byte to WRAM with proper banking support (GBC)
        /// </summary>
        private void WriteWramByte(ushort address, byte value)
        {
            if (_isGameBoyColor)
            {
                if (address >= 0xC000 && address <= 0xCFFF)
                {
                    // Bank 0 (fixed) - 0xC000-0xCFFF
                    _memory[address] = value;
                }
                else if (address >= 0xD000 && address <= 0xDFFF)
                {
                    // Current bank - 0xD000-0xDFFF
                    int bankIndex = _wramBank;
                    int offset = address - 0xD000;
                    _wramBanks[bankIndex][offset] = value;
                }
                else
                {
                    _memory[address] = value;
                }
            }
            else
            {
                _memory[address] = value;
            }
        }

        /// <summary>
        /// Reads a byte from VRAM with proper banking support (GBC)
        /// </summary>
        private byte ReadVramByte(ushort address)
        {
            if (_isGameBoyColor)
            {
                // VRAM is 16KB total, split into two 8KB banks
                int bankIndex = _vramBank;
                int offset = address - VRAM_START;
                return _vramBanks[bankIndex][offset];
            }
            
            // Fallback to regular memory access
            return _memory[address];
        }

        public byte ReadVram(ushort address, int bankIndex)
    {
        // Ensure the address is within the valid VRAM range.
        if (address < 0x8000 || address > 0x9FFF)
        {
            // This case should ideally not be hit if the PPU logic is correct.
            // You can log an error here if you want.
            return 0xFF;
        }

        int offset = address - VRAM_START;
        return _vramBanks[bankIndex][offset];
    }

        /// <summary>
        /// Writes a byte to VRAM with proper banking support (GBC)
        /// </summary>
        private void WriteVramByte(ushort address, byte value)
        {
            if (_isGameBoyColor)
            {
                // VRAM is 16KB total, split into two 8KB banks
                int bankIndex = _vramBank;
                int offset = address - VRAM_START;
                _vramBanks[bankIndex][offset] = value;
            }
            else
            {
                _memory[address] = value;
            }
        }

        // Memory region constants
        private const ushort ROM_START = 0x0000;
        private const ushort ROM_END = 0x7FFF;
        private const ushort VRAM_START = 0x8000;
        private const ushort VRAM_END = 0x9FFF;
        private const ushort EXTERNAL_RAM_START = 0xA000;
        private const ushort EXTERNAL_RAM_END = 0xBFFF;
        private const ushort WORK_RAM_START = 0xC000;
        private const ushort WORK_RAM_END = 0xDFFF;
        private const ushort ECHO_RAM_START = 0xE000;
        private const ushort ECHO_RAM_END = 0xFDFF;
        private const ushort OAM_START = 0xFE00;
        private const ushort OAM_END = 0xFE9F;
        private const ushort UNUSABLE_START = 0xFEA0;
        private const ushort UNUSABLE_END = 0xFEFF;
        private const ushort IO_REGISTERS_START = 0xFF00;
        private const ushort IO_REGISTERS_END = 0xFF7F;
        private const ushort HIGH_RAM_START = 0xFF80;
        private const ushort HIGH_RAM_END = 0xFFFE;
        private const ushort INTERRUPT_ENABLE = 0xFFFF;

        private bool bootRomEnabled = false;

        public Mmu(Joypad joypad, Ppu ppu, Timer timer, Cpu cpu, Apu apu)
        {
            _joypad = joypad;
            _ppu = ppu;
            _timer = timer;
            _cpu = cpu;
            _apu = apu;

            // Initialize memory for Game Boy Color support
            // 64KB main memory + 32KB WRAM banks + 16KB VRAM banks = 112KB total
            _memory = new WatchedMemory(256000); // Keep existing size for now, will optimize later
            _memory.ValueChanged += (index, oldVal, newVal) =>
            {
                if (index == 0x0038)
                {
                    Log.Warning($"Memory[{index}] changed from {oldVal:X2} to {newVal:X2}");
                }
            };

            InitializeMemory();
            InitializeGbcMemory();
        }

        private void InitializeMemory()
        {
            // Initialize memory with proper Game Boy values
            // Most uninitialized memory should contain 0xFF, but some areas have specific values

            // Fill entire memory with 0xFF (uninitialized memory value)
            for (int i = 0; i < _memory.Length; i++)
            {
                _memory[i] = 0xFF;
            }

            // Initialize specific memory regions with proper values

            // HRAM (High RAM) 0xFF80-0xFFFE should be initialized to 0x00
            for (int i = 0xFF80; i <= 0xFFFE; i++)
            {
                _memory[i] = 0x00;
            }
        }

        private void InitializeGbcMemory()
        {
            // Initialize GBC WRAM banks (8 banks of 4KB each)
            for (int bank = 0; bank < 8; bank++)
            {
                _wramBanks[bank] = new byte[4096]; // 4KB per bank
                
                // Initialize with 0xFF (uninitialized memory value)
                for (int i = 0; i < 4096; i++)
                {
                    _wramBanks[bank][i] = 0xFF;
                }
            }
            
            // Initialize GBC VRAM banks (2 banks of 8KB each)
            for (int bank = 0; bank < 2; bank++)
            {
                _vramBanks[bank] = new byte[8192]; // 8KB per bank
                
                // Initialize with 0x00 (VRAM initial value)
                for (int i = 0; i < 8192; i++)
                {
                    _vramBanks[bank][i] = 0x00;
                }
            }
        }

        public void LoadCartridge(byte[] romData)
        {
            if (romData == null || romData.Length == 0)
            {
                throw new ArgumentException("ROM data cannot be null or empty", nameof(romData));
            }

            // Parse ROM header to determine cartridge type
            var header = RomHeader.Parse(romData);
            Log.Information(header.ToString());
            Log.Information($"Loading cartridge: {header.Title} (Type: 0x{header.CartridgeType:X2})");

            // Detect Game Boy Color mode
            _isGameBoyColor = header.CgbFlag == "CGB Compatible" || header.CgbFlag == "CGB Only";
            Log.Information($"Game Boy Color mode: {(_isGameBoyColor ? "Enabled" : "Disabled")}");
            
            // Set PPU to GBC mode if this is a GBC game
            if (_isGameBoyColor)
            {
                _ppu.SetGbcMode(true);
            }

            // Create appropriate MBC based on cartridge type
            _mbc = CreateMbc(header.CartridgeType, romData, header.RamSize);

            Log.Information($"Cartridge loaded successfully with {_mbc.GetType().Name}");
            Log.Information("ROM loaded successfully ({ByteCount} bytes)", romData.Length);
        }

        public bool HasPersistentData => _mbc is IBatteryBackedMbc battery && battery.HasPersistentData;

        public void SavePersistentData(BinaryWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            if (_mbc is not IBatteryBackedMbc battery || !battery.HasPersistentData)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            battery.SavePersistentData(writer);
        }

        public void LoadPersistentData(BinaryReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            bool hasData = reader.ReadBoolean();
            if (!hasData)
                return;
            if (_mbc is not IBatteryBackedMbc battery)
                return;
            battery.LoadPersistentData(reader);
        }

        private IMbc CreateMbc(byte cartridgeType, byte[] romData, int ramSize)
        {
            switch (cartridgeType)
            {
                case 0x00: // ROM ONLY
                    return new RomOnly(romData);
                
                case 0x01: // MBC1
                    return new Mbc1(romData, ramSize);
                
                case 0x02: // MBC1 + RAM
                    return new Mbc1(romData, ramSize);
                
                case 0x03: // MBC1 + RAM + Battery
                    return new Mbc1(romData, ramSize);
                
                case 0x05: // MBC2
                    return new Mbc2(romData);
                
                case 0x06: // MBC2 + Battery
                    return new Mbc2(romData);
                
                case 0x0F: // MBC3 + Timer + Battery
                    return new Mbc3(romData, ramSize);
                
                case 0x10: // MBC3 + Timer + RAM + Battery
                    return new Mbc3(romData, ramSize);
                
                case 0x11: // MBC3
                    return new Mbc3(romData, ramSize);
                
                case 0x12: // MBC3 + RAM
                    return new Mbc3(romData, ramSize);
                
                case 0x13: // MBC3 + RAM + Battery
                    return new Mbc3(romData, ramSize);
                
                case 0x19: // MBC5
                    return new Mbc5(romData, ramSize, false);
                
                case 0x1A: // MBC5 + RAM
                    return new Mbc5(romData, ramSize, false);
                
                case 0x1B: // MBC5 + RAM + Battery
                    return new Mbc5(romData, ramSize, false);
                
                case 0x1C: // MBC5 + Rumble
                    return new Mbc5(romData, ramSize, true);
                
                case 0x1D: // MBC5 + Rumble + RAM
                    return new Mbc5(romData, ramSize, true);
                
                case 0x1E: // MBC5 + Rumble + RAM + Battery
                    return new Mbc5(romData, ramSize, true);
                
                default:
                    Log.Warning($"Unsupported cartridge type 0x{cartridgeType:X2}, falling back to ROM-only");
                    return new RomOnly(romData);
            }
        }
        
        /// <summary>
        /// Advances the H-Blank DMA transfer by one chunk (16 bytes).
        /// This should be called by the emulator's main loop every time the PPU enters H-Blank mode.
        /// </summary>
        public void TickHblankDma()
        {
            if (!_hdmaActive)
            {
                return;
            }

            // VRAM is accessible during H-Blank, so we can perform the transfer
            PerformDmaChunk();
        }

        public byte ReadByte(ushort address)
        {
            // Handle ROM area (0x0000-0x7FFF) with MBC
            if (address >= ROM_START && address <= ROM_END)
            {
                if (_mbc != null)
                {
                    return _mbc.ReadRom(address);
                }
                else
                {
                    // Fallback for when no MBC is loaded
                    if (address < _memory.Length)
                    {
                        return _memory[address];
                    }
                    else
                    {
                        return 0xFF;
                    }
                }
            }

            // Handle external RAM area (0xA000-0xBFFF) with MBC
            if (address >= EXTERNAL_RAM_START && address <= EXTERNAL_RAM_END)
            {
                if (_mbc != null)
                {
                    return _mbc.ReadRam(address);
                }
                else
                {
                    return 0xFF; // No external RAM without MBC
                }
            }

            // Handle I/O registers with special behavior
            if (address >= IO_REGISTERS_START && address <= IO_REGISTERS_END)
            {
                return ReadIORegister(address);
            }

            // Handle echo RAM mirroring
            if (address >= ECHO_RAM_START && address <= ECHO_RAM_END)
            {
                // In GBC mode, echo RAM mirrors the current WRAM bank
                if (_isGameBoyColor)
                {
                    // Echo RAM mirrors the current WRAM bank
                    ushort mirroredAddress = (ushort)(address - 0x2000);
                    if (mirroredAddress >= 0xD000 && mirroredAddress <= 0xDFFF)
                    {
                        // This mirrors the current WRAM bank (0xD000-0xDFFF)
                        return _memory[mirroredAddress];
                    }
                    else
                    {
                        // This mirrors bank 0 (0xC000-0xCFFF)
                        return _memory[mirroredAddress];
                    }
                }
                else
                {
                    return _memory[address - 0x2000]; // Mirror of 0xC000-0xDDFF
                }
            }

            // Handle unusable memory region
            if (address >= UNUSABLE_START && address <= UNUSABLE_END)
            {
                Log.Warning($"ReadByte: Address {address:X4} unusable");
                return 0xFF; // Unusable memory returns 0xFF
            }

            // Handle interrupt enable register
            if (address == INTERRUPT_ENABLE)
            {
                return _memory[address];
            }

            // Handle Video RAM with banking (GBC)
            if (address >= VRAM_START && address <= VRAM_END)
            {
                return ReadVramByte(address);
            }

            // Handle Work RAM with banking (GBC)
            if (address >= WORK_RAM_START && address <= WORK_RAM_END)
            {
                return ReadWramByte(address);
            }

            // Validate address bounds
            if (address >= _memory.Length)
            {
                Log.Warning($"ReadByte: Address {address:X4} out of bounds");
                return 0xFF;
            }

            return _memory[address];
        }

        private byte ReadIORegister(ushort address)
        {
            if (address >= 0xFF10 && address <= 0xFF26)
            {
                return _apu.ReadRegister(address);
            }
            
            if (address >= 0xFF30 && address <= 0xFF3F)
            {
                return _apu.ReadRegister(address);
            }

            switch (address)
            {
                case 0xFF00: // P1 - Joypad register
                    return _joypad.ReadP1();

                case 0xFF01: // SB - Serial transfer data
                    return _memory[address];

                case 0xFF02: // SC - Serial transfer control
                    return _memory[address];

                case 0xFF04: // DIV - Divider register
                    return _timer.ReadDIV();

                case 0xFF05: // TIMA - Timer counter
                    return _timer.ReadTIMA();

                case 0xFF06: // TMA - Timer modulo
                    return _timer.ReadTMA();

                case 0xFF07: // TAC - Timer control
                    return _timer.ReadTAC();

                case 0xFF0F: // IF - Interrupt flag
                    return _cpu.if_register;

                case 0xFFFF: // IE - Interrupt flag
                    return _cpu.ie_register;

                case 0xFF40: // LCDC - LCD control
                    return _ppu.ReadLCDC();

                case 0xFF41: // STAT - LCD status
                    return _ppu.ReadSTAT();

                case 0xFF42: // SCY - Scroll Y
                    return _ppu.ReadSCY();

                case 0xFF43: // SCX - Scroll X
                    return _ppu.ReadSCX();

                case 0xFF44: // LY - LCDC Y coordinate
                    return _ppu.ReadLY();

                case 0xFF45: // LYC - LY compare
                    return _ppu.ReadLYC();

                case 0xFF46: // DMA - DMA transfer and start address
                    return _memory[address];

                case 0xFF47: // BGP - BG palette data
                    return _ppu.ReadBGP();

                case 0xFF48: // OBP0 - Object palette 0 data
                    return _ppu.ReadOBP0();

                case 0xFF49: // OBP1 - Object palette 1 data
                    return _ppu.ReadOBP1();

                case 0xFF4A: // WY - Window Y position
                    return _ppu.ReadWY();

                case 0xFF4B: // WX - Window X position
                    return _ppu.ReadWX();

                case 0xFF4C: // Unused
                    return 0xFF;

                case 0xFF4D: // KEY1 - Double-speed mode control (GBC only)
                    if (_isGameBoyColor)
                    {
                        byte key1Value = 0;
                        if (_doubleSpeedMode) key1Value |= 0x80; // Current speed bit
                        if (_speedSwitchRequested) key1Value |= 0x01; // Speed switch request bit
                        return key1Value;
                    }
                    return 0xFF;

                case 0xFF4E: // Unused
                    return 0xFF;

                case 0xFF4F: // VBK - VRAM bank selection (GBC only)
                    return _isGameBoyColor ? (byte)(_vramBank | 0xFE) : (byte)0xFF; // Bit 0 is bank, bits 1-7 are always 1

                case 0xFF50: // Unused
                    return 0xFF;

                case 0xFF51: // HDMA1 - HDMA source high (GBC only)
                    return _isGameBoyColor ? _hdmaSourceHigh : (byte)0xFF;

                case 0xFF52: // HDMA2 - HDMA source low (GBC only)
                    return _isGameBoyColor ? _hdmaSourceLow : (byte)0xFF;

                case 0xFF53: // HDMA3 - HDMA destination high (GBC only)
                    return _isGameBoyColor ? _hdmaDestHigh : (byte)0xFF;

                case 0xFF54: // HDMA4 - HDMA destination low (GBC only)
                    return _isGameBoyColor ? _hdmaDestLow : (byte)0xFF;

                case 0xFF55: // HDMA5 - HDMA length/mode (GBC only)
                    if (_isGameBoyColor)
                    {
                        // If a transfer is active, bit 7 is 0 and the lower bits show remaining length
                        // If inactive, it returns 0xFF
                        return _hdmaActive ? (byte)(_hdmaLengthMode & 0x7F) : (byte)0xFF;
                    }
                    return 0xFF; // Not available on DMG

                case 0xFF56: // RP - Infrared port (GBC only)
                    return _isGameBoyColor ? _infraredPort : (byte)0xFF;

                case 0xFF57: // Unused
                case 0xFF58: // Unused
                case 0xFF59: // Unused
                case 0xFF5A: // Unused
                case 0xFF5B: // Unused
                case 0xFF5C: // Unused
                case 0xFF5D: // Unused
                case 0xFF5E: // Unused
                case 0xFF5F: // Unused
                case 0xFF60: // Unused
                case 0xFF61: // Unused
                case 0xFF62: // Unused
                case 0xFF63: // Unused
                case 0xFF64: // Unused
                case 0xFF65: // Unused
                case 0xFF66: // Unused
                case 0xFF67: // Unused
                    return 0xFF;

                case 0xFF68: // BCPS/BGPI - Background palette index (GBC only)
                    return _isGameBoyColor ? _ppu.ReadBCPS() : (byte)0xFF;

                case 0xFF69: // BCPD/BGPD - Background palette data (GBC only)
                    return _isGameBoyColor ? _ppu.ReadBCPD() : (byte)0xFF;

                case 0xFF6A: // OCPS/OBPI - Object palette index (GBC only)
                    return _isGameBoyColor ? _ppu.ReadOCPS() : (byte)0xFF;

                case 0xFF6B: // OCPD/OBPD - Object palette data (GBC only)
                    return _isGameBoyColor ? _ppu.ReadOCPD() : (byte)0xFF;

                case 0xFF6C: // Unused
                case 0xFF6D: // Unused
                case 0xFF6E: // Unused
                case 0xFF6F: // Unused
                    return 0xFF;

                case 0xFF70: // SVBK - WRAM bank selection (GBC only)
                    return _isGameBoyColor ? (byte)(_wramBank | 0xF8) : (byte)0xFF; // Bits 0-2 are bank, 3-7 are always 1

                case 0xFF71: // Unused
                case 0xFF72: // Unused
                case 0xFF73: // Unused
                case 0xFF74: // Unused
                case 0xFF75: // Unused
                case 0xFF76: // Unused
                case 0xFF77: // Unused
                case 0xFF78: // Unused
                case 0xFF79: // Unused
                case 0xFF7A: // Unused
                case 0xFF7B: // Unused
                case 0xFF7C: // Unused
                case 0xFF7D: // Unused
                case 0xFF7E: // Unused
                case 0xFF7F: // Unused
                    return 0xFF;

                default:
                    // Unused or unhandled I/O registers return 0xFF on read.
                    return 0xFF;
            }
        }

        public ushort ReadWord(ushort address)
        {
            // Game Boy is little-endian, so read low byte then high byte
            byte low = ReadByte(address);
            byte high = ReadByte((ushort)(address + 1));
            return (ushort)((high << 8) | low);
        }

        // Use bulk memory operations where possible
        public void ReadBulk(ushort address, byte[] buffer, int count)
        {
            // Validate parameters
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (address + count > _memory.Length) throw new ArgumentOutOfRangeException(nameof(address));

            // For simple memory regions, use direct array copy for better performance
            if (address >= WORK_RAM_START && address + count <= WORK_RAM_END + 1)
            {
                Array.Copy(_memory.ToArray(), address, buffer, 0, count);
                return;
            }

            if (address >= HIGH_RAM_START && address + count <= HIGH_RAM_END + 1)
            {
                Array.Copy(_memory.ToArray(), address, buffer, 0, count);
                return;
            }

            // For other regions, use individual reads to handle special cases
            for (int i = 0; i < count; i++)
            {
                buffer[i] = ReadByte((ushort)(address + i));
            }
        }
        
         

        public void WriteByte(ushort address, byte value)
        {
            if (address == 0xFF50)
            {
                bootRomEnabled = false;
                Log.Warning($"Disabling bootrom");
                return;
            }

            // Handle ROM area (0x0000-0x7FFF) - MBC register writes
            if (address >= ROM_START && address <= ROM_END)
            {
                if (_mbc != null)
                {
                    _mbc.WriteRom(address, value);
                }
                // ROM area writes are handled by MBC, don't write to memory
                return;
            }

            // Handle external RAM area (0xA000-0xBFFF) with MBC
            if (address >= EXTERNAL_RAM_START && address <= EXTERNAL_RAM_END)
            {
                if (_mbc != null)
                {
                    _mbc.WriteRam(address, value);
                }
                // External RAM writes are handled by MBC, don't write to memory
                return;
            }

            // Validate address bounds
            if (address >= _memory.Length)
            {
                Log.Warning($"WriteByte: Address {address:X4} out of bounds");
                return;
            }

            // Handle I/O registers with special behavior
            if (address >= IO_REGISTERS_START && address <= IO_REGISTERS_END)
            {
                WriteIORegister(address, value);
                return;
            }

            // Handle echo RAM mirroring
            if (address >= ECHO_RAM_START && address <= ECHO_RAM_END)
            {
                // In GBC mode, echo RAM mirrors the current WRAM bank
                if (_isGameBoyColor)
                {
                    ushort mirroredAddress = (ushort)(address - 0x2000);
                    // Let the dedicated WRAM write handler manage the banking logic.
                    WriteWramByte(mirroredAddress, value);
                }
                else
                {
                    _memory[address - 0x2000] = value; // Mirror to 0xC000-0xDDFF
                }
                return;
            }

            // Handle unusable memory region
            if (address >= UNUSABLE_START && address <= UNUSABLE_END)
            {
                // Writes to unusable memory are ignored
                return;
            }

            // Handle interrupt enable register
            if (address == INTERRUPT_ENABLE)
            {
                _cpu.ie_register = value;
                return;
            }

            // Protect critical interrupt vectors from being overwritten
            if (IsInterruptVector(address))
            {
                _memory[address] = value;
                Log.Warning($"Writing to Interrupt vectors at address {address:X4} value {value:X2}.");
                return;
            }

            // Check VRAM access restrictions during LCD rendering
            if (address >= VRAM_START && address <= VRAM_END)
            {
                if (_ppu.IsVRAMAccessRestricted())
                {
                    // VRAM access is restricted during certain LCD modes
                    //TODO: timing is currently broken here
                    //return;
                }

                // Handle Video RAM with banking (GBC)
                WriteVramByte(address, value);
                return;
            }

            // Check OAM access restrictions during LCD rendering
            if (address >= OAM_START && address <= OAM_END)
            {
                if (_ppu.IsOAMAccessRestricted())
                {
                    // OAM access is restricted during certain LCD modes
                    //TODO: timing is currently broken here
                    //return;
                }
            }

            // Handle Work RAM with banking (GBC)
            if (address >= WORK_RAM_START && address <= WORK_RAM_END)
            {
                WriteWramByte(address, value);
                return;
            }

            _memory[address] = value;
        }

        private void WriteIORegister(ushort address, byte value)
        {

            if (address >= 0xFF10 && address <= 0xFF26)
            {
                _apu.WriteRegister(address, value);
                return; // Return early
            }
            
            if (address >= 0xFF30 && address <= 0xFF3F)
            {
                _apu.WriteRegister(address, value);
                return; // Return early
            }

            switch (address)
            {
                case 0xFF00: // P1 - Joypad register
                    _joypad.WriteP1(value);
                    break;

                case 0xFF01: // SB - Serial transfer data
                    _memory[address] = value;
                    Log.Debug($"Writing to serial data (SB) register {address:X4} value {value:X2}.");
                    break;

                case 0xFF02: // SC - Serial transfer control
                    _memory[address] = value;
                    Log.Debug($"Writing to serial control (SC) register {address:X4} value {value:X2}.");
                    if (value == 0x81)
                    {
                        // The trigger value was written to the Serial Control (SC) register.
                        // Now, we read the character from the Serial Data (SB) register.
                        byte serialData = _memory[0xFF01];
                        char character = Convert.ToChar(serialData);

                        // Fire the event with the captured character.
                        OnSerialData?.Invoke(character);
                    }
                    break;

                case 0xFF04: // DIV - Divider register
                    _timer.WriteDIV(value);
                    break;

                case 0xFF05: // TIMA - Timer counter
                    _timer.WriteTIMA(value);
                    break;

                case 0xFF06: // TMA - Timer modulo
                    _timer.WriteTMA(value);
                    break;

                case 0xFF07: // TAC - Timer control
                    _timer.WriteTAC(value);
                    break;

                case 0xFF0F: // IF - Interrupt flag
                    _cpu.if_register = value;
                    break;

                case 0xFFFF: // IE - Interrupt enable flag
                    _cpu.ie_register = value;
                    return;

                case 0xFF40: // LCDC - LCD control
                    _ppu.WriteLCDC(value);
                    break;

                case 0xFF41: // STAT - LCD status
                    _ppu.WriteSTAT(value);
                    break;

                case 0xFF42: // SCY - Scroll Y
                    _ppu.WriteSCY(value);
                    break;

                case 0xFF43: // SCX - Scroll X
                    _ppu.WriteSCX(value);
                    break;

                case 0xFF44: // LY - LCDC Y coordinate (read-only)
                    // LY is read-only, ignore writes
                    break;

                case 0xFF45: // LYC - LY compare
                    _ppu.WriteLYC(value);
                    break;

                case 0xFF46: // DMA - DMA transfer and start address
                    _memory[address] = value;
                    PerformDMATransfer(value);
                    break;

                case 0xFF47: // BGP - BG palette data
                    _ppu.WriteBGP(value);
                    break;

                case 0xFF48: // OBP0 - Object palette 0 data
                    _ppu.WriteOBP0(value);
                    break;

                case 0xFF49: // OBP1 - Object palette 1 data
                    _ppu.WriteOBP1(value);
                    break;

                case 0xFF4A: // WY - Window Y position
                    _ppu.WriteWY(value);
                    break;

                case 0xFF4B: // WX - Window X position
                    _ppu.WriteWX(value);
                    break;

                case 0xFF4C: // Unused
                case 0xFF4E: // Unused
                    // Writes to unused registers are ignored
                    break;

                case 0xFF4F: // VBK - VRAM bank selection (GBC only)
                    if (_isGameBoyColor)
                    {
                        byte newBank = (byte)(value & 0x01); // Only bit 0 is used
                        _vramBank = newBank;
                        Log.Debug($"VRAM bank switched to: {_vramBank}");
                    }
                    break;

                case 0xFF4D: // KEY1 - Double-speed mode control (GBC only)
                    if (_isGameBoyColor)
                    {
                        // Bit 0: Speed switch request (0=normal, 1=double)
                        _speedSwitchRequested = (value & 0x01) != 0;
                        Log.Debug($"KEY1 write: Speed switch requested = {_speedSwitchRequested}");
                    }
                    break;

                case 0xFF51: // HDMA1 - HDMA source high (GBC only)
                    if (_isGameBoyColor)
                    {
                        _hdmaSourceHigh = value;
                    }
                    break;

                case 0xFF52: // HDMA2 - HDMA source low (GBC only)
                    if (_isGameBoyColor)
                    {
                        _hdmaSourceLow = value;
                    }
                    break;

                case 0xFF53: // HDMA3 - HDMA destination high (GBC only)
                    if (_isGameBoyColor)
                    {
                        _hdmaDestHigh = value;
                    }
                    break;

                case 0xFF54: // HDMA4 - HDMA destination low (GBC only)
                    if (_isGameBoyColor)
                    {
                        _hdmaDestLow = value;
                    }
                    break;

                case 0xFF55: // HDMA5 - HDMA length/mode/start (GBC only)
                    if (_isGameBoyColor)
                    {
                        // If an HDMA is already active, a write to this register can cancel it
                        if (_hdmaActive)
                        {
                            // Writing with bit 7 unset cancels the current transfer
                            if ((value & 0x80) == 0)
                            {
                                _hdmaActive = false;
                                Log.Debug("HDMA transfer cancelled by game.");
                                // The length register is left with the remaining length
                                _hdmaLengthMode = (byte)(_hdmaLengthMode | 0x80); // Set bit 7 to indicate inactive
                            }
                            return; // Ignore other writes while active
                        }

                        // Set up a new transfer
                        _hdmaLengthMode = value;

                        // Calculate source and destination addresses
                        // Source: 0x0000-0x7FF0 or 0xA000-0xDFF0. Low 4 bits are ignored.
                        _hdmaSource = (ushort)((_hdmaSourceHigh << 8) | (_hdmaSourceLow & 0xF0));

                        // Destination: 0x8000-0x9FF0 (VRAM). High 3 and low 4 bits are ignored.
                        _hdmaDestination = (ushort)(((_hdmaDestHigh & 0x1F) << 8) | (_hdmaDestLow & 0xF0));
                        _hdmaDestination |= 0x8000; // Ensure it's in VRAM range

                        // Length is (value & 0x7F) + 1 blocks of 16 bytes
                        _hdmaRemainingBytes = ((value & 0x7F) + 1) * 0x10;

                        byte mode = (byte)(value & 0x80);

                        if (mode == 0) // General-Purpose DMA (GDMA)
                        {
                            Log.Debug($"GDMA transfer requested: {_hdmaRemainingBytes} bytes from {_hdmaSource:X4} to {_hdmaDestination:X4}");
                            // Halts the CPU and transfers everything at once.
                            // For emulation, we can just do it in a loop.
                            int chunks = _hdmaRemainingBytes / 0x10;
                            for (int i = 0; i < chunks; i++)
                            {
                                PerformDmaChunk();
                            }
                        }
                        else // H-Blank DMA (HDMA)
                        {
                            Log.Debug($"HDMA transfer started: {_hdmaRemainingBytes} bytes from {_hdmaSource:X4} to {_hdmaDestination:X4}");
                            _hdmaActive = true;
                            // The transfer will be performed chunk-by-chunk in TickHblankDma()
                        }
                    }
                    break;

                case 0xFF56: // RP - Infrared port (GBC only)
                    if (_isGameBoyColor)
                    {
                        _infraredPort = value;
                        Log.Debug($"Infrared port write: {value:X2}");
                    }
                    break;

                case 0xFF57: // Unused
                case 0xFF58: // Unused
                case 0xFF59: // Unused
                case 0xFF5A: // Unused
                case 0xFF5B: // Unused
                case 0xFF5C: // Unused
                case 0xFF5D: // Unused
                case 0xFF5E: // Unused
                case 0xFF5F: // Unused
                case 0xFF60: // Unused
                case 0xFF61: // Unused
                case 0xFF62: // Unused
                case 0xFF63: // Unused
                case 0xFF64: // Unused
                case 0xFF65: // Unused
                case 0xFF66: // Unused
                case 0xFF67: // Unused
                    // Writes to unused registers are ignored
                    break;

                case 0xFF68: // BCPS/BGPI - Background palette index (GBC only)
                    if (_isGameBoyColor)
                    {
                        _ppu.WriteBCPS(value);
                    }
                    break;

                case 0xFF69: // BCPD/BGPD - Background palette data (GBC only)
                    if (_isGameBoyColor)
                    {
                        _ppu.WriteBCPD(value);
                    }
                    break;

                case 0xFF6A: // OCPS/OBPI - Object palette index (GBC only)
                    if (_isGameBoyColor)
                    {
                        _ppu.WriteOCPS(value);
                    }
                    break;

                case 0xFF6B: // OCPD/OBPD - Object palette data (GBC only)
                    if (_isGameBoyColor)
                    {
                        _ppu.WriteOCPD(value);
                    }
                    break;

                case 0xFF6C: // Unused
                case 0xFF6D: // Unused
                case 0xFF6E: // Unused
                case 0xFF6F: // Unused
                    // Writes to unused registers are ignored
                    break;

                case 0xFF70: // SVBK - WRAM bank selection (GBC only)
                    if (_isGameBoyColor)
                    {
                        byte newBank = (byte)(value & 0x07); // Only bits 0-2 are used
                        if (newBank == 0) newBank = 1; // Bank 0 is not accessible, use bank 1 instead
                        _wramBank = newBank;
                        Log.Debug($"WRAM bank switched to: {_wramBank}");
                    }
                    break;

                case 0xFF71: // Unused
                case 0xFF72: // Unused
                case 0xFF73: // Unused
                case 0xFF74: // Unused
                case 0xFF75: // Unused
                case 0xFF76: // Unused
                case 0xFF77: // Unused
                case 0xFF78: // Unused
                case 0xFF79: // Unused
                case 0xFF7A: // Unused
                case 0xFF7B: // Unused
                case 0xFF7C: // Unused
                case 0xFF7D: // Unused
                case 0xFF7E: // Unused
                case 0xFF7F: // Unused
                    // Writes to unused registers are ignored
                    break;

                default:
                    // For other I/O registers, store the value
                    _memory[address] = value;
                    break;
            }
        }

        public void WriteWord(ushort address, ushort value)
        {
            // Game Boy is little-endian: write the low byte to the initial address,
            // and the high byte to the next address.
            byte low = (byte)(value & 0xFF);
            byte high = (byte)(value >> 8);

            WriteByte(address, low);
            WriteByte((ushort)(address + 1), high);
        }

        private bool IsInterruptVector(ushort address)
        {
            return address == 0x0000 || address == 0x0008 || address == 0x0010 || address == 0x0018 ||
                   address == 0x0020 || address == 0x0028 || address == 0x0030 || address == 0x0038 ||
                   address == 0x0040 || address == 0x0048 || address == 0x0050 || address == 0x0058 || address == 0x0060;
        }

        /// <summary>
        /// Performs a DMA transfer from the specified source address to OAM (0xFE00-0xFE9F).
        /// The DMA register contains the high byte of the source address (0x00-0xDF).
        /// The low byte is always 0x00, so the source address is (value << 8).
        /// </summary>
        /// <param name="dmaValue">The high byte of the source address (0x00-0xDF)</param>
        private void PerformDMATransfer(byte dmaValue)
        {
            // Calculate the source address: high byte from DMA register, low byte is always 0x00
            ushort sourceAddress = (ushort)(dmaValue << 8);

            // Validate source address range (0x0000-0xDF00)
            if (sourceAddress > 0xDF00)
            {
                Log.Warning($"DMA transfer: Invalid source address {sourceAddress:X4}. DMA value was {dmaValue:X2}");
                return;
            }

            // DMA transfers 160 bytes (0xA0) from source to OAM (0xFE00-0xFE9F)
            const ushort oamStart = 0xFE00;
            const int transferSize = 0xA0; // 160 bytes

            // Perform the transfer
            for (int i = 0; i < transferSize; i++)
            {
                ushort srcAddr = (ushort)(sourceAddress + i);
                ushort dstAddr = (ushort)(oamStart + i);

                // Read from source and write to OAM
                // Note: We need to use ReadByte to handle any special memory regions
                byte data = ReadByte(srcAddr);
                _memory[dstAddr] = data;
            }

            Log.Debug($"DMA transfer completed: {transferSize} bytes from {sourceAddress:X4} to OAM");
        }
    }
}
