using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace Ryu64.MIPS
{
    public partial class R4300
    {
        // Experimental compile-time placement only: no flag check in guest execution.
        // "zero" isolates r0; "1" also caches nonzero registers. Default stays off
        // until repeated gameplay measurements establish a stable gain.
        private static readonly string CpuJitRegisterMode =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_REGISTERS") ?? "0";

        private static bool IsCpuJitAlu(int kind)
            => (kind >= 9 && kind <= 15) || kind == 25
                || (kind >= 64 && kind < 128 && kind != 72 && kind != 73);

        // Compile-time register placement, with no cache in emulated state.
        // Restrict locals to blocks whose operations never call a GPR-reading
        // interpreter helper. GPU RAM reconciliation does not change GPRs.
        private sealed class CpuJitRegisters
        {
            private readonly ILGenerator il;
            private readonly LocalBuilder array;
            private readonly LocalBuilder[] locals;
            private readonly bool loop;
            private Dictionary<uint, Label> exits;
            private uint cached, written, incoming, dirty;

            internal CpuJitRegisters(ILGenerator il, LocalBuilder array, List<uint> words, bool loop)
            {
                this.il = il; this.array = array; this.loop = loop;
                bool nonzero = CpuJitRegisterMode == "1";
                if (!nonzero && CpuJitRegisterMode != "zero") return;
                var accesses = new int[32];
                for (int i = 0; i < words.Count; i++)
                {
                    uint word = words[i];
                    if (word == 0) continue;
                    var d = new OpcodeTable.OpcodeDesc(word);
                    int kind = GetCpuBlockOpcodeKind(word);
                    uint reads = 0, writes = 0;
                    if (CpuJitBranch(kind))
                    {
                        // Delay-slot memory/COP instructions use shared helpers.
                        if (i + 1 >= words.Count || !IsCpuJitAlu(GetCpuBlockOpcodeKind(words[i + 1]))) return;
                        if (kind != 2 && kind != 3) reads = 1u << d.op1;
                        if (kind == 4 || kind == 5) reads |= 1u << d.op2;
                        if (kind == 3 || kind == 73) writes = 1u << (kind == 3 ? 31 : d.op3);
                    }
                    else if (kind == 32 || kind == 33 || kind == 35 || kind == 36
                        || kind == 37 || kind == 39 || kind == 55)
                    { reads = 1u << d.op1; writes = 1u << d.op2; }
                    else if (kind == 43) reads = (1u << d.op1) | (1u << d.op2);
                    else if (IsCpuJitAlu(kind))
                    {
                        writes = 1u << (kind < 64 ? d.op2 : d.op3);
                        if (kind < 64) { if (kind != 15) reads = 1u << d.op1; }
                        else if (kind != 80 && kind != 82)
                        {
                            reads = 1u << d.op2;
                            if (kind != 64 && kind != 66 && kind != 67 && kind < 120)
                                reads |= 1u << d.op1;
                        }
                    }
                    else return;
                    incoming |= reads & ~written & ~1u;
                    written |= writes & ~1u;
                    for (int r = 1; r < 32; r++)
                    {
                        if ((reads & (1u << r)) != 0) accesses[r]++;
                        if ((writes & (1u << r)) != 0) accesses[r]++;
                    }
                }
                locals = new LocalBuilder[32];
                // A temporary written before its first read needs no entry load.
                // Include write/read pairs, but require more reuse for live inputs.
                for (int r = 1; r < 32; r++)
                    if (accesses[r] < ((incoming & (1u << r)) != 0 ? 3 : 2)) accesses[r] = 0;
                // Leave host registers available for RAM pointers and guards.
                for (int n = 0; nonzero && n < 4; n++)
                {
                    int best = 0;
                    for (int r = 1; r < 32; r++)
                        if (accesses[r] > accesses[best]) best = r;
                    if (accesses[best] == 0) break;
                    locals[best] = il.DeclareLocal(typeof(ulong)); cached |= 1u << best; accesses[best] = 0;
                }
                // Keep the interpreter's temporary r0 value at exits, including
                // rejected guards before normalization. Within a valid block,
                // the host JIT can remove overwritten boundary normalizations.
                locals[0] = il.DeclareLocal(typeof(ulong)); cached |= 1; written |= 1;
            }

            internal void Initialize()
            {
                // Straight-line exits publish only values actually written before
                // their guard. Backedges can leave values from a previous iteration,
                // so initialize every cached register and publish all loop outputs.
                uint inputs = cached & (loop ? cached : incoming);
                for (int r = 0; r < 32; r++)
                    if ((inputs & (1u << r)) != 0) { ReadArray(r); il.Emit(OpCodes.Stloc, locals[r]); }
            }

            private void ArraySlot(int r)
            { il.Emit(OpCodes.Ldloc, array); il.Emit(OpCodes.Ldc_I4, r); }
            private void ReadArray(int r) { ArraySlot(r); il.Emit(OpCodes.Ldelem_I8); }
            internal void Read(int r)
            { if ((cached & (1u << r)) == 0) ReadArray(r); else il.Emit(OpCodes.Ldloc, locals[r]); }
            internal void BeginWrite(int r) { if ((cached & (1u << r)) == 0) ArraySlot(r); }
            internal void EndWrite(int r)
            {
                if ((cached & (1u << r)) == 0) il.Emit(OpCodes.Stelem_I8);
                else { il.Emit(OpCodes.Stloc, locals[r]); dirty |= 1u << r; }
            }
            internal void NormalizeZero()
            { BeginWrite(0); il.Emit(OpCodes.Ldc_I8, 0L); EndWrite(0); }
            internal Label ExitLabel(Label finish)
            {
                uint mask = cached & (loop ? written : dirty);
                if (mask == 0) return finish;
                if (exits == null) exits = new Dictionary<uint, Label>();
                if (!exits.TryGetValue(mask, out Label label))
                { label = il.DefineLabel(); exits.Add(mask, label); }
                return label;
            }

            internal void EmitExits(Label finish)
            {
                if (exits == null) return;
                // Share epilogues between guards with the same live output set.
                // Future temporaries keep their original array value at early exits.
                foreach (var exit in exits)
                {
                    il.MarkLabel(exit.Value);
                    for (int r = 0; r < 32; r++)
                        if ((exit.Key & (1u << r)) != 0)
                        { ArraySlot(r); il.Emit(OpCodes.Ldloc, locals[r]); il.Emit(OpCodes.Stelem_I8); }
                    il.Emit(OpCodes.Br, finish);
                }
            }
        }
    }
}
