using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        // Debug-buffertar (ARGB32 per pixel), ersätter Bitmap-fälten
        // Storlekarna matchar originalets bilder
        public const int PATTERN_TABLE_WIDTH  = 128;
        public const int PATTERN_TABLE_HEIGHT = 1024;

        public const int SCROLL_TEX_WIDTH  = 1024;
        public const int SCROLL_TEX_HEIGHT = 1024;

        public const int SPRITE_TEX_WIDTH  = 512;
        public const int SPRITE_TEX_HEIGHT = 512;

        public uint[] g_scrollA_pixels;   // 1024x1024
        public uint[] g_scrollB_pixels;   // 1024x1024
        public uint[] g_scrollW_pixels;   // 1024x1024
        public uint[] g_scrollS_pixels;   // 512x512
        public uint[] g_pattern_pixels;   // 128x1024

        private uint[] MONOCOLOR_TABLE;   // 16 nyanser, fylls i initialize()
        private bool[] g_sprite_enable;   // 80 flaggor, fylls i initialize()

        /// <summary>
        /// Fyll debug-pixlar baserat på snapshot-data.
        /// Kalla denna efter att en frame är färdigrenderad (t.ex. i rendering_frame()).
        /// </summary>
        private void rendering_data()
        {
            EnsureDebugSurfaces();

            // --- Pattern table ---
            // Läser g_snap_renderer_vram (4 pages: normal/flipH/flipV/flipHV) och visar mono-tiles.
            // Layout: 16 tiles per rad, 8x8 pixlar per tile => 128 px bred, 1024 px hög.
            Array.Clear(g_pattern_pixels, 0, g_pattern_pixels.Length);
            for (int ch = 0; ch < PATTERN_MAX; ch++)
            {
                if (!g_pattern_chk[ch]) continue;
                g_pattern_chk[ch] = false;

                int tileX = (ch & 0x0F) * 8;
                int tileY = ((ch >> 4) & 0x3F) * 16; // 2048 tiles / 16 = 128 rader; varje rad 8px hög -> 1024px
                int baseAddr = ch << 4; // 16 ord per tile (8*2)

                for (int dy = 0; dy < 8; dy++)
                {
                    for (int dx = 0; dx < 8; dx++)
                    {
                        uint w = g_snap_renderer_vram[baseAddr + (dy << 1) + (dx >> 2)];
                        uint nib = (w >> ((3 - (dx & 3)) << 2)) & 0x0F;
                        uint col = MONOCOLOR_TABLE[nib];
                        PutPixel(g_pattern_pixels, PATTERN_TABLE_WIDTH, PATTERN_TABLE_HEIGHT,
                                 tileX + dx, tileY + dy, col);
                    }
                }
            }

            // --- Scroll A ---
            DrawScrollLayer(
                dest: g_scrollA_pixels,
                width: SCROLL_TEX_WIDTH,
                height: SCROLL_TEX_HEIGHT,
                tableWordIndex: g_vdp_reg_2_scrolla >> 1
            );

            // --- Scroll B ---
            DrawScrollLayer(
                dest: g_scrollB_pixels,
                width: SCROLL_TEX_WIDTH,
                height: SCROLL_TEX_HEIGHT,
                tableWordIndex: g_vdp_reg_4_scrollb >> 1
            );

            // --- Window ---
            Array.Clear(g_scrollW_pixels, 0, g_scrollW_pixels.Length);
            for (int wy = 0; wy < g_scroll_ycell; wy++)
            {
                int wordIndex = (g_vdp_reg_3_windows >> 1) + (wy * g_scroll_xcell);
                for (int wx = 0; wx < g_scroll_xcell; wx++, wordIndex++)
                {
                    uint w = g_snap_renderer_vram[wordIndex];
                    uint pal = ((w >> 13) & 0x3) << 4;
                    uint rev = (w >> 11) & 0x3; // 0:normal 1:H 2:V 3:HV
                    uint chr = (w & 0x07FF);
                    int picBase = (int)((rev * VRAM_DATASIZE) + (chr << 4));
                    int px = wx * 8;
                    int py = wy * 8;

                    Blit8x8(g_scrollW_pixels, SCROLL_TEX_WIDTH, SCROLL_TEX_HEIGHT,
                            picBase, pal, px, py);
                }
            }

            // --- Sprites (g_scrollS_pixels) ---
            DrawSpriteSheetDebug();
        }

        // ============================================================
        // Helpers
        // ============================================================

        private void EnsureDebugSurfaces()
        {
            g_scrollA_pixels ??= new uint[SCROLL_TEX_WIDTH * SCROLL_TEX_HEIGHT];
            g_scrollB_pixels ??= new uint[SCROLL_TEX_WIDTH * SCROLL_TEX_HEIGHT];
            g_scrollW_pixels ??= new uint[SCROLL_TEX_WIDTH * SCROLL_TEX_HEIGHT];
            g_scrollS_pixels ??= new uint[SPRITE_TEX_WIDTH * SPRITE_TEX_HEIGHT];
            g_pattern_pixels ??= new uint[PATTERN_TABLE_WIDTH * PATTERN_TABLE_HEIGHT];
        }

        private static void PutPixel(uint[] buf, int w, int h, int x, int y, uint argb)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            buf[y * w + x] = argb;
        }

        private void Blit8x8(uint[] dest, int dw, int dh, int picBase, uint pal, int dstX, int dstY)
        {
            for (int dy = 0; dy < 8; dy++)
            {
                for (int dx = 0; dx < 8; dx++)
                {
                    uint w = g_snap_renderer_vram[picBase + (dy << 1) + (dx >> 2)];
                    uint nib = (w >> ((3 - (dx & 3)) << 2)) & 0x0F;
                    uint color = g_snap_color[pal + nib];
                    PutPixel(dest, dw, dh, dstX + dx, dstY + dy, color);
                }
            }
        }

        private void DrawScrollLayer(uint[] dest, int width, int height, int tableWordIndex)
        {
            Array.Clear(dest, 0, dest.Length);

            int wordIndex = tableWordIndex;
            for (int wy = 0; wy < g_scroll_ycell; wy++)
            {
                for (int wx = 0; wx < g_scroll_xcell; wx++, wordIndex++)
                {
                    uint w = g_snap_renderer_vram[wordIndex];
                    uint pal = ((w >> 13) & 0x3) << 4;
                    uint rev = (w >> 11) & 0x3; // 0:normal 1:H 2:V 3:HV
                    uint chr = (w & 0x07FF);
                    int picBase = (int)((rev * VRAM_DATASIZE) + (chr << 4));
                    int px = wx * 8;
                    int py = wy * 8;

                    Blit8x8(dest, width, height, picBase, pal, px, py);
                }
            }
        }

        private void DrawSpriteSheetDebug()
        {
            Array.Clear(g_scrollS_pixels, 0, g_scrollS_pixels.Length);

            // Markera aktiva sprites via link-kedja
            for (int i = 0; i < g_max_sprite_num; i++) g_sprite_enable[i] = false;

            int link = 0;
            for (int i = 0; i < g_max_sprite_num; i++)
            {
                int addr = (g_vdp_reg_5_sprite >> 1) + (link << 2);
                ushort val2 = (ushort)g_snap_renderer_vram[addr + 1];
                link = val2 & 0x007F;
                if (link >= g_max_sprite_num) break;
                g_sprite_enable[link] = true;
            }

            // Rita alla sprites (enkelt, utan priority-mix—det här är en debugsurface)
            for (int i = g_max_sprite_num - 1; i >= 0; i--)
            {
                int addr = (g_vdp_reg_5_sprite >> 1) + (i << 2);
                ushort v1 = (ushort)g_snap_renderer_vram[addr + 0];
                ushort v2 = (ushort)g_snap_renderer_vram[addr + 1];
                ushort v3 = (ushort)g_snap_renderer_vram[addr + 2];
                ushort v4 = (ushort)g_snap_renderer_vram[addr + 3];

                int posX = v4 & 0x01FF;
                int posY = v1 & g_sprite_vmask;
                int xCells = ((v2 >> 10) & 0x3) + 1;
                int yCells = ((v2 >> 8) & 0x3) + 1;
                int picW = xCells << 3;
                int picH = yCells << 3;
                int pal  = ((v3 >> 13) & 0x3) << 4;
                int rev  = ((v3 >> 11) & 0x3);
                int chr  = v3 & 0x07FF;

                for (int cy = 0; cy < yCells; cy++)
                {
                    for (int cx = 0; cx < xCells; cx++)
                    {
                        int chrCur;
                        switch (rev)
                        {
                            case 0: chrCur = chr + (yCells * cx) + cy; break;                          // normal
                            case 1: chrCur = chr + (yCells * (xCells - cx - 1)) + cy; break;          // H
                            case 2: chrCur = chr + (yCells * cx) + (yCells - cy - 1); break;          // V
                            default: chrCur = chr + (yCells * (xCells - cx - 1)) + (yCells - cy - 1); // HV
                            break;
                        }
                        if (chrCur > 0x7FF) continue;

                        int picBase = (int)((rev * VRAM_DATASIZE) + (chrCur << 4));
                        int dx0 = posX + (cx * 8);
                        int dy0 = posY + (cy * 8);

                        for (int dy = 0; dy < 8; dy++)
                        {
                            for (int dx = 0; dx < 8; dx++)
                            {
                                int sx = dx0 + dx;
                                int sy = dy0 + dy;
                                if ((uint)sx >= SPRITE_TEX_WIDTH || (uint)sy >= SPRITE_TEX_HEIGHT) continue;

                                uint w = g_snap_renderer_vram[picBase + (dy << 1) + (dx >> 2)];
                                uint nib = (w >> ((3 - (dx & 3)) << 2)) & 0x0F;
                                uint color = g_snap_color[pal + nib];

                                g_scrollS_pixels[sy * SPRITE_TEX_WIDTH + sx] = color;
                            }
                        }
                    }
                }

                // Rita ram runt sprite (grön om aktiv, röd annars) – debug
                uint boxCol = g_sprite_enable[i] ? 0xFF00FF00u : 0xFFFF0000u;
                // top/bottom
                for (int x = 0; x < picW; x++)
                {
                    int sx1 = posX + x;
                    int sy1 = posY;
                    int sy2 = posY + picH - 1;
                    if ((uint)sx1 < SPRITE_TEX_WIDTH && (uint)sy1 < SPRITE_TEX_HEIGHT)
                        g_scrollS_pixels[sy1 * SPRITE_TEX_WIDTH + sx1] = boxCol;
                    if ((uint)sx1 < SPRITE_TEX_WIDTH && (uint)sy2 < SPRITE_TEX_HEIGHT)
                        g_scrollS_pixels[sy2 * SPRITE_TEX_WIDTH + sx1] = boxCol;
                }
                // left/right
                for (int y = 0; y < picH; y++)
                {
                    int sx1 = posX;
                    int sx2 = posX + picW - 1;
                    int sy1 = posY + y;
                    if ((uint)sx1 < SPRITE_TEX_WIDTH && (uint)sy1 < SPRITE_TEX_HEIGHT)
                        g_scrollS_pixels[sy1 * SPRITE_TEX_WIDTH + sx1] = boxCol;
                    if ((uint)sx2 < SPRITE_TEX_WIDTH && (uint)sy1 < SPRITE_TEX_HEIGHT)
                        g_scrollS_pixels[sy1 * SPRITE_TEX_WIDTH + sx2] = boxCol;
                }
            }

            // Rita en visningsram (grön) för den aktiva displayytan mitt på sprite-bilden (matchar originalets hjälplinjer)
            uint frameCol = 0xFF00FF00u;
            int fx0 = 128, fy0 = 128;
            int fx1 = fx0 + g_display_xsize - 1;
            int fy1 = fy0 + g_display_ysize - 1;

            for (int x = fx0; x <= fx1; x++)
            {
                if ((uint)x < SPRITE_TEX_WIDTH)
                {
                    if ((uint)fy0 < SPRITE_TEX_HEIGHT) g_scrollS_pixels[fy0 * SPRITE_TEX_WIDTH + x] = frameCol;
                    if ((uint)fy1 < SPRITE_TEX_HEIGHT) g_scrollS_pixels[fy1 * SPRITE_TEX_WIDTH + x] = frameCol;
                }
            }
            for (int y = fy0; y <= fy1; y++)
            {
                if ((uint)y < SPRITE_TEX_HEIGHT)
                {
                    if ((uint)fx0 < SPRITE_TEX_WIDTH) g_scrollS_pixels[y * SPRITE_TEX_WIDTH + fx0] = frameCol;
                    if ((uint)fx1 < SPRITE_TEX_WIDTH) g_scrollS_pixels[y * SPRITE_TEX_WIDTH + fx1] = frameCol;
                }
            }
        }
    }
}
