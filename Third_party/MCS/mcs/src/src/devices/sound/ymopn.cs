// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using devcb_write_line = mame.devcb_write<mame.Type_constant_s32, mame.devcb_value_const_unsigned_1<mame.Type_constant_s32>>;
using offs_t = System.UInt32;
using s32 = System.Int32;
using stream_buffer_sample_t = System.Single;
using u8 = System.Byte;
using uint32_t = System.UInt32;

using static mame.device_global;
using static mame.diexec_global;
using static mame.emucore_global;


namespace mame
{
    // ======================> ym2203_device
    //
    // YM2203 implementation backed by the existing AY/YM SSG core plus a small
    // OPN FM renderer. Timer/status/IRQ behaviour follows the YM2203 register
    // contract used by MAME drivers; the FM renderer consumes the real OPN
    // operator/channel registers instead of mutating PSG state.
    public class ym2203_device : ay8910_device
    {
        public static readonly emu.detail.device_type_impl YM2203 = DEFINE_DEVICE_TYPE("ym2203", "YM2203 OPN", (type, mconfig, tag, owner, clock) => { return new ym2203_device(mconfig, tag, owner, clock); });

        const u8 STATUS_TIMERA = 0x01;
        const u8 STATUS_TIMERB = 0x02;
        const u8 STATUS_BUSY = 0x80;
        const int FM_CHANNELS = 3;
        const int FM_OPERATORS_PER_CHANNEL = 4;
        const int OPN_OPERATORS = 12;
        const int OPN_DEFAULT_PRESCALE = 6;
        const double FM_MIX_GAIN = 1.0;
        const int FM_PHASE_MASK = 0x3ff;
        const int FM_OPERATOR_MIN = -0x2000;
        const int FM_OPERATOR_MAX = 0x1fff;
        const int FM_ENVELOPE_QUIET = 0x380;
        const int FM_PHASE_COUNTER_SCALE = 1 << 20;
        static readonly int [,] OPN_OPERATOR_OFFSET =
        {
            { 0, 8, 4, 12 },
            { 1, 9, 5, 13 },
            { 2, 10, 6, 14 }
        };
        static readonly int [] OPN_ALGORITHM_OPS =
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
        static readonly u8 [,] OPN_DETUNE_ADJUSTMENT =
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

        enum fm_envelope_stage
        {
            Attack,
            Decay,
            Sustain,
            Release,
            Off
        }

