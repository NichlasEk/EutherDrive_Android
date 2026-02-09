using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        private const int PATTERN_MAX      = 2048;
        private const int DISPLAY_XSIZE    = 320;
        private const int DISPLAY_YSIZE    = 480;
        private const int DISPLAY_BUFSIZE  = DISPLAY_XSIZE * DISPLAY_YSIZE;
        public  int       SPRITE_XSIZE     = 512;
        public  int       SPRITE_YSIZE     = 512;
        private const int VRAM_DATASIZE    = 65536 / 2;
        private const int VSRAM_DATASIZE   = 20;
        private const int COLOR_MAX        = 64;
        private const int MAX_SPRITE       = 20;

        // Render-data
        private bool[]         g_pattern_chk = Array.Empty<bool>();
        internal uint[]        g_renderer_vram = Array.Empty<uint>();
        private VDP_LINE_SNAP[] g_line_snap = Array.Empty<VDP_LINE_SNAP>();
        private uint[]         g_game_cmap = Array.Empty<uint>();
        private uint[]         g_game_primap = Array.Empty<uint>();
        private uint[]         g_game_shadowmap = Array.Empty<uint>();

        // Snapshots (för “double buffering”/analys)
        private VDP_REGISTER    g_snap_register = new VDP_REGISTER();
        private uint[]          g_snap_renderer_vram = Array.Empty<uint>();
        private VDP_LINE_SNAP[] g_snap_line_snap = Array.Empty<VDP_LINE_SNAP>();
        private uint[]          g_snap_color = Array.Empty<uint>();
        private uint[]          g_snap_color_shadow = Array.Empty<uint>();
        private uint[]          g_snap_color_highlight = Array.Empty<uint>();

        // Framebuffer (ARGB32 per pixel)
        public uint[] g_game_screen = Array.Empty<uint>();

        private static readonly bool SmsNameTableMaskSms1 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_NAMETABLE_MASK_SMS1"), "1", StringComparison.Ordinal);

        // SMS per-line background metadata for sprite priority blending
        private byte[] _smsBgColorLine = Array.Empty<byte>();
        private bool[] _smsBgPriorityLine = Array.Empty<bool>();

        // Geometri / storlek
        public  int g_display_xsize;
        private int g_output_xsize;
        public  int g_display_ysize;
        private int g_output_ysize;
        private int g_display_xcell;
        private int g_display_ycell;
        private int g_scroll_xcell;
        private int g_scroll_ycell;
        public  int g_scroll_xsize;
        public  int g_scroll_ysize;
        private int g_scroll_xsize_mask;
        private int g_scroll_ysize_mask;
        public  int g_vertical_line_max;

        // Window A clip
        private int g_screenA_left_x;
        private int g_screenA_right_x;
        private int g_screenA_top_y;
        private int g_screenA_bottom_y;

        // Spritebegränsningar
        private int g_max_sprite_num;
        private int g_max_sprite_line;
        private int g_max_sprite_cell;
        private int g_sprite_vmask;
        private bool[] g_sprite_line_mask = Array.Empty<bool>();
        private uint[] g_game_field_even = Array.Empty<uint>();
        private uint[] g_game_field_odd = Array.Empty<uint>();

        // GPU-path stöds inte i headless-läget
        public bool rendering_gpu;

        // Interlace snapshot buffer (för att spara sprite-data vid double-field rendering)
        private VDP_LINE_SNAP[] _interlaceSnapshotBuffer = Array.Empty<VDP_LINE_SNAP>();

        // Kör en scanline
        private void rendering_line()
        {
            if (g_vdp_interlace_mode == 2 && InterlaceOutput == InterlaceOutputPolicy.DoubleField)
            {
                byte currentField = (byte)(g_vdp_interlace_field & 0x01);
                uint[] dest = (currentField == 0) ? g_game_field_even : g_game_field_odd;
                RenderLineWithField(currentField, g_scanline, dest);
                return;
            }

            RenderLineWithField(g_vdp_interlace_field, GetOutputLineForScanline(g_scanline));
        }

        private static readonly bool DebugShadowHighlight =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_SH"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSmsNameTable =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_NT"), "1", StringComparison.Ordinal);
        private long _smsNtTraceFrame = -1;

        private void RenderLineWithField(byte field, int outputLineOverride = -1, uint[]? targetBuffer = null)
        {
            g_vdp_interlace_field = (byte)(field & 0x01);

            if (md_main.g_masterSystemMode)
            {
                RenderSmsLine(outputLineOverride, targetBuffer);
                return;
            }

            // Debug log display status every 60 frames
            if (DebugShadowHighlight && g_scanline == 0 && _frameCounter % 60 == 0)
            {
                Debug.WriteLine($"[VDP-DISPLAY] frame={_frameCounter} display={g_vdp_reg_1_6_display} sh_mode={g_vdp_reg_12_3_shadow}");
            }

            if (MdTracerCore.MdLog.Enabled && g_scanline == 0)
                MdTracerCore.MdLog.WriteLine($"[VDP] frame={_frameCounter} display={g_vdp_reg_1_6_display}");

            // Sprite overflow is evaluated per scanline regardless of display enable.
            if (!md_main.g_masterSystemMode)
                EvaluateSpriteOverflowForLine(g_scanline);

             if (g_vdp_reg_1_6_display == 1)
             {
                 if (md_main.g_masterSystemMode && !_smsFirstLineRendered && g_scanline == 0)
                 {
                     _smsFirstLineRendered = true;
                     if (MdTracerCore.MdLog.Enabled)
                         MdTracerCore.MdLog.WriteLine("[SMS VDP] first scanline rendered");
                 }
                 
                 // DEBUG: Log when rendering starts (gated)
                 if (TraceVdpRender && (_frameCounter - 7059) < 5 && g_scanline < 10)
                 {
                     Console.WriteLine($"[VDP-RENDER] frame={_frameCounter} scanline={g_scanline} display=ON");
                 }
                 
                 // Ta snapshot av VDP-tillstånd för den här linjen
                 rendering_line_snap();

                 // Beräkna output line
                 int outputLine = (outputLineOverride >= 0) ? outputLineOverride : GetOutputLineForScanline(g_scanline);

                 // Alltid CPU-rendering i headless
                 rendering_line_cpu(outputLine, targetBuffer);
             }
              else
              {
                  // Display off: preserve framebuffer (for savestate compatibility) OR fill with black
                  if (PreserveFramebufferOnDisplayOff)
                 {
                     // Skip filling - preserve existing framebuffer from savestate
                 }
                 else
                 {
                     // Traditional behavior: fill with black
                     int outputLine = (outputLineOverride >= 0) ? outputLineOverride : GetOutputLineForScanline(g_scanline);
                     if ((uint)outputLine < (uint)g_output_ysize)
                     {
                         int pos = outputLine * g_output_xsize;
                         for (int x = 0; x < g_output_xsize; x++)
                         {
                             if (targetBuffer != null)
                                 targetBuffer[pos++] = 0xFF000000u;
                             else
                                 g_game_screen[pos++] = 0xFF000000u;
                         }
                     }
                 }
             }
        }

        private void RenderSmsLine(int outputLineOverride = -1, uint[]? targetBuffer = null)
        {
            int outputLine = (outputLineOverride >= 0) ? outputLineOverride : GetOutputLineForScanline(g_scanline);
            int smsLine = g_scanline;
            if (outputLine < 0 || outputLine >= g_output_ysize)
                return;

            uint[] dest = targetBuffer ?? g_game_screen;
            int width = g_output_xsize;
            int pos = outputLine * width;
            bool displayEnabled = (_smsRegs[1] & 0x40) != 0;
            // Backdrop color always uses sprite palette (CRAM base 0x10).
            int backdropIndex = 0x10 | (_smsRegs[7] & 0x0F);
            uint backdrop = (uint)backdropIndex < (uint)_smsPalette.Length ? _smsPalette[backdropIndex] : 0xFF000000u;

            if (!displayEnabled)
            {
                for (int x = 0; x < width; x++)
                    dest[pos + x] = 0xFF000000u;
                return;
            }

            for (int x = 0; x < width; x++)
                dest[pos + x] = backdrop;

            int visibleHeight = g_display_ysize;
            if (smsLine < 0 || smsLine >= visibleHeight)
                return;

            int displayWidth = Math.Min(g_display_xsize, width);
            if (_smsBgColorLine.Length < displayWidth)
                _smsBgColorLine = new byte[displayWidth];
            if (_smsBgPriorityLine.Length < displayWidth)
                _smsBgPriorityLine = new bool[displayWidth];
            Array.Clear(_smsBgColorLine, 0, displayWidth);
            Array.Clear(_smsBgPriorityLine, 0, displayWidth);
            int hscroll = _smsLatchedHScroll;
            int vscroll = _smsLatchedVScroll;
            bool hscrollLock = (_smsLatchedReg0 & 0x40) != 0;
            bool vscrollLock = (_smsLatchedReg0 & 0x80) != 0;
            bool hideLeftColumn = (_smsLatchedReg0 & 0x20) != 0;
            int effectiveHscroll = (hscrollLock && smsLine < 16) ? 0 : hscroll;
            int coarseX = (effectiveHscroll >> 3) & 0x1F;
            int fineX = effectiveHscroll & 0x07;
            // In SMS mode 4, base nametable address uses bits 0-3 of register 2,
            // but the effective address depends on 192/224-line mode.
            int nameBase = (_smsRegs[2] & 0x0F) << 10;
            if (g_display_ysize > 192)
            {
                // 224-line mode: mask out bit 11 and offset by $0700
                nameBase = (nameBase & 0xF000) | 0x0700;
            }
            else
            {
                // 192-line mode: mask out bit 10
                nameBase &= 0xF800;
            }
            int nameTableMask = 0x3FFF;
            if (SmsNameTableMaskSms1 && (_smsRegs[2] & 0x01) == 0)
                nameTableMask &= ~(1 << 10);
            // In SMS mode 4, background pattern table base is fixed at 0x0000.
            int patternBase = 0x0000;
            // backdrop already computed above

            int nameTableRows = (g_display_ysize > 192) ? 32 : 28;
            if (TraceSmsNameTable && _smsNtTraceFrame != _frameCounter)
            {
                _smsNtTraceFrame = _frameCounter;
                int effectiveVscroll = (vscrollLock && 0 >= 24) ? 0 : vscroll;
                int coarseY = (effectiveVscroll >> 3) & 0x1F;
                int fineY = effectiveVscroll & 0x07;
                int y = (smsLine + fineY) & 0xFF;
                int tileRow = ((y >> 3) + coarseY) % nameTableRows;
                int tileCol = (0 + (32 - coarseX)) & 0x1F;
                int entryAddr = (nameBase + ((tileRow * 32 + tileCol) * 2)) & nameTableMask;
                Console.WriteLine(
                    $"[SMS NT] frame={_frameCounter} line={smsLine} nameBase=0x{nameBase:X4} mask=0x{nameTableMask:X4} " +
                    $"rows={nameTableRows} hscroll={hscroll} vscroll={vscroll} coarseX={coarseX} fineX={fineX} " +
                    $"tileRow={tileRow} tileCol={tileCol} entryAddr=0x{entryAddr:X4}");
            }
            for (int column = 0; column < 32; column++)
            {
                int effectiveVscroll = (vscrollLock && column >= 24) ? 0 : vscroll;
                int coarseY = (effectiveVscroll >> 3) & 0x1F;
                int fineY = effectiveVscroll & 0x07;
                int y = (smsLine + fineY) & 0xFF;
                int tileRow = ((y >> 3) + coarseY) % nameTableRows;
                int rowInTile = y & 0x07;

                int tileCol = (column + (32 - coarseX)) & 0x1F;
                int entryAddr = (nameBase + ((tileRow * 32 + tileCol) * 2)) & nameTableMask;
                if ((uint)(entryAddr + 1) >= (uint)_smsVram.Length)
                    continue;

                ushort entry = (ushort)(_smsVram[entryAddr] | (_smsVram[entryAddr + 1] << 8));
                int tileIndex = entry & 0x1FF;
                // High-byte bits: b4=priority, b3=palette, b2=vflip, b1=hflip, b0=tile index MSB.
                bool priority = (entry & 0x1000) != 0;
                bool paletteBit = (entry & 0x0800) != 0;
                bool flipY = (entry & 0x0400) != 0;
                bool flipX = (entry & 0x0200) != 0;

                int row = flipY ? (7 - rowInTile) : rowInTile;
                int patternAddr = patternBase + (tileIndex * 32) + (row * 4);
                if ((uint)(patternAddr + 3) >= (uint)_smsVram.Length)
                    continue;

                byte b0 = _smsVram[patternAddr + 0];
                byte b1 = _smsVram[patternAddr + 1];
                byte b2 = _smsVram[patternAddr + 2];
                byte b3 = _smsVram[patternAddr + 3];

                for (int bgTileCol = 0; bgTileCol < 8; bgTileCol++)
                {
                    int x = (column * 8) + fineX + bgTileCol;
                    if (x >= displayWidth)
                        break;

                    if (hideLeftColumn && x < 8)
                    {
                        dest[pos + x] = backdrop;
                        _smsBgColorLine[x] = 0;
                        _smsBgPriorityLine[x] = false;
                        continue;
                    }

                    int bit = flipX ? bgTileCol : (7 - bgTileCol);
                    int mask = 1 << bit;
                    int color = ((b0 & mask) != 0 ? 1 : 0)
                                | ((b1 & mask) != 0 ? 2 : 0)
                                | ((b2 & mask) != 0 ? 4 : 0)
                                | ((b3 & mask) != 0 ? 8 : 0);

                    int paletteIndex = (paletteBit ? 16 : 0) + color;
                    if ((uint)paletteIndex >= (uint)_smsPalette.Length)
                        dest[pos + x] = backdrop;
                    else
                        dest[pos + x] = _smsPalette[paletteIndex];

                    _smsBgColorLine[x] = (byte)color;
                    _smsBgPriorityLine[x] = priority;
                }
            }

            RenderSmsSprites(displayWidth, outputLine, g_scanline, dest);
        }

        private void RenderSmsSprites(int displayWidth, int outputLine, int scanline, uint[] dest)
        {
            int satBase = (_smsRegs[5] & 0x7E) << 7;
            // SAT base is effectively aligned to 0x100 for sprite lookups.
            int satBaseAligned = satBase & 0xFF00;
            // Sprite pattern base: only bit 13 is used in mode 4 (legacy bits masked).
            int spritePatternBase = ((_smsRegs[6] & 0x04) != 0) ? 0x2000 : 0x0000;
            bool sprites8x16 = (_smsRegs[1] & 0x02) != 0;
            bool spriteZoom = (_smsRegs[1] & 0x01) != 0;
            int spriteHeight = (sprites8x16 ? 16 : 8) * (spriteZoom ? 2 : 1);
            int spriteWidth = spriteZoom ? 16 : 8;
            bool shiftSpritesLeft = (_smsRegs[0] & 0x08) != 0;
            int maxSprites = 64;
            int spritesOnLine = 0;
            int yTableBase = satBaseAligned & 0x3FFF;
            int xTableBase = (satBaseAligned + 0x80) & 0x3FFF;

            for (int i = 0; i < maxSprites; i++)
            {
                int yAddr = (yTableBase + i) & 0x3FFF;
                byte yRaw = _smsVram[yAddr];
                if (yRaw == 0xD0 && g_display_ysize <= 192)
                    break;

                int spriteY = yRaw;
                int spriteBottom = (spriteY + spriteHeight) & 0xFF;
                bool overlapsLine = spriteY < spriteBottom
                    ? (scanline >= spriteY && scanline < spriteBottom)
                    : (scanline >= spriteY || scanline < spriteBottom);
                if (!overlapsLine)
                    continue;

                int line = scanline >= spriteY ? (scanline - spriteY) : (scanline + 256 - spriteY);

                spritesOnLine++;
                if (spritesOnLine > 8)
                {
                    g_vdp_status_6_sprite = 1;
                    // Enforce 8 sprites per scanline (hardware limit)
                    break;
                }

                int xAddr = (xTableBase + i * 2) & 0x3FFF;
                int xRaw = _smsVram[xAddr];
                int tile = _smsVram[(xAddr + 1) & 0x3FFF];

                if (sprites8x16)
                    tile &= 0xFE;

                int srcRow = spriteZoom ? (line >> 1) : line;
                int tileRow = srcRow & 7;
                int tileIndex = tile + ((sprites8x16 && srcRow >= 8) ? 1 : 0);
                int patternAddr = (spritePatternBase + tileIndex * 32 + tileRow * 4) & 0x3FFF;

                if ((uint)(patternAddr + 3) >= (uint)_smsVram.Length)
                    continue;

                byte b0 = _smsVram[patternAddr + 0];
                byte b1 = _smsVram[patternAddr + 1];
                byte b2 = _smsVram[patternAddr + 2];
                byte b3 = _smsVram[patternAddr + 3];

                for (int col = 0; col < spriteWidth; col++)
                {
                    int srcCol = spriteZoom ? (col >> 1) : col;
                    int bit = 7 - srcCol;
                    int mask = 1 << bit;
                    int color = ((b0 & mask) != 0 ? 1 : 0)
                                | ((b1 & mask) != 0 ? 2 : 0)
                                | ((b2 & mask) != 0 ? 4 : 0)
                                | ((b3 & mask) != 0 ? 8 : 0);
                    if (color == 0)
                        continue;

                    int x = xRaw + col + (shiftSpritesLeft ? -8 : 0);
                    if ((uint)x >= (uint)displayWidth)
                        continue;
                    // Hide-left-column affects background only; sprites are still visible.

                    int paletteIndex = 16 + color;
                    if ((uint)paletteIndex >= (uint)_smsPalette.Length)
                        continue;

                    if (_smsBgColorLine[x] == 0 || !_smsBgPriorityLine[x])
                        dest[outputLine * g_output_xsize + x] = _smsPalette[paletteIndex];
                }
            }
        }

        // Avsluta en frame (ingen separat render-tråd eller DX)
        private void rendering_frame()
        {
            // VIKTIGT: I interlace mode flippar fältet varje ram.
            // Vi ska INTE sätta fältet till 0 här - det förstör fält-växlingen!
            // AdvanceInterlaceField() hanterar fält-växlingen korrekt.

            ForceVBlankForTest();
            ForceMdVBlankForTest();
            // "Lås in" det som behövs för postprocess/analys
            rendering_frame_snap();
            WeaveInterlaceFields();
            UpdateRgbaFrameFromGameScreen();
            SmsLogFrameHash();
            AdvanceInterlaceField();
            _frameCounter++;
            LogMdWriteSummary();
            MaybeLogVdpTiming();
            MaybeLogVdpState();
            LogInterlaceDebug();
            
            // VRAM truth test for debugging (optional)
            // if (_frameCounter >= 4910 && _frameCounter <= 4920)
            // {
            //     md_main.g_md_vdp?.LogVramTruthTest();
            // }
        }

        private void WeaveInterlaceFields()
        {
            if (g_vdp_interlace_mode != 2 || InterlaceOutput != InterlaceOutputPolicy.DoubleField)
                return;

            int fieldLineCount = g_display_ysize;
            int outputLines = g_output_ysize;
            int width = g_output_xsize;
            int maxLines = Math.Min(fieldLineCount * 2, outputLines);

            for (int y = 0; y < fieldLineCount && (y << 1) < maxLines; y++)
            {
                int evenBase = y * width;
                int oddBase = y * width;
                int outEven = (y << 1) * width;
                int outOdd = ((y << 1) + 1) * width;

                Array.Copy(g_game_field_even, evenBase, g_game_screen, outEven, width);
                if ((y << 1) + 1 < maxLines)
                    Array.Copy(g_game_field_odd, oddBase, g_game_screen, outOdd, width);
            }
        }

        // --- Hjälp (valfritt) ---
        // Exponera en enkel pekare till framebuffer om du vill hämta bilden från UI-lagret:
        public uint[] GetFrameBuffer()
        {
            // DEBUG: Log first pixel value
            if (_frameCounter < 5 && g_game_screen.Length > 0)
            {
                Console.WriteLine($"[VDP-DEBUG] GetFrameBuffer called at frame {_frameCounter}, g_game_screen[0]=0x{g_game_screen[0]:X8}, length={g_game_screen.Length}");
            }
            return g_game_screen;
        }
    }
}
