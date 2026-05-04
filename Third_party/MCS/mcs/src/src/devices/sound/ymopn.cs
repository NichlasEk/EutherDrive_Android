// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using offs_t = System.UInt32;
using u8 = System.Byte;
using uint8_t = System.Byte;
using uint32_t = System.UInt32;

using static mame.device_global;


namespace mame
{
    // ======================> ym2203_device (STUB - no YM2203 OPN core ported yet)
    public class ym2203_device : device_t
    {
        public static readonly emu.detail.device_type_impl YM2203 = DEFINE_DEVICE_TYPE("ym2203", "YM2203 OPN (stub)", (type, mconfig, tag, owner, clock) => { return new ym2203_device(mconfig, tag, owner, clock); });

        ym2203_device(machine_config mconfig, string tag, device_t owner, uint32_t clock)
            : base(mconfig, YM2203, tag, owner, clock)
        {
        }

        protected override void device_start()
        {
        }

        protected override void device_reset()
        {
        }

        public void write(offs_t offset, u8 data)
        {
        }

        public u8 read(offs_t offset)
        {
            return 0;
        }

        // route helpers
        public void add_route(int index, string tag, float gain)
        {
        }
    }


    public static class ymopn_global
    {
        public static ym2203_device YM2203(machine_config mconfig, string tag, uint32_t clock) { return emu.detail.device_type_impl.op<ym2203_device>(mconfig, tag, ym2203_device.YM2203, clock); }
        public static ym2203_device YM2203<bool_Required>(machine_config mconfig, device_finder<ym2203_device, bool_Required> finder, uint32_t clock) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, ym2203_device.YM2203, clock); }
    }
}
