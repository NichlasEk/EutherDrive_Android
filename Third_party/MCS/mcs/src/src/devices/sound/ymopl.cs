// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using offs_t = System.UInt32;
using stream_buffer_sample_t = System.Single;
using u8 = System.Byte;
using uint32_t = System.UInt32;

using static mame.device_global;
using static mame.diexec_global;
using static mame.emucore_global;
using static mame.ymopl_global;

namespace mame
{
    public class ym3812_device : device_t
    {
        public class device_sound_interface_ym3812 : device_sound_interface
        {
            public device_sound_interface_ym3812(machine_config mconfig, device_t device) : base(mconfig, device) { }

            public override void sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
            {
                ((ym3812_device)device()).device_sound_interface_sound_stream_update(stream, inputs, outputs);
            }
        }

        public static readonly emu.detail.device_type_impl YM3812 =
            DEFINE_DEVICE_TYPE("ym3812", "YM3812 OPL2", (type, mconfig, tag, owner, clock) => new ym3812_device(mconfig, tag, owner, clock));

        const u8 STATUS_TIMER1 = 0x40;
        const u8 STATUS_TIMER2 = 0x20;
        const u8 STATUS_IRQ = 0x80;
        const int ChannelCount = 9;
        const int OperatorCount = 18;
        const double TwoPi = Math.PI * 2.0;
        const double OutputGain = 0.18;

        static readonly int [] OperatorMap =
        {
            0, 1, 2, 8, 9, 10, 16, 17, 18,
            3, 4, 5, 11, 12, 13, 19, 20, 21
        };

        static readonly int [] ChannelBase =
        {
            0, 1, 2, 8, 9, 10, 16, 17, 18
        };

        readonly device_sound_interface_ym3812 m_disound;
        readonly u8 [] m_regs = new u8[0x100];
        readonly double [] m_phase = new double[OperatorCount];
        readonly double [] m_env = new double[OperatorCount];
        readonly bool [] m_key = new bool[ChannelCount];
        sound_stream m_stream;
        Action<int> m_irq_handler;
        u8 m_address;
        u8 m_status;

        ym3812_device(machine_config mconfig, string tag, device_t owner, uint32_t clock)
            : base(mconfig, YM3812, tag, owner, clock)
        {
            m_class_interfaces.Add(new device_sound_interface_ym3812(mconfig, this));
            m_disound = GetClassInterface<device_sound_interface_ym3812>();
        }

        public device_sound_interface_ym3812 disound => m_disound;

        public void set_irq_handler(Action<int> handler) => m_irq_handler = handler;

        public u8 read(offs_t offset) => (offset & 1) == 0 ? status_r() : (u8)0x00;

        public void write(offs_t offset, u8 data)
        {
            if ((offset & 1) == 0)
                address_w(data);
            else
                data_w(data);
        }

        public u8 status_r() => m_status;

        public void address_w(u8 data)
        {
            m_address = data;
        }

        public void data_w(u8 data)
        {
            m_stream?.update();
            u8 reg = m_address;
            m_regs[reg] = data;

            if (reg == 0x04)
            {
                if ((data & 0x80) != 0)
                    ClearStatus((u8)(STATUS_TIMER1 | STATUS_TIMER2));
                return;
            }

            if (reg >= 0xb0 && reg <= 0xb8)
            {
                int ch = reg - 0xb0;
                bool key = (data & 0x20) != 0;
                if (key != m_key[ch])
                {
                    m_key[ch] = key;
                    KeyChannel(ch, key);
                }
            }
        }

        public void frame_irq()
        {
            if ((m_regs[0x04] & 0x40) != 0)
                return;

            SetStatus(STATUS_TIMER1);
        }

        protected override void device_start()
        {
            m_stream = m_disound.stream_alloc(0, 1, Math.Max(1U, clock() / 72));
            save_item(NAME(new { m_regs }));
            save_item(NAME(new { m_phase }));
            save_item(NAME(new { m_env }));
            save_item(NAME(new { m_key }));
            SaveStateRef(nameof(m_address), () => m_address, value => m_address = value);
            SaveStateRef(nameof(m_status), () => m_status, value => m_status = value);
            machine().save().register_postload(SyncIrqLine);
        }

        protected override void device_reset()
        {
            Array.Clear(m_regs, 0, m_regs.Length);
            Array.Clear(m_phase, 0, m_phase.Length);
            Array.Clear(m_env, 0, m_env.Length);
            Array.Clear(m_key, 0, m_key.Length);
            m_address = 0;
            m_status = 0;
            m_irq_handler?.Invoke(CLEAR_LINE);
        }

