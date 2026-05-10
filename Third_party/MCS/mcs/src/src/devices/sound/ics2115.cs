// license:BSD-3-Clause
// copyright-holders:Alex Marshall,nimitz,austere,Edward Fast
// Ported from MAME src/devices/sound/ics2115.cpp

using System;

using devcb_write_line = mame.devcb_write<mame.Type_constant_s32, mame.devcb_value_const_unsigned_1<mame.Type_constant_s32>>;
using device_type = mame.emu.detail.device_type_impl_base;
using MemoryU8 = mame.MemoryContainer<System.Byte>;
using offs_t = System.UInt32;
using s16 = System.Int16;
using s32 = System.Int32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using u64 = System.UInt64;

using static mame.device_global;
using static mame.diexec_global;
using static mame.emucore_global;
using static mame.disound_global;

namespace mame
{
    public class ics2115_device : device_t
    {
        public static readonly emu.detail.device_type_impl ICS2115 =
            DEFINE_DEVICE_TYPE("ics2115", "ICS2115 WaveFront Synthesizer", (type, mconfig, tag, owner, clock) => new ics2115_device(mconfig, tag, owner, clock));

        const u16 Revision = 0x0001;
        const int Voices = 32;
        const int VolumeBits = 15;
        const int RampShift = 6;
        static readonly bool TracePgmSound = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM_SOUND_TRACE") == "1";

        public class device_sound_interface_ics2115 : device_sound_interface
        {
            public device_sound_interface_ics2115(machine_config mconfig, device_t device) : base(mconfig, device) { }

            public override void sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
            {
                ((ics2115_device)device()).device_sound_interface_sound_stream_update(stream, inputs, outputs);
            }
        }

        sealed class IcsVoice
        {
            public s32 OscLeft;
            public u32 OscAcc;
            public u32 OscStart;
            public u32 OscEnd;
            public u16 OscFc;
            public u8 OscCtl;
            public u8 OscSAddr;
            public s32 VolLeft;
            public u32 VolAdd;
            public u32 VolStart;
            public u32 VolEnd;
            public u32 VolAcc;
            public u16 VolRegAcc;
            public u8 VolIncr;
            public u8 VolPan;
            public u8 VolMode;
            public u8 OscConf;
            public u8 VolCtrl;
            public bool StateOn;
            public int StateRamp;
            public readonly u16 [] Regs = new u16[0x20];

            public bool Playing => StateOn && !GetBit(OscConf, 1);

            public void Reset()
            {
                Array.Clear(Regs, 0, Regs.Length);
                OscConf = 0x02;
                OscLeft = 0;
                OscAcc = 0;
                OscStart = 0;
                OscEnd = 0;
                OscFc = 0;
                OscCtl = 0;
                OscSAddr = 0;
                VolLeft = 0;
                VolAdd = 0;
                VolStart = 0;
                VolEnd = 0;
                VolAcc = 0;
                VolRegAcc = 0;
                VolIncr = 0;
                VolPan = 0x7f;
                VolMode = 0;
                VolCtrl = 0x01;
                StateOn = false;
                StateRamp = 0;
            }

            public void UpdateRamp()
            {
                if (StateOn && !GetBit(OscConf, 1))
                {
                    if (StateRamp < 0x40)
                        StateRamp++;
                    else
                        StateRamp = 0x40;
                }
                else if (StateRamp != 0)
                {
                    StateRamp--;
                }
            }