        readonly bool m_trace;
        readonly devcb_write_line m_irq_handler;
        readonly u8 [] m_opn_regs = new u8[0x100];
        readonly double [,] m_fm_phase = new double[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly double [,] m_fm_env = new double[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly int [,] m_fm_env_attenuation = new int[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly double [,] m_fm_last = new double[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly int [,] m_fm_int_last = new int[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly int [,] m_fm_feedback = new int[FM_CHANNELS, 2];
        readonly int [] m_fm_feedback_in = new int[FM_CHANNELS];
        readonly fm_envelope_stage [,] m_fm_stage = new fm_envelope_stage[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly bool [,] m_fm_key_state = new bool[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly bool [,] m_fm_ssg_inverted = new bool[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly int [] m_fm_opout = new int[8];
        readonly u8 [] m_fm_key_mask = new u8[FM_CHANNELS];
        readonly u8 [] m_block_freq_latch = new u8[2];
        readonly bool [] m_timer_running = new bool[2];
        emu_timer m_timer_a;
        emu_timer m_timer_b;
        attotime m_busy_end;
        u8 m_address;
        u8 m_status;
        int m_clock_prescale;
        double m_fm_clock_accumulator;
        int m_fm_held_sample;
        float m_psg_route0_gain = 1.0f;
        float m_fm_route_gain = 1.0f;
        int m_trace_write_count;
        u8 m_last_trace_address;
        u8 m_last_trace_data;
        uint32_t m_env_counter;

        ym2203_device(machine_config mconfig, string tag, device_t owner, uint32_t clock)
            : base(mconfig, YM2203, tag, owner, clock, psg_type_t.PSG_TYPE_YM, 3, 2)
        {
            m_trace = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM2203_TRACE") == "1";
            m_irq_handler = new devcb_write_line(this);
        }

        public devcb_write_line.binder irq_handler() { return m_irq_handler.bind(); }

        public void write(offs_t offset, u8 data)
        {
            if ((offset & 1) == 0)
                ym2203_address_w(data);
            else
                ym2203_data_w(data);
        }

        public u8 read(offs_t offset)
        {
            return (offset & 1) == 0 ? read_status() : ym2203_data_r();
        }

        public void add_route(int index, string tag, float gain)
        {
            if (index >= 0 && index < 3)
            {
                if (index == 0)
                    m_psg_route0_gain = gain;
                base.add_route((uint32_t)index, tag, gain);
            }
            else if (index == 3)
                m_fm_route_gain = gain;
        }

        protected override void device_start()
        {
            base.device_start();

            m_irq_handler.resolve_safe();
            m_timer_a = timer_alloc(timer_a_expired);
            m_timer_b = timer_alloc(timer_b_expired);

            save_item(NAME(new { m_address }));
            save_item(NAME(new { m_status }));
            save_item(NAME(new { m_busy_end }));
            save_item(NAME(new { m_clock_prescale }));
            save_item(NAME(new { m_fm_clock_accumulator }));
            save_item(NAME(new { m_fm_held_sample }));
            save_item(NAME(new { m_psg_route0_gain }));
            save_item(NAME(new { m_fm_route_gain }));
            save_item(NAME(new { m_opn_regs }));
            save_item(NAME(new { m_fm_feedback }));
            save_item(NAME(new { m_fm_feedback_in }));
            save_item(NAME(new { m_fm_ssg_inverted }));
        }

        protected override void device_reset()
        {
            base.device_reset();

            Array.Clear(m_opn_regs, 0, m_opn_regs.Length);
            Array.Clear(m_fm_phase, 0, m_fm_phase.Length);
            Array.Clear(m_fm_env, 0, m_fm_env.Length);
            Array.Clear(m_fm_env_attenuation, 0, m_fm_env_attenuation.Length);
            Array.Clear(m_fm_last, 0, m_fm_last.Length);
            Array.Clear(m_fm_int_last, 0, m_fm_int_last.Length);
            Array.Clear(m_fm_feedback, 0, m_fm_feedback.Length);
            Array.Clear(m_fm_feedback_in, 0, m_fm_feedback_in.Length);
            Array.Clear(m_fm_stage, 0, m_fm_stage.Length);
            Array.Clear(m_fm_key_state, 0, m_fm_key_state.Length);
            Array.Clear(m_fm_ssg_inverted, 0, m_fm_ssg_inverted.Length);
            Array.Clear(m_fm_key_mask, 0, m_fm_key_mask.Length);
            Array.Clear(m_block_freq_latch, 0, m_block_freq_latch.Length);
            for (int channel = 0; channel < FM_CHANNELS; channel++)
            {
                for (int slot = 0; slot < FM_OPERATORS_PER_CHANNEL; slot++)
                {
                    m_fm_stage[channel, slot] = fm_envelope_stage.Off;
                    m_fm_env_attenuation[channel, slot] = 0x3ff;
                }
            }
            m_address = 0;
            m_status = 0;
            m_busy_end = attotime.zero;
            m_clock_prescale = OPN_DEFAULT_PRESCALE;
            m_fm_clock_accumulator = 0.0;
            m_fm_held_sample = 0;
            m_env_counter = 0;
            m_trace_write_count = 0;
            m_last_trace_address = 0xff;
            m_last_trace_data = 0xff;
            m_timer_running[0] = false;
            m_timer_running[1] = false;
            if (m_timer_a != null)
                m_timer_a.enable(false);
            if (m_timer_b != null)
                m_timer_b.enable(false);
            update_irq();
        }

        static int Algorithm(int op2in, int op3in, int op4in, bool op1out, bool op2out, bool op3out)
        {
            return op2in
                | (op3in << 1)
                | (op4in << 4)
                | (op1out ? 1 << 7 : 0)
                | (op2out ? 1 << 8 : 0)
                | (op3out ? 1 << 9 : 0);
        }

        void ym2203_address_w(u8 data)
        {
            m_address = data;
            if (m_trace)
                logerror("{0}: {1} address 0x{2:X2}\n", machine().describe_context(), tag(), m_address);

            if (m_address <= 0x0f)
                base.address_w(data);
            else if (m_address == 0x2d)
                update_prescale(6);
            else if (m_address == 0x2e && m_clock_prescale == 6)
                update_prescale(3);
            else if (m_address == 0x2f)
                update_prescale(2);
        }

        void ym2203_data_w(u8 data)
        {
            if (m_trace)
            {
                bool duplicate = m_address == m_last_trace_address && data == m_last_trace_data;
                bool interesting = (m_address == 0x28 && data != 0) || (m_address >= 0x30 && m_address <= 0xb6);
                if (interesting && !duplicate && m_trace_write_count < 2048)
                {
                    logerror("{0}: {1} write reg=0x{2:X2} data=0x{3:X2}\n", machine().describe_context(), tag(), m_address, data);
                    Console.Error.WriteLine($"[YM2203] {tag()} reg=0x{m_address:X2} data=0x{data:X2}");
                    m_trace_write_count++;
                }
                m_last_trace_address = m_address;
                m_last_trace_data = data;
            }

            if (m_address <= 0x0f)
            {
                base.data_w(data);
                mark_busy();
                return;
            }

            update_sound_stream();

            if (write_latched_block_freq(data))
            {
                mark_busy();
                return;
            }

            m_opn_regs[m_address] = data;

            switch (m_address)
            {
            case 0x24:
            case 0x25:
            case 0x26:
                reload_timer_register(m_address);
                break;
            case 0x27:
                mode_w(data);
                break;
            case 0x28:
                keyon_w(data);
                break;
            }

            mark_busy();
        }

        u8 read_status()
        {
            u8 result = m_status;
            if (machine().time() < m_busy_end)
                result |= STATUS_BUSY;
            return result;
        }

        void mark_busy()
        {
            m_busy_end = machine().time() + attotime.from_ticks((uint32_t)(32 * m_clock_prescale), clock());
        }

        void update_prescale(int prescale)
        {
            if (m_clock_prescale == prescale)
                return;

            m_clock_prescale = prescale;
            for (int index = 0; index < 2; index++)
            {
                if (!m_timer_running[index])
                    continue;

                m_timer_running[index] = false;
                update_timer(index, (m_opn_regs[0x27] & (1 << index)) != 0);
            }
        }

        u8 ym2203_data_r()
        {
            return m_address <= 0x0f ? data_r() : (u8)0xff;
        }

        bool write_latched_block_freq(u8 data)
        {
            if ((m_address & 0xf0) != 0xa0)
                return false;

            int channel = m_address & 0x03;
            if (channel == 3)
                return true;

            int latch = (m_address >> 3) & 0x01;
            if ((m_address & 0x04) != 0)
            {
                // MAME/ymfm uses temporary B8/B9 latches: upper FNUM/block writes
                // don't affect the active channel frequency until the low byte arrives.
                m_block_freq_latch[latch] = (u8)(data & 0x3f);
                return true;
            }

            m_opn_regs[m_address] = data;
            m_opn_regs[m_address | 0x04] = m_block_freq_latch[latch];
            return true;
        }

        void mode_w(u8 data)
        {
            if ((data & 0x10) != 0)
                m_status = (u8)(m_status & ~STATUS_TIMERA);
            if ((data & 0x20) != 0)
                m_status = (u8)(m_status & ~STATUS_TIMERB);

            update_timer(0, (data & 0x01) != 0);
            update_timer(1, (data & 0x02) != 0);
            update_irq();
        }

        void reload_timer_register(u8 reg)
        {
            u8 mode = m_opn_regs[0x27];
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

            uint32_t period = index == 0
                ? (uint32_t)(1024 - timer_a_value())
                : (uint32_t)(16 * (256 - m_opn_regs[0x26]));
            if (period == 0)
                period = 1;

            uint32_t clocks = period * OPN_OPERATORS * (uint32_t)m_clock_prescale;
            timer.adjust(attotime.from_ticks(clocks, clock()));
            m_timer_running[index] = true;
        }

        int timer_a_value()
        {
            return ((m_opn_regs[0x24] << 2) | (m_opn_regs[0x25] & 0x03)) & 0x3ff;
        }

        void timer_a_expired(int param)
        {
            if ((m_opn_regs[0x27] & 0x04) != 0)
                m_status = (u8)(m_status | STATUS_TIMERA);
            update_irq();
            m_timer_running[0] = false;
            update_timer(0, (m_opn_regs[0x27] & 0x01) != 0);
        }

        void timer_b_expired(int param)
        {
            if ((m_opn_regs[0x27] & 0x08) != 0)
                m_status = (u8)(m_status | STATUS_TIMERB);
            update_irq();
            m_timer_running[1] = false;
            update_timer(1, (m_opn_regs[0x27] & 0x02) != 0);
        }

        void update_irq()
        {
            u8 enabled = 0;
            if ((m_opn_regs[0x27] & 0x04) != 0)
                enabled |= STATUS_TIMERA;
            if ((m_opn_regs[0x27] & 0x08) != 0)
                enabled |= STATUS_TIMERB;

            m_irq_handler.op_s32((m_status & enabled) != 0 ? ASSERT_LINE : CLEAR_LINE);
        }

        void keyon_w(u8 data)
        {
            int channel = data & 0x03;
            if (channel >= FM_CHANNELS)
                return;

            u8 newMask = (u8)(data >> 4);
            m_fm_key_mask[channel] = newMask;
        }

        void prepare_fm_channel(int channel)
        {
            for (int slot = 0; slot < FM_OPERATORS_PER_CHANNEL; slot++)
            {
                bool wasOn = m_fm_key_state[channel, slot];
                bool isOn = (m_fm_key_mask[channel] & (1 << slot)) != 0;
                if (wasOn == isOn)
                    continue;

                m_fm_key_state[channel, slot] = isOn;
                if (isOn)
                {
                    int blockFreq = operator_block_freq(channel, slot, channel_block_freq(channel));
                    int slotOffset = OPN_OPERATOR_OFFSET[channel, slot];
                    u8 attack = m_opn_regs[0x50 + slotOffset];
                    int keycode = keycode_from_block_freq(blockFreq);
                    int keyScale = (attack >> 6) & 0x03;
                    int attackRate = effective_rate((attack & 0x1f) * 2, keycode >> (keyScale ^ 3));

                    m_fm_stage[channel, slot] = fm_envelope_stage.Attack;
                    m_fm_ssg_inverted[channel, slot] = ssg_eg_enabled(slotOffset) && ((ssg_eg_mode(slotOffset) & 0x04) != 0);
                    m_fm_env[channel, slot] = 0.0;
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
        }

        protected override void device_sound_interface_sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
        {
            base.device_sound_interface_sound_stream_update(stream, inputs, outputs);
            mix_fm(outputs);
        }

        void mix_fm(std.vector<write_stream_view> outputs)
        {
            if (outputs.empty())
                return;

            s32 samples = (s32)outputs[0].samples();
            double sampleRate = outputs[0].sample_rate();
            if (sampleRate <= 0)
                sampleRate = Math.Max(1, fm_source_sample_rate());
            double opnSampleRate = fm_source_sample_rate();

            for (s32 sample = 0; sample < samples; sample++)
            {
                m_fm_clock_accumulator += opnSampleRate;
                while (m_fm_clock_accumulator >= sampleRate)
                {
                    m_fm_clock_accumulator -= sampleRate;
                    m_fm_held_sample = clock_fm_once(opnSampleRate);
                }

                double routeCompensation = m_psg_route0_gain > 0.0f ? m_fm_route_gain / m_psg_route0_gain : m_fm_route_gain;
                stream_buffer_sample_t mixed = (stream_buffer_sample_t)Math.Clamp((m_fm_held_sample / 32768.0) * FM_MIX_GAIN * routeCompensation, -0.90, 0.90);
                outputs[0].put(sample, outputs[0].get(sample) + mixed);
            }
        }

        int clock_fm_once(double opnSampleRate)
        {
            for (int channel = 0; channel < FM_CHANNELS; channel++)
                prepare_fm_channel(channel);

            if ((++m_env_counter & 0x03) == 3)
                m_env_counter++;

            for (int channel = 0; channel < FM_CHANNELS; channel++)
                clock_fm_channel(channel);

            int fm = 0;
            for (int channel = 0; channel < FM_CHANNELS; channel++)
                fm += output_fm_channel(channel);

            return roundtrip_fp(Math.Clamp(fm, -32768, 32767));
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

        void clock_fm_channel(int channel)
        {
            int blockFreq = channel_block_freq(channel);
            if (blockFreq == 0)
                return;

            int feedback = (m_opn_regs[0xb0 + channel] >> 3) & 0x07;
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

            int feedback = (m_opn_regs[0xb0 + channel] >> 3) & 0x07;
            int algorithm = m_opn_regs[0xb0 + channel] & 0x07;
            int feedbackInput = feedback == 0 ? 0 : (m_fm_feedback[channel, 0] + m_fm_feedback[channel, 1]) >> (10 - feedback);

            int op0 = compute_fm_operator_volume(channel, 0, feedbackInput);
            m_fm_feedback_in[channel] = op0;
            int [] opout = m_fm_opout;
            Array.Clear(opout, 0, opout.Length);
            opout[0] = 0;
            opout[1] = op0;

            int algorithmOps = OPN_ALGORITHM_OPS[algorithm & 0x07];
            opout[2] = compute_fm_operator_volume(channel, 1, opout[algorithmOps & 0x01] >> 1);
            opout[5] = opout[1] + opout[2];
            opout[3] = compute_fm_operator_volume(channel, 2, opout[(algorithmOps >> 1) & 0x07] >> 1);
            opout[6] = opout[1] + opout[3];
            opout[7] = opout[2] + opout[3];
            int op3 = compute_fm_operator_volume(channel, 3, opout[(algorithmOps >> 4) & 0x07] >> 1);

            m_fm_last[channel, 0] = op0;
            m_fm_last[channel, 1] = opout[2];
            m_fm_last[channel, 2] = opout[3];
            m_fm_last[channel, 3] = op3;
            m_fm_int_last[channel, 0] = op0;
            m_fm_int_last[channel, 1] = opout[2];
            m_fm_int_last[channel, 2] = opout[3];
            m_fm_int_last[channel, 3] = op3;

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
            return ((m_opn_regs[0xa4 + channel] & 0x3f) << 8) | m_opn_regs[0xa0 + channel];
        }

        int operator_block_freq(int channel, int slot, int normalBlockFreq)
        {
            if (channel != 2 || (m_opn_regs[0x27] & 0xc0) == 0)
                return normalBlockFreq;

            int slotOffset = OPN_OPERATOR_OFFSET[channel, slot];
            switch (slotOffset)
            {
            case 2:
                return ((m_opn_regs[0xac + 1] & 0x3f) << 8) | m_opn_regs[0xa8 + 1];
            case 10:
                return ((m_opn_regs[0xac + 2] & 0x3f) << 8) | m_opn_regs[0xa8 + 2];
            case 6:
                return ((m_opn_regs[0xac + 0] & 0x3f) << 8) | m_opn_regs[0xa8 + 0];
            default:
                return normalBlockFreq;
            }
        }

        double fm_source_sample_rate()
        {
            // MAME ymfm_opn.h: YM2203 prescale 6 FM updates at input_clock / 72.
            return Math.Max(1.0, clock() / (m_clock_prescale * 12.0));
        }

        void clock_fm_operator_state(int channel, int slot, int blockFreq)
        {
            int slotOffset = OPN_OPERATOR_OFFSET[channel, slot];
            u8 dtMul = m_opn_regs[0x30 + slotOffset];
            u8 attack = m_opn_regs[0x50 + slotOffset];
            u8 decay = m_opn_regs[0x60 + slotOffset];
            u8 sustainRate = m_opn_regs[0x70 + slotOffset];
            u8 sustainLevel = m_opn_regs[0x80 + slotOffset];
            u8 release = m_opn_regs[0x80 + slotOffset];

            int detune = (dtMul >> 4) & 0x07;
            int keycode = keycode_from_block_freq(blockFreq);
            if (ssg_eg_enabled(slotOffset))
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
            int slotOffset = OPN_OPERATOR_OFFSET[channel, slot];
            u8 totalLevel = m_opn_regs[0x40 + slotOffset];
            int phase = (((int)m_fm_phase[channel, slot] >> 10) + modulation) & FM_PHASE_MASK;
            int envelopeAttenuation = m_fm_env_attenuation[channel, slot];
            if (m_fm_ssg_inverted[channel, slot])
                envelopeAttenuation = (0x200 - envelopeAttenuation) & 0x3ff;
            int attenuation = envelopeAttenuation + ((totalLevel & 0x7f) << 3);
            if (attenuation > FM_ENVELOPE_QUIET)
                return 0;

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
            int result = OPN_DETUNE_ADJUSTMENT[keycode & 0x1f, detune & 0x03];
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
            int sustain = ((sustainLevel >> 4) & 0x0f);
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

            int mode = ssg_eg_mode(slotOffset);
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

        bool ssg_eg_enabled(int slotOffset)
        {
            return (m_opn_regs[0x90 + slotOffset] & 0x08) != 0;
        }

        int ssg_eg_mode(int slotOffset)
        {
            return m_opn_regs[0x90 + slotOffset] & 0x07;
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
            return (int)((OPN_ATTENUATION_INCREMENT[rate] >> (index * 4)) & 0x0f);
        }

        static int phase_to_attenuation(int phase)
        {
            int index = phase & 0x1ff;
            if ((index & 0x100) != 0)
                index = (~index) & 0xff;
            return OPN_LOG_SINE_TABLE[index];
        }

        static int attenuation_to_amplitude(int attenuation)
        {
            int intPart = (attenuation >> 8) & 0x1f;
            if (intPart >= 13)
                return 0;

            int fractPart = attenuation & 0xff;
            return ((OPN_POW2_TABLE[fractPart] << 2) & 0xffff) >> intPart;
        }

        static readonly ushort [] OPN_LOG_SINE_TABLE = build_log_sine_table();
        static readonly ushort [] OPN_POW2_TABLE = build_pow2_table();
        static readonly uint32_t [] OPN_ATTENUATION_INCREMENT =
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

    }


    public static class ymopn_global
    {
        public static ym2203_device YM2203(machine_config mconfig, string tag, uint32_t clock) { return emu.detail.device_type_impl.op<ym2203_device>(mconfig, tag, ym2203_device.YM2203, clock); }
        public static ym2203_device YM2203<bool_Required>(machine_config mconfig, device_finder<ym2203_device, bool_Required> finder, uint32_t clock) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, ym2203_device.YM2203, clock); }
    }
}
