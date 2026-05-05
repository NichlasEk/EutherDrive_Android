// license:BSD-3-Clause
// copyright-holders:David Haywood, Vas Crabb
// Ported from MAME shared/taito68705.cpp

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using u8 = System.Byte;
using u32 = System.UInt32;

using static mame.device_global;
using static mame.diexec_global;
using static mame.emucore_global;
using static mame.m68705_global;
using static mame.util;


namespace mame
{
    public class taito68705_mcu_device : device_t
    {
        public static readonly emu.detail.device_type_impl TAITO68705_MCU = DEFINE_DEVICE_TYPE("taito68705", "Taito MC68705 MCU Interface", (type, mconfig, tag, owner, clock) => { return new taito68705_mcu_device(mconfig, tag, owner, clock); });


        required_device<m68705p_device> m_mcu;

        bool m_latch_driven;
        bool m_reset_input;
        bool m_host_flag;
        bool m_mcu_flag;
        u8 m_host_latch;
        u8 m_mcu_latch;
        u8 m_pa_output;
        u8 m_pb_output;
        bool m_trace_status;
        int m_trace_count;
        u8 m_trace_last_portc;


        taito68705_mcu_device(machine_config mconfig, string tag, device_t owner, u32 clock)
            : base(mconfig, TAITO68705_MCU, tag, owner, clock)
        {
            m_mcu = new required_device<m68705p_device>(this, "mcu");

            m_latch_driven = false;
            m_reset_input = false;
            m_host_flag = false;
            m_mcu_flag = false;
            m_host_latch = 0xff;
            m_mcu_latch = 0xff;
            m_pa_output = 0xff;
            m_pb_output = 0xff;
            m_trace_status = Environment.GetEnvironmentVariable("EUTHERDRIVE_XAIN_STATUS") == "1";
            m_trace_count = 0;
            m_trace_last_portc = 0;
        }


        public u8 data_r()
        {
            u8 result = m_mcu_latch;

            if (!machine().side_effects_disabled())
                m_mcu_flag = false;

            trace($"data_r result=0x{result:X2} host={m_host_flag} mcu={m_mcu_flag}");

            return result;
        }


        public void data_w(u8 data)
        {
            if (!m_reset_input)
                m_host_flag = true;

            m_host_latch = data;
            if (m_latch_driven)
                m_mcu.op0.pa_w(data);
            m_mcu.op0.set_input_line(M68705_IRQ_LINE, m_host_flag ? ASSERT_LINE : CLEAR_LINE);

            trace($"data_w data=0x{data:X2} latch_driven={m_latch_driven} host={m_host_flag} irq={(m_host_flag ? ASSERT_LINE : CLEAR_LINE)}");
        }


        public void reset_w(int state)
        {
            m_reset_input = ASSERT_LINE == state;

            if (CLEAR_LINE != state)
            {
                m_host_flag = false;
                m_mcu_flag = false;
                m_mcu.op0.set_input_line(M68705_IRQ_LINE, CLEAR_LINE);
            }

            m_mcu.op0.set_input_line(INPUT_LINE_RESET, state);

            trace($"reset_w state={state} reset_input={m_reset_input} host={m_host_flag} mcu={m_mcu_flag}");
        }


        public int host_semaphore_r() { return m_host_flag ? 1 : 0; }
        public int mcu_semaphore_r() { return m_mcu_flag ? 1 : 0; }


        protected override void device_add_mconfig(machine_config config)
        {
            M68705P5(config, m_mcu, DERIVED_CLOCK(1, 1));
            m_mcu.op0.portc_r().set(mcu_portc_r).reg();
            m_mcu.op0.porta_w().set(mcu_pa_w).reg();
            m_mcu.op0.portb_w().set(mcu_portb_w).reg();
        }


        protected override void device_start()
        {
            save_item(NAME(new { m_latch_driven }));
            save_item(NAME(new { m_reset_input }));
            save_item(NAME(new { m_host_flag }));
            save_item(NAME(new { m_mcu_flag }));
            save_item(NAME(new { m_host_latch }));
            save_item(NAME(new { m_mcu_latch }));
            save_item(NAME(new { m_pa_output }));
            save_item(NAME(new { m_pb_output }));

            m_latch_driven = false;
            m_reset_input = false;
            m_host_latch = 0xff;
            m_mcu_latch = 0xff;
            m_pa_output = 0xff;
            m_pb_output = 0xff;
        }


        protected override void device_reset()
        {
            m_host_flag = false;
            m_mcu_flag = false;
            m_mcu.op0.set_input_line(M68705_IRQ_LINE, CLEAR_LINE);
        }


        u8 mcu_pa_r()
        {
            return m_latch_driven ? m_host_latch : (u8)0xff;
        }


        void mcu_pa_w(u8 data)
        {
            m_pa_output = data;
            trace($"mcu_pa_w pc=0x{m_mcu.op0.debug_pc():X4} data=0x{data:X2} pa_value=0x{pa_value():X2}");
        }


        u8 mcu_portc_r()
        {
            u8 result = (u8)((m_host_flag ? 0x01 : 0x00) | (m_mcu_flag ? 0x00 : 0x02) | 0xfc);
            if (result != m_trace_last_portc)
            {
                trace($"mcu_portc_r pc=0x{m_mcu.op0.debug_pc():X4} result=0x{result:X2} host={m_host_flag} mcu={m_mcu_flag}");
                m_trace_last_portc = result;
            }
            return result;
        }


        void mcu_portb_w(u8 data)
        {
            trace($"mcu_portb_w pc=0x{m_mcu.op0.debug_pc():X4} data=0x{data:X2} old=0x{m_pb_output:X2} pa=0x{pa_value():X2}");
            latch_control(data, 1, 2);
        }


        u8 pa_value()
        {
            return (u8)(m_pa_output & (m_latch_driven ? m_host_latch : 0xff));
        }


        void latch_control(u8 data, int host_bit, int mcu_bit)
        {
            u8 old_pa_value = pa_value();

            if (BIT(data, host_bit) != 0)
            {
                m_latch_driven = false;
                m_mcu.op0.pa_w(0xff);

                if (BIT(m_pb_output, host_bit) == 0)
                {
                    m_host_flag = false;
                    m_mcu.op0.set_input_line(M68705_IRQ_LINE, CLEAR_LINE);
                }
            }
            else
            {
                m_latch_driven = true;
                m_mcu.op0.pa_w(m_host_latch);
            }

            if (BIT(data, mcu_bit) == 0)
            {
                if (!m_reset_input)
                    m_mcu_flag = true;

                if (BIT(m_pb_output, mcu_bit) != 0)
                    m_mcu_latch = old_pa_value;
            }

            trace($"latch_control pc=0x{m_mcu.op0.debug_pc():X4} data=0x{data:X2} oldpb=0x{m_pb_output:X2} host={m_host_flag} mcu={m_mcu_flag} host_latch=0x{m_host_latch:X2} mcu_latch=0x{m_mcu_latch:X2} latch_driven={m_latch_driven}");
            m_pb_output = data;
        }


        void trace(string message)
        {
            if (!m_trace_status || m_trace_count >= 512)
                return;

            Console.Error.WriteLine($"[TAITO68705] {message}");
            m_trace_count++;
        }
    }


    public static class taito68705_global
    {
        public static taito68705_mcu_device TAITO68705_MCU<bool_Required>(machine_config mconfig, device_finder<taito68705_mcu_device, bool_Required> finder, XTAL clock) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, taito68705_mcu_device.TAITO68705_MCU, clock); }
    }
}
