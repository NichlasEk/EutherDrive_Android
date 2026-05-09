// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using devcb_write_line = mame.devcb_write<mame.Type_constant_s32, mame.devcb_value_const_unsigned_1<mame.Type_constant_s32>>;
using device_type = mame.emu.detail.device_type_impl_base;
using offs_t = System.UInt32;
using s32 = System.Int32;
using u8 = System.Byte;
using u32 = System.UInt32;

using static mame.device_global;
using static mame.diexec_global;
using static mame.emucore_global;


namespace mame
{
    // Register-compatible YM2610 shell for Neo Geo bring-up.  Full FM/SSG are
    // still being filled in; ADPCM-A is implemented for Neo Geo V-ROM playback.
    public class ym2610_device : device_t
    {
        public static readonly emu.detail.device_type_impl YM2610 = DEFINE_DEVICE_TYPE("ym2610", "YM2610 OPNB", (type, mconfig, tag, owner, clock) => { return new ym2610_device(mconfig, tag, owner, clock); });

        const u8 STATUS_TIMER_A = 0x01;
        const u8 STATUS_TIMER_B = 0x02;
        const u8 STATUS_BUSY = 0x80;
        const u8 EOS_FLAGS_MASK = 0xbf;
        const int OUTPUTS = 3;
        const int ADPCMA_CHANNELS = 6;
        const int ADPCMA_ADDRESS_SHIFT = 8;
        const int ADPCMB_ADDRESS_SHIFT = 8;
        const int ADPCMB_STEP_MIN = 127;
        const int ADPCMB_STEP_MAX = 24576;
        const u8 ADPCMB_STATUS_EOS = 0x01;
        const u8 ADPCMB_STATUS_BRDY = 0x02;
        const u8 ADPCMB_STATUS_PLAYING = 0x04;
        const int SSG_CHANNELS = 3;
        const double SSG_SIMPLE_GAIN = 900.0;
        const int FM_CHANNELS = 6;
        const double FM_SIMPLE_GAIN = 2400.0;
        static readonly ushort [] s_adpcma_steps =
        {
             16,  17,   19,   21,   23,   25,   28,
             31,  34,   37,   41,   45,   50,   55,
             60,  66,   73,   80,   88,   97,  107,
            118, 130,  143,  157,  173,  190,  209,
            230, 253,  279,  307,  337,  371,  408,
            449, 494,  544,  598,  658,  724,  796,
            876, 963, 1060, 1166, 1282, 1411, 1552
        };
        static readonly sbyte [] s_adpcma_step_inc = { -1, -1, -1, -1, 2, 5, 7, 9 };
        static readonly u8 [] s_adpcmb_step_scale = { 57, 57, 57, 57, 77, 102, 128, 153 };
        static readonly int [,] s_opn_operator_offset =
        {
            { 0, 8, 4, 12 },
            { 1, 9, 5, 13 },
            { 2, 10, 6, 14 }
        };

        public class device_sound_interface_ym2610 : device_sound_interface
        {
            public device_sound_interface_ym2610(machine_config mconfig, device_t device) : base(mconfig, device) { }

            public override void sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
            {
                ((ym2610_device)device()).device_sound_interface_sound_stream_update(stream, inputs, outputs);
            }
        }

