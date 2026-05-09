// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Skeleton Neo Geo registration for Euther Drive.  Hardware emulation still
// needs the 68000/Z80/YM2610/video devices ported from BSD-3-Clause sources.

using System;
using System.Collections.Generic;
using System.Linq;

using device_type = mame.emu.detail.device_type_impl_base;
using MemoryU8 = mame.MemoryContainer<System.Byte>;
using offs_t = System.UInt32;
using PointerU8 = mame.Pointer<System.Byte>;
using required_memory_bank = mame.memory_bank_finder<mame.bool_const_true>;
using s32 = System.Int32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using u64 = System.UInt64;
using size_t = System.UInt64;

using static mame.diexec_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.emupal_global;
using static mame.gamedrv_global;
using static mame.gen_latch_global;
using static mame.hash_global;
using static mame.input_merger_global;
using static mame.ioport_global;
using static mame.ioport_ioport_type_helper;
using static mame.m68000_global;
using static mame.romentry_global;
using static mame.screen_global;
using static mame.disound_global;
using static mame.speaker_global;
using static mame.ym2610_global;
using static mame.z80_global;


namespace mame
{
    class neogeo_state : driver_device
    {
        static readonly XTAL NEOGEO_MAIN_CLOCK = new XTAL(24_000_000) / 2;
        static readonly XTAL NEOGEO_AUDIO_CLOCK = new XTAL(24_000_000) / 6;
        static readonly XTAL NEOGEO_YM2610_CLOCK = new XTAL(24_000_000) / 3;
        const u64 NEOGEO_MAIN_CLOCK_HZ = 12_000_000;
        const u32 NEOGEO_PIXEL_CLOCK = 24_000_000 / 4;
        const int NEOGEO_HTOTAL = 0x180;
        const int NEOGEO_HBEND = 0x01c;
        const int NEOGEO_HBSTART = 0x15c;
        const int NEOGEO_VTOTAL = 0x108;
        const int NEOGEO_VBEND = 0x010;
        const int NEOGEO_VBSTART = 0x0f0;
        const int NEOGEO_VISIBLE_WIDTH = 320;
        const int NEOGEO_VISIBLE_HEIGHT = 224;
        const int NEOGEO_VISIBLE_TOP = NEOGEO_VBEND;
        const int NEOGEO_VISIBLE_BOTTOM = NEOGEO_VISIBLE_TOP + NEOGEO_VISIBLE_HEIGHT - 1;
        const int MAX_SPRITES_PER_SCREEN = 381;
        const int MAX_SPRITES_PER_LINE = 96;
        const u8 IRQ2CTRL_ENABLE = 0x10;
        static readonly u16 [] zoom_x_tables =
        {
            0x0080, 0x0880, 0x0888, 0x2888,
            0x288a, 0x2a8a, 0x2aaa, 0xaaaa,
            0xaaea, 0xbaea, 0xbaeb, 0xbbeb,
            0xbbef, 0xfbef, 0xfbff, 0xffff
        };

        readonly required_device<m68000_device> m_maincpu;
        readonly required_device<z80_device> m_audiocpu;
        readonly required_device<ym2610_device> m_ym;
        readonly required_device<palette_device> m_palette;
        readonly required_device<generic_latch_8_device> m_soundlatch;
        readonly required_device<generic_latch_8_device> m_soundlatch2;
        readonly required_device<input_merger_device> m_audionmi;
        readonly required_memory_bank m_audio8000;
        readonly required_memory_bank m_audioc000;
        readonly required_memory_bank m_audioe000;
        readonly required_memory_bank m_audiof000;
        readonly int [] m_audio_bank_entries = new int[4];
        readonly u16 [] m_videoram = new u16[0x8800];
        readonly u16 [] m_paletteram = new u16[0x2000];
        const u8 RTC_MODE_SHIFT = 0x01;
        const u8 RTC_MODE_TIME_SET = 0x02;
        const u8 RTC_MODE_TIME_READ = 0x03;
        const u8 RTC_MODE_TP_64HZ = 0x04;
        const u8 RTC_MODE_TP_256HZ = 0x05;
        const u8 RTC_MODE_TP_2048HZ = 0x06;
        const u8 RTC_MODE_TP_4096HZ = 0x07;
        const u8 RTC_MODE_TP_1S_INT = 0x08;
        const u8 RTC_MODE_TP_10S_INT = 0x09;
        const u8 RTC_MODE_TP_30S_INT = 0x0a;
        const u8 RTC_MODE_TP_60S_INT = 0x0b;
        const u8 RTC_MODE_INT_RESET_OUTPUT = 0x0c;
        const u8 RTC_MODE_INT_RUN_CLOCK = 0x0d;
        const u8 RTC_MODE_INT_STOP_CLOCK = 0x0e;
        const u8 RTC_MODE_TEST = 0x0f;
        u16 m_vram_offset;
        u16 m_vram_modulo;
        u16 m_vram_read_buffer;
        u32 m_display_counter;
        u32 m_sprite_gfx_address_mask;
        MemoryU8 m_sprite_gfx8;
        u32 m_bank_base;
        u32 m_palette_bank;
        u8 m_system_latch;
        u8 m_screen_shadow;
        u8 m_auto_animation_speed;
        u8 m_auto_animation_disabled;
        u8 m_auto_animation_counter;
        u8 m_auto_animation_frame_counter;
        u8 m_display_position_interrupt_control;
        u8 m_vblank_interrupt_pending;
        u8 m_display_position_interrupt_pending;
        u8 m_irq3_pending;
        u8 m_fixed_layer_source;
        u8 m_use_cart_vectors;
        u8 m_rtc_data_in;
        u8 m_rtc_clk;
        u8 m_rtc_stb;
        u8 m_rtc_data_out;
        u8 m_rtc_serial_command;
        u8 m_rtc_command_bits;
        u8 m_rtc_mode;
        u8 m_rtc_tp_state;
        u32 m_rtc_tp_counter;
        u32 m_rtc_tp_anchor_frame;
        u32 m_rtc_tp_frame_half_period;
        u64 m_rtc_tp_anchor_cycles;
        u64 m_rtc_tp_half_period_cycles;
        u64 m_rtc_shift;
        int m_rtc_shift_bits;
        int m_rtc_shift_pos;
        bool m_trace_neogeo;
        bool m_trace_neogeo_video;
        bool m_trace_neogeo_input;
        bool m_trace_neogeo_audio;
        bool m_direct_cart_boot;
        bool m_use_sprite_line_timer;
        emu_timer m_sprite_line_timer;
        u32 m_frame_counter;
        u32 m_last_video_trace_frame;
        u32 m_sprite_line_count;
        u32 m_sprite_pen_count;
        u32 m_sprite_plot_count;
        u32 m_sprite_visible_skip_count;
        u32 m_sprite_zoomy_missing_count;
        u32 m_vram_write_count;
        u32 m_vram_fixed_write_count;
        u32 m_vram_sprite_write_count;
        u32 m_palette_write_count;
        u32 m_maincpu_rom_read_count;
        u32 m_banked_rom_read_count;
        u32 m_banked_vector_read_count;
        u32 m_io_control_write_count;
        u32 m_audio_command_write_count;
        u32 m_audio_result_read_count;
        u8 m_audio_nmi_enabled;
        u32 m_audio_latch_read_count;
        u32 m_audio_reply_write_count;
        u32 m_audio_bank_select_count;


        public neogeo_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
            m_maincpu = new required_device<m68000_device>(this, "maincpu");
            m_audiocpu = new required_device<z80_device>(this, "audiocpu");
            m_ym = new required_device<ym2610_device>(this, "ymsnd");
            m_palette = new required_device<palette_device>(this, "palette");
            m_soundlatch = new required_device<generic_latch_8_device>(this, "soundlatch");
            m_soundlatch2 = new required_device<generic_latch_8_device>(this, "soundlatch2");
            m_audionmi = new required_device<input_merger_device>(this, "audionmi");
            m_audio8000 = new required_memory_bank(this, "audio_8000");
            m_audioc000 = new required_memory_bank(this, "audio_c000");
            m_audioe000 = new required_memory_bank(this, "audio_e000");
            m_audiof000 = new required_memory_bank(this, "audio_f000");
        }


        void main_map(address_map map, device_t device)
        {
            map.op(0x000000, 0x00007f).r((read16_delegate)banked_vectors_r);
            map.op(0x000080, 0x0fffff).r((read16_delegate)maincpu_rom_r);
            map.op(0x100000, 0x10ffff).mirror(0x0f0000).ram();
            map.op(0x200000, 0x2fffff).r((read16_delegate)banked_cart_rom_r);
            map.op(0x2ffff0, 0x2fffff).w((write16_delegate)write_banksel);
            map.op(0x300000, 0x300001).mirror(0x01ff7e).r((read16_delegate)p1_input_r);
            map.op(0x300080, 0x300081).mirror(0x01ff7e).r((read16_delegate)test_r);
            map.op(0x320000, 0x320001).mirror(0x01fffe).r((read16_delegate)audio_coin_r);
            map.op(0x320000, 0x320001).mirror(0x01fffe).w((write16_delegate)audio_command_word_w);
            map.op(0x340000, 0x340001).mirror(0x01fffe).r((read16_delegate)p2_input_r);
            map.op(0x380000, 0x380001).mirror(0x01fffe).r((read16_delegate)system_r);
            map.op(0x380000, 0x3800ff).mirror(0x01ff00).w((write16_delegate)io_control_w);
            map.op(0x3a0000, 0x3a001f).mirror(0x01ffe0).w((write16_delegate)system_latch_w);
            map.op(0x3c0000, 0x3c0007).mirror(0x01fff8).r((read16_delegate)video_register_r);
            map.op(0x3c0000, 0x3c000f).mirror(0x01fff0).w((write16_delegate)video_register_w);
            map.op(0x400000, 0x401fff).mirror(0x3fe000).rw((read16_delegate)paletteram_r, (write16_delegate)paletteram_w);
            map.op(0xc00000, 0xc1ffff).mirror(0x0e0000).r((read16_delegate)mainbios_rom_r);
            map.op(0xd00000, 0xd0ffff).mirror(0x0f0000).ram();
        }