            public bool UpdateOscillator()
            {
                if (GetBit(OscConf, 1))
                    return false;

                if (GetBit(OscConf, 6))
                {
                    OscAcc -= (u32)(OscFc << 2);
                    OscLeft = unchecked((s32)(OscAcc - OscStart));
                }
                else
                {
                    OscAcc += (u32)(OscFc << 2);
                    OscLeft = unchecked((s32)(OscEnd - OscAcc));
                }

                if (OscLeft > 0)
                    return false;

                bool irq = false;
                if (GetBit(OscConf, 5))
                {
                    OscConf = SetBit(OscConf, 7, true);
                    irq = true;
                }

                if (GetBit(OscConf, 3))
                {
                    if (GetBit(OscConf, 4))
                        OscConf = SetBit(OscConf, 6, !GetBit(OscConf, 6));

                    if (GetBit(OscConf, 6))
                    {
                        OscAcc = unchecked((u32)(OscEnd + OscLeft));
                        OscLeft = unchecked((s32)(OscAcc - OscStart));
                    }
                    else
                    {
                        OscAcc = unchecked((u32)(OscStart - OscLeft));
                        OscLeft = unchecked((s32)(OscEnd - OscAcc));
                    }
                }
                else
                {
                    StateOn = false;
                    OscConf = SetBit(OscConf, 1, true);
                    OscAcc = GetBit(OscConf, 6) ? OscStart : OscEnd;
                }

                return irq;
            }

            public bool UpdateVolumeEnvelope()
            {
                bool bc = VolAcc >= VolEnd || VolAcc <= VolEnd;
                if (GetBit(VolCtrl, 0) || GetBit(VolCtrl, 1))
                    return false;

                if (GetBit(VolCtrl, 6))
                {
                    VolAcc -= VolAdd;
                    VolLeft = unchecked((s32)(VolAcc - VolStart));
                }
                else
                {
                    VolAcc += VolAdd;
                    VolLeft = unchecked((s32)(VolEnd - VolAcc));
                }

                if (VolLeft > 0)
                    return false;

                bool irq = false;
                if (GetBit(VolCtrl, 5))
                {
                    VolCtrl = SetBit(VolCtrl, 7, true);
                    irq = true;
                }

                if (GetBit(OscConf, 2))
                    return irq;

                if (GetBit(VolCtrl, 3))
                {
                    if (bc)
                    {
                        if (!GetBit(VolCtrl, 4))
                        {
                            VolAcc = !GetBit(VolCtrl, 6)
                                ? unchecked((u32)(VolStart - (VolEnd - (VolAcc + VolIncr))))
                                : unchecked((u32)(VolEnd + ((VolAcc - VolIncr) - VolStart)));
                        }
                        else
                        {
                            VolAcc = !GetBit(VolCtrl, 6)
                                ? unchecked((u32)(VolEnd + (VolEnd - (VolAcc + VolIncr))))
                                : unchecked((u32)(VolStart - ((VolAcc - VolIncr) - VolStart)));
                        }
                    }
                }
                else
                {
                    VolCtrl = SetBit(VolCtrl, 0, true);
                }

                return irq;
            }

            public void RegisterSaveState(device_t owner, int index)
            {
                save_manager save = owner.machine().save();
                string module = owner.name();
                string tag = owner.tag();
                save.save_item(owner, module, tag, index, Regs, "m_voice.Regs");
                save.save_item_ref(owner, module, tag, index, "m_voice.OscLeft", () => OscLeft, value => OscLeft = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.OscAcc", () => OscAcc, value => OscAcc = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.OscStart", () => OscStart, value => OscStart = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.OscEnd", () => OscEnd, value => OscEnd = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.OscFc", () => OscFc, value => OscFc = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.OscCtl", () => OscCtl, value => OscCtl = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.OscSAddr", () => OscSAddr, value => OscSAddr = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolLeft", () => VolLeft, value => VolLeft = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolAdd", () => VolAdd, value => VolAdd = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolStart", () => VolStart, value => VolStart = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolEnd", () => VolEnd, value => VolEnd = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolAcc", () => VolAcc, value => VolAcc = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolRegAcc", () => VolRegAcc, value => VolRegAcc = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolIncr", () => VolIncr, value => VolIncr = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolPan", () => VolPan, value => VolPan = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolMode", () => VolMode, value => VolMode = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.OscConf", () => OscConf, value => OscConf = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.VolCtrl", () => VolCtrl, value => VolCtrl = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.StateOn", () => StateOn, value => StateOn = value);
                save.save_item_ref(owner, module, tag, index, "m_voice.StateRamp", () => StateRamp, value => StateRamp = value);
            }
        }