        readonly device_sound_interface_ym2610 m_disound;
        readonly devcb_write_line m_irq_handler;
        readonly u8 [] m_regs = new u8[0x200];
        readonly u8 [] m_address = new u8[2];
        readonly bool [] m_timer_running = new bool[2];
        readonly bool [] m_adpcma_playing = new bool[ADPCMA_CHANNELS];
        readonly u32 [] m_adpcma_curnibble = new u32[ADPCMA_CHANNELS];
        readonly u8 [] m_adpcma_curbyte = new u8[ADPCMA_CHANNELS];
        readonly u32 [] m_adpcma_curaddress = new u32[ADPCMA_CHANNELS];
        readonly s32 [] m_adpcma_accumulator = new s32[ADPCMA_CHANNELS];
        readonly s32 [] m_adpcma_step_index = new s32[ADPCMA_CHANNELS];
        readonly u8 [] m_adpcmb_regs = new u8[0x10];
        readonly double [] m_fm_phase = new double[FM_CHANNELS];
        readonly u8 [] m_fm_key_mask = new u8[FM_CHANNELS];
        u32 m_adpcma_clock_counter;
        u32 m_adpcmb_status = ADPCMB_STATUS_BRDY;
        u32 m_adpcmb_buffer;
        u32 m_adpcmb_nibbles;
        u32 m_adpcmb_position;
        u32 m_adpcmb_curaddress;
        s32 m_adpcmb_accumulator;
        s32 m_adpcmb_output;
        s32 m_adpcmb_prev_output;
        s32 m_adpcmb_step = ADPCMB_STEP_MIN;
        readonly double [] m_ssg_phase = new double[SSG_CHANNELS];
        double m_ssg_noise_phase;
        u32 m_ssg_noise_lfsr = 1;
        int m_ssg_noise_output = 1;
        sound_stream m_stream;
        emu_timer m_timer_a;
        emu_timer m_timer_b;
        attotime m_busy_end;
        u8 m_status;
        u8 m_eos_status;
        u8 m_flag_mask = EOS_FLAGS_MASK;
        readonly bool m_trace = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM2610_TRACE"), "1", StringComparison.Ordinal);
        readonly bool m_test_tone = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM2610_TEST_TONE"), "1", StringComparison.Ordinal);
        int m_trace_count;
        double m_test_tone_phase;


        ym2610_device(machine_config mconfig, string tag, device_t owner, u32 clock)
            : this(mconfig, YM2610, tag, owner, clock)
        {
        }


        ym2610_device(machine_config mconfig, device_type type, string tag, device_t owner, u32 clock)
            : base(mconfig, type, tag, owner, clock)
        {
            m_class_interfaces.Add(new device_sound_interface_ym2610(mconfig, this));
            m_disound = GetClassInterface<device_sound_interface_ym2610>();
            m_irq_handler = new devcb_write_line(this);
        }


        public device_sound_interface_ym2610 disound { get { return m_disound; } }

        public devcb_write_line.binder irq_handler() { return m_irq_handler.bind(); }


        public u8 read(offs_t offset)
        {
            if (m_stream != null)
                m_stream.update();

            switch (offset & 0x03)
            {
            case 0:
                return read_status();
            case 1:
                return read_data(0);
            case 2:
                return read_status_hi();
            case 3:
                return read_data(1);
            default:
                return 0xff;
            }
        }


        public void write(offs_t offset, u8 data)
        {
            if (m_stream != null)
                m_stream.update();

            switch (offset & 0x03)
            {
            case 0:
                m_address[0] = data;
                break;
            case 1:
                write_data(0, data);
                break;
            case 2:
                m_address[1] = data;
                break;
            case 3:
                write_data(1, data);
                break;
            }

            mark_busy();
        }


        protected override void device_start()
        {
            m_irq_handler.resolve_safe();
            m_stream = m_disound.stream_alloc(0, OUTPUTS, Math.Max(1, clock() / 144));
            m_timer_a = timer_alloc(timer_a_expired);
            m_timer_b = timer_alloc(timer_b_expired);

            save_item(NAME(new { m_regs }));
            save_item(NAME(new { m_address }));
            save_item(NAME(new { m_status }));
            save_item(NAME(new { m_eos_status }));
            save_item(NAME(new { m_flag_mask }));
            save_item(NAME(new { m_busy_end }));
            save_item(NAME(new { m_timer_running }));
            save_item(NAME(new { m_adpcma_playing }));
            save_item(NAME(new { m_adpcma_curnibble }));
            save_item(NAME(new { m_adpcma_curbyte }));
            save_item(NAME(new { m_adpcma_curaddress }));
            save_item(NAME(new { m_adpcma_accumulator }));
            save_item(NAME(new { m_adpcma_step_index }));
            save_item(NAME(new { m_adpcmb_regs }));
            save_item(NAME(new { m_adpcmb_status }));
            save_item(NAME(new { m_adpcmb_buffer }));
            save_item(NAME(new { m_adpcmb_nibbles }));
            save_item(NAME(new { m_adpcmb_position }));
            save_item(NAME(new { m_adpcmb_curaddress }));
            save_item(NAME(new { m_adpcmb_accumulator }));
            save_item(NAME(new { m_adpcmb_output }));
            save_item(NAME(new { m_adpcmb_prev_output }));
            save_item(NAME(new { m_adpcmb_step }));
            save_item(NAME(new { m_fm_phase }));
            save_item(NAME(new { m_fm_key_mask }));
            save_item(NAME(new { m_adpcma_clock_counter }));
            save_item(NAME(new { m_ssg_phase }));
            save_item(NAME(new { m_ssg_noise_phase }));
            save_item(NAME(new { m_ssg_noise_lfsr }));
            save_item(NAME(new { m_ssg_noise_output }));
            save_item(NAME(new { m_test_tone_phase }));
        }


