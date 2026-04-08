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
        // Load the available bytes and pad to 1024 if the file is smaller.
        byte[] vectors = new byte[M68kVectorsLength];
        try 
        {
            byte[] raw = LoadResource("m68k_vectors.bin", -1);
            Buffer.BlockCopy(raw, 0, vectors, 0, Math.Min(raw.Length, M68kVectorsLength));
        }
        catch 
        {
            // Fallback
        }

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

        int len = expectedLength > 0 ? expectedLength : (int)stream.Length;
        byte[] data = new byte[len];
        int offset = 0;
        while (offset < data.Length)
        {
            int read = stream.Read(data, offset, data.Length - offset);
            if (read <= 0)
                break;
            offset += read;
        }

        if (expectedLength > 0 && offset != expectedLength)
            throw new InvalidOperationException($"Embedded 32X boot ROM '{fileName}' had length {offset}, expected {expectedLength}.");

        return data;
    }
}
