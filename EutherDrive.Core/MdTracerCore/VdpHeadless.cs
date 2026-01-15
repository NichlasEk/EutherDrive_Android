using System;

namespace EutherDrive.Core.MdTracerCore
{
    // Minimal VDP för port-I/O och en enkel framebuffer. Vi bygger ut successivt.
    public partial class md_vdp
    {
        // Portstatus
        private static ushort _ctrlLatch;
        private static byte   _fifo; // dummy

        // Framebuffer (ARGB32)
        public static int Width  { get; private set; } = 320;
        public static int Height { get; private set; } = 224;
        private static uint[] _frame = new uint[320 * 224];

        // Backdrop color (VDP register #7 i riktig HW) – vi använder bara en variabel tills vidare
        private static byte _backdropIndex = 0x0E; // typisk blå

        public static ReadOnlySpan<uint> GetFrame() => _frame;

        public static void Reset()
        {
            _ctrlLatch = 0;
            _fifo = 0;
            Width = 320; Height = 224;
            if (_frame.Length != Width * Height) _frame = new uint[Width * Height];
            Array.Fill(_frame, 0xFF202020u); // mörkgrå
        }

        // Dessa fyra metoder ska matcha vad bus-koden väntar sig när 68k r/w VDP-portarna
        public static void write_port_control(ushort val)
        {
            _ctrlLatch = val;
            // TODO: tolka kommando (VRAM/CRAM/VSRAM access, register writes) när vi implementerar VDP “riktigt”.
            // Tillfälligt: låt vissa värden ändra backdrop för att se liv
            _backdropIndex = (byte)(val & 0x3F);
        }

        public static void write_port_data(ushort val)
        {
            // TODO: skrivningar till VRAM/CRAM/VSRAM beroende på _ctrlLatch
            // Tills vidare: ignorera.
        }

        public static ushort read_port_control()
        {
            // Bit 7 = VBlank i riktig HW; vi kan toggla förenklat
            return 0x0000;
        }

        public static ushort read_port_data()
        {
            // TODO: läs från VRAM/CRAM/VSRAM enligt _ctrlLatch
            return 0x0000;
        }

        // Kallas en gång per frame från adaptern: rita något enkelt så vi ser att MD-kärnan löper
        public static void RenderFrame(int tick)
        {
            // Enkel bakgrund + rörligt band (ARGB)
            uint back = ArgbFromIndex(_backdropIndex);
            int w = Width, h = Height;
            if (_frame.Length != w * h) _frame = new uint[w * h];

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte r = (byte)((x + tick) & 0xFF);
                    byte g = (byte)((y * 2) & 0xFF);
                    byte b = (byte)((x ^ y ^ tick) & 0xFF);
                    uint band = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

                    _frame[row + x] = (tick & 8) != 0 ? band : back;
                }
            }

            // ✅ NYTT: rita input-driven cursor/HUD ovanpå
   //         DebugOverlay_EndOfFrame();
        }

        private static uint ArgbFromIndex(byte idx)
        {
            // Placeholder-palett: 64 färger – bara en enkel ramp.
            byte r = (byte)(idx * 3 & 0xFF);
            byte g = (byte)(idx * 5 & 0xFF);
            byte b = (byte)(idx * 7 & 0xFF);
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }
}
