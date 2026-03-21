using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProjectPSX.Disassembler;

namespace ProjectPSX {
    internal unsafe class CPU {  //MIPS R3000A-compatible 32-bit RISC CPU MIPS R3051 with 5 KB L1 cache, running at 33.8688 MHz // 33868800
        private const bool StrictAddressExceptions = true;
        private static readonly bool ExperimentalInstructionCache =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_DISABLE_ICACHE") != "1"
            && Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_EXPERIMENTAL_ICACHE") != "0";
        private static readonly string? FaultTraceFile =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_FAULT_TRACE_FILE");
        private static readonly int FaultTraceLimit = ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_FAULT_TRACE_LIMIT"), 64);
        private static readonly string? Cop0TraceFile =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_COP0_TRACE_FILE");
        private static readonly int Cop0TraceLimit = ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_COP0_TRACE_LIMIT"), 256);
        private static readonly int? Cop0TraceRegister = ParseOptionalRegisterIndex(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_COP0_TRACE_REGISTER"));
        private static readonly int Cop0TraceWordsBefore = ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_COP0_TRACE_WORDS_BEFORE"), 0);
        private static readonly int Cop0TraceWordsAfter = ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_COP0_TRACE_WORDS_AFTER"), 0);
        private static readonly bool BiosTraceEnabled =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BIOS_TRACE") == "1";
        private static readonly bool TraceCurrentPcEnabled = HasTraceCurrentPcConsumer();
        private static int s_faultTraceCount;
        private static int s_cop0TraceCount;

        private uint PC_Now; // PC on current execution as PC and PC Predictor go ahead after fetch. This is handy on Branch Delay so it dosn't give erronious PC-4
        private uint PC = 0xbfc0_0000; // Bios Entry Point
        private uint PC_Predictor = 0xbfc0_0004; //next op for branch delay slot emulation

        private uint[] GPR = new uint[32];
        private uint HI;
        private uint LO;

        private bool opcodeIsBranch;
        private bool opcodeIsDelaySlot;

        private bool opcodeTookBranch;
        private bool opcodeInDelaySlotTookBranch;

        private static uint[] ExceptionAdress = new uint[] { 0x8000_0080, 0xBFC0_0180 };

        //CoPro Regs
        private uint[] COP0_GPR = new uint[16];
        private const int SR = 12;
        private const int CAUSE = 13;
        private const int EPC = 14;
        private const int BADA = 8;
        private const int JUMPDEST = 6;

        private bool dontIsolateCache = true;
        private const int InstructionCacheLineCount = 256;
        private const int InstructionCacheWordsPerLine = 4;
        private const int InstructionCacheLineSizeBytes = 16;
        private const int InstructionCacheTotalBytes = InstructionCacheLineCount * InstructionCacheLineSizeBytes;
        private readonly uint[] _instructionCacheTags = new uint[InstructionCacheLineCount];
        private readonly bool[] _instructionCacheValid = new bool[InstructionCacheLineCount];
        private readonly uint[] _instructionCacheData = new uint[InstructionCacheLineCount * InstructionCacheWordsPerLine];
        private readonly Instr[] _decodedInstructionCache = new Instr[InstructionCacheLineCount * InstructionCacheWordsPerLine];
        private readonly delegate*<CPU, void>[] _decodedInstructionHandlers = new delegate*<CPU, void>[InstructionCacheLineCount * InstructionCacheWordsPerLine];
        private readonly byte[] _decodedInstructionValidMask = new byte[InstructionCacheLineCount];
        private bool _instructionCacheEnabled;
        private bool _instructionCacheRuntimeAllowed;
        private delegate*<CPU, void> _currentOpcodeHandler = &NOP;

        private GTE gte;
        [NonSerialized]
        private BUS bus;

        [NonSerialized]
        private BIOS_Disassembler bios;
        [NonSerialized]
        private MIPS_Disassembler mips;

        private struct MEM {
            public uint register;
            public uint value;
        }
        private MEM writeBack;
        private MEM memoryLoad;
        private MEM delayedMemoryLoad;

        public struct Instr {
            public uint value;                     //raw
            private byte _opcode;
            private byte _rs;
            private byte _rt;
            private byte _rd;
            private byte _sa;
            private byte _function;
            private byte _id;
            private ushort _imm;
            private short _immSigned;
            private uint _addr;

            public uint opcode => _opcode;

            //I-Type
            public uint rs => _rs;
            public uint rt => _rt;
            public uint imm => _imm;
            public uint imm_s => unchecked((uint)_immSigned);

            //R-Type
            public uint rd => _rd;
            public uint sa => _sa;
            public uint function => _function;

            //J-Type                                       
            public uint addr => _addr;

            //id / Cop
            public uint id => _id;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Decode(uint raw) {
                value = raw;
                _opcode = (byte)(raw >> 26);
                _rs = (byte)((raw >> 21) & 0x1F);
                _rt = (byte)((raw >> 16) & 0x1F);
                _imm = (ushort)raw;
                _immSigned = (short)raw;
                _rd = (byte)((raw >> 11) & 0x1F);
                _sa = (byte)((raw >> 6) & 0x1F);
                _function = (byte)(raw & 0x3F);
                _addr = raw & 0x03FF_FFFF;
                _id = (byte)(_opcode & 0x3);
            }
        }
        private Instr instr;

        //Debug
        public bool debug = false;
        public static uint TraceCurrentPC;
        public uint CurrentPC => PC_Now;
        public uint GetRegister(int index) => GPR[index & 31];
        public uint GetCop0Register(int index) => COP0_GPR[index & 15];
        public uint StackPointer => GPR[29];
        public uint ReturnAddress => GPR[31];

        public CPU(BUS bus) {
            this.bus = bus;
            bios = new BIOS_Disassembler(bus);
            mips = new MIPS_Disassembler(ref HI, ref LO, GPR, COP0_GPR);
            gte = new GTE();
            bus.SetMemoryCacheControlObserver(OnMemoryCacheControlChanged);

            COP0_GPR[15] = 0x2; //PRID Processor ID
            FlushInstructionCache();
        }

        public void ObserveRamWrite(uint physicalAddress, int sizeBytes) {
            if (!ExperimentalInstructionCache || sizeBytes <= 0) {
                return;
            }

            InvalidateInstructionCacheRange(physicalAddress, sizeBytes);
        }

        private static delegate*<CPU, void>[] opcodeMainTable = new delegate*<CPU, void>[] {
                &SPECIAL,  &BCOND,  &J,      &JAL,    &BEQ,    &BNE,    &BLEZ,   &BGTZ,
                &ADDI,     &ADDIU,  &SLTI,   &SLTIU,  &ANDI,   &ORI,    &XORI,   &LUI,
                &COP0,     &NOP,    &COP2,   &NOP,    &NA,     &NA,     &NA,     &NA,
                &NA,       &NA,     &NA,     &NA,     &NA,     &NA,     &NA,     &NA,
                &LB,       &LH,     &LWL,    &LW,     &LBU,    &LHU,    &LWR,    &NA,
                &SB,       &SH,     &SWL,    &SW,     &NA,     &NA,     &SWR,    &NA,
                &NOP,      &NOP,    &LWC2,   &NOP,    &NA,     &NA,     &NA,     &NA,
                &NOP,      &NOP,    &SWC2,   &NOP,    &NA,     &NA,     &NA,     &NA,
            };

        private static delegate*<CPU, void>[] opcodeSpecialTable = new delegate*<CPU, void>[] {
                &SLL,   &NA,    &SRL,   &SRA,   &SLLV,    &NA,     &SRLV, &SRAV,
                &JR,    &JALR,  &NA,    &NA,    &SYSCALL, &BREAK,  &NA,   &NA,
                &MFHI,  &MTHI,  &MFLO,  &MTLO,  &NA,      &NA,     &NA,   &NA,
                &MULT,  &MULTU, &DIV,   &DIVU,  &NA,      &NA,     &NA,   &NA,
                &ADD,   &ADDU,  &SUB,   &SUBU,  &AND,     &OR,     &XOR,  &NOR,
                &NA,    &NA,    &SLT,   &SLTU,  &NA,      &NA,     &NA,   &NA,
                &NA,    &NA,    &NA,    &NA,    &NA,      &NA,     &NA,   &NA,
                &NA,    &NA,    &NA,    &NA,    &NA,      &NA,     &NA,   &NA,
            };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run() {
            return RunSingleStep(out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RunSlice(int maxCpuCycles, out int instructionsExecuted, out bool runtimeRamObserved) {
            instructionsExecuted = 0;
            runtimeRamObserved = false;

            int cpuCyclesExecuted = 0;
            int cpuCycleBudget = Math.Max(1, maxCpuCycles);
            while (cpuCyclesExecuted < cpuCycleBudget) {
                cpuCyclesExecuted += RunSingleStep(out bool stepObservedRuntimeRam);
                instructionsExecuted++;
                runtimeRamObserved |= stepObservedRuntimeRam;

                if (bus.ShouldYieldCpuSlice) {
                    break;
                }
            }

            return cpuCyclesExecuted;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int RunSingleStep(out bool runtimeRamObserved) {
            int ticks = fetchDecode();
            if (instr.value != 0) { //Skip Nops
                _currentOpcodeHandler(this); //Execute
            }
            MemAccess();
            WriteBack();

            //if (debug) {
            //  mips.PrintRegs();
            //  mips.disassemble(instr, PC_Now, PC_Predictor);
            //}

            if (BiosTraceEnabled) {
                bios.verbose(PC_Now, GPR);
            }

            uint physicalPc = PC_Now & 0x1FFF_FFFF;
            runtimeRamObserved = physicalPc < 0x1FC0_0000 && physicalPc >= 0x0001_0000;
            return ticks;
        }

        public void NotifyBiosExited() {
            if (!ExperimentalInstructionCache) {
                return;
            }
            _instructionCacheRuntimeAllowed = true;
            UpdateInstructionCacheControl();
        }

        public void RefreshRuntimeStateAfterLoad() {
            dontIsolateCache = (COP0_GPR[SR] & 0x0001_0000) == 0;
            UpdateInstructionCacheControl();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void handleInterrupts() {
            bool interruptPending = bus.interruptController.interruptPending();
            if (interruptPending) {
                COP0_GPR[CAUSE] |= 0x400;
            } else {
                COP0_GPR[CAUSE] &= ~(uint)0x400;
            }

            bool IEC = (COP0_GPR[SR] & 0x1) == 1;
            byte IM = (byte)((COP0_GPR[SR] >> 8) & 0xFF);
            byte IP = (byte)((COP0_GPR[CAUSE] >> 8) & 0xFF);

            if (!IEC || (IM & IP) == 0) {
                return;
            }

            //Executable address space is limited to ram and bios on psx
            uint load = FetchInstructionWord(PC);

            //This is actually the "next" opcode if it's a GTE one
            //just postpone the interrupt so it doesn't glitch out
            //Crash Bandicoot intro is a good example for this
            uint instr = load >> 26;
            if (instr == 0x12) { //COP2 MTC2
                return;
            }

            EXCEPTION(this, EX.INTERRUPT);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int fetchDecode() {
            //Executable address space is limited to ram and bios on psx
            PC_Now = PC;
            if (TraceCurrentPcEnabled) {
                TraceCurrentPC = PC_Now;
            }
            PC = PC_Predictor;
            PC_Predictor += 4;

            opcodeIsDelaySlot = opcodeIsBranch;
            opcodeInDelaySlotTookBranch = opcodeTookBranch;
            opcodeIsBranch = false;
            opcodeTookBranch = false;

            if (StrictAddressExceptions && (PC_Now & 0x3) != 0) {
                COP0_GPR[BADA] = PC_Now;
                EXCEPTION(this, EX.LOAD_ADRESS_ERROR);
                SetCurrentInstruction(0);
                return 1;
            }

            uint maskedPC = PC_Now & 0x1FFF_FFFF;
            if (maskedPC < 0x1F00_0000) {
                if (_instructionCacheEnabled && IsInstructionCacheable(PC_Now)) {
                    LoadDecodedInstruction(maskedPC);
                } else {
                    SetCurrentInstruction(FetchInstructionWord(PC_Now));
                }
                return 1;
            } else {
                SetCurrentInstruction(bus.LoadFromBios(maskedPC));
                return 20;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint FetchInstructionWord(uint virtualPc) {
            if (!ExperimentalInstructionCache) {
                uint physicalAddress = virtualPc & 0x1FFF_FFFF;
                return physicalAddress < 0x1F00_0000
                    ? bus.LoadFromRam(physicalAddress)
                    : bus.LoadFromBios(physicalAddress);
            }

            uint physical = virtualPc & 0x1FFF_FFFF;
            if (physical >= 0x1F00_0000) {
                return bus.LoadFromBios(physical);
            }

            if (!_instructionCacheEnabled || !IsInstructionCacheable(virtualPc)) {
                return bus.LoadFromRam(physical);
            }

            return LoadFromInstructionCache(physical);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsInstructionCacheable(uint virtualPc) {
            uint segment = virtualPc >> 29;
            return segment <= 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint LoadFromInstructionCache(uint physicalAddress) {
            int lineIndex = (int)((physicalAddress >> 4) & (InstructionCacheLineCount - 1));
            uint tag = physicalAddress & 0x1FFF_F000;
            int lineOffset = lineIndex * InstructionCacheWordsPerLine;

            if (!_instructionCacheValid[lineIndex] || _instructionCacheTags[lineIndex] != tag) {
                uint lineBase = physicalAddress & ~0xFu;
                _instructionCacheTags[lineIndex] = tag;
                _instructionCacheValid[lineIndex] = true;
                _decodedInstructionValidMask[lineIndex] = 0;
                _instructionCacheData[lineOffset + 0] = bus.LoadFromRam(lineBase + 0);
                _instructionCacheData[lineOffset + 1] = bus.LoadFromRam(lineBase + 4);
                _instructionCacheData[lineOffset + 2] = bus.LoadFromRam(lineBase + 8);
                _instructionCacheData[lineOffset + 3] = bus.LoadFromRam(lineBase + 12);
            }

            int wordIndex = (int)((physicalAddress >> 2) & 0x3);
            return _instructionCacheData[lineOffset + wordIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint LoadFromInstructionCacheNoFill(uint physicalAddress) {
            int lineIndex = (int)((physicalAddress >> 4) & (InstructionCacheLineCount - 1));
            uint tag = physicalAddress & 0x1FFF_F000;
            if (!_instructionCacheValid[lineIndex] || _instructionCacheTags[lineIndex] != tag) {
                return 0;
            }

            int lineOffset = lineIndex * InstructionCacheWordsPerLine;
            int wordIndex = (int)((physicalAddress >> 2) & 0x3);
            return _instructionCacheData[lineOffset + wordIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StoreToInstructionCacheWord(uint physicalAddress, uint value) {
            int lineIndex = (int)((physicalAddress >> 4) & (InstructionCacheLineCount - 1));
            uint tag = physicalAddress & 0x1FFF_F000;
            int lineOffset = lineIndex * InstructionCacheWordsPerLine;

            if (!_instructionCacheValid[lineIndex] || _instructionCacheTags[lineIndex] != tag) {
                _instructionCacheValid[lineIndex] = true;
                _instructionCacheTags[lineIndex] = tag;
                _decodedInstructionValidMask[lineIndex] = 0;
                Array.Clear(_instructionCacheData, lineOffset, InstructionCacheWordsPerLine);
            }

            int wordIndex = (int)((physicalAddress >> 2) & 0x3);
            _instructionCacheData[lineOffset + wordIndex] = value;
            _decodedInstructionValidMask[lineIndex] &= (byte)~(1 << wordIndex);
        }

        private void UpdateInstructionCacheControl() {
            if (!ExperimentalInstructionCache) {
                _instructionCacheEnabled = false;
                return;
            }

            bool cacheIsolation = (COP0_GPR[SR] & 0x0001_0000) != 0;
            uint cacheControl = bus.MemoryCacheControl;
            bool cacheControlWritten = bus.MemoryCacheWriteCount != 0;
            bool instructionCacheEnabled = _instructionCacheRuntimeAllowed
                && cacheControlWritten
                && (cacheControl & 0x0000_0800) != 0;

            if (_instructionCacheEnabled && !instructionCacheEnabled) {
                FlushInstructionCache();
            }

            if (cacheIsolation && cacheControlWritten && (cacheControl & 0x6) != 0) {
                FlushInstructionCache();
            }

            _instructionCacheEnabled = instructionCacheEnabled;
        }

        private void OnMemoryCacheControlChanged() {
            UpdateInstructionCacheControl();
        }

        private void FlushInstructionCache() {
            Array.Clear(_instructionCacheValid, 0, _instructionCacheValid.Length);
            Array.Clear(_decodedInstructionValidMask, 0, _decodedInstructionValidMask.Length);
        }

        private void InvalidateInstructionCacheRange(uint physicalAddress, int sizeBytes) {
            if (!_instructionCacheRuntimeAllowed || sizeBytes <= 0) {
                return;
            }

            if (sizeBytes >= InstructionCacheTotalBytes) {
                FlushInstructionCache();
                return;
            }

            ulong start = physicalAddress & 0x1FFF_FFFFu;
            ulong endExclusive = start + (uint)sizeBytes;
            if (endExclusive <= start) {
                FlushInstructionCache();
                return;
            }

            ulong lineBase = start & ~0xFul;
            ulong lastLine = (endExclusive - 1) & ~0xFul;
            while (lineBase <= lastLine) {
                int lineIndex = (int)((lineBase >> 4) & (InstructionCacheLineCount - 1));
                uint tag = (uint)(lineBase & 0x1FFF_F000u);
                if (_instructionCacheValid[lineIndex] && _instructionCacheTags[lineIndex] == tag) {
                    _instructionCacheValid[lineIndex] = false;
                    _decodedInstructionValidMask[lineIndex] = 0;
                }
                lineBase += 0x10;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LoadDecodedInstruction(uint physicalAddress) {
            int lineIndex = (int)((physicalAddress >> 4) & (InstructionCacheLineCount - 1));
            int wordIndex = (int)((physicalAddress >> 2) & 0x3);
            int lineOffset = lineIndex * InstructionCacheWordsPerLine;
            uint raw = LoadFromInstructionCache(physicalAddress);
            byte bit = (byte)(1 << wordIndex);

            if ((_decodedInstructionValidMask[lineIndex] & bit) == 0) {
                int cacheIndex = lineOffset + wordIndex;
                _decodedInstructionCache[cacheIndex].Decode(raw);
                _decodedInstructionHandlers[cacheIndex] = ResolveInstructionHandler(in _decodedInstructionCache[cacheIndex]);
                _decodedInstructionValidMask[lineIndex] |= bit;
            }

            int decodedIndex = lineOffset + wordIndex;
            instr = _decodedInstructionCache[decodedIndex];
            _currentOpcodeHandler = _decodedInstructionHandlers[decodedIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static delegate*<CPU, void> ResolveInstructionHandler(in Instr decoded) {
            return decoded.opcode == 0
                ? opcodeSpecialTable[decoded.function]
                : opcodeMainTable[decoded.opcode];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetCurrentInstruction(uint rawValue) {
            instr.Decode(rawValue);
            _currentOpcodeHandler = ResolveInstructionHandler(in instr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MemAccess() {
            if (delayedMemoryLoad.register != memoryLoad.register) { //if loadDelay on same reg it is lost/overwritten (amidog tests)
                ref uint r0 = ref MemoryMarshal.GetArrayDataReference(GPR);
                Unsafe.Add(ref r0, (nint)memoryLoad.register) = memoryLoad.value;
            }
            memoryLoad = delayedMemoryLoad;
            delayedMemoryLoad.register = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteBack() {
            ref uint r0 = ref MemoryMarshal.GetArrayDataReference(GPR);
            Unsafe.Add(ref r0, (nint)writeBack.register) = writeBack.value;
            writeBack.register = 0;
            r0 = 0;
        }

        // Non Implemented by the CPU Opcodes
        private static void NOP(CPU cpu) { /*nop*/ }

        private static void NA(CPU cpu) => EXCEPTION(cpu, EX.ILLEGAL_INSTR, cpu.instr.id);


        // Main Table Opcodes
        private static void SPECIAL(CPU cpu) => opcodeSpecialTable[cpu.instr.function](cpu);

        private static void BCOND(CPU cpu) {
            cpu.opcodeIsBranch = true;
            uint op = cpu.instr.rt;

            bool should_link = (op & 0x1E) == 0x10;
            bool should_branch = (int)(cpu.GPR[cpu.instr.rs] ^ (op << 31)) < 0;

            if (should_link) cpu.GPR[31] = cpu.PC_Predictor;
            if (should_branch) BRANCH(cpu);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void J(CPU cpu) {
            cpu.opcodeIsBranch = true;
            cpu.opcodeTookBranch = true;
            cpu.PC_Predictor = (cpu.PC_Predictor & 0xF000_0000) | (cpu.instr.addr << 2);
        }

        private static void JAL(CPU cpu) {
            cpu.setGPR(31, cpu.PC_Predictor);
            J(cpu);
        }

        private static void BEQ(CPU cpu) {
            cpu.opcodeIsBranch = true;
            if (cpu.GPR[cpu.instr.rs] == cpu.GPR[cpu.instr.rt]) {
                BRANCH(cpu);
            }
        }

        private static void BNE(CPU cpu) {
            cpu.opcodeIsBranch = true;
            if (cpu.GPR[cpu.instr.rs] != cpu.GPR[cpu.instr.rt]) {
                BRANCH(cpu);
            }
        }

        private static void BLEZ(CPU cpu) {
            cpu.opcodeIsBranch = true;
            if (((int)cpu.GPR[cpu.instr.rs]) <= 0) {
                BRANCH(cpu);
            }
        }

        private static void BGTZ(CPU cpu) {
            cpu.opcodeIsBranch = true;
            if (((int)cpu.GPR[cpu.instr.rs]) > 0) {
                BRANCH(cpu);
            }
        }

        private static void ADDI(CPU cpu) {
            uint rs = cpu.GPR[cpu.instr.rs];
            uint imm_s = cpu.instr.imm_s;
            uint result = rs + imm_s;

#if CPU_EXCEPTIONS
            if(checkOverflow(rs, imm_s, result)) {
                EXCEPTION(cpu, EX.OVERFLOW, cpu.instr.id);
            } else {
                cpu.setGPR(cpu.instr.rt, result);
            }
#else
            cpu.setGPR(cpu.instr.rt, result);
#endif
        }

        private static void ADDIU(CPU cpu) => cpu.setGPR(cpu.instr.rt, cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s);

        private static void SLTI(CPU cpu) {
            bool condition = (int)cpu.GPR[cpu.instr.rs] < (int)cpu.instr.imm_s;
            cpu.setGPR(cpu.instr.rt, condition ? 1u : 0u);
        }

        private static void SLTIU(CPU cpu) {
            bool condition = cpu.GPR[cpu.instr.rs] < cpu.instr.imm_s;
            cpu.setGPR(cpu.instr.rt, condition ? 1u : 0u);
        }

        private static void ANDI(CPU cpu) => cpu.setGPR(cpu.instr.rt, cpu.GPR[cpu.instr.rs] & cpu.instr.imm);

        private static void ORI(CPU cpu) => cpu.setGPR(cpu.instr.rt, cpu.GPR[cpu.instr.rs] | cpu.instr.imm);

        private static void XORI(CPU cpu) => cpu.setGPR(cpu.instr.rt, cpu.GPR[cpu.instr.rs] ^ cpu.instr.imm);

        private static void LUI(CPU cpu) => cpu.setGPR(cpu.instr.rt, cpu.instr.imm << 16);

        private static void COP0(CPU cpu) {
            if (cpu.instr.rs == 0b0_0000) MFC0(cpu);
            else if (cpu.instr.rs == 0b0_0100) MTC0(cpu);
            else if (cpu.instr.rs == 0b1_0000) RFE(cpu);
            else EXCEPTION(cpu, EX.ILLEGAL_INSTR, cpu.instr.id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MFC0(CPU cpu) {
            uint mfc = cpu.instr.rd;
            if (mfc == 3 || mfc >= 5 && mfc <= 9 || mfc >= 11 && mfc <= 15) {
                TraceCop0Access(cpu, "mfc0", mfc, cpu.COP0_GPR[mfc]);
                delayedLoad(cpu, cpu.instr.rt, cpu.COP0_GPR[mfc]);
            } else {
                EXCEPTION(cpu, EX.ILLEGAL_INSTR, cpu.instr.id);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MTC0(CPU cpu) {
            uint value = cpu.GPR[cpu.instr.rt];
            uint register = cpu.instr.rd;

            if (register == CAUSE) { //only bits 8 and 9 are writable
                cpu.COP0_GPR[CAUSE] &= ~(uint)0x300;
                cpu.COP0_GPR[CAUSE] |= value & 0x300;
            } else if (register == SR) {
                //This can trigger soft interrupts
                cpu.dontIsolateCache = (value & 0x10000) == 0;
                bool prevIEC = (cpu.COP0_GPR[SR] & 0x1) == 1;
                bool currentIEC = (value & 0x1) == 1;

                cpu.COP0_GPR[SR] = value;
                cpu.UpdateInstructionCacheControl();

                uint IM = (value >> 8) & 0x3;
                uint IP = (cpu.COP0_GPR[CAUSE] >> 8) & 0x3;

                if (!prevIEC && currentIEC && (IM & IP) > 0) {
                    cpu.PC = cpu.PC_Predictor;
                    EXCEPTION(cpu, EX.INTERRUPT, cpu.instr.id);
                }

            } else {
                cpu.COP0_GPR[register] = value;
            }

            TraceCop0Access(cpu, "mtc0", register, cpu.COP0_GPR[register]);

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RFE(CPU cpu) {
            uint mode = cpu.COP0_GPR[SR] & 0x3F;
            cpu.COP0_GPR[SR] &= ~(uint)0x3F;
            cpu.COP0_GPR[SR] |= mode >> 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EXCEPTION(CPU cpu, EX cause, uint coprocessor = 0) {
            TraceFault(cpu, cause, coprocessor);

            uint mode = cpu.COP0_GPR[SR] & 0x3F;
            cpu.COP0_GPR[SR] &= ~(uint)0x3F;
            cpu.COP0_GPR[SR] |= (mode << 2) & 0x3F;

            uint OldCause = cpu.COP0_GPR[CAUSE] & 0xff00;
            cpu.COP0_GPR[CAUSE] = (uint)cause << 2;
            cpu.COP0_GPR[CAUSE] |= OldCause;
            cpu.COP0_GPR[CAUSE] |= coprocessor << 28;

            if (cause == EX.INTERRUPT) {
                cpu.COP0_GPR[EPC] = cpu.PC;
                //hack: related to the delay of the ex interrupt
                cpu.opcodeIsDelaySlot = cpu.opcodeIsBranch;
                cpu.opcodeInDelaySlotTookBranch = cpu.opcodeTookBranch;
            } else {
                cpu.COP0_GPR[EPC] = cpu.PC_Now;
            }

            if (cpu.opcodeIsDelaySlot) {
                cpu.COP0_GPR[EPC] -= 4;
                cpu.COP0_GPR[CAUSE] |= (uint)1 << 31;
                cpu.COP0_GPR[JUMPDEST] = cpu.PC;

                if (cpu.opcodeInDelaySlotTookBranch) {
                    cpu.COP0_GPR[CAUSE] |= (1 << 30);
                }
            }

            cpu.PC = ExceptionAdress[(cpu.COP0_GPR[SR] & 0x400000) >> 22];
            cpu.PC_Predictor = cpu.PC + 4;
        }

        private static void TraceFault(CPU cpu, EX cause, uint coprocessor) {
            if (string.IsNullOrWhiteSpace(FaultTraceFile) || s_faultTraceCount >= FaultTraceLimit) {
                return;
            }

            s_faultTraceCount++;
            try {
                string line =
                    $"[PSX-FAULT] cause={cause} cop={coprocessor} pc_now={cpu.PC_Now:x8} pc={cpu.PC:x8} next={cpu.PC_Predictor:x8} " +
                    $"sr={cpu.COP0_GPR[SR]:x8} causeReg={cpu.COP0_GPR[CAUSE]:x8} epc={cpu.COP0_GPR[EPC]:x8} badv={cpu.COP0_GPR[BADA]:x8}{Environment.NewLine}";
                File.AppendAllText(FaultTraceFile, line);
            } catch {
            }
        }

        private static int ParseOptionalPositiveInt(string? raw, int fallback) {
            return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
        }

        private static bool HasTraceCurrentPcConsumer() {
            return Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE") == "1"
                || HasTraceRange("EUTHERDRIVE_PSX_TRACE_RAM_READ_START", "EUTHERDRIVE_PSX_TRACE_RAM_READ_END")
                || HasTraceRange("EUTHERDRIVE_PSX_TRACE_RAM_WRITE_START", "EUTHERDRIVE_PSX_TRACE_RAM_WRITE_END")
                || Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_TRACE_CD_DMA") == "1"
                || Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_IRQ_TRACE") == "1"
                || Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_DMA_IRQ_TRACE") == "1"
                || Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_SPU_DMA_TRACE") == "1"
                || Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_CD_PROTECT_TRACE") == "1"
                || !string.IsNullOrWhiteSpace(FaultTraceFile)
                || !string.IsNullOrWhiteSpace(Cop0TraceFile);
        }

        private static bool HasTraceRange(string startName, string endName) {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(startName))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(endName));
        }

        private static int? ParseOptionalRegisterIndex(string? raw) {
            if (string.IsNullOrWhiteSpace(raw)) {
                return null;
            }

            if (int.TryParse(raw, out int value) && value >= 0 && value < 16) {
                return value;
            }

            return null;
        }

        private static void TraceCop0Access(CPU cpu, string op, uint register, uint value) {
            if (string.IsNullOrWhiteSpace(Cop0TraceFile) || s_cop0TraceCount >= Cop0TraceLimit) {
                return;
            }

            if (Cop0TraceRegister.HasValue && register != (uint)Cop0TraceRegister.Value) {
                return;
            }

            s_cop0TraceCount++;
            try {
                string line =
                    $"[PSX-COP0] op={op} pc={cpu.PC_Now:x8} next={cpu.PC_Predictor:x8} instr={cpu.instr.value:x8} " +
                    $"rd={register} rt={cpu.instr.rt} rtv={cpu.GPR[cpu.instr.rt]:x8} value={value:x8} " +
                    $"sr={cpu.COP0_GPR[SR]:x8} cause={cpu.COP0_GPR[CAUSE]:x8} epc={cpu.COP0_GPR[EPC]:x8}";
                if (Cop0TraceWordsBefore > 0 || Cop0TraceWordsAfter > 0) {
                    line += " ctx[";
                    for (int i = -Cop0TraceWordsBefore; i <= Cop0TraceWordsAfter; i++) {
                        if (i != -Cop0TraceWordsBefore) {
                            line += " ";
                        }

                        uint address = unchecked(cpu.PC_Now + (uint)(i * 4));
                        uint raw = cpu.bus.load32(address);
                        line += $"{address:x8}:{raw:x8}";
                    }
                    line += "]";
                }

                if (cpu.PC_Now >= 0x8009_6464 && cpu.PC_Now <= 0x8009_6494) {
                    uint a1 = cpu.GPR[5];
                    line += $" a1={a1:x8}";
                    if (TryLoadRamBytes(cpu, a1, 8, out string bytes)) {
                        line += $" a1bytes={bytes}";
                    }
                }

                line += Environment.NewLine;
                File.AppendAllText(Cop0TraceFile, line);
            } catch {
            }
        }

        private static bool TryLoadRamBytes(CPU cpu, uint address, int count, out string text) {
            uint physical = address & 0x1FFF_FFFF;
            bool isMappedRam = physical < 0x0020_0000;
            bool looksLikeRamPointer = address < 0x0020_0000 || (address >= 0x8000_0000 && address < 0x8020_0000);
            if (!isMappedRam || !looksLikeRamPointer || count <= 0) {
                text = string.Empty;
                return false;
            }

            Span<byte> bytes = stackalloc byte[count];
            for (int i = 0; i < count; i++) {
                uint byteAddress = physical + (uint)i;
                uint word = cpu.bus.LoadFromRam(byteAddress & ~3u);
                int shift = (int)((byteAddress & 3u) * 8u);
                bytes[i] = (byte)((word >> shift) & 0xFFu);
            }

            text = BitConverter.ToString(bytes.ToArray()).Replace("-", "");
            return true;
        }

        private static void COP2(CPU cpu) {
            if ((cpu.instr.rs & 0x10) == 0) {
                switch (cpu.instr.rs) {
                    case 0b0_0000: MFC2(cpu); break;
                    case 0b0_0010: CFC2(cpu); break;
                    case 0b0_0100: MTC2(cpu); break;
                    case 0b0_0110: CTC2(cpu); break;
                    default: EXCEPTION(cpu, EX.ILLEGAL_INSTR, cpu.instr.id); break;
                }
            } else {
                cpu.gte.execute(cpu.instr.value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MFC2(CPU cpu) => delayedLoad(cpu, cpu.instr.rt, cpu.gte.loadData(cpu.instr.rd));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CFC2(CPU cpu) => delayedLoad(cpu, cpu.instr.rt, cpu.gte.loadControl(cpu.instr.rd));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MTC2(CPU cpu) => cpu.gte.writeData(cpu.instr.rd, cpu.GPR[cpu.instr.rt]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CTC2(CPU cpu) => cpu.gte.writeControl(cpu.instr.rd, cpu.GPR[cpu.instr.rt]);

        private static void LWC2(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;

            if (StrictAddressExceptions && (addr & 0x3) != 0) {
                cpu.COP0_GPR[BADA] = addr;
                EXCEPTION(cpu, EX.LOAD_ADRESS_ERROR, cpu.instr.id);
            } else {
                uint value = cpu.bus.load32(addr);
                cpu.gte.writeData(cpu.instr.rt, value);
            }
        }

        private static void SWC2(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;

            if (StrictAddressExceptions && (addr & 0x3) != 0) {
                cpu.COP0_GPR[BADA] = addr;
                EXCEPTION(cpu, EX.STORE_ADRESS_ERROR, cpu.instr.id);
            } else {
                cpu.bus.write32(addr, cpu.gte.loadData(cpu.instr.rt));
            }
        }

        private static void LB(CPU cpu) { //todo redo this as it unnecesary load32
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;
            uint value = (uint)(sbyte)cpu.LoadData8(addr);
            delayedLoad(cpu, cpu.instr.rt, value);
        }

        private static void LBU(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;
            uint value = cpu.LoadData8(addr);
            delayedLoad(cpu, cpu.instr.rt, value);
        }

        private static void LH(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;

            if (StrictAddressExceptions && (addr & 0x1) != 0) {
                cpu.COP0_GPR[BADA] = addr;
                EXCEPTION(cpu, EX.LOAD_ADRESS_ERROR, cpu.instr.id);
            } else {
                uint value = (uint)(short)cpu.LoadData16(addr);
                delayedLoad(cpu, cpu.instr.rt, value);
            }
        }

        private static void LHU(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;

            if (StrictAddressExceptions && (addr & 0x1) != 0) {
                cpu.COP0_GPR[BADA] = addr;
                EXCEPTION(cpu, EX.LOAD_ADRESS_ERROR, cpu.instr.id);
            } else {
                uint value = cpu.LoadData16(addr);
                delayedLoad(cpu, cpu.instr.rt, value);
            }
        }

        private static void LW(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;

            if (StrictAddressExceptions && (addr & 0x3) != 0) {
                cpu.COP0_GPR[BADA] = addr;
                EXCEPTION(cpu, EX.LOAD_ADRESS_ERROR, cpu.instr.id);
            } else {
                uint value = cpu.LoadData32(addr);
                delayedLoad(cpu, cpu.instr.rt, value);
            }
        }

        private static void LWL(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;
            uint aligned_addr = addr & 0xFFFF_FFFC;
            uint aligned_load = cpu.LoadData32(aligned_addr);

            uint LRValue = cpu.GPR[cpu.instr.rt];

            if (cpu.instr.rt == cpu.memoryLoad.register) {
                LRValue = cpu.memoryLoad.value;
            }

            int shift = (int)((addr & 0x3) << 3);
            uint mask = (uint)0x00FF_FFFF >> shift;
            uint value = (LRValue & mask) | (aligned_load << (24 - shift)); 

            delayedLoad(cpu, cpu.instr.rt, value);
        }

        private static void LWR(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;
            uint aligned_addr = addr & 0xFFFF_FFFC;
            uint aligned_load = cpu.LoadData32(aligned_addr);

            uint LRValue = cpu.GPR[cpu.instr.rt];

            if (cpu.instr.rt == cpu.memoryLoad.register) {
                LRValue = cpu.memoryLoad.value;
            }

            int shift = (int)((addr & 0x3) << 3);
            uint mask = 0xFFFF_FF00 << (24 - shift);
            uint value = (LRValue & mask) | (aligned_load >> shift);

            delayedLoad(cpu, cpu.instr.rt, value);
        }

        private static void SB(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;
            cpu.StoreData8(addr, (byte)cpu.GPR[cpu.instr.rt]);
        }

        private static void SH(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;

            if (StrictAddressExceptions && (addr & 0x1) != 0) {
                cpu.COP0_GPR[BADA] = addr;
                EXCEPTION(cpu, EX.STORE_ADRESS_ERROR, cpu.instr.id);
            } else {
                cpu.StoreData16(addr, (ushort)cpu.GPR[cpu.instr.rt]);
            }
        }

        private static void SW(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;

            if (StrictAddressExceptions && (addr & 0x3) != 0) {
                cpu.COP0_GPR[BADA] = addr;
                EXCEPTION(cpu, EX.STORE_ADRESS_ERROR, cpu.instr.id);
            } else {
                cpu.StoreData32(addr, cpu.GPR[cpu.instr.rt]);
            }
        }

        private static void SWR(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;
            uint aligned_addr = addr & 0xFFFF_FFFC;
            uint aligned_load = cpu.LoadData32(aligned_addr);

            int shift = (int)((addr & 0x3) << 3);
            uint mask = (uint)0x00FF_FFFF >> (24 - shift);
            uint value = (aligned_load & mask) | (cpu.GPR[cpu.instr.rt] << shift);

            cpu.StoreData32(aligned_addr, value);
        }

        private static void SWL(CPU cpu) {
            uint addr = cpu.GPR[cpu.instr.rs] + cpu.instr.imm_s;
            uint aligned_addr = addr & 0xFFFF_FFFC;
            uint aligned_load = cpu.LoadData32(aligned_addr);

            int shift = (int)((addr & 0x3) << 3);
            uint mask = 0xFFFF_FF00 << shift;
            uint value = (aligned_load & mask) | (cpu.GPR[cpu.instr.rt] >> (24 - shift));

            cpu.StoreData32(aligned_addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BRANCH(CPU cpu) {
            cpu.opcodeTookBranch = true;
            cpu.PC_Predictor = cpu.PC + (cpu.instr.imm_s << 2);
        }


        // Special Table Opcodes (Nested on Opcode 0x00 with additional function param)

        private static void SLL(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rt] << (int)cpu.instr.sa);

        private static void SRL(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rt] >> (int)cpu.instr.sa);

        private static void SRA(CPU cpu) => cpu.setGPR(cpu.instr.rd, (uint)((int)cpu.GPR[cpu.instr.rt] >> (int)cpu.instr.sa));

        private static void SLLV(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rt] << (int)(cpu.GPR[cpu.instr.rs] & 0x1F));

        private static void SRLV(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rt] >> (int)(cpu.GPR[cpu.instr.rs] & 0x1F));

        private static void SRAV(CPU cpu) => cpu.setGPR(cpu.instr.rd, (uint)((int)cpu.GPR[cpu.instr.rt] >> (int)(cpu.GPR[cpu.instr.rs] & 0x1F)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void JR(CPU cpu) {
            cpu.opcodeIsBranch = true;
            cpu.opcodeTookBranch = true;
            cpu.PC_Predictor = cpu.GPR[cpu.instr.rs];
        }

        private static void SYSCALL(CPU cpu) => EXCEPTION(cpu, EX.SYSCALL, cpu.instr.id);

        private static void BREAK(CPU cpu) => EXCEPTION(cpu, EX.BREAK);

        private static void JALR(CPU cpu) {
            cpu.setGPR(cpu.instr.rd, cpu.PC_Predictor);
            JR(cpu);
        }

        private static void MFHI(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.HI);

        private static void MTHI(CPU cpu) => cpu.HI = cpu.GPR[cpu.instr.rs];

        private static void MFLO(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.LO);

        private static void MTLO(CPU cpu) => cpu.LO = cpu.GPR[cpu.instr.rs];

        private static void MULT(CPU cpu) {
            long value = (long)(int)cpu.GPR[cpu.instr.rs] * (long)(int)cpu.GPR[cpu.instr.rt]; //sign extend to pass amidog cpu test

            cpu.HI = (uint)(value >> 32);
            cpu.LO = (uint)value;
        }

        private static void MULTU(CPU cpu) {
            ulong value = (ulong)cpu.GPR[cpu.instr.rs] * (ulong)cpu.GPR[cpu.instr.rt]; //sign extend to pass amidog cpu test

            cpu.HI = (uint)(value >> 32);
            cpu.LO = (uint)value;
        }

        private static void DIV(CPU cpu) {
            int n = (int)cpu.GPR[cpu.instr.rs];
            int d = (int)cpu.GPR[cpu.instr.rt];

            if (d == 0) {
                cpu.HI = (uint)n;
                if (n >= 0) {
                    cpu.LO = 0xFFFF_FFFF;
                } else {
                    cpu.LO = 1;
                }
            } else if ((uint)n == 0x8000_0000 && d == -1) {
                cpu.HI = 0;
                cpu.LO = 0x8000_0000;
            } else {
                cpu.HI = (uint)(n % d);
                cpu.LO = (uint)(n / d);
            }
        }

        private static void DIVU(CPU cpu) {
            uint n = cpu.GPR[cpu.instr.rs];
            uint d = cpu.GPR[cpu.instr.rt];

            if (d == 0) {
                cpu.HI = n;
                cpu.LO = 0xFFFF_FFFF;
            } else {
                cpu.HI = n % d;
                cpu.LO = n / d;
            }
        }

        private static void ADD(CPU cpu) {
            uint rs = cpu.GPR[cpu.instr.rs];
            uint rt = cpu.GPR[cpu.instr.rt];
            uint result = rs + rt;

#if CPU_EXCEPTIONS
            if (checkOverflow(rs, rt, result)) {
                EXCEPTION(cpu, EX.OVERFLOW, cpu.instr.id);
            } else {
                cpu.setGPR(cpu.instr.rd, result);
            }
#else
            cpu.setGPR(cpu.instr.rd, result);
#endif
        }

        private static void ADDU(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rs] + cpu.GPR[cpu.instr.rt]);

        private static void SUB(CPU cpu) {
            uint rs = cpu.GPR[cpu.instr.rs];
            uint rt = cpu.GPR[cpu.instr.rt];
            uint result = rs - rt;

#if CPU_EXCEPTIONS
            if (checkUnderflow(rs, rt, result)) {
                EXCEPTION(cpu, EX.OVERFLOW, cpu.instr.id);
            } else {
                cpu.setGPR(cpu.instr.rd, result);
            }
#else
            cpu.setGPR(cpu.instr.rd, result);
#endif
        }

        private static void SUBU(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rs] - cpu.GPR[cpu.instr.rt]);

        private static void AND(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rs] & cpu.GPR[cpu.instr.rt]);

        private static void OR(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rs] | cpu.GPR[cpu.instr.rt]);

        private static void XOR(CPU cpu) => cpu.setGPR(cpu.instr.rd, cpu.GPR[cpu.instr.rs] ^ cpu.GPR[cpu.instr.rt]);

        private static void NOR(CPU cpu) => cpu.setGPR(cpu.instr.rd, ~(cpu.GPR[cpu.instr.rs] | cpu.GPR[cpu.instr.rt]));

        private static void SLT(CPU cpu) {
            bool condition = (int)cpu.GPR[cpu.instr.rs] < (int)cpu.GPR[cpu.instr.rt];
            cpu.setGPR(cpu.instr.rd, condition ? 1u : 0u);
        }

        private static void SLTU(CPU cpu) {
            bool condition = cpu.GPR[cpu.instr.rs] < cpu.GPR[cpu.instr.rt];
            cpu.setGPR(cpu.instr.rd, condition ? 1u : 0u);
        }


        // Accesory methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool checkOverflow(uint a, uint b, uint r) => ((r ^ a) & (r ^ b) & 0x8000_0000) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool checkUnderflow(uint a, uint b, uint r) => ((r ^ a) & (a ^ b) & 0x8000_0000) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint LoadData32(uint virtualAddress) {
            if (!dontIsolateCache) {
                return 0;
            }

            if (bus.TryLoadData32Fast(virtualAddress, out uint fastValue)) {
                return fastValue;
            }

            return bus.load32(virtualAddress);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint LoadData16(uint virtualAddress) {
            if (!dontIsolateCache) {
                return 0;
            }

            if (bus.TryLoadData16Fast(virtualAddress, out ushort fastValue)) {
                return fastValue;
            }

            return (ushort)bus.load32(virtualAddress);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint LoadData8(uint virtualAddress) {
            if (!dontIsolateCache) {
                return 0;
            }

            if (bus.TryLoadData8Fast(virtualAddress, out byte fastValue)) {
                return fastValue;
            }

            return (byte)bus.load32(virtualAddress);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StoreData32(uint virtualAddress, uint value) {
            if (!dontIsolateCache) {
                return;
            }

            if (bus.TryStoreData32Fast(virtualAddress, value)) {
                return;
            }

            bus.write32(virtualAddress, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StoreData16(uint virtualAddress, ushort value) {
            if (!dontIsolateCache) {
                return;
            }

            if (bus.TryStoreData16Fast(virtualAddress, value)) {
                return;
            }

            bus.write16(virtualAddress, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StoreData8(uint virtualAddress, byte value) {
            if (!dontIsolateCache) {
                return;
            }

            if (bus.TryStoreData8Fast(virtualAddress, value)) {
                return;
            }

            bus.write8(virtualAddress, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool UsesCacheIsolatedDataAccess(uint virtualAddress) {
            if (!ExperimentalInstructionCache) {
                return false;
            }

            if (dontIsolateCache) {
                return false;
            }

            uint physical = virtualAddress & 0x1FFF_FFFF;
            return _instructionCacheRuntimeAllowed
                && physical < 0x1F00_0000
                && IsInstructionCacheable(virtualAddress);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void setGPR(uint regN, uint value) {
            writeBack.register = regN;
            writeBack.value = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void delayedLoad(CPU cpu, uint regN, uint value) {
            cpu.delayedMemoryLoad.register = regN;
            cpu.delayedMemoryLoad.value = value;
        }

        private void TTY() {
            if (PC == 0x00000B0 && GPR[9] == 0x3D || PC == 0x00000A0 && GPR[9] == 0x3C) {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.Write((char)GPR[4]);
                Console.ResetColor();
            }
        }
    }
}
