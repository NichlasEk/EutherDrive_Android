// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Ported from MAME pgm.cpp

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
using static mame.gen_latch_global;
using static mame.hash_global;
using static mame.ics2115_global;
using static mame.igs023_video_global;
using static mame.ioport_global;
using static mame.ioport_ioport_type_helper;
using static mame.m68000_global;
using static mame.romentry_global;
using static mame.screen_global;
using static mame.speaker_global;
using static mame.z80_global;

namespace mame
{
    class pgm_state : driver_device
    {
        const int ScreenWidth = 448;
        const int ScreenHeight = 224;
        const uint MainRamStart = 0x800000;
        const uint MainRamEnd = 0x81ffff;
        static readonly bool TracePgmSound = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM_SOUND_TRACE") == "1";
        static readonly u8 [] KovBATable =
        {
            0x00, 0x29, 0x2c, 0x35, 0x3a, 0x41, 0x4a, 0x4e, 0x57, 0x5e, 0x77, 0x79, 0x7a, 0x7b, 0x7c, 0x7d,
            0x7e, 0x7f, 0x80, 0x81, 0x82, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8c, 0x8d, 0x8e, 0x90,
            0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0x9b, 0x9c, 0x9e, 0xa3, 0xd4, 0xa9, 0xaf, 0xb5, 0xbb, 0xc1,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        static readonly u8 [] KovB0Table = { 2, 0, 1, 4, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        readonly required_device<m68000_device> m_maincpu;
        readonly required_device<z80_device> m_soundcpu;
        readonly required_device<igs023_video_device> m_video;
        readonly required_device<ics2115_device> m_ics;
        readonly required_device<generic_latch_8_device> m_soundlatch0;
        readonly required_device<generic_latch_8_device> m_soundlatch1;
        readonly required_device<generic_latch_8_device> m_soundlatch2;
        readonly PgmV3021Rtc m_rtc = new PgmV3021Rtc();
        readonly u8 [] m_mainram = new u8[0x20000];
        readonly u8 [] m_z80ram = new u8[0x10000];
        readonly u16 [] m_paletteram = new u16[0xa00];
        readonly u32 [] m_kov_slots = new u32[0x100];
        MemoryU8 m_mainrom;
        int m_mainromBytes;
        u16 m_value0;
        u16 m_value1;
        u16 m_valuekey;
        u16 m_ddp3lastcommand;
        u32 m_valueresponse;
        int m_curslots;
        u16 m_kov_c0_value;
        u16 m_kov_cb_value;
        u16 m_kov_fe_value;
        int m_simregion;
        int m_trace_z80_ram_writes;
        int m_trace_sound_writes;

        public pgm_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
            m_maincpu = new required_device<m68000_device>(this, "maincpu");
            m_soundcpu = new required_device<z80_device>(this, "soundcpu");
            m_video = new required_device<igs023_video_device>(this, "igs023");
            m_ics = new required_device<ics2115_device>(this, "ics");
            m_soundlatch0 = new required_device<generic_latch_8_device>(this, "soundlatch0");
            m_soundlatch1 = new required_device<generic_latch_8_device>(this, "soundlatch1");
            m_soundlatch2 = new required_device<generic_latch_8_device>(this, "soundlatch2");
        }

        public void pgm(machine_config config)
        {
            M68000(config, m_maincpu, new XTAL(20_000_000));
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, pgm_68k_map);

            Z80(config, m_soundcpu, new XTAL(33_868_800) / 4);
            m_soundcpu.op0.memory().set_addrmap(AS_PROGRAM, pgm_z80_mem);
            m_soundcpu.op0.memory().set_addrmap(AS_IO, pgm_z80_io);

            screen_device screen = SCREEN(config, "screen", SCREEN_TYPE_RASTER);
            screen.set_screen_update(screen_update);
            screen.set_raw(new XTAL(50_000_000) / 5, 640, 0, ScreenWidth, 264, 0, ScreenHeight);
            screen.screen_vblank().set((write_line_delegate)vblank_irq).reg();

            SPEAKER(config, "mono").front_center();
            GENERIC_LATCH_8(config, m_soundlatch0);
            GENERIC_LATCH_8(config, m_soundlatch1);
            GENERIC_LATCH_8(config, m_soundlatch2);

            IGS023_VIDEO(config, m_video, 0);
            m_video.op0.set_palette_reader(palette_pen);

            ICS2115(config, m_ics, new XTAL(33_868_800));
            m_ics.op0.irq().set_inputline(m_soundcpu, 0).reg();
            m_ics.op0.disound.add_route(ALL_OUTPUTS, "mono", 1.0);
        }

        void pgm_68k_map(address_map map, device_t device)
        {
            map.op(0x000000, 0x5fffff).rom();
            map.op(0x4f0000, 0x4f003f).r((read16_delegate)kov_protram_r);
            map.op(0x500000, 0x500005).rw((read16_delegate)kov_sim_r, (write16_delegate)kov_sim_w);
            map.op(0x700006, 0x700007).nopw();
            map.op(MainRamStart, MainRamEnd).mirror(0x0e0000).rw((read16_delegate)mainram_r, (write16_delegate)mainram_w);
            map.op(0x900000, 0x907fff).mirror(0x0f8000).rw((read16_delegate)video_ram_r, (write16_delegate)video_ram_w);
            map.op(0xa00000, 0xa013ff).rw((read16_delegate)palette_r, (write16_delegate)palette_w);
            map.op(0xb00000, 0xb0ffff).rw((read16_delegate)video_regs_r, (write16_delegate)video_regs_w);
            map.op(0xc00000, 0xc0000f).rw((read16_delegate)sound_rtc_stub_r, (write16_delegate)sound_rtc_stub_w);
            map.op(0xc08000, 0xc08007).rw((read16_delegate)input_stub_r, (write16_delegate)input_stub_w);
            map.op(0xc10000, 0xc1ffff).rw((read16_delegate)z80_ram_68k_r, (write16_delegate)z80_ram_68k_w);
        }

