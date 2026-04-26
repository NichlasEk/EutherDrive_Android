// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using mame.eutherdrive_m68000;

using offs_t = System.UInt32;  //using offs_t = u32;
using stream_buffer_sample_t = System.Single;
using uint8_t = System.Byte;
using uint16_t = System.UInt16;
using uint32_t = System.UInt32;

using static mame._6821pia_global;
using static mame.ay8910_global;
using static mame.dac_global;
using static mame.device_global;
using static mame.diexec_global;
using static mame.disound_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.flt_biquad_global;
using static mame.hash_global;
using static mame.m6809_global;
using static mame.midway_global;
using static mame.rescap_global;
using static mame.romentry_global;
using static mame.z80_global;


namespace mame
{
    //**************************************************************************
    //  TYPE DEFINITIONS
    //**************************************************************************

    // ======================> midway_ssio_device
    public class midway_ssio_device : device_t
                                      //device_mixer_interface
    {
        //DEFINE_DEVICE_TYPE(MIDWAY_SSIO,               midway_ssio_device,               "midssio", "Midway SSIO Sound Board")
        public static readonly emu.detail.device_type_impl MIDWAY_SSIO = DEFINE_DEVICE_TYPE("midssio", "Midway SSIO Sound Board", (type, mconfig, tag, owner, clock) => { return new midway_ssio_device(mconfig, tag, owner, clock); });


        device_mixer_interface m_dimixer;


        // devices
        required_device<z80_device> m_cpu;
        required_device<ay8910_device> m_ay0;
        required_device<ay8910_device> m_ay1;

        // I/O ports
        optional_ioport_array<u32_const_5> m_ports;

        // internal state
        uint8_t [] m_data = new uint8_t [4];
        uint8_t m_status;
        uint8_t m_14024_count;
        uint8_t m_mute;
        uint8_t [] m_overall = new uint8_t [2];
        uint8_t [,] m_duty_cycle = new uint8_t [2, 3];
        uint8_t [] m_ayvolume_lookup = new uint8_t [16];

        // I/O port overrides
        uint8_t [] m_custom_input_mask = new uint8_t [5];
        read8smo_delegate [] m_custom_input = new read8smo_delegate [5];  //read8smo_delegate::array<5> m_custom_input;
        uint8_t [] m_custom_output_mask = new uint8_t [2];
        write8smo_delegate [] m_custom_output = new write8smo_delegate [2];  //write8smo_delegate::array<2> m_custom_output;


        // construction/destruction
        midway_ssio_device(machine_config mconfig, string tag, device_t owner, uint32_t clock = 16_000_000)
            : base(mconfig, MIDWAY_SSIO, tag, owner, clock)
        {
            m_class_interfaces.Add(new device_mixer_interface(mconfig, this, 2));  //, device_mixer_interface(mconfig, *this, 2)
            m_dimixer = GetClassInterface<device_mixer_interface>();


            m_cpu = new required_device<z80_device>(this, "cpu");
            m_ay0 = new required_device<ay8910_device>(this, "ay0");
            m_ay1 = new required_device<ay8910_device>(this, "ay1");
            m_ports = new optional_ioport_array<u32_const_5>(this, "IP{0}", 0U);
            m_status = 0;
            m_14024_count = 0;
            m_mute = 0;
            m_custom_input_mask = new uint8_t [] { 0, 0, 0, 0, 0 };
            m_custom_input = new read8smo_delegate [5];
            m_custom_output_mask = new uint8_t [] { 0, 0 };
            m_custom_output = new write8smo_delegate [2];


            std.fill(m_data, (uint8_t)0);
            std.fill(m_overall, (uint8_t)0);
            //for (auto &duty_cycle : m_duty_cycle)
            //    std::fill(std::begin(duty_cycle), std::end(duty_cycle), 0);
            std.fill(m_duty_cycle, (uint8_t)0);
            std.fill(m_ayvolume_lookup, (uint8_t)0);
        }


        public device_mixer_interface dimixer { get { return m_dimixer; } }


        // helpers
        public void suspend_cpu()
        {
            m_cpu.op0.execute().suspend(SUSPEND_REASON_DISABLE, true);
        }


        // read/write
        uint8_t read() { throw new emu_unimplemented(); }
        void write(offs_t offset, uint8_t data) { throw new emu_unimplemented(); }
        //DECLARE_WRITE_LINE_MEMBER(reset_write);


        uint8_t ioport_read(offs_t offset)
        {
            uint8_t result = (uint8_t)m_ports[(int)offset].read_safe(0xff);
            if (m_custom_input[offset] != null)
                result = (uint8_t)((result & ~m_custom_input_mask[offset]) |
                                   (m_custom_input[offset]() & m_custom_input_mask[offset]));
            return result;
        }


        void ioport_write(offs_t offset, uint8_t data)
        {
            int which = (int)(offset >> 2);
            if (m_custom_output[which] != null)
                m_custom_output[which]((uint8_t)(data & m_custom_output_mask[which]));
        }


        // configuration

        //template <typename... T>
        public void set_custom_input(int which, uint8_t mask, object owner, read8smo_delegate callback)  //void set_custom_input(int which, uint8_t mask, T &&... args)
        {
            m_custom_input_mask[which] = mask;
            m_custom_input[which] = callback;  //m_custom_input[which].set(std::forward<T>(args)...);
        }


        //template <typename... T>
        public void set_custom_output(int which, uint8_t mask, object owner, write8smo_delegate callback)  //void set_custom_output(int which, uint8_t mask, T &&... args)
        {
            m_custom_output_mask[which / 4] = mask;
            m_custom_output[which / 4] = callback;  //m_custom_output[which / 4].set(std::forward<T>(args)...);
        }


        // internal communications
        uint8_t irq_clear() { throw new emu_unimplemented(); }
        void status_w(uint8_t data) { throw new emu_unimplemented(); }
        uint8_t data_r(offs_t offset) { throw new emu_unimplemented(); }


        void ssio_map(address_map map, device_t device)
        {
            map.unmap_value_high();
            map.op(0x0000, 0x3fff).rom();
            map.op(0x8000, 0x83ff).mirror(0x0c00).ram();
            map.op(0x9000, 0x9003).mirror(0x0ffc).r(data_r);
            map.op(0xa000, 0xa000).mirror(0x0ffc).w("ay0", (data) => { ((ay8910_device)subdevice("ay0")).address_w(data); });
            map.op(0xa001, 0xa001).mirror(0x0ffc).r("ay0", () => { return ((ay8910_device)subdevice("ay0")).data_r(); });
            map.op(0xa002, 0xa002).mirror(0x0ffc).w("ay0", (data) => { ((ay8910_device)subdevice("ay0")).data_w(data); });
            map.op(0xb000, 0xb000).mirror(0x0ffc).w("ay1", (data) => { ((ay8910_device)subdevice("ay1")).address_w(data); });
            map.op(0xb001, 0xb001).mirror(0x0ffc).r("ay1", () => { return ((ay8910_device)subdevice("ay1")).data_r(); });
            map.op(0xb002, 0xb002).mirror(0x0ffc).w("ay1", (data) => { ((ay8910_device)subdevice("ay1")).data_w(data); });
            map.op(0xc000, 0xcfff).nopr().w(status_w);
            map.op(0xd000, 0xdfff).nopw();    // low bit controls yellow LED
            map.op(0xe000, 0xefff).r(irq_clear);
            map.op(0xf000, 0xffff).portr("DIP");    // 6 DIP switches
        }


        public static void ssio_input_ports(address_map map, string ssio, device_t device)
        {
            map.op(0x00, 0x04).mirror(0x18).r(ssio, (offset) => { return ((midway_ssio_device)device.subdevice(ssio)).ioport_read(offset); });
            map.op(0x07, 0x07).mirror(0x18).r(ssio, () => { return ((midway_ssio_device)device.subdevice(ssio)).read(); });
            map.op(0x00, 0x07).w(ssio, (offset, data) => { ((midway_ssio_device)device.subdevice(ssio)).ioport_write(offset, data); });
            map.op(0x1c, 0x1f).w(ssio, (offset, data) => { ((midway_ssio_device)device.subdevice(ssio)).write(offset, data); });
        }


        //-------------------------------------------------
        //  ROM definitions
        //-------------------------------------------------

        //ROM_START( midway_ssio )
        static readonly tiny_rom_entry [] rom_midway_ssio =
        {
            ROM_REGION( 0x0020, "proms", 0 ),
            ROM_LOAD( "82s123.12d",   0x0000, 0x0020, CRC("e1281ee9") + SHA1("9ac9b01d24affc0ee9227a4364c4fd8f8290343a") ),    /* from shollow, assuming it's the same */

            ROM_END,
        };


        // device-level overrides
        protected override Pointer<tiny_rom_entry> device_rom_region()
        {
            return new Pointer<tiny_rom_entry>(new MemoryContainer<tiny_rom_entry>(rom_midway_ssio));  //return ROM_NAME(midway_ssio);
        }


        protected override void device_add_mconfig(machine_config config)
        {
            Z80(config, m_cpu, DERIVED_CLOCK(1, 2*4));
            m_cpu.op0.memory().set_addrmap(AS_PROGRAM, ssio_map);
            if (clock() != 0)
                m_cpu.op0.execute().set_periodic_int(DEVICE_SELF, clock_14024, attotime.from_hz(clock() / (2*16*10)));

            AY8910(config, m_ay0, DERIVED_CLOCK(1, 2*4));
            m_ay0.op0.port_a_write_callback().set(porta0_w).reg();
            m_ay0.op0.port_b_write_callback().set(portb0_w).reg();
            m_ay0.op0.add_route(ALL_OUTPUTS, this.dimixer, 0.33, AUTO_ALLOC_INPUT, 0);

            AY8910(config, m_ay1, DERIVED_CLOCK(1, 2*4));
            m_ay1.op0.port_a_write_callback().set(porta1_w).reg();
            m_ay1.op0.port_b_write_callback().set(portb1_w).reg();
            m_ay1.op0.add_route(ALL_OUTPUTS, this.dimixer, 0.33, AUTO_ALLOC_INPUT, 1);
        }


        protected override ioport_constructor device_input_ports() { return null; }


        protected override void device_start()
        {
            compute_ay8910_modulation();
            save_item(NAME(new { m_data }));
            save_item(NAME(new { m_status }));
            save_item(NAME(new { m_14024_count }));
            save_item(NAME(new { m_mute }));
            save_item(NAME(new { m_overall }));
            save_item(NAME(new { m_duty_cycle }));
        }


        protected override void device_reset()
        {
            // latches also get reset
            std.memset(m_data, (uint8_t)0);
            m_status = 0;
            m_14024_count = 0;
        }


        //TIMER_CALLBACK_MEMBER(synced_write);


        // internal helpers
        void compute_ay8910_modulation()
        {
            //
            // AY-8910 modulation:
            //
            // Starts with a 16MHz oscillator
            //  /2 via 7474 flip-flip @ F11
            //
            // This signal clocks the binary counter @ E11 which
            // cascades into the decade counter @ D11. This combo
            // effectively counts from 0-159 and then wraps. The
            // value from these counters is input to an 82S123 PROM,
            // which appears to be standard on all games.
            //
            // One bit at a time from this PROM is clocked at a time
            // and the resulting inverted signal becomes a clock for
            // the down counters at F3, F4, F5, F8, F9, and F10. The
            // value in these down counters are reloaded after the 160
            // counts from the binary/decade counter combination.
            //
            // When these down counters are loaded, the TC signal is
            // clear, which mutes the voice. When the down counters
            // cross through 0, the TC signal goes high and the 4016
            // multiplexers allow the AY-8910 voice to go through.
            // Thus, writing a 0 to the counters will enable the
            // voice for the longest period of time, while writing
            // a 15 enables it for the shortest period of time.
            // This creates an effective duty cycle for the voice.
            //
            // Given that the down counters are reset 50000 times per
            // second (SSIO_CLOCK/2/160), which is above the typical
            // frequency of sound output. So we simply apply a volume
            // adjustment to each voice according to the duty cycle.
            //

            // loop over all possible values of the duty cycle
            Pointer<uint8_t> prom = new Pointer<uint8_t>(memregion("proms").base_());  //uint8_t *prom = memregion("proms")->base();
            for (int volval = 0; volval < 16; volval++)
            {
                // loop over all the clocks until we run out; look up in the PROM
                // to find out when the next clock should fire
                int remaining_clocks = volval;
                int cur = 0, prev = 1;
                int curclock;
                for (curclock = 0; curclock < 160 && remaining_clocks != 0; curclock++)
                {
                    cur = prom[curclock / 8] & (0x80 >> (curclock % 8));

                    // check for a high -> low transition
                    if (cur == 0 && prev != 0)
                        remaining_clocks--;

                    prev = cur;
                }

                // treat the duty cycle as a volume
                m_ayvolume_lookup[15 - volval] = (uint8_t)(curclock * 100 / 160);
            }
        }


        void update_volumes()
        {
            m_ay0.op0.set_volume(0, m_mute != 0 ? 0 : m_ayvolume_lookup[m_duty_cycle[0, 0]]);
            m_ay0.op0.set_volume(1, m_mute != 0 ? 0 : m_ayvolume_lookup[m_duty_cycle[0, 1]]);
            m_ay0.op0.set_volume(2, m_mute != 0 ? 0 : m_ayvolume_lookup[m_duty_cycle[0, 2]]);
            m_ay1.op0.set_volume(0, m_mute != 0 ? 0 : m_ayvolume_lookup[m_duty_cycle[1, 0]]);
            m_ay1.op0.set_volume(1, m_mute != 0 ? 0 : m_ayvolume_lookup[m_duty_cycle[1, 1]]);
            m_ay1.op0.set_volume(2, m_mute != 0 ? 0 : m_ayvolume_lookup[m_duty_cycle[1, 2]]);
        }


        //INTERRUPT_GEN_MEMBER(clock_14024);
        void clock_14024(device_t device) { throw new emu_unimplemented(); }


        void porta0_w(uint8_t data)
        {
            m_duty_cycle[0, 0] = (uint8_t)(data & 15);
            m_duty_cycle[0, 1] = (uint8_t)(data >> 4);
            update_volumes();
        }


        void portb0_w(uint8_t data)
        {
            m_duty_cycle[0, 2] = (uint8_t)(data & 15);
            m_overall[0] = (uint8_t)((data >> 4) & 7);
            update_volumes();
        }


        void porta1_w(uint8_t data)
        {
            m_duty_cycle[1, 0] = (uint8_t)(data & 15);
            m_duty_cycle[1, 1] = (uint8_t)(data >> 4);
            update_volumes();
        }


        void portb1_w(uint8_t data)
        {
            m_duty_cycle[1, 2] = (uint8_t)(data & 15);
            m_overall[1] = (uint8_t)((data >> 4) & 7);
            m_mute = (uint8_t)(data & 0x80);
            update_volumes();
        }
    }


