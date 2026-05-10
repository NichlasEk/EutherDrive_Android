// license:BSD-3-Clause
// copyright-holders:David Haywood,Edward Fast
// Ported from MAME src/mame/igs/igs023_video.cpp

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using MemoryU8 = mame.MemoryContainer<System.Byte>;
using offs_t = System.UInt32;
using PointerU32 = mame.PointerU32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using uint32_t = System.UInt32;

using static mame.device_global;
using static mame.emucore_global;
using static mame.emumem_global;

namespace mame
{
    public class igs023_video_device : device_t
    {
        public static readonly emu.detail.device_type_impl IGS023_VIDEO =
            DEFINE_DEVICE_TYPE("igs023", "IGS023 Video System", (type, mconfig, tag, owner, clock) => new igs023_video_device(mconfig, tag, owner, clock));

        const int ScreenWidth = 448;
        const int ScreenHeight = 224;
        const int BgColumns = 64;
        const int BgRows = 16;
        const int TxColumns = 64;
        const int TxRows = 32;

        readonly u16 [] m_bg_videoram = new u16[0x1000];
        readonly u16 [] m_tx_videoram = new u16[0x2000];
        readonly u16 [] m_rowscrollram = new u16[0x1000];
        readonly u16 [] m_spritebuffer = new u16[0x1000];
        readonly u16 [] m_zoomram = new u16[0x40];
        readonly Sprite [] m_spritelist = new Sprite[0x100];
        readonly byte [] m_priority = new byte[ScreenWidth * ScreenHeight];
        readonly uint [] m_bgPaletteCache = new uint[32 * 32];
        readonly uint [] m_txPaletteCache = new uint[32 * 16];
        readonly uint [] m_spritePaletteCache = new uint[32 * 32];
        readonly byte [] [] m_bgTileCache = new byte[0x10000][];

        MemoryU8 m_gfx;
        MemoryU8 m_adata;
        MemoryU8 m_bdata;
        byte [] m_gfxRaw;
        byte [] m_adataRaw;
        byte [] m_bdataRaw;
        int m_gfxBytes;
        int m_adataWords;
        int m_bdataWords;
        Func<int, uint> m_paletteReader;
        int m_spriteCount;
        u32 m_aoffset;
        u8 m_abit;
        u32 m_boffset;
        u16 m_bg_yscroll;
        u16 m_bg_xscroll;
        u16 m_bg_scale;
        u16 m_tx_yscroll;
        u16 m_tx_xscroll;
        u16 m_ctrl;

        sealed class Sprite
        {
            public int X;
            public int Y;
            public bool XGrow;
            public bool YGrow;
            public u32 XZoom;
            public u32 YZoom;
            public u32 Color;
            public u32 Offs;
            public u32 Width;
            public u32 Height;
            public u8 Flip;
            public u8 Pri;
        }

        public igs023_video_device(machine_config mconfig, string tag, device_t owner, u32 clock)
            : base(mconfig, IGS023_VIDEO, tag, owner, clock)
        {
            for (int i = 0; i < m_spritelist.Length; i++)
                m_spritelist[i] = new Sprite();
        }

        public void set_palette_reader(Func<int, uint> reader)
        {
            m_paletteReader = reader;
        }

        protected override void device_start()
        {
            memory_region gfx = memregion(DEVICE_SELF);
            if (gfx != null)
            {
                m_gfx = gfx.base_();
                m_gfxRaw = m_gfx.data_raw;
                m_gfxBytes = (int)Math.Min(gfx.bytes(), int.MaxValue);
            }

            memory_region sprcol = memregion("sprcol");
            if (sprcol != null)
            {
                m_adata = sprcol.base_();
                m_adataRaw = m_adata.data_raw;
                m_adataWords = (int)Math.Min(sprcol.bytes() / 2, int.MaxValue);
            }

            memory_region sprmask = memregion("sprmask");
            if (sprmask != null)
            {
                m_bdata = sprmask.base_();
                m_bdataRaw = m_bdata.data_raw;
                m_bdataWords = (int)Math.Min(sprmask.bytes() / 2, int.MaxValue);
            }

            save_item(NAME(new { m_bg_videoram }));
            save_item(NAME(new { m_tx_videoram }));
            save_item(NAME(new { m_rowscrollram }));
            save_item(NAME(new { m_spritebuffer }));
            save_item(NAME(new { m_zoomram }));
            SaveStateRef(nameof(m_bg_yscroll), () => m_bg_yscroll, value => m_bg_yscroll = value);
            SaveStateRef(nameof(m_bg_xscroll), () => m_bg_xscroll, value => m_bg_xscroll = value);
            SaveStateRef(nameof(m_bg_scale), () => m_bg_scale, value => m_bg_scale = value);
            SaveStateRef(nameof(m_tx_yscroll), () => m_tx_yscroll, value => m_tx_yscroll = value);
            SaveStateRef(nameof(m_tx_xscroll), () => m_tx_xscroll, value => m_tx_xscroll = value);
            SaveStateRef(nameof(m_ctrl), () => m_ctrl, value => m_ctrl = value);
            machine().save().register_postload(ParseSpriteBuffer);
        }