        protected override void device_reset()
        {
            Array.Clear(m_regs, 0, m_regs.Length);
            Array.Clear(m_address, 0, m_address.Length);
            ResetAdpcmA();
            ResetAdpcmB();
            Array.Clear(m_fm_phase, 0, m_fm_phase.Length);
            Array.Clear(m_fm_key_mask, 0, m_fm_key_mask.Length);
            Array.Clear(m_ssg_phase, 0, m_ssg_phase.Length);
            m_adpcma_clock_counter = 0;
            m_ssg_noise_phase = 0;
            m_ssg_noise_lfsr = 1;
            m_ssg_noise_output = 1;
            m_test_tone_phase = 0;
            m_status = 0;
            m_eos_status = 0;
            m_flag_mask = EOS_FLAGS_MASK;
            m_busy_end = attotime.zero;
            m_timer_running[0] = false;
            m_timer_running[1] = false;
            if (m_timer_a != null)
                m_timer_a.enable(false);
            if (m_timer_b != null)
                m_timer_b.enable(false);
            update_irq();
        }


        protected override void device_clock_changed()
        {
            if (m_stream != null)
                m_stream.set_sample_rate(Math.Max(1, clock() / 144));
        }


        void device_sound_interface_sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
        {
            for (int output = 0; output < (int)outputs.size(); output++)
                outputs[output].fill(0);

            if (outputs.size() < OUTPUTS)
                return;

            int samples = (int)outputs[0].samples();
            double sampleRate = outputs[0].sample_rate();
            if (sampleRate <= 0)
                sampleRate = Math.Max(1, clock() / 144);
            for (int sample = 0; sample < samples; sample++)
            {
                int fm = ClockSimpleFm(sampleRate) + ClockSimpleSsg(sampleRate);
                if (m_test_tone)
                    fm += ClockTestTone(sampleRate);
                outputs[0].put_int(sample, fm, 32768);

                int left = 0;
                int right = 0;
                u8 ended = 0;
                bool clockAdpcmA = (++m_adpcma_clock_counter & 0x03) == 0;

                for (int channel = 0; channel < ADPCMA_CHANNELS; channel++)
                {
                    if (clockAdpcmA && ClockAdpcmA(channel))
                        ended |= (u8)(1 << channel);
                    MixAdpcmA(channel, ref left, ref right);
                }

                if (ClockAdpcmB())
                    ended |= 0x80;
                MixAdpcmB(ref left, ref right);

                m_eos_status = (u8)(m_eos_status | ended);
                outputs[1].put_int(sample, left, 32768);
                outputs[2].put_int(sample, right, 32768);
            }
        }


        int ClockTestTone(double sampleRate)
        {
            m_test_tone_phase += 440.0 / sampleRate;
            m_test_tone_phase -= Math.Floor(m_test_tone_phase);
            return (int)(Math.Sin(m_test_tone_phase * Math.PI * 2.0) * 8000.0);
        }


        u8 read_status()
        {
            u8 result = m_status;
            if (machine().time() < m_busy_end)
                result |= STATUS_BUSY;
            if (m_trace && m_trace_count < 2000)
            {
                Console.Error.WriteLine($"[YM2610] read status={result:x2}");
                m_trace_count++;
            }
            return result;
        }


        u8 read_status_hi()
        {
            u8 result = (u8)(m_eos_status & m_flag_mask);
            if (m_trace && m_trace_count < 2000)
            {
                Console.Error.WriteLine($"[YM2610] read status_hi={result:x2} eos={m_eos_status:x2} mask={m_flag_mask:x2}");
                m_trace_count++;
            }
            return result;
        }


        u8 read_data(int port)
        {
            if (port != 0)
                return 0;

            u8 address = m_address[0];
            if (address < 0x0e)
                return m_regs[address];
            if (address < 0x10)
                return 0xff;
            if (address == 0xff)
                return 1;

            return 0;
        }


