using System.Runtime.InteropServices;

namespace EutherDrive.Core.MdTracerCore
{
    // Riktig "union" i C# via explicit layout
    [StructLayout(LayoutKind.Explicit)]
    internal struct UNION_UINT
    {
        // 32-bit
        [FieldOffset(0)] public uint l;

        // 16-bit low word (LSW)
        [FieldOffset(0)] public ushort w;

        // 16-bit high word (MSW) = det som din kod efterfrågar som wup
        [FieldOffset(2)] public ushort wup;

        // Bytes (LSB..MSB)
        [FieldOffset(0)] public byte b0;
        [FieldOffset(1)] public byte b1;
        [FieldOffset(2)] public byte b2;
        [FieldOffset(3)] public byte b3;

        public byte this[int i]
        {
            get => i switch
            {
                0 => b0,
                1 => b1,
                2 => b2,
                3 => b3,
                _ => (byte)0
            };
            set
            {
                switch (i)
                {
                    case 0: b0 = value; break;
                    case 1: b1 = value; break;
                    case 2: b2 = value; break;
                    case 3: b3 = value; break;
                }
            }
        }
    }
}
