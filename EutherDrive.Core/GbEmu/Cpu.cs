using System;
using Serilog;

namespace EutherDrive.Core.GbEmu
{
    // Structure to hold opcode information for debugging
    internal struct OpcodeInfo
    {
        public required byte Opcode{ get; init; }
        public required string Mnemonic{ get; init; }
        public required string Description{ get; init; }
        public required string Cycles{ get; init; }
        public required int Bytes{ get; init; }
        //public string[] Parameters; //Good for docs but not much else, so unused for now
        public required Func<Cpu, Mmu, string> GetParameterString{ get; init; }
        public required Func<Cpu, Mmu, int> Execute{ get; init; }

        public bool AutoIncrementPC { get; init; } = true;

        public OpcodeInfo() { }
    }

    internal class Cpu
    {
        // Registers
        public byte A, F, B, C, D, E, H, L;
        public ushort SP; // Stack Pointer
        public ushort PC; // Program Counter

        [NonSerialized]
        private Mmu? _mmu;
        private Mmu Mmu => _mmu ?? throw new InvalidOperationException("MMU not initialized");

        // Debugging and stepping
        [field: NonSerialized]
        public bool IsStepping { get; set; } = false;
        [field: NonSerialized]
        public bool IsPaused { get; set; } = false;
        [field: NonSerialized]
        public OpcodeInfo LastExecutedOpcode { get; private set; }
        [field: NonSerialized]
        public string LastOpcodeLog { get; private set; } = "";