        void write_data(int port, u8 data)
        {
            u32 index = reg_index(port);
            if (index < m_regs.Length)
                m_regs[index] = data;

            if (m_trace && m_trace_count < 2000 && (port == 1 || m_address[0] == 0x1c || m_address[0] == 0x28 || (m_address[0] >= 0x10 && m_address[0] <= 0x1c) || (m_address[0] >= 0x24 && m_address[0] <= 0x27)))
            {
                Console.Error.WriteLine($"[YM2610] port={port} addr={m_address[port]:x2} data={data:x2}");
                m_trace_count++;
            }

            if (port == 0)
            {
                switch (m_address[0])
                {
                case 0x1c:
                    m_flag_mask = (u8)(~data & EOS_FLAGS_MASK);
                    m_eos_status = (u8)(m_eos_status & ~(data & EOS_FLAGS_MASK));
                    if ((data & 0x80) != 0)
                        m_adpcmb_status &= ~(u32)ADPCMB_STATUS_EOS;
                    break;
                case >= 0x10 and < 0x1c:
                    WriteAdpcmB((u8)(m_address[0] & 0x0f), data);
                    break;
                case 0x24:
                case 0x25:
                case 0x26:
                    reload_timer_register(m_address[0]);
                    break;
                case 0x27:
                    mode_w(data);
                    break;
                case 0x28:
                    keyon_w(data);
                    break;
                }
            }
            else if (port == 1 && m_address[1] < 0x30)
            {
                WriteAdpcmA(m_address[1], data);
            }
        }


        u32 reg_index(int port)
        {
            return (u32)((port << 8) | m_address[port]);
        }


        void mark_busy()
        {
            m_busy_end = machine().time() + attotime.from_ticks(32, Math.Max(1U, clock()));
        }


        void mode_w(u8 data)
        {
            if ((data & 0x10) != 0)
                m_status = (u8)(m_status & ~STATUS_TIMER_A);
            if ((data & 0x20) != 0)
                m_status = (u8)(m_status & ~STATUS_TIMER_B);

            update_timer(0, (data & 0x01) != 0);
            update_timer(1, (data & 0x02) != 0);
            update_irq();
        }


        void reload_timer_register(u8 reg)
        {
            u8 mode = m_regs[0x27];
            if ((reg == 0x24 || reg == 0x25) && (mode & 0x01) != 0)
                update_timer(0, true);
            else if (reg == 0x26 && (mode & 0x02) != 0)
                update_timer(1, true);
        }


        void update_timer(int index, bool load)
        {
            emu_timer timer = index == 0 ? m_timer_a : m_timer_b;
            if (timer == null)
                return;

            if (!load)
            {
                timer.enable(false);
                m_timer_running[index] = false;
                return;
            }

            u32 period = index == 0
                ? (u32)(1024 - (((m_regs[0x24] << 2) | (m_regs[0x25] & 0x03)) & 0x3ff))
                : (u32)(16 * (256 - m_regs[0x26]));
            if (period == 0)
                period = 1;

            timer.adjust(attotime.from_ticks(period * 12, Math.Max(1U, clock())));
            m_timer_running[index] = true;
        }


        void timer_a_expired(s32 param)
        {
            if ((m_regs[0x27] & 0x04) != 0)
                m_status = (u8)(m_status | STATUS_TIMER_A);
            m_timer_running[0] = false;
            update_irq();
            update_timer(0, (m_regs[0x27] & 0x01) != 0);
        }


        void timer_b_expired(s32 param)
        {
            if ((m_regs[0x27] & 0x08) != 0)
                m_status = (u8)(m_status | STATUS_TIMER_B);
            m_timer_running[1] = false;
            update_irq();
            update_timer(1, (m_regs[0x27] & 0x02) != 0);
        }


        void update_irq()
        {
            u8 enabled = 0;
            if ((m_regs[0x27] & 0x04) != 0)
                enabled |= STATUS_TIMER_A;
            if ((m_regs[0x27] & 0x08) != 0)
                enabled |= STATUS_TIMER_B;

            m_irq_handler.op_s32((m_status & enabled) != 0 ? ASSERT_LINE : CLEAR_LINE);
        }


