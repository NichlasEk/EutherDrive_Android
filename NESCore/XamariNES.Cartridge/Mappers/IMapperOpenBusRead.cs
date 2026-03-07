namespace XamariNES.Cartridge.Mappers
{
    public interface IMapperOpenBusRead
    {
        byte ReadByte(int offset, byte cpuOpenBus);
    }
}