        void audio_map(address_map map, device_t device)
        {
            map.op(0x0000, 0x7fff).rom().region("audiocpu", 0);
            map.op(0x8000, 0xbfff).bankr(m_audio8000);
            map.op(0xc000, 0xdfff).bankr(m_audioc000);
            map.op(0xe000, 0xefff).bankr(m_audioe000);
            map.op(0xf000, 0xf7ff).bankr(m_audiof000);
            map.op(0xf800, 0xffff).ram();
        }


        void audio_io_map(address_map map, device_t device)
        {
            map.op(0x0000, 0xffff).rw((read8sm_delegate)audio_io_r, (write8sm_delegate)audio_io_w);
        }


        void audio_command_word_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            u8 command = (u8)(((mem_mask & 0xff00) != 0) ? (data >> 8) : (data & 0xff));
            if (m_trace_neogeo_audio && m_audio_command_write_count < 256)
                Console.Error.WriteLine($"[NEOGEO] audio_command_word offset=0x{offset:x} data=0x{data:x4} mask=0x{mem_mask:x4} command=0x{command:x2}");
            audio_command_w(command);
        }

        void audio_command_w(u8 data)
        {
            m_audio_command_write_count++;
            if (m_trace_neogeo_audio)
                Console.Error.WriteLine($"[NEOGEO] audio_command data=0x{data:x2}");
            m_soundlatch.op0.write(data);
            machine().scheduler().perfect_quantum(attotime.from_usec(50));
        }


        u8 audio_io_r(offs_t offset)
        {
            u8 port = (u8)(offset & 0xff);

            if (port == 0x00)
            {
                m_audio_latch_read_count++;
                u8 data = m_soundlatch.op0.read();
                if (m_trace_neogeo_audio && m_audio_latch_read_count <= 256)
                    Console.Error.WriteLine($"[NEOGEO] audio_latch_r port=0x{offset:x4} data=0x{data:x2}");
                return data;
            }

            if (port >= 0x04 && port <= 0x07)
                return m_ym.op0.read((offs_t)(port - 0x04));

            if ((port & 0x0f) >= 0x08 && (port & 0x0f) <= 0x0b)
            {
                offs_t bankOffset = (offs_t)((port - 0x08) & 0x03);
                audio_bank_select_w(bankOffset, (u8)((offset >> 8) & 0xff));
                return 0;
            }

            return 0xff;
        }


        void audio_io_w(offs_t offset, u8 data)
        {
            u8 port = (u8)(offset & 0xff);

            if (port == 0x00)
            {
                m_soundlatch.op0.read();
                return;
            }

            if (port >= 0x04 && port <= 0x07)
            {
                m_ym.op0.write((offs_t)(port - 0x04), data);
                return;
            }

            if ((port & 0xef) == 0x08)
            {
                m_audio_nmi_enabled = (u8)(((port & 0x10) == 0) ? 1 : 0);
                if (m_trace_neogeo_audio && m_audio_latch_read_count <= 256)
                    Console.Error.WriteLine($"[NEOGEO] audio_nmi {(m_audio_nmi_enabled != 0 ? "enable" : "disable")} port=0x{offset:x4}");
                m_audionmi.op0.in_w<u32_const_1>(m_audio_nmi_enabled);
                return;
            }

            if (port == 0x0c)
            {
                m_audio_reply_write_count++;
                if (m_trace_neogeo_audio && m_audio_reply_write_count <= 256)
                    Console.Error.WriteLine($"[NEOGEO] audio_reply data=0x{data:x2}");
                m_soundlatch2.op0.write(data);
                return;
            }
        }


        u16 banked_vectors_r(address_space space, offs_t offset, u16 mem_mask)
        {
            m_banked_vector_read_count++;
            memory_region region = memregion(m_use_cart_vectors != 0 ? "maincpu" : "mainbios");
            return read_region_word_be(region, (int)(offset << 1));
        }


        u16 maincpu_rom_r(address_space space, offs_t offset, u16 mem_mask)
        {
            m_maincpu_rom_read_count++;
            return read_region_word_be(memregion("maincpu"), 0x80 + (int)(offset << 1));
        }


        u16 mainbios_rom_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return read_region_word_be(memregion("mainbios"), (int)(offset << 1));
        }


        u16 banked_cart_rom_r(address_space space, offs_t offset, u16 mem_mask)
        {
            m_banked_rom_read_count++;
            return read_region_word_be(memregion("maincpu"), (int)(m_bank_base + (offset << 1)));
        }


        void write_banksel(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            memory_region region = memregion("maincpu");
            u32 length = region != null ? region.bytes() : 0;
            int bank = (mem_mask & 0xff00) != 0 && (mem_mask & 0x00ff) == 0 ? (data >> 8) & 0x07 : data & 0x07;
            u32 nextBase = (u32)((bank + 1) * 0x100000);
            if (length <= 0x100000 || nextBase >= length)
                nextBase = length > 0x100000 ? 0x100000U : 0U;

            m_bank_base = nextBase;
            if (m_trace_neogeo)
                Console.Error.WriteLine($"[NEOGEO] banksel data=0x{data:x4} mask=0x{mem_mask:x4} bank={bank} base=0x{m_bank_base:x}");
        }


        static u16 read_region_word_be(memory_region region, int byteOffset)
        {
            if (region == null || region.base_() == null)
                return 0xffff;

            if (byteOffset < 0 || byteOffset + 1 >= region.bytes())
                return 0xffff;

            MemoryU8 data = region.base_();
            return (u16)((data[byteOffset] << 8) | data[byteOffset + 1]);
        }


        u16 p1_input_r(address_space space, offs_t offset, u16 mem_mask)
        {
            u16 result = (u16)(((ioport("P1").read() & 0x00ff) << 8) | 0x00ff);
            if (m_trace_neogeo_input && (result & 0xff00) != 0xff00)
                Console.Error.WriteLine($"[NEOGEO-INPUT] P1=0x{(result >> 8) & 0x00ff:x2}");
            return result;
        }

        u16 p2_input_r(address_space space, offs_t offset, u16 mem_mask)
            => (u16)(((ioport("P2").read() & 0x00ff) << 8) | 0x00ff);

        u16 test_r(address_space space, offs_t offset, u16 mem_mask) => 0xffbf;

        u16 audio_coin_r(address_space space, offs_t offset, u16 mem_mask)
        {
            m_audio_result_read_count++;
            u8 result = m_soundlatch2.op0.read();
            if (m_trace_neogeo && m_audio_result_read_count <= 16)
                Console.Error.WriteLine($"[NEOGEO] audio_coin result=0x{result:x2}");
            u16 rtc = (u16)((rtc_tp_r() != 0 ? 0x0040 : 0x0000) | (m_rtc_data_out != 0 ? 0x0080 : 0x0000));
            u16 input = (u16)ioport("AUDIO_COIN").read();
            if (m_trace_neogeo_input && (input & 0x001f) != 0x001f)
                Console.Error.WriteLine($"[NEOGEO-INPUT] AUDIO_COIN=0x{input & 0x001f:x2}");
            return (u16)((result << 8) | (input & 0x001f) | rtc);
        }

        u16 system_r(address_space space, offs_t offset, u16 mem_mask)
        {
            u16 input = (u16)ioport("SYSTEM").read();
            if (m_trace_neogeo_input && (input & 0x0f00) != 0)
                Console.Error.WriteLine($"[NEOGEO-INPUT] SYSTEM=0x{input & 0x0f00:x4}");
            return (u16)(0xfaff | (input & 0x0f00));
        }


        void io_control_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0x00ff) == 0)
                return;

            m_io_control_write_count++;
            if (m_trace_neogeo)
            {
                Console.Error.WriteLine(
                    $"[NEOGEO] io_control offset=0x{offset:x2} group=0x{offset & 0x78:x2} data=0x{data & 0x00ff:x2} mask=0x{mem_mask:x4}");
            }

            if ((offset & 0x78) == 0x28)
                rtc_control_w((u8)(data & 0x07));
        }


        void rtc_control_w(u8 data)
        {
            u8 nextData = (u8)(data & 0x01);
            u8 nextClk = (u8)((data >> 1) & 0x01);
            u8 nextStb = (u8)((data >> 2) & 0x01);

            if (m_rtc_clk == 0 && nextClk != 0)
            {
                u8 serialIn = (u8)(m_rtc_serial_command & 1);
                m_rtc_serial_command = (u8)((m_rtc_serial_command >> 1) | (nextData << 3));
                if (m_rtc_command_bits < 4)
                    m_rtc_command_bits++;

                if (m_rtc_mode == RTC_MODE_SHIFT)
                {
                    m_rtc_shift = (m_rtc_shift >> 1) | ((u64)serialIn << 47);
                    update_rtc_data_out();
                }
            }

            if (m_rtc_stb == 0 && nextStb != 0)
                latch_rtc_command();

            m_rtc_data_in = nextData;
            m_rtc_clk = nextClk;
            m_rtc_stb = nextStb;
        }


        void latch_rtc_command()
        {
            m_rtc_mode = (u8)(m_rtc_serial_command & 0x0f);
            m_rtc_command_bits = 0;

            switch (m_rtc_mode)
            {
            case RTC_MODE_SHIFT:
                update_rtc_data_out();
                break;
            case RTC_MODE_TIME_SET:
                update_rtc_data_out();
                break;
            case RTC_MODE_TIME_READ:
                load_rtc_shift_register();
                break;
            case RTC_MODE_TP_64HZ:
            case RTC_MODE_TP_256HZ:
            case RTC_MODE_TP_2048HZ:
            case RTC_MODE_TP_4096HZ:
            case RTC_MODE_TP_1S_INT:
            case RTC_MODE_TP_10S_INT:
            case RTC_MODE_TP_30S_INT:
            case RTC_MODE_TP_60S_INT:
                start_rtc_tp_mode(m_rtc_mode);
                break;
            case RTC_MODE_INT_RESET_OUTPUT:
                m_rtc_data_out = 0;
                break;
            }

            if (m_trace_neogeo)
                Console.Error.WriteLine($"[NEOGEO] rtc command=0x{m_rtc_mode:x1} shift=0x{m_rtc_shift:x12} dout={m_rtc_data_out}");
        }


        void start_rtc_tp_mode(u8 mode)
        {
            m_rtc_tp_counter = 0;
            m_rtc_tp_anchor_frame = m_frame_counter;
            m_rtc_tp_frame_half_period = 0;
            m_rtc_tp_anchor_cycles = m_maincpu.op0.total_cycles();

            switch (mode)
            {
            case RTC_MODE_TP_64HZ:
                m_rtc_tp_half_period_cycles = rtc_cycles_for_32768_divisor(512);
                break;
            case RTC_MODE_TP_256HZ:
                m_rtc_tp_half_period_cycles = rtc_cycles_for_32768_divisor(128);
                break;
            case RTC_MODE_TP_2048HZ:
                m_rtc_tp_half_period_cycles = rtc_cycles_for_32768_divisor(16);
                break;
            case RTC_MODE_TP_4096HZ:
                m_rtc_tp_half_period_cycles = rtc_cycles_for_32768_divisor(8);
                break;
            case RTC_MODE_TP_1S_INT:
                m_rtc_tp_state = 1;
                m_rtc_tp_half_period_cycles = 0;
                m_rtc_tp_frame_half_period = 30;
                break;
            case RTC_MODE_TP_10S_INT:
                m_rtc_tp_state = 1;
                m_rtc_tp_half_period_cycles = 0;
                m_rtc_tp_frame_half_period = 300;
                break;
            case RTC_MODE_TP_30S_INT:
                m_rtc_tp_state = 1;
                m_rtc_tp_half_period_cycles = 0;
                m_rtc_tp_frame_half_period = 900;
                break;
            case RTC_MODE_TP_60S_INT:
                m_rtc_tp_state = 1;
                m_rtc_tp_half_period_cycles = 0;
                m_rtc_tp_frame_half_period = 1800;
                break;
            default:
                m_rtc_tp_half_period_cycles = rtc_cycles_for_32768_divisor(512);
                break;
            }
        }


        u8 rtc_tp_r()
        {
            m_rtc_tp_counter++;
            if (m_rtc_tp_frame_half_period != 0)
            {
                u32 elapsedFrames = m_frame_counter - m_rtc_tp_anchor_frame;
                u32 framePhase = elapsedFrames / m_rtc_tp_frame_half_period;
                return (u8)(m_rtc_tp_state ^ (u8)(framePhase & 1));
            }

            if (m_rtc_tp_half_period_cycles == 0)
                return m_rtc_tp_state;

            u64 elapsed = m_maincpu.op0.total_cycles() - m_rtc_tp_anchor_cycles;
            u64 phase = elapsed / m_rtc_tp_half_period_cycles;
            return (u8)(m_rtc_tp_state ^ (u8)(phase & 1));
        }


        static u64 rtc_cycles_for_32768_divisor(u64 divisor)
        {
            return Math.Max(1, (NEOGEO_MAIN_CLOCK_HZ * divisor) / (32768 * 2));
        }


        void load_rtc_shift_register()
        {
            DateTime now = DateTime.Now;
            u8 dayOfWeek = (u8)now.DayOfWeek;
            m_rtc_shift =
                (u64)bcd(now.Second) |
                ((u64)bcd(now.Minute) << 8) |
                ((u64)bcd(now.Hour) << 16) |
                ((u64)bcd(now.Day) << 24) |
                ((u64)((now.Month << 4) | dayOfWeek) << 32) |
                ((u64)bcd(now.Year % 100) << 40);
            update_rtc_data_out();
        }


        void update_rtc_data_out()
        {
            m_rtc_data_out = (u8)(m_rtc_shift & 1);
        }


        static u8 bcd(int value)
        {
            return (u8)(((value / 10) << 4) | (value % 10));
        }


        void audio_bank_select_w(offs_t offset, u8 data)
        {
            int bank = (int)(offset & 0x03);
            int entries = Math.Max(1, m_audio_bank_entries[bank]);
            int entry = data % entries;
            m_audio_bank_select_count++;

            if (m_trace_neogeo_audio && m_audio_bank_select_count <= 256)
                Console.Error.WriteLine($"[NEOGEO] audio_bank port={bank} entry={entry} raw=0x{data:x2}");

            switch (bank)
            {
            case 0:
                m_audiof000.op0.set_entry(entry);
                break;
            case 1:
                m_audioe000.op0.set_entry(entry);
                break;
            case 2:
                m_audioc000.op0.set_entry(entry);
                break;
            case 3:
                m_audio8000.op0.set_entry(entry);
                break;
            }
        }


        u16 video_register_r(address_space space, offs_t offset, u16 mem_mask)
        {
            switch (offset & 0x03)
            {
            case 0:
            case 1:
                return get_videoram_data();
            case 2:
                return m_vram_modulo;
            case 3:
                return get_video_control();
            default:
                return 0xffff;
            }
        }


        void video_register_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (mem_mask == 0x00ff)
                return;
            if (mem_mask == 0xff00)
                data = (u16)((data & 0xff00) | (data >> 8));

            switch (offset & 0x07)
            {
            case 0:
                set_videoram_offset(data);
                break;
            case 1:
                set_videoram_data(data);
                break;
            case 2:
                m_vram_modulo = data;
                break;
            case 3:
                set_video_control(data);
                break;
            case 4:
                m_display_counter = (m_display_counter & 0x0000ffff) | ((u32)data << 16);
                break;
            case 5:
                m_display_counter = (m_display_counter & 0xffff0000) | data;
                break;
            case 6:
                acknowledge_interrupt(data);
                break;
            }
        }


        void set_video_control(u16 data)
        {
            m_auto_animation_speed = (u8)(data >> 8);
            m_auto_animation_disabled = (u8)((data & 0x0008) != 0 ? 1 : 0);
            m_display_position_interrupt_control = (u8)(data & 0x00f0);
        }


        void vblank_interrupt(device_t device)
        {
            m_frame_counter++;
            advance_auto_animation();
            service_display_position_interrupt();
            m_vblank_interrupt_pending = 1;
            update_interrupts();
        }


        void acknowledge_interrupt(u16 data)
        {
            if ((data & 0x0001) != 0)
                m_irq3_pending = 0;
            if ((data & 0x0002) != 0)
                m_display_position_interrupt_pending = 0;
            if ((data & 0x0004) != 0)
                m_vblank_interrupt_pending = 0;

            update_interrupts();
        }


        void update_interrupts()
        {
            m_maincpu.op0.set_input_line(3, m_irq3_pending != 0 ? ASSERT_LINE : CLEAR_LINE);
            m_maincpu.op0.set_input_line(2, m_display_position_interrupt_pending != 0 ? ASSERT_LINE : CLEAR_LINE);
            m_maincpu.op0.set_input_line(1, m_vblank_interrupt_pending != 0 ? ASSERT_LINE : CLEAR_LINE);
        }


        void system_latch_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0x00ff) == 0)
                return;

            int bit = (int)(offset & 0x07);
            int state = ((offset & 0x08) != 0) ? 1 : 0;
            u8 oldLatch = m_system_latch;

            if (state != 0)
                m_system_latch |= (u8)(1 << bit);
            else
                m_system_latch &= (u8)~(1 << bit);

            if (bit == 0 || bit == 5 || bit == 7)
                update_screen_before_video_mutation();

            switch (bit)
            {
            case 0:
                m_screen_shadow = (u8)state;
                break;
            case 1:
                m_use_cart_vectors = (u8)state;
                break;
            case 5:
                m_fixed_layer_source = (u8)state;
                break;
            case 7:
                m_palette_bank = state != 0 ? 0x1000U : 0U;
                break;
            }

            if (m_trace_neogeo && oldLatch != m_system_latch)
            {
                Console.Error.WriteLine(
                    $"[NEOGEO] latch bit={bit} state={state} latch=0x{m_system_latch:x2} cartvec={m_use_cart_vectors} fixsrc={m_fixed_layer_source} palbank=0x{m_palette_bank:x}");
            }
        }


        u16 paletteram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_paletteram[m_palette_bank + (offset & 0x0fff)];
        }


        void paletteram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            update_screen_before_video_mutation();
            int index = (int)(m_palette_bank + (offset & 0x0fff));
            u16 old = m_paletteram[index];
            u16 updated = (u16)((old & ~mem_mask) | (data & mem_mask));
            m_paletteram[index] = updated;
            set_palette_color(index, updated);
            m_palette_write_count++;
        }


        void set_videoram_offset(u16 data)
        {
            m_vram_offset = normalize_videoram_offset(data);
            m_vram_read_buffer = m_videoram[videoram_index(m_vram_offset)];
        }


        u16 get_videoram_data()
        {
            return m_vram_read_buffer;
        }


        void set_videoram_data(u16 data)
        {
            update_screen_before_video_mutation();
            u16 offset = m_vram_offset;
            m_videoram[offset] = data;
            m_vram_write_count++;
            if ((offset & 0x7000) == 0x7000)
                m_vram_fixed_write_count++;
            if ((offset & 0x8000) != 0)
                m_vram_sprite_write_count++;
            set_videoram_offset((u16)((m_vram_offset & 0x8000) | ((m_vram_offset + m_vram_modulo) & 0x7fff)));
        }


        static u16 normalize_videoram_offset(u16 data)
        {
            return (u16)((data & 0x8000) != 0 ? 0x8000 | (data & 0x07ff) : data & 0x7fff);
        }


        static int videoram_index(u16 offset)
        {
            return normalize_videoram_offset(offset);
        }


        u16 get_video_control()
        {
            int vcounter = 0x100;
            screen_device screen = subdevice<screen_device>("screen");
            if (screen != null)
                vcounter += screen.vpos();
            if (vcounter >= 0x200)
                vcounter -= 264;
            return (u16)(((vcounter << 7) & 0xff80) | (m_auto_animation_counter & 0x07));
        }


        void update_screen_before_video_mutation()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_PARTIAL_UPDATES")))
                return;

            screen_device screen = subdevice<screen_device>("screen");
            if (screen == null)
                return;

            int scanline = screen.vpos() - 1;
            if (scanline >= 0)
                screen.update_partial(scanline);
        }


        void start_sprite_line_timer()
        {
            screen_device screen = subdevice<screen_device>("screen");
            if (screen == null)
                return;

            m_sprite_line_timer = timer_alloc(sprite_line_timer_callback);
            m_sprite_line_timer.adjust(screen.time_until_pos(0), 0);
        }


        void sprite_line_timer_callback(s32 scanline)
        {
            screen_device screen = subdevice<screen_device>("screen");
            if (screen == null)
                return;

            if (scanline != 0)
                screen.update_partial(scanline - 1);

            parse_sprites(scanline);

            int nextScanline = (scanline + 1) % NEOGEO_VTOTAL;
            m_sprite_line_timer.adjust(screen.time_until_pos(nextScanline), nextScanline);
        }


        public void neogeo_skeleton(machine_config config)
        {
            M68000(config, m_maincpu, NEOGEO_MAIN_CLOCK);
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, main_map);
            m_maincpu.op0.execute().set_vblank_int("screen", vblank_interrupt);

            Z80(config, m_audiocpu, NEOGEO_AUDIO_CLOCK);
            m_audiocpu.op0.memory().set_addrmap(AS_PROGRAM, audio_map);
            m_audiocpu.op0.memory().set_addrmap(AS_IO, audio_io_map);

            INPUT_MERGER_ALL_HIGH(config, m_audionmi);
            m_audionmi.op0.output_handler().set_inputline(m_audiocpu, INPUT_LINE_NMI).reg();

            GENERIC_LATCH_8(config, m_soundlatch);
            m_soundlatch.op0.data_pending_callback().set(m_audionmi, (int state) => { m_audionmi.op0.in_w<u32_const_0>(state); }).reg();
            GENERIC_LATCH_8(config, m_soundlatch2);

            YM2610(config, m_ym, NEOGEO_YM2610_CLOCK);
            m_ym.op0.irq_handler().set_inputline(m_audiocpu, 0).reg();

            SPEAKER(config, "lspeaker").front_left();
            SPEAKER(config, "rspeaker").front_right();
            m_ym.op0.disound.add_route(0, "lspeaker", 0.84);
            m_ym.op0.disound.add_route(0, "rspeaker", 0.84);
            m_ym.op0.disound.add_route(1, "lspeaker", 0.98);
            m_ym.op0.disound.add_route(2, "rspeaker", 0.98);

            screen_device screen = SCREEN(config, "screen", SCREEN_TYPE_RASTER);
            screen.set_screen_update(screen_update);
            screen.set_palette(m_palette);
            screen.set_refresh_hz(59.185606);
            screen.set_size(NEOGEO_VISIBLE_WIDTH, NEOGEO_VTOTAL);
            screen.set_visarea(0, NEOGEO_VISIBLE_WIDTH - 1, NEOGEO_VISIBLE_TOP, NEOGEO_VISIBLE_BOTTOM);

            PALETTE(config, m_palette).set_entries(0x4000);
        }


        protected override void machine_start()
        {
            m_trace_neogeo = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_TRACE"));
            m_trace_neogeo_video = m_trace_neogeo || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_VIDEO_TRACE"));
            m_trace_neogeo_input = m_trace_neogeo || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_INPUT_TRACE"));
            m_trace_neogeo_audio = m_trace_neogeo || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_AUDIO_TRACE"));
            m_direct_cart_boot = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_DIRECT_CART_BOOT"));
            m_use_sprite_line_timer = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_LINE_TIMER"));
            configure_audio_banks();
            configure_sprite_region();
            normalize_maincpu_p_rom_layout();
            configure_maincpu_bank();
            initialize_default_palette();
            m_irq3_pending = 1;
            m_rtc_tp_state = 1;
            m_rtc_tp_half_period_cycles = rtc_cycles_for_32768_divisor(512);
            m_rtc_tp_anchor_cycles = 0;
            m_rtc_tp_anchor_frame = 0;
            m_rtc_tp_frame_half_period = 0;
            m_audio_nmi_enabled = 0;
            update_interrupts();
            if (m_trace_neogeo_video)
            {
                Console.Error.WriteLine(
                    $"[NEOGEO] start sprites_mask=0x{m_sprite_gfx_address_mask:x} maincpu={RegionBytes("maincpu")} mainbios={RegionBytes("mainbios")} fixed={RegionBytes("fixed")} fixedbios={RegionBytes("fixedbios")} sprites={RegionBytes("sprites")} zoomy={RegionBytes("spritegen:zoomy")}");
                foreach (KeyValuePair<string, memory_region> region in machine().memory().regions())
                    Console.Error.WriteLine($"[NEOGEO] region {region.Key} bytes={region.Value.bytes()}");
                Console.Error.WriteLine(
                    $"[NEOGEO] vectors maincpu={read_region_word_be(memregion("maincpu"), 0):x4} {read_region_word_be(memregion("maincpu"), 2):x4} {read_region_word_be(memregion("maincpu"), 4):x4} {read_region_word_be(memregion("maincpu"), 6):x4} mainbios={read_region_word_be(memregion("mainbios"), 0):x4} {read_region_word_be(memregion("mainbios"), 2):x4} {read_region_word_be(memregion("mainbios"), 4):x4} {read_region_word_be(memregion("mainbios"), 6):x4}");
            }
            if (m_use_sprite_line_timer)
                start_sprite_line_timer();
            save_item(NAME(new { m_videoram }));
            save_item(NAME(new { m_paletteram }));
            save_item(NAME(new { m_vram_offset }));
            save_item(NAME(new { m_vram_modulo }));
            save_item(NAME(new { m_vram_read_buffer }));
            save_item(NAME(new { m_display_counter }));
            save_item(NAME(new { m_sprite_gfx_address_mask }));
            save_item(NAME(new { m_bank_base }));
            save_item(NAME(new { m_palette_bank }));
            save_item(NAME(new { m_system_latch }));
            save_item(NAME(new { m_screen_shadow }));
            save_item(NAME(new { m_auto_animation_speed }));
            save_item(NAME(new { m_auto_animation_disabled }));
            save_item(NAME(new { m_auto_animation_counter }));
            save_item(NAME(new { m_auto_animation_frame_counter }));
            save_item(NAME(new { m_display_position_interrupt_control }));
            save_item(NAME(new { m_vblank_interrupt_pending }));
            save_item(NAME(new { m_display_position_interrupt_pending }));
            save_item(NAME(new { m_irq3_pending }));
            save_item(NAME(new { m_fixed_layer_source }));
            save_item(NAME(new { m_use_cart_vectors }));
            save_item(NAME(new { m_rtc_data_in }));
            save_item(NAME(new { m_rtc_clk }));
            save_item(NAME(new { m_rtc_stb }));
            save_item(NAME(new { m_rtc_data_out }));
            save_item(NAME(new { m_rtc_serial_command }));
            save_item(NAME(new { m_rtc_command_bits }));
            save_item(NAME(new { m_rtc_mode }));
            save_item(NAME(new { m_rtc_tp_state }));
            save_item(NAME(new { m_rtc_tp_counter }));
            save_item(NAME(new { m_rtc_tp_anchor_frame }));
            save_item(NAME(new { m_rtc_tp_frame_half_period }));
            save_item(NAME(new { m_rtc_tp_anchor_cycles }));
            save_item(NAME(new { m_rtc_tp_half_period_cycles }));
            save_item(NAME(new { m_rtc_shift }));
            save_item(NAME(new { m_rtc_shift_bits }));
            save_item(NAME(new { m_rtc_shift_pos }));
            save_item(NAME(new { m_frame_counter }));
            save_item(NAME(new { m_last_video_trace_frame }));
            save_item(NAME(new { m_sprite_line_count }));
            save_item(NAME(new { m_sprite_pen_count }));
            save_item(NAME(new { m_sprite_plot_count }));
            save_item(NAME(new { m_sprite_visible_skip_count }));
            save_item(NAME(new { m_sprite_zoomy_missing_count }));
            save_item(NAME(new { m_vram_write_count }));
            save_item(NAME(new { m_vram_fixed_write_count }));
            save_item(NAME(new { m_vram_sprite_write_count }));
            save_item(NAME(new { m_palette_write_count }));
            save_item(NAME(new { m_maincpu_rom_read_count }));
            save_item(NAME(new { m_banked_rom_read_count }));
            save_item(NAME(new { m_banked_vector_read_count }));
            save_item(NAME(new { m_io_control_write_count }));
            save_item(NAME(new { m_audio_command_write_count }));
            save_item(NAME(new { m_audio_result_read_count }));
            save_item(NAME(new { m_audio_nmi_enabled }));
            save_item(NAME(new { m_audio_latch_read_count }));
            save_item(NAME(new { m_audio_reply_write_count }));
            save_item(NAME(new { m_audio_bank_select_count }));
        }


        void normalize_maincpu_p_rom_layout()
        {
            memory_region region = memregion("maincpu");
            if (region == null || region.base_() == null || region.bytes() < 8)
                return;

            if (read_region_word_be(region, 0) != 0x1000 || read_region_word_be(region, 2) != 0x00f3)
                return;

            int length = (int)(region.bytes() & ~1U);
            MemoryU8 data = region.base_();
            for (int i = 0; i + 1 < length; i += 2)
            {
                u8 tmp = data[i];
                data[i] = data[i + 1];
                data[i + 1] = tmp;
            }

            if (m_trace_neogeo)
                Console.Error.WriteLine($"[NEOGEO] normalized maincpu P-ROM word order for 0x{length:x} bytes");
        }


        protected override void machine_reset()
        {
            m_audio_nmi_enabled = 0;
            m_soundlatch.op0.read();
            m_audionmi.op0.in_w<u32_const_1>(0);

            if (m_direct_cart_boot)
            {
                m_use_cart_vectors = 1;
                m_fixed_layer_source = 1;
                m_maincpu.op0.reset_from_bus();
                if (m_trace_neogeo)
                    Console.Error.WriteLine("[NEOGEO] direct cart boot enabled");
            }

            update_interrupts();
        }


        void configure_audio_banks()
        {
            memory_region audio = memregion("audiocpu");
            if (audio == null || audio.base_() == null)
                return;

            configure_audio_bank(m_audiof000.op0, 0, 0xf000, 0x0800);
            configure_audio_bank(m_audioe000.op0, 1, 0xe000, 0x1000);
            configure_audio_bank(m_audioc000.op0, 2, 0xc000, 0x2000);
            configure_audio_bank(m_audio8000.op0, 3, 0x8000, 0x4000);

            m_audiof000.op0.set_entry(Math.Min(0x1e, m_audio_bank_entries[0] - 1));
            m_audioe000.op0.set_entry(Math.Min(0x0e, m_audio_bank_entries[1] - 1));
            m_audioc000.op0.set_entry(Math.Min(0x06, m_audio_bank_entries[2] - 1));
            m_audio8000.op0.set_entry(Math.Min(0x02, m_audio_bank_entries[3] - 1));
        }


        void configure_audio_bank(memory_bank bank, int index, int preferredStart, int stride)
        {
            memory_region audio = memregion("audiocpu");
            int bytes = (int)audio.bytes();
            int entries = bytes > 0x10000 ? 256 : 1;
            u32 addressMask = bytes > 0x10000 ? (u32)((bytes - 0x10000 - 1) & 0x3ffff) : 0;

            for (int entry = 0; entry < entries; entry++)
            {
                u32 address = bytes > 0x10000
                    ? 0x10000U + (((u32)entry << (11 + index)) & addressMask)
                    : 0U;

                if (address + stride > bytes)
                    address = (u32)Math.Max(0, bytes - stride);

                bank.configure_entry(entry, new PointerU8(audio.base_(), (int)address));
            }

            bank.set_entry(0);
            m_audio_bank_entries[index] = entries;
        }


        void configure_sprite_region()
        {
            memory_region sprites = memregion("sprites");
            if (sprites == null || sprites.base_() == null || sprites.bytes() == 0)
            {
                m_sprite_gfx_address_mask = 0;
                m_sprite_gfx8 = null;
                return;
            }

            m_sprite_gfx_address_mask = get_region_mask((u32)sprites.bytes());
            optimize_sprite_data(sprites.base_(), (int)sprites.bytes());
        }


        void optimize_sprite_data(MemoryU8 source, int sourceBytes)
        {
            int optimizedBytes = checked((int)(m_sprite_gfx_address_mask + 1));
            m_sprite_gfx8 = new MemoryU8(optimizedBytes, true);

            int dest = 0;
            for (int sourceOffset = 0; sourceOffset < sourceBytes; sourceOffset += 0x80)
            {
                for (int y = 0; y < 0x10; y++)
                {
                    int row = sourceOffset + (y << 2);

                    for (int x = 0; x < 8; x++)
                    {
                        m_sprite_gfx8[dest++] = (u8)(
                            (((source[row + 0x43] >> x) & 0x01) << 3) |
                            (((source[row + 0x41] >> x) & 0x01) << 2) |
                            (((source[row + 0x42] >> x) & 0x01) << 1) |
                            (((source[row + 0x40] >> x) & 0x01) << 0));
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        m_sprite_gfx8[dest++] = (u8)(
                            (((source[row + 0x03] >> x) & 0x01) << 3) |
                            (((source[row + 0x01] >> x) & 0x01) << 2) |
                            (((source[row + 0x02] >> x) & 0x01) << 1) |
                            (((source[row + 0x00] >> x) & 0x01) << 0));
                    }
                }
            }
        }


        void configure_maincpu_bank()
        {
            memory_region maincpu = memregion("maincpu");
            if (maincpu != null && maincpu.bytes() > 0x100000)
                m_bank_base = 0x100000;
            else
                m_bank_base = 0;
        }


        int RegionBytes(string tag)
        {
            memory_region region = memregion(tag);
            return region != null ? (int)region.bytes() : 0;
        }


        static u32 get_region_mask(u32 regionSize)
        {
            u32 mask = 0xffffffff;
            u32 length = regionSize * 2;

            for (u32 bit = 0x80000000; bit != 0; bit >>= 1)
            {
                if (((length - 1) & bit) != 0)
                    break;

                mask >>= 1;
            }

            return mask;
        }


        u32 screen_update(screen_device screen, bitmap_ind16 bitmap, rectangle cliprect)
        {
            bitmap.fill((u16)(active_palette_base() + 0x0fff), cliprect);
            draw_sprites(bitmap, cliprect);
            draw_fixed_layer(bitmap, cliprect);
            if (m_trace_neogeo_video &&
                m_last_video_trace_frame != m_frame_counter &&
                (m_frame_counter == 1 || (m_frame_counter % 60) == 0))
            {
                m_last_video_trace_frame = m_frame_counter;
                CountVideoState(out int fixedTiles, out int activeSprites);
                Console.Error.WriteLine(
                    $"[NEOGEO] frame={m_frame_counter} pc=0x{m_maincpu.op0.Pc:x6} z80pc=0x{m_audiocpu.op0.DebugPc:x4} z80ins={m_audiocpu.op0.DebugInstructionCount} sr=0x{m_maincpu.op0.StatusRegister:x4} imask={m_maincpu.op0.InterruptPriorityMask} stop={(m_maincpu.op0.IsStopped ? 1 : 0)} latch=0x{m_system_latch:x2} cartvec={m_use_cart_vectors} fixsrc={m_fixed_layer_source} palbank=0x{m_palette_bank:x} bank=0x{m_bank_base:x} bg=0x{active_palette_base() + 0x0fff:x4} cartreads={m_maincpu_rom_read_count} bankreads={m_banked_rom_read_count} vecreads={m_banked_vector_read_count} iow={m_io_control_write_count} audcmd={m_audio_command_write_count} audrd={m_audio_result_read_count} audlatch={m_audio_latch_read_count} audreply={m_audio_reply_write_count} audbank={m_audio_bank_select_count} vram={m_vram_write_count} fixedw={m_vram_fixed_write_count} spritew={m_vram_sprite_write_count} palw={m_palette_write_count} fixed_tiles={fixedTiles} active_sprites={activeSprites} sprlines={m_sprite_line_count} sprpens={m_sprite_pen_count} sprplots={m_sprite_plot_count} sprskip={m_sprite_visible_skip_count} zoomymiss={m_sprite_zoomy_missing_count} irq=({m_irq3_pending},{m_display_position_interrupt_pending},{m_vblank_interrupt_pending})");
            }
            return 0;
        }


        void CountVideoState(out int fixedTiles, out int activeSprites)
        {
            fixedTiles = 0;
            for (int row = 0; row < 32; row++)
            {
                for (int xTile = 0; xTile < 40; xTile++)
                {
                    if (m_videoram[(0x7000 | row) + (xTile * 0x20)] != 0)
                        fixedTiles++;
                }
            }

            activeSprites = 0;
            for (int spriteNumber = 0; spriteNumber < MAX_SPRITES_PER_SCREEN; spriteNumber++)
            {
                u16 yControl = m_videoram[0x8200 | spriteNumber];
                if ((yControl & 0x3f) != 0)
                    activeSprites++;
            }
        }


        void advance_auto_animation()
        {
            if (m_auto_animation_disabled != 0)
                return;

            if (m_auto_animation_frame_counter == 0)
            {
                m_auto_animation_frame_counter = m_auto_animation_speed;
                m_auto_animation_counter++;
            }
            else
            {
                m_auto_animation_frame_counter--;
            }
        }


        void service_display_position_interrupt()
        {
            if ((m_display_position_interrupt_control & IRQ2CTRL_ENABLE) == 0)
                return;

            m_display_position_interrupt_pending = 1;
            update_interrupts();
        }


        void initialize_default_palette()
        {
            for (int index = 0; index < m_paletteram.Length; index++)
                set_palette_color(index, (u16)index);

            for (int index = 0; index < m_paletteram.Length; index++)
                set_palette_color(index, m_paletteram[index]);
        }


        void set_palette_color(int index, u16 data)
        {
            if (index < 0 || index >= m_palette.op0.m_entries)
                return;

            int red = ((data >> 14) & 0x01) | ((data >> 7) & 0x1e);
            int green = ((data >> 13) & 0x01) | ((data >> 3) & 0x1e);
            int blue = ((data >> 12) & 0x01) | ((data << 1) & 0x1e);

            if ((data & 0x8000) != 0)
            {
                red = (red * 3) / 4;
                green = (green * 3) / 4;
                blue = (blue * 3) / 4;
            }

            m_palette.op0.set_pen_color((u32)index, expand_5bit(red), expand_5bit(green), expand_5bit(blue));
            set_shadow_palette_color(index, red, green, blue);
        }


        void set_shadow_palette_color(int index, int red, int green, int blue)
        {
            int shadowIndex = index + 0x2000;
            if (shadowIndex >= m_palette.op0.m_entries)
                return;

            m_palette.op0.set_pen_color((u32)shadowIndex, expand_5bit(red / 2), expand_5bit(green / 2), expand_5bit(blue / 2));
        }


        u32 active_palette_base()
        {
            return m_palette_bank + (m_screen_shadow != 0 ? 0x2000U : 0U);
        }


        static u8 expand_5bit(int value)
        {
            value &= 0x1f;
            return (u8)((value << 3) | (value >> 2));
        }


        void draw_sprites(bitmap_ind16 bitmap, rectangle cliprect)
        {
            memory_region sprites = memregion("sprites");
            if (sprites == null || sprites.base_() == null || sprites.bytes() == 0 || m_sprite_gfx_address_mask == 0)
                return;

            MemoryU8 spriteBase = m_sprite_gfx8 ?? sprites.base_();
            int spriteBytes = (int)sprites.bytes();
            memory_region zoomy = memregion("spritegen:zoomy");
            MemoryU8 zoomyBase = zoomy != null ? zoomy.base_() : null;
            int zoomyBytes = zoomy != null ? (int)zoomy.bytes() : 0;

            int minY = Math.Max(NEOGEO_VISIBLE_TOP, cliprect.min_y);
            int maxY = Math.Min(NEOGEO_VISIBLE_BOTTOM, cliprect.max_y);
            for (int scanline = minY; scanline <= maxY; scanline++)
                draw_sprite_scanline(bitmap, scanline, spriteBase, spriteBytes, zoomyBase, zoomyBytes);
        }


        void draw_sprite_scanline(bitmap_ind16 bitmap, int scanline, MemoryU8 spriteBase, int spriteBytes, MemoryU8 zoomyBase, int zoomyBytes)
        {
            int y = 0;
            int x = 0;
            int rows = 0;
            int zoomY = 0;
            int zoomX = 0;

            if (!m_use_sprite_line_timer)
                parse_sprites(scanline);

            int spriteListBase = (scanline & 1) != 0 ? 0x8680 : 0x8600;
            int maxSpriteIndex;
            for (maxSpriteIndex = MAX_SPRITES_PER_LINE - 1; maxSpriteIndex >= 0; maxSpriteIndex--)
            {
                if (m_videoram[spriteListBase + maxSpriteIndex] != 0)
                    break;
            }

            if (maxSpriteIndex != MAX_SPRITES_PER_LINE - 1)
                maxSpriteIndex++;

            for (int spriteIndex = 0; spriteIndex <= maxSpriteIndex; spriteIndex++)
            {
                int spriteNumber = m_videoram[spriteListBase + spriteIndex] & 0x01ff;
                u16 yControl = m_videoram[0x8200 | spriteNumber];
                u16 zoomControl = m_videoram[0x8000 | spriteNumber];

                if ((yControl & 0x40) != 0)
                {
                    x = (x + zoomX + 1) & 0x01ff;
                    zoomX = (zoomControl >> 8) & 0x0f;
                }
                else
                {
                    y = 0x200 - (yControl >> 7);
                    x = m_videoram[0x8400 | spriteNumber] >> 7;
                    rows = yControl & 0x3f;
                    zoomY = zoomControl & 0xff;
                    zoomX = (zoomControl >> 8) & 0x0f;
                }

                if (rows == 0 || !sprite_on_scanline(scanline, y, rows))
                    continue;

                draw_sprite_line(bitmap, scanline, spriteNumber, x, y, rows, zoomY, zoomX, spriteBase, spriteBytes, zoomyBase, zoomyBytes);
            }
        }


        void parse_sprites(int scanline)
        {
            int y = 0;
            int rows = 0;
            int spriteListBase = (scanline & 1) != 0 ? 0x8680 : 0x8600;
            int activeSpriteCount = 0;

            for (int spriteNumber = 0; spriteNumber < MAX_SPRITES_PER_SCREEN; spriteNumber++)
            {
                u16 yControl = m_videoram[0x8200 | spriteNumber];

                if ((yControl & 0x40) == 0)
                {
                    y = 0x200 - (yControl >> 7);
                    rows = yControl & 0x3f;
                }

                if (rows == 0 || !sprite_on_scanline(scanline, y, rows))
                    continue;

                m_videoram[spriteListBase + activeSpriteCount] = (u16)spriteNumber;
                activeSpriteCount++;

                if (activeSpriteCount == MAX_SPRITES_PER_LINE)
                    break;
            }

            int clearCount = MAX_SPRITES_PER_LINE - activeSpriteCount + 1;
            for (int index = 0; index < clearCount; index++)
                m_videoram[spriteListBase + activeSpriteCount + index] = 0;
        }


        static bool sprite_on_scanline(int scanline, int y, int rows)
        {
            return rows == 0 || rows >= 0x20 || (((scanline - y) & 0x1ff) < (rows * 0x10));
        }


        void draw_sprite_line(bitmap_ind16 bitmap, int scanline, int spriteNumber, int x, int y, int rows, int zoomY, int zoomX, MemoryU8 spriteBase, int spriteBytes, MemoryU8 zoomyBase, int zoomyBytes)
        {
            if (x >= 0x140 && x <= 0x1f0)
            {
                m_sprite_visible_skip_count++;
                return;
            }

            m_sprite_line_count++;

            int spriteLine = (scanline - y) & 0x1ff;
            int zoomLine = spriteLine & 0xff;
            bool invert = (spriteLine & 0x100) != 0;

            if (invert)
                zoomLine ^= 0xff;

            if (rows > 0x20)
            {
                zoomLine %= (zoomY + 1) << 1;
                if (zoomLine > zoomY)
                {
                    zoomLine = ((zoomY + 1) << 1) - 1 - zoomLine;
                    invert = !invert;
                }
            }

            int spriteY;
            int tile;
            if (zoomyBase != null && zoomyBytes > 0)
            {
                u8 spriteYAndTile = zoomyBase[((zoomY << 8) | zoomLine) % zoomyBytes];
                spriteY = spriteYAndTile & 0x0f;
                tile = spriteYAndTile >> 4;
            }
            else
            {
                m_sprite_zoomy_missing_count++;
                spriteY = zoomLine & 0x0f;
                tile = (zoomLine >> 4) & 0x1f;
            }

            if (invert)
            {
                spriteY ^= 0x0f;
                tile ^= 0x1f;
            }

            int attrAndCodeOffset = (spriteNumber << 6) | (tile << 1);
            u16 attr = m_videoram[attrAndCodeOffset + 1];
            u32 code = (u32)(((attr << 12) & 0xf0000) | m_videoram[attrAndCodeOffset]);

            if (m_auto_animation_disabled == 0)
            {
                if ((attr & 0x08) != 0)
                    code = (code & ~0x07U) | (u32)(m_auto_animation_counter & 0x07);
                else if ((attr & 0x04) != 0)
                    code = (code & ~0x03U) | (u32)(m_auto_animation_counter & 0x03);
            }

            if ((attr & 0x02) != 0)
                spriteY ^= 0x0f;

            u16 zoomXTable = zoom_x_tables[zoomX & 0x0f];
            int gfxBase = (int)(((code << 8) | ((u32)spriteY << 4)) & m_sprite_gfx_address_mask);
            int xInc = 1;
            if ((attr & 0x01) != 0)
            {
                gfxBase += 0x0f;
                xInc = -1;
            }

            int paletteBase = (int)(active_palette_base() + ((attr >> 8) << 4));
            int drawX = x <= 0x01f0 ? x : 0;
            for (int pixel = 0; pixel < 0x10; pixel++)
            {
                if ((zoomXTable & 0x8000) != 0)
                {
                    if (x <= 0x01f0 || x >= 0x0200)
                    {
                        if (drawX >= 0 && drawX < NEOGEO_VISIBLE_WIDTH && scanline >= 0 && scanline < NEOGEO_VTOTAL)
                        {
                            int pen = m_sprite_gfx8 != null
                                ? spriteBase[gfxBase & (int)m_sprite_gfx_address_mask]
                                : read_sprite_pen(spriteBase, spriteBytes, gfxBase);
                            if (pen != 0)
                            {
                                m_sprite_pen_count++;
                                bitmap.pix(scanline, drawX)[0] = (u16)(paletteBase | pen);
                                m_sprite_plot_count++;
                            }
                        }

                        drawX++;
                    }

                    if (x > 0x01f0)
                        x++;
                }

                zoomXTable <<= 1;
                if (zoomXTable == 0)
                    break;

                gfxBase += xInc;
            }
        }


        static int read_sprite_pen(MemoryU8 spriteBase, int spriteBytes, int romAddress)
        {
            if (spriteBytes <= 0)
                return 0;

            int srcOffset = (((romAddress & ~0xff) >> 1) | (((romAddress & 0x08) ^ 0x08) << 3) | ((romAddress & 0xf0) >> 2)) % spriteBytes;
            int x = romAddress & 0x07;
            int bit0 = (spriteBase[srcOffset % spriteBytes] >> x) & 0x01;
            int bit1 = (spriteBase[(srcOffset + 2) % spriteBytes] >> x) & 0x01;
            int bit2 = (spriteBase[(srcOffset + 1) % spriteBytes] >> x) & 0x01;
            int bit3 = (spriteBase[(srcOffset + 3) % spriteBytes] >> x) & 0x01;

            return (bit3 << 3) | (bit2 << 2) | (bit1 << 1) | bit0;
        }


        void draw_fixed_layer(bitmap_ind16 bitmap, rectangle cliprect)
        {
            memory_region fixedRegion = m_fixed_layer_source != 0 ? memregion("fixed") : memregion("fixedbios");
            if (fixedRegion == null || fixedRegion.base_() == null || fixedRegion.bytes() == 0)
                return;

            MemoryU8 fixedBase = fixedRegion.base_();
            int addrMask = (int)fixedRegion.bytes() - 1;

            int minY = Math.Max(NEOGEO_VISIBLE_TOP, cliprect.min_y);
            int maxY = Math.Min(NEOGEO_VISIBLE_BOTTOM, cliprect.max_y);
            for (int y = minY; y <= maxY; y++)
            {
                int row = (y >> 3) & 0x1f;
                int rowPixel = y & 0x07;
                for (int xTile = 0; xTile < 40; xTile++)
                {
                    u16 codeAndPalette = m_videoram[(0x7000 | row) + (xTile * 0x20)];
                    int code = codeAndPalette & 0x0fff;
                    int palette = (codeAndPalette >> 12) & 0x0f;
                    int gfxOffset = ((code << 5) | rowPixel) & addrMask;
                    int x = xTile * 8;

                    draw_fixed_pair(bitmap, fixedBase, addrMask, gfxOffset + 0x10, x + 0, y, palette);
                    draw_fixed_pair(bitmap, fixedBase, addrMask, gfxOffset + 0x18, x + 2, y, palette);
                    draw_fixed_pair(bitmap, fixedBase, addrMask, gfxOffset + 0x00, x + 4, y, palette);
                    draw_fixed_pair(bitmap, fixedBase, addrMask, gfxOffset + 0x08, x + 6, y, palette);
                }
            }
        }


        void draw_fixed_pair(bitmap_ind16 bitmap, MemoryU8 gfx, int addrMask, int offset, int x, int y, int palette)
        {
            u8 data = gfx[offset & addrMask];
            plot_fixed_pixel(bitmap, x, y, palette, data & 0x0f);
            plot_fixed_pixel(bitmap, x + 1, y, palette, (data >> 4) & 0x0f);
        }


        void plot_fixed_pixel(bitmap_ind16 bitmap, int x, int y, int palette, int pen)
        {
            if (pen == 0 || x < 0 || x >= NEOGEO_VISIBLE_WIDTH || y < 0 || y >= NEOGEO_VTOTAL)
                return;
            bitmap.pix(y, x)[0] = (u16)(active_palette_base() + (u32)((palette << 4) | pen));
        }
    }


    public class neogeo : construct_ioport_helper
    {
        const u32 ROM_GROUPWORD = 0x100;


        void construct_ioport_neogeo(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("P1");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP ); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN ); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT( 0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT ); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT( 0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT ); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_BUTTON1 ); PORT_PLAYER(1);
            PORT_BIT( 0x20, IP_ACTIVE_LOW, IPT_BUTTON2 ); PORT_PLAYER(1);
            PORT_BIT( 0x40, IP_ACTIVE_LOW, IPT_BUTTON3 ); PORT_PLAYER(1);
            PORT_BIT( 0x80, IP_ACTIVE_LOW, IPT_BUTTON4 ); PORT_PLAYER(1);

            PORT_START("P2");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP ); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN ); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT( 0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT ); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT( 0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT ); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_BUTTON1 ); PORT_PLAYER(2);
            PORT_BIT( 0x20, IP_ACTIVE_LOW, IPT_BUTTON2 ); PORT_PLAYER(2);
            PORT_BIT( 0x40, IP_ACTIVE_LOW, IPT_BUTTON3 ); PORT_PLAYER(2);
            PORT_BIT( 0x80, IP_ACTIVE_LOW, IPT_BUTTON4 ); PORT_PLAYER(2);

            PORT_START("SYSTEM");
            PORT_BIT( 0x0100, IP_ACTIVE_HIGH, IPT_START1 );
            PORT_BIT( 0x0200, IP_ACTIVE_HIGH, IPT_BUTTON5 ); PORT_PLAYER(1); PORT_NAME("P1 Select");
            PORT_BIT( 0x0400, IP_ACTIVE_HIGH, IPT_START2 );
            PORT_BIT( 0x0800, IP_ACTIVE_HIGH, IPT_BUTTON5 ); PORT_PLAYER(2); PORT_NAME("P2 Select");
            PORT_BIT( 0xf0ff, IP_ACTIVE_LOW, IPT_UNUSED );

            PORT_START("AUDIO_COIN");
            PORT_BIT( 0x0001, IP_ACTIVE_LOW, IPT_COIN1 );
            PORT_BIT( 0x0002, IP_ACTIVE_LOW, IPT_COIN2 );
            PORT_BIT( 0x0004, IP_ACTIVE_LOW, IPT_SERVICE1 );
            PORT_BIT( 0x0018, IP_ACTIVE_LOW, IPT_UNUSED );

            INPUT_PORTS_END();
        }


        static tiny_rom_entry ROM_LOAD16_WORD_SWAP(string name, u32 offset, u32 length, string hash)
        {
            return ROMX_LOAD(name, offset, length, hash, ROM_GROUPWORD | ROM_REVERSE);
        }


        // ROM_START( neogeo )
        static readonly tiny_rom_entry [] rom_neogeo =
        {
            ROM_REGION( 0x80000, "mainbios", 0 ),
            ROM_LOAD16_WORD_SWAP( "sp-s2.sp1", 0x00000, 0x020000, CRC("9036d879") + SHA1("4f5ed7105b7128794654ce82b51723e16e389543") ),

            ROM_REGION( 0x100000, "maincpu", ROMREGION_ERASEFF ),

            ROM_REGION( 0x20000, "audiobios", 0 ),
            ROM_LOAD( "sm1.sm1", 0x00000, 0x20000, CRC("94416d67") + SHA1("42f9d7ddd6c0931fd64226a60dc73602b2819dcf") ),

            ROM_REGION( 0x50000, "audiocpu", 0 ),
            ROM_LOAD( "sm1.sm1", 0x00000, 0x20000, CRC("94416d67") + SHA1("42f9d7ddd6c0931fd64226a60dc73602b2819dcf") ),

            ROM_REGION( 0x20000, "spritegen:zoomy", 0 ),
            ROM_LOAD( "000-lo.lo", 0x00000, 0x20000, CRC("5a86cff2") + SHA1("5992277debadeb64d1c1c64b0a92d9293eaf7e4a") ),

            ROM_REGION( 0x20000, "fixed", ROMREGION_ERASEFF ),

            ROM_REGION( 0x20000, "fixedbios", 0 ),
            ROM_LOAD( "sfix.sfix", 0x000000, 0x20000, CRC("c2ea0cfd") + SHA1("fd4a618cdcdbf849374f0a50dd8efe9dbab706c3") ),

            ROM_REGION( 0x100000, "sprites", ROMREGION_ERASEFF ),

            ROM_END,
        };


        // ROM_START( nam1975 )
        static readonly tiny_rom_entry [] rom_nam1975 =
        {
            ROM_REGION( 0x80000, "mainbios", 0 ),
            ROM_LOAD16_WORD_SWAP( "sp-s2.sp1", 0x00000, 0x020000, CRC("9036d879") + SHA1("4f5ed7105b7128794654ce82b51723e16e389543") ),

            ROM_REGION( 0x20000, "spritegen:zoomy", 0 ),
            ROM_LOAD( "000-lo.lo", 0x00000, 0x20000, CRC("5a86cff2") + SHA1("5992277debadeb64d1c1c64b0a92d9293eaf7e4a") ),

            ROM_REGION( 0x20000, "fixedbios", 0 ),
            ROM_LOAD( "sfix.sfix", 0x000000, 0x20000, CRC("c2ea0cfd") + SHA1("fd4a618cdcdbf849374f0a50dd8efe9dbab706c3") ),

            ROM_REGION( 0x100000, "maincpu", 0 ),
            ROM_LOAD16_WORD_SWAP( "001-p1.p1", 0x000000, 0x080000, CRC("cc9fc951") + SHA1("92f4e6ddeeb825077d92dbb70b50afea985f15c0") ),

            ROM_REGION( 0x040000, "fixed", 0 ),
            ROM_LOAD( "001-s1.s1", 0x000000, 0x020000, CRC("7988ba51") + SHA1("bc2f661f381b06b34ac2fa215dd5689d3bf84832") ),

            ROM_REGION( 0x050000, "audiocpu", 0 ),
            ROM_LOAD( "001-m1.m1", 0x000000, 0x040000, CRC("ba874463") + SHA1("a83514f4b20301f84a98699900e2593f1c1b8846") ),
            ROM_RELOAD(             0x010000, 0x040000 ),

            ROM_REGION( 0x080000, "ymsnd:adpcma", 0 ),
            ROM_LOAD( "001-v11.v11", 0x000000, 0x080000, CRC("a7c3d5e5") + SHA1("e3efc86940f91c53b7724c4566cfc21ea1a7a465") ),

            ROM_REGION( 0x180000, "ymsnd:adpcmb", 0 ),
            ROM_LOAD( "001-v21.v21", 0x000000, 0x080000, CRC("55e670b3") + SHA1("a047049646a90b6db2d1882264df9256aa5a85e5") ),
            ROM_LOAD( "001-v22.v22", 0x080000, 0x080000, CRC("ab0d8368") + SHA1("404114db9f3295929080b87a5d0106b40da6223a") ),
            ROM_LOAD( "001-v23.v23", 0x100000, 0x080000, CRC("df468e28") + SHA1("4e5d4a709a4737a87bba4083aeb788f657862f1a") ),

            ROM_REGION( 0x300000, "sprites", 0 ),
            ROM_LOAD16_BYTE( "001-c1.c1", 0x000000, 0x080000, CRC("32ea98e1") + SHA1("c2fb3fb7dd14523a4b4b7fbdb81f44cb4cc48239") ),
            ROM_LOAD16_BYTE( "001-c2.c2", 0x000001, 0x080000, CRC("cbc4064c") + SHA1("224c970fd060d841fd430c946ef609bb57b6d78c") ),
            ROM_LOAD16_BYTE( "001-c3.c3", 0x100000, 0x080000, CRC("0151054c") + SHA1("f24fb501a7845f64833f4e5a461bcf9dc3262557") ),
            ROM_LOAD16_BYTE( "001-c4.c4", 0x100001, 0x080000, CRC("0a32570d") + SHA1("f108446ec7844fde25f7a4ab454f76d384bf5e52") ),
            ROM_LOAD16_BYTE( "001-c5.c5", 0x200000, 0x080000, CRC("90b74cc2") + SHA1("89898da36db259180e5261ed45eafc99ca13e504") ),
            ROM_LOAD16_BYTE( "001-c6.c6", 0x200001, 0x080000, CRC("e62bed58") + SHA1("d05b2903b212a51ee131e52c761b714cb787683e") ),

            ROM_END,
        };


        // ROM_START( mslug )
        static readonly tiny_rom_entry [] rom_mslug =
        {
            ROM_REGION( 0x80000, "mainbios", 0 ),
            ROM_LOAD16_WORD_SWAP( "sp-s2.sp1", 0x00000, 0x020000, CRC("9036d879") + SHA1("4f5ed7105b7128794654ce82b51723e16e389543") ),

            ROM_REGION( 0x20000, "spritegen:zoomy", 0 ),
            ROM_LOAD( "000-lo.lo", 0x00000, 0x20000, CRC("5a86cff2") + SHA1("5992277debadeb64d1c1c64b0a92d9293eaf7e4a") ),

            ROM_REGION( 0x20000, "fixedbios", 0 ),
            ROM_LOAD( "sfix.sfix", 0x000000, 0x20000, CRC("c2ea0cfd") + SHA1("fd4a618cdcdbf849374f0a50dd8efe9dbab706c3") ),

            ROM_REGION( 0x200000, "maincpu", 0 ),
            ROM_LOAD16_WORD_SWAP( "201-p1.p1", 0x100000, 0x100000, CRC("08d8daa5") + SHA1("b888993dbb7e9f0a28a01d7d2e1da00ef9cf6f38") ),
            ROM_CONTINUE(                         0x000000, 0x100000 ),

            ROM_REGION( 0x040000, "fixed", 0 ),
            ROM_LOAD( "201-s1.s1", 0x000000, 0x020000, CRC("2f55958d") + SHA1("550b53628daec9f1e1e11a398854092d90f9505a") ),

            ROM_REGION( 0x030000, "audiocpu", 0 ),
            ROM_LOAD( "201-m1.m1", 0x000000, 0x020000, CRC("c28b3253") + SHA1("fd75bd15aed30266a8b3775f276f997af57d1c06") ),
            ROM_RELOAD(             0x010000, 0x020000 ),

            ROM_REGION( 0x800000, "ymsnd:adpcma", 0 ),
            ROM_LOAD( "201-v1.v1", 0x000000, 0x400000, CRC("23d22ed1") + SHA1("cd076928468ad6bcc5f19f88cb843ecb5e660681") ),
            ROM_LOAD( "201-v2.v2", 0x400000, 0x400000, CRC("472cf9db") + SHA1("5f79ea9286d22ed208128f9c31ca75552ce08b57") ),

            ROM_REGION( 0x1000000, "sprites", 0 ),
            ROM_LOAD16_BYTE( "201-c1.c1", 0x000000, 0x400000, CRC("72813676") + SHA1("7b045d1a48980cb1a140699011cb1a3d4acdc4d1") ),
            ROM_LOAD16_BYTE( "201-c2.c2", 0x000001, 0x400000, CRC("96f62574") + SHA1("cb7254b885989223bba597b8ff0972dfa5957816") ),
            ROM_LOAD16_BYTE( "201-c3.c3", 0x800000, 0x400000, CRC("5121456a") + SHA1("0a7a27d603d1bb2520b5570ebf5b34a106e255a6") ),
            ROM_LOAD16_BYTE( "201-c4.c4", 0x800001, 0x400000, CRC("f4ad59a3") + SHA1("4e94fda8ee63abf0f92afe08060a488546e5c280") ),

            ROM_END,
        };


        static void neogeo_state_neogeo(machine_config config, device_t device) { ((neogeo_state)device).neogeo_skeleton(config); }


        static neogeo m_neogeo = new neogeo();


        static device_t device_creator_neogeo_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new neogeo_state(mconfig, (device_type)type, tag); }

        public static bool install_dynamic_driver(string name, string parent, string year, string manufacturer, string fullname, tiny_rom_entry [] rom)
        {
            if (string.IsNullOrWhiteSpace(name) || rom == null || rom.Length == 0)
                return false;
            if (driver_list.find(name) >= 0)
                return false;

            game_driver driver = GAME(
                device_creator_neogeo_state,
                rom,
                string.IsNullOrWhiteSpace(year) ? "????" : year,
                name,
                string.IsNullOrWhiteSpace(parent) ? "neogeo" : parent,
                neogeo_state_neogeo,
                m_neogeo.construct_ioport_neogeo,
                driver_device.empty_init,
                ROT0,
                string.IsNullOrWhiteSpace(manufacturer) ? "SNK" : manufacturer,
                string.IsNullOrWhiteSpace(fullname) ? name : fullname,
                MACHINE_IS_SKELETON);

            game_driver [] existing = drivlist_global.s_drivers_sorted ?? Array.Empty<game_driver>();
            game_driver [] expanded = new game_driver[existing.Length + 1];
            Array.Copy(existing, expanded, existing.Length);
            expanded[expanded.Length - 1] = driver;
            drivlist_global.s_drivers_sorted = expanded.OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase).ToArray();
            drivlist_global.s_driver_count = (ulong)drivlist_global.s_drivers_sorted.Length;
            return true;
        }


        //                                                       creator,                    rom,         YEAR,   NAME,     PARENT,   MACHINE,             INPUT, INIT,                     MONITOR,COMPANY, FULLNAME,                                   FLAGS
        public static readonly game_driver driver_neogeo  = GAME( device_creator_neogeo_state, rom_neogeo,  "1990", "neogeo",  "0",      neogeo_state_neogeo, m_neogeo.construct_ioport_neogeo,  driver_device.empty_init, ROT0,   "SNK",   "Neo Geo MVS BIOS",                       MACHINE_IS_BIOS_ROOT | MACHINE_IS_SKELETON );
        public static readonly game_driver driver_nam1975 = GAME( device_creator_neogeo_state, rom_nam1975, "1990", "nam1975", "neogeo", neogeo_state_neogeo, m_neogeo.construct_ioport_neogeo,  driver_device.empty_init, ROT0,   "SNK",   "NAM-1975 (NGM-001 ~ NGH-001)",          MACHINE_IS_SKELETON );
        public static readonly game_driver driver_mslug   = GAME( device_creator_neogeo_state, rom_mslug,   "1996", "mslug",   "neogeo", neogeo_state_neogeo, m_neogeo.construct_ioport_neogeo,  driver_device.empty_init, ROT0,   "Nazca", "Metal Slug - Super Vehicle-001",        MACHINE_IS_SKELETON );
    }
}
