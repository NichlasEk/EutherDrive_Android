using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Reflection;
using System.Threading;

namespace Ryu64.MIPS
{
    public partial class R4300
    {
        // Compile only hot, event-free direct-RAM blocks. Validate complete code
        // bytes on every entry; a store always ends the compiled region.
        private static readonly bool CpuJitEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT") != "0";
        private const uint CpuJitMaximumInstructions = 512;
        private sealed class CpuJitEntry
        {
            internal uint Pc, First;
            internal int Hits;
            internal Func<uint, uint, bool, uint> Run;
        }
        private static readonly CpuJitEntry[] CpuJitCache = new CpuJitEntry[65536];
        private static readonly Dictionary<uint, CpuJitEntry> CpuJitEntries = new Dictionary<uint, CpuJitEntry>();
        private sealed class CpuJitCode
        {
            internal volatile Func<uint, uint, bool, uint> Run;
        }
        // The CPU thread alone owns dictionaries. A worker publishes only an
        // immutable delegate through its holder after compiling copied words.
        private static readonly Dictionary<string, CpuJitCode> CpuJitVersions = new Dictionary<string, CpuJitCode>();
        private static int CpuJitCompilePending;
        // The differential probe forces synchronous publication via reflection.
        private static bool CpuJitSynchronous = false;
        private static int CpuJitCompilations;
        private static long CpuJitInstructions;
        private static int CpuJitInvalidations;
        private static int CpuJitRejectedCompilations;
        private static int CpuJitHotThreshold = 256;
        private static volatile bool CpuJitUnavailable;

        private static void ResetCpuJitCache()
        {
            Array.Clear(CpuJitCache, 0, CpuJitCache.Length);
            CpuJitEntries.Clear();
            CpuJitVersions.Clear();
            CpuJitCompilations = 0;
            CpuJitInstructions = 0;
            CpuJitInvalidations = 0;
            CpuJitRejectedCompilations = 0;
        }

        private static uint TryRunCpuJit(uint pc, uint word, uint budget, uint elapsed, bool history)
        {
            if (!CpuJitEnabled || CpuJitUnavailable || budget < 2) return 0;
            int slot = (int)((pc >> 2) & (CpuJitCache.Length - 1));
            var entry = CpuJitCache[slot];
            if (entry == null || entry.Pc != pc)
            {
                if (!CpuJitEntries.TryGetValue(pc, out entry))
                {
                    if (CpuJitEntries.Count >= 8192) return 0;
                    entry = new CpuJitEntry { Pc = pc, First = word };
                    CpuJitEntries.Add(pc, entry);
                }
                CpuJitCache[slot] = entry;
            }
            if (entry.First != word)
            {
                CpuJitInvalidations++;
                entry.First = word;
                entry.Run = null;
                entry.Hits = 0;
            }
            if (entry.Run == null)
            {
                if (++entry.Hits != CpuJitHotThreshold) return 0;
                entry.Hits = 0;
                try { entry.Run = CompileCpuJit(pc); }
                catch (PlatformNotSupportedException) { CpuJitUnavailable = true; return 0; }
                if (entry.Run == null)
                {
                    CpuJitRejectedCompilations++;
                    entry.Hits = -1024;
                    return 0;
                }
            }
            uint result = entry.Run(budget, elapsed, history);
            if (result == uint.MaxValue)
            {
                CpuJitInvalidations++;
                entry.Run = null;
                entry.Hits = 0;
                return 0;
            }
            CpuJitInstructions += result;
            return result;
        }

