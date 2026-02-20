using System;
using EutherDrive.Core.Cpu.Z80Emu;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_z80
    {
        private static readonly bool UseJgenesisZ80 = ParseUseJgenesisZ80();

        private static bool ParseUseJgenesisZ80()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_JGENESIS");
            if (string.IsNullOrEmpty(raw))
                return true;
            return string.Equals(raw, "1", StringComparison.Ordinal);
        }

        private Z80? _jgZ80;
        private JgBusAdapter? _jgBus;

        private sealed class JgBusAdapter : IBusInterface
        {
            private readonly md_z80 _parent;

            public JgBusAdapter(md_z80 parent)
            {
                _parent = parent;
            }

            public byte ReadMemory(ushort address)
            {
                _parent.SyncLegacyRegsFromJgQuick();
                return _parent.read8(address);
            }

            public void WriteMemory(ushort address, byte value)
            {
                _parent.SyncLegacyRegsFromJgQuick();
                _parent.write8(address, value);
            }

            public byte ReadIo(ushort address)
            {
                _parent.SyncLegacyRegsFromJgQuick();
                uint normalized = _parent.NormalizeIoPort(address);
                return _parent.read8(normalized);
            }

            public void WriteIo(ushort address, byte value)
            {
                _parent.SyncLegacyRegsFromJgQuick();
                uint normalized = _parent.NormalizeIoPort(address);
                _parent.write8(normalized, value);
            }

            public InterruptLine Nmi()
            {
                if (_parent.ConsumeNmiLine())
                    return InterruptLine.Low;
                return InterruptLine.High;
            }

            public InterruptLine Int()
            {
                return _parent.g_interrupt_irq ? InterruptLine.Low : InterruptLine.High;
            }

            public bool BusReq()
            {
                return md_main.g_md_bus?.Z80BusGranted ?? false;
            }

            public bool Reset()
            {
                return md_main.g_md_bus?.Z80Reset ?? false;
            }
        }

        private void EnsureJgZ80()
        {
            if (_jgZ80 == null)
            {
                _jgZ80 = new Z80();
                _jgBus = new JgBusAdapter(this);
            }
        }

        private bool ConsumeNmiLine()
        {
            if (!g_interrupt_nmi)
                return false;
            g_interrupt_nmi = false;
            return true;
        }

        private void SyncLegacyRegsFromJgQuick()
        {
            if (_jgZ80 == null)
                return;

            var r = _jgZ80.Registers;
            g_reg_PC = r.Pc;
        }

        private void SyncLegacyRegsFromJg()
        {
            if (_jgZ80 == null)
                return;

            var r = _jgZ80.Registers;
            g_reg_PC = r.Pc;
            g_reg_SP = r.Sp;
            g_reg_A = r.A;
            g_reg_B = r.B;
            g_reg_C = r.C;
            g_reg_D = r.D;
            g_reg_E = r.E;
            g_reg_H = r.H;
            g_reg_L = r.L;
            g_reg_IX = r.Ix;
            g_reg_IY = r.Iy;
            g_reg_R = r.R;
            g_reg_I = r.I;

            g_reg_Au = r.Ap;
            g_reg_Bu = r.Bp;
            g_reg_Cu = r.Cp;
            g_reg_Du = r.Dp;
            g_reg_Eu = r.Ep;
            g_reg_Hu = r.Hp;
            g_reg_Lu = r.Lp;
            g_reg_Fu = r.Fp.ToByte();

            g_IFF1 = r.Iff1;
            g_IFF2 = r.Iff2;
            g_interruptMode = (int)r.InterruptMode;
            g_halt = r.Halted;

            g_flag_S = r.F.Sign ? 1 : 0;
            g_flag_Z = r.F.Zero ? 1 : 0;
            g_flag_H = r.F.HalfCarry ? 1 : 0;
            g_flag_PV = r.F.Overflow ? 1 : 0;
            g_flag_N = r.F.Subtract ? 1 : 0;
            g_flag_C = r.F.Carry ? 1 : 0;
        }

        private void ResetJgZ80()
        {
            if (_jgZ80 == null)
                return;
            _jgZ80.SetPc(0);
            _jgZ80.SetSp(g_reg_SP);
        }

        private void RunJgenesis(int in_clock)
        {
            EnsureJgZ80();
            if (_jgZ80 == null || _jgBus == null)
                return;

            md_main.g_md_bus?.ApplyZ80BusReqLatch();
            bool busRequested = md_main.g_md_bus?.Z80BusGranted ?? false;
            bool z80reset = md_main.g_md_bus?.Z80Reset ?? false;

            _budgetCycles += in_clock;
            if (TraceZ80Stats)
                _z80StatsBudgetCount += in_clock;

            if (!g_active)
                return;

            if (HaltOnBusReq && busRequested)
                return;

            if (busRequested || z80reset)
            {
                if (TraceZ80Stats)
                    _z80StatsBlockedCount++;
                return;
            }

            g_clock_total += in_clock;
            while (g_clock_total > 0)
            {
                uint tCycles = _jgZ80.ExecuteInstruction(_jgBus);
                int waitCycles = ConsumeWaitCycles();
                uint totalCycles = tCycles + (uint)waitCycles;

                _totalCycles += totalCycles;
                if (TraceZ80Stats)
                {
                    _z80StatsCycleCount += totalCycles;
                    _z80StatsInstrCount++;
                }

                LineCycles += (int)totalCycles;
                md_main.g_md_music?.TickYmTimersFromZ80((int)totalCycles);
                TickIrqAutoClear((int)totalCycles);

                g_clock_total -= (int)totalCycles;
            }

            SyncLegacyRegsFromJg();
        }

        private int ConsumeWaitCycles()
        {
            int waitCycles = _waitCycles;
            _waitCycles = 0;
            return waitCycles;
        }
    }
}