        int ClockSimpleSsg(double sampleRate)
        {
            u8 enable = m_regs[0x07];
            int noisePeriod = m_regs[0x06] & 0x1f;
            if (noisePeriod == 0)
                noisePeriod = 1;

            double ssgClock = Math.Max(1, clock() / 4.0);
            double noiseFrequency = ssgClock / (16.0 * noisePeriod);
            m_ssg_noise_phase += noiseFrequency / sampleRate;
            while (m_ssg_noise_phase >= 1.0)
            {
                m_ssg_noise_phase -= 1.0;
                uint bit = (m_ssg_noise_lfsr ^ (m_ssg_noise_lfsr >> 3)) & 1U;
                m_ssg_noise_lfsr = (m_ssg_noise_lfsr >> 1) | (bit << 16);
                if (m_ssg_noise_lfsr == 0)
                    m_ssg_noise_lfsr = 1;
                m_ssg_noise_output = (int)(m_ssg_noise_lfsr & 1U);
            }

            double mixed = 0;
            for (int channel = 0; channel < SSG_CHANNELS; channel++)
            {
                bool toneDisabled = (enable & (1 << channel)) != 0;
                bool noiseDisabled = (enable & (1 << (channel + 3))) != 0;
                if (toneDisabled && noiseDisabled)
                    continue;

                int fine = m_regs[channel * 2];
                int coarse = m_regs[channel * 2 + 1] & 0x0f;
                int period = fine | (coarse << 8);
                if (period == 0)
                    period = 1;

                double toneFrequency = ssgClock / (16.0 * period);
                m_ssg_phase[channel] += toneFrequency / sampleRate;
                m_ssg_phase[channel] -= Math.Floor(m_ssg_phase[channel]);

                bool toneOn = toneDisabled || m_ssg_phase[channel] < 0.5;
                bool noiseOn = noiseDisabled || m_ssg_noise_output != 0;
                int level = m_regs[0x08 + channel] & 0x0f;
                if ((m_regs[0x08 + channel] & 0x10) != 0)
                    level = 0x0f;

                if (level == 0)
                    continue;

                mixed += (toneOn && noiseOn ? 1.0 : -1.0) * (level / 15.0);
            }

            return Math.Clamp((int)(mixed * SSG_SIMPLE_GAIN), -32768, 32767);
        }


        void keyon_w(u8 data)
        {
            int channel = data & 0x03;
            if (channel >= 3)
                return;
            if ((data & 0x04) != 0)
                channel += 3;

            m_fm_key_mask[channel] = (u8)(data >> 4);
            if (m_fm_key_mask[channel] != 0)
                m_fm_phase[channel] = 0;
        }


        int ClockSimpleFm(double sampleRate)
        {
            double mixed = 0;
            for (int channel = 0; channel < FM_CHANNELS; channel++)
            {
                if (m_fm_key_mask[channel] == 0)
                    continue;

                double frequency = SimpleFmFrequency(channel);
                double amplitude = SimpleFmAmplitude(channel);
                if (frequency <= 0 || amplitude <= 0)
                    continue;

                m_fm_phase[channel] += frequency / sampleRate;
                m_fm_phase[channel] -= Math.Floor(m_fm_phase[channel]);
                mixed += Math.Sin(m_fm_phase[channel] * Math.PI * 2.0) * amplitude;
            }

            return Math.Clamp((int)mixed, -32768, 32767);
        }


        double SimpleFmFrequency(int channel)
        {
            int bank = channel >= 3 ? 0x100 : 0x000;
            int ch = channel % 3;
            int fnum = m_regs[bank + 0xa0 + ch] | ((m_regs[bank + 0xa4 + ch] & 0x07) << 8);
            int block = (m_regs[bank + 0xa4 + ch] >> 3) & 0x07;
            if (fnum == 0)
                return 0;

            double baseRate = Math.Max(1, clock() / 144.0);
            return baseRate * fnum / 2048.0 * Math.Pow(2.0, block - 4);
        }


        double SimpleFmAmplitude(int channel)
        {
            int bank = channel >= 3 ? 0x100 : 0x000;
            int ch = channel % 3;
            int activeOperators = 0;
            int levelSum = 0;
            for (int slot = 0; slot < 4; slot++)
            {
                if ((m_fm_key_mask[channel] & (1 << slot)) == 0)
                    continue;

                int tl = m_regs[bank + 0x40 + s_opn_operator_offset[ch, slot]] & 0x7f;
                levelSum += 127 - tl;
                activeOperators++;
            }

            if (activeOperators == 0)
                return 0;

            double normalized = levelSum / (127.0 * activeOperators);
            return normalized * FM_SIMPLE_GAIN;
        }


