using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using EutherDrive.Audio;
using EutherDrive.Core;
using Ryu64.MIPS;
using Ryu64Core;

// Diagnostic frontend model: N64Adapter and the real timed AudioEngine,
// with a null sink instead of Avalonia or the host audio driver.
if (args.Length != 4 || (args[3] != "current" && args[3] != "all"))
    throw new ArgumentException("Usage: N64AudioPlaybackProbe BIG_ENDIAN_SM64_USA_ROM STATE_AT_60 NEW_OUTPUT current|all");
if (File.Exists(args[2]) || File.Exists(args[2] + ".guest.json"))
    throw new IOException("Use a new output path; existing evidence is preserved");
byte[] header = new byte[24];
using (var rom = File.OpenRead(args[0])) rom.ReadExactly(header);
if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header) != 0x80371240
    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)) != 0x635a2bff
    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)) != 0x8b022326)
    throw new ArgumentException("The scripted input requires original big-endian Super Mario 64 USA");
using var adapter = new N64Adapter();
adapter.LoadRom(args[0]);
var core = (Ryu64Core.Ryu64Core)typeof(N64Adapter).GetField("_core", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
core.LoadState(args[1]);
if (core.GetCycleCounter() / 93750000 != 60)
    throw new ArgumentException("A core state at guest second 60 is required");
var memory = R4300.memory;
long producedFrames = 0, blocks = 0, pulledFrames = 0, skippedFrames = 0, polls = 0;
double producedSeconds = 0;
memory.PerfAudioCapture = (pcm, rate) => { Interlocked.Add(ref producedFrames, pcm.Length / 2); Interlocked.Increment(ref blocks); producedSeconds += pcm.Length / (2.0 * rate); };
var clock = Stopwatch.StartNew();
var guestPoints = new System.Collections.Concurrent.ConcurrentQueue<object>();
int nextGuest = 65;
memory.PerfControllerRead = () =>
{
    ulong cycles = core.GetCycleCounter(); int second = (int)(cycles / 93750000);
    memory.SetControllerState((ushort)(second % 4 == 0 ? 0x8000 : 0), 0, 80);
    if (second >= nextGuest)
    {
        double wall = clock.Elapsed.TotalSeconds;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.RDRAM));
        guestPoints.Enqueue(new { second = nextGuest, cycles, wall, producedSeconds, producedFrames, blocks, ram = hash }); nextGuest += 5;
    }
};
using var engine = new AudioEngine(new NullAudioSink(), 44100, 2, 256, 16384);
engine.SetTargetBufferedFrames(8820); engine.Start();
clock.Restart(); double next = 0; int nextReport = 1; bool fill = true;
var points = new List<object>();
while (Volatile.Read(ref nextGuest) <= 90 && clock.Elapsed.TotalSeconds < 70)
{
    double now = clock.Elapsed.TotalSeconds;
    if (now < next) { Thread.Sleep(1); continue; }
    if (now - next > 4.0 / 60) next = now;
    next += 1.0 / 60;
    adapter.RunFrame(); polls++;
    var audio = adapter.GetAudioBuffer(out int rate, out int channels);
    if (!audio.IsEmpty)
    {
        if (rate != 44100 || channels != 2) throw new Exception("Unexpected format");
        pulledFrames += audio.Length / 2;
        int buffered = engine.BufferedFrames;
        if (buffered <= 4410) fill = true; else if (buffered >= 8379) fill = false;
        if (args[3] == "all" || fill) engine.Submit(audio); else skippedFrames += audio.Length / 2;
    }
    if (clock.Elapsed.TotalSeconds >= nextReport)
    {
        points.Add(new { wall = clock.Elapsed.TotalSeconds, guest = core.GetCycleCounter() / 93750000.0, producedSeconds, blocks, producedFrames, pulledFrames, skippedFrames, polls, buffered = engine.BufferedFrames, underruns = engine.UnderrunEventsTotal, dropped = engine.DroppedFramesTotal });
        File.WriteAllText(args[2], JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true })); nextReport++;
    }
}
core.Stop(); engine.Stop();
File.WriteAllText(args[2] + ".guest.json", JsonSerializer.Serialize(guestPoints, new JsonSerializerOptions { WriteIndented = true }));
if (Volatile.Read(ref nextGuest) <= 90) throw new TimeoutException("Did not reach the guest-second-90 checkpoint");
Console.WriteLine("audioProbe=complete policy=" + args[3]);