        sealed class IcsTimer
        {
            public u8 Scale;
            public u8 Preset;
            public emu_timer Timer;
            public u64 Period;

            public void RegisterSaveState(device_t owner, int index)
            {
                save_manager save = owner.machine().save();
                string module = owner.name();
                string tag = owner.tag();
                save.save_item_ref(owner, module, tag, index, "m_timer.Scale", () => Scale, value => Scale = value);
                save.save_item_ref(owner, module, tag, index, "m_timer.Preset", () => Preset, value => Preset = value);
                save.save_item_ref(owner, module, tag, index, "m_timer.Period", () => Period, value => Period = value);
            }
        }

        readonly device_sound_interface_ics2115 m_disound;
        readonly devcb_write_line m_irq_cb;
        readonly s16 [] m_ulaw = new s16[256];
        readonly u16 [] m_volume = new u16[4096];
        readonly u16 [] m_panlaw = new u16[256];
        readonly IcsVoice [] m_voice = new IcsVoice[Voices];
        readonly IcsTimer [] m_timer = { new IcsTimer(), new IcsTimer() };
        readonly u16 [] m_regs = new u16[0x40];
        sound_stream m_stream;
        MemoryU8 m_rom;
        int m_romBytes;
        u8 m_active_osc;
        u8 m_osc_select;
        u8 m_reg_select;
        u8 m_irq_enabled;
        u8 m_irq_pending;
        bool m_irq_on;
        u8 m_vmode;
        int m_trace_writes;
        int m_trace_keyons;

        ics2115_device(machine_config mconfig, string tag, device_t owner, u32 clock)
            : base(mconfig, ICS2115, tag, owner, clock)
        {
            m_class_interfaces.Add(new device_sound_interface_ics2115(mconfig, this));
            m_disound = GetClassInterface<device_sound_interface_ics2115>();
            m_irq_cb = new devcb_write_line(this);
            for (int i = 0; i < m_voice.Length; i++)
                m_voice[i] = new IcsVoice();
        }

        public device_sound_interface_ics2115 disound => m_disound;
        public devcb_write_line.binder irq() => m_irq_cb.bind();

        protected override void device_start()
        {
            m_irq_cb.resolve_safe();
            memory_region region = memregion(DEVICE_SELF);
            if (region != null)
            {
                m_rom = region.base_();
                m_romBytes = (int)Math.Min(region.bytes(), int.MaxValue);
                Trace($"rom bytes={m_romBytes}");
            }

            m_timer[0].Timer = timer_alloc(timer_cb);
            m_timer[1].Timer = timer_alloc(timer_cb);
            m_stream = m_disound.stream_alloc(0, 2, Math.Max(1U, clock() / (32 * 32)));

            for (int i = 0; i < 4096; i++)
                m_volume[i] = (u16)(((0x100 | (i & 0xff)) << (VolumeBits - 9)) >> (15 - (i >> 8)));

            u16 [] lut = new u16[8];
            const u16 lutInitial = 33 << 2;
            for (int i = 0; i < 8; i++)
                lut[i] = (u16)((lutInitial << i) - lutInitial);

            const int panLevel = 16;
            for (int i = 0; i < 256; i++)
            {
                u8 exponent = (u8)((~i >> 4) & 0x07);
                u8 mantissa = (u8)(~i & 0x0f);
                s16 value = (s16)(lut[exponent] + (mantissa << (exponent + 3)));
                m_ulaw[i] = (i & 0x80) != 0 ? (s16)(-value) : value;
                m_panlaw[i] = (u16)(panLevel - Log2Floor(i));
            }
            m_panlaw[0] = 0x0fff;

            SaveStateRef(nameof(m_reg_select), () => m_reg_select, value => m_reg_select = value);
            SaveStateRef(nameof(m_osc_select), () => m_osc_select, value => m_osc_select = value);
            SaveStateRef(nameof(m_irq_enabled), () => m_irq_enabled, value => m_irq_enabled = value);
            SaveStateRef(nameof(m_irq_pending), () => m_irq_pending, value => m_irq_pending = value);
            SaveStateRef(nameof(m_irq_on), () => m_irq_on, value => m_irq_on = value);
            SaveStateRef(nameof(m_active_osc), () => m_active_osc, value => m_active_osc = value);
            SaveStateRef(nameof(m_vmode), () => m_vmode, value => m_vmode = value);
            save_item(NAME(new { m_regs }));
            for (int i = 0; i < m_voice.Length; i++)
                m_voice[i].RegisterSaveState(this, i);
            for (int i = 0; i < m_timer.Length; i++)
                m_timer[i].RegisterSaveState(this, i);
            machine().save().register_postload(PostLoadState);
        }