        void ResetAdpcmA()
        {
            Array.Clear(m_adpcma_playing, 0, m_adpcma_playing.Length);
            Array.Clear(m_adpcma_curnibble, 0, m_adpcma_curnibble.Length);
            Array.Clear(m_adpcma_curbyte, 0, m_adpcma_curbyte.Length);
            Array.Clear(m_adpcma_curaddress, 0, m_adpcma_curaddress.Length);
            Array.Clear(m_adpcma_accumulator, 0, m_adpcma_accumulator.Length);
            Array.Clear(m_adpcma_step_index, 0, m_adpcma_step_index.Length);
            for (int channel = 0; channel < ADPCMA_CHANNELS; channel++)
                m_regs[0x100 + 0x08 + channel] = 0xdf;
        }


        void ResetAdpcmB()
        {
            Array.Clear(m_adpcmb_regs, 0, m_adpcmb_regs.Length);
            m_adpcmb_status = ADPCMB_STATUS_BRDY;
            m_adpcmb_buffer = 0;
            m_adpcmb_nibbles = 0;
            m_adpcmb_position = 0;
            m_adpcmb_curaddress = 0;
            m_adpcmb_accumulator = 0;
            m_adpcmb_output = 0;
            m_adpcmb_prev_output = 0;
            m_adpcmb_step = ADPCMB_STEP_MIN;
        }


        void WriteAdpcmB(u8 reg, u8 data)
        {
            if (reg == 0x00)
                data = (u8)((data | 0x20) & ~0x40);

            m_adpcmb_regs[reg] = data;

            if (reg != 0x00)
                return;

            if ((data & 0x01) != 0)
            {
                m_adpcmb_status = ADPCMB_STATUS_BRDY | ((m_adpcmb_status & ADPCMB_STATUS_PLAYING) != 0 ? ADPCMB_STATUS_EOS : 0U);
                return;
            }

            m_adpcmb_status = ADPCMB_STATUS_BRDY;
            m_adpcmb_curaddress = AdpcmBStart() << ADPCMB_ADDRESS_SHIFT;
            if ((data & 0x80) == 0)
                return;

            m_adpcmb_buffer = 0;
            m_adpcmb_nibbles = 0;
            m_adpcmb_position = 0;
            m_adpcmb_accumulator = 0;
            m_adpcmb_output = 0;
            m_adpcmb_prev_output = 0;
            m_adpcmb_step = ADPCMB_STEP_MIN;
            m_adpcmb_status = ADPCMB_STATUS_BRDY | ADPCMB_STATUS_PLAYING;
            m_eos_status = (u8)(m_eos_status & ~0x80);

            if (m_trace && m_trace_count < 2000)
            {
                memory_region region = AdpcmBRegion();
                u32 bytes = region != null ? region.bytes() : 0;
                Console.Error.WriteLine($"[YM2610] ADPCM-B keyon start={AdpcmBStart():x4} end={AdpcmBEnd():x4} delta={AdpcmBDelta():x4} level={m_adpcmb_regs[0x0b]:x2} pan={m_adpcmb_regs[0x01]:x2} region={bytes:x}");
                m_trace_count++;
            }
        }


        bool ClockAdpcmB()
        {
            m_adpcmb_prev_output = m_adpcmb_output;
            if ((m_adpcmb_status & ADPCMB_STATUS_PLAYING) == 0 || (m_adpcmb_regs[0x00] & 0x80) == 0)
            {
                m_adpcmb_position = 0;
                return false;
            }

            u32 deltaN = AdpcmBDelta();
            if (deltaN == 0)
                return false;

            u32 position = m_adpcmb_position + deltaN;
            m_adpcmb_position = position & 0xffff;
            if (position < 0x10000)
                return false;

            if (m_adpcmb_nibbles == 0 && RequestAdpcmBData())
                return FinishAdpcmB();

            u8 data = (u8)ConsumeAdpcmBNibbles(1);
            int delta = (2 * (data & 0x07) + 1) * m_adpcmb_step / 8;
            if ((data & 0x08) != 0)
                delta = -delta;

            m_adpcmb_accumulator = Math.Clamp(m_adpcmb_accumulator + delta, -32768, 32767);
            m_adpcmb_step = Math.Clamp((m_adpcmb_step * s_adpcmb_step_scale[data & 0x07]) / 64, ADPCMB_STEP_MIN, ADPCMB_STEP_MAX);
            m_adpcmb_output = m_adpcmb_accumulator;

            if (m_adpcmb_nibbles < 3 && RequestAdpcmBData())
                return FinishAdpcmB();

            return false;
        }


