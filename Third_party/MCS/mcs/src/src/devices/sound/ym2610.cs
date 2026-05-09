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
    // Register-compatible YM2610 shell for Neo Geo bring-up.  The FM/SSG/ADPCM
    // renderer is intentionally still silent; this preserves the MAME-facing bus,
    // IRQ, and save-state surface while the OPNB core is filled in chip by chip.
    public class ym2610_device : device_t
    {
        public static readonly emu.detail.device_type_impl YM2610 = DEFINE_DEVICE_TYPE("ym2610", "YM2610 OPNB", (type, mconfig, tag, owner, clock) => { return new ym2610_device(mconfig, tag, owner, clock); });

        const u8 STATUS_TIMER_A = 0x01;
        const u8 STATUS_TIMER_B = 0x02;
        const u8 STATUS_BUSY = 0x80;
        const int OUTPUTS = 3;

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
        sound_stream m_stream;
        emu_timer m_timer_a;
        emu_timer m_timer_b;
        attotime m_busy_end;
        u8 m_status;


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
            save_item(NAME(new { m_busy_end }));
            save_item(NAME(new { m_timer_running }));
        }


        protected override void device_reset()
        {
            Array.Clear(m_regs, 0, m_regs.Length);
            Array.Clear(m_address, 0, m_address.Length);
            m_status = 0;
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
        }


        u8 read_status()
        {
            u8 result = m_status;
            if (machine().time() < m_busy_end)
                result |= STATUS_BUSY;
            return result;
        }


        u8 read_status_hi()
        {
            return 0;
        }


        u8 read_data(int port)
        {
            u32 index = reg_index(port);
            return index < m_regs.Length ? m_regs[index] : (u8)0xff;
        }


        void write_data(int port, u8 data)
        {
            u32 index = reg_index(port);
            if (index < m_regs.Length)
                m_regs[index] = data;

            if (port == 0)
            {
                switch (m_address[0])
                {
                case 0x24:
                case 0x25:
                case 0x26:
                    reload_timer_register(m_address[0]);
                    break;
                case 0x27:
                    mode_w(data);
                    break;
                }
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
