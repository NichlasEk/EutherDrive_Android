using System.Runtime.InteropServices;

namespace EutherDrive.Core.MdTracerCore;

// “union” så gammal kod med .l/.w/.wup/.b0 osv funkar
[StructLayout(LayoutKind.Explicit)]
internal struct md_u32
{
    [FieldOffset(0)] public uint   l;     // 32-bit
    [FieldOffset(0)] public ushort w;     // low 16
    [FieldOffset(2)] public ushort wup;   // high 16

    [FieldOffset(0)] public byte b0;
    [FieldOffset(1)] public byte b1;
    [FieldOffset(2)] public byte b2;
    [FieldOffset(3)] public byte b3;
}