        void SaveStateRef<T>(string itemName, Func<T> getter, Action<T> setter)
        {
            machine().save().save_item_ref(this, name(), tag(), 0, itemName, getter, setter);
        }

        protected override void device_reset()
        {
            m_spriteCount = 0;
            m_aoffset = 0;
            m_abit = 0;
            m_boffset = 0;
            m_bg_yscroll = 0;
            m_bg_xscroll = 0;
            m_bg_scale = 0;
            m_tx_yscroll = 0;
            m_tx_xscroll = 0;
            m_ctrl = 0;
            Array.Clear(m_bg_videoram, 0, m_bg_videoram.Length);
            Array.Clear(m_tx_videoram, 0, m_tx_videoram.Length);
            Array.Clear(m_rowscrollram, 0, m_rowscrollram.Length);
            Array.Clear(m_spritebuffer, 0, m_spritebuffer.Length);
            Array.Clear(m_zoomram, 0, m_zoomram.Length);
        }

        public u16 videoram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            u32 address = (offset << 1) & 0x7fff;
            if (address < 0x4000)
                return m_bg_videoram[((address & 0x0fff) >> 1) & 0x7ff];
            if (address >= 0x7000)
                return m_rowscrollram[((address - 0x7000) >> 1) & 0x7ff];

            return m_tx_videoram[((address - 0x4000) >> 1) & 0xfff];
        }

