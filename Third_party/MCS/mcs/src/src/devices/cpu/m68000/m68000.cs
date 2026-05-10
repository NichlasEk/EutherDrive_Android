// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using offs_t = System.UInt32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using uint32_t = System.UInt32;

using static mame.device_global;
using static mame.diexec_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.machine_global;


namespace mame
{
    public class m68000_device : cpu_device
    {
        public static readonly emu.detail.device_type_impl M68000 = DEFINE_DEVICE_TYPE("m68000", "Motorola MC68000", (type, mconfig, tag, owner, clock) => { return new m68000_device(mconfig, tag, owner, clock); });


        sealed class device_execute_interface_m68000 : device_execute_interface
        {
            public device_execute_interface_m68000(machine_config mconfig, device_t device) : base(mconfig, device) { }

            protected override u32 execute_min_cycles() { return 4; }
            protected override u32 execute_max_cycles() { return 158; }
            protected override u32 execute_input_lines() { return 8; }
            protected override void execute_run() { ((m68000_device)device()).device_execute_interface_execute_run(); }
            protected override void execute_set_input(int inputnum, int state) { ((m68000_device)device()).device_execute_interface_execute_set_input(inputnum, state); }
        }


        sealed class device_memory_interface_m68000 : device_memory_interface
        {
            public device_memory_interface_m68000(machine_config mconfig, device_t device) : base(mconfig, device) { }

            protected override space_config_vector memory_space_config() { return ((m68000_device)device()).device_memory_interface_memory_space_config(); }
        }


        sealed class mcs_bus : eutherdrive_m68000.IBusInterface
        {
            readonly m68000_device m_owner;

            public mcs_bus(m68000_device owner)
            {
                m_owner = owner;
            }

            public u8 ReadByte(u32 address)
            {
                address &= 0x00ff_ffff;
                if (m_owner.m_fast_read_byte != null && m_owner.m_fast_read_byte(address, out u8 value))
                    return value;
                return m_owner.read_byte(address);
            }

            public u16 ReadWord(u32 address)
            {
                address &= 0x00ff_ffff;
                if (m_owner.m_fast_read_word != null && m_owner.m_fast_read_word(address, out u16 value))
                    return value;
                return m_owner.read_word(address);
            }
            public u32 ReadLong(u32 address) => ((u32)ReadWord(address) << 16) | ReadWord(address + 2);

            public void WriteByte(u32 address, u8 value)
            {
                address &= 0x00ff_ffff;
                if (m_owner.m_fast_write_byte != null && m_owner.m_fast_write_byte(address, value))
                    return;
                m_owner.write_byte(address, value);
            }

            public void WriteWord(u32 address, u16 value)
            {
                address &= 0x00ff_ffff;
                if (m_owner.m_fast_write_word != null && m_owner.m_fast_write_word(address, value))
                    return;
                m_owner.write_word(address, value);
            }
            public void WriteLong(u32 address, u32 value)
            {
                WriteWord(address, (u16)(value >> 16));
                WriteWord(address + 2, (u16)value);
            }

            public u8 InterruptLevel() => m_owner.highest_interrupt_level();
            public void AcknowledgeInterrupt(u8 level) => m_owner.standard_irq_callback(level == 1 ? INPUT_LINE_IRQ0 : level);

            public bool Reset() => false;
            public bool Halt() => false;

            public eutherdrive_m68000.BusSignals Signals => default;
            public u16 CurrentOpcode => m_owner.CurrentOpcode;
        }


        readonly address_space_config m_program_config;
        readonly eutherdrive_m68000.M68000 m_core;
        readonly mcs_bus m_bus;
        public delegate bool fast_read_byte_delegate(u32 address, out u8 value);
        public delegate bool fast_read_word_delegate(u32 address, out u16 value);
        public delegate bool fast_write_byte_delegate(u32 address, u8 value);
        public delegate bool fast_write_word_delegate(u32 address, u16 value);
        fast_read_byte_delegate m_fast_read_byte;
        fast_read_word_delegate m_fast_read_word;
        fast_write_byte_delegate m_fast_write_byte;
        fast_write_word_delegate m_fast_write_word;
        readonly bool[] m_irq_lines = new bool[8];
        readonly u32[] m_state_d = new u32[8];
        readonly u32[] m_state_a = new u32[7];
        readonly intref m_icount = new intref();