    // ======================> midway_sounds_good_device
    public class midway_sounds_good_device : device_t
    {
        //DEFINE_DEVICE_TYPE(MIDWAY_SOUNDS_GOOD,        midway_sounds_good_device,        "midsg",   "Midway Sounds Good Sound Board")
        public static readonly emu.detail.device_type_impl MIDWAY_SOUNDS_GOOD = DEFINE_DEVICE_TYPE("midsg", "Midway Sounds Good Sound Board", (type, mconfig, tag, owner, clock) => { return new midway_sounds_good_device(mconfig, tag, owner, clock); });


        public class device_sound_interface_sounds_good : device_sound_interface
        {
            public device_sound_interface_sounds_good(machine_config mconfig, device_t device) : base(mconfig, device) { }

            public override void sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
            {
                ((midway_sounds_good_device)device()).device_sound_interface_sound_stream_update(stream, inputs, outputs);
            }
        }


        const uint32_t SoundCpuClock = 8_000_000;
        const uint32_t OutputSampleRate = 48_000;

        device_sound_interface_sounds_good m_disound;
        sound_stream m_stream;
        M68000 m_cpu;
        SoundsGoodBus m_bus;

        uint8_t m_status;
        uint16_t m_dacval;
        stream_buffer_sample_t m_dac_sample;
        double m_cycle_remainder;
        bool m_reset_asserted;
        bool m_cpu_started;
        static readonly bool s_trace_sounds_good = Environment.GetEnvironmentVariable("EUTHERDRIVE_MCS_SG_TRACE") == "1";
        static int s_trace_sounds_good_remaining = 240;