        public void videoram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            u32 address = (offset << 1) & 0x7fff;
            if (address < 0x4000)
            {
                int index = (int)(((address & 0x0fff) >> 1) & 0x7ff);
                COMBINE_DATA(ref m_bg_videoram[index], data, mem_mask);
            }
            else if (address >= 0x7000)
            {
                int index = (int)(((address - 0x7000) >> 1) & 0x7ff);
                COMBINE_DATA(ref m_rowscrollram[index], data, mem_mask);
            }
            else
            {
                int index = (int)(((address - 0x4000) >> 1) & 0xfff);
                COMBINE_DATA(ref m_tx_videoram[index], data, mem_mask);
            }
        }

        public u16 videoregs_r(address_space space, offs_t offset, u16 mem_mask)
        {
            u32 address = (offset << 1) & 0xffff;
            if (address < 0x1000)
                return m_spritebuffer[(address >> 1) & 0x7ff];
            if (address == 0x2000)
                return m_bg_yscroll;
            if (address == 0x3000)
                return m_bg_xscroll;
            if (address == 0x4000)
                return m_bg_scale;
            if (address == 0x5000)
                return m_tx_yscroll;
            if (address == 0x6000)
                return m_tx_xscroll;
            if (address == 0xe000)
                return m_ctrl;

            return 0;
        }

        public void videoregs_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            u32 address = (offset << 1) & 0xffff;
            if (address < 0x1000)
            {
                int index = (int)((address >> 1) & 0x7ff);
                COMBINE_DATA(ref m_spritebuffer[index], data, mem_mask);
            }
            else if (address >= 0x1000 && address <= 0x103f)
            {
                int index = (int)((address - 0x1000) >> 1);
                COMBINE_DATA(ref m_zoomram[index], data, mem_mask);
            }
            else if (address == 0x2000)
                COMBINE_DATA(ref m_bg_yscroll, data, mem_mask);
            else if (address == 0x3000)
                COMBINE_DATA(ref m_bg_xscroll, data, mem_mask);
            else if (address == 0x4000)
                COMBINE_DATA(ref m_bg_scale, data, mem_mask);
            else if (address == 0x5000)
                COMBINE_DATA(ref m_tx_yscroll, data, mem_mask);
            else if (address == 0x6000)
                COMBINE_DATA(ref m_tx_xscroll, data, mem_mask);
            else if (address == 0xe000)
                COMBINE_DATA(ref m_ctrl, data, mem_mask);
        }

        public void get_sprites(Func<offs_t, u16> readSpriteRam)
        {
            if (!sprite_dma(readSpriteRam))
                return;

            ParseSpriteBuffer();
        }

        void ParseSpriteBuffer()
        {
            m_spriteCount = 0;
            for (int spriteNum = 0; spriteNum < 0x1000 / 2 && m_spriteCount < m_spritelist.Length; spriteNum += 8)
            {
                u16 spr4 = m_spritebuffer[spriteNum + 4];
                if ((spr4 & 0x7fff) == 0)
                    break;

                Sprite sprite = m_spritelist[m_spriteCount++];
                u16 spr0 = m_spritebuffer[spriteNum + 0];
                bool xgrow = (spr0 & 0x8000) != 0;
                int xzom = (spr0 & 0x7800) >> 11;
                sprite.X = SignExtend(spr0 & 0x07ff, 11);

                u16 spr1 = m_spritebuffer[spriteNum + 1];
                bool ygrow = (spr1 & 0x8000) != 0;
                int yzom = (spr1 & 0x7800) >> 11;
                sprite.Y = SignExtend(spr1 & 0x03ff, 10);

                u16 spr2 = m_spritebuffer[spriteNum + 2];
                u16 spr3 = m_spritebuffer[spriteNum + 3];

                sprite.Flip = (u8)((spr2 & 0x6000) >> 13);
                sprite.Color = (u32)((spr2 & 0x1f00) >> 8);
                sprite.Pri = (u8)((spr2 & 0x0080) >> 7);
                sprite.Offs = (u32)(((spr2 & 0x007f) << 16) | spr3);
                sprite.Width = (u32)((spr4 & 0x7e00) >> 9);
                sprite.Height = (u32)(spr4 & 0x01ff);

                if (xgrow)
                    xzom = 0x10 - xzom;
                if (ygrow)
                    yzom = 0x10 - yzom;

                sprite.XZoom = xzom < 0x10 ? (xzom == 0x0f ? 1U : (u32)((m_zoomram[xzom * 2] << 16) | m_zoomram[xzom * 2 + 1])) : 0;
                sprite.YZoom = yzom < 0x10 ? (yzom == 0x0f ? 1U : (u32)((m_zoomram[yzom * 2] << 16) | m_zoomram[yzom * 2 + 1])) : 0;
                sprite.XGrow = xgrow;
                sprite.YGrow = ygrow;
            }
        }

        public uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            int minY = Math.Max(cliprect.min_y, 0);
            int maxY = Math.Min(cliprect.max_y, ScreenHeight - 1);
            int minX = Math.Max(cliprect.min_x, 0);
            int maxX = Math.Min(cliprect.max_x, ScreenWidth - 1);
            UpdatePaletteCaches();
            uint backdrop = PalettePen(0x3ff);

            Array.Clear(m_priority, 0, m_priority.Length);
            for (int y = minY; y <= maxY; y++)
            {
                PointerU32 row = bitmap.pix(y);
                for (int x = minX; x <= maxX; x++)
                    row[x] = backdrop;
            }

            if ((m_ctrl & 0x1000) == 0)
                DrawBackground(bitmap, minX, maxX, minY, maxY);

            DrawSprites(bitmap, minX, maxX, minY, maxY);

            if ((m_ctrl & 0x0800) == 0)
                DrawText(bitmap, minX, maxX, minY, maxY);

            return 0;
        }

        bool sprite_dma(Func<offs_t, u16> readSpriteRam)
        {
            if ((m_ctrl & 0x0001) == 0 || readSpriteRam == null)
                return false;

            u16 [] ramMask = { 0xffff, 0xfbff, 0x7fff, 0xffff, 0xffff };
            int offs = 0;
            for (int i = 0, dst = 0; i < 256; i++, dst += 8)
            {
                for (int src = 0; src < 5; src++)
                    m_spritebuffer[dst + src] = (u16)(readSpriteRam((offs_t)offs++) & ramMask[src]);
                if ((m_spritebuffer[dst + 4] & 0x7fff) == 0)
                    return true;
            }

            return true;
        }

        void UpdatePaletteCaches()
        {
            for (int i = 0; i < m_bgPaletteCache.Length; i++)
                m_bgPaletteCache[i] = PalettePen(0x400 + i);

            for (int i = 0; i < m_txPaletteCache.Length; i++)
                m_txPaletteCache[i] = PalettePen(0x800 + i);

            for (int i = 0; i < m_spritePaletteCache.Length; i++)
                m_spritePaletteCache[i] = PalettePen(i);
        }

        void DrawBackground(bitmap_rgb32 bitmap, int minX, int maxX, int minY, int maxY)
        {
            if (m_gfxRaw == null || m_gfxBytes <= 0)
                return;

            for (int y = minY; y <= maxY; y++)
            {
                int srcY = (y + m_bg_yscroll) & 0x1ff;
                int tileY = (srcY >> 5) & (BgRows - 1);
                int rowBase = tileY * BgColumns * 2;
                int pyBase = srcY & 31;
                int rowScroll = m_rowscrollram[y & 0x7ff];
                int srcXBase = minX + m_bg_xscroll + rowScroll;
                int priorityBase = y * ScreenWidth;
                PointerU32 row = bitmap.pix(y);

                for (int x = minX; x <= maxX;)
                {
                    int srcX = srcXBase & 0x7ff;
                    int tileX = (srcX >> 5) & (BgColumns - 1);
                    int tileIndex = rowBase + tileX * 2;
                    u16 tile = m_bg_videoram[tileIndex & 0x7ff];
                    u16 attr = m_bg_videoram[(tileIndex + 1) & 0x7ff];
                    int px = srcX & 31;
                    int py = (attr & 0x0080) != 0 ? 31 - pyBase : pyBase;
                    int palette = (attr & 0x003e) >> 1;
                    int paletteBase = palette * 32;
                    int run = Math.Min(maxX - x + 1, 32 - px);
                    byte [] decodedTile = GetDecodedBgTile(tile);
                    int decodedRowBase = py << 5;

                    if ((attr & 0x0040) != 0)
                    {
                        int flippedPx = 31 - px;
                        for (int i = 0; i < run; i++, x++, srcXBase++, flippedPx--)
                        {
                            int pen = decodedTile != null
                                ? decodedTile[decodedRowBase + flippedPx]
                                : DecodeBgPixel(tile, flippedPx, py);
                            if (pen == 31)
                                continue;

                            row[x] = m_bgPaletteCache[paletteBase + pen];
                            m_priority[priorityBase + x] |= 2;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < run; i++, x++, srcXBase++, px++)
                        {
                            int pen = decodedTile != null
                                ? decodedTile[decodedRowBase + px]
                                : DecodeBgPixel(tile, px, py);
                            if (pen == 31)
                                continue;

                            row[x] = m_bgPaletteCache[paletteBase + pen];
                            m_priority[priorityBase + x] |= 2;
                        }
                    }
                }
            }
        }

        void DrawText(bitmap_rgb32 bitmap, int minX, int maxX, int minY, int maxY)
        {
            if (m_gfxRaw == null || m_gfxBytes <= 0)
                return;

            for (int y = minY; y <= maxY; y++)
            {
                int srcY = (y + m_tx_yscroll) & 0xff;
                int tileY = (srcY >> 3) & (TxRows - 1);
                int rowBase = tileY * TxColumns * 2;
                int pyBase = srcY & 7;
                int srcXBase = minX + m_tx_xscroll;
                PointerU32 row = bitmap.pix(y);

                for (int x = minX; x <= maxX;)
                {
                    int srcX = srcXBase & 0x1ff;
                    int tileX = (srcX >> 3) & (TxColumns - 1);
                    int tileIndex = rowBase + tileX * 2;
                    u16 tile = m_tx_videoram[tileIndex & 0xfff];
                    u16 attr = m_tx_videoram[(tileIndex + 1) & 0xfff];
                    int px = srcX & 7;
                    int py = (attr & 0x0080) != 0 ? 7 - pyBase : pyBase;
                    int palette = (attr & 0x003e) >> 1;
                    int paletteBase = palette * 16;
                    int run = Math.Min(maxX - x + 1, 8 - px);

                    if ((attr & 0x0040) != 0)
                    {
                        int flippedPx = 7 - px;
                        for (int i = 0; i < run; i++, x++, srcXBase++, flippedPx--)
                        {
                            int pen = DecodeTxPixel(tile, flippedPx, py);
                            if (pen == 15)
                                continue;

                            row[x] = m_txPaletteCache[paletteBase + pen];
                        }
                    }
                    else
                    {
                        for (int i = 0; i < run; i++, x++, srcXBase++, px++)
                        {
                            int pen = DecodeTxPixel(tile, px, py);
                            if (pen == 15)
                                continue;

                            row[x] = m_txPaletteCache[paletteBase + pen];
                        }
                    }
                }
            }
        }

        void DrawSprites(bitmap_rgb32 bitmap, int minX, int maxX, int minY, int maxY)
        {
            for (int i = m_spriteCount - 1; i >= 0; i--)
            {
                Sprite sprite = m_spritelist[i];
                if ((m_ctrl & 0x2000) != 0 && sprite.Pri == 0)
                    continue;

                m_boffset = sprite.Offs;
                if (sprite.XZoom == 0 && sprite.YZoom == 0)
                    DrawSpriteBasic(sprite, bitmap, minX, maxX, minY, maxY);
                else
                    DrawSpriteZoomed(sprite, bitmap, minX, maxX, minY, maxY);
            }
        }

        void DrawSpriteBasic(Sprite sprite, bitmap_rgb32 bitmap, int minX, int maxX, int minY, int maxY)
        {
            if (m_adataRaw == null || m_bdataRaw == null || m_adataWords == 0 || m_bdataWords == 0 || sprite.Width == 0 || sprite.Height == 0)
                return;

            m_aoffset = (((u32)ReadMaskWord(m_boffset + 1) << 16) | ReadMaskWord(m_boffset)) >> 2;
            m_abit = 0;
            m_boffset += 2;
            int realYSize = (int)sprite.Height - 1;
            int realXSize = (int)(sprite.Width * 16) - 1;

            for (int ycnt = 0; ycnt < sprite.Height; ycnt++)
            {
                int ydrawpos = !GetFlipY(sprite.Flip) ? sprite.Y + ycnt : sprite.Y + realYSize - ycnt;
                bool draw = ydrawpos >= minY && ydrawpos <= maxY;
                DrawSpriteLineBasic((int)sprite.Width, bitmap, minX, maxX, ydrawpos, sprite.Flip, sprite.X, (int)sprite.Pri, realXSize, (int)sprite.Color, draw);
                if (!draw && ((!GetFlipY(sprite.Flip) && ydrawpos >= maxY) || (GetFlipY(sprite.Flip) && ydrawpos < minY)))
                    return;
            }
        }

        void DrawSpriteZoomed(Sprite sprite, bitmap_rgb32 bitmap, int minX, int maxX, int minY, int maxY)
        {
            if (m_adataRaw == null || m_bdataRaw == null || m_adataWords == 0 || m_bdataWords == 0 || sprite.Width == 0 || sprite.Height == 0)
                return;

            m_aoffset = (((u32)ReadMaskWord(m_boffset + 1) << 16) | ReadMaskWord(m_boffset)) >> 2;
            m_abit = 0;
            m_boffset += 2;

            int realYSize = 0;
            for (int y = 0; y < sprite.Height; y++)
            {
                bool yzoomBit = ((sprite.YZoom >> (y & 0x1f)) & 1) != 0;
                if (sprite.YGrow || !yzoomBit)
                    realYSize += yzoomBit ? 2 : 1;
            }
            realYSize--;

            int realXSize = 0;
            for (int x = 0; x < sprite.Width * 16; x++)
            {
                bool xzoomBit = ((sprite.XZoom >> (x & 0x1f)) & 1) != 0;
                if (sprite.XGrow || !xzoomBit)
                    realXSize += xzoomBit ? 2 : 1;
            }
            realXSize--;

            int ycntdraw = 0;
            for (int ycnt = 0; ycnt < sprite.Height; ycnt++)
            {
                bool yzoomBit = ((sprite.YZoom >> (ycnt & 0x1f)) & 1) != 0;
                if (yzoomBit && !sprite.YGrow)
                {
                    DrawSpriteLine((int)sprite.Width, bitmap, minX, maxX, 0, sprite.XZoom, sprite.XGrow, sprite.Flip, sprite.X, (int)sprite.Pri, realXSize, (int)sprite.Color, false);
                    continue;
                }

                int repeats = yzoomBit && sprite.YGrow ? 2 : 1;
                int saveAOffset = (int)m_aoffset;
                u8 saveABit = m_abit;
                u32 saveBOffset = m_boffset;
                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    if (repeat != 0)
                    {
                        m_aoffset = (u32)saveAOffset;
                        m_abit = saveABit;
                        m_boffset = saveBOffset;
                    }

                    int ydrawpos = !GetFlipY(sprite.Flip) ? sprite.Y + ycntdraw : sprite.Y + realYSize - ycntdraw;
                    bool draw = ydrawpos >= minY && ydrawpos <= maxY;
                    DrawSpriteLine((int)sprite.Width, bitmap, minX, maxX, ydrawpos, sprite.XZoom, sprite.XGrow, sprite.Flip, sprite.X, (int)sprite.Pri, realXSize, (int)sprite.Color, draw);
                    if (!draw && ((!GetFlipY(sprite.Flip) && ydrawpos >= maxY) || (GetFlipY(sprite.Flip) && ydrawpos < minY)))
                        return;
                    ycntdraw++;
                }
            }
        }

        void DrawSpriteLineBasic(int wide, bitmap_rgb32 bitmap, int minX, int maxX, int ydrawpos, int flip, int xpos, int pri, int realXSize, int palette, bool draw)
        {
            int xcntdraw = 0;
            PointerU32 row = draw && ydrawpos >= 0 && ydrawpos < ScreenHeight ? bitmap.pix(ydrawpos) : null;
            for (int xcnt = 0; xcnt < wide; xcnt++)
            {
                u16 mask = ReadMaskWord(m_boffset);
                for (int x = 0; x < 16; x++)
                {
                    if ((mask & 1) == 0)
                    {
                        uint color = m_spritePaletteCache[palette * 32 + GetSpritePix()];
                        if (draw)
                            DrawSpritePixel(row, minX, maxX, ydrawpos, xpos, flip, pri, realXSize, color, ref xcntdraw);
                        else
                            xcntdraw++;
                    }
                    else
                        xcntdraw++;

                    mask >>= 1;
                }
                m_boffset++;
            }
        }

        void DrawSpriteLine(int wide, bitmap_rgb32 bitmap, int minX, int maxX, int ydrawpos, u32 xzoom, bool xgrow, int flip, int xpos, int pri, int realXSize, int palette, bool draw)
        {
            int xoffset = 0;
            int xcntdraw = 0;
            PointerU32 row = draw && ydrawpos >= 0 && ydrawpos < ScreenHeight ? bitmap.pix(ydrawpos) : null;
            for (int xcnt = 0; xcnt < wide; xcnt++)
            {
                u16 mask = ReadMaskWord(m_boffset);
                for (int x = 0; x < 16; x++)
                {
                    bool xzoomBit = ((xzoom >> (xoffset & 0x1f)) & 1) != 0;
                    xoffset++;

                    if ((mask & 1) == 0)
                    {
                        uint color = m_spritePaletteCache[palette * 32 + GetSpritePix()];
                        if (draw && (xgrow || !xzoomBit))
                        {
                            int count = xzoomBit ? 2 : 1;
                            for (int i = 0; i < count; i++)
                                DrawSpritePixel(row, minX, maxX, ydrawpos, xpos, flip, pri, realXSize, color, ref xcntdraw);
                        }
                    }
                    else if (xgrow || !xzoomBit)
                    {
                        xcntdraw += xzoomBit ? 2 : 1;
                    }

                    mask >>= 1;
                }
                m_boffset++;
            }
        }

        void DrawSpritePixel(PointerU32 row, int minX, int maxX, int ydrawpos, int xpos, int flip, int pri, int realXSize, uint color, ref int xcntdraw)
        {
            int xdrawpos = !GetFlipX(flip) ? xpos + xcntdraw : xpos + realXSize - xcntdraw;
            if (row != null && xdrawpos >= minX && xdrawpos <= maxX)
            {
                int priorityIndex = ydrawpos * ScreenWidth + xdrawpos;
                if ((m_priority[priorityIndex] & 1) == 0)
                {
                    if (pri == 0 || (m_priority[priorityIndex] & 2) == 0)
                        row[xdrawpos] = color;
                }
                m_priority[priorityIndex] |= 1;
            }

            xcntdraw++;
        }

        u8 GetSpritePix()
        {
            u8 src = (u8)((ReadColorWord(m_aoffset) >> m_abit) & 0x1f);
            m_abit += 5;
            if (m_abit >= 15)
            {
                m_aoffset++;
                m_abit = 0;
            }
            return src;
        }

        int DecodeTxPixel(u16 tile, int x, int y)
        {
            int offset = tile * 32 + y * 4 + (x >> 1);
            if ((uint)offset >= (uint)m_gfxBytes)
                return 15;

            u8 packed = m_gfxRaw[offset];
            return (x & 1) == 0 ? packed & 0x0f : (packed >> 4) & 0x0f;
        }

        int DecodeBgPixel(u16 tile, int x, int y)
        {
            byte [] decoded = GetDecodedBgTile(tile);
            if (decoded != null)
                return decoded[(y << 5) | x];

            int bitBase = tile * 32 * 32 * 5 + y * 32 * 5 + x * 5;
            int byteOffset = bitBase >> 3;
            if ((uint)(byteOffset + 1) >= (uint)m_gfxBytes)
                return 31;

            int bits = m_gfxRaw[byteOffset] | (m_gfxRaw[byteOffset + 1] << 8);
            return (bits >> (bitBase & 7)) & 0x1f;
        }

        byte [] GetDecodedBgTile(u16 tile)
        {
            if (m_gfxRaw == null)
                return null;

            byte [] decoded = m_bgTileCache[tile];
            if (decoded != null)
                return decoded;

            int tileBitBase = tile * 32 * 32 * 5;
            int lastByte = (tileBitBase + (32 * 32 * 5) - 1) >> 3;
            if ((uint)(lastByte + 1) >= (uint)m_gfxBytes)
                return null;

            decoded = new byte[32 * 32];
            for (int y = 0; y < 32; y++)
            {
                int rowBitBase = tileBitBase + y * 32 * 5;
                int rowOffset = y << 5;
                for (int x = 0; x < 32; x++)
                {
                    int bitBase = rowBitBase + x * 5;
                    int byteOffset = bitBase >> 3;
                    int bits = m_gfxRaw[byteOffset] | (m_gfxRaw[byteOffset + 1] << 8);
                    decoded[rowOffset + x] = (byte)((bits >> (bitBase & 7)) & 0x1f);
                }
            }

            m_bgTileCache[tile] = decoded;
            return decoded;
        }

        u16 ReadColorWord(u32 offset)
        {
            if (m_adataRaw == null || m_adataWords == 0)
                return 0xffff;

            int word = (int)(offset & (u32)(m_adataWords - 1));
            int byteOffset = word << 1;
            return (u16)(m_adataRaw[byteOffset] | (m_adataRaw[byteOffset + 1] << 8));
        }

        u16 ReadMaskWord(u32 offset)
        {
            if (m_bdataRaw == null || m_bdataWords == 0)
                return 0xffff;

            int word = (int)(offset & (u32)(m_bdataWords - 1));
            int byteOffset = word << 1;
            return (u16)(m_bdataRaw[byteOffset] | (m_bdataRaw[byteOffset + 1] << 8));
        }

        uint PalettePen(int pen)
        {
            if (m_paletteReader != null)
                return m_paletteReader(pen);

            return 0;
        }

        static bool GetFlipY(int flip) => (flip & 2) != 0;
        static bool GetFlipX(int flip) => (flip & 1) != 0;

        static int SignExtend(int value, int bits)
        {
            int shift = 32 - bits;
            return (value << shift) >> shift;
        }
    }

    public static class igs023_video_global
    {
        public static igs023_video_device IGS023_VIDEO(machine_config mconfig, string tag, u32 clock)
        {
            return emu.detail.device_type_impl.op<igs023_video_device>(mconfig, tag, igs023_video_device.IGS023_VIDEO, clock);
        }

        public static igs023_video_device IGS023_VIDEO<bool_Required>(machine_config mconfig, device_finder<igs023_video_device, bool_Required> finder, u32 clock)
            where bool_Required : bool_const, new()
        {
            return emu.detail.device_type_impl.op(mconfig, finder, igs023_video_device.IGS023_VIDEO, clock);
        }
    }
}
