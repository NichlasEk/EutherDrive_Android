// license:BSD-3-Clause
// copyright-holders:Carlos A. Lozano, Rob Rosenbrock, Phil Stroffolino
// Ported from MAME xain_v.cpp

using System;

using offs_t = System.UInt32;
using s32 = System.Int32;
using tilemap_memory_index = System.UInt32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using uint8_t = System.Byte;

using static mame.digfx_global;
using static mame.drawgfx_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.tilemap_global;


namespace mame
{
    partial class xain_state : driver_device
    {
        void get_bg_tile_info_0(tilemap_t tilemap, ref tile_data tileinfo, tilemap_memory_index tile_index)
        {
            u8 attr = m_bgram[0][tile_index | 0x400U].op;
            tileinfo.set(2,
                    (u32)(m_bgram[0][tile_index].op | ((attr & 7) << 8)),
                    (u32)((attr & 0x70) >> 4),
                    (attr & 0x80) != 0 ? TILE_FLIPX : (u8)0);
        }

        void get_bg_tile_info_1(tilemap_t tilemap, ref tile_data tileinfo, tilemap_memory_index tile_index)
        {
            u8 attr = m_bgram[1][tile_index | 0x400U].op;
            tileinfo.set(1,
                    (u32)(m_bgram[1][tile_index].op | ((attr & 7) << 8)),
                    (u32)((attr & 0x70) >> 4),
                    (attr & 0x80) != 0 ? TILE_FLIPX : (u8)0);
        }

        void get_char_tile_info(tilemap_t tilemap, ref tile_data tileinfo, tilemap_memory_index tile_index)
        {
            u8 attr = m_charram[tile_index | 0x400U].op;
            tileinfo.set(0,
                    (u32)(m_charram[tile_index].op | ((attr & 3) << 8)),
                    (u32)((attr & 0xe0) >> 5),
                    0);
        }

        tilemap_memory_index back_scan(u32 col, u32 row, u32 num_cols, u32 num_rows)
        {
            return (col & 0x0f) + ((row & 0x0f) << 4) + ((col & 0x10) << 4) + ((row & 0x10) << 5);
        }


        protected override void video_start()
        {
            m_bg_tilemap_0 = machine().tilemap().create(m_gfxdecode.op0, get_bg_tile_info_0, back_scan, 16, 16, 32, 32);
            m_bg_tilemap_1 = machine().tilemap().create(m_gfxdecode.op0, get_bg_tile_info_1, back_scan, 16, 16, 32, 32);
            m_char_tilemap = machine().tilemap().create(m_gfxdecode.op0, get_char_tile_info, tilemap_standard_mapper.TILEMAP_SCAN_ROWS, 8, 8, 32, 32);

            m_bg_tilemap_0.set_transparent_pen(0);
            m_bg_tilemap_1.set_transparent_pen(0);
            m_char_tilemap.set_transparent_pen(0);

            save_item(NAME(new { m_pri }));
        }


        void charram_w(offs_t offset, uint8_t data)
        {
            m_charram[offset].op = data;
            m_char_tilemap.mark_tile_dirty((tilemap_memory_index)(offset & 0x3ffU));

            if (m_trace_status && data != 0 && m_trace_ram_count < 64)
            {
                Console.Error.WriteLine($"[XAIN] charram_w offs=0x{offset:X4} data=0x{data:X2}");
                m_trace_ram_count++;
            }
        }


        void bgram_w_0(offs_t offset, uint8_t data)
        {
            m_bgram[0][offset].op = data;
            m_bg_tilemap_0.mark_tile_dirty((tilemap_memory_index)(offset & 0x3ffU));

            if (m_trace_status && data != 0 && m_trace_ram_count < 64)
            {
                Console.Error.WriteLine($"[XAIN] bgram0_w offs=0x{offset:X4} data=0x{data:X2}");
                m_trace_ram_count++;
            }
        }


        void bgram_w_1(offs_t offset, uint8_t data)
        {
            m_bgram[1][offset].op = data;
            m_bg_tilemap_1.mark_tile_dirty((tilemap_memory_index)(offset & 0x3ffU));

            if (m_trace_status && data != 0 && m_trace_ram_count < 64)
            {
                Console.Error.WriteLine($"[XAIN] bgram1_w offs=0x{offset:X4} data=0x{data:X2}");
                m_trace_ram_count++;
            }
        }


        void flipscreen_w(uint8_t data)
        {
            flip_screen_set((int)(data & 1));
        }


