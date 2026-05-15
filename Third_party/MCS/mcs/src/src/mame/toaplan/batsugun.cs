// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Minimal Toaplan Batsugun registration for Euther Drive MCS bring-up.

using System;
using device_type = mame.emu.detail.device_type_impl_base;
using MemoryU8 = mame.MemoryContainer<System.Byte>;
using offs_t = System.UInt32;
using s32 = System.Int32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using uint32_t = System.UInt32;

using static mame.diexec_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.gamedrv_global;
using static mame.hash_global;
using static mame.ioport_global;
using static mame.ioport_input_string_helper;
using static mame.ioport_ioport_type_helper;
using static mame.m68000_global;
using static mame.romentry_global;
using static mame.screen_global;

namespace mame
{
    class batsugun_state : driver_device
    {
        const int ScreenWidth = 320;
        const int ScreenHeight = 240;
        const int WorkRamWords = 0x8000;
        const int SharedRamSize = 0x10000;
        const int PaletteWords = 0x800;
        const int VdpCount = 2;
        const int VdpLayerCount = 3;
        const int VdpLayerWords = 0x800;
        const int VdpSpriteWords = 0x400;
        const int TileSize = 8;
        const int TilePixels = TileSize * TileSize;
        const int TileBytes = 16;
        const int GP9001_PRIMASK = 0x000f;
        const int GP9001_PRIMASK_TMAPS = 0x000e;

        static readonly XTAL MainClock = new XTAL(32_000_000) / 2;
        static readonly XTAL PixelClock = new XTAL(27_000_000) / 4;
        static readonly bool TraceHost = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_HOST"));
        static readonly bool TraceInput = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_INPUT"));
        static readonly bool TraceVideo = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_VIDEO"));
        static readonly bool TraceShared = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_SHARED"));
        static readonly bool TraceWorkRam = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_WORKRAM"));
        static readonly bool NativeSoundBridge = Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_ENABLE_SOUND_BRIDGE") == "1"
            || Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_SOUND") == "1";

        readonly required_device<m68000_device> m_maincpu;
        MemoryU8 m_mainrom;
        int m_mainrom_bytes;
        readonly u16[] m_workram = new u16[WorkRamWords];
        readonly u8[] m_sharedram = new u8[SharedRamSize];
        readonly u16[] m_paletteram = new u16[PaletteWords];
        readonly u32[] m_palette = new u32[PaletteWords];
        readonly u16[,,] m_vdp_vram = new u16[VdpCount, VdpLayerCount, VdpLayerWords];
        readonly u16[,] m_vdp_spriteram = new u16[VdpCount, VdpSpriteWords];
        readonly u16[,] m_vdp_spriteram_buffer = new u16[VdpCount, VdpSpriteWords];
        readonly u16[] m_vdp_voffs = new u16[VdpCount];
        readonly u16[] m_vdp_scroll_reg = new u16[VdpCount];
        readonly u16[,] m_vdp_scrollx = new u16[VdpCount, 4];
        readonly u16[,] m_vdp_scrolly = new u16[VdpCount, 4];
        readonly byte[] m_priority_bitmap = new byte[ScreenWidth * ScreenHeight];
        readonly u16[] m_vdp0_bitmap = new u16[ScreenWidth * ScreenHeight];
        readonly u16[] m_vdp1_bitmap = new u16[ScreenWidth * ScreenHeight];
        byte[][][] m_decoded_tiles = new byte[VdpCount][][];
        int[] m_tile_counts = new int[VdpCount];
        emu_timer m_vdp_irq_timer;
        int m_frame_counter;
        int m_vdpcount_reads;
        int m_trace_count;
        int m_shared_read_trace_count;
        int m_shared_write_trace_count;
        int m_workram_trace_count;
        int m_external_coin_frames;
        int m_external_start_frames;
        bool m_sound_reset_released;
        bool m_video_dirty = true;

        public batsugun_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
            m_maincpu = new required_device<m68000_device>(this, "maincpu");
        }

        public void batsugun(machine_config config)
        {
            M68000(config, m_maincpu, MainClock);
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, batsugun_68k_mem);

            screen_device screen = SCREEN(config, "screen", SCREEN_TYPE_RASTER);
            screen.set_video_attributes(VIDEO_UPDATE_BEFORE_VBLANK);
            screen.set_screen_update(screen_update);
            screen.set_raw(PixelClock, 432, 0, 320, 262, 0, 240);
            screen.screen_vblank().set((write_line_delegate)screen_vblank).reg();
        }

        void batsugun_68k_mem(address_map map, device_t device)
        {
            map.op(0x000000, 0x07ffff).rom();
            map.op(0x100000, 0x10ffff).rw((read16_delegate)workram_r, (write16_delegate)workram_w);
            map.op(0x200010, 0x200011).r((read16_delegate)in1_r);
            map.op(0x200014, 0x200015).r((read16_delegate)in2_r);
            map.op(0x200018, 0x200019).r((read16_delegate)sys_r);
            map.op(0x20001c, 0x20001d).w((write16_delegate)coin_sound_reset_w);
            map.op(0x210000, 0x21ffff).rw((read16_delegate)shared_r, (write16_delegate)shared_w);
            map.op(0x300000, 0x30000d).rw((read16_delegate)vdp0_r, (write16_delegate)vdp0_w);
            map.op(0x400000, 0x400fff).rw((read16_delegate)palette_r, (write16_delegate)palette_w);
            map.op(0x500000, 0x50000d).rw((read16_delegate)vdp1_r, (write16_delegate)vdp1_w);
            map.op(0x700000, 0x700001).r((read16_delegate)vdpcount_r);
        }

        u16 workram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int index = WordOffset(offset, WorkRamWords);
            TraceWorkRamAccess("R", index, m_workram[index], mem_mask);
            return m_workram[index];
        }

        void workram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int index = WordOffset(offset, WorkRamWords);
            m_workram[index] = CombineWord(m_workram[index], data, mem_mask);
            TraceWorkRamAccess("W", index, m_workram[index], mem_mask);
        }

        u16 in1_r(address_space space, offs_t offset, u16 mem_mask)
            => ReadInputPort("IN1");

        u16 in2_r(address_space space, offs_t offset, u16 mem_mask)
            => ReadInputPort("IN2");

        u16 sys_r(address_space space, offs_t offset, u16 mem_mask)
        {
            u16 value = ReadInputPort("SYS");
            TraceLimited(TraceInput && value != 0, $"[BATSUGUN input] SYS=0x{value:x4}");
            return value;
        }

        void coin_sound_reset_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0x00ff) != 0)
            {
                machine().bookkeeping().coin_counter_w(0, data & 0x01);
                machine().bookkeeping().coin_counter_w(1, data & 0x02);
                machine().bookkeeping().coin_lockout_w(0, (data & 0x04) == 0 ? 1 : 0);
                machine().bookkeeping().coin_lockout_w(1, (data & 0x08) == 0 ? 1 : 0);
                m_sound_reset_released = (data & 0x20) != 0;
                TraceLimited(TraceHost, $"[BATSUGUN sound-reset] data=0x{data:x4} released={m_sound_reset_released} pc=0x{m_maincpu.op0.Pc:x6}");
                if (!NativeSoundBridge && (data & 0x20) != 0)
                    ReleaseSoundCpuShim();
            }
        }

        void ReleaseSoundCpuShim()
        {
            UpdateSoundCpuShimInputs();
            TraceLimited(TraceHost, $"[BATSUGUN sound-shim] ready pc=0x{m_maincpu.op0.Pc:x6}");
        }

        void UpdateSoundCpuShimInputs()
        {
            m_sharedram[0x7800] = 0xff;
            m_sharedram[0x7802] = (u8)(ReadInputPort("DSWA") & 0xff);
            m_sharedram[0x7803] = (u8)(ReadInputPort("DSWB") & 0xff);
            m_sharedram[0x7804] = (u8)((ReadInputPort("JMPR") >> 4) & 0x0f);
        }

        u16 shared_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int index = (int)(offset & (SharedRamSize - 1));
            TraceSharedAccess("R", index, m_sharedram[index]);
            return (u16)(0xff00 | m_sharedram[index]);
        }

        void shared_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0x00ff) == 0)
                return;

            int index = (int)(offset & (SharedRamSize - 1));
            m_sharedram[index] = (u8)(data & 0xff);
            TraceSharedAccess("W", index, m_sharedram[index]);
        }

        u16 palette_r(address_space space, offs_t offset, u16 mem_mask)
            => m_paletteram[WordOffset(offset, PaletteWords)];

        void palette_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int index = WordOffset(offset, PaletteWords);
            u16 value = CombineWord(m_paletteram[index], data, mem_mask);
            if (m_paletteram[index] == value)
                return;

            m_paletteram[index] = value;
            UpdatePalette(index);
            m_video_dirty = true;
        }

        u16 vdp0_r(address_space space, offs_t offset, u16 mem_mask) => vdp_r(0, offset, mem_mask);
        u16 vdp1_r(address_space space, offs_t offset, u16 mem_mask) => vdp_r(1, offset, mem_mask);
        void vdp0_w(address_space space, offs_t offset, u16 data, u16 mem_mask) => vdp_w(0, offset, data, mem_mask);
        void vdp1_w(address_space space, offs_t offset, u16 data, u16 mem_mask) => vdp_w(1, offset, data, mem_mask);

        u16 vdp_r(int vdp, offs_t offset, u16 mem_mask)
        {
            switch (((int)offset << 1) & 0x0c)
            {
                case 0x04:
                    return VdpVideoRamRead(vdp);
                case 0x0c:
                    return VdpStatus();
                default:
                    TraceLimited(TraceHost, $"[BATSUGUN vdp{vdp}] unhandled read off=0x{offset:x} pc=0x{m_maincpu.op0.Pc:x6}");
                    return 0xffff;
            }
        }

        void vdp_w(int vdp, offs_t offset, u16 data, u16 mem_mask)
        {
            switch (((int)offset << 1) & 0x0c)
            {
                case 0x00:
                    m_vdp_voffs[vdp] = CombineWord(m_vdp_voffs[vdp], data, mem_mask);
                    break;
                case 0x04:
                    VdpVideoRamWrite(vdp, data, mem_mask);
                    break;
                case 0x08:
                    if ((mem_mask & 0x00ff) != 0)
                        m_vdp_scroll_reg[vdp] = (u16)(data & 0x8f);
                    break;
                case 0x0c:
                    VdpScrollDataWrite(vdp, data, mem_mask);
                    break;
            }
        }

        u16 VdpVideoRamRead(int vdp)
        {
            int offs = m_vdp_voffs[vdp]++;
            if (TryDecodeVdpAddress(offs, out int layer, out int index))
                return m_vdp_vram[vdp, layer, index];

            if (offs >= 0x1800 && offs < 0x1c00)
                return m_vdp_spriteram[vdp, offs - 0x1800];

            return 0xffff;
        }

        void VdpVideoRamWrite(int vdp, u16 data, u16 mem_mask)
        {
            int offs = m_vdp_voffs[vdp]++;
            if (TryDecodeVdpAddress(offs, out int layer, out int index))
            {
                u16 value = CombineWord(m_vdp_vram[vdp, layer, index], data, mem_mask);
                if (m_vdp_vram[vdp, layer, index] != value)
                {
                    m_vdp_vram[vdp, layer, index] = value;
                    m_video_dirty = true;
                }
                return;
            }

            if (offs >= 0x1800 && offs < 0x1c00)
            {
                int spriteIndex = offs - 0x1800;
                u16 value = CombineWord(m_vdp_spriteram[vdp, spriteIndex], data, mem_mask);
                if (m_vdp_spriteram[vdp, spriteIndex] != value)
                {
                    m_vdp_spriteram[vdp, spriteIndex] = value;
                    m_video_dirty = true;
                }
            }
        }

        static bool TryDecodeVdpAddress(int offs, out int layer, out int index)
        {
            layer = -1;
            index = -1;
            if (offs < 0 || offs >= 0x1800)
                return false;

            layer = offs >> 11;
            index = offs & (VdpLayerWords - 1);
            return layer < VdpLayerCount;
        }

        void VdpScrollDataWrite(int vdp, u16 data, u16 mem_mask)
        {
            int reg = m_vdp_scroll_reg[vdp] & 0x7f;
            int layer = reg >> 1;

            if (reg <= 0x07)
            {
                if ((reg & 1) == 0)
                    m_vdp_scrollx[vdp, layer] = CombineWord(m_vdp_scrollx[vdp, layer], data, mem_mask);
                else
                    m_vdp_scrolly[vdp, layer] = CombineWord(m_vdp_scrolly[vdp, layer], data, mem_mask);
                m_video_dirty = true;
                return;
            }

            if ((reg & 0x7f) == 0x0f && vdp == 0)
                m_maincpu.op0.set_input_line(4, CLEAR_LINE);
        }

        u16 VdpStatus()
        {
            screen_device screen = subdevice<screen_device>("screen");
            int vpos = ((screen?.vpos() ?? 0) + 15) % 262;
            return (u16)(vpos >= 245 ? 1 : 0);
        }

        u16 vdpcount_r(address_space space, offs_t offset, u16 mem_mask)
        {
            m_vdpcount_reads++;
            screen_device screen = subdevice<screen_device>("screen");
            int hpos = screen?.hpos() ?? 0;
            int vpos = ((screen?.vpos() ?? 0) + 15) % 262;
            u16 videoStatus = 0xff00;
            if (hpos > 325 && hpos < 380)
                videoStatus &= unchecked((u16)~0x8000);
            if (vpos >= 232 && vpos <= 245)
            {
                videoStatus &= unchecked((u16)~0x4000);
                videoStatus &= unchecked((u16)~0x0100);
            }
            if (vpos < 256)
                videoStatus |= (u16)(vpos & 0xff);
            else
                videoStatus |= 0xff;
            return videoStatus;
        }

        uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            if (!NativeSoundBridge && m_sharedram[0x7800] == 0xff)
                UpdateSoundCpuShimInputs();
            EnsureGraphicsDecoded();
            if (m_video_dirty)
            {
                Array.Clear(m_vdp0_bitmap, 0, m_vdp0_bitmap.Length);
                Array.Clear(m_vdp1_bitmap, 0, m_vdp1_bitmap.Length);
                Array.Clear(m_priority_bitmap, 0, m_priority_bitmap.Length);
                RenderVdp(0, m_vdp0_bitmap, cliprect);
                Array.Clear(m_priority_bitmap, 0, m_priority_bitmap.Length);
                RenderVdp(1, m_vdp1_bitmap, cliprect);
                m_video_dirty = false;
            }

            PresentMixedFrame(bitmap, cliprect);
            m_frame_counter++;
            TraceVideoState();
            return 0;
        }

        void screen_vblank(int state)
        {
            if (state != 0)
            {
                Buffer.BlockCopy(m_vdp_spriteram, 0, m_vdp_spriteram_buffer, 0, m_vdp_spriteram.Length * sizeof(u16));
                m_video_dirty = true;
                screen_device screen = subdevice<screen_device>("screen");
                if (screen != null && m_vdp_irq_timer != null)
                    m_vdp_irq_timer.adjust(screen.time_until_pos(0xe6));
                else
                    m_maincpu.op0.set_input_line(4, ASSERT_LINE);
            }
        }

        void raise_vdp_irq(s32 param)
        {
            m_maincpu.op0.set_input_line(4, ASSERT_LINE);
        }

        protected override void machine_start()
        {
            memory_region maincpu = memregion("maincpu");
            if (maincpu != null && maincpu.base_() != null)
            {
                m_mainrom = maincpu.base_();
                m_mainrom_bytes = (int)Math.Min(maincpu.bytes(), int.MaxValue);
            }

            m_maincpu.op0.set_fast_memory_handlers(
                Fast68kReadByte,
                Fast68kReadWord,
                Fast68kWriteByte,
                Fast68kWriteWord,
                Fast68kReadLong,
                Fast68kWriteLong);

            m_vdp_irq_timer = timer_alloc(raise_vdp_irq);

            save_item(NAME(new { m_workram }));
            save_item(NAME(new { m_sharedram }));
            save_item(NAME(new { m_paletteram }));
            save_item(NAME(new { m_vdp_vram }));
            save_item(NAME(new { m_vdp_spriteram }));
            save_item(NAME(new { m_vdp_spriteram_buffer }));
            save_item(NAME(new { m_vdp_voffs }));
            save_item(NAME(new { m_vdp_scroll_reg }));
            save_item(NAME(new { m_vdp_scrollx }));
            save_item(NAME(new { m_vdp_scrolly }));
        }

        protected override void machine_reset()
        {
            Array.Clear(m_workram, 0, m_workram.Length);
            Array.Clear(m_sharedram, 0, m_sharedram.Length);
            Array.Clear(m_paletteram, 0, m_paletteram.Length);
            Array.Clear(m_palette, 0, m_palette.Length);
            Array.Clear(m_vdp_vram, 0, m_vdp_vram.Length);
            Array.Clear(m_vdp_spriteram, 0, m_vdp_spriteram.Length);
            Array.Clear(m_vdp_spriteram_buffer, 0, m_vdp_spriteram_buffer.Length);
            Array.Clear(m_vdp_voffs, 0, m_vdp_voffs.Length);
            Array.Clear(m_vdp_scroll_reg, 0, m_vdp_scroll_reg.Length);
            Array.Clear(m_vdp_scrollx, 0, m_vdp_scrollx.Length);
            Array.Clear(m_vdp_scrolly, 0, m_vdp_scrolly.Length);
            m_frame_counter = 0;
            m_vdpcount_reads = 0;
            m_shared_read_trace_count = 0;
            m_shared_write_trace_count = 0;
            m_workram_trace_count = 0;
            m_sound_reset_released = false;
            m_video_dirty = true;
        }

        public int BatsugunSharedRamLength => m_sharedram.Length;
        public bool BatsugunSoundResetReleased => m_sound_reset_released;

        public void CopyBatsugunSharedRamTo(byte[] destination)
        {
            if (destination == null)
                return;

            Buffer.BlockCopy(m_sharedram, 0, destination, 0, Math.Min(destination.Length, m_sharedram.Length));
        }

        public void CopyBatsugunSharedRamFrom(byte[] source)
        {
            if (source == null)
                return;

            Buffer.BlockCopy(source, 0, m_sharedram, 0, Math.Min(source.Length, m_sharedram.Length));
        }

        bool Fast68kReadByte(u32 address, out u8 value)
        {
            address &= 0x00ff_ffff;
            if (m_mainrom != null && address < m_mainrom_bytes)
            {
                value = m_mainrom[(int)(address ^ 1)];
                return true;
            }

            if (address >= 0x100000 && address <= 0x10ffff)
            {
                u16 word = m_workram[((int)(address - 0x100000) >> 1) & (WorkRamWords - 1)];
                value = ((address & 1) == 0) ? (u8)(word >> 8) : (u8)word;
                return true;
            }

            if (address >= 0x210000 && address <= 0x21ffff)
            {
                value = ((address & 1) == 0) ? (u8)0xff : m_sharedram[((int)(address - 0x210000) >> 1) & (SharedRamSize - 1)];
                return true;
            }

            if ((address & 1) == 0 && Fast68kReadWord(address, out u16 wordValue))
            {
                value = (u8)(wordValue >> 8);
                return true;
            }
            if ((address & 1) != 0 && Fast68kReadWord(address - 1, out wordValue))
            {
                value = (u8)wordValue;
                return true;
            }

            value = 0xff;
            return false;
        }

        bool Fast68kReadWord(u32 address, out u16 value)
        {
            address &= 0x00ff_ffff;
            if ((address & 1) != 0)
            {
                value = 0xffff;
                return false;
            }

            if (m_mainrom != null && address + 1 < m_mainrom_bytes)
            {
                value = (u16)((m_mainrom[(int)(address + 1)] << 8) | m_mainrom[(int)address]);
                return true;
            }

            if (address >= 0x100000 && address <= 0x10ffff)
            {
                value = m_workram[((int)(address - 0x100000) >> 1) & (WorkRamWords - 1)];
                return true;
            }

            if (address >= 0x200010 && address <= 0x200011)
            {
                value = in1_r(null, 0, 0xffff);
                return true;
            }
            if (address >= 0x200014 && address <= 0x200015)
            {
                value = in2_r(null, 0, 0xffff);
                return true;
            }
            if (address >= 0x200018 && address <= 0x200019)
            {
                value = sys_r(null, 0, 0xffff);
                return true;
            }

            if (address >= 0x210000 && address <= 0x21ffff)
            {
                value = (u16)(0xff00 | m_sharedram[((int)(address - 0x210000) >> 1) & (SharedRamSize - 1)]);
                return true;
            }

            if (address >= 0x300000 && address <= 0x30000d)
            {
                value = vdp_r(0, (address - 0x300000) >> 1, 0xffff);
                return true;
            }

            if (address >= 0x400000 && address <= 0x400fff)
            {
                value = m_paletteram[((int)(address - 0x400000) >> 1) & (PaletteWords - 1)];
                return true;
            }

            if (address >= 0x500000 && address <= 0x50000d)
            {
                value = vdp_r(1, (address - 0x500000) >> 1, 0xffff);
                return true;
            }

            if (address >= 0x700000 && address <= 0x700001)
            {
                value = vdpcount_r(null, 0, 0xffff);
                return true;
            }

            value = 0xffff;
            return false;
        }

        bool Fast68kReadLong(u32 address, out u32 value)
        {
            if ((address & 1) == 0
                && Fast68kReadWord(address, out u16 hi)
                && Fast68kReadWord((address + 2) & 0x00ff_ffff, out u16 lo))
            {
                value = ((u32)hi << 16) | lo;
                return true;
            }

            value = 0xffff_ffff;
            return false;
        }

        bool Fast68kWriteByte(u32 address, u8 value)
        {
            address &= 0x00ff_ffff;
            u16 data = ((address & 1) == 0) ? (u16)(value << 8) : value;
            u16 memMask = ((address & 1) == 0) ? (u16)0xff00 : (u16)0x00ff;
            return Fast68kWriteWord(address & 0xffff_fffe, data, memMask);
        }

        bool Fast68kWriteWord(u32 address, u16 value)
            => Fast68kWriteWord(address, value, 0xffff);

        bool Fast68kWriteLong(u32 address, u32 value)
        {
            if ((address & 1) != 0)
                return false;

            bool hi = Fast68kWriteWord(address, (u16)(value >> 16), 0xffff);
            bool lo = Fast68kWriteWord((address + 2) & 0x00ff_ffff, (u16)value, 0xffff);
            return hi && lo;
        }

        bool Fast68kWriteWord(u32 address, u16 value, u16 memMask)
        {
            address &= 0x00ff_ffff;
            if ((address & 1) != 0)
                return false;

            if (address >= 0x100000 && address <= 0x10ffff)
            {
                int index = ((int)(address - 0x100000) >> 1) & (WorkRamWords - 1);
                m_workram[index] = CombineWord(m_workram[index], value, memMask);
                TraceWorkRamAccess("W", index, m_workram[index], memMask);
                return true;
            }

            if (address >= 0x20001c && address <= 0x20001d)
            {
                coin_sound_reset_w(null, 0, value, memMask);
                return true;
            }

            if (address >= 0x210000 && address <= 0x21ffff)
            {
                if ((memMask & 0x00ff) != 0)
                {
                    int index = ((int)(address - 0x210000) >> 1) & (SharedRamSize - 1);
                    m_sharedram[index] = (u8)(value & 0xff);
                    TraceSharedAccess("W", index, m_sharedram[index]);
                }
                return true;
            }

            if (address >= 0x300000 && address <= 0x30000d)
            {
                vdp_w(0, (address - 0x300000) >> 1, value, memMask);
                return true;
            }

            if (address >= 0x400000 && address <= 0x400fff)
            {
                palette_w(null, (address - 0x400000) >> 1, value, memMask);
                return true;
            }

            if (address >= 0x500000 && address <= 0x50000d)
            {
                vdp_w(1, (address - 0x500000) >> 1, value, memMask);
                return true;
            }

            return false;
        }

        u16 ReadInputPort(string tag)
        {
            ioport_port port = ioport(tag);
            return port != null ? (u16)(port.read() & 0xffff) : (u16)0;
        }

        void EnsureGraphicsDecoded()
        {
            DecodeVdpIfNeeded(0, "gp9001_0");
            DecodeVdpIfNeeded(1, "gp9001_1");
        }

        void DecodeVdpIfNeeded(int vdp, string regionName)
        {
            if (m_decoded_tiles[vdp] != null)
                return;

            memory_region region = memregion(regionName);
            if (region == null || region.base_() == null)
            {
                m_decoded_tiles[vdp] = Array.Empty<byte[]>();
                m_tile_counts[vdp] = 0;
                return;
            }

            m_tile_counts[vdp] = Math.Min((int)(region.bytes() / 2 / TileBytes), 0x20000);
            m_decoded_tiles[vdp] = DecodeTiles(region.base_(), (int)region.bytes(), m_tile_counts[vdp]);
        }

        static byte[][] DecodeTiles(MemoryU8 rom, int bytes, int tileCount)
        {
            byte[][] decoded = new byte[tileCount][];
            int halfBits = (bytes / 2) * 8;
            int[] planeOffsets = { halfBits + 8, halfBits, 8, 0 };

            for (int tile = 0; tile < tileCount; tile++)
            {
                byte[] pixels = new byte[TilePixels];
                int tileBitBase = tile * TileBytes * 8;
                for (int y = 0; y < TileSize; y++)
                {
                    int rowBitBase = tileBitBase + y * 16;
                    for (int x = 0; x < TileSize; x++)
                    {
                        int pen = 0;
                        for (int plane = 0; plane < 4; plane++)
                        {
                            int bitOffset = planeOffsets[plane] + rowBitBase + x;
                            int byteOffset = bitOffset >> 3;
                            if ((uint)byteOffset >= (uint)bytes)
                                continue;

                            int bit = 7 - (bitOffset & 7);
                            pen |= ((rom[byteOffset] >> bit) & 1) << (3 - plane);
                        }
                        pixels[(y << 3) | x] = (byte)pen;
                    }
                }
                decoded[tile] = pixels;
            }

            return decoded;
        }

        void RenderVdp(int vdp, u16[] target, rectangle cliprect)
        {
            for (int layer = 0; layer < VdpLayerCount; layer++)
                RenderTileLayer(vdp, layer, target, cliprect);
            RenderSprites(vdp, target, cliprect);
        }

        void RenderTileLayer(int vdp, int layer, u16[] target, rectangle cliprect)
        {
            if (m_tile_counts[vdp] == 0)
                return;

            int scrollX = (m_vdp_scrollx[vdp, layer] + LayerXOffset(layer)) & 0x1ff;
            int scrollY = (m_vdp_scrolly[vdp, layer] - 0x1ef) & 0x1ff;
            int startTileX = ((cliprect.min_x + scrollX) >> 4) - 1;
            int endTileX = ((cliprect.max_x + scrollX) >> 4) + 1;
            int startTileY = ((cliprect.min_y + scrollY) >> 4) - 1;
            int endTileY = ((cliprect.max_y + scrollY) >> 4) + 1;

            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                int tileY = ty & 31;
                int screenY = (ty << 4) - scrollY;
                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    int tileX = tx & 31;
                    int screenX = (tx << 4) - scrollX;
                    int tileIndex = (tileY * 32 + tileX) << 1;
                    u16 attrib = m_vdp_vram[vdp, layer, tileIndex & (VdpLayerWords - 1)];
                    u16 codeWord = m_vdp_vram[vdp, layer, (tileIndex + 1) & (VdpLayerWords - 1)];
                    int color = attrib & 0x0fff;
                    int priority = ((color << 4) >> 12) & GP9001_PRIMASK_TMAPS;
                    int codeBase = (codeWord << 2) % Math.Max(1, m_tile_counts[vdp]);
                    Draw16x16Tile(vdp, target, cliprect, codeBase, screenX, screenY, color, priority);
                }
            }
        }

        static int LayerXOffset(int layer)
            => layer switch { 0 => -0x1d6, 1 => -0x1d8, _ => -0x1da };

        void Draw16x16Tile(int vdp, u16[] target, rectangle cliprect, int codeBase, int dstX, int dstY, int color, int priority)
        {
            Draw8x8Tile(vdp, target, cliprect, codeBase + 0, dstX, dstY, color, priority);
            Draw8x8Tile(vdp, target, cliprect, codeBase + 1, dstX + 8, dstY, color, priority);
            Draw8x8Tile(vdp, target, cliprect, codeBase + 2, dstX, dstY + 8, color, priority);
            Draw8x8Tile(vdp, target, cliprect, codeBase + 3, dstX + 8, dstY + 8, color, priority);
        }

        void Draw8x8Tile(int vdp, u16[] target, rectangle cliprect, int code, int dstX, int dstY, int color, int priority)
        {
            byte[] pixels = m_decoded_tiles[vdp][code % m_tile_counts[vdp]];
            int minX = Math.Max(dstX, cliprect.min_x);
            int minY = Math.Max(dstY, cliprect.min_y);
            int maxX = Math.Min(dstX + 7, cliprect.max_x);
            int maxY = Math.Min(dstY + 7, cliprect.max_y);
            if (minX > maxX || minY > maxY)
                return;

            for (int y = minY; y <= maxY; y++)
            {
                int py = y - dstY;
                int row = y * ScreenWidth;
                for (int x = minX; x <= maxX; x++)
                {
                    int pen = pixels[(py << 3) | (x - dstX)];
                    if (pen == 0)
                        continue;

                    int idx = row + x;
                    if (priority >= m_priority_bitmap[idx])
                    {
                        target[idx] = (u16)(((color << 4) | pen) & 0x07ff);
                        m_priority_bitmap[idx] = (byte)priority;
                    }
                }
            }
        }

        void RenderSprites(int vdp, u16[] target, rectangle cliprect)
        {
            if (m_tile_counts[vdp] == 0)
                return;

            u16[,] source = m_vdp_spriteram_buffer;
            int spriteScrollX = (m_vdp_scrollx[vdp, 3] - 0x1cc) & 0x1ff;
            int spriteScrollY = (m_vdp_scrolly[vdp, 3] - 0x1ef) & 0x1ff;
            int oldX = (-spriteScrollX) & 0x1ff;
            int oldY = (-spriteScrollY) & 0x1ff;

            for (int offs = 0; offs < VdpSpriteWords; offs += 4)
            {
                u16 attrib = source[vdp, offs];
                if ((attrib & 0x8000) == 0)
                    continue;

                int priority = (attrib >> 8) & GP9001_PRIMASK;
                int sprite = (((attrib & 3) << 16) | source[vdp, offs + 1]) % m_tile_counts[vdp];
                int color = (attrib >> 2) & 0x3f;
                int width = ((source[vdp, offs + 2] & 0x0f) + 1) * 8;
                int height = ((source[vdp, offs + 3] & 0x0f) + 1) * 8;
                int sxBase;
                int syBase;
                if ((attrib & 0x4000) == 0)
                {
                    sxBase = ((source[vdp, offs + 2] >> 7) - spriteScrollX) & 0x1ff;
                    syBase = ((source[vdp, offs + 3] >> 7) - spriteScrollY) & 0x1ff;
                }
                else
                {
                    sxBase = (oldX + (source[vdp, offs + 2] >> 7)) & 0x1ff;
                    syBase = (oldY + (source[vdp, offs + 3] >> 7)) & 0x1ff;
                }
                oldX = sxBase;
                oldY = syBase;

                bool flipX = (attrib & 0x1000) != 0;
                bool flipY = (attrib & 0x2000) != 0;
                if (flipX)
                {
                    sxBase -= 7;
                    if (sxBase >= 0x1c0) sxBase -= 0x200;
                }
                else if (sxBase >= 0x180)
                {
                    sxBase -= 0x200;
                }

                if (flipY)
                {
                    syBase -= 7;
                    if (syBase >= 0x1c0) syBase -= 0x200;
                }
                else if (syBase >= 0x180)
                {
                    syBase -= 0x200;
                }

                for (int dy = 0; dy < height; dy += 8)
                {
                    int sy = flipY ? syBase - dy : syBase + dy;
                    for (int dx = 0; dx < width; dx += 8)
                    {
                        int sx = flipX ? sxBase - dx : sxBase + dx;
                        DrawSpriteTile(vdp, target, cliprect, sprite++, sx, sy, color, priority, flipX, flipY);
                    }
                }
            }
        }

        void DrawSpriteTile(int vdp, u16[] target, rectangle cliprect, int code, int dstX, int dstY, int color, int priority, bool flipX, bool flipY)
        {
            byte[] pixels = m_decoded_tiles[vdp][code % m_tile_counts[vdp]];
            int minX = Math.Max(dstX, cliprect.min_x);
            int minY = Math.Max(dstY, cliprect.min_y);
            int maxX = Math.Min(dstX + 7, cliprect.max_x);
            int maxY = Math.Min(dstY + 7, cliprect.max_y);
            if (minX > maxX || minY > maxY)
                return;

            for (int y = minY; y <= maxY; y++)
            {
                int py = flipY ? 7 - (y - dstY) : y - dstY;
                int row = y * ScreenWidth;
                for (int x = minX; x <= maxX; x++)
                {
                    int px = flipX ? 7 - (x - dstX) : x - dstX;
                    int pen = pixels[(py << 3) | px];
                    if (pen == 0)
                        continue;

                    int idx = row + x;
                    if (priority >= m_priority_bitmap[idx])
                    {
                        target[idx] = (u16)((((color & 0x3f) << 4) | pen) & 0x07ff);
                        m_priority_bitmap[idx] = (byte)priority;
                    }
                }
            }
        }

        void PresentMixedFrame(bitmap_rgb32 bitmap, rectangle cliprect)
        {
            for (int y = cliprect.min_y; y <= cliprect.max_y; y++)
            {
                PointerU32 bitmapRow = bitmap.pix(y);
                byte[] bitmapData = bitmapRow.Buffer.data_raw;
                int rowOffset = bitmapRow.Offset;
                int row = y * ScreenWidth;
                for (int x = cliprect.min_x; x <= cliprect.max_x; x++)
                {
                    u16 pix0 = m_vdp0_bitmap[row + x];
                    u16 pix1 = m_vdp1_bitmap[row + x];
                    u16 mixed;
                    if ((pix1 & 0x000f) == 0)
                        mixed = pix0;
                    else if ((pix0 & 0x000f) == 0)
                        mixed = pix1;
                    else
                        mixed = (u16)(((pix0 & 0x0780) > (pix1 & 0x0780)) ? pix1 : pix0);

                    WriteRgb32(bitmapData, rowOffset + (x << 2), m_palette[mixed & (PaletteWords - 1)]);
                }
            }
        }

        void UpdatePalette(int index)
        {
            u16 raw = m_paletteram[index & (PaletteWords - 1)];
            int r = (raw & 0x001f) << 3;
            int g = ((raw >> 5) & 0x001f) << 3;
            int b = ((raw >> 10) & 0x001f) << 3;
            r |= r >> 5;
            g |= g >> 5;
            b |= b >> 5;
            m_palette[index & (PaletteWords - 1)] = 0xff000000U | (u32)(r << 16) | (u32)(g << 8) | (u32)b;
        }

        static void WriteRgb32(byte[] data, int offset, u32 color)
        {
            data[offset] = (byte)color;
            data[offset + 1] = (byte)(color >> 8);
            data[offset + 2] = (byte)(color >> 16);
            data[offset + 3] = (byte)(color >> 24);
        }

        static u16 CombineWord(u16 value, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0xff00) != 0)
                value = (u16)((value & 0x00ff) | (data & 0xff00));
            if ((mem_mask & 0x00ff) != 0)
                value = (u16)((value & 0xff00) | (data & 0x00ff));
            return value;
        }

        static int WordOffset(offs_t offset, int wordCount)
            => (int)(offset & (uint)(wordCount - 1));

        void TraceLimited(bool enabled, string message)
        {
            if (!enabled || m_trace_count >= 256)
                return;

            Console.WriteLine(message);
            m_trace_count++;
        }

        void TraceVideoState()
        {
            if (!TraceVideo || (m_frame_counter % 60) != 0)
                return;

            Console.WriteLine(
                "[BATSUGUN video] " +
                $"frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:x6} pal={CountNonZero(m_paletteram)} " +
                $"v0={CountNonZeroVram(0, 0)}/{CountNonZeroVram(0, 1)}/{CountNonZeroVram(0, 2)} sp0={CountVisibleSprites(0)} " +
                $"v1={CountNonZeroVram(1, 0)}/{CountNonZeroVram(1, 1)}/{CountNonZeroVram(1, 2)} sp1={CountVisibleSprites(1)} " +
                $"irqword={m_workram[0x08ff]:x4}/{m_workram[0x11fe]:x4} " +
                $"scroll0={m_vdp_scrollx[0, 0]:x4},{m_vdp_scrolly[0, 0]:x4},{m_vdp_scrollx[0, 3]:x4},{m_vdp_scrolly[0, 3]:x4} " +
                $"scroll1={m_vdp_scrollx[1, 0]:x4},{m_vdp_scrolly[1, 0]:x4},{m_vdp_scrollx[1, 3]:x4},{m_vdp_scrolly[1, 3]:x4} " +
                $"voffs={m_vdp_voffs[0]:x4}/{m_vdp_voffs[1]:x4} shared={FormatSharedPrefix()}");
        }

        int CountNonZeroVram(int vdp, int layer)
        {
            int count = 0;
            for (int i = 0; i < VdpLayerWords; i++)
                if (m_vdp_vram[vdp, layer, i] != 0)
                    count++;
            return count;
        }

        int CountVisibleSprites(int vdp)
        {
            int count = 0;
            for (int i = 0; i < VdpSpriteWords; i += 4)
                if ((m_vdp_spriteram_buffer[vdp, i] & 0x8000) != 0)
                    count++;
            return count;
        }

        static int CountNonZero(u16[] data)
        {
            int count = 0;
            for (int i = 0; i < data.Length; i++)
                if (data[i] != 0)
                    count++;
            return count;
        }

        string FormatSharedPrefix()
        {
            return $"{m_sharedram[0]:x2}{m_sharedram[1]:x2}{m_sharedram[2]:x2}{m_sharedram[3]:x2}-" +
                   $"{m_sharedram[4]:x2}{m_sharedram[5]:x2}{m_sharedram[6]:x2}{m_sharedram[7]:x2}-" +
                   $"{m_sharedram[8]:x2}{m_sharedram[9]:x2}{m_sharedram[10]:x2}{m_sharedram[11]:x2}-" +
                   $"{m_sharedram[12]:x2}{m_sharedram[13]:x2}{m_sharedram[14]:x2}{m_sharedram[15]:x2}";
        }

        void TraceSharedAccess(string op, int index, u8 value)
        {
            if (!TraceShared)
                return;

            if (op == "R")
            {
                if (m_shared_read_trace_count >= 256 || (index >= 0x0100 && index < 0x7800))
                    return;
                m_shared_read_trace_count++;
            }
            else
            {
                if (m_shared_write_trace_count >= 640 || (index >= 0x0100 && index < 0x7800))
                    return;
                m_shared_write_trace_count++;
            }

            Console.WriteLine($"[BATSUGUN shared] {op} idx=0x{index:x4} value=0x{value:x2} pc=0x{m_maincpu.op0.Pc:x6}");
        }

        void TraceWorkRamAccess(string op, int index, u16 value, u16 memMask)
        {
            if (!TraceWorkRam || m_workram_trace_count >= 384)
                return;
            if (index < 0x0700 || index > 0x1200)
                return;
            if (op != "R" && index != 0x0740 && index != 0x08ff && index != 0x11fe)
                return;

            Console.WriteLine($"[BATSUGUN workram] {op} idx=0x{index:x4} value=0x{value:x4} mask=0x{memMask:x4} pc=0x{m_maincpu.op0.Pc:x6}");
            m_workram_trace_count++;
        }
    }

    public class batsugun : construct_ioport_helper
    {
        const u32 ROM_GROUPWORD = 0x100;
        static readonly batsugun m_batsugun = new batsugun();

        static tiny_rom_entry ROM_LOAD16_WORD_SWAP(string name, u32 offset, u32 length, string hash)
        {
            return ROMX_LOAD(name, offset, length, hash, ROM_GROUPWORD | ROM_REVERSE);
        }

        static readonly tiny_rom_entry[] rom_batsugunsp =
        {
            ROM_REGION(0x080000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("tp-030sp.u69", 0x000000, 0x080000, CRC("8072a0cd") + SHA1("3a0a9cdf894926a16800c4882a2b00383d981367")),

            ROM_REGION(0x400000, "gp9001_0", 0),
            ROM_LOAD("tp030_3l.bin", 0x000000, 0x100000, CRC("3024b793") + SHA1("e161db940f069279356fca2c5bf2753f07773705")),
            ROM_LOAD("tp030_3h.bin", 0x100000, 0x100000, CRC("ed75730b") + SHA1("341f0f728144a049486d996c9bb14078578c6879")),
            ROM_LOAD("tp030_4l.bin", 0x200000, 0x100000, CRC("fedb9861") + SHA1("4b0917056bd359b21935358c6bcc729262be6417")),
            ROM_LOAD("tp030_4h.bin", 0x300000, 0x100000, CRC("d482948b") + SHA1("31be7dc5cff072403b783bf203b9805ffcad7284")),

            ROM_REGION(0x200000, "gp9001_1", 0),
            ROM_LOAD("tp030_5.bin", 0x000000, 0x100000, CRC("bcf5ba05") + SHA1("40f98888a29cdd30cda5dfb60fdc667c69b0fdb0")),
            ROM_LOAD("tp030_6.bin", 0x100000, 0x100000, CRC("0666fecd") + SHA1("aa8f921fc51590b5b05bbe0b0ad0cce5ff359c64")),

            ROM_REGION(0x040000, "oki", 0),
            ROM_LOAD("tp030_2.bin", 0x000000, 0x040000, CRC("276146f5") + SHA1("bf11d1f6782cefcad77d52af4f7e6054a8f93440")),

            ROM_REGION(0x001000, "plds", 0),
            ROM_LOAD("tp030_u19_gal16v8b-15.bin", 0x000000, 0x000117, CRC("f71669e8") + SHA1("ec1fbe04605fee864af4b01f001af227938c9f21")),

            ROM_END,
        };

        static void batsugun_state_batsugun(machine_config config, device_t device) { ((batsugun_state)device).batsugun(config); }
        static device_t device_creator_batsugun_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new batsugun_state(mconfig, (device_type)type, tag); }

        void construct_ioport_batsugun(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("IN1");
            PORT_BIT(0x0001, IP_ACTIVE_HIGH, IPT_JOYSTICK_UP); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0002, IP_ACTIVE_HIGH, IPT_JOYSTICK_DOWN); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0004, IP_ACTIVE_HIGH, IPT_JOYSTICK_LEFT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0008, IP_ACTIVE_HIGH, IPT_JOYSTICK_RIGHT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0010, IP_ACTIVE_HIGH, IPT_BUTTON1); PORT_PLAYER(1);
            PORT_BIT(0x0020, IP_ACTIVE_HIGH, IPT_BUTTON2); PORT_PLAYER(1);
            PORT_BIT(0x0040, IP_ACTIVE_HIGH, IPT_BUTTON3); PORT_PLAYER(1);
            PORT_BIT(0xff80, IP_ACTIVE_HIGH, IPT_UNUSED);

            PORT_START("IN2");
            PORT_BIT(0x0001, IP_ACTIVE_HIGH, IPT_JOYSTICK_UP); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x0002, IP_ACTIVE_HIGH, IPT_JOYSTICK_DOWN); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x0004, IP_ACTIVE_HIGH, IPT_JOYSTICK_LEFT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x0008, IP_ACTIVE_HIGH, IPT_JOYSTICK_RIGHT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x0010, IP_ACTIVE_HIGH, IPT_BUTTON1); PORT_PLAYER(2);
            PORT_BIT(0x0020, IP_ACTIVE_HIGH, IPT_BUTTON2); PORT_PLAYER(2);
            PORT_BIT(0x0040, IP_ACTIVE_HIGH, IPT_BUTTON3); PORT_PLAYER(2);
            PORT_BIT(0xff80, IP_ACTIVE_HIGH, IPT_UNUSED);

            PORT_START("SYS");
            PORT_BIT(0x01, IP_ACTIVE_HIGH, IPT_SERVICE1);
            PORT_BIT(0x02, IP_ACTIVE_HIGH, IPT_TILT);
            PORT_BIT(0x04, IP_ACTIVE_HIGH, IPT_SERVICE);
            PORT_BIT(0x08, IP_ACTIVE_HIGH, IPT_COIN1);
            PORT_BIT(0x10, IP_ACTIVE_HIGH, IPT_COIN2);
            PORT_BIT(0x20, IP_ACTIVE_HIGH, IPT_START1);
            PORT_BIT(0x40, IP_ACTIVE_HIGH, IPT_START2);
            PORT_BIT(0x80, IP_ACTIVE_HIGH, IPT_UNUSED);

            PORT_START("DSWA");
            PORT_DIPNAME(0x0001, 0x0000, "Continue Price"); PORT_DIPLOCATION("SW1:!1");
            PORT_DIPSETTING(0x0000, DEF_STR(Normal)); PORT_DIPSETTING(0x0001, "Discount");
            PORT_DIPNAME(0x0002, 0x0000, DEF_STR(Flip_Screen)); PORT_DIPLOCATION("SW1:!2");
            PORT_DIPSETTING(0x0000, DEF_STR(Off)); PORT_DIPSETTING(0x0002, DEF_STR(On));
            PORT_SERVICE(0x0004, IP_ACTIVE_HIGH); PORT_DIPLOCATION("SW1:!3");
            PORT_DIPNAME(0x0008, 0x0000, DEF_STR(Demo_Sounds)); PORT_DIPLOCATION("SW1:!4");
            PORT_DIPSETTING(0x0008, DEF_STR(Off)); PORT_DIPSETTING(0x0000, DEF_STR(On));
            PORT_DIPNAME(0x0030, 0x0000, DEF_STR(Coin_A)); PORT_DIPLOCATION("SW1:!5,!6");
            PORT_DIPSETTING(0x0030, DEF_STR(_4C_1C)); PORT_DIPSETTING(0x0020, DEF_STR(_3C_1C)); PORT_DIPSETTING(0x0010, DEF_STR(_2C_1C)); PORT_DIPSETTING(0x0000, DEF_STR(_1C_1C));
            PORT_DIPNAME(0x00c0, 0x0000, DEF_STR(Coin_B)); PORT_DIPLOCATION("SW1:!7,!8");
            PORT_DIPSETTING(0x0000, DEF_STR(_1C_1C)); PORT_DIPSETTING(0x0040, DEF_STR(_1C_2C)); PORT_DIPSETTING(0x0080, DEF_STR(_1C_3C)); PORT_DIPSETTING(0x00c0, DEF_STR(_1C_4C));

            PORT_START("DSWB");
            PORT_DIPNAME(0x0003, 0x0000, DEF_STR(Difficulty)); PORT_DIPLOCATION("SW2:!1,!2");
            PORT_DIPSETTING(0x0001, DEF_STR(Easy)); PORT_DIPSETTING(0x0000, DEF_STR(Normal)); PORT_DIPSETTING(0x0002, DEF_STR(Hard)); PORT_DIPSETTING(0x0003, "Very Hard");
            PORT_DIPNAME(0x000c, 0x0000, DEF_STR(Bonus_Life)); PORT_DIPLOCATION("SW2:!3,!4");
            PORT_DIPSETTING(0x000c, DEF_STR(None)); PORT_DIPSETTING(0x0008, "1500k only"); PORT_DIPSETTING(0x0000, "1000k only"); PORT_DIPSETTING(0x0004, "500k and every 600k");
            PORT_DIPNAME(0x0030, 0x0000, DEF_STR(Lives)); PORT_DIPLOCATION("SW2:!5,!6");
            PORT_DIPSETTING(0x0030, "1"); PORT_DIPSETTING(0x0020, "2"); PORT_DIPSETTING(0x0000, "3"); PORT_DIPSETTING(0x0010, "5");
            PORT_DIPNAME(0x0040, 0x0000, "Invulnerability (Cheat)"); PORT_DIPLOCATION("SW2:!7");
            PORT_DIPSETTING(0x0000, DEF_STR(Off)); PORT_DIPSETTING(0x0040, DEF_STR(On));
            PORT_DIPNAME(0x0080, 0x0000, DEF_STR(Allow_Continue)); PORT_DIPLOCATION("SW2:!8");
            PORT_DIPSETTING(0x0080, DEF_STR(No)); PORT_DIPSETTING(0x0000, DEF_STR(Yes));

            PORT_START("JMPR");
            PORT_CONFNAME(0x00f0, 0x0090, "Region");
            PORT_CONFSETTING(0x0090, "Europe");
            PORT_CONFSETTING(0x0080, "Europe (Taito Corp.)");
            PORT_CONFSETTING(0x00b0, "USA");
            PORT_CONFSETTING(0x00a0, "USA (Taito Corp.)");
            PORT_CONFSETTING(0x00f0, "Japan");
            PORT_CONFSETTING(0x00d0, "Japan (Taito Corp.)");
            PORT_CONFSETTING(0x0070, "Southeast Asia");
            PORT_CONFSETTING(0x0060, "Southeast Asia (Taito Corp.)");
            PORT_CONFSETTING(0x0050, "Taiwan");
            PORT_CONFSETTING(0x0040, "Taiwan (Taito Corp.)");
            PORT_CONFSETTING(0x0030, "Hong Kong");
            PORT_CONFSETTING(0x0020, "Hong Kong (Taito Corp.)");
            PORT_CONFSETTING(0x0010, "Korea");
            PORT_CONFSETTING(0x0000, "Korea (Unite Trading)");
        }

        public static readonly game_driver driver_batsugunsp = GAME(device_creator_batsugun_state, rom_batsugunsp, "1993", "batsugunsp", "0", batsugun_state_batsugun, m_batsugun.construct_ioport_batsugun, driver_device.empty_init, ROT270, "Toaplan", "Batsugun - Special Version", MACHINE_IS_SKELETON | MACHINE_NO_SOUND_HW);
    }
}