        device_memory_interface_m68000 m_dimemory;
        address_space m_program;
        u32 m_ppc;
        u32 m_state_usp;
        u32 m_state_ssp;
        u16 m_state_sr;
        u32 m_state_pc;
        u16 m_state_prefetch;


        public m68000_device(machine_config mconfig, string tag, device_t owner, uint32_t clock)
            : this(mconfig, M68000, tag, owner, clock)
        {
        }


        public m68000_device(machine_config mconfig, device_type type, string tag, device_t owner, uint32_t clock)
            : base(mconfig, type, tag, owner, clock)
        {
            m_class_interfaces.Add(new device_execute_interface_m68000(mconfig, this));
            m_class_interfaces.Add(new device_memory_interface_m68000(mconfig, this));
            m_dimemory = GetClassInterface<device_memory_interface_m68000>();

            m_program_config = new address_space_config("program", ENDIANNESS_BIG, 16, 24, 0);
            m_core = eutherdrive_m68000.M68000.CreateBuilder()
                .AllowTasWrites(true)
                .Name(tag)
                .Build();
            m_bus = new mcs_bus(this);
            m_core.BindBus(m_bus);
        }


        public u32 Pc => m_core.Pc;
        public u16 StatusRegister => m_core.StatusRegister;
        public u8 InterruptPriorityMask => m_core.InterruptPriorityMask;
        public bool IsStopped => m_core.IsStopped;
        public u16 CurrentOpcode => m_core.NextOpcode;
        public eutherdrive_m68000.M68000.M68000State GetState() => m_core.GetState();


        public void set_fast_memory_handlers(
            fast_read_byte_delegate read_byte,
            fast_read_word_delegate read_word,
            fast_write_byte_delegate write_byte,
            fast_write_word_delegate write_word)
        {
            m_fast_read_byte = read_byte;
            m_fast_read_word = read_word;
            m_fast_write_byte = write_byte;
            m_fast_write_word = write_word;
        }


        public void reset_from_bus()
        {
            Array.Clear(m_irq_lines, 0, m_irq_lines.Length);
            m_core.Reset(m_bus);
            m_ppc = m_core.Pc;
        }


        protected override void device_start()
        {
            m_program = m_dimemory.space(AS_PROGRAM);
            set_icountptr(m_icount);
            save_item(NAME(new { m_irq_lines }));
            save_item(NAME(new { m_state_d }));
            save_item(NAME(new { m_state_a }));
            SaveStateRef(nameof(m_ppc), () => m_ppc, value => m_ppc = value);
            SaveStateRef(nameof(m_state_usp), () => m_state_usp, value => m_state_usp = value);
            SaveStateRef(nameof(m_state_ssp), () => m_state_ssp, value => m_state_ssp = value);
            SaveStateRef(nameof(m_state_sr), () => m_state_sr, value => m_state_sr = value);
            SaveStateRef(nameof(m_state_pc), () => m_state_pc, value => m_state_pc = value);
            SaveStateRef(nameof(m_state_prefetch), () => m_state_prefetch, value => m_state_prefetch = value);
            machine().save().register_presave(PresaveState);
            machine().save().register_postload(PostloadState);
        }

        void SaveStateRef<T>(string itemName, Func<T> getter, Action<T> setter)
        {
            machine().save().save_item_ref(this, name(), tag(), 0, itemName, getter, setter);
        }

