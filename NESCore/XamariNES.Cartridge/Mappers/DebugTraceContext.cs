using System;

namespace XamariNES.Cartridge.Mappers
{
    public static class DebugTraceContext
    {
        [ThreadStatic]
        private static int _cpuPc;
        [ThreadStatic]
        private static long _cpuCycles;
        [ThreadStatic]
        private static int _ppuScanline;
        [ThreadStatic]
        private static int _ppuDot;

        public static void SetCpu(int pc, long cycles)
        {
            _cpuPc = pc & 0xFFFF;
            _cpuCycles = cycles;
        }

        public static void SetPpu(int scanline, int dot)
        {
            _ppuScanline = scanline;
            _ppuDot = dot;
        }

        public static int CpuPc => _cpuPc & 0xFFFF;

        public static long CpuCycles => _cpuCycles;

        public static string FormatCpu()
        {
            return $"cpuPc=0x{CpuPc:X4} cpuCycles={CpuCycles}";
        }

        public static string FormatPpu()
        {
            return $"ppuScanline={_ppuScanline} ppuDot={_ppuDot}";
        }
    }
}
