namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        // Kompatibilitet: vissa delar av koden förväntar sig "reset()"
        public void reset()
        {
            // Om du redan har Reset() eller liknande – kalla den här istället.
            // Reset();
        }
    }
}