        void pgm_z80_mem(address_map map, device_t device)
        {
            map.op(0x0000, 0xffff).rw(z80_program_r, z80_program_w);
        }

        void pgm_z80_io(address_map map, device_t device)
        {
            map.op(0x8000, 0x8003).rw(m_ics, (offs_t offset) => m_ics.op0.read(offset), (offs_t offset, u8 data) => m_ics.op0.write(offset, data));
            map.op(0x8100, 0x81ff).r(m_soundlatch2, () => m_soundlatch2.op0.read()).w(z80_latch3_w);
            map.op(0x8200, 0x82ff).rw(m_soundlatch0, () => m_soundlatch0.op0.read(), (u8 data) => m_soundlatch0.op0.write(data));
            map.op(0x8400, 0x84ff).rw(m_soundlatch1, () => m_soundlatch1.op0.read(), (u8 data) => m_soundlatch1.op0.write(data));
        }

        protected override void machine_start()
        {
            memory_region maincpu = memregion("maincpu");
            if (maincpu != null)
            {
                m_mainrom = maincpu.base_();
                m_mainromBytes = (int)Math.Min(maincpu.bytes(), int.MaxValue);
            }

            m_maincpu.op0.set_fast_memory_handlers(
                Fast68kReadByte,
                Fast68kReadWord,
                Fast68kWriteByte,
                Fast68kWriteWord);
            m_soundcpu.op0.set_fast_memory_handlers(
                FastZ80ReadByte,
                FastZ80WriteByte);

            save_item(NAME(new { m_mainram }));
            save_item(NAME(new { m_z80ram }));
            save_item(NAME(new { m_paletteram }));
            save_item(NAME(new { m_kov_slots }));
            SaveStateRef(nameof(m_value0), () => m_value0, value => m_value0 = value);
            SaveStateRef(nameof(m_value1), () => m_value1, value => m_value1 = value);
            SaveStateRef(nameof(m_valuekey), () => m_valuekey, value => m_valuekey = value);
            SaveStateRef(nameof(m_ddp3lastcommand), () => m_ddp3lastcommand, value => m_ddp3lastcommand = value);
            SaveStateRef(nameof(m_valueresponse), () => m_valueresponse, value => m_valueresponse = value);
            SaveStateRef(nameof(m_curslots), () => m_curslots, value => m_curslots = value);
            SaveStateRef(nameof(m_kov_c0_value), () => m_kov_c0_value, value => m_kov_c0_value = value);
            SaveStateRef(nameof(m_kov_cb_value), () => m_kov_cb_value, value => m_kov_cb_value = value);
            SaveStateRef(nameof(m_kov_fe_value), () => m_kov_fe_value, value => m_kov_fe_value = value);
            SaveStateRef(nameof(m_simregion), () => m_simregion, value => m_simregion = value);
            m_rtc.RegisterSaveState(this, "m_rtc");
        }

        protected override void machine_reset()
        {
            Array.Clear(m_mainram, 0, m_mainram.Length);
            Array.Clear(m_z80ram, 0, m_z80ram.Length);
            Array.Clear(m_paletteram, 0, m_paletteram.Length);
            m_trace_z80_ram_writes = 0;
            m_trace_sound_writes = 0;
            m_rtc.Reset();
            ResetKovProtection();
            m_maincpu.op0.reset_from_bus();
            m_soundcpu.op0.set_input_line(INPUT_LINE_HALT, ASSERT_LINE);
        }

        void SaveStateRef<T>(string itemName, Func<T> getter, Action<T> setter)
        {
            machine().save().save_item_ref(this, name(), tag(), 0, itemName, getter, setter);
        }

        bool Fast68kReadByte(u32 address, out u8 value)
        {
            address &= 0x00ff_ffff;
            if (IsFastRomByteAddress(address))
            {
                value = m_mainrom[(int)(address ^ 1)];
                return true;
            }
            if (IsFastMainRamAddress(address))
            {
                value = m_mainram[address & (uint)(m_mainram.Length - 1)];
                return true;
            }
            if (IsFastZ80RamAddress(address))
            {
                value = m_z80ram[address & 0xffff];
                return true;
            }
            if (TryFast68kMappedRead(address, (address & 1) == 0 ? (u16)0xff00 : (u16)0x00ff, out u16 mappedValue))
            {
                value = (address & 1) == 0 ? (u8)(mappedValue >> 8) : (u8)mappedValue;
                return true;
            }

            value = 0xff;
            return false;
        }

        bool Fast68kReadWord(u32 address, out u16 value)
        {
            address &= 0x00ff_ffff;
            if ((address & 1) == 0 && IsFastRomWordAddress(address))
            {
                value = (u16)((m_mainrom[(int)(address + 1)] << 8) | m_mainrom[(int)address]);
                return true;
            }
            if ((address & 1) == 0 && IsFastMainRamAddress(address))
            {
                uint byteOffset = address & (uint)(m_mainram.Length - 1);
                value = (u16)((m_mainram[byteOffset] << 8) | m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)]);
                return true;
            }
            if ((address & 1) == 0 && IsFastZ80RamAddress(address))
            {
                uint byteOffset = address & 0xffff;
                value = (u16)((m_z80ram[byteOffset] << 8) | m_z80ram[(byteOffset + 1) & 0xffff]);
                return true;
            }
            if ((address & 1) == 0 && TryFast68kMappedRead(address, 0xffff, out value))
                return true;