        void SaveStateRef<T>(string itemName, Func<T> getter, Action<T> setter)
        {
            machine().save().save_item_ref(this, name(), tag(), 0, itemName, getter, setter);
        }

        void PostLoadState()
        {
            if (m_stream != null)
                m_stream.set_sample_rate(Math.Max(1U, clock() / ((u32)(m_active_osc + 1) * 32)));

            for (int i = 0; i < m_timer.Length; i++)
            {
                u64 period = m_timer[i].Period;
                if (period == 0)
                {
                    m_timer[i].Timer?.adjust(attotime.never, i);
                    continue;
                }

                attotime tp = attotime.from_ticks(period, Math.Max(1U, clock()));
                m_timer[i].Timer?.adjust(tp, i, tp);
            }

            recalc_irq();
        }

        protected override void device_reset()
        {
            m_irq_enabled = 0;
            m_irq_pending = 0;
            m_active_osc = 31;
            if (m_stream != null)
                m_stream.set_sample_rate(Math.Max(1U, clock() / ((u32)(m_active_osc + 1) * 32)));
            m_osc_select = 0;
            m_reg_select = 0;
            m_vmode = 0;
            m_irq_on = false;
            m_trace_writes = 0;
            m_trace_keyons = 0;
            Array.Clear(m_regs, 0, m_regs.Length);

            foreach (IcsVoice voice in m_voice)
                voice.Reset();

            for (int i = 0; i < m_timer.Length; i++)
            {
                m_timer[i].Timer?.adjust(attotime.never, i);
                m_timer[i].Period = 0;
                m_timer[i].Scale = 0;
                m_timer[i].Preset = 0;
            }
        }

        protected override void device_clock_changed()
        {
            if (m_stream != null)
                m_stream.set_sample_rate(Math.Max(1U, clock() / ((u32)(m_active_osc + 1) * 32)));
        }

        public u8 read(offs_t offset)
        {
            u8 ret = 0;
            switch (offset & 0x03)
            {
            case 0:
                m_stream?.update();
                if (m_irq_on)
                {
                    ret |= 0x80;
                    if (m_irq_enabled != 0 && (m_irq_pending & 3) != 0)
                        ret |= 0x01;
                    for (int i = 0; i <= m_active_osc; i++)
                    {
                        if (GetBit(m_voice[i].OscConf, 7))
                        {
                            ret |= 0x02;
                            break;
                        }
                    }
                }
                break;
            case 1:
                ret = m_reg_select;
                break;
            case 2:
                ret = (u8)reg_read();
                break;
            case 3:
                ret = (u8)(reg_read() >> 8);
                break;
            }
            return ret;
        }

        public void write(offs_t offset, u8 data)
        {
            switch (offset & 0x03)
            {
            case 1:
                m_reg_select = data;
                break;
            case 2:
                TraceWrite(offset, data);
                reg_write(data, 0x00ff);
                break;
            case 3:
                TraceWrite(offset, data);
                reg_write((u16)(data << 8), 0xff00);
                break;
            }
        }

        public u16 word_r(offs_t offset, u16 mem_mask)
        {
            if (offset == 0 || offset == 1)
                return (mem_mask & 0x00ff) != 0 ? read(offset) : (u16)0;
            if (offset == 2)
                return (u16)(reg_read() & mem_mask);
            return 0;
        }

        public void word_w(offs_t offset, u16 data, u16 mem_mask)
        {
            if (offset == 0 || offset == 1)
            {
                if ((mem_mask & 0x00ff) != 0)
                    write(offset, (u8)data);
            }
            else if (offset == 2)
            {
                reg_write(data, mem_mask);
            }
        }