        private static void RecordCpuJitHistory(RecentInst[] pattern, int period, uint entries, uint elapsed)
        {
            // Compilation expands the repeating PC/opcode pattern. Copy its
            // surviving suffix into the ring instead of rebuilding every entry
            // on every native backedge. The caller recorded the first entry;
            // delay slots have already been excluded from the supplied count.
            uint first = elapsed == 0 ? 1u : 0u;
            if (entries <= first) return;
            entries -= first;
            var recent = _recentInst;
            int position = _recentInstPos;
            if (entries > recent.Length)
            {
                uint skipped = entries - (uint)recent.Length;
                position = (int)((position + skipped) & RecentInstHistoryMask);
                first = (first + skipped) % (uint)period;
                entries = (uint)recent.Length;
            }
            int count = (int)entries;
            int tail = Math.Min(count, recent.Length - position);
            pattern.AsSpan((int)first, tail).CopyTo(recent.AsSpan(position));
            if (tail != count)
                pattern.AsSpan((int)first + tail, count - tail).CopyTo(recent);
            _recentInstPos = (position + count) & RecentInstHistoryMask;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static ulong ReadCpuJit32(byte[] ram, int address)
            => unchecked((ulong)(long)System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(ram.AsSpan(address, 4)));

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static ulong ReadCpuJit64(byte[] ram, int address)
            => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ram.AsSpan(address, 8));

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static ulong ReadCpuJit16(byte[] ram, int address)
            => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(ram.AsSpan(address, 2));

        private static bool CpuJitBranch(int kind) => (kind >= 2 && kind <= 7) || kind == 72 || kind == 73 || kind == 128 || kind == 129;
        private static bool CpuJitStore(int kind) => kind == 40 || kind == 41 || kind == 43 || kind == 63;

        private static bool IsInvariantCpuJitLoop(List<uint> words)
        {
            // A register read before its first write is an input to the loop.
            // If none of those inputs is written, repeating a taken iteration
            // cannot change any result. RAM is stable inside the quiet window;
            // reject stores, CP0 and anything outside the pure ALU/load subset.
            uint inputs = 0, written = 0;
            foreach (uint word in words)
            {
                var d = new OpcodeTable.OpcodeDesc(word);
                int kind = GetCpuBlockOpcodeKind(word);
                uint reads, writes;
                if (kind == 4 || kind == 5)
                {
                    reads = (1u << d.op1) | (1u << d.op2); writes = 0;
                }
                else if (kind == 6 || kind == 7 || kind == 128 || kind == 129)
                {
                    reads = 1u << d.op1; writes = 0;
                }
                else if ((kind >= 9 && kind <= 15) || kind == 25
                    || kind == 32 || kind == 33 || kind == 35 || kind == 36
                    || kind == 37 || kind == 39 || kind == 55)
                {
                    reads = kind == 15 ? 0 : 1u << d.op1;
                    writes = 1u << d.op2;
                }
                else if (kind >= 64 && kind < 128 && kind != 72 && kind != 73)
                {
                    reads = 1u << d.op2;
                    if (kind != 64 && kind != 66 && kind != 67 && kind < 120)
                        reads |= 1u << d.op1;
                    writes = 1u << d.op3;
                }
                else return false;
                // r0 is normalized before every instruction, including delay
                // slots. A discarded write to it is never a loop dependency.
                inputs |= reads & ~written & ~1u;
                written |= writes & ~1u;
            }
            return (inputs & written) == 0;
        }

        private static bool IsCpuJitBackedge(uint start, uint pc, uint word)
        {
            int kind = GetCpuBlockOpcodeKind(word);
            return ((kind >= 4 && kind <= 7) || kind == 128 || kind == 129)
                && unchecked(pc + 4u + (uint)((int)(short)word << 2)) == start;
        }

        private static void ExtendInvariantCpuJitLoop(uint start, List<uint> words)
        {
            int length = words.Count;
            if (length < 3 || !IsCpuJitBackedge(start, start + (uint)(length - 2) * 4, words[length - 2])
                || !IsInvariantCpuJitLoop(words)) return;
            // A poll may check several independent RAM flags before repeating.
            // Extend only through pure instructions to another backedge to the
            // same entry, proving every possible taken prefix independently.
            var candidate = new List<uint>(words);
            for (int i = length; i < 16; i++)
            {
                uint pc = start + (uint)i * 4;
                if (!memory.TryReadRdramUInt32PhysicalFast(pc & 0x1fffffffu, out uint word)
                    || IsExistingLoopEntry(pc, word)) break;
                int kind = GetCpuBlockOpcodeKind(word);
                if (kind < 0 || CpuJitStore(kind) || kind == 16 || kind == 144) break;
                if (!CpuJitBranch(kind)) { candidate.Add(word); continue; }
                if (i == 15 || !IsCpuJitBackedge(start, pc, word)
                    || !memory.TryReadRdramUInt32PhysicalFast((pc + 4) & 0x1fffffffu, out uint delay)) break;
                int dk = GetCpuBlockOpcodeKind(delay);
                if (dk < 0 || CpuJitBranch(dk) || CpuJitStore(dk) || dk == 16 || dk == 144) break;
                candidate.Add(word); candidate.Add(delay);
                if (!IsInvariantCpuJitLoop(candidate)) break;
                words.AddRange(candidate.GetRange(words.Count, candidate.Count - words.Count));
                i++;
            }
        }

