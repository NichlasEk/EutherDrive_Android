namespace XamariNES.Cartridge.Mappers
{
    public interface IPpuMemoryMapperEx
    {
        byte ReadPpuRender(int address, byte[] vram, bool sprite);
    }
}