        u16 reg_read()
        {
            m_stream?.update();
            u16 ret = 0;
            IcsVoice voice = m_voice[m_osc_select];

            switch (m_reg_select)
            {
            case 0x00:
                ret = (u16)(voice.OscConf | (voice.StateOn ? 0x08 : 0x00));
                ret <<= 8;
                break;
            case 0x01: ret = voice.OscFc; break;
            case 0x02: ret = (u16)((voice.OscStart >> 16) & 0xffff); break;
            case 0x03: ret = (u16)(voice.OscStart & 0xff00); break;
            case 0x04: ret = (u16)((voice.OscEnd >> 16) & 0xffff); break;
            case 0x05: ret = (u16)(voice.OscEnd & 0xff00); break;
            case 0x06: ret = voice.VolIncr; break;
            case 0x07: ret = (u16)(voice.VolStart >> 18); break;
            case 0x08: ret = (u16)(voice.VolEnd >> 18); break;
            case 0x09: ret = (u16)(voice.VolAcc >> 10); break;
            case 0x0a: ret = (u16)((voice.OscAcc >> 16) & 0xffff); break;
            case 0x0b: ret = (u16)(voice.OscAcc & 0xfff8); break;
            case 0x0c: ret = (u16)(voice.VolPan << 8); break;
            case 0x0d:
                ret = !m_vmode.Equals(0) ? (u16)0x01 : (u16)(GetBit(voice.VolCtrl, 5) ? 0x81 : 0x01);
                ret <<= 8;
                break;
            case 0x0e:
                ret = m_active_osc;
                break;
            case 0x0f:
                ret = 0xff;
                for (int i = 0; i <= m_active_osc; i++)
                {
                    IcsVoice v = m_voice[i];
                    if (GetBit(v.OscConf, 7) || GetBit(v.VolCtrl, 7))
                    {
                        ret = (u16)(i | 0xe0);
                        if (GetBit(v.OscConf, 7))
                        {
                            v.OscConf = SetBit(v.OscConf, 7, false);
                            ret &= unchecked((u16)~0x80);
                        }
                        if (GetBit(v.VolCtrl, 7))
                        {
                            v.VolCtrl = SetBit(v.VolCtrl, 7, false);
                            ret &= unchecked((u16)~0x40);
                        }
                        recalc_irq();
                        break;
                    }
                }
                ret <<= 8;
                break;
            case 0x10: ret = (u16)(voice.OscCtl << 8); break;
            case 0x11: ret = (u16)(voice.OscSAddr << 8); break;
            case 0x40:
            case 0x41:
                ret = m_timer[m_reg_select & 1].Preset;
                m_irq_pending = (u8)(m_irq_pending & ~(1 << (m_reg_select & 1)));
                recalc_irq();
                break;
            case 0x43: ret = (u16)(m_irq_pending & 3); break;
            case 0x4a: ret = m_irq_pending; break;
            case 0x4b: ret = 0x80; break;
            case 0x4c: ret = Revision; break;
            default:
                ret = 0;
                break;
            }

            return ret;
        }

