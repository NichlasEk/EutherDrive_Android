using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore;

internal static class MdVdpInterlaceModeTest
{
    public static void Run()
    {
        var vdp = new md_vdp();
        ushort reg12Mode2 = (ushort)(0x8000 | (0x0C << 8) | 0x06);
        vdp.write16(0xC00004, reg12Mode2);

        Debug.Assert(vdp.g_vdp_interlace_mode == 2, "Expected interlace mode 2 after reg 0x0C write.");

        byte field0 = vdp.InterlaceField;
        StepFrame(vdp);
        byte field1 = vdp.InterlaceField;
        Debug.Assert(field1 != field0, "Expected field parity to flip after one frame.");

        StepFrame(vdp);
        byte field2 = vdp.InterlaceField;
        Debug.Assert(field2 == field0, "Expected field parity to flip back after two frames.");
    }

    private static void StepFrame(md_vdp vdp)
    {
        int lines = vdp.g_display_ysize;
        for (int line = 0; line <= lines; line++)
            vdp.run(line);
    }
}
