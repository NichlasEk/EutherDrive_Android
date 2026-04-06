// IORegisters.cs
namespace EutherDrive.Core.GbEmu
{
    public static class IORegisters
    {
        public const ushort P1_JOYP = 0xFF00; // Joypad
        public const ushort SB = 0xFF01;      // Serial transfer data
        public const ushort SC = 0xFF02;      // Serial transfer control
        public const ushort DIV = 0xFF04;     // Divider Register
        public const ushort TIMA = 0xFF05;    // Timer counter
        public const ushort TMA = 0xFF06;     // Timer Modulo
        public const ushort TAC = 0xFF07;     // Timer Control
        public const ushort IF = 0xFF0F;      // Interrupt Flag
        public const ushort LCDC = 0xFF40;    // LCD Control
        public const ushort STAT = 0xFF41;    // LCDC Status
        public const ushort SCY = 0xFF42;     // Scroll Y
        public const ushort SCX = 0xFF43;     // Scroll X
        public const ushort LY = 0xFF44;      // LCDC Y-Coordinate
        public const ushort LYC = 0xFF45;     // LY Compare
        public const ushort DMA = 0xFF46;     // DMA Transfer and Start Address
        public const ushort BGP = 0xFF47;     // BG Palette Data
        public const ushort OBP0 = 0xFF48;    // Object Palette 0 Data
        public const ushort OBP1 = 0xFF49;    // Object Palette 1 Data
        public const ushort WY = 0xFF4A;      // Window Y Position
        public const ushort WX = 0xFF4B;      // Window X Position
        public const ushort VBK = 0xFF4F;     // VRAM Bank Select (GBC)
        public const ushort SVBK = 0xFF70;    // WRAM Bank Select (GBC)
        public const ushort IE = 0xFFFF;      // Interrupt Enable
    }
}