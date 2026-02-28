using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ePceCD
{
    public enum DotClock : int
    {
        MHZ_10 = 2,
        MHZ_5 = 4,
        MHZ_7 = 3
    }

    [Serializable]
    public class PPU : IDisposable // HuC6270A
    {
        private static readonly bool TraceVdcRegs =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VDC_LOG"), "1", StringComparison.Ordinal);
        private static readonly bool AlignSpritePattern =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_PATTERN_ALIGN"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSpriteFetch =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_FETCH_TRACE"), "1", StringComparison.Ordinal);
        private static readonly int TraceSpriteFetchLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_FETCH_TRACE_LIMIT"), out int sfLim) && sfLim > 0 ? sfLim : 4000;
        private static readonly int TraceVdcRegsLimit = 200;
        private int _traceVdcRegsCount;
        private int _traceSpriteFetchCount;
        [Serializable]
        private struct SpriteAttribute
        {
            public int m_X;
            public int m_Y;
            public int m_Pattern;
            public int m_Mode1Offset;
            [NonSerialized]
            public bool m_Mode1;
            //TODO: add CG support, ex: YS I & II
            public bool m_CGPage;

            public bool m_VerticalFlip;
            public bool m_HorizontalFlip;

            public int m_Width;
            public int m_Height;

            public bool m_Priority;
            public int m_Palette;
        }
        private SpriteAttribute[] m_SAT;
        private int m_RenderLine;
        public int CYCLES_PER_LINE = 1368;
        public int SCREEN_WIDTH = 256;
        private ushort[] m_VRAM;
        private int[] PALETTE = new int[512];
        private DotClock m_VCE_DotClock;

        [NonSerialized]
        public IntPtr _screenBufPtr;

        public int[] _screenBuf;
        public bool FrameReady;

        #region REGISTER Vars

        private bool m_DoSAT_DMA;
        private bool m_WaitingIRQ;

        private bool m_VDC_BSY;
        private bool m_VDC_VD;
        private bool m_VDC_DV;
        private bool m_VDC_DS;
        private bool m_VDC_RR;
        private bool m_VDC_OR;
        private bool m_VDC_CR;

        private int m_VDC_Reg;
        private int m_VDC_MAWR;
        private int m_VDC_MARR;
        private int m_VDC_RCR;
        private int m_VDC_BXR;
        private int m_VDC_BYR;
        private int m_VDC_BYR_Offset;
        private ushort m_VDC_DSR;
        private ushort m_VDC_DESR;
        private ushort m_VDC_LENR;
        private ushort m_VDC_VSAR;
        private int m_VDC_VSR;
        private int m_VDC_MWR;

        private static readonly int[] ScreenSizeX = { 32, 64, 128, 128, 32, 64, 128, 128 };
        private static readonly int[] ScreenSizeY = { 32, 32, 32, 32, 64, 64, 64, 64 };
        private static readonly int[] ScreenSizeXPixels = { 32 * 8, 64 * 8, 128 * 8, 128 * 8, 32 * 8, 64 * 8, 128 * 8, 128 * 8 };
        private static readonly int[] ScreenSizeYPixels = { 32 * 8, 32 * 8, 32 * 8, 32 * 8, 64 * 8, 64 * 8, 64 * 8, 64 * 8 };
        private static readonly int[] ScreenSizeXPixelsMask = {
            (32 * 8) - 1, (64 * 8) - 1, (128 * 8) - 1, (128 * 8) - 1,
            (32 * 8) - 1, (64 * 8) - 1, (128 * 8) - 1, (128 * 8) - 1
        };
        private static readonly int[] ScreenSizeYPixelsMask = {
            (32 * 8) - 1, (32 * 8) - 1, (32 * 8) - 1, (32 * 8) - 1,
            (64 * 8) - 1, (64 * 8) - 1, (64 * 8) - 1, (64 * 8) - 1
        };

        private int GetEffectiveVdw()
        {
            int vdw = m_VDC_VDW + 1;
            if (vdw <= 0)
                vdw = 1;
            if (vdw > 262)
                vdw = 262;
            return vdw;
        }

        private void LogVdcRegs(string tag)
        {
            if (!TraceVdcRegs)
                return;
            if (_traceVdcRegsCount++ >= TraceVdcRegsLimit)
                return;
            Console.WriteLine($"[PCE-VDC] {tag} reg=0x{m_VDC_Reg:X2} HDR={m_VDC_HDR} VSR=0x{m_VDC_VSR:X4} VDW={m_VDC_VDW} line={m_RenderLine}");
        }

        private int GetEffectiveVds(int vdw)
        {
            int vds = (m_VDC_VSR >> 8) & 0xFF;
            if (vds < 0) vds = 0;
            if (vds > 261) vds = 261;
            int maxStart = 262 - vdw;
            if (vds > maxStart)
                vds = maxStart;
            if (vds < 0)
                vds = 0;
            return vds;
        }

        public int m_VDC_HDR;
        public int m_VDC_VDW;
        private int m_LatchedVDS;
        private int m_LatchedVDW;

        private int m_VDC_BAT_Width;
        private int m_VDC_BAT_Height;
        private int m_LatchedBxr;
        private int m_BgCounterY;
        private int m_BgOffsetY;
        private bool m_LatchedEnableBackground;
        private bool m_LatchedEnableSprites;
        private int m_LatchedMWR;

        private bool m_VDC_DMA_Enable;
        private bool m_VDC_SATBDMA_IRQ;
        private bool m_VDC_VRAMDMA_IRQ;
        private bool m_VDC_SRCDECR;
        private bool m_VDC_DSTDECR;
        private bool m_VDC_SATB_ENA;

        private bool m_VDC_EnableBackground;
        private bool m_VDC_EnableSprites;
        private bool m_VDC_VBKIRQ;
        private bool m_VDC_RCRIRQ;
        private bool m_VDC_SprOvIRQ;
        private bool m_VDC_Spr0Col;
        private int m_VDC_Increment;
        private int m_VdcStatusLogCount;
        private bool m_VdcStatusSuppressed;
        private int m_VdcDmaLogCount;
        private bool m_VdcDmaSuppressed;

        // VCE REGISTERS
        //private bool m_VCE_BW;
        //private bool m_VCE_Blur;
        private ushort[] m_VCE;
        private int m_VCE_Index;
        //private int ScanlineCount;
        //private bool Grayscale;

        #endregion

        [NonSerialized]
        public IRenderHandler host;

        public PPU(IRenderHandler render)
        {
            host = render;

            m_VRAM = new ushort[0x10000];
            m_SAT = new SpriteAttribute[0x40];
            m_VCE = new ushort[0x200];
            m_VCE_Index = 0;

            _screenBuf = new int[1024 * 1024];
            _screenBufPtr = Marshal.AllocHGlobal(1024 * 1024 * sizeof(int));


            for (int i = 0; i < 512; i++)
            {
                int b = ((i) & 0x7) * 0x49 >> 1;
                int r = ((i >> 3) & 0x7) * 0x49 >> 1;
                int g = ((i >> 6) & 0x7) * 0x49 >> 1;
                //PALETTE[i] = (r << 16) | (g << 8) | b;
                PALETTE[i] = (0xFF << 24) | (r << 16) | (g << 8) | b; //ARGB888
            }

            for (int i = 0; i < 0x40; i++)
                m_SAT[i] = new SpriteAttribute();

            m_RenderLine = 0;
            m_DoSAT_DMA = false;
            m_WaitingIRQ = false;

            m_VCE_DotClock = DotClock.MHZ_5;
            m_VDC_Increment = 1;

            m_VDC_BSY = false;
            m_LatchedVDS = 14;
            m_LatchedVDW = 240;
        }

        public PPU()
        {
            host = null!;

            m_VRAM = new ushort[0x10000];
            m_SAT = new SpriteAttribute[0x40];
            m_VCE = new ushort[0x200];
            m_VCE_Index = 0;

            _screenBuf = new int[1024 * 1024];
            _screenBufPtr = Marshal.AllocHGlobal(1024 * 1024 * sizeof(int));

            for (int i = 0; i < 512; i++)
            {
                int b = ((i) & 0x7) * 0x49 >> 1;
                int r = ((i >> 3) & 0x7) * 0x49 >> 1;
                int g = ((i >> 6) & 0x7) * 0x49 >> 1;
                PALETTE[i] = (0xFF << 24) | (r << 16) | (g << 8) | b;
            }

            for (int i = 0; i < 0x40; i++)
                m_SAT[i] = new SpriteAttribute();

            m_RenderLine = 0;
            m_DoSAT_DMA = false;
            m_WaitingIRQ = false;

            m_VCE_DotClock = DotClock.MHZ_5;
            m_VDC_Increment = 1;

            m_VDC_BSY = false;
            m_LatchedVDS = 14;
            m_LatchedVDW = 240;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_screenBufPtr);
        }

        public unsafe void Reset()
        {
            Array.Clear(m_VRAM);

            for (var i = 0; i < 64; i++)
            {
                m_SAT[i].m_X = 0;
                m_SAT[i].m_Y = 0;
            }

            m_WaitingIRQ = false;
            m_BgCounterY = 0;
            m_BgOffsetY = 0;
            m_LatchedBxr = 0;
        }

        public unsafe void tick()
        {
            if (m_RenderLine == 0)
            {
                m_LatchedVDS = GetEffectiveVds(GetEffectiveVdw());
                m_LatchedVDW = GetEffectiveVdw();
            }
            int vdw = m_LatchedVDW;
            int vds = Math.Max(14, m_LatchedVDS + 2);
            int vde = Math.Min(261, vds + vdw - 1);
            if (m_RenderLine + 1 > vde + 1)
            {
                HandleDMA();
            }
            else if (m_RenderLine >= Math.Max(14, vds) && m_RenderLine <= Math.Min(256, vde))
            {
                DrawScanLine();
            }

            m_RenderLine++;

            if (m_RenderLine + 1 == vde + 1)
            {
                m_DoSAT_DMA = m_DoSAT_DMA | m_VDC_SATB_ENA;
                if (m_VDC_VBKIRQ)
                {
                    m_VDC_VD = true;
                    m_WaitingIRQ = true;
                    if (TraceVdcRegs && !m_VdcStatusSuppressed && m_VdcStatusLogCount < 20)
                    {
                        Console.WriteLine($"[PCE-VDC] VBK line={m_RenderLine} VSR=0x{m_VDC_VSR:X4} VDW={m_VDC_VDW} RCR={m_VDC_RCR} CR={(m_VDC_EnableBackground ? 1 : 0)}:{(m_VDC_EnableSprites ? 1 : 0)}");
                        m_VdcStatusLogCount++;
                    }
                }
            }
            else
            {
                int rasterLine = m_RenderLine - vds;
                if (rasterLine >= 0 && rasterLine < vdw && (m_VDC_RCR - 64) == rasterLine)
                {
                    if (m_VDC_RCRIRQ)
                    {
                        m_VDC_RR = true;
                        m_WaitingIRQ = true;
                        if (TraceVdcRegs && !m_VdcStatusSuppressed && m_VdcStatusLogCount < 20)
                        {
                            Console.WriteLine($"[PCE-VDC] RCR line={m_RenderLine} RCR={m_VDC_RCR}");
                            m_VdcStatusLogCount++;
                        }
                    }
                }
            }

            if (m_RenderLine >= 262)
            {
                m_RenderLine = 0;
                //ConvertColor();
                Marshal.Copy(_screenBufPtr, _screenBuf, 0, _screenBuf.Length);
                FrameReady = true;
                host.RenderFrame(_screenBuf, SCREEN_WIDTH, vdw);
            }
        }

        private unsafe void HandleDMA()
        {
            int DmaCycles = CYCLES_PER_LINE / (int)m_VCE_DotClock;
            if (m_DoSAT_DMA)
            {
                DmaCycles -= 256;

                Parallel.For(0, 64, i =>
                {
                    int g = m_VDC_VSAR + i * 4;
                    int sat0 = m_VRAM[g++];
                    int sat1 = m_VRAM[g++];
                    int sat2 = m_VRAM[g++];
                    int sat3 = m_VRAM[g++];
                    int cgy = (sat3 >> 12) & 0x03;
                    int cgx = (sat3 >> 8) & 0x01;
                    bool mode1 = ((m_VDC_MWR >> 2) & 0x03) == 1;
                    int mode1Offset = mode1 ? (sat2 & 1) << 5 : 0;
                    int width = (cgx == 0) ? 1 : 2;
                    int height = (cgy == 0) ? 16 : (cgy == 1 ? 32 : 64);
                    int pattern = (sat2 >> 1) & 0x3FF;
                    if (AlignSpritePattern)
                    {
                        if (width == 2) pattern &= 0xFFFE;
                        switch (cgy)
                        {
                            case 1: pattern &= 0xFFFD; break;
                            case 2:
                            case 3: pattern &= 0xFFF9; break;
                        }
                    }
                    m_SAT[i].m_Y = (sat0 & 0x3FF) - 64;
                    m_SAT[i].m_X = (sat1 & 0x3FF) - 32;
                    m_SAT[i].m_Pattern = pattern << 6;
                    m_SAT[i].m_Mode1Offset = mode1Offset;
                    m_SAT[i].m_Mode1 = mode1;
                    m_SAT[i].m_CGPage = (sat2 & 0x0001) != 0;
                    m_SAT[i].m_Palette = ((sat3 & 0xF) << 4) | ((i == 0) ? 0x4100 : 0x2100);
                    m_SAT[i].m_Priority = (sat3 & 0x80) != 0;
                    m_SAT[i].m_Width = width;
                    m_SAT[i].m_Height = height;
                    m_SAT[i].m_HorizontalFlip = (sat3 & 0x0800) != 0;
                    m_SAT[i].m_VerticalFlip = (sat3 & 0x8000) != 0;
                });

                if (m_VDC_SATBDMA_IRQ)
                {
                    m_VDC_DS = true;
                    m_WaitingIRQ = true;
                }
                m_DoSAT_DMA = false;
            }

            if (m_VDC_DMA_Enable)
            {
                while (DmaCycles >= 2)
                {
                    m_VRAM[m_VDC_DSTDECR ? m_VDC_DESR-- : m_VDC_DESR++] =
                        m_VRAM[m_VDC_SRCDECR ? m_VDC_DSR-- : m_VDC_DSR++];
                    DmaCycles -= 2;
                    if (--m_VDC_LENR == 0)
                    {
                        m_VDC_DMA_Enable = false;
                        if (TraceVdcRegs && !m_VdcDmaSuppressed && m_VdcDmaLogCount < 50)
                        {
                            Console.WriteLine($"[PCE-VDC] VRAMDMA done line={m_RenderLine}");
                            m_VdcDmaLogCount++;
                        }
                        if (m_VDC_VRAMDMA_IRQ)
                        {
                            m_VDC_DV = true;
                            m_WaitingIRQ = true;
                        }
                    }
                }
            }
        }

        public void RebuildSatFromVram()
        {
            for (int i = 0; i < 64; i++)
            {
                int g = m_VDC_VSAR + i * 4;
                int sat0 = m_VRAM[g++];
                int sat1 = m_VRAM[g++];
                int sat2 = m_VRAM[g++];
                int sat3 = m_VRAM[g++];
                int cgy = (sat3 >> 12) & 0x03;
                int cgx = (sat3 >> 8) & 0x01;
                bool mode1 = ((m_VDC_MWR >> 2) & 0x03) == 1;
                int mode1Offset = mode1 ? (sat2 & 1) << 5 : 0;
                int width = (cgx == 0) ? 1 : 2;
                int height = (cgy == 0) ? 16 : (cgy == 1 ? 32 : 64);
                int pattern = (sat2 >> 1) & 0x3FF;
                if (AlignSpritePattern)
                {
                    if (width == 2) pattern &= 0xFFFE;
                    switch (cgy)
                    {
                        case 1: pattern &= 0xFFFD; break;
                        case 2:
                        case 3: pattern &= 0xFFF9; break;
                    }
                }
                m_SAT[i].m_Y = (sat0 & 0x3FF) - 64;
                m_SAT[i].m_X = (sat1 & 0x3FF) - 32;
                m_SAT[i].m_Pattern = pattern << 6;
                m_SAT[i].m_Mode1Offset = mode1Offset;
                m_SAT[i].m_Mode1 = mode1;
                m_SAT[i].m_CGPage = (sat2 & 0x0001) != 0;
                m_SAT[i].m_Palette = ((sat3 & 0xF) << 4) | ((i == 0) ? 0x4100 : 0x2100);
                m_SAT[i].m_Priority = (sat3 & 0x80) != 0;
                m_SAT[i].m_Width = width;
                m_SAT[i].m_Height = height;
                m_SAT[i].m_HorizontalFlip = (sat3 & 0x0800) != 0;
                m_SAT[i].m_VerticalFlip = (sat3 & 0x8000) != 0;
            }
        }

        public void AfterStateLoad()
        {
            // Keep serialized scanline/DMA timing as-is.
            // Forcing line 0 + SAT DMA here made HuCard savestates resume at the wrong point.
            FrameReady = false;
        }

        public void DumpDebugSnapshot(string directory, string prefix)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Directory cannot be empty.", nameof(directory));
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Prefix cannot be empty.", nameof(prefix));

            Directory.CreateDirectory(directory);

            var vramBytes = new byte[m_VRAM.Length * sizeof(ushort)];
            Buffer.BlockCopy(m_VRAM, 0, vramBytes, 0, vramBytes.Length);
            File.WriteAllBytes(Path.Combine(directory, $"{prefix}_vram.bin"), vramBytes);

            var vceBytes = new byte[m_VCE.Length * sizeof(ushort)];
            Buffer.BlockCopy(m_VCE, 0, vceBytes, 0, vceBytes.Length);
            File.WriteAllBytes(Path.Combine(directory, $"{prefix}_vce.bin"), vceBytes);

            using (var writer = new StreamWriter(Path.Combine(directory, $"{prefix}_ppu.txt")))
            {
                writer.WriteLine($"render_line={m_RenderLine}");
                writer.WriteLine($"frame_ready={FrameReady}");
                writer.WriteLine($"dot_clock={m_VCE_DotClock}");
                writer.WriteLine($"screen_width={SCREEN_WIDTH}");
                writer.WriteLine($"cycles_per_line={CYCLES_PER_LINE}");
                writer.WriteLine($"reg_vsr=0x{m_VDC_VSR:X4}");
                writer.WriteLine($"reg_vdw=0x{m_VDC_VDW:X4}");
                writer.WriteLine($"reg_hdr=0x{m_VDC_HDR:X4}");
                writer.WriteLine($"reg_bxr=0x{m_VDC_BXR:X4}");
                writer.WriteLine($"reg_byr=0x{m_VDC_BYR:X4}");
                writer.WriteLine($"reg_rcr=0x{m_VDC_RCR:X4}");
                writer.WriteLine($"reg_mawr=0x{m_VDC_MAWR:X4}");
                writer.WriteLine($"reg_marr=0x{m_VDC_MARR:X4}");
                writer.WriteLine($"reg_vsar=0x{m_VDC_VSAR:X4}");
                writer.WriteLine($"spr_pattern_align={(AlignSpritePattern ? 1 : 0)}");
                writer.WriteLine($"enable_bg={m_VDC_EnableBackground}");
                writer.WriteLine($"enable_spr={m_VDC_EnableSprites}");
                writer.WriteLine($"do_sat_dma={m_DoSAT_DMA}");
                writer.WriteLine($"waiting_irq={m_WaitingIRQ}");
            }

            using (var writer = new StreamWriter(Path.Combine(directory, $"{prefix}_sprites.txt")))
            {
                writer.WriteLine("idx x y pattern mode1 mode1ofs cgpage hflip vflip w h prio pal");
                for (int i = 0; i < m_SAT.Length; i++)
                {
                    var s = m_SAT[i];
                    writer.WriteLine(
                        $"{i:D2} {s.m_X:D4} {s.m_Y:D4} 0x{s.m_Pattern:X4} {(s.m_Mode1 ? 1 : 0)} {s.m_Mode1Offset:D2} {(s.m_CGPage ? 1 : 0)} {(s.m_HorizontalFlip ? 1 : 0)} {(s.m_VerticalFlip ? 1 : 0)} {s.m_Width:D1} {s.m_Height:D1} {(s.m_Priority ? 1 : 0)} {s.m_Palette:D2}");
                }
            }

            var satWords = new ushort[0x100];
            for (int i = 0; i < satWords.Length; i++)
                satWords[i] = m_VRAM[0x7F00 + i];
            var satBytes = new byte[satWords.Length * sizeof(ushort)];
            Buffer.BlockCopy(satWords, 0, satBytes, 0, satBytes.Length);
            File.WriteAllBytes(Path.Combine(directory, $"{prefix}_sat_raw.bin"), satBytes);
        }

        public void AppendDeterminismTrace(StringBuilder sb)
        {
            if (sb == null)
                return;

            sb.Append(" ppu_line=").Append(m_RenderLine);
            sb.Append(" vsr=").Append(m_VDC_VSR.ToString("X4"));
            sb.Append(" vdw=").Append(m_VDC_VDW.ToString("X4"));
            sb.Append(" hdr=").Append(m_VDC_HDR.ToString("X4"));
            sb.Append(" bxr=").Append(m_VDC_BXR.ToString("X4"));
            sb.Append(" byr=").Append(m_VDC_BYR.ToString("X4"));
            sb.Append(" mawr=").Append(m_VDC_MAWR.ToString("X4"));
            sb.Append(" marr=").Append(m_VDC_MARR.ToString("X4"));
            sb.Append(" vsar=").Append(m_VDC_VSAR.ToString("X4"));
            sb.Append(" mwr=").Append(m_VDC_MWR.ToString("X4"));
            sb.Append(" bg=").Append(m_VDC_EnableBackground ? 1 : 0);
            sb.Append(" spr=").Append(m_VDC_EnableSprites ? 1 : 0);
            sb.Append(" sat_dma=").Append(m_DoSAT_DMA ? 1 : 0);
            sb.Append(" wait_irq=").Append(m_WaitingIRQ ? 1 : 0);
            sb.Append(" vram_hash=").Append(ComputeVramHash().ToString("X16"));
            sb.Append(" sat_hash=").Append(ComputeSatHash().ToString("X16"));
            sb.Append(" vce_hash=").Append(ComputeVceHash().ToString("X16"));
        }

        private static ulong Fnv1a64(ulong hash, uint value)
        {
            const ulong prime = 1099511628211ul;
            hash ^= (byte)value;
            hash *= prime;
            hash ^= (byte)(value >> 8);
            hash *= prime;
            hash ^= (byte)(value >> 16);
            hash *= prime;
            hash ^= (byte)(value >> 24);
            hash *= prime;
            return hash;
        }

        private ulong ComputeVramHash()
        {
            ulong h = 1469598103934665603ul;
            for (int i = 0; i < m_VRAM.Length; i++)
                h = Fnv1a64(h, m_VRAM[i]);
            return h;
        }

        private ulong ComputeVceHash()
        {
            ulong h = 1469598103934665603ul;
            for (int i = 0; i < m_VCE.Length; i++)
                h = Fnv1a64(h, m_VCE[i]);
            return h;
        }

        private ulong ComputeSatHash()
        {
            ulong h = 1469598103934665603ul;
            for (int i = 0; i < m_SAT.Length; i++)
            {
                var s = m_SAT[i];
                h = Fnv1a64(h, (uint)s.m_X);
                h = Fnv1a64(h, (uint)s.m_Y);
                h = Fnv1a64(h, (uint)s.m_Pattern);
                h = Fnv1a64(h, (uint)s.m_Mode1Offset);
                h = Fnv1a64(h, (uint)(s.m_Mode1 ? 1 : 0));
                h = Fnv1a64(h, (uint)(s.m_CGPage ? 1 : 0));
                h = Fnv1a64(h, (uint)(s.m_VerticalFlip ? 1 : 0));
                h = Fnv1a64(h, (uint)(s.m_HorizontalFlip ? 1 : 0));
                h = Fnv1a64(h, (uint)s.m_Width);
                h = Fnv1a64(h, (uint)s.m_Height);
                h = Fnv1a64(h, (uint)(s.m_Priority ? 1 : 0));
                h = Fnv1a64(h, (uint)s.m_Palette);
            }
            return h;
        }

        private unsafe void DrawScanLine()
        {
            int vdw = m_LatchedVDW;
            int vds = Math.Max(14, m_LatchedVDS + 2);
            int visibleLine = m_RenderLine - vds;
            if (visibleLine < 0 || visibleLine >= vdw)
                return;

            int i;
            m_LatchedEnableBackground = m_VDC_EnableBackground;
            m_LatchedEnableSprites = m_VDC_EnableSprites;
            m_LatchedMWR = m_VDC_MWR;
            m_LatchedBxr = m_VDC_BXR;
            if (visibleLine == 0)
                m_BgCounterY = m_VDC_BYR;
            else
                m_BgCounterY = (m_BgCounterY + 1) & 0x3FF;
            m_BgOffsetY = m_BgCounterY;
            int* ScanLinePtr = (int*)_screenBufPtr.ToPointer() + SCREEN_WIDTH * visibleLine;

            for (i = 0; i < SCREEN_WIDTH; i++) ScanLinePtr[i] = 0x100;

            if (m_LatchedEnableSprites)
            {
                int BufferIndexes = 0;
                int BufferUsage;
                SpriteAttribute[] SprBuffer = new SpriteAttribute[17];
                for (i = 0, BufferUsage = 0; i < 64 && BufferUsage < 17; i++)
                {
                    int y = m_SAT[i].m_Y;
                    if (visibleLine < y || visibleLine >= y + m_SAT[i].m_Height) continue;
                    BufferUsage += m_SAT[i].m_Width;
                    SprBuffer[BufferIndexes++] = m_SAT[i];
                }
                if (BufferUsage > 16)
                {
                    if (m_VDC_SprOvIRQ)
                    {
                        m_VDC_OR = true;
                        m_WaitingIRQ = true;
                    }
                    BufferUsage = 16;
                }
                for (i = BufferIndexes - 1; i >= 0; i--)
                {
                    int SprOffY;
                    if (SprBuffer[i].m_VerticalFlip)
                        SprOffY = SprBuffer[i].m_Height - 1 - visibleLine + SprBuffer[i].m_Y;
                    else
                        SprOffY = visibleLine - SprBuffer[i].m_Y;
                    int tileY = SprOffY >> 4;
                    int tileLineOffset = tileY * 128;
                    int offsetY = SprOffY & 0xF;
                    int tile = SprBuffer[i].m_Pattern + tileLineOffset + offsetY + SprBuffer[i].m_Mode1Offset;
                    int x = SprBuffer[i].m_X;
                    int* spx = ScanLinePtr;
                    spx += x;
                    if (x >= (m_VDC_HDR + 1) << 3) continue;
                    if (x > -32)
                    {
                        switch (SprBuffer[i].m_Width)
                        {
                            case 1:
                                DrawSPRTile(ScanLinePtr, ref spx, SprBuffer[i].m_Palette, tile, SprBuffer[i].m_Priority, SprBuffer[i].m_HorizontalFlip, SprBuffer[i].m_Mode1);
                                TraceSpriteTileFetch(visibleLine, i, SprBuffer[i], tile);
                                break;
                            case 2:
                                if (SprBuffer[i].m_HorizontalFlip)
                                {
                                    DrawSPRTile(ScanLinePtr, ref spx, SprBuffer[i].m_Palette, tile + 64, SprBuffer[i].m_Priority, true, SprBuffer[i].m_Mode1);
                                    DrawSPRTile(ScanLinePtr, ref spx, SprBuffer[i].m_Palette, tile, SprBuffer[i].m_Priority, true, SprBuffer[i].m_Mode1);
                                    TraceSpriteTileFetch(visibleLine, i, SprBuffer[i], tile + 64);
                                    TraceSpriteTileFetch(visibleLine, i, SprBuffer[i], tile);
                                }
                                else
                                {
                                    DrawSPRTile(ScanLinePtr, ref spx, SprBuffer[i].m_Palette, tile, SprBuffer[i].m_Priority, false, SprBuffer[i].m_Mode1);
                                    DrawSPRTile(ScanLinePtr, ref spx, SprBuffer[i].m_Palette, tile + 64, SprBuffer[i].m_Priority, false, SprBuffer[i].m_Mode1);
                                    TraceSpriteTileFetch(visibleLine, i, SprBuffer[i], tile);
                                    TraceSpriteTileFetch(visibleLine, i, SprBuffer[i], tile + 64);
                                }
                                break;
                        }
                    }
                }
            }

            if (m_LatchedEnableBackground)
            {
                int screenReg = (m_LatchedMWR >> 4) & 0x07;
                int screenSizeX = ScreenSizeX[screenReg];
                int bgY = m_BgOffsetY & ScreenSizeYPixelsMask[screenReg];
                int tileY = bgY & 7;
                int batOffset = (bgY >> 3) * screenSizeX;
                int prevTileCol = -1;
                int palette = 0;
                int byte1 = 0, byte2 = 0, byte3 = 0, byte4 = 0;

                for (i = 0; i < SCREEN_WIDTH; i++)
                {
                    int bgX = (m_LatchedBxr + i) & ScreenSizeXPixelsMask[screenReg];
                    int tileCol = bgX >> 3;
                    if (tileCol != prevTileCol)
                    {
                        int batEntry = m_VRAM[batOffset + tileCol];
                        int tileIndex = batEntry & 0x07FF;
                        palette = ((batEntry >> 12) & 0x0F) << 4;
                        int tileData = tileIndex << 4;
                        int lineStartA = tileData + tileY;
                        int lineStartB = lineStartA + 8;
                        byte1 = m_VRAM[lineStartA] & 0xFF;
                        byte2 = (m_VRAM[lineStartA] >> 8) & 0xFF;
                        byte3 = m_VRAM[lineStartB] & 0xFF;
                        byte4 = (m_VRAM[lineStartB] >> 8) & 0xFF;
                        prevTileCol = tileCol;
                    }
                    int tileX = 7 - (bgX & 7);
                    int bgColor =
                        ((byte1 >> tileX) & 1) |
                        (((byte2 >> tileX) & 1) << 1) |
                        (((byte3 >> tileX) & 1) << 2) |
                        (((byte4 >> tileX) & 1) << 3);
                    int* dst = ScanLinePtr + i;
                    if ((*dst & 0x1000) != 0)
                        continue;
                    if (bgColor == 0 && (*dst & 0x6000) != 0)
                        continue;
                    *dst = palette | bgColor;
                }

            }

            //colorindex to ARGB8888
            //ushort grayscaleBit = (ushort)(Grayscale ? 0x200 : 0);
            int color = 0;
            int* LineWritePtr = ScanLinePtr;
            for (i = 0; i < SCREEN_WIDTH; i++, ScanLinePtr++)
            {
                if ((*ScanLinePtr & 0x6000) == 0x6000) m_VDC_CR = m_VDC_Spr0Col;
                //color = PALETTE[m_VCE[*ScanLinePtr & 0x1FF] | grayscaleBit];
                color = PALETTE[m_VCE[*ScanLinePtr & 0x1FF]];
                *(LineWritePtr++) = color;
            }
        }

        public unsafe void ConvertColor()
        {
            int color = 0;
            for (int y = 0; y < m_VDC_VDW; y++)
            {
                int* LineWritePtr = (int*)_screenBufPtr.ToPointer() + SCREEN_WIDTH * y;
                for (int i = 0; i < SCREEN_WIDTH; i++, LineWritePtr++)
                {
                    color = PALETTE[m_VCE[*LineWritePtr & 0x1FF]];
                    *(LineWritePtr) = color;
                }
            }
        }

        public unsafe void DrawSPRTile(int* ScanLinePtr, ref int* px, int palette, int tile, bool priority, bool flip, bool mode1)
        {
            int p0 = m_VRAM[tile];
            int p1 = m_VRAM[tile + 16];
            int p2 = mode1 ? 0 : m_VRAM[tile + 32];
            int p3 = mode1 ? 0 : m_VRAM[tile + 48];
            int color = 0;
            int* scanEnd = ScanLinePtr + SCREEN_WIDTH;

            if (priority) palette |= 0x1000;

            if (flip)
                for (int x = 0; x < 16; x++, px++)
                {
                    if (px >= scanEnd) break;
                    if (px < ScanLinePtr) continue;
                    color =
                        ((p0 >> x) & 1) |
                        (((p1 >> x) & 1) << 1) |
                        (((p2 >> x) & 1) << 2) |
                        (((p3 >> x) & 1) << 3);
                    if (color == 0) continue;
                    *px = palette | color;
                }
            else
                for (int x = 15; x >= 0; x--, px++)
                {
                    if (px >= scanEnd) break;
                    if (px < ScanLinePtr) continue;
                    color =
                        ((p0 >> x) & 1) |
                        (((p1 >> x) & 1) << 1) |
                        (((p2 >> x) & 1) << 2) |
                        (((p3 >> x) & 1) << 3);
                    if (color == 0) continue;
                    *px = palette | color;
                }
        }

        private void TraceSpriteTileFetch(int visibleLine, int bufferIndex, SpriteAttribute s, int tileBase)
        {
            if (!TraceSpriteFetch)
                return;
            if (_traceSpriteFetchCount >= TraceSpriteFetchLimit)
                return;
            if ((uint)(tileBase + 48) >= (uint)m_VRAM.Length)
                return;
            _traceSpriteFetchCount++;
            ushort p0 = m_VRAM[tileBase];
            ushort p1 = m_VRAM[tileBase + 16];
            ushort p2 = m_VRAM[tileBase + 32];
            ushort p3 = m_VRAM[tileBase + 48];
            Console.WriteLine(
                $"[PCE-SPR] line={visibleLine} idx={bufferIndex} x={s.m_X} y={s.m_Y} w={s.m_Width} h={s.m_Height} pat=0x{s.m_Pattern:X4} tile=0x{tileBase:X4} mode1={(s.m_Mode1 ? 1 : 0)} p0=0x{p0:X4} p1=0x{p1:X4} p2=0x{p2:X4} p3=0x{p3:X4}");
        }

        public unsafe void DrawBGTile(int* ScanLinePtr, ref int* px, int palette, int tile)
        {
            int word0 = m_VRAM[tile];
            int word1 = m_VRAM[tile + 8];
            int p0 = word0 & 0xFF;
            int p1 = (word0 >> 8) & 0xFF;
            int p2 = word1 & 0xFF;
            int p3 = (word1 >> 8) & 0xFF;
            int color = 0;

            for (int x = 7; x >= 0; x--, px++)
            {
                if (px < ScanLinePtr) continue;
                if ((*px & 0x1000) != 0) continue;
                color =
                    ((p0 >> x) & 1) |
                    (((p1 >> x) & 1) << 1) |
                    (((p2 >> x) & 1) << 2) |
                    (((p3 >> x) & 1) << 3);
                if (color == 0 && (*px & 0x6000) != 0) continue;
                *px = palette | color;
            }
        }

        public void WriteVDC(int address, byte data)
        {
            if (address == 0)
            {
                m_VDC_Reg = data & 0x1F;
                return;
            }
            switch (address)
            {
                case 2: // 写入寄存器低字节
                    WriteVDCRegisterLSB(m_VDC_Reg, data);
                    break;

                case 3: // 写入寄存器高字节
                    WriteVDCRegisterMSB(m_VDC_Reg, data);
                    break;
            }
        }

        private void WriteVDCRegisterLSB(int reg, byte data)
        {
            switch (reg)
            {
                case 0x00: m_VDC_MAWR = (m_VDC_MAWR & 0xFF00) | data; break;
                case 0x01: m_VDC_MARR = (m_VDC_MARR & 0xFF00) | data; break;
                case 0x02: m_VRAM[m_VDC_MAWR] = (ushort)((m_VRAM[m_VDC_MAWR] & 0xFF00) | data); break;
                case 0x05:
                    m_VDC_EnableBackground = (data & 0x80) != 0;
                    m_VDC_EnableSprites = (data & 0x40) != 0;
                    m_VDC_VBKIRQ = (data & 0x08) != 0;
                    m_VDC_RCRIRQ = (data & 0x04) != 0;
                    m_VDC_SprOvIRQ = (data & 0x02) != 0;
                    m_VDC_Spr0Col = (data & 0x01) != 0;
                    if (TraceVdcRegs)
                        Console.WriteLine($"[PCE-VDC] LSB-CR data=0x{data:X2} BG={(m_VDC_EnableBackground ? 1 : 0)} SPR={(m_VDC_EnableSprites ? 1 : 0)} VBKIRQ={(m_VDC_VBKIRQ ? 1 : 0)}");
                    break;
                case 0x06: m_VDC_RCR = (m_VDC_RCR & 0x0300) | data; break;
                case 0x07: m_VDC_BXR = (m_VDC_BXR & 0x0300) | data; break;
                case 0x08:
                    m_VDC_BYR_Offset = (m_RenderLine + 1 >= m_VDC_VDW || !m_VDC_EnableBackground) ? 0 : (m_RenderLine - 1);
                    m_VDC_BYR = (m_VDC_BYR & 0x0100) | data;
                    m_BgCounterY = m_VDC_BYR;
                    break;
                case 0x09:
                    m_VDC_MWR = (m_VDC_MWR & 0xFF00) | data;
                    switch (data & 0x30)
                    {
                        case 0x00:
                            m_VDC_BAT_Width = 32;
                            break;
                        case 0x10:
                            m_VDC_BAT_Width = 64;
                            break;
                        default:
                            m_VDC_BAT_Width = 128;
                            break;
                    }
                    //VramAccessMode = data & 0x03;
                    //SpriteAccessMode = (data >> 2) & 0x03;
                    m_VDC_BAT_Height = ((data & 0x40) == 0) ? 32 : 64;
                    //m_VDC_CgMode = (data & 0x80) != 0;
                    break;
                case 0x0A:
                    //m_VDC_HSW = data & 0x1F;
                    break;
                case 0x0B:
                    m_VDC_HDR = data & 0x7F;
                    SCREEN_WIDTH = (m_VDC_HDR + 1) * 8;
                    if (SCREEN_WIDTH == 336) SCREEN_WIDTH = 352;
                    LogVdcRegs("LSB-HDR");
                    break;
                case 0x0C:
                    m_VDC_VSR = (m_VDC_VSR & 0xFF00) | data;
                    LogVdcRegs("LSB-VSR");
                    break;
                case 0x0D: m_VDC_VDW = (m_VDC_VDW & 0x100) | data; break;
                case 0x0F:
                    m_VDC_SATBDMA_IRQ = (data & 0x01) != 0;
                    m_VDC_VRAMDMA_IRQ = (data & 0x02) != 0;
                    m_VDC_SRCDECR = (data & 0x04) != 0;
                    m_VDC_DSTDECR = (data & 0x08) != 0;
                    m_VDC_SATB_ENA = (data & 0x10) != 0;
                    break;
                case 0x10: m_VDC_DSR = (ushort)((m_VDC_DSR & 0xFF00) | data); break;
                case 0x11: m_VDC_DESR = (ushort)((m_VDC_DESR & 0xFF00) | data); break;
                case 0x12: m_VDC_LENR = (ushort)((m_VDC_LENR & 0xFF00) | data); break;
                case 0x13: m_VDC_VSAR = (ushort)((m_VDC_VSAR & 0xFF00) | data); m_DoSAT_DMA = true; break;
            }
        }

        private void WriteVDCRegisterMSB(int reg, byte data)
        {
            switch (reg)
            {
                case 0x00: m_VDC_MAWR = (m_VDC_MAWR & 0xFF) | (data << 8); break;
                case 0x01: m_VDC_MARR = (m_VDC_MARR & 0xFF) | (data << 8); break;
                case 0x02:
                    m_VRAM[m_VDC_MAWR] = (ushort)((m_VRAM[m_VDC_MAWR] & 0x00FF) | (data << 8));
                    m_VDC_MAWR = (m_VDC_MAWR + m_VDC_Increment) & 0x7FFF;
                    break;
                case 0x05:
                    switch (data & 0x18)
                    {
                        case 0x00:
                            m_VDC_Increment = 1;
                            break;
                        case 0x08:
                            m_VDC_Increment = 32;
                            break;
                        case 0x10:
                            m_VDC_Increment = 64;
                            break;
                        default:
                            m_VDC_Increment = 128;
                            break;
                    }
                    break;
                case 0x06: m_VDC_RCR = (m_VDC_RCR & 0xFF) | ((data << 8) & 0x0300); break;
                case 0x07: m_VDC_BXR = (m_VDC_BXR & 0xFF) | ((data << 8) & 0x0300); break;
                case 0x08:
                    m_VDC_BYR_Offset = (m_RenderLine + 1 >= m_VDC_VDW || !m_VDC_EnableBackground) ? 0 : (m_RenderLine - 1);
                    m_VDC_BYR = (m_VDC_BYR & 0xFF) | ((data << 8) & 0x0100);
                    m_BgCounterY = m_VDC_BYR;
                    break;
                case 0x0A:
                    //m_VDC_HDS = data & 0x7F;
                    break;
                case 0x0B:
                    //m_VDC_HDE = (data & 0x7F);
                    break;
                case 0x0C:
                    m_VDC_VSR = ((data << 8) & 0xFF00) | (m_VDC_VSR & 0x00FF);
                    LogVdcRegs("MSB-VSR");
                    break;
                case 0x0D: m_VDC_VDW = ((data << 8) & 0x100) | (m_VDC_VDW & 0xFF); break;
                case 0x10: m_VDC_DSR = (ushort)((m_VDC_DSR & 0xFF) | (data << 8)); break;
                case 0x11: m_VDC_DESR = (ushort)((m_VDC_DESR & 0xFF) | (data << 8)); break;
                case 0x12:
                    m_VDC_LENR = (ushort)((m_VDC_LENR & 0xFF) | (data << 8));
                    m_VDC_DMA_Enable = true;
                    if (TraceVdcRegs && !m_VdcDmaSuppressed)
                    {
                        if (m_VdcDmaLogCount < 50)
                        {
                            Console.WriteLine($"[PCE-VDC] VRAMDMA start DSR=0x{m_VDC_DSR:X4} DESR=0x{m_VDC_DESR:X4} LEN=0x{m_VDC_LENR:X4} line={m_RenderLine}");
                            m_VdcDmaLogCount++;
                        }
                        else
                        {
                            Console.WriteLine("[PCE-VDC] VRAMDMA logging suppressed.");
                            m_VdcDmaSuppressed = true;
                        }
                    }
                    break;
                case 0x13: m_VDC_VSAR = (ushort)((m_VDC_VSAR & 0xFF) | (data << 8)); m_DoSAT_DMA = true; break;
            }
        }

        public byte ReadVDC(int address)
        {
            switch (address)
            {
                case 0:
                    byte status = (byte)(
                        (m_VDC_BSY ? 0x40 : 0) |
                        (m_VDC_VD ? 0x20 : 0) |
                        (m_VDC_DV ? 0x10 : 0) |
                        (m_VDC_DS ? 0x08 : 0) |
                        (m_VDC_RR ? 0x04 : 0) |
                        (m_VDC_OR ? 0x02 : 0) |
                        (m_VDC_CR ? 0x01 : 0));
                    if (TraceVdcRegs && !m_VdcStatusSuppressed)
                    {
                        if (m_VdcStatusLogCount < 50)
                        {
                            Console.WriteLine($"[PCE-VDC] STATUS line={m_RenderLine} status=0x{status:X2} VSR=0x{m_VDC_VSR:X4} VDW={m_VDC_VDW} RCR={m_VDC_RCR}");
                            m_VdcStatusLogCount++;
                        }
                        else
                        {
                            Console.WriteLine("[PCE-VDC] STATUS logging suppressed.");
                            m_VdcStatusSuppressed = true;
                        }
                    }
                    m_VDC_VD = false;
                    m_VDC_DV = false;
                    m_VDC_DS = false;
                    m_VDC_RR = false;
                    m_VDC_OR = false;
                    m_VDC_CR = false;
                    m_WaitingIRQ = false;
                    return status;

                case 2: return (byte)m_VRAM[m_VDC_MARR];
                case 3:
                    byte data = (byte)(m_VRAM[m_VDC_MARR] >> 8);
                    if (m_VDC_Reg == 2)
                        m_VDC_MARR = (m_VDC_MARR + m_VDC_Increment) & 0x7FFF;
                    return data;
            }
            return 0;
        }

        public void WriteVCE(int address, byte data)
        {
            switch (address)
            {
                case 0:
                    //ScanlineCount = (data & 0x04) != 0 ? 263 : 262;
                    //Grayscale = (data & 0x80) != 0;
                    // 设置时钟频率
                    switch (data & 3)
                    {
                        case 0:
                            m_VCE_DotClock = DotClock.MHZ_5;
                            break;
                        case 1:
                            m_VCE_DotClock = DotClock.MHZ_7;
                            break;
                        default:
                            m_VCE_DotClock = DotClock.MHZ_10;
                            break;
                    }
                    break;

                case 2:
                    // 设置调色板索引低字节
                    m_VCE_Index = (m_VCE_Index & 0x100) | (data & 0xFF);
                    break;

                case 3:
                    // 设置调色板索引高字节
                    m_VCE_Index = (m_VCE_Index & 0xFF) | ((data & 0x01) << 8);
                    break;

                case 4:
                    // 写入调色板数据低字节
                    m_VCE[m_VCE_Index] = (ushort)((m_VCE[m_VCE_Index] & 0xFF00) | data);
                    break;

                case 5:
                    // 写入调色板数据高字节
                    m_VCE[m_VCE_Index] = (ushort)((m_VCE[m_VCE_Index] & 0xFF) | ((data << 8) & 0x100));
                    m_VCE_Index = (m_VCE_Index + 1) & 0x1FF;
                    break;
            }
        }

        public byte ReadVCE(int address)
        {
            switch (address)
            {
                case 4:
                    return (byte)m_VCE[m_VCE_Index];
                case 5:
                    {
                        byte data = (byte)((m_VCE[m_VCE_Index] >> 8) | 0xFE);
                        m_VCE_Index = (m_VCE_Index + 1) & 0x1FF;
                        return data;
                    }
                default:
                    return 0xFF;
            }
        }

        public bool IRQPending()
        {
            bool wait = m_WaitingIRQ;
            m_WaitingIRQ = false;
            return wait;
        }
    }
}