            value = 0xffff;
            return false;
        }

        bool Fast68kWriteByte(u32 address, u8 value)
        {
            address &= 0x00ff_ffff;
            if (IsFastMainRamAddress(address))
            {
                m_mainram[address & (uint)(m_mainram.Length - 1)] = value;
                return true;
            }
            if (IsFastZ80RamAddress(address))
            {
                uint byteOffset = address & 0xffff;
                m_z80ram[byteOffset] = value;
                TraceZ80RamWrite(byteOffset, value);
                return true;
            }
            if (TryFast68kMappedWrite(
                address,
                (address & 1) == 0 ? (u16)(value << 8) : value,
                (address & 1) == 0 ? (u16)0xff00 : (u16)0x00ff))
                return true;

            return false;
        }

        bool Fast68kWriteWord(u32 address, u16 value)
        {
            address &= 0x00ff_ffff;
            if ((address & 1) != 0)
                return false;

            if (IsFastMainRamAddress(address))
            {
                uint byteOffset = address & (uint)(m_mainram.Length - 1);
                m_mainram[byteOffset] = (u8)(value >> 8);
                m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)] = (u8)value;
                return true;
            }
            if (IsFastZ80RamAddress(address))
            {
                uint byteOffset = address & 0xffff;
                m_z80ram[byteOffset] = (u8)(value >> 8);
                TraceZ80RamWrite(byteOffset, (u8)(value >> 8));
                m_z80ram[(byteOffset + 1) & 0xffff] = (u8)value;
                TraceZ80RamWrite((byteOffset + 1) & 0xffff, (u8)value);
                return true;
            }
            if (TryFast68kMappedWrite(address, value, 0xffff))
                return true;

            return false;
        }

        bool IsFastRomByteAddress(u32 address)
        {
            if (m_mainrom == null || address >= 0x600000 || address >= (uint)m_mainromBytes)
                return false;

            return (address < 0x4f0000 || address > 0x4f003f)
                && (address < 0x500000 || address > 0x500005);
        }

        bool IsFastRomWordAddress(u32 address)
        {
            if (m_mainrom == null || address >= 0x600000 || address + 1 >= (uint)m_mainromBytes)
                return false;

            return (address < 0x4f0000 || address > 0x4f003e)
                && (address < 0x500000 || address > 0x500004);
        }

        static bool IsFastMainRamAddress(u32 address)
        {
            return address >= MainRamStart && address <= 0x8fffff;
        }

        static bool IsFastZ80RamAddress(u32 address)
        {
            return address >= 0xc10000 && address <= 0xc1ffff;
        }

        bool TryFast68kMappedRead(u32 address, u16 memMask, out u16 value)
        {
            u32 wordAddress = address & 0x00ff_fffe;
            if (wordAddress >= 0x900000 && wordAddress <= 0x9ffffe)
            {
                value = video_ram_r(null, (wordAddress & 0x7fff) >> 1, memMask);
                return true;
            }
            if (wordAddress >= 0xa00000 && wordAddress <= 0xa013fe)
            {
                value = palette_r(null, (wordAddress - 0xa00000) >> 1, memMask);
                return true;
            }
            if (wordAddress >= 0xb00000 && wordAddress <= 0xb0fffe)
            {
                value = video_regs_r(null, (wordAddress - 0xb00000) >> 1, memMask);
                return true;
            }
            if (wordAddress >= 0xc00000 && wordAddress <= 0xc0000e)
            {
                value = sound_rtc_stub_r(null, (wordAddress - 0xc00000) >> 1, memMask);
                return true;
            }
            if (wordAddress >= 0xc08000 && wordAddress <= 0xc08006)
            {
                value = input_stub_r(null, (wordAddress - 0xc08000) >> 1, memMask);
                return true;
            }

            value = 0xffff;
            return false;
        }

        bool TryFast68kMappedWrite(u32 address, u16 data, u16 memMask)
        {
            u32 wordAddress = address & 0x00ff_fffe;
            if (wordAddress == 0x700006)
                return true;
            if (wordAddress >= 0x900000 && wordAddress <= 0x9ffffe)
            {
                video_ram_w(null, (wordAddress & 0x7fff) >> 1, data, memMask);
                return true;
            }
            if (wordAddress >= 0xa00000 && wordAddress <= 0xa013fe)
            {
                palette_w(null, (wordAddress - 0xa00000) >> 1, data, memMask);
                return true;
            }
            if (wordAddress >= 0xb00000 && wordAddress <= 0xb0fffe)
            {
                video_regs_w(null, (wordAddress - 0xb00000) >> 1, data, memMask);
                return true;
            }
            if (wordAddress >= 0xc00000 && wordAddress <= 0xc0000e)
            {
                sound_rtc_stub_w(null, (wordAddress - 0xc00000) >> 1, data, memMask);
                return true;
            }
            if (wordAddress >= 0xc08000 && wordAddress <= 0xc08006)
            {
                input_stub_w(null, (wordAddress - 0xc08000) >> 1, data, memMask);
                return true;
            }

            return false;
        }

        void vblank_irq(int state)
        {
            if (state != 0)
            {
                m_video.op0.get_sprites(sprite_ram_word);
                m_maincpu.op0.set_input_line(6, HOLD_LINE);
            }
            else
            {
                m_maincpu.op0.set_input_line(4, HOLD_LINE);
            }
        }

        u16 mainram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            uint byteOffset = (offset << 1) & (uint)(m_mainram.Length - 1);
            return (u16)((m_mainram[byteOffset] << 8) | m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)]);
        }

        void mainram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            uint byteOffset = (offset << 1) & (uint)(m_mainram.Length - 1);
            if ((mem_mask & 0xff00) != 0)
                m_mainram[byteOffset] = (u8)(data >> 8);
            if ((mem_mask & 0x00ff) != 0)
                m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)] = (u8)data;
        }

        u16 sprite_ram_word(offs_t offset)
        {
            uint byteOffset = (offset << 1) & (uint)(m_mainram.Length - 1);
            return (u16)((m_mainram[byteOffset] << 8) | m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)]);
        }

        u16 video_ram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_video.op0.videoram_r(space, offset, mem_mask);
        }

        void video_ram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            m_video.op0.videoram_w(space, offset, data, mem_mask);
        }

        u16 video_regs_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_video.op0.videoregs_r(space, offset, mem_mask);
        }

        void video_regs_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            m_video.op0.videoregs_w(space, offset, data, mem_mask);
        }

        u16 palette_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_paletteram[offset % (uint)m_paletteram.Length];
        }

        void palette_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int index = (int)(offset % (uint)m_paletteram.Length);
            COMBINE_DATA(ref m_paletteram[index], data, mem_mask);
        }

        uint palette_pen(int pen)
        {
            u16 rgb555 = m_paletteram[pen % m_paletteram.Length];
            uint r = Expand5((rgb555 >> 10) & 0x1f);
            uint g = Expand5((rgb555 >> 5) & 0x1f);
            uint b = Expand5(rgb555 & 0x1f);
            return (r << 16) | (g << 8) | b;
        }

        u16 sound_rtc_stub_r(address_space space, offs_t offset, u16 mem_mask)
        {
            u16 result = 0xffff;
            int baseByte = (int)offset << 1;
            if ((mem_mask & 0xff00) != 0)
                result = (u16)((result & 0x00ff) | (read_sound_byte(baseByte) << 8));
            if ((mem_mask & 0x00ff) != 0)
                result = (u16)((result & 0xff00) | read_sound_byte(baseByte + 1));
            return result;
        }

        void sound_rtc_stub_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int baseByte = (int)offset << 1;
            if ((mem_mask & 0xff00) != 0)
                write_sound_byte(baseByte, (u8)(data >> 8));
            if ((mem_mask & 0x00ff) != 0)
                write_sound_byte(baseByte + 1, (u8)data);

            if (baseByte == 0x08)
                z80_reset_w(data);
            else if (baseByte == 0x0a)
                z80_ctrl_w(data);
        }

        u16 input_stub_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return (offset & 3) switch
            {
                0 => (u16)ioport("P1P2").read(),
                1 => (u16)ioport("P3P4").read(),
                2 => (u16)ioport("Service").read(),
                3 => (u16)ioport("DSW").read(),
                _ => 0xffff
            };
        }

        void input_stub_w(address_space space, offs_t offset, u16 data, u16 mem_mask) { }

        u8 read_sound_byte(int address)
        {
            return address switch
            {
                0x03 => m_soundlatch0.op0.read(),
                0x05 => m_soundlatch1.op0.read(),
                0x07 => m_rtc.Read(),
                0x0d => m_soundlatch2.op0.read(),
                _ => 0xff
            };
        }

        void write_sound_byte(int address, u8 data)
        {
            switch (address)
            {
            case 0x03:
                m68k_latch1_w(data);
                break;
            case 0x05:
                m_soundlatch1.op0.write(data);
                break;
            case 0x07:
                m_rtc.Write(data);
                break;
            case 0x0d:
                m_soundlatch2.op0.write(data);
                break;
            }
        }

        void z80_reset_w(u16 data)
        {
            if (data == 0x5050)
            {
                TraceSound($"z80 reset release data=0x{data:x4}");
                m_ics.op0.reset();
                m_soundcpu.op0.set_input_line(INPUT_LINE_HALT, CLEAR_LINE);
                m_soundcpu.op0.pulse_input_line(INPUT_LINE_RESET, attotime.zero);
            }
            else
            {
                TraceSound($"z80 halt data=0x{data:x4}");
                m_soundcpu.op0.set_input_line(INPUT_LINE_HALT, ASSERT_LINE);
            }
        }

        void z80_ctrl_w(u16 data)
        {
            TraceSound($"z80 ctrl data=0x{data:x4}");
        }

        void m68k_latch1_w(u8 data)
        {
            TraceSound($"m68k latch0/NMI data=0x{data:x2}");
            m_soundlatch0.op0.write(data);
            m_soundcpu.op0.pulse_input_line(INPUT_LINE_NMI, attotime.zero);
        }

        void z80_latch3_w(u8 data)
        {
            m_soundlatch2.op0.write(data);
        }

        u16 z80_ram_68k_r(address_space space, offs_t offset, u16 mem_mask)
        {
            uint byteOffset = (offset << 1) & 0xffff;
            return (u16)((m_z80ram[byteOffset] << 8) | m_z80ram[(byteOffset + 1) & 0xffff]);
        }

        void z80_ram_68k_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            uint byteOffset = (offset << 1) & 0xffff;
            if ((mem_mask & 0xff00) != 0)
            {
                m_z80ram[byteOffset] = (u8)(data >> 8);
                TraceZ80RamWrite(byteOffset, (u8)(data >> 8));
            }
            if ((mem_mask & 0x00ff) != 0)
            {
                m_z80ram[(byteOffset + 1) & 0xffff] = (u8)data;
                TraceZ80RamWrite((byteOffset + 1) & 0xffff, (u8)data);
            }
        }

        u8 z80_program_r(offs_t offset) => m_z80ram[offset & 0xffff];

        void z80_program_w(offs_t offset, u8 data)
        {
            m_z80ram[offset & 0xffff] = data;
        }

        bool FastZ80ReadByte(u16 address, out u8 value)
        {
            value = m_z80ram[address];
            return true;
        }

        bool FastZ80WriteByte(u16 address, u8 value)
        {
            m_z80ram[address] = value;
            return true;
        }

        void TraceZ80RamWrite(uint offset, u8 data)
        {
            if (!TracePgmSound || m_trace_z80_ram_writes >= 48)
                return;

            m_trace_z80_ram_writes++;
            Console.Error.WriteLine($"[PGM-SOUND] z80ram[{offset:x4}]=0x{data:x2} pc=0x{m_maincpu.op0.Pc:x6}");
        }

        void TraceSound(string message)
        {
            if (!TracePgmSound || m_trace_sound_writes >= 96)
                return;

            m_trace_sound_writes++;
            Console.Error.WriteLine($"[PGM-SOUND] {message} pc=0x{m_maincpu.op0.Pc:x6}");
        }

        u16 kov_sim_r(address_space space, offs_t offset, u16 mem_mask)
        {
            if (offset == 0)
            {
                u16 data = (u16)m_valueresponse;
                u16 realkey = (u16)((m_valuekey >> 8) | m_valuekey);
                return (u16)(data ^ realkey);
            }

            if (offset == 1)
            {
                u16 data = (u16)(m_valueresponse >> 16);
                u16 realkey = (u16)((m_valuekey >> 8) | m_valuekey);
                return (u16)(data ^ realkey);
            }

            return 0xffff;
        }

        void kov_sim_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (offset == 0)
            {
                m_value0 = data;
                return;
            }

            if (offset != 1)
                return;

            if ((data >> 8) == 0xff)
                m_valuekey = 0xff00;

            u16 realkey = (u16)((m_valuekey >> 8) | m_valuekey);
            m_valuekey += 0x0100;
            m_valuekey &= 0xff00;
            if (m_valuekey == 0xff00)
                m_valuekey = 0x0100;

            data ^= realkey;
            m_value1 = data;
            m_value0 ^= realkey;
            m_ddp3lastcommand = (u16)(m_value1 & 0xff);
            CommandHandlerKov();
        }

        u16 kov_protram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return offset == 4 ? (u16)m_simregion : (u16)0;
        }

        void ResetKovProtection()
        {
            Array.Clear(m_kov_slots, 0, m_kov_slots.Length);
            m_value0 = 0;
            m_value1 = 0;
            m_valuekey = 0;
            m_ddp3lastcommand = 0;
            m_valueresponse = 0;
            m_curslots = 0;
            m_kov_c0_value = 0;
            m_kov_cb_value = 0;
            m_kov_fe_value = 0;
            m_simregion = 5;
        }

        void CommandHandlerKov()
        {
            switch (m_ddp3lastcommand)
            {
            case 0x67:
            case 0x8e:
            case 0xa3:
            case 0x33:
            case 0x3a:
            case 0xc5:
                m_valueresponse = 0x880000;
                break;

            case 0x99:
                m_simregion = 5;
                m_valueresponse = (u32)(0x880000 | (m_simregion << 8));
                m_valuekey = 0x0100;
                break;

            case 0x9d:
            case 0xe0:
            case 0x9e:
                m_valueresponse = (u32)(0xa00000 + ((m_value0 & 0x1f) * 0x40));
                break;

            case 0xb0:
                m_valueresponse = KovB0Table[m_value0 & 0x0f];
                break;

            case 0xb4:
            case 0xb7:
                m_valueresponse = 0x880000;
                if (m_value0 == 0x0102)
                    m_value0 = 0x0100;
                m_kov_slots[(m_value0 >> 8) & 0x0f] = m_kov_slots[m_value0 & 0x0f];
                break;

            case 0xba:
                m_valueresponse = KovBATable[m_value0 & 0x3f];
                break;

            case 0xc0:
                m_valueresponse = 0x880000;
                m_kov_c0_value = m_value0;
                break;

            case 0xc3:
                m_valueresponse = (u32)(0x904000 + ((m_kov_c0_value + (m_value0 * 0x40)) * 4));
                break;

            case 0xcb:
                m_valueresponse = 0x880000;
                m_kov_cb_value = m_value0;
                break;

            case 0xcc:
            {
                int y = m_value0;
                if ((y & 0x400) != 0)
                    y = -(0x400 - (y & 0x3ff));
                m_valueresponse = (u32)(0x900000 + ((m_kov_cb_value + (y * 0x40)) * 4));
                break;
            }

            case 0xd0:
            case 0xcd:
                m_valueresponse = (u32)(0xa01000 + (m_value0 * 0x20));
                break;

            case 0xd6:
                m_valueresponse = 0x880000;
                m_kov_slots[0] = m_kov_slots[m_value0 & 0x0f];
                break;

            case 0xdc:
            case 0x11:
                m_valueresponse = (u32)(0xa00800 + (m_value0 * 0x40));
                break;

            case 0xe5:
            {
                m_valueresponse = 0x880000;
                int sel = (m_curslots >> 12) & 0x0f;
                m_kov_slots[sel] = (m_kov_slots[sel] & 0x00ff0000) | (u32)(m_value0 & 0xffff);
                break;
            }

            case 0xe7:
            {
                m_valueresponse = 0x880000;
                m_curslots = m_value0;
                int sel = (m_curslots >> 12) & 0x0f;
                m_kov_slots[sel] = (m_kov_slots[sel] & 0x0000ffff) | (u32)((m_value0 & 0x00ff) << 16);
                break;
            }

            case 0xf0:
                m_valueresponse = 0x00c000;
                break;

            case 0xf8:
            case 0xab:
                m_valueresponse = m_kov_slots[m_value0 & 0x0f] & 0x00ffffff;
                break;

            case 0xfc:
                m_valueresponse = (u32)((m_value0 * m_kov_fe_value) >> 6);
                break;

            case 0xfe:
                m_valueresponse = 0x880000;
                m_kov_fe_value = m_value0;
                break;

            default:
                m_valueresponse = 0x880000;
                break;
            }
        }

        public void init_kov()
        {
            memory_region region = memregion("maincpu");
            if (region == null || region.base_() == null || region.bytes() < 0x500000)
                return;

            PgmCrypt.KovDecrypt(region.base_(), 0x100000, 0x400000);
            ResetKovProtection();
        }

        uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            return m_video.op0.screen_update(screen, bitmap, cliprect);
        }

        static uint Expand5(int value) => (uint)((value << 3) | (value >> 2));
    }

    public class pgm : construct_ioport_helper
    {
        const u32 ROM_GROUPWORD = 0x100;
        const u32 ROMREGION_16BIT = 0x100;

        static readonly pgm m_pgm = new pgm();

        static tiny_rom_entry ROM_REGION16_LE(u32 length, string tag, u32 flags)
        {
            return ROM_REGION(length, tag, flags | ROMREGION_16BIT | ROMREGION_LE);
        }

        static tiny_rom_entry ROM_LOAD16_WORD_SWAP(string name, u32 offset, u32 length, string hash)
        {
            return ROMX_LOAD(name, offset, length, hash, ROM_GROUPWORD | ROM_REVERSE);
        }

        static readonly tiny_rom_entry [] rom_pgm =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),

            ROM_REGION(0x280000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),

            ROM_REGION(0x200000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),

            ROM_REGION16_LE(0x1000000, "igs023:sprcol", ROMREGION_ERASEFF),
            ROM_REGION16_LE(0x1000000, "igs023:sprmask", ROMREGION_ERASEFF),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_kov =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),
            ROM_LOAD16_WORD_SWAP("p0600.117", 0x100000, 0x400000, CRC("c4d19fe6") + SHA1("14ef31539bfbc665e76c9703ee01b12228344052")),

            ROM_REGION(0x4000, "prot", ROMREGION_ERASEFF),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t0600.rom", 0x180000, 0x800000, CRC("4acc1ad6") + SHA1("0668dbd5e856c2406910c6b7382548b37c631780")),

            ROM_REGION16_LE(0x2000000, "igs023:sprcol", 0),
            ROM_LOAD("a0600.rom", 0x0000000, 0x0800000, CRC("d8167834") + SHA1("fa55a99629d03b2ea253392352f70d2c8639a991")),
            ROM_LOAD("a0601.rom", 0x0800000, 0x0800000, CRC("ff7a4373") + SHA1("7def9fca7513ad5a117da230bebd2e3c78679041")),
            ROM_LOAD("a0602.rom", 0x1000000, 0x0800000, CRC("e7a32959") + SHA1("3d0ed684dc5b269238890836b2ce7ef46aa5265b")),
            ROM_LOAD("a0603.rom", 0x1800000, 0x0400000, CRC("ec31abda") + SHA1("ee526655369bae63b0ef0730e9768b765c9950fc")),

            ROM_REGION16_LE(0x1000000, "igs023:sprmask", 0),
            ROM_LOAD("b0600.rom", 0x0000000, 0x0800000, CRC("7d3cd059") + SHA1("00cf994b63337e0e4ebe96453daf45f24192af1c")),
            ROM_LOAD("b0601.rom", 0x0800000, 0x0400000, CRC("a0bb1c2f") + SHA1("0542348c6e27779e0a98de16f04f9c18158f2b28")),

            ROM_REGION(0x800000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),
            ROM_LOAD("m0600.rom", 0x400000, 0x400000, CRC("3ada4fd6") + SHA1("4c87adb25d31cbd41f04fbffe31f7bc37173da76")),

            ROM_END,
        };

        static void pgm_state_pgm(machine_config config, device_t device) { ((pgm_state)device).pgm(config); }
        static void pgm_state_init_kov(device_t owner) { ((pgm_state)owner).init_kov(); }
        static device_t device_creator_pgm_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new pgm_state(mconfig, (device_type)type, tag); }

        void construct_ioport_pgm(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("P1");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(1);
            PORT_BIT(0x20, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(1);
            PORT_BIT(0x40, IP_ACTIVE_LOW, IPT_BUTTON3); PORT_PLAYER(1);
            PORT_BIT(0x80, IP_ACTIVE_LOW, IPT_START1);

            PORT_START("P2");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(2);
            PORT_BIT(0x20, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(2);
            PORT_BIT(0x40, IP_ACTIVE_LOW, IPT_BUTTON3); PORT_PLAYER(2);
            PORT_BIT(0x80, IP_ACTIVE_LOW, IPT_START2);

            PORT_START("SYSTEM");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_COIN1);
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_COIN2);
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_SERVICE1);
            PORT_BIT(0xf8, IP_ACTIVE_LOW, IPT_UNUSED);

            PORT_START("P1P2");
            PORT_BIT(0x0001, IP_ACTIVE_LOW, IPT_START1);
            PORT_BIT(0x0002, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0004, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0008, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0010, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x0020, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(1);
            PORT_BIT(0x0040, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(1);
            PORT_BIT(0x0080, IP_ACTIVE_LOW, IPT_BUTTON3); PORT_PLAYER(1);
            PORT_BIT(0x0100, IP_ACTIVE_LOW, IPT_START2);
            PORT_BIT(0x0200, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x0400, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x0800, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x1000, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x2000, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(2);
            PORT_BIT(0x4000, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(2);
            PORT_BIT(0x8000, IP_ACTIVE_LOW, IPT_BUTTON3); PORT_PLAYER(2);

            PORT_START("P3P4");
            PORT_BIT(0xffff, IP_ACTIVE_LOW, IPT_UNUSED);

            PORT_START("Service");
            PORT_BIT(0x0001, IP_ACTIVE_LOW, IPT_COIN1);
            PORT_BIT(0x0002, IP_ACTIVE_LOW, IPT_COIN2);
            PORT_BIT(0x0004, IP_ACTIVE_LOW, IPT_COIN3);
            PORT_BIT(0x0008, IP_ACTIVE_LOW, IPT_COIN4);
            PORT_BIT(0x0010, IP_ACTIVE_LOW, IPT_UNUSED);
            PORT_BIT(0x0020, IP_ACTIVE_LOW, IPT_SERVICE1);
            PORT_BIT(0x0040, IP_ACTIVE_LOW, IPT_UNUSED);
            PORT_BIT(0x0080, IP_ACTIVE_LOW, IPT_UNUSED);
            PORT_BIT(0x0100, IP_ACTIVE_LOW, IPT_BUTTON4); PORT_PLAYER(1);
            PORT_BIT(0x0200, IP_ACTIVE_LOW, IPT_BUTTON4); PORT_PLAYER(2);
            PORT_BIT(0xfc00, IP_ACTIVE_LOW, IPT_UNUSED);

            PORT_START("DSW");
            PORT_BIT(0xffff, IP_ACTIVE_LOW, IPT_UNUSED);
        }

        public static readonly game_driver driver_pgm = GAME(device_creator_pgm_state, rom_pgm, "1997", "pgm", "0", pgm_state_pgm, m_pgm.construct_ioport_pgm, driver_device.empty_init, ROT0, "IGS", "PGM (Polygame Master) System BIOS", MACHINE_IS_BIOS_ROOT | MACHINE_IS_SKELETON);
        public static readonly game_driver driver_kov = GAME(device_creator_pgm_state, rom_kov, "1999", "kov", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_kov, ROT0, "IGS", "Knights of Valour / Sanguo Zhan Ji / Sangoku Senki (ver. 117, Hong Kong)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND | MACHINE_UNEMULATED_PROTECTION);
    }

    sealed class PgmV3021Rtc
    {
        readonly u8 [] m_ram = new u8[16];
        bool m_cs;
        int m_io;
        int m_addr;
        int m_data;
        int m_cnt;
        int m_mode;

        public void Reset()
        {
            m_cs = false;
            m_io = 0;
            m_addr = 0;
            m_data = 0;
            m_cnt = 0;
            m_mode = 0;
            LoadHostClock(clearStatus: true);
        }

        public void RegisterSaveState(device_t owner, string prefix)
        {
            save_manager save = owner.machine().save();
            save.save_item(owner, owner.name(), owner.tag(), 0, m_ram, $"{prefix}.m_ram");
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_cs", () => m_cs, value => m_cs = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_io", () => m_io, value => m_io = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_addr", () => m_addr, value => m_addr = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_data", () => m_data, value => m_data = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_cnt", () => m_cnt, value => m_cnt = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_mode", () => m_mode, value => m_mode = value);
        }

        public u8 Read()
        {
            u8 result = (u8)(m_data & 1);
            PulseCs();
            return result;
        }

        public void Write(u8 data)
        {
            m_io = data & 1;
            PulseCs();
        }

        void PulseCs()
        {
            SetCs(true);
            SetCs(false);
        }

        void SetCs(bool state)
        {
            if (m_cs == state)
                return;

            m_cs = state;
            if (!m_cs)
                return;

            if (m_mode == 0)
            {
                m_addr = ((m_addr >> 1) | (m_io << 3)) & 0x0f;
                if (++m_cnt < 4)
                    return;

                if (m_addr == 0x0e)
                {
                    if ((m_ram[0] & 0x10) == 0)
                        m_ram[1] = 0;
                }
                else if (m_addr == 0x0f)
                {
                    LoadHostClock(clearStatus: false);
                }
                else
                {
                    m_data = m_ram[m_addr & 0x0f];
                    m_mode = 1;
                }

                m_cnt = 0;
                return;
            }

            m_data = ((m_data >> 1) | (m_io << 7)) & 0xff;
            if (++m_cnt >= 8)
            {
                if (m_addr != 1 && m_addr <= 9)
                    m_ram[m_addr & 0x0f] = (u8)m_data;

                m_mode = 0;
                m_cnt = 0;
            }
        }

        void LoadHostClock(bool clearStatus)
        {
            DateTime now = DateTime.Now;
            u8 sec = ToBcd(now.Second);
            u8 min = ToBcd(now.Minute);
            u8 hour = ToBcd(now.Hour);
            u8 day = ToBcd(now.Day);
            u8 month = ToBcd(now.Month);
            u8 year = ToBcd(now.Year % 100);
            u8 weekday = ToBcd(((int)now.DayOfWeek + 1));
            u8 week = ToBcd((now.DayOfYear / 7) + 1);

            m_ram[1] = 0;
            if (!clearStatus)
            {
                if (m_ram[2] != sec) m_ram[1] |= 1 << 0;
                if (m_ram[3] != min) m_ram[1] |= 1 << 1;
                if (m_ram[4] != hour) m_ram[1] |= 1 << 2;
                if (m_ram[5] != day) m_ram[1] |= 1 << 3;
                if (m_ram[6] != month) m_ram[1] |= 1 << 4;
                if (m_ram[7] != year) m_ram[1] |= 1 << 5;
                if (m_ram[8] != weekday) m_ram[1] |= 1 << 6;
                if (m_ram[9] != week) m_ram[1] |= 1 << 7;
            }

            m_ram[2] = sec;
            m_ram[3] = min;
            m_ram[4] = hour;
            m_ram[5] = day;
            m_ram[6] = month;
            m_ram[7] = year;
            m_ram[8] = weekday;
            m_ram[9] = week;
        }

        static u8 ToBcd(int value) => (u8)(((value / 10) << 4) | (value % 10));
    }

    static class PgmCrypt
    {
        static readonly u8 [] KovTab =
        {
            0x17, 0x1c, 0xe3, 0x02, 0x62, 0x59, 0x97, 0x4a, 0x67, 0x4d, 0x1f, 0x11, 0x76, 0x64, 0xc1, 0xe1,
            0xd2, 0x41, 0x9f, 0xfd, 0xfa, 0x04, 0xfe, 0xab, 0x89, 0xeb, 0xc0, 0xf5, 0xac, 0x2b, 0x64, 0x22,
            0x90, 0x7d, 0x88, 0xc5, 0x8c, 0xe0, 0xd9, 0x70, 0x3c, 0xf4, 0x7d, 0x31, 0x1c, 0xca, 0xe2, 0xf1,
            0x31, 0x82, 0x86, 0xb1, 0x55, 0x95, 0x77, 0x01, 0x77, 0x3b, 0xab, 0xe6, 0x88, 0xef, 0x77, 0x11,
            0x56, 0x01, 0xac, 0x55, 0xf7, 0x6d, 0x9b, 0x6d, 0x92, 0x14, 0x23, 0xae, 0x4b, 0x80, 0xae, 0x6a,
            0x43, 0xcc, 0x35, 0xfe, 0xa1, 0x0d, 0xb3, 0x21, 0x4e, 0x4c, 0x99, 0x80, 0xc2, 0x3d, 0xce, 0x46,
            0x9b, 0x5d, 0x68, 0x75, 0xfe, 0x1e, 0x25, 0x41, 0x24, 0xa0, 0x79, 0xfd, 0xb5, 0x67, 0x93, 0x07,
            0x3a, 0x78, 0x24, 0x64, 0xe1, 0xa3, 0x62, 0x75, 0x38, 0x65, 0x8a, 0xbf, 0xf9, 0x7c, 0x00, 0xa0,
            0x6d, 0xdb, 0x1f, 0x80, 0x37, 0x37, 0x8e, 0x97, 0x1a, 0x45, 0x61, 0x0e, 0x10, 0x24, 0x8a, 0x27,
            0xf2, 0x44, 0x91, 0x3e, 0x62, 0x44, 0xc5, 0x55, 0xe6, 0x8e, 0x5a, 0x25, 0x8a, 0x90, 0x25, 0x74,
            0xa0, 0x95, 0x33, 0xf7, 0x51, 0xce, 0xe4, 0xa0, 0x13, 0xcf, 0x33, 0x1e, 0x59, 0x5b, 0xec, 0x42,
            0xc5, 0xb8, 0xe4, 0xc5, 0x71, 0x38, 0xc5, 0x6b, 0x8d, 0x1d, 0x84, 0xf8, 0x4e, 0x21, 0x6d, 0xdc,
            0x2c, 0xf1, 0xae, 0xad, 0x19, 0xc5, 0xed, 0x8e, 0x36, 0xb5, 0x81, 0x94, 0xfe, 0x62, 0x3a, 0xe8,
            0xc9, 0x95, 0x84, 0xbd, 0x65, 0x15, 0x16, 0x15, 0xd2, 0xe7, 0x16, 0xd7, 0x9c, 0xd3, 0xd2, 0x66,
            0xf6, 0x46, 0xe3, 0x32, 0x62, 0x51, 0x86, 0x4a, 0x67, 0xcc, 0x4d, 0xea, 0x37, 0x45, 0xd5, 0xa6,
            0x80, 0xe6, 0xba, 0xb3, 0x08, 0xd8, 0x30, 0x5b, 0x5f, 0xf2, 0x5a, 0xfb, 0x63, 0xb0, 0xa4, 0x41
        };

        public static void KovDecrypt(MemoryU8 rom, int offset, int length)
        {
            int words = Math.Min(length, rom.Count - offset) / 2;
            for (int i = 0; i < words; i++)
            {
                int byteOffset = offset + i * 2;
                u16 x = ReadLeWord(rom, byteOffset);

                if ((i & 0x040480) != 0x000080) x ^= 0x0001;
                if ((i & 0x004008) == 0x004008) x ^= 0x0002;
                if ((i & 0x000030) == 0x000010 && (i & 0x180000) != 0x080000) x ^= 0x0004;
                if ((i & 0x000242) != 0x000042) x ^= 0x0008;
                if ((i & 0x008100) == 0x008000) x ^= 0x0010;
                if ((i & 0x022004) != 0x000004) x ^= 0x0020;
                if ((i & 0x011800) != 0x010000) x ^= 0x0040;
                if ((i & 0x004820) == 0x004820) x ^= 0x0080;
                x ^= (u16)(KovTab[i & 0xff] << 8);

                WriteLeWord(rom, byteOffset, x);
            }
        }

        static u16 ReadLeWord(MemoryU8 rom, int offset)
        {
            return (u16)(rom[offset] | (rom[offset + 1] << 8));
        }

        static void WriteLeWord(MemoryU8 rom, int offset, u16 value)
        {
            rom[offset] = (u8)value;
            rom[offset + 1] = (u8)(value >> 8);
        }
    }
}
