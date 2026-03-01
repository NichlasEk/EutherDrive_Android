namespace EutherDrive.Core.MdTracerCore
{
    internal static partial class md_main
    {
        internal static void PowerCycleReset()
        {
            g_md_bus?.FlushSram("powercycle");
            g_md_z80?.FlushSmsSram("powercycle");
            g_md_bus = null;
            g_md_control = null;
            g_md_io = null;
            g_md_m68k = null;
            g_md_z80 = null;
            g_md_music = null;
            g_md_vdp = null;
            g_form_code_trace = null;
            g_md_cartridge = null;
            _m68kEmuBus = null;

            g_masterSystemMode = false;
            g_masterSystemRom = null;
            g_masterSystemRomSize = 0;
            g_masterSystemRomPath = null;

            g_screenA_enable = g_screenB_enable = g_screenW_enable = g_screenS_enable = false;
            g_pattern_enable = g_pallete_enable = g_code_enable = false;
            g_io_enable = g_music_enable = g_registry_enable = g_flow_enable = false;

            g_trace_fsb = false;
            g_trace_sip = false;
            g_hard_reset_req = false;
            g_trace_nextframe = false;

            g_task_usage = 0;
            _z80ResetCycleId = 0;
        }
    }
}
