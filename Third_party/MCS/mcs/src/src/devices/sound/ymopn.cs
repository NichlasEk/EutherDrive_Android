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
        const int FM_CHANNELS = 3;
        const int FM_OPERATORS_PER_CHANNEL = 4;
        const int OPN_OPERATORS = 12;
        const int OPN_DEFAULT_PRESCALE = 6;
        const double TWO_PI = Math.PI * 2.0;
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

        readonly bool m_trace;
        readonly devcb_write_line m_irq_handler;
        readonly u8 [] m_opn_regs = new u8[0x100];
        readonly double [,] m_fm_phase = new double[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly double [,] m_fm_env = new double[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly double [,] m_fm_last = new double[FM_CHANNELS, FM_OPERATORS_PER_CHANNEL];
        readonly u8 [] m_fm_key_mask = new u8[FM_CHANNELS];
        emu_timer m_timer_a;
        emu_timer m_timer_b;
        u8 m_address;
        u8 m_status;

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
            return (offset & 1) == 0 ? m_status : ym2203_data_r();
        }

        public void add_route(int index, string tag, float gain)
        {
            if (index >= 0 && index < 3)
                base.add_route((uint32_t)index, tag, gain);
            else if (index == 3)
                base.add_route(0, tag, gain);
        }

        protected override void device_start()
        {
            base.device_start();

            m_irq_handler.resolve_safe();
            m_timer_a = timer_alloc(timer_a_expired);
            m_timer_b = timer_alloc(timer_b_expired);

            save_item(NAME(new { m_address }));
            save_item(NAME(new { m_status }));
            save_item(NAME(new { m_opn_regs }));
        }

        protected override void device_reset()
        {
            base.device_reset();

            Array.Clear(m_opn_regs, 0, m_opn_regs.Length);
            Array.Clear(m_fm_phase, 0, m_fm_phase.Length);
            Array.Clear(m_fm_env, 0, m_fm_env.Length);
            Array.Clear(m_fm_last, 0, m_fm_last.Length);
            Array.Clear(m_fm_key_mask, 0, m_fm_key_mask.Length);
            m_address = 0;
            m_status = 0;
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
        }

        void ym2203_data_w(u8 data)
        {
            if (m_trace)
                logerror("{0}: {1} write reg=0x{2:X2} data=0x{3:X2}\n", machine().describe_context(), tag(), m_address, data);

            if (m_address <= 0x0f)
            {
                base.data_w(data);
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
        }

        u8 ym2203_data_r()
        {
            return m_address <= 0x0f ? data_r() : (u8)0xff;
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
                return;
            }

            uint32_t period = index == 0
                ? (uint32_t)(1024 - timer_a_value())
                : (uint32_t)(16 * (256 - m_opn_regs[0x26]));
            if (period == 0)
                period = 1;

            uint32_t clocks = period * OPN_OPERATORS * OPN_DEFAULT_PRESCALE;
            timer.adjust(attotime.from_ticks(clocks, clock()));
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
            update_timer(0, (m_opn_regs[0x27] & 0x01) != 0);
        }

        void timer_b_expired(int param)
        {
            if ((m_opn_regs[0x27] & 0x08) != 0)
                m_status = (u8)(m_status | STATUS_TIMERB);
            update_irq();
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

            m_fm_key_mask[channel] = (u8)(data >> 4);
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
                sampleRate = Math.Max(1, clock() / (OPN_DEFAULT_PRESCALE * 24.0));

            for (s32 sample = 0; sample < samples; sample++)
            {
                double fm = 0.0;
                for (int channel = 0; channel < FM_CHANNELS; channel++)
                    fm += render_fm_channel(channel, sampleRate);

                stream_buffer_sample_t mixed = (stream_buffer_sample_t)Math.Clamp(fm * 0.32, -0.80, 0.80);
                outputs[0].put(sample, outputs[0].get(sample) + mixed);
            }
        }

        double render_fm_channel(int channel, double sampleRate)
        {
            int fnum = m_opn_regs[0xa0 + channel] | ((m_opn_regs[0xa4 + channel] & 0x07) << 8);
            int block = (m_opn_regs[0xa4 + channel] >> 3) & 0x07;
            if (fnum == 0)
                return 0.0;

            double baseFrequency = clock() * fnum * Math.Pow(2.0, block - 21) / 72.0;
            if (baseFrequency <= 0.0)
                return 0.0;

            double feedback = (m_opn_regs[0xb0 + channel] >> 3) & 0x07;
            int algorithm = m_opn_regs[0xb0 + channel] & 0x07;

            double op0 = clock_fm_operator(
                channel,
                0,
                baseFrequency,
                feedback != 0.0 ? (m_fm_last[channel, 0] + m_fm_last[channel, 1]) * feedback * 0.18 : 0.0,
                sampleRate);
            double [] opout = new double[8];
            opout[0] = 0.0;
            opout[1] = op0;

            int algorithmOps = OPN_ALGORITHM_OPS[algorithm & 0x07];
            opout[2] = clock_fm_operator(channel, 1, baseFrequency, opout[algorithmOps & 0x01] * 2.5, sampleRate);
            opout[5] = opout[1] + opout[2];
            opout[3] = clock_fm_operator(channel, 2, baseFrequency, opout[(algorithmOps >> 1) & 0x07] * 2.5, sampleRate);
            opout[6] = opout[1] + opout[3];
            opout[7] = opout[2] + opout[3];
            double op3 = clock_fm_operator(channel, 3, baseFrequency, opout[(algorithmOps >> 4) & 0x07] * 2.5, sampleRate);

            m_fm_last[channel, 0] = op0;
            m_fm_last[channel, 1] = opout[2];
            m_fm_last[channel, 2] = opout[3];
            m_fm_last[channel, 3] = op3;

            double result = op3;
            int carriers = 1;
            if ((algorithmOps & (1 << 7)) != 0)
            {
                result += opout[1];
                carriers++;
            }
            if ((algorithmOps & (1 << 8)) != 0)
            {
                result += opout[2];
                carriers++;
            }
            if ((algorithmOps & (1 << 9)) != 0)
            {
                result += opout[3];
                carriers++;
            }

            return result / carriers;
        }

        double clock_fm_operator(int channel, int slot, double baseFrequency, double modulation, double sampleRate)
        {
            int slotOffset = OPN_OPERATOR_OFFSET[channel, slot];
            u8 dtMul = m_opn_regs[0x30 + slotOffset];
            u8 totalLevel = m_opn_regs[0x40 + slotOffset];
            u8 attack = m_opn_regs[0x50 + slotOffset];
            u8 decay = m_opn_regs[0x60 + slotOffset];
            u8 sustainRate = m_opn_regs[0x70 + slotOffset];
            u8 sustainLevel = m_opn_regs[0x80 + slotOffset];
            u8 release = m_opn_regs[0x80 + slotOffset];

            bool keyOn = (m_fm_key_mask[channel] & (1 << slot)) != 0;
            double sustain = 1.0 - (((sustainLevel >> 4) & 0x0f) / 15.0);
            double target = keyOn ? Math.Max(0.08, sustain) : 0.0;
            double speed = keyOn
                ? (0.00008 + (((attack >> 1) & 0x1f) / 31.0) * 0.018)
                : (0.00002 + ((release & 0x0f) / 15.0) * 0.006);

            if (keyOn && m_fm_env[channel, slot] < 0.96)
                target = 1.0;
            else if (keyOn && sustainRate != 0)
                speed = 0.00002 + (((decay >> 1) & 0x1f) / 31.0) * 0.002;

            m_fm_env[channel, slot] += (target - m_fm_env[channel, slot]) * speed;

            double multiple = dtMul & 0x0f;
            if (multiple == 0.0)
                multiple = 0.5;

            double detune = (((dtMul >> 4) & 0x07) - 3) * 0.0025;
            double step = (baseFrequency * multiple * (1.0 + detune)) / sampleRate;
            m_fm_phase[channel, slot] += step;
            m_fm_phase[channel, slot] -= Math.Floor(m_fm_phase[channel, slot]);

            double tl = 1.0 - Math.Min(127, totalLevel & 0x7f) / 127.0;
            double amplitude = m_fm_env[channel, slot] * tl * tl;
            return Math.Sin((m_fm_phase[channel, slot] + modulation) * TWO_PI) * amplitude;
        }

    }


    public static class ymopn_global
    {
        public static ym2203_device YM2203(machine_config mconfig, string tag, uint32_t clock) { return emu.detail.device_type_impl.op<ym2203_device>(mconfig, tag, ym2203_device.YM2203, clock); }
        public static ym2203_device YM2203<bool_Required>(machine_config mconfig, device_finder<ym2203_device, bool_Required> finder, uint32_t clock) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, ym2203_device.YM2203, clock); }
    }
}
