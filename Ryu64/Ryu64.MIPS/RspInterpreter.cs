using System;

namespace Ryu64.MIPS
{
    internal sealed class RspInterpreter
    {
        private readonly Memory _memory;
        private readonly uint[] _gpr = new uint[32];
        private uint _pc;
        private bool _branchPending;
        private uint _branchTarget;
        private uint _hi;
        private uint _lo;

        public RspInterpreter(Memory memory)
        {
            _memory = memory;
        }

        public bool ExecuteTask(out uint executedInstructions, out string stopReason)
        {
            Array.Clear(_gpr, 0, _gpr.Length);
            _hi = 0;
            _lo = 0;
            _pc = _memory.ReadRspPc();
            _branchPending = false;
            _branchTarget = 0;
            stopReason = "max-instructions";

            for (executedInstructions = 0; executedInstructions < 200000; executedInstructions++)
            {
                uint pc = _pc & 0x0FFCu;
                uint instr = _memory.ReadSpImemWord(pc);
                uint sequentialPc = (pc + 4) & 0x0FFCu;
                bool branchDue = _branchPending;
                uint dueTarget = _branchTarget;
                _branchPending = false;

                if (!Step(pc, instr, out stopReason))
                {
                    _memory.WriteRspPc(_pc);
                    _gpr[0] = 0;
                    return stopReason == "break";
                }

                _gpr[0] = 0;

                _pc = branchDue ? (dueTarget & 0x0FFCu) : sequentialPc;
            }

            _memory.WriteRspPc(_pc);
            return false;
        }

        private bool Step(uint pc, uint instr, out string stopReason)
        {
            uint op = instr >> 26;
            uint rs = (instr >> 21) & 0x1F;
            uint rt = (instr >> 16) & 0x1F;
            uint rd = (instr >> 11) & 0x1F;
            uint sa = (instr >> 6) & 0x1F;
            uint funct = instr & 0x3F;
            short imm = unchecked((short)instr);
            uint uimm = instr & 0xFFFFu;
            uint target = instr & 0x03FFFFFFu;

            stopReason = string.Empty;

            switch (op)
            {
                case 0x00:
                    return ExecuteSpecial(pc, funct, rs, rt, rd, sa, out stopReason);
                case 0x01:
                    return ExecuteRegImm(pc, rt, rs, imm, out stopReason);
                case 0x02:
                    SetBranch(((pc + 4) & 0xF0000000u) | (target << 2));
                    return true;
                case 0x03:
                    WriteGpr(31, pc + 8);
                    SetBranch(((pc + 4) & 0xF0000000u) | (target << 2));
                    return true;
                case 0x04:
                    if (_gpr[rs] == _gpr[rt]) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x05:
                    if (_gpr[rs] != _gpr[rt]) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x06:
                    if ((int)_gpr[rs] <= 0) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x07:
                    if ((int)_gpr[rs] > 0) SetBranch(BranchTarget(pc, imm));
                    return true;
                case 0x08:
                case 0x09:
                    WriteGpr(rt, _gpr[rs] + (uint)(int)imm);
                    return true;
                case 0x0A:
                    WriteGpr(rt, (int)_gpr[rs] < imm ? 1u : 0u);
                    return true;
                case 0x0B:
                    WriteGpr(rt, _gpr[rs] < unchecked((uint)(int)imm) ? 1u : 0u);
                    return true;
                case 0x0C:
                    WriteGpr(rt, _gpr[rs] & uimm);
                    return true;
                case 0x0D:
                    WriteGpr(rt, _gpr[rs] | uimm);
                    return true;
                case 0x0E:
                    WriteGpr(rt, _gpr[rs] ^ uimm);
                    return true;
                case 0x0F:
                    WriteGpr(rt, uimm << 16);
                    return true;
                case 0x10:
                    return ExecuteCop0(pc, rs, rt, rd, out stopReason);
                case 0x20:
                    WriteGpr(rt, unchecked((uint)(int)(sbyte)ReadByte(_gpr[rs] + (uint)(int)imm)));
                    return true;
                case 0x21:
                    WriteGpr(rt, unchecked((uint)(int)(short)ReadHalf(_gpr[rs] + (uint)(int)imm)));
                    return true;
                case 0x23:
                    WriteGpr(rt, ReadWord(_gpr[rs] + (uint)(int)imm));
                    return true;
                case 0x24:
                    WriteGpr(rt, ReadByte(_gpr[rs] + (uint)(int)imm));
                    return true;
                case 0x25:
                    WriteGpr(rt, ReadHalf(_gpr[rs] + (uint)(int)imm));
                    return true;
                case 0x28:
                    WriteByte(_gpr[rs] + (uint)(int)imm, (byte)_gpr[rt]);
                    return true;
                case 0x29:
                    WriteHalf(_gpr[rs] + (uint)(int)imm, (ushort)_gpr[rt]);
                    return true;
                case 0x2B:
                    WriteWord(_gpr[rs] + (uint)(int)imm, _gpr[rt]);
                    return true;
                case 0x12:
                    stopReason = $"unsupported-cop2 pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
                case 0x32:
                case 0x3A:
                    stopReason = $"unsupported-vector-mem pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
                default:
                    stopReason = $"unsupported-op pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
            }
        }

