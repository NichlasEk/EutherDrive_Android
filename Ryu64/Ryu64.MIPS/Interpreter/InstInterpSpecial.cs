using System;

namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        public static void SYSCALL(OpcodeTable.OpcodeDesc Desc)
        {
            if (Desc.op4 > 0 && Common.Variables.UTEsyscall)
            {
                // Special type of SYSCALL used for the Fraser CPU Tests
                char TestChar = (char)(Desc.op2 + 64);
                string TestResult = $"Test Result - Set:{Desc.op3} Test:{Desc.op4} Result:{TestChar}";

                if (TestChar == 'F')
                    Common.Logger.PrintErrorLine(TestResult);
                else if (TestChar == 'P')
                    Common.Logger.PrintSuccessLine(TestResult);
                else
                    Common.Logger.PrintInfoLine(TestResult);
            }
            else
            {
                // Regular VR4300 SYSCALL raises a general exception.
                R4300.RaiseSyscallException(Registers.R4300.PC);
                return;
            }

            Registers.R4300.PC += 4;
        }

        public static void BREAK(OpcodeTable.OpcodeDesc Desc)
        {
            _ = Desc;
            R4300.RaiseBreakException(Registers.R4300.PC);
        }
    }
}
