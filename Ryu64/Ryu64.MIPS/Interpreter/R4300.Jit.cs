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
        // Default to the measured CACHE-entry path; broad mapped admission and
        // mapped loads remain separate experiments, not implied by this switch.
        private static readonly bool CpuJitMappedEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED") != "0";
        private static readonly bool CpuJitMappedAdmission = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_ADMISSION") != "0";
        private static readonly bool CpuJitMappedLoads = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS") == "1";
        private static readonly bool CpuJitMappedCache = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE") != "0";
        private static readonly bool CpuJitMappedCacheOnly = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE_ONLY") != "0";
        private const uint CpuJitMaximumInstructions = 512;
        private const int CpuJitMaximumVersions = 1024;
        private sealed class CpuJitEntry
        {
            internal uint Pc, First;
            internal int Hits;
            internal CpuJitCode Code;
        }
        private static readonly CpuJitEntry[] CpuJitCache = new CpuJitEntry[65536];
        private static readonly Dictionary<uint, CpuJitEntry> CpuJitEntries = new Dictionary<uint, CpuJitEntry>();
        private sealed class CpuJitCode
        {
            internal volatile Func<uint, uint, bool, uint> Run;
            internal ulong LastUsed;
            internal bool Retired;
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
#if N64_CPU_JIT_PROFILE
            ProfileCpuJit(pc, "attempt", 1);
#endif
            if (!CpuJitEnabled || CpuJitUnavailable || budget < 2) return 0;
            int slot = (int)((pc >> 2) & (CpuJitCache.Length - 1));
            var entry = CpuJitCache[slot];
            if (entry == null || entry.Pc != pc)
            {
                if (!CpuJitEntries.TryGetValue(pc, out entry))
                {
                    entry = new CpuJitEntry { Pc = pc, First = word };
                    // The bounded secondary lookup must not permanently bar
                    // code first reached later in a game. The direct cache is
                    // also bounded and can still learn those hot addresses.
                    if (CpuJitEntries.Count < 8192) CpuJitEntries.Add(pc, entry);
                }
                CpuJitCache[slot] = entry;
            }
            if (entry.First != word)
            {
                CpuJitInvalidations++;
                entry.First = word;
                entry.Code = null;
                entry.Hits = 0;
            }
            var code = entry.Code;
            var run = code?.Run;
            if (run == null)
            {
                if (code != null && !code.Retired) return 0; // Worker still compiling.
                if (++entry.Hits != CpuJitHotThreshold) return 0;
                entry.Hits = 0;
                try { entry.Code = code = FindOrCompileCpuJit(pc); run = code?.Run; }
                catch (PlatformNotSupportedException) { CpuJitUnavailable = true; return 0; }
                if (run == null)
                {
                    if (code == null)
                    {
                        CpuJitRejectedCompilations++;
                        entry.Hits = -1024;
                    }
                    return 0;
                }
            }
            uint result = run(budget, elapsed, history);
            if (result == uint.MaxValue)
            {
                CpuJitInvalidations++;
                entry.Code = null;
                entry.Hits = 0;
                return 0;
            }
            CpuJitInstructions += result;
            if (result != 0) code.LastUsed = CycleCounter;
#if N64_CPU_JIT_PROFILE
            ProfileCpuJit(pc, "executions", result == 0 ? 0 : 1);
            ProfileCpuJit(pc, "instructions", result);
            if (result == 0) ProfileCpuJit(pc, "guardExit", 1);
#endif
            return result;
        }

        // Mapped code used to pay translation and quiet-budget discovery on
        // every interpreted instruction. Warm its existing bounded JIT cache
        // first; only a published delegate can justify that execution setup.
        private static bool PrepareMappedCpuJit(uint pc, uint word, int kind)
        {
            if (!CpuJitEnabled || CpuJitUnavailable || kind == 16 || kind == 144 || CpuJitStore(kind)) return false;
            if ((kind == 17 || (uint)(kind - 32) < 32)
                && !(CpuJitMappedLoads && IsCpuJitIntegerLoad(kind))
                && !CanAccessCpuBlockOperand(new OpcodeTable.OpcodeDesc(word), kind, -1, 0)) return false;
            int slot = (int)((pc >> 2) & (CpuJitCache.Length - 1));
            var entry = CpuJitCache[slot];
            if (entry == null || entry.Pc != pc)
            {
                if (!CpuJitEntries.TryGetValue(pc, out entry))
                {
                    entry = new CpuJitEntry { Pc = pc, First = word };
                    // The bounded secondary lookup must not permanently bar
                    // code first reached later in a game. The direct cache is
                    // also bounded and can still learn those hot addresses.
                    if (CpuJitEntries.Count < 8192) CpuJitEntries.Add(pc, entry);
                }
                CpuJitCache[slot] = entry;
            }
            if (entry.First != word)
            {
                CpuJitInvalidations++;
                entry.First = word;
                entry.Code = null;
                entry.Hits = 0;
            }
            var code = entry.Code;
            var run = code?.Run;
            if (run == null)
            {
                if (code != null && !code.Retired) return false; // Worker still compiling.
                if (++entry.Hits != CpuJitHotThreshold) return false;
                entry.Hits = 0;
                try { entry.Code = code = FindOrCompileCpuJit(pc); run = code?.Run; }
                catch (PlatformNotSupportedException) { CpuJitUnavailable = true; return false; }
                if (run == null)
                {
                    if (code == null)
                    {
                        CpuJitRejectedCompilations++;
                        entry.Hits = -1024;
                    }
                    return false;
                }
            }
            return true;
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
        private static bool CpuJitStore(int kind) => kind == 40 || kind == 41 || kind == 43 || kind == 57 || kind == 61 || kind == 63;

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
                else if (kind == 80 || kind == 82)
                {
                    // HI/LO cannot change inside this pure ALU/load subset;
                    // their writers and multiply/divide remain block boundaries.
                    reads = 0; writes = 1u << d.op3;
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
            => FindOrCompileCpuJit(start)?.Run;

        private static bool MakeRoomForCpuJit()
        {
            if (CpuJitVersions.Count < CpuJitMaximumVersions) return true;
            // Keep the native-code cap. Reuse space only after a completed
            // block has gone unused for roughly one emulated second, allowing
            // scene code to replace cold startup routines without rapid churn.
            const ulong idleCycles = 1UL << 27;
            string oldestKey = null;
            CpuJitCode oldest = null;
            foreach (var version in CpuJitVersions)
            {
                var code = version.Value;
                if (code.Run == null || CycleCounter < code.LastUsed || CycleCounter - code.LastUsed < idleCycles) continue;
                if (oldest == null || code.LastUsed < oldest.LastUsed)
                { oldestKey = version.Key; oldest = code; }
            }
            if (oldest == null) return false;
            CpuJitVersions.Remove(oldestKey);
            oldest.Retired = true;
            oldest.Run = null; // All address entries share this holder.
            return true;
        }

        private static CpuJitCode FindOrCompileCpuJit(uint start)
        {
            bool mapped = start < 0x80000000u || start >= 0xc0000000u;
            uint physical = start & 0x1fffffffu;
            uint asid = (uint)Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] & 255;
            if (mapped && (!CpuJitMappedEnabled || !TryResolveMappedCode(start, out physical))) return null;
            var words = new List<uint>();
            int maximumWords = mapped ? Math.Min(16, (int)((4096 - (start & 4095)) / 4)) : 16;
            for (int i = 0; i < maximumWords; i++)
            {
                uint pc = start + (uint)i * 4;
                if (!memory.TryReadRdramUInt32PhysicalFast(physical + (uint)i * 4, out uint word)) break;
                int kind = GetCpuJitOpcodeKind(word, mapped);
                if (kind < 0 || (mapped && (kind == 16 || kind == 144))
                    || (i != 0 && !mapped && IsExistingLoopEntry(pc, word))) break;
                if (CpuJitBranch(kind))
                {
                    if (i + 1 == maximumWords || !memory.TryReadRdramUInt32PhysicalFast(physical + (uint)(i + 1) * 4, out uint delay)) break;
                    int dk = GetCpuBlockOpcodeKind(delay);
                    if (dk < 0 || CpuJitBranch(dk) || (mapped && (dk == 16 || dk == 144))) break;
                    words.Add(word); words.Add(delay);
                    break;
                }
                words.Add(word);
                if (CpuJitStore(kind)) break;
            }
            if (words.Count < 2)
            {
#if N64_CPU_JIT_PROFILE
                ProfileCpuJit(start, "shortOrUnsupported", 1);
#endif
                return null;
            }
            if (!mapped) ExtendInvariantCpuJitLoop(start, words);
            var key = new System.Text.StringBuilder(start.ToString("x8"));
            if (mapped) key.Append("@m:").Append(physical).Append(':').Append(asid);
            foreach (uint word in words) key.Append(':').Append(word.ToString("x8"));
            string identity = key.ToString();
            if (CpuJitVersions.TryGetValue(identity, out var existing)) return existing;
            if (CpuJitSynchronous)
            {
                if (!MakeRoomForCpuJit()) return null;
                var direct = BuildCpuJitResolved(start, words, physical, mapped, asid);
                var ready = new CpuJitCode { Run = direct, LastUsed = CycleCounter };
                CpuJitVersions.Add(identity, ready);
                CpuJitCompilations++;
                return ready;
            }
            if (Interlocked.CompareExchange(ref CpuJitCompilePending, 1, 0) != 0)
            {
#if N64_CPU_JIT_PROFILE
                ProfileCpuJit(start, "workerBusy", 1);
#endif
                return null;
            }
            if (!MakeRoomForCpuJit())
            {
                Volatile.Write(ref CpuJitCompilePending, 0);
#if N64_CPU_JIT_PROFILE
                ProfileCpuJit(start, "versionLimit", 1);
#endif
                return null;
            }
            var holder = new CpuJitCode { LastUsed = CycleCounter };
            CpuJitVersions.Add(identity, holder);
            CpuJitCompilations++;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { holder.Run = BuildCpuJitResolved(start, words, physical, mapped, asid); }
                catch (PlatformNotSupportedException) { CpuJitUnavailable = true; }
                catch (Exception ex)
                {
                    CpuJitUnavailable = true;
                    Common.Logger.PrintWarningLine("[N64CPUJIT] compilation disabled: " + ex.Message);
                }
                finally { Volatile.Write(ref CpuJitCompilePending, 0); }
            });
            return holder;
        }

        private static int GetCpuJitOpcodeKind(uint word, bool mapped)
            => mapped && CpuJitMappedCache && (word >> 26) == 47 ? 47 : GetCpuBlockOpcodeKind(word);

        private static bool IsCpuJitIntegerLoad(int kind)
            => kind == 32 || kind == 33 || kind == 35 || kind == 36 || kind == 37 || kind == 39 || kind == 55;

        // Resolve before touching guest state. The caller falls back at the
        // original virtual PC on alignment, TLB or non-RAM failure. An aligned
        // integer operand cannot cross a 4 KiB translation subpage.
        private static bool TryResolveCpuJitLoad(uint address, uint width, out uint physical)
        {
            physical = 0;
            if ((address & (width - 1)) != 0) return false;
            if (address >= 0x80000000u && address < 0xc0000000u)
                physical = address & 0x1fffffffu;
            else
            {
                try { physical = TLB.TranslateAddress(address, true) & 0x1fffffffu; }
                catch (Common.Exceptions.TLBMissException) { return false; }
            }
            return (ulong)physical + width <= (ulong)memory.RDRAM.Length;
        }

        // Strict fetch translation: no low-physical bring-up fallback for compiled code.
        private static bool TryResolveMappedCode(uint pc, out uint physical)
        {
            physical = 0;
            if ((pc & 3) != 0) return false;
            try { physical = TLB.TranslateAddress(pc, true) & 0x1fffffffu; }
            catch (Common.Exceptions.TLBMissException) { return false; }
            return physical >= 0x4000 && (ulong)physical + 4 <= (ulong)memory.RDRAM.Length;
        }

        // Worker consumes only captured mapping identity and copied words. Validate
        // the live translation on every entry, so remaps/reset/load cannot run stale code.
        private static Func<uint, uint, bool, uint> BuildCpuJitResolved(uint start, List<uint> words,
            uint physical, bool mapped, uint asid)
        {
            var run = BuildCpuJitAt(start, words, physical);
            if (!mapped) return run;
            return (budget, elapsed, history) =>
                ((uint)Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] & 255) == asid
                && TryResolveMappedCode(start, out uint current) && current == physical
                    ? run(budget, elapsed, history) : uint.MaxValue;
        }

        private static readonly FieldInfo JitMemoryField = typeof(R4300).GetField(nameof(memory));
        private static readonly FieldInfo JitRamField = typeof(Memory).GetField(nameof(Memory.RDRAM));
        private static readonly FieldInfo JitRegsField = typeof(Registers.R4300).GetField(nameof(Registers.R4300.Reg));
        private static readonly FieldInfo JitPcField = typeof(Registers.R4300).GetField(nameof(Registers.R4300.PC));
        private static readonly FieldInfo JitHiField = typeof(Registers.R4300).GetField(nameof(Registers.R4300.HI));
        private static readonly FieldInfo JitLoField = typeof(Registers.R4300).GetField(nameof(Registers.R4300.LO));
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
            => BuildCpuJitAt(start, words, start & 0x1fffffffu);

        private static Func<uint, uint, bool, uint> BuildCpuJitAt(uint start, List<uint> words, uint physical)
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
                int kind = GetCpuJitOpcodeKind(word, start < 0x80000000u || start >= 0xc0000000u);
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
            var registers = new CpuJitRegisters(il, regs, words, loop);
            var body = il.DefineLabel();
            var zero = il.DefineLabel();
            var invalid = il.DefineLabel();
            var finish = il.DefineLabel();
            var ret = il.DefineLabel();
            void U(uint value) => il.Emit(OpCodes.Ldc_I4, unchecked((int)value));
            void Pc(uint value) { U(value); il.Emit(OpCodes.Stsfld, JitPcField); }
            void Exit(uint count) { U(count); if (loop) { il.Emit(OpCodes.Ldloc, completed); il.Emit(OpCodes.Add); } il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Br, registers.ExitLabel(finish)); }
            void Desc(uint word) { U(word); il.Emit(OpCodes.Newobj, JitDescConstructor); }
            void Elapsed(int count) { il.Emit(OpCodes.Ldarg_2); U((uint)count); il.Emit(OpCodes.Add); }

            il.Emit(OpCodes.Ldarg_1); U((uint)words.Count); il.Emit(OpCodes.Blt_Un, zero);
            il.Emit(OpCodes.Ldsfld, JitMemoryField); il.Emit(OpCodes.Ldfld, JitRamField); il.Emit(OpCodes.Stloc, ram);
