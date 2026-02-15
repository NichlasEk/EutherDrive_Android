namespace XamariNES.Cartridge.Mappers
{
    public interface IMapperIrqProvider
    {
        bool IrqPending { get; }
    }
}
