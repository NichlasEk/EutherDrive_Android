using System;

namespace EutherDrive.Core.Z80Debug
{
    public sealed class Z80DebugState
    {
        // Registers
        public ushort PC { get; set; }
        public ushort SP { get; set; }
        public ushort BC { get; set; }
        public ushort DE { get; set; }
        public ushort HL { get; set; }
        public ushort IX { get; set; }
        public ushort IY { get; set; }
        public byte A { get; set; }
        public byte B { get; set; }
        public byte C { get; set; }
        public byte D { get; set; }
        public byte E { get; set; }
        public byte H { get; set; }
        public byte L { get; set; }
        
        // Alternate registers
        public ushort AFu { get; set; }
        public ushort BCu { get; set; }
        public ushort DEu { get; set; }
        public ushort HLu { get; set; }
        
        // Interrupt registers
        public byte I { get; set; }
        public byte R { get; set; }
        
        // Flags
        public bool FlagS { get; set; }
        public bool FlagZ { get; set; }
        public bool FlagH { get; set; }
        public bool FlagPV { get; set; }
        public bool FlagN { get; set; }
        public bool FlagC { get; set; }
        
        // Alternate flags
        public bool FlagSu { get; set; }
        public bool FlagZu { get; set; }
        public bool FlagHu { get; set; }
        public bool FlagPVu { get; set; }
        public bool FlagNu { get; set; }
        public bool FlagCu { get; set; }
        
        // Interrupt state
        public bool IFF1 { get; set; }
        public bool IFF2 { get; set; }
        public int InterruptMode { get; set; }
        public bool Halt { get; set; }
        public bool IRQ { get; set; }
        public bool NMI { get; set; }
        
        // Z80 status
        public bool Active { get; set; }
        public bool BusGranted { get; set; }
        public bool Reset { get; set; }
        public long TotalCycles { get; set; }
        public long BudgetCycles { get; set; }
        
        // Memory watchpoints
        public byte BootArea0x40 { get; set; }
        public byte Flag0x65 { get; set; }
        
        // RAM dump (0x0000-0x1FFF)
        public byte[] Ram { get; set; } = Array.Empty<byte>();
        
        // Bank register
        public uint BankRegister { get; set; }
        public uint BankBase => (BankRegister & 0x1FFu) * 0x8000u;
    }
}