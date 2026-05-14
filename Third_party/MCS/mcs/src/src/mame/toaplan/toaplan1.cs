// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Minimal Toaplan 1 Out Zone registration for Euther Drive MCS bring-up.

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using MemoryU8 = mame.MemoryContainer<System.Byte>;
using offs_t = System.UInt32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using uint32_t = System.UInt32;

using static mame.diexec_global;
using static mame.disound_global;
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
using static mame.speaker_global;
using static mame.ymopl_global;
using static mame.z80_global;


namespace mame
{
    class toaplan1_state : driver_device
    {
        const int SharedRamSize = 0x800;
        const int MainRamWords = 0x2000;
        const int TileOffsetWords = 0x4;
        const int BcuLayerCount = 4;
        const int BcuLayerWords = 0x1000;
        const int SpriteRamWords = 0x800;
        const int SpriteSizeRamWords = 0x80;
        const int PaletteWords = 0x1000;
        const int ScreenWidth = 320;
        const int ScreenHeight = 240;
        const int TileBytes = 16;
        const int TilePixels = 64;
        const int TileSize = 8;
        const int BcuOffsetX = -0x1ef;
        const int BcuOffsetY = -0x101;
        static readonly XTAL MasterClock = new XTAL(28_000_000);
        static readonly XTAL PixelClock = MasterClock / 4;
        static readonly bool TraceVideo = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_TOAPLAN_TRACE_VIDEO"));
        static readonly bool TraceShared = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_TOAPLAN_TRACE_SHARED"));
        static readonly bool TraceHost = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_TOAPLAN_TRACE_HOST"));
        static readonly bool TraceInput = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_TOAPLAN_TRACE_INPUT"));
        static readonly bool TraceMainRam = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_TOAPLAN_TRACE_MAINRAM"));
        static readonly bool TraceZ80 = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_TOAPLAN_TRACE_Z80"));
        const int HTotal = 450;
        const int HBEnd = 0;
        const int HBStart = 320;
        const int VTotal55 = 282;
        const int VBEnd = 0;
        const int VBStart = 240;

        readonly required_device<m68000_device> m_maincpu;
        readonly required_device<z80_device> m_audiocpu;
        readonly required_device<ym3812_device> m_ymsnd;
        readonly u8 [] m_sharedram = new u8[SharedRamSize];
        readonly u16 [] m_mainram = new u16[MainRamWords];
        readonly u32 [,] m_bcu_vram = new u32[BcuLayerCount, BcuLayerWords];
        readonly u16 [] m_bcu_scrollx = new u16[BcuLayerCount];
        readonly u16 [] m_bcu_scrolly = new u16[BcuLayerCount];
        readonly u16 [] m_spriteram = new u16[SpriteRamWords];
        readonly u16 [] m_spritesizeram = new u16[SpriteSizeRamWords];
        readonly u16 [,] m_paletteram = new u16[2, PaletteWords];
        readonly u32 [] [] m_palette_colors = { new u32[PaletteWords], new u32[PaletteWords] };
        readonly u16 [] m_tile_offsets = new u16[TileOffsetWords];
        readonly byte [] m_priority_bitmap = new byte[ScreenWidth * ScreenHeight];
        readonly byte [] m_bcu_bitmap_cache = new byte[ScreenWidth * ScreenHeight * 4];
        readonly byte [] m_bcu_priority_cache = new byte[ScreenWidth * ScreenHeight];
        byte [] [] m_decoded_bcu_tiles;
        byte [] [] m_decoded_fcu_tiles;
        int m_bcu_tile_count;
        int m_fcu_tile_count;
        u16 m_bcu_ram_offs;
        u16 m_spriteram_offs;
        u8 m_fcu_flipscreen;
        u8 m_bcu_flipscreen;
        u8 m_vctrl_intenable;
        u8 m_vblank_state;
        u16 m_bcu_offsetx;
        u16 m_bcu_offsety;
        int m_frame_counter;
        int m_shared_trace_count;
        int m_host_trace_count;
        int m_z80_shared1_spin_trace_count;
        int m_input_trace_count;
        int m_mainram_trace_count;
        int m_external_start_frames;
        int m_external_coin_frames;
        bool m_video_dirty = true;
        bool m_bcu_dirty = true;
        bool m_fcu_dirty = true;
        bool m_bcu_cache_valid;


        public toaplan1_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
            m_maincpu = new required_device<m68000_device>(this, "maincpu");
            m_audiocpu = new required_device<z80_device>(this, "audiocpu");
            m_ymsnd = new required_device<ym3812_device>(this, "ymsnd");
        }


        public void SetExternalInputState(bool start, bool coin)
        {
            if ((start || coin) && TraceInput)
                Console.WriteLine($"[TOAPLAN input] external direct start={start} coin={coin}");
            if ((start || coin) && TraceInput)
                TraceLimited(true, ref m_input_trace_count, 128,
                    $"[TOAPLAN input] external start={start} coin={coin}");

            if (start)
                m_external_start_frames = Math.Max(m_external_start_frames, 12);
            if (coin)
                m_external_coin_frames = Math.Max(m_external_coin_frames, 12);
        }


        public void outzone(machine_config config)
        {
            M68000(config, m_maincpu, new XTAL(10_000_000));
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, outzone_main_map);
            m_maincpu.op0.set_reset_instruction_handler(reset_sound);

            Z80(config, m_audiocpu, MasterClock / 8);
            m_audiocpu.op0.memory().set_addrmap(AS_PROGRAM, sound_map);
            m_audiocpu.op0.memory().set_addrmap(AS_IO, outzone_sound_io_map);

            config.set_maximum_quantum(attotime.from_hz(600));

            screen_device screen = SCREEN(config, "screen", SCREEN_TYPE_RASTER);
            screen.set_screen_update(screen_update);
            screen.set_raw(PixelClock, HTotal, HBEnd, HBStart, VTotal55, VBEnd, VBStart);
            screen.screen_vblank().set((write_line_delegate)screen_vblank).reg();

            SPEAKER(config, "mono").front_center();
            YM3812(config, m_ymsnd, MasterClock / 8);
            m_ymsnd.op0.set_irq_handler(state => m_audiocpu.op0.set_input_line(0, state));
            m_ymsnd.op0.disound.add_route(ALL_OUTPUTS, "mono", 0.75);
        }


        void outzone_main_map(address_map map, device_t device)
        {
            map.op(0x000000, 0x03ffff).rom();
            map.op(0x100000, 0x100007).rw((read16_delegate)fcu_host_r, (write16_delegate)fcu_host_w);
            map.op(0x140000, 0x140fff).rw((read16_delegate)shared_r, (write16_delegate)shared_w);
            map.op(0x200000, 0x20001f).rw((read16_delegate)bcu_host_r, (write16_delegate)bcu_host_w);
            map.op(0x240000, 0x243fff).rw((read16_delegate)mainram_r, (write16_delegate)mainram_w);
            map.op(0x300000, 0x307fff).rw((read16_delegate)vctrl_r, (write16_delegate)vctrl_w);
            map.op(0x340000, 0x340007).rw((read16_delegate)tile_offset_r, (write16_delegate)tile_offset_w);
        }


        void sound_map(address_map map, device_t device)
        {
            map.op(0x0000, 0x7fff).rom();
            map.op(0x8000, 0x87ff).rw((read8sm_delegate)shared_sound_r, (write8sm_delegate)shared_sound_w);
        }


        void outzone_sound_io_map(address_map map, device_t device)
        {
            map.global_mask(0xff);
            map.op(0x00, 0x01).rw((read8sm_delegate)ym3812_r, (write8sm_delegate)ym3812_w);
            map.op(0x04, 0x04).w((write8sm_delegate)coin_w);
            map.op(0x08, 0x08).r((read8sm_delegate)dswa_r);
            map.op(0x0c, 0x0c).r((read8sm_delegate)dswb_r);
            map.op(0x10, 0x10).r((read8sm_delegate)system_r);
            map.op(0x14, 0x14).r((read8sm_delegate)p1_r);
            map.op(0x18, 0x18).r((read8sm_delegate)p2_r);
            map.op(0x1c, 0x1c).r((read8sm_delegate)tjump_r);
        }


        u16 fcu_host_r(address_space space, offs_t offset, u16 mem_mask)
        {
            switch ((offset << 1) & 0x6)
            {
                case 0x0:
                    return m_vblank_state;
                case 0x2:
                    return m_spriteram_offs;
                case 0x4:
                    return m_spriteram[m_spriteram_offs & (SpriteRamWords - 1)];
                case 0x6:
                    return m_spritesizeram[m_spriteram_offs & (SpriteSizeRamWords - 1)];
                default:
                    return 0;
            }
        }


        void fcu_host_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            switch ((offset << 1) & 0x6)
            {
                case 0x2:
                    m_spriteram_offs = CombineWord(m_spriteram_offs, data, mem_mask);
                    break;
                case 0x4:
                {
                    int index = m_spriteram_offs & (SpriteRamWords - 1);
                    u16 value = CombineWord(m_spriteram[index], data, mem_mask);
                    if (m_spriteram[index] != value)
                    {
                        m_spriteram[index] = value;
                        MarkFcuDirty();
                    }
                    m_spriteram_offs++;
                    break;
                }
                case 0x6:
                {
                    int index = m_spriteram_offs & (SpriteSizeRamWords - 1);
                    u16 value = CombineWord(m_spritesizeram[index], data, mem_mask);
                    if (m_spritesizeram[index] != value)
                    {
                        m_spritesizeram[index] = value;
                        MarkFcuDirty();
                    }
                    m_spriteram_offs++;
                    break;
                }
            }
        }


        u16 shared_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int index = (int)(offset & (SharedRamSize - 1));
            u8 shared = m_sharedram[index];
            if (index == 6)
            {
                u8 system = SystemInputValue();
                shared = MergeLiveInputMirror(shared, system);
                if (TraceInput && system != 0)
                    Console.WriteLine($"[TOAPLAN input] M68K shared[6] overlay system=0x{system:x2} shared=0x{shared:x2} coinFrames={m_external_coin_frames} startFrames={m_external_start_frames}");
            }
            else if (index == 7)
            {
                shared = MergeLiveInputMirror(shared, (u8)(ioport("P1").read() & 0xff));
            }
            else if (index == 8)
            {
                shared = MergeLiveInputMirror(shared, (u8)(ioport("P2").read() & 0xff));
            }

            u16 value = (u16)(0xff00 | shared);
            if ((index == 6 || index == 7 || index == 8) && TraceInput)
                TraceLimited(true, ref m_input_trace_count, 128,
                    $"[TOAPLAN input] shared idx=0x{index:x3} value=0x{shared:x2} coinFrames={m_external_coin_frames} startFrames={m_external_start_frames}");

            TraceLimited(TraceShared, ref m_shared_trace_count, 96,
                $"[TOAPLAN shared] M68K R off=0x{offset:x3} idx=0x{index:x3} mask=0x{mem_mask:x4} -> 0x{value:x4}");
            return value;
        }


        static u8 MergeLiveInputMirror(u8 shared, u8 live)
        {
            if (live == 0x00)
                return shared;

            return (u8)(shared | live);
        }


        void shared_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0x00ff) == 0)
                return;

            int index = (int)(offset & (SharedRamSize - 1));
            m_sharedram[index] = (u8)(data & 0xff);
            TraceLimited(TraceShared, ref m_shared_trace_count, 96,
                $"[TOAPLAN shared] M68K W off=0x{offset:x3} idx=0x{index:x3} data=0x{data:x4} mask=0x{mem_mask:x4} byte=0x{m_sharedram[index]:x2}");
        }


        u16 mainram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_mainram[offset & (MainRamWords - 1)];
        }


        void mainram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int index = (int)(offset & (MainRamWords - 1));
            u16 value = CombineWord(m_mainram[index], data, mem_mask);
            m_mainram[index] = value;

            u32 pc = m_maincpu.op0.Pc;
            if (TraceMainRam && pc != 0x013954 && pc != 0x013962 && index >= 0x5a0 && index <= 0x5d0)
                TraceLimited(true, ref m_mainram_trace_count, 256,
                    $"[TOAPLAN mainram] W addr=0x{0x240000 + (index << 1):x6} idx=0x{index:x4} data=0x{data:x4} mask=0x{mem_mask:x4} val=0x{value:x4} pc=0x{pc:x6}");
        }


        u16 bcu_host_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int reg = (int)(offset << 1);
            if (reg == 0x02)
                return m_bcu_ram_offs;

            if (reg == 0x04 || reg == 0x06)
                return bcu_tileram_r(reg);

            if (reg >= 0x10 && reg <= 0x1f)
                return bcu_scroll_r((reg - 0x10) >> 1);

            return 0;
        }


        void bcu_host_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int reg = (int)(offset << 1);
            if (reg == 0x00)
                return;

            if (reg == 0x02)
            {
                m_bcu_ram_offs = CombineWord(m_bcu_ram_offs, data, mem_mask);
                TraceLimited(TraceHost, ref m_host_trace_count, 128,
                    $"[TOAPLAN host] BCU offs=0x{m_bcu_ram_offs:x4} data=0x{data:x4} mask=0x{mem_mask:x4}");
                return;
            }

            if (reg == 0x04 || reg == 0x06)
            {
                bcu_tileram_w(reg, data, mem_mask);
                TraceLimited(TraceHost, ref m_host_trace_count, 128,
                    $"[TOAPLAN host] BCU tile reg=0x{reg:x2} offs=0x{m_bcu_ram_offs:x4} data=0x{data:x4} mask=0x{mem_mask:x4}");
                return;
            }

            if (reg >= 0x10 && reg <= 0x1f)
            {
                bcu_scroll_w((reg - 0x10) >> 1, data, mem_mask);
                TraceLimited(TraceHost, ref m_host_trace_count, 128,
                    $"[TOAPLAN host] BCU scroll reg=0x{reg:x2} data=0x{data:x4} mask=0x{mem_mask:x4}");
            }
        }


        u16 vctrl_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int byteOffset = (int)(offset << 1);
            if (byteOffset == 0)
                return m_vblank_state;

            if (byteOffset >= 0x4000 && byteOffset < 0x6000)
                return m_paletteram[0, (byteOffset - 0x4000) >> 1];

            if (byteOffset >= 0x6000 && byteOffset < 0x8000)
                return m_paletteram[1, (byteOffset - 0x6000) >> 1];

            return 0;
        }


        void vctrl_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int byteOffset = (int)(offset << 1);
            if (byteOffset == 0x0002)
            {
                m_vctrl_intenable = (u8)(data & 0xff);
                TraceLimited(TraceHost, ref m_host_trace_count, 128,
                    $"[TOAPLAN host] VCTRL int data=0x{data:x4} mask=0x{mem_mask:x4} enable=0x{m_vctrl_intenable:x2}");
                return;
            }

            if (byteOffset >= 0x4000 && byteOffset < 0x6000)
            {
                int index = (byteOffset - 0x4000) >> 1;
                u16 value = CombineWord(m_paletteram[0, index], data, mem_mask);
                if (m_paletteram[0, index] != value)
                {
                    m_paletteram[0, index] = value;
                    UpdatePaletteColor(0, index);
                    MarkBcuDirty();
                }
                return;
            }

            if (byteOffset >= 0x6000 && byteOffset < 0x8000)
            {
                int index = (byteOffset - 0x6000) >> 1;
                u16 value = CombineWord(m_paletteram[1, index], data, mem_mask);
                if (m_paletteram[1, index] != value)
                {
                    m_paletteram[1, index] = value;
                    UpdatePaletteColor(1, index);
                    MarkFcuDirty();
                }
            }
        }


        u16 tile_offset_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_tile_offsets[offset & (TileOffsetWords - 1)];
        }


        void tile_offset_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int index = (int)(offset & (TileOffsetWords - 1));
            u16 value = CombineWord(m_tile_offsets[index], data, mem_mask);
            if (m_tile_offsets[index] == value)
                return;

            m_tile_offsets[index] = value;

            if (index == 0)
                m_bcu_offsetx = m_tile_offsets[index];
            else if (index == 1)
                m_bcu_offsety = m_tile_offsets[index];

            if (index == 3)
                m_fcu_flipscreen = (u8)(data & 0xff);
            if (index == 3)
                MarkFcuDirty();
            else
                MarkBcuDirty();
        }


        u8 shared_sound_r(offs_t offset)
        {
            int index = (int)(offset & (SharedRamSize - 1));
            u8 value = m_sharedram[index];
            if (index == 1 && value == 0)
                TraceLimited(TraceShared, ref m_z80_shared1_spin_trace_count, 8,
                    $"[TOAPLAN shared] Z80 R spin off=0x{offset:x3} idx=0x{index:x3} -> 0x{value:x2}");
            else
                TraceLimited(TraceShared, ref m_shared_trace_count, 160,
                    $"[TOAPLAN shared] Z80 R off=0x{offset:x3} idx=0x{index:x3} -> 0x{value:x2}");
            return value;
        }


        void shared_sound_w(offs_t offset, u8 data)
        {
            int index = (int)(offset & (SharedRamSize - 1));
            m_sharedram[index] = data;
            if (TraceInput && index == 6 && data != 0)
                Console.WriteLine($"[TOAPLAN input] Z80 mirror SYSTEM -> shared[6]=0x{data:x2}");
            TraceLimited(TraceShared, ref m_shared_trace_count, 96,
                $"[TOAPLAN shared] Z80 W off=0x{offset:x3} idx=0x{index:x3} data=0x{data:x2}");
        }


        u8 ym3812_r(offs_t offset) => m_ymsnd.op0.read(offset);
        void ym3812_w(offs_t offset, u8 data) => m_ymsnd.op0.write(offset, data);
        void coin_w(offs_t offset, u8 data)
        {
            machine().bookkeeping().coin_counter_w(0, data & 0x01);
            machine().bookkeeping().coin_counter_w(1, data & 0x02);
            machine().bookkeeping().coin_lockout_w(0, (data & 0x04) == 0 ? 1 : 0);
            machine().bookkeeping().coin_lockout_w(1, (data & 0x08) == 0 ? 1 : 0);
        }
        u8 dswa_r(offs_t offset) => (u8)(ioport("DSWA").read() & 0xff);
        u8 dswb_r(offs_t offset) => (u8)(ioport("DSWB").read() & 0xff);
        u8 system_r(offs_t offset)
        {
            u8 value = SystemInputValue();
            if ((m_external_coin_frames > 0 || m_external_start_frames > 0 || value != 0x00) && TraceInput)
            {
                if (value != 0)
                    Console.WriteLine($"[TOAPLAN input] SYSTEM pc=0x{m_audiocpu.op0.DebugPc:x4} value=0x{value:x2} raw=0x{ioport("SYSTEM").read() & 0xff:x2} coinFrames={m_external_coin_frames} startFrames={m_external_start_frames}");
                TraceLimited(true, ref m_input_trace_count, 128,
                    $"[TOAPLAN input] SYSTEM raw=0x{ioport("SYSTEM").read() & 0xff:x2} value=0x{value:x2} coinFrames={m_external_coin_frames} startFrames={m_external_start_frames}");
            }
            return value;
        }
        u8 p1_r(offs_t offset) => (u8)(ioport("P1").read() & 0xff);
        u8 p2_r(offs_t offset) => (u8)(ioport("P2").read() & 0xff);
        u8 tjump_r(offs_t offset) => (u8)(ioport("TJUMP").read() & 0xff);


        void screen_vblank(int state)
        {
            m_vblank_state = (u8)(state != 0 ? 1 : 0);
            if (state != 0 && m_vctrl_intenable != 0)
                m_maincpu.op0.set_input_line(4, HOLD_LINE);
            if (state != 0)
                m_ymsnd.op0.frame_irq();
        }


        void reset_sound()
        {
            m_audiocpu.op0.pulse_input_line(INPUT_LINE_RESET, attotime.zero);
        }


        uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            LatchLiveSystemInput();
            if (m_video_dirty)
            {
                EnsureGraphicsDecoded();
                bool redrawBcu = m_bcu_dirty || !m_bcu_cache_valid;
                if (redrawBcu)
                {
                    if (m_bcu_tile_count == 0)
                        bitmap.fill(0xff000000U, cliprect);
                    Array.Clear(m_priority_bitmap, 0, m_priority_bitmap.Length);
                    RenderBcu(bitmap, cliprect);
                    SaveBcuCache(bitmap);
                    m_bcu_dirty = false;
                }
                else
                {
                    if (!RestoreBcuCache(bitmap))
                    {
                        if (m_bcu_tile_count == 0)
                            bitmap.fill(0xff000000U, cliprect);
                        Array.Clear(m_priority_bitmap, 0, m_priority_bitmap.Length);
                        RenderBcu(bitmap, cliprect);
                        SaveBcuCache(bitmap);
                        m_bcu_dirty = false;
                    }
                }

                if (m_fcu_dirty)
                    RenderFcu(bitmap, cliprect);
                m_fcu_dirty = false;
                m_video_dirty = false;
            }
            else if (!m_bcu_cache_valid)
            {
                EnsureGraphicsDecoded();
                if (m_bcu_tile_count == 0)
                {
                    bitmap.fill(0xff000000U, cliprect);
                    Array.Clear(m_priority_bitmap, 0, m_priority_bitmap.Length);
                    SaveBcuCache(bitmap);
                }
            }
            TraceVideoState();
            if (m_external_start_frames > 0)
                m_external_start_frames--;
            if (m_external_coin_frames > 0)
                m_external_coin_frames--;
            m_frame_counter++;
            if (TraceZ80 && (m_frame_counter % 30 == 0 || m_external_coin_frames > 0 || m_external_start_frames > 0))
                Console.WriteLine($"[TOAPLAN z80] frame={m_frame_counter} pc=0x{m_audiocpu.op0.DebugPc:x4} shared2=0x{m_sharedram[2]:x2} shared6=0x{m_sharedram[6]:x2} coinFrames={m_external_coin_frames} startFrames={m_external_start_frames}");
            return 0;
        }


        void MarkAllVideoDirty()
        {
            m_video_dirty = true;
            m_bcu_dirty = true;
            m_fcu_dirty = true;
            m_bcu_cache_valid = false;
        }


        void MarkBcuDirty()
        {
            m_video_dirty = true;
            m_bcu_dirty = true;
            m_fcu_dirty = true;
        }


        void MarkFcuDirty()
        {
            m_video_dirty = true;
            m_fcu_dirty = true;
        }


        void LatchLiveSystemInput()
        {
            u8 system = (u8)(ioport("SYSTEM").read() & 0xff);
            if ((system & 0x08) != 0)
                m_external_coin_frames = Math.Max(m_external_coin_frames, 24);
            if ((system & 0x20) != 0)
                m_external_start_frames = Math.Max(m_external_start_frames, 24);
        }


        u8 SystemInputValue()
        {
            u8 value = (u8)(ioport("SYSTEM").read() & 0xff);
            if (m_external_coin_frames > 0)
                value |= 0x08;
            if (m_external_start_frames > 0)
                value |= 0x20;
            return value;
        }

        protected override void machine_start()
        {
            save_item(NAME(new { m_sharedram }));
            save_item(NAME(new { m_mainram }));
            save_item(NAME(new { m_bcu_vram }));
            save_item(NAME(new { m_bcu_scrollx }));
            save_item(NAME(new { m_bcu_scrolly }));
            save_item(NAME(new { m_spriteram }));
            save_item(NAME(new { m_spritesizeram }));
            save_item(NAME(new { m_paletteram }));
            save_item(NAME(new { m_tile_offsets }));
            SaveStateRef(nameof(m_bcu_ram_offs), () => m_bcu_ram_offs, value => m_bcu_ram_offs = value);
            SaveStateRef(nameof(m_spriteram_offs), () => m_spriteram_offs, value => m_spriteram_offs = value);
            SaveStateRef(nameof(m_fcu_flipscreen), () => m_fcu_flipscreen, value => m_fcu_flipscreen = value);
            SaveStateRef(nameof(m_bcu_flipscreen), () => m_bcu_flipscreen, value => m_bcu_flipscreen = value);
            SaveStateRef(nameof(m_vctrl_intenable), () => m_vctrl_intenable, value => m_vctrl_intenable = value);
            SaveStateRef(nameof(m_vblank_state), () => m_vblank_state, value => m_vblank_state = value);
            SaveStateRef(nameof(m_bcu_offsetx), () => m_bcu_offsetx, value => m_bcu_offsetx = value);
            SaveStateRef(nameof(m_bcu_offsety), () => m_bcu_offsety, value => m_bcu_offsety = value);
            SaveStateRef(nameof(m_frame_counter), () => m_frame_counter, value => m_frame_counter = value);
            SaveStateRef(nameof(m_external_start_frames), () => m_external_start_frames, value => m_external_start_frames = value);
            SaveStateRef(nameof(m_external_coin_frames), () => m_external_coin_frames, value => m_external_coin_frames = value);
        }

        protected override void machine_reset()
        {
            Array.Clear(m_sharedram, 0, m_sharedram.Length);
            Array.Clear(m_bcu_vram, 0, m_bcu_vram.Length);
            Array.Clear(m_bcu_scrollx, 0, m_bcu_scrollx.Length);
            Array.Clear(m_bcu_scrolly, 0, m_bcu_scrolly.Length);
            Array.Clear(m_spriteram, 0, m_spriteram.Length);
            Array.Clear(m_spritesizeram, 0, m_spritesizeram.Length);
            Array.Clear(m_paletteram, 0, m_paletteram.Length);
            Array.Clear(m_palette_colors[0], 0, m_palette_colors[0].Length);
            Array.Clear(m_palette_colors[1], 0, m_palette_colors[1].Length);
            Array.Clear(m_tile_offsets, 0, m_tile_offsets.Length);
            m_decoded_bcu_tiles = null;
            m_decoded_fcu_tiles = null;
            m_bcu_tile_count = 0;
            m_fcu_tile_count = 0;
            m_bcu_ram_offs = 0;
            m_spriteram_offs = 0;
            m_fcu_flipscreen = 0;
            m_bcu_flipscreen = 0;
            m_vctrl_intenable = 0;
            m_vblank_state = 0;
            m_bcu_offsetx = 0;
            m_bcu_offsety = 0;
            m_frame_counter = 0;
            m_shared_trace_count = 0;
            m_host_trace_count = 0;
            m_z80_shared1_spin_trace_count = 0;
            m_input_trace_count = 0;
            m_mainram_trace_count = 0;
            m_external_start_frames = 0;
            m_external_coin_frames = 0;
            MarkAllVideoDirty();
            Array.Clear(m_mainram, 0, m_mainram.Length);
            reset_sound();
        }

        protected override void device_post_load()
        {
            RebuildPaletteColors();
            MarkAllVideoDirty();
        }

        void SaveStateRef<T>(string itemName, Func<T> getter, Action<T> setter)
        {
            machine().save().save_item_ref(this, name(), tag(), 0, itemName, getter, setter);
        }


        u16 bcu_tileram_r(int reg)
        {
            int layer = (m_bcu_ram_offs >> 12) & 3;
            int index = m_bcu_ram_offs & (BcuLayerWords - 1);
            int shift = (reg == 0x04) ? 16 : 0;
            return (u16)((m_bcu_vram[layer, index] >> shift) & 0xffff);
        }


        void bcu_tileram_w(int reg, u16 data, u16 mem_mask)
        {
            int layer = (m_bcu_ram_offs >> 12) & 3;
            int index = m_bcu_ram_offs & (BcuLayerWords - 1);
            int shift = (reg == 0x04) ? 16 : 0;
            u32 mask = (u32)mem_mask << shift;
            u32 value = (m_bcu_vram[layer, index] & ~mask) | ((u32)(data & mem_mask) << shift);
            if (m_bcu_vram[layer, index] != value)
            {
                m_bcu_vram[layer, index] = value;
                MarkBcuDirty();
            }
        }


        u16 bcu_scroll_r(int scrollIndex)
        {
            int layer = scrollIndex >> 1;
            return (scrollIndex & 1) == 0 ? m_bcu_scrollx[layer] : m_bcu_scrolly[layer];
        }


        void bcu_scroll_w(int scrollIndex, u16 data, u16 mem_mask)
        {
            int layer = scrollIndex >> 1;
            if ((scrollIndex & 1) == 0)
            {
                u16 value = CombineWord(m_bcu_scrollx[layer], data, mem_mask);
                if (m_bcu_scrollx[layer] == value)
                    return;
                m_bcu_scrollx[layer] = value;
            }
            else
            {
                u16 value = CombineWord(m_bcu_scrolly[layer], data, mem_mask);
                if (m_bcu_scrolly[layer] == value)
                    return;
                m_bcu_scrolly[layer] = value;
            }
            MarkBcuDirty();
        }


        static u16 CombineWord(u16 value, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0xff00) != 0)
                value = (u16)((value & 0x00ff) | (data & 0xff00));
            if ((mem_mask & 0x00ff) != 0)
                value = (u16)((value & 0xff00) | (data & 0x00ff));
            return value;
        }


        static void TraceLimited(bool enabled, ref int counter, int limit, string message)
        {
            if (!enabled || counter >= limit)
                return;

            Console.WriteLine(message);
            counter++;
        }


        void EnsureGraphicsDecoded()
        {
            if (m_decoded_bcu_tiles == null)
            {
                memory_region tiles = memregion("tiles");
                if (tiles != null && tiles.base_() != null)
                {
                    m_bcu_tile_count = Math.Min((int)(tiles.bytes() / 2 / TileBytes), 0x8000);
                    m_decoded_bcu_tiles = DecodeTiles(tiles.base_(), (int)tiles.bytes(), m_bcu_tile_count);
                }
                else
                {
                    m_decoded_bcu_tiles = Array.Empty<byte []>();
                    m_bcu_tile_count = 0;
                }
            }

            if (m_decoded_fcu_tiles == null)
            {
                memory_region sprites = memregion("sprites");
                if (sprites != null && sprites.base_() != null)
                {
                    m_fcu_tile_count = Math.Min((int)(sprites.bytes() / 2 / TileBytes), 0x8000);
                    m_decoded_fcu_tiles = DecodeTiles(sprites.base_(), (int)sprites.bytes(), m_fcu_tile_count);
                }
                else
                {
                    m_decoded_fcu_tiles = Array.Empty<byte []>();
                    m_fcu_tile_count = 0;
                }
            }
        }


        static byte [] [] DecodeTiles(MemoryU8 rom, int bytes, int tileCount)
        {
            byte [] [] decoded = new byte[tileCount][];
            int halfBits = (bytes / 2) * 8;
            int [] planeOffsets = { halfBits + 8, halfBits, 8, 0 };

            for (int tile = 0; tile < tileCount; tile++)
            {
                byte [] pixels = new byte[TilePixels];
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


        void RenderBcu(bitmap_rgb32 bitmap, rectangle cliprect)
        {
            if (m_bcu_tile_count == 0)
                return;

            RenderBcuLayer(bitmap, cliprect, 0, -1, true, 0);
            for (int priority = 1; priority < 16; priority++)
            {
                for (int layer = BcuLayerCount - 1; layer >= 0; layer--)
                    RenderBcuLayer(bitmap, cliprect, layer, priority, false, (byte)priority);
            }
        }


        void SaveBcuCache(bitmap_rgb32 bitmap)
        {
            if (bitmap.width() < ScreenWidth || bitmap.height() < ScreenHeight)
            {
                m_bcu_cache_valid = false;
                return;
            }

            PointerU32 firstRow = bitmap.pix(0);
            byte [] bitmapData = firstRow.Buffer.data_raw;
            int bitmapOffset = firstRow.Offset;
            int sourceRowBytes = bitmap.rowpixels() * 4;
            int cacheRowBytes = ScreenWidth * 4;
            for (int y = 0; y < ScreenHeight; y++)
                Buffer.BlockCopy(bitmapData, bitmapOffset + y * sourceRowBytes, m_bcu_bitmap_cache, y * cacheRowBytes, cacheRowBytes);
            Array.Copy(m_priority_bitmap, m_bcu_priority_cache, m_bcu_priority_cache.Length);
            m_bcu_cache_valid = true;
        }


        bool RestoreBcuCache(bitmap_rgb32 bitmap)
        {
            if (bitmap.width() < ScreenWidth || bitmap.height() < ScreenHeight || !m_bcu_cache_valid)
            {
                MarkAllVideoDirty();
                return false;
            }

            PointerU32 firstRow = bitmap.pix(0);
            byte [] bitmapData = firstRow.Buffer.data_raw;
            int bitmapOffset = firstRow.Offset;
            int destinationRowBytes = bitmap.rowpixels() * 4;
            int cacheRowBytes = ScreenWidth * 4;
            for (int y = 0; y < ScreenHeight; y++)
                Buffer.BlockCopy(m_bcu_bitmap_cache, y * cacheRowBytes, bitmapData, bitmapOffset + y * destinationRowBytes, cacheRowBytes);
            Array.Copy(m_bcu_priority_cache, m_priority_bitmap, m_priority_bitmap.Length);
            return true;
        }


        void RenderBcuLayer(bitmap_rgb32 bitmap, rectangle cliprect, int layer, int priority, bool opaque, byte priorityValue)
        {
            int layerDx = layer switch { 0 => 6, 1 => 4, 2 => 2, _ => 0 };
            int scrollX = ((m_bcu_scrollx[layer] >> 7) - m_bcu_offsetx - BcuOffsetX + layerDx) & 0x1ff;
            int scrollY = ((m_bcu_scrolly[layer] >> 7) - m_bcu_offsety - BcuOffsetY) & 0x1ff;

            int startTileX = ((cliprect.min_x + scrollX) >> 3) - 1;
            int endTileX = ((cliprect.max_x + scrollX) >> 3) + 1;
            int startTileY = ((cliprect.min_y + scrollY) >> 3) - 1;
            int endTileY = ((cliprect.max_y + scrollY) >> 3) + 1;

            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                int tileY = ty & 63;
                int screenY = (ty << 3) - scrollY;

                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    int tileX = tx & 63;
                    int screenX = (tx << 3) - scrollX;
                    int tileIndex = tileY * 64 + tileX;
                    u32 entry = m_bcu_vram[layer, tileIndex];
                    int tilePriority = (int)((entry >> 28) & 0x0f);
                    bool invisible = (entry & 0x8000U) != 0;
                    if (!opaque && (invisible || tilePriority != priority))
                        continue;

                    int code = (int)(entry & 0x7fff) % m_bcu_tile_count;
                    int color = (int)((entry >> 16) & 0x3f);
                    DrawTile(bitmap, cliprect, m_decoded_bcu_tiles[code], screenX, screenY, color, 0, opaque, priorityValue);
                }
            }
        }


        void RenderFcu(bitmap_rgb32 bitmap, rectangle cliprect)
        {
            if (m_fcu_tile_count == 0)
                return;

            for (int offs = SpriteRamWords - 4; offs >= 0; offs -= 4)
            {
                if ((m_spriteram[offs] & 0x8000) != 0)
                    continue;

                u16 attrib = m_spriteram[offs + 1];
                int spritePriority = (attrib >> 12) & 0x0f;
                uint priorityMask = spritePriority >= 15 ? 0U : uint.MaxValue << (spritePriority + 1);
                int code = m_spriteram[offs] & 0x7fff;
                int color = attrib & 0x3f;
                int sizeIndex = (attrib >> 6) & 0x3f;
                int width = (m_spritesizeram[sizeIndex] & 0x0f) * 8;
                int height = ((m_spritesizeram[sizeIndex] >> 4) & 0x0f) * 8;
                if (width <= 0 || height <= 0)
                    continue;

                int sxBase = (m_spriteram[offs + 2] >> 7) & 0x1ff;
                int syBase = (m_spriteram[offs + 3] >> 7) & 0x1ff;
                if (sxBase >= 0x180) sxBase -= 0x200;
                if (syBase >= 0x180) syBase -= 0x200;

                for (int dy = 0; dy < height; dy += 8)
                {
                    for (int dx = 0; dx < width; dx += 8)
                    {
                        int tileCode = code++ % m_fcu_tile_count;
                        DrawSpriteTile(bitmap, cliprect, m_decoded_fcu_tiles[tileCode], sxBase + dx, syBase + dy, color, priorityMask);
                    }
                }
            }
        }


        void DrawTile(bitmap_rgb32 bitmap, rectangle cliprect, byte [] pixels, int dstX, int dstY, int color, int paletteLayer, bool opaque, byte priorityValue)
        {
            int minX = Math.Max(dstX, cliprect.min_x);
            int minY = Math.Max(dstY, cliprect.min_y);
            int maxX = Math.Min(dstX + 7, cliprect.max_x);
            int maxY = Math.Min(dstY + 7, cliprect.max_y);
            if (minX > maxX || minY > maxY)
                return;

            u32[] palette = m_palette_colors[paletteLayer & 1];
            int paletteBase = color << 4;

            for (int y = minY; y <= maxY; y++)
            {
                int py = y - dstY;
                int priorityRow = y * ScreenWidth;
                PointerU32 bitmapRow = bitmap.pix(y);
                byte[] bitmapData = bitmapRow.Buffer.data_raw;
                int bitmapRowOffset = bitmapRow.Offset;
                for (int x = minX; x <= maxX; x++)
                {
                    int pen = pixels[(py << 3) | (x - dstX)];
                    if (pen == 0 && !opaque)
                        continue;

                    WriteRgb32(bitmapData, bitmapRowOffset + (x << 2), palette[(paletteBase | pen) & 0x3ff]);
                    if (!opaque)
                        m_priority_bitmap[priorityRow + x] = priorityValue;
                }
            }
        }


        void DrawSpriteTile(bitmap_rgb32 bitmap, rectangle cliprect, byte [] pixels, int dstX, int dstY, int color, uint priorityMask)
        {
            int minX = Math.Max(dstX, cliprect.min_x);
            int minY = Math.Max(dstY, cliprect.min_y);
            int maxX = Math.Min(dstX + 7, cliprect.max_x);
            int maxY = Math.Min(dstY + 7, cliprect.max_y);
            if (minX > maxX || minY > maxY)
                return;

            u32[] palette = m_palette_colors[1];
            int paletteBase = color << 4;

            for (int y = minY; y <= maxY; y++)
            {
                int py = y - dstY;
                int priorityRow = y * ScreenWidth;
                PointerU32 bitmapRow = bitmap.pix(y);
                byte[] bitmapData = bitmapRow.Buffer.data_raw;
                int bitmapRowOffset = bitmapRow.Offset;
                for (int x = minX; x <= maxX; x++)
                {
                    int pen = pixels[(py << 3) | (x - dstX)];
                    if (pen == 0)
                        continue;

                    int priorityIndex = priorityRow + x;
                    if (((1U << (m_priority_bitmap[priorityIndex] & 0x1f)) & priorityMask) == 0)
                        WriteRgb32(bitmapData, bitmapRowOffset + (x << 2), palette[(paletteBase | pen) & 0x3ff]);
                    m_priority_bitmap[priorityIndex] = 31;
                }
            }
        }


        static void WriteRgb32(byte [] data, int offset, u32 color)
        {
            data[offset] = (byte)color;
            data[offset + 1] = (byte)(color >> 8);
            data[offset + 2] = (byte)(color >> 16);
            data[offset + 3] = (byte)(color >> 24);
        }


        u32 PaletteColor(int layer, int index)
        {
            return m_palette_colors[layer & 1][index & 0x3ff];
        }


        void RebuildPaletteColors()
        {
            for (int layer = 0; layer < 2; layer++)
                for (int index = 0; index < PaletteWords; index++)
                    UpdatePaletteColor(layer, index);
        }


        void UpdatePaletteColor(int layer, int index)
        {
            u16 raw = m_paletteram[layer & 1, index & 0x3ff];
            int r = (raw & 0x001f) << 3;
            int g = ((raw >> 5) & 0x001f) << 3;
            int b = ((raw >> 10) & 0x001f) << 3;
            r |= r >> 5;
            g |= g >> 5;
            b |= b >> 5;
            m_palette_colors[layer & 1][index & 0x3ff] = 0xff000000U | (u32)(r << 16) | (u32)(g << 8) | (u32)b;
        }


        void TraceVideoState()
        {
            if (!TraceVideo || (m_frame_counter % 30) != 0)
                return;

            int tileEntries = 0;
            int spriteEntries = 0;
            int palette0 = 0;
            int palette1 = 0;

            for (int layer = 0; layer < BcuLayerCount; layer++)
            {
                for (int i = 0; i < BcuLayerWords; i++)
                {
                    if (m_bcu_vram[layer, i] != 0)
                        tileEntries++;
                }
            }

            for (int i = 0; i < SpriteRamWords; i += 4)
            {
                if ((m_spriteram[i] & 0x7fff) != 0 || m_spriteram[i + 1] != 0 || m_spriteram[i + 2] != 0 || m_spriteram[i + 3] != 0)
                    spriteEntries++;
            }

            for (int i = 0; i < 0x400; i++)
            {
                if (m_paletteram[0, i] != 0)
                    palette0++;
                if (m_paletteram[1, i] != 0)
                    palette1++;
            }

            Console.Error.WriteLine(
                $"[TOAPLAN] frame={m_frame_counter} tiles={tileEntries} sprites={spriteEntries} pal0={palette0} pal1={palette1} " +
                $"bcuOff=0x{m_bcu_ram_offs:x4} sprOff=0x{m_spriteram_offs:x4} int={m_vctrl_intenable} " +
                $"offs={m_bcu_offsetx:x4}/{m_bcu_offsety:x4} pc=0x{m_maincpu.op0.Pc:x6}");
        }


        public void init_outzone()
        {
        }
    }


    public class toaplan1 : construct_ioport_helper
    {
        const u32 ROM_GROUPWORD = 0x100;
        static readonly toaplan1 m_toaplan1 = new toaplan1();

        static tiny_rom_entry ROM_LOAD16_WORD(string name, u32 offset, u32 length, string hash)
        {
            return ROMX_LOAD(name, offset, length, hash, ROM_GROUPWORD);
        }

        static readonly tiny_rom_entry [] rom_outzone =
        {
            ROM_REGION(0x040000, "maincpu", 0),
            ROM_LOAD16_BYTE("tp_018_07.6f", 0x000001, 0x020000, CRC("9704db16") + SHA1("3d65c7a24d0a7c62d9f68745a177ea1ab06b8d69")),
            ROM_LOAD16_BYTE("tp_018_08.6f", 0x000000, 0x020000, CRC("127a38d7") + SHA1("7dfe88264c7094c876e00f789e3b3f757c76dcfc")),

            ROM_REGION(0x008000, "audiocpu", 0),
            ROM_LOAD("tp_018_09.3j", 0x000000, 0x008000, CRC("73d8e235") + SHA1("6d19566485318bb036d16eedb38abebbfb320ce1")),

            ROM_REGION(0x100000, "tiles", 0),
            ROM_LOAD16_WORD("tp-018_rom5.19h", 0x000000, 0x080000, CRC("c64ec7b6") + SHA1("690804336ea640d1fd907cbb4b826f1bd3eec396")),
            ROM_LOAD16_WORD("tp-018_rom6.22h", 0x080000, 0x080000, CRC("64b6c5ac") + SHA1("0eba3469c1b06f6b9e2da7d34181f31470461f13")),

            ROM_REGION(0x080000, "sprites", 0),
            ROM_LOAD16_BYTE("tp-018_rom2.1c", 0x000000, 0x020000, CRC("6bb72d16") + SHA1("075b79af804275e7e0679c22d827a2315c98acbc")),
            ROM_LOAD16_BYTE("tp-018_rom1.1e", 0x000001, 0x020000, CRC("0934782d") + SHA1("169e4896d19a632e226b571de9329d5f6f449b08")),
            ROM_LOAD16_BYTE("tp-018_rom3.1d", 0x040000, 0x020000, CRC("ec903c07") + SHA1("6cdf6b92665e6b1c5df1f5a304a83eb8995a9d0f")),
            ROM_LOAD16_BYTE("tp-018_rom4.1b", 0x040001, 0x020000, CRC("50cbf1a8") + SHA1("0133deecd464d9d057c838759e76b48e77202d7e")),

            ROM_REGION(0x000040, "proms", 0),
            ROM_LOAD("tp018_10.rom10.18a", 0x000000, 0x000020, CRC("bc88cced") + SHA1("d59307282bcf24ee8d2bd1b8c6d26343abbac225")),
            ROM_LOAD("tp018_11.rom11.22c", 0x000020, 0x000020, CRC("a1e17492") + SHA1("2ab7fb10672b7ef17271266f760e6e45611549c6")),

            ROM_END,
        };


        static void toaplan1_state_outzone(machine_config config, device_t device) { ((toaplan1_state)device).outzone(config); }
        static void toaplan1_state_init_outzone(device_t owner) { ((toaplan1_state)owner).init_outzone(); }
        static device_t device_creator_toaplan1_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new toaplan1_state(mconfig, (device_type)type, tag); }


        void construct_ioport_outzone(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("DSWA");
            PORT_DIPUNUSED_DIPLOC(0x01, IP_ACTIVE_HIGH, "SW1:!1");
            PORT_DIPNAME(0x02, 0x00, DEF_STR(Flip_Screen)); PORT_DIPLOCATION("SW1:!2");
            PORT_DIPSETTING(0x00, DEF_STR(Off));
            PORT_DIPSETTING(0x02, DEF_STR(On));
            PORT_SERVICE_DIPLOC(0x04, IP_ACTIVE_HIGH, "SW1:!3");
            PORT_DIPNAME(0x08, 0x00, DEF_STR(Demo_Sounds)); PORT_DIPLOCATION("SW1:!4");
            PORT_DIPSETTING(0x08, DEF_STR(Off));
            PORT_DIPSETTING(0x00, DEF_STR(On));
            PORT_DIPNAME(0x30, 0x00, DEF_STR(Coin_A)); PORT_DIPLOCATION("SW1:!5,!6");
            PORT_DIPSETTING(0x30, DEF_STR(_4C_1C));
            PORT_DIPSETTING(0x20, DEF_STR(_3C_1C));
            PORT_DIPSETTING(0x10, DEF_STR(_2C_1C));
            PORT_DIPSETTING(0x00, DEF_STR(_1C_1C));
            PORT_DIPNAME(0xc0, 0x00, DEF_STR(Coin_B)); PORT_DIPLOCATION("SW1:!7,!8");
            PORT_DIPSETTING(0x00, DEF_STR(_1C_1C));
            PORT_DIPSETTING(0x40, DEF_STR(_1C_3C));
            PORT_DIPSETTING(0x80, DEF_STR(_1C_4C));
            PORT_DIPSETTING(0xc0, DEF_STR(_1C_6C));

            PORT_START("DSWB");
            PORT_DIPNAME(0x03, 0x00, DEF_STR(Difficulty)); PORT_DIPLOCATION("SW2:!1,!2");
            PORT_DIPSETTING(0x01, DEF_STR(Easy));
            PORT_DIPSETTING(0x00, DEF_STR(Normal));
            PORT_DIPSETTING(0x02, DEF_STR(Hard));
            PORT_DIPSETTING(0x03, "Very Hard");
            PORT_DIPNAME(0x0c, 0x00, DEF_STR(Bonus_Life)); PORT_DIPLOCATION("SW2:!3,!4");
            PORT_DIPSETTING(0x00, "Every 300k");
            PORT_DIPSETTING(0x04, "200k and 500k");
            PORT_DIPSETTING(0x08, "300k Only");
            PORT_DIPSETTING(0x0c, DEF_STR(None));
            PORT_DIPNAME(0x30, 0x00, DEF_STR(Lives)); PORT_DIPLOCATION("SW2:!5,!6");
            PORT_DIPSETTING(0x30, "1");
            PORT_DIPSETTING(0x20, "2");
            PORT_DIPSETTING(0x00, "3");
            PORT_DIPSETTING(0x10, "5");
            PORT_DIPNAME(0x40, 0x00, "Invulnerability"); PORT_DIPLOCATION("SW2:!7");
            PORT_DIPSETTING(0x00, DEF_STR(Off));
            PORT_DIPSETTING(0x40, DEF_STR(On));
            PORT_DIPUNUSED_DIPLOC(0x80, IP_ACTIVE_HIGH, "SW2:!8");

            PORT_START("SYSTEM");
            PORT_BIT(0x01, IP_ACTIVE_HIGH, IPT_SERVICE1);
            PORT_BIT(0x02, IP_ACTIVE_HIGH, IPT_TILT);
            PORT_BIT(0x04, IP_ACTIVE_HIGH, IPT_SERVICE);
            PORT_BIT(0x08, IP_ACTIVE_HIGH, IPT_COIN1);
            PORT_BIT(0x10, IP_ACTIVE_HIGH, IPT_COIN2);
            PORT_BIT(0x20, IP_ACTIVE_HIGH, IPT_START1);
            PORT_BIT(0x40, IP_ACTIVE_HIGH, IPT_START2);
            PORT_BIT(0x80, IP_ACTIVE_HIGH, IPT_UNUSED);

            PORT_START("P1");
            PORT_BIT(0x01, IP_ACTIVE_HIGH, IPT_JOYSTICK_UP); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_HIGH, IPT_JOYSTICK_DOWN); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_HIGH, IPT_JOYSTICK_LEFT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_HIGH, IPT_JOYSTICK_RIGHT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_HIGH, IPT_BUTTON1); PORT_PLAYER(1);
            PORT_BIT(0x20, IP_ACTIVE_HIGH, IPT_BUTTON2); PORT_PLAYER(1);
            PORT_BIT(0x40, IP_ACTIVE_HIGH, IPT_BUTTON3); PORT_PLAYER(1);
            PORT_BIT(0x80, IP_ACTIVE_HIGH, IPT_UNUSED);

            PORT_START("P2");
            PORT_BIT(0x01, IP_ACTIVE_HIGH, IPT_JOYSTICK_UP); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_HIGH, IPT_JOYSTICK_DOWN); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_HIGH, IPT_JOYSTICK_LEFT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_HIGH, IPT_JOYSTICK_RIGHT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_HIGH, IPT_BUTTON1); PORT_PLAYER(2);
            PORT_BIT(0x20, IP_ACTIVE_HIGH, IPT_BUTTON2); PORT_PLAYER(2);
            PORT_BIT(0x40, IP_ACTIVE_HIGH, IPT_BUTTON3); PORT_PLAYER(2);
            PORT_BIT(0x80, IP_ACTIVE_HIGH, IPT_UNUSED);

            PORT_START("TJUMP");
            PORT_DIPNAME(0x0f, 0x02, "Region"); PORT_DIPLOCATION("JMPR:!1,!2,!3,!4");
            PORT_DIPSETTING(0x00, "Japan");
            PORT_DIPSETTING(0x01, "USA");
            PORT_DIPSETTING(0x02, "Europe");
            PORT_DIPSETTING(0x03, "Hong Kong");
            PORT_DIPSETTING(0x04, "Korea");
            PORT_DIPSETTING(0x05, "Taiwan");
            PORT_DIPSETTING(0x06, "Taiwan (Spacy Co., Ltd.)");
            PORT_DIPSETTING(0x07, "USA (Romstar, Inc.)");
            PORT_DIPSETTING(0x08, "Hong Kong & China (Honest Trading Co.)");
            PORT_BIT(0xf0, IP_ACTIVE_HIGH, IPT_UNKNOWN);
        }


        public static readonly game_driver driver_outzone = GAME(device_creator_toaplan1_state, rom_outzone, "1990", "outzone", "0", toaplan1_state_outzone, m_toaplan1.construct_ioport_outzone, toaplan1_state_init_outzone, ROT270, "Toaplan", "Out Zone", MACHINE_IS_SKELETON);
    }
}
