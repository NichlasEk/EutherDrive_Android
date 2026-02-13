using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal static partial class md_main
    {
        public static bool g_masterSystemMode;
        public static byte[] g_masterSystemRom = Array.Empty<byte>();
        public static int g_masterSystemRomSize;
        public static string? g_masterSystemRomPath;
        public static SmsMapperType g_masterSystemMapper = SmsMapperType.Sega;
    }

    internal enum SmsMapperType
    {
        Sega = 0,
        Codemasters = 1,
        KoreanA000 = 2,
        Korean6000Ram = 3,
        Korean6000RamWide = 4,
        Msx8k = 5,
        Nemesis = 6
    }
}
