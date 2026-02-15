namespace XamariNES.Cartridge.Mappers
{
    public interface IPpuMaskObserver
    {
        void OnPpuMaskWrite(byte value);
    }
}
