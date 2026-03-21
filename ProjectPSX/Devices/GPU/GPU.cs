using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace ProjectPSX.Devices {

    public class GPU {

        private uint GPUREAD;     //1F801810h-Read GPUREAD Receive responses to GP0(C0h) and GP1(10h) commands

        private uint command;
        private int commandSize;
        private uint[] commandBuffer = new uint[16];
        [NonSerialized]
        private ushort[] vramCopyScratch = Array.Empty<ushort>();
        private int pointer;

        private int scanLine = 0;

        private static readonly int[] resolutions = { 256, 320, 512, 640, 384 };//gpustat res index
        private static readonly int[] dotClockDiv = { 10, 8, 5, 4, 7 };
        private const int IdentityTextureModulationColor = 0x00808080;
        private static readonly ushort[] modulate1555RByShade = BuildDynamicModulate1555Table(0);
        private static readonly ushort[] modulate1555GByShade = BuildDynamicModulate1555Table(5);
        private static readonly ushort[] modulate1555BByShade = BuildDynamicModulate1555Table(10);
        private static readonly ushort[] blend1555AddR = BuildSemiTransparentBlend1555Table(0, 1);
        private static readonly ushort[] blend1555AddG = BuildSemiTransparentBlend1555Table(5, 1);
        private static readonly ushort[] blend1555AddB = BuildSemiTransparentBlend1555Table(10, 1);
        private static readonly ushort[] blend1555SubR = BuildSemiTransparentBlend1555Table(0, 2);
        private static readonly ushort[] blend1555SubG = BuildSemiTransparentBlend1555Table(5, 2);
        private static readonly ushort[] blend1555SubB = BuildSemiTransparentBlend1555Table(10, 2);
        private static readonly ushort[] blend1555QuarterAddR = BuildSemiTransparentBlend1555Table(0, 3);
        private static readonly ushort[] blend1555QuarterAddG = BuildSemiTransparentBlend1555Table(5, 3);
        private static readonly ushort[] blend1555QuarterAddB = BuildSemiTransparentBlend1555Table(10, 3);

        [NonSerialized]
        private IHostWindow window;

        private VRAM vram = new VRAM(); //Vram is 8888 and we transform everything to it
        private VRAM1555 vram1555 = new VRAM1555(); //an un transformed 1555 to 8888 vram so we can fetch clut indexes without reverting to 1555

        [NonSerialized]
        private int[] color1555to8888LUT;

        public bool debug;

        private enum Mode {
            COMMAND,
            VRAM
        }
        private Mode mode;

        private struct Primitive {
            public bool isShaded;
            public bool isTextured;
            public bool isSemiTransparent;
            public bool isRawTextured;//if not: blended
            public int depth;
            public int semiTransparencyMode;
            public Point2D clut;
            public Point2D textureBase;
        }

        private struct VramTransfer {
            public int x, y;
            public ushort w, h;
            public int origin_x;
            public int origin_y;
            public int halfWords;
        }
        private VramTransfer vramTransfer;


        [StructLayout(LayoutKind.Explicit)]
        private struct Point2D {
            [FieldOffset(0)] public short x;
            [FieldOffset(2)] public short y;
        }
        private Point2D min = new Point2D();
        private Point2D max = new Point2D();

        [StructLayout(LayoutKind.Explicit)]
        private struct TextureData {
            [FieldOffset(0)] public ushort val;
            [FieldOffset(0)] public byte x;
            [FieldOffset(1)] public byte y;
        }
        TextureData textureData = new TextureData();

        [StructLayout(LayoutKind.Explicit)]
        private struct Color {
            [FieldOffset(0)] public uint val;
            [FieldOffset(0)] public byte r;
            [FieldOffset(1)] public byte g;
            [FieldOffset(2)] public byte b;
            [FieldOffset(3)] public byte m;
        }
        private Color color0;
        private Color color1;
        private Color color2;

        private bool isTextureDisabledAllowed;

        //GP0
        private byte textureXBase;
        private byte textureYBase;
        private byte transparencyMode;
        private byte textureDepth;
        private bool isDithered;
        private bool isDrawingToDisplayAllowed;
        private int maskWhileDrawing;
        private bool checkMaskBeforeDraw;
        private bool isInterlaceField;
        private bool isReverseFlag;
        private bool isTextureDisabled;
        private byte horizontalResolution2;
        private byte horizontalResolution1;
        private bool isVerticalResolution480;
        private bool isPal;
        [NonSerialized]
        private bool? forcedVideoStandardPal;
        private bool is24BitDepth;
        private bool isVerticalInterlace;
        private bool isDisplayDisabled;
        private bool isInterruptRequested;
        private bool isDmaRequest;

        private bool isReadyToReceiveCommand = true; //todo
        private bool isReadyToSendVRAMToCPU; 
        private bool isReadyToReceiveDMABlock = true; //todo

        private byte dmaDirection;
        private bool isOddLine;

        private bool isTexturedRectangleXFlipped;
        private bool isTexturedRectangleYFlipped;

        private uint drawModeBits = 0xFFFF_FFFF;
        private uint displayModeBits = 0xFFFF_FFFF;
        uint displayVerticalRange = 0xFFFF_FFFF;
        uint displayHorizontalRange = 0xFFFF_FFFF;

        private uint textureWindowBits = 0xFFFF_FFFF;
        private int preMaskX;
        private int preMaskY;
        private int postMaskX;
        private int postMaskY;
        private bool textureWindowIdentity = true;

        private ushort drawingAreaLeft;
        private ushort drawingAreaRight;
        private ushort drawingAreaTop;
        private ushort drawingAreaBottom;
        private short drawingXOffset;
        private short drawingYOffset;

        private ushort displayVRAMXStart;
        private ushort displayVRAMYStart;
        private ushort displayX1;
        private ushort displayX2;
        private ushort displayY1;
        private ushort displayY2;

        private int videoCycles;
        private int horizontalTiming = 3413;
        private int verticalTiming = 263;

        public GPU(IHostWindow window) {
            this.window = window;
            mode = Mode.COMMAND;
            initColorTable();
            GP1_00_ResetGPU();
        }

        public bool IsPalMode => forcedVideoStandardPal ?? isPal;

        public void SetVideoStandardOverride(bool? forcePal) {
            forcedVideoStandardPal = forcePal;
            ApplyVideoTiming();
        }

        public void ResyncAfterLoad(IHostWindow window) {
            this.window = window;
            if (color1555to8888LUT == null || color1555to8888LUT.Length != ushort.MaxValue + 1) {
                initColorTable();
            }

            int horizontalRes = resolutions[horizontalResolution2 << 2 | horizontalResolution1];
            int verticalRes = isVerticalResolution480 ? 480 : 240;
            window.SetDisplayMode(horizontalRes, verticalRes, is24BitDepth);
            window.SetHorizontalRange(displayX1, displayX2);
            window.SetVerticalRange(displayY1, displayY2);
            window.SetVRAMStart(displayVRAMXStart, displayVRAMYStart);
        }

        public void initColorTable() {
            color1555to8888LUT = new int[ushort.MaxValue + 1];
            for (int m = 0; m < 2; m++) {
                for (int r = 0; r < 32; r++) {
                    for (int g = 0; g < 32; g++) {
                        for (int b = 0; b < 32; b++) {
                            int r8 = (r << 3) | (r >> 2);
                            int g8 = (g << 3) | (g >> 2);
                            int b8 = (b << 3) | (b >> 2);
                            color1555to8888LUT[m << 15 | b << 10 | g << 5 | r] =
                                (m << 24) | (r8 << 16) | (g8 << 8) | b8;
                        }
                    }
                }
            }
        }

        public bool tick(int cycles) {
            //Video clock is the cpu clock multiplied by 11/7.
            videoCycles += cycles * 11 / 7;


            if (videoCycles >= horizontalTiming) {
                videoCycles -= horizontalTiming;
                scanLine++;

                if (!isVerticalResolution480) {
                    isOddLine = (scanLine & 0x1) != 0;
                }

                if (scanLine >= verticalTiming) {
                    scanLine = 0;

                    if (isVerticalInterlace && isVerticalResolution480) {
                        isOddLine = !isOddLine;
                        isInterlaceField = !isOddLine;
                    }

                    window.Render(vram1555.Bits);
                    return true;
                }
            }
            return false;
        }

        public string DebugSummary() {
            int horizontalRes = resolutions[horizontalResolution2 << 2 | horizontalResolution1];
            int verticalRes = isVerticalResolution480 ? 480 : 240;
            return
                $"gpu[hres={horizontalRes} vres={verticalRes} 24={(is24BitDepth ? 1 : 0)} pal={(IsPalMode ? 1 : 0)} interlace={(isVerticalInterlace ? 1 : 0)} " +
                $"disp=({displayX1}-{displayX2},{displayY1}-{displayY2}) vram=({displayVRAMXStart},{displayVRAMYStart}) " +
                $"scan=({scanLine},{videoCycles}) mode={mode}]";
        }

        public (int dot, bool hblank, bool bBlank) getBlanksAndDot() { //test
            int dot = dotClockDiv[horizontalResolution2 << 2 | horizontalResolution1];
            bool hBlank = videoCycles < displayX1 || videoCycles > displayX2;
            bool vBlank = scanLine < displayY1 || scanLine > displayY2;

            return (dot, hBlank, vBlank);
        }

        public uint loadGPUSTAT() {
            uint GPUSTAT = 0;

            GPUSTAT |= drawModeBits & 0x7FF;
            GPUSTAT |= (uint)maskWhileDrawing << 11;
            GPUSTAT |= (uint)(checkMaskBeforeDraw ? 1 : 0) << 12;
            GPUSTAT |= (uint)(isInterlaceField ? 1 : 0) << 13;
            GPUSTAT |= (uint)(isReverseFlag ? 1 : 0) << 14;
            GPUSTAT |= (uint)(isTextureDisabled ? 1 : 0) << 15;
            GPUSTAT |= (uint)horizontalResolution2 << 16;
            GPUSTAT |= (uint)horizontalResolution1 << 17;
            GPUSTAT |= (uint)(isVerticalResolution480 ? 1 : 0) << 19;
            GPUSTAT |= (uint)(IsPalMode ? 1 : 0) << 20;
            GPUSTAT |= (uint)(is24BitDepth ? 1 : 0) << 21;
            GPUSTAT |= (uint)(isVerticalInterlace ? 1 : 0) << 22;
            GPUSTAT |= (uint)(isDisplayDisabled ? 1 : 0) << 23;
            GPUSTAT |= (uint)(isInterruptRequested ? 1 : 0) << 24;
            GPUSTAT |= (uint)(isDmaRequest ? 1 : 0) << 25;

            GPUSTAT |= (uint)(isReadyToReceiveCommand ? 1 : 0) << 26;
            GPUSTAT |= (uint)(isReadyToSendVRAMToCPU ? 1 : 0) << 27;
            GPUSTAT |= (uint)(isReadyToReceiveDMABlock ? 1 : 0) << 28;

            GPUSTAT |= (uint)dmaDirection << 29;
            GPUSTAT |= (uint)(isOddLine ? 1 : 0) << 31;

            //Console.WriteLine("[GPU] LOAD GPUSTAT: {0}", GPUSTAT.ToString("x8"));
            return GPUSTAT;
        }

        public uint loadGPUREAD() {
            //TODO check if correct and refact
            uint value;
            if (vramTransfer.halfWords > 0) {
                value = readFromVRAM();
            } else {
                value = GPUREAD;
            }
            //Console.WriteLine("[GPU] LOAD GPUREAD: {0}", value.ToString("x8"));
            return value;
        }

        public void write(uint addr, uint value) {
            uint register = addr & 0xF;
            if (register == 0) {
                writeGP0(value);
            } else if (register == 4) {
                writeGP1(value);
            } else {
                Console.WriteLine($"[GPU] Unhandled GPU write access to register {register} : {value}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void writeGP0(uint value) {
            //Console.WriteLine("Direct " + value.ToString("x8"));
            //Console.WriteLine(mode);
            if (mode == Mode.COMMAND) {
                DecodeGP0Command(value);
            } else {
                WriteToVRAM(value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void processDma(Span<uint> dma) {
            if (mode == Mode.COMMAND) {
                    DecodeGP0Command(dma);
            } else {
                for (int i = 0; i < dma.Length; i++) {
                    WriteToVRAM(dma[i]);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteToVRAM(uint value) {
            ushort pixel1 = (ushort)(value >> 16);
            ushort pixel0 = (ushort)(value & 0xFFFF);

            pixel0 |= (ushort)(maskWhileDrawing << 15);
            pixel1 |= (ushort)(maskWhileDrawing << 15);

            drawVRAMPixel(pixel0);

            //Force exit if we arrived to the end pixel (fixes weird artifacts on textures on Metal Gear Solid)
            if (--vramTransfer.halfWords == 0) {
                mode = Mode.COMMAND;
                return;
            }

            drawVRAMPixel(pixel1);

            if (--vramTransfer.halfWords == 0) {
                mode = Mode.COMMAND;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint readFromVRAM() {
            ushort pixel0 = vram1555.GetPixel(vramTransfer.x & 0x3FF, vramTransfer.y & 0x1FF);
            stepVramTransfer();
            ushort pixel1 = vram1555.GetPixel(vramTransfer.x & 0x3FF, vramTransfer.y & 0x1FF);
            stepVramTransfer();

            vramTransfer.halfWords -= 2;

            if (vramTransfer.halfWords == 0) {
                isReadyToSendVRAMToCPU = false;
                isReadyToReceiveDMABlock = true;
            }

            return (uint)(pixel1 << 16 | pixel0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void stepVramTransfer() {
            if (++vramTransfer.x == vramTransfer.origin_x + vramTransfer.w) {
                vramTransfer.x -= vramTransfer.w;
                vramTransfer.y++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void drawVRAMPixel(ushort color1555) {
            if (!checkMaskBeforeDraw || (vram1555.GetPixel(vramTransfer.x & 0x3FF, vramTransfer.y & 0x1FF) & 0x8000) == 0) {
                vram1555.SetPixel(vramTransfer.x & 0x3FF, vramTransfer.y & 0x1FF, color1555);
            }

            stepVramTransfer();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeGP0Command(uint value) {
            if (pointer == 0) {
                command = value >> 24;
                commandSize = CommandSizeTable[(int)command];
                //Console.WriteLine("[GPU] Direct GP0 COMMAND: {0} size: {1}", value.ToString("x8"), commandSize);
            }

            commandBuffer[pointer++] = value;
            //Console.WriteLine("[GPU] Direct GP0: {0} buffer: {1}", value.ToString("x8"), pointer);

            if (pointer == commandSize || commandSize == 16 && (value & 0xF000_F000) == 0x5000_5000) {
                pointer = 0;
                //Console.WriteLine("EXECUTING");
                ExecuteGP0(command, commandBuffer.AsSpan());
                pointer = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeGP0Command(Span<uint> buffer) {
            //Console.WriteLine(commandBuffer.Length);

            while (pointer < buffer.Length) {
                if (mode == Mode.COMMAND) {
                    command = buffer[pointer] >> 24;
                    //if (debug) Console.WriteLine("Buffer Executing " + command.ToString("x2") + " pointer " + pointer);
                    ExecuteGP0(command, buffer);
                } else {
                    WriteToVRAM(buffer[pointer++]);
                }
            }
            pointer = 0;
            //Console.WriteLine("fin");
        }

        private void ExecuteGP0(uint opcode, Span<uint> buffer) {
            //Console.WriteLine("GP0 Command: " + opcode.ToString("x2"));
            switch (opcode) {
                case 0x00: GP0_00_NOP(); break;
                case 0x01: GP0_01_MemClearCache(); break;
                case 0x02: GP0_02_FillRectVRAM(buffer); break;
                case 0x1F: GP0_1F_InterruptRequest(); break;

                case 0xE1: GP0_E1_SetDrawMode(buffer[pointer++]); break;
                case 0xE2: GP0_E2_SetTextureWindow(buffer[pointer++]); break;
                case 0xE3: GP0_E3_SetDrawingAreaTopLeft(buffer[pointer++]); break;
                case 0xE4: GP0_E4_SetDrawingAreaBottomRight(buffer[pointer++]); break;
                case 0xE5: GP0_E5_SetDrawingOffset(buffer[pointer++]); break;
                case 0xE6: GP0_E6_SetMaskBit(buffer[pointer++]); break;

                case uint _ when opcode >= 0x20 && opcode <= 0x3F:
                    GP0_RenderPolygon(buffer); break;
                case uint _ when opcode >= 0x40 && opcode <= 0x5F:
                    GP0_RenderLine(buffer); break;
                case uint _ when opcode >= 0x60 && opcode <= 0x7F:
                    GP0_RenderRectangle(buffer); break;
                case uint _ when opcode >= 0x80 && opcode <= 0x9F:
                    GP0_MemCopyRectVRAMtoVRAM(buffer); break;
                case uint _ when opcode >= 0xA0 && opcode <= 0xBF:
                    GP0_MemCopyRectCPUtoVRAM(buffer); break;
                case uint _ when opcode >= 0xC0 && opcode <= 0xDF:
                    GP0_MemCopyRectVRAMtoCPU(buffer); break;
                case uint _ when (opcode >= 0x3 && opcode <= 0x1E) || opcode == 0xE0 || opcode >= 0xE7 && opcode <= 0xEF:
                    GP0_00_NOP(); break;

                default: Console.WriteLine("[GPU] Unsupported GP0 Command " + opcode.ToString("x8")); /*Console.ReadLine();*/ GP0_00_NOP(); break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GP0_00_NOP() => pointer++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GP0_01_MemClearCache() => pointer++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GP0_02_FillRectVRAM(Span<uint> buffer) {
            color0.val = buffer[pointer++];
            uint yx = buffer[pointer++];
            uint hw = buffer[pointer++];

            ushort x = (ushort)(yx & 0x3F0);
            ushort y = (ushort)((yx >> 16) & 0x1FF);

            ushort w = (ushort)(((hw & 0x3FF) + 0xF) & ~0xF);
            ushort h = (ushort)((hw >> 16) & 0x1FF);

            int color = color0.r << 16 | color0.g << 8 | color0.b;
            ushort rawColor = PackColor1555(color);

            if(x + w <= 0x3FF && y + h <= 0x1FF) {
                var vram1555Span = new Span<ushort>(vram1555.Bits);
                for (int yPos = y; yPos < h + y; yPos++) {
                    vram1555Span.Slice(x + (yPos * 1024), w).Fill(rawColor);
                }
            } else {
                for (int yPos = y; yPos < h + y; yPos++) {
                    for (int xPos = x; xPos < w + x; xPos++) {
                        int writeX = xPos & 0x3FF;
                        int writeY = yPos & 0x1FF;
                        vram1555.SetPixel(writeX, writeY, rawColor);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GP0_1F_InterruptRequest() {
            pointer++;
            isInterruptRequested = true;
        }

        public void GP0_RenderPolygon(Span<uint> buffer) {
            uint command = buffer[pointer];
            //Console.WriteLine(command.ToString("x8") +  " "  + commandBuffer.Length + " " + pointer);

            bool isQuad = (command & (1 << 27)) != 0;

            bool isShaded = (command & (1 << 28)) != 0;
            bool isTextured = (command & (1 << 26)) != 0;
            bool isSemiTransparent = (command & (1 << 25)) != 0;
            bool isRawTextured = (command & (1 << 24)) != 0;

            Primitive primitive = new Primitive();
            primitive.isShaded = isShaded;
            primitive.isTextured = isTextured;
            primitive.isSemiTransparent = isSemiTransparent;
            primitive.isRawTextured = isRawTextured;

            int vertexN = isQuad ? 4 : 3;
            Span<uint> c = stackalloc uint[vertexN];
            Span<Point2D> v = stackalloc Point2D[vertexN];
            Span<TextureData> t = stackalloc TextureData[vertexN];

            if (!isShaded) {
                uint color = buffer[pointer++];
                uint rgbColor = (uint)GetRgbColor(color);
                c[0] = rgbColor; //triangle 1 opaque color
                c[1] = rgbColor; //triangle 2 opaque color
            }

            primitive.semiTransparencyMode = transparencyMode;

            for (int i = 0; i < vertexN; i++) {
                if (isShaded) c[i] = buffer[pointer++];

                uint xy = buffer[pointer++];
                v[i].x = (short)(signed11bit(xy & 0xFFFF) + drawingXOffset);
                v[i].y = (short)(signed11bit(xy >> 16) + drawingYOffset);

                if (isTextured) {
                    uint textureData = buffer[pointer++];
                    t[i].val = (ushort)textureData;
                    if (i == 0) {
                        uint palette = textureData >> 16;

                        primitive.clut.x = (short)((palette & 0x3f) << 4);
                        primitive.clut.y = (short)((palette >> 6) & 0x1FF);
                    } else if (i == 1) {
                        uint texpage = textureData >> 16;

                        //SET GLOBAL GPU E1
                        GP0_E1_SetDrawMode(texpage);

                        primitive.depth = textureDepth;
                        primitive.textureBase.x = (short)(textureXBase << 6);
                        primitive.textureBase.y = (short)(textureYBase << 8);
                        primitive.semiTransparencyMode = transparencyMode;
                    }
                }
            }

            rasterizeTri(v[0], v[1], v[2], t[0], t[1], t[2], c[0], c[1], c[2], primitive);
            if (isQuad) rasterizeTri(v[1], v[2], v[3], t[1], t[2], t[3], c[1], c[2], c[3], primitive);
        }

        private void rasterizeTri(Point2D v0, Point2D v1, Point2D v2, TextureData t0, TextureData t1, TextureData t2, uint c0, uint c1, uint c2, Primitive primitive) {

            int area = orient2d(v0, v1, v2);

            if (area == 0) return;

            if (area < 0) {
                (v1, v2) = (v2, v1);
                (t1, t2) = (t2, t1);
                (c1, c2) = (c2, c1);
                area = -area;
            }

            /*boundingBox*/
            int minX = Math.Min(v0.x, Math.Min(v1.x, v2.x));
            int minY = Math.Min(v0.y, Math.Min(v1.y, v2.y));
            int maxX = Math.Max(v0.x, Math.Max(v1.x, v2.x));
            int maxY = Math.Max(v0.y, Math.Max(v1.y, v2.y));

            if ((maxX - minX) > 1024 || (maxY - minY) > 512) return;

            /*clip*/
            min.x = (short)Math.Max(minX, drawingAreaLeft);
            min.y = (short)Math.Max(minY, drawingAreaTop);
            max.x = (short)Math.Min(maxX, drawingAreaRight + 1);
            max.y = (short)Math.Min(maxY, drawingAreaBottom + 1);

            int A01 = v0.y - v1.y, B01 = v1.x - v0.x;
            int A12 = v1.y - v2.y, B12 = v2.x - v1.x;
            int A20 = v2.y - v0.y, B20 = v0.x - v2.x;

            int bias0 = isTopLeft(v1, v2) ? 0 : -1;
            int bias1 = isTopLeft(v2, v0) ? 0 : -1;
            int bias2 = isTopLeft(v0, v1) ? 0 : -1;

            int w0_row = orient2d(v1, v2, min) + bias0;
            int w1_row = orient2d(v2, v0, min) + bias1;
            int w2_row = orient2d(v0, v1, min) + bias2;
            ushort[] vram1555Bits = vram1555.Bits;
            bool checkMask = checkMaskBeforeDraw;
            int maskBits = maskWhileDrawing << 24;
            ushort genericMaskBit1555 = (ushort)(maskWhileDrawing << 15);
            bool shaded = primitive.isShaded;
            bool textured = primitive.isTextured;
            bool flatOpaqueFill = !primitive.isShaded && !primitive.isTextured && !primitive.isSemiTransparent;
            Span<ushort> genericFlatModulateR = stackalloc ushort[32];
            Span<ushort> genericFlatModulateG = stackalloc ushort[32];
            Span<ushort> genericFlatModulateB = stackalloc ushort[32];

            if (textured && !primitive.isRawTextured && !shaded) {
                BuildModulate1555Tables((int)c0, genericFlatModulateR, genericFlatModulateG, genericFlatModulateB);
            }

            if (flatOpaqueFill) {
                int fillColor = (int)c0 | maskBits;
                ushort fillColor1555 = PackColor1555(fillColor);
                int fillSpanWidth = max.x - min.x;

                for (int y = min.y; y < max.y; y++) {
                    if (TryGetTriangleSpanOffsets(w0_row, w1_row, w2_row, A12, A20, A01, fillSpanWidth, out int spanStart, out int spanEnd)) {
                        int pixelIndex = (y << 10) + min.x + spanStart;
                        int spanLength = spanEnd - spanStart;

                        if (!checkMask) {
                            Array.Fill(vram1555Bits, fillColor1555, pixelIndex, spanLength);
                        } else {
                            int spanLimit = pixelIndex + spanLength;
                            for (; pixelIndex < spanLimit; pixelIndex++) {
                                if ((vram1555Bits[pixelIndex] & 0x8000) == 0) {
                                    vram1555Bits[pixelIndex] = fillColor1555;
                                }
                            }
                        }
                    }

                    w0_row += B12;
                    w1_row += B20;
                    w2_row += B01;
                }

                return;
            }

            int u0Row = w0_row - bias0;
            int u1Row = w1_row - bias1;
            int u2Row = w2_row - bias2;

            int shadeRRow = 0, shadeGRow = 0, shadeBRow = 0;
            int shadeRStepX = 0, shadeGStepX = 0, shadeBStepX = 0;
            int shadeRStepY = 0, shadeGStepY = 0, shadeBStepY = 0;
            if (shaded) {
                int c0r = (int)(c0 >> 16) & 0xFF;
                int c0g = (int)(c0 >> 8) & 0xFF;
                int c0b = (int)c0 & 0xFF;
                int c1r = (int)(c1 >> 16) & 0xFF;
                int c1g = (int)(c1 >> 8) & 0xFF;
                int c1b = (int)c1 & 0xFF;
                int c2r = (int)(c2 >> 16) & 0xFF;
                int c2g = (int)(c2 >> 8) & 0xFF;
                int c2b = (int)c2 & 0xFF;

                shadeRRow = c0r * u0Row + c1r * u1Row + c2r * u2Row;
                shadeGRow = c0g * u0Row + c1g * u1Row + c2g * u2Row;
                shadeBRow = c0b * u0Row + c1b * u1Row + c2b * u2Row;
                shadeRStepX = c0r * A12 + c1r * A20 + c2r * A01;
                shadeGStepX = c0g * A12 + c1g * A20 + c2g * A01;
                shadeBStepX = c0b * A12 + c1b * A20 + c2b * A01;
                shadeRStepY = c0r * B12 + c1r * B20 + c2r * B01;
                shadeGStepY = c0g * B12 + c1g * B20 + c2g * B01;
                shadeBStepY = c0b * B12 + c1b * B20 + c2b * B01;
            }

            int texXRow = 0, texYRow = 0;
            int texXStepX = 0, texYStepX = 0;
            int texXStepY = 0, texYStepY = 0;
            int genericClutX = primitive.clut.x;
            int genericClutRowBase = primitive.clut.y << 10;
            int genericTextureBaseX = primitive.textureBase.x;
            int genericTextureBaseY = primitive.textureBase.y;
            int genericTextureDepth = primitive.depth;
            bool genericTextureWindowIdentity = textureWindowIdentity;
            if (textured) {
                texXRow = t0.x * u0Row + t1.x * u1Row + t2.x * u2Row;
                texYRow = t0.y * u0Row + t1.y * u1Row + t2.y * u2Row;
                texXStepX = t0.x * A12 + t1.x * A20 + t2.x * A01;
                texYStepX = t0.y * A12 + t1.y * A20 + t2.y * A01;
                texXStepY = t0.x * B12 + t1.x * B20 + t2.x * B01;
                texYStepY = t0.y * B12 + t1.y * B20 + t2.y * B01;
            }

            bool opaqueTexturedFastPath = textured && !shaded && !primitive.isSemiTransparent && !checkMask;
            bool gouraudTexturedFastPath = textured && shaded && !primitive.isSemiTransparent && !checkMask;
            bool semiTransparentTexturedFastPath = textured && primitive.isSemiTransparent && !checkMask;
            bool semiTransparentGouraudTexturedFastPath = textured && shaded && primitive.isSemiTransparent && !checkMask;
            int baseColor = (int)c0;
            bool passthroughTexturedFastPath =
                opaqueTexturedFastPath &&
                (primitive.isRawTextured || (baseColor & 0x00FF_FFFF) == IdentityTextureModulationColor);
            bool semiTransparentPassthroughTexturedFastPath =
                semiTransparentTexturedFastPath &&
                !shaded &&
                (primitive.isRawTextured || (baseColor & 0x00FF_FFFF) == IdentityTextureModulationColor);

            if (textureWindowIdentity) {
                if (semiTransparentPassthroughTexturedFastPath &&
                    TryRasterizeTrianglePassthroughTexturedIdentity(
                        min,
                        max,
                        primitive,
                        area,
                        w0_row,
                        w1_row,
                        w2_row,
                        A12,
                        A20,
                        A01,
                        B12,
                        B20,
                        B01,
                        texXRow,
                        texYRow,
                        texXStepX,
                        texYStepX,
                        texXStepY,
                        texYStepY,
                        primitive.semiTransparencyMode)) {
                    return;
                }

                if (semiTransparentTexturedFastPath &&
                    !shaded &&
                    TryRasterizeTriangleFlatTexturedIdentity(
                        min,
                        max,
                        primitive,
                        area,
                        w0_row,
                        w1_row,
                        w2_row,
                        A12,
                        A20,
                        A01,
                        B12,
                        B20,
                        B01,
                        texXRow,
                        texYRow,
                        texXStepX,
                        texYStepX,
                        texXStepY,
                        texYStepY,
                        baseColor,
                        maskBits,
                        primitive.semiTransparencyMode)) {
                    return;
                }

                if (semiTransparentGouraudTexturedFastPath &&
                    TryRasterizeTriangleGouraudTexturedIdentity(
                        min,
                        max,
                        primitive,
                        area,
                        w0_row,
                        w1_row,
                        w2_row,
                        A12,
                        A20,
                        A01,
                        B12,
                        B20,
                        B01,
                        texXRow,
                        texYRow,
                        texXStepX,
                        texYStepX,
                        texXStepY,
                        texYStepY,
                        shadeRRow,
                        shadeGRow,
                        shadeBRow,
                        shadeRStepX,
                        shadeGStepX,
                        shadeBStepX,
                        shadeRStepY,
                        shadeGStepY,
                        shadeBStepY,
                        maskBits,
                        primitive.semiTransparencyMode)) {
                    return;
                }

                if (passthroughTexturedFastPath &&
                    TryRasterizeTrianglePassthroughTexturedIdentity(
                        min,
                        max,
                        primitive,
                        area,
                        w0_row,
                        w1_row,
                        w2_row,
                        A12,
                        A20,
                        A01,
                        B12,
                        B20,
                        B01,
                        texXRow,
                        texYRow,
                        texXStepX,
                        texYStepX,
                        texXStepY,
                        texYStepY)) {
                    return;
                }

                if (opaqueTexturedFastPath &&
                    TryRasterizeTriangleFlatTexturedIdentity(
                        min,
                        max,
                        primitive,
                        area,
                        w0_row,
                        w1_row,
                        w2_row,
                        A12,
                        A20,
                        A01,
                        B12,
                        B20,
                        B01,
                        texXRow,
                        texYRow,
                        texXStepX,
                        texYStepX,
                        texXStepY,
                        texYStepY,
                        baseColor,
                        maskBits)) {
                    return;
                }

                if (gouraudTexturedFastPath &&
                    TryRasterizeTriangleGouraudTexturedIdentity(
                        min,
                        max,
                        primitive,
                        area,
                        w0_row,
                        w1_row,
                        w2_row,
                        A12,
                        A20,
                        A01,
                        B12,
                        B20,
                        B01,
                        texXRow,
                        texYRow,
                        texXStepX,
                        texYStepX,
                        texXStepY,
                        texYStepY,
                        shadeRRow,
                        shadeGRow,
                        shadeBRow,
                        shadeRStepX,
                        shadeGStepX,
                        shadeBStepX,
                        shadeRStepY,
                        shadeGStepY,
                        shadeBStepY,
                        maskBits)) {
                    return;
                }
            }

            if (passthroughTexturedFastPath) {
                int clutX = primitive.clut.x;
                int clutRowBase = primitive.clut.y << 10;
                int textureBaseX = primitive.textureBase.x;
                int textureBaseY = primitive.textureBase.y;
                int textureDepth = primitive.depth;
                ushort maskBit1555 = (ushort)(maskWhileDrawing << 15);
                ulong reciprocal = BuildUnsignedReciprocal(area);

                if (textureWindowIdentity) {
                    switch (textureDepth) {
                        case 0:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = FastDivideNonNegative(texX, area, reciprocal) & 0xFF;
                                        int texelY = FastDivideNonNegative(texY, area, reciprocal) & 0xFF;
                                        ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        case 1:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = FastDivideNonNegative(texX, area, reciprocal) & 0xFF;
                                        int texelY = FastDivideNonNegative(texY, area, reciprocal) & 0xFF;
                                        ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        default:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = FastDivideNonNegative(texX, area, reciprocal) & 0xFF;
                                        int texelY = FastDivideNonNegative(texY, area, reciprocal) & 0xFF;
                                        ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, texelX, texelY, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                    }
                } else {
                    switch (textureDepth) {
                        case 0:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = maskTexelAxis(FastDivideNonNegative(texX, area, reciprocal), preMaskX, postMaskX);
                                        int texelY = maskTexelAxis(FastDivideNonNegative(texY, area, reciprocal), preMaskY, postMaskY);
                                        ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        case 1:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = maskTexelAxis(FastDivideNonNegative(texX, area, reciprocal), preMaskX, postMaskX);
                                        int texelY = maskTexelAxis(FastDivideNonNegative(texY, area, reciprocal), preMaskY, postMaskY);
                                        ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        default:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = maskTexelAxis(FastDivideNonNegative(texX, area, reciprocal), preMaskX, postMaskX);
                                        int texelY = maskTexelAxis(FastDivideNonNegative(texY, area, reciprocal), preMaskY, postMaskY);
                                        ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, texelX, texelY, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                    }
                }

                return;
            }

            if (opaqueTexturedFastPath) {
                int clutX = primitive.clut.x;
                int clutRowBase = primitive.clut.y << 10;
                int textureBaseX = primitive.textureBase.x;
                int textureBaseY = primitive.textureBase.y;
                int textureDepth = primitive.depth;
                ushort maskBit1555 = (ushort)(maskBits >> 9);
                ulong reciprocal = BuildUnsignedReciprocal(area);
                Span<ushort> modulateR = stackalloc ushort[32];
                Span<ushort> modulateG = stackalloc ushort[32];
                Span<ushort> modulateB = stackalloc ushort[32];
                BuildModulate1555Tables(baseColor, modulateR, modulateG, modulateB);

                if (textureWindowIdentity) {
                    switch (textureDepth) {
                        case 0:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = FastDivideNonNegative(texX, area, reciprocal) & 0xFF;
                                        int texelY = FastDivideNonNegative(texY, area, reciprocal) & 0xFF;
                                        ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            vram1555Bits[pixelIndex] = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        case 1:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = FastDivideNonNegative(texX, area, reciprocal) & 0xFF;
                                        int texelY = FastDivideNonNegative(texY, area, reciprocal) & 0xFF;
                                        ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            vram1555Bits[pixelIndex] = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        default:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = FastDivideNonNegative(texX, area, reciprocal) & 0xFF;
                                        int texelY = FastDivideNonNegative(texY, area, reciprocal) & 0xFF;
                                        ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, texelX, texelY, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            vram1555Bits[pixelIndex] = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                    }
                } else {
                    switch (textureDepth) {
                        case 0:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = maskTexelAxis(FastDivideNonNegative(texX, area, reciprocal), preMaskX, postMaskX);
                                        int texelY = maskTexelAxis(FastDivideNonNegative(texY, area, reciprocal), preMaskY, postMaskY);
                                        ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            vram1555Bits[pixelIndex] = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        case 1:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = maskTexelAxis(FastDivideNonNegative(texX, area, reciprocal), preMaskX, postMaskX);
                                        int texelY = maskTexelAxis(FastDivideNonNegative(texY, area, reciprocal), preMaskY, postMaskY);
                                        ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, texelX, texelY, clutX, clutRowBase, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            vram1555Bits[pixelIndex] = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                        default:
                            for (int y = min.y; y < max.y; y++) {
                                int w0 = w0_row;
                                int w1 = w1_row;
                                int w2 = w2_row;
                                int rowBase = y << 10;
                                int texX = texXRow;
                                int texY = texYRow;

                                for (int x = min.x; x < max.x; x++) {
                                    if ((w0 | w1 | w2) >= 0) {
                                        int texelX = maskTexelAxis(FastDivideNonNegative(texX, area, reciprocal), preMaskX, postMaskX);
                                        int texelY = maskTexelAxis(FastDivideNonNegative(texY, area, reciprocal), preMaskY, postMaskY);
                                        ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, texelX, texelY, textureBaseX, textureBaseY);

                                        if (rawTexel != 0) {
                                            int pixelIndex = rowBase + x;
                                            vram1555Bits[pixelIndex] = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        }
                                    }

                                    w0 += A12;
                                    w1 += A20;
                                    w2 += A01;
                                    texX += texXStepX;
                                    texY += texYStepX;
                                }

                                w0_row += B12;
                                w1_row += B20;
                                w2_row += B01;
                                texXRow += texXStepY;
                                texYRow += texYStepY;
                            }
                            break;
                    }
                }

                return;
            }

            int genericSpanWidth = max.x - min.x;
            ulong genericReciprocal = 0;
            int genericTexXQuotientStep = 0, genericTexXRemainderStep = 0;
            int genericTexYQuotientStep = 0, genericTexYRemainderStep = 0;
            int genericShadeRQuotientStep = 0, genericShadeRRemainderStep = 0;
            int genericShadeGQuotientStep = 0, genericShadeGRemainderStep = 0;
            int genericShadeBQuotientStep = 0, genericShadeBRemainderStep = 0;

            if (genericSpanWidth > 0 && (textured || shaded)) {
                genericReciprocal = BuildUnsignedReciprocal(area);
                if (textured) {
                    ComputeFloorStep(area, texXStepX, out genericTexXQuotientStep, out genericTexXRemainderStep);
                    ComputeFloorStep(area, texYStepX, out genericTexYQuotientStep, out genericTexYRemainderStep);
                }

                if (shaded) {
                    ComputeFloorStep(area, shadeRStepX, out genericShadeRQuotientStep, out genericShadeRRemainderStep);
                    ComputeFloorStep(area, shadeGStepX, out genericShadeGQuotientStep, out genericShadeGRemainderStep);
                    ComputeFloorStep(area, shadeBStepX, out genericShadeBQuotientStep, out genericShadeBRemainderStep);
                }
            }

            for (int y = min.y; y < max.y; y++) {
                if (TryGetTriangleSpanOffsets(w0_row, w1_row, w2_row, A12, A20, A01, genericSpanWidth, out int spanStart, out int spanEnd)) {
                    int pixelIndex = (y << 10) + min.x + spanStart;

                    int texelX = 0, texelY = 0;
                    int texelXRemainder = 0, texelYRemainder = 0;
                    if (textured) {
                        int texX = texXRow + spanStart * texXStepX;
                        int texY = texYRow + spanStart * texYStepX;
                        InitScaledFloorNonNegative(texX, area, genericReciprocal, out texelX, out texelXRemainder);
                        InitScaledFloorNonNegative(texY, area, genericReciprocal, out texelY, out texelYRemainder);
                    }

                    int shadeRValue = 0, shadeGValue = 0, shadeBValue = 0;
                    int shadeRValueRemainder = 0, shadeGValueRemainder = 0, shadeBValueRemainder = 0;
                    if (shaded) {
                        int shadeR = shadeRRow + spanStart * shadeRStepX;
                        int shadeG = shadeGRow + spanStart * shadeGStepX;
                        int shadeB = shadeBRow + spanStart * shadeBStepX;
                        InitScaledFloorNonNegative(shadeR, area, genericReciprocal, out shadeRValue, out shadeRValueRemainder);
                        InitScaledFloorNonNegative(shadeG, area, genericReciprocal, out shadeGValue, out shadeGValueRemainder);
                        InitScaledFloorNonNegative(shadeB, area, genericReciprocal, out shadeBValue, out shadeBValueRemainder);
                    }

                    for (int x = spanStart; x < spanEnd; x++) {
                        if (checkMask && (vram1555Bits[pixelIndex] & 0x8000) != 0) {
                            goto AdvanceGenericTrianglePixel;
                        }

                        if (textured) {
                            int sampleX = genericTextureWindowIdentity
                                ? (texelX & 0xFF)
                                : maskTexelAxis(texelX, preMaskX, postMaskX);
                            int sampleY = genericTextureWindowIdentity
                                ? (texelY & 0xFF)
                                : maskTexelAxis(texelY, preMaskY, postMaskY);
                            ushort rawTexel = genericTextureDepth switch {
                                0 => GetTexelRaw4Fast(vram1555Bits, sampleX, sampleY, genericClutX, genericClutRowBase, genericTextureBaseX, genericTextureBaseY),
                                1 => GetTexelRaw8Fast(vram1555Bits, sampleX, sampleY, genericClutX, genericClutRowBase, genericTextureBaseX, genericTextureBaseY),
                                _ => GetTexelRaw16Fast(vram1555Bits, sampleX, sampleY, genericTextureBaseX, genericTextureBaseY)
                            };
                            if (rawTexel == 0) {
                                goto AdvanceGenericTrianglePixel;
                            }

                            ushort packedTexturedColor;
                            if (primitive.isRawTextured) {
                                packedTexturedColor = rawTexel;
                            } else if (shaded) {
                                packedTexturedColor = ModulateRawTexel1555(rawTexel, 0, shadeRValue, shadeGValue, shadeBValue);
                            } else {
                                packedTexturedColor = ModulateRawTexel1555(rawTexel, 0, genericFlatModulateR, genericFlatModulateG, genericFlatModulateB);
                            }

                            if (primitive.isSemiTransparent) {
                                WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, packedTexturedColor, genericMaskBit1555, primitive.semiTransparencyMode);
                            } else {
                                vram1555Bits[pixelIndex] = (ushort)(packedTexturedColor | genericMaskBit1555);
                            }

                            goto AdvanceGenericTrianglePixel;
                        }

                        int color = shaded
                            ? (shadeRValue << 16) | (shadeGValue << 8) | shadeBValue
                            : (int)c0;

                        ushort packedColor = PackColor1555(color);
                        if (primitive.isSemiTransparent) {
                            packedColor = BlendRawSemiTransparent1555(vram1555Bits[pixelIndex], packedColor, primitive.semiTransparencyMode);
                        }

                        if (maskBits != 0) {
                            packedColor |= 0x8000;
                        }

                        vram1555Bits[pixelIndex] = packedColor;

AdvanceGenericTrianglePixel:
                        pixelIndex++;
                        if (textured) {
                            AdvanceScaledFloor(ref texelX, ref texelXRemainder, genericTexXQuotientStep, genericTexXRemainderStep, area);
                            AdvanceScaledFloor(ref texelY, ref texelYRemainder, genericTexYQuotientStep, genericTexYRemainderStep, area);
                        }

                        if (shaded) {
                            AdvanceScaledFloor(ref shadeRValue, ref shadeRValueRemainder, genericShadeRQuotientStep, genericShadeRRemainderStep, area);
                            AdvanceScaledFloor(ref shadeGValue, ref shadeGValueRemainder, genericShadeGQuotientStep, genericShadeGRemainderStep, area);
                            AdvanceScaledFloor(ref shadeBValue, ref shadeBValueRemainder, genericShadeBQuotientStep, genericShadeBRemainderStep, area);
                        }
                    }
                }

                w0_row += B12;
                w1_row += B20;
                w2_row += B01;
                shadeRRow += shadeRStepY;
                shadeGRow += shadeGStepY;
                shadeBRow += shadeBStepY;
                texXRow += texXStepY;
                texYRow += texYStepY;
            }
        }

        private bool TryRasterizeTrianglePassthroughTexturedIdentity(
            Point2D min,
            Point2D max,
            Primitive primitive,
            int area,
            int w0Row,
            int w1Row,
            int w2Row,
            int w0StepX,
            int w1StepX,
            int w2StepX,
            int w0StepY,
            int w1StepY,
            int w2StepY,
            int texXRow,
            int texYRow,
            int texXStepX,
            int texYStepX,
            int texXStepY,
            int texYStepY,
            int semiTranspMode = -1) {
            int spanWidth = max.x - min.x;
            if (spanWidth <= 0) {
                return true;
            }

            ushort[] vram1555Bits = vram1555.Bits;
            int clutX = primitive.clut.x;
            int clutRowBase = primitive.clut.y << 10;
            int textureBaseX = primitive.textureBase.x;
            int textureBaseY = primitive.textureBase.y;
            int yStart = min.y;
            int yEnd = max.y;
            int xBase = min.x;
            ushort maskBit1555 = (ushort)(maskWhileDrawing << 15);
            ulong reciprocal = BuildUnsignedReciprocal(area);
            ComputeFloorStep(area, texXStepX, out int texXQuotientStep, out int texXRemainderStep);
            ComputeFloorStep(area, texYStepX, out int texYQuotientStep, out int texYRemainderStep);

            switch (primitive.depth) {
                case 0:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, clutX, clutRowBase, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, semiTranspMode);
                                    } else {
                                        ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                        vram1555Bits[pixelIndex] = packedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                    }
                    return true;
                case 1:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, clutX, clutRowBase, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, semiTranspMode);
                                    } else {
                                        ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                        vram1555Bits[pixelIndex] = packedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                    }
                    return true;
                default:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, semiTranspMode);
                                    } else {
                                        ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                        vram1555Bits[pixelIndex] = packedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                    }
                    return true;
            }
        }

        private bool TryRasterizeTriangleFlatTexturedIdentity(
            Point2D min,
            Point2D max,
            Primitive primitive,
            int area,
            int w0Row,
            int w1Row,
            int w2Row,
            int w0StepX,
            int w1StepX,
            int w2StepX,
            int w0StepY,
            int w1StepY,
            int w2StepY,
            int texXRow,
            int texYRow,
            int texXStepX,
            int texYStepX,
            int texXStepY,
            int texYStepY,
            int baseColor,
            int maskBits,
            int semiTranspMode = -1) {
            int spanWidth = max.x - min.x;
            if (spanWidth <= 0) {
                return true;
            }

            ushort[] vram1555Bits = vram1555.Bits;
            int clutX = primitive.clut.x;
            int clutRowBase = primitive.clut.y << 10;
            int textureBaseX = primitive.textureBase.x;
            int textureBaseY = primitive.textureBase.y;
            int yStart = min.y;
            int yEnd = max.y;
            int xBase = min.x;
            ulong reciprocal = BuildUnsignedReciprocal(area);
            ComputeFloorStep(area, texXStepX, out int texXQuotientStep, out int texXRemainderStep);
            ComputeFloorStep(area, texYStepX, out int texYQuotientStep, out int texYRemainderStep);
            ushort maskBit1555 = (ushort)(maskWhileDrawing << 15);
            Span<ushort> modulateR = stackalloc ushort[32];
            Span<ushort> modulateG = stackalloc ushort[32];
            Span<ushort> modulateB = stackalloc ushort[32];
            BuildModulate1555Tables(baseColor, modulateR, modulateG, modulateB);

            switch (primitive.depth) {
                case 0:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, clutX, clutRowBase, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, semiTranspMode);
                                    } else {
                                        vram1555Bits[pixelIndex] = modulatedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                    }
                    return true;
                case 1:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, clutX, clutRowBase, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, semiTranspMode);
                                    } else {
                                        vram1555Bits[pixelIndex] = modulatedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                    }
                    return true;
                default:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, semiTranspMode);
                                    } else {
                                        vram1555Bits[pixelIndex] = modulatedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                    }
                    return true;
            }
        }

        private bool TryRasterizeTriangleGouraudTexturedIdentity(
            Point2D min,
            Point2D max,
            Primitive primitive,
            int area,
            int w0Row,
            int w1Row,
            int w2Row,
            int w0StepX,
            int w1StepX,
            int w2StepX,
            int w0StepY,
            int w1StepY,
            int w2StepY,
            int texXRow,
            int texYRow,
            int texXStepX,
            int texYStepX,
            int texXStepY,
            int texYStepY,
            int shadeRRow,
            int shadeGRow,
            int shadeBRow,
            int shadeRStepX,
            int shadeGStepX,
            int shadeBStepX,
            int shadeRStepY,
            int shadeGStepY,
            int shadeBStepY,
            int maskBits,
            int semiTranspMode = -1) {
            if (primitive.isRawTextured) {
                return TryRasterizeTrianglePassthroughTexturedIdentity(
                    min,
                    max,
                    primitive,
                    area,
                    w0Row,
                    w1Row,
                    w2Row,
                    w0StepX,
                    w1StepX,
                    w2StepX,
                    w0StepY,
                    w1StepY,
                    w2StepY,
                    texXRow,
                    texYRow,
                    texXStepX,
                    texYStepX,
                    texXStepY,
                    texYStepY,
                    semiTranspMode);
            }

            int spanWidth = max.x - min.x;
            if (spanWidth <= 0) {
                return true;
            }

            ushort[] vram1555Bits = vram1555.Bits;
            int clutX = primitive.clut.x;
            int clutRowBase = primitive.clut.y << 10;
            int textureBaseX = primitive.textureBase.x;
            int textureBaseY = primitive.textureBase.y;
            int yStart = min.y;
            int yEnd = max.y;
            int xBase = min.x;
            ulong reciprocal = BuildUnsignedReciprocal(area);
            ComputeFloorStep(area, texXStepX, out int texXQuotientStep, out int texXRemainderStep);
            ComputeFloorStep(area, texYStepX, out int texYQuotientStep, out int texYRemainderStep);
            ComputeFloorStep(area, shadeRStepX, out int shadeRQuotientStep, out int shadeRRemainderStep);
            ComputeFloorStep(area, shadeGStepX, out int shadeGQuotientStep, out int shadeGRemainderStep);
            ComputeFloorStep(area, shadeBStepX, out int shadeBQuotientStep, out int shadeBRemainderStep);
            ushort maskBit1555 = (ushort)(maskBits >> 9);

            switch (primitive.depth) {
                case 0:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            int shadeR = shadeRRow + spanStart * shadeRStepX;
                            int shadeG = shadeGRow + spanStart * shadeGStepX;
                            int shadeB = shadeBRow + spanStart * shadeBStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);
                            InitScaledFloorNonNegative(shadeR, area, reciprocal, out int shadeRValue, out int shadeRValueRemainder);
                            InitScaledFloorNonNegative(shadeG, area, reciprocal, out int shadeGValue, out int shadeGValueRemainder);
                            InitScaledFloorNonNegative(shadeB, area, reciprocal, out int shadeBValue, out int shadeBValueRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, clutX, clutRowBase, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, shadeRValue, shadeGValue, shadeBValue);
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, semiTranspMode);
                                    } else {
                                        vram1555Bits[pixelIndex] = modulatedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                                AdvanceScaledFloor(ref shadeRValue, ref shadeRValueRemainder, shadeRQuotientStep, shadeRRemainderStep, area);
                                AdvanceScaledFloor(ref shadeGValue, ref shadeGValueRemainder, shadeGQuotientStep, shadeGRemainderStep, area);
                                AdvanceScaledFloor(ref shadeBValue, ref shadeBValueRemainder, shadeBQuotientStep, shadeBRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                        shadeRRow += shadeRStepY;
                        shadeGRow += shadeGStepY;
                        shadeBRow += shadeBStepY;
                    }
                    return true;
                case 1:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            int shadeR = shadeRRow + spanStart * shadeRStepX;
                            int shadeG = shadeGRow + spanStart * shadeGStepX;
                            int shadeB = shadeBRow + spanStart * shadeBStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);
                            InitScaledFloorNonNegative(shadeR, area, reciprocal, out int shadeRValue, out int shadeRValueRemainder);
                            InitScaledFloorNonNegative(shadeG, area, reciprocal, out int shadeGValue, out int shadeGValueRemainder);
                            InitScaledFloorNonNegative(shadeB, area, reciprocal, out int shadeBValue, out int shadeBValueRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, clutX, clutRowBase, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, shadeRValue, shadeGValue, shadeBValue);
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, semiTranspMode);
                                    } else {
                                        vram1555Bits[pixelIndex] = modulatedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                                AdvanceScaledFloor(ref shadeRValue, ref shadeRValueRemainder, shadeRQuotientStep, shadeRRemainderStep, area);
                                AdvanceScaledFloor(ref shadeGValue, ref shadeGValueRemainder, shadeGQuotientStep, shadeGRemainderStep, area);
                                AdvanceScaledFloor(ref shadeBValue, ref shadeBValueRemainder, shadeBQuotientStep, shadeBRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                        shadeRRow += shadeRStepY;
                        shadeGRow += shadeGStepY;
                        shadeBRow += shadeBStepY;
                    }
                    return true;
                default:
                    for (int y = yStart; y < yEnd; y++) {
                        if (TryGetTriangleSpanOffsets(w0Row, w1Row, w2Row, w0StepX, w1StepX, w2StepX, spanWidth, out int spanStart, out int spanEnd)) {
                            int pixelIndex = (y << 10) + xBase + spanStart;
                            int texX = texXRow + spanStart * texXStepX;
                            int texY = texYRow + spanStart * texYStepX;
                            int shadeR = shadeRRow + spanStart * shadeRStepX;
                            int shadeG = shadeGRow + spanStart * shadeGStepX;
                            int shadeB = shadeBRow + spanStart * shadeBStepX;
                            InitScaledFloorNonNegative(texX, area, reciprocal, out int texelX, out int texelXRemainder);
                            InitScaledFloorNonNegative(texY, area, reciprocal, out int texelY, out int texelYRemainder);
                            InitScaledFloorNonNegative(shadeR, area, reciprocal, out int shadeRValue, out int shadeRValueRemainder);
                            InitScaledFloorNonNegative(shadeG, area, reciprocal, out int shadeGValue, out int shadeGValueRemainder);
                            InitScaledFloorNonNegative(shadeB, area, reciprocal, out int shadeBValue, out int shadeBValueRemainder);

                            for (int x = spanStart; x < spanEnd; x++) {
                                ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, texelX & 0xFF, texelY & 0xFF, textureBaseX, textureBaseY);

                                if (rawTexel != 0) {
                                    ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, shadeRValue, shadeGValue, shadeBValue);
                                    if (semiTranspMode >= 0) {
                                        WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, semiTranspMode);
                                    } else {
                                        vram1555Bits[pixelIndex] = modulatedTexel;
                                    }
                                }

                                pixelIndex++;
                                AdvanceScaledFloor(ref texelX, ref texelXRemainder, texXQuotientStep, texXRemainderStep, area);
                                AdvanceScaledFloor(ref texelY, ref texelYRemainder, texYQuotientStep, texYRemainderStep, area);
                                AdvanceScaledFloor(ref shadeRValue, ref shadeRValueRemainder, shadeRQuotientStep, shadeRRemainderStep, area);
                                AdvanceScaledFloor(ref shadeGValue, ref shadeGValueRemainder, shadeGQuotientStep, shadeGRemainderStep, area);
                                AdvanceScaledFloor(ref shadeBValue, ref shadeBValueRemainder, shadeBQuotientStep, shadeBRemainderStep, area);
                            }
                        }

                        w0Row += w0StepY;
                        w1Row += w1StepY;
                        w2Row += w2StepY;
                        texXRow += texXStepY;
                        texYRow += texYStepY;
                        shadeRRow += shadeRStepY;
                        shadeGRow += shadeGStepY;
                        shadeBRow += shadeBStepY;
                    }
                    return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetTriangleSpanOffsets(
            int w0,
            int w1,
            int w2,
            int w0StepX,
            int w1StepX,
            int w2StepX,
            int spanWidth,
            out int spanStart,
            out int spanEnd) {
            spanStart = 0;
            spanEnd = spanWidth;

            return RestrictTriangleSpanForEdge(w0, w0StepX, spanWidth, ref spanStart, ref spanEnd) &&
                RestrictTriangleSpanForEdge(w1, w1StepX, spanWidth, ref spanStart, ref spanEnd) &&
                RestrictTriangleSpanForEdge(w2, w2StepX, spanWidth, ref spanStart, ref spanEnd) &&
                spanStart < spanEnd;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RestrictTriangleSpanForEdge(int w, int stepX, int spanWidth, ref int spanStart, ref int spanEnd) {
            if (stepX > 0) {
                spanStart = Math.Max(spanStart, CeilDivByPositive(-w, stepX));
            } else if (stepX < 0) {
                spanEnd = Math.Min(spanEnd, FloorDivByPositive(w, -stepX) + 1);
            } else if (w < 0) {
                return false;
            }

            if (spanStart < 0) {
                spanStart = 0;
            }

            if (spanEnd > spanWidth) {
                spanEnd = spanWidth;
            }

            return spanStart < spanEnd;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CeilDivByPositive(int numerator, int denominator) {
            return numerator >= 0 ? (numerator + denominator - 1) / denominator : -((-numerator) / denominator);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FloorDivByPositive(int numerator, int denominator) {
            return numerator >= 0 ? numerator / denominator : -(((-numerator) + denominator - 1) / denominator);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong BuildUnsignedReciprocal(int divisor) {
            return ((1UL << 32) + (uint)divisor - 1UL) / (uint)divisor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InitScaledFloorNonNegative(int numerator, int divisor, ulong reciprocal, out int quotient, out int remainder) {
            quotient = FastDivideNonNegative(numerator, divisor, reciprocal);
            remainder = numerator - quotient * divisor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ComputeFloorStep(int divisor, int step, out int quotientStep, out int remainderStep) {
            quotientStep = FloorDivByPositive(step, divisor);
            remainderStep = step - quotientStep * divisor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AdvanceScaledFloor(ref int quotient, ref int remainder, int quotientStep, int remainderStep, int divisor) {
            quotient += quotientStep;
            remainder += remainderStep;

            if (remainder >= divisor) {
                quotient++;
                remainder -= divisor;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ModulateRawTexel1555(ushort rawTexel, ushort maskBit1555, Span<ushort> modulateR, Span<ushort> modulateG, Span<ushort> modulateB) {
            return (ushort)(
                (rawTexel & 0x8000) |
                maskBit1555 |
                modulateR[rawTexel & 0x1F] |
                modulateG[(rawTexel >> 5) & 0x1F] |
                modulateB[(rawTexel >> 10) & 0x1F]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ModulateRawTexel1555(ushort rawTexel, ushort maskBit1555, int shadeR, int shadeG, int shadeB) {
            return (ushort)(
                (rawTexel & 0x8000) |
                maskBit1555 |
                modulate1555RByShade[(shadeR << 5) | (rawTexel & 0x1F)] |
                modulate1555GByShade[(shadeG << 5) | ((rawTexel >> 5) & 0x1F)] |
                modulate1555BByShade[(shadeB << 5) | ((rawTexel >> 10) & 0x1F)]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteSemiTransparentTexturedRawPixel(ushort[] vram1555Bits, int pixelIndex, ushort frontRaw, ushort maskBit1555, int semiTranspMode) {
            bool shouldBlend = (frontRaw & 0x8000) != 0;
            frontRaw |= maskBit1555;
            if (shouldBlend) {
                frontRaw = BlendRawSemiTransparent1555(vram1555Bits[pixelIndex], frontRaw, semiTranspMode);
            }

            vram1555Bits[pixelIndex] = frontRaw;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort BlendRawSemiTransparent1555(ushort backRaw, ushort frontRaw, int semiTranspMode) {
            if (semiTranspMode == 0) {
                int rb = (((backRaw & 0x7C1F) + (frontRaw & 0x7C1F)) >> 1) & 0x7C1F;
                int gBits = (((backRaw & 0x03E0) + (frontRaw & 0x03E0)) >> 1) & 0x03E0;
                return (ushort)((frontRaw & 0x8000) | rb | gBits);
            }

            int rIndex = ((backRaw & 0x1F) << 5) | (frontRaw & 0x1F);
            int gIndex = (((backRaw >> 5) & 0x1F) << 5) | ((frontRaw >> 5) & 0x1F);
            int bIndex = (((backRaw >> 10) & 0x1F) << 5) | ((frontRaw >> 10) & 0x1F);

            switch (semiTranspMode) {
                case 1:
                    return (ushort)(
                        (frontRaw & 0x8000) |
                        blend1555AddR[rIndex] |
                        blend1555AddG[gIndex] |
                        blend1555AddB[bIndex]);
                case 2:
                    return (ushort)(
                        (frontRaw & 0x8000) |
                        blend1555SubR[rIndex] |
                        blend1555SubG[gIndex] |
                        blend1555SubB[bIndex]);
                case 3:
                    return (ushort)(
                        (frontRaw & 0x8000) |
                        blend1555QuarterAddR[rIndex] |
                        blend1555QuarterAddG[gIndex] |
                        blend1555QuarterAddB[bIndex]);
                default:
                    return frontRaw;
            }
        }

        private static ushort[] BuildDynamicModulate1555Table(int shift) {
            ushort[] table = new ushort[256 * 32];

            for (int shade = 0; shade < 256; shade++) {
                int shadeBase = shade << 5;
                for (int texel = 0; texel < 32; texel++) {
                    int texel8 = (texel << 3) | (texel >> 2);
                    int modulated = clampToFF((shade * texel8) >> 7) >> 3;
                    table[shadeBase | texel] = (ushort)(modulated << shift);
                }
            }

            return table;
        }

        private static ushort[] BuildSemiTransparentBlend1555Table(int shift, int semiTranspMode) {
            ushort[] table = new ushort[32 * 32];

            for (int back = 0; back < 32; back++) {
                int rowBase = back << 5;
                for (int front = 0; front < 32; front++) {
                    int blended = semiTranspMode switch {
                        1 => Math.Min(0x1F, back + front),
                        2 => Math.Max(0, back - front),
                        3 => Math.Min(0x1F, back + (front >> 2)),
                        _ => front
                    };

                    table[rowBase | front] = (ushort)(blended << shift);
                }
            }

            return table;
        }

        private static void BuildModulate1555Tables(int baseColor, Span<ushort> modulateR, Span<ushort> modulateG, Span<ushort> modulateB) {
            int baseR = (baseColor >> 16) & 0xFF;
            int baseG = (baseColor >> 8) & 0xFF;
            int baseB = baseColor & 0xFF;

            for (int i = 0; i < 32; i++) {
                int texel8 = (i << 3) | (i >> 2);
                modulateR[i] = (ushort)(clampToFF((baseR * texel8) >> 7) >> 3);
                modulateG[i] = (ushort)((clampToFF((baseG * texel8) >> 7) >> 3) << 5);
                modulateB[i] = (ushort)((clampToFF((baseB * texel8) >> 7) >> 3) << 10);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastDivideNonNegative(int numerator, int divisor, ulong reciprocal) {
            uint unsignedNumerator = (uint)numerator;
            uint unsignedDivisor = (uint)divisor;
            uint quotient = (uint)(((ulong)unsignedNumerator * reciprocal) >> 32);

            if ((ulong)quotient * unsignedDivisor > unsignedNumerator) {
                quotient--;
            } else if ((ulong)(quotient + 1U) * unsignedDivisor <= unsignedNumerator) {
                quotient++;
            }

            return (int)quotient;
        }

        private void GP0_RenderLine(Span<uint> buffer) {
            //Console.WriteLine("size " + commandBuffer.Count);
            //int arguments = 0;
            uint command = buffer[pointer++];
            //arguments++;

            uint color1 = (uint)GetRgbColor(command);
            uint color2 = color1;

            bool isPoly = (command & (1 << 27)) != 0;
            bool isShaded = (command & (1 << 28)) != 0;
            bool isTransparent = (command & (1 << 25)) != 0;

            //if (isTextureMapped /*isRaw*/) return;

            uint v1 = buffer[pointer++];
            //arguments++;

            if (isShaded) {
                color2 = buffer[pointer++];
                //arguments++;
            }
            uint v2 = buffer[pointer++];
            //arguments++;

            rasterizeLine(v1, v2, color1, color2, isTransparent);

            if (!isPoly) return;
            //renderline = 0;
            while (/*arguments < 0xF &&*/ (buffer[pointer] & 0xF000_F000) != 0x5000_5000) {
                //Console.WriteLine("DOING ANOTHER LINE " + ++renderline);
                //arguments++;
                color1 = color2;
                if (isShaded) {
                    color2 = buffer[pointer++];
                    //arguments++;
                }
                v1 = v2;
                v2 = buffer[pointer++];
                rasterizeLine(v1, v2, color1, color2, isTransparent);
                //Console.WriteLine("RASTERIZE " + ++rasterizeline);
                //window.update(VRAM.Bits);
                //Console.ReadLine();
            }

            /*if (arguments != 0xF) */
            pointer++; // discard 5555_5555 termination (need to rewrite all this from the GP0...)
        }

        private void rasterizeLine(uint v1, uint v2, uint color1, uint color2, bool isTransparent) {
            short x = signed11bit(v1 & 0xFFFF);
            short y = signed11bit(v1 >> 16);

            short x2 = signed11bit(v2 & 0xFFFF);
            short y2 = signed11bit(v2 >> 16);

            if (Math.Abs(x - x2) > 0x3FF || Math.Abs(y - y2) > 0x1FF) return;

            x += drawingXOffset;
            y += drawingYOffset;

            x2 += drawingXOffset;
            y2 += drawingYOffset;

            int w = x2 - x;
            int h = y2 - y;
            int dx1 = 0, dy1 = 0, dx2 = 0, dy2 = 0;

            if (w < 0) dx1 = -1; else if (w > 0) dx1 = 1;
            if (h < 0) dy1 = -1; else if (h > 0) dy1 = 1;
            if (w < 0) dx2 = -1; else if (w > 0) dx2 = 1;

            int longest = Math.Abs(w);
            int shortest = Math.Abs(h);

            if (!(longest > shortest)) {
                longest = Math.Abs(h);
                shortest = Math.Abs(w);
                if (h < 0) dy2 = -1; else if (h > 0) dy2 = 1;
                dx2 = 0;
            }

            int numerator = longest >> 1;

            for (int i = 0; i <= longest; i++) {
                float ratio = (float)i / longest;
                int color = interpolate(color1, color2, ratio);

                //x = (short)Math.Min(Math.Max(x, drawingAreaLeft), drawingAreaRight); //this generates glitches on RR4
                //y = (short)Math.Min(Math.Max(y, drawingAreaTop), drawingAreaBottom);

                if (x >= drawingAreaLeft && x < drawingAreaRight && y >= drawingAreaTop && y < drawingAreaBottom) {
                    ushort packedColor = PackColor1555(color);
                    if (isTransparent) {
                        packedColor = BlendRawSemiTransparent1555(vram1555.GetPixel(x, y), packedColor, transparencyMode);
                    }

                    if (maskWhileDrawing != 0) {
                        packedColor |= 0x8000;
                    }

                    vram1555.SetPixel(x, y, packedColor);
                }

                numerator += shortest;
                if (!(numerator < longest)) {
                    numerator -= longest;
                    x += (short)dx1;
                    y += (short)dy1;
                } else {
                    x += (short)dx2;
                    y += (short)dy2;
                }
            }
            //Console.ReadLine();
        }

        private void GP0_RenderRectangle(Span<uint> buffer) {
            //1st Color+Command(CcBbGgRrh)
            //2nd Vertex(YyyyXxxxh)
            //3rd Texcoord+Palette(ClutYyXxh)(for 4bpp Textures Xxh must be even!) //Only textured
            //4rd (3rd non textured) Width + Height(YsizXsizh)(variable opcode only)(max 1023x511)
            uint command = buffer[pointer++];
            uint color = command & 0xFFFFFF;
            uint opcode = command >> 24;

            bool isTextured = (command & (1 << 26)) != 0;
            bool isSemiTransparent = (command & (1 << 25)) != 0;
            bool isRawTextured = (command & (1 << 24)) != 0;

            Primitive primitive = new Primitive();
            primitive.isTextured = isTextured;
            primitive.isSemiTransparent = isSemiTransparent;
            primitive.isRawTextured = isRawTextured;

            uint vertex = buffer[pointer++];
            short xo = (short)(vertex & 0xFFFF);
            short yo = (short)(vertex >> 16);

            if (isTextured) {
                uint texture = buffer[pointer++];
                textureData.val = (ushort)texture;

                ushort palette = (ushort)((texture >> 16) & 0xFFFF);
                primitive.clut.x = (short)((palette & 0x3f) << 4);
                primitive.clut.y = (short)((palette >> 6) & 0x1FF);
            }

            primitive.depth = textureDepth;
            primitive.textureBase.x = (short)(textureXBase << 6);
            primitive.textureBase.y = (short)(textureYBase << 8);
            primitive.semiTransparencyMode = transparencyMode;

            short width;
            short heigth;

            uint type = (opcode & 0x18) >> 3;

            if (type == 0) {
                uint hw = buffer[pointer++];
                width = (short)(hw & 0xFFFF);
                heigth = (short)(hw >> 16);
            } else if (type == 1) {
                width = 1; heigth = 1;
            } else if (type == 2) {
                width = 8; heigth = 8;
            } else {
                width = 16; heigth = 16;
            }

            short y = (short)(yo + drawingYOffset);
            short x = (short)(xo + drawingXOffset);

            Point2D origin;
            origin.x = x;
            origin.y = y;

            Point2D size;
            size.x = (short)(x + width);
            size.y = (short)(y + heigth);

            rasterizeRect(origin, size, textureData, color, primitive);
        }

        private void rasterizeRect(Point2D origin, Point2D size, TextureData texture, uint bgrColor, Primitive primitive) {
            int xOrigin = Math.Max(origin.x, drawingAreaLeft);
            int yOrigin = Math.Max(origin.y, drawingAreaTop);
            int width = Math.Min(size.x, drawingAreaRight + 1);
            int height = Math.Min(size.y, drawingAreaBottom + 1);
            if (xOrigin >= width || yOrigin >= height) {
                return;
            }

            int rectWidth = size.x - origin.x;
            int rectHeight = size.y - origin.y;
            bool flipX = isTexturedRectangleXFlipped;
            bool flipY = isTexturedRectangleYFlipped;

            int baseColor = GetRgbColor(bgrColor);
            ushort[] vram1555Bits = vram1555.Bits;
            bool checkMask = checkMaskBeforeDraw;
            int maskBits = maskWhileDrawing << 24;
            ushort genericMaskBit1555 = (ushort)(maskWhileDrawing << 15);
            ushort genericPackedBaseColor = PackColor1555(baseColor);
            bool flatOpaqueFill = !primitive.isTextured && !primitive.isSemiTransparent;
            bool texturedFastPath = primitive.isTextured && !checkMask;
            bool passthroughTexturedFastPath =
                texturedFastPath &&
                (primitive.isRawTextured || (baseColor & 0x00FF_FFFF) == IdentityTextureModulationColor);
            int genericClutX = primitive.clut.x;
            int genericClutRowBase = primitive.clut.y << 10;
            int genericTextureBaseX = primitive.textureBase.x;
            int genericTextureBaseY = primitive.textureBase.y;
            int genericTextureDepth = primitive.depth;
            bool genericTextureWindowIdentity = textureWindowIdentity;
            Span<ushort> genericRectModulateR = stackalloc ushort[32];
            Span<ushort> genericRectModulateG = stackalloc ushort[32];
            Span<ushort> genericRectModulateB = stackalloc ushort[32];

            if (primitive.isTextured && !primitive.isRawTextured) {
                BuildModulate1555Tables(baseColor, genericRectModulateR, genericRectModulateG, genericRectModulateB);
            }

            if (flatOpaqueFill) {
                int fillColor = baseColor | maskBits;
                ushort fillColor1555 = PackColor1555(fillColor);
                int rowWidth = width - xOrigin;

                if (!checkMask) {
                    for (int y = yOrigin; y < height; y++) {
                        int rowBase = (y << 10) + xOrigin;
                        Array.Fill(vram1555Bits, fillColor1555, rowBase, rowWidth);
                    }
                } else {
                    for (int y = yOrigin; y < height; y++) {
                        int rowBase = y << 10;
                        for (int x = xOrigin; x < width; x++) {
                            int pixelIndex = rowBase + x;
                            if ((vram1555Bits[pixelIndex] & 0x8000) != 0) {
                                continue;
                            }

                            vram1555Bits[pixelIndex] = fillColor1555;
                        }
                    }
                }

                return;
            }

            if (passthroughTexturedFastPath) {
                int clutX = primitive.clut.x;
                int clutRowBase = primitive.clut.y << 10;
                int textureBaseX = primitive.textureBase.x;
                int textureBaseY = primitive.textureBase.y;
                int textureDepth = primitive.depth;
                ushort maskBit1555 = (ushort)(maskWhileDrawing << 15);

                if (textureWindowIdentity) {
                    switch (textureDepth) {
                        case 0:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int wrappedV = (texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY)) & 0xFF;
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, u & 0xFF, wrappedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, primitive.semiTransparencyMode);
                                        } else {
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        case 1:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int wrappedV = (texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY)) & 0xFF;
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, u & 0xFF, wrappedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, primitive.semiTransparencyMode);
                                        } else {
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        default:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int wrappedV = (texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY)) & 0xFF;
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, u & 0xFF, wrappedV, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, primitive.semiTransparencyMode);
                                        } else {
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                    }
                } else {
                    switch (textureDepth) {
                        case 0:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int maskedV = maskTexelAxis(texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY), preMaskY, postMaskY);
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, maskTexelAxis(u, preMaskX, postMaskX), maskedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, primitive.semiTransparencyMode);
                                        } else {
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        case 1:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int maskedV = maskTexelAxis(texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY), preMaskY, postMaskY);
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, maskTexelAxis(u, preMaskX, postMaskX), maskedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, primitive.semiTransparencyMode);
                                        } else {
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        default:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int maskedV = maskTexelAxis(texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY), preMaskY, postMaskY);
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, maskTexelAxis(u, preMaskX, postMaskX), maskedV, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, rawTexel, maskBit1555, primitive.semiTransparencyMode);
                                        } else {
                                            ushort packedTexel = (ushort)(rawTexel | maskBit1555);
                                            vram1555Bits[pixelIndex] = packedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                    }
                }

                return;
            }

            if (texturedFastPath) {
                int clutX = primitive.clut.x;
                int clutRowBase = primitive.clut.y << 10;
                int textureBaseX = primitive.textureBase.x;
                int textureBaseY = primitive.textureBase.y;
                int textureDepth = primitive.depth;
                ushort maskBit1555 = (ushort)(maskWhileDrawing << 15);
                Span<ushort> modulateR = stackalloc ushort[32];
                Span<ushort> modulateG = stackalloc ushort[32];
                Span<ushort> modulateB = stackalloc ushort[32];
                BuildModulate1555Tables(baseColor, modulateR, modulateG, modulateB);

                if (textureWindowIdentity) {
                    switch (textureDepth) {
                        case 0:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int wrappedV = (texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY)) & 0xFF;
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, u & 0xFF, wrappedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, primitive.semiTransparencyMode);
                                        } else {
                                            vram1555Bits[pixelIndex] = modulatedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        case 1:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int wrappedV = (texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY)) & 0xFF;
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, u & 0xFF, wrappedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, primitive.semiTransparencyMode);
                                        } else {
                                            vram1555Bits[pixelIndex] = modulatedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        default:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int wrappedV = (texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY)) & 0xFF;
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, u & 0xFF, wrappedV, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, primitive.semiTransparencyMode);
                                        } else {
                                            vram1555Bits[pixelIndex] = modulatedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                    }
                } else {
                    switch (textureDepth) {
                        case 0:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int maskedV = maskTexelAxis(texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY), preMaskY, postMaskY);
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw4Fast(vram1555Bits, maskTexelAxis(u, preMaskX, postMaskX), maskedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, primitive.semiTransparencyMode);
                                        } else {
                                            vram1555Bits[pixelIndex] = modulatedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        case 1:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int maskedV = maskTexelAxis(texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY), preMaskY, postMaskY);
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw8Fast(vram1555Bits, maskTexelAxis(u, preMaskX, postMaskX), maskedV, clutX, clutRowBase, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, primitive.semiTransparencyMode);
                                        } else {
                                            vram1555Bits[pixelIndex] = modulatedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                        default:
                            for (int y = yOrigin; y < height; y++) {
                                int rowBase = y << 10;
                                int sourceY = y - origin.y;
                                int maskedV = maskTexelAxis(texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY), preMaskY, postMaskY);
                                int sourceX = xOrigin - origin.x;
                                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                                int uStep = flipX ? -1 : 1;

                                for (int x = xOrigin; x < width; x++) {
                                    ushort rawTexel = GetTexelRaw16Fast(vram1555Bits, maskTexelAxis(u, preMaskX, postMaskX), maskedV, textureBaseX, textureBaseY);
                                    if (rawTexel != 0) {
                                        int pixelIndex = rowBase + x;
                                        ushort modulatedTexel = ModulateRawTexel1555(rawTexel, maskBit1555, modulateR, modulateG, modulateB);
                                        if (primitive.isSemiTransparent) {
                                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, modulatedTexel, 0, primitive.semiTransparencyMode);
                                        } else {
                                            vram1555Bits[pixelIndex] = modulatedTexel;
                                        }
                                    }

                                    u += uStep;
                                }
                            }
                            break;
                    }
                }

                return;
            }

            for (int y = yOrigin; y < height; y++) {
                int rowBase = y << 10;
                int sourceY = y - origin.y;
                int v = texture.y + (flipY ? (rectHeight - 1 - sourceY) : sourceY);
                int sampleV = genericTextureWindowIdentity
                    ? (v & 0xFF)
                    : maskTexelAxis(v, preMaskY, postMaskY);
                int sourceX = xOrigin - origin.x;
                int u = texture.x + (flipX ? (rectWidth - 1 - sourceX) : sourceX);
                int uStep = flipX ? -1 : 1;

                for (int x = xOrigin; x < width; x++) {
                    int pixelIndex = rowBase + x;
                    //Check background mask
                    if (checkMask && (vram1555Bits[pixelIndex] & 0x8000) != 0) {
                        u += uStep;
                        continue;
                    }

                    if (primitive.isTextured) {
                        int sampleX = genericTextureWindowIdentity
                            ? (u & 0xFF)
                            : maskTexelAxis(u, preMaskX, postMaskX);
                        ushort rawTexel = genericTextureDepth switch {
                            0 => GetTexelRaw4Fast(vram1555Bits, sampleX, sampleV, genericClutX, genericClutRowBase, genericTextureBaseX, genericTextureBaseY),
                            1 => GetTexelRaw8Fast(vram1555Bits, sampleX, sampleV, genericClutX, genericClutRowBase, genericTextureBaseX, genericTextureBaseY),
                            _ => GetTexelRaw16Fast(vram1555Bits, sampleX, sampleV, genericTextureBaseX, genericTextureBaseY)
                        };
                        if (rawTexel == 0) {
                            u += uStep;
                            continue;
                        }

                        ushort packedTexturedColor = primitive.isRawTextured
                            ? rawTexel
                            : ModulateRawTexel1555(rawTexel, 0, genericRectModulateR, genericRectModulateG, genericRectModulateB);

                        if (primitive.isSemiTransparent) {
                            WriteSemiTransparentTexturedRawPixel(vram1555Bits, pixelIndex, packedTexturedColor, genericMaskBit1555, primitive.semiTransparencyMode);
                        } else {
                            vram1555Bits[pixelIndex] = (ushort)(packedTexturedColor | genericMaskBit1555);
                        }

                        u += uStep;
                        continue;
                    }

                    ushort packedColor = genericPackedBaseColor;
                    if (primitive.isSemiTransparent) {
                        packedColor = BlendRawSemiTransparent1555(vram1555Bits[pixelIndex], packedColor, primitive.semiTransparencyMode);
                    }

                    if (maskBits != 0) {
                        packedColor |= 0x8000;
                    }

                    vram1555Bits[pixelIndex] = packedColor;
                    u += uStep;
                }

            }
        }

        private void GP0_MemCopyRectVRAMtoVRAM(Span<uint> buffer) {
            pointer++; //Command/Color parameter unused
            uint sourceXY = buffer[pointer++];
            uint destinationXY = buffer[pointer++];
            uint wh = buffer[pointer++];

            ushort sx = (ushort)(sourceXY & 0x3FF);
            ushort sy = (ushort)((sourceXY >> 16) & 0x1FF);

            ushort dx = (ushort)(destinationXY & 0x3FF);
            ushort dy = (ushort)((destinationXY >> 16) & 0x1FF);

            ushort w = (ushort)((((wh & 0xFFFF) - 1) & 0x3FF) + 1);
            ushort h = (ushort)((((wh >> 16) - 1) & 0x1FF) + 1);

            // VRAM blits must copy raw 16-bit VRAM words, not the expanded RGB view.
            // Text/CLUT pages are stored as packed indices and get corrupted otherwise.
            int copyLength = w * h;
            EnsureVramCopyScratchCapacity(copyLength);
            ushort[] copyBuffer = vramCopyScratch;
            int copyIndex = 0;
            for (int yPos = 0; yPos < h; yPos++) {
                for (int xPos = 0; xPos < w; xPos++) {
                    copyBuffer[copyIndex++] = vram1555.GetPixel((sx + xPos) & 0x3FF, (sy + yPos) & 0x1FF);
                }
            }

            copyIndex = 0;
            for (int yPos = 0; yPos < h; yPos++) {
                for (int xPos = 0; xPos < w; xPos++) {
                    ushort rawColor = copyBuffer[copyIndex++];
                    if (checkMaskBeforeDraw) {
                        ushort destColor = vram1555.GetPixel((dx + xPos) & 0x3FF, (dy + yPos) & 0x1FF);
                        if ((destColor & 0x8000) != 0) continue;
                    }

                    rawColor |= (ushort)(maskWhileDrawing << 15);
                    vram1555.SetPixel((dx + xPos) & 0x3FF, (dy + yPos) & 0x1FF, rawColor);
                }
            }
        }

        private void EnsureVramCopyScratchCapacity(int requiredLength) {
            if (vramCopyScratch.Length < requiredLength) {
                vramCopyScratch = new ushort[requiredLength];
            }
        }

        private void GP0_MemCopyRectCPUtoVRAM(Span<uint> buffer) { //todo rewrite VRAM coord struct mess
            pointer++; //Command/Color parameter unused
            uint yx = buffer[pointer++];
            uint wh = buffer[pointer++];

            ushort x = (ushort)(yx & 0x3FF);
            ushort y = (ushort)((yx >> 16) & 0x1FF);

            ushort w = (ushort)((((wh & 0xFFFF) - 1) & 0x3FF) + 1);
            ushort h = (ushort)((((wh >> 16) - 1) & 0x1FF) + 1);

            vramTransfer.x = x;
            vramTransfer.y = y;
            vramTransfer.w = w;
            vramTransfer.h = h;
            vramTransfer.origin_x = x;
            vramTransfer.origin_y = y;
            vramTransfer.halfWords = w * h;

            mode = Mode.VRAM;
        }

        private void GP0_MemCopyRectVRAMtoCPU(Span<uint> buffer) {
            pointer++; //Command/Color parameter unused
            uint yx = buffer[pointer++];
            uint wh = buffer[pointer++];

            ushort x = (ushort)(yx & 0x3FF);
            ushort y = (ushort)((yx >> 16) & 0x1FF);

            ushort w = (ushort)((((wh & 0xFFFF) - 1) & 0x3FF) + 1);
            ushort h = (ushort)((((wh >> 16) - 1) & 0x1FF) + 1);

            vramTransfer.x = x;
            vramTransfer.y = y;
            vramTransfer.w = w;
            vramTransfer.h = h;
            vramTransfer.origin_x = x;
            vramTransfer.origin_y = y;
            vramTransfer.halfWords = w * h;

            isReadyToSendVRAMToCPU = true;
            isReadyToReceiveDMABlock = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int maskTexelAxis(int axis, int preMaskAxis, int postMaskAxis) {
            return axis & 0xFF & preMaskAxis | postMaskAxis;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int getTexel(int x, int y, Point2D clut, Point2D textureBase, int depth) {
            return GetTexelFast(
                vram1555.Bits,
                color1555to8888LUT,
                x,
                y,
                clut.x,
                clut.y << 10,
                textureBase.x,
                textureBase.y,
                depth);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetTexelFast(
            ushort[] vram1555Bits,
            int[] color1555to8888LUT,
            int x,
            int y,
            int clutX,
            int clutRowBase,
            int textureBaseX,
            int textureBaseY,
            int depth) {
            return depth switch {
                0 => GetTexel4Fast(vram1555Bits, color1555to8888LUT, x, y, clutX, clutRowBase, textureBaseX, textureBaseY),
                1 => GetTexel8Fast(vram1555Bits, color1555to8888LUT, x, y, clutX, clutRowBase, textureBaseX, textureBaseY),
                _ => GetTexel16Fast(vram1555Bits, color1555to8888LUT, x, y, textureBaseX, textureBaseY)
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GetTexelRawFast(
            ushort[] vram1555Bits,
            int x,
            int y,
            int clutX,
            int clutRowBase,
            int textureBaseX,
            int textureBaseY,
            int depth) {
            return depth switch {
                0 => GetTexelRaw4Fast(vram1555Bits, x, y, clutX, clutRowBase, textureBaseX, textureBaseY),
                1 => GetTexelRaw8Fast(vram1555Bits, x, y, clutX, clutRowBase, textureBaseX, textureBaseY),
                _ => GetTexelRaw16Fast(vram1555Bits, x, y, textureBaseX, textureBaseY)
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetTexel4Fast(
            ushort[] vram1555Bits,
            int[] color1555to8888LUT,
            int x,
            int y,
            int clutX,
            int clutRowBase,
            int textureBaseX,
            int textureBaseY) {
            int textureRowBase = (y + textureBaseY) << 10;
            return color1555to8888LUT[vram1555Bits[clutRowBase + clutX + ((vram1555Bits[textureRowBase + textureBaseX + (x >> 2)] >> ((x & 3) << 2)) & 0xF)]];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetTexel8Fast(
            ushort[] vram1555Bits,
            int[] color1555to8888LUT,
            int x,
            int y,
            int clutX,
            int clutRowBase,
            int textureBaseX,
            int textureBaseY) {
            int textureRowBase = (y + textureBaseY) << 10;
            return color1555to8888LUT[vram1555Bits[clutRowBase + clutX + ((vram1555Bits[textureRowBase + textureBaseX + (x >> 1)] >> ((x & 1) << 3)) & 0xFF)]];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetTexel16Fast(
            ushort[] vram1555Bits,
            int[] color1555to8888LUT,
            int x,
            int y,
            int textureBaseX,
            int textureBaseY) {
            return color1555to8888LUT[vram1555Bits[((y + textureBaseY) << 10) + textureBaseX + x]];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GetTexelRaw4Fast(
            ushort[] vram1555Bits,
            int x,
            int y,
            int clutX,
            int clutRowBase,
            int textureBaseX,
            int textureBaseY) {
            int textureRowBase = (y + textureBaseY) << 10;
            return vram1555Bits[clutRowBase + clutX + ((vram1555Bits[textureRowBase + textureBaseX + (x >> 2)] >> ((x & 3) << 2)) & 0xF)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GetTexelRaw8Fast(
            ushort[] vram1555Bits,
            int x,
            int y,
            int clutX,
            int clutRowBase,
            int textureBaseX,
            int textureBaseY) {
            int textureRowBase = (y + textureBaseY) << 10;
            return vram1555Bits[clutRowBase + clutX + ((vram1555Bits[textureRowBase + textureBaseX + (x >> 1)] >> ((x & 1) << 3)) & 0xFF)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GetTexelRaw16Fast(
            ushort[] vram1555Bits,
            int x,
            int y,
            int textureBaseX,
            int textureBaseY) {
            return vram1555Bits[((y + textureBaseY) << 10) + textureBaseX + x];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int get4bppTexel(int x, int y, Point2D clut, Point2D textureBase) {
            ushort index = vram1555.GetPixel(x / 4 + textureBase.x, y + textureBase.y);
            int p = (index >> (x & 3) * 4) & 0xF;
            return color1555to8888LUT[vram1555.GetPixel(clut.x + p, clut.y)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int get8bppTexel(int x, int y, Point2D clut, Point2D textureBase) {
            ushort index = vram1555.GetPixel(x / 2 + textureBase.x, y + textureBase.y);
            int p = (index >> (x & 1) * 8) & 0xFF;
            return color1555to8888LUT[vram1555.GetPixel(clut.x + p, clut.y)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int get16bppTexel(int x, int y, Point2D textureBase) {
            return color1555to8888LUT[vram1555.GetPixel(x + textureBase.x, y + textureBase.y)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int orient2d(Point2D a, Point2D b, Point2D c) {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private void GP0_E1_SetDrawMode(uint val) {
            uint bits = val & 0xFF_FFFF;

            if (bits == drawModeBits) return;

            drawModeBits = bits;

            textureXBase = (byte)(val & 0xF);
            textureYBase = (byte)((val >> 4) & 0x1);
            transparencyMode = (byte)((val >> 5) & 0x3);
            textureDepth = (byte)((val >> 7) & 0x3);
            isDithered = ((val >> 9) & 0x1) != 0;
            isDrawingToDisplayAllowed = ((val >> 10) & 0x1) != 0;
            isTextureDisabled = isTextureDisabledAllowed && ((val >> 11) & 0x1) != 0;
            isTexturedRectangleXFlipped = ((val >> 12) & 0x1) != 0;
            isTexturedRectangleYFlipped = ((val >> 13) & 0x1) != 0;

            //Console.WriteLine("[GPU] [GP0] DrawMode " + val.ToString("x8"));
        }

        private void GP0_E2_SetTextureWindow(uint val) {
            uint bits = val & 0xFF_FFFF;

            if (bits == textureWindowBits) return;

            textureWindowBits = bits;

            byte textureWindowMaskX = (byte)(val & 0x1F);
            byte textureWindowMaskY = (byte)((val >> 5) & 0x1F);
            byte textureWindowOffsetX = (byte)((val >> 10) & 0x1F);
            byte textureWindowOffsetY = (byte)((val >> 15) & 0x1F);

            preMaskX = ~(textureWindowMaskX * 8);
            preMaskY = ~(textureWindowMaskY * 8);
            postMaskX = (textureWindowOffsetX & textureWindowMaskX) * 8;
            postMaskY = (textureWindowOffsetY & textureWindowMaskY) * 8;
            textureWindowIdentity = textureWindowMaskX == 0 && textureWindowMaskY == 0;
        }

        private void GP0_E3_SetDrawingAreaTopLeft(uint val) {
            drawingAreaTop = (ushort)((val >> 10) & 0x1FF);
            drawingAreaLeft = (ushort)(val & 0x3FF);
        }

        private void GP0_E4_SetDrawingAreaBottomRight(uint val) {
            drawingAreaBottom = (ushort)((val >> 10) & 0x1FF);
            drawingAreaRight = (ushort)(val & 0x3FF);
        }

        private void GP0_E5_SetDrawingOffset(uint val) {
            drawingXOffset = signed11bit(val & 0x7FF);
            drawingYOffset = signed11bit((val >> 11) & 0x7FF);
        }

        private void GP0_E6_SetMaskBit(uint val) {
            maskWhileDrawing = (int)(val & 0x1);
            checkMaskBeforeDraw = (val & 0x2) != 0;
        }

        public void writeGP1(uint value) {
            //Console.WriteLine($"[GPU] GP1 Write Value: {value:x8}");
            uint opcode = value >> 24;
            switch (opcode) {
                case 0x00: GP1_00_ResetGPU(); break;
                case 0x01: GP1_01_ResetCommandBuffer(); break;
                case 0x02: GP1_02_AckGPUInterrupt(); break;
                case 0x03: GP1_03_DisplayEnable(value); break;
                case 0x04: GP1_04_DMADirection(value); break;
                case 0x05: GP1_05_DisplayVRAMStart(value); break;
                case 0x06: GP1_06_DisplayHorizontalRange(value); break;
                case 0x07: GP1_07_DisplayVerticalRange(value); break;
                case 0x08: GP1_08_DisplayMode(value); break;
                case 0x09: GP1_09_TextureDisable(value); break;
                case uint _ when opcode >= 0x10 && opcode <= 0x1F:
                    GP1_GPUInfo(value); break;
                default: Console.WriteLine("[GPU] Unsupported GP1 Command " + opcode.ToString("x8")); Console.ReadLine(); break;
            }
        }

        private void GP1_00_ResetGPU() {
            GP1_01_ResetCommandBuffer();
            GP1_02_AckGPUInterrupt();
            GP1_03_DisplayEnable(1);
            GP1_04_DMADirection(0);
            GP1_05_DisplayVRAMStart(0);
            GP1_06_DisplayHorizontalRange(0xC00200);
            GP1_07_DisplayVerticalRange(0x040010);
            GP1_08_DisplayMode(0);

            GP0_E1_SetDrawMode(0);
            GP0_E2_SetTextureWindow(0);
            GP0_E3_SetDrawingAreaTopLeft(0);
            GP0_E4_SetDrawingAreaBottomRight(0);
            GP0_E5_SetDrawingOffset(0);
            GP0_E6_SetMaskBit(0);
        }

        private void GP1_01_ResetCommandBuffer() => pointer = 0;

        private void GP1_02_AckGPUInterrupt() => isInterruptRequested = false;

        private void GP1_03_DisplayEnable(uint value) => isDisplayDisabled = (value & 1) != 0;

        private void GP1_04_DMADirection(uint value) {
            dmaDirection = (byte)(value & 0x3);

            isDmaRequest = dmaDirection switch {
                0 => false,
                1 => isReadyToReceiveDMABlock,
                2 => isReadyToReceiveDMABlock,
                3 => isReadyToSendVRAMToCPU,
                _ => false,
            };
        }


        private void GP1_05_DisplayVRAMStart(uint value) {
            displayVRAMXStart = (ushort)(value & 0x3FE);
            displayVRAMYStart = (ushort)((value >> 10) & 0x1FE);

            window.SetVRAMStart(displayVRAMXStart, displayVRAMYStart);
        }

        private void GP1_06_DisplayHorizontalRange(uint value) {
            uint bits = value & 0xFF_FFFF;

            if (bits == displayHorizontalRange) return;

            displayHorizontalRange = bits;

            displayX1 = (ushort)(value & 0xFFF);
            displayX2 = (ushort)((value >> 12) & 0xFFF);

            window.SetHorizontalRange(displayX1, displayX2);
        }

        private void GP1_07_DisplayVerticalRange(uint value) {
            uint bits = value & 0xFF_FFFF;

            if (bits == displayVerticalRange) return;

            displayVerticalRange = bits;

            displayY1 = (ushort)(value & 0x3FF);
            displayY2 = (ushort)((value >> 10) & 0x3FF);

            window.SetVerticalRange(displayY1, displayY2);
        }

        private void GP1_08_DisplayMode(uint value) {
            uint bits = value & 0xFF_FFFF;

            if (bits == displayModeBits) return;

            displayModeBits = bits;

            horizontalResolution1 = (byte)(value & 0x3);
            isVerticalResolution480 = (value & 0x4) != 0;
            isPal = (value & 0x8) != 0;
            is24BitDepth = (value & 0x10) != 0;
            isVerticalInterlace = (value & 0x20) != 0;
            horizontalResolution2 = (byte)((value & 0x40) >> 6);
            isReverseFlag = (value & 0x80) != 0;

            isInterlaceField = isVerticalInterlace;
            ApplyVideoTiming();

            int horizontalRes = resolutions[horizontalResolution2 << 2 | horizontalResolution1];
            int verticalRes = isVerticalResolution480 ? 480 : 240;

            window.SetDisplayMode(horizontalRes, verticalRes, is24BitDepth);
        }

        private void ApplyVideoTiming() {
            bool effectivePal = IsPalMode;
            horizontalTiming = effectivePal ? 3406 : 3413;
            verticalTiming = effectivePal ? 314 : 263;
        }

        private void GP1_09_TextureDisable(uint value) => isTextureDisabledAllowed = (value & 0x1) != 0;

        private void GP1_GPUInfo(uint value) {
            uint info = value & 0xF;
            switch (info) {
                case 0x2: GPUREAD = textureWindowBits; break;
                case 0x3: GPUREAD = (uint)(drawingAreaTop << 10 | drawingAreaLeft); break;
                case 0x4: GPUREAD = (uint)(drawingAreaBottom << 10 | drawingAreaRight); break;
                case 0x5: GPUREAD = (uint)(drawingYOffset << 11 | (ushort)drawingXOffset); break;
                case 0x7: GPUREAD = 2; break;
                case 0x8: GPUREAD = 0; break;
                default: Console.WriteLine("[GPU] GP1 Unhandled GetInfo: " + info.ToString("x8")); break;
            }
        }

        private int handleSemiTransp(int x, int y, int color, int semiTranspMode) {
            color0.val = (uint)color1555to8888LUT[vram1555.GetPixel(x, y)]; //back
            return handleSemiTransp((int)color0.val, color, semiTranspMode);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int handleSemiTransp(int backColor, int color, int semiTranspMode) {
            if (semiTranspMode == 0) {
                int rb = (((backColor & 0x00FF_00FF) + (color & 0x00FF_00FF)) >> 1) & 0x00FF_00FF;
                int g = (((backColor & 0x0000_FF00) + (color & 0x0000_FF00)) >> 1) & 0x0000_FF00;
                return (color & unchecked((int)0xFF00_0000)) | rb | g;
            }

            color0.val = (uint)backColor; //back
            color1.val = (uint)color; //front
            switch (semiTranspMode) {
                case 1://1.0 x B + 1.0 x F    ;aka B+F
                    color1.r = clampToFF(color0.r + color1.r);
                    color1.g = clampToFF(color0.g + color1.g);
                    color1.b = clampToFF(color0.b + color1.b);
                    break;
                case 2: //1.0 x B - 1.0 x F    ;aka B-F
                    color1.r = clampToZero(color0.r - color1.r);
                    color1.g = clampToZero(color0.g - color1.g);
                    color1.b = clampToZero(color0.b - color1.b);
                    break;
                case 3: //1.0 x B +0.25 x F    ;aka B+F/4
                    color1.r = clampToFF(color0.r + (color1.r >> 2));
                    color1.g = clampToFF(color0.g + (color1.g >> 2));
                    color1.b = clampToFF(color0.b + (color1.b >> 2));
                    break;
            }//actually doing RGB calcs on BGR struct...
            return (int)color1.val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte clampToZero(int v) {
            if (v < 0) return 0;
            else return (byte)v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte clampToFF(int v) {
            if (v > 0xFF) return 0xFF;
            else return (byte)v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort PackColor1555(int color) {
            int m = (color >> 24) & 0xFF;
            int r = ((color >> 16) & 0xFF) >> 3;
            int g = ((color >> 8) & 0xFF) >> 3;
            int b = (color & 0xFF) >> 3;
            return (ushort)(((m != 0 ? 1 : 0) << 15) | (b << 10) | (g << 5) | r);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ModulateColor(int color, int texel) {
            int r = clampToFF(((color >> 16) & 0xFF) * ((texel >> 16) & 0xFF) >> 7);
            int g = clampToFF(((color >> 8) & 0xFF) * ((texel >> 8) & 0xFF) >> 7);
            int b = clampToFF((color & 0xFF) * (texel & 0xFF) >> 7);
            return (texel & unchecked((int)0xFF00_0000)) | (r << 16) | (g << 8) | b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetRgbColor(uint value) {
            color0.val = value;
            return (color0.m << 24 | color0.r << 16 | color0.g << 8 | color0.b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool isTopLeft(Point2D a, Point2D b) => a.y == b.y && b.x > a.x || b.y < a.y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int interpolate(uint c1, uint c2, float ratio) {
            color1.val = c1;
            color2.val = c2;

            byte r = (byte)(color2.r * ratio + color1.r * (1 - ratio));
            byte g = (byte)(color2.g * ratio + color1.g * (1 - ratio));
            byte b = (byte)(color2.b * ratio + color1.b * (1 - ratio));

            return (r << 16 | g << 8 | b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int interpolate(int w0, int w1, int w2, int t0, int t1, int t2, int area) {
            //https://codeplea.com/triangular-interpolation
            return (t0 * w0 + t1 * w1 + t2 * w2) / area;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static short signed11bit(uint n) {
            return (short)(((int)n << 21) >> 21);
        }

        //This is only needed for the Direct GP0 commands as the command number needs to be
        //known ahead of the first command on queue.
        private static readonly byte[] CommandSizeTable = {
            //0  1   2   3   4   5   6   7   8   9   A   B   C   D   E   F
             1,  1,  3,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1, //0
             1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1, //1
             4,  4,  4,  4,  7,  7,  7,  7,  5,  5,  5,  5,  9,  9,  9,  9, //2
             6,  6,  6,  6,  9,  9,  9,  9,  8,  8,  8,  8, 12, 12, 12, 12, //3
             3,  3,  3,  3,  3,  3,  3,  3, 16, 16, 16, 16, 16, 16, 16, 16, //4
             4,  4,  4,  4,  4,  4,  4,  4, 16, 16, 16, 16, 16, 16, 16, 16, //5
             3,  3,  3,  1,  4,  4,  4,  4,  2,  1,  2,  1,  3,  3,  3,  3, //6
             2,  1,  2,  1,  3,  3,  3,  3,  2,  1,  2,  2,  3,  3,  3,  3, //7
             4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4, //8
             4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4, //9
             3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3, //A
             3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3, //B
             3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3, //C
             3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3, //D
             1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1, //E
             1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1  //F
        };
    }
}