#if N64_LIVE_GPU
            il.Emit(OpCodes.Ldsfld, JitMemoryField); U(physical); U((uint)words.Count * 4);
            il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.GpuBeforeRead)));
#endif
            il.Emit(OpCodes.Ldloc, ram); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4);
            U(physical + (uint)words.Count * 4); il.Emit(OpCodes.Blt_Un, invalid);
            for (int i = 0; i < words.Count; i += 2)
            {
                bool pair = i + 1 < words.Count;
                byte[] bytes = new byte[pair ? 8 : 4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(), words[i]);
                if (pair) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), words[i + 1]);
                il.Emit(OpCodes.Ldloc, ram); U(physical + (uint)i * 4);
                il.Emit(OpCodes.Call, pair ? JitCode64 : JitCode32);
                if (pair) il.Emit(OpCodes.Ldc_I8, unchecked((long)BitConverter.ToUInt64(bytes, 0)));
                else U(BitConverter.ToUInt32(bytes, 0));
                il.Emit(OpCodes.Bne_Un, invalid);
            }
            il.Emit(OpCodes.Ldsfld, JitRegsField); il.Emit(OpCodes.Stloc, regs);
            registers.Initialize();
            il.MarkLabel(body);
            for (int i = 0; i < words.Count; i++)
            {
                uint pc = start + (uint)i * 4, word = words[i];
                int kind = GetCpuJitOpcodeKind(word, start < 0x80000000u || start >= 0xc0000000u);
                var desc = new OpcodeTable.OpcodeDesc(word);
                bool branch = CpuJitBranch(kind);
                bool directMemory = kind == 32 || kind == 33 || kind == 35 || kind == 36 || kind == 37 || kind == 39 || kind == 43 || kind == 55;
                uint delay = branch ? words[i + 1] : 0;
                int dk = branch ? GetCpuBlockOpcodeKind(delay) : -1;
                if (directMemory)
                {
                    uint width = kind == 55 ? 8u : kind == 32 || kind == 36 ? 1u : kind == 33 || kind == 37 ? 2u : 4u;
                    if (desc.op1 == 0) il.Emit(OpCodes.Ldc_I8, 0L);
                    else registers.Read(desc.op1);
                    il.Emit(OpCodes.Ldc_I8, (long)(short)desc.Imm); il.Emit(OpCodes.Add); il.Emit(OpCodes.Conv_U4); il.Emit(OpCodes.Stloc, address);
                    var badAddress = il.DefineLabel(); var validAddress = il.DefineLabel();
                    if (CpuJitMappedLoads && (start < 0x80000000u || start >= 0xc0000000u) && IsCpuJitIntegerLoad(kind))
                    {
                        il.Emit(OpCodes.Ldloc, address); U(width); il.Emit(OpCodes.Ldloca, address);
                        il.Emit(OpCodes.Call, JitMethod(nameof(TryResolveCpuJitLoad)));
                        il.Emit(OpCodes.Brtrue, validAddress);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldloc, address); U(0xc0000000u | (width - 1)); il.Emit(OpCodes.And);
                        U(0x80000000u); il.Emit(OpCodes.Bne_Un, badAddress);
                        il.Emit(OpCodes.Ldloc, address); U(0x1fffffffu); il.Emit(OpCodes.And);
                        il.Emit(OpCodes.Ldloc, ram); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U4); U(width); il.Emit(OpCodes.Sub);
                        il.Emit(OpCodes.Ble_Un, validAddress);
                    }
                    il.MarkLabel(badAddress); Pc(pc); Exit((uint)i);
                    il.MarkLabel(validAddress);
                }
                // ALU/NOP delay slots were validated at compilation. COP1 also
                // needs a live CU1 check; loads/stores need an address guard.
                else if (kind != 47 && ((branch ? dk : kind) == 17 || (uint)((branch ? dk : kind) - 32) < 32))
                {
                    var valid = il.DefineLabel();
                    Desc(branch ? delay : word); U((uint)(branch ? dk : kind));
                    U(unchecked((uint)(kind == 3 ? 31 : kind == 73 ? (int)desc.op3 : -1))); U(pc + 8);
                    il.Emit(OpCodes.Call, JitValidate); il.Emit(OpCodes.Brtrue, valid);
                    Pc(pc); Exit((uint)i); il.MarkLabel(valid);
                }
                registers.NormalizeZero();
                if (branch)
                {
                    // Capture the target before link writes or delay-slot operands.
                    if (kind == 2 || kind == 3) U((pc & 0xf0000000u) | (desc.Target << 2));
                    else if (kind == 72 || kind == 73)
                    {
                        registers.Read(desc.op1); il.Emit(OpCodes.Conv_U4);
                    }
                    else
                    {
                        var taken = il.DefineLabel(); var selected = il.DefineLabel();
                        registers.Read(desc.op1);
                        if (kind == 4 || kind == 5)
                        {
                            registers.Read(desc.op2);
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
                        int link = kind == 3 ? 31 : desc.op3;
                        registers.BeginWrite(link);
                        il.Emit(OpCodes.Ldc_I8, (long)unchecked((int)(pc + 8))); registers.EndWrite(link);
                    }
                    registers.NormalizeZero();
                    if (!EmitCpuJitAlu(il, registers, new OpcodeTable.OpcodeDesc(delay), dk))
                    {
                        Pc(pc + 4);
                        if (!EmitCpuJitCop1(il, new OpcodeTable.OpcodeDesc(delay), dk))
                        { Desc(delay); U((uint)dk); Elapsed(i); il.Emit(OpCodes.Call, JitExecute); }
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
                        il.MarkLabel(leave); il.Emit(OpCodes.Ldloc, completed); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Br, registers.ExitLabel(finish));
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
                        registers.Read(desc.op2); il.Emit(OpCodes.Conv_U4);
                        il.Emit(OpCodes.Call, JitStore32);
                    }
                    else
                    {
#if N64_LIVE_GPU
                        il.Emit(OpCodes.Ldsfld, JitMemoryField);
                        il.Emit(OpCodes.Ldloc, address); U(0x1fffffffu); il.Emit(OpCodes.And);
                        U(kind == 55 ? 8u : kind == 32 || kind == 36 ? 1u : kind == 33 || kind == 37 ? 2u : 4u);
                        il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.GpuBeforeRead)));