        private bool ExecuteSpecial(uint pc, uint funct, uint rs, uint rt, uint rd, uint sa, out string stopReason)
        {
            stopReason = string.Empty;
            switch (funct)
            {
                case 0x00: WriteGpr(rd, _gpr[rt] << (int)sa); return true;
                case 0x02: WriteGpr(rd, _gpr[rt] >> (int)sa); return true;
                case 0x03: WriteGpr(rd, unchecked((uint)((int)_gpr[rt] >> (int)sa))); return true;
                case 0x04: WriteGpr(rd, _gpr[rt] << (int)(_gpr[rs] & 0x1F)); return true;
                case 0x06: WriteGpr(rd, _gpr[rt] >> (int)(_gpr[rs] & 0x1F)); return true;
                case 0x07: WriteGpr(rd, unchecked((uint)((int)_gpr[rt] >> (int)(_gpr[rs] & 0x1F)))); return true;
                case 0x08: SetBranch(_gpr[rs]); return true;
                case 0x09: WriteGpr(rd == 0 ? 31u : rd, pc + 8); SetBranch(_gpr[rs]); return true;
                case 0x0D: stopReason = "break"; return false;
                case 0x10: WriteGpr(rd, _hi); return true;
                case 0x12: WriteGpr(rd, _lo); return true;
                case 0x18:
                    {
                        long prod = (long)(int)_gpr[rs] * (long)(int)_gpr[rt];
                        _lo = (uint)prod;
                        _hi = (uint)(prod >> 32);
                        return true;
                    }
                case 0x19:
                    {
                        ulong prod = (ulong)_gpr[rs] * (ulong)_gpr[rt];
                        _lo = (uint)prod;
                        _hi = (uint)(prod >> 32);
                        return true;
                    }
                case 0x20:
                case 0x21: WriteGpr(rd, _gpr[rs] + _gpr[rt]); return true;
                case 0x22:
                case 0x23: WriteGpr(rd, _gpr[rs] - _gpr[rt]); return true;
                case 0x24: WriteGpr(rd, _gpr[rs] & _gpr[rt]); return true;
                case 0x25: WriteGpr(rd, _gpr[rs] | _gpr[rt]); return true;
                case 0x26: WriteGpr(rd, _gpr[rs] ^ _gpr[rt]); return true;
                case 0x27: WriteGpr(rd, ~(_gpr[rs] | _gpr[rt])); return true;
                case 0x2A: WriteGpr(rd, (int)_gpr[rs] < (int)_gpr[rt] ? 1u : 0u); return true;
                case 0x2B: WriteGpr(rd, _gpr[rs] < _gpr[rt] ? 1u : 0u); return true;
                default:
                    stopReason = $"unsupported-special pc=0x{pc:x3} funct=0x{funct:x2}";
                    return false;
            }
        }

        private bool ExecuteRegImm(uint pc, uint rt, uint rs, short imm, out string stopReason)
        {
            stopReason = string.Empty;
            switch (rt)
            {
                case 0x00: if ((int)_gpr[rs] < 0) SetBranch(BranchTarget(pc, imm)); return true;
                case 0x01: if ((int)_gpr[rs] >= 0) SetBranch(BranchTarget(pc, imm)); return true;
                default:
                    stopReason = $"unsupported-regimm pc=0x{pc:x3} rt=0x{rt:x2}";
                    return false;
            }
        }

        private bool ExecuteCop0(uint pc, uint rs, uint rt, uint rd, out string stopReason)
        {
            stopReason = string.Empty;
            switch (rs)
            {
                case 0x00:
                    WriteGpr(rt, _memory.ReadRspCp0((int)rd));
                    return true;
                case 0x04:
                    _memory.WriteRspCp0((int)rd, _gpr[rt]);
                    return true;
                default:
                    stopReason = $"unsupported-cop0 pc=0x{pc:x3} rs=0x{rs:x2}";
                    return false;
            }
        }

        private uint ReadWord(uint address)
        {
            return _memory.ReadSpDmemWord(address & 0x0FFFu);
        }

        private ushort ReadHalf(uint address)
        {
            uint word = ReadWord(address & ~3u);
            int shift = (int)((2 - ((address & 2u) >> 1)) * 16);
            return (ushort)((word >> shift) & 0xFFFFu);
        }

        private byte ReadByte(uint address)
        {
            uint word = ReadWord(address & ~3u);
            int shift = (int)((3 - (address & 3u)) * 8);
            return (byte)((word >> shift) & 0xFFu);
        }

        private void WriteWord(uint address, uint value)
        {
            _memory.WriteSpDmemWord(address & 0x0FFFu, value);
        }

        private void WriteHalf(uint address, ushort value)
        {
            uint aligned = address & ~3u;
            uint word = ReadWord(aligned);
            int shift = (int)((2 - ((address & 2u) >> 1)) * 16);
            uint mask = 0xFFFFu << shift;
            WriteWord(aligned, (word & ~mask) | ((uint)value << shift));
        }

        private void WriteByte(uint address, byte value)
        {
            uint aligned = address & ~3u;
            uint word = ReadWord(aligned);
            int shift = (int)((3 - (address & 3u)) * 8);
            uint mask = 0xFFu << shift;
            WriteWord(aligned, (word & ~mask) | ((uint)value << shift));
        }

        private void WriteGpr(uint reg, uint value)
        {
            if (reg != 0)
                _gpr[reg] = value;
        }

        private void SetBranch(uint target)
        {
            _branchPending = true;
            _branchTarget = target;
        }

        private static uint BranchTarget(uint pc, short imm)
        {
            return unchecked((uint)((int)(pc + 4) + (imm << 2)));
        }
    }
}
