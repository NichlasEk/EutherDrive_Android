using System;
using Serilog;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Memory Bank Controller 5 (MBC5) implementation
    /// Supports ROM sizes up to 8MB, RAM up to 128KB, and Rumble functionality
    /// </summary>
    public class Mbc5 : IMbc, IBatteryBackedMbc
    {
        [NonSerialized]
        private readonly byte[] _romData;
        private readonly byte[] _ramData;
        private readonly int _romSize;
        private readonly int _ramSize;
        private readonly int _romBankCount;
        private readonly int _ramBankCount;
        private readonly bool _hasRumble;

        // MBC5 registers
        private bool _ramEnabled = false;
        private int _romBankNumber = 1; // Bank 0 is always mapped to 0x0000-0x3FFF
        private int _ramBankNumber = 0;
        private bool _rumbleEnabled = false;

        public bool IsRamEnabled => _ramEnabled;
        public int CurrentRomBank => _romBankNumber;
        public int CurrentRamBank => _ramBankNumber;
        public bool IsRumbleEnabled => _rumbleEnabled;
        public bool HasPersistentData => _ramSize > 0;

        public Mbc5(byte[] romData, int ramSize, bool hasRumble = false)
        {
            _romData = romData ?? throw new ArgumentNullException(nameof(romData));
            _romSize = romData.Length;
            _ramSize = ramSize;
            _hasRumble = hasRumble;

            // Calculate bank counts
            _romBankCount = _romSize / 0x4000; // 16KB per bank
            _ramBankCount = _ramSize > 0 ? _ramSize / 0x2000 : 0; // 8KB per bank

            // Initialize RAM if present
            if (_ramSize > 0)
            {
                _ramData = new byte[_ramSize];
                // Initialize RAM with 0xFF (uninitialized value)
                for (int i = 0; i < _ramSize; i++)
                {
                    _ramData[i] = 0xFF;
                }
            }
            else
            {
                _ramData = new byte[0];
            }

            Log.Information($"MBC5 initialized: ROM={_romSize / 1024}KB ({_romBankCount} banks), RAM={_ramSize / 1024}KB ({_ramBankCount} banks), Rumble={(_hasRumble ? "Yes" : "No")}");
            Log.Information($"MBC5: Initial ROM bank 0, RAM bank 0, RAM disabled");
        }

        public byte ReadRom(ushort address)
        {
            if (address < 0x4000)
            {
                // Bank 0 area (0x0000-0x3FFF) - always bank 0
                if (address < _romSize)
                {
                    return _romData[address];
                }
                else
                {
                    Log.Warning($"MBC5: ROM read out of bounds at address 0x{address:X4}");
                    return 0xFF;
                }
            }
            else if (address < 0x8000)
            {
                // Bank 1 area (0x4000-0x7FFF) - uses ROM bank number
                int romAddress = (_romBankNumber * 0x4000) + (address - 0x4000);
                
                if (romAddress < _romSize)
                {
                    return _romData[romAddress];
                }
                else
                {
                    Log.Warning($"MBC5: ROM read out of bounds at address 0x{address:X4} (ROM address 0x{romAddress:X6})");
                    return 0xFF;
                }
            }
            else
            {
                Log.Warning($"MBC5: Invalid ROM read address 0x{address:X4}");
                return 0xFF;
            }
        }

        public void WriteRom(ushort address, byte value)
        {
            if (address < 0x2000)
            {
                // RAM Enable Register (0x0000-0x1FFF)
                _ramEnabled = (value & 0x0F) == 0x0A;
                Log.Debug($"MBC5: RAM {( _ramEnabled ? "enabled" : "disabled")} (value: 0x{value:X2})");
            }
            else if (address < 0x3000)
            {
                // ROM Bank Number Low Register (0x2000-0x2FFF)
                // Lower 8 bits of ROM bank number
                _romBankNumber = (_romBankNumber & 0x100) | value;
                
                // Ensure bank number doesn't exceed available banks
                if (_romBankNumber >= _romBankCount)
                {
                    _romBankNumber = _romBankNumber % _romBankCount;
                }

                Log.Debug($"MBC5: ROM bank set to {_romBankNumber} (low byte: 0x{value:X2})");
            }
            else if (address < 0x4000)
            {
                // ROM Bank Number High Register (0x3000-0x3FFF)
                // Upper bit of ROM bank number (bit 8)
                _romBankNumber = (_romBankNumber & 0xFF) | ((value & 0x01) << 8);
                
                // Ensure bank number doesn't exceed available banks
                if (_romBankNumber >= _romBankCount)
                {
                    _romBankNumber = _romBankNumber % _romBankCount;
                }

                Log.Debug($"MBC5: ROM bank set to {_romBankNumber} (high bit: 0x{value:X2})");
            }
            else if (address < 0x6000)
            {
                // RAM Bank Number / Rumble Register (0x4000-0x5FFF)
                if (_hasRumble)
                {
                    // Rumble control (bit 3)
                    bool newRumbleState = (value & 0x08) != 0;
                    if (newRumbleState != _rumbleEnabled)
                    {
                        _rumbleEnabled = newRumbleState;
                        Log.Debug($"MBC5: Rumble {( _rumbleEnabled ? "enabled" : "disabled")} (value: 0x{value:X2})");
                        // TODO: Implement actual rumble feedback
                    }
                    
                    // RAM bank selection (lower 4 bits)
                    _ramBankNumber = value & 0x0F;
                }
                else
                {
                    // RAM bank selection (lower 4 bits)
                    _ramBankNumber = value & 0x0F;
                }
                
                if (_ramBankNumber >= _ramBankCount)
                {
                    _ramBankNumber = _ramBankNumber % _ramBankCount;
                }

                Log.Debug($"MBC5: RAM bank set to {_ramBankNumber} (value: 0x{value:X2})");
            }
            else
            {
                Log.Warning($"MBC5: Invalid ROM write address 0x{address:X4}");
            }
        }

        public byte ReadRam(ushort address)
        {
            if (!_ramEnabled)
            {
                return 0xFF; // RAM disabled
            }

            // Convert address to RAM address
            ushort ramAddress = (ushort)(address - 0xA000);
            
            // Add bank offset
            int bankOffset = _ramBankNumber * 0x2000; // 8KB per bank
            int fullRamAddress = bankOffset + ramAddress;

            if (fullRamAddress < _ramSize)
            {
                return _ramData[fullRamAddress];
            }
            else
            {
                Log.Warning($"MBC5: RAM read out of bounds at address 0x{address:X4} (RAM address 0x{fullRamAddress:X6})");
                return 0xFF;
            }
        }

        public void WriteRam(ushort address, byte value)
        {
            if (!_ramEnabled)
            {
                return; // RAM disabled, ignore write
            }

            // Convert address to RAM address
            ushort ramAddress = (ushort)(address - 0xA000);
            
            // Add bank offset
            int bankOffset = _ramBankNumber * 0x2000; // 8KB per bank
            int fullRamAddress = bankOffset + ramAddress;

            if (fullRamAddress < _ramSize)
            {
                _ramData[fullRamAddress] = value;
            }
            else
            {
                Log.Warning($"MBC5: RAM write out of bounds at address 0x{address:X4} (RAM address 0x{fullRamAddress:X6})");
            }
        }

        public void SavePersistentData(BinaryWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.Write(_ramData.Length);
            writer.Write(_ramData);
        }

        public void LoadPersistentData(BinaryReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            int length = reader.ReadInt32();
            byte[] data = reader.ReadBytes(length);
            Array.Clear(_ramData, 0, _ramData.Length);
            data.AsSpan(0, Math.Min(data.Length, _ramData.Length)).CopyTo(_ramData);
        }
    }
}
