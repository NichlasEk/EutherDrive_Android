// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using devcb_write_line = mame.devcb_write<mame.Type_constant_s32, mame.devcb_value_const_unsigned_1<mame.Type_constant_s32>>;
using offs_t = System.UInt32;
using u8 = System.Byte;
using uint32_t = System.UInt32;

using static mame.device_global;
using static mame.diexec_global;
using static mame.emucore_global;


namespace mame
{
    // ======================> ym2203_device
    //
    // Minimal YM2203 implementation for drivers that rely on the SSG portion.
    // The three PSG channels are backed by the existing AY/YM SSG core and
    // participate in MAME's sound routing. OPN timer/status handling is
    // implemented so sound programs that use the YM2203 IRQ line for pacing can
    // run. FM writes are currently approximated by steering key-on/frequency
    // state into the PSG channels; this keeps games audible until ymfm_opn is
    // ported.
    public class ym2203_device : ay8910_device
    {
        public static readonly emu.detail.device_type_impl YM2203 = DEFINE_DEVICE_TYPE("ym2203", "YM2203 OPN", (type, mconfig, tag, owner, clock) => { return new ym2203_device(mconfig, tag, owner, clock); });

        const u8 STATUS_TIMERA = 0x01;
        const u8 STATUS_TIMERB = 0x02;
        const int OPN_OPERATORS = 12;
        const int OPN_DEFAULT_PRESCALE = 6;

        readonly bool m_trace;
        readonly devcb_write_line m_irq_handler;
        readonly u8 [] m_opn_regs = new u8[0x100];
        readonly bool [] m_fm_key_on = new bool[3];
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
            Array.Clear(m_fm_key_on, 0, m_fm_key_on.Length);
            m_address = 0;
            m_status = 0;
            if (m_timer_a != null)
                m_timer_a.enable(false);
            if (m_timer_b != null)
                m_timer_b.enable(false);
            update_irq();
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
            default:
                if (is_fm_channel_register(m_address))
                    update_fm_surrogate(m_address & 0x03);
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
            if (channel >= 3)
                return;

            m_fm_key_on[channel] = (data & 0xf0) != 0;
            update_fm_surrogate(channel);
        }

        static bool is_fm_channel_register(u8 reg)
        {
            int channel = reg & 0x03;
            if (channel >= 3)
                return false;

            return (reg >= 0x40 && reg <= 0x9e)
                || (reg >= 0xa0 && reg <= 0xa6)
                || (reg >= 0xb0 && reg <= 0xb6);
        }

        void update_fm_surrogate(int channel)
        {
            if (channel < 0 || channel >= 3)
                return;

            int period = fm_surrogate_period(channel);
            int volume = m_fm_key_on[channel] ? 1 : 0;

            write_ssg_register((u8)(channel * 2), (u8)(period & 0xff));
            write_ssg_register((u8)(channel * 2 + 1), (u8)((period >> 8) & 0x0f));
            write_ssg_register((u8)(0x08 + channel), (u8)volume);
        }

        int fm_surrogate_period(int channel)
        {
            int fnum = m_opn_regs[0xa0 + channel] | ((m_opn_regs[0xa4 + channel] & 0x07) << 8);
            int block = (m_opn_regs[0xa4 + channel] >> 3) & 0x07;
            if (fnum <= 0)
                return 0x0fff;

            int scaled = fnum << Math.Max(0, block - 1);
            if (scaled <= 0)
                return 0x0fff;

            return Math.Clamp(0x180000 / scaled, 1, 0x0fff);
        }

        void write_ssg_register(u8 reg, u8 data)
        {
            base.address_w(reg);
            base.data_w(data);
        }
    }


    public static class ymopn_global
    {
        public static ym2203_device YM2203(machine_config mconfig, string tag, uint32_t clock) { return emu.detail.device_type_impl.op<ym2203_device>(mconfig, tag, ym2203_device.YM2203, clock); }
        public static ym2203_device YM2203<bool_Required>(machine_config mconfig, device_finder<ym2203_device, bool_Required> finder, uint32_t clock) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, ym2203_device.YM2203, clock); }
    }
}
