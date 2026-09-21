using System.Reflection.Emit;

namespace Ryu64.MIPS
{
    public partial class R4300
    {
        private static bool IsCpuBlockCop1(int kind)
            => kind == 17 || kind == 49 || kind == 53 || kind == 57 || kind == 61;

        private static bool IsStraightCop1Instruction(uint word)
        {
            // Match OpcodeTable's supported encodings. Branches (including
            // likely branches) retain their ordinary delay-slot handling.
            uint format = (word >> 21) & 31, function = word & 63;
            switch (format)
            {
                case 0: case 1: case 4: case 5: return true;
                case 2: case 6: return (word & 0x7ff) == 0;
                case 16: return function <= 15 || function == 33 || function == 36 || function == 37 || function >= 48;
                case 17: return function <= 15 || function == 32 || function == 36 || function == 37 || function >= 48;
                case 20: case 21: return function == 32 || function == 33;
                default: return false;
            }
        }

        private static bool EmitCpuJitCop1(ILGenerator il, OpcodeTable.OpcodeDesc desc, int kind)
        {
            if (!IsCpuBlockCop1(kind)) return false;
            // Resolve dispatch once. Reuse the interpreter's arithmetic and
            // FR-dependent register mapping, including NaNs, rounding, control
            // registers and high halves of paired registers. The caller guards
            // CU1 and any RAM access before changing PC or register state.
            il.Emit(OpCodes.Ldc_I4, unchecked((int)desc.Opcode));
            il.Emit(OpCodes.Newobj, typeof(OpcodeTable.OpcodeDesc).GetConstructor(new[] { typeof(uint) }));
            il.Emit(OpCodes.Call, OpcodeTable.GetOpcodeInfo(desc.Opcode).Interpret.Method);
            return true;
        }
    }
}