        // construction/destruction
        midway_sounds_good_device(machine_config mconfig, string tag, device_t owner, uint32_t clock = 16_000_000)
            : base(mconfig, MIDWAY_SOUNDS_GOOD, tag, owner, clock)
        {
            m_class_interfaces.Add(new device_sound_interface_sounds_good(mconfig, this));
            m_disound = GetClassInterface<device_sound_interface_sounds_good>();
            m_cpu = M68000.CreateBuilder().AllowTasWrites(true).Name("MCS-SoundsGood").Build();
            m_bus = new SoundsGoodBus(this);
            m_status = 0;
            m_dacval = 0;
            m_dac_sample = 0;
            m_cycle_remainder = 0;
            m_reset_asserted = false;
            m_cpu_started = false;
        }


        public device_sound_interface_sounds_good disound { get { return m_disound; } }


        // read/write
        public uint8_t read() { return m_status; }


        public void write(uint8_t data)
        {
            m_stream?.update();
            m_bus.WriteCommand(data);
        }


        public void reset_write(int state)
        {
            bool asserted = state != 0;
            if (m_reset_asserted == asserted)
                return;

            m_stream?.update();
            m_reset_asserted = asserted;
            if (m_reset_asserted)
            {
                m_status = 0;
                m_bus.ResetPia();
            }
            else
            {
                ResetSoundCpu();
            }
        }


