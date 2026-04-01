using System;
using System.IO;
using System.Runtime.CompilerServices;
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
        private static readonly bool TraceSpriteFetch =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_FETCH_TRACE"), "1", StringComparison.Ordinal);
        private static readonly bool SpriteDrawForward =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_DRAW_FORWARD"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSpriteLine =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_LINE_TRACE"), "1", StringComparison.Ordinal);
        private static readonly bool TracePixelLine =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_PIXEL_TRACE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VRAM_WRITE_TRACE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVceWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VCE_WRITE_TRACE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSatFrames =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SAT_FRAME_TRACE"), "1", StringComparison.Ordinal);
        private static readonly bool ForceSatFromVsar =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_FORCE_SAT_FROM_VSAR"), "1", StringComparison.Ordinal);
        private static readonly int TraceSpriteLineOnly =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_LINE_TRACE_LINE"), out int slLine) ? slLine : -1;
        private static readonly int TraceSpriteLineMin =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_LINE_TRACE_MIN"), out int slMin) ? slMin : -1;
        private static readonly int TraceSpriteLineMax =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_LINE_TRACE_MAX"), out int slMax) ? slMax : -1;
        private static readonly int TracePixelLineOnly =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_PIXEL_TRACE_LINE"), out int pxLine) ? pxLine : -1;
        private static readonly int TracePixelLineMin =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_PIXEL_TRACE_MIN"), out int pxMin) ? pxMin : -1;
        private static readonly int TracePixelLineMax =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_PIXEL_TRACE_MAX"), out int pxMax) ? pxMax : -1;
        private static readonly int TracePixelXMin =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_PIXEL_TRACE_XMIN"), out int pxXMin) ? pxXMin : 0;
        private static readonly int TracePixelXMax =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_PIXEL_TRACE_XMAX"), out int pxXMax) ? pxXMax : 255;
        private static readonly int TraceSpriteFetchLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_FETCH_TRACE_LIMIT"), out int sfLim) && sfLim > 0 ? sfLim : 4000;
        private static readonly int TraceSpriteFetchLineOnly =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_FETCH_TRACE_LINE"), out int sfLine) ? sfLine : -1;
        private static readonly int TraceSpriteFetchLineMin =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_FETCH_TRACE_MIN"), out int sfMin) ? sfMin : -1;
        private static readonly int TraceSpriteFetchLineMax =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_FETCH_TRACE_MAX"), out int sfMax) ? sfMax : -1;
        private static readonly int TraceSpriteLineLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_LINE_TRACE_LIMIT"), out int slLim) && slLim > 0 ? slLim : 4000;
        private static readonly int TracePixelLineLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_PIXEL_TRACE_LIMIT"), out int plLim) && plLim > 0 ? plLim : 4000;
        private static readonly int TraceVramWriteLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VRAM_WRITE_TRACE_LIMIT"), out int vwLim) && vwLim > 0 ? vwLim : 4000;
        private static readonly int TraceVceWriteLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VCE_WRITE_TRACE_LIMIT"), out int vcwLim) && vcwLim > 0 ? vcwLim : 4000;
        private static readonly int TraceVramWriteMin =
            ParseOptionalHexEnv("EUTHERDRIVE_PCE_VRAM_WRITE_MIN", -1);
        private static readonly int TraceVramWriteMax =
            ParseOptionalHexEnv("EUTHERDRIVE_PCE_VRAM_WRITE_MAX", -1);
        private static readonly int TraceSatFrameLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SAT_FRAME_TRACE_LIMIT"), out int satFrameLim) && satFrameLim > 0 ? satFrameLim : 120;
        private static readonly int TraceSpriteSatOnly =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_TRACE_SAT"), out int satOnly) ? satOnly : -1;
        private static readonly string? TraceSpriteFile =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_TRACE_FILE");
        private static readonly bool TraceSpriteStdout =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SPR_TRACE_STDOUT"), "1", StringComparison.Ordinal);
        private static readonly object TraceSpriteFileLock = new object();
        private sealed class TransientState
        {
            public ushort VdcVwr;
            public int SatTransferNextWord;
            public int SatTransferWordsRemaining;
            public ushort VramTransferSrc;
            public ushort VramTransferDest;
            public int VramTransferWordsRemaining;
        }
        private static readonly ConditionalWeakTable<PPU, TransientState> TransientStates = new();
        private static readonly int TraceVdcRegsLimit = 200;
        [NonSerialized]
        private int _traceVdcRegsCount;
        [NonSerialized]
        private int _traceSpriteFetchCount;
        [NonSerialized]
        private int _traceSpriteLineCount;
        [NonSerialized]
        private int _tracePixelLineCount;
        [NonSerialized]
        private int _traceVramWriteCount;
        [NonSerialized]
        private int _traceVceWriteCount;
        [NonSerialized]
        private int _traceSatFrameCount;

        private TransientState Transient => TransientStates.GetOrCreateValue(this);

        private static void WriteSpriteTrace(string line)
        {
            bool wroteFile = false;
            if (!string.IsNullOrWhiteSpace(TraceSpriteFile))
            {
                lock (TraceSpriteFileLock)
                {
                    File.AppendAllText(TraceSpriteFile!, line + Environment.NewLine);
                }
                wroteFile = true;
            }

            if (!wroteFile || TraceSpriteStdout)
                Console.WriteLine(line);
        }

        private static bool TraceLineSelected(int line, int exact, int min, int max)
        {
            if (exact >= 0)
                return line == exact;
            if (min >= 0 && line < min)
                return false;
            if (max >= 0 && line > max)
                return false;
            return true;
        }

        private static int ParseOptionalHexEnv(string name, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            value = value.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(2);

            return int.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }

        private void TraceVramWriteIfNeeded(int address, ushort value)
        {
            if (!TraceVramWrites || _traceVramWriteCount >= TraceVramWriteLimit)
                return;
            if (TraceVramWriteMin >= 0 && address < TraceVramWriteMin)
                return;
            if (TraceVramWriteMax >= 0 && address > TraceVramWriteMax)
                return;

            int vds = Math.Max(14, m_LatchedVDS);
            int visibleLine = m_DisplayCounter - vds;
            bool inDisplay = visibleLine >= 0 && visibleLine < m_LatchedVDW;
            _traceVramWriteCount++;
            WriteSpriteTrace($"[PCE-VRAMW] frame={m_FrameCounter} render={m_RenderLine} dispctr={m_DisplayCounter} vis={visibleLine} in_display={(inDisplay ? 1 : 0)} mawr=0x{address:X4} value=0x{value:X4} reg=0x{m_VDC_Reg:X2}");
        }

        private void TraceVceWriteIfNeeded(int address, int index, ushort value, byte data)
        {
            if (!TraceVceWrites || _traceVceWriteCount >= TraceVceWriteLimit)
                return;

            int vds = Math.Max(14, m_LatchedVDS);
            int visibleLine = m_DisplayCounter - vds;
            bool inDisplay = visibleLine >= 0 && visibleLine < m_LatchedVDW;
            _traceVceWriteCount++;
            WriteSpriteTrace($"[PCE-VCEW] frame={m_FrameCounter} render={m_RenderLine} dispctr={m_DisplayCounter} vis={visibleLine} in_display={(inDisplay ? 1 : 0)} addr=0x{address:X1} index=0x{index:X3} data=0x{data:X2} value=0x{value:X4}");
        }

        private void TraceSatFrameIfNeeded()
        {
            if (!TraceSatFrames || _traceSatFrameCount >= TraceSatFrameLimit)
                return;

            _traceSatFrameCount++;
            int spriteFetchMode = (m_LatchedMWR >> 2) & 0x03;
            WriteSpriteTrace($"[PCE-SATFRAME] frame={m_FrameCounter} vsar=0x{m_VDC_VSAR:X4} mwr=0x{m_LatchedMWR:X2} fetchMode={spriteFetchMode}");

            for (int i = 0; i < 64; i++)
            {
                int baseIndex = i << 2;
                ushort sat0 = m_SatRaw[baseIndex + 0];
                ushort sat1 = m_SatRaw[baseIndex + 1];
                ushort sat2 = m_SatRaw[baseIndex + 2];
                ushort sat3 = m_SatRaw[baseIndex + 3];
                if (sat0 == 0 && sat1 == 0 && sat2 == 0 && sat3 == 0)
                    continue;

                int cgy = (sat3 >> 12) & 0x03;
                int cgx = (sat3 >> 8) & 0x01;
                int widthPx = cgx == 0 ? 16 : 32;
                int height = cgy == 0 ? 16 : (cgy == 1 ? 32 : 64);
                int pattern = (sat2 >> 1) & 0x03FF;
                if (widthPx == 32)
                    pattern &= 0xFFFE;
                switch (cgy)
                {
                    case 1:
                        pattern &= 0xFFFD;
                        break;
                    case 2:
                    case 3:
                        pattern &= 0xFFF9;
                        break;
                }

                int spriteAddress = pattern << 6;
                string chunk0;
                string chunk1 = "";
                string chunk2 = "";
                string chunk3 = "";
                if (widthPx == 32)
                {
                    chunk0 = $"0x{spriteAddress:X4}";
                    chunk1 = $"0x{(spriteAddress + 0x40):X4}";
                    if (height >= 32)
                    {
                        chunk2 = $"0x{(spriteAddress + 0x80):X4}";
                        chunk3 = $"0x{(spriteAddress + 0xC0):X4}";
                    }
                }
                else
                {
                    chunk0 = $"0x{spriteAddress:X4}";
                    if (height >= 32)
                        chunk1 = $"0x{(spriteAddress + 0x40):X4}";
                    if (height >= 64)
                    {
                        chunk2 = $"0x{(spriteAddress + 0x80):X4}";
                        chunk3 = $"0x{(spriteAddress + 0xC0):X4}";
                    }
                }

                WriteSpriteTrace(
                    $"[PCE-SATFRAME] frame={m_FrameCounter} sat={i:D2} x={(sat1 & 0x3FF) - 32} y={(sat0 & 0x3FF) - 64} w={widthPx} h={height} pat=0x{spriteAddress:X4} sat2=0x{sat2:X4} sat3=0x{sat3:X4} chunks={chunk0},{chunk1},{chunk2},{chunk3}");
            }
        }

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

        private struct BufferedSpriteEntry
        {
            public int SatIndex;
            public int X;
            public int Flags;
            public int Palette;
            public ushort Plane0;
            public ushort Plane1;
            public ushort Plane2;
            public ushort Plane3;
        }
        private SpriteAttribute[] m_SAT;
        private ushort[] m_SatRaw;
        private int m_RenderLine;
        private int m_FrameCounter;
        [NonSerialized]
        private BufferedSpriteEntry[] _currentLineSprites = new BufferedSpriteEntry[16];
        [NonSerialized]
        private int _currentLineSpriteCount;
        [NonSerialized]
        private int _currentLineSpriteVisible = -1;
        [NonSerialized]
        private BufferedSpriteEntry[] _nextLineSprites = new BufferedSpriteEntry[16];
        [NonSerialized]
        private int _nextLineSpriteCount;
        [NonSerialized]
        private int _nextLineSpriteVisible = -1;
        public int CYCLES_PER_LINE = 1365;
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
        private bool m_TriggerSAT_DMA;
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

        private ushort ReadVramWord(int address)
        {
            if ((uint)address < 0x8000u)
                return m_VRAM[address];

            return 0;
        }

        private void LoadSpritePlanes(int lineStart, bool mode1, out ushort plane0, out ushort plane1, out ushort plane2, out ushort plane3)
        {
            plane0 = ReadVramWord(lineStart + 0);
            plane1 = ReadVramWord(lineStart + 16);
            plane2 = mode1 ? (ushort)0 : ReadVramWord(lineStart + 32);
            plane3 = mode1 ? (ushort)0 : ReadVramWord(lineStart + 48);
        }

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
            Console.WriteLine($"[PCE-VDC] {tag} reg=0x{m_VDC_Reg:X2} HDS={m_VDC_HDS} HDE={m_VDC_HDE} HDW={m_VDC_HDW} VSR=0x{m_VDC_VSR:X4} VDW={m_VDC_VDW} line={m_RenderLine}");
        }

        private int GetEffectiveVds(int vdw)
        {
            int vsw = m_VDC_VSR & 0x1F;
            int vds = (m_VDC_VSR >> 8) & 0xFF;
            int start = vds + vsw;
            if (start < 0) start = 0;
            if (start > 261) start = 261;
            int maxStart = 262 - vdw;
            if (start > maxStart)
                start = maxStart;
            if (start < 0)
                start = 0;
            return start;
        }

        public int m_VDC_HDS;
        public int m_VDC_HDE;
        public int m_VDC_HDW;
        public int m_VDC_VDW;
        private int m_VDC_VCR;
        private int m_LatchedVDS;
        private int m_LatchedVDW;
        [NonSerialized]
        private int m_LatchedHDS;
        [NonSerialized]
        private int m_LatchedHDE;
        [NonSerialized]
        private int m_LatchedHDW;
        [NonSerialized]
        private int m_LatchedScreenWidth;
        private int m_DisplayCounter;

        private int m_VDC_BAT_Width;
        private int m_VDC_BAT_Height;
        private int m_LatchedBxr;
        private int m_BgCounterY;
        private int m_BgOffsetY;
        private int[] m_BxrByLine = new int[262];
        private int[] m_ByrByLine = new int[262];
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
        [NonSerialized]
        private int m_VdcStatusLogCount;
        [NonSerialized]
        private bool m_VdcStatusSuppressed;
        [NonSerialized]
        private int m_VdcDmaLogCount;
        [NonSerialized]
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
            m_SatRaw = new ushort[0x100];
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
            m_FrameCounter = 0;
            m_DoSAT_DMA = false;
            m_WaitingIRQ = false;

            m_VCE_DotClock = DotClock.MHZ_5;
            m_VDC_Increment = 1;

            m_VDC_BSY = false;
            m_LatchedVDS = 14;
            m_LatchedVDW = 240;
            m_LatchedHDS = 2;
            m_LatchedHDE = 4;
            m_LatchedHDW = 31;
            m_LatchedScreenWidth = 256;
            m_VDC_VCR = 0x0C;
            m_DisplayCounter = 0;
            m_LatchedMWR = 0;
        }

        public PPU()
        {
            host = null!;

            m_VRAM = new ushort[0x10000];
            m_SAT = new SpriteAttribute[0x40];
            m_SatRaw = new ushort[0x100];
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
            m_FrameCounter = 0;
            m_DoSAT_DMA = false;
            m_WaitingIRQ = false;

            m_VCE_DotClock = DotClock.MHZ_5;
            m_VDC_Increment = 1;

            m_VDC_BSY = false;
            m_LatchedVDS = 14;
            m_LatchedVDW = 240;
            m_LatchedHDS = 2;
            m_LatchedHDE = 4;
            m_LatchedHDW = 31;
            m_LatchedScreenWidth = 256;
            m_VDC_VCR = 0x0C;
            m_DisplayCounter = 0;
            m_LatchedMWR = 0;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_screenBufPtr);
        }

        public unsafe void Reset()
        {
            Array.Clear(m_VRAM);
            Array.Clear(m_SatRaw);

                for (var i = 0; i < 64; i++)
                {
                    m_SAT[i].m_X = 0;
                    m_SAT[i].m_Y = 0;
                }

            m_WaitingIRQ = false;
            m_BgCounterY = 0;
            m_BgOffsetY = 0;
            m_LatchedBxr = 0;
            m_FrameCounter = 0;
            m_DisplayCounter = 0;
            m_DoSAT_DMA = false;
            m_TriggerSAT_DMA = false;
            Transient.SatTransferNextWord = 0;
            Transient.SatTransferWordsRemaining = 0;
            Transient.VramTransferSrc = 0;
            Transient.VramTransferDest = 0;
            Transient.VramTransferWordsRemaining = 0;
            _currentLineSpriteCount = 0;
            _currentLineSpriteVisible = -1;
            _nextLineSpriteCount = 0;
            _nextLineSpriteVisible = -1;
        }

        private void BuildSpriteLineBuffer(int visibleLine)
        {
            _nextLineSpriteCount = 0;
            _nextLineSpriteVisible = visibleLine;
            int spriteFetchMode = (m_LatchedMWR >> 2) & 0x03;
            bool mode1 = spriteFetchMode == 1;
            int eligibleEntryCount = 0;
            int firstDroppedSat = -1;
            int firstDroppedX = 0;

            for (int i = 0; i < 64; i++)
            {
                int spriteOffset = i << 2;
                ushort sat0 = m_SatRaw[spriteOffset + 0];
                ushort sat1 = m_SatRaw[spriteOffset + 1];
                ushort sat2 = m_SatRaw[spriteOffset + 2];
                ushort sat3 = m_SatRaw[spriteOffset + 3];
                int spriteY = (sat0 & 0x3FF) - 64;
                int flags = sat3;
                int cgy = (flags >> 12) & 0x03;
                int height = cgy == 0 ? 16 : (cgy == 1 ? 32 : 64);
                if (visibleLine < spriteY || visibleLine >= spriteY + height)
                    continue;

                int y = visibleLine - spriteY;
                if (y >= height)
                    continue;

                if (_nextLineSpriteCount >= 16)
                {
                    if (m_VDC_SprOvIRQ)
                    {
                        m_VDC_OR = true;
                        m_WaitingIRQ = true;
                    }
                    break;
                }

                int cgx = (flags >> 8) & 0x01;
                int widthPx = cgx == 0 ? 16 : 32;
                int spriteX = sat1 & 0x3FF;
                eligibleEntryCount += widthPx == 32 ? 2 : 1;
                int pattern = (sat2 >> 1) & 0x03FF;
                if (widthPx == 32)
                    pattern &= 0xFFFE;
                switch (cgy)
                {
                    case 1:
                        pattern &= 0xFFFD;
                        break;
                    case 2:
                    case 3:
                        pattern &= 0xFFF9;
                        break;
                }

                if ((flags & 0x8000) != 0)
                    y = height - 1 - y;

                int tileY = y >> 4;
                int tileLineOffset = tileY * 128;
                int offsetY = y & 0x0F;
                int spriteAddress = pattern << 6;
                int mode1Offset = mode1 ? ((sat2 & 1) << 5) : 0;
                if (widthPx == 16)
                {
                    int lineStart = spriteAddress + tileLineOffset + offsetY + mode1Offset;
                    LoadSpritePlanes(lineStart, mode1, out ushort plane0, out ushort plane1, out ushort plane2, out ushort plane3);
                    _nextLineSprites[_nextLineSpriteCount++] = new BufferedSpriteEntry
                    {
                        SatIndex = i,
                        X = spriteX - 0x20,
                        Flags = flags,
                        Palette = ((sat3 & 0xF) << 4) | ((i == 0) ? 0x4100 : 0x2100),
                        Plane0 = plane0,
                        Plane1 = plane1,
                        Plane2 = plane2,
                        Plane3 = plane3,
                    };
                }
                else
                {
                    int lineStart = spriteAddress + tileLineOffset + offsetY + mode1Offset;
                    int leftLine = lineStart + (((flags & 0x0800) != 0) ? 64 : 0);
                    LoadSpritePlanes(leftLine, mode1, out ushort leftPlane0, out ushort leftPlane1, out ushort leftPlane2, out ushort leftPlane3);
                    _nextLineSprites[_nextLineSpriteCount++] = new BufferedSpriteEntry
                    {
                        SatIndex = i,
                        X = spriteX - 0x20,
                        Flags = flags,
                        Palette = ((sat3 & 0xF) << 4) | ((i == 0) ? 0x4100 : 0x2100),
                        Plane0 = leftPlane0,
                        Plane1 = leftPlane1,
                        Plane2 = leftPlane2,
                        Plane3 = leftPlane3,
                    };
                    if (_nextLineSpriteCount >= 16)
                    {
                        if (firstDroppedSat < 0)
                        {
                            firstDroppedSat = i;
                            firstDroppedX = spriteX - 0x20 + 16;
                        }
                        if (m_VDC_SprOvIRQ)
                        {
                            m_VDC_OR = true;
                            m_WaitingIRQ = true;
                        }
                        break;
                    }
                    int rightLine = lineStart + (((flags & 0x0800) != 0) ? 0 : 64);
                    LoadSpritePlanes(rightLine, mode1, out ushort rightPlane0, out ushort rightPlane1, out ushort rightPlane2, out ushort rightPlane3);
                    _nextLineSprites[_nextLineSpriteCount++] = new BufferedSpriteEntry
                    {
                        SatIndex = i,
                        X = spriteX - 0x20 + 16,
                        Flags = flags,
                        Palette = ((sat3 & 0xF) << 4) | ((i == 0) ? 0x4100 : 0x2100),
                        Plane0 = rightPlane0,
                        Plane1 = rightPlane1,
                        Plane2 = rightPlane2,
                        Plane3 = rightPlane3,
                    };
                }
            }

            if (TraceSpriteLine && _traceSpriteLineCount < TraceSpriteLineLimit &&
                TraceLineSelected(visibleLine, TraceSpriteLineOnly, TraceSpriteLineMin, TraceSpriteLineMax))
            {
                _traceSpriteLineCount++;
                WriteSpriteTrace($"[PCE-SPRLINE] frame={m_FrameCounter} render={m_RenderLine} line={visibleLine} count={_nextLineSpriteCount} eligible={eligibleEntryCount} dropped_sat={firstDroppedSat} dropped_x={firstDroppedX} dot={(int)m_VCE_DotClock} hdw={m_VDC_HDW} mwr=0x{m_VDC_MWR:X2}");
                for (int si = 0; si < _nextLineSpriteCount; si++)
                {
                    var entry = _nextLineSprites[si];
                    if (TraceSpriteSatOnly >= 0 && entry.SatIndex != TraceSpriteSatOnly)
                        continue;
                    WriteSpriteTrace(
                        $"[PCE-SPRLINE] frame={m_FrameCounter} render={m_RenderLine} line={visibleLine} sat={entry.SatIndex:D2} x={entry.X} flags=0x{entry.Flags:X4} pal=0x{entry.Palette:X4} p0=0x{entry.Plane0:X4} p1=0x{entry.Plane1:X4} p2=0x{entry.Plane2:X4} p3=0x{entry.Plane3:X4}");
                }
            }
        }

        public unsafe void tick()
        {
            if (m_RenderLine == 0)
            {
                if (ForceSatFromVsar)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        int baseIndex = i << 2;
                        int g = (m_VDC_VSAR + baseIndex) & 0x7FFF;
                        m_SatRaw[baseIndex + 0] = ReadVramWord(g + 0);
                        m_SatRaw[baseIndex + 1] = ReadVramWord(g + 1);
                        m_SatRaw[baseIndex + 2] = ReadVramWord(g + 2);
                        m_SatRaw[baseIndex + 3] = ReadVramWord(g + 3);
                    }
                }
                m_LatchedVDS = GetEffectiveVds(GetEffectiveVdw());
                m_LatchedVDW = GetEffectiveVdw();
                m_LatchedHDS = m_VDC_HDS;
                m_LatchedHDE = m_VDC_HDE;
                m_LatchedHDW = m_VDC_HDW;
                m_LatchedScreenWidth = (m_LatchedHDW + 1) * 8;
                m_LatchedMWR = m_VDC_MWR;
                m_DisplayCounter = 0;
                m_LatchedBxr = m_VDC_BXR & 0x3FF;
                m_BgCounterY = m_VDC_BYR & 0x1FF;
                m_BgOffsetY = m_BgCounterY;
                _currentLineSpriteCount = 0;
                _currentLineSpriteVisible = -1;
                _nextLineSpriteCount = 0;
                _nextLineSpriteVisible = -1;
                TraceSatFrameIfNeeded();
            }
            int vdw = m_LatchedVDW;
            int vds = Math.Max(14, m_LatchedVDS);
            int visibleLine = m_DisplayCounter - vds;
            bool inDisplay = visibleLine >= 0 && visibleLine < vdw;
            if (!inDisplay)
            {
                HandleDMA();
                if (visibleLine + 1 == 0 && _nextLineSpriteVisible != 0)
                    BuildSpriteLineBuffer(0);
            }
            else
            {
                if (_nextLineSpriteVisible == visibleLine)
                {
                    var swapSprites = _currentLineSprites;
                    _currentLineSprites = _nextLineSprites;
                    _nextLineSprites = swapSprites;
                    _currentLineSpriteCount = _nextLineSpriteCount;
                    _currentLineSpriteVisible = _nextLineSpriteVisible;
                    _nextLineSpriteCount = 0;
                    _nextLineSpriteVisible = -1;
                }
                else if (_currentLineSpriteVisible != visibleLine)
                {
                    _currentLineSpriteCount = 0;
                    _currentLineSpriteVisible = visibleLine;
                }

                DrawScanLine(visibleLine);

                if (visibleLine + 1 < vdw)
                    BuildSpriteLineBuffer(visibleLine + 1);
            }

            m_RenderLine++;
            m_DisplayCounter++;
            int displayReset = vds + vdw + 3 + (m_VDC_VCR & 0xFF);
            if (m_DisplayCounter >= displayReset)
                m_DisplayCounter = 0;

            if (visibleLine + 1 == vdw)
            {
                if (m_TriggerSAT_DMA || m_VDC_SATB_ENA)
                {
                    m_DoSAT_DMA = true;
                    m_TriggerSAT_DMA = false;
                    Transient.SatTransferNextWord = 0;
                    Transient.SatTransferWordsRemaining = 0x100;
                }
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
                int rasterLine = visibleLine;
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
                m_FrameCounter++;
                _currentLineSpriteCount = 0;
                _currentLineSpriteVisible = -1;
                _nextLineSpriteCount = 0;
                _nextLineSpriteVisible = -1;
                //ConvertColor();
                Marshal.Copy(_screenBufPtr, _screenBuf, 0, _screenBuf.Length);
                FrameReady = true;
                host.RenderFrame(_screenBuf, m_LatchedScreenWidth, vdw);
            }
        }

        private unsafe void HandleDMA()
        {
            int DmaCycles = CYCLES_PER_LINE / (int)m_VCE_DotClock;
            if (m_DoSAT_DMA)
            {
                int transferWords = Math.Min(DmaCycles / 4, Transient.SatTransferWordsRemaining);
                for (int word = 0; word < transferWords; word++)
                {
                    int satWordIndex = Transient.SatTransferNextWord++;
                    int source = (m_VDC_VSAR + satWordIndex) & 0x7FFF;
                    m_SatRaw[satWordIndex] = ReadVramWord(source);
                    Transient.SatTransferWordsRemaining--;
                }
                DmaCycles -= transferWords * 4;

                if (Transient.SatTransferWordsRemaining <= 0)
                {
                    if (m_VDC_SATBDMA_IRQ)
                    {
                        m_VDC_DS = true;
                        m_WaitingIRQ = true;
                    }
                    m_DoSAT_DMA = false;
                }
            }

            if (m_VDC_DMA_Enable)
            {
                int transferWords = Math.Min(DmaCycles / 4, Transient.VramTransferWordsRemaining);
                for (int word = 0; word < transferWords; word++)
                {
                    int dest = Transient.VramTransferDest;
                    ushort value = ReadVramWord(Transient.VramTransferSrc);
                    if ((uint)dest < 0x8000u)
                    {
                        m_VRAM[dest] = value;
                        TraceVramWriteIfNeeded(dest, value);
                    }

                    Transient.VramTransferSrc = (ushort)(Transient.VramTransferSrc + (m_VDC_SRCDECR ? -1 : 1));
                    Transient.VramTransferDest = (ushort)(Transient.VramTransferDest + (m_VDC_DSTDECR ? -1 : 1));
                    Transient.VramTransferWordsRemaining--;
                }

                DmaCycles -= transferWords * 4;

                if (Transient.VramTransferWordsRemaining <= 0)
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
                int width = (cgx == 0) ? 1 : 2;
                int height = (cgy == 0) ? 16 : (cgy == 1 ? 32 : 64);
                int pattern = (sat2 >> 1) & 0x03FF;
                if (width == 2) pattern &= 0xFFFE;
                switch (cgy)
                {
                    case 1: pattern &= 0xFFFD; break;
                    case 2:
                    case 3: pattern &= 0xFFF9; break;
                }
                int baseIndex = i << 2;
                m_SatRaw[baseIndex + 0] = (ushort)sat0;
                m_SatRaw[baseIndex + 1] = (ushort)sat1;
                m_SatRaw[baseIndex + 2] = (ushort)sat2;
                m_SatRaw[baseIndex + 3] = (ushort)sat3;
            }
        }

        public void AfterStateLoad()
        {
            // Keep serialized scanline/DMA timing as-is.
            // Forcing line 0 + SAT DMA here made HuCard savestates resume at the wrong point.
            FrameReady = false;
            m_LatchedHDS = m_VDC_HDS;
            m_LatchedHDE = m_VDC_HDE;
            m_LatchedHDW = m_VDC_HDW;
            m_LatchedScreenWidth = (m_VDC_HDW + 1) * 8;
            _currentLineSpriteCount = 0;
            _currentLineSpriteVisible = -1;
            _nextLineSpriteCount = 0;
            _nextLineSpriteVisible = -1;
            Transient.SatTransferNextWord = 0;
            Transient.SatTransferWordsRemaining = 0;
            Transient.VramTransferSrc = 0;
            Transient.VramTransferDest = 0;
            Transient.VramTransferWordsRemaining = 0;
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
                writer.WriteLine($"reg_hds=0x{m_VDC_HDS:X4}");
                writer.WriteLine($"reg_hde=0x{m_VDC_HDE:X4}");
                writer.WriteLine($"reg_hdw=0x{m_VDC_HDW:X4}");
                writer.WriteLine($"reg_bxr=0x{m_VDC_BXR:X4}");
                writer.WriteLine($"reg_byr=0x{m_VDC_BYR:X4}");
                writer.WriteLine($"reg_rcr=0x{m_VDC_RCR:X4}");
                writer.WriteLine($"reg_mawr=0x{m_VDC_MAWR:X4}");
                writer.WriteLine($"reg_marr=0x{m_VDC_MARR:X4}");
                writer.WriteLine($"reg_vsar=0x{m_VDC_VSAR:X4}");
                writer.WriteLine("spr_pattern_align=1");
                writer.WriteLine($"enable_bg={m_VDC_EnableBackground}");
                writer.WriteLine($"enable_spr={m_VDC_EnableSprites}");
                writer.WriteLine($"do_sat_dma={m_DoSAT_DMA}");
                writer.WriteLine($"waiting_irq={m_WaitingIRQ}");
            }

            using (var writer = new StreamWriter(Path.Combine(directory, $"{prefix}_sprites.txt")))
            {
                writer.WriteLine("idx x y pattern cgsel hflip vflip w h prio pal sat0 sat1 sat2 sat3");
                int spriteFetchMode = (m_LatchedMWR >> 2) & 0x03;
                for (int i = 0; i < 64; i++)
                {
                    int baseIndex = i << 2;
                    ushort sat0 = m_SatRaw[baseIndex + 0];
                    ushort sat1 = m_SatRaw[baseIndex + 1];
                    ushort sat2 = m_SatRaw[baseIndex + 2];
                    ushort sat3 = m_SatRaw[baseIndex + 3];
                    int cgy = (sat3 >> 12) & 0x03;
                    int cgx = (sat3 >> 8) & 0x01;
                    int width = cgx == 0 ? 1 : 2;
                    int height = cgy == 0 ? 16 : (cgy == 1 ? 32 : 64);
                    int pattern = (sat2 >> 1) & 0x03FF;
                    if (width == 2) pattern &= 0xFFFE;
                    switch (cgy)
                    {
                        case 1: pattern &= 0xFFFD; break;
                        case 2:
                        case 3: pattern &= 0xFFF9; break;
                    }
                    writer.WriteLine(
                        $"{i:D2} {(sat1 & 0x3FF) - 32:D4} {(sat0 & 0x3FF) - 64:D4} 0x{(pattern << 6):X4} {((spriteFetchMode >= 2 && (sat2 & 1) != 0) ? 1 : 0):D2} {(((sat3 & 0x0800) != 0) ? 1 : 0)} {(((sat3 & 0x8000) != 0) ? 1 : 0)} {width:D1} {height:D1} {(((sat3 & 0x80) != 0) ? 1 : 0)} {((sat3 & 0xF) << 4):D2} 0x{sat0:X4} 0x{sat1:X4} 0x{sat2:X4} 0x{sat3:X4}");
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
            sb.Append(" hds=").Append(m_VDC_HDS.ToString("X4"));
            sb.Append(" hde=").Append(m_VDC_HDE.ToString("X4"));
            sb.Append(" hdw=").Append(m_VDC_HDW.ToString("X4"));
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
            for (int i = 0; i < m_SatRaw.Length; i++)
                h = Fnv1a64(h, m_SatRaw[i]);
            return h;
        }

        private unsafe void DrawScanLine(int visibleLine)
        {
            int i;
            int screenWidth = m_LatchedScreenWidth > 0 ? m_LatchedScreenWidth : SCREEN_WIDTH;
            m_LatchedEnableBackground = m_VDC_EnableBackground;
            m_LatchedEnableSprites = m_VDC_EnableSprites;
            int* ScanLinePtr = (int*)_screenBufPtr.ToPointer() + screenWidth * visibleLine;
            Span<int> spriteLine = stackalloc int[512];
            Span<int> spritePriorityLine = stackalloc int[512];
            Span<int> spriteOwnerLine = stackalloc int[512];
            Span<int> bgLine = stackalloc int[512];

            for (i = 0; i < screenWidth; i++)
            {
                ScanLinePtr[i] = 0x000;
                spriteLine[i] = 0;
                spritePriorityLine[i] = 0;
                spriteOwnerLine[i] = -1;
                bgLine[i] = 0;
            }

            if (m_LatchedEnableBackground)
            {
                if (visibleLine == 0)
                    m_BgCounterY = m_VDC_BYR & 0x1FF;
                else
                    m_BgCounterY = (m_BgCounterY + 1) & 0x1FF;
                m_BgOffsetY = m_BgCounterY;
                m_LatchedBxr = m_VDC_BXR & 0x3FF;
                int screenReg = (m_LatchedMWR >> 4) & 0x07;
                bool bgCgMode = (m_LatchedMWR & 0x03) == 0x03 && (m_LatchedMWR & 0x80) != 0;
                int screenSizeX = ScreenSizeX[screenReg];
                int bgY = m_BgOffsetY & ScreenSizeYPixelsMask[screenReg];
                int tileY = bgY & 7;
                int batOffset = (bgY >> 3) * screenSizeX;
                int prevTileCol = -1;
                int palette = 0;
                int byte1 = 0, byte2 = 0, byte3 = 0, byte4 = 0;

                for (i = 0; i < screenWidth; i++)
                {
                    int bgX = (m_LatchedBxr + i) & ScreenSizeXPixelsMask[screenReg];
                    int tileCol = bgX >> 3;
                    if (tileCol != prevTileCol)
                    {
                        int batEntry = ReadVramWord(batOffset + tileCol);
                        int tileIndex = batEntry & 0x07FF;
                        palette = ((batEntry >> 12) & 0x0F) << 4;
                        int tileData = tileIndex << 4;
                        int lineStartA = tileData + tileY;
                        int lineStartB = lineStartA + 8;
                        if (bgCgMode)
                        {
                            int word = ReadVramWord(lineStartB);
                            byte1 = word & 0xFF;
                            byte2 = (word >> 8) & 0xFF;
                            byte3 = 0;
                            byte4 = 0;
                        }
                        else
                        {
                            int wordA = ReadVramWord(lineStartA);
                            int wordB = ReadVramWord(lineStartB);
                            byte1 = wordA & 0xFF;
                            byte2 = (wordA >> 8) & 0xFF;
                            byte3 = wordB & 0xFF;
                            byte4 = (wordB >> 8) & 0xFF;
                        }
                        prevTileCol = tileCol;
                    }
                    int tileX = 7 - (bgX & 7);
                    int bgColor =
                        ((byte1 >> tileX) & 1) |
                        (((byte2 >> tileX) & 1) << 1) |
                        (((byte3 >> tileX) & 1) << 2) |
                        (((byte4 >> tileX) & 1) << 3);
                    ScanLinePtr[i] = bgColor == 0 ? 0 : (palette | bgColor);
                    bgLine[i] = ScanLinePtr[i];
                }

            }

            if (m_LatchedEnableSprites)
            {
                if (SpriteDrawForward)
                {
                    for (i = 0; i < _currentLineSpriteCount; i++)
                    {
                        var entry = _currentLineSprites[i];
                        DrawSPRTile(bgLine, spriteLine, spritePriorityLine, spriteOwnerLine, entry.SatIndex, entry.Palette, entry.Flags, entry.X, entry.Plane0, entry.Plane1, entry.Plane2, entry.Plane3);
                    }
                }
                else
                {
                    for (i = _currentLineSpriteCount - 1; i >= 0; i--)
                    {
                        var entry = _currentLineSprites[i];
                        DrawSPRTile(bgLine, spriteLine, spritePriorityLine, spriteOwnerLine, entry.SatIndex, entry.Palette, entry.Flags, entry.X, entry.Plane0, entry.Plane1, entry.Plane2, entry.Plane3);
                    }
                }

                for (i = 0; i < screenWidth; i++)
                {
                    if ((spriteLine[i] & 0x0F) != 0 &&
                        (spritePriorityLine[i] != 0 || (bgLine[i] & 0x0F) == 0))
                        ScanLinePtr[i] = spriteLine[i];
                }
            }

            if (TracePixelLine && _tracePixelLineCount < TracePixelLineLimit &&
                TraceLineSelected(visibleLine, TracePixelLineOnly, TracePixelLineMin, TracePixelLineMax))
            {
                _tracePixelLineCount++;
                int xMin = Math.Clamp(TracePixelXMin, 0, screenWidth - 1);
                int xMax = Math.Clamp(TracePixelXMax, xMin, screenWidth - 1);
                WriteSpriteTrace($"[PCE-PIX] frame={m_FrameCounter} render={m_RenderLine} line={visibleLine} xrange={xMin}-{xMax} sprites={_currentLineSpriteCount}");
                for (i = xMin; i <= xMax; i++)
                {
                    WriteSpriteTrace(
                        $"[PCE-PIX] line={visibleLine} x={i:D3} bg=0x{bgLine[i]:X3} spr=0x{spriteLine[i]:X3} sat={spriteOwnerLine[i]:D2} final=0x{ScanLinePtr[i]:X3}");
                }
            }

            //colorindex to ARGB8888
            //ushort grayscaleBit = (ushort)(Grayscale ? 0x200 : 0);
            int color = 0;
            int* LineWritePtr = ScanLinePtr;
            for (i = 0; i < screenWidth; i++, ScanLinePtr++)
            {
                //color = PALETTE[m_VCE[*ScanLinePtr & 0x1FF] | grayscaleBit];
                color = PALETTE[m_VCE[*ScanLinePtr & 0x1FF]];
                *(LineWritePtr++) = color;
            }
        }

        public unsafe void ConvertColor()
        {
            int color = 0;
            int screenWidth = m_LatchedScreenWidth > 0 ? m_LatchedScreenWidth : SCREEN_WIDTH;
            for (int y = 0; y < m_VDC_VDW; y++)
            {
                int* LineWritePtr = (int*)_screenBufPtr.ToPointer() + screenWidth * y;
                for (int i = 0; i < screenWidth; i++, LineWritePtr++)
                {
                    color = PALETTE[m_VCE[*LineWritePtr & 0x1FF]];
                    *(LineWritePtr) = color;
                }
            }
        }

        public unsafe void DrawSPRTile(Span<int> bgLine, Span<int> spriteLine, Span<int> spritePriorityLine, Span<int> spriteOwnerLine, int satIndex, int palette, int flags, int pos, ushort plane0, ushort plane1, ushort plane2, ushort plane3)
        {
            int screenWidth = m_LatchedScreenWidth > 0 ? m_LatchedScreenWidth : SCREEN_WIDTH;
            if ((pos + 15) < 0 || pos >= screenWidth)
                return;

            bool priority = (flags & 0x0080) != 0;
            bool flip = (flags & 0x0800) != 0;
            int startX = pos < 0 ? -pos : 0;
            int endX = pos + 15 >= screenWidth ? (screenWidth - pos - 1) : 15;
            Span<byte> line = stackalloc byte[16];

            int p0Left = (plane0 >> 8) & 0xFF;
            int p1Left = (plane1 >> 8) & 0xFF;
            int p2Left = (plane2 >> 8) & 0xFF;
            int p3Left = (plane3 >> 8) & 0xFF;
            int p0Right = plane0 & 0xFF;
            int p1Right = plane1 & 0xFF;
            int p2Right = plane2 & 0xFF;
            int p3Right = plane3 & 0xFF;

            for (int bit = 7, x = 0; bit >= 0; bit--, x++)
            {
                line[x] =
                    (byte)(((p0Left >> bit) & 1) |
                           (((p1Left >> bit) & 1) << 1) |
                           (((p2Left >> bit) & 1) << 2) |
                           (((p3Left >> bit) & 1) << 3));
            }

            for (int bit = 7, x = 8; bit >= 0; bit--, x++)
            {
                line[x] =
                    (byte)(((p0Right >> bit) & 1) |
                           (((p1Right >> bit) & 1) << 1) |
                           (((p2Right >> bit) & 1) << 2) |
                           (((p3Right >> bit) & 1) << 3));
            }

            for (int x = startX; x <= endX; x++)
            {
                int pixelIndex = flip ? (15 - x) : x;
                int pixel = line[pixelIndex];

                if ((pixel & 0x0F) == 0)
                    continue;

                int xInScreen = pos + x;
                if (!priority && (bgLine[xInScreen] & 0x0F) != 0)
                    pixel = 0;

                if (satIndex == 0 && (spriteLine[xInScreen] & 0x0F) != 0)
                {
                    m_VDC_CR = m_VDC_Spr0Col;
                    if (m_VDC_Spr0Col)
                        m_WaitingIRQ = true;
                }

                spriteLine[xInScreen] = pixel == 0 ? 0 : (pixel | palette | 0x100);
                spritePriorityLine[xInScreen] = pixel == 0 ? 0 : (priority ? 1 : 0);
                spriteOwnerLine[xInScreen] = satIndex;
            }
        }

        public unsafe void DrawBGTile(int* ScanLinePtr, ref int* px, int palette, int tile)
        {
            int word0 = ReadVramWord(tile);
            int word1 = ReadVramWord(tile + 8);
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

        public int PeekSelectedVdcRegister() => m_VDC_Reg;
        public int PeekFrameCounter() => m_FrameCounter;
        public int PeekRenderLine() => m_RenderLine;
        public int PeekDisplayCounter() => m_DisplayCounter;

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
                case 0x02:
                    Transient.VdcVwr = (ushort)((Transient.VdcVwr & 0xFF00) | data);
                    break;
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
                case 0x07:
                    m_VDC_BXR = (m_VDC_BXR & 0x0300) | data;
                    break;
                case 0x08:
                    m_VDC_BYR_Offset = (m_RenderLine + 1 >= m_VDC_VDW || !m_VDC_EnableBackground) ? 0 : (m_RenderLine - 1);
                    m_VDC_BYR = (m_VDC_BYR & 0x0100) | data;
                    m_BgCounterY = m_VDC_BYR & 0x1FF;
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
                    m_VDC_HDW = data & 0x7F;
                    SCREEN_WIDTH = (m_VDC_HDW + 1) * 8;
                    LogVdcRegs("LSB-HDW");
                    break;
                case 0x0C:
                    m_VDC_VSR = (m_VDC_VSR & 0xFF00) | data;
                    LogVdcRegs("LSB-VSR");
                    break;
                case 0x0D: m_VDC_VDW = (m_VDC_VDW & 0x100) | data; break;
                case 0x0E: m_VDC_VCR = data; break;
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
                case 0x13:
                    m_VDC_VSAR = (ushort)((m_VDC_VSAR & 0xFF00) | data);
                    break;
            }
        }

        private void WriteVDCRegisterMSB(int reg, byte data)
        {
            switch (reg)
            {
                case 0x00: m_VDC_MAWR = (m_VDC_MAWR & 0xFF) | (data << 8); break;
                case 0x01: m_VDC_MARR = (m_VDC_MARR & 0xFF) | (data << 8); break;
                case 0x02:
                    ushort vdcVwr = (ushort)((Transient.VdcVwr & 0x00FF) | (data << 8));
                    Transient.VdcVwr = vdcVwr;
                    m_VRAM[m_VDC_MAWR] = vdcVwr;
                    TraceVramWriteIfNeeded(m_VDC_MAWR, vdcVwr);
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
                case 0x07:
                    m_VDC_BXR = (m_VDC_BXR & 0xFF) | ((data << 8) & 0x0300);
                    break;
                case 0x08:
                    m_VDC_BYR_Offset = (m_RenderLine + 1 >= m_VDC_VDW || !m_VDC_EnableBackground) ? 0 : (m_RenderLine - 1);
                    m_VDC_BYR = (m_VDC_BYR & 0xFF) | ((data << 8) & 0x0100);
                    m_BgCounterY = m_VDC_BYR & 0x1FF;
                    break;
                case 0x0A:
                    m_VDC_HDS = data & 0x7F;
                    LogVdcRegs("MSB-HDS");
                    break;
                case 0x0B:
                    m_VDC_HDE = data & 0x7F;
                    LogVdcRegs("MSB-HDE");
                    break;
                case 0x0C:
                    m_VDC_VSR = ((data << 8) & 0xFF00) | (m_VDC_VSR & 0x00FF);
                    LogVdcRegs("MSB-VSR");
                    break;
                case 0x0D: m_VDC_VDW = ((data << 8) & 0x100) | (m_VDC_VDW & 0xFF); break;
                case 0x0E: break;
                case 0x10: m_VDC_DSR = (ushort)((m_VDC_DSR & 0xFF) | (data << 8)); break;
                case 0x11: m_VDC_DESR = (ushort)((m_VDC_DESR & 0xFF) | (data << 8)); break;
                case 0x12:
                    m_VDC_LENR = (ushort)((m_VDC_LENR & 0xFF) | (data << 8));
                    m_VDC_DMA_Enable = true;
                    Transient.VramTransferSrc = m_VDC_DSR;
                    Transient.VramTransferDest = m_VDC_DESR;
                    Transient.VramTransferWordsRemaining = m_VDC_LENR + 1;
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
                case 0x13:
                    m_VDC_VSAR = (ushort)((m_VDC_VSAR & 0xFF) | (data << 8));
                    m_TriggerSAT_DMA = true;
                    break;
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
                    TraceVceWriteIfNeeded(4, m_VCE_Index, m_VCE[m_VCE_Index], data);
                    break;

                case 5:
                    // 写入调色板数据高字节
                    m_VCE[m_VCE_Index] = (ushort)((m_VCE[m_VCE_Index] & 0xFF) | ((data << 8) & 0x100));
                    TraceVceWriteIfNeeded(5, m_VCE_Index, m_VCE[m_VCE_Index], data);
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