        bool FinishAdpcmB()
        {
            if ((m_adpcmb_regs[0x00] & 0x10) != 0)
            {
                m_adpcmb_curaddress = AdpcmBStart() << ADPCMB_ADDRESS_SHIFT;
                return true;
            }

            m_adpcmb_status = (m_adpcmb_status | ADPCMB_STATUS_EOS) & ~(u32)ADPCMB_STATUS_PLAYING;
            m_adpcmb_buffer = 0;
            m_adpcmb_nibbles = 0;
            return true;
        }


        bool RequestAdpcmBData()
        {
            AppendAdpcmBByte(ReadAdpcmBByte(m_adpcmb_curaddress));
            return AdvanceAdpcmBAddress();
        }


        void AppendAdpcmBByte(u8 data)
        {
            if (m_adpcmb_nibbles > 6)
                m_adpcmb_nibbles = 6;
            m_adpcmb_buffer |= (u32)data << (int)(24 - 4 * m_adpcmb_nibbles);
            m_adpcmb_nibbles += 2;
        }


        u32 ConsumeAdpcmBNibbles(u8 count)
        {
            u32 result = m_adpcmb_buffer >> (32 - 4 * count);
            m_adpcmb_buffer <<= 4 * count;
            m_adpcmb_nibbles = m_adpcmb_nibbles > count ? m_adpcmb_nibbles - count : 0;
            return result;
        }


        bool AdvanceAdpcmBAddress()
        {
            if ((m_adpcmb_curaddress & ((1U << ADPCMB_ADDRESS_SHIFT) - 1)) == ((1U << ADPCMB_ADDRESS_SHIFT) - 1))
            {
                u32 unitAddress = m_adpcmb_curaddress >> ADPCMB_ADDRESS_SHIFT;
                if (unitAddress == AdpcmBEnd())
                    return true;
            }

            m_adpcmb_curaddress = (m_adpcmb_curaddress + 1) & 0xffffff;
            return false;
        }


        void MixAdpcmB(ref int left, ref int right)
        {
            if ((m_adpcmb_status & ADPCMB_STATUS_PLAYING) == 0)
                return;

            int interp = (int)(((long)m_adpcmb_prev_output * (((m_adpcmb_position ^ 0xffff) + 1) & 0xffff)
                + (long)m_adpcmb_output * m_adpcmb_position) >> 16);
            int value = (interp * m_adpcmb_regs[0x0b]) >> 9;

            if ((m_adpcmb_regs[0x01] & 0x80) != 0)
                left += value;
            if ((m_adpcmb_regs[0x01] & 0x40) != 0)
                right += value;
        }


        void WriteAdpcmA(u8 reg, u8 data)
        {
            if (reg != 0x00)
                return;

            bool keyon = (data & 0x80) == 0;
            for (int channel = 0; channel < ADPCMA_CHANNELS; channel++)
            {
                if ((data & (1 << channel)) == 0)
                    continue;

                m_adpcma_playing[channel] = keyon;
                if (!keyon)
                    continue;

                m_adpcma_curaddress[channel] = AdpcmAStart(channel) << ADPCMA_ADDRESS_SHIFT;
                m_adpcma_curnibble[channel] = 0;
                m_adpcma_curbyte[channel] = 0;
                m_adpcma_accumulator[channel] = 0;
                m_adpcma_step_index[channel] = 0;
                if (m_trace && m_trace_count < 2000)
                {
                    memory_region region = AdpcmARegion();
                    u32 bytes = region != null ? region.bytes() : 0;
                    Console.Error.WriteLine($"[YM2610] ADPCM-A keyon ch={channel} start={AdpcmAStart(channel):x4} end={AdpcmAEnd(channel):x4} panvol={m_regs[0x100 + 0x08 + channel]:x2} total={m_regs[0x101]:x2} region={bytes:x}");
                    m_trace_count++;
                }
            }
        }


