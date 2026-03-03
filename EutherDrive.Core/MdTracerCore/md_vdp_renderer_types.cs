namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        internal sealed class VDP_REGISTER
        {
            public int display_xsize;
            public int display_ysize;
            public int scroll_xsize;
            public int scroll_xcell;
            public int scroll_mask;
            public int scrollw_xcell;
            public int vdp_reg_1_6_display;
            public int vdp_reg_2_scrolla;
            public int vdp_reg_4_scrollb;
            public int vdp_reg_3_windows;
            public uint vdp_reg_7_backcolor;
            public uint vdp_reg_12_3_shadow;
            public uint screenA_left;
            public uint screenA_right;
            public uint screenA_top;
            public uint screenA_bottom;
        }

        internal struct VDP_LINE_SNAP
        {
            public int hscrollA;
            public int hscrollB;
            public int scrolla_base;
            public int scrollb_base;
            public int window_base;
            public byte reg11_vscroll_mode;
            public byte reg17_window_h;
            public byte reg18_window_v;
            public uint reg7_backcolor;
            public uint reg12_shadow_enable;
            public int window_x_st;
            public int window_x_ed;
            public int sprite_rendrere_num;
            public bool sprite_overflow;

            public int[] vscrollA;
            public int[] vscrollB;

            public int[] sprite_left;
            public int[] sprite_right;
            public int[] sprite_top;
            public int[] sprite_bottom;

            public int[] sprite_xcell_size;
            public int[] sprite_ycell_size;
            public int[] sprite_y_in_sprite;

            public uint[] sprite_priority;
            public uint[] sprite_palette;
            public uint[] sprite_reverse;
            public uint[] sprite_char;
        }

        private struct SpriteRowCacheRow
        {
            public int Count;
            public int TotalSprites;
            public int TotalCells;
            public bool Overflow;
            public byte[] SpriteIndices;
            public byte[] YInSprite;
            public byte[] Width;
            public byte[] Height;
        }
    }
}