        void reg_write(u16 data, u16 mem_mask)
        {
            m_stream?.update();
            IcsVoice voice = m_voice[m_osc_select];
            if (m_reg_select < 0x20)
                voice.Regs[m_reg_select] = Combine(voice.Regs[m_reg_select], data, mem_mask);
            else if (m_reg_select >= 0x40 && m_reg_select < 0x80)
                m_regs[m_reg_select - 0x40] = Combine(m_regs[m_reg_select - 0x40], data, mem_mask);

            bool high = (mem_mask & 0xff00) != 0;
            bool low = (mem_mask & 0x00ff) != 0;
            switch (m_reg_select)
            {
            case 0x00:
                if (high)
                {
                    voice.OscConf = (u8)((voice.OscConf & 0x80) | ((data >> 8) & 0x7f));
                    recalc_irq();
                }
                break;
            case 0x01:
                if (high) voice.OscFc = (u16)((voice.OscFc & 0x00fe) | (data & 0xff00));
                if (low) voice.OscFc = (u16)((voice.OscFc & 0xff00) | (data & 0x00fe));
                break;
            case 0x02:
                if (high) voice.OscStart = (voice.OscStart & 0x00ffffff) | ((u32)(data & 0xff00) << 16);
                if (low) voice.OscStart = (voice.OscStart & 0xff00ffff) | ((u32)(data & 0x00ff) << 16);
                break;
            case 0x03:
                if (high) voice.OscStart = (voice.OscStart & 0xffff00ff) | (u32)(data & 0xff00);
                break;
            case 0x04:
                if (high) voice.OscEnd = (voice.OscEnd & 0x00ffffff) | ((u32)(data & 0xff00) << 16);
                if (low) voice.OscEnd = (voice.OscEnd & 0xff00ffff) | ((u32)(data & 0x00ff) << 16);
                break;
            case 0x05:
                if (high) voice.OscEnd = (voice.OscEnd & 0xffff00ff) | (u32)(data & 0xff00);
                break;
            case 0x06:
                if (high) voice.VolIncr = (u8)(data >> 8);
                break;
            case 0x07:
                if (low) voice.VolStart = (u32)(data & 0xff) << 18;
                break;
            case 0x08:
                if (low) voice.VolEnd = (u32)(data & 0xff) << 18;
                break;
            case 0x09:
                if (high) voice.VolRegAcc = (u16)((voice.VolRegAcc & 0x00ff) | (data & 0xff00));
                if (low) voice.VolRegAcc = (u16)((voice.VolRegAcc & 0xff00) | (data & 0x00ff));
                voice.VolAcc = (u32)voice.VolRegAcc << 10;
                break;
            case 0x0a:
                if (high) voice.OscAcc = (voice.OscAcc & 0x00ffffff) | ((u32)(data & 0xff00) << 16);
                if (low) voice.OscAcc = (voice.OscAcc & 0xff00ffff) | ((u32)(data & 0x00ff) << 16);
                break;
            case 0x0b:
                if (high) voice.OscAcc = (voice.OscAcc & 0xffff00ff) | (u32)(data & 0xff00);
                if (low) voice.OscAcc = (voice.OscAcc & 0xffffff00) | (u32)(data & 0x00f8);
                break;
            case 0x0c:
                if (high) voice.VolPan = (u8)(data >> 8);
                break;
            case 0x0d:
                if (high)
                {
                    voice.VolCtrl = (u8)((voice.VolCtrl & 0x80) | ((data >> 8) & 0x7f));
                    recalc_irq();
                }
                break;
            case 0x0e:
                if (high)
                {
                    m_active_osc = (u8)((data >> 8) & 0x1f);
                    m_stream?.set_sample_rate(Math.Max(1U, clock() / ((u32)(m_active_osc + 1) * 32)));
                    if (m_osc_select > m_active_osc)
                        m_osc_select = 0;
                }
                break;
            case 0x10:
                if (high)
                {
                    u8 ctl = (u8)(data >> 8);
                    voice.OscCtl = ctl;
                    voice.StateOn = ctl == 0;
                    if (ctl == 0)
                        keyon();
                    else if (ctl == 0x0f && m_vmode == 0)
                    {
                        voice.OscConf = SetBit(voice.OscConf, 1, true);
                        voice.VolCtrl = SetBit(voice.VolCtrl, 1, true);
                    }
                }
                break;
            case 0x11:
                if (high) voice.OscSAddr = (u8)(data >> 8);
                break;
            case 0x12:
                if (high) m_vmode = (u8)(data >> 8);
                break;
            case 0x40:
            case 0x41:
                if (low)
                {
                    m_timer[m_reg_select & 1].Preset = (u8)data;
                    recalc_timer(m_reg_select & 1);
                }
                break;
            case 0x42:
            case 0x43:
                if (low)
                {
                    m_timer[m_reg_select & 1].Scale = (u8)data;
                    recalc_timer(m_reg_select & 1);
                }
                break;
            case 0x4a:
                if (low)
                {
                    m_irq_enabled = (u8)data;
                    recalc_irq();
                }
                break;
            case 0x4f:
                if (low)
                    m_osc_select = (u8)(data % (1 + m_active_osc));
                break;
            }
        }

