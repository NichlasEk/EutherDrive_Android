using System;
using System.Runtime.InteropServices;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        internal struct ResetFingerprintSnapshot
        {
            public ushort Status;
            public int Scanline;
            public long FrameCounter;
            public bool DmaActive;
            public byte DmaMode;
            public uint DmaSourceAddress;
            public int DmaLength;
            public bool DmaFillRequest;
            public ushort DmaFillData;
            public ushort VdpDestAddress;
            public int VdpCode;
            public bool CommandSelect;
            public ushort CommandWord;
            public byte AutoIncrement;
            public byte StatusVBlank;
            public byte StatusSprite;
            public byte StatusCollision;
            public byte StatusFrame;
            public byte StatusVBlankRequest;
            public byte StatusHBlankRequest;
            public byte StatusDma;
            public byte StatusFull;
            public uint RegHash;
            public uint VramHash;
            public uint CramHash;
            public uint VsramHash;
        }

        internal ResetFingerprintSnapshot GetResetFingerprintSnapshot()
        {
            ushort status = PeekVdpStatus();
            uint regsHash = ComputeByteArrayHash(g_vdp_reg, Math.Min(g_vdp_reg.Length, 24));

            return new ResetFingerprintSnapshot
            {
                Status = status,
                Scanline = g_scanline,
                FrameCounter = _frameCounter,
                DmaActive = g_dma_leng > 0,
                DmaMode = (byte)g_dma_mode,
                DmaSourceAddress = g_dma_src_addr,
                DmaLength = g_dma_leng,
                DmaFillRequest = g_dma_fill_req,
                DmaFillData = g_dma_fill_data,
                VdpDestAddress = g_vdp_reg_dest_address,
                VdpCode = g_vdp_reg_code,
                CommandSelect = g_command_select,
                CommandWord = g_command_word,
                AutoIncrement = g_vdp_reg_15_autoinc,
                StatusVBlank = g_vdp_status_7_vinterrupt,
                StatusSprite = g_vdp_status_6_sprite,
                StatusCollision = g_vdp_status_5_collision,
                StatusFrame = g_vdp_status_4_frame,
                StatusVBlankRequest = g_vdp_status_3_vbrank,
                StatusHBlankRequest = g_vdp_status_2_hbrank,
                StatusDma = g_vdp_status_1_dma,
                StatusFull = g_vdp_status_8_full,
                RegHash = regsHash,
                VramHash = ComputeSampledHash(g_vram),
                CramHash = ComputeSampledHash(MemoryMarshal.AsBytes(g_cram.AsSpan())),
                VsramHash = ComputeSampledHash(MemoryMarshal.AsBytes(g_vsram.AsSpan()))
            };
        }

        private static uint ComputeByteArrayHash(byte[] data, int maxLength)
        {
            if (data == null || data.Length == 0)
                return 0;

            uint hash = 2166136261u;
            int limit = Math.Min(data.Length, maxLength);
            for (int i = 0; i < limit; i++)
            {
                hash ^= data[i];
                hash *= 16777619u;
            }
            return hash;
        }

        private static uint ComputeSampledHash(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return 0;

            uint hash = 2166136261u;
            int sampleLen = Math.Min(64, data.Length);
            int midStart = Math.Max(0, (data.Length - sampleLen) / 2);
            int endStart = Math.Max(0, data.Length - sampleLen);

            Span<int> offsets = stackalloc int[] { 0, midStart, endStart };
            foreach (int start in offsets)
            {
                for (int i = 0; i < sampleLen && start + i < data.Length; i++)
                {
                    hash ^= data[start + i];
                    hash *= 16777619u;
                }
            }

            return hash;
        }
    }
}
