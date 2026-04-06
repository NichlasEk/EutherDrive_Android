using System;
using Serilog;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Memory Bank Controller 1 (MBC1) implementation
    /// Supports ROM sizes up to 2MB and RAM sizes up to 32KB
    /// </summary>
    public class Mbc1 : IMbc, IBatteryBackedMbc
    {
        [NonSerialized]
        private readonly byte[] _romData;
        private readonly byte[] _ramData;
        private readonly int _romSize;
        private readonly int _ramSize;
        private readonly int _romBankCount;
        private readonly int _ramBankCount;

        // MBC1 registers
        private bool _ramEnabled = false;
        private int _romBankNumber = 1; // Bank 0 is always mapped to 0x0000-0x3FFF
        private int _ramBankNumber = 0;
        private bool _bankingMode = false; // false = ROM banking mode, true = RAM banking mode

        public bool IsRamEnabled => _ramEnabled;
        public int CurrentRomBank => _romBankNumber;
        public int CurrentRamBank => _ramBankNumber;
        public bool HasPersistentData => _ramSize > 0;

        public Mbc1(byte[] romData, int ramSize)
        {
            _romData = romData ?? throw new ArgumentNullException(nameof(romData));
            _romSize = romData.Length;
            _ramSize = ramSize;

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

            Log.Information($"MBC1 initialized: ROM={_romSize / 1024}KB ({_romBankCount} banks), RAM={_ramSize / 1024}KB ({_ramBankCount} banks)");
            Log.Information($"MBC1: Initial ROM bank 0, RAM bank 0, RAM disabled, ROM banking mode");
        }

        public byte ReadRom(ushort address)
        {
            if (address < 0x4000)
            {
                // Bank 0 area (0x0000-0x3FFF)
                // In ROM banking mode: always bank 0
                // In RAM banking mode: bank 0 or upper bits of ROM bank number
                int bankNumber = _bankingMode ? (_romBankNumber & 0x60) : 0;
                int romAddress = (bankNumber * 0x4000) + address;
                
                if (romAddress < _romSize)
                {
                    return _romData[romAddress];
                }
                else
                {
                    Log.Warning($"MBC1: ROM read out of bounds at address 0x{address:X4} (ROM address 0x{romAddress:X6})");
                    return 0xFF;
                }
            }
            else if (address < 0x8000)
            {
                // Bank 1 area (0x4000-0x7FFF)
                // Always uses the ROM bank number
                int romAddress = (_romBankNumber * 0x4000) + (address - 0x4000);
                
                if (romAddress < _romSize)
                {
                    return _romData[romAddress];
                }
                else
                {
                    Log.Warning($"MBC1: ROM read out of bounds at address 0x{address:X4} (ROM address 0x{romAddress:X6})");
                    return 0xFF;
                }
            }
            else
            {
                Log.Warning($"MBC1: Invalid ROM read address 0x{address:X4}");
                return 0xFF;
            }
        }

        public void WriteRom(ushort address, byte value)
        {
            if (address < 0x2000)
            {
                // RAM Enable Register (0x0000-0x1FFF)
                // Enable/disable external RAM
                _ramEnabled = (value & 0x0F) == 0x0A;
                Log.Debug($"MBC1: RAM {( _ramEnabled ? "enabled" : "disabled")} (value: 0x{value:X2})");
            }
            else if (address < 0x4000)
            {
                // ROM Bank Number Register (0x2000-0x3FFF)
                // Lower 5 bits of ROM bank number
                int lowerBits = value & 0x1F;
                
                // If lower bits are 0, set to 1 (bank 0 is not accessible in this area)
                if (lowerBits == 0)
                {
                    lowerBits = 1;
                }

                // Update ROM bank number
                if (_bankingMode)
                {
                    // In RAM banking mode, only lower 5 bits are used
                    _romBankNumber = (_romBankNumber & 0x60) | lowerBits;
                }
                else
                {
                    // In ROM banking mode, use all bits
                    _romBankNumber = (_romBankNumber & 0x60) | lowerBits;
                }

                // Ensure bank number doesn't exceed available banks
                if (_romBankNumber >= _romBankCount)
                {
                    _romBankNumber = _romBankNumber % _romBankCount;
                }

                Log.Debug($"MBC1: ROM bank set to {_romBankNumber} (value: 0x{value:X2})");
            }
            else if (address < 0x6000)
            {
                // RAM Bank Number / Upper Bits of ROM Bank Number Register (0x4000-0x5FFF)
                int upperBits = value & 0x03;
                
                if (_bankingMode)
                {
                    // RAM banking mode: set RAM bank number
                    _ramBankNumber = upperBits;
                    if (_ramBankNumber >= _ramBankCount)
                    {
                        _ramBankNumber = _ramBankNumber % _ramBankCount;
                    }
                    Log.Debug($"MBC1: RAM bank set to {_ramBankNumber} (value: 0x{value:X2})");
                }
                else
                {
                    // ROM banking mode: set upper bits of ROM bank number
                    _romBankNumber = (_romBankNumber & 0x1F) | (upperBits << 5);
                    if (_romBankNumber >= _romBankCount)
                    {
                        _romBankNumber = _romBankNumber % _romBankCount;
                    }
                    Log.Debug($"MBC1: ROM bank upper bits set, bank now {_romBankNumber} (value: 0x{value:X2})");
                }
            }
            else if (address < 0x8000)
            {
                // Banking Mode Select Register (0x6000-0x7FFF)
                // 0x00 = ROM banking mode, 0x01 = RAM banking mode
                _bankingMode = (value & 0x01) != 0;
                Log.Debug($"MBC1: Banking mode set to {(_bankingMode ? "RAM" : "ROM")} banking (value: 0x{value:X2})");
            }
            else
            {
                Log.Warning($"MBC1: Invalid ROM write address 0x{address:X4}");
            }
        }

        public byte ReadRam(ushort address)
        {
            if (!_ramEnabled || _ramSize == 0)
            {
                return 0xFF; // RAM disabled or not present
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
                Log.Warning($"MBC1: RAM read out of bounds at address 0x{address:X4} (RAM address 0x{fullRamAddress:X6})");
                return 0xFF;
            }
        }

        public void WriteRam(ushort address, byte value)
        {
            if (!_ramEnabled || _ramSize == 0)
            {
                return; // RAM disabled or not present, ignore write
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
                Log.Warning($"MBC1: RAM write out of bounds at address 0x{address:X4} (RAM address 0x{fullRamAddress:X6})");
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
