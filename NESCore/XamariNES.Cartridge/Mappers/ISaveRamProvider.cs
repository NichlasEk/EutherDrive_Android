namespace XamariNES.Cartridge.Mappers
{
    public interface ISaveRamProvider
    {
        bool BatteryBacked { get; }
        bool IsSaveRamDirty { get; }
        byte[] GetSaveRam();
        void ClearSaveRamDirty();
    }
}