        void set_status_bit(uint8_t bit, uint8_t value)
        {
            uint8_t mask = (uint8_t)(1 << bit);
            m_status = (uint8_t)((m_status & ~mask) | ((value != 0) ? mask : 0));
        }


        void write_dac(uint16_t data)
        {
            m_dacval = (uint16_t)(data & 0x03ff);
            m_dac_sample = (stream_buffer_sample_t)(((m_dacval - 512) / 512.0) * 0.75);
            TraceSoundsGood("dac=" + m_dacval.ToString("X3") + " sample=" + m_dac_sample.ToString("0.000"));
        }


        void ResetSoundCpu()
        {
            if (m_bus.RomLength < 8)
                return;

            m_bus.ClearResetPulse();
            m_cpu.Reset(m_bus);
            m_cpu_started = true;
            m_cycle_remainder = 0;
            TraceSoundsGood("cpu reset pc=0x" + m_cpu.Pc.ToString("X6") + " sp=0x" + m_cpu.Ssp.ToString("X6"));
        }


        void RunSoundCycles(uint32_t cycles)
        {
            if (m_reset_asserted || !m_cpu_started)
                return;

            uint32_t remaining = cycles;
            int guard = 0;
            while (remaining != 0 && guard++ < 4096)
            {
                uint32_t used = m_cpu.ExecuteInstruction(m_bus);
                if (guard == 1)
                    TraceSoundsGood("run pc=0x" + m_cpu.Pc.ToString("X6") + " irq=" + m_bus.InterruptLevel().ToString());
                if (used == 0)
                    used = 1;
                if (used >= remaining)
                    break;
                remaining -= used;
            }
        }


