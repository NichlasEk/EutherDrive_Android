using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.Arcade.Vegas;

internal sealed partial class MipsR5000Core
{
    private readonly bool _experimentRuntimeGeneratedConditionalBlock =
        GauntletDarkLegacyAdapter.IsTruthy(Environment.GetEnvironmentVariable(
            "EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_GENERATED_CONDITIONAL_BLOCK"));
    private Func<MipsR5000Core, uint, int>? _runtimeGeneratedConditionalBlock;
    private ulong _runtimeGeneratedConditionalBlockRuns;

    private bool TryRunRuntimeGeneratedConditionalBlock(ulong pc, uint runtimeMainState)
    {
        if (_remainingProbeSteps < 7)
            return false;

        if (_runtimeGeneratedConditionalBlock is null)
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
                return false;
            // Deliberately restrict admission to the reference loop. This experiment
            // shares its immutable-text assumption; it is not a general code cache.
            uint[] words = new uint[7];
            ReadOnlySpan<uint> reference =
                [0xc4600000U, 0x24630004U, 0x24e70001U, 0x28e20003U,
                 0xe4c00000U, 0x1440fffaU, 0x24c60004U];
            for (int i = 0; i < words.Length; i++)
            {
                words[i] = _memory.ReadRuntimeInstruction32(pc + (ulong)(i * 4));
                if (words[i] != reference[i] || _memory.NeedsRuntimeCpuPcAt(pc + (ulong)(i * 4)))
                    return false;
            }
            _runtimeGeneratedConditionalBlock = EmitConditionalBlock(pc, words);
        }

        int executed = _runtimeGeneratedConditionalBlock(this, runtimeMainState);
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * (ulong)executed);
        _instructionCounter += (ulong)executed;
        _probeStepDebt += executed - 1;
        _runtimeCompactConditionalBlockRuns++;
        _runtimeCompactConditionalBlockInstructions += (ulong)executed;
        _runtimeGeneratedConditionalBlockRuns++;
        return true;
    }

    // Emit guest operands and control flow directly into IL. No expression-tree
    // register prologue/epilogue and no runtime opcode dispatch inside the block.
    private static Func<MipsR5000Core, uint, int> EmitConditionalBlock(ulong pc, uint[] words)
    {
        if (words.Length != 7 || words[0] >> 26 != 0x31 ||
            words[1] >> 26 != 9 || words[2] >> 26 != 9 || words[3] >> 26 != 10 ||
            words[4] >> 26 != 0x39 || words[5] >> 26 != 5 || words[6] >> 26 != 9)
            throw new ArgumentException("Unsupported conditional block shape", nameof(words));

        var method = new DynamicMethod("GauntletConditionalBlock", typeof(int),
            [typeof(MipsR5000Core), typeof(uint)], typeof(MipsR5000Core), true);
        ILGenerator il = method.GetILGenerator();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo gpr = typeof(MipsR5000Core).GetField(nameof(_gpr), fields)!;
        FieldInfo fpr = typeof(MipsR5000Core).GetField(nameof(_fpr), fields)!;
        FieldInfo memory = typeof(MipsR5000Core).GetField(nameof(_memory), fields)!;
        MethodInfo read = typeof(VegasMemoryMap).GetMethod(nameof(VegasMemoryMap.ReadRuntimeData32))!;
        MethodInfo write = typeof(VegasMemoryMap).GetMethod(nameof(VegasMemoryMap.WriteRuntimeData32))!;
        MethodInfo state = typeof(VegasMemoryMap).GetProperty(nameof(VegasMemoryMap.RuntimeMainState))!.GetMethod!;
        MethodInfo setPc = typeof(MipsR5000Core).GetProperty(nameof(Pc))!.GetSetMethod(true)!;
        MethodInfo setLast = typeof(MipsR5000Core).GetProperty(nameof(LastFetchedInstruction))!.GetSetMethod(true)!;

        void LoadField(FieldInfo field)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
        }
        void LoadGpr(int register)
        {
            if (register == 0)
                il.Emit(OpCodes.Ldc_I8, 0L);
            else
            {
                LoadField(gpr);
                il.Emit(OpCodes.Ldc_I4, register);
                il.Emit(OpCodes.Ldelem_I8);
            }
        }
        void Address(uint op)
        {
            LoadGpr((int)(op >> 21 & 31));
            il.Emit(OpCodes.Ldc_I8, (long)(short)op);
            il.Emit(OpCodes.Add);
        }
        void Instruction(uint op)
        {
            int rt = (int)(op >> 16 & 31);
            switch (op >> 26)
            {
                case 0x31: // lwc1
                    LoadField(fpr);
                    il.Emit(OpCodes.Ldc_I4, rt);
                    LoadField(memory);
                    Address(op);
                    il.Emit(OpCodes.Callvirt, read);
                    il.Emit(OpCodes.Conv_U8);
                    il.Emit(OpCodes.Stelem_I8);
                    break;
                case 0x39: // swc1; architectural register writes precede the helper
                    LoadField(memory);
                    Address(op);
                    LoadField(fpr);
                    il.Emit(OpCodes.Ldc_I4, rt);
                    il.Emit(OpCodes.Ldelem_I8);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Callvirt, write);
                    break;
                case 9: // addiu: wrap to 32 bits, then sign extend
                case 10: // slti: signed 64-bit comparison with signed immediate
                    if (rt == 0) break;
                    LoadField(gpr);
                    il.Emit(OpCodes.Ldc_I4, rt);
                    LoadGpr((int)(op >> 21 & 31));
                    il.Emit(OpCodes.Ldc_I8, (long)(short)op);
                    if (op >> 26 == 9)
                    {
                        il.Emit(OpCodes.Add);
                        il.Emit(OpCodes.Conv_I4);
                        il.Emit(OpCodes.Conv_I8);
                    }
                    else
                    {
                        il.Emit(OpCodes.Clt);
                        il.Emit(OpCodes.Conv_U8);
                    }
                    il.Emit(OpCodes.Stelem_I8);
                    break;
                default:
                    throw new ArgumentException("Unsupported opcode", nameof(words));
            }
        }
        void SetPc(ulong value)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I8, unchecked((long)value));
            il.Emit(OpCodes.Call, setPc);
        }
        void Return(int count, uint last)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, unchecked((int)last));
            il.Emit(OpCodes.Call, setLast);
            il.Emit(OpCodes.Ldc_I4, count);
            il.Emit(OpCodes.Ret);
        }

        for (int i = 0; i < 5; i++) Instruction(words[i]);
        SetPc(pc + 20);
        Label branch = il.DefineLabel();
        LoadField(memory);
        il.Emit(OpCodes.Callvirt, state);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Beq, branch);
        Return(5, words[4]); // Store changed runtime state: leave before the branch.
        il.MarkLabel(branch);

        uint terminal = words[5];
        Label notTaken = il.DefineLabel();
        Label delay = il.DefineLabel();
        LoadGpr((int)(terminal >> 21 & 31));
        LoadGpr((int)(terminal >> 16 & 31));
        il.Emit(OpCodes.Beq, notTaken);
        SetPc(unchecked(pc + 24UL + (ulong)((long)(short)terminal * 4)));
        il.Emit(OpCodes.Br, delay);
        il.MarkLabel(notTaken);
        SetPc(pc + 28);
        il.MarkLabel(delay);
        Instruction(words[6]);
        Return(7, words[6]);
        return method.CreateDelegate<Func<MipsR5000Core, uint, int>>();
    }
}