        void draw_sprites(bitmap_ind16 bitmap, rectangle cliprect)
        {
            for (int offs = 0; offs < (int)m_spriteram.bytes(); offs += 4)
            {
                int sx, sy, flipx, flipy;
                int attr = m_spriteram[offs + 1].op;
                int numtile = m_spriteram[offs + 2].op | ((attr & 7) << 8);
                int color = (attr & 0x38) >> 3;

                sx = 238 - m_spriteram[offs + 3].op;
                if (sx <= -7) sx += 256;
                sy = 240 - m_spriteram[offs].op;
                if (sy <= -7) sy += 256;
                flipx = attr & 0x40;
                flipy = 0;
                if (flip_screen() != 0)
                {
                    sx = 238 - sx;
                    sy = 240 - sy;
                    flipx = flipx != 0 ? 0 : 1;
                    flipy = 1;
                }

                if ((attr & 0x80) != 0)
                {
                    m_gfxdecode.op0.gfx(3).transpen(bitmap, cliprect,
                            (u32)numtile,
                            (u32)color,
                            flipx, flipy,
                            sx, flipy != 0 ? sy + 16 : sy - 16, 0);
                    m_gfxdecode.op0.gfx(3).transpen(bitmap, cliprect,
                            (u32)(numtile + 1),
                            (u32)color,
                            flipx, flipy,
                            sx, sy, 0);
                }
                else
                {
                    m_gfxdecode.op0.gfx(3).transpen(bitmap, cliprect,
                            (u32)numtile,
                            (u32)color,
                            flipx, flipy,
                            sx, sy, 0);
                }
            }
        }


        u32 screen_update(screen_device screen, bitmap_ind16 bitmap, rectangle cliprect)
        {
            switch (m_pri & 0x7)
            {
            case 0:
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, 0, 0);
                draw_sprites(bitmap, cliprect);
                m_char_tilemap.draw(screen, bitmap, cliprect, 0, 0);
                break;
            case 1:
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, 0, 0);
                draw_sprites(bitmap, cliprect);
                m_char_tilemap.draw(screen, bitmap, cliprect, 0, 0);
                break;
            case 2:
                m_char_tilemap.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, 0, 0);
                draw_sprites(bitmap, cliprect);
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, 0, 0);
                break;
            case 3:
                m_char_tilemap.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, 0, 0);
                draw_sprites(bitmap, cliprect);
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, 0, 0);
                break;
            case 4:
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                m_char_tilemap.draw(screen, bitmap, cliprect, 0, 0);
                draw_sprites(bitmap, cliprect);
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, 0, 0);
                break;
            case 5:
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                m_char_tilemap.draw(screen, bitmap, cliprect, 0, 0);
                draw_sprites(bitmap, cliprect);
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, 0, 0);
                break;
            case 6:
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                draw_sprites(bitmap, cliprect);
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, 0, 0);
                m_char_tilemap.draw(screen, bitmap, cliprect, 0, 0);
                break;
            case 7:
                m_bg_tilemap_1.draw(screen, bitmap, cliprect, TILEMAP_DRAW_OPAQUE, 0);
                draw_sprites(bitmap, cliprect);
                m_bg_tilemap_0.draw(screen, bitmap, cliprect, 0, 0);
                m_char_tilemap.draw(screen, bitmap, cliprect, 0, 0);
                break;
            }

            if (m_trace_status && cliprect.top() == 8)
            {
                if (m_trace_screen_count < 180 && ((m_trace_screen_count < 20) || ((m_trace_screen_count % 30) == 0)))
                {
                    int pixels = 0;
                    u32 fp = 2166136261U;
                    for (int y = cliprect.top(); y <= cliprect.bottom(); y++)
                    {
                        PointerU16 row = bitmap.pix(y);
                        for (int x = cliprect.left(); x <= cliprect.right(); x++)
                        {
                            u16 pix = row[x];
                            if (pix != 0)
                                pixels++;
                            fp = (fp ^ pix) * 16777619U;
                        }
                    }

                    int charNonZero = CountNonZero(m_charram, 0x800);
                    int bg0NonZero = CountNonZero(m_bgram[0], 0x800);
                    int bg1NonZero = CountNonZero(m_bgram[1], 0x800);
                    int sprNonZero = CountNonZero(m_spriteram, (int)m_spriteram.bytes());
                    Console.Error.WriteLine($"[XAIN] frame_update n={m_trace_screen_count} pri={m_pri & 7} clip={cliprect.left()},{cliprect.top()}-{cliprect.right()},{cliprect.bottom()} pixnz={pixels} fp=0x{fp:X8} ram char={charNonZero} bg0={bg0NonZero} bg1={bg1NonZero} spr={sprNonZero} vblank={m_vblank}");
                }

                m_trace_screen_count++;
            }

            return 0;
        }


        int CountNonZero(shared_ptr_finder<u8, bool_const_true> ptr, int max)
        {
            int count = 0;
            int limit = Math.Min(max, (int)ptr.bytes());
            for (int i = 0; i < limit; i++)
            {
                if (ptr[i].op != 0)
                    count++;
            }
            return count;
        }
    }
}
