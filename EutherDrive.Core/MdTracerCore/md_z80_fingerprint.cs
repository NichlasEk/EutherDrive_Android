namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_z80
    {
        internal struct ResetFingerprintSnapshot
        {
            public ushort Pc;
            public ushort Sp;
            public bool Iff1;
            public bool Iff2;
            public int InterruptMode;
            public bool InterruptPending;
            public bool BusGranted;
            public bool ResetLine;
        }

        internal ResetFingerprintSnapshot GetResetFingerprintSnapshot()
        {
            var bus = md_main.g_md_bus;
            return new ResetFingerprintSnapshot
            {
                Pc = g_reg_PC,
                Sp = g_reg_SP,
                Iff1 = g_IFF1,
                Iff2 = g_IFF2,
                InterruptMode = g_interruptMode,
                InterruptPending = g_interrupt_irq,
                BusGranted = bus?.Z80BusGranted ?? true,
                ResetLine = bus?.Z80Reset ?? false
            };
        }
    }
}
