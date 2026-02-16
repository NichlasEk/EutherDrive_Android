namespace EutherDrive.Core;

using EutherDrive.Core.MdTracerCore;

internal sealed class MdTracerM68kContextRunner
{
    private readonly MdTracerM68kRunner _runner = new();

    public void EnsureInit()
    {
        _runner.EnsureInit();
    }

    public void RunSome(md_m68k.MdM68kContext context, int budget)
    {
        md_m68k.ApplyContext(context);
        _runner.EnsureInit();
        _runner.RunSome(budget);
        md_m68k.CaptureContext(context);
    }
}
