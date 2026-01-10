using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        public void reset()
        {
            ResetState();
        }

        private void ResetState()
        {
            if (g_vram != null && g_vram.Length > 0)
            {
                TrackVramClear("ResetState()");
                Array.Clear(g_vram, 0, g_vram.Length);
            }

            if (g_cram != null && g_cram.Length > 0)
                Array.Clear(g_cram, 0, g_cram.Length);

            if (g_vsram != null && g_vsram.Length > 0)
                Array.Clear(g_vsram, 0, g_vsram.Length);

            if (g_color != null && g_color.Length > 0)
                Array.Fill(g_color, 0xFF000000u);

            if (g_color_shadow != null && g_color_shadow.Length > 0)
                Array.Fill(g_color_shadow, 0xFF000000u);

            if (g_color_highlight != null && g_color_highlight.Length > 0)
                Array.Fill(g_color_highlight, 0xFF000000u);

            if (g_pattern_chk != null && g_pattern_chk.Length > 0)
                Array.Clear(g_pattern_chk, 0, g_pattern_chk.Length);

            if (g_game_cmap != null && g_game_cmap.Length > 0)
                Array.Clear(g_game_cmap, 0, g_game_cmap.Length);

            if (g_game_primap != null && g_game_primap.Length > 0)
                Array.Clear(g_game_primap, 0, g_game_primap.Length);

            if (g_game_shadowmap != null && g_game_shadowmap.Length > 0)
                Array.Clear(g_game_shadowmap, 0, g_game_shadowmap.Length);

            if (g_game_screen != null && g_game_screen.Length > 0)
                Array.Fill(g_game_screen, 0xFF000000u);

            if (g_renderer_vram != null && g_renderer_vram.Length > 0)
                Array.Clear(g_renderer_vram, 0, g_renderer_vram.Length);

            if (g_vdp_reg == null || g_vdp_reg.Length != 24)
                g_vdp_reg = new byte[24];
            else
                Array.Clear(g_vdp_reg, 0, g_vdp_reg.Length);

            g_vdp_reg_code = 0;
            g_vdp_reg_dest_address = 0;
            g_vdp_reg_2_scrolla = 0xFFFF;
            g_vdp_reg_3_windows = 0xFFFF;
            g_vdp_reg_4_scrollb = 0xFFFF;
            g_vdp_reg_5_sprite = 0xFFFF;
            g_vdp_reg_7_backcolor = 0;
            g_vdp_reg_10_hint = 0;
            g_vdp_reg_11_3_ext = 0;
            g_vdp_reg_11_2_vscroll = 0;
            g_vdp_reg_11_1_hscroll = 0;
            g_vdp_reg_12_7_cellmode1 = 0;
            g_vdp_reg_12_3_shadow = 0;
            g_vdp_reg_12_2_interlacemode = 0;
            g_vdp_interlace_mode = 0;
            g_vdp_interlace_field = 0;
            ApplyInterlaceOverrides();
            g_vdp_reg_12_0_cellmode2 = 0;
            g_sprite_vmask = 0x1FF;
            g_vdp_reg_13_hscroll = 0;
            g_vdp_reg_15_autoinc = 0;
            g_vdp_reg_16_5_scrollV = 0;
            g_vdp_reg_16_1_scrollH = 0;
            g_vdp_reg_17_7_windows = 0;
            g_vdp_reg_17_4_basspointer = 0;
            g_vdp_reg_18_7_windows = 0;
            g_vdp_reg_18_4_basspointer = 0;
            g_vdp_reg_19_dma_counter_low = 0;
            g_vdp_reg_20_dma_counter_high = 0;
            g_vdp_reg_21_dma_source_low = 0;
            g_vdp_reg_22_dma_source_mid = 0;
            g_vdp_reg_23_dma_mode = 0;
            g_vdp_reg_23_5_dma_high = 0;

            g_display_xsize = 256;
            g_display_ysize = 224;
            g_scroll_xcell = 32;
            g_scroll_ycell = 32;
            g_scroll_xsize = 256;
            g_scroll_ysize = 256;
            g_scroll_xsize_mask = 0x00FF;
            g_scroll_ysize_mask = 0x00FF;
            g_vertical_line_max = 262;
            UpdateOutputWidth();

            g_vdp_status_9_empl = 1;
            g_vdp_status_8_full = 0;
            g_vdp_status_7_vinterrupt = 0;
            g_vdp_status_6_sprite = 0;
            g_vdp_status_5_collision = 0;
            g_vdp_status_4_frame = 0;
            g_vdp_status_3_vbrank = 0;
            g_vdp_status_2_hbrank = 0;
            g_vdp_status_1_dma = 0;
            g_vdp_status_0_tvmode = 0;
            g_vdp_c00008_hvcounter = 0;
            g_vdp_c00008_hvcounter_latched = false;

            _frameCounter = 0;
            g_scanline = 0;
            g_hinterrupt_counter = -1;
            _vblankActive = false;
            _forceVBlankLogged = false;
            _forceMdVBlankLogged = false;
            _lastForcedVBlankFrame = -1;
            _lastForcedMdVBlankFrame = -1;
            _lastTriggerVBlankLogFrame = -1;
            _lastStatusReadLogFrame = -1;

            _smsBeWritesThisFrame = 0;
            _smsBfWritesThisFrame = 0;
            _smsBeWritesLastFrame = 0;
            _smsBfWritesLastFrame = 0;
            _smsVramWritesTotal = 0;
            _smsCramWritesTotal = 0;
            _smsVramWritesAtLastSummary = 0;
            _smsCramWritesAtLastSummary = 0;
            _smsCommandPending = false;
            _smsCommandLow = 0;
            _smsVdpCode = 0;
            _smsVdpAddr = 0;
            _smsCommandLogCount = 0;
            _smsDisplayOnLogged = false;
            g_hmodeLogged = false;
            _smsDataIgnoredLogged = false;
            _smsCramWriteLogged = false;
            _smsFirstLineRendered = false;
            _smsFrameHashCounter = 0;
            _smsLastFrameHash = 0;

            g_dma_mode = 0;
            g_dma_src_addr = 0;
            g_dma_leng = 0;
            g_dma_fill_req = false;
            g_dma_fill_data = 0;

            ApplyHorizontalMode(IsH40Mode());
            ClearVBlank();
        }
    }
}