        void device_sound_interface_sound_stream_update(sound_stream stream, std.vector<read_stream_view> inputs, std.vector<write_stream_view> outputs)
        {
            outputs[0].fill(0);
            outputs[1].fill(0);

            bool irqInvalid = false;
            for (int osc = 0; osc <= m_active_osc; osc++)
            {
                if (fill_output(m_voice[osc], outputs[0], outputs[1]))
                    irqInvalid = true;
            }

            if (irqInvalid)
                recalc_irq();
        }

        bool fill_output(IcsVoice voice, write_stream_view leftOutput, write_stream_view rightOutput)
        {
            bool irqInvalid = false;
            int fineShift = 10 - (1 << (3 * (voice.VolIncr >> 6)));
            u32 baseAdd = (u32)(voice.VolIncr & 0x3f);
            voice.VolAdd = fineShift >= 0 ? baseAdd << fineShift : baseAdd >> -fineShift;

            for (int i = 0; i < leftOutput.samples(); i++)
            {
                u32 volacc = (voice.VolAcc >> 14) & 0x0fff;
                int vlefti = (int)volacc - m_panlaw[255 - voice.VolPan];
                int vrighti = (int)volacc - m_panlaw[voice.VolPan];
                int vleft = vlefti > 0 ? (m_volume[Math.Min(vlefti, 4095)] * voice.StateRamp >> RampShift) : 0;
                int vright = vrighti > 0 ? (m_volume[Math.Min(vrighti, 4095)] * voice.StateRamp >> RampShift) : 0;
                s32 sample = get_sample(voice);

                if (m_vmode == 0 || voice.Playing)
                {
                    leftOutput.add_int(i, (sample * vleft) >> (5 + VolumeBits), 32768);
                    rightOutput.add_int(i, (sample * vright) >> (5 + VolumeBits), 32768);
                }

                voice.UpdateRamp();
                if (voice.Playing)
                {
                    if (voice.UpdateOscillator())
                        irqInvalid = true;
                    if (voice.UpdateVolumeEnvelope())
                        irqInvalid = true;
                }
            }

            return irqInvalid;
        }

        s32 get_sample(IcsVoice voice)
        {
            u32 curaddr = voice.OscAcc >> 12;
            u32 nextaddr = voice.StateOn && GetBit(voice.OscConf, 3) && !GetBit(voice.OscConf, 4) && voice.OscLeft < (voice.OscFc << 2)
                ? voice.OscStart >> 12
                : curaddr + 2;

            s16 sample1;
            s16 sample2;
            if (GetBit(voice.OscConf, 0))
            {
                sample1 = m_ulaw[read_sample(voice, curaddr)];
                sample2 = m_ulaw[read_sample(voice, curaddr + 1)];
            }
            else if (GetBit(voice.OscConf, 2))
            {
                sample1 = (s16)(unchecked((sbyte)read_sample(voice, curaddr)) << 8);
                sample2 = (s16)(unchecked((sbyte)read_sample(voice, curaddr + 1)) << 8);
            }
            else
            {
                sample1 = (s16)(read_sample(voice, curaddr) | (unchecked((sbyte)read_sample(voice, curaddr + 1)) << 8));
                sample2 = (s16)(read_sample(voice, nextaddr) | (unchecked((sbyte)read_sample(voice, nextaddr + 1)) << 8));
            }

            s32 diff = sample2 - sample1;
            s32 fract = (s32)((voice.OscAcc & 0x0ff8) >> 3);
            return ((sample1 << 9) + diff * fract) >> 9;
        }

        u8 read_sample(IcsVoice voice, u32 addr)
        {
            if (m_rom == null || m_romBytes <= 0)
                return 0xff;

            u32 full = ((u32)voice.OscSAddr << 20) | (addr & 0x0fffff);
            return m_rom[(int)(full % (u32)m_romBytes)];
        }

