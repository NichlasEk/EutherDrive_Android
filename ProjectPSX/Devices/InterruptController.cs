using System;

namespace ProjectPSX.Devices {
    public class InterruptController {
        private static readonly bool TraceIrq = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_IRQ_TRACE") == "1";

        private uint ISTAT; //IF Trigger that needs to be ack
        private uint IMASK; //IE Global Interrupt enable

        internal void set(Interrupt interrupt) {
            uint previous = ISTAT;
            ISTAT |= (uint)interrupt;
            TraceChange("set", previous, ISTAT, interrupt: interrupt);
            //Console.WriteLine($"ISTAT SET MANUAL FROM DEVICE: {ISTAT:x8} IMASK {IMASK:x8}");
        }


        internal void writeISTAT(uint value) {
            uint previous = ISTAT;
            ISTAT &= value & 0x7FF;
            TraceChange("ack", previous, ISTAT, value: value);
            //Console.ForegroundColor = ConsoleColor.Magenta;
            //Console.WriteLine($"[IRQ] [ISTAT] Write {value:x8} ISTAT {ISTAT:x8}");
            //Console.ResetColor();
            //Console.ReadLine();
        }

        internal void writeIMASK(uint value) {
            uint previous = IMASK;
            IMASK = value & 0x7FF;
            if (TraceIrq && previous != IMASK) {
                Console.WriteLine($"[IRQ] mask old={previous:x3} new={IMASK:x3} istat={ISTAT:x3} pc={CPU.TraceCurrentPC:x8}");
            }
            //Console.WriteLine($"[IRQ] [IMASK] Write {IMASK:x8}");
            //Console.ReadLine();
        }

        internal uint loadISTAT() {
            //Console.WriteLine($"[IRQ] [ISTAT] Load {ISTAT:x8}");
            //Console.ReadLine();
            return ISTAT;
        }

        internal uint loadIMASK() {
            //Console.WriteLine($"[IRQ] [IMASK] Load {IMASK:x8}");
            //Console.ReadLine();
            return IMASK;
        }

        internal uint DebugISTAT => ISTAT;
        internal uint DebugIMASK => IMASK;

        internal bool interruptPending() {
            return (ISTAT & IMASK) != 0;
        }

        internal void write(uint addr, uint value) {
            uint register = addr & 0xF;
            if(register == 0) {
                uint previous = ISTAT;
                ISTAT &= value & 0x7FF;
                TraceChange("ack", previous, ISTAT, value: value);
            } else if(register == 4) {
                uint previous = IMASK;
                IMASK = value & 0x7FF;
                if (TraceIrq && previous != IMASK) {
                    Console.WriteLine($"[IRQ] mask old={previous:x3} new={IMASK:x3} istat={ISTAT:x3} pc={CPU.TraceCurrentPC:x8}");
                }
            }
        }

        internal uint load(uint addr) {
            uint register = addr & 0xF;
            if (register == 0) {
                return ISTAT;
            } else if (register == 4) {
                return IMASK;
            } else {
                return 0xFFFF_FFFF;
            }
        }

        private void TraceChange(string op, uint previous, uint current, uint? value = null, Interrupt? interrupt = null) {
            if (!TraceIrq || previous == current) {
                return;
            }

            string extra = interrupt.HasValue
                ? $" src={interrupt.Value}"
                : value.HasValue
                    ? $" value={value.Value:x8}"
                    : string.Empty;
            Console.WriteLine($"[IRQ] {op}{extra} istat={previous:x3}->{current:x3} imask={IMASK:x3} pc={CPU.TraceCurrentPC:x8}");
        }
    }
}
