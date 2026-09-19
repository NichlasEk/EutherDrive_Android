using System.Diagnostics;
using EutherDrive.Core;
using EutherDrive.Core.Arcade.Taito;

if (args.Length == 2 && args[0] == "--checks") { Checks.Run(args[1]); return; }
if (args.Length == 0) throw new ArgumentException("ArkanoidProbe ROM.zip [frames=1800] [output-directory]");
var core = new ArkanoidAdapter(); core.LoadRom(args[0]);
int frames = args.Length > 1 ? int.Parse(args[1]) : 1800;
string output = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "arkanoid-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var sw = Stopwatch.StartNew();
using var wav = new BinaryWriter(File.Create(Path.Combine(output, "audio.wav")));
core.GetAudioBuffer(out int sampleRate, out int channels);
wav.Write("RIFF"u8); wav.Write(0); wav.Write("WAVEfmt "u8); wav.Write(16); wav.Write((short)1);
wav.Write((short)channels); wav.Write(sampleRate); wav.Write(sampleRate * channels * 2);
wav.Write((short)(channels * 2)); wav.Write((short)16); wav.Write("data"u8); wav.Write(0);
long samples = 0, squares = 0, emulationTicks = 0; int peak = 0;
for (int f = 0; f < frames; f++)
{
    core.SetInputState(false, false, f > 650 && f % 120 < 60, f > 650 && f % 120 >= 60,
        f > 650 && f % 60 < 5, false, false, f is >= 620 and < 625,
        false, false, false, f is >= 560 and < 565, PadType.SixButton);
    long before = Stopwatch.GetTimestamp(); core.RunFrame(); emulationTicks += Stopwatch.GetTimestamp() - before;
    foreach (short s in core.GetAudioBuffer(out _, out _)) { wav.Write(s); samples++; squares += (long)s * s; peak = Math.Max(peak, Math.Abs((int)s)); }
    if (f % 120 == 119 || f == frames - 1)
    {
        Console.WriteLine($"frame={f + 1} z80={core.ProgramCounter:X4} mcu={core.McuProgramCounter:X3} mcuInstructions={core.McuInstructions} control={core.Control:X2}");
        var pixels = core.GetFrameBuffer(out int w, out int h, out _);
        using var file = File.Create(Path.Combine(output, $"frame-{f + 1:D5}.ppm"));
        file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"));
        for (int i = 0; i < pixels.Length; i += 4) { file.WriteByte(pixels[i + 2]); file.WriteByte(pixels[i + 1]); file.WriteByte(pixels[i]); }
    }
}
wav.BaseStream.Position = 4; wav.Write(checked((int)(samples * 2 + 36)));
wav.BaseStream.Position = 40; wav.Write(checked((int)(samples * 2)));
Console.WriteLine($"{frames / sw.Elapsed.TotalSeconds:F1} fps (including captures); audio peak={peak}, RMS={Math.Sqrt((double)squares / Math.Max(1, samples)):F1}; captures={output}");
Console.WriteLine($"Emulation only: {frames * (double)Stopwatch.Frequency / emulationTicks:F1} fps, {emulationTicks * 1000.0 / Stopwatch.Frequency / frames:F3} ms/frame");
