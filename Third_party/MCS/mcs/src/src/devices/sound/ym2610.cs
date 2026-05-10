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
        const int FM_OPERATORS_PER_CHANNEL = 4;
        const int FM_PHASE_MASK = 0x3ff;
        const int FM_OPERATOR_MIN = -0x2000;
        const int FM_OPERATOR_MAX = 0x1fff;
        const int FM_ENVELOPE_QUIET = 0x380;
        const int FM_PHASE_COUNTER_SCALE = 1 << 20;
        const double FM_MIX_GAIN = 8.0;
        const int ADPCMA_DEFAULT_MIX_GAIN_PERCENT = 300;
        const int ADPCMB_DEFAULT_MIX_GAIN_PERCENT = 100;
        const u8 YM2610_FM_CHANNEL_MASK = 0x36;
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
        static readonly int [] s_opn_algorithm_ops =
        {
            Algorithm(1, 2, 3, false, false, false),
            Algorithm(0, 5, 3, false, false, false),
            Algorithm(0, 2, 6, false, false, false),
            Algorithm(1, 0, 7, false, false, false),
            Algorithm(1, 0, 3, false, true,  false),
            Algorithm(1, 1, 1, false, true,  true),
            Algorithm(1, 0, 0, false, true,  true),
            Algorithm(0, 0, 0, true,  true,  true)
        };
        static readonly u8 [,] s_opn_detune_adjustment =
        {
            {  0,  0,  1,  2 }, {  0,  0,  1,  2 }, {  0,  0,  1,  2 }, {  0,  0,  1,  2 },
            {  0,  1,  2,  2 }, {  0,  1,  2,  3 }, {  0,  1,  2,  3 }, {  0,  1,  2,  3 },
            {  0,  1,  2,  4 }, {  0,  1,  3,  4 }, {  0,  1,  3,  4 }, {  0,  1,  3,  5 },
            {  0,  2,  4,  5 }, {  0,  2,  4,  6 }, {  0,  2,  4,  6 }, {  0,  2,  5,  7 },
            {  0,  2,  5,  8 }, {  0,  3,  6,  8 }, {  0,  3,  6,  9 }, {  0,  3,  7, 10 },
            {  0,  4,  8, 11 }, {  0,  4,  8, 12 }, {  0,  4,  9, 13 }, {  0,  5, 10, 14 },
            {  0,  5, 11, 16 }, {  0,  6, 12, 17 }, {  0,  6, 13, 19 }, {  0,  7, 14, 20 },
            {  0,  8, 16, 22 }, {  0,  8, 16, 22 }, {  0,  8, 16, 22 }, {  0,  8, 16, 22 }
        };

        static int Algorithm(int op2in, int op3in, int op4in, bool op1out, bool op2out, bool op3out)
        {
            return op2in
                | (op3in << 1)
                | (op4in << 4)
                | (op1out ? 1 << 7 : 0)
                | (op2out ? 1 << 8 : 0)
                | (op3out ? 1 << 9 : 0);
        }

        enum fm_envelope_stage
        {
            Attack,
            Decay,
            Sustain,
            Release,
            Off
        }

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
        readonly double [,] m_fm_phase = new double[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly int [,] m_fm_env_attenuation = new int[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly int [,] m_fm_feedback = new int[FM_CHANNELS, 2];
        readonly int [] m_fm_feedback_in = new int[FM_CHANNELS];
        readonly fm_envelope_stage [,] m_fm_stage = new fm_envelope_stage[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly bool [,] m_fm_key_state = new bool[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly bool [,] m_fm_ssg_inverted = new bool[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly int [] m_fm_opout = new int[8];
        readonly u8 [] m_fm_key_mask = new u8[FM_CHANNELS];
        readonly u8 [] m_fm_csm_key_mask = new u8[FM_CHANNELS];
        readonly u8 [] m_block_freq_latch = new u8[4];
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
        double m_fm_clock_accumulator;
        double m_adpcma_clock_accumulator;
        int m_fm_held_sample;
        u32 m_env_counter;
        sound_stream m_stream;
        emu_timer m_timer_a;
        emu_timer m_timer_b;
        attotime m_busy_end;
        u8 m_status;
        u8 m_eos_status;
        u8 m_flag_mask = EOS_FLAGS_MASK;
        readonly bool m_trace = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM2610_TRACE"), "1", StringComparison.Ordinal);
        readonly bool m_test_tone = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM2610_TEST_TONE"), "1", StringComparison.Ordinal);
        int m_fm_mix_gain_percent = ParseGainPercent("EUTHERDRIVE_YM2610_FM_GAIN_PERCENT", ADPCMB_DEFAULT_MIX_GAIN_PERCENT);
        int m_adpcma_mix_gain_percent = ParseGainPercent("EUTHERDRIVE_YM2610_ADPCMA_GAIN_PERCENT", ADPCMA_DEFAULT_MIX_GAIN_PERCENT);
        int m_adpcmb_mix_gain_percent = ParseGainPercent("EUTHERDRIVE_YM2610_ADPCMB_GAIN_PERCENT", ADPCMB_DEFAULT_MIX_GAIN_PERCENT);
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
            save_item(NAME(new { m_fm_env_attenuation }));
            save_item(NAME(new { m_fm_feedback }));
            save_item(NAME(new { m_fm_feedback_in }));
            save_item(NAME(new { m_fm_stage }));
            save_item(NAME(new { m_fm_key_state }));
            save_item(NAME(new { m_fm_ssg_inverted }));
            save_item(NAME(new { m_fm_key_mask }));
            save_item(NAME(new { m_fm_csm_key_mask }));
            save_item(NAME(new { m_block_freq_latch }));
            save_item(NAME(new { m_fm_clock_accumulator }));
            save_item(NAME(new { m_adpcma_clock_accumulator }));
            save_item(NAME(new { m_fm_held_sample }));
            save_item(NAME(new { m_env_counter }));
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
            Array.Clear(m_fm_env_attenuation, 0, m_fm_env_attenuation.Length);
            Array.Clear(m_fm_feedback, 0, m_fm_feedback.Length);
            Array.Clear(m_fm_feedback_in, 0, m_fm_feedback_in.Length);
            Array.Clear(m_fm_stage, 0, m_fm_stage.Length);
            Array.Clear(m_fm_key_state, 0, m_fm_key_state.Length);
            Array.Clear(m_fm_ssg_inverted, 0, m_fm_ssg_inverted.Length);
            Array.Clear(m_fm_key_mask, 0, m_fm_key_mask.Length);
            Array.Clear(m_fm_csm_key_mask, 0, m_fm_csm_key_mask.Length);
            Array.Clear(m_block_freq_latch, 0, m_block_freq_latch.Length);
            for (int channel = 0; channel < FM_CHANNELS; channel++)
            {
                for (int slot = 0; slot < FM_OPERATORS_PER_CHANNEL; slot++)
                {
                    m_fm_stage[channel, slot] = fm_envelope_stage.Off;
                    m_fm_env_attenuation[channel, slot] = 0x3ff;
                }
            }
            Array.Clear(m_ssg_phase, 0, m_ssg_phase.Length);
            m_ssg_noise_phase = 0;
            m_ssg_noise_lfsr = 1;
            m_ssg_noise_output = 1;
            m_test_tone_phase = 0;
            m_fm_clock_accumulator = 0;
            m_adpcma_clock_accumulator = 0;
            m_fm_held_sample = 0;
            m_env_counter = 0;
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
                int fm = ClockOpnFm(sampleRate) + ClockSimpleSsg(sampleRate);
                if (m_test_tone)
                    fm += ClockTestTone(sampleRate);
                fm = ApplyMixGain(fm, m_fm_mix_gain_percent);
                outputs[0].put_int_clamp(sample, fm, 32768);

                int left = 0;
                int right = 0;
                u8 ended = 0;

                m_adpcma_clock_accumulator += AdpcmAClockRate();
                while (m_adpcma_clock_accumulator >= sampleRate)
                {
                    m_adpcma_clock_accumulator -= sampleRate;
                    for (int channel = 0; channel < ADPCMA_CHANNELS; channel++)
                    {
                        if (ClockAdpcmA(channel))
                            ended |= (u8)(1 << channel);
                    }
                }

                for (int channel = 0; channel < ADPCMA_CHANNELS; channel++)
                    MixAdpcmA(channel, ref left, ref right);

                if (ClockAdpcmB())
                    ended |= 0x80;
                MixAdpcmB(ref left, ref right);

                m_eos_status = (u8)(m_eos_status | ended);
                outputs[1].put_int_clamp(sample, left, 32768);
                outputs[2].put_int_clamp(sample, right, 32768);
            }
        }


        static int ParseGainPercent(string name, int defaultPercent)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int value))
                return Math.Clamp(value, 25, 600);

            return defaultPercent;
        }


        public void set_neogeo_mix_gain_percent(int adpcmaPercent, int musicPercent)
        {
            m_adpcma_mix_gain_percent = ClampMixGainPercent(adpcmaPercent);
            m_adpcmb_mix_gain_percent = ClampMixGainPercent(musicPercent);
            m_fm_mix_gain_percent = ClampMixGainPercent(musicPercent);
        }


        static int ClampMixGainPercent(int value)
        {
            return Math.Clamp(value, 0, 600);
        }


        static int ApplyMixGain(int value, int gainPercent)
        {
            long scaled = (long)value * gainPercent / 100;
            return (int)Math.Clamp(scaled, short.MinValue, short.MaxValue);
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
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                    m_flag_mask = (u8)(~data & EOS_FLAGS_MASK);
                    m_eos_status = (u8)(m_eos_status & ~(data & EOS_FLAGS_MASK));
                    if ((data & 0x80) != 0)
                        m_adpcmb_status &= ~(u32)ADPCMB_STATUS_EOS;
                    break;
                case >= 0x10 and < 0x1c:
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                    WriteAdpcmB((u8)(m_address[0] & 0x0f), data);
                    break;
                case 0x24:
                case 0x25:
                case 0x26:
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                    reload_timer_register(m_address[0]);
                    break;
                case 0x27:
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                    mode_w(data);
                    break;
                case 0x28:
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                    keyon_w(data);
                    break;
                default:
                    if (write_latched_block_freq(port, m_address[0], data))
                        break;
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                    break;
                }
            }
            else if (port == 1)
            {
                if (m_address[1] < 0x30)
                {
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                    WriteAdpcmA(m_address[1], data);
                }
                else
                {
                    if (write_latched_block_freq(port, m_address[1], data))
                        return;
                    if (index < m_regs.Length)
                        m_regs[index] = data;
                }
            }
        }


        u32 reg_index(int port)
        {
            return (u32)((port << 8) | m_address[port]);
        }


        void mark_busy()
        {
            m_busy_end = machine().time() + attotime.from_ticks(32 * 6, Math.Max(1U, clock()));
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
            if (m_timer_running[index])
                return;

            u32 period = index == 0
                ? (u32)(1024 - (((m_regs[0x24] << 2) | (m_regs[0x25] & 0x03)) & 0x3ff))
                : (u32)(16 * (256 - m_regs[0x26]));
            if (period == 0)
                period = 1;

            u32 clocks = period * (u32)(FM_CHANNELS * FM_OPERATORS_PER_CHANNEL * 6);
            timer.adjust(attotime.from_ticks(clocks, Math.Max(1U, clock())));
            m_timer_running[index] = true;
        }


        void timer_a_expired(s32 param)
        {
            if ((m_regs[0x27] & 0x04) != 0)
                m_status = (u8)(m_status | STATUS_TIMER_A);
            if ((m_regs[0x27] & 0xc0) == 0x80)
                m_fm_csm_key_mask[2] = 0x0f;
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
        }


        bool write_latched_block_freq(int port, u8 address, u8 data)
        {
            if ((address & 0xf0) != 0xa0)
                return false;

            int channel = address & 0x03;
            if (channel == 3)
                return true;

            int latch = (port * 2) + ((address >> 3) & 0x01);
            if ((address & 0x04) != 0)
            {
                m_block_freq_latch[latch] = (u8)(data & 0x3f);
                return true;
            }

            u32 index = (u32)((port << 8) | address);
            if (index < m_regs.Length)
                m_regs[index] = data;
            u32 highIndex = index | 0x04;
            if (highIndex < m_regs.Length)
                m_regs[highIndex] = m_block_freq_latch[latch];
            return true;
        }


        int ClockOpnFm(double sampleRate)
        {
            if (sampleRate <= 0)
                sampleRate = OpnFmClockRate();

            m_fm_clock_accumulator += OpnFmClockRate();
            while (m_fm_clock_accumulator >= sampleRate)
            {
                m_fm_clock_accumulator -= sampleRate;
                m_fm_held_sample = clock_fm_once();
            }

            return Math.Clamp((int)(m_fm_held_sample * FM_MIX_GAIN), -32768, 32767);
        }


        double OpnFmClockRate()
        {
            return Math.Max(1.0, clock() / 144.0);
        }


        double AdpcmAClockRate()
        {
            return Math.Max(1.0, OpnFmClockRate() / 3.0);
        }


        int clock_fm_once()
        {
            for (int channel = 0; channel < FM_CHANNELS; channel++)
                prepare_fm_channel(channel);

            if ((++m_env_counter & 0x03) == 3)
                m_env_counter++;

            for (int channel = 0; channel < FM_CHANNELS; channel++)
                clock_fm_channel(channel);

            int fm = 0;
            for (int channel = 0; channel < FM_CHANNELS; channel++)
            {
                if ((YM2610_FM_CHANNEL_MASK & (1 << channel)) != 0)
                    fm += output_fm_channel(channel);
            }

            return roundtrip_fp(Math.Clamp(fm, -32768, 32767));
        }


        void prepare_fm_channel(int channel)
        {
            for (int slot = 0; slot < FM_OPERATORS_PER_CHANNEL; slot++)
            {
                bool wasOn = m_fm_key_state[channel, slot];
                bool isOn = ((m_fm_key_mask[channel] | m_fm_csm_key_mask[channel]) & (1 << slot)) != 0;
                if (wasOn == isOn)
                    continue;

                m_fm_key_state[channel, slot] = isOn;
                if (isOn)
                {
                    int blockFreq = operator_block_freq(channel, slot, channel_block_freq(channel));
                    int slotOffset = s_opn_operator_offset[channel % 3, slot];
                    int bank = register_bank(channel);
                    u8 attack = m_regs[bank + 0x50 + slotOffset];
                    int keycode = keycode_from_block_freq(blockFreq);
                    int keyScale = (attack >> 6) & 0x03;
                    int attackRate = effective_rate((attack & 0x1f) * 2, keycode >> (keyScale ^ 3));

                    m_fm_stage[channel, slot] = fm_envelope_stage.Attack;
                    m_fm_ssg_inverted[channel, slot] = ssg_eg_enabled(channel, slotOffset) && ((ssg_eg_mode(channel, slotOffset) & 0x04) != 0);
                    m_fm_env_attenuation[channel, slot] = attackRate >= 62 ? 0 : 0x3ff;
                    m_fm_phase[channel, slot] = 0.0;
                }
                else
                {
                    if (m_fm_stage[channel, slot] < fm_envelope_stage.Release && m_fm_ssg_inverted[channel, slot])
                    {
                        m_fm_env_attenuation[channel, slot] = (0x200 - m_fm_env_attenuation[channel, slot]) & 0x3ff;
                        m_fm_ssg_inverted[channel, slot] = false;
                    }
                    m_fm_stage[channel, slot] = fm_envelope_stage.Release;
                }
            }
            m_fm_csm_key_mask[channel] = 0;
        }


        void clock_fm_channel(int channel)
        {
            int blockFreq = channel_block_freq(channel);
            if (blockFreq == 0)
                return;

            m_fm_feedback[channel, 0] = m_fm_feedback[channel, 1];
            m_fm_feedback[channel, 1] = m_fm_feedback_in[channel];
            for (int slot = 0; slot < FM_OPERATORS_PER_CHANNEL; slot++)
                clock_fm_operator_state(channel, slot, operator_block_freq(channel, slot, blockFreq));
        }


        int output_fm_channel(int channel)
        {
            int blockFreq = channel_block_freq(channel);
            if (blockFreq == 0)
                return 0;

            int bank = register_bank(channel);
            int ch = channel % 3;
            int feedback = (m_regs[bank + 0xb0 + ch] >> 3) & 0x07;
            int algorithm = m_regs[bank + 0xb0 + ch] & 0x07;
            int feedbackInput = feedback == 0 ? 0 : (m_fm_feedback[channel, 0] + m_fm_feedback[channel, 1]) >> (10 - feedback);

            int op0 = compute_fm_operator_volume(channel, 0, feedbackInput);
            m_fm_feedback_in[channel] = op0;
            int [] opout = m_fm_opout;
            Array.Clear(opout, 0, opout.Length);
            opout[0] = 0;
            opout[1] = op0;

            int algorithmOps = s_opn_algorithm_ops[algorithm & 0x07];
            opout[2] = compute_fm_operator_volume(channel, 1, opout[algorithmOps & 0x01] >> 1);
            opout[5] = opout[1] + opout[2];
            opout[3] = compute_fm_operator_volume(channel, 2, opout[(algorithmOps >> 1) & 0x07] >> 1);
            opout[6] = opout[1] + opout[3];
            opout[7] = opout[2] + opout[3];
            int op3 = compute_fm_operator_volume(channel, 3, opout[(algorithmOps >> 4) & 0x07] >> 1);

            int result = op3;
            if ((algorithmOps & (1 << 7)) != 0)
                result += opout[1];
            if ((algorithmOps & (1 << 8)) != 0)
                result += opout[2];
            if ((algorithmOps & (1 << 9)) != 0)
                result += opout[3];

            return Math.Clamp(result, -32768, 32767);
        }


        int channel_block_freq(int channel)
        {
            int bank = register_bank(channel);
            int ch = channel % 3;
            return ((m_regs[bank + 0xa4 + ch] & 0x3f) << 8) | m_regs[bank + 0xa0 + ch];
        }


        int operator_block_freq(int channel, int slot, int normalBlockFreq)
        {
            int ch = channel % 3;
            if (ch != 2 || (m_regs[0x27] & 0xc0) == 0)
                return normalBlockFreq;

            int bank = register_bank(channel);
            int slotOffset = s_opn_operator_offset[ch, slot];
            switch (slotOffset)
            {
            case 2:
                return ((m_regs[bank + 0xac + 1] & 0x3f) << 8) | m_regs[bank + 0xa8 + 1];
            case 10:
                return ((m_regs[bank + 0xac + 2] & 0x3f) << 8) | m_regs[bank + 0xa8 + 2];
            case 6:
                return ((m_regs[bank + 0xac + 0] & 0x3f) << 8) | m_regs[bank + 0xa8 + 0];
            default:
                return normalBlockFreq;
            }
        }


        void clock_fm_operator_state(int channel, int slot, int blockFreq)
        {
            int bank = register_bank(channel);
            int slotOffset = s_opn_operator_offset[channel % 3, slot];
            u8 dtMul = m_regs[bank + 0x30 + slotOffset];
            u8 attack = m_regs[bank + 0x50 + slotOffset];
            u8 decay = m_regs[bank + 0x60 + slotOffset];
            u8 sustainRate = m_regs[bank + 0x70 + slotOffset];
            u8 sustainLevel = m_regs[bank + 0x80 + slotOffset];
            u8 release = m_regs[bank + 0x80 + slotOffset];

            int detune = (dtMul >> 4) & 0x07;
            int keycode = keycode_from_block_freq(blockFreq);
            if (ssg_eg_enabled(channel, slotOffset))
                clock_ssg_eg_state(channel, slot, slotOffset);
            else
                m_fm_ssg_inverted[channel, slot] = false;
            clock_envelope(channel, slot, attack, decay, sustainRate, sustainLevel, release, keycode);
            int phaseStep = block_freq_to_phase_step(blockFreq, dtMul & 0x0f, detune_adjustment(detune, keycode));
            m_fm_phase[channel, slot] += phaseStep;
            m_fm_phase[channel, slot] %= FM_PHASE_COUNTER_SCALE;
        }


        int compute_fm_operator_volume(int channel, int slot, int modulation)
        {
            int bank = register_bank(channel);
            int slotOffset = s_opn_operator_offset[channel % 3, slot];
            u8 totalLevel = m_regs[bank + 0x40 + slotOffset];
            int phase = (((int)m_fm_phase[channel, slot] >> 10) + modulation) & FM_PHASE_MASK;
            int envelopeAttenuation = m_fm_env_attenuation[channel, slot];
            if (m_fm_ssg_inverted[channel, slot])
                envelopeAttenuation = (0x200 - envelopeAttenuation) & 0x3ff;
            if (m_fm_env_attenuation[channel, slot] > FM_ENVELOPE_QUIET)
                return 0;

            int attenuation = Math.Min(envelopeAttenuation + ((totalLevel & 0x7f) << 3), 0x3ff);
            int sineAttenuation = phase_to_attenuation(phase);
            int amplitude = attenuation_to_amplitude(sineAttenuation + (attenuation << 2));
            int output = (phase & 0x200) != 0 ? -amplitude : amplitude;
            return Math.Clamp(output, FM_OPERATOR_MIN, FM_OPERATOR_MAX);
        }


        int block_freq_to_phase_step(int blockFreq, int multiple, int detunePhaseStep)
        {
            int fnum = (blockFreq & 0x7ff) << 1;
            int block = (blockFreq >> 11) & 0x07;
            int phaseStep = (fnum << block) >> 2;
            phaseStep = (phaseStep + detunePhaseStep) & 0x1ffff;
            int x1Multiple = multiple * 2;
            if (x1Multiple == 0)
                x1Multiple = 1;

            return (phaseStep * x1Multiple) >> 1;
        }


        static int keycode_from_block_freq(int blockFreq)
        {
            int keycode = ((blockFreq >> 10) & 0x0f) << 1;
            int fnumBits = (blockFreq >> 7) & 0x0f;
            keycode |= (0xfe80 >> fnumBits) & 1;
            return keycode & 0x1f;
        }


        static int detune_adjustment(int detune, int keycode)
        {
            int result = s_opn_detune_adjustment[keycode & 0x1f, detune & 0x03];
            return (detune & 0x04) != 0 ? -result : result;
        }


        void clock_envelope(int channel, int slot, u8 attack, u8 decay, u8 sustainRateReg, u8 sustainLevel, u8 release, int keycode)
        {
            if ((m_env_counter & 0x03) != 0)
                return;

            int keyScale = (attack >> 6) & 0x03;
            int ksr = keycode >> (keyScale ^ 3);
            int attackRate = effective_rate((attack & 0x1f) * 2, ksr);
            int decayRate = effective_rate((decay & 0x1f) * 2, ksr);
            int sustainRate = effective_rate((sustainRateReg & 0x1f) * 2, ksr);
            int releaseRate = effective_rate(((release & 0x0f) * 4) + 2, ksr);
            int sustain = (sustainLevel >> 4) & 0x0f;
            sustain |= (sustain + 1) & 0x10;
            sustain <<= 5;
            int attenuation = m_fm_env_attenuation[channel, slot];

            if (m_fm_stage[channel, slot] == fm_envelope_stage.Attack && attenuation == 0)
                m_fm_stage[channel, slot] = fm_envelope_stage.Decay;
            if (m_fm_stage[channel, slot] == fm_envelope_stage.Decay && attenuation >= sustain)
                m_fm_stage[channel, slot] = fm_envelope_stage.Sustain;

            switch (m_fm_stage[channel, slot])
            {
            case fm_envelope_stage.Attack:
                if (attackRate == 0)
                    break;
                if (attackRate >= 62)
                {
                    attenuation = 0;
                    m_fm_stage[channel, slot] = fm_envelope_stage.Decay;
                    break;
                }

                int attackIncrement = envelope_increment(attackRate, (int)(m_env_counter >> 2));
                attenuation += ((~attenuation) * attackIncrement) >> 4;
                if (attenuation <= 0)
                {
                    attenuation = 0;
                    m_fm_stage[channel, slot] = fm_envelope_stage.Decay;
                }
                break;

            case fm_envelope_stage.Decay:
                if (decayRate == 0)
                {
                    m_fm_stage[channel, slot] = fm_envelope_stage.Sustain;
                    break;
                }
                attenuation += envelope_increment(decayRate, (int)(m_env_counter >> 2));
                if (attenuation >= sustain)
                {
                    attenuation = sustain;
                    m_fm_stage[channel, slot] = fm_envelope_stage.Sustain;
                }
                break;

            case fm_envelope_stage.Sustain:
                if (!m_fm_key_state[channel, slot])
                {
                    m_fm_stage[channel, slot] = fm_envelope_stage.Release;
                    break;
                }
                attenuation += envelope_increment(sustainRate, (int)(m_env_counter >> 2));
                break;

            case fm_envelope_stage.Release:
                attenuation += envelope_increment(releaseRate, (int)(m_env_counter >> 2));
                if (attenuation >= 0x3ff)
                {
                    attenuation = 0x3ff;
                    m_fm_stage[channel, slot] = fm_envelope_stage.Off;
                }
                break;

            case fm_envelope_stage.Off:
                attenuation = 0x3ff;
                break;
            }

            m_fm_env_attenuation[channel, slot] = Math.Clamp(attenuation, 0, 0x3ff);
        }


        void clock_ssg_eg_state(int channel, int slot, int slotOffset)
        {
            if ((m_fm_env_attenuation[channel, slot] & 0x200) == 0)
                return;

            int mode = ssg_eg_mode(channel, slotOffset);
            if ((mode & 0x01) != 0)
            {
                m_fm_ssg_inverted[channel, slot] = (((mode >> 2) & 1) ^ ((mode >> 1) & 1)) != 0;
                if (m_fm_stage[channel, slot] != fm_envelope_stage.Attack)
                    m_fm_env_attenuation[channel, slot] = m_fm_ssg_inverted[channel, slot] ? 0x200 : 0x3ff;
            }
            else
            {
                if ((mode & 0x02) != 0)
                    m_fm_ssg_inverted[channel, slot] = !m_fm_ssg_inverted[channel, slot];

                if (m_fm_stage[channel, slot] == fm_envelope_stage.Decay || m_fm_stage[channel, slot] == fm_envelope_stage.Sustain)
                    m_fm_stage[channel, slot] = fm_envelope_stage.Attack;

                if ((mode & 0x02) == 0)
                    m_fm_phase[channel, slot] = 0.0;
            }

            if (m_fm_stage[channel, slot] == fm_envelope_stage.Release)
                m_fm_env_attenuation[channel, slot] = 0x3ff;
        }


        bool ssg_eg_enabled(int channel, int slotOffset)
        {
            return (m_regs[register_bank(channel) + 0x90 + slotOffset] & 0x08) != 0;
        }


        int ssg_eg_mode(int channel, int slotOffset)
        {
            return m_regs[register_bank(channel) + 0x90 + slotOffset] & 0x07;
        }


        static int effective_rate(int rawRate, int ksr)
        {
            return rawRate == 0 ? 0 : Math.Min(rawRate + ksr, 63);
        }


        static int envelope_increment(int rate, int envCounter)
        {
            rate = Math.Clamp(rate, 0, 63);
            if (rate == 0)
                return 0;

            int shift = 11 - (rate >> 2);
            if (shift < 0)
                shift = 0;
            if ((envCounter & ((1 << shift) - 1)) != 0)
                return 0;

            int index = (envCounter >> shift) & 0x07;
            return (int)((s_opn_attenuation_increment[rate] >> (index * 4)) & 0x0f);
        }


        static int phase_to_attenuation(int phase)
        {
            int index = phase & 0x1ff;
            if ((index & 0x100) != 0)
                index = (~index) & 0xff;
            return s_opn_log_sine_table[index];
        }


        static int attenuation_to_amplitude(int attenuation)
        {
            int intPart = (attenuation >> 8) & 0x1f;
            if (intPart >= 13)
                return 0;

            int fractPart = attenuation & 0xff;
            return ((s_opn_pow2_table[fractPart] << 2) & 0xffff) >> intPart;
        }


        static int register_bank(int channel)
        {
            return channel >= 3 ? 0x100 : 0x000;
        }


        static int roundtrip_fp(int value)
        {
            if (value < -32768)
                return -32768;
            if (value > 32767)
                return 32767;

            int scan = value ^ (value >> 31);
            int leading = 0;
            uint bits = (uint)(scan << 17);
            while (leading < 32 && (bits & 0x80000000U) == 0)
            {
                leading++;
                bits <<= 1;
            }

            int exponent = Math.Max(7 - leading, 1) - 1;
            int mask = (1 << exponent) - 1;
            return value & ~mask;
        }


        static readonly ushort [] s_opn_log_sine_table = build_log_sine_table();
        static readonly ushort [] s_opn_pow2_table = build_pow2_table();
        static readonly u32 [] s_opn_attenuation_increment =
        {
            0x00000000, 0x00000000, 0x10101010, 0x10101010,
            0x10101010, 0x10101010, 0x11101110, 0x11101110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x10101010, 0x10111010, 0x11101110, 0x11111110,
            0x11111111, 0x21112111, 0x21212121, 0x22212221,
            0x22222222, 0x42224222, 0x42424242, 0x44424442,
            0x44444444, 0x84448444, 0x84848484, 0x88848884,
            0x88888888, 0x88888888, 0x88888888, 0x88888888
        };


        static ushort [] build_log_sine_table()
        {
            ushort [] table = new ushort[256];
            for (int i = 0; i < table.Length; i++)
            {
                double n = ((i << 1) | 1) / 512.0;
                double sine = Math.Sin(n * Math.PI / 2.0);
                table[i] = (ushort)Math.Round(-Math.Log(sine, 2.0) * 256.0);
            }
            return table;
        }


        static ushort [] build_pow2_table()
        {
            ushort [] table = new ushort[256];
            for (int i = 0; i < table.Length; i++)
            {
                double n = (i + 1) / 256.0;
                table[i] = (ushort)Math.Round(Math.Pow(2.0, -n) * 2048.0);
            }
            return table;
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
            if ((m_adpcmb_status & ADPCMB_STATUS_PLAYING) == 0 || (m_adpcmb_regs[0x00] & 0x80) == 0)
            {
                m_adpcmb_prev_output = m_adpcmb_output;
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
            m_adpcmb_prev_output = m_adpcmb_output;
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
            value = ApplyMixGain(value, m_adpcmb_mix_gain_percent);

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
            value = ApplyMixGain(value, m_adpcma_mix_gain_percent);

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