        void keyon()
        {
            m_voice[m_osc_select].StateRamp = 0x40;
            if (TracePgmSound && m_trace_keyons < 32)
            {
                IcsVoice voice = m_voice[m_osc_select];
                m_trace_keyons++;
                Console.Error.WriteLine(
                    $"[ICS2115] keyon voice={m_osc_select} fc=0x{voice.OscFc:x4} " +
                    $"acc=0x{voice.OscAcc >> 12:x6} start=0x{voice.OscStart >> 12:x6} end=0x{voice.OscEnd >> 12:x6} " +
                    $"saddr=0x{voice.OscSAddr:x2} vol=0x{voice.VolAcc >> 10:x4} pan=0x{voice.VolPan:x2}");
            }
        }

        void recalc_irq()
        {
            bool irq = (m_irq_pending & m_irq_enabled) != 0;
            for (int i = 0; !irq && i < m_voice.Length; i++)
            {
                irq |= GetBit(m_voice[i].OscConf, 5) && GetBit(m_voice[i].OscConf, 7);
                irq |= GetBit(m_voice[i].VolCtrl, 5) && GetBit(m_voice[i].VolCtrl, 7);
            }
            m_irq_on = irq;
            m_irq_cb.op_s32(irq ? ASSERT_LINE : CLEAR_LINE);
        }

        void timer_cb(s32 param)
        {
            int bit = param & 1;
            if ((m_irq_pending & (1 << bit)) == 0)
            {
                m_irq_pending = (u8)(m_irq_pending | (1 << bit));
                recalc_irq();
            }
        }

        void recalc_timer(int timer)
        {
            u64 period = (u64)(((m_timer[timer].Scale & 0x1f) + 1) * (m_timer[timer].Preset + 1));
            period <<= 4 + (m_timer[timer].Scale >> 5);
            if (m_timer[timer].Period == period)
                return;

            m_timer[timer].Period = period;
            attotime tp = attotime.from_ticks(period, Math.Max(1U, clock()));
            m_timer[timer].Timer?.adjust(tp, timer, tp);
        }

        static u16 Combine(u16 oldValue, u16 data, u16 memMask)
        {
            return (u16)((oldValue & ~memMask) | (data & memMask));
        }

        static bool GetBit(u8 value, int bit) => ((value >> bit) & 1) != 0;

        static u8 SetBit(u8 value, int bit, bool state)
        {
            return state ? (u8)(value | (1 << bit)) : (u8)(value & ~(1 << bit));
        }

        static int Log2Floor(int value)
        {
            if (value <= 0)
                return 0;

            int result = 0;
            while ((value >>= 1) != 0)
                result++;
            return result;
        }

        void TraceWrite(offs_t offset, u8 data)
        {
            if (!TracePgmSound || m_trace_writes >= 128)
                return;

            m_trace_writes++;
            Console.Error.WriteLine($"[ICS2115] write off={offset & 3} reg=0x{m_reg_select:x2} data=0x{data:x2} voice={m_osc_select}");
        }

        void Trace(string message)
        {
            if (TracePgmSound)
                Console.Error.WriteLine($"[ICS2115] {message}");
        }
    }

    public static class ics2115_global
    {
        public static ics2115_device ICS2115(machine_config mconfig, string tag, u32 clock)
        {
            return emu.detail.device_type_impl.op<ics2115_device>(mconfig, tag, ics2115_device.ICS2115, clock);
        }

        public static ics2115_device ICS2115<bool_Required>(machine_config mconfig, device_finder<ics2115_device, bool_Required> finder, u32 clock)
            where bool_Required : bool_const, new()
        {
            return emu.detail.device_type_impl.op(mconfig, finder, ics2115_device.ICS2115, clock);
        }

        public static ics2115_device ICS2115<bool_Required>(machine_config mconfig, device_finder<ics2115_device, bool_Required> finder, XTAL clock)
            where bool_Required : bool_const, new()
        {
            return emu.detail.device_type_impl.op(mconfig, finder, ics2115_device.ICS2115, clock);
        }
    }
}