        void PresaveState()
        {
            var state = m_core.GetState();
            Array.Copy(state.Data, m_state_d, m_state_d.Length);
            Array.Copy(state.Address, m_state_a, m_state_a.Length);
            m_state_usp = state.Usp;
            m_state_ssp = state.Ssp;
            m_state_sr = state.Sr;
            m_state_pc = state.Pc;
            m_state_prefetch = state.Prefetch;
            m_ppc = state.Pc;
        }

        void PostloadState()
        {
            m_core.SetState(new eutherdrive_m68000.M68000.M68000State(
                m_state_d,
                m_state_a,
                m_state_usp,
                m_state_ssp,
                m_state_sr,
                m_state_pc,
                m_state_prefetch));
            m_ppc = m_state_pc;
        }


        protected override void device_reset()
        {
            Array.Clear(m_irq_lines, 0, m_irq_lines.Length);
            m_core.Reset(m_bus);
            m_ppc = m_core.Pc;
        }


        void device_execute_interface_execute_run()
        {
            bool callDebuggerHook = (machine().debug_flags & DEBUG_FLAG_CALL_HOOK) != 0;
            if (callDebuggerHook)
            {
                do
                {
                    m_ppc = m_core.Pc;
                    debugger_instruction_hook(m_ppc);

                    u32 cycles = m_core.ExecuteBoundInstruction();
                    int elapsed = cycles > int.MaxValue ? int.MaxValue : (int)cycles;
                    m_icount.i -= Math.Max(1, elapsed);
                }
                while (m_icount.i > 0);
                return;
            }

            do
            {
                u32 cycles = m_core.TryConsumeBoundIdleLoop(m_icount.i, out u32 idleCycles)
                    ? idleCycles
                    : m_core.ExecuteBoundInstruction();
                int elapsed = cycles > int.MaxValue ? int.MaxValue : (int)cycles;
                m_icount.i -= Math.Max(1, elapsed);
            }
            while (m_icount.i > 0);

            m_ppc = m_core.Pc;
        }


        void device_execute_interface_execute_set_input(int inputnum, int state)
        {
            if (inputnum < INPUT_LINE_IRQ0 || inputnum > INPUT_LINE_IRQ0 + 7)
                return;

            int level = inputnum == INPUT_LINE_IRQ0 ? 1 : inputnum - INPUT_LINE_IRQ0;
            m_irq_lines[level] = state != CLEAR_LINE;
        }


        space_config_vector device_memory_interface_memory_space_config()
        {
            return new space_config_vector()
            {
                std.make_pair(AS_PROGRAM, m_program_config)
            };
        }


        u8 read_byte(offs_t address) => m_program.read_byte(address & 0x00ff_ffff);
        u16 read_word(offs_t address) => m_program.read_word(address & 0x00ff_ffff);
        void write_byte(offs_t address, u8 value) => m_program.write_byte(address & 0x00ff_ffff, value);
        void write_word(offs_t address, u16 value) => m_program.write_word(address & 0x00ff_ffff, value);


        u8 highest_interrupt_level()
        {
            for (int level = 7; level >= 1; level--)
            {
                if (m_irq_lines[level])
                    return (u8)level;
            }

            return 0;
        }
    }


    public static class m68000_global
    {
        public static m68000_device M68000(machine_config mconfig, string tag, u32 clock) { return emu.detail.device_type_impl.op<m68000_device>(mconfig, tag, m68000_device.M68000, clock); }
        public static m68000_device M68000(machine_config mconfig, string tag, XTAL clock) { return emu.detail.device_type_impl.op<m68000_device>(mconfig, tag, m68000_device.M68000, clock); }
        public static m68000_device M68000<bool_Required>(machine_config mconfig, device_finder<m68000_device, bool_Required> finder, u32 clock) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, m68000_device.M68000, clock); }
        public static m68000_device M68000<bool_Required>(machine_config mconfig, device_finder<m68000_device, bool_Required> finder, XTAL clock) where bool_Required : bool_const, new() { return emu.detail.device_type_impl.op(mconfig, finder, m68000_device.M68000, clock); }
    }
}