        bool ClockAdpcmA(int channel)
        {
            if (!m_adpcma_playing[channel])
            {
                m_adpcma_accumulator[channel] = 0;
                return false;
            }

            u32 end = (AdpcmAEnd(channel) + 1) << ADPCMA_ADDRESS_SHIFT;
            if (m_adpcma_curnibble[channel] == 0 && ((m_adpcma_curaddress[channel] ^ end) & 0x0fffff) == 0)
            {
                m_adpcma_playing[channel] = false;
                m_adpcma_accumulator[channel] = 0;
                return true;
            }

            u8 data;
            if (m_adpcma_curnibble[channel] == 0)
            {
                m_adpcma_curbyte[channel] = ReadAdpcmAByte(m_adpcma_curaddress[channel]++);
                data = (u8)(m_adpcma_curbyte[channel] >> 4);
                m_adpcma_curnibble[channel] = 1;
            }
            else
            {
                data = (u8)(m_adpcma_curbyte[channel] & 0x0f);
                m_adpcma_curnibble[channel] = 0;
            }

            int stepIndex = m_adpcma_step_index[channel];
            int delta = (2 * (data & 0x07) + 1) * s_adpcma_steps[stepIndex] / 8;
            if ((data & 0x08) != 0)
                delta = -delta;

            m_adpcma_accumulator[channel] = (m_adpcma_accumulator[channel] + delta) & 0x0fff;

            m_adpcma_step_index[channel] = Math.Clamp(stepIndex + s_adpcma_step_inc[data & 0x07], 0, 48);
            return false;
        }


        void MixAdpcmA(int channel, ref int left, ref int right)
        {
            if (!m_adpcma_playing[channel])
                return;

            int reg = 0x100 + 0x08 + channel;
            int channelLevel = m_regs[reg] & 0x1f;
            int totalLevel = m_regs[0x101] & 0x3f;
            int attenuationSteps = (channelLevel ^ 0x1f) + (totalLevel ^ 0x3f);
            if (attenuationSteps >= 63)
                return;

            short signedAccumulator = unchecked((short)(m_adpcma_accumulator[channel] << 4));
            int multiplier = 15 - (attenuationSteps & 0x07);
            int shift = 5 + (attenuationSteps >> 3);
            int value = ((signedAccumulator * multiplier) >> shift) & ~3;

            if ((m_regs[reg] & 0x80) != 0)
                left += value;
            if ((m_regs[reg] & 0x40) != 0)
                right += value;
        }


        u32 AdpcmAStart(int channel)
        {
            return (u32)(m_regs[0x100 + 0x10 + channel] | (m_regs[0x100 + 0x18 + channel] << 8));
        }


        u32 AdpcmAEnd(int channel)
        {
            return (u32)(m_regs[0x100 + 0x20 + channel] | (m_regs[0x100 + 0x28 + channel] << 8));
        }


        u8 ReadAdpcmAByte(u32 address)
        {
            memory_region region = AdpcmARegion();
            if (region == null || region.base_() == null || address >= region.bytes())
                return 0;

            return region.base_()[(int)address];
        }


        u32 AdpcmBStart()
        {
            return (u32)(m_adpcmb_regs[0x02] | (m_adpcmb_regs[0x03] << 8));
        }


        u32 AdpcmBEnd()
        {
            return (u32)(m_adpcmb_regs[0x04] | (m_adpcmb_regs[0x05] << 8));
        }


        u32 AdpcmBDelta()
        {
            return (u32)(m_adpcmb_regs[0x09] | (m_adpcmb_regs[0x0a] << 8));
        }


        u8 ReadAdpcmBByte(u32 address)
        {
            memory_region region = AdpcmBRegion();
            if (region == null || region.base_() == null || address >= region.bytes())
                return 0;

            return region.base_()[(int)address];
        }


        memory_region AdpcmARegion() => memregion("adpcma");


        memory_region AdpcmBRegion() => memregion("adpcmb") ?? memregion("adpcma");
    }


    public static class ym2610_global
    {
        public static ym2610_device YM2610<bool_Required>(machine_config mconfig, device_finder<ym2610_device, bool_Required> finder, u32 clock) where bool_Required : bool_const, new()
        {
            return emu.detail.device_type_impl.op(mconfig, finder, ym2610_device.YM2610, clock);
        }


        public static ym2610_device YM2610<bool_Required>(machine_config mconfig, device_finder<ym2610_device, bool_Required> finder, XTAL clock) where bool_Required : bool_const, new()
        {
            return emu.detail.device_type_impl.op(mconfig, finder, ym2610_device.YM2610, clock);
        }
    }
}
