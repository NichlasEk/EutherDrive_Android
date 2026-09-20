using System;
using System.Buffers.Binary;

namespace Ryu64.MIPS
{
    public partial class R4300
    {
        // The dispatch key is the primary opcode, SPECIAL function + 64, or
        // 128/129 for BLTZ/BGEZ, or 144 for MTC0 STATUS.
        // Match the reserved bits used by OpcodeTable.
        // Trapping arithmetic, FPU, TLB operations and other CP0 writes stay
        // on the ordinary instruction path.
        private static readonly uint[] CpuBlockReservedBits = CreateCpuBlockReservedBits();

        private static uint[] CreateCpuBlockReservedBits()
        {
            var masks = new uint[128];
            for (int i = 0; i < masks.Length; i++) masks[i] = uint.MaxValue;
            foreach (int primary in new[] { 2,3,4,5,9,10,11,12,13,14,25,32,33,35,36,37,39,40,41,43,55,63 })
                masks[primary] = 0;
            masks[15] = 0x03e00000u;
            masks[6] = masks[7] = 0x001f0000u;
            foreach (int function in new[] { 0,2,3,56,58,59,60,62,63 })
                masks[64 + function] = 0x03e00000u;
            foreach (int function in new[] { 4,6,7,20,22,23,33,35,36,37,38,39,42,43,45,47 })
                masks[64 + function] = 0x000007c0u;
            masks[72] = 0x001fffc0u;
            masks[73] = 0x001f07c0u;
            return masks;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int GetCpuBlockOpcodeKind(uint opcode)
        {
            int primary = (int)(opcode >> 26);
            if (primary == 1)
            {
                uint condition = (opcode >> 16) & 31;
                return condition <= 1 ? 128 + (int)condition : -1;
            }
            if (primary == 16)
            {
                if ((opcode & 0x03e00000u) == 0) return 16;
                // STATUS alone cannot create a pending interrupt. The block
                // starts with no pending IP bits and ends before new events.
                return (opcode & 0x03e0f800u) == 0x00806000u ? 144 : -1;
            }
            int kind = primary == 0 ? 64 + (int)(opcode & 63) : primary;
            // Unsupported entries reject every nonzero encoding. The sole
            // zero encoding is SLL/NOP and has its own valid SPECIAL entry.
            return (opcode & CpuBlockReservedBits[kind]) == 0 ? kind : -1;
        }

        private static bool IsExistingLoopEntry(uint pc, uint opcode)
        {
            switch (opcode)
            {
                case 0:
                    // Low boot PCs are excluded by the block. Elsewhere a NOP
                    // can only enter the zero-loop shortcut after this branch.
                    return memory.TryReadRdramUInt32PhysicalFast((pc - 4) & 0x1fffffffu, out uint previous)
                        && previous == 0x1520fffbu;
                case 0x2129fff8: case 0x2529fff8: case 0xad000000: case 0xad000004:
                case 0x1520fffc: case 0x21080008: case 0x1520fffb:
                case 0x8c8b0004: case 0x24a50001: case 0x00ab082b:
                case 0x5420fffc: case 0xa0a00000: case 0x24420008: case 0x0043082b:
                case 0x24080000: case 0x24090000: case 0xac49fffc: case 0x1420fffa:
                case 0xac48fff8: case 0x01e4082a: case 0x0411ffff: case 0x1000ffff:
                case 0xafa40000: // Preserve the longer multiply leaf batch.
                    return true;
            }
            return false;
        }

        private static uint GetRandomAfterInstructions(uint done)
        {
            // Before any instruction, RANDOM may still contain an out-of-range
            // saved value. MFC0 must preserve it until a boundary has occurred.
            if (done == 0) return (uint)Registers.COP0.Reg[Registers.COP0.RANDOM_REG];
            uint random = (uint)Registers.COP0.Reg[Registers.COP0.RANDOM_REG] & 31;
            uint wired = (uint)Registers.COP0.Reg[Registers.COP0.WIRED_REG] & 31;
            // With no wired TLB entries, RANDOM is a 32-value down-counter.
            if (wired == 0) return unchecked(random - done) & 31u;
            uint firstRun = random > wired ? random - wired : 0;
            return done <= firstRun ? random - done
                : 31 - ((done - firstRun - 1) % (32 - wired));
        }

        private static void ExecuteCpuBlockInstruction(OpcodeTable.OpcodeDesc desc, int kind, uint elapsed)
        {
            switch (kind)
            {
                case 9: InstInterp.ADDIU(desc); break;
                case 10: InstInterp.SLTI(desc); break;
                case 11: InstInterp.SLTIU(desc); break;
                case 12: InstInterp.ANDI(desc); break;
                case 13: InstInterp.ORI(desc); break;
                case 14: InstInterp.XORI(desc); break;
                case 15: InstInterp.LUI(desc); break;
                case 16:
                    uint value;
                    if (desc.op3 == Registers.COP0.COUNT_REG)
                        value = (uint)((Count + elapsed) >> 1);
                    else if (desc.op3 == Registers.COP0.RANDOM_REG)
                        value = GetRandomAfterInstructions(elapsed);
                    else
                        value = (uint)Registers.COP0.Reg[desc.op3];
                    Registers.R4300.Reg[desc.op2] = unchecked((ulong)(long)(int)value);
                    Registers.R4300.PC += 4;
                    break;
                case 25: InstInterp.DADDIU(desc); break;
                case 32: InstInterp.LB(desc); break;
                case 33: InstInterp.LH(desc); break;
                case 36: InstInterp.LBU(desc); break;
                case 37: InstInterp.LHU(desc); break;
                case 39: InstInterp.LWU(desc); break;
                case 40: InstInterp.SB(desc); break;
                case 41: InstInterp.SH(desc); break;
                case 63: InstInterp.SD(desc); break;
                case 144: InstInterp.MTC0(desc); break;
                case 35:
                    Registers.R4300.Reg[desc.op2] = unchecked((ulong)(long)BinaryPrimitives.ReadInt32BigEndian(memory.RDRAM.AsSpan(CpuBlockRamAddress(desc), 4)));
                    Registers.R4300.PC += 4;
                    break;
                case 43:
                    memory.WriteValidatedRdramUInt32((uint)CpuBlockRamAddress(desc), (uint)Registers.R4300.Reg[desc.op2]);
                    Registers.R4300.PC += 4;
                    break;
                case 55:
                    Registers.R4300.Reg[desc.op2] = BinaryPrimitives.ReadUInt64BigEndian(memory.RDRAM.AsSpan(CpuBlockRamAddress(desc), 8));
                    Registers.R4300.PC += 4;
                    break;
                case 64: InstInterp.SLL(desc); break;
                case 66: InstInterp.SRL(desc); break;
                case 67: InstInterp.SRA(desc); break;
                case 68: InstInterp.SLLV(desc); break;
                case 70: InstInterp.SRLV(desc); break;
                case 71: InstInterp.SRAV(desc); break;
                case 84: InstInterp.DSLLV(desc); break;
                case 86: InstInterp.DSRLV(desc); break;
                case 87: InstInterp.DSRAV(desc); break;
                case 97: InstInterp.ADDU(desc); break;
                case 99: InstInterp.SUBU(desc); break;
                case 100: InstInterp.AND(desc); break;
                case 101: InstInterp.OR(desc); break;
                case 102: InstInterp.XOR(desc); break;
                case 103: InstInterp.NOR(desc); break;
                case 106: InstInterp.SLT(desc); break;
                case 107: InstInterp.SLTU(desc); break;
                case 109: InstInterp.DADDU(desc); break;
                case 111: InstInterp.DSUBU(desc); break;
                case 120: InstInterp.DSLL(desc); break;
                case 122: InstInterp.DSRL(desc); break;
                case 123: InstInterp.DSRA(desc); break;
                case 124: InstInterp.DSLL32(desc); break;
                case 126: InstInterp.DSRL32(desc); break;
                case 127: InstInterp.DSRA32(desc); break;
            }
        }

        // CanAccessCpuBlockOperand has checked direct RAM, alignment and the
        // entire access width. Resolve the address after branch link writes and
        // r0 normalization, just as the ordinary load handlers do.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int CpuBlockRamAddress(OpcodeTable.OpcodeDesc desc)
        {
            return (int)(unchecked((uint)(Registers.R4300.Reg[desc.op1]
                + (ulong)(long)(short)desc.Imm)) & 0x1fffffffu);
        }

        // The delay instruction is validated before any branch/link state changes.
        // Resolve operands before it, and write the link before its register reads,
        // exactly as the ordinary branch handlers do. Fetch the target live afterward.
        private static void ExecuteQuietBranch(OpcodeTable.OpcodeDesc desc, int kind, uint delayOpcode, int delayKind, uint elapsed)
        {
            uint branchPc = Registers.R4300.PC;
            uint target;
            if (kind == 2 || kind == 3)
                target = (branchPc & 0xf0000000u) | (desc.Target << 2);
            else if (kind == 72 || kind == 73)
                target = (uint)Registers.R4300.Reg[desc.op1];
            else
            {
                long value = (long)Registers.R4300.Reg[desc.op1];
                bool take = kind == 128 ? value < 0 : kind == 129 ? value >= 0
                    : kind == 6 ? value <= 0 : kind == 7 ? value > 0
                    : kind == 4 ? Registers.R4300.Reg[desc.op1] == Registers.R4300.Reg[desc.op2]
                    : Registers.R4300.Reg[desc.op1] != Registers.R4300.Reg[desc.op2];
                target = take ? unchecked(branchPc + 4u + (uint)((int)(short)desc.Imm << 2)) : branchPc + 8;
            }
            if (kind == 3 || kind == 73)
                Registers.R4300.Reg[kind == 3 ? 31 : desc.op3] = unchecked((ulong)(long)(int)(branchPc + 8));
            Registers.R4300.PC = branchPc + 4;
            Registers.R4300.Reg[0] = 0;
            ExecuteCpuBlockInstruction(new OpcodeTable.OpcodeDesc(delayOpcode), delayKind, elapsed);
            Registers.R4300.PC = target;
        }

        private static bool CanAccessCpuBlockOperand(OpcodeTable.OpcodeDesc desc, int kind, int linkRegister, uint linkValue)
        {
            uint width;
            switch (kind)
            {
                case 32: case 36: case 40: width = 1; break;
                case 33: case 37: case 41: width = 2; break;
                case 35: case 39: case 43: width = 4; break;
                case 55: case 63: width = 8; break;
                default: return true;
            }
            ulong baseValue = desc.op1 == 0 ? 0 : desc.op1 == linkRegister
                ? unchecked((ulong)(long)(int)linkValue) : Registers.R4300.Reg[desc.op1];
            uint address = unchecked((uint)(baseValue + (ulong)(long)(short)desc.Imm));
            return address >= 0x80000000u && address < 0xc0000000u
                && (address & (width - 1)) == 0
                && (ulong)(address & 0x1fffffffu) + width <= (ulong)memory.RDRAM.Length;
        }

        // A bounded interpreter for quiet intervals: no code cache and no
        // synthetic cycles. Fetch after every preceding store/branch, execute
        // validated RAM accesses or usual handlers, aggregating event-free clock boundaries.
        // MFC0 observes its precise intermediate Count/RANDOM, including the
        // delay-before-branch accounting order used by InterpretOpcode.
        // The caller has already recorded the first history entry.
        private static uint TryAdvanceCpuBlock(uint pc, uint opcode, uint maximumInstructions, bool recordHistory)
        {
            int kind = GetCpuBlockOpcodeKind(opcode);
            if (kind < 0 || !FastIdleLoop || maximumInstructions < 2 || CpuBatchTracingEnabled
                || Common.Variables.Debug || Common.Settings.STEP_MODE || CpuWindowTracingEnabled
                || TraceSm64DispatchWindow || TraceHotPcSamples || InstInterp.BranchTracingEnabled
                || _executingDelaySlot || _delaySlotExceptionPending || (pc & 3) != 0
                || pc < 0x80004000u || pc >= 0xc0000000u
                || memory.HasPendingRcpInterrupt
                || (Registers.COP0.Reg[Registers.COP0.CAUSE_REG] & CauseIpMask) != 0)
                return 0;
            uint physical = pc & 0x1fffffffu;
            if (physical < 0x4000 || (ulong)physical + 8 > (ulong)memory.RDRAM.Length)
                return 0;
            uint limit = memory.GetQuietCpuCycles(Math.Min(maximumInstructions, CpuJitEnabled ? CpuJitMaximumInstructions : 32u));
            ulong count = Registers.COP0.Reg[Registers.COP0.COUNT_REG];
            if (limit < 2 || count >= uint.MaxValue || (Count >> 1) != count
                || ((Count + limit) >> 1) >= uint.MaxValue
                || CountCompareReached((uint)count, (uint)((Count + limit) >> 1), (uint)Registers.COP0.Reg[Registers.COP0.COMPARE_REG]))
                return 0;
            uint done = 0;
            bool tryCompiled = true;
            while (done < limit)
            {
                uint compiled = tryCompiled ? TryRunCpuJit(pc, opcode, limit - done, done, recordHistory) : 0;
                if (compiled != 0)
                {
                    done += compiled;
                    // A terminal self-branch must keep the outer watchdog cadence.
                    uint terminalPc = pc + (compiled >= 2 ? compiled - 2 : 0) * 4;
                    if (Registers.R4300.PC == terminalPc) break;
                    pc = Registers.R4300.PC;
                    physical = pc & 0x1fffffffu;
                    if (done == limit || (pc & 3) != 0 || pc < 0x80000000u || pc >= 0xc0000000u
                        || physical < 0x4000 || !memory.TryReadRdramUInt32PhysicalFast(physical, out opcode)
                        || IsExistingLoopEntry(pc, opcode) || (kind = GetCpuBlockOpcodeKind(opcode)) < 0)
                        break;
                    continue;
                }
                if (done == 0) limit = Math.Min(limit, 32);
                tryCompiled = false;
                var desc = new OpcodeTable.OpcodeDesc(opcode);
                // Only load/store primary opcodes can access memory. Avoid a
                // validator call for arithmetic, CP0 and branch instructions.
                if ((uint)(kind - 32) < 32 && !CanAccessCpuBlockOperand(desc, kind, -1, 0)) break;
                bool branch = CpuJitBranch(kind);
                uint delayOpcode = 0;
                int delayKind = -1;
                if (branch && (done + 2 > limit
                    || !memory.TryReadRdramUInt32PhysicalFast(physical + 4, out delayOpcode)
                    || (delayKind = GetCpuBlockOpcodeKind(delayOpcode)) < 0
                    || CpuJitBranch(delayKind)
                    || !CanAccessCpuBlockOperand(new OpcodeTable.OpcodeDesc(delayOpcode), delayKind,
                        kind == 3 ? 31 : kind == 73 ? desc.op3 : -1, pc + 8)))
                    break;
                Registers.R4300.Reg[0] = 0;
                if (recordHistory && done != 0)
                {
                    _recentInst[_recentInstPos] = new RecentInst { Pc = pc, Op = opcode };
                    _recentInstPos = (_recentInstPos + 1) & RecentInstHistoryMask;
                }
                if (branch)
                {
                    ExecuteQuietBranch(desc, kind, delayOpcode, delayKind, done);
                    done += 2;
                    // A branch to itself remains one outer-loop iteration, so
                    // the existing stuck-PC watchdog keeps its exact cadence.
                    if (Registers.R4300.PC == pc) break;
                }
                else
                {
                    ExecuteCpuBlockInstruction(desc, kind, done);
                    done++;
                }
                pc = Registers.R4300.PC;
                physical = pc & 0x1fffffffu;
                if (done == limit || (pc & 3) != 0 || pc < 0x80000000u || pc >= 0xc0000000u
                    || physical < 0x4000 || !memory.TryReadRdramUInt32PhysicalFast(physical, out opcode)
                    || IsExistingLoopEntry(pc, opcode) || (kind = GetCpuBlockOpcodeKind(opcode)) < 0)
                    break;
            }
            if (done == 0) return 0;
            CycleCounter += done;
            Count += done;
            memory.TickQuietCpuCycles(done);
            Registers.COP0.Reg[Registers.COP0.COUNT_REG] = (uint)(Count >> 1);
            Registers.COP0.Reg[Registers.COP0.RANDOM_REG] = GetRandomAfterInstructions(done);
            Common.Measure.InstructionCount += done;
            Common.Measure.CycleCounter = CycleCounter;
            return done;
        }
    }
}