        void device_sound_interface_sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
        {
            write_stream_view output = outputs[0];
            double sampleRate = Math.Max(1.0, output.sample_rate());

            for (int i = 0; i < output.samples(); i++)
            {
                double sample = 0.0;
                for (int ch = 0; ch < ChannelCount; ch++)
                    sample += RenderChannel(ch, sampleRate);

                output.put(i, (stream_buffer_sample_t)Math.Clamp(sample * OutputGain, -1.0, 1.0));
            }
        }

        double RenderChannel(int ch, double sampleRate)
        {
            int carrier = ch + 9;
            int modulator = ch;
            StepEnvelope(modulator);
            StepEnvelope(carrier);

            if (m_env[carrier] <= 0.0001)
                return 0.0;

            double freq = ChannelFrequency(ch);
            if (freq <= 0.0)
                return 0.0;

            double mod = OperatorSample(modulator, freq, sampleRate, 0.0);
            double feedback = ((m_regs[0xc0 + ch] >> 1) & 0x07) / 7.0;
            return OperatorSample(carrier, freq, sampleRate, mod * feedback * 6.0);
        }

        double OperatorSample(int op, double baseFreq, double sampleRate, double phaseMod)
        {
            int slot = OperatorMap[op];
            int multiple = m_regs[0x20 + slot] & 0x0f;
            if (multiple == 0)
                multiple = 1;

            double freq = baseFreq * multiple;
            m_phase[op] += freq / sampleRate;
            m_phase[op] -= Math.Floor(m_phase[op]);
            double volume = OperatorVolume(op);
            return Math.Sin(m_phase[op] * TwoPi + phaseMod) * m_env[op] * volume;
        }

        double ChannelFrequency(int ch)
        {
            int fnum = m_regs[0xa0 + ch] | ((m_regs[0xb0 + ch] & 0x03) << 8);
            int block = (m_regs[0xb0 + ch] >> 2) & 0x07;
            if (fnum == 0)
                return 0.0;

            return fnum * Math.Pow(2.0, block - 20) * clock() / 72.0;
        }

        double OperatorVolume(int op)
        {
            int slot = OperatorMap[op];
            int totalLevel = m_regs[0x40 + slot] & 0x3f;
            return Math.Pow(10.0, -totalLevel / 20.0);
        }

        void StepEnvelope(int op)
        {
            int slot = OperatorMap[op];
            if (IsOperatorKeyed(op))
            {
                int attack = m_regs[0x60 + slot] >> 4;
                double step = 0.0002 + attack * 0.00022;
                m_env[op] = Math.Min(1.0, m_env[op] + step);
                return;
            }

            int release = m_regs[0x80 + slot] & 0x0f;
            double stepRelease = 0.00005 + release * 0.00009;
            m_env[op] = Math.Max(0.0, m_env[op] - stepRelease);
        }

        bool IsOperatorKeyed(int op)
        {
            int ch = op % 9;
            return m_key[ch];
        }

        void KeyChannel(int ch, bool key)
        {
            if (key)
            {
                m_phase[ch] = 0;
                m_phase[ch + 9] = 0;
            }
        }

        void SetStatus(u8 bits)
        {
            m_status |= bits;
            if ((m_status & (STATUS_TIMER1 | STATUS_TIMER2)) != 0)
                m_status |= STATUS_IRQ;
            m_irq_handler?.Invoke((m_status & STATUS_IRQ) != 0 ? ASSERT_LINE : CLEAR_LINE);
        }

        void ClearStatus(u8 bits)
        {
            m_status = (u8)(m_status & ~bits);
            if ((m_status & (STATUS_TIMER1 | STATUS_TIMER2)) == 0)
                m_status = (u8)(m_status & ~STATUS_IRQ);
            m_irq_handler?.Invoke((m_status & STATUS_IRQ) != 0 ? ASSERT_LINE : CLEAR_LINE);
        }

        void SaveStateRef<T>(string itemName, Func<T> getter, Action<T> setter)
        {
            machine().save().save_item_ref(this, name(), tag(), 0, itemName, getter, setter);
        }

        void SyncIrqLine()
        {
            m_irq_handler?.Invoke((m_status & STATUS_IRQ) != 0 ? ASSERT_LINE : CLEAR_LINE);
        }
    }

    public static class ymopl_global
    {
        public static ym3812_device YM3812<bool_Required>(machine_config mconfig, device_finder<ym3812_device, bool_Required> finder, XTAL clock)
            where bool_Required : bool_const, new()
        {
            return emu.detail.device_type_impl.op(mconfig, finder, ym3812_device.YM3812, clock);
        }
    }
}
