namespace XamariNES.Cartridge.Mappers
{
    public interface IPpuScanlineObserver
    {
        void OnPpuScanline(int scanline, bool renderingEnabled);
    }
}
