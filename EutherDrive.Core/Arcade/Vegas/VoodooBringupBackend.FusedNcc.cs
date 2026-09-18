using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.Arcade.Vegas;

internal partial class VoodooBringupBackend
{
    private readonly bool _experimentFusedNcc = GauntletDarkLegacyAdapter.IsTruthy(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_EXPERIMENT_FUSED_NCC"));
    private readonly PackedNcc[]?[,] _packedNccLuts = new PackedNcc[2, 2][];
    private readonly record struct PackedNcc(ulong Rg, ulong B);

    public void InvalidateNccSamplingCaches()
    {
        Array.Clear(_tmuNccRgbaLutValid);
        Array.Clear(_packedNccLuts);
    }

    // Called by the existing NCC rebuild; shares its register-write invalidation.
    private void RebuildPackedNcc(int tmu, int table, TextureRgba[] colors)
    {
        PackedNcc[] packed = _packedNccLuts[tmu, table] ??= new PackedNcc[256];
        for (int i = 0; i < packed.Length; i++)
            packed[i] = new(colors[i].R | (ulong)colors[i].G << 32, colors[i].B);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReadNccRawPair(int tmu, int x0, int x1, int y, int width,
        bool sixteenBit, uint start, bool swap, out ushort raw0, out ushort raw1)
    {
        int memoryTmu = _experimentTmu1SampleTmu0Memory && tmu == 1 ? 0 : tmu;
        uint size = sixteenBit ? 2U : 1U, row = (uint)(y * width);
        uint a0 = MapTextureBankByteAddress(memoryTmu, start + (row + (uint)x0) * size);
        uint a1 = MapTextureBankByteAddress(memoryTmu, start + (row + (uint)x1) * size);
        uint word0 = ReadTexture32(a0 & ~3U);
        uint word1 = (a0 & ~3U) == (a1 & ~3U) ? word0 : ReadTexture32(a1 & ~3U);
        if (sixteenBit)
        {
            raw0 = (ushort)(word0 >> (int)((GetTexture16BitLaneByteAddress(a0) & 2U) * 8));
            raw1 = (ushort)(word1 >> (int)((GetTexture16BitLaneByteAddress(a1) & 2U) * 8));
            if (swap) { raw0 = BinaryPrimitives.ReverseEndianness(raw0); raw1 = BinaryPrimitives.ReverseEndianness(raw1); }
        }
        else
        {
            uint lane0 = a0 & 3U, lane1 = a1 & 3U;
            if (_experimentReverse8BitTextureSampleLanes) { lane0 = 3 - lane0; lane1 = 3 - lane1; }
            raw0 = (byte)(word0 >> (int)(lane0 * 8));
            raw1 = (byte)(word1 >> (int)(lane1 * 8));
        }
    }

    private TextureRgba SampleFusedNcc(int tmu, int x0, int x1, int y0, int y1,
        int width, int format, uint start, bool swap, PackedNcc[] lut, int fx, int fy)
    {
        bool alpha = format == 9;
        ReadNccRawPair(tmu,x0,x1,y0,width,alpha,start,swap,out ushort r00,out ushort r10);
        ReadNccRawPair(tmu,x0,x1,y1,width,alpha,start,swap,out ushort r01,out ushort r11);
        return FilterPackedNcc(lut,r00,r10,r01,r11,alpha,fx,fy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TextureRgba FilterPackedNcc(PackedNcc[] lut, ushort r00, ushort r10,
        ushort r01, ushort r11, bool alpha, int fx, int fy)
    {
        uint w00 = (uint)((256-fx)*(256-fy)), w10 = (uint)(fx*(256-fy));
        uint w01 = (uint)((256-fx)*fy), w11 = (uint)(fx*fy);
        PackedNcc a=lut[(byte)r00], b=lut[(byte)r10], c=lut[(byte)r01], d=lut[(byte)r11];
        ulong rg = a.Rg*w00 + b.Rg*w10 + c.Rg*w01 + d.Rg*w11 + 0x0000800000008000UL;
        // The 8-bit format has constant alpha. The 16-bit format's high byte
        // supplies alpha independently of the NCC lookup's RGB.
        ulong ba = alpha
            ? (a.B | (ulong)(r00>>8)<<32)*w00 + (b.B | (ulong)(r10>>8)<<32)*w10 +
              (c.B | (ulong)(r01>>8)<<32)*w01 + (d.B | (ulong)(r11>>8)<<32)*w11 + 0x0000800000008000UL
            : a.B*w00 + b.B*w10 + c.B*w01 + d.B*w11 + 0x8000;
        return new((byte)(rg>>16),(byte)(rg>>48),(byte)(ba>>16),alpha ? (byte)(ba>>48) : (byte)255);
    }
}