        private static Func<uint, uint, bool, uint> CompileCpuJit(uint start)
        {
            var words = new List<uint>();
            for (int i = 0; i < 16; i++)
            {
                uint pc = start + (uint)i * 4;
                if (!memory.TryReadRdramUInt32PhysicalFast(pc & 0x1fffffffu, out uint word)) break;
                int kind = GetCpuBlockOpcodeKind(word);
                if (kind < 0 || (i != 0 && IsExistingLoopEntry(pc, word))) break;
                if (CpuJitBranch(kind))
                {
                    if (i == 15 || !memory.TryReadRdramUInt32PhysicalFast((pc + 4) & 0x1fffffffu, out uint delay)) break;
                    int dk = GetCpuBlockOpcodeKind(delay);
                    if (dk < 0 || CpuJitBranch(dk)) break;
                    words.Add(word); words.Add(delay);
                    break;
                }
                words.Add(word);
                if (CpuJitStore(kind)) break;
            }
            if (words.Count < 2) return null;
            ExtendInvariantCpuJitLoop(start, words);
            var key = new System.Text.StringBuilder(start.ToString("x8"));
            foreach (uint word in words) key.Append(':').Append(word.ToString("x8"));
            string identity = key.ToString();
            if (CpuJitVersions.TryGetValue(identity, out var existing)) return existing.Run;
            if (CpuJitVersions.Count >= 128) return null;
            if (CpuJitSynchronous)
            {
                var direct = BuildCpuJit(start, words);
                CpuJitVersions.Add(identity, new CpuJitCode { Run = direct });
                CpuJitCompilations++;
                return direct;
            }
            if (Interlocked.CompareExchange(ref CpuJitCompilePending, 1, 0) != 0) return null;
            var holder = new CpuJitCode();
            CpuJitVersions.Add(identity, holder);
            CpuJitCompilations++;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { holder.Run = BuildCpuJit(start, words); }
                catch (PlatformNotSupportedException) { CpuJitUnavailable = true; }
                catch (Exception ex)
                {
                    CpuJitUnavailable = true;
                    Common.Logger.PrintWarningLine("[N64CPUJIT] compilation disabled: " + ex.Message);
                }
                finally { Volatile.Write(ref CpuJitCompilePending, 0); }
            });
            return null;
        }

        private static readonly FieldInfo JitMemoryField = typeof(R4300).GetField(nameof(memory));
        private static readonly FieldInfo JitRamField = typeof(Memory).GetField(nameof(Memory.RDRAM));
        private static readonly FieldInfo JitRegsField = typeof(Registers.R4300).GetField(nameof(Registers.R4300.Reg));
        private static readonly FieldInfo JitPcField = typeof(Registers.R4300).GetField(nameof(Registers.R4300.PC));
        private static readonly ConstructorInfo JitDescConstructor = typeof(OpcodeTable.OpcodeDesc).GetConstructor(new[] { typeof(uint) });
        private static MethodInfo JitMethod(string name) => typeof(R4300).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo JitValidate = JitMethod(nameof(CanAccessCpuBlockOperand));
        private static readonly MethodInfo JitExecute = JitMethod(nameof(ExecuteCpuBlockInstruction));
        private static readonly MethodInfo JitHistory = JitMethod(nameof(RecordCpuJitHistory));
        private static readonly MethodInfo JitRead16 = JitMethod(nameof(ReadCpuJit16));
        private static readonly MethodInfo JitStore32 = typeof(Memory).GetMethod("WriteValidatedRdramUInt32", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo JitRead32 = JitMethod(nameof(ReadCpuJit32));
        private static readonly MethodInfo JitRead64 = JitMethod(nameof(ReadCpuJit64));
        private static readonly MethodInfo JitCode32 = typeof(BitConverter).GetMethod(nameof(BitConverter.ToUInt32), new[] { typeof(byte[]), typeof(int) });
        private static readonly MethodInfo JitCode64 = typeof(BitConverter).GetMethod(nameof(BitConverter.ToUInt64), new[] { typeof(byte[]), typeof(int) });

        // Build from copied instruction words only. The worker never reads or
        // writes live emulated state. The history pattern is the closed first argument.
        private static Func<uint, uint, bool, uint> BuildCpuJit(uint start, List<uint> words)
        {
            for (int i = 0; i < words.Count - 2; i++)
                if (CpuJitBranch(GetCpuBlockOpcodeKind(words[i])))
                    return BuildCpuJitPollingChain(start, words);
            int terminalKind = GetCpuBlockOpcodeKind(words[words.Count - 2]);
            uint branchPc = start + (uint)(words.Count - 2) * 4;
            uint branchWord = words[words.Count - 2];
            bool loop = words.Count >= 3 && ((terminalKind >= 4 && terminalKind <= 7) || terminalKind == 128 || terminalKind == 129)
                && unchecked(branchPc + 4u + (uint)((int)(short)branchWord << 2)) == start;
            foreach (uint word in words)
            {
                int kind = GetCpuBlockOpcodeKind(word);
                // No code writes or time-dependent CP0 reads/writes inside a
                // native backedge. Hardware cannot run within the quiet budget.
                if (CpuJitStore(kind) || kind == 16 || kind == 144) loop = false;
            }
            bool invariantLoop = loop && IsInvariantCpuJitLoop(words);
            var method = new DynamicMethod("N64_" + start.ToString("x8"), typeof(uint),
                new[] { typeof(RecentInst[]), typeof(uint), typeof(uint), typeof(bool) }, typeof(R4300).Module, true);
            var il = method.GetILGenerator();
            var regs = il.DeclareLocal(typeof(ulong[]));
            var ram = il.DeclareLocal(typeof(byte[]));
            var address = il.DeclareLocal(typeof(uint));
            var result = il.DeclareLocal(typeof(uint));
            var target = il.DeclareLocal(typeof(uint));
            var completed = il.DeclareLocal(typeof(uint));
            var body = il.DefineLabel();
            var zero = il.DefineLabel();
            var invalid = il.DefineLabel();
            var finish = il.DefineLabel();
            var ret = il.DefineLabel();
            void U(uint value) => il.Emit(OpCodes.Ldc_I4, unchecked((int)value));
            void Pc(uint value) { U(value); il.Emit(OpCodes.Stsfld, JitPcField); }
            void Exit(uint count) { U(count); if (loop) { il.Emit(OpCodes.Ldloc, completed); il.Emit(OpCodes.Add); } il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Br, finish); }
            void Desc(uint word) { U(word); il.Emit(OpCodes.Newobj, JitDescConstructor); }
            void Elapsed(int count) { il.Emit(OpCodes.Ldarg_2); U((uint)count); il.Emit(OpCodes.Add); }

            il.Emit(OpCodes.Ldarg_1); U((uint)words.Count); il.Emit(OpCodes.Blt_Un, zero);
            il.Emit(OpCodes.Ldsfld, JitMemoryField); il.Emit(OpCodes.Ldfld, JitRamField); il.Emit(OpCodes.Stloc, ram);
            il.Emit(OpCodes.Ldloc, ram); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4);
            U((start & 0x1fffffffu) + (uint)words.Count * 4); il.Emit(OpCodes.Blt_Un, invalid);
            for (int i = 0; i < words.Count; i += 2)
            {
                bool pair = i + 1 < words.Count;
                byte[] bytes = new byte[pair ? 8 : 4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(), words[i]);
                if (pair) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), words[i + 1]);
                il.Emit(OpCodes.Ldloc, ram); U((start & 0x1fffffffu) + (uint)i * 4);
                il.Emit(OpCodes.Call, pair ? JitCode64 : JitCode32);
                if (pair) il.Emit(OpCodes.Ldc_I8, unchecked((long)BitConverter.ToUInt64(bytes, 0)));
                else U(BitConverter.ToUInt32(bytes, 0));
                il.Emit(OpCodes.Bne_Un, invalid);
            }
            il.Emit(OpCodes.Ldsfld, JitRegsField); il.Emit(OpCodes.Stloc, regs);
            il.MarkLabel(body);
            for (int i = 0; i < words.Count; i++)
            {
                uint pc = start + (uint)i * 4, word = words[i];
                int kind = GetCpuBlockOpcodeKind(word);
                var desc = new OpcodeTable.OpcodeDesc(word);
                bool branch = CpuJitBranch(kind);
                bool directMemory = kind == 32 || kind == 33 || kind == 35 || kind == 36 || kind == 37 || kind == 39 || kind == 43 || kind == 55;
                uint delay = branch ? words[i + 1] : 0;
                int dk = branch ? GetCpuBlockOpcodeKind(delay) : -1;
                if (directMemory)
                {
                    uint width = kind == 55 ? 8u : kind == 32 || kind == 36 ? 1u : kind == 33 || kind == 37 ? 2u : 4u;
                    if (desc.op1 == 0) il.Emit(OpCodes.Ldc_I8, 0L);
                    else { il.Emit(OpCodes.Ldloc, regs); U(desc.op1); il.Emit(OpCodes.Ldelem_I8); }
                    il.Emit(OpCodes.Ldc_I8, (long)(short)desc.Imm); il.Emit(OpCodes.Add); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Stloc, address);
                    var badAddress = il.DefineLabel(); var validAddress = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, address); U(0xc0000000u | (width - 1)); il.Emit(OpCodes.And);
                    U(0x80000000u); il.Emit(OpCodes.Bne_Un, badAddress);
                    il.Emit(OpCodes.Ldloc, address); U(0x1fffffffu); il.Emit(OpCodes.And);
                    il.Emit(OpCodes.Ldloc, ram); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4); U(width); il.Emit(OpCodes.Sub);
                    il.Emit(OpCodes.Ble_Un, validAddress);
                    il.MarkLabel(badAddress); Pc(pc); Exit((uint)i);
                    il.MarkLabel(validAddress);
                }
                // ALU/NOP delay slots were validated at compilation and cannot
                // touch memory. Only emit an operand guard for a load/store.
                else if ((uint)((branch ? dk : kind) - 32) < 32)
                {
                    var valid = il.DefineLabel();
                    Desc(branch ? delay : word); U((uint)(branch ? dk : kind));
                    U(unchecked((uint)(kind == 3 ? 31 : kind == 73 ? (int)desc.op3 : -1))); U(pc + 8);
                    il.Emit(OpCodes.Call, JitValidate); il.Emit(OpCodes.Brtrue, valid);
                    Pc(pc); Exit((uint)i); il.MarkLabel(valid);
                }
                il.Emit(OpCodes.Ldloc, regs); U(0); il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Stelem_I8);
                if (branch)
                {
                    // Capture the target before link writes or delay-slot operands.
                    if (kind == 2 || kind == 3) U((pc & 0xf0000000u) | (desc.Target << 2));
                    else if (kind == 72 || kind == 73)
                    {
                        il.Emit(OpCodes.Ldloc, regs); U(desc.op1); il.Emit(OpCodes.Ldelem_I8); il.Emit(OpCodes.Conv_U4);
                    }
                    else
                    {
                        var taken = il.DefineLabel(); var selected = il.DefineLabel();
                        il.Emit(OpCodes.Ldloc, regs); U(desc.op1); il.Emit(OpCodes.Ldelem_I8);
                        if (kind == 4 || kind == 5)
                        {
                            il.Emit(OpCodes.Ldloc, regs); U(desc.op2); il.Emit(OpCodes.Ldelem_I8);
                            il.Emit(kind == 4 ? OpCodes.Beq : OpCodes.Bne_Un, taken);
                        }
                        else
                        {
                            il.Emit(OpCodes.Ldc_I8, 0L);
                            il.Emit(kind == 128 ? OpCodes.Blt : kind == 129 ? OpCodes.Bge
                                : kind == 6 ? OpCodes.Ble : OpCodes.Bgt, taken);
                        }
                        U(pc + 8); il.Emit(OpCodes.Br, selected);
                        il.MarkLabel(taken); U(unchecked(pc + 4u + (uint)((int)(short)desc.Imm << 2)));
                        il.MarkLabel(selected);
                    }
                    il.Emit(OpCodes.Stloc, target);
                    if (kind == 3 || kind == 73)
                    {
                        il.Emit(OpCodes.Ldloc, regs); U(kind == 3 ? 31u : desc.op3);
                        il.Emit(OpCodes.Ldc_I8, (long)unchecked((int)(pc + 8))); il.Emit(OpCodes.Stelem_I8);
                    }
                    il.Emit(OpCodes.Ldloc, regs); U(0); il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Stelem_I8);
                    if (!EmitCpuJitAlu(il, regs, new OpcodeTable.OpcodeDesc(delay), dk))
                    {
                        Pc(pc + 4); Desc(delay); U((uint)dk); Elapsed(i); il.Emit(OpCodes.Call, JitExecute);
                    }
                    il.Emit(OpCodes.Ldloc, target); il.Emit(OpCodes.Stsfld, JitPcField);
                    if (loop)
                    {
                        il.Emit(OpCodes.Ldloc, completed); U((uint)words.Count); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, completed);
                        var leave = il.DefineLabel();
                        il.Emit(OpCodes.Ldloc, target); U(start); il.Emit(OpCodes.Bne_Un, leave);
                        if (invariantLoop)
                        {
                            // The first iteration performed every code/address
                            // guard and established the final register values.
                            // Account for all complete identical iterations up
                            // to the existing device/COUNT boundary.
                            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_1);
                            U((uint)words.Count); il.Emit(OpCodes.Rem_Un); il.Emit(OpCodes.Sub);
                            il.Emit(OpCodes.Stloc, completed);
                        }
                        else
                        {
                            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, completed); il.Emit(OpCodes.Sub);
                            U((uint)words.Count); il.Emit(OpCodes.Bge_Un, body);
                        }
                        il.MarkLabel(leave); il.Emit(OpCodes.Ldloc, completed); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Br, finish);
                    }
                    else Exit((uint)words.Count);
                    break;
                }
                if (directMemory)
                {
                    if (kind == 43)
                    {
                        il.Emit(OpCodes.Ldsfld, JitMemoryField);
                        il.Emit(OpCodes.Ldloc, address); U(0x1fffffffu); il.Emit(OpCodes.And);
                        il.Emit(OpCodes.Ldloc, regs); U(desc.op2); il.Emit(OpCodes.Ldelem_I8); il.Emit(OpCodes.Conv_U4);
                        il.Emit(OpCodes.Call, JitStore32);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldloc, regs); U(desc.op2);
                        il.Emit(OpCodes.Ldloc, ram); il.Emit(OpCodes.Ldloc, address); U(0x1fffffffu); il.Emit(OpCodes.And);
                        if (kind == 32 || kind == 36)
                        {
                            il.Emit(OpCodes.Ldelem_U1);
                            if (kind == 32) { il.Emit(OpCodes.Conv_I1); il.Emit(OpCodes.Conv_I8); }
                            else il.Emit(OpCodes.Conv_U8);
                        }
                        else
                        {
                            il.Emit(OpCodes.Call, kind == 55 ? JitRead64 : kind == 33 || kind == 37 ? JitRead16 : JitRead32);
                            if (kind == 33) { il.Emit(OpCodes.Conv_I2); il.Emit(OpCodes.Conv_I8); }
                            else if (kind == 39) { il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Conv_U8); }
                        }
                        il.Emit(OpCodes.Stelem_I8);
                    }
                }
                else if (!EmitCpuJitAlu(il, regs, desc, kind))
                {
                    Pc(pc); Desc(word); U((uint)kind); Elapsed(i); il.Emit(OpCodes.Call, JitExecute);
                }
            }
            Pc(start + (uint)words.Count * 4); U((uint)words.Count); il.Emit(OpCodes.Stloc, result);
            il.MarkLabel(finish);
            il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Brfalse, ret);
            il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Brfalse, ret);
            bool terminalBranch = CpuJitBranch(terminalKind);
            int period = words.Count - (terminalBranch ? 1 : 0);
            il.Emit(OpCodes.Ldarg_0); U((uint)period); il.Emit(OpCodes.Ldloc, result);
            if (terminalBranch)
            {
                // One unrecorded delay slot per complete iteration, including
                // a non-looping branch or a prefix before a failed load guard.
                il.Emit(OpCodes.Ldloc, result); U((uint)words.Count); il.Emit(OpCodes.Div_Un); il.Emit(OpCodes.Sub);
            }
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, JitHistory);
            il.MarkLabel(ret); il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
            il.MarkLabel(zero); U(0); il.Emit(OpCodes.Ret);
            il.MarkLabel(invalid); U(uint.MaxValue); il.Emit(OpCodes.Ret);
            var history = new RecentInst[loop ? RecentInstHistorySize + period - 1 : period];
            for (int i = 0; i < history.Length; i++)
                history[i] = new RecentInst { Pc = start + (uint)(i % period) * 4, Op = words[i % period] };
            var compiled = (Func<uint, uint, bool, uint>)method.CreateDelegate(typeof(Func<uint, uint, bool, uint>), history);
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(compiled);
            return compiled;
        }

        private static Func<uint, uint, bool, uint> BuildCpuJitPollingChain(uint start, List<uint> words)
        {
            var runs = new List<Func<uint, uint, bool, uint>>();
            var lengths = new List<uint>();
            var patterns = new List<RecentInst[]>();
            var entries = new List<RecentInst>();
            var prefixEntries = new uint[words.Count + 1];
            byte[] code = new byte[words.Count * 4];
            for (int i = 0; i < words.Count; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(code.AsSpan(i * 4), words[i]);
            int begin = 0;
            for (int i = 0; i < words.Count; i++)
            {
                entries.Add(new RecentInst { Pc = start + (uint)i * 4, Op = words[i] });
                prefixEntries[i + 1] = (uint)entries.Count;
                if (!CpuJitBranch(GetCpuBlockOpcodeKind(words[i]))) continue;
                i++; // Branch delay slots are executed but not recorded.
                prefixEntries[i + 1] = (uint)entries.Count;
                int length = i + 1 - begin;
                runs.Add(BuildCpuJit(start + (uint)begin * 4, words.GetRange(begin, length)));
                lengths.Add((uint)length);
                var pattern = new RecentInst[RecentInstHistorySize + entries.Count - 1];
                for (int h = 0; h < pattern.Length; h++) pattern[h] = entries[h % entries.Count];
                patterns.Add(pattern);
                begin = i + 1;
            }
            var native = runs.ToArray();
            var sizes = lengths.ToArray();
            var history = patterns.ToArray();
            int physical = (int)(start & 0x1fffffffu);
            Func<uint, uint, bool, uint> run = (budget, elapsed, record) =>
            {
                // Short windows retain the ordinary first-block behavior.
                if (budget < words.Count) return native[0](budget, elapsed, record);
                var ram = memory.RDRAM;
                if ((ulong)physical + (uint)code.Length > (ulong)ram.Length
                    || !ram.AsSpan(physical, code.Length).SequenceEqual(code)) return uint.MaxValue;
                uint done = 0;
                for (int i = 0; i < native.Length; i++)
                {
                    uint n = native[i](sizes[i], elapsed + done, false);
                    if (n == uint.MaxValue)
                    {
                        if (done == 0) return n;
                        break;
                    }
                    done += n;
                    if (n != sizes[i]) break; // A load guard left a safe prefix.
                    if (Registers.R4300.PC != start) continue;
                    uint repetitions = budget / done;
                    if (record)
                        RecordCpuJitHistory(history[i], (int)prefixEntries[done], repetitions * prefixEntries[done], elapsed);
                    return repetitions * done;
                }
                if (record && done != 0)
                    RecordCpuJitHistory(history[history.Length - 1], entries.Count, prefixEntries[done], elapsed);
                return done;
            };
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(run);
            return run;
        }

        private static bool EmitCpuJitAlu(ILGenerator il, LocalBuilder regs, OpcodeTable.OpcodeDesc d, int kind)
        {
            // r0 was normalized at the instruction boundary; NOP has no other effect.
            if (d.Opcode == 0) return true;
            if (!((kind >= 9 && kind <= 15) || kind == 25 || (kind >= 64 && kind < 128 && kind != 72 && kind != 73))) return false;
            void R(int index) { il.Emit(OpCodes.Ldloc, regs); il.Emit(OpCodes.Ldc_I4, index); il.Emit(OpCodes.Ldelem_I8); }
            void Sx() { il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Conv_I8); }
            void Pair() { R(d.op1); R(d.op2); }
            il.Emit(OpCodes.Ldloc, regs); il.Emit(OpCodes.Ldc_I4, kind < 64 ? (int)d.op2 : d.op3);
            switch (kind)
            {
                case 9:
                    R(d.op1); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Ldc_I4, (int)(short)d.Imm); il.Emit(OpCodes.Add); Sx(); break;
                case 10: case 11:
                    R(d.op1); il.Emit(OpCodes.Ldc_I8, (long)(short)d.Imm); il.Emit(kind == 10 ? OpCodes.Clt : OpCodes.Clt_Un); il.Emit(OpCodes.Conv_U8); break;
                case 12: case 13: case 14:
                    R(d.op1); il.Emit(OpCodes.Ldc_I8, (long)d.Imm); il.Emit(kind == 12 ? OpCodes.And : kind == 13 ? OpCodes.Or : OpCodes.Xor); break;
                case 15:
                    il.Emit(OpCodes.Ldc_I8, (long)unchecked((int)((uint)d.Imm << 16))); break;
                case 25:
                    R(d.op1); il.Emit(OpCodes.Ldc_I8, (long)(short)d.Imm); il.Emit(OpCodes.Add); break;
                case 97: case 99:
                    R(d.op1); il.Emit(OpCodes.Conv_U4); R(d.op2); il.Emit(OpCodes.Conv_U4); il.Emit(kind == 97 ? OpCodes.Add : OpCodes.Sub); Sx(); break;
                case 100: case 101: case 102: case 103:
                    Pair(); il.Emit(kind == 100 ? OpCodes.And : kind == 102 ? OpCodes.Xor : OpCodes.Or); if (kind == 103) il.Emit(OpCodes.Not); break;
                case 106: case 107:
                    Pair(); il.Emit(kind == 106 ? OpCodes.Clt : OpCodes.Clt_Un); il.Emit(OpCodes.Conv_U8); break;
                case 109: case 111:
                    Pair(); il.Emit(kind == 109 ? OpCodes.Add : OpCodes.Sub); break;
                case 64: case 66: case 67: case 68: case 70: case 71:
                    R(d.op2); il.Emit(OpCodes.Conv_U4);
                    if (kind < 68) il.Emit(OpCodes.Ldc_I4, (int)d.op4);
                    else { R(d.op1); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); }
                    il.Emit(kind == 64 || kind == 68 ? OpCodes.Shl : kind == 67 || kind == 71 ? OpCodes.Shr : OpCodes.Shr_Un); Sx(); break;
                case 84: case 86: case 87: case 120: case 122: case 123: case 124: case 126: case 127:
                    R(d.op2);
                    if (kind >= 120) il.Emit(OpCodes.Ldc_I4, d.op4 + (kind >= 124 ? 32 : 0));
                    else { R(d.op1); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4, 63); il.Emit(OpCodes.And); }
                    il.Emit(kind == 84 || kind == 120 || kind == 124 ? OpCodes.Shl : kind == 87 || kind == 123 || kind == 127 ? OpCodes.Shr : OpCodes.Shr_Un); break;
                default: throw new InvalidOperationException("CPU JIT ALU decoder mismatch");
            }
            il.Emit(OpCodes.Stelem_I8);
            return true;
        }
    }
}
