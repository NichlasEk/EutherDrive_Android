namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        internal struct ResetFingerprintSnapshot
        {
            public uint Pc;
            public ushort Sr;
            public uint Sp;
            public byte InterruptMask;
            public bool VInterrupt;
            public bool HInterrupt;
            public bool ExtInterrupt;
        }

        internal ResetFingerprintSnapshot GetResetFingerprintSnapshot() => new()
        {
            Pc = g_reg_PC,
            Sr = g_reg_SR,
            Sp = g_reg_addr[7].l,
            InterruptMask = g_status_interrupt_mask,
            VInterrupt = g_interrupt_V_req,
            HInterrupt = g_interrupt_H_req,
            ExtInterrupt = g_interrupt_EXT_req
        };
    }
}
