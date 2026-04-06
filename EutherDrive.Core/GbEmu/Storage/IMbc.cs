namespace EutherDrive.Core.GbEmu
{
    public interface IBatteryBackedMbc
    {
        bool HasPersistentData { get; }
        void SavePersistentData(BinaryWriter writer);
        void LoadPersistentData(BinaryReader reader);
    }

    /// <summary>
    /// Interface for Memory Bank Controllers (MBCs)
    /// </summary>
    public interface IMbc
    {
        /// <summary>
        /// Reads a byte from the cartridge ROM
        /// </summary>
        /// <param name="address">The address to read from (0x0000-0x7FFF)</param>
        /// <returns>The byte value at the specified address</returns>
        byte ReadRom(ushort address);

        /// <summary>
        /// Writes a byte to the cartridge (used for MBC register writes)
        /// </summary>
        /// <param name="address">The address to write to (0x0000-0x7FFF)</param>
        /// <param name="value">The value to write</param>
        void WriteRom(ushort address, byte value);

        /// <summary>
        /// Reads a byte from external RAM
        /// </summary>
        /// <param name="address">The address to read from (0xA000-0xBFFF)</param>
        /// <returns>The byte value at the specified address</returns>
        byte ReadRam(ushort address);

        /// <summary>
        /// Writes a byte to external RAM
        /// </summary>
        /// <param name="address">The address to write to (0xA000-0xBFFF)</param>
        /// <param name="value">The value to write</param>
        void WriteRam(ushort address, byte value);

        /// <summary>
        /// Gets whether external RAM is enabled
        /// </summary>
        bool IsRamEnabled { get; }

        /// <summary>
        /// Gets the current ROM bank number
        /// </summary>
        int CurrentRomBank { get; }

        /// <summary>
        /// Gets the current RAM bank number
        /// </summary>
        int CurrentRamBank { get; }
    }    
}
