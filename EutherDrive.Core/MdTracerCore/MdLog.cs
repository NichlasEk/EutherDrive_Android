using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal static class MdLog
    {
        internal static bool Enabled = false;

        internal static void WriteLine(string message)
        {
            if (Enabled)
                Console.WriteLine(message);
        }
    }
}
