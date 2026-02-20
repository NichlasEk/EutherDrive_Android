using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal static class Z80Bits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Bit(this byte value, int bit) => ((value >> bit) & 1) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Bit(this ushort value, int bit) => ((value >> bit) & 1) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Lsb(this ushort value) => (byte)(value & 0xFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Msb(this ushort value) => (byte)(value >> 8);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort WithLsb(this ushort value, byte lsb) => (ushort)((value & 0xFF00) | lsb);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort WithMsb(this ushort value, byte msb) => (ushort)((value & 0x00FF) | (msb << 8));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitCount(this byte value) => BitOperations.PopCount(value);
    }
}
