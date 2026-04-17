using System;

namespace Ryu64.MIPS
{
    internal sealed class RspInterpreter
    {
        private readonly Memory _memory;
        private readonly uint[] _gpr = new uint[32];
        private readonly byte[,] _vr = new byte[32, 16];
        private readonly ushort[] _vcc = new ushort[2];
        private readonly ushort[] _vco = new ushort[2];
        private byte _vce;
        private uint _pc;
        private bool _branchPending;
        private uint _branchTarget;
        private uint _hi;
        private uint _lo;
        private uint _lastPc;
        private uint _lastInstr;
        private uint _samePcRunLength;

        public RspInterpreter(Memory memory)
        {
            _memory = memory;
        }

        public bool ExecuteTask(out uint executedInstructions, out string stopReason)
        {
            Array.Clear(_gpr, 0, _gpr.Length);
            Array.Clear(_vr, 0, _vr.Length);
            Array.Clear(_vcc, 0, _vcc.Length);
            Array.Clear(_vco, 0, _vco.Length);
            _vce = 0;
            _hi = 0;
            _lo = 0;
            _pc = _memory.ReadRspPc();
            _branchPending = false;
            _branchTarget = 0;
            _lastPc = 0xffffffffu;
            _lastInstr = 0;
            _samePcRunLength = 0;
            stopReason = "max-instructions";

            for (executedInstructions = 0; executedInstructions < 200000; executedInstructions++)
            {
                uint pc = _pc & 0x0FFCu;
                uint instr = _memory.ReadSpImemWord(pc);
                if (pc == _lastPc && instr == _lastInstr)
                {
                    _samePcRunLength++;
                    if (_samePcRunLength >= 65536)
                    {
                        stopReason = $"loop pc=0x{pc:x3} op=0x{instr:x8} repeats={_samePcRunLength}";
                        _memory.WriteRspPc(_pc);
                        _gpr[0] = 0;
                        return false;
                    }
                }
                else
                {
                    _lastPc = pc;
                    _lastInstr = instr;
                    _samePcRunLength = 0;
                }
                uint sequentialPc = (pc + 4) & 0x0FFCu;
                bool branchDue = _branchPending;
                uint dueTarget = _branchTarget;
                _branchPending = false;

                if (!Step(pc, instr, out stopReason))
                {
                    if (string.IsNullOrEmpty(stopReason))
                        stopReason = $"unknown-stop pc=0x{pc:x3} op=0x{instr:x8}";
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
                case 0x12:
                    return ExecuteCop2(pc, rs, rt, rd, instr, out stopReason);
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
                case 0x32:
                    return ExecuteVectorMemory(pc, true, rs, rt, instr, out stopReason);
                case 0x3A:
                    return ExecuteVectorMemory(pc, false, rs, rt, instr, out stopReason);
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

        private bool ExecuteCop2(uint pc, uint rs, uint rt, uint rd, uint instr, out string stopReason)
        {
            stopReason = string.Empty;
            uint element = (instr >> 7) & 0xF;
            switch (rs)
            {
                case 0x00: // MFC2
                    WriteGpr(rt, unchecked((uint)(int)(short)ReadVectorElement((int)rd, (int)(element & 0xEu))));
                    return true;
                case 0x02: // CFC2
                    WriteGpr(rt, ReadVectorControl((int)rd));
                    return true;
                case 0x04: // MTC2
                    WriteVectorElement((int)rd, (int)(element & 0xEu), (ushort)_gpr[rt]);
                    return true;
                case 0x06: // CTC2
                    WriteVectorControl((int)rd, (ushort)_gpr[rt]);
                    return true;
                default:
                    stopReason = $"unsupported-cop2 pc=0x{pc:x3} rs=0x{rs:x2} op=0x{instr:x8}";
                    return false;
            }
        }

        private bool ExecuteVectorMemory(uint pc, bool isLoad, uint rs, uint vt, uint instr, out string stopReason)
        {
            int subop = (int)((instr >> 11) & 0x1F);
            int element = (int)((instr >> 7) & 0xF);
            int offset = SignExtend7((int)(instr & 0x7F));
            uint baseAddress = _gpr[rs];
            stopReason = string.Empty;

            switch (subop)
            {
                case 0: // BV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)offset, 1);
                    return true;
                case 1: // SV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 1), 2);
                    return true;
                case 2: // LV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 2), 4);
                    return true;
                case 3: // DV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 3), 8);
                    return true;
                case 4: // QV
                    TransferVectorBytes(isLoad, (int)vt, element, baseAddress + (uint)(offset << 4), 16);
                    return true;
                default:
                    stopReason = $"unsupported-vector-mem pc=0x{pc:x3} op=0x{instr:x8}";
                    return false;
            }
        }

        private void TransferVectorBytes(bool isLoad, int vt, int element, uint address, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int lane = (element + i) & 0xF;
                uint addr = (address + (uint)i) & 0xFFFu;
                if (isLoad)
                    _vr[vt, lane] = ReadByte(addr);
                else
                    WriteByte(addr, _vr[vt, lane]);
            }
        }

        private ushort ReadVectorElement(int vt, int element)
        {
            int lane = element & 0xF;
            byte hi = _vr[vt, lane];
            byte lo = _vr[vt, (lane + 1) & 0xF];
            return (ushort)((hi << 8) | lo);
        }

        private void WriteVectorElement(int vt, int element, ushort value)
        {
            int lane = element & 0xF;
            _vr[vt, lane] = (byte)(value >> 8);
            _vr[vt, (lane + 1) & 0xF] = (byte)value;
        }

        private uint ReadVectorControl(int rd)
        {
            switch (rd & 3)
            {
                case 0: return _vco[0];
                case 1: return _vco[1];
                case 2: return _vcc[0];
                case 3: return _vcc[1];
                default: return _vce;
            }
        }

        private void WriteVectorControl(int rd, ushort value)
        {
            switch (rd & 3)
            {
                case 0: _vco[0] = value; break;
                case 1: _vco[1] = value; break;
                case 2: _vcc[0] = value; break;
                case 3: _vcc[1] = value; break;
                default: _vce = (byte)value; break;
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

        private static int SignExtend7(int value)
        {
            value &= 0x7F;
            return (value & 0x40) != 0 ? value | unchecked((int)0xFFFFFF80) : value;
        }

        private static uint BranchTarget(uint pc, short imm)
        {
            return unchecked((uint)((int)(pc + 4) + (imm << 2)));
        }
    }
}
