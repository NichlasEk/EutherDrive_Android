using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal static class md_psg_trace
    {
        private const int LogLimitPerFrame = 8;
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PSG") == "1";

        private static long _lastFrame = -1;
        private static int _writesThisFrame;
        private static int _detailLogsThisFrame;

        public static void TraceWrite(string source, uint addr, byte value, uint pc)
        {
            if (!Enabled)
                return;

            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            if (frame != _lastFrame)
            {
                if (_lastFrame >= 0)
                {
                    Console.WriteLine($"[PSG] frame={_lastFrame} writes={_writesThisFrame}");
                }
                _lastFrame = frame;
                _writesThisFrame = 0;
                _detailLogsThisFrame = 0;
            }

            _writesThisFrame++;
            if (_detailLogsThisFrame < LogLimitPerFrame)
            {
                _detailLogsThisFrame++;
                Console.WriteLine($"[PSG] {source} addr=0x{addr:X6} val=0x{value:X2} pc=0x{pc:X6}");
            }
        }
    }
}