#endif
                        registers.BeginWrite(desc.op2);
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
                        registers.EndWrite(desc.op2);
                    }
                }
                else if (kind == 47)
                {
                    // Preserve the interpreter's existing CACHE stub (PC only).
                    // Admission is restricted to opt-in mapped-code blocks.
                    Pc(pc + 4);
                }
                else if (!EmitCpuJitAlu(il, registers, desc, kind))
                {
                    Pc(pc);
                    if (!EmitCpuJitCop1(il, desc, kind))
                    { Desc(word); U((uint)kind); Elapsed(i); il.Emit(OpCodes.Call, JitExecute); }
                }
            }
            Pc(start + (uint)words.Count * 4); U((uint)words.Count); il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Br, registers.ExitLabel(finish));
            registers.EmitExits(finish);
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
#if N64_LIVE_GPU
                memory.GpuBeforeRead((uint)physical, (uint)code.Length);
#endif
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

        private static bool EmitCpuJitAlu(ILGenerator il, CpuJitRegisters regs, OpcodeTable.OpcodeDesc d, int kind)
        {
            // r0 was normalized at the instruction boundary; NOP has no other effect.
            if (d.Opcode == 0) return true;
            if (!IsCpuJitAlu(kind)) return false;
            void R(int index) => regs.Read(index);
            void Sx() { il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Conv_I8); }
            void Pair() { R(d.op1); R(d.op2); }
            int destination = kind < 64 ? d.op2 : d.op3;
            regs.BeginWrite(destination);
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
                case 80: case 82:
                    il.Emit(OpCodes.Ldsfld, kind == 80 ? JitHiField : JitLoField); break;
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
            regs.EndWrite(destination);
            return true;
        }
    }
}
