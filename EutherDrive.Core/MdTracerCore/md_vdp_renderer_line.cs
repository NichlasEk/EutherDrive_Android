using System.Diagnostics;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        private static readonly bool TraceRenderPlaneDebug =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_RENDER_PLANE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceRenderRead =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_RENDER_READ"), "1", StringComparison.Ordinal);
        // TEMPORARY: Force direct VRAM reads to eliminate cache mismatch
        // Set to true to read patterns directly from vram[] instead of g_renderer_vram
        private static readonly bool AllowRenderDebug =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_ALLOW_RENDER_DEBUG"), "1", StringComparison.Ordinal);
        private static readonly string? ForceDirectVramReadSpritesEnv =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_DIRECT_VRAM");
        private static readonly bool ForceDirectVramReadSpritesEnvSet = ForceDirectVramReadSpritesEnv != null;
        private static bool ForceDirectVramReadSprites =
            AllowRenderDebug && string.Equals(ForceDirectVramReadSpritesEnv, "1", StringComparison.Ordinal);
        private static readonly bool ForceDirectVramReadPlanes =
            AllowRenderDebug &&
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_DIRECT_VRAM_PLANES"), "1", StringComparison.Ordinal);
        private static readonly bool DisableWindow =
            AllowRenderDebug &&
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DISABLE_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly bool DisableSprites =
            AllowRenderDebug &&
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DISABLE_SPRITES"), "1", StringComparison.Ordinal);
        private static readonly bool ForceScrollZero =
            AllowRenderDebug &&
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_SCROLL_ZERO"), "1", StringComparison.Ordinal);
        private static readonly bool TraceTileFetch =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_TILE_FETCH"), "1", StringComparison.Ordinal);
        private static readonly bool DebugTileRendering =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_TILE_RENDERING"), "1", StringComparison.Ordinal);
        private static readonly int TraceTileFetchScanline =
            ParseTraceInt("EUTHERDRIVE_TRACE_TILE_FETCH_SCANLINE", 112);
        private static readonly int TraceTileFetchLimit =
            ParseTraceInt("EUTHERDRIVE_TRACE_TILE_FETCH_LIMIT", 128);
        [NonSerialized] private int _traceTileFetchRemaining = TraceTileFetchLimit;
        [NonSerialized] private long _traceTileFetchFrame = -1;
        private static readonly bool TracePriMap =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PRI_MAP"), "1", StringComparison.Ordinal);
        private static readonly int TracePriMapScanline =
            ParseTraceInt("EUTHERDRIVE_TRACE_PRI_MAP_SCANLINE", 112);
        private static readonly int TracePriMapX =
            ParseTraceInt("EUTHERDRIVE_TRACE_PRI_MAP_X", 0);
        private static readonly int TracePriMapWidth =
            ParseTraceInt("EUTHERDRIVE_TRACE_PRI_MAP_WIDTH", 32);
        [NonSerialized] private long _tracePriMapFrame = -1;

        private static int ParseTraceInt(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            return value < 0 ? fallback : value;
        }

        // When display is OFF, preserve framebuffer instead of filling with black
        // Default is TRUE - many games use display toggle as an effect (Mystic Defender, etc.)
        // Can be toggled at runtime via static property or EUTHERDRIVE_FILL_FB_ON_DISPLAY_OFF=1 env var
        private static bool _preserveFramebufferOnDisplayOff =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FILL_FB_ON_DISPLAY_OFF"), "1", StringComparison.Ordinal);

        public static bool PreserveFramebufferOnDisplayOff
        {
            get => _preserveFramebufferOnDisplayOff;
            set => _preserveFramebufferOnDisplayOff = value;
        }

        // Helper to read a word from vram[] (handles the MD byte-swap)
        private ushort vram_read_render(int addr)
        {
            addr &= 0xFFFF;
            addr &= 0xFFFE; // Word-align, wrap at 64KB
            return (ushort)((g_vram[addr] << 8) | g_vram[addr ^ 1]);
        }

        private int GetTileRebaseOffsetBytes(TileRebaseKind rebaseKind)
        {
            if (g_vdp_reg_1_7_vram128 == 0)
                return 0;

            bool useRebase = rebaseKind switch
            {
                TileRebaseKind.PlaneA => g_vdp_reg_14_plane_a_rebase != 0,
                TileRebaseKind.PlaneB => g_vdp_reg_14_plane_b_rebase != 0,
                TileRebaseKind.Window => g_vdp_reg_14_plane_a_rebase != 0,
                _ => false
            };

            return useRebase ? 0x10000 : 0;
        }

        private uint ReadPatternPixelMode2Direct(int tileIndex, int xInTile, int yInTile, uint reverse, TileRebaseKind rebaseKind)
        {
            int x = ((reverse & 0x01) != 0) ? (7 - xInTile) : xInTile;
            int cellHeight = GetCellHeightPixels();
            int y = ((reverse & 0x02) != 0) ? (cellHeight - 1 - yInTile) : yInTile;
            int baseAddr = ((tileIndex & 0x3FF) << 6) + GetTileRebaseOffsetBytes(rebaseKind);
            int byteAddr = baseAddr + (y << 2) + ((x >> 2) << 1);
            ushort word = vram_read_render(byteAddr);
            return (uint)((word >> ((3 - (x & 3)) << 2)) & 0x0f);
        }

        private uint ReadPatternPixelDirect(int tileIndex, int xInTile, int yInTile, uint reverse, TileRebaseKind rebaseKind)
        {
            if (g_vdp_interlace_mode == 2)
                return ReadPatternPixelMode2Direct(tileIndex, xInTile, yInTile, reverse, rebaseKind);

            int x = ((reverse & 0x01) != 0) ? (7 - xInTile) : xInTile;
            int y = ((reverse & 0x02) != 0) ? (7 - yInTile) : yInTile;
            int baseAddr = ((tileIndex & 0x07FF) << 5) + GetTileRebaseOffsetBytes(rebaseKind);
            int byteAddr = baseAddr + (y << 2) + ((x >> 2) << 1);
            ushort word = vram_read_render(byteAddr);
            
            // DEBUG: Log first few tile reads
            if (DebugTileRendering && _frameCounter >= 115 && _frameCounter < 125 && tileIndex < 100)
            {
                Console.WriteLine($"[TILE-DEBUG] frame={_frameCounter} tile={tileIndex} x={xInTile} y={yInTile} base=0x{baseAddr:X4} byteAddr=0x{byteAddr:X4} word=0x{word:X4} reverse={reverse} pixel={((word >> ((3 - (x & 3)) << 2)) & 0x0f):X1}");
            }
            
            return (uint)((word >> ((3 - (x & 3)) << 2)) & 0x0f);
        }

        private ushort ReadNameTableWord(int wordIndex)
        {
            int byteAddr = (wordIndex << 1) & 0xFFFF;
            return vram_read_render(byteAddr);
        }

        private void rendering_line_cpu(int outputLine, uint[]? targetBuffer = null)
        {
            uint[] destBuffer = targetBuffer ?? g_game_screen;
            int w_vscroll_mask = 0xffff;
            if (g_vdp_reg_11_2_vscroll == 1)
            {
                w_vscroll_mask = 0x000f;
            }
            TraceNameTableRowDumpIfNeeded();
            int renderLine = GetRenderLine(g_scanline);
            int cellShift = GetCellHeightShift();
            if (TraceTileFetch && g_scanline == TraceTileFetchScanline && _traceTileFetchFrame != _frameCounter)
            {
                _traceTileFetchFrame = _frameCounter;
                _traceTileFetchRemaining = TraceTileFetchLimit;
            }

            // DIAGNOSTIC: Dump name tables from both sources when interlace=2 (once per 60 frames)
            if (g_vdp_interlace_mode == 2 && g_scanline == 0 && _frameCounter % 60 == 0)
            {
                bool isH40 = (g_vdp_reg_12_7_cellmode1 != 0);
                int scrollMask = isH40 ? 0xFE00 : 0xFC00;
                int scrollA_base = g_vdp_reg_2_scrolla & scrollMask;
                int scrollB_base = g_vdp_reg_4_scrollb & scrollMask;

                // Count non-zero in first 64 entries from both sources
                int directNonZeroA = 0, cacheNonZeroA = 0;
                int directNonZeroB = 0, cacheNonZeroB = 0;

                for (int i = 0; i < 64; i++)
                {
                    int addrA = (scrollA_base + (i << 1)) & 0xFFFE;
                    int addrB = (scrollB_base + (i << 1)) & 0xFFFE;

                    ushort directA = vram_read_render(addrA);
                    ushort directB = vram_read_render(addrB);

                    int cacheIdxA = 0x6000 + i;
                    int cacheIdxB = 0x6800 + i;
                    uint cacheA = cacheIdxA < g_renderer_vram.Length ? g_renderer_vram[cacheIdxA] : 0;
                    uint cacheB = cacheIdxB < g_renderer_vram.Length ? g_renderer_vram[cacheIdxB] : 0;

                    if (directA != 0) directNonZeroA++;
                    if (cacheA != 0) cacheNonZeroA++;
                    if (directB != 0) directNonZeroB++;
                    if (cacheB != 0) cacheNonZeroB++;
                }

                string sourceUsed = ForceDirectVramReadPlanes ? "VRAM" : "CACHE";

                Console.WriteLine($"[VRAM-VS-CACHE] frame={_frameCounter} interlace=2 source={sourceUsed} " +
                    $"scrollA_base=0x{scrollA_base:X4} scrollB_base=0x{scrollB_base:X4} " +
                    $"directA={directNonZeroA}/64 cacheA={cacheNonZeroA}/64 " +
                    $"directB={directNonZeroB}/64 cacheB={cacheNonZeroB}/64");

                // Show first few entries for comparison if they differ
                if (directNonZeroA != cacheNonZeroA || directNonZeroB != cacheNonZeroB)
                {
                    Console.WriteLine($"[VRAM-VS-CACHE-DETAIL] first 8 entries:");
                    for (int i = 0; i < 8; i++)
                    {
                        int addrA = (scrollA_base + (i << 1)) & 0xFFFE;
                        int addrB = (scrollB_base + (i << 1)) & 0xFFFE;
                        ushort directA = vram_read_render(addrA);
                        ushort directB = vram_read_render(addrB);
                        uint cacheA = g_renderer_vram[0x6000 + i];
                        uint cacheB = g_renderer_vram[0x6800 + i];
                        Console.WriteLine($"  i={i}: addrA=0x{addrA:X4} direct=0x{directA:X4} cache=0x{cacheA:X4} | addrB=0x{addrB:X4} direct=0x{directB:X4} cache=0x{cacheB:X4}");
                    }
                }
            }

            // Nollställ rad-buffertar för aktuell scanline
            for (int dx = 0; dx < g_display_xsize; dx++)
            {
                g_game_cmap[dx] = 0;
                g_game_primap[dx] = 0;
                g_game_shadowmap[dx] = 0;
                g_sprite_line_mask[dx] = false;
            }

            // --- Scroll B ---
            {
                int w_view_x = ForceScrollZero ? 0 : g_line_snap[g_scanline].hscrollB;
                uint w_priority = 0;
                uint w_palette = 0;
                uint w_reverse = 0;
                uint w_char = 0;
                int w_view_addr = 0;
                int w_view_dx = 8;
                int w_view_dy = 0;
                int scrollB_byte_addr = g_vdp_reg_4_scrollb & (IsH40Mode() ? 0xFE00 : 0xFC00);
                int w_screen_adrdr = scrollB_byte_addr >> 1;
                int w_pic_addr = 0;

                 // Debug log first entry in interlace mode 2
                if (g_vdp_interlace_mode == 2 && g_scanline == 0 && MdTracerCore.MdLog.Enabled)
                {
                    MdTracerCore.MdLog.WriteLine(
                        $"[RENDER] field={g_vdp_interlace_field} scrollB_addr=0x{w_screen_adrdr:X4} " +
                        $"cacheB=0x{g_renderer_vram[0x6800]:X4} cacheA=0x{g_renderer_vram[0x6000]:X4}");
                }
                
                // DEBUG: Log first few tiles of Plane B
                if (g_scanline == 100 && _frameCounter >= 100 && _frameCounter <= 110)
                {
                    int debug_scrollB_byte_addr = g_vdp_reg_4_scrollb & (IsH40Mode() ? 0xFE00 : 0xFC00);
                    int debug_scrollB_word_addr = debug_scrollB_byte_addr >> 1;
                    if (TraceRenderPlaneDebug)
                        Console.WriteLine($"[DEBUG-PLANE-B] frame={_frameCounter} scanline={g_scanline} reg4=0x{g_vdp_reg_4_scrollb:X4} byte_addr=0x{debug_scrollB_byte_addr:X4} word_addr=0x{debug_scrollB_word_addr:X4}");
                    // Check VRAM directly too
                    for (int i = 0; i < 10; i++)
                    {
                        uint cacheVal = g_renderer_vram[debug_scrollB_word_addr + i];
                        uint vramAddr = (uint)(debug_scrollB_byte_addr + (i << 1));
                        ushort vramVal = vram_read_render((int)vramAddr);
                        if (TraceRenderPlaneDebug)
                            Console.WriteLine($"[DEBUG-PLANE-B] tile[{i}] cache@0x{debug_scrollB_word_addr + i:X4}=0x{cacheVal:X4} vram@0x{vramAddr:X4} val=0x{vramVal:X4} pri={(vramVal>>15)&1} char={vramVal&0x7FF}");
                    }
                }

                for (int wx = 0; wx < g_display_xsize; wx++)
                {
                    if ((wx & w_vscroll_mask) == 0)
                    {
                        int w_view_y = ForceScrollZero ? g_scanline : g_line_snap[g_scanline].vscrollB[wx >> 4];
                        w_view_dy = GetRowInCell(w_view_y);
                        w_view_addr = w_screen_adrdr + ((w_view_y >> cellShift) * g_scroll_xcell);
                        w_view_dx = 8;
                    }
                    if (w_view_dx == 8)
                    {
                        w_view_x %= g_scroll_xsize;
                        w_view_dx = w_view_x & 7;
                          uint w_val = ReadNameTableWord(w_view_addr + (w_view_x >> 3));
                         w_priority = ((w_val >> 15) & 0x0001);
                         w_palette  = (((w_val >> 13) & 0x0003) << 4);
                          w_reverse  = ((w_val >> 11) & 0x0003);
                         w_char     =  (w_val & 0x07ff);
                         
                         // DEBUG: Log first few name table reads
                         if (DebugTileRendering && _frameCounter < 30 && wx < 64 && (wx % 8) == 0)
                         {
                             Console.WriteLine($"[NAMETABLE-DEBUG] frame={_frameCounter} scanline={g_scanline} wx={wx} addr=0x{w_view_addr + (w_view_x >> 3):X4} val=0x{w_val:X4} tile={w_char} pal={w_palette>>4} pri={w_priority} rev={w_reverse}");
                         }

                        bool useDirectPixel = ForceDirectVramReadPlanes || w_reverse != 0;
                        w_pic_addr = useDirectPixel ? -1 : GetTileWordAddress((int)w_char, w_view_dy, w_reverse, TileRebaseKind.PlaneB);
                        if (TraceTileFetch && g_scanline == TraceTileFetchScanline && _traceTileFetchRemaining > 0)
                        {
                            _traceTileFetchRemaining--;
                            int nameIndex = w_view_addr + (w_view_x >> 3);
                            int nameByteAddr = (nameIndex << 1) & 0xFFFF;
                            int tileBase = (int)(w_char & 0x07FF) << 5;
                            Console.WriteLine(
                                $"[TILEFETCH] plane=B frame={_frameCounter} scanline={g_scanline} wx={wx} " +
                                $"hscroll={g_line_snap[g_scanline].hscrollB} vscroll={g_line_snap[g_scanline].vscrollB[wx >> 4]} " +
                                $"nameBase=0x{scrollB_byte_addr:X4} nameIdx=0x{nameIndex:X4} nameAddr=0x{nameByteAddr:X4} " +
                                $"raw=0x{w_val:X4} tile=0x{w_char:X3} pal={w_palette >> 4} prio={w_priority} rev={w_reverse} " +
                                $"tileBase=0x{tileBase:X4} picAddr={(w_pic_addr >= 0 ? $"0x{w_pic_addr:X4}" : "direct")}");
                        }

                        // Debug: log first pattern read
                        if (g_scanline == 100 && wx == 0 && _frameCounter == 100 && w_pic_addr >= 0)
                        {
                            if (TraceRenderRead)
                                Console.WriteLine($"[RENDER-READ] frame={_frameCounter} scanline={g_scanline} tile={w_char} row={w_view_dy} pic_addr={w_pic_addr} rvram[{w_pic_addr}]=0x{g_renderer_vram[w_pic_addr]:X4}");
                        }
                    }
                    uint picValue;
                    if (w_pic_addr < 0)
                    {
                        picValue = ReadPatternPixelDirect((int)w_char, w_view_dx, w_view_dy, w_reverse, TileRebaseKind.PlaneB);
                    }
                    else
                    {
                        uint w_pic_w = g_renderer_vram[w_pic_addr + (w_view_dx >> 2)];
                        picValue = (uint)((w_pic_w >> ((3 - (w_view_dx & 3)) << 2)) & 0x0f);
                    }
                    if (picValue != 0)
                    {
                        g_game_cmap[wx]   = w_palette + picValue;
                        g_game_primap[wx] = w_priority;
                        g_game_shadowmap[wx] = w_priority;
                    }
                    w_view_x += 1;
                    w_view_dx += 1;
                }
            }

            // --- Scroll A ---
            {
                int w_view_x = ForceScrollZero ? 0 : g_line_snap[g_scanline].hscrollA;
                    uint w_priority = 0;
                    uint w_palette = 0;
                    uint w_reverse = 0;
                    uint w_char = 0;
                    int w_view_addr = 0;
                    int w_view_dx = 8;
                    int w_view_dy = 0;
                     int scrollA_byte_addr = g_vdp_reg_2_scrolla & (IsH40Mode() ? 0xFE00 : 0xFC00);
                     int w_screen_adrdr = scrollA_byte_addr >> 1;
                     
                 // DEBUG: Log first few tiles of Plane A
                 if (g_scanline == 100 && _frameCounter >= 100 && _frameCounter <= 110)
                 {
                     int debug_scrollA_byte_addr = g_vdp_reg_2_scrolla & (IsH40Mode() ? 0xFE00 : 0xFC00);
                     int debug_scrollA_word_addr = debug_scrollA_byte_addr >> 1;
                     if (TraceRenderPlaneDebug)
                         Console.WriteLine($"[DEBUG-PLANE-A] frame={_frameCounter} scanline={g_scanline} reg2=0x{g_vdp_reg_2_scrolla:X4} byte_addr=0x{debug_scrollA_byte_addr:X4} word_addr=0x{debug_scrollA_word_addr:X4}");
                     for (int i = 0; i < 10; i++)
                     {
                         uint cacheVal = g_renderer_vram[debug_scrollA_word_addr + i];
                         uint vramAddr = (uint)(debug_scrollA_byte_addr + (i << 1));
                         ushort vramVal = vram_read_render((int)vramAddr);
                         if (TraceRenderPlaneDebug)
                             Console.WriteLine($"[DEBUG-PLANE-A] tile[{i}] cache@0x{debug_scrollA_word_addr + i:X4}=0x{cacheVal:X4} vram@0x{vramAddr:X4} val=0x{vramVal:X4} pri={(vramVal>>15)&1} char={vramVal&0x7FF}");
                     }
                 }
                    int w_pic_addr = 0;

                    for (int wx = 0; wx < g_display_xsize; wx++)
                    {
                        if ((wx & w_vscroll_mask) == 0)
                        {
                            int w_view_y = ForceScrollZero ? g_scanline : g_line_snap[g_scanline].vscrollA[wx >> 4];
                            w_view_dy = GetRowInCell(w_view_y);
                            w_view_addr = w_screen_adrdr + ((w_view_y >> cellShift) * g_scroll_xcell);
                            w_view_dx = 8;
                        }
                    if (w_view_dx == 8)
                    {
                        w_view_x %= g_scroll_xsize;
                        w_view_dx = w_view_x & 7;
                          uint w_val = ReadNameTableWord(w_view_addr + (w_view_x >> 3));
                            w_priority = ((w_val >> 15) & 0x0001);
                            w_palette  = (((w_val >> 13) & 0x0003) << 4);
                        w_reverse  = ((w_val >> 11) & 0x0003);

                            w_char     =  (w_val & 0x07ff);

                        bool useDirectPixel = ForceDirectVramReadPlanes || w_reverse != 0;
                        w_pic_addr = useDirectPixel ? -1 : GetTileWordAddress((int)w_char, w_view_dy, w_reverse, TileRebaseKind.PlaneA);
                        if (TraceTileFetch && g_scanline == TraceTileFetchScanline && _traceTileFetchRemaining > 0)
                        {
                            _traceTileFetchRemaining--;
                            int nameIndex = w_view_addr + (w_view_x >> 3);
                            int nameByteAddr = (nameIndex << 1) & 0xFFFF;
                            int tileBase = (int)(w_char & 0x07FF) << 5;
                            Console.WriteLine(
                                $"[TILEFETCH] plane=A frame={_frameCounter} scanline={g_scanline} wx={wx} " +
                                $"hscroll={g_line_snap[g_scanline].hscrollA} vscroll={g_line_snap[g_scanline].vscrollA[wx >> 4]} " +
                                $"nameBase=0x{scrollA_byte_addr:X4} nameIdx=0x{nameIndex:X4} nameAddr=0x{nameByteAddr:X4} " +
                                $"raw=0x{w_val:X4} tile=0x{w_char:X3} pal={w_palette >> 4} prio={w_priority} rev={w_reverse} " +
                                $"tileBase=0x{tileBase:X4} picAddr={(w_pic_addr >= 0 ? $"0x{w_pic_addr:X4}" : "direct")}");
                        }
                    }
                        if (g_game_primap[wx] <= w_priority)
                        {
                            uint picValue;
                            if (w_pic_addr < 0)
                            {
                                picValue = ReadPatternPixelDirect((int)w_char, w_view_dx, w_view_dy, w_reverse, TileRebaseKind.PlaneA);
                            }
                            else
                            {
                                uint w_pic_w = g_renderer_vram[w_pic_addr + (w_view_dx >> 2)];
                                picValue = (uint)((w_pic_w >> ((3 - (w_view_dx & 3)) << 2)) & 0x0f);
                            }
                            if (picValue != 0)
                            {
                                g_game_cmap[wx]   = w_palette + picValue;
                                g_game_primap[wx] = w_priority;
                            }
                            g_game_shadowmap[wx] |= w_priority;
                        }
                        w_view_x += 1;
                        w_view_dx += 1;
                    }
            }

            if (TracePriMap && g_scanline == TracePriMapScanline && _tracePriMapFrame != _frameCounter)
            {
                _tracePriMapFrame = _frameCounter;
                int startX = Math.Max(0, TracePriMapX);
                int endX = Math.Min(g_display_xsize, startX + Math.Max(1, TracePriMapWidth));
                System.Text.StringBuilder pri = new System.Text.StringBuilder(endX - startX);
                System.Text.StringBuilder cov = new System.Text.StringBuilder(endX - startX);
                for (int x = startX; x < endX; x++)
                {
                    pri.Append(g_game_primap[x] != 0 ? '1' : '0');
                    cov.Append(g_game_cmap[x] != 0 ? '#' : '.');
                }
                Console.WriteLine($"[PRIMAP] frame={_frameCounter} scanline={g_scanline} x={startX}..{endX - 1} pri={pri} cov={cov}");
            }

            // --- Sprites ---
            if (!DisableSprites)
            {
                bool allowSpriteMasking = false;
                for (int w_sp = 0; w_sp < g_line_snap[g_scanline].sprite_rendrere_num; w_sp++)
                {
                    int  w_left       = g_line_snap[g_scanline].sprite_left[w_sp];
                    int  w_top        = g_line_snap[g_scanline].sprite_top[w_sp];
                    int  w_xcell_size = g_line_snap[g_scanline].sprite_xcell_size[w_sp];
                    int  w_ycell_size = g_line_snap[g_scanline].sprite_ycell_size[w_sp];
                    uint w_priority   = g_line_snap[g_scanline].sprite_priority[w_sp];
                    uint w_palette    = g_line_snap[g_scanline].sprite_palette[w_sp];
                    uint w_reverse    = g_line_snap[g_scanline].sprite_reverse[w_sp];
                    int  w_char       = (int)g_line_snap[g_scanline].sprite_char[w_sp];

                    int rawX = w_left + 128;
                    if (rawX == 0)
                    {
                        if (allowSpriteMasking)
                            break;
                    }
                    else
                    {
                        allowSpriteMasking = true;
                    }

                    int cellHeight = GetCellHeightPixels();
                    // I interlace mode 2, använd full linje (scanline*2 + field) för korrekt sprite-rad.
                    int lineY = (g_vdp_interlace_mode == 2)
                        ? ((g_scanline << 1) | g_vdp_interlace_field)
                        : g_scanline;
                    int w_y     = lineY - w_top;
                    int w_ycell = w_y >> cellShift;
                    int w_cy    = w_y & (cellHeight - 1);
                    int w_posx  = w_left;
                    int cellHeightTiles = g_vdp_interlace_mode == 2 ? 1 : (cellHeight >> 3);
                    int rowStride = w_xcell_size * cellHeightTiles;
                    int yCellIndex = w_ycell * cellHeightTiles;
                    int yCellIndexFlipped = (w_ycell_size - w_ycell - 1) * cellHeightTiles;

                    for (int w_cur_xcell = 0; w_cur_xcell < w_xcell_size; w_cur_xcell++)
                    {
                        int w_char_cur = 0;
                        switch (w_reverse)
                        {
                            case 0: w_char_cur = w_char + (rowStride * yCellIndex) + w_cur_xcell; break;
                            case 1: w_char_cur = w_char + (rowStride * yCellIndex) + (w_xcell_size - w_cur_xcell - 1); break;
                            case 2: w_char_cur = w_char + (rowStride * yCellIndexFlipped) + w_cur_xcell; break;
                            default:w_char_cur = w_char + (rowStride * yCellIndexFlipped) + (w_xcell_size - w_cur_xcell - 1); break;
                        }
                        for (int w_cx = 0; w_cx < 8; w_cx++)
                        {
                            if ((0 <= w_posx) && (w_posx < g_display_xsize))
                            {
                                if (g_game_primap[w_posx] <= w_priority && !g_sprite_line_mask[w_posx])
                                {
                                    uint w_pic_w;
                                    int x_in_tile = w_cx;
                                    int y_in_tile = w_cy;

                                    bool useDirectSprite = ForceDirectVramReadSprites || w_reverse != 0;
                                    if (useDirectSprite)
                                    {
                                        x_in_tile = ((w_reverse & 0x01) != 0) ? (7 - w_cx) : w_cx;
                                        if ((w_reverse & 0x02) != 0)
                                            y_in_tile = (cellHeight - 1) - w_cy;

                                        if (g_vdp_interlace_mode == 2)
                                        {
                                            int tileIndex = w_char_cur & 0x3FF;
                                            int baseAddr = tileIndex << 6; // 64 bytes per pattern (8x16)
                                            int byteAddr = baseAddr + (y_in_tile << 2) + ((x_in_tile >> 2) << 1);
                                            w_pic_w = vram_read_render(byteAddr);
                                        }
                                        else
                                        {
                                            int tileIndex = w_char_cur & 0x7FF;
                                            int baseAddr = tileIndex << 5; // 32 bytes per pattern (8x8)
                                            int byteAddr = baseAddr + (y_in_tile << 2) + ((x_in_tile >> 2) << 1);
                                            w_pic_w = vram_read_render(byteAddr);
                                        }
                                    }
                                    else
                                    {
                                        int w_num = GetTileWordAddress(w_char_cur, y_in_tile, w_reverse, TileRebaseKind.None) + (x_in_tile >> 2);
                                        w_pic_w = g_renderer_vram[w_num];
                                    }

                                    uint w_pic = (w_pic_w >> ((3 - (x_in_tile & 3)) << 2)) & 0x0f;

                                    if (w_pic != 0)
                                    {
                                        uint w_color = (uint)(w_palette + w_pic);
                                        if (g_vdp_reg_12_3_shadow == 0)
                                        {
                                            g_game_cmap[w_posx]   = w_color;
                                            g_game_primap[w_posx] = w_priority;
                                        }
                                        else if (w_color == 0x3e)
                                        {
                                            // Palette 3, color 14: Transparent, makes underlying pixel HIGHLIGHT
                                            uint w_map = g_game_shadowmap[w_posx];
                                            if (w_map < 2) g_game_shadowmap[w_posx] = (uint)(w_map + 1);
                                            // Don't set g_sprite_line_mask - this sprite is transparent
                                        }
                                        else if (w_color == 0x3f)
                                        {
                                            // Palette 3, color 15: Transparent, makes underlying pixel SHADOW
                                            uint w_map = g_game_shadowmap[w_posx];
                                            if (w_map > 0) g_game_shadowmap[w_posx] = (uint)(w_map - 1);
                                            // Don't set g_sprite_line_mask - this sprite is transparent
                                        }
                                        else if ((w_color & 0x0f) == 0x0e)
                                        {
                                            // Colors 0x0E, 0x1E, 0x2E: ALWAYS NORMAL (no shadow inheritance)
                                            g_game_cmap[w_posx]     = w_color;
                                            g_game_primap[w_posx]   = w_priority;
                                            g_game_shadowmap[w_posx] |= w_priority;
                                        }
                                        else
                                        {
                                            g_game_cmap[w_posx]   = w_color;
                                            g_game_primap[w_posx] = w_priority;
                                            g_game_shadowmap[w_posx] |= w_priority;
                                        }
                                        // Only set sprite line mask for non-transparent sprites
                                        if (w_color != 0x3e && w_color != 0x3f)
                                        {
                                            g_sprite_line_mask[w_posx] = true;
                                        }
                                    }
                                }
                            }
                            w_posx += 1;
                        }
                    }
                }
            }

            // --- Window ---
            {
                if (DisableWindow)
                    goto SkipWindow;
                int w_xcell_st = g_line_snap[g_scanline].window_x_st;
                int w_xcell_ed = g_line_snap[g_scanline].window_x_ed;
                if (w_xcell_st != w_xcell_ed)
                {
                    // Window-rader behöver full interlace-linje i mode 2 för korrekt tile-rad.
                    int lineY = (g_vdp_interlace_mode == 0) ? g_scanline : GetInterlaceLine(g_scanline);
                    int w_view_dy = GetRowInCell(lineY);
                     int w_addr = (g_vdp_reg_3_windows >> 1) + ((lineY >> cellShift) * g_scroll_xcell) + w_xcell_st;
                     
                     // DEBUG: Log window info
                     if (g_scanline == 100 && _frameCounter >= 7058 && _frameCounter <= 7060)
                     {
                         Console.WriteLine($"[DEBUG-WINDOW] frame={_frameCounter} scanline={g_scanline} base=0x{(g_vdp_reg_3_windows >> 1):X4} (reg3=0x{g_vdp_reg_3_windows:X4})");
                         Console.WriteLine($"[DEBUG-WINDOW] xcell_st={w_xcell_st} xcell_ed={w_xcell_ed} lineY={lineY}");
                         for (int i = 0; i < 5 && (w_xcell_st + i) <= w_xcell_ed; i++)
                         {
                             uint val = g_renderer_vram[w_addr + i];
                             Console.WriteLine($"[DEBUG-WINDOW] tile[{i}]=0x{val:X4} pri={(val>>15)&1} char={val&0x7FF}");
                         }
                     }
                    int w_posx = w_xcell_st << 3;
                    for (int w_cx = w_xcell_st; w_cx <= w_xcell_ed; w_cx++)
                    {
                        uint w_val = ReadNameTableWord(w_addr++);
                        uint w_priority = ((w_val >> 15) & 0x0001);
                        uint w_palette  = (((w_val >> 13) & 0x0003) << 4);
                        uint w_reverse_bits = (w_val >> 11) & 0x0003;
                        uint w_char     = (w_val & 0x07ff);

                        for (int w_dx = 0; w_dx < 8; w_dx++)
                        {
                            if ((g_game_cmap[w_posx] == 0) || (g_game_primap[w_posx] <= w_priority))
                            {
                                // DIRECT VRAM MODE: Read pattern directly from vram[]
                                if (ForceDirectVramReadPlanes || w_reverse_bits != 0)
                                {
                                    uint picValueDirect = ReadPatternPixelDirect((int)w_char, w_dx, w_view_dy, w_reverse_bits, TileRebaseKind.Window);

                                    if (picValueDirect != 0)
                                    {
                                        g_game_cmap[w_posx]   = w_palette + picValueDirect;
                                        g_game_primap[w_posx] = w_priority;
                                        g_game_shadowmap[w_posx] |= w_priority;
                                    }
                                }
                                else
                                {
                                    int  w_pic_addr = GetTileWordAddress((int)w_char, w_view_dy, w_reverse_bits, TileRebaseKind.Window) + (w_dx >> 2);
                                    uint w_pic_w    = g_renderer_vram[w_pic_addr];
                                     uint picValue   = (uint)((w_pic_w >> ((3 - (w_dx & 3)) << 2)) & 0x0f);
                                    if (picValue != 0)
                                    {
                                        g_game_cmap[w_posx]   = w_palette + picValue;
                                        g_game_primap[w_posx] = w_priority;
                                        g_game_shadowmap[w_posx] |= w_priority;
                                    }
                                }
                            }
                            w_posx += 1;
                        }
                    }
                }
            SkipWindow: ;
            }

            // --- Skriv ut till framebuffern ---
            {
                if ((uint)outputLine >= (uint)g_output_ysize)
                    return;

                int w_base = outputLine * g_output_xsize;
                int visibleWidth = g_display_xsize;
                int outputWidth = g_output_xsize;
                for (int wx = 0; wx < visibleWidth; wx++)
                {
                    uint w_colnum = g_game_cmap[wx];
                     if (w_colnum == 0) w_colnum = g_vdp_reg_7_backcolor;

                     uint color;
                     if (g_vdp_reg_12_3_shadow == 0)
                     {
                         color = g_color[w_colnum];
                     }
                     else
                     {
                         uint w_shadow = g_game_shadowmap[wx];
                         color = (w_shadow == 0) ? g_color_shadow[w_colnum]
                         : (w_shadow == 2) ? g_color_highlight[w_colnum]
                         : g_color[w_colnum];
                     }
                     destBuffer[w_base + wx] = color;
                }

                if (outputWidth > visibleWidth)
                {
                    uint borderColor = g_color[g_vdp_reg_7_backcolor];
                    for (int wx = visibleWidth; wx < outputWidth; wx++)
                    {
                        destBuffer[w_base + wx] = borderColor;
                    }
                }
            }
        }
    }
}
