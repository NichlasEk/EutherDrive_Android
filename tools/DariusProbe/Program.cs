using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EutherDrive.Core;
using EutherDrive.Core.Arcade.Taito;
using EutherDrive.Core.Savestates;

if (args.Length == 1 && args[0] == "--check-gfx-planes")
{
    GfxPlaneChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--check-m68k-trace")
{
    TraceSwitchChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--check-task-frame-filter")
{
    TaskFrameFilterChecks.Run();
    return;
}
if (args.Length < 1)
    throw new ArgumentException("Usage: DariusProbe ROM.zip [frames=1800] [state-directory slot]");
int frames = args.Length > 1 ? int.Parse(args[1]) : 1800;
using var core = new DariusGaidenAdapter();
core.LoadRom(args[0]);
core.SetMasterVolumePercent(100);
bool loadedState = args.Length >= 4;
if (loadedState)
{
    var identity = core.RomIdentity!;
    new SavestateService().Load(new RomIdentity(identity.Name, identity.Hash, args[2]), core, int.Parse(args[3]));
}
using var video = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
using var audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
long ticks = 0, gameplayTicks = 0;
int gameplayFrames = 0, overBudget = 0;
var frameTimes = new List<double>();
long budget = (long)(Stopwatch.Frequency / core.GetTargetFps());
for (int frame = 0; frame < frames; frame++)
{
    core.SetInputState(false, false, false, false, loadedState || frame >= 700, false, false,
        !loadedState && frame is >= 650 and <= 654, false, false, false,
        !loadedState && frame is >= 600 and <= 604, PadType.SixButton);
    long start = Stopwatch.GetTimestamp();
    core.RunFrame();
    long elapsed = Stopwatch.GetTimestamp() - start;
    ticks += elapsed;
    if (loadedState || frame >= 900)
    {
        gameplayFrames++;
        gameplayTicks += elapsed;
        if (elapsed > budget) overBudget++;
        frameTimes.Add(elapsed * 1000.0 / Stopwatch.Frequency);
    }
    video.AppendData(core.GetFrameBuffer(out _, out _, out _));
    audio.AppendData(MemoryMarshal.AsBytes(core.GetAudioBuffer(out _, out _)));
}
using var state = new MemoryStream();
using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, leaveOpen: true))
    core.SaveState(writer);
Console.WriteLine($"frames={frames} runMs={ticks * 1000.0 / Stopwatch.Frequency:F3} gameplayFrames={gameplayFrames} gameplayMs={gameplayTicks * 1000.0 / Stopwatch.Frequency:F3} overBudget={overBudget} targetFps={core.GetTargetFps():F3}");
if (frameTimes.Count != 0)
{
    frameTimes.Sort();
    Console.WriteLine($"p95Ms={frameTimes[(int)((frameTimes.Count - 1) * .95)]:F3} p99Ms={frameTimes[(int)((frameTimes.Count - 1) * .99)]:F3} maxMs={frameTimes[^1]:F3} over25={frameTimes.Count(t => t > 25)} over33={frameTimes.Count(t => t > 33)}");
}
Console.WriteLine($"videoSHA256={Convert.ToHexString(video.GetHashAndReset())}");
Console.WriteLine($"audioSHA256={Convert.ToHexString(audio.GetHashAndReset())}");
Console.WriteLine($"stateSHA256={Convert.ToHexString(SHA256.HashData(state.GetBuffer().AsSpan(0, (int)state.Length)))}");
Console.WriteLine(core.DebugSummary);
