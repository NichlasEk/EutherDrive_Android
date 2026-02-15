namespace XamariNES.Cartridge.Mappers
{
    public interface IPpuMemoryMapper
    {
        byte ReadPpu(int address, byte[] vram);
        void WritePpu(int address, byte value, byte[] vram);
    }
}
