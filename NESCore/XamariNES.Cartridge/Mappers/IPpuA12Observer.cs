namespace XamariNES.Cartridge.Mappers
{
    public interface IPpuA12Observer
    {
        void NotifyPpuA12(int ppuAddress, long ppuCycle);
    }
}