        static void TraceSoundsGood(string message)
        {
            if (!s_trace_sounds_good || s_trace_sounds_good_remaining-- <= 0)
                return;
            Console.WriteLine("[MCS-SG] " + message);
        }


        protected override void device_add_mconfig(machine_config config) { }


        protected override void device_start()
        {
            m_stream = m_disound.stream_alloc(0, 1, OutputSampleRate);
            m_bus.LoadRom(memregion("cpu"));
            ResetSoundCpu();

            save_item(NAME(new { m_status }));
            save_item(NAME(new { m_dacval }));
        }


        protected override void device_reset()
        {
            m_status = 0;
            m_dacval = 0;
            m_dac_sample = 0;
            m_cycle_remainder = 0;
            m_reset_asserted = false;
            m_bus.ResetPia();
            ResetSoundCpu();
        }


        void device_sound_interface_sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
        {
            var output = outputs[0];
            double cyclesPerSample = SoundCpuClock / (double)OutputSampleRate;
            for (int sample = 0; sample < output.samples(); sample++)
            {
                m_cycle_remainder += cyclesPerSample;
                uint32_t wholeCycles = (uint32_t)m_cycle_remainder;
                if (wholeCycles != 0)
                {
                    m_cycle_remainder -= wholeCycles;
                    RunSoundCycles(wholeCycles);
                }

                output.put(sample, m_dac_sample);
            }
        }


        sealed class SoundsGoodBus : IBusInterface
        {
            readonly midway_sounds_good_device m_owner;
            readonly uint8_t [] m_ram = new uint8_t [0x1000];
            uint8_t [] m_rom = Array.Empty<uint8_t>();
            uint8_t m_ddr_a;
            uint8_t m_ddr_b;
            uint8_t m_ctl_a;
            uint8_t m_ctl_b;
            uint8_t m_out_a;
            uint8_t m_out_b;
            uint8_t m_in_b;
            int m_ca1 = 1;
            bool m_irq_a1;
            bool m_irq_b1;
            bool m_reset_pulse;

            public SoundsGoodBus(midway_sounds_good_device owner)
            {
                m_owner = owner;
            }

            public ushort CurrentOpcode { get; private set; }
            public BusSignals Signals { get { return new BusSignals(m_reset_pulse); } }
            public int RomLength { get { return m_rom.Length; } }

            public void LoadRom(memory_region region)
            {
                if (region == null || region.bytes() == 0)
                {
                    m_rom = Array.Empty<uint8_t>();
                    return;
                }

                m_rom = new uint8_t [region.bytes()];
                MemoryContainer<uint8_t> source = region.base_();
                for (int i = 0; i < m_rom.Length; i++)
                    m_rom[i] = source[i];
            }

