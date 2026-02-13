namespace KSNES.ROM;

public interface IROM : IHasAccessToSystem
{
    byte Read(int bank, int adr);
    void Write(int bank, int adr, byte value);
    Header Header { get; }
    int RomLength { get; }
    void LoadROM(byte[] data, Header header);
    void LoadSRAM();
    void ResetCoprocessor();
    void RunCoprocessor(ulong snesCycles);
    byte ReadRomByteLoRom(uint address);
}
