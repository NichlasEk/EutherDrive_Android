using System;

namespace KSNES.Specialchips.OBC1;

public sealed class Obc1
{
    private const ushort OamBase0 = 0x1C00;
    private const ushort OamBase1 = 0x1800;
    
    private readonly byte[] _rom;
    private readonly byte[] _sram;
    private ushort _oamBase;
    private ushort _oamIndex;
    private byte _register7;
    
    public Obc1(byte[] rom, byte[] sram)
    {
        _rom = rom;
        _sram = sram;
        _oamBase = OamBase0;
        _oamIndex = 0;
        _register7 = 0;
    }
    
    public byte? Read(uint address)
    {
        byte bank = (byte)((address >> 16) & 0xFF);
        ushort offset = (ushort)(address & 0xFFFF);
        
        switch (bank, offset)
        {
            case (<= 0x3F or >= 0x80 and <= 0xBF, >= 0x7FF0 and <= 0x7FF7):
                // OBC1 internal ports
                return ReadObc1Port(address);
                
            case (<= 0x3F or >= 0x80 and <= 0xBF, >= 0x8000 and <= 0xFFFF):
            case (>= 0x40 and <= 0x6F or >= 0xC0 and <= 0xFF, _):
                // ROM
                uint romAddr = LoromMapRomAddress(address, (uint)_rom.Length);
                return _rom[romAddr];
                
            case (<= 0x3F or >= 0x80 and <= 0xBF, (>= 0x6000 and <= 0x7FEF) or (>= 0x7FF8 and <= 0x7FFF)):
            case (>= 0x70 and <= 0x7D, >= 0x6000 and <= 0x7FFF):
                // SRAM (8KB)
                uint sramAddr = address & 0x1FFF;
                return _sram[sramAddr];
                
            default:
                return null;
        }
    }
    
    private byte ReadObc1Port(uint address)
    {
        byte port = (byte)(address & 7);
        
        switch (port)
        {
            case 0:
            case 1:
            case 2:
            case 3:
                // OAM lower bytes
                ushort sramAddr = (ushort)(_oamBase + (_oamIndex << 2) + port);
                return _sram[sramAddr];
                
            case 4:
                // OAM upper bits
                sramAddr = (ushort)(_oamBase + 0x200 + (_oamIndex >> 2));
                return _sram[sramAddr];
                
            case 5:
                // OAM base in SRAM (bit 0; 0=$7C00, 1=$7800)
                return (byte)(_oamBase == OamBase1 ? 1 : 0);
                
            case 6:
                // OAM index
                return (byte)_oamIndex;
                
            case 7:
                // Unknown (SRAM vs. I/O ports?)
                return _register7;
                
            default:
                return 0;
        }
    }
    
    public void Write(uint address, byte value)
    {
        byte bank = (byte)((address >> 16) & 0xFF);
        ushort offset = (ushort)(address & 0xFFFF);
        
        switch (bank, offset)
        {
            case (<= 0x3F or >= 0x80 and <= 0xBF, >= 0x7FF0 and <= 0x7FF7):
                // OBC1 internal ports
                WriteObc1Port(address, value);
                break;
                
            case (<= 0x3F or >= 0x80 and <= 0xBF, (>= 0x6000 and <= 0x7FEF) or (>= 0x7FF8 and <= 0x7FFF)):
            case (>= 0x70 and <= 0x7D, >= 0x6000 and <= 0x7FFF):
                // SRAM (8KB)
                uint sramAddr = address & 0x1FFF;
                _sram[sramAddr] = value;
                break;
        }
    }
    
    private void WriteObc1Port(uint address, byte value)
    {
        byte port = (byte)(address & 7);
        
        switch (port)
        {
            case 0:
            case 1:
            case 2:
            case 3:
                // OAM lower bytes
                ushort sramAddr = (ushort)(_oamBase + (_oamIndex << 2) + port);
                _sram[sramAddr] = value;
                break;
                
            case 4:
                // OAM upper bits
                // Only set the 2 bits for the specified OAM index
                sramAddr = (ushort)(_oamBase + 0x200 + (_oamIndex >> 2));
                byte shift = (byte)(2 * (_oamIndex & 0x03));
                _sram[sramAddr] = (byte)((_sram[sramAddr] & ~(0x03 << shift)) | ((value & 0x03) << shift));
                break;
                
            case 5:
                // OAM base in SRAM (bit 0; 0=$7C00, 1=$7800)
                _oamBase = ((value & 0x01) != 0) ? OamBase1 : OamBase0;
                break;
                
            case 6:
                // OAM index (0-127)
                _oamIndex = (ushort)(value & 0x7F);
                break;
                
            case 7:
                // Unknown (SRAM vs. I/O ports?)
                _register7 = value;
                break;
        }
    }
    
    public byte[] GetSram() => _sram;
    
    private static uint LoromMapRomAddress(uint address, uint romSize)
    {
        // LoROM mapping: ((address & 0x7F0000) >> 1) | (address & 0x007FFF)
        uint mapped = ((address & 0x7F0000) >> 1) | (address & 0x007FFF);
        uint mask = romSize - 1;
        
        // Check if romSize is power of two
        if ((romSize & (romSize - 1)) == 0)
            return mapped & mask;
        
        return mapped % romSize;
    }
}
