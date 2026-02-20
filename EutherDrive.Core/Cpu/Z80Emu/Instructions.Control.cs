namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal static partial class Instructions
    {
        internal sealed partial class InstructionExecutor
        {
            public uint Halt()
            {
                Registers.Halted = true;
                return 4;
            }

            public uint Di()
            {
                Registers.Iff1 = false;
                Registers.Iff2 = false;
                return 4;
            }

            public uint Ei()
            {
                Registers.Iff1 = true;
                Registers.Iff2 = true;
                Registers.InterruptDelay = true;
                return 4;
            }

            public uint Im(InterruptMode mode)
            {
                Registers.InterruptMode = mode;
                return 8;
            }
        }
    }
}