            public void ClearResetPulse()
            {
                m_reset_pulse = false;
            }

            public void ResetPia()
            {
                Array.Clear(m_ram, 0, m_ram.Length);
                m_ddr_a = 0;
                m_ddr_b = 0;
                m_ctl_a = 0;
                m_ctl_b = 0;
                m_out_a = 0;
                m_out_b = 0;
                m_in_b = 0;
                m_ca1 = 1;
                m_irq_a1 = false;
                m_irq_b1 = false;
                m_reset_pulse = true;
            }

            public void WriteCommand(uint8_t data)
            {
                TraceSoundsGood("cmd=0x" + data.ToString("X2"));
                m_in_b = (uint8_t)((m_in_b & 0xf0) | ((data >> 1) & 0x0f));
                Ca1Write((~data) & 0x01);
            }

            public byte InterruptLevel() { return (byte)((IrqAState() || IrqBState()) ? 4 : 0); }
            public void AcknowledgeInterrupt(byte level) { }
            public bool Reset() { return m_reset_pulse; }
            public bool Halt() { return false; }

            public byte ReadByte(uint address)
            {
                address &= 0x0007ffff;
                if (address < 0x040000)
                    return address < m_rom.Length ? m_rom[address] : (byte)0xff;
                if (address >= 0x060000 && address <= 0x060fff)
                    return ReadPiaByte(address);
                if (address >= 0x070000 && address <= 0x070fff)
                    return m_ram[address & 0x0fff];
                return 0xff;
            }

            public ushort ReadWord(uint address)
            {
                CurrentOpcode = (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
                return CurrentOpcode;
            }

            public uint ReadLong(uint address) { return (uint)((ReadWord(address) << 16) | ReadWord(address + 2)); }

            public void WriteByte(uint address, byte value)
            {
                address &= 0x0007ffff;
                if (address >= 0x060000 && address <= 0x060fff)
                {
                    WritePiaByte(address, value);
                    return;
                }

                if (address >= 0x070000 && address <= 0x070fff)
                    m_ram[address & 0x0fff] = value;
            }

            public void WriteWord(uint address, ushort value)
            {
                WriteByte(address, (byte)(value >> 8));
                WriteByte(address + 1, (byte)value);
            }

            public void WriteLong(uint address, uint value)
            {
                WriteWord(address, (ushort)(value >> 16));
                WriteWord(address + 2, (ushort)value);
            }

            uint8_t ReadPiaByte(uint32_t address)
            {
                if ((address & 1) != 0)
                    return 0xff;
                return ReadPiaRegister(PiaRegisterFromAddress(address));
            }

            void WritePiaByte(uint32_t address, uint8_t data)
            {
                if ((address & 1) != 0)
                    return;
                WritePiaRegister(PiaRegisterFromAddress(address), data);
            }

            static uint32_t PiaRegisterFromAddress(uint32_t address)
            {
                uint32_t offset = (address >> 1) & 3;
                return ((offset << 1) & 2) | ((offset >> 1) & 1);
            }

            uint8_t ReadPiaRegister(uint32_t reg)
            {
                switch (reg & 3)
                {
                    case 0:
                        if (!OutputSelected(m_ctl_a))
                            return m_ddr_a;
                        m_irq_a1 = false;
                        return (uint8_t)((m_out_a & m_ddr_a) | (0xff & ~m_ddr_a));

                    case 1:
                        return (uint8_t)(m_ctl_a | (m_irq_a1 ? 0x80 : 0));

                    case 2:
                        if (!OutputSelected(m_ctl_b))
                            return m_ddr_b;
                        m_irq_b1 = false;
                        return (uint8_t)((m_out_b & m_ddr_b) | (m_in_b & ~m_ddr_b));

                    default:
                        return (uint8_t)(m_ctl_b | (m_irq_b1 ? 0x80 : 0));
                }
            }

            void WritePiaRegister(uint32_t reg, uint8_t data)
            {
                TraceSoundsGood("pia-w r" + (reg & 3).ToString() + "=0x" + data.ToString("X2") + " ca=0x" + m_ctl_a.ToString("X2") + " cb=0x" + m_ctl_b.ToString("X2") + " ddra=0x" + m_ddr_a.ToString("X2") + " ddrb=0x" + m_ddr_b.ToString("X2"));
                switch (reg & 3)
                {
                    case 0:
                        if (OutputSelected(m_ctl_a))
                            PortAWrite(data);
                        else
                            m_ddr_a = data;
                        break;

                    case 1:
                        m_ctl_a = (uint8_t)(data & 0x3f);
                        break;

                    case 2:
                        if (OutputSelected(m_ctl_b))
                            PortBWrite(data);
                        else
                            m_ddr_b = data;
                        break;

                    default:
                        m_ctl_b = (uint8_t)(data & 0x3f);
                        break;
                }
            }

            void PortAWrite(uint8_t data)
            {
                m_out_a = data;
                uint16_t dac = (uint16_t)((data << 2) | (m_owner.m_dacval & 3));
                m_owner.write_dac(dac);
            }

            void PortBWrite(uint8_t data)
            {
                m_out_b = data;
                uint8_t zMask = (uint8_t)~m_ddr_b;
                uint16_t dac = (uint16_t)((m_owner.m_dacval & ~3) | (data >> 6));
                m_owner.write_dac(dac);

                if ((zMask & 0x10) == 0)
                    m_owner.set_status_bit(0, (uint8_t)((data >> 4) & 1));
                if ((zMask & 0x20) == 0)
                    m_owner.set_status_bit(1, (uint8_t)((data >> 5) & 1));
            }

            void Ca1Write(int state)
            {
                state &= 1;
                if (m_ca1 != state && ((state != 0 && C1LowToHigh(m_ctl_a)) || (state == 0 && C1HighToLow(m_ctl_a))))
                    m_irq_a1 = true;
                m_ca1 = state;
            }

            bool IrqAState() { return m_irq_a1 && Irq1Enabled(m_ctl_a); }
            bool IrqBState() { return m_irq_b1 && Irq1Enabled(m_ctl_b); }
            static bool Irq1Enabled(uint8_t control) { return (control & 0x01) != 0; }
            static bool C1LowToHigh(uint8_t control) { return (control & 0x02) != 0; }
            static bool C1HighToLow(uint8_t control) { return (control & 0x02) == 0; }
            static bool OutputSelected(uint8_t control) { return (control & 0x04) != 0; }
        }
    }


