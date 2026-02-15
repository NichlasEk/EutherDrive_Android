namespace XamariNES.Cartridge.Mappers
{
    public interface IPpuCtrlObserver
    {
        void OnPpuCtrlWrite(byte value);
    }
}
