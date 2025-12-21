namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // VDP : chips:315-5313
    //----------------------------------------------------------------
    internal partial class md_vdp
    {
        public int g_scanline;
        private int g_hinterrupt_counter;

        // --- UI-agnostiska inparametrar (matas från Avalonia) ---
        public bool MouseClickInterrupt { get; set; }
        public int MouseClickPosX { get; set; }
        public int MouseClickPosY { get; set; }

        // --- Minimal framebuffer (RGBA32) för Avalonia ---
        public int FrameWidth  { get; private set; } = 320;  // MD standard 320x224 (NTSC)
        public int FrameHeight { get; private set; } = 224;
        public int Pitch => FrameWidth * 4;                  // bytes per rad
        public byte[] RgbaFrame { get; private set; } = Array.Empty<byte>();

        public md_vdp()
        {
            initialize();
            EnsureFrameBuffer();
            dx_rendering_initialize(); // no-op stub just nu
        }

        private void EnsureFrameBuffer()
        {
            if (RgbaFrame == null || RgbaFrame.Length != FrameWidth * FrameHeight * 4)
                RgbaFrame = new byte[FrameWidth * FrameHeight * 4];
        }

        /// <summary>Byt upplösning (valfritt att kalla om du vill synka till VDP-registret senare).</summary>
        public void SetFrameSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            FrameWidth = width;
            FrameHeight = height;
            EnsureFrameBuffer();
        }

        public void run(int in_vline)
        {

            md_m68k.g_interrupt_V_req = true;
            md_main.g_md_z80.irq_request(true);

            g_scanline = in_vline;

            if (g_scanline == 0)
            {
                rendering_line();
                set_hinterrupt();
                interrupt_check();
            }
            else if (g_scanline < g_display_ysize)   // g_display_ysize finns i dina VDP-filer
            {
                rendering_line();
                interrupt_check();
            }
            else if (g_scanline == g_display_ysize)
            {
                rendering_frame();
                interrupt_check();

                g_vdp_status_3_vbrank = 1;
                md_m68k.g_interrupt_V_req = true;
                g_vdp_status_7_vinterrupt = 1;
                md_main.g_md_z80.irq_request(true);
            }
            else if (g_scanline == g_vertical_line_max - 1) // också definierad i VDP
            {
                g_vdp_status_3_vbrank = 0;
                g_vdp_status_4_frame = (byte)((g_vdp_status_4_frame == 0) ? 1 : 0);
                g_vdp_status_5_collision = 0;
                g_vdp_status_6_sprite = 0;
            }
        }

        private void set_hvcounter()
        {
            if (g_vdp_reg_12_2_interlacemode == 0)
            {
                g_vdp_c00008_hvcounter = (ushort)(((MouseClickPosX >> 1) & 0x00ff)
                + (MouseClickPosY << 8));
            }
            else
            {
                g_vdp_c00008_hvcounter = (ushort)(((MouseClickPosX >> 1) & 0x00ff)
                + ((MouseClickPosY << 8) & 0xfe00)
                + (MouseClickPosY & 0x0100));
            }
        }

        private static void DrawCrosshairArgb(int cx, int cy, byte r, byte g, byte b)
        {
            uint c = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            DrawRectArgb(cx - 4, cy - 1, 9, 3, c);
            DrawRectArgb(cx - 1, cy - 4, 3, 9, c);
        }

        private static void DrawRectArgb(int x0, int y0, int w, int h, uint argb)
        {
            int x1 = Math.Min(Width, x0 + w);
            int y1 = Math.Min(Height, y0 + h);
            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);

            for (int y = y0; y < y1; y++)
            {
                int row = y * Width;
                for (int x = x0; x < x1; x++)
                    _frame[row + x] = argb;
            }
        }


        private void set_hinterrupt()
        {
            g_hinterrupt_counter = g_vdp_reg_10_hint;
        }

        private void interrupt_check()
        {
            // H-INT
            g_hinterrupt_counter -= 1;
            if (g_hinterrupt_counter < 0)
            {
                md_m68k.g_interrupt_H_req = true;
                set_hinterrupt();
            }

            // EXT (mus-klick)
            if (MouseClickInterrupt)
            {
                if (g_vdp_reg_11_3_ext == 1)
                {
                    md_m68k.g_interrupt_EXT_req = true;

                    if ((g_vdp_reg_0_1_hvcounter == 1) && (g_vdp_c00008_hvcounter_latched == false))
                    {
                        set_hvcounter();
                        g_vdp_c00008_hvcounter_latched = true;
                    }
                    else
                    {
                        g_vdp_c00008_hvcounter_latched = false;
                    }
                }
                else
                {
                    MouseClickInterrupt = false;
                }
            }
        }

        // --------- Stubs (temporärt tills vi portar riktiga rendern) ----------
        private void dx_rendering_initialize() { /* no-op i headless */ }

        /// <summary>Fyll aktuell scanline i RgbaFrame med en testgradient.</summary>
        private void rendering_line()
        {
            // Skydda mot out-of-range om dina VDP-registrets storlekar ändras
            int y = g_scanline;
            if ((uint)y >= (uint)FrameHeight) return;

            int offset = y * Pitch;
            // enkel färg: R=gradient över x, G=gradient över y, B=“frame parity”
            byte g = (byte)((y * 255) / (FrameHeight - 1));
            byte b = (byte)(g_vdp_status_4_frame == 0 ? 32 : 96);

            for (int x = 0; x < FrameWidth; x++)
            {
                byte r = (byte)((x * 255) / (FrameWidth - 1));
                RgbaFrame[offset + 0] = r;   // R
                RgbaFrame[offset + 1] = g;   // G
                RgbaFrame[offset + 2] = b;   // B
                RgbaFrame[offset + 3] = 255; // A
                offset += 4;
            }
        }

        /// <summary>Körs när vi passerar g_display_ysize; här kan man göra postprocessing om man vill.</summary>
        private void rendering_frame()
        {
            // no-op: RgbaFrame är redan fylld rad för rad i rendering_line()
        }


    }
}