        // Helper properties for 16-bit register pairs
        public ushort AF { get => (ushort)((A << 8) | F); set { A = (byte)(value >> 8); F = (byte)(value & 0x00F0); } }
        public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)(value & 0x00FF); } }
        public ushort DE { get => (ushort)((D << 8) | E); set { D = (byte)(value >> 8); E = (byte)(value & 0x00FF); } }
        public ushort HL { get => (ushort)((H << 8) | L); set { H = (byte)(value >> 8); L = (byte)(value & 0x00FF); } }

        public bool FlagZ { get { return (F & 0x80) != 0; } set { F = value ? (byte)(F | 0x80) : (byte)(F & ~0x80); } }
        public bool FlagN { get { return (F & 0x40) != 0; } set { F = value ? (byte)(F | 0x40) : (byte)(F & ~0x40); } }
        public bool FlagH { get { return (F & 0x20) != 0; } set { F = value ? (byte)(F | 0x20) : (byte)(F & ~0x20); } }
        public bool FlagC { get { return (F & 0x10) != 0; } set { F = value ? (byte)(F | 0x10) : (byte)(F & ~0x10); } }

        private bool _interruptMasterEnable;

        private int _enableInterruptsScheduled = 0; // For EI delay; 0 = no, >0 = countdown

        private bool is_halted;
        private bool halt_bug_active = false;

        // Opcode lookup table
        private static readonly OpcodeInfo[] OpcodeTable = new OpcodeInfo[256];

        private static readonly OpcodeInfo[] ExtendedOpcodeTable = new OpcodeInfo[256];

        // 0xFFFF - Interrupt Enable Register
        public byte ie_register { get; set; }

        // 0xFF0F - Interrupt Flag Register
        private byte _interruptFlagRegister;
        public byte if_register
        {
            get { return (byte)(_interruptFlagRegister | 0xE0); } // Upper 3 bits are always 1 on read
            set { _interruptFlagRegister = (byte)(value & 0x1F); } // Only lower 5 bits are writeable
        }

        public enum Interrupt
        {
            VBlank = 0, // V-Blank Interrupt, Priority 1
            LcdStat = 1, // LCD STAT Interrupt, Priority 2
            Timer = 2, // Timer Interrupt, Priority 3
            Serial = 3, // Serial Interrupt, Priority 4
            Joypad = 4  // Joypad Interrupt, Priority 5
        }

        static Cpu()
        {
            InitializeOpcodeTable();
            InitializeExtendedOpcodeTable();
        }

        public Cpu(Mmu? mmu)
        {
            _mmu = mmu;
            // Initialize registers to boot ROM start values
            // These values are the same for both DMG and GBC
            PC = 0x0000;
            SP = 0xFFFE;  // Boot ROM sets this with first instruction
            AF = 0x01B0;  // A=0x01, F=0xB0 (Z=1, N=0, H=1, C=1)
            BC = 0x0013;  // B=0x00, C=0x13
            DE = 0x00D8;  // D=0x00, E=0xD8
            HL = 0x014D;  // H=0x01, L=0x4D
        }

        /// <summary>
        /// Initialize CPU registers for Game Boy Color mode
        /// GBC has different initial register values after boot ROM execution
        /// </summary>
        public void InitializeForGbc()
        {
            // GBC-specific register initialization values
            // These are the values the GBC boot ROM sets up
            PC = 0x0100;  // GBC boot ROM jumps to 0x0100 after completion
            SP = 0xFFFE;  // Stack pointer remains the same
            AF = 0x1180;  // A=0x11, F=0x80 (Z=1, N=0, H=0, C=0) - GBC specific
            BC = 0x0000;  // B=0x00, C=0x00 - GBC specific
            DE = 0x0008;  // D=0x00, E=0x08 - GBC specific  
            HL = 0x007C;  // H=0x00, L=0x7C - GBC specific
            
            Log.Information("CPU initialized for Game Boy Color mode");
        }

        /// <summary>
        /// Initialize CPU registers for DMG (original Game Boy) mode
        /// </summary>
        public void InitializeForDmg()
        {
            // DMG-specific register initialization values
            PC = 0x0100;  // DMG boot ROM jumps to 0x0100 after completion
            SP = 0xFFFE;  // Stack pointer
            AF = 0x01B0;  // A=0x01, F=0xB0 (Z=1, N=0, H=1, C=1) - DMG specific
            BC = 0x0013;  // B=0x00, C=0x13 - DMG specific
            DE = 0x00D8;  // D=0x00, E=0xD8 - DMG specific
            HL = 0x014D;  // H=0x01, L=0x4D - DMG specific
            
            Log.Information("CPU initialized for DMG mode");
        }

        public void SetMmu(Mmu mmu)
        {
            _mmu = mmu;
        }

        // Initialize the opcode table with all GameBoy opcodes
        private static void InitializeOpcodeTable()
        {

            /*
            "n" → 8-bit immediate
            "nn" → 16-bit immediate
            "r" → register
            "d8" → immediate 8-bit data
            "d16" → immediate 16-bit data
            "a16" → 16-bit address
            */

            // 8-bit Load Instructions
            OpcodeTable[0x06] = new OpcodeInfo
            {
                Opcode = 0x06,
                Mnemonic = "LD B,n",
                Description = "Load immediate value into B",
                Cycles ="8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD B, 0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) =>
                {
                    cpu.B = mmu.ReadByte((ushort)(cpu.PC + 1));
                    return 8;
                }
            };
            OpcodeTable[0x07] = new OpcodeInfo { Opcode = 0x07, Mnemonic = "RLCA", Description = "Rotate A left", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "RLCA", Execute = (c,m) => { c.RLCA(); return 4; } };

            OpcodeTable[0x08] = new OpcodeInfo
            {
                Opcode = 0x08,
                Mnemonic = "LD (nn),SP",
                Description = "Store SP at immediate address",
                Cycles = "20",
                Bytes = 3,
                GetParameterString = (cpu, mmu) => 
                {
                    // Read the 16-bit immediate address for logging purposes
                    ushort addr = mmu.ReadWord((ushort)(cpu.PC + 1));
                    return $"LD (0x{addr:X4}),SP";
                },
                Execute = (cpu, mmu) => 
                {
                    // 1. Read the 16-bit immediate address from the code
                    ushort addr = mmu.ReadWord((ushort)(cpu.PC + 1));

                    mmu.WriteWord(addr, cpu.SP);
                    
                    // This operation takes 20 clock cycles
                    return 20;
                }
            };

            OpcodeTable[0x09] = new OpcodeInfo
            {
                Opcode = 0x09,
                Mnemonic = "ADD HL,BC",
                Description = "Add BC to HL",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"ADD HL,BC (HL=0x{cpu.HL:X4}, BC=0x{cpu.BC:X4})",
                Execute = (cpu, mmu) =>
                {
                    int result = cpu.HL + cpu.BC;
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.HL & 0x0FFF) + (cpu.BC & 0x0FFF) > 0x0FFF;
                    cpu.FlagC = result > 0xFFFF;
                    cpu.HL = (ushort)result;
                    return 8;
                }
            };

            OpcodeTable[0x0A] = new OpcodeInfo
            {
                Opcode = 0x0A,
                Mnemonic = "LD A,(BC)",
                Description = "Load from address in BC into A",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"LD A,(BC) (BC=0x{cpu.BC:X4})",
                Execute = (cpu, mmu) =>
                {
                    cpu.A = mmu.ReadByte(cpu.BC);
                    return 8;
                }
            };

            OpcodeTable[0x0E] = new OpcodeInfo
            {
                Opcode = 0x0E,
                Mnemonic = "LD C,n",
                Description = "Load immediate value into C",
                Cycles ="8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD C, 0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) => {
                    cpu.C = mmu.ReadByte((ushort)(cpu.PC + 1));
                    return 8;
                }
            };
            OpcodeTable[0x0F] = new OpcodeInfo { Opcode = 0x0F, Mnemonic = "RRCA", Description = "Rotate A right", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "RRCA", Execute = (c,m) => { c.RRCA(); return 4; } };
            OpcodeTable[0x10] = new OpcodeInfo
            {
                Opcode = 0x10,
                Mnemonic = "STOP",
                Description = "Stop CPU",
                Cycles = "4",
                Bytes = 2,
                GetParameterString = (c, m) => "STOP",
                Execute = (cpu, mmu) =>
                {
                    if (mmu.IsGameBoyColor && mmu.IsSpeedSwitchRequested())
                    {
                        mmu.PerformSpeedSwitch();
                        // Speed switching takes time, but for emulation, 
                        // the cycle cost is minimal and handled by the switch itself.
                    }
                    else
                    {
                        cpu.is_halted = true; // For DMG or if no switch is requested
                    }
                    return 4; 
                }
            };
            OpcodeTable[0x16] = new OpcodeInfo
            {
                Opcode = 0x16,
                Mnemonic = "LD D,n",
                Description = "Load immediate value into D",
                Cycles ="8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD D, 0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) =>
                {
                    cpu.D = mmu.ReadByte((ushort)(cpu.PC + 1));
                    return 8;
                }
            };
            OpcodeTable[0x17] = new OpcodeInfo { Opcode = 0x17, Mnemonic = "RLA", Description = "Rotate A left through carry", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "RLA", Execute = (c,m) => { c.RLA(); return 4; } };
            OpcodeTable[0x1E] = new OpcodeInfo
            {
                Opcode = 0x1E,
                Mnemonic = "LD E,n",
                Description = "Load immediate value into E",
                Cycles ="8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD E, 0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) =>
                {
                    cpu.E = mmu.ReadByte((ushort)(cpu.PC + 1));
                    return 8;
                }
            };
            OpcodeTable[0x1F] = new OpcodeInfo { Opcode = 0x1F, Mnemonic = "RRA", Description = "Rotate A right through carry", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "RRA", Execute = (c,m) => { c.RRA(); return 4; } };
            OpcodeTable[0x26] = new OpcodeInfo
            {
                Opcode = 0x26,
                Mnemonic = "LD H,n",
                Description = "Load immediate value into H",
                Cycles ="8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD H, 0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) =>
                {
                    cpu.H = mmu.ReadByte((ushort)(cpu.PC + 1));
                    return 8;
                }
            };
            OpcodeTable[0x27] = new OpcodeInfo { Opcode = 0x27, Mnemonic = "DAA", Description = "Decimal adjust A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "DAA", Execute = (c,m) => { c.DAA(); return 4; } };
            OpcodeTable[0x2E] = new OpcodeInfo
            {
                Opcode = 0x2E,
                Mnemonic = "LD L,n",
                Description = "Load immediate value into L",
                Cycles ="8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD L, 0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) =>
                {
                    cpu.L = mmu.ReadByte((ushort)(cpu.PC + 1));
                    return 8;
                }
            };
            OpcodeTable[0x2F] = new OpcodeInfo { Opcode = 0x2F, Mnemonic = "CPL", Description = "Complement A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "CPL", Execute = (c,m) => { c.CPL(); return 4; } };
            OpcodeTable[0x37] = new OpcodeInfo { Opcode = 0x37, Mnemonic = "SCF", Description = "Set carry flag", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "SCF", Execute = (c,m) => { c.SCF(); return 4; } };
            OpcodeTable[0x3E] = new OpcodeInfo
            {
                Opcode = 0x3E,
                Mnemonic = "LD A,n",
                Description = "Load immediate value into A",
                Cycles ="8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD A, 0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) =>
                {
                    cpu.A = mmu.ReadByte((ushort)(cpu.PC + 1));
                    return 8;
                }
            };
            OpcodeTable[0x3F] = new OpcodeInfo { Opcode = 0x3F, Mnemonic = "CCF", Description = "Complement carry flag", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => "CCF", Execute = (c,m) => { c.CCF(); return 4; } };

            // 16-bit Load Instructions
            OpcodeTable[0x01] = new OpcodeInfo
            {
                Opcode = 0x01,
                Mnemonic = "LD BC,nn",
                Description = "Load immediate 16-bit value into BC",
                Cycles = "12",
                Bytes = 3,
                GetParameterString = (cpu, mmu) => $"LD BC, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}",
                Execute = (cpu, mmu) =>
                {
                    cpu.C = mmu.ReadByte((ushort)(cpu.PC + 1));
                    cpu.B = mmu.ReadByte((ushort)(cpu.PC + 2));
                    return 12;
                }
            };
            OpcodeTable[0x11] = new OpcodeInfo
            {
                Opcode = 0x11,
                Mnemonic = "LD DE,nn",
                Description = "Load immediate 16-bit value into DE",
                Cycles = "12",
                Bytes = 3,
                GetParameterString = (cpu, mmu) => $"LD DE, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}",
                Execute = (cpu, mmu) =>
                {
                    cpu.E = mmu.ReadByte((ushort)(cpu.PC + 1));
                    cpu.D = mmu.ReadByte((ushort)(cpu.PC + 2));
                    return 12;
                }
            };
            OpcodeTable[0x21] = new OpcodeInfo
            {
                Opcode = 0x21,
                Mnemonic = "LD HL,nn",
                Description = "Load immediate 16-bit value into HL",
                Cycles = "12",
                Bytes = 3,
                GetParameterString = (cpu, mmu) => $"LD HL, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}",
                Execute = (cpu, mmu) =>
                {
                    cpu.L = mmu.ReadByte((ushort)(cpu.PC + 1));
                    cpu.H = mmu.ReadByte((ushort)(cpu.PC + 2));
                    return 12;
                }
            };
            OpcodeTable[0x31] = new OpcodeInfo
            {
                Opcode = 0x31,
                Mnemonic = "LD SP,nn",
                Description = "Load immediate 16-bit value into SP",
                Cycles = "12",
                Bytes = 3,
                GetParameterString = (cpu, mmu) => $"LD SP, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}",
                Execute = (cpu, mmu) =>
                {
                    byte spLo = mmu.ReadByte((ushort)(cpu.PC + 1));
                    byte spHi = mmu.ReadByte((ushort)(cpu.PC + 2));
                    cpu.SP = (ushort)((spHi << 8) | spLo);
                    return 12;
                }
            };

            OpcodeTable[0xE8] = new OpcodeInfo
            {
                Opcode = 0xE8,
                Mnemonic = "ADD SP,r8",
                Description = "Add signed immediate to SP",
                Cycles = "16",
                Bytes = 2,
                GetParameterString = (c, m) => $"ADD SP, {(sbyte)m.ReadByte((ushort)(c.PC + 1))}",
                Execute = (c, m) =>
                {
                    sbyte offset = (sbyte)m.ReadByte((ushort)(c.PC + 1));
                    ushort result = (ushort)(c.SP + offset);
                    // Flags are weird for this one
                    c.FlagZ = false;
                    c.FlagN = false;
                    c.FlagH = ((c.SP & 0x0F) + (offset & 0x0F)) > 0x0F;
                    c.FlagC = ((c.SP & 0xFF) + (offset & 0xFF)) > 0xFF;
                    c.SP = result;
                    return 16;
                }
            };

            OpcodeTable[0xF9] = new OpcodeInfo
            {
                Opcode = 0xF9,
                Mnemonic = "LD SP,HL",
                Description = "Load HL into SP",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (c, m) => "LD SP,HL",
                Execute = (c, m) =>
                {
                    c.SP = c.HL;
                    return 8;
                }
            };

            OpcodeTable[0xF8] = new OpcodeInfo
            {
                Opcode = 0xF8,
                Mnemonic = "LD HL,SP+r8",
                Description = "Load SP + signed immediate into HL",
                Cycles = "12",
                Bytes = 2,
                GetParameterString = (c, m) => $"LD HL, SP+{(sbyte)m.ReadByte((ushort)(c.PC + 1))}",
                Execute = (c, m) =>
                {
                    sbyte offset = (sbyte)m.ReadByte((ushort)(c.PC + 1));
                    ushort result = (ushort)(c.SP + offset);
                    // Flags are the same as ADD SP,r8
                    c.FlagZ = false;
                    c.FlagN = false;
                    c.FlagH = ((c.SP & 0x0F) + (offset & 0x0F)) > 0x0F;
                    c.FlagC = ((c.SP & 0xFF) + (offset & 0xFF)) > 0xFF;
                    c.HL = result;
                    return 12;
                }
            };

            // 16-bit Increment/Decrement Instructions
            OpcodeTable[0x03] = new OpcodeInfo
            {
                Opcode = 0x03,
                Mnemonic = "INC BC",
                Description = "Increment 16-bit register BC",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC BC (BC={Hex4(cpu.BC)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.BC++;
                    return 8;
                }
            };
            OpcodeTable[0x0B] = new OpcodeInfo
            {
                Opcode = 0x0B,
                Mnemonic = "DEC BC",
                Description = "Decrement 16-bit register BC",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC BC (BC={Hex4(cpu.BC)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.BC--;
                    return 8;
                }
            };
            OpcodeTable[0x13] = new OpcodeInfo
            {
                Opcode = 0x13,
                Mnemonic = "INC DE",
                Description = "Increment 16-bit register DE",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC DE (DE={Hex4(cpu.DE)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.DE++;
                    return 8;
                }
            };
            OpcodeTable[0x1B] = new OpcodeInfo
            {
                Opcode = 0x1B,
                Mnemonic = "DEC DE",
                Description = "Decrement 16-bit register DE",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC DE (DE={Hex4(cpu.DE)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.DE--;
                    return 8;
                }
            };
            OpcodeTable[0x23] = new OpcodeInfo
            {
                Opcode = 0x23,
                Mnemonic = "INC HL",
                Description = "Increment 16-bit register HL",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC HL (HL={Hex4(cpu.HL)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.HL++;
                    return 8;
                }
            };
            OpcodeTable[0x2B] = new OpcodeInfo
            {
                Opcode = 0x2B,
                Mnemonic = "DEC HL",
                Description = "Decrement 16-bit register HL",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC HL (HL={Hex4(cpu.HL)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.HL--;
                    return 8;
                }
            };
            OpcodeTable[0x33] = new OpcodeInfo
            {
                Opcode = 0x33,
                Mnemonic = "INC SP",
                Description = "Increment 16-bit register SP",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC SP (SP={Hex4(cpu.SP)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.SP++;
                    return 8;
                }
            };
            OpcodeTable[0x3B] = new OpcodeInfo
            {
                Opcode = 0x3B,
                Mnemonic = "DEC SP",
                Description = "Decrement 16-bit register SP",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC SP (SP={Hex4(cpu.SP)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.SP--;
                    return 8;
                }
            };

            OpcodeTable[0x39] = new OpcodeInfo
            {
                Opcode = 0x39,
                Mnemonic = "ADD HL,SP",
                Description = "Add SP to HL",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"ADD HL,SP (HL=0x{cpu.HL:X4}, SP=0x{cpu.SP:X4})",
                Execute = (cpu, mmu) =>
                {
                    // Use a 32-bit integer to detect overflow for the carry flag.
                    int result = cpu.HL + cpu.SP;

                    // --- Set Flags ---
                    // N (Subtract) is always reset to 0.
                    cpu.FlagN = false;

                    // H (Half Carry) is set if a carry occurs from bit 11 to bit 12.
                    cpu.FlagH = (cpu.HL & 0x0FFF) + (cpu.SP & 0x0FFF) > 0x0FFF;

                    // C (Carry) is set if a carry occurs from bit 15.
                    cpu.FlagC = result > 0xFFFF;

                    // Note: The Z (Zero) flag is not affected by this instruction.

                    // Store the 16-bit result back in HL.
                    cpu.HL = (ushort)result;

                    // This operation takes 8 clock cycles.
                    return 8;
                }
            };

            // 8-bit Increment/Decrement Instructions
            OpcodeTable[0x04] = new OpcodeInfo
            {
                Opcode = 0x04,
                Mnemonic = "INC B",
                Description = "Increment 8-bit register B",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC B (B={Hex2(cpu.B)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.B & 0x0F) == 0x0F;
                    cpu.B++;
                    cpu.FlagZ = cpu.B == 0;
                    return 4;
                }
            };
            OpcodeTable[0x05] = new OpcodeInfo
            {
                Opcode = 0x05,
                Mnemonic = "DEC B",
                Description = "Decrement 8-bit register B",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC B (B={Hex2(cpu.B)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = true;
                    cpu.FlagH = (cpu.B & 0x0F) == 0x00;
                    cpu.B--;
                    cpu.FlagZ = cpu.B == 0;
                    return 4;
                }
            };
            OpcodeTable[0x0C] = new OpcodeInfo
            {
                Opcode = 0x0C,
                Mnemonic = "INC C",
                Description = "Increment 8-bit register C",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC C (C={Hex2(cpu.C)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.C & 0x0F) == 0x0F;
                    cpu.C++;
                    cpu.FlagZ = cpu.C == 0;
                    return 4;
                }
            };
            OpcodeTable[0x0D] = new OpcodeInfo
            {
                Opcode = 0x0D,
                Mnemonic = "DEC C",
                Description = "Decrement 8-bit register C",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC C (C={Hex2(cpu.C)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = true;
                    cpu.FlagH = (cpu.C & 0x0F) == 0x00;
                    cpu.C--;
                    cpu.FlagZ = cpu.C == 0;
                    return 4;
                }
            };
            OpcodeTable[0x14] = new OpcodeInfo
            {
                Opcode = 0x14,
                Mnemonic = "INC D",
                Description = "Increment 8-bit register D",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC D (D={Hex2(cpu.D)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.D & 0x0F) == 0x0F;
                    cpu.D++;
                    cpu.FlagZ = cpu.D == 0;
                    return 4;
                }
            };
            OpcodeTable[0x15] = new OpcodeInfo
            {
                Opcode = 0x15,
                Mnemonic = "DEC D",
                Description = "Decrement 8-bit register D",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC D (D={Hex2(cpu.D)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = true;
                    cpu.FlagH = (cpu.D & 0x0F) == 0x00;
                    cpu.D--;
                    cpu.FlagZ = cpu.D == 0;
                    return 4;
                }
            };
            OpcodeTable[0x1C] = new OpcodeInfo
            {
                Opcode = 0x1C,
                Mnemonic = "INC E",
                Description = "Increment 8-bit register E",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC E (E={Hex2(cpu.E)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.E & 0x0F) == 0x0F;
                    cpu.E++;
                    cpu.FlagZ = cpu.E == 0;
                    return 4;
                }
            };
            OpcodeTable[0x1D] = new OpcodeInfo
            {
                Opcode = 0x1D,
                Mnemonic = "DEC E",
                Description = "Decrement 8-bit register E",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC E (E={Hex2(cpu.E)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = true;
                    cpu.FlagH = (cpu.E & 0x0F) == 0x00;
                    cpu.E--;
                    cpu.FlagZ = cpu.E == 0;
                    return 4;
                }
            };
            OpcodeTable[0x24] = new OpcodeInfo
            {
                Opcode = 0x24,
                Mnemonic = "INC H",
                Description = "Increment 8-bit register H",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC H (H={Hex2(cpu.H)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.H & 0x0F) == 0x0F;
                    cpu.H++;
                    cpu.FlagZ = cpu.H == 0;
                    return 4;
                }
            };
            OpcodeTable[0x25] = new OpcodeInfo
            {
                Opcode = 0x25,
                Mnemonic = "DEC H",
                Description = "Decrement 8-bit register H",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC H (H={Hex2(cpu.H)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = true;
                    cpu.FlagH = (cpu.H & 0x0F) == 0x00;
                    cpu.H--;
                    cpu.FlagZ = cpu.H == 0;
                    return 4;
                }
            };
            OpcodeTable[0x2C] = new OpcodeInfo
            {
                Opcode = 0x2C,
                Mnemonic = "INC L",
                Description = "Increment 8-bit register L",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC L (L={Hex2(cpu.L)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.L & 0x0F) == 0x0F;
                    cpu.L++;
                    cpu.FlagZ = cpu.L == 0;
                    return 4;
                }
            };
            OpcodeTable[0x2D] = new OpcodeInfo
            {
                Opcode = 0x2D,
                Mnemonic = "DEC L",
                Description = "Decrement 8-bit register L",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC L (L={Hex2(cpu.L)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = true;
                    cpu.FlagH = (cpu.L & 0x0F) == 0x00;
                    cpu.L--;
                    cpu.FlagZ = cpu.L == 0;
                    return 4;
                }
            };
            OpcodeTable[0x34] = new OpcodeInfo
            {
                Opcode = 0x34,
                Mnemonic = "INC (HL)",
                Description = "Increment value at memory address pointed by HL",
                Cycles = "12",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC (HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})",
                Execute = (cpu, mmu) =>
                {
                    byte value = mmu.ReadByte(cpu.HL);
                    cpu.FlagN = false;
                    cpu.FlagH = (value & 0x0F) == 0x0F;
                    value++;
                    mmu.WriteByte(cpu.HL, value);
                    cpu.FlagZ = value == 0;
                    return 12;
                }
            };
            OpcodeTable[0x35] = new OpcodeInfo
            {
                Opcode = 0x35,
                Mnemonic = "DEC (HL)",
                Description = "Decrement value at memory address pointed by HL",
                Cycles = "12",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC (HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})",
                Execute = (cpu, mmu) =>
                {
                    byte value = mmu.ReadByte(cpu.HL);
                    cpu.FlagN = true;
                    cpu.FlagH = (value & 0x0F) == 0x00;
                    value--;
                    mmu.WriteByte(cpu.HL, value);
                    cpu.FlagZ = value == 0;
                    return 12;
                }
            };
            OpcodeTable[0x36] = new OpcodeInfo
            {
                Opcode = 0x36,
                Mnemonic = "LD (HL),n",
                Description = "Load immediate 8-bit value into memory pointed by HL",
                Cycles = "12",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"LD (HL),0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2} (HL={Hex4(cpu.HL)})",
                Execute = (cpu, mmu) =>
                {
                    byte value = mmu.ReadByte((ushort)(cpu.PC + 1));
                    mmu.WriteByte(cpu.HL, value);
                    return 12;
                }
            };

            OpcodeTable[0x3A] = new OpcodeInfo
            {
                Opcode = 0x3A,
                Mnemonic = "LD A,(HL-)",
                Description = "Load from address in HL into A, then decrement HL",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => "LD A,(HL-)",
                Execute = (cpu, mmu) =>
                {
                    cpu.A = mmu.ReadByte(cpu.HL);
                    cpu.HL--;
                    return 8;
                }
            };

            OpcodeTable[0x3C] = new OpcodeInfo
            {
                Opcode = 0x3C,
                Mnemonic = "INC A",
                Description = "Increment 8-bit register A",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"INC A (A={Hex2(cpu.A)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.A & 0x0F) == 0x0F;
                    cpu.A++;
                    cpu.FlagZ = cpu.A == 0;
                    return 4;
                }
            };
            OpcodeTable[0x3D] = new OpcodeInfo
            {
                Opcode = 0x3D,
                Mnemonic = "DEC A",
                Description = "Decrement 8-bit register A",
                Cycles = "4",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"DEC A (A={Hex2(cpu.A)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.FlagN = true;
                    cpu.FlagH = (cpu.A & 0x0F) == 0x00;
                    cpu.A--;
                    cpu.FlagZ = cpu.A == 0;
                    return 4;
                }
            };

            // Register to Register Loads
            OpcodeTable[0x40] = new OpcodeInfo { Opcode = 0x40, Mnemonic = "LD B,B", Description = "Load B into B", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { return 4; }};
            OpcodeTable[0x41] = new OpcodeInfo { Opcode = 0x41, Mnemonic = "LD B,C", Description = "Load C into B", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.B = cpu.C; return 4; }};
            OpcodeTable[0x42] = new OpcodeInfo { Opcode = 0x42, Mnemonic = "LD B,D", Description = "Load D into B", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.B = cpu.D; return 4; }};
            OpcodeTable[0x43] = new OpcodeInfo { Opcode = 0x43, Mnemonic = "LD B,E", Description = "Load E into B", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.B = cpu.E; return 4; }};
            OpcodeTable[0x44] = new OpcodeInfo { Opcode = 0x44, Mnemonic = "LD B,H", Description = "Load H into B", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.B = cpu.H; return 4; }};
            OpcodeTable[0x45] = new OpcodeInfo { Opcode = 0x45, Mnemonic = "LD B,L", Description = "Load L into B", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.B = cpu.L; return 4; }};
            OpcodeTable[0x46] = new OpcodeInfo { Opcode = 0x46, Mnemonic = "LD B,(HL)", Description = "Load value from memory address pointed by HL into B", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,(HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { cpu.B = mmu.ReadByte(cpu.HL); return 8; }};
            OpcodeTable[0x47] = new OpcodeInfo { Opcode = 0x47, Mnemonic = "LD B,A", Description = "Load A into B", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD B,A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.B = cpu.A; return 4; }};

            OpcodeTable[0x48] = new OpcodeInfo { Opcode = 0x48, Mnemonic = "LD C,B", Description = "Load B into C", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.C = cpu.B; return 4; }};
            OpcodeTable[0x49] = new OpcodeInfo { Opcode = 0x49, Mnemonic = "LD C,C", Description = "Load C into C", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { return 4; }};
            OpcodeTable[0x4A] = new OpcodeInfo { Opcode = 0x4A, Mnemonic = "LD C,D", Description = "Load D into C", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.C = cpu.D; return 4; }};
            OpcodeTable[0x4B] = new OpcodeInfo { Opcode = 0x4B, Mnemonic = "LD C,E", Description = "Load E into C", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.C = cpu.E; return 4; }};
            OpcodeTable[0x4C] = new OpcodeInfo { Opcode = 0x4C, Mnemonic = "LD C,H", Description = "Load H into C", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.C = cpu.H; return 4; }};
            OpcodeTable[0x4D] = new OpcodeInfo { Opcode = 0x4D, Mnemonic = "LD C,L", Description = "Load L into C", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.C = cpu.L; return 4; }};
            OpcodeTable[0x4E] = new OpcodeInfo { Opcode = 0x4E, Mnemonic = "LD C,(HL)", Description = "Load value from memory address pointed by HL into C", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,(HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { cpu.C = mmu.ReadByte(cpu.HL); return 8; }};
            OpcodeTable[0x4F] = new OpcodeInfo { Opcode = 0x4F, Mnemonic = "LD C,A", Description = "Load A into C", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD C,A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.C = cpu.A; return 4; }};

            OpcodeTable[0x50] = new OpcodeInfo { Opcode = 0x50, Mnemonic = "LD D,B", Description = "Load B into D", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.D = cpu.B; return 4; }};
            OpcodeTable[0x51] = new OpcodeInfo { Opcode = 0x51, Mnemonic = "LD D,C", Description = "Load C into D", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.D = cpu.C; return 4; }};
            OpcodeTable[0x52] = new OpcodeInfo { Opcode = 0x52, Mnemonic = "LD D,D", Description = "Load D into D", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { return 4; }};
            OpcodeTable[0x53] = new OpcodeInfo { Opcode = 0x53, Mnemonic = "LD D,E", Description = "Load E into D", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.D = cpu.E; return 4; }};
            OpcodeTable[0x54] = new OpcodeInfo { Opcode = 0x54, Mnemonic = "LD D,H", Description = "Load H into D", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.D = cpu.H; return 4; }};
            OpcodeTable[0x55] = new OpcodeInfo { Opcode = 0x55, Mnemonic = "LD D,L", Description = "Load L into D", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.D = cpu.L; return 4; }};
            OpcodeTable[0x56] = new OpcodeInfo { Opcode = 0x56, Mnemonic = "LD D,(HL)", Description = "Load value from memory address pointed by HL into D", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,(HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { cpu.D = mmu.ReadByte(cpu.HL); return 8; }};
            OpcodeTable[0x57] = new OpcodeInfo { Opcode = 0x57, Mnemonic = "LD D,A", Description = "Load A into D", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD D,A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.D = cpu.A; return 4; }};

            OpcodeTable[0x58] = new OpcodeInfo { Opcode = 0x58, Mnemonic = "LD E,B", Description = "Load B into E", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.E = cpu.B; return 4; }};
            OpcodeTable[0x59] = new OpcodeInfo { Opcode = 0x59, Mnemonic = "LD E,C", Description = "Load C into E", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.E = cpu.C; return 4; }};
            OpcodeTable[0x5A] = new OpcodeInfo { Opcode = 0x5A, Mnemonic = "LD E,D", Description = "Load D into E", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.E = cpu.D; return 4; }};
            OpcodeTable[0x5B] = new OpcodeInfo { Opcode = 0x5B, Mnemonic = "LD E,E", Description = "Load E into E", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { return 4; }};
            OpcodeTable[0x5C] = new OpcodeInfo { Opcode = 0x5C, Mnemonic = "LD E,H", Description = "Load H into E", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.E = cpu.H; return 4; }};
            OpcodeTable[0x5D] = new OpcodeInfo { Opcode = 0x5D, Mnemonic = "LD E,L", Description = "Load L into E", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.E = cpu.L; return 4; }};
            OpcodeTable[0x5E] = new OpcodeInfo { Opcode = 0x5E, Mnemonic = "LD E,(HL)", Description = "Load value from memory address pointed by HL into E", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,(HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { cpu.E = mmu.ReadByte(cpu.HL); return 8; }};
            OpcodeTable[0x5F] = new OpcodeInfo { Opcode = 0x5F, Mnemonic = "LD E,A", Description = "Load A into E", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD E,A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.E = cpu.A; return 4; }};

            OpcodeTable[0x60] = new OpcodeInfo { Opcode = 0x60, Mnemonic = "LD H,B", Description = "Load B into H", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.H = cpu.B; return 4; }};
            OpcodeTable[0x61] = new OpcodeInfo { Opcode = 0x61, Mnemonic = "LD H,C", Description = "Load C into H", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.H = cpu.C; return 4; }};
            OpcodeTable[0x62] = new OpcodeInfo { Opcode = 0x62, Mnemonic = "LD H,D", Description = "Load D into H", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.H = cpu.D; return 4; }};
            OpcodeTable[0x63] = new OpcodeInfo { Opcode = 0x63, Mnemonic = "LD H,E", Description = "Load E into H", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.H = cpu.E; return 4; }};
            OpcodeTable[0x64] = new OpcodeInfo { Opcode = 0x64, Mnemonic = "LD H,H", Description = "Load H into H", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { return 4; }};
            OpcodeTable[0x65] = new OpcodeInfo { Opcode = 0x65, Mnemonic = "LD H,L", Description = "Load L into H", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.H = cpu.L; return 4; }};
            OpcodeTable[0x66] = new OpcodeInfo { Opcode = 0x66, Mnemonic = "LD H,(HL)", Description = "Load value from memory address pointed by HL into H", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,(HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { cpu.H = mmu.ReadByte(cpu.HL); return 8; }};
            OpcodeTable[0x67] = new OpcodeInfo { Opcode = 0x67, Mnemonic = "LD H,A", Description = "Load A into H", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD H,A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.H = cpu.A; return 4; }};

            OpcodeTable[0x68] = new OpcodeInfo { Opcode = 0x68, Mnemonic = "LD L,B", Description = "Load B into L", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.L = cpu.B; return 4; }};
            OpcodeTable[0x69] = new OpcodeInfo { Opcode = 0x69, Mnemonic = "LD L,C", Description = "Load C into L", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.L = cpu.C; return 4; }};
            OpcodeTable[0x6A] = new OpcodeInfo { Opcode = 0x6A, Mnemonic = "LD L,D", Description = "Load D into L", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.L = cpu.D; return 4; }};
            OpcodeTable[0x6B] = new OpcodeInfo { Opcode = 0x6B, Mnemonic = "LD L,E", Description = "Load E into L", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.L = cpu.E; return 4; }};
            OpcodeTable[0x6C] = new OpcodeInfo { Opcode = 0x6C, Mnemonic = "LD L,H", Description = "Load H into L", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.L = cpu.H; return 4; }};
            OpcodeTable[0x6D] = new OpcodeInfo { Opcode = 0x6D, Mnemonic = "LD L,L", Description = "Load L into L", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { return 4; }};
            OpcodeTable[0x6E] = new OpcodeInfo { Opcode = 0x6E, Mnemonic = "LD L,(HL)", Description = "Load value from memory address pointed by HL into L", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,(HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { cpu.L = mmu.ReadByte(cpu.HL); return 8; }};
            OpcodeTable[0x6F] = new OpcodeInfo { Opcode = 0x6F, Mnemonic = "LD L,A", Description = "Load A into L", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD L,A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.L = cpu.A; return 4; }};
            OpcodeTable[0x70] = new OpcodeInfo { Opcode = 0x70, Mnemonic = "LD (HL),B", Description = "Store B into memory pointed by HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL),B (HL={Hex4(cpu.HL)}, B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.B); return 8; }};
            OpcodeTable[0x71] = new OpcodeInfo { Opcode = 0x71, Mnemonic = "LD (HL),C", Description = "Store C into memory pointed by HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL),C (HL={Hex4(cpu.HL)}, C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.C); return 8; }};
            OpcodeTable[0x72] = new OpcodeInfo { Opcode = 0x72, Mnemonic = "LD (HL),D", Description = "Store D into memory pointed by HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL),D (HL={Hex4(cpu.HL)}, D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.D); return 8; }};
            OpcodeTable[0x73] = new OpcodeInfo { Opcode = 0x73, Mnemonic = "LD (HL),E", Description = "Store E into memory pointed by HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL),E (HL={Hex4(cpu.HL)}, E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.E); return 8; }};
            OpcodeTable[0x74] = new OpcodeInfo { Opcode = 0x74, Mnemonic = "LD (HL),H", Description = "Store H into memory pointed by HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL),H (HL={Hex4(cpu.HL)}, H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.H); return 8; }};
            OpcodeTable[0x75] = new OpcodeInfo { Opcode = 0x75, Mnemonic = "LD (HL),L", Description = "Store L into memory pointed by HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL),L (HL={Hex4(cpu.HL)}, L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.L); return 8; }};
            OpcodeTable[0x77] = new OpcodeInfo { Opcode = 0x77, Mnemonic = "LD (HL),A", Description = "Store A into memory pointed by HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL),A (HL={Hex4(cpu.HL)}, A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.A); return 8; }};

            OpcodeTable[0x78] = new OpcodeInfo { Opcode = 0x78, Mnemonic = "LD A,B", Description = "Load B into A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.A = cpu.B; return 4; }};
            OpcodeTable[0x79] = new OpcodeInfo { Opcode = 0x79, Mnemonic = "LD A,C", Description = "Load C into A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,C (C={Hex2(cpu.C)})" , Execute = (cpu, mmu) => { cpu.A = cpu.C; return 4; }};
            OpcodeTable[0x7A] = new OpcodeInfo { Opcode = 0x7A, Mnemonic = "LD A,D", Description = "Load D into A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,D (D={Hex2(cpu.D)})" , Execute = (cpu, mmu) => { cpu.A = cpu.D; return 4; }};
            OpcodeTable[0x7B] = new OpcodeInfo { Opcode = 0x7B, Mnemonic = "LD A,E", Description = "Load E into A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,E (E={Hex2(cpu.E)})" , Execute = (cpu, mmu) => { cpu.A = cpu.E; return 4; }};
            OpcodeTable[0x7C] = new OpcodeInfo { Opcode = 0x7C, Mnemonic = "LD A,H", Description = "Load H into A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,H (H={Hex2(cpu.H)})" , Execute = (cpu, mmu) => { cpu.A = cpu.H; return 4; }};
            OpcodeTable[0x7D] = new OpcodeInfo { Opcode = 0x7D, Mnemonic = "LD A,L", Description = "Load L into A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,L (L={Hex2(cpu.L)})" , Execute = (cpu, mmu) => { cpu.A = cpu.L; return 4; }};
            OpcodeTable[0x7E] = new OpcodeInfo { Opcode = 0x7E, Mnemonic = "LD A,(HL)", Description = "Load value from memory address pointed by HL into A", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,(HL) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})" , Execute = (cpu, mmu) => { cpu.A = mmu.ReadByte(cpu.HL); return 8; }};
            OpcodeTable[0x7F] = new OpcodeInfo{Opcode = 0x7F, Mnemonic = "LD A,A", Description = "Load A into A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) =>{ return 4; }};

            // Memory Loads
            OpcodeTable[0x02] = new OpcodeInfo { Opcode = 0x02, Mnemonic = "LD (BC),A", Description = "Store A into memory address pointed by BC", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (BC),A ([BC]={Hex4(cpu.BC)}, A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.BC, cpu.A); return 8; } };
            OpcodeTable[0x12] = new OpcodeInfo { Opcode = 0x12, Mnemonic = "LD (DE),A", Description = "Store A into memory address pointed by DE", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (DE),A ([DE]={Hex4(cpu.DE)}, A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.DE, cpu.A); return 8; } };
            OpcodeTable[0x22] = new OpcodeInfo { Opcode = 0x22, Mnemonic = "LD (HL+),A", Description = "Store A into memory address pointed by HL, then increment HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL+),A ([HL]={Hex4(cpu.HL)}, A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.A); cpu.HL++; return 8; } };
            OpcodeTable[0x32] = new OpcodeInfo { Opcode = 0x32, Mnemonic = "LD (HL-),A", Description = "Store A into memory address pointed by HL, then decrement HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD (HL-),A ([HL]={Hex4(cpu.HL)}, A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { mmu.WriteByte(cpu.HL, cpu.A); cpu.HL--; return 8; } };

            OpcodeTable[0x2A] = new OpcodeInfo { Opcode = 0x2A, Mnemonic = "LD A,(HL+)", Description = "Load value from memory address pointed by HL into A then increase HL", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => $"LD A,(HL+) ([HL]={Hex4(cpu.HL)} => {Hex2(mmu.ReadByte(cpu.HL))})" , Execute = (cpu, mmu) => { cpu.A = mmu.ReadByte(cpu.HL); cpu.HL++; return 8; }};


            // Control Instructions
            OpcodeTable[0x00] = new OpcodeInfo { Opcode = 0x00, Mnemonic = "NOP", Description = "No operation", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => "NOP", Execute = (cpu, mmu) => { return 4; } };
            OpcodeTable[0x76] = new OpcodeInfo { Opcode = 0x76, Mnemonic = "HALT", Description = "Halt CPU until interrupt", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => "HALT", Execute = (cpu, mmu) => { cpu.is_halted = true; return 4; } };
            OpcodeTable[0xF3] = new OpcodeInfo { Opcode = 0xF3, Mnemonic = "DI", Description = "Disable interrupts", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => "DI", Execute = (cpu, mmu) => { cpu._interruptMasterEnable = false; return 4; } };
            OpcodeTable[0xFB] = new OpcodeInfo { Opcode = 0xFB, Mnemonic = "EI", Description = "Enable interrupts", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => "EI", Execute = (cpu, mmu) => { cpu._enableInterruptsScheduled = 2; return 4; } };

            // Relative Jump Instructions
            OpcodeTable[0x18] = new OpcodeInfo
            {
                Opcode = 0x18,
                Mnemonic = "JR n",
                Description = "Jump relative by signed offset",
                Cycles = "12",
                Bytes = 2,
                AutoIncrementPC = false,
                GetParameterString = (cpu, mmu) =>
                {
                    sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                    ushort target = (ushort)(cpu.PC + 2 + offset);
                    return $"JR 0x{target:X4} (offset={offset:+0;-#})";
                },
                Execute = (cpu, mmu) =>
                {
                    sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                    cpu.PC = (ushort)(cpu.PC + 2 + offset);
                    return 12;
                }
            };
            OpcodeTable[0x19] = new OpcodeInfo
            {
                Opcode = 0x19,
                Mnemonic = "ADD HL,DE",
                Description = "Add DE to HL",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"ADD HL,DE (HL={Hex4(cpu.HL)}, DE={Hex4(cpu.DE)})",
                Execute = (cpu, mmu) =>
                {
                    int result = cpu.HL + cpu.DE;
                    cpu.FlagN = false;
                    cpu.FlagH = (cpu.HL & 0x0FFF) + (cpu.DE & 0x0FFF) > 0x0FFF;
                    cpu.FlagC = result > 0xFFFF;
                    cpu.HL = (ushort)result;
                    return 8;
                }
            };
            OpcodeTable[0x1A] = new OpcodeInfo
            {
                Opcode = 0x1A,
                Mnemonic = "LD A,(DE)",
                Description = "Load A from memory address pointed by DE",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"LD A,(DE) (DE={Hex4(cpu.DE)}, A={Hex2(cpu.A)})",
                Execute = (cpu, mmu) =>
                {
                    cpu.A = mmu.ReadByte(cpu.DE);
                    return 8;
                }
            };
            OpcodeTable[0x20] = new OpcodeInfo
            {
                Opcode = 0x20,
                Mnemonic = "JR NZ,n",
                Description = "Jump relative if Z flag is not set",
                Cycles = "8-12",
                Bytes = 2,
                AutoIncrementPC = false,
                GetParameterString = (cpu, mmu) =>
                {
                    sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                    ushort target = (ushort)(cpu.PC + 2 + offset);
                    return $"JR NZ, 0x{target:X4} (offset={offset:+0;-#}, Z={cpu.FlagZ})";
                },
                Execute = (cpu, mmu) =>
                {
                    if (!cpu.FlagZ)
                    {
                        sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                        cpu.PC = (ushort)(cpu.PC + 2 + offset);
                        return 12;
                    }
                    cpu.PC += 2;
                    return 8;
                }
            };
            OpcodeTable[0x28] = new OpcodeInfo
            {
                Opcode = 0x28,
                Mnemonic = "JR Z,n",
                Description = "Jump relative if Z flag is set",
                Cycles = "8-12",
                Bytes = 2,
                AutoIncrementPC = false,
                GetParameterString = (cpu, mmu) =>
                {
                    sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                    ushort target = (ushort)(cpu.PC + 2 + offset);
                    return $"JR Z, 0x{target:X4} (offset={offset:+0;-#}, Z={cpu.FlagZ})";
                },
                Execute = (cpu, mmu) =>
                {
                    if (cpu.FlagZ)
                    {
                        sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                        cpu.PC = (ushort)(cpu.PC + 2 + offset);
                        return 12;
                    }
                    cpu.PC += 2;
                    return 8;
                }
            };
            OpcodeTable[0x29] = new OpcodeInfo
            {
                Opcode = 0x29,
                Mnemonic = "ADD HL,HL",
                Description = "Add HL to HL",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"ADD HL,HL (HL={Hex4(cpu.HL)})",
                Execute = (cpu, mmu) =>
                {
                    // Use a 32-bit integer to easily detect the 16-bit overflow for the carry flag.
                    int result = cpu.HL + cpu.HL;

                    // --- Set Flags ---
                    // N is always reset.
                    cpu.FlagN = false;

                    // H is set if a carry occurs from bit 11 to bit 12.
                    cpu.FlagH = (cpu.HL & 0x0FFF) + (cpu.HL & 0x0FFF) > 0x0FFF;

                    // C is set if a carry occurs from bit 15.
                    cpu.FlagC = result > 0xFFFF;

                    // Z is not affected by 16-bit ADD instructions.

                    // Store the 16-bit result back in HL.
                    cpu.HL = (ushort)result;

                    // This operation takes 8 clock cycles.
                    return 8;
                }
            };


            OpcodeTable[0x30] = new OpcodeInfo
            {
                Opcode = 0x30,
                Mnemonic = "JR NC,n",
                Description = "Jump relative if C flag is not set",
                Cycles = "8-12",
                Bytes = 2,
                AutoIncrementPC = false,
                GetParameterString = (cpu, mmu) =>
                {
                    sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                    ushort target = (ushort)(cpu.PC + 2 + offset);
                    return $"JR NC, 0x{target:X4} (offset={offset:+0;-#}, C={cpu.FlagC})";
                },
                Execute = (cpu, mmu) =>
                {
                    if (!cpu.FlagC)
                    {
                        sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                        cpu.PC = (ushort)(cpu.PC + 2 + offset);
                        return 12;
                    }
                    cpu.PC += 2;
                    return 8;
                }
            };
            OpcodeTable[0x38] = new OpcodeInfo
            {
                Opcode = 0x38,
                Mnemonic = "JR C,n",
                Description = "Jump relative if C flag is set",
                Cycles = "8-12",
                Bytes = 2,
                AutoIncrementPC = false,
                GetParameterString = (cpu, mmu) =>
                {
                    sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                    ushort target = (ushort)(cpu.PC + 2 + offset);
                    return $"JR C, 0x{target:X4} (offset={offset:+0;-#}, C={cpu.FlagC})";
                },
                Execute = (cpu, mmu) =>
                {
                    if (cpu.FlagC)
                    {
                        sbyte offset = (sbyte)mmu.ReadByte((ushort)(cpu.PC + 1));
                        cpu.PC = (ushort)(cpu.PC + 2 + offset);
                        return 12;
                    }
                    cpu.PC += 2;
                    return 8;
                }
            };
            //16 bit calls/jumps
            OpcodeTable[0xC1] = new OpcodeInfo { Opcode = 0xC1, Mnemonic = "POP BC", Description = "Pop from stack into BC", Cycles = "12", Bytes = 1, GetParameterString = (c,m) => "POP BC", Execute = (c,m) => { c.BC = c.PopWord(); return 12; } };
            OpcodeTable[0xC2] = new OpcodeInfo { Opcode = 0xC2, Mnemonic = "JP NZ,nn", Description = "Jump to address if Z flag is not set", Cycles = "12-16", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"JP NZ, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Jump(!cpu.FlagZ)) return 16; else return 12; } };
            OpcodeTable[0xC3] = new OpcodeInfo { Opcode = 0xC3, Mnemonic = "JP nn", Description = "Jump to address", Cycles = "16", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"JP 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { cpu.PC = mmu.ReadWord((ushort)(cpu.PC+1)); return 16; } };
            OpcodeTable[0xC4] = new OpcodeInfo { Opcode = 0xC4, Mnemonic = "CALL NZ,nn", Description = "Call subroutine if Z flag is not set", Cycles = "12-24", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"CALL NZ, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Call(!cpu.FlagZ)) return 24; else return 12; } };
            OpcodeTable[0xC5] = new OpcodeInfo { Opcode = 0xC5, Mnemonic = "PUSH BC", Description = "Push BC onto stack", Cycles = "16", Bytes = 1, GetParameterString = (c,m) => "PUSH BC", Execute = (c,m) => { c.PushWord(c.BC); return 16; } };
            OpcodeTable[0xC6] = new OpcodeInfo { Opcode = 0xC6, Mnemonic = "ADD A,n", Description = "Add immediate to A", Cycles = "8", Bytes = 2, GetParameterString = (c,m) => $"ADD A,0x{m.ReadByte((ushort)(c.PC + 1)):X2}", Execute = (c,m) => { c.AddToA(m.ReadByte((ushort)(c.PC + 1))); return 8; } };
            OpcodeTable[0xC7] = new OpcodeInfo { Opcode = 0xC7, Mnemonic = "RST 00H", Description = "Call to 0x0000", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 00H", Execute = (c,m) => { c.RST(0x0000); return 16; } };
            OpcodeTable[0xC8] = new OpcodeInfo { Opcode = 0xC8, Mnemonic = "RET Z", Description = "Return if Z flag is set", Cycles = "8-20", Bytes = 1, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => "RET Z", Execute = (cpu, mmu) => { if(cpu.Ret(cpu.FlagZ)) return 20; else return 8;} };
            OpcodeTable[0xC9] = new OpcodeInfo { Opcode = 0xC9, Mnemonic = "RET", Description = "Return from subroutine", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => "RET", Execute = (cpu, mmu) => { cpu.PC = cpu.PopWord(); return 16; } };
            OpcodeTable[0xCA] = new OpcodeInfo { Opcode = 0xCA, Mnemonic = "JP Z,nn", Description = "Jump to address if Z flag is set", Cycles = "12-16", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"JP Z, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Jump(cpu.FlagZ)) return 16; else return 12;} };
            OpcodeTable[0xCC] = new OpcodeInfo { Opcode = 0xCC, Mnemonic = "CALL Z,nn", Description = "Call subroutine if Z flag is set", Cycles = "12-24", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"CALL Z, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Call(cpu.FlagZ)) return 24; else return 12;} };
            OpcodeTable[0xCD] = new OpcodeInfo { Opcode = 0xCD, Mnemonic = "CALL nn", Description = "Call subroutine at address", Cycles = "24", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"CALL 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { cpu.Call(true); return 24; } };
            OpcodeTable[0xCE] = new OpcodeInfo { Opcode = 0xCE, Mnemonic = "ADC A,n", Description = "Add immediate and carry to A", Cycles = "8", Bytes = 2, GetParameterString = (c,m) => $"ADC A,0x{m.ReadByte((ushort)(c.PC + 1)):X2}", Execute = (c,m) => { c.ADC(m.ReadByte((ushort)(c.PC + 1))); return 8; } };
            OpcodeTable[0xCF] = new OpcodeInfo { Opcode = 0xCF, Mnemonic = "RST 08H", Description = "Call to 0x0008", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 08H", Execute = (c,m) => { c.RST(0x0008); return 16; } };
            OpcodeTable[0xD0] = new OpcodeInfo { Opcode = 0xD0, Mnemonic = "RET NC", Description = "Return if C flag is not set", Cycles = "8-20", Bytes = 1, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => "RET NC", Execute = (cpu, mmu) => { if(cpu.Ret(!cpu.FlagC)) return 20; else return 8; } };
            OpcodeTable[0xD1] = new OpcodeInfo { Opcode = 0xD1, Mnemonic = "POP DE", Description = "Pop from stack into DE", Cycles = "12", Bytes = 1, GetParameterString = (c,m) => "POP DE", Execute = (c,m) => { c.DE = c.PopWord(); return 12; } };
            OpcodeTable[0xD2] = new OpcodeInfo { Opcode = 0xD2, Mnemonic = "JP NC,nn", Description = "Jump to address if C flag is not set", Cycles = "12-16", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"JP NC, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Jump(!cpu.FlagC)) return 16; else return 12;} };
            OpcodeTable[0xD4] = new OpcodeInfo { Opcode = 0xD4, Mnemonic = "CALL NC,nn", Description = "Call subroutine if C flag is not set", Cycles = "12-24", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"CALL NC, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Call(!cpu.FlagC)) return 24; else return 12;} };
            OpcodeTable[0xD5] = new OpcodeInfo { Opcode = 0xD5, Mnemonic = "PUSH DE", Description = "Push DE onto stack", Cycles = "16", Bytes = 1, GetParameterString = (c,m) => "PUSH DE", Execute = (c,m) => { c.PushWord(c.DE); return 16; } };
            OpcodeTable[0xD6] = new OpcodeInfo { Opcode = 0xD6, Mnemonic = "SUB A,n", Description = "Subtract immediate from A", Cycles = "8", Bytes = 2, GetParameterString = (c,m) => $"SUB A,0x{m.ReadByte((ushort)(c.PC + 1)):X2}", Execute = (c,m) => { c.SubFromA(m.ReadByte((ushort)(c.PC + 1))); return 8; } };
            OpcodeTable[0xD7] = new OpcodeInfo { Opcode = 0xD7, Mnemonic = "RST 10H", Description = "Call to 0x0010", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 10H", Execute = (c,m) => { c.RST(0x0010); return 16; } };
            OpcodeTable[0xD8] = new OpcodeInfo { Opcode = 0xD8, Mnemonic = "RET C", Description = "Return if C flag is set", Cycles = "8-20", Bytes = 1, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => "RET C", Execute = (cpu, mmu) => { if(cpu.Ret(cpu.FlagC)) return 20; else return 8; } };
            OpcodeTable[0xD9] = new OpcodeInfo { Opcode = 0xD9, Mnemonic = "RETI", Description = "Return from interrupt", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => "RETI",
                Execute = (cpu, mmu) => {
                    byte low = mmu.ReadByte(cpu.SP);
                    byte high = mmu.ReadByte((ushort)(cpu.SP + 1));
            
                    Log.Debug($"Reading PC from SP before RETI {(ushort)((high << 8) | low):X4}");


                    cpu.PC = cpu.PopWord();
                    Log.Debug($"PC after RETI {cpu.PC:X4}");

                    cpu._interruptMasterEnable = true;
                    return 16; } };
            OpcodeTable[0xDA] = new OpcodeInfo { Opcode = 0xDA, Mnemonic = "JP C,nn", Description = "Jump to address if C flag is set", Cycles = "12-16", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"JP C, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Jump(cpu.FlagC)) return 16; else return 12;} };
            OpcodeTable[0xDC] = new OpcodeInfo { Opcode = 0xDC, Mnemonic = "CALL C,nn", Description = "Call subroutine if C flag is set", Cycles = "12-24", Bytes = 3, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => $"CALL C, 0x{mmu.ReadWord((ushort)(cpu.PC + 1)):X4}", Execute = (cpu, mmu) => { if (cpu.Call(cpu.FlagC)) return 24; else return 12;} };
            OpcodeTable[0xDE] = new OpcodeInfo
            {
                Opcode = 0xDE,
                Mnemonic = "SBC A,n",
                Description = "Subtract immediate and carry from A",
                Cycles = "8",
                Bytes = 2,
                GetParameterString = (cpu, mmu) => $"SBC A,0x{mmu.ReadByte((ushort)(cpu.PC + 1)):X2}",
                Execute = (cpu, mmu) =>
                {
                    byte value = mmu.ReadByte((ushort)(cpu.PC + 1));
                    cpu.SBC(value); // Reuse your existing SBC helper method
                    return 8;
                }
            };
            OpcodeTable[0xDF] = new OpcodeInfo { Opcode = 0xDF, Mnemonic = "RST 18H", Description = "Call to 0x0018", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 18H", Execute = (c,m) => { c.RST(0x0018); return 16; } };
            OpcodeTable[0xE0] = new OpcodeInfo { Opcode = 0xE0, Mnemonic = "LDH (n),A", Description = "Store A to high memory", Cycles = "12", Bytes = 2, GetParameterString = (c,m) => $"LDH (0x{m.ReadByte((ushort)(c.PC + 1)):X2}),A", Execute = (c,m) => { byte offset = m.ReadByte((ushort)(c.PC + 1)); ushort address = (ushort)(0xFF00 + offset); m.WriteByte(address, c.A); return 12; } };
            OpcodeTable[0xE1] = new OpcodeInfo { Opcode = 0xE1, Mnemonic = "POP HL", Description = "Pop from stack into HL", Cycles = "12", Bytes = 1, GetParameterString = (c,m) => "POP HL", Execute = (c,m) => { c.HL = c.PopWord(); return 12; } };
            OpcodeTable[0xE2] = new OpcodeInfo { Opcode = 0xE2, Mnemonic = "LD (C),A", Description = "Store A to high memory (0xFF00 + C)", Cycles = "8", Bytes = 1, GetParameterString = (c,m) => $"LD (C),A (C={Hex2(c.C)}, A={Hex2(c.A)})", Execute = (c,m) => { ushort address = (ushort)(0xFF00 + c.C); m.WriteByte(address, c.A); return 8; } };
            OpcodeTable[0xE5] = new OpcodeInfo { Opcode = 0xE5, Mnemonic = "PUSH HL", Description = "Push HL onto stack", Cycles = "16", Bytes = 1, GetParameterString = (c,m) => "PUSH HL", Execute = (c,m) => { c.PushWord(c.HL); return 16; } };
            OpcodeTable[0xE6] = new OpcodeInfo { Opcode = 0xE6, Mnemonic = "AND A,n", Description = "AND immediate with A", Cycles = "8", Bytes = 2, GetParameterString = (c,m) => $"AND A,0x{m.ReadByte((ushort)(c.PC + 1)):X2}", Execute = (c,m) => { c.AndWithA(m.ReadByte((ushort)(c.PC + 1))); return 8; } };
            OpcodeTable[0xE7] = new OpcodeInfo { Opcode = 0xE7, Mnemonic = "RST 20H", Description = "Call to 0x0020", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 20H", Execute = (c,m) => { c.RST(0x0020); return 16; } };
            OpcodeTable[0xE9] = new OpcodeInfo { Opcode = 0xE9, Mnemonic = "JP (HL)", Description = "Jump to address in HL", Cycles = "4", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => $"JP (HL) (HL={Hex4(c.HL)})", Execute = (c,m) => { c.PC = c.HL; return 4; } };
            OpcodeTable[0xEA] = new OpcodeInfo { Opcode = 0xEA, Mnemonic = "LD (nn),A", Description = "Store A to memory address", Cycles = "16", Bytes = 3, GetParameterString = (c,m) => $"LD (0x{m.ReadWord((ushort)(c.PC + 1)):X4}),A", Execute = (c,m) => { ushort address = m.ReadWord((ushort)(c.PC + 1)); m.WriteByte(address, c.A); return 16; } };
            OpcodeTable[0xEE] = new OpcodeInfo { Opcode = 0xEE, Mnemonic = "XOR A,n", Description = "XOR immediate with A", Cycles = "8", Bytes = 2, GetParameterString = (c,m) => $"XOR A,0x{m.ReadByte((ushort)(c.PC + 1)):X2}", Execute = (c,m) => { c.XorWithA(m.ReadByte((ushort)(c.PC + 1))); return 8; } };
            OpcodeTable[0xEF] = new OpcodeInfo { Opcode = 0xEF, Mnemonic = "RST 28H", Description = "Call to 0x0028", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 28H", Execute = (c,m) => { c.RST(0x0028); return 16; } };
            OpcodeTable[0xF0] = new OpcodeInfo { Opcode = 0xF0, Mnemonic = "LDH A,(n)", Description = "Load A from high memory", Cycles = "12", Bytes = 2, GetParameterString = (c,m) => $"LDH A,(0x{m.ReadByte((ushort)(c.PC + 1)):X2})", Execute = (c,m) => { byte offset = m.ReadByte((ushort)(c.PC + 1)); ushort address = (ushort)(0xFF00 + offset); c.A = m.ReadByte(address); return 12; } };
            OpcodeTable[0xF1] = new OpcodeInfo { Opcode = 0xF1, Mnemonic = "POP AF", Description = "Pop from stack into AF", Cycles = "12", Bytes = 1, GetParameterString = (c,m) => "POP AF", Execute = (c,m) => { c.AF = c.PopWord(); return 12; } };
            OpcodeTable[0xF2] = new OpcodeInfo
            {
                Opcode = 0xF2,
                Mnemonic = "LD A,(C)",
                Description = "Load A from address 0xFF00 + C",
                Cycles = "8",
                Bytes = 1,
                GetParameterString = (cpu, mmu) => $"LD A,(C) (C=0x{cpu.C:X2})",
                Execute = (cpu, mmu) =>
                {
                    // Calculate the source address from 0xFF00 + the value in register C.
                    ushort address = (ushort)(0xFF00 + cpu.C);
                    
                    // Load the byte from that address into register A.
                    cpu.A = mmu.ReadByte(address);
                    
                    // This operation takes 8 clock cycles.
                    return 8;
                }
            };
            OpcodeTable[0xF5] = new OpcodeInfo { Opcode = 0xF5, Mnemonic = "PUSH AF", Description = "Push AF onto stack", Cycles = "16", Bytes = 1, GetParameterString = (c,m) => "PUSH AF", Execute = (c,m) => { c.PushWord(c.AF); return 16; } };
            OpcodeTable[0xF6] = new OpcodeInfo { Opcode = 0xF6, Mnemonic = "OR A,n", Description = "OR immediate with A", Cycles = "8", Bytes = 2, GetParameterString = (c,m) => $"OR A,0x{m.ReadByte((ushort)(c.PC + 1)):X2}", Execute = (c,m) => { c.OrWithA(m.ReadByte((ushort)(c.PC + 1))); return 8; } };
            OpcodeTable[0xF7] = new OpcodeInfo { Opcode = 0xF7, Mnemonic = "RST 30H", Description = "Call to 0x0030", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 30H", Execute = (c,m) => { c.RST(0x0030); return 16; } };
            OpcodeTable[0xFA] = new OpcodeInfo { Opcode = 0xFA, Mnemonic = "LD A,(nn)", Description = "Load A from memory address", Cycles = "16", Bytes = 3, GetParameterString = (c,m) => $"LD A,(0x{m.ReadWord((ushort)(c.PC + 1)):X4})", Execute = (c,m) => { ushort address = m.ReadWord((ushort)(c.PC + 1)); c.A = m.ReadByte(address); return 16; } };
            OpcodeTable[0xFE] = new OpcodeInfo { Opcode = 0xFE, Mnemonic = "CP A,n", Description = "Compare immediate with A", Cycles = "8", Bytes = 2, GetParameterString = (c,m) => $"CP A,0x{m.ReadByte((ushort)(c.PC + 1)):X2}", Execute = (c,m) => { c.CompareWithA(m.ReadByte((ushort)(c.PC + 1))); return 8; } };
            OpcodeTable[0xFF] = new OpcodeInfo { Opcode = 0xFF, Mnemonic = "RST 38H", Description = "Call to 0x0038", Cycles = "16", Bytes = 1, AutoIncrementPC = false, GetParameterString = (c,m) => "RST 38H",
                Execute = (c,m) => {
                    c.RST(0x0038);
                    Log.Debug($"0x0038 = {m.ReadByte(0x0038):X2}");

                    return 16; } };
            OpcodeTable[0xC0] = new OpcodeInfo { Opcode = 0xC0, Mnemonic = "RET NZ", Description = "Return if Z flag is not set", Cycles = "8-20", Bytes = 1, AutoIncrementPC = false, GetParameterString = (cpu, mmu) => "RET NZ", Execute = (cpu, mmu) => { if(cpu.Ret(!cpu.FlagZ)) return 20; else return 8; } };

            // Arithmetic Instructions
            OpcodeTable[0x80] = new OpcodeInfo { Opcode = 0x80, Mnemonic = "ADD A,B", Description = "Add B to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,B", cpu, "B", cpu.B, (byte)(cpu.A + cpu.B)), Execute = (cpu, mmu) => { cpu.AddToA(cpu.B); return 4; }};
            OpcodeTable[0x81] = new OpcodeInfo { Opcode = 0x81, Mnemonic = "ADD A,C", Description = "Add C to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,C", cpu, "C", cpu.C, (byte)(cpu.A + cpu.C)), Execute = (cpu, mmu) => { cpu.AddToA(cpu.C); return 4; }};
            OpcodeTable[0x82] = new OpcodeInfo { Opcode = 0x82, Mnemonic = "ADD A,D", Description = "Add D to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,D", cpu, "D", cpu.D, (byte)(cpu.A + cpu.D)), Execute = (cpu, mmu) => { cpu.AddToA(cpu.D); return 4; }};
            OpcodeTable[0x83] = new OpcodeInfo { Opcode = 0x83, Mnemonic = "ADD A,E", Description = "Add E to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,E", cpu, "E", cpu.E, (byte)(cpu.A + cpu.E)), Execute = (cpu, mmu) => { cpu.AddToA(cpu.E); return 4; }};
            OpcodeTable[0x84] = new OpcodeInfo { Opcode = 0x84, Mnemonic = "ADD A,H", Description = "Add H to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,H", cpu, "H", cpu.H, (byte)(cpu.A + cpu.H)), Execute = (cpu, mmu) => { cpu.AddToA(cpu.H); return 4; }};
            OpcodeTable[0x85] = new OpcodeInfo { Opcode = 0x85, Mnemonic = "ADD A,L", Description = "Add L to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,L", cpu, "L", cpu.L, (byte)(cpu.A + cpu.L)), Execute = (cpu, mmu) => { cpu.AddToA(cpu.L); return 4; }};
            OpcodeTable[0x86] = new OpcodeInfo { Opcode = 0x86, Mnemonic = "ADD A,(HL)", Description = "Add value from memory address pointed by HL to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,(HL)", cpu, "(HL)", mmu.ReadByte(cpu.HL), (byte)(cpu.A + mmu.ReadByte(cpu.HL))), Execute = (cpu, mmu) => {  cpu.AddToA(mmu.ReadByte(cpu.HL)); return 8; }};
            OpcodeTable[0x87] = new OpcodeInfo { Opcode = 0x87, Mnemonic = "ADD A,A", Description = "Add A to A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("ADD A,A", cpu, "A", cpu.A, (byte)(cpu.A + cpu.A)), Execute = (cpu, mmu) => { cpu.AddToA(cpu.A); return 4; } };
            OpcodeTable[0x88] = new OpcodeInfo { Opcode = 0x88, Mnemonic = "ADC A,B", Description = "Add B and carry to A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"ADC A,B (A={Hex2(c.A)}, B={Hex2(c.B)}, C={c.FlagC})", Execute = (c,m) => { c.ADC(c.B); return 4; } };
            OpcodeTable[0x89] = new OpcodeInfo { Opcode = 0x89, Mnemonic = "ADC A,C", Description = "Add C and carry to A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"ADC A,C (A={Hex2(c.A)}, C={Hex2(c.C)}, C={c.FlagC})", Execute = (c,m) => { c.ADC(c.C); return 4; } };
            OpcodeTable[0x8A] = new OpcodeInfo { Opcode = 0x8A, Mnemonic = "ADC A,D", Description = "Add D and carry to A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"ADC A,D (A={Hex2(c.A)}, D={Hex2(c.D)}, C={c.FlagC})", Execute = (c,m) => { c.ADC(c.D); return 4; } };
            OpcodeTable[0x8B] = new OpcodeInfo { Opcode = 0x8B, Mnemonic = "ADC A,E", Description = "Add E and carry to A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"ADC A,E (A={Hex2(c.A)}, E={Hex2(c.E)}, C={c.FlagC})", Execute = (c,m) => { c.ADC(c.E); return 4; } };
            OpcodeTable[0x8C] = new OpcodeInfo { Opcode = 0x8C, Mnemonic = "ADC A,H", Description = "Add H and carry to A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"ADC A,H (A={Hex2(c.A)}, H={Hex2(c.H)}, C={c.FlagC})", Execute = (c,m) => { c.ADC(c.H); return 4; } };
            OpcodeTable[0x8D] = new OpcodeInfo { Opcode = 0x8D, Mnemonic = "ADC A,L", Description = "Add L and carry to A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"ADC A,L (A={Hex2(c.A)}, L={Hex2(c.L)}, C={c.FlagC})", Execute = (c,m) => { c.ADC(c.L); return 4; } };
            OpcodeTable[0x8E] = new OpcodeInfo { Opcode = 0x8E, Mnemonic = "ADC A,(HL)", Description = "Add value from (HL) and carry to A", Cycles = "8", Bytes = 1, GetParameterString = (c,m) => $"ADC A,(HL) (A={Hex2(c.A)}, (HL)={Hex2(m.ReadByte(c.HL))}, C={c.FlagC})", Execute = (c,m) => { c.ADC(m.ReadByte(c.HL)); return 8; } };
            OpcodeTable[0x8F] = new OpcodeInfo { Opcode = 0x8F, Mnemonic = "ADC A,A", Description = "Add A and carry to A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"ADC A,A (A={Hex2(c.A)}, C={c.FlagC})", Execute = (c,m) => { c.ADC(c.A); return 4; } };

            OpcodeTable[0x90] = new OpcodeInfo { Opcode = 0x90, Mnemonic = "SUB A,B", Description = "Subtract B from A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,B", cpu, "B", cpu.B, (byte)(cpu.A - cpu.B)), Execute = (cpu, mmu) => { cpu.SubFromA(cpu.B); return 4; }};
            OpcodeTable[0x91] = new OpcodeInfo { Opcode = 0x91, Mnemonic = "SUB A,C", Description = "Subtract C from A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,C", cpu, "C", cpu.C, (byte)(cpu.A - cpu.C)), Execute = (cpu, mmu) => { cpu.SubFromA(cpu.C); return 4; }};
            OpcodeTable[0x92] = new OpcodeInfo { Opcode = 0x92, Mnemonic = "SUB A,D", Description = "Subtract D from A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,D", cpu, "D", cpu.D, (byte)(cpu.A - cpu.D)), Execute = (cpu, mmu) => { cpu.SubFromA(cpu.D); return 4; }};
            OpcodeTable[0x93] = new OpcodeInfo { Opcode = 0x93, Mnemonic = "SUB A,E", Description = "Subtract E from A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,E", cpu, "E", cpu.E, (byte)(cpu.A - cpu.E)), Execute = (cpu, mmu) => { cpu.SubFromA(cpu.E); return 4; }};
            OpcodeTable[0x94] = new OpcodeInfo { Opcode = 0x94, Mnemonic = "SUB A,H", Description = "Subtract H from A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,H", cpu, "H", cpu.H, (byte)(cpu.A - cpu.H)), Execute = (cpu, mmu) => { cpu.SubFromA(cpu.H); return 4; }};
            OpcodeTable[0x95] = new OpcodeInfo { Opcode = 0x95, Mnemonic = "SUB A,L", Description = "Subtract L from A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,L", cpu, "L", cpu.L, (byte)(cpu.A - cpu.L)), Execute = (cpu, mmu) => { cpu.SubFromA(cpu.L); return 4; }};
            OpcodeTable[0x96] = new OpcodeInfo { Opcode = 0x96, Mnemonic = "SUB A,(HL)", Description = "Subtract value from memory address pointed by HL from A", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,(HL)", cpu, "(HL)", mmu.ReadByte(cpu.HL), (byte)(cpu.A - mmu.ReadByte(cpu.HL))), Execute = (cpu, mmu) => { cpu.SubFromA(mmu.ReadByte(cpu.HL)); return 8; }};
            OpcodeTable[0x97] = new OpcodeInfo { Opcode = 0x97, Mnemonic = "SUB A,A", Description = "Subtract A from A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("SUB A,A", cpu, "A", cpu.A, (byte)(cpu.A - cpu.A)), Execute = (cpu, mmu) => { cpu.SubFromA(cpu.A); return 4; } };
            OpcodeTable[0x98] = new OpcodeInfo { Opcode = 0x98, Mnemonic = "SBC A,B", Description = "Subtract B and carry from A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"SBC A,B (A={Hex2(c.A)}, B={Hex2(c.B)}, C={c.FlagC})", Execute = (c,m) => { c.SBC(c.B); return 4; } };
            OpcodeTable[0x99] = new OpcodeInfo { Opcode = 0x99, Mnemonic = "SBC A,C", Description = "Subtract C and carry from A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"SBC A,C (A={Hex2(c.A)}, C={Hex2(c.C)}, C={c.FlagC})", Execute = (c,m) => { c.SBC(c.C); return 4; } };
            OpcodeTable[0x9A] = new OpcodeInfo { Opcode = 0x9A, Mnemonic = "SBC A,D", Description = "Subtract D and carry from A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"SBC A,D (A={Hex2(c.A)}, D={Hex2(c.D)}, C={c.FlagC})", Execute = (c,m) => { c.SBC(c.D); return 4; } };
            OpcodeTable[0x9B] = new OpcodeInfo { Opcode = 0x9B, Mnemonic = "SBC A,E", Description = "Subtract E and carry from A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"SBC A,E (A={Hex2(c.A)}, E={Hex2(c.E)}, C={c.FlagC})", Execute = (c,m) => { c.SBC(c.E); return 4; } };
            OpcodeTable[0x9C] = new OpcodeInfo { Opcode = 0x9C, Mnemonic = "SBC A,H", Description = "Subtract H and carry from A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"SBC A,H (A={Hex2(c.A)}, H={Hex2(c.H)}, C={c.FlagC})", Execute = (c,m) => { c.SBC(c.H); return 4; } };
            OpcodeTable[0x9D] = new OpcodeInfo { Opcode = 0x9D, Mnemonic = "SBC A,L", Description = "Subtract L and carry from A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"SBC A,L (A={Hex2(c.A)}, L={Hex2(c.L)}, C={c.FlagC})", Execute = (c,m) => { c.SBC(c.L); return 4; } };
            OpcodeTable[0x9E] = new OpcodeInfo { Opcode = 0x9E, Mnemonic = "SBC A,(HL)", Description = "Subtract value from (HL) and carry from A", Cycles = "8", Bytes = 1, GetParameterString = (c,m) => $"SBC A,(HL) (A={Hex2(c.A)}, (HL)={Hex2(m.ReadByte(c.HL))}, C={c.FlagC})", Execute = (c,m) => { c.SBC(m.ReadByte(c.HL)); return 8; } };
            OpcodeTable[0x9F] = new OpcodeInfo { Opcode = 0x9F, Mnemonic = "SBC A,A", Description = "Subtract A and carry from A", Cycles = "4", Bytes = 1, GetParameterString = (c,m) => $"SBC A,A (A={Hex2(c.A)}, C={c.FlagC})", Execute = (c,m) => { c.SBC(c.A); return 4; } };

            // Logical Instructions
            OpcodeTable[0xA0] = new OpcodeInfo { Opcode = 0xA0, Mnemonic = "AND A,B", Description = "Logical AND B with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,B", cpu, "B", cpu.B, (byte)(cpu.A & cpu.B)), Execute = (cpu, mmu) => { cpu.AndWithA(cpu.B); return 4; }};
            OpcodeTable[0xA1] = new OpcodeInfo { Opcode = 0xA1, Mnemonic = "AND A,C", Description = "Logical AND C with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,C", cpu, "C", cpu.C, (byte)(cpu.A & cpu.C)), Execute = (cpu, mmu) => { cpu.AndWithA(cpu.C); return 4; }};
            OpcodeTable[0xA2] = new OpcodeInfo { Opcode = 0xA2, Mnemonic = "AND A,D", Description = "Logical AND D with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,D", cpu, "D", cpu.D, (byte)(cpu.A & cpu.D)), Execute = (cpu, mmu) => { cpu.AndWithA(cpu.D); return 4; }};
            OpcodeTable[0xA3] = new OpcodeInfo { Opcode = 0xA3, Mnemonic = "AND A,E", Description = "Logical AND E with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,E", cpu, "E", cpu.E, (byte)(cpu.A & cpu.E)), Execute = (cpu, mmu) => { cpu.AndWithA(cpu.E); return 4; }};
            OpcodeTable[0xA4] = new OpcodeInfo { Opcode = 0xA4, Mnemonic = "AND A,H", Description = "Logical AND H with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,H", cpu, "H", cpu.H, (byte)(cpu.A & cpu.H)), Execute = (cpu, mmu) => { cpu.AndWithA(cpu.H); return 4; }};
            OpcodeTable[0xA5] = new OpcodeInfo { Opcode = 0xA5, Mnemonic = "AND A,L", Description = "Logical AND L with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,L", cpu, "L", cpu.L, (byte)(cpu.A & cpu.L)), Execute = (cpu, mmu) => { cpu.AndWithA(cpu.L); return 4; }};
            OpcodeTable[0xA6] = new OpcodeInfo { Opcode = 0xA6, Mnemonic = "AND A,(HL)", Description = "Logical AND value from memory address pointed by HL with with A", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,L", cpu, "(HL)", mmu.ReadByte(cpu.HL), (byte)(cpu.A & mmu.ReadByte(cpu.HL))), Execute = (cpu, mmu) => { cpu.AndWithA(mmu.ReadByte(cpu.HL)); return 8; }};
            OpcodeTable[0xA7] = new OpcodeInfo { Opcode = 0xA7, Mnemonic = "AND A,A", Description = "Logical AND A with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("AND A,A", cpu, "A", cpu.A, (byte)(cpu.A & cpu.A)), Execute = (cpu, mmu) => { cpu.AndWithA(cpu.A); return 4; }};

            OpcodeTable[0xA8] = new OpcodeInfo { Opcode = 0xA8, Mnemonic = "XOR A,B", Description = "Logical XOR B with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,B", cpu, "B", cpu.B, (byte)(cpu.A ^ cpu.B)), Execute = (cpu, mmu) => { cpu.XorWithA(cpu.B); return 4; }};
            OpcodeTable[0xA9] = new OpcodeInfo { Opcode = 0xA9, Mnemonic = "XOR A,C", Description = "Logical XOR C with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,C", cpu, "C", cpu.C, (byte)(cpu.A ^ cpu.C)), Execute = (cpu, mmu) => { cpu.XorWithA(cpu.C); return 4; }};
            OpcodeTable[0xAA] = new OpcodeInfo { Opcode = 0xAA, Mnemonic = "XOR A,D", Description = "Logical XOR D with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,D", cpu, "D", cpu.D, (byte)(cpu.A ^ cpu.D)), Execute = (cpu, mmu) => { cpu.XorWithA(cpu.D); return 4; }};
            OpcodeTable[0xAB] = new OpcodeInfo { Opcode = 0xAB, Mnemonic = "XOR A,E", Description = "Logical XOR E with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,E", cpu, "E", cpu.E, (byte)(cpu.A ^ cpu.E)), Execute = (cpu, mmu) => { cpu.XorWithA(cpu.E); return 4; }};
            OpcodeTable[0xAC] = new OpcodeInfo { Opcode = 0xAC, Mnemonic = "XOR A,H", Description = "Logical XOR H with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,H", cpu, "H", cpu.H, (byte)(cpu.A ^ cpu.H)), Execute = (cpu, mmu) => { cpu.XorWithA(cpu.H); return 4; }};
            OpcodeTable[0xAD] = new OpcodeInfo { Opcode = 0xAD, Mnemonic = "XOR A,L", Description = "Logical XOR L with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,L", cpu, "L", cpu.L, (byte)(cpu.A ^ cpu.L)), Execute = (cpu, mmu) => { cpu.XorWithA(cpu.L); return 4; }};
            OpcodeTable[0xAE] = new OpcodeInfo { Opcode = 0xAE, Mnemonic = "XOR A,(HL)", Description = "Logical XOR value from memory address pointed by HL with with A", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,(HL)", cpu, "(HL)", mmu.ReadByte(cpu.HL), (byte)(cpu.A ^ mmu.ReadByte(cpu.HL))), Execute = (cpu, mmu) => { cpu.XorWithA(mmu.ReadByte(cpu.HL)); return 8; }};
            OpcodeTable[0xAF] = new OpcodeInfo { Opcode = 0xAF, Mnemonic = "XOR A,A", Description = "Logical XOR A with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("XOR A,A", cpu, "A", cpu.A, (byte)(cpu.A ^ cpu.A)), Execute = (cpu, mmu) => { cpu.XorWithA(cpu.A); return 4; } };

            OpcodeTable[0xB0] = new OpcodeInfo { Opcode = 0xB0, Mnemonic = "OR A,B", Description = "Logical OR B with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,B", cpu, "B", cpu.B, (byte)(cpu.A | cpu.B)), Execute = (cpu, mmu) => { cpu.OrWithA(cpu.B); return 4; }};
            OpcodeTable[0xB1] = new OpcodeInfo { Opcode = 0xB1, Mnemonic = "OR A,C", Description = "Logical OR C with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,C", cpu, "C", cpu.C, (byte)(cpu.A | cpu.C)), Execute = (cpu, mmu) => { cpu.OrWithA(cpu.C); return 4; }};
            OpcodeTable[0xB2] = new OpcodeInfo { Opcode = 0xB2, Mnemonic = "OR A,D", Description = "Logical OR D with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,D", cpu, "D", cpu.D, (byte)(cpu.A | cpu.D)), Execute = (cpu, mmu) => { cpu.OrWithA(cpu.D); return 4; }};
            OpcodeTable[0xB3] = new OpcodeInfo { Opcode = 0xB3, Mnemonic = "OR A,E", Description = "Logical OR E with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,E", cpu, "E", cpu.E, (byte)(cpu.A | cpu.E)), Execute = (cpu, mmu) => { cpu.OrWithA(cpu.E); return 4; }};
            OpcodeTable[0xB4] = new OpcodeInfo { Opcode = 0xB4, Mnemonic = "OR A,H", Description = "Logical OR H with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,H", cpu, "H", cpu.H, (byte)(cpu.A | cpu.H)), Execute = (cpu, mmu) => { cpu.OrWithA(cpu.H); return 4; }};
            OpcodeTable[0xB5] = new OpcodeInfo { Opcode = 0xB5, Mnemonic = "OR A,L", Description = "Logical OR L with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,L", cpu, "L", cpu.L, (byte)(cpu.A | cpu.L)), Execute = (cpu, mmu) => { cpu.OrWithA(cpu.L); return 4; }};
            OpcodeTable[0xB6] = new OpcodeInfo { Opcode = 0xB6, Mnemonic = "OR A,(HL)", Description = "Logical OR value from memory address pointed by HL with with A", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,(HL)", cpu, "(HL)", mmu.ReadByte(cpu.HL), (byte)(cpu.A | mmu.ReadByte(cpu.HL))), Execute = (cpu, mmu) => { cpu.OrWithA(mmu.ReadByte(cpu.HL)); return 8; }};
            OpcodeTable[0xB7] = new OpcodeInfo { Opcode = 0xB7, Mnemonic = "OR A,A", Description = "Logical OR A with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("OR A,A", cpu, "A", cpu.A, (byte)(cpu.A | cpu.A)), Execute = (cpu, mmu) => { cpu.OrWithA(cpu.A); return 4; }};

            // Compare Instructions
            OpcodeTable[0xB8] = new OpcodeInfo { Opcode = 0xB8, Mnemonic = "CP A,B", Description = "Compare B with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,B", cpu, "B", cpu.B, (byte)(cpu.A - cpu.B)), Execute = (cpu, mmu) => { cpu.CompareWithA(cpu.B); return 4; } };
            OpcodeTable[0xB9] = new OpcodeInfo { Opcode = 0xB9, Mnemonic = "CP A,C", Description = "Compare C with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,C", cpu, "C", cpu.C, (byte)(cpu.A - cpu.C)), Execute = (cpu, mmu) => { cpu.CompareWithA(cpu.C); return 4; } };
            OpcodeTable[0xBA] = new OpcodeInfo { Opcode = 0xBA, Mnemonic = "CP A,D", Description = "Compare D with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,D", cpu, "D", cpu.D, (byte)(cpu.A - cpu.D)), Execute = (cpu, mmu) => { cpu.CompareWithA(cpu.D); return 4; } };
            OpcodeTable[0xBB] = new OpcodeInfo { Opcode = 0xBB, Mnemonic = "CP A,E", Description = "Compare E with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,E", cpu, "E", cpu.E, (byte)(cpu.A - cpu.E)), Execute = (cpu, mmu) => { cpu.CompareWithA(cpu.E); return 4; } };
            OpcodeTable[0xBC] = new OpcodeInfo { Opcode = 0xBC, Mnemonic = "CP A,H", Description = "Compare H with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,H", cpu, "H", cpu.H, (byte)(cpu.A - cpu.H)), Execute = (cpu, mmu) => { cpu.CompareWithA(cpu.H); return 4; } };
            OpcodeTable[0xBD] = new OpcodeInfo { Opcode = 0xBD, Mnemonic = "CP A,L", Description = "Compare L with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,L", cpu, "L", cpu.L, (byte)(cpu.A - cpu.L)), Execute = (cpu, mmu) => { cpu.CompareWithA(cpu.L); return 4; } };
            OpcodeTable[0xBE] = new OpcodeInfo { Opcode = 0xBE, Mnemonic = "CP A,(HL)", Description = "Compare value from memory address pointed by HL with A", Cycles ="8", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,(HL)", cpu, "(HL)", mmu.ReadByte(cpu.HL), (byte)(cpu.A - mmu.ReadByte(cpu.HL))), Execute = (cpu, mmu) => { cpu.CompareWithA(mmu.ReadByte(cpu.HL)); return 8; } };
            OpcodeTable[0xBF] = new OpcodeInfo { Opcode = 0xBF, Mnemonic = "CP A,A", Description = "Compare A with A", Cycles ="4", Bytes = 1, GetParameterString = (cpu, mmu) => FormatAluLog("CP A,A", cpu, "A", cpu.A, (byte)(cpu.A - cpu.A)), Execute = (cpu, mmu) => { cpu.CompareWithA(cpu.A); return 4; } };
            // Add more opcodes as needed...
        }

        private static void InitializeExtendedOpcodeTable()
        {
            // RLC commands (0x00-0x07)
            ExtendedOpcodeTable[0x00] = new OpcodeInfo { Opcode = 0x00, Mnemonic = "RLC B", Description = "Rotates the 8-bit register B value left in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.RLC(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x01] = new OpcodeInfo { Opcode = 0x01, Mnemonic = "RLC C", Description = "Rotates the 8-bit register C value left in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.RLC(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x02] = new OpcodeInfo { Opcode = 0x02, Mnemonic = "RLC D", Description = "Rotates the 8-bit register D value left in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.RLC(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x03] = new OpcodeInfo { Opcode = 0x03, Mnemonic = "RLC E", Description = "Rotates the 8-bit register E value left in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.RLC(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x04] = new OpcodeInfo { Opcode = 0x04, Mnemonic = "RLC H", Description = "Rotates the 8-bit register H value left in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.RLC(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x05] = new OpcodeInfo { Opcode = 0x05, Mnemonic = "RLC L", Description = "Rotates the 8-bit register L value left in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.RLC(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x06] = new OpcodeInfo { Opcode = 0x06, Mnemonic = "RLC (HL)", Description = "Rotates the value at memory address HL left in a circular manner", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.RLC(value)); return 16; }};
            ExtendedOpcodeTable[0x07] = new OpcodeInfo { Opcode = 0x07, Mnemonic = "RLC A", Description = "Rotates the 8-bit register A value left in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RLC A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.RLC(cpu.A); return 8; }};

            // RRC commands (0x08-0x0F)
            ExtendedOpcodeTable[0x08] = new OpcodeInfo { Opcode = 0x08, Mnemonic = "RRC B", Description = "Rotates the 8-bit register B value right in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.RRC(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x09] = new OpcodeInfo { Opcode = 0x09, Mnemonic = "RRC C", Description = "Rotates the 8-bit register C value right in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.RRC(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x0A] = new OpcodeInfo { Opcode = 0x0A, Mnemonic = "RRC D", Description = "Rotates the 8-bit register D value right in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.RRC(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x0B] = new OpcodeInfo { Opcode = 0x0B, Mnemonic = "RRC E", Description = "Rotates the 8-bit register E value right in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.RRC(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x0C] = new OpcodeInfo { Opcode = 0x0C, Mnemonic = "RRC H", Description = "Rotates the 8-bit register H value right in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.RRC(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x0D] = new OpcodeInfo { Opcode = 0x0D, Mnemonic = "RRC L", Description = "Rotates the 8-bit register L value right in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.RRC(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x0E] = new OpcodeInfo { Opcode = 0x0E, Mnemonic = "RRC (HL)", Description = "Rotates the value at memory address HL right in a circular manner", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.RRC(value)); return 16; }};
            ExtendedOpcodeTable[0x0F] = new OpcodeInfo { Opcode = 0x0F, Mnemonic = "RRC A", Description = "Rotates the 8-bit register A value right in a circular manner", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RRC A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.RRC(cpu.A); return 8; }};

            // RL commands (0x10-0x17)
            ExtendedOpcodeTable[0x10] = new OpcodeInfo { Opcode = 0x10, Mnemonic = "RL B", Description = "Rotates the 8-bit register B value left through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.RL(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x11] = new OpcodeInfo { Opcode = 0x11, Mnemonic = "RL C", Description = "Rotates the 8-bit register C value left through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.RL(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x12] = new OpcodeInfo { Opcode = 0x12, Mnemonic = "RL D", Description = "Rotates the 8-bit register D value left through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.RL(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x13] = new OpcodeInfo { Opcode = 0x13, Mnemonic = "RL E", Description = "Rotates the 8-bit register E value left through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.RL(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x14] = new OpcodeInfo { Opcode = 0x14, Mnemonic = "RL H", Description = "Rotates the 8-bit register H value left through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.RL(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x15] = new OpcodeInfo { Opcode = 0x15, Mnemonic = "RL L", Description = "Rotates the 8-bit register L value left through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.RL(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x16] = new OpcodeInfo { Opcode = 0x16, Mnemonic = "RL (HL)", Description = "Rotates the value at memory address HL left through carry", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.RL(value)); return 16; }};
            ExtendedOpcodeTable[0x17] = new OpcodeInfo { Opcode = 0x17, Mnemonic = "RL A", Description = "Rotates the 8-bit register A value left through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RL A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.RL(cpu.A); return 8; }};

            // RR commands (0x18-0x1F)
            ExtendedOpcodeTable[0x18] = new OpcodeInfo { Opcode = 0x18, Mnemonic = "RR B", Description = "Rotates the 8-bit register B value right through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.RR(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x19] = new OpcodeInfo { Opcode = 0x19, Mnemonic = "RR C", Description = "Rotates the 8-bit register C value right through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.RR(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x1A] = new OpcodeInfo { Opcode = 0x1A, Mnemonic = "RR D", Description = "Rotates the 8-bit register D value right through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.RR(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x1B] = new OpcodeInfo { Opcode = 0x1B, Mnemonic = "RR E", Description = "Rotates the 8-bit register E value right through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.RR(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x1C] = new OpcodeInfo { Opcode = 0x1C, Mnemonic = "RR H", Description = "Rotates the 8-bit register H value right through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.RR(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x1D] = new OpcodeInfo { Opcode = 0x1D, Mnemonic = "RR L", Description = "Rotates the 8-bit register L value right through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.RR(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x1E] = new OpcodeInfo { Opcode = 0x1E, Mnemonic = "RR (HL)", Description = "Rotates the value at memory address HL right through carry", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.RR(value)); return 16; }};
            ExtendedOpcodeTable[0x1F] = new OpcodeInfo { Opcode = 0x1F, Mnemonic = "RR A", Description = "Rotates the 8-bit register A value right through carry", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"RR A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.RR(cpu.A); return 8; }};

            // SLA commands (0x20-0x27)
            ExtendedOpcodeTable[0x20] = new OpcodeInfo { Opcode = 0x20, Mnemonic = "SLA B", Description = "Shifts the 8-bit register B value left arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.SLA(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x21] = new OpcodeInfo { Opcode = 0x21, Mnemonic = "SLA C", Description = "Shifts the 8-bit register C value left arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.SLA(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x22] = new OpcodeInfo { Opcode = 0x22, Mnemonic = "SLA D", Description = "Shifts the 8-bit register D value left arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.SLA(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x23] = new OpcodeInfo { Opcode = 0x23, Mnemonic = "SLA E", Description = "Shifts the 8-bit register E value left arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.SLA(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x24] = new OpcodeInfo { Opcode = 0x24, Mnemonic = "SLA H", Description = "Shifts the 8-bit register H value left arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.SLA(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x25] = new OpcodeInfo { Opcode = 0x25, Mnemonic = "SLA L", Description = "Shifts the 8-bit register L value left arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.SLA(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x26] = new OpcodeInfo { Opcode = 0x26, Mnemonic = "SLA (HL)", Description = "Shifts the value at memory address HL left arithmetically", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.SLA(value)); return 16; }};
            ExtendedOpcodeTable[0x27] = new OpcodeInfo { Opcode = 0x27, Mnemonic = "SLA A", Description = "Shifts the 8-bit register A value left arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SLA A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.SLA(cpu.A); return 8; }};

            // SRA commands (0x28-0x2F)
            ExtendedOpcodeTable[0x28] = new OpcodeInfo { Opcode = 0x28, Mnemonic = "SRA B", Description = "Shifts the 8-bit register B value right arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.SRA(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x29] = new OpcodeInfo { Opcode = 0x29, Mnemonic = "SRA C", Description = "Shifts the 8-bit register C value right arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.SRA(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x2A] = new OpcodeInfo { Opcode = 0x2A, Mnemonic = "SRA D", Description = "Shifts the 8-bit register D value right arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.SRA(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x2B] = new OpcodeInfo { Opcode = 0x2B, Mnemonic = "SRA E", Description = "Shifts the 8-bit register E value right arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.SRA(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x2C] = new OpcodeInfo { Opcode = 0x2C, Mnemonic = "SRA H", Description = "Shifts the 8-bit register H value right arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.SRA(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x2D] = new OpcodeInfo { Opcode = 0x2D, Mnemonic = "SRA L", Description = "Shifts the 8-bit register L value right arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.SRA(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x2E] = new OpcodeInfo { Opcode = 0x2E, Mnemonic = "SRA (HL)", Description = "Shifts the value at memory address HL right arithmetically", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.SRA(value)); return 16; }};
            ExtendedOpcodeTable[0x2F] = new OpcodeInfo { Opcode = 0x2F, Mnemonic = "SRA A", Description = "Shifts the 8-bit register A value right arithmetically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRA A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.SRA(cpu.A); return 8; }};

            // SWAP commands (0x30-0x37)
            ExtendedOpcodeTable[0x30] = new OpcodeInfo { Opcode = 0x30, Mnemonic = "SWAP B", Description = "Swaps upper and lower nibbles of B", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.SWAP(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x31] = new OpcodeInfo { Opcode = 0x31, Mnemonic = "SWAP C", Description = "Swaps upper and lower nibbles of C", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.SWAP(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x32] = new OpcodeInfo { Opcode = 0x32, Mnemonic = "SWAP D", Description = "Swaps upper and lower nibbles of D", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.SWAP(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x33] = new OpcodeInfo { Opcode = 0x33, Mnemonic = "SWAP E", Description = "Swaps upper and lower nibbles of E", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.SWAP(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x34] = new OpcodeInfo { Opcode = 0x34, Mnemonic = "SWAP H", Description = "Swaps upper and lower nibbles of H", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.SWAP(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x35] = new OpcodeInfo { Opcode = 0x35, Mnemonic = "SWAP L", Description = "Swaps upper and lower nibbles of L", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.SWAP(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x36] = new OpcodeInfo { Opcode = 0x36, Mnemonic = "SWAP (HL)", Description = "Swaps upper and lower nibbles of value at (HL)", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.SWAP(value)); return 16; }};
            ExtendedOpcodeTable[0x37] = new OpcodeInfo { Opcode = 0x37, Mnemonic = "SWAP A", Description = "Swaps upper and lower nibbles of A", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SWAP A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.SWAP(cpu.A); return 8; }};

            // SRL commands (0x38-0x3F)
            ExtendedOpcodeTable[0x38] = new OpcodeInfo { Opcode = 0x38, Mnemonic = "SRL B", Description = "Shifts the 8-bit register B value right logically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL B (B={Hex2(cpu.B)})", Execute = (cpu, mmu) => { cpu.B = cpu.SRL(cpu.B); return 8; }};
            ExtendedOpcodeTable[0x39] = new OpcodeInfo { Opcode = 0x39, Mnemonic = "SRL C", Description = "Shifts the 8-bit register C value right logically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL C (C={Hex2(cpu.C)})", Execute = (cpu, mmu) => { cpu.C = cpu.SRL(cpu.C); return 8; }};
            ExtendedOpcodeTable[0x3A] = new OpcodeInfo { Opcode = 0x3A, Mnemonic = "SRL D", Description = "Shifts the 8-bit register D value right logically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL D (D={Hex2(cpu.D)})", Execute = (cpu, mmu) => { cpu.D = cpu.SRL(cpu.D); return 8; }};
            ExtendedOpcodeTable[0x3B] = new OpcodeInfo { Opcode = 0x3B, Mnemonic = "SRL E", Description = "Shifts the 8-bit register E value right logically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL E (E={Hex2(cpu.E)})", Execute = (cpu, mmu) => { cpu.E = cpu.SRL(cpu.E); return 8; }};
            ExtendedOpcodeTable[0x3C] = new OpcodeInfo { Opcode = 0x3C, Mnemonic = "SRL H", Description = "Shifts the 8-bit register H value right logically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL H (H={Hex2(cpu.H)})", Execute = (cpu, mmu) => { cpu.H = cpu.SRL(cpu.H); return 8; }};
            ExtendedOpcodeTable[0x3D] = new OpcodeInfo { Opcode = 0x3D, Mnemonic = "SRL L", Description = "Shifts the 8-bit register L value right logically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL L (L={Hex2(cpu.L)})", Execute = (cpu, mmu) => { cpu.L = cpu.SRL(cpu.L); return 8; }};
            ExtendedOpcodeTable[0x3E] = new OpcodeInfo { Opcode = 0x3E, Mnemonic = "SRL (HL)", Description = "Shifts the value at memory address HL right logically", Cycles ="16", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL (HL) (HL={Hex4(cpu.HL)}, value={Hex2(mmu.ReadByte(cpu.HL))})", Execute = (cpu, mmu) => { byte value = mmu.ReadByte(cpu.HL); mmu.WriteByte(cpu.HL, cpu.SRL(value)); return 16; }};
            ExtendedOpcodeTable[0x3F] = new OpcodeInfo { Opcode = 0x3F, Mnemonic = "SRL A", Description = "Shifts the 8-bit register A value right logically", Cycles ="8", Bytes = 2, GetParameterString = (cpu, mmu) => $"SRL A (A={Hex2(cpu.A)})", Execute = (cpu, mmu) => { cpu.A = cpu.SRL(cpu.A); return 8; }};

            // BIT opcodes (0x40-0x7F)
            InitializeBITOpcodes();
            
            // RES opcodes (0x80-0xBF)
            InitializeRESOpcodes();
            
            // SET opcodes (0xC0-0xFF)
            InitializeSETOpcodes();
        }

        private static void InitializeBITOpcodes(){
            // BIT opcodes (0x40-0x7F)
            string[] registers = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
            Func<Cpu, Mmu, byte>[] registerGetters = {
                (c, m) => c.B,
                (c, m) => c.C,
                (c, m) => c.D,
                (c, m) => c.E,
                (c, m) => c.H,
                (c, m) => c.L,
                (c, m) => m.ReadByte(c.HL),
                (c, m) => c.A,
            };

            for (int bit = 0; bit < 8; bit++)
            {
                for (int regIndex = 0; regIndex < 8; regIndex++)
                {
                    byte opcode = (byte)(0x40 + bit * 8 + regIndex);
                    int currentBit = bit;
                    int currentRegIndex = regIndex; // Capture the current value to avoid closure issue
                    string regName = registers[regIndex];
                    int cycles = (regName == "(HL)") ? 12 : 8;

                    ExtendedOpcodeTable[opcode] = new OpcodeInfo
                    {
                        Opcode = opcode,
                        Mnemonic = $"BIT {currentBit},{regName}",
                        Description = $"Test bit {currentBit} of {regName}",
                        Cycles = cycles.ToString(),
                        Bytes = 2, // CB prefix + opcode
                        GetParameterString = (cpu, mmu) => {
                            if (regName == "(HL)")
                            {
                                return $"BIT {currentBit},{regName} ({Hex2(mmu.ReadByte(cpu.HL))})";
                            }
                            return $"BIT {currentBit},{regName}";
                        },
                        Execute = (cpu, mmu) =>
                        {
                            byte value = registerGetters[currentRegIndex](cpu, mmu);
                            cpu.BIT((byte)currentBit, value);
                            return cycles;
                        }
                    };
                }
            }
        }

        private static void InitializeRESOpcodes()
        {
            // RES opcodes (0x80-0xBF)
            string[] registers = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
            
            Action<Cpu, Mmu, byte>[] registerSetters = {
                (c, m, val) => c.B = val,
                (c, m, val) => c.C = val,
                (c, m, val) => c.D = val,
                (c, m, val) => c.E = val,
                (c, m, val) => c.H = val,
                (c, m, val) => c.L = val,
                (c, m, val) => m.WriteByte(c.HL, val),
                (c, m, val) => c.A = val,
            };

            Func<Cpu, Mmu, byte>[] registerGetters = {
                (c, m) => c.B,
                (c, m) => c.C,
                (c, m) => c.D,
                (c, m) => c.E,
                (c, m) => c.H,
                (c, m) => c.L,
                (c, m) => m.ReadByte(c.HL),
                (c, m) => c.A,
            };

            for (int bit = 0; bit < 8; bit++)
            {
                for (int regIndex = 0; regIndex < 8; regIndex++)
                {
                    byte opcode = (byte)(0x80 + bit * 8 + regIndex);
                    int currentBit = bit;
                    int currentRegIndex = regIndex; // Capture the current value to avoid closure issue
                    string regName = registers[regIndex];
                    int cycles = (regName == "(HL)") ? 16 : 8;

                    ExtendedOpcodeTable[opcode] = new OpcodeInfo
                    {
                        Opcode = opcode,
                        Mnemonic = $"RES {currentBit},{regName}",
                        Description = $"Reset bit {currentBit} of {regName}",
                        Cycles = cycles.ToString(),
                        Bytes = 2, // CB prefix + opcode
                        GetParameterString = (cpu, mmu) => $"RES {currentBit},{regName}",
                        Execute = (cpu, mmu) =>
                        {
                            byte value = registerGetters[currentRegIndex](cpu, mmu);
                            byte result = cpu.RES((byte)currentBit, value);
                            registerSetters[currentRegIndex](cpu, mmu, result);
                            return cycles;
                        }
                    };
                }
            }
        }

        private static void InitializeSETOpcodes()
        {
            // SET opcodes (0xC0-0xFF)
            string[] registers = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
            
            Action<Cpu, Mmu, byte>[] registerSetters = {
                (c, m, val) => c.B = val,
                (c, m, val) => c.C = val,
                (c, m, val) => c.D = val,
                (c, m, val) => c.E = val,
                (c, m, val) => c.H = val,
                (c, m, val) => c.L = val,
                (c, m, val) => m.WriteByte(c.HL, val),
                (c, m, val) => c.A = val,
            };

            Func<Cpu, Mmu, byte>[] registerGetters = {
                (c, m) => c.B,
                (c, m) => c.C,
                (c, m) => c.D,
                (c, m) => c.E,
                (c, m) => c.H,
                (c, m) => c.L,
                (c, m) => m.ReadByte(c.HL),
                (c, m) => c.A,
            };

            for (int bit = 0; bit < 8; bit++)
            {
                for (int regIndex = 0; regIndex < 8; regIndex++)
                {
                    byte opcode = (byte)(0xC0 + bit * 8 + regIndex);
                    int currentBit = bit;
                    int currentRegIndex = regIndex; // Capture the current value to avoid closure issue
                    string regName = registers[regIndex];
                    int cycles = (regName == "(HL)") ? 16 : 8;

                    ExtendedOpcodeTable[opcode] = new OpcodeInfo
                    {
                        Opcode = opcode,
                        Mnemonic = $"SET {currentBit},{regName}",
                        Description = $"Set bit {currentBit} of {regName}",
                        Cycles = cycles.ToString(),
                        Bytes = 2, // CB prefix + opcode
                        GetParameterString = (cpu, mmu) => $"SET {currentBit},{regName}",
                        Execute = (cpu, mmu) =>
                        {
                            byte value = registerGetters[currentRegIndex](cpu, mmu);
                            byte result = cpu.SET((byte)currentBit, value);
                            registerSetters[currentRegIndex](cpu, mmu, result);
                            return cycles;
                        }
                    };
                }
            }
        }

        public int Step()
        {
             // EI instruction sets a schedule, this method processes it.
            ProcessScheduledInterruptEnable();

            int ticks = 0;
            // Check for pending interrupts to handle halt state
            byte pending_interrupts = (byte)(ie_register & if_register);

            if (is_halted)
            {
                // If there are pending interrupts, the CPU must wake up.
                if (pending_interrupts != 0)
                {
                    is_halted = false;

                    // Check for the HALT bug trigger condition: wake up while IME is disabled.
                    if (!_interruptMasterEnable)
                    {
                        halt_bug_active = true;
                    }
                }
                else
                {
                    // Stay halted, consume 4 cycles, and do nothing else.
                    return 4; 
                }
            }
                // Fetch and execute one instruction
            ticks = Execute();
            
            // Check for interrupts after every single action,
            // whether it was a real instruction or just a halt-tick.
            ticks += handle_interrupts();

            return ticks;
        }

        private void ProcessScheduledInterruptEnable()
        {
            if (_enableInterruptsScheduled > 0)
            {
                _enableInterruptsScheduled--;
                if (_enableInterruptsScheduled == 0)
                {
                    _interruptMasterEnable = true;
                }
            }
        }

        // This function should be called after every single instruction executes.
        int handle_interrupts()
        {
            // 1. Check if the master switch is off. If so, do nothing.
            if (!_interruptMasterEnable)
            {
                return 0;
            }

            // 3. Find out which interrupts are both enabled AND requested.
            byte requested_interrupts = (byte)(ie_register & if_register & 0x1F);

            if (requested_interrupts == 0)
            {
                return 0; // No active interrupts to handle.
            }

            // 4. Handle the highest-priority interrupt.
            // Iterate from bit 0 (VBlank, highest priority) to bit 4 (Joypad, lowest).
            for (int i = 0; i < 5; i++)
            {
                if (((requested_interrupts >> i) & 1) == 1)
                {
                    // An interrupt was found! Let's service it.

                    // a. Disable the master interrupt switch.
                    _interruptMasterEnable = false;

                    // b. Clear the interrupt flag in the IF register.
                    if_register &= (byte)~(1 << i);

                    if (i == 4)
                    {
                        Log.Debug($"Handling joypad int at {PC:X4} and putting on SP at {SP:X4}");

                    }
                    // c. Push the current program counter onto the stack.
                    // This is a 16-bit value, so it takes two pushes.
                    PushWord(PC);

                    if (i == 4)
                    {
                        byte low = Mmu.ReadByte(SP);
                        byte high = Mmu.ReadByte((ushort)(SP + 1));
            
                        Log.Debug($"Reading SP after joypad int {(ushort)((high << 8) | low):X4}");
                    }

                    // d. Jump to the interrupt's vector address.
                    switch (i)
                    {
                        case 0: PC = 0x0040; break; // VBlank
                        case 1: PC = 0x0048; break; // LCD STAT
                        case 2: PC = 0x0050; break; // Timer
                        case 3: PC = 0x0058; break; // Serial
                        case 4: PC = 0x0060; break; // Joypad
                    }

                    Log.Debug("Handling interrupt: {Interrupt}", i);
                    // This entire hardware sequence takes 20 clock cycles.
                    // You should add these cycles to your cycle counter.
                    // Since we've handled the highest priority interrupt, we're done.
                    return 20;
                }
            }
            return 0;
        }

        // Executes a single instruction and returns the number of clock cycles it took.
        public int Execute()
        {
            OpcodeInfo opcodeInfo;
            byte opcode = Mmu.ReadByte(PC);

            if(opcode == 0xCB)
            {
                opcode = Mmu.ReadByte((ushort)(PC + 1));
                opcodeInfo = ExtendedOpcodeTable[opcode];
            }else{
                opcodeInfo = OpcodeTable[opcode];
            }

            if (opcodeInfo.Opcode == 0 && opcode != 0x00) // Not initialized
            {
                Log.Warning("Unknown opcode executed: 0x{opcode:X2} at PC=0x{PC:X4}", opcode, PC);
                opcodeInfo = new OpcodeInfo
                {
                    Opcode = opcode,
                    Mnemonic = $"UNK 0x{opcode:X2}",
                    Description = "Unknown opcode",
                    Cycles = "4",
                    Bytes = 1,
                    GetParameterString = (cpu, mmu) => $"UNK 0x{opcode:X2}",
                    Execute = (cpu, mmu) => { return 4; }
                };
            }

            // Log the opcode execution
            string opcodeLog = opcodeInfo.GetParameterString(this, Mmu);
            LastExecutedOpcode = opcodeInfo;
            LastOpcodeLog = opcodeLog;

            Log.Debug("Executing: {Opcode} at PC=0x{PC:X4}", opcodeLog, PC);

            // Check if the implementation exists
            if (opcodeInfo.Execute == null)
            {
                throw new NotImplementedException($"Execution for opcode 0x{opcode:X2} ({opcodeInfo.Mnemonic}) is not implemented.");
            }
            // 2. Execute its implementation
            int cycles = opcodeInfo.Execute(this, Mmu); // 'this' refers to the CPU instance

            // 3. Conditionally advance the Program Counter
            // If the halt bug was triggered, we consume the bug and DO NOT increment the PC.
            if (halt_bug_active)
            {
                halt_bug_active = false;
            }
            // Otherwise, increment the PC as normal (for non-jump instructions).
            else if (opcodeInfo.AutoIncrementPC)
            {
                PC += (ushort)opcodeInfo.Bytes;
            }
            //int cycles = opcodeInfo.Execute?.Invoke(this, _mmu) ?? ExecuteOpcode(opcode, opcodeInfo);

            // If stepping, pause after this instruction
            if (IsStepping)
            {
                IsPaused = true;
                IsStepping = false;
            }

            return cycles;
        }

        private void ValidateOpcodeTable()
        {
            for (int i = 0; i < OpcodeTable.Length; i++)
            {
                // Check for missing entries or entries without an implementation
                if (OpcodeTable[i].Execute == null)
                {
                    throw new InvalidOperationException($"Opcode table validation failed. Opcode 0x{i:X2} is not implemented.");
                }
            }
        }

        private bool Call(bool flag)
        {
            if (flag)
            {
                ushort callAddr = Mmu.ReadWord((ushort)(PC + 1));
                PC += 3;
                PushWord(PC);
                PC = callAddr;
                return true;
            }
            PC += 3;
            return false;
        }

        private bool Jump(bool flag)
        {
            if (flag)
            {
                PC = Mmu.ReadWord((ushort)(PC + 1));
                return true;
            }
            PC += 3;
            return false;
        }

        // Helper method for ADD operations
        private void AddToA(byte value)
        {
            int result = A + value;

            // Set flags
            FlagZ = (result & 0xFF) == 0;
            FlagN = false;
            FlagH = (A & 0x0F) + (value & 0x0F) > 0x0F;
            FlagC = result > 0xFF;

            A = (byte)(result & 0xFF);
        }

        // Helper method for SUB operations
        private void SubFromA(byte value)
        {
            int result = A - value;

            // Set flags
            FlagZ = (result & 0xFF) == 0;
            FlagN = true;
            FlagH = (A & 0x0F) < (value & 0x0F);
            FlagC = result < 0;

            A = (byte)(result & 0xFF);
        }

        // Helper method for AND operations
        private void AndWithA(byte value)
        {
            A &= value;

            // Set flags
            FlagZ = A == 0;
            FlagN = false;
            FlagH = true;
            FlagC = false;
        }

        // Helper method for OR operations
        private void OrWithA(byte value)
        {
            A |= value;

            // Set flags
            FlagZ = A == 0;
            FlagN = false;
            FlagH = false;
            FlagC = false;
        }

        private bool Ret(bool flag)
        { 
            if (flag)
            {
                PC = PopWord();
                return true;
            }
            PC += 1;
            return false;
        }

        // Helper method for XOR operations
        private void XorWithA(byte value)
        {
            A ^= value;

            // Set flags
            FlagZ = A == 0;
            FlagN = false;
            FlagH = false;
            FlagC = false;
        }

        // Helper method for CP operations (compare)
        private void CompareWithA(byte value)
        {
            int result = A - value;

            // Set flags (A is not modified)
            FlagZ = (result & 0xFF) == 0;
            FlagN = true;
            FlagH = (A & 0x0F) < (value & 0x0F);
            FlagC = result < 0;
        }

        private byte RLC(byte b)
        {
            byte result = (byte)((b << 1) | (b >> 7));
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            FlagC = (b & 0x80) != 0;
            return result;
        }

        private byte RRC(byte b)
        {
            byte result = (byte)((b >> 1) | (b << 7));
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            FlagC = (b & 0x01) != 0;
            return result;
        }

        private byte RL(byte b)
        {
            byte oldCarry = FlagC ? (byte)1 : (byte)0;
            byte result = (byte)((b << 1) | oldCarry);
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            FlagC = (b & 0x80) != 0;
            return result;
        }

        private byte RR(byte b)
        {
            byte oldCarry = FlagC ? (byte)0x80 : (byte)0;
            byte result = (byte)((b >> 1) | oldCarry);
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            FlagC = (b & 0x01) != 0;
            return result;
        }

        private byte SLA(byte b)
        {
            byte result = (byte)(b << 1);
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            FlagC = (b & 0x80) != 0;
            return result;
        }

        private byte SRA(byte b)
        {
            byte result = (byte)((b >> 1) | (b & 0x80)); // Preserve MSB for sign extension
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            FlagC = (b & 0x01) != 0;
            return result;
        }

        private byte SWAP(byte value)
        {
            byte result = (byte)(((value & 0x0F) << 4) | ((value & 0xF0) >> 4));
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            FlagC = false;
            return result;
        }

        private byte SRL(byte value)
        {
            FlagC = (value & 0x01) != 0;
            byte result = (byte)(value >> 1);
            FlagZ = result == 0;
            FlagN = false;
            FlagH = false;
            return result;
        }

        private void BIT(byte bit, byte value)
        {
            FlagZ = (value & (1 << bit)) == 0;
            FlagN = false;
            FlagH = true;
        }

        private byte RES(byte bit, byte value)
        {
            return (byte)(value & ~(1 << bit));
        }

        private byte SET(byte bit, byte value)
        {
            return (byte)(value | (1 << bit));
        }

        private void RLCA()
        {
            FlagC = (A & 0x80) != 0;
            A = (byte)((A << 1) | (A >> 7));
            FlagZ = false; // Note: Z flag is cleared in RLCA/RRCA/RLA/RRA
            FlagN = false;
            FlagH = false;
        }

        private void RRCA()
        {
            FlagC = (A & 0x01) != 0;
            A = (byte)((A >> 1) | (A << 7));
            FlagZ = false;
            FlagN = false;
            FlagH = false;
        }

        private void RLA()
        {
            bool oldCarry = FlagC;
            FlagC = (A & 0x80) != 0;
            A = (byte)((A << 1) | (oldCarry ? 1 : 0));
            FlagZ = false;
            FlagN = false;
            FlagH = false;
        }

        private void RRA()
        {
            bool oldCarry = FlagC;
            FlagC = (A & 0x01) != 0;
            A = (byte)((A >> 1) | (oldCarry ? 0x80 : 0));
            FlagZ = false;
            FlagN = false;
            FlagH = false;
        }

        private void DAA()
        {
            byte correction = 0;
            bool carry = false;

            if (FlagH || (!FlagN && (A & 0x0F) > 9))
            {
                correction |= 0x06;
            }

            if (FlagC || (!FlagN && A > 0x99))
            {
                correction |= 0x60;
                carry = true;
            }
            
            A += (byte)(FlagN ? -correction : correction);

            FlagZ = A == 0;
            FlagH = false;
            FlagC = carry;
        }

        private void CPL()
        {
            A = (byte)~A;
            FlagN = true;
            FlagH = true;
        }

        private void SCF()
        {
            FlagN = false;
            FlagH = false;
            FlagC = true;
        }

        private void CCF()
        {
            FlagN = false;
            FlagH = false;
            FlagC = !FlagC;
        }

        private void ADC(byte value)
        {
            int carry = FlagC ? 1 : 0;
            int result = A + value + carry;
            FlagZ = (result & 0xFF) == 0;
            FlagH = ((A & 0x0F) + (value & 0x0F) + carry) > 0x0F;
            FlagC = result > 0xFF;
            FlagN = false;
            A = (byte)result;
        }

        private void SBC(byte value)
        {
            int carry = FlagC ? 1 : 0;
            int result = A - value - carry;
            FlagZ = (result & 0xFF) == 0;
            FlagH = ((A & 0x0F) - (value & 0x0F) - carry) < 0;
            FlagC = result < 0;
            FlagN = true;
            A = (byte)result;
        }

        private void RST(ushort address)
        {
            PushWord((ushort)(PC + 1));
            PC = address;
        }

        /// <summary>
        /// Sets the corresponding bit in the Interrupt Flag (IF) register (0xFF0F).
        /// </summary>
        /// <param name="interrupt">The interrupt to request.</param>
        public void RequestInterrupt(Interrupt interrupt)
        {
            // 1. Read the current value of the IF register from memory.
            byte currentFlags = if_register;

            // 2. Set the bit corresponding to the requested interrupt.
            //    (1 << (int)interrupt) creates a bitmask, e.g., for VBlank (0) it's 0b00001.
            byte newFlags = (byte)(currentFlags | (1 << (int)interrupt));

            if_register = newFlags;
        }

        private void PushWord(ushort value)
        {
            // Push high byte then low byte
            byte high = (byte)(value >> 8);
            byte low = (byte)(value & 0xFF);
            SP--;
            Mmu.WriteByte(SP, high);
            SP--;
            Mmu.WriteByte(SP, low);
        }

        private ushort PopWord()
        {
            // Pop low byte then high byte
            byte low = Mmu.ReadByte(SP);
            SP++;
            byte high = Mmu.ReadByte(SP);
            SP++;
            return (ushort)((high << 8) | low);
        }

        // Debug methods
        public void StepInstruction()
        {
            IsStepping = true;
            IsPaused = false;
        }

        public void Continue()
        {
            IsPaused = false;
            IsStepping = false;
        }

        public void Pause()
        {
            IsPaused = true;
        }

        private static string Hex2(byte v)
        {
            return $"0x{v:X2}";
        }

        private static string Hex4(ushort v)
        {
            return $"0x{v:X4}";
        }

        private static string FormatAluLog(string mnemonic, Cpu cpu, string srcName, byte srcVal, byte result)
        {
            return $"{mnemonic} (A={Hex2(cpu.A)}, {srcName}={Hex2(srcVal)} -> {Hex2(result)})";
        }
    }
}
