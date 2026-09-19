using System.Buffers.Binary;
using System.Diagnostics;
using Ryu64.MIPS;

if (args.Length == 1 && args[0] == "--check-eeprom")
{
    EepromChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--check-render")
{
    RenderChecks.Run();
    return;
}
if (args.Length < 2) throw new ArgumentException("Usage: N64Probe ROM OUTPUT_DIR [seconds=30] [state.bin]");
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
byte[] rom = File.ReadAllBytes(args[0]);
uint header = BinaryPrimitives.ReadUInt32BigEndian(rom);
if (header == 0x37804012)
    for (int i = 0; i < rom.Length; i += 2) (rom[i], rom[i + 1]) = (rom[i + 1], rom[i]);
else if (header == 0x40123780)
    for (int i = 0; i < rom.Length; i += 4) Array.Reverse(rom, i, 4);
else if (header != 0x80371240) throw new InvalidDataException("Unknown N64 ROM byte order");
string normalized = Path.Combine(output, "input.z64");
File.WriteAllBytes(normalized, rom);
var core = new Ryu64Core.Ryu64Core();
core.LoadROM(normalized);
if (args.Length > 3) core.LoadState(args[3]);
if (Environment.GetEnvironmentVariable("N64_PROBE_REPLAY_RDP") == "1")
{
    var memory = R4300.memory;
    uint start = memory.ReadUInt32(0x04100000);
    uint end = memory.ReadUInt32(0x04100004);
    var execute = typeof(Memory).GetMethod("ExecuteRdpDisplayList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    execute.Invoke(memory, new object[] { start, end });
    uint color = memory.LastRdpColorImageAddress;
    var pixels = new byte[320 * 240 * 2];
    Buffer.BlockCopy(memory.RDRAM, (int)color, pixels, 0, pixels.Length);
    WriteFrame(Path.Combine(output, "replay.ppm"), pixels, 320, 240, 2);
    Console.WriteLine($"replay=0x{start:x8}-0x{end:x8} color=0x{color:x8}");
    return;
}
core.Start();
var timer = Stopwatch.StartNew();
int seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
try
{
    for (int second = 0; second < seconds; second++)
    {
        Thread.Sleep(1000);
        if (Environment.GetEnvironmentVariable("N64_PROBE_AUTO_INPUT") == "1")
            core.SetInputState(new Ryu64Core.InputState { Start = second >= 15 && second % 10 < 2, A = second >= 35 && second % 10 is >= 5 and < 7 });
        Console.WriteLine($"seconds={timer.Elapsed.TotalSeconds:F2} {core.LastExecutionStatus}");
        if (second % 5 == 4)
        {
            Console.WriteLine(core.LastPerformanceStatus);
            uint origin = R4300.memory.ReadUInt32(0x04400004) & 0xffffff;
            int viWidth = (int)(R4300.memory.ReadUInt32(0x04400008) & 0xfff);
            int viBpp = (R4300.memory.ReadUInt32(0x04400000) & 3) == 3 ? 4 : 2;
            if (origin >= 0x1000 && viWidth > 0 && origin + viWidth * 240 * viBpp <= R4300.memory.RDRAM.Length)
            {
                var vi = new byte[viWidth * 240 * viBpp];
                Buffer.BlockCopy(R4300.memory.RDRAM, (int)origin, vi, 0, vi.Length);
                WriteFrame(Path.Combine(output, $"vi-{second + 1:D4}.ppm"), vi, viWidth, 240, viBpp);
            }
            if (origin >= 0x1000 && core.TryGetFramebuffer(out var pixels, out int width, out int height, out int bpp))
            {
                WriteFrame(Path.Combine(output, $"frame-{second + 1:D4}.ppm"), pixels, width, height, bpp);
            }
        }
    }
    core.Stop();
    core.SaveState(Path.Combine(output, "state.bin"));
}
finally { core.Stop(); }
File.WriteAllBytes(Path.Combine(output, "rdram.bin"), R4300.memory.RDRAM);
Console.WriteLine(core.LastPerformanceStatus);

static void WriteFrame(string path, byte[] pixels, int width, int height, int bpp)
{
    using var frame = File.Create(path);
    frame.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    for (int p = 0; p < width * height; p++)
    {
        if (bpp == 2)
        {
            int c = (pixels[p * 2] << 8) | pixels[p * 2 + 1];
            frame.WriteByte((byte)(((c >> 11) & 31) * 255 / 31));
            frame.WriteByte((byte)(((c >> 6) & 31) * 255 / 31));
            frame.WriteByte((byte)(((c >> 1) & 31) * 255 / 31));
        }
        else frame.Write(pixels, p * bpp, 3);
    }
}
