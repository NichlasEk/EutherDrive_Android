// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Ported from MAME pgm.cpp

using System;
using System.Diagnostics;

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
    class pgm_state : driver_device, IPgmArm7Bus
    {
        const int ScreenWidth = 448;
        const int ScreenHeight = 224;
        const uint MainRamStart = 0x800000;
        const uint MainRamEnd = 0x81ffff;
        const int MainCpuClockHz = 20_000_000;
        const int SvgArm7ClockHz = 22_000_000;
        const int Kov2Arm7ClockHz = 20_000_000;
        // PGM screen timing is 10 MHz / (640 * 264).
        const int SvgArm7CyclesPerFrame = 371_712;
        const int Kov2Arm7CyclesPerFrame = 337_920;
        const int Arm7SavestateCookie = 0x41524d37;
        static readonly bool TracePgmSound = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM_SOUND_TRACE") == "1";
        static readonly bool TracePgmProfile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM_PROFILE") == "1";
        static readonly int[] ArmWait0 = new int[16];
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
        readonly PgmArm7Core m_arm7;
        readonly byte [] m_armInternalRom = new byte[0x4000];
        readonly byte [] m_armRam = new byte[0x40000];
        readonly byte [] m_armRam2 = new byte[0x400];
        readonly byte [] m_armAuxRam = new byte[0x400];
        readonly byte [] m_kov2ArmSharedRam = new byte[0x10000];
        readonly u32 [] m_kov2XorTable = new u32[0x100];
        readonly byte [][] m_svg_shareram = { new byte[0x20000], new byte[0x20000] };
        MemoryU8 m_mainrom;
        MemoryU8 m_armExternalRom;
        int m_mainromBytes;
        int m_armExternalRomBytes;
        u16 m_value0;
        u16 m_value1;
        u16 m_valuekey;
        u16 m_ddp3lastcommand;
        u32 m_valueresponse;
        int m_curslots;
        u16 m_kov_c0_value;
        u16 m_kov_cb_value;
        u16 m_kov_fe_value;
        u16 m_asic3_reg;
        readonly u16 [] m_asic3_latch = new u16[3];
        u16 m_asic3_x;
        u16 m_asic3_hilo;
        u16 m_asic3_hold;
        int m_asic3Region;
        bool m_useCaveType1Sim;
        bool m_useDdp3Type1Sim;
        bool m_useSvgArmType3;
        bool m_useTheGladArmType3;
        bool m_useKov2ArmType2;
        int m_svg_ram_sel;
        u32 m_svg_latchdata_68k_w;
        u32 m_svg_latchdata_arm_w;
        u32 m_kov2_latchdata_68k_w;
        u32 m_kov2_latchdata_arm_w;
        uint m_armLastPrefetchedPc;
        int m_arm7SavestateCookie;
        int m_pgmFrameCounter;
        int m_traceArmRamSelWrites;
        int m_traceArmSharedReads;
        int m_traceArmSharedWrites;
        int m_traceArmLatchReads;
        int m_traceArmLatchWrites;
        int m_traceArmSpeedupReads;
        int m_traceRtcAccesses;
        bool m_traceDmnfrntErrorDumped;
        int m_simregion;
        int m_trace_z80_ram_writes;
        int m_trace_sound_writes;
        long m_profileLastTicks = Stopwatch.GetTimestamp();
        long m_profileFrames;
        long m_profileArmTicks;
        long m_profileTraceTicks;
        long m_profileSpriteDmaTicks;
        long m_profileIrqTicks;

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
            m_arm7 = new PgmArm7Core(this);
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
            map.op(0x400000, 0x400005).rw((read16_delegate)cave_type1_sim_r, (write16_delegate)cave_type1_sim_w);
            map.op(0x4f0000, 0x4f003f).r((read16_delegate)kov_protram_r);
            map.op(0x500000, 0x50ffff).rw((read16_delegate)pgm_500000_r, (write16_delegate)pgm_500000_w);
            map.op(0x5c0000, 0x5c0001).rw((read16_delegate)svg_68k_nmi_r, (write16_delegate)svg_68k_nmi_w);
            map.op(0x5c0300, 0x5c0301).rw((read16_delegate)svg_latch_68k_r, (write16_delegate)svg_latch_68k_w);
            map.op(0x700006, 0x700007).nopw();
            map.op(MainRamStart, MainRamEnd).mirror(0x0e0000).rw((read16_delegate)mainram_r, (write16_delegate)mainram_w);
            map.op(0x900000, 0x907fff).mirror(0x0f8000).rw((read16_delegate)video_ram_r, (write16_delegate)video_ram_w);
            map.op(0xa00000, 0xa013ff).rw((read16_delegate)palette_r, (write16_delegate)palette_w);
            map.op(0xb00000, 0xb0ffff).rw((read16_delegate)video_regs_r, (write16_delegate)video_regs_w);
            map.op(0xc00000, 0xc0000f).rw((read16_delegate)sound_rtc_stub_r, (write16_delegate)sound_rtc_stub_w);
            map.op(0xc04000, 0xc0400f).rw((read16_delegate)asic3_r, (write16_delegate)asic3_w);
            map.op(0xc08000, 0xc08007).rw((read16_delegate)input_stub_r, (write16_delegate)input_stub_w);
            map.op(0xc10000, 0xc1ffff).rw((read16_delegate)z80_ram_68k_r, (write16_delegate)z80_ram_68k_w);
            map.op(0xd00000, 0xd0ffff).rw((read16_delegate)kov2_arm7_ram_r, (write16_delegate)kov2_arm7_ram_w);
            map.op(0xd10000, 0xd10001).rw((read16_delegate)kov2_arm7_latch_68k_r, (write16_delegate)kov2_arm7_latch_68k_w);
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
            memory_region user1 = memregion("user1");
            if (user1 != null)
            {
                m_armExternalRom = user1.base_();
                m_armExternalRomBytes = (int)Math.Min(user1.bytes(), int.MaxValue);
            }

            m_maincpu.op0.set_fast_memory_handlers(
                Fast68kReadByte,
                Fast68kReadWord,
                Fast68kWriteByte,
                Fast68kWriteWord,
                Fast68kReadLong,
                Fast68kWriteLong);
            m_maincpu.op0.set_idle_loop_consumed_handler(MainCpuIdleLoopConsumed);
            m_soundcpu.op0.set_fast_memory_handlers(
                FastZ80ReadByte,
                FastZ80WriteByte);

            save_item(NAME(new { m_mainram }));
            save_item(NAME(new { m_z80ram }));
            save_item(NAME(new { m_paletteram }));
            save_item(NAME(new { m_kov_slots }));
            save_item(NAME(new { m_armInternalRom }));
            save_item(NAME(new { m_armRam }));
            save_item(NAME(new { m_armRam2 }));
            save_item(NAME(new { m_armAuxRam }));
            save_item(NAME(new { m_kov2ArmSharedRam }));
            save_item(NAME(new { m_kov2XorTable }));
            save_item(NAME(new { m_svg_shareram }));
            SaveStateRef(nameof(m_value0), () => m_value0, value => m_value0 = value);
            SaveStateRef(nameof(m_value1), () => m_value1, value => m_value1 = value);
            SaveStateRef(nameof(m_valuekey), () => m_valuekey, value => m_valuekey = value);
            SaveStateRef(nameof(m_ddp3lastcommand), () => m_ddp3lastcommand, value => m_ddp3lastcommand = value);
            SaveStateRef(nameof(m_valueresponse), () => m_valueresponse, value => m_valueresponse = value);
            SaveStateRef(nameof(m_curslots), () => m_curslots, value => m_curslots = value);
            SaveStateRef(nameof(m_kov_c0_value), () => m_kov_c0_value, value => m_kov_c0_value = value);
            SaveStateRef(nameof(m_kov_cb_value), () => m_kov_cb_value, value => m_kov_cb_value = value);
            SaveStateRef(nameof(m_kov_fe_value), () => m_kov_fe_value, value => m_kov_fe_value = value);
            SaveStateRef(nameof(m_asic3_reg), () => m_asic3_reg, value => m_asic3_reg = value);
            save_item(NAME(new { m_asic3_latch }));
            SaveStateRef(nameof(m_asic3_x), () => m_asic3_x, value => m_asic3_x = value);
            SaveStateRef(nameof(m_asic3_hilo), () => m_asic3_hilo, value => m_asic3_hilo = value);
            SaveStateRef(nameof(m_asic3_hold), () => m_asic3_hold, value => m_asic3_hold = value);
            SaveStateRef(nameof(m_asic3Region), () => m_asic3Region, value => m_asic3Region = value);
            SaveStateRef(nameof(m_useCaveType1Sim), () => m_useCaveType1Sim, value => m_useCaveType1Sim = value);
            SaveStateRef(nameof(m_useDdp3Type1Sim), () => m_useDdp3Type1Sim, value => m_useDdp3Type1Sim = value);
            SaveStateRef(nameof(m_useSvgArmType3), () => m_useSvgArmType3, value => m_useSvgArmType3 = value);
            SaveStateRef(nameof(m_useTheGladArmType3), () => m_useTheGladArmType3, value => m_useTheGladArmType3 = value);
            SaveStateRef(nameof(m_useKov2ArmType2), () => m_useKov2ArmType2, value => m_useKov2ArmType2 = value);
            SaveStateRef(nameof(m_svg_ram_sel), () => m_svg_ram_sel, value => m_svg_ram_sel = value);
            SaveStateRef(nameof(m_svg_latchdata_68k_w), () => m_svg_latchdata_68k_w, value => m_svg_latchdata_68k_w = value);
            SaveStateRef(nameof(m_svg_latchdata_arm_w), () => m_svg_latchdata_arm_w, value => m_svg_latchdata_arm_w = value);
            SaveStateRef(nameof(m_kov2_latchdata_68k_w), () => m_kov2_latchdata_68k_w, value => m_kov2_latchdata_68k_w = value);
            SaveStateRef(nameof(m_kov2_latchdata_arm_w), () => m_kov2_latchdata_arm_w, value => m_kov2_latchdata_arm_w = value);
            SaveStateRef(nameof(m_armLastPrefetchedPc), () => m_armLastPrefetchedPc, value => m_armLastPrefetchedPc = value);
            SaveStateRef(nameof(m_arm7SavestateCookie), () => m_arm7SavestateCookie, value => m_arm7SavestateCookie = value);
            SaveStateRef(nameof(m_pgmFrameCounter), () => m_pgmFrameCounter, value => m_pgmFrameCounter = value);
            SaveStateRef(nameof(m_simregion), () => m_simregion, value => m_simregion = value);
            m_arm7.RegisterSaveState(this, "m_arm7");
            m_rtc.RegisterSaveState(this, "m_rtc");
            machine().save().register_presave(PreparePgmSaveState);
            machine().save().register_postload(PostloadPgmSaveState);
        }

        protected override void machine_reset()
        {
            Array.Clear(m_mainram, 0, m_mainram.Length);
            Array.Clear(m_z80ram, 0, m_z80ram.Length);
            Array.Clear(m_paletteram, 0, m_paletteram.Length);
            Array.Clear(m_armRam, 0, m_armRam.Length);
            Array.Clear(m_armRam2, 0, m_armRam2.Length);
            Array.Clear(m_armAuxRam, 0, m_armAuxRam.Length);
            Array.Clear(m_kov2ArmSharedRam, 0, m_kov2ArmSharedRam.Length);
            Array.Clear(m_svg_shareram[0], 0, m_svg_shareram[0].Length);
            Array.Clear(m_svg_shareram[1], 0, m_svg_shareram[1].Length);
            m_trace_z80_ram_writes = 0;
            m_trace_sound_writes = 0;
            m_traceDmnfrntErrorDumped = false;
            m_arm7SavestateCookie = 0;
            m_pgmFrameCounter = 0;
            m_rtc.Reset();
            ResetKovProtection();
            ResetAsic3Protection();
            ResetSvgArmType3Runtime();
            ResetKov2ArmType2Runtime();
            m_maincpu.op0.reset_from_bus();
            m_soundcpu.op0.set_input_line(INPUT_LINE_HALT, ASSERT_LINE);
        }

        void SaveStateRef<T>(string itemName, Func<T> getter, Action<T> setter)
        {
            machine().save().save_item_ref(this, name(), tag(), 0, itemName, getter, setter);
        }

        void PreparePgmSaveState()
        {
            m_arm7SavestateCookie = Arm7SavestateCookie;
        }

        void PostloadPgmSaveState()
        {
            if (!m_useSvgArmType3)
                return;

            if (m_arm7SavestateCookie == Arm7SavestateCookie)
                return;

            // Older PGM savestates did not contain the local ARM7 core state. Avoid
            // a long synchronous catch-up from the wrong ARM timeline after load.
            long targetCycles = GetArmTargetCyclesFromMainTime();
            if (targetCycles > m_arm7.Cycles)
                m_arm7.Cycles = targetCycles;
            m_arm7SavestateCookie = Arm7SavestateCookie;
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
                if (m_useSvgArmType3 && !m_useTheGladArmType3 && address == 0x80a03c)
                {
                    value = dmnfrnt_main_speedup_r();
                    return true;
                }
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

        bool Fast68kReadLong(u32 address, out u32 value)
        {
            address &= 0x00ff_ffff;
            if ((address & 1) != 0)
            {
                value = 0xffff_ffff;
                return false;
            }

            if (IsFastRomWordAddress(address) && IsFastRomWordAddress((address + 2) & 0x00ff_ffff))
            {
                value = ((u32)m_mainrom[(int)(address + 1)] << 24)
                    | ((u32)m_mainrom[(int)address] << 16)
                    | ((u32)m_mainrom[(int)(address + 3)] << 8)
                    | m_mainrom[(int)(address + 2)];
                return true;
            }
            if (IsFastMainRamAddress(address) && IsFastMainRamAddress((address + 3) & 0x00ff_ffff))
            {
                if (m_useSvgArmType3 && !m_useTheGladArmType3 && address == 0x80a03c)
                {
                    value = 0xffff_ffff;
                    return false;
                }

                uint byteOffset = address & (uint)(m_mainram.Length - 1);
                value = ((u32)m_mainram[byteOffset] << 24)
                    | ((u32)m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)] << 16)
                    | ((u32)m_mainram[(byteOffset + 2) & (m_mainram.Length - 1)] << 8)
                    | m_mainram[(byteOffset + 3) & (m_mainram.Length - 1)];
                return true;
            }
            if (IsFastZ80RamAddress(address) && IsFastZ80RamAddress((address + 3) & 0x00ff_ffff))
            {
                uint byteOffset = address & 0xffff;
                value = ((u32)m_z80ram[byteOffset] << 24)
                    | ((u32)m_z80ram[(byteOffset + 1) & 0xffff] << 16)
                    | ((u32)m_z80ram[(byteOffset + 2) & 0xffff] << 8)
                    | m_z80ram[(byteOffset + 3) & 0xffff];
                return true;
            }

            value = 0xffff_ffff;
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

        bool Fast68kWriteLong(u32 address, u32 value)
        {
            address &= 0x00ff_ffff;
            if ((address & 1) != 0)
                return false;

            if (IsFastMainRamAddress(address) && IsFastMainRamAddress((address + 3) & 0x00ff_ffff))
            {
                uint byteOffset = address & (uint)(m_mainram.Length - 1);
                m_mainram[byteOffset] = (u8)(value >> 24);
                m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)] = (u8)(value >> 16);
                m_mainram[(byteOffset + 2) & (m_mainram.Length - 1)] = (u8)(value >> 8);
                m_mainram[(byteOffset + 3) & (m_mainram.Length - 1)] = (u8)value;
                return true;
            }
            if (IsFastZ80RamAddress(address) && IsFastZ80RamAddress((address + 3) & 0x00ff_ffff))
            {
                uint byteOffset = address & 0xffff;
                m_z80ram[byteOffset] = (u8)(value >> 24);
                TraceZ80RamWrite(byteOffset, (u8)(value >> 24));
                m_z80ram[(byteOffset + 1) & 0xffff] = (u8)(value >> 16);
                TraceZ80RamWrite((byteOffset + 1) & 0xffff, (u8)(value >> 16));
                m_z80ram[(byteOffset + 2) & 0xffff] = (u8)(value >> 8);
                TraceZ80RamWrite((byteOffset + 2) & 0xffff, (u8)(value >> 8));
                m_z80ram[(byteOffset + 3) & 0xffff] = (u8)value;
                TraceZ80RamWrite((byteOffset + 3) & 0xffff, (u8)value);
                return true;
            }

            return false;
        }

        bool IsFastRomByteAddress(u32 address)
        {
            if (m_mainrom == null || address >= 0x600000 || address >= (uint)m_mainromBytes)
                return false;

            if (m_useSvgArmType3 && ((address >= 0x500000 && address <= 0x50ffff) || address == 0x5c0000 || address == 0x5c0001 || address == 0x5c0300 || address == 0x5c0301))
                return false;
            if (m_useKov2ArmType2 && ((address >= 0xd00000 && address <= 0xd0ffff) || address == 0xd10000 || address == 0xd10001))
                return false;

            return (address < 0x4f0000 || address > 0x4f003f)
                && (!m_useCaveType1Sim || address < 0x400000 || address > 0x400005)
                && (address < 0x500000 || address > 0x500005);
        }

        bool IsFastRomWordAddress(u32 address)
        {
            if (m_mainrom == null || address >= 0x600000 || address + 1 >= (uint)m_mainromBytes)
                return false;

            if (m_useSvgArmType3 && ((address >= 0x500000 && address <= 0x50fffe) || address == 0x5c0000 || address == 0x5c0300))
                return false;
            if (m_useKov2ArmType2 && ((address >= 0xd00000 && address <= 0xd0fffe) || address == 0xd10000))
                return false;

            return (address < 0x4f0000 || address > 0x4f003e)
                && (!m_useCaveType1Sim || address < 0x400000 || address > 0x400004)
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
            if (m_useCaveType1Sim && wordAddress >= 0x400000 && wordAddress <= 0x400004)
            {
                value = cave_type1_sim_r(null, (wordAddress - 0x400000) >> 1, memMask);
                return true;
            }
            if (m_useDdp3Type1Sim && wordAddress >= 0x500000 && wordAddress <= 0x500004)
            {
                value = pgm_500000_r(null, (wordAddress - 0x500000) >> 1, memMask);
                return true;
            }
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
            if (m_useSvgArmType3 && wordAddress >= 0x500000 && wordAddress <= 0x50fffe)
            {
                value = svg_m68k_ram_r(null, (wordAddress - 0x500000) >> 1, memMask);
                return true;
            }
            if (m_useSvgArmType3 && wordAddress == 0x5c0000)
            {
                value = svg_68k_nmi_r(null, 0, memMask);
                return true;
            }
            if (m_useSvgArmType3 && wordAddress == 0x5c0300)
            {
                value = svg_latch_68k_r(null, 0, memMask);
                return true;
            }
            if (wordAddress >= 0xc04000 && wordAddress <= 0xc0400e)
            {
                value = asic3_r(null, (wordAddress - 0xc04000) >> 1, memMask);
                return true;
            }
            if (wordAddress >= 0xc08000 && wordAddress <= 0xc08006)
            {
                value = input_stub_r(null, (wordAddress - 0xc08000) >> 1, memMask);
                return true;
            }
            if (m_useKov2ArmType2 && wordAddress >= 0xd00000 && wordAddress <= 0xd0fffe)
            {
                value = kov2_arm7_ram_r(null, (wordAddress - 0xd00000) >> 1, memMask);
                return true;
            }
            if (m_useKov2ArmType2 && wordAddress == 0xd10000)
            {
                value = kov2_arm7_latch_68k_r(null, 0, memMask);
                return true;
            }

            value = 0xffff;
            return false;
        }

        bool TryFast68kMappedWrite(u32 address, u16 data, u16 memMask)
        {
            u32 wordAddress = address & 0x00ff_fffe;
            if (m_useCaveType1Sim && wordAddress >= 0x400000 && wordAddress <= 0x400004)
            {
                cave_type1_sim_w(null, (wordAddress - 0x400000) >> 1, data, memMask);
                return true;
            }
            if (m_useDdp3Type1Sim && wordAddress >= 0x500000 && wordAddress <= 0x500004)
            {
                pgm_500000_w(null, (wordAddress - 0x500000) >> 1, data, memMask);
                return true;
            }
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
            if (m_useSvgArmType3 && wordAddress >= 0x500000 && wordAddress <= 0x50fffe)
            {
                svg_m68k_ram_w(null, (wordAddress - 0x500000) >> 1, data, memMask);
                return true;
            }
            if (m_useSvgArmType3 && wordAddress == 0x5c0000)
            {
                svg_68k_nmi_w(null, 0, data, memMask);
                return true;
            }
            if (m_useSvgArmType3 && wordAddress == 0x5c0300)
            {
                svg_latch_68k_w(null, 0, data, memMask);
                return true;
            }
            if (wordAddress >= 0xc04000 && wordAddress <= 0xc0400e)
            {
                asic3_w(null, (wordAddress - 0xc04000) >> 1, data, memMask);
                return true;
            }
            if (wordAddress >= 0xc08000 && wordAddress <= 0xc08006)
            {
                input_stub_w(null, (wordAddress - 0xc08000) >> 1, data, memMask);
                return true;
            }
            if (m_useKov2ArmType2 && wordAddress >= 0xd00000 && wordAddress <= 0xd0fffe)
            {
                kov2_arm7_ram_w(null, (wordAddress - 0xd00000) >> 1, data, memMask);
                return true;
            }
            if (m_useKov2ArmType2 && wordAddress == 0xd10000)
            {
                kov2_arm7_latch_68k_w(null, 0, data, memMask);
                return true;
            }

            return false;
        }

        void vblank_irq(int state)
        {
            if (state != 0)
            {
                m_rtc.TickFrame();
                m_pgmFrameCounter++;
                long profileStart = TracePgmProfile ? Stopwatch.GetTimestamp() : 0;
                RunArmFrame();
                if (TracePgmProfile)
                    m_profileArmTicks += Stopwatch.GetTimestamp() - profileStart;
                profileStart = TracePgmProfile ? Stopwatch.GetTimestamp() : 0;
                TraceArmFrame();
                if (TracePgmProfile)
                    m_profileTraceTicks += Stopwatch.GetTimestamp() - profileStart;
                profileStart = TracePgmProfile ? Stopwatch.GetTimestamp() : 0;
                m_video.op0.get_sprites(sprite_ram_word);
                if (TracePgmProfile)
                    m_profileSpriteDmaTicks += Stopwatch.GetTimestamp() - profileStart;
                profileStart = TracePgmProfile ? Stopwatch.GetTimestamp() : 0;
                m_maincpu.op0.set_input_line(6, HOLD_LINE);
                if (TracePgmProfile)
                {
                    m_profileIrqTicks += Stopwatch.GetTimestamp() - profileStart;
                    MaybeReportPgmProfile();
                }
            }
            else
            {
                m_maincpu.op0.set_input_line(4, HOLD_LINE);
            }
        }

        void MaybeReportPgmProfile()
        {
            m_profileFrames++;
            long now = Stopwatch.GetTimestamp();
            long elapsed = now - m_profileLastTicks;
            if (elapsed < Stopwatch.Frequency)
                return;

            double scale = 1000.0 / Stopwatch.Frequency;
            Console.WriteLine(
                $"[PGM-PROFILE] frames={m_profileFrames} arm_ms={m_profileArmTicks * scale:0.0} " +
                $"trace_ms={m_profileTraceTicks * scale:0.0} sprite_dma_ms={m_profileSpriteDmaTicks * scale:0.0} " +
                $"irq_ms={m_profileIrqTicks * scale:0.0} arm_cycles={m_arm7.Cycles} arm_pc=0x{m_arm7.Registers[15]:x8}");
            m_profileLastTicks = now;
            m_profileFrames = 0;
            m_profileArmTicks = 0;
            m_profileTraceTicks = 0;
            m_profileSpriteDmaTicks = 0;
            m_profileIrqTicks = 0;
        }

        void MainCpuIdleLoopConsumed(u32 startPc, u32 cycles)
        {
            if (m_useSvgArmType3 && (startPc == 0x0011a8 || startPc == 0x0010e2c6 || startPc == 0x0010e398))
                SyncArmToMainTime();
            else if (m_useKov2ArmType2 && (startPc == 0x00106868 || startPc == 0x00106884))
                SyncArmToMainTime();
        }

        u16 mainram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            if (m_useSvgArmType3 && !m_useTheGladArmType3 && ((offset << 1) & 0x0fffff) == 0x00a03c)
                return dmnfrnt_main_speedup_r();

            uint byteOffset = (offset << 1) & (uint)(m_mainram.Length - 1);
            return (u16)((m_mainram[byteOffset] << 8) | m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)]);
        }

        u16 dmnfrnt_main_speedup_r()
        {
            uint byteOffset = 0xa03c & (uint)(m_mainram.Length - 1);
            u16 data = (u16)((m_mainram[byteOffset] << 8) | m_mainram[(byteOffset + 1) & (m_mainram.Length - 1)]);
            uint pc = m_maincpu.op0.Pc;
            if (pc == 0x10193a || pc == 0x1019a4)
            {
                if (TracePgmArmEnabled())
                    Console.Error.WriteLine($"[PGM-ARM] main speedup pc=0x{pc:x6} data=0x{data:x4} arm=0x{m_arm7.Registers[15]:x8} crash={m_arm7.CrashDetected}");
                SyncArmToMainTime();
                m_maincpu.op0.execute().spin_until_interrupt();
            }
            return data;
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

        u16 pgm_500000_r(address_space space, offs_t offset, u16 mem_mask)
        {
            if (m_useSvgArmType3)
                return svg_m68k_ram_r(space, offset, mem_mask);

            if ((m_useDdp3Type1Sim || !m_useCaveType1Sim) && offset <= 2)
                return kov_sim_r(space, offset, mem_mask);

            return 0xffff;
        }

        void pgm_500000_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (m_useSvgArmType3)
            {
                svg_m68k_ram_w(space, offset, data, mem_mask);
                return;
            }

            if ((m_useDdp3Type1Sim || !m_useCaveType1Sim) && offset <= 2)
                kov_sim_w(space, offset, data, mem_mask);
        }

        u16 svg_m68k_ram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            SyncArmToMainTime();
            int ramSel = (m_svg_ram_sel & 1) ^ 1;
            uint byteOffset = (offset << 1) & 0x1ffff;
            if (TracePgmArmEnabled() && (offset == 0x158 / 2 || offset == 0xa03c / 2))
                Console.Error.WriteLine($"[PGM-ARM] 68k shared r off=0x{byteOffset:x5} sel={ramSel} val=0x{ReadLe16(m_svg_shareram[ramSel], byteOffset):x4} m68k=0x{m_maincpu.op0.Pc:x6} arm=0x{m_arm7.Registers[15]:x8}");
            return ReadLe16(m_svg_shareram[ramSel], byteOffset);
        }

        void svg_m68k_ram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int ramSel = (m_svg_ram_sel & 1) ^ 1;
            uint byteOffset = (offset << 1) & 0x1ffff;
            CombineLe16(m_svg_shareram[ramSel], byteOffset, data, mem_mask);
        }

        u16 svg_68k_nmi_r(address_space space, offs_t offset, u16 mem_mask) => 0;

        void svg_68k_nmi_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (!m_useSvgArmType3)
                return;

            SyncArmToMainTime();
            m_arm7.PulseFiq();
            RunArmSlice(256);
            m_arm7.ClearFiq();
            SyncArmToMainTime();
        }

        u16 svg_latch_68k_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return (u16)m_svg_latchdata_arm_w;
        }

        void svg_latch_68k_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            COMBINE_DATA(ref m_svg_latchdata_68k_w, data, mem_mask);
            if (TracePgmArmEnabled())
                Console.Error.WriteLine($"[PGM-ARM] 68k latch w data=0x{data:x4} mask=0x{mem_mask:x4} latch=0x{m_svg_latchdata_68k_w:x8} m68k=0x{m_maincpu.op0.Pc:x6}");
            SyncArmToMainTime();
        }

        u16 kov2_arm7_ram_r(address_space space, offs_t offset, u16 mem_mask)
        {
            if (!m_useKov2ArmType2)
                return 0xffff;

            SyncArmToMainTime();
            uint byteOffset = (offset << 1) & 0xffff;
            u16 value = ReadLe16(m_kov2ArmSharedRam, byteOffset);
            if (TracePgmArmEnabled() && m_maincpu.op0.Pc >= 0x13a200 && m_maincpu.op0.Pc <= 0x13a500)
                TraceArmEvent(ref m_traceArmSharedReads, $"[PGM-ARM] kov2 68k shared r off=0x{byteOffset:x4} val=0x{value:x4} mask=0x{mem_mask:x4} m68k=0x{m_maincpu.op0.Pc:x6}", 160);
            return value;
        }

        void kov2_arm7_ram_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (!m_useKov2ArmType2)
                return;

            uint byteOffset = (offset << 1) & 0xffff;
            CombineLe16(m_kov2ArmSharedRam, byteOffset, data, mem_mask);
            if (TracePgmArmEnabled() && m_maincpu.op0.Pc >= 0x13a200 && m_maincpu.op0.Pc <= 0x13a500)
                TraceArmEvent(ref m_traceArmSharedWrites, $"[PGM-ARM] kov2 68k shared w off=0x{byteOffset:x4} data=0x{data:x4} mask=0x{mem_mask:x4} val=0x{ReadLe16(m_kov2ArmSharedRam, byteOffset):x4} m68k=0x{m_maincpu.op0.Pc:x6}", 160);
        }

        u16 kov2_arm7_latch_68k_r(address_space space, offs_t offset, u16 mem_mask)
        {
            if (!m_useKov2ArmType2)
                return 0xffff;

            SyncArmToMainTime();
            u16 value = (u16)m_kov2_latchdata_arm_w;
            if (TracePgmArmEnabled())
                TraceArmEvent(ref m_traceArmLatchReads, $"[PGM-ARM] kov2 68k latch r val=0x{value:x4} mask=0x{mem_mask:x4} m68k=0x{m_maincpu.op0.Pc:x6}", 128);
            return value;
        }

        void kov2_arm7_latch_68k_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (!m_useKov2ArmType2)
                return;

            COMBINE_DATA(ref m_kov2_latchdata_68k_w, data, mem_mask);
            if (TracePgmArmEnabled())
                Console.Error.WriteLine($"[PGM-ARM] kov2 68k latch w data=0x{data:x4} mask=0x{mem_mask:x4} latch=0x{m_kov2_latchdata_68k_w:x8} m68k=0x{m_maincpu.op0.Pc:x6}");
            m_arm7.PulseFiq();
            SyncArmToMainTime();
        }

        u8 read_sound_byte(int address)
        {
            u8 value = address switch
            {
                0x03 => m_soundlatch0.op0.read(),
                0x05 => m_soundlatch1.op0.read(),
                0x07 => m_rtc.Read(),
                0x0d => m_soundlatch2.op0.read(),
                _ => 0xff
            };
            if (address == 0x07)
                TraceArmEvent(ref m_traceRtcAccesses, $"[PGM-RTC] m68k rtc r value=0x{value:x2} pc=0x{m_maincpu.op0.Pc:x6}", 128);
            return value;
        }

        void write_sound_byte(int address, u8 data)
        {
            if (address == 0x07)
                TraceArmEvent(ref m_traceRtcAccesses, $"[PGM-RTC] m68k rtc w data=0x{data:x2} pc=0x{m_maincpu.op0.Pc:x6}", 128);

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

        int[] IPgmArm7Bus.WaitstatesNonseq16 => ArmWait0;
        int[] IPgmArm7Bus.WaitstatesNonseq32 => ArmWait0;
        int[] IPgmArm7Bus.WaitstatesSeq16 => ArmWait0;
        int[] IPgmArm7Bus.WaitstatesSeq32 => ArmWait0;
        uint IPgmArm7Bus.LastPrefetchedPc { get => m_armLastPrefetchedPc; set => m_armLastPrefetchedPc = value; }
        int IPgmArm7Bus.MemoryStall(uint pc, int wait) => wait;
        void IPgmArm7Bus.OnIrqEnable() { }

        bool IPgmArm7Bus.IsExecutableAddress(uint address)
        {
            return address < 0x4000
                || (address >= 0x08000000 && address <= (m_useKov2ArmType2 ? 0x083fffff : 0x087fffff))
                || (address >= 0x10000000 && address <= 0x100003ff)
                || (address >= 0x18000000 && address <= (m_useKov2ArmType2 ? 0x1800ffff : 0x1803ffff))
                || (address >= 0x38000000 && address <= (m_useKov2ArmType2 ? 0x38000003 : 0x3800ffff))
                || (!m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x48000003)
                || (m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x4800ffff)
                || (address >= 0x50000000 && address <= 0x500003ff);
        }

        byte IPgmArm7Bus.Load8(uint address) => ArmRead8(address);
        ushort IPgmArm7Bus.Load16(uint address) => ArmRead16(address);
        uint IPgmArm7Bus.Load32(uint address)
        {
            address &= ~3u;
            return ArmRead32(address);
        }
        ushort IPgmArm7Bus.Fetch16(uint address) => ArmRead16(address & ~1u);
        uint IPgmArm7Bus.Fetch32(uint address) => ArmRead32(address & ~3u);
        void IPgmArm7Bus.Store8(uint address, byte value) => ArmWrite8(address, value);
        void IPgmArm7Bus.Store16(uint address, ushort value) => ArmWrite16(address, value);
        void IPgmArm7Bus.Store32(uint address, uint value) => ArmWrite32(address, value);

        byte ArmRead8(uint address)
        {
            if (address < 0x4000)
                return m_armInternalRom[address & 0x3fff];
            if (address >= 0x08000000 && address <= (m_useKov2ArmType2 ? 0x083fffff : 0x087fffff))
            {
                uint offset = address - 0x08000000;
                if (m_useKov2ArmType2)
                    return (byte)(ReadKov2ExternalArmWord(offset & ~3u) >> (int)((offset & 3) * 8));
                return offset < m_armExternalRomBytes ? m_armExternalRom[(int)offset] : (byte)0xff;
            }
            if (address >= 0x10000000 && address <= 0x100003ff)
                return m_armRam2[(address - 0x10000000) & 0x3ff];
            if (address >= 0x18000000 && address <= (m_useKov2ArmType2 ? 0x1800ffff : 0x1803ffff))
                return m_armRam[(address - 0x18000000) & (m_useKov2ArmType2 ? 0xffffu : 0x3ffffu)];
            if (address >= 0x38000000 && address <= (m_useKov2ArmType2 ? 0x38000003 : 0x3800ffff))
            {
                if (m_useKov2ArmType2 && address <= 0x38000003)
                {
                    uint pc = m_arm7.Registers[15] - 8;
                    uint value = m_kov2_latchdata_68k_w;
                    TraceArmEvent(ref m_traceArmLatchReads, $"[PGM-ARM] kov2 arm latch r8 off={(address - 0x38000000) & 3} val=0x{((value >> (int)(((address - 0x38000000) & 3) * 8)) & 0xff):x2} latch68=0x{value:x8} pc=0x{pc:x8} fiqLine={(m_arm7.FiqLineAsserted ? 1 : 0)} fiqPending={(m_arm7.FiqPending ? 1 : 0)}", 128);
                    m_arm7.ClearFiq();
                    return (byte)(value >> (int)(((address - 0x38000000) & 3) * 8));
                }
                if (m_useKov2ArmType2)
                    return 0xff;

                uint offset = (address - 0x38000000) & 0xffff;
                if ((offset & 0xfff8) == 0x150 || (offset & 0xfff8) == 0xa038)
                    TraceArmEvent(ref m_traceArmSharedReads, $"[PGM-ARM] arm shared r8 bank={m_svg_ram_sel & 1} off=0x{offset:x5} val=0x{m_svg_shareram[m_svg_ram_sel & 1][offset]:x2} pc=0x{m_arm7.Registers[15] - 8:x8}", 160);
                return m_svg_shareram[m_svg_ram_sel & 1][(address - 0x38000000) & 0xffff];
            }
            if (address >= 0x48000000 && address <= 0x48000003)
            {
                if (m_useKov2ArmType2)
                    return m_kov2ArmSharedRam[(address - 0x48000000) & 0xffff];

                uint shift = ((address - 0x48000000) & 3) * 8;
                uint pc = m_arm7.Registers[15] - 8;
                if (m_useSvgArmType3 && m_svg_latchdata_68k_w == 0 && (pc == 0x08000fb4 || pc == 0x08000fb8))
                    m_arm7.Cycles += 500;
                TraceArmEvent(ref m_traceArmLatchReads, $"[PGM-ARM] arm latch r8 off={(address - 0x48000000) & 3} val=0x{((m_svg_latchdata_68k_w >> (int)shift) & 0xff):x2} latch68=0x{m_svg_latchdata_68k_w:x8} pc=0x{pc:x8}", 64);
                return (byte)(m_svg_latchdata_68k_w >> (int)(((address - 0x48000000) & 3) * 8));
            }
            if (m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x4800ffff)
                return m_kov2ArmSharedRam[(address - 0x48000000) & 0xffff];
            if (address >= 0x50000000 && address <= 0x500003ff)
                return m_armAuxRam[(address - 0x50000000) & 0x3ff];
            return 0xff;
        }

        ushort ArmRead16(uint address)
        {
            address &= ~1u;
            if (address < 0x4000)
                return ReadLe16(m_armInternalRom, address & 0x3fff);
            if (address >= 0x08000000 && address <= (m_useKov2ArmType2 ? 0x083fffff : 0x087fffff))
            {
                uint offset = address - 0x08000000;
                if (m_useKov2ArmType2)
                    return (u16)(ReadKov2ExternalArmWord(offset & ~3u) >> (int)((offset & 2) * 8));
                if (offset + 1 < m_armExternalRomBytes)
                    return (u16)(m_armExternalRom[(int)offset] | (m_armExternalRom[(int)(offset + 1)] << 8));
            }
            if (address >= 0x10000000 && address <= 0x100003ff)
                return ReadLe16(m_armRam2, (address - 0x10000000) & 0x3ff);
            if (address >= 0x18000000 && address <= (m_useKov2ArmType2 ? 0x1800ffff : 0x1803ffff))
                return ReadLe16(m_armRam, (address - 0x18000000) & (m_useKov2ArmType2 ? 0xffffu : 0x3ffffu));
            if (address >= 0x38000000 && address <= (m_useKov2ArmType2 ? 0x38000003 : 0x3800ffff))
            {
                if (m_useKov2ArmType2 && address <= 0x38000002)
                {
                    uint pc = m_arm7.Registers[15] - 8;
                    uint value = m_kov2_latchdata_68k_w;
                    TraceArmEvent(ref m_traceArmLatchReads, $"[PGM-ARM] kov2 arm latch r16 off={(address - 0x38000000) & 2} val=0x{((value >> (int)(((address - 0x38000000) & 2) * 8)) & 0xffff):x4} latch68=0x{value:x8} pc=0x{pc:x8} fiqLine={(m_arm7.FiqLineAsserted ? 1 : 0)} fiqPending={(m_arm7.FiqPending ? 1 : 0)}", 128);
                    m_arm7.ClearFiq();
                    return (u16)(value >> (int)(((address - 0x38000000) & 2) * 8));
                }
                if (m_useKov2ArmType2)
                    return 0xffff;
                return ReadLe16(m_svg_shareram[m_svg_ram_sel & 1], (address - 0x38000000) & 0xffff);
            }
            if (address >= 0x48000000 && address <= 0x48000002)
            {
                if (m_useKov2ArmType2)
                    return ReadLe16(m_kov2ArmSharedRam, (address - 0x48000000) & 0xffff);
                return (u16)(m_svg_latchdata_68k_w >> (int)(((address - 0x48000000) & 2) * 8));
            }
            if (m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x4800ffff)
                return ReadLe16(m_kov2ArmSharedRam, (address - 0x48000000) & 0xffff);
            if (address >= 0x50000000 && address <= 0x500003ff)
                return ReadLe16(m_armAuxRam, (address - 0x50000000) & 0x3ff);
            return (ushort)(ArmRead8(address) | (ArmRead8(address + 1) << 8));
        }

        uint ArmRead32(uint address)
        {
            address &= ~3u;
            if (address < 0x4000)
                return ReadLe32(m_armInternalRom, address & 0x3fff);
            if (address >= 0x08000000 && address <= (m_useKov2ArmType2 ? 0x083fffff : 0x087fffff))
            {
                uint offset = address - 0x08000000;
                if (m_useKov2ArmType2)
                    return ReadKov2ExternalArmWord(offset);
                if (offset + 3 < m_armExternalRomBytes)
                    return (u32)(m_armExternalRom[(int)offset]
                        | (m_armExternalRom[(int)(offset + 1)] << 8)
                        | (m_armExternalRom[(int)(offset + 2)] << 16)
                        | (m_armExternalRom[(int)(offset + 3)] << 24));
            }
            if (address >= 0x10000000 && address <= 0x100003ff)
            {
                ApplyTheGladArmRam2Speedup(address);
                return ReadLe32(m_armRam2, (address - 0x10000000) & 0x3ff);
            }
            if (address >= 0x18000000 && address <= (m_useKov2ArmType2 ? 0x1800ffff : 0x1803ffff))
            {
                ApplyDemonFrontArmRamSpeedup(address);
                return ReadLe32(m_armRam, (address - 0x18000000) & (m_useKov2ArmType2 ? 0xffffu : 0x3ffffu));
            }
            if (address >= 0x38000000 && address <= (m_useKov2ArmType2 ? 0x38000003 : 0x3800ffff))
            {
                if (m_useKov2ArmType2 && address == 0x38000000)
                {
                    uint pc = m_arm7.Registers[15] - 8;
                    uint value = m_kov2_latchdata_68k_w;
                    TraceArmEvent(ref m_traceArmLatchReads, $"[PGM-ARM] kov2 arm latch r32 val=0x{value:x8} pc=0x{pc:x8} fiqLine={(m_arm7.FiqLineAsserted ? 1 : 0)} fiqPending={(m_arm7.FiqPending ? 1 : 0)}", 128);
                    m_arm7.ClearFiq();
                    return value;
                }
                if (m_useKov2ArmType2)
                    return 0xffff_ffff;
                return ReadLe32(m_svg_shareram[m_svg_ram_sel & 1], (address - 0x38000000) & 0xffff);
            }
            if (address == 0x48000000)
            {
                if (m_useKov2ArmType2)
                    return ReadLe32(m_kov2ArmSharedRam, 0);
                return m_svg_latchdata_68k_w;
            }
            if (m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x4800ffff)
                return ReadLe32(m_kov2ArmSharedRam, (address - 0x48000000) & 0xffff);
            if (address >= 0x50000000 && address <= 0x500003ff)
                return ReadLe32(m_armAuxRam, (address - 0x50000000) & 0x3ff);
            return (uint)(ArmRead8(address)
                | (ArmRead8(address + 1) << 8)
                | (ArmRead8(address + 2) << 16)
                | (ArmRead8(address + 3) << 24));
        }

        void ApplyDemonFrontArmRamSpeedup(uint address)
        {
            if (!m_useSvgArmType3 || m_useTheGladArmType3 || (address & ~3u) != 0x18000444)
                return;

            uint pc = m_arm7.Registers[15] - 8;
            if (pc == 0x08000fea)
                m_arm7.Cycles += 500;
        }

        void ApplyTheGladArmRam2Speedup(uint address)
        {
            if (!m_useTheGladArmType3 || (address & ~3u) != 0x1000000c)
                return;

            uint pc = m_arm7.Registers[15] - 8;
            if (pc == 0x000007c4)
                m_arm7.Cycles += 500;
        }

        void ArmWrite8(uint address, byte value)
        {
            if (address >= 0x10000000 && address <= 0x100003ff)
                m_armRam2[(address - 0x10000000) & 0x3ff] = value;
            else if (address >= 0x18000000 && address <= (m_useKov2ArmType2 ? 0x1800ffff : 0x1803ffff))
                m_armRam[(address - 0x18000000) & (m_useKov2ArmType2 ? 0xffffu : 0x3ffffu)] = value;
            else if (address >= 0x38000000 && address <= (m_useKov2ArmType2 ? 0x38000003 : 0x3800ffff))
            {
                if (m_useKov2ArmType2)
                {
                    uint shift = ((address - 0x38000000) & 3) * 8;
                    m_kov2_latchdata_arm_w = (m_kov2_latchdata_arm_w & ~(0xffu << (int)shift)) | ((uint)value << (int)shift);
                    TraceArmEvent(ref m_traceArmLatchWrites, $"[PGM-ARM] kov2 arm latch w8 off={(address - 0x38000000) & 3} val=0x{value:x2} latchArm=0x{m_kov2_latchdata_arm_w:x8} pc=0x{m_arm7.Registers[15] - 8:x8}", 64);
                    return;
                }

                uint offset = (address - 0x38000000) & 0xffff;
                m_svg_shareram[m_svg_ram_sel & 1][(address - 0x38000000) & 0xffff] = value;
                if ((offset & 0xfff8) == 0x150 || (offset & 0xfff8) == 0xa038)
                    TraceArmEvent(ref m_traceArmSharedWrites, $"[PGM-ARM] arm shared w8 bank={m_svg_ram_sel & 1} off=0x{offset:x5} val=0x{value:x2} pc=0x{m_arm7.Registers[15] - 8:x8}", 160);
            }
            else if (address >= 0x48000000 && address <= 0x48000003)
            {
                if (m_useKov2ArmType2)
                {
                    m_kov2ArmSharedRam[(address - 0x48000000) & 0xffff] = value;
                    return;
                }

                uint shift = ((address - 0x48000000) & 3) * 8;
                m_svg_latchdata_arm_w = (m_svg_latchdata_arm_w & ~(0xffu << (int)shift)) | ((uint)value << (int)shift);
                TraceArmEvent(ref m_traceArmLatchWrites, $"[PGM-ARM] arm latch w8 off={(address - 0x48000000) & 3} val=0x{value:x2} latchArm=0x{m_svg_latchdata_arm_w:x8} pc=0x{m_arm7.Registers[15] - 8:x8}", 64);
            }
            else if (m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x4800ffff)
                m_kov2ArmSharedRam[(address - 0x48000000) & 0xffff] = value;
            else if (address >= 0x40000018 && address <= 0x4000001b)
            {
                m_svg_ram_sel = value & 1;
                TraceArmEvent(ref m_traceArmRamSelWrites, $"[PGM-ARM] arm ram_sel w8 addr=0x{address:x8} val=0x{value:x2} sel={m_svg_ram_sel} pc=0x{m_arm7.Registers[15] - 8:x8}", 64);
            }
            else if (address >= 0x50000000 && address <= 0x500003ff)
            {
                if (m_useKov2ArmType2)
                    Kov2XorTableWrite(address, value);
                m_armAuxRam[(address - 0x50000000) & 0x3ff] = value;
            }
        }

        void ArmWrite16(uint address, ushort value)
        {
            address &= ~1u;
            if (!TracePgmArmEnabled())
            {
                if (address >= 0x10000000 && address <= 0x100003ff)
                {
                    WriteLe16(m_armRam2, (address - 0x10000000) & 0x3ff, value);
                    return;
                }
                if (address >= 0x18000000 && address <= (m_useKov2ArmType2 ? 0x1800ffff : 0x1803ffff))
                {
                    WriteLe16(m_armRam, (address - 0x18000000) & (m_useKov2ArmType2 ? 0xffffu : 0x3ffffu), value);
                    return;
                }
                if (address >= 0x38000000 && address <= (m_useKov2ArmType2 ? 0x38000002 : 0x3800ffff))
                {
                    if (m_useKov2ArmType2)
                    {
                        uint shift = ((address - 0x38000000) & 2) * 8;
                        m_kov2_latchdata_arm_w = (m_kov2_latchdata_arm_w & ~(0xffffu << (int)shift)) | ((uint)value << (int)shift);
                        return;
                    }

                    WriteLe16(m_svg_shareram[m_svg_ram_sel & 1], (address - 0x38000000) & 0xffff, value);
                    return;
                }
                if (address >= 0x48000000 && address <= 0x48000002)
                {
                    if (m_useKov2ArmType2)
                    {
                        WriteLe16(m_kov2ArmSharedRam, (address - 0x48000000) & 0xffff, value);
                        return;
                    }

                    uint shift = ((address - 0x48000000) & 2) * 8;
                    m_svg_latchdata_arm_w = (m_svg_latchdata_arm_w & ~(0xffffu << (int)shift)) | ((uint)value << (int)shift);
                    return;
                }
                if (m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x4800ffff)
                {
                    WriteLe16(m_kov2ArmSharedRam, (address - 0x48000000) & 0xffff, value);
                    return;
                }
                if (address >= 0x50000000 && address <= 0x500003ff)
                {
                    if (m_useKov2ArmType2)
                        Kov2XorTableWrite(address, (byte)value);
                    WriteLe16(m_armAuxRam, (address - 0x50000000) & 0x3ff, value);
                    return;
                }
            }
            ArmWrite8(address, (byte)value);
            ArmWrite8(address + 1, (byte)(value >> 8));
        }

        void ArmWrite32(uint address, uint value)
        {
            address &= ~3u;
            if (address == 0x40000018)
            {
                m_svg_ram_sel = (int)(value & 1);
                TraceArmEvent(ref m_traceArmRamSelWrites, $"[PGM-ARM] arm ram_sel w32 val=0x{value:x8} sel={m_svg_ram_sel} pc=0x{m_arm7.Registers[15] - 8:x8}", 64);
                return;
            }

            if (!TracePgmArmEnabled())
            {
                if (address >= 0x10000000 && address <= 0x100003ff)
                {
                    WriteLe32(m_armRam2, (address - 0x10000000) & 0x3ff, value);
                    return;
                }
                if (address >= 0x18000000 && address <= (m_useKov2ArmType2 ? 0x1800ffff : 0x1803ffff))
                {
                    WriteLe32(m_armRam, (address - 0x18000000) & (m_useKov2ArmType2 ? 0xffffu : 0x3ffffu), value);
                    return;
                }
                if (address >= 0x38000000 && address <= (m_useKov2ArmType2 ? 0x38000000 : 0x3800ffff))
                {
                    if (m_useKov2ArmType2)
                    {
                        m_kov2_latchdata_arm_w = value;
                        return;
                    }

                    WriteLe32(m_svg_shareram[m_svg_ram_sel & 1], (address - 0x38000000) & 0xffff, value);
                    return;
                }
                if (address == 0x48000000)
                {
                    if (m_useKov2ArmType2)
                    {
                        WriteLe32(m_kov2ArmSharedRam, 0, value);
                        return;
                    }

                    m_svg_latchdata_arm_w = value;
                    return;
                }
                if (m_useKov2ArmType2 && address >= 0x48000000 && address <= 0x4800ffff)
                {
                    WriteLe32(m_kov2ArmSharedRam, (address - 0x48000000) & 0xffff, value);
                    return;
                }
                if (address >= 0x50000000 && address <= 0x500003ff)
                {
                    if (m_useKov2ArmType2)
                        Kov2XorTableWrite(address, (byte)value);
                    WriteLe32(m_armAuxRam, (address - 0x50000000) & 0x3ff, value);
                    return;
                }
            }

            ArmWrite8(address, (byte)value);
            ArmWrite8(address + 1, (byte)(value >> 8));
            ArmWrite8(address + 2, (byte)(value >> 16));
            ArmWrite8(address + 3, (byte)(value >> 24));
        }

        void RunArmSlice(int cycles)
        {
            if ((!m_useSvgArmType3 && !m_useKov2ArmType2) || cycles <= 0 || m_arm7.CrashDetected)
                return;

            m_arm7.Run(m_arm7.Cycles + cycles);
        }

        void RunArmFrame()
        {
            if ((!m_useSvgArmType3 && !m_useKov2ArmType2) || m_arm7.CrashDetected)
                return;

            long targetCycles = Math.Max(GetArmTargetCyclesFromMainTime(), (long)m_pgmFrameCounter * GetArmCyclesPerFrame());
            if (targetCycles > m_arm7.Cycles)
                m_arm7.Run(targetCycles);
        }

        void SyncArmToMainTime()
        {
            if ((!m_useSvgArmType3 && !m_useKov2ArmType2) || m_arm7.CrashDetected)
                return;

            long targetCycles = GetArmTargetCyclesFromMainTime();
            if (targetCycles > m_arm7.Cycles)
                m_arm7.Run(targetCycles);
        }

        long GetArmTargetCyclesFromMainTime()
        {
            ulong mainCycles = m_maincpu.op0.execute().total_cycles();
            return (long)Math.Min((ulong)long.MaxValue, mainCycles * (ulong)GetArmClockHz() / MainCpuClockHz);
        }

        int GetArmClockHz() => m_useKov2ArmType2 ? Kov2Arm7ClockHz : SvgArm7ClockHz;

        int GetArmCyclesPerFrame() => m_useKov2ArmType2 ? Kov2Arm7CyclesPerFrame : SvgArm7CyclesPerFrame;

        void TraceArmFrame()
        {
            if (!TracePgmArmEnabled())
                return;

            TraceDmnfrntResourceError();

            if ((m_pgmFrameCounter % 30) != 0)
                return;

            Console.Error.WriteLine(
                m_useKov2ArmType2
                    ? $"[PGM-ARM] frame={m_pgmFrameCounter} m68k=0x{m_maincpu.op0.Pc:x6} armPc=0x{m_arm7.Registers[15]:x8} armCyc={m_arm7.Cycles} crash={m_arm7.CrashDetected} crashPc=0x{m_arm7.CrashPc:x8} kov2Latch68=0x{m_kov2_latchdata_68k_w:x8} kov2LatchArm=0x{m_kov2_latchdata_arm_w:x8} fiqLine={(m_arm7.FiqLineAsserted ? 1 : 0)} fiqPending={(m_arm7.FiqPending ? 1 : 0)} shared000=0x{ReadLe32(m_kov2ArmSharedRam, 0x000):x8} shared138=0x{ReadLe32(m_kov2ArmSharedRam, 0x138):x8} xor0=0x{m_kov2XorTable[0]:x8} main[a03c]=0x{ReadMainRamWord(0xa03c):x4}"
                    : $"[PGM-ARM] frame={m_pgmFrameCounter} m68k=0x{m_maincpu.op0.Pc:x6} armPc=0x{m_arm7.Registers[15]:x8} armCyc={m_arm7.Cycles} crash={m_arm7.CrashDetected} crashPc=0x{m_arm7.CrashPc:x8} sel={m_svg_ram_sel} latch68=0x{m_svg_latchdata_68k_w:x8} latchArm=0x{m_svg_latchdata_arm_w:x8} ram0[158]=0x{ReadLe16(m_svg_shareram[0], 0x158):x4} ram1[158]=0x{ReadLe16(m_svg_shareram[1], 0x158):x4} main[a03c]=0x{ReadMainRamWord(0xa03c):x4}");
        }

        static bool TracePgmArmEnabled()
        {
            return Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM_ARM_TRACE") == "1";
        }

        void TraceArmEvent(ref int counter, string message, int limit)
        {
            if (!TracePgmArmEnabled() || counter >= limit)
                return;

            counter++;
            Console.Error.WriteLine(message);
        }

        void TraceDmnfrntResourceError()
        {
            if (m_traceDmnfrntErrorDumped || !m_useSvgArmType3 || m_useTheGladArmType3 || m_maincpu.op0.Pc != 0x101194)
                return;

            m_traceDmnfrntErrorDumped = true;
            var state = m_maincpu.op0.GetState();
            uint fp = state.Address.Length > 6 ? state.Address[6] : 0;
            uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
            uint arg0 = Read68kLong(fp + 8);
            uint arg1 = Read68kLong(fp + 12);
            uint arg2 = Read68kLong(fp + 16);
            uint ret = Read68kLong(fp + 4);

            Console.Error.WriteLine(
                $"[PGM-ARM] dmnfrnt resource-error frame={m_pgmFrameCounter} pc=0x{state.Pc:x6} sr=0x{state.Sr:x4} " +
                $"sp=0x{sp:x6} fp=0x{fp:x6} ret=0x{ret:x6} arg0=0x{arg0:x6} arg1=0x{arg1:x6} arg2=0x{arg2:x6}");
            Console.Error.WriteLine(
                $"[PGM-ARM] dmnfrnt resource-error strings arg0='{Read68kCString(arg0, 96)}' " +
                $"arg1='{Read68kCString(arg1, 96)}' arg2='{Read68kCString(arg2, 96)}'");

            for (int i = 0; i < 10; i++)
            {
                uint addr = sp + (uint)(i * 4);
                Console.Error.WriteLine($"[PGM-ARM] dmnfrnt stack[{i}] @0x{addr:x6}=0x{Read68kLong(addr):x8}");
            }
        }

        void ResetSvgArmType3Runtime()
        {
            if (!m_useSvgArmType3)
                return;

            if (m_useTheGladArmType3)
                CreateTheGladDummyInternalArmRegion();
            else
                CreateDummyInternalArmRegion();
            m_svg_ram_sel = m_useTheGladArmType3 ? 0 : 1;
            m_svg_latchdata_68k_w = 0;
            m_svg_latchdata_arm_w = 0;
            m_armLastPrefetchedPc = 0;
            if (!m_useTheGladArmType3)
            {
                WriteLe16(m_svg_shareram[1], 0x158, 0x0005);
                WriteLe16(m_svg_shareram[0], 0x158, 0x0005);
            }
            m_arm7.Reset(0);
        }

        void ResetKov2ArmType2Runtime()
        {
            if (!m_useKov2ArmType2)
                return;

            memory_region prot = memregion("prot");
            if (prot != null && prot.base_() != null)
            {
                int count = (int)Math.Min(prot.bytes(), (ulong)m_armInternalRom.Length);
                for (int i = 0; i < count; i++)
                    m_armInternalRom[i] = prot.base_()[i];
            }

            Array.Clear(m_kov2XorTable, 0, m_kov2XorTable.Length);
            m_kov2_latchdata_68k_w = 0;
            m_kov2_latchdata_arm_w = 0;
            m_armLastPrefetchedPc = 0;
            m_arm7.Reset(0);
        }

        u32 ReadKov2ExternalArmWord(uint offset)
        {
            offset &= ~3u;
            u32 value = 0xffff_ffff;
            if (offset + 3 < m_armExternalRomBytes)
            {
                value = (u32)(m_armExternalRom[(int)offset]
                    | (m_armExternalRom[(int)(offset + 1)] << 8)
                    | (m_armExternalRom[(int)(offset + 2)] << 16)
                    | (m_armExternalRom[(int)(offset + 3)] << 24));
            }

            return value ^ m_kov2XorTable[(offset >> 2) & 0xff];
        }

        void Kov2XorTableWrite(uint address, byte value)
        {
            uint offset = address - 0x50000000;
            if ((offset & 3) != 0)
                return;

            m_kov2XorTable[(offset >> 2) & 0xff] = ((u32)value << 24) | ((u32)value << 8);
        }

        void CreateDummyInternalArmRegion()
        {
            for (int i = 0; i < m_armInternalRom.Length; i += 4)
                WriteLe32(m_armInternalRom, (uint)i, 0xe12fff1e);

            WriteLe32(m_armInternalRom, 0x0000, 0xe59fd088);
            WriteLe32(m_armInternalRom, 0x0004, 0xe3a00680);
            WriteLe32(m_armInternalRom, 0x0008, 0xe12fff10);
            WriteLe32(m_armInternalRom, 0x0090, 0x10000400);
        }

        void CreateTheGladDummyInternalArmRegion()
        {
            memory_region prot = memregion("prot");
            if (prot != null && prot.base_() != null)
            {
                int count = (int)Math.Min(prot.bytes(), (ulong)m_armInternalRom.Length);
                for (int i = 0; i < count; i++)
                    m_armInternalRom[i] = prot.base_()[i];
            }
            else
            {
                Array.Clear(m_armInternalRom, 0, m_armInternalRom.Length);
            }

            for (uint i = 0; i < 0x188; i += 4)
                WriteLe32(m_armInternalRom, i, 0xeafffffe);

            WriteLe32(m_armInternalRom, 0x0000, 0xea00000a);
            WriteLe32(m_armInternalRom, 0x001c, 0xe59ff000);
            WriteLe32(m_armInternalRom, 0x0020, 0x08000010);
            WriteLe32(m_armInternalRom, 0x0024, 0x08000010);

            uint baseOffset = 0x30;
            WriteTheGladWords(ref baseOffset,
                0x00d2, 0xe3a0, 0xf000, 0xe121,
                0x4001, 0xe3a0, 0x4b06, 0xe284, 0x0cfa, 0xe3a0, 0xd804, 0xe080,
                0x00d1, 0xe3a0, 0xf000, 0xe121, 0x0cf6, 0xe3a0, 0xd804, 0xe080,
                0x00d7, 0xe3a0, 0xf000, 0xe121, 0x0cff, 0xe3a0, 0xd804, 0xe080,
                0x00db, 0xe3a0, 0xf000, 0xe121, 0x4140, 0xe1c4, 0x0cfe, 0xe3a0,
                0xd804, 0xe080, 0x00d3, 0xe3a0, 0xf000, 0xe121, 0x4a01, 0xe3a0,
                0x0b01, 0xe3a0, 0xd804, 0xe080, 0x5a0f, 0xe3a0, 0x0008, 0xe3a0,
                0x8805, 0xe080, 0x0010, 0xe3a0, 0x0000, 0xe5c8, 0x7805, 0xe1a0,
                0x6a01, 0xe3a0, 0x0012, 0xe3a0, 0x0a02, 0xe280, 0x6806, 0xe080,
                0x6000, 0xe587,
                0x00d3, 0xe3a0, 0xf000, 0xe121, 0x4001, 0xe3a0, 0x4b06, 0xe284,
                0x0cf2, 0xe3a0, 0xd804, 0xe080, 0x0013, 0xe3a0, 0xf000, 0xe121,
                0x0028, 0xea00);

            baseOffset = 0xe8;
            WriteTheGladWords(ref baseOffset,
                0xe004, 0xe52d, 0x00d3, 0xe3a0, 0xf000, 0xe121, 0xe004, 0xe49d, 0xff1e, 0xe12f,
                0xe004, 0xe52d, 0x0013, 0xe3a0, 0xf000, 0xe121, 0xe004, 0xe49d, 0xff1e, 0xe12f,
                0x00d1, 0xe3a0, 0xf000, 0xe121, 0xd0b8, 0xe59f, 0x00d3, 0xe3a0,
                0xf000, 0xe121, 0xd0b0, 0xe59f, 0x10b8, 0xe59f, 0x0000, 0xe3a0,
                0x0000, 0xe581, 0xf302, 0xe3a0);

            WriteLe32(m_armInternalRom, 0x0150, 0xe12fff1e);
            WriteLe32(m_armInternalRom, 0x0184, 0xe59f105c);
        }

        void WriteTheGladWords(ref uint offset, params u16[] words)
        {
            foreach (u16 word in words)
            {
                WriteLe16(m_armInternalRom, offset, word);
                offset += 2;
            }
        }

        static u16 ReadLe16(byte[] data, uint offset)
        {
            offset %= (uint)data.Length;
            return (u16)(data[offset] | (data[(offset + 1) % (uint)data.Length] << 8));
        }

        static u32 ReadLe32(byte[] data, uint offset)
        {
            offset %= (uint)data.Length;
            return (u32)(data[offset]
                | (data[(offset + 1) % (uint)data.Length] << 8)
                | (data[(offset + 2) % (uint)data.Length] << 16)
                | (data[(offset + 3) % (uint)data.Length] << 24));
        }

        static void WriteLe16(byte[] data, uint offset, u16 value)
        {
            offset %= (uint)data.Length;
            data[offset] = (byte)value;
            data[(offset + 1) % (uint)data.Length] = (byte)(value >> 8);
        }

        static void WriteLe32(byte[] data, uint offset, u32 value)
        {
            offset %= (uint)data.Length;
            data[offset] = (byte)value;
            data[(offset + 1) % (uint)data.Length] = (byte)(value >> 8);
            data[(offset + 2) % (uint)data.Length] = (byte)(value >> 16);
            data[(offset + 3) % (uint)data.Length] = (byte)(value >> 24);
        }

        static void CombineLe16(byte[] data, uint offset, u16 value, u16 memMask)
        {
            u16 current = ReadLe16(data, offset);
            current = (u16)((current & ~memMask) | (value & memMask));
            WriteLe16(data, offset, current);
        }

        u16 ReadMainRamWord(uint offset)
        {
            offset &= (uint)(m_mainram.Length - 1);
            return (u16)((m_mainram[offset] << 8) | m_mainram[(offset + 1) & (m_mainram.Length - 1)]);
        }

        u8 Read68kByte(uint address)
        {
            address &= 0x00ff_ffff;
            if (address >= MainRamStart && address <= MainRamEnd)
                return m_mainram[(address - MainRamStart) & (uint)(m_mainram.Length - 1)];

            if (m_mainrom != null && address < (uint)m_mainromBytes)
                return m_mainrom[(int)(address ^ 1)];

            return 0xff;
        }

        u16 Read68kWord(uint address)
        {
            return (u16)((Read68kByte(address) << 8) | Read68kByte(address + 1));
        }

        u32 Read68kLong(uint address)
        {
            return ((u32)Read68kWord(address) << 16) | Read68kWord(address + 2);
        }

        string Read68kCString(uint address, int maxLength)
        {
            if (address == 0 || address == 0xffffffff)
                return string.Empty;

            char[] chars = new char[maxLength];
            int length = 0;
            for (; length < maxLength; length++)
            {
                byte value = Read68kByte(address + (uint)length);
                if (value == 0)
                    break;

                chars[length] = value >= 0x20 && value < 0x7f ? (char)value : '.';
            }

            return new string(chars, 0, length);
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
            if (m_useCaveType1Sim || m_useDdp3Type1Sim)
                CommandHandlerDdp3();
            else
                CommandHandlerKov();
        }

        u16 cave_type1_sim_r(address_space space, offs_t offset, u16 mem_mask)
        {
            if (!m_useCaveType1Sim)
                return ReadMainRomWord(0x400000 + (offset << 1));

            return kov_sim_r(space, offset, mem_mask);
        }

        void cave_type1_sim_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (m_useCaveType1Sim)
                kov_sim_w(space, offset, data, mem_mask);
        }

        u16 ReadMainRomWord(u32 address)
        {
            if (m_mainrom == null || address + 1 >= (uint)m_mainromBytes)
                return 0xffff;

            return (u16)((m_mainrom[(int)(address + 1)] << 8) | m_mainrom[(int)address]);
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

        void CommandHandlerDdp3()
        {
            switch (m_ddp3lastcommand)
            {
            case 0x40:
                m_valueresponse = 0x880000;
                m_kov_slots[(m_value0 >> 10) & 0x1f] =
                    (m_kov_slots[(m_value0 >> 5) & 0x1f] + m_kov_slots[m_value0 & 0x1f]) & 0x00ffffff;
                break;

            case 0x67:
                m_valueresponse = 0x880000;
                m_curslots = (m_value0 & 0xff00) >> 8;
                m_kov_slots[m_curslots] = (u32)((m_value0 & 0x00ff) << 16);
                break;

            case 0xe5:
                m_valueresponse = 0x880000;
                m_kov_slots[m_curslots] |= m_value0;
                break;

            case 0x8e:
                m_valueresponse = m_kov_slots[m_value0 & 0xff];
                break;

            case 0x99:
            case 0x38:
                m_simregion = 0;
                m_valuekey = 0x0100;
                m_valueresponse = (u32)(0x880000 | (m_simregion << 8));
                break;

            default:
                m_valueresponse = 0x880000;
                if (TracePgmArmEnabled())
                    Console.Error.WriteLine($"[PGM-TYPE1] unhandled ddp3 command=0x{m_ddp3lastcommand:x2} value=0x{m_value0:x4} pc=0x{m_maincpu.op0.Pc:x6}");
                break;
            }
        }

        void ResetAsic3Protection()
        {
            m_asic3_reg = 0;
            Array.Clear(m_asic3_latch, 0, m_asic3_latch.Length);
            m_asic3_x = 0;
            m_asic3_hilo = 0;
            m_asic3_hold = 0;
            m_asic3Region = 0;
        }

        u16 asic3_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_asic3_reg switch
            {
                0x00 => (u16)((m_asic3_latch[0] & 0x00f7) | ((m_asic3Region << 3) & 0x0008)),
                0x01 => m_asic3_latch[1],
                0x02 => (u16)((m_asic3_latch[2] & 0x007f) | ((m_asic3Region << 6) & 0x0080)),
                0x03 => (u16)Bitswap8(m_asic3_hold, 5, 2, 9, 7, 10, 13, 12, 15),
                0x20 => 0x49,
                0x21 => 0x47,
                0x22 => 0x53,
                0x24 => 0x41,
                0x25 => 0x41,
                0x26 => 0x7f,
                0x27 => 0x41,
                0x28 => 0x41,
                0x2a => 0x3e,
                0x2b => 0x41,
                0x2c => 0x49,
                0x2d => 0xf9,
                0x2e => 0x0a,
                0x30 => 0x26,
                0x31 => 0x49,
                0x32 => 0x49,
                0x33 => 0x49,
                0x34 => 0x32,
                _ => 0
            };
        }

        void asic3_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if (offset == 0)
            {
                m_asic3_reg = data;
                return;
            }

            switch (m_asic3_reg)
            {
            case 0x00:
            case 0x01:
            case 0x02:
                m_asic3_latch[m_asic3_reg] = (u16)(data << 1);
                break;

            case 0x40:
                m_asic3_hilo = (u16)((m_asic3_hilo << 8) | (data & 0x00ff));
                break;

            case 0x41:
            case 0x42:
            case 0x43:
            case 0x44:
            case 0x45:
            case 0x46:
            case 0x47:
                break;

            case 0x48:
                m_asic3_x = 0;
                if ((m_asic3_hilo & 0x0090) == 0) m_asic3_x |= 0x01;
                if ((m_asic3_hilo & 0x0006) == 0) m_asic3_x |= 0x02;
                if ((m_asic3_hilo & 0x9000) == 0) m_asic3_x |= 0x04;
                if ((m_asic3_hilo & 0x0a00) == 0) m_asic3_x |= 0x08;
                break;

            case 0xa0:
                m_asic3_hold = 0;
                break;

            default:
                if (m_asic3_reg >= 0x80 && m_asic3_reg <= 0x87)
                    Asic3ComputeHold(m_asic3_reg & 0x07, data);
                break;
            }
        }

        void Asic3ComputeHold(int y, int z)
        {
            u16 old = m_asic3_hold;

            m_asic3_hold = (u16)((old << 1) | (old >> 15));
            m_asic3_hold ^= 0x2bad;
            m_asic3_hold ^= (u16)Bit(z, y);
            m_asic3_hold ^= (u16)(Bit(m_asic3_x, 2) << 10);
            m_asic3_hold ^= (u16)Bit(old, 5);

            switch (m_asic3Region)
            {
            case 0:
            case 1:
                m_asic3_hold ^= (u16)(Bit(old, 10) ^ Bit(old, 8) ^ (Bit(m_asic3_x, 0) << 1) ^ (Bit(m_asic3_x, 1) << 6) ^ (Bit(m_asic3_x, 3) << 14));
                break;

            case 2:
                m_asic3_hold ^= (u16)(Bit(old, 10) ^ Bit(old, 8) ^ (Bit(m_asic3_x, 0) << 4) ^ (Bit(m_asic3_x, 1) << 6) ^ (Bit(m_asic3_x, 3) << 12));
                break;

            case 3:
                m_asic3_hold ^= (u16)(Bit(old, 7) ^ Bit(old, 6) ^ (Bit(m_asic3_x, 0) << 4) ^ (Bit(m_asic3_x, 1) << 6) ^ (Bit(m_asic3_x, 3) << 12));
                break;

            case 4:
                m_asic3_hold ^= (u16)(Bit(old, 7) ^ Bit(old, 6) ^ (Bit(m_asic3_x, 0) << 3) ^ (Bit(m_asic3_x, 1) << 8) ^ (Bit(m_asic3_x, 3) << 14));
                break;
            }
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

        public void init_ddpdoj()
        {
            memory_region region = memregion("maincpu");
            if (region != null && region.base_() != null)
                PgmCrypt.Py2k2Decrypt(region.base_(), 0x100000, 0x400000);

            m_useDdp3Type1Sim = true;
            ResetKovProtection();
        }

        public void init_ket()
        {
            memory_region region = memregion("maincpu");
            if (region != null && region.base_() != null)
                PgmCrypt.KetDecrypt(region.base_(), 0, 0x400000);

            m_useCaveType1Sim = true;
            m_useDdp3Type1Sim = false;
            ResetKovProtection();
        }

        public void init_espgal()
        {
            memory_region region = memregion("maincpu");
            if (region != null && region.base_() != null)
                PgmCrypt.EspgalDecrypt(region.base_(), 0, 0x400000);

            m_useCaveType1Sim = true;
            m_useDdp3Type1Sim = false;
            ResetKovProtection();
        }

        public void init_kov2()
        {
            memory_region region = memregion("user1");
            if (region != null && region.base_() != null)
                PgmCrypt.Kov2Decrypt(region.base_(), 0, 0x200000);

            m_useKov2ArmType2 = true;
            ResetKov2ArmType2Runtime();
        }

        public void init_orlegend()
        {
            ResetAsic3Protection();
        }

        public void init_dmnfrnt()
        {
            memory_region region = memregion("user1");
            if (region != null && region.base_() != null)
                PgmCrypt.DemonFrontDecrypt(region.base_(), 0, Math.Min(0x400000, (int)region.bytes()));

            m_useSvgArmType3 = true;
            m_useTheGladArmType3 = false;
            if (TracePgmArmEnabled())
                Console.Error.WriteLine("[PGM-ARM] init_dmnfrnt type3 enabled");
            ResetSvgArmType3Runtime();
        }

        public void init_theglad()
        {
            memory_region region = memregion("user1");
            if (region != null && region.base_() != null)
                PgmCrypt.TheGladDecrypt(region.base_(), 0, Math.Min(0x200000, (int)region.bytes()));

            m_useSvgArmType3 = true;
            m_useTheGladArmType3 = true;
            if (TracePgmArmEnabled())
                Console.Error.WriteLine("[PGM-ARM] init_theglad type3 enabled");
            ResetSvgArmType3Runtime();
        }

        uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            return m_video.op0.screen_update(screen, bitmap, cliprect);
        }

        static uint Expand5(int value) => (uint)((value << 3) | (value >> 2));

        static int Bit(int value, int bit) => (value >> bit) & 1;
        static int Bit(u16 value, int bit) => (value >> bit) & 1;

        static int Bitswap8(int value, int b7, int b6, int b5, int b4, int b3, int b2, int b1, int b0)
            => (Bit(value, b7) << 7)
                | (Bit(value, b6) << 6)
                | (Bit(value, b5) << 5)
                | (Bit(value, b4) << 4)
                | (Bit(value, b3) << 3)
                | (Bit(value, b2) << 2)
                | (Bit(value, b1) << 1)
                | Bit(value, b0);
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

        static readonly tiny_rom_entry [] rom_orlegend =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),
            ROM_LOAD16_WORD_SWAP("p0103.rom", 0x100000, 0x200000, CRC("d5e93543") + SHA1("f081edc26514ca8354c13c7f6f89aba8e4d3e7d2")),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t0100.rom", 0x180000, 0x400000, CRC("61425e1e") + SHA1("20753b86fc12003cfd763d903f034dbba8010b32")),

            ROM_REGION16_LE(0x2000000, "igs023:sprcol", 0),
            ROM_LOAD("a0100.rom", 0x0000000, 0x400000, CRC("8b3bd88a") + SHA1("42db3a60c6ba9d83ebe2008c8047d094027f65a7")),
            ROM_LOAD("a0101.rom", 0x0400000, 0x400000, CRC("3b9e9644") + SHA1("5b95ec1d25c3bc3504c93547f5adb5ce24376405")),
            ROM_LOAD("a0102.rom", 0x0800000, 0x400000, CRC("069e2c38") + SHA1("9bddca8c2f5bd80f4abe4e1f062751736dc151dd")),
            ROM_LOAD("a0103.rom", 0x0c00000, 0x400000, CRC("4460a3fd") + SHA1("cbebdb65c17605853f7d0b298018dd8801a25a58")),
            ROM_LOAD("a0104.rom", 0x1000000, 0x400000, CRC("5f8abb56") + SHA1("6c1ddc0309862a141aa0c0f63b641aec9257aaee")),
            ROM_LOAD("a0105.rom", 0x1400000, 0x400000, CRC("a17a7147") + SHA1("44eeb43c6b0ebb829559a20ae357383fbdeecd82")),

            ROM_REGION16_LE(0x1000000, "igs023:sprmask", 0),
            ROM_LOAD("b0100.rom", 0x0000000, 0x400000, CRC("69d2e48c") + SHA1("5b5f759007264c07b3b39be8e03a713698e1fc2a")),
            ROM_LOAD("b0101.rom", 0x0400000, 0x400000, CRC("0d587bf3") + SHA1("5347828b0a6e4ddd7a263663d2c2604407e4d49c")),
            ROM_LOAD("b0102.rom", 0x0800000, 0x400000, CRC("43823c1e") + SHA1("e10a1a9a81b51b11044934ff702e35d8d7ab1b08")),

            ROM_REGION(0x600000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),
            ROM_LOAD("m0100.rom", 0x400000, 0x200000, CRC("e5c36c83") + SHA1("50c6f66770e8faa3df349f7d68c407a7ad021716")),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_dmnfrnt =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),
            ROM_LOAD16_WORD_SWAP("v105_16m.u5", 0x100000, 0x200000, CRC("bda083bd") + SHA1("58d6438737a2c43aa8bbcb7f34fb51375b781b1c")),

            ROM_REGION(0x4000, "prot", ROMREGION_ERASEFF),

            ROM_REGION(0x800000, "user1", 0),
            ROM_LOAD("chinese-v105.u62", 0x000000, 0x400000, CRC("c798c2ef") + SHA1("91e364c33b935293fa765ca521cdb67ac45ec70f")),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t04501.u29", 0x180000, 0x800000, CRC("900eaaac") + SHA1("4033cb7b28fcadb92d5af3ea7fdd1c22747618fd")),

            ROM_REGION16_LE(0x2000000, "igs023:sprcol", 0),
            ROM_LOAD("a04501.u3", 0x0000000, 0x0800000, CRC("9741bea6") + SHA1("e3e904249be228628c8c2bd3495cda23586dc048")),
            ROM_LOAD("a04502.u4", 0x0800000, 0x0800000, CRC("e104f405") + SHA1("124b3deed3e838f8bae6c7d78bdd788859597585")),
            ROM_LOAD("a04503.u6", 0x1000000, 0x0800000, CRC("bfd5cfe3") + SHA1("fbe4c0a2987c2036df707b86597d78124ee2e665")),

            ROM_REGION16_LE(0x1000000, "igs023:sprmask", 0),
            ROM_LOAD("b04501.u9", 0x0000000, 0x0800000, CRC("29320b7d") + SHA1("59c78805e666f912df201c34616744f46057937b")),
            ROM_LOAD("b04502.u11", 0x0800000, 0x0200000, CRC("578c00e9") + SHA1("14235cc8b0f8c7dd659512f017a2d4aacd91d89d")),

            ROM_REGION(0x1000000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),
            ROM_LOAD("w04501.u5", 0x400000, 0x800000, CRC("3ab58137") + SHA1("b221f7e551ff0bfa3fd97b6ebedbac69442a66e9")),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_theglad =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),
            ROM_LOAD16_WORD_SWAP("v101.u6", 0x100000, 0x080000, CRC("f799e866") + SHA1("dccc3c903357c40c3cf85ac0ae8fc12fb0f853a6")),

            ROM_REGION(0x4000, "prot", 0),
            ROM_LOAD("theglad_igs027a_v100_overseas.bin", 0x0188, 0x3e78, CRC("02fe6f52") + SHA1("0b0ddf4507856cfc5b7d4ef7e4c5375254c2a024")),

            ROM_REGION(0x800000, "user1", 0),
            ROM_LOAD("v107.u26", 0x000000, 0x200000, CRC("f7c61357") + SHA1("52d31c464dfc83c5371b078cb6b73c0d0e0d57e3")),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t04601.u33", 0x180000, 0x800000, CRC("e5dab371") + SHA1("2e3c93958eb0326b6b84b95c2168626f26bbac76")),

            ROM_REGION16_LE(0x2000000, "igs023:sprcol", 0),
            ROM_LOAD("a04601.u2", 0x0000000, 0x0800000, CRC("d9b2e004") + SHA1("8e1882b800fe9f12d7d49303e7417ba5b6f8ef85")),
            ROM_LOAD("a04602.u4", 0x0800000, 0x0800000, CRC("14f22308") + SHA1("7fad54704e8c97eab723f53dfb50fb3e7bb606d2")),
            ROM_LOAD("a04603.u6", 0x1000000, 0x0800000, CRC("8f621e17") + SHA1("b0f87f378e0115d0c95017ca0f1b0d508827a7c6")),

            ROM_REGION16_LE(0x1000000, "igs023:sprmask", 0),
            ROM_LOAD("b04601.u11", 0x0000000, 0x0800000, CRC("ee72bccf") + SHA1("73c25fe659f6c903447066e4ef83d2f580449d76")),
            ROM_LOAD("b04602.u12", 0x0800000, 0x0400000, CRC("7dba9c38") + SHA1("a03d509274e8f6a500a7ebe2da5aab8bed4e7f2f")),

            ROM_REGION(0x1000000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),
            ROM_LOAD("w04601.u1", 0x400000, 0x800000, CRC("5f15ddb3") + SHA1("c38dcef8e06802a84e42a7fc9fa505475fc3ac65")),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_ddpdoj =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("ddp3_bios.u37", 0x000000, 0x080000, CRC("b3cc5c8f") + SHA1("02d9511cf71e4a0d6ca8fd9a1ef2c79b0d001824")),
            ROM_LOAD16_WORD_SWAP("ddp3_v101.u36", 0x100000, 0x200000, CRC("195b5c1e") + SHA1("f18d791c034b0a3d85888a92fb5d326ee3deb04f")),

            ROM_REGION(0x4000, "prot", ROMREGION_ERASEFF),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t04401w064.u19", 0x180000, 0x800000, CRC("3a95f19c") + SHA1("fd3c47cf0b8b1e20c6bec4be68a089fc8bbf4dbe")),

            ROM_REGION16_LE(0x2000000, "igs023:sprcol", 0),
            ROM_LOAD("a04401w064.u7", 0x0000000, 0x0800000, CRC("ed229794") + SHA1("1cf1863495a18c7c7d277a9be43ec116b00960b0")),
            ROM_LOAD("a04402w064.u8", 0x0800000, 0x0800000, CRC("752167b0") + SHA1("c33c3398dd8e479c9d5bd348924958a6aecbf0fc")),

            ROM_REGION16_LE(0x1000000, "igs023:sprmask", 0),
            ROM_LOAD("b04401w064.u1", 0x0000000, 0x0800000, CRC("8cbff066") + SHA1("eef1cd566bc70ebf45f047e56026803d5c1dac43")),

            ROM_REGION(0x1000000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),
            ROM_LOAD("m04401b032.u17", 0x400000, 0x400000, CRC("5a0dbd76") + SHA1("06ab202f6bd5ebfb35b9d8cc7a8fb83ec8840659")),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_ket =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("ketsui_v100.u38", 0x000000, 0x200000, CRC("dfe62f3b") + SHA1("baa58d1ce47a707f84f65779ac0689894793e9d9")),

            ROM_REGION(0x4000, "prot", ROMREGION_ERASEFF),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t04701w064.u19", 0x180000, 0x800000, CRC("2665b041") + SHA1("fb1107778b66f2af0de77ac82e1ee2902f53a959")),

            ROM_REGION16_LE(0x1000000, "igs023:sprcol", 0),
            ROM_LOAD("a04701w064.u7", 0x0000000, 0x0800000, CRC("5ef1b94b") + SHA1("f10dfa46e0a4d297c3a856aea5b49d648f98935c")),
            ROM_LOAD("a04702w064.u8", 0x0800000, 0x0800000, CRC("26d6da7f") + SHA1("f20e07a7994f41b5ed917f8b0119dc5542f3541c")),

            ROM_REGION16_LE(0x0800000, "igs023:sprmask", 0),
            ROM_LOAD("b04701w064.u1", 0x0000000, 0x0800000, CRC("1bec008d") + SHA1("07d117dc2eebb35727fb18a7c563acbaf25a8d36")),

            ROM_REGION(0x1000000, "ics", 0),
            ROM_LOAD("m04701b032.u17", 0x400000, 0x400000, CRC("b46e22d1") + SHA1("670853dc485942fb96380568494bdf3235f446ee")),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_espgal =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("espgaluda_v100.u38", 0x000000, 0x200000, CRC("08ecec34") + SHA1("bce2e7fb9105ed51603d09cbd3a9eeb5b8f47ee2")),

            ROM_REGION(0x4000, "prot", ROMREGION_ERASEFF),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("t01s.u18", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t04801w064.u19", 0x180000, 0x800000, CRC("6021c79e") + SHA1("fbc340dafb18aa3094de29b881318a5a9794e4bc")),

            ROM_REGION16_LE(0x1000000, "igs023:sprcol", 0),
            ROM_LOAD("a04801w064.u7", 0x0000000, 0x0800000, CRC("26dd4932") + SHA1("9bbabb5a53cb5ba88397cc2c258980f3b70314ce")),
            ROM_LOAD("a04802w064.u8", 0x0800000, 0x0800000, CRC("0e6bf7a9") + SHA1("a7541e2b5a0df2bc62a5b347e54dbc2ed1922db2")),

            ROM_REGION16_LE(0x0800000, "igs023:sprmask", 0),
            ROM_LOAD("b04801w064.u1", 0x0000000, 0x0800000, CRC("98dce13a") + SHA1("61d48b7117459f7babc022b68231f6928177a71d")),

            ROM_REGION(0x1000000, "ics", 0),
            ROM_LOAD("w04801b032.u17", 0x400000, 0x400000, CRC("60298536") + SHA1("6b7333f16cce778c5725dbdf75a5446f0906397a")),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_kov2 =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),
            ROM_LOAD16_WORD_SWAP("u18.107", 0x100000, 0x400000, CRC("661a5b2c") + SHA1("125054fabc93d4f4cba869c3e6adf863650d30cf")),

            ROM_REGION(0x4000, "prot", 0),
            ROM_LOAD("kov2_v100_hongkong.asic", 0x000000, 0x004000, CRC("e0d7679f") + SHA1("e1c2d127eba4ddbeb8ad173c55b90ac1467e1ca8")),

            ROM_REGION(0x800000, "user1", 0),
            ROM_LOAD("u19.102", 0x000000, 0x200000, CRC("462e2980") + SHA1("3da7c3d2c65b59f50c78be1c25922b71d40f6080")),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t1200.rom", 0x180000, 0x800000, CRC("d7e26609") + SHA1("bdad810f82fcf1d50a8791bdc495374ec5a309c6")),

            ROM_REGION16_LE(0x4000000, "igs023:sprcol", 0),
            ROM_LOAD("a1200.rom", 0x0000000, 0x0800000, CRC("ceeb81d8") + SHA1("5476729443fc1bc9593ae10fbf7cbc5d7290b017")),
            ROM_LOAD("a1201.rom", 0x0800000, 0x0800000, CRC("21063ca7") + SHA1("cf561b44902425a920d5cbea5bf65dd9530b2289")),
            ROM_LOAD("a1202.rom", 0x1000000, 0x0800000, CRC("4bb92fae") + SHA1("f0b6d72ed425de1c69dc8f8d5795ea760a4a59b0")),
            ROM_LOAD("a1203.rom", 0x1800000, 0x0800000, CRC("e73cb627") + SHA1("4c6e48b845a5d1e8f9899010fbf273d54c2b8899")),
            ROM_LOAD("a1204.rom", 0x2000000, 0x0200000, CRC("14b4b5bb") + SHA1("d7db5740eec971f2782fb2885ee3af8f2a796550")),

            ROM_REGION16_LE(0x2000000, "igs023:sprmask", 0),
            ROM_LOAD("b1200.rom", 0x0000000, 0x0800000, CRC("bed7d994") + SHA1("019dfba8154256d64cd249eb0fa4c451edce34b8")),
            ROM_LOAD("b1201.rom", 0x0800000, 0x0800000, CRC("f251eb57") + SHA1("56a5fc14ab7822f83379cecb26638e5bb266349a")),

            ROM_REGION(0x1000000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),
            ROM_LOAD("m1200.rom", 0x800000, 0x800000, CRC("b0d88720") + SHA1("44ab137e3f8e15b7cb5697ffbd9b1143d8210c4f")),

            ROM_END,
        };

        static void pgm_state_pgm(machine_config config, device_t device) { ((pgm_state)device).pgm(config); }
        static void pgm_state_init_kov(device_t owner) { ((pgm_state)owner).init_kov(); }
        static void pgm_state_init_ddpdoj(device_t owner) { ((pgm_state)owner).init_ddpdoj(); }
        static void pgm_state_init_ket(device_t owner) { ((pgm_state)owner).init_ket(); }
        static void pgm_state_init_espgal(device_t owner) { ((pgm_state)owner).init_espgal(); }
        static void pgm_state_init_kov2(device_t owner) { ((pgm_state)owner).init_kov2(); }
        static void pgm_state_init_orlegend(device_t owner) { ((pgm_state)owner).init_orlegend(); }
        static void pgm_state_init_dmnfrnt(device_t owner) { ((pgm_state)owner).init_dmnfrnt(); }
        static void pgm_state_init_theglad(device_t owner) { ((pgm_state)owner).init_theglad(); }
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
        public static readonly game_driver driver_orlegend = GAME(device_creator_pgm_state, rom_orlegend, "1997", "orlegend", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_orlegend, ROT0, "IGS", "Oriental Legend / Xiyou Shi E Zhuan (ver. 126)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND);
        public static readonly game_driver driver_dmnfrnt = GAME(device_creator_pgm_state, rom_dmnfrnt, "2002", "dmnfrnt", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_dmnfrnt, ROT0, "IGS", "Demon Front / Moyu Zhanxian (V105)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND);
        public static readonly game_driver driver_theglad = GAME(device_creator_pgm_state, rom_theglad, "2003", "theglad", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_theglad, ROT0, "IGS", "The Gladiator / Shen Jian Fu Mo Lu / Shen Jian Fengyun (V101)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND);
        public static readonly game_driver driver_ddpdoj = GAME(device_creator_pgm_state, rom_ddpdoj, "2002", "ddpdoj", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_ddpdoj, ROT270, "Cave (AMI license)", "DoDonPachi Dai-Ou-Jou (V101)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND | MACHINE_UNEMULATED_PROTECTION);
        public static readonly game_driver driver_ket = GAME(device_creator_pgm_state, rom_ket, "2002", "ket", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_ket, ROT270, "Cave (AMI license)", "Ketsui Kizuna Jigoku Tachi (V100)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND | MACHINE_UNEMULATED_PROTECTION);
        public static readonly game_driver driver_espgal = GAME(device_creator_pgm_state, rom_espgal, "2003", "espgal", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_espgal, ROT270, "Cave (AMI license)", "Espgaluda (V100)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND | MACHINE_UNEMULATED_PROTECTION);
        public static readonly game_driver driver_kov2 = GAME(device_creator_pgm_state, rom_kov2, "2000", "kov2", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, pgm_state_init_kov2, ROT0, "IGS", "Knights of Valour 2 / Sanguo Zhan Ji 2 / Sangoku Senki 2 (ver. 100, Hong Kong)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND | MACHINE_UNEMULATED_PROTECTION);
    }

    sealed class PgmV3021Rtc
    {
        readonly u8 [] m_ram = new u8[16];
        readonly u8 [] m_clock = new u8[16];
        bool m_started;
        bool m_cs;
        int m_io;
        int m_addr;
        int m_data;
        int m_cnt;
        int m_mode;
        long m_clockTicks;
        int m_tickFrames;

        public void Reset()
        {
            EnsureStarted();
            m_cs = false;
            m_io = 0;
            m_addr = 0;
            m_data = 0;
            m_cnt = 0;
            m_mode = 0;
            m_ram[0] = 0;
            m_ram[1] = 0;
        }

        public void RegisterSaveState(device_t owner, string prefix)
        {
            save_manager save = owner.machine().save();
            save.save_item(owner, owner.name(), owner.tag(), 0, m_ram, $"{prefix}.m_ram");
            save.save_item(owner, owner.name(), owner.tag(), 0, m_clock, $"{prefix}.m_clock");
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_started", () => m_started, value => m_started = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_cs", () => m_cs, value => m_cs = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_io", () => m_io, value => m_io = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_addr", () => m_addr, value => m_addr = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_data", () => m_data, value => m_data = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_cnt", () => m_cnt, value => m_cnt = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_mode", () => m_mode, value => m_mode = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_clockTicks", () => m_clockTicks, value => m_clockTicks = value);
            save.save_item_ref(owner, owner.name(), owner.tag(), 0, $"{prefix}.m_tickFrames", () => m_tickFrames, value => m_tickFrames = value);
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
                    {
                        TraceRtc($"cmd=copy_ram_to_clock status0=0x{m_ram[0]:x2}");
                        CopyRamToClock();
                    }
                }
                else if (m_addr == 0x0f)
                {
                    TraceRtc("cmd=copy_clock_to_ram");
                    CopyClockToRam();
                }
                else
                {
                    m_data = m_ram[m_addr & 0x0f];
                    m_mode = 1;
                    TraceRtc($"addr=0x{m_addr:x1} read_data=0x{m_data:x2}");
                }

                m_cnt = 0;
                return;
            }

            m_data = ((m_data >> 1) | (m_io << 7)) & 0xff;
            if (++m_cnt >= 8)
            {
                if (m_addr != 1 && m_addr <= 9)
                {
                    m_ram[m_addr & 0x0f] = (u8)m_data;
                    TraceRtc($"addr=0x{m_addr:x1} write_data=0x{m_data:x2}");
                }

                m_mode = 0;
                m_cnt = 0;
            }
        }

        void EnsureStarted()
        {
            if (m_started)
                return;

            LoadDefaultClock();
            CopyClockToRam();
            m_started = true;
        }

        public void TickFrame()
        {
            EnsureStarted();
            if (++m_tickFrames < 60)
                return;

            m_tickFrames = 0;
            m_clockTicks += TimeSpan.TicksPerSecond;
            CopyTicksToClockRegisters();
        }

        void LoadDefaultClock()
        {
            DateTime now = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM_RTC_HOST") == "1"
                ? DateTime.Now
                : new DateTime(2002, 5, 10, 10, 33, 23);

            m_clockTicks = now.Ticks;
            CopyTicksToClockRegisters();
        }

        void CopyTicksToClockRegisters()
        {
            DateTime now = new DateTime(m_clockTicks);
            m_clock[0] = 0;
            m_clock[1] = 0;
            m_clock[2] = ToBcd(now.Second);
            m_clock[3] = ToBcd(now.Minute);
            m_clock[4] = ToBcd(now.Hour);
            m_clock[5] = ToBcd(now.Day);
            m_clock[6] = ToBcd(now.Month);
            m_clock[7] = ToBcd(now.Year % 100);
            m_clock[8] = ToBcd(((int)now.DayOfWeek + 1));
            m_clock[9] = ToBcd((now.Day % 7) + 1);
        }

        void CopyClockToRam()
        {
            m_ram[1] = 0;
            if (m_ram[2] != m_clock[2]) m_ram[1] |= 1 << 0;
            if (m_ram[3] != m_clock[3]) m_ram[1] |= 1 << 1;
            if (m_ram[4] != m_clock[4]) m_ram[1] |= 1 << 2;
            if (m_ram[5] != m_clock[5]) m_ram[1] |= 1 << 3;
            if (m_ram[6] != m_clock[6]) m_ram[1] |= 1 << 4;
            if (m_ram[7] != m_clock[7]) m_ram[1] |= 1 << 5;
            if (m_ram[8] != m_clock[8]) m_ram[1] |= 1 << 6;
            if (m_ram[9] != m_clock[9]) m_ram[1] |= 1 << 7;

            for (int i = 2; i <= 9; i++)
                m_ram[i] = m_clock[i];
        }

        void CopyRamToClock()
        {
            m_ram[1] = 0;
            for (int i = 2; i <= 9; i++)
                m_clock[i] = m_ram[i];

            m_clockTicks = ClockRegistersToTicks();
            m_tickFrames = 0;
        }

        long ClockRegistersToTicks()
        {
            int second = FromBcd(m_clock[2], 0, 59);
            int minute = FromBcd(m_clock[3], 0, 59);
            int hour = FromBcd(m_clock[4], 0, 23);
            int day = FromBcd(m_clock[5], 1, 31);
            int month = FromBcd(m_clock[6], 1, 12);
            int year = FromBcd(m_clock[7], 0, 99);
            int fullYear = year >= 70 ? 1900 + year : 2000 + year;

            try
            {
                return new DateTime(fullYear, month, day, hour, minute, second).Ticks;
            }
            catch (ArgumentOutOfRangeException)
            {
                return new DateTime(2002, 5, 10, 10, 33, 23).Ticks;
            }
        }

        static u8 ToBcd(int value) => (u8)(((value / 10) << 4) | (value % 10));

        static int FromBcd(int value, int min, int max)
        {
            int decoded = ((value >> 4) * 10) + (value & 0x0f);
            if (decoded < min)
                return min;
            if (decoded > max)
                return max;
            return decoded;
        }

        static void TraceRtc(string message)
        {
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM_ARM_TRACE") == "1")
                Console.Error.WriteLine($"[PGM-RTC] {message}");
        }
    }

    static class PgmCrypt
    {
        static readonly u8 [] TheGladTab =
        {
            0x49, 0x47, 0x53, 0x30, 0x30, 0x30, 0x35, 0x52, 0x44, 0x31, 0x30, 0x32, 0x31, 0x32, 0x30, 0x33,
            0xc4, 0xa3, 0x46, 0x78, 0x30, 0xb3, 0x8b, 0xd5, 0x2f, 0xc4, 0x44, 0xbf, 0xdb, 0x76, 0xdb, 0xea,
            0xb4, 0xeb, 0x95, 0x4d, 0x15, 0x21, 0x99, 0xa1, 0xd7, 0x8c, 0x40, 0x1d, 0x43, 0xf3, 0x9f, 0x71,
            0x3d, 0x8c, 0x52, 0x01, 0xaf, 0x5b, 0x8b, 0x63, 0x34, 0xc8, 0x5c, 0x1b, 0x06, 0x7f, 0x41, 0x96,
            0x2a, 0x8d, 0xf1, 0x64, 0xda, 0xb8, 0x67, 0xba, 0x33, 0x1f, 0x2b, 0x28, 0x20, 0x13, 0xe6, 0x96,
            0x86, 0x34, 0x25, 0x85, 0xb0, 0xd0, 0x6d, 0x85, 0xfe, 0x78, 0x81, 0xf1, 0xca, 0xe4, 0xef, 0xf2,
            0x9b, 0x09, 0xe1, 0xb4, 0x8d, 0x79, 0x22, 0xe2, 0x00, 0xfb, 0x6f, 0x68, 0x80, 0x6a, 0x00, 0x69,
            0xf5, 0xd3, 0x57, 0x7e, 0x0c, 0xca, 0x48, 0x31, 0xe5, 0x0d, 0x4a, 0xb9, 0xfd, 0x5c, 0xfd, 0xf8,
            0x5f, 0x98, 0xfb, 0xb3, 0x07, 0x1a, 0xe3, 0x10, 0x96, 0x56, 0xa3, 0x56, 0x3d, 0xb1, 0x07, 0xe0,
            0xe3, 0x9f, 0x7f, 0x62, 0x99, 0x01, 0x35, 0x60, 0x40, 0xbe, 0x4f, 0xeb, 0x79, 0xa0, 0x82, 0x9f,
            0xcd, 0x71, 0xd8, 0xda, 0x1e, 0x56, 0xc2, 0x3e, 0x4e, 0x6b, 0x60, 0x69, 0x2d, 0x9f, 0x10, 0xf4,
            0xa9, 0xd3, 0x36, 0xaa, 0x31, 0x2e, 0x4c, 0x0a, 0x69, 0xc3, 0x2a, 0xff, 0x15, 0x67, 0x96, 0xde,
            0x3f, 0xcc, 0x0f, 0xa1, 0xac, 0xe2, 0xd6, 0x62, 0x7e, 0x6f, 0x3e, 0x1b, 0x2a, 0xed, 0x36, 0x9c,
            0x9d, 0xa4, 0x14, 0xcd, 0xaa, 0x08, 0xa4, 0x26, 0xb7, 0x55, 0x70, 0x6c, 0xa9, 0x69, 0x52, 0xae,
            0x0c, 0xe1, 0x38, 0x7f, 0x87, 0x78, 0x38, 0x75, 0x80, 0x9c, 0xd4, 0xe2, 0x0b, 0x52, 0x8f, 0xd2,
            0x19, 0x4c, 0xb0, 0x45, 0xde, 0x48, 0x55, 0xae, 0x82, 0xab, 0xbc, 0xab, 0x0c, 0x5e, 0xce, 0x07
        };

        static readonly u8 [] DemonFrontTab =
        {
            0x51, 0xc4, 0xe3, 0x10, 0x1c, 0xad, 0x8a, 0x39, 0x8c, 0xe0, 0xa5, 0x04, 0x0f, 0xe4, 0x35, 0xc3,
            0x2d, 0x6b, 0x32, 0xe2, 0x60, 0x54, 0x63, 0x06, 0xa3, 0xf1, 0x0b, 0x5f, 0x6c, 0x5c, 0xb3, 0xec,
            0x77, 0x61, 0x69, 0xe7, 0x3c, 0xb7, 0x42, 0x72, 0x1a, 0x70, 0xb0, 0x96, 0xa4, 0x28, 0xc0, 0xfb,
            0x0a, 0x00, 0xcb, 0x15, 0x49, 0x48, 0xd3, 0x94, 0x58, 0xcf, 0x41, 0x86, 0x17, 0x71, 0xb1, 0xbd,
            0x21, 0x01, 0x37, 0x1e, 0xba, 0xeb, 0xf3, 0x59, 0xf6, 0xa7, 0x29, 0x4f, 0xb5, 0xca, 0x4c, 0x34,
            0x20, 0xa2, 0x62, 0x4b, 0x93, 0x9e, 0x47, 0x9f, 0x8d, 0x0e, 0x1b, 0xb6, 0x4d, 0x82, 0xd5, 0xf4,
            0x85, 0x79, 0x53, 0x92, 0x9b, 0xf7, 0xea, 0x44, 0x76, 0x1f, 0x22, 0x45, 0xed, 0xbe, 0x11, 0x55,
            0xaf, 0xf5, 0xf8, 0x50, 0x07, 0xe6, 0xc7, 0x5e, 0xd7, 0xde, 0xe5, 0x26, 0x2b, 0xf2, 0x6a, 0x8b,
            0xb8, 0x98, 0x89, 0xdb, 0x14, 0x5b, 0xc5, 0x78, 0xdc, 0xd0, 0x87, 0x5d, 0xc1, 0x0d, 0x95, 0x97,
            0x7e, 0xa8, 0x24, 0x3d, 0xe1, 0xd1, 0x19, 0xa6, 0x99, 0xd8, 0x83, 0x1d, 0xff, 0x30, 0x9d, 0x05,
            0xd4, 0x02, 0x27, 0x7b, 0x13, 0xb2, 0x7f, 0x40, 0x12, 0xa0, 0x68, 0x67, 0x4e, 0x3a, 0x46, 0xb9,
            0xee, 0xdf, 0x66, 0xd6, 0x8f, 0xa9, 0x0c, 0x91, 0x65, 0x18, 0x52, 0x56, 0xd9, 0x74, 0x09, 0x6e,
            0xc6, 0x73, 0xc9, 0xfc, 0x03, 0x43, 0xef, 0xaa, 0x7c, 0xbb, 0x2c, 0x90, 0xcc, 0xce, 0xe8, 0xae,
            0x2a, 0xf9, 0x57, 0x88, 0xc8, 0xe9, 0x5a, 0xdd, 0x2e, 0x7d, 0x64, 0xc2, 0x6d, 0x3e, 0xfa, 0x80,
            0x16, 0xcd, 0x6f, 0x84, 0x8e, 0x9c, 0xf0, 0xac, 0xb4, 0x9a, 0x2f, 0xbc, 0x31, 0x23, 0xfe, 0x38,
            0x08, 0x75, 0xa1, 0x33, 0xab, 0xd2, 0xda, 0x81, 0xbf, 0x7a, 0x3b, 0x3f, 0x4a, 0xfd, 0x25, 0x36
        };

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

        static readonly u8 [] Py2k2Tab =
        {
            0x74, 0xe8, 0xa8, 0x64, 0x26, 0x44, 0xa6, 0x9a, 0xa5, 0x69, 0xa2, 0xd3, 0x6d, 0xba, 0xff, 0xf3,
            0xeb, 0x6e, 0xe3, 0x70, 0x72, 0x58, 0x27, 0xd9, 0xe4, 0x9f, 0x50, 0xa2, 0xdd, 0xce, 0x6e, 0xf6,
            0x44, 0x72, 0x0c, 0x7e, 0x4d, 0x41, 0x77, 0x2d, 0x00, 0xad, 0x1a, 0x5f, 0x6b, 0xc0, 0x1d, 0x4e,
            0x4c, 0x72, 0x62, 0x3c, 0x32, 0x28, 0x43, 0xf8, 0x9d, 0x52, 0x05, 0x7e, 0xd1, 0xee, 0x82, 0x61,
            0x3b, 0x3f, 0x77, 0xf3, 0x8f, 0x7e, 0x3f, 0xf1, 0xdf, 0x8f, 0x68, 0x43, 0xd7, 0x68, 0xdf, 0x19,
            0x87, 0xff, 0x74, 0xe5, 0x3f, 0x43, 0x8e, 0x80, 0x0f, 0x7e, 0xdb, 0x32, 0xe8, 0xd1, 0x66, 0x8f,
            0xbe, 0xe2, 0x33, 0x94, 0xc8, 0x32, 0x39, 0xfa, 0xf0, 0x43, 0xde, 0x84, 0x18, 0xd0, 0x6d, 0xd5,
            0x74, 0x98, 0xf8, 0x64, 0xcf, 0x84, 0xc6, 0xea, 0x55, 0x32, 0xe2, 0x38, 0xdd, 0xea, 0xfd, 0x6c,
            0xeb, 0x6e, 0xe3, 0x70, 0xae, 0x38, 0xc7, 0xd9, 0x54, 0x84, 0x10, 0xc1, 0xfd, 0x1e, 0x6e, 0x6d,
            0x37, 0xe0, 0x03, 0x9e, 0x06, 0x36, 0x68, 0x5b, 0xe3, 0xf6, 0x7f, 0x0b, 0x56, 0x79, 0xe0, 0xa8,
            0x98, 0x77, 0xc7, 0x2b, 0xa5, 0x79, 0xff, 0x2f, 0xca, 0x15, 0x71, 0x7e, 0x02, 0xbf, 0x87, 0xb7,
            0x7a, 0x8e, 0xe6, 0x64, 0x32, 0x62, 0x2a, 0xca, 0x23, 0x72, 0x87, 0xb5, 0x0c, 0x02, 0x4b, 0xee,
            0x44, 0x72, 0x9c, 0x7e, 0x5d, 0xc1, 0xa7, 0x1d, 0x30, 0x38, 0xda, 0xc9, 0x5b, 0xd0, 0x11, 0xf9,
            0xb1, 0x72, 0x6c, 0x04, 0x31, 0xc9, 0x50, 0x60, 0x6f, 0xc1, 0xf2, 0xae, 0x00, 0xf4, 0x5d, 0x66,
            0x43, 0x0e, 0x7a, 0xc3, 0x76, 0xae, 0x3c, 0xc2, 0xb7, 0xc9, 0x52, 0xf4, 0x74, 0x51, 0xaf, 0x12,
            0x19, 0xc6, 0x75, 0xe8, 0x6c, 0x54, 0x7e, 0x63, 0xdd, 0xae, 0x07, 0x5a, 0xb7, 0x00, 0xb5, 0x5e
        };

        static readonly u8 [] KetTab =
        {
            0x49, 0x47, 0x53, 0x30, 0x30, 0x30, 0x34, 0x52, 0x44, 0x31, 0x30, 0x32, 0x31, 0x30, 0x31, 0x35,
            0x7c, 0x49, 0x27, 0xa5, 0xff, 0xf6, 0x98, 0x2d, 0x0f, 0x3d, 0x12, 0x23, 0xe2, 0x30, 0x50, 0xcf,
            0xf1, 0x82, 0xf0, 0xce, 0x48, 0x44, 0x5b, 0xf3, 0x0d, 0xdf, 0xf8, 0x5d, 0x50, 0x53, 0x91, 0xd9,
            0x12, 0xaf, 0x05, 0x7a, 0x98, 0xd0, 0x2f, 0x76, 0xf1, 0x5d, 0x17, 0x44, 0xc5, 0x03, 0x58, 0xf4,
            0x61, 0xee, 0xd1, 0xce, 0x00, 0x88, 0x90, 0x2e, 0x5c, 0x76, 0xfb, 0x9f, 0x75, 0xcf, 0x40, 0x37,
            0xa1, 0x9f, 0x00, 0x32, 0xd5, 0x9c, 0x37, 0xd2, 0x32, 0x27, 0x6f, 0x76, 0xd3, 0x86, 0x25, 0xf9,
            0xd6, 0x60, 0x7b, 0x4e, 0xa9, 0x7a, 0x20, 0x59, 0x96, 0xb1, 0x7d, 0x10, 0x92, 0x37, 0x22, 0xd2,
            0x42, 0x12, 0x6f, 0x07, 0x4f, 0xd2, 0x87, 0xfa, 0xeb, 0x92, 0x71, 0xf3, 0xa4, 0x31, 0x91, 0x98,
            0x68, 0xd2, 0x47, 0x86, 0xda, 0x92, 0xe5, 0x2b, 0xd4, 0x89, 0xd7, 0xe7, 0x3d, 0x03, 0x0d, 0x63,
            0x0c, 0x00, 0xac, 0x31, 0x9d, 0xe9, 0xf6, 0xa5, 0x34, 0x95, 0x77, 0xf2, 0xcf, 0x7c, 0x72, 0x89,
            0x31, 0x3a, 0x8b, 0xae, 0x2b, 0x47, 0xb6, 0x5d, 0x2d, 0xf5, 0x5f, 0x5c, 0x0e, 0xab, 0xdb, 0xa1,
            0x18, 0x60, 0x0e, 0xe6, 0x58, 0x5b, 0x5e, 0x8b, 0x24, 0x29, 0xd8, 0xac, 0xed, 0xdf, 0xa2, 0x83,
            0x46, 0x91, 0xa1, 0xff, 0x35, 0x13, 0x6a, 0xa5, 0xba, 0xef, 0x6e, 0xa8, 0x9e, 0xa6, 0x62, 0x44,
            0x7e, 0x2c, 0xed, 0x60, 0x17, 0x9e, 0x96, 0x64, 0xd3, 0x46, 0xec, 0x58, 0x95, 0xd1, 0xf7, 0x3e,
            0xc2, 0xcf, 0xdf, 0xb0, 0x90, 0x6c, 0xdb, 0xbe, 0x93, 0x6d, 0x5d, 0x02, 0x85, 0x6e, 0x7c, 0x05,
            0x55, 0x5a, 0xa1, 0xd7, 0x73, 0x2b, 0x76, 0xe9, 0x5b, 0xe4, 0x0c, 0x2e, 0x60, 0xcb, 0x4b, 0x72
        };

        static readonly u8 [] EspgalTab =
        {
            0x49, 0x47, 0x53, 0x30, 0x30, 0x30, 0x37, 0x52, 0x44, 0x31, 0x30, 0x33, 0x30, 0x39, 0x30, 0x39,
            0xa7, 0xf1, 0x0a, 0xca, 0x69, 0xb2, 0xce, 0x86, 0xec, 0x3d, 0xa2, 0x5a, 0x03, 0xe9, 0xbf, 0xba,
            0xf7, 0xd5, 0xec, 0x68, 0x03, 0x90, 0x15, 0xcc, 0x0d, 0x08, 0x2d, 0x76, 0xa5, 0xb5, 0x41, 0xf1,
            0x43, 0x06, 0xdd, 0xcb, 0xbd, 0x0c, 0xa4, 0xe2, 0x08, 0x65, 0x2a, 0xf0, 0x30, 0x6b, 0x15, 0x59,
            0x99, 0x9e, 0x75, 0x35, 0x77, 0x4f, 0x60, 0x99, 0x8c, 0x8f, 0xd2, 0x2b, 0x21, 0x57, 0xc3, 0xe5,
            0x48, 0xf9, 0x8a, 0x29, 0x50, 0xc6, 0x71, 0x06, 0x89, 0x01, 0x9a, 0xc9, 0x39, 0x04, 0x12, 0xc8,
            0xdf, 0xb1, 0x33, 0x6b, 0xa7, 0x1c, 0x3f, 0x7b, 0x2d, 0x76, 0x3a, 0xaf, 0x76, 0x3d, 0x08, 0x74,
            0x2c, 0xa2, 0xc8, 0xfd, 0x1a, 0x3a, 0x6f, 0x8b, 0xe8, 0xe9, 0xa9, 0xfe, 0x17, 0x0c, 0xed, 0x9d,
            0x40, 0xe6, 0xdf, 0x22, 0x89, 0x4d, 0xea, 0x09, 0x68, 0x96, 0x1e, 0x1a, 0x9c, 0xbd, 0x47, 0x35,
            0x68, 0xd9, 0x4f, 0x5e, 0x12, 0xbf, 0xd6, 0x09, 0x9d, 0xf6, 0x0f, 0xa7, 0xc2, 0xdb, 0xde, 0x70,
            0x35, 0x15, 0x2f, 0x73, 0x16, 0x3c, 0x9a, 0xdc, 0xb5, 0xc5, 0x35, 0x86, 0x8a, 0x31, 0xb8, 0xc1,
            0x74, 0x76, 0xd7, 0x65, 0x32, 0xad, 0xdc, 0x17, 0x1f, 0xfe, 0x85, 0xda, 0x32, 0xc9, 0x1d, 0xda,
            0x36, 0x16, 0xde, 0x76, 0x45, 0x3f, 0x85, 0x8c, 0x8b, 0xdc, 0x37, 0x08, 0x39, 0xef, 0x94, 0xaf,
            0xc8, 0x51, 0x19, 0x29, 0x70, 0x5d, 0xbb, 0x4e, 0xe8, 0xdb, 0xc2, 0xb2, 0x5f, 0x2e, 0xe3, 0x73,
            0xba, 0xc2, 0xa1, 0x42, 0x10, 0xb0, 0xe5, 0xb0, 0x64, 0xb4, 0xdc, 0xbb, 0xa1, 0x51, 0x12, 0x98,
            0xdc, 0x43, 0xcc, 0xc3, 0xc5, 0x25, 0xab, 0x45, 0x6e, 0x63, 0x7e, 0x45, 0x40, 0x63, 0x67, 0xd2
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

        public static void DemonFrontDecrypt(MemoryU8 rom, int offset, int length)
        {
            int words = Math.Min(length, rom.Count - offset) / 2;
            for (int i = 0; i < words; i++)
            {
                int byteOffset = offset + i * 2;
                u16 x = ReadLeWord(rom, byteOffset);

                if ((i & 0x040080) != 0x000080) x ^= 0x0001;
                if ((i & 0x104008) == 0x104008) x ^= 0x0002;
                if ((i & 0x080030) == 0x080010) x ^= 0x0004;
                if ((i & 0x000042) != 0x000042) x ^= 0x0008;
                if ((i & 0x008100) == 0x008000) x ^= 0x0010;
                if ((i & 0x002004) != 0x000004) x ^= 0x0020;
                if ((i & 0x011800) != 0x010000) x ^= 0x0040;
                if ((i & 0x004820) == 0x004820) x ^= 0x0080;
                x ^= (u16)(DemonFrontTab[(i >> 1) & 0xff] << 8);

                WriteLeWord(rom, byteOffset, x);
            }
        }

        public static void TheGladDecrypt(MemoryU8 rom, int offset, int length)
        {
            int words = Math.Min(length, rom.Count - offset) / 2;
            for (int i = 0; i < words; i++)
            {
                int byteOffset = offset + i * 2;
                u16 x = ReadLeWord(rom, byteOffset);

                if ((i & 0x040080) != 0x000080) x ^= 0x0001;
                if ((i & 0x104008) == 0x104008) x ^= 0x0002;
                if ((i & 0x080030) == 0x080010) x ^= 0x0004;
                if ((i & 0x000042) != 0x000042) x ^= 0x0008;
                if ((i & 0x008100) == 0x008000) x ^= 0x0010;
                if ((i & 0x022004) != 0x000004) x ^= 0x0020;
                if ((i & 0x011800) != 0x010000) x ^= 0x0040;
                if ((i & 0x000820) == 0x000820) x ^= 0x0080;
                x ^= (u16)(TheGladTab[(i >> 1) & 0xff] << 8);

                WriteLeWord(rom, byteOffset, x);
            }
        }

        public static void Py2k2Decrypt(MemoryU8 rom, int offset, int length)
        {
            Decrypt(rom, offset, length, Py2k2Tab, i =>
            {
                u16 x = 0;
                if ((i & 0x040480) != 0x000080) x ^= 0x0001;
                if ((i & 0x084008) == 0x084008) x ^= 0x0002;
                if ((i & 0x000030) == 0x000010 && (i & 0x180000) != 0x080000) x ^= 0x0004;
                if ((i & 0x000042) != 0x000042) x ^= 0x0008;
                if ((i & 0x008100) == 0x008000) x ^= 0x0010;
                if ((i & 0x022004) != 0x000004) x ^= 0x0020;
                if ((i & 0x011800) != 0x010000) x ^= 0x0040;
                if ((i & 0x004820) == 0x004820) x ^= 0x0080;
                return x;
            });
        }

        public static void KetDecrypt(MemoryU8 rom, int offset, int length)
        {
            Decrypt(rom, offset, length, KetTab, i =>
            {
                u16 x = 0;
                if ((i & 0x040480) != 0x000080) x ^= 0x0001;
                if ((i & 0x004008) == 0x004008) x ^= 0x0002;
                if ((i & 0x080030) == 0x000010) x ^= 0x0004;
                if ((i & 0x000042) != 0x000042) x ^= 0x0008;
                if ((i & 0x008100) == 0x008000) x ^= 0x0010;
                if ((i & 0x002004) != 0x000004) x ^= 0x0020;
                if ((i & 0x011800) != 0x010000) x ^= 0x0040;
                if ((i & 0x000820) == 0x000820) x ^= 0x0080;
                return x;
            });
        }

        public static void EspgalDecrypt(MemoryU8 rom, int offset, int length)
        {
            Decrypt(rom, offset, length, EspgalTab, i =>
            {
                u16 x = 0;
                if ((i & 0x040480) != 0x000080) x ^= 0x0001;
                if ((i & 0x084008) == 0x084008) x ^= 0x0002;
                if ((i & 0x000030) == 0x000010) x ^= 0x0004;
                if ((i & 0x000042) != 0x000042) x ^= 0x0008;
                if ((i & 0x048100) == 0x048000) x ^= 0x0010;
                if ((i & 0x022004) != 0x000004) x ^= 0x0020;
                if ((i & 0x011800) != 0x010000) x ^= 0x0040;
                if ((i & 0x000820) == 0x000820) x ^= 0x0080;
                return x;
            });
        }

        public static void Kov2Decrypt(MemoryU8 rom, int offset, int length)
        {
            int words = Math.Min(length, rom.Count - offset) / 2;
            for (int i = 0; i < words; i++)
            {
                int byteOffset = offset + i * 2;
                u16 x = ReadLeWord(rom, byteOffset);

                if ((i & 0x040080) != 0x000080) x ^= 0x0001;
                if ((i & 0x080030) == 0x080010) x ^= 0x0004;
                if ((i & 0x000042) != 0x000042) x ^= 0x0008;
                if ((i & 0x048100) == 0x048000) x ^= 0x0010;
                if ((i & 0x022004) != 0x000004) x ^= 0x0020;
                if ((i & 0x001800) != 0x000000) x ^= 0x0040;
                if ((i & 0x000820) == 0x000820) x ^= 0x0080;

                WriteLeWord(rom, byteOffset, x);
            }
        }

        static void Decrypt(MemoryU8 rom, int offset, int length, u8 [] table, Func<int, u16> bitDecrypt)
        {
            int words = Math.Min(length, rom.Count - offset) / 2;
            for (int i = 0; i < words; i++)
            {
                int byteOffset = offset + i * 2;
                u16 x = ReadLeWord(rom, byteOffset);
                x ^= bitDecrypt(i);
                x ^= (u16)(table[i & 0xff] << 8);
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
