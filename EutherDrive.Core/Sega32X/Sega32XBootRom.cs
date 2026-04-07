using System.Reflection;

namespace EutherDrive.Core.Sega32X;

internal static class Sega32XBootRom
{
    public const int MasterBootRomLength = 2048;
    public const int SlaveBootRomLength = 1024;
    public const int M68kVectorsLength = 256;
    public const int SecurityProgramCartridgeAddress = 0x400;
    public const int SecurityProgramOffsetInMasterBootRom = 0x36C;
    public const int SecurityProgramLength = 0x400;

    private static readonly Lazy<byte[]> MasterBootRom = new(() => LoadResource("sh2_master_boot_rom.bin", MasterBootRomLength));
    private static readonly Lazy<byte[]> SlaveBootRom = new(() => LoadResource("sh2_slave_boot_rom.bin", SlaveBootRomLength));
    private static readonly Lazy<byte[]> M68kVectors = new(() => LoadPatchedM68kVectors());
    private static readonly Lazy<byte[]> SecurityProgram = new(() => ExtractSecurityProgram(MasterBootRom.Value));

    public static ReadOnlySpan<byte> GetMasterBootRom() => MasterBootRom.Value;
    public static ReadOnlySpan<byte> GetSlaveBootRom() => SlaveBootRom.Value;
    public static ReadOnlySpan<byte> GetM68kVectors() => M68kVectors.Value;
    public static ReadOnlySpan<byte> GetSecurityProgram() => SecurityProgram.Value;

    public static bool SecurityProgramMatches(ReadOnlySpan<byte> romData)
    {
        if (romData.Length < SecurityProgramCartridgeAddress + SecurityProgramLength)
            return false;

        return romData.Slice(SecurityProgramCartridgeAddress, SecurityProgramLength)
            .SequenceEqual(GetSecurityProgram());
    }

    private static byte[] LoadPatchedM68kVectors()
    {
        byte[] vectors = LoadResource("m68k_vectors.bin", M68kVectorsLength);

        // Match jgenesis/testpico behavior: HINT vector starts at zero.
        vectors[0x70] = 0;
        vectors[0x71] = 0;
        vectors[0x72] = 0;
        vectors[0x73] = 0;
        return vectors;
    }

    private static byte[] ExtractSecurityProgram(byte[] masterBootRom)
    {
        byte[] securityProgram = new byte[SecurityProgramLength];
        Buffer.BlockCopy(
            masterBootRom,
            SecurityProgramOffsetInMasterBootRom,
            securityProgram,
            0,
            SecurityProgramLength);
        return securityProgram;
    }

    private static byte[] LoadResource(string fileName, int expectedLength)
    {
        Assembly assembly = typeof(Sega32XBootRom).Assembly;
        string resourceName = $"EutherDrive.Core.Sega32X.BootRoms.{fileName}";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Embedded 32X boot ROM resource '{resourceName}' was not found.");

        byte[] data = new byte[expectedLength];
        int offset = 0;
        while (offset < data.Length)
        {
            int read = stream.Read(data, offset, data.Length - offset);
            if (read <= 0)
                break;
            offset += read;
        }

        if (offset != expectedLength)
            throw new InvalidOperationException($"Embedded 32X boot ROM '{fileName}' had length {offset}, expected {expectedLength}.");

        return data;
    }
}
