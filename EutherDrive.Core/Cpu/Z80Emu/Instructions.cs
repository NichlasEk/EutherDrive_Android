using System;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal enum BlockMode
    {
        Increment,
        Decrement,
    }

    internal static class BlockModeExtensions
    {
        public static ushort Apply(this BlockMode mode, ushort value)
        {
            return mode == BlockMode.Increment ? (ushort)(value + 1) : (ushort)(value - 1);
        }
    }

    internal static partial class Instructions
    {
        private enum InterruptType
        {
            Nmi,
            Int,
        }

        private struct ParseResult
        {
            public byte Opcode;
            public IndexRegister? IndexPrefix;
            public uint IndexFetchTCycles;
        }

        internal sealed partial class InstructionExecutor
        {
            private readonly Registers _registers;
            private readonly IBusInterface _bus;

            public InstructionExecutor(Registers registers, IBusInterface bus)
            {
                _registers = registers;
                _bus = bus;
            }

            internal byte FetchOperand()
            {
                byte operand = _bus.ReadMemory(_registers.Pc);
                _registers.Pc = (ushort)(_registers.Pc + 1);
                return operand;
            }

            internal ushort FetchOperandU16()
            {
                byte lsb = FetchOperand();
                byte msb = FetchOperand();
                return (ushort)(lsb | (msb << 8));
            }

            private ParseResult ParseOpcode()
            {
                IndexRegister? index = null;
                uint tCycles = 0;
                while (true)
                {
                    byte opcode = FetchOperand();
                    switch (opcode)
                    {
                        case 0xDD:
                            index = IndexRegister.IX;
                            tCycles += 4;
                            break;
                        case 0xFD:
                            index = IndexRegister.IY;
                            tCycles += 4;
                            break;
                        default:
                            return new ParseResult
                            {
                                Opcode = opcode,
                                IndexPrefix = index,
                                IndexFetchTCycles = tCycles,
                            };
                    }
                }
            }

            internal ushort FetchIndirectHlAddress(IndexRegister? index)
            {
                if (index.HasValue)
                {
                    ushort baseAddr = index.Value.ReadFrom(_registers);
                    sbyte offset = unchecked((sbyte)FetchOperand());
                    return (ushort)((baseAddr + offset) & 0xFFFF);
                }

                return Register16.HL.ReadFrom(_registers);
            }

            internal ushort ReadMemoryU16(ushort address)
            {
                byte lsb = _bus.ReadMemory(address);
                byte msb = _bus.ReadMemory((ushort)(address + 1));
                return (ushort)(lsb | (msb << 8));
            }

            internal void WriteMemoryU16(ushort address, ushort value)
            {
                byte lsb = (byte)value;
                byte msb = (byte)(value >> 8);
                _bus.WriteMemory(address, lsb);
                _bus.WriteMemory((ushort)(address + 1), msb);
            }

            internal void PushStack(ushort value)
            {
                byte lsb = (byte)value;
                byte msb = (byte)(value >> 8);

                _registers.Sp = (ushort)(_registers.Sp - 1);
                _bus.WriteMemory(_registers.Sp, msb);
                _registers.Sp = (ushort)(_registers.Sp - 1);
                _bus.WriteMemory(_registers.Sp, lsb);
            }

            internal ushort PopStack()
            {
                byte lsb = _bus.ReadMemory(_registers.Sp);
                _registers.Sp = (ushort)(_registers.Sp + 1);
                byte msb = _bus.ReadMemory(_registers.Sp);
                _registers.Sp = (ushort)(_registers.Sp + 1);

                return (ushort)(lsb | (msb << 8));
            }

            private InterruptType? CheckPendingInterrupt()
            {
                if (_registers.InterruptDelay)
                {
                    return null;
                }

                if (_bus.Nmi() == InterruptLine.Low && _registers.LastNmi == InterruptLine.High)
                {
                    return InterruptType.Nmi;
                }

                if (_registers.Iff1 && _bus.Int() == InterruptLine.Low)
                {
                    return InterruptType.Int;
                }

                return null;
            }

            private uint InterruptServiceRoutine(InterruptType interruptType)
            {
                _registers.Halted = false;

                switch (interruptType)
                {
                    case InterruptType.Nmi:
                        PushStack(_registers.Pc);
                        _registers.Pc = 0x0066;
                        _registers.Iff1 = false;
                        return 11;
                    case InterruptType.Int:
                        _registers.Iff1 = false;
                        _registers.Iff2 = false;

                        switch (_registers.InterruptMode)
                        {
                            case InterruptMode.Mode0:
                            case InterruptMode.Mode1:
                                PushStack(_registers.Pc);
                                _registers.Pc = 0x0038;
                                return 13;
                            case InterruptMode.Mode2:
                                // Treat as mode 1
                                PushStack(_registers.Pc);
                                _registers.Pc = 0x0038;
                                return 19;
                        }
                        break;
                }

                return 11;
            }

            private uint ExecuteCbPrefix(IndexRegister? index)
            {
                (IndexRegister Index, sbyte Offset)? indexWithOffset = null;
                if (index.HasValue)
                {
                    sbyte offset = unchecked((sbyte)FetchOperand());
                    indexWithOffset = (index.Value, offset);
                }

                byte opcode2 = FetchOperand();

                if ((opcode2 <= 0x05) || opcode2 == 0x07)
                    return RlcR(opcode2, indexWithOffset);
                if (opcode2 == 0x06)
                    return RlcHl(indexWithOffset);
                if ((opcode2 >= 0x08 && opcode2 <= 0x0D) || opcode2 == 0x0F)
                    return RrcR(opcode2, indexWithOffset);
                if (opcode2 == 0x0E)
                    return RrcHl(indexWithOffset);
                if ((opcode2 >= 0x10 && opcode2 <= 0x15) || opcode2 == 0x17)
                    return RlR(opcode2, indexWithOffset);
                if (opcode2 == 0x16)
                    return RlHl(indexWithOffset);
                if ((opcode2 >= 0x18 && opcode2 <= 0x1D) || opcode2 == 0x1F)
                    return RrR(opcode2, indexWithOffset);
                if (opcode2 == 0x1E)
                    return RrHl(indexWithOffset);
                if ((opcode2 >= 0x20 && opcode2 <= 0x25) || opcode2 == 0x27)
                    return SlaR(opcode2, indexWithOffset);
                if (opcode2 == 0x26)
                    return SlaHl(indexWithOffset);
                if ((opcode2 >= 0x28 && opcode2 <= 0x2D) || opcode2 == 0x2F)
                    return SraR(opcode2, indexWithOffset);
                if (opcode2 == 0x2E)
                    return SraHl(indexWithOffset);
                if ((opcode2 >= 0x30 && opcode2 <= 0x35) || opcode2 == 0x37)
                    return SllR(opcode2, indexWithOffset);
                if (opcode2 == 0x36)
                    return SllHl(indexWithOffset);
                if ((opcode2 >= 0x38 && opcode2 <= 0x3D) || opcode2 == 0x3F)
                    return SrlR(opcode2, indexWithOffset);
                if (opcode2 == 0x3E)
                    return SrlHl(indexWithOffset);

                if (opcode2 >= 0x40 && opcode2 <= 0x7F)
                {
                    if ((opcode2 & 0x07) == 0x06)
                        return BitBHl(opcode2, indexWithOffset);
                    return BitBR(opcode2);
                }

                if (opcode2 >= 0x80 && opcode2 <= 0xBF)
                {
                    if ((opcode2 & 0x07) == 0x06)
                        return ResBHl(opcode2, indexWithOffset);
                    return ResBR(opcode2, indexWithOffset);
                }

                if ((opcode2 & 0x07) == 0x06)
                    return SetBHl(opcode2, indexWithOffset);
                return SetBR(opcode2, indexWithOffset);
            }

            private uint ExecuteEdPrefix()
            {
                byte opcode2 = FetchOperand();

                return opcode2 switch
                {
                    0x40 or 0x48 or 0x50 or 0x58 or 0x60 or 0x68 or 0x70 or 0x78 => InRC(opcode2),
                    0x41 or 0x49 or 0x51 or 0x59 or 0x61 or 0x69 or 0x71 or 0x79 => OutCR(opcode2),
                    0x42 or 0x52 or 0x62 or 0x72 => SbcHlSs(opcode2),
                    0x43 or 0x53 or 0x63 or 0x73 => LdDirectDd(opcode2),
                    0x44 => Neg(),
                    0x45 or 0x4D => RetiRetn(),
                    0x46 => Im(InterruptMode.Mode0),
                    0x47 => LdIrA(Register8.I),
                    0x4A or 0x5A or 0x6A or 0x7A => AdcHlSs(opcode2),
                    0x4B or 0x5B or 0x6B or 0x7B => LdDdDirect(opcode2),
                    0x4F => LdIrA(Register8.R),
                    0x56 => Im(InterruptMode.Mode1),
                    0x57 => LdAIr(Register8.I),
                    0x5E => Im(InterruptMode.Mode2),
                    0x5F => LdAIr(Register8.R),
                    0x67 => Rrd(),
                    0x6F => Rld(),
                    0xA0 => BlockTransfer(BlockMode.Increment, false),
                    0xA1 => CompareBlock(BlockMode.Increment, false),
                    0xA2 => InBlock(BlockMode.Increment, false),
                    0xA3 => OutBlock(BlockMode.Increment, false),
                    0xA8 => BlockTransfer(BlockMode.Decrement, false),
                    0xA9 => CompareBlock(BlockMode.Decrement, false),
                    0xAA => InBlock(BlockMode.Decrement, false),
                    0xAB => OutBlock(BlockMode.Decrement, false),
                    0xB0 => BlockTransfer(BlockMode.Increment, true),
                    0xB1 => CompareBlock(BlockMode.Increment, true),
                    0xB2 => InBlock(BlockMode.Increment, true),
                    0xB3 => OutBlock(BlockMode.Increment, true),
                    0xB8 => BlockTransfer(BlockMode.Decrement, true),
                    0xB9 => CompareBlock(BlockMode.Decrement, true),
                    0xBA => InBlock(BlockMode.Decrement, true),
                    0xBB => OutBlock(BlockMode.Decrement, true),
                    _ => Control.Nop(),
                };
            }

            public uint Execute()
            {
                _registers.R = (byte)((_registers.R + 1) & 0x7F | (_registers.R & 0x80));

                InterruptType? interruptType = CheckPendingInterrupt();

                _registers.InterruptDelay = false;
                _registers.LastNmi = _bus.Nmi();

                if (interruptType.HasValue)
                {
                    return InterruptServiceRoutine(interruptType.Value);
                }

                if (_registers.Halted)
                {
                    return Control.Nop();
                }

                ParseResult parsed = ParseOpcode();
                byte opcode = parsed.Opcode;
                IndexRegister? index = parsed.IndexPrefix;

                uint instructionCycles = opcode switch
                {
                    0x00 => Control.Nop(),
                    0x01 or 0x11 or 0x21 or 0x31 => LdDdImmediate(opcode, index),
                    0x02 => LdIndirectA(Register16.BC),
                    0x03 or 0x13 or 0x23 or 0x33 => IncSs(opcode, index),
                    0x04 or 0x0C or 0x14 or 0x1C or 0x24 or 0x2C or 0x3C => IncR(opcode, index),
                    0x34 => IncHl(index),
                    0x05 or 0x0D or 0x15 or 0x1D or 0x25 or 0x2D or 0x3D => DecR(opcode, index),
                    0x35 => DecHl(index),
                    0x06 or 0x0E or 0x16 or 0x1E or 0x26 or 0x2E or 0x3E => LdRImmediate(opcode, index),
                    0x36 => LdHlImmediate(index),
                    0x07 => Rlca(),
                    0x08 => ExchangeAf(),
                    0x09 or 0x19 or 0x29 or 0x39 => AddHlSs(opcode, index),
                    0x0A => LdAIndirect(Register16.BC),
                    0x0B or 0x1B or 0x2B or 0x3B => DecSs(opcode, index),
                    0x0F => Rrca(),
                    0x10 => DjnzE(),
                    0x12 => LdIndirectA(Register16.DE),
                    0x17 => Rla(),
                    0x18 => JrE(),
                    0x1A => LdAIndirect(Register16.DE),
                    0x1F => Rra(),
                    0x20 => JrNzE(),
                    0x22 => LdDirectHl(index),
                    0x27 => Daa(),
                    0x28 => JrZE(),
                    0x2A => LdHlDirect(index),
                    0x2F => Cpl(),
                    0x30 => JrNcE(),
                    0x32 => LdDirectA(),
                    0x37 => Scf(),
                    0x38 => JrCE(),
                    0x3A => LdADirect(),
                    0x3F => Ccf(),
                    0x40 or 0x41 or 0x42 or 0x43 or 0x44 or 0x45 or 0x47 or 0x48 or 0x49 or 0x4A or 0x4B or 0x4C
                    or 0x4D or 0x4F or 0x50 or 0x51 or 0x52 or 0x53 or 0x54 or 0x55 or 0x57 or 0x58 or 0x59 or 0x5A
                    or 0x5B or 0x5C or 0x5D or 0x5F or 0x60 or 0x61 or 0x62 or 0x63 or 0x64 or 0x65 or 0x67 or 0x68
                    or 0x69 or 0x6A or 0x6B or 0x6C or 0x6D or 0x6F or 0x78 or 0x79 or 0x7A or 0x7B or 0x7C or 0x7D
                    or 0x7F => LdRR(opcode, index),
                    0x46 or 0x4E or 0x56 or 0x5E or 0x66 or 0x6E or 0x7E => LdRHl(opcode, index),
                    0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x77 => LdHlR(opcode, index),
                    0x76 => Halt(),
                    0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x87 => AddAR(opcode, index, false),
                    0x86 => AddAHl(index, false),
                    0x88 or 0x89 or 0x8A or 0x8B or 0x8C or 0x8D or 0x8F => AddAR(opcode, index, true),
                    0x8E => AddAHl(index, true),
                    0x90 or 0x91 or 0x92 or 0x93 or 0x94 or 0x95 or 0x97 => SubAR(opcode, index, false),
                    0x96 => SubAHl(index, false),
                    0x98 or 0x99 or 0x9A or 0x9B or 0x9C or 0x9D or 0x9F => SubAR(opcode, index, true),
                    0x9E => SubAHl(index, true),
                    0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0xA7 => AndAR(opcode, index),
                    0xA6 => AndAHl(index),
                    0xA8 or 0xA9 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAF => XorAR(opcode, index),
                    0xAE => XorAHl(index),
                    0xB0 or 0xB1 or 0xB2 or 0xB3 or 0xB4 or 0xB5 or 0xB7 => OrAR(opcode, index),
                    0xB6 => OrAHl(index),
                    0xB8 or 0xB9 or 0xBA or 0xBB or 0xBC or 0xBD or 0xBF => CpAR(opcode, index),
                    0xBE => CpAHl(index),
                    0xC0 or 0xC8 or 0xD0 or 0xD8 or 0xE0 or 0xE8 or 0xF0 or 0xF8 => RetCc(opcode),
                    0xC1 or 0xD1 or 0xE1 or 0xF1 => PopQq(opcode, index),
                    0xC2 or 0xCA or 0xD2 or 0xDA or 0xE2 or 0xEA or 0xF2 or 0xFA => JpCcNn(opcode),
                    0xC3 => JpNn(),
                    0xC4 or 0xCC or 0xD4 or 0xDC or 0xE4 or 0xEC or 0xF4 or 0xFC => CallCcNn(opcode),
                    0xC5 or 0xD5 or 0xE5 or 0xF5 => PushQq(opcode, index),
                    0xC6 => AddAImmediate(false),
                    0xC7 or 0xCF or 0xD7 or 0xDF or 0xE7 or 0xEF or 0xF7 or 0xFF => RstP(opcode),
                    0xC9 => Ret(),
                    0xCB => ExecuteCbPrefix(index),
                    0xCD => CallNn(),
                    0xCE => AddAImmediate(true),
                    0xD3 => OutNA(),
                    0xD6 => SubAImmediate(false),
                    0xD9 => ExchangeBcdehl(),
                    0xDB => InAN(),
                    0xDE => SubAImmediate(true),
                    0xE3 => ExchangeStackHl(index),
                    0xE6 => AndAImmediate(),
                    0xE9 => JpHl(index),
                    0xEB => ExchangeDeHl(),
                    0xED => ExecuteEdPrefix(),
                    0xEE => XorAImmediate(),
                    0xF3 => Di(),
                    0xF6 => OrAImmediate(),
                    0xF9 => LdSpHl(index),
                    0xFB => Ei(),
                    0xFE => CpAImmediate(),
                    _ => Control.Nop(),
                };

                return parsed.IndexFetchTCycles + instructionCycles;
            }

            private static bool SignFlag(byte value) => value.Bit(7);
            private static bool ZeroFlag(byte value) => value == 0;
            private static bool ParityFlag(byte value) => (value.BitCount() & 1) == 0;

            // Helpers used by instruction modules
            public byte FetchOperandPublic() => FetchOperand();
            public ushort FetchOperandU16Public() => FetchOperandU16();
            public ushort FetchIndirectHlAddressPublic(IndexRegister? index) => FetchIndirectHlAddress(index);
            public ushort ReadMemoryU16Public(ushort address) => ReadMemoryU16(address);
            public void WriteMemoryU16Public(ushort address, ushort value) => WriteMemoryU16(address, value);
            public void PushStackPublic(ushort value) => PushStack(value);
            public ushort PopStackPublic() => PopStack();

            public Registers Registers => _registers;
            public IBusInterface Bus => _bus;
            public static bool SignFlagPublic(byte value) => SignFlag(value);
            public static bool ZeroFlagPublic(byte value) => ZeroFlag(value);
            public static bool ParityFlagPublic(byte value) => ParityFlag(value);
        }

        public static uint Execute(Registers registers, IBusInterface bus)
        {
            var executor = new InstructionExecutor(registers, bus);
            return executor.Execute();
        }

        internal static InstructionExecutor CreateExecutor(Registers registers, IBusInterface bus)
        {
            return new InstructionExecutor(registers, bus);
        }
    }

    internal static class Control
    {
        public static uint Nop() => 4;
    }
}
