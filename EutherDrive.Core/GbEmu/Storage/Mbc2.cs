using System;
using Serilog;

namespace EutherDrive.Core.GbEmu
{
    /// <summary>
    /// Memory Bank Controller 2 (MBC2) implementation
    /// Supports ROM sizes up to 256KB and 512 bytes of built-in RAM
    /// </summary>
    public class Mbc2 : IMbc, IBatteryBackedMbc
    {
        [NonSerialized]
        private readonly byte[] _romData;
        private readonly byte[] _ramData;
        private readonly int _romSize;
        private readonly int _romBankCount;

        // MBC2 registers
        private bool _ramEnabled = false;
        private int _romBankNumber = 1; // Bank 0 is always mapped to 0x0000-0x3FFF

        public bool IsRamEnabled => _ramEnabled;
        public int CurrentRomBank => _romBankNumber;
        public int CurrentRamBank => 0; // MBC2 only has one RAM bank
        public bool HasPersistentData => true;

        public Mbc2(byte[] romData)
        {
            _romData = romData ?? throw new ArgumentNullException(nameof(romData));
            _romSize = romData.Length;

            // Calculate bank count (16KB per bank)
            _romBankCount = _romSize / 0x4000;

            // MBC2 has 512 bytes of built-in RAM
            _ramData = new byte[512];
            // Initialize RAM with 0xFF (uninitialized value)
            for (int i = 0; i < 512; i++)
            {
                _ramData[i] = 0xFF;
            }

            Log.Information($"MBC2 initialized: ROM={_romSize / 1024}KB ({_romBankCount} banks), RAM=512 bytes");
            Log.Information($"MBC2: Initial ROM bank 0, RAM disabled");
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
                    Log.Warning($"MBC2: ROM read out of bounds at address 0x{address:X4}");
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
                    Log.Warning($"MBC2: ROM read out of bounds at address 0x{address:X4} (ROM address 0x{romAddress:X6})");
                    return 0xFF;
                }
            }
            else
            {
                Log.Warning($"MBC2: Invalid ROM read address 0x{address:X4}");
                return 0xFF;
            }
        }

        public void WriteRom(ushort address, byte value)
        {
            if (address < 0x2000)
            {
                // RAM Enable Register (0x0000-0x1FFF)
                // Only the least significant bit of the upper address byte is used
                // to determine if this is a RAM enable command
                if ((address & 0x0100) == 0)
                {
                    // RAM enable/disable command
                    _ramEnabled = (value & 0x0F) == 0x0A;
                    Log.Debug($"MBC2: RAM {( _ramEnabled ? "enabled" : "disabled")} (value: 0x{value:X2})");
                }
            }
            else if (address < 0x4000)
            {
                // ROM Bank Number Register (0x2000-0x3FFF)
                // Only the least significant bit of the upper address byte is used
                // to determine if this is a ROM bank select command
                if ((address & 0x0100) != 0)
                {
                    // ROM bank select command
                    int bankNumber = value & 0x0F;
                    
                    // If bank number is 0, set to 1 (bank 0 is not accessible in this area)
                    if (bankNumber == 0)
                    {
                        bankNumber = 1;
                    }

                    _romBankNumber = bankNumber;

                    // Ensure bank number doesn't exceed available banks
                    if (_romBankNumber >= _romBankCount)
                    {
                        _romBankNumber = _romBankNumber % _romBankCount;
                    }

                    Log.Debug($"MBC2: ROM bank set to {_romBankNumber} (value: 0x{value:X2})");
                }
            }
            else
            {
                Log.Warning($"MBC2: Invalid ROM write address 0x{address:X4}");
            }
        }

        public byte ReadRam(ushort address)
        {
            if (!_ramEnabled)
            {
                return 0xFF; // RAM disabled
            }

            // Convert address to RAM address (0xA000-0xA1FF)
            ushort ramAddress = (ushort)(address - 0xA000);
            
            if (ramAddress < 512)
            {
                // MBC2 RAM only returns the lower 4 bits
                return (byte)(_ramData[ramAddress] & 0x0F);
            }
            else
            {
                Log.Warning($"MBC2: RAM read out of bounds at address 0x{address:X4}");
                return 0xFF;
            }
        }

        public void WriteRam(ushort address, byte value)
        {
            if (!_ramEnabled)
            {
                return; // RAM disabled, ignore write
            }

            // Convert address to RAM address (0xA000-0xA1FF)
            ushort ramAddress = (ushort)(address - 0xA000);
            
            if (ramAddress < 512)
            {
                // MBC2 RAM only stores the lower 4 bits
                _ramData[ramAddress] = (byte)(value & 0x0F);
            }
            else
            {
                Log.Warning($"MBC2: RAM write out of bounds at address 0x{address:X4}");
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