    // ======================> midway_turbo_cheap_squeak_device
    public class midway_turbo_cheap_squeak_device : device_t
                                                    //device_mixer_interface
    {
        //DEFINE_DEVICE_TYPE(MIDWAY_TURBO_CHEAP_SQUEAK, midway_turbo_cheap_squeak_device, "midtcs",  "Midway Turbo Cheap Squeak Sound Board")
        public static readonly emu.detail.device_type_impl MIDWAY_TURBO_CHEAP_SQUEAK = DEFINE_DEVICE_TYPE("midtcs", "Midway Turbo Cheap Squeak Sound Board", (type, mconfig, tag, owner, clock) => { return new midway_turbo_cheap_squeak_device(mconfig, tag, owner, clock); });


        device_mixer_interface m_dimixer;


        // devices
        required_device<mc6809e_device> m_cpu;
        required_device<pia6821_device> m_pia;
        required_device<ad7533_device> m_dac;
        required_device_array<filter_biquad_device, u32_const_3> m_dac_filter;

        // internal state
        uint8_t m_status;
        uint16_t m_dacval;


        // construction/destruction
        midway_turbo_cheap_squeak_device(machine_config mconfig, string tag, device_t owner, uint32_t clock = 8_000_000)
            : base(mconfig, MIDWAY_TURBO_CHEAP_SQUEAK, tag, owner, clock)
        {
            m_class_interfaces.Add(new device_mixer_interface(mconfig, this));  //, device_mixer_interface(mconfig, *this)
            m_dimixer = GetClassInterface<device_mixer_interface>();


            m_cpu = new required_device<mc6809e_device>(this, "cpu");
            m_pia = new required_device<pia6821_device>(this, "pia");
            m_dac = new required_device<ad7533_device>(this, "dac");
            m_dac_filter = new required_device_array<filter_biquad_device, u32_const_3>(this, "dac_filter{0}", 0U, (base_, tag_) => { return new device_finder<filter_biquad_device, bool_const_true>(base_, tag_); });
            m_status = 0;
            m_dacval = 0;
        }


        public device_mixer_interface dimixer { get { return m_dimixer; } }


        // read/write
        //uint8_t read();
        //void write(uint8_t data);
        //DECLARE_WRITE_LINE_MEMBER(reset_write);


        void turbocs_map(address_map map, device_t device)
        {
            map.unmap_value_high();
            map.op(0x0000, 0x07ff).mirror(0x3800).ram();
            map.op(0x4000, 0x4003).mirror(0x3ffc).rw("pia", (offset) => { return ((pia6821_device)subdevice("pia")).read_alt(offset); }, (offset, data) => { ((pia6821_device)subdevice("pia")).write_alt(offset, data); });
            map.op(0x8000, 0xffff).rom();
        }


        // device-level overrides

        protected override void device_add_mconfig(machine_config config)
        {
            MC6809E(config, m_cpu, DERIVED_CLOCK(1, 4));
            m_cpu.op0.memory().set_addrmap(AS_PROGRAM, turbocs_map);

            PIA6821(config, m_pia, 0);
            m_pia.op0.writepa_handler().set(porta_w).reg();
            m_pia.op0.writepb_handler().set(portb_w).reg();
            m_pia.op0.irqa_handler().set((write_line_delegate)irq_w).reg();
            m_pia.op0.irqb_handler().set((write_line_delegate)irq_w).reg();

            AD7533(config, m_dac, 0); /// ad7533jn.u11

            // The DAC filters here are identical to those on the "Sounds Good" and "Cheap Squeak Deluxe" boards.
            //LM359 @U14.2, 2nd order MFB low-pass (fc = 5404.717733, Q = 0.625210, gain = -1.000000)
            FILTER_BIQUAD(config, m_dac_filter[2]).opamp_mfb_lowpass_setup(RES_K(150), RES_K(82), RES_K(150), CAP_P(470), CAP_P(150)); // R36, R37, R39, C19, C20
            m_dac_filter[2].op0.disound.add_route(ALL_OUTPUTS, this.dimixer, 1.0);
            //LM359 @U13.2, 2nd order MFB low-pass (fc = 5310.690763, Q = 1.608630, gain = -1.000000)
            FILTER_BIQUAD(config, m_dac_filter[1]).opamp_mfb_lowpass_setup(RES_K(33), RES_K(18), RES_K(33), CAP_P(5600), CAP_P(270)); // R32, R33, R35, C16, C17
            m_dac_filter[1].op0.disound.add_route(ALL_OUTPUTS, m_dac_filter[2].op0.disound, 1.0);
            //LM359 @U13.1, 1st order MFB low-pass (fc = 4912.189602, Q = 0.707107(ignored), gain = -1.000000)
            FILTER_BIQUAD(config, m_dac_filter[0]).opamp_mfb_lowpass_setup(RES_K(120), RES_K(0), RES_K(120), CAP_P(0), CAP_P(270)); // R27, <short>, R31, <nonexistent>, C14
            m_dac_filter[0].op0.disound.add_route(ALL_OUTPUTS, m_dac_filter[1].op0.disound, 1.0);
            m_dac.op0.disound.add_route(ALL_OUTPUTS, m_dac_filter[0].op0.disound, 1.0);
        }


        protected override void device_start()
        {
            save_item(NAME(new { m_status }));
            save_item(NAME(new { m_dacval }));
        }


        protected override void device_reset() { }

        //TIMER_CALLBACK_MEMBER(synced_write);


        // internal communications
        void porta_w(uint8_t data)
        {
            m_dacval = (uint16_t)((data << 2) | (m_dacval & 3));
            m_dac.op0.write(m_dacval);
        }

        void portb_w(uint8_t data) { throw new emu_unimplemented(); }

        //DECLARE_WRITE_LINE_MEMBER(irq_w);
        void irq_w(int state)
        {
            int combined_state = m_pia.op0.irq_a_state() | m_pia.op0.irq_b_state();
            m_cpu.op0.set_input_line(M6809_IRQ_LINE, combined_state != 0 ? ASSERT_LINE : CLEAR_LINE);
        }
    }


    static class midway_global
    {
        public static midway_ssio_device MIDWAY_SSIO<bool_Required>(machine_config mconfig, device_finder<midway_ssio_device, bool_Required> finder) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, midway_ssio_device.MIDWAY_SSIO, 0); }
        public static midway_sounds_good_device MIDWAY_SOUNDS_GOOD<bool_Required>(machine_config mconfig, device_finder<midway_sounds_good_device, bool_Required> finder) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, midway_sounds_good_device.MIDWAY_SOUNDS_GOOD, 0); }
        public static midway_turbo_cheap_squeak_device MIDWAY_TURBO_CHEAP_SQUEAK<bool_Required>(machine_config mconfig, device_finder<midway_turbo_cheap_squeak_device, bool_Required> finder) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, midway_turbo_cheap_squeak_device.MIDWAY_TURBO_CHEAP_SQUEAK, 0); }
    }
}
