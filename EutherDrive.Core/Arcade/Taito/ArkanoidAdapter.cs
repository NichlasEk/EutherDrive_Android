// SPDX-License-Identifier: BSD-3-Clause
// Board/video reference: MAME arkanoid.cpp/arkanoid_v.cpp (Brad Oliver, Stephane Humbert).
using System.IO.Compression;
using EutherDrive.Core.Cpu.Z80Emu;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Arcade.Taito;

public sealed class ArkanoidAdapter : IEmulatorCore, IBusInterface, ISavestateCapable
{
    [NonSerialized] private readonly byte[] rom = new byte[65536];
    private readonly byte[] ram = new byte[0x2000];
    [NonSerialized] private readonly byte[] graphics = new byte[4096 * 64], frame = new byte[224 * 256 * 4];
    [NonSerialized] private readonly uint[] palette = new uint[512];
    [NonSerialized] private readonly int sampleRate = int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_OUTPUT_HZ"), out int rate)
        && rate is >= 22050 and <= 192000 ? rate : 44100;
    [NonSerialized] private readonly short[] audio = new short[6600];
    private Z80 cpu = new();
    private ArkanoidMcu? mcu;
    private readonly ArkanoidPsg psg = new();
    private long cycles, frameEnd, mcuCycles, nextSample;
    private int control, watchdog;
    [NonSerialized] private int audioCount, volume = 100;
    [field: NonSerialized] public RomIdentity? RomIdentity { get; private set; }
    public long? FrameCounter { get; private set; }
    private bool irq, left, right, fire, start, coin;
    private byte paddle;
    public ushort ProgramCounter => cpu.Pc;
    public int McuProgramCounter => mcu?.Pc ?? 0;
    public long McuInstructions => mcu?.Instructions ?? 0;
    public int Control => control;
    public double GetTargetFps() => 6_000_000.0 / (384 * 264);
    public void SetMasterVolumePercent(int value) => volume = Math.Clamp(value, 0, 200);
    public static bool IsSupportedArchive(string path) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "arkanoid" or "arkanoidu";

    public void LoadRom(string path)
    {
        if (!IsSupportedArchive(path)) throw new NotSupportedException("Supported Arkanoid sets: arkanoid.zip and arkanoidu.zip.");
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        static string Normalize(string name) => Path.GetFileName(name).Replace("__", "-");
        void ReadArchive(string archive)
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > 0x20000) continue;
                using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output);
                entries[Normalize(entry.Name)] = output.ToArray();
            }
        }
        string parent = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "arkanoid.zip");
        if (!Path.GetFullPath(path).Equals(parent, StringComparison.Ordinal) && File.Exists(parent)) ReadArchive(parent);
        ReadArchive(path);
        byte[] Get(int size, params string[] names)
        {
            foreach (string name in names)
                if (entries.TryGetValue(name, out var data))
                { if (data.Length != size) throw new InvalidDataException($"Incorrect size: {name}"); return data; }
            throw new FileNotFoundException($"Arkanoid ROM missing: {string.Join(" / ", names)}. Keep arkanoid.zip beside the clone archive.");
        }
        bool usa = Path.GetFileNameWithoutExtension(path).Equals("arkanoidu", StringComparison.OrdinalIgnoreCase);
        Get(32768, usa ? "a75-19.ic17" : "a75-01-1.ic17").CopyTo(rom, 0);
        Get(32768, usa ? "a75-18.ic16" : "a75-11.ic16").CopyTo(rom, 32768);
        byte[] mcuImage = Get(2048, usa ? "a75-20.ic14" : "a75-06.ic14");
        mcu = new ArkanoidMcu(mcuImage);
        var planes = new[] { Get(32768, "a75-03.ic64"), Get(32768, "a75-04.ic63"), Get(32768, "a75-05.ic62") };
        for (int tile = 0; tile < 4096; tile++)
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    graphics[tile * 64 + y * 8 + x] = DecodeGraphicsPixel(
                        planes[0][tile * 8 + y], planes[1][tile * 8 + y], planes[2][tile * 8 + y], x);
        var red = Get(512, "a75-07.ic24"); var green = Get(512, "a75-08.ic23"); var blue = Get(512, "a75-09.ic22");
        for (int i = 0; i < 512; i++) palette[i] = 0xff000000u | (uint)((red[i] & 15) * 17 << 16 | (green[i] & 15) * 17 << 8 | (blue[i] & 15) * 17);
        using var identityData = new MemoryStream();
        identityData.Write(rom); identityData.Write(mcuImage);
        foreach (var plane in planes) identityData.Write(plane);
        identityData.Write(red); identityData.Write(green); identityData.Write(blue);
        RomIdentity = new RomIdentity(Path.GetFileNameWithoutExtension(path), RomIdentity.ComputeSha256(identityData.ToArray()));
        Reset();
    }
    // MAME's layout { 0x10000, 0x8000, 0 } lists the MOST significant plane first.
    // Therefore IC64 (region offset 0) supplies bit 0, not bit 2.
    internal static byte DecodeGraphicsPixel(byte ic64, byte ic63, byte ic62, int x)
        => (byte)(((ic64 >> (7 - x)) & 1) | (((ic63 >> (7 - x)) & 1) << 1)
            | (((ic62 >> (7 - x)) & 1) << 2));
    public void Reset()
    {
        cpu = new Z80(); cpu.ApplyResetLine(); mcu?.Reset(); psg.Reset();
        Array.Clear(ram); Array.Clear(frame); cycles = frameEnd = mcuCycles = 0; nextSample = 6_000_000;
        control = audioCount = watchdog = 0; irq = false; paddle = 0;
        left = right = fire = start = coin = false;
        FrameCounter = 0;
    }
    public void RunFrame()
    {
        if (mcu == null) throw new InvalidOperationException("No Arkanoid ROM loaded.");
        if (++watchdog >= 128) Reset();
        paddle = unchecked((byte)(paddle + (right ? 5 : 0) - (left ? 5 : 0)));
        mcu.Paddle = (control & 4) == 0 ? paddle : (byte)0;
        audioCount = 0; frameEnd += 384 * 264;
        while (cycles < frameEnd)
        {
            cycles += cpu.ExecuteInstruction(this);
            if (cpu.LastInterruptAccepted) irq = false;
            while (mcuCycles * 8 < cycles) mcuCycles += mcu.Step();
            while (nextSample <= cycles * sampleRate)
            {
                short sample = psg.Sample(volume, sampleRate);
                audio[audioCount++] = sample; audio[audioCount++] = sample;
                nextSample += 6_000_000;
            }
        }
        Draw(); irq = true; FrameCounter++;
    }
    public void SaveState(BinaryWriter writer)
    {
        if (mcu == null) throw new InvalidOperationException("No ROM loaded.");
        writer.Write(0x41524b31); writer.Write(sampleRate);
        StateBinarySerializer.WriteInto(writer, this);
    }
    public void LoadState(BinaryReader reader)
    {
        if (mcu == null) throw new InvalidOperationException("No ROM loaded.");
        if (reader.ReadInt32() != 0x41524b31 || reader.ReadInt32() != sampleRate)
            throw new InvalidDataException("Incompatible Arkanoid state version or audio sample rate.");
        StateBinarySerializer.ReadInto(reader, this);
        audioCount = 0; Draw();
    }
    public byte ReadMemory(ushort address)
    {
        if (address < 0xc000) return rom[address];
        if (address < 0xd000) return ram[address & 0x7ff];
        if (address < 0xe000)
        {
            int port = address & 0x19;
            if (port == 1) return psg.Read();
            if ((address & 0x1c) == 8) return 255;
            if ((address & 0x1c) == 12) return (byte)((start ? 0 : 1) | 14 | (coin ? 16 : 0) |
                (mcu!.HostPending ? 0 : 64) | (mcu.ReplyPending ? 0 : 128));
            if ((address & 0x18) == 16) return (byte)(fire ? 254 : 255);
            if ((address & 0x18) == 24) return mcu!.HostRead();
            return 255;
        }
        return address < 0xf000 ? ram[0x1000 + (address & 0xfff)] : (byte)0;
    }
    public void WriteMemory(ushort address, byte value)
    {
        if (address < 0xc000) return;
        if (address < 0xd000) { ram[address & 0x7ff] = value; return; }
        if (address < 0xe000)
        {
            switch (address & 0x18)
            {
                case 0: if ((address & 1) == 0) psg.Address(value); else psg.Write(value); break;
                case 8: control = value; mcu!.Enable((value & 128) != 0); break;
                case 16: watchdog = 0; break;
                case 24: mcu!.HostWrite(value); break;
            }
        }
        else if (address < 0xf000) ram[0x1000 + (address & 0xfff)] = value;
    }
    public byte ReadIo(ushort address) => 255;
    public void WriteIo(ushort address, byte value) { }
    public InterruptLine Nmi() => InterruptLine.High;
    public InterruptLine Int() => irq ? InterruptLine.Low : InterruptLine.High;
    public bool BusReq() => false;
    bool IBusInterface.Reset() => false;
    private void Draw()
    {
        int gfxBank = (control >> 5) & 1, colorBank = (control & 64) >> 1;
        bool flipX = (control & 1) != 0, flipY = (control & 2) != 0;
        void Tile(int code, int color, int sx, int sy, bool transparent)
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int pen = graphics[(code & 4095) * 64 + (flipY ? 7 - y : y) * 8 + (flipX ? 7 - x : x)];
                    int px = sx + x, py = sy + y;
                    if (px < 0 || px >= 256 || py < 16 || py >= 240 || (transparent && pen == 0)) continue;
                    // ROT90: native 256x224 -> upright 224x256.
                    int offset = (px * 224 + 239 - py) * 4;
                    uint rgb = palette[color * 8 + pen];
                    frame[offset] = (byte)rgb; frame[offset + 1] = (byte)(rgb >> 8);
                    frame[offset + 2] = (byte)(rgb >> 16); frame[offset + 3] = 255;
                }
        }
        for (int i = 0; i < 1024; i++)
        {
            int attr = ram[0x1000 + i * 2], code = ram[0x1001 + i * 2] | (attr & 7) << 8 | gfxBank << 11;
            int sx = (i & 31) * 8, sy = (i >> 5) * 8;
            Tile(code, (attr >> 3) + colorBank, flipX ? 248 - sx : sx, flipY ? 248 - sy : sy, false);
        }
        for (int i = 0; i < 64; i += 4)
        {
            int sx = ram[0x1800 + i], sy = 248 - ram[0x1801 + i], attr = ram[0x1802 + i];
            int code = ram[0x1803 + i] | (attr & 3) << 8 | gfxBank << 10;
            if (flipX) sx = 248 - sx; if (flipY) sy = 248 - sy;
            Tile(code * 2, (attr >> 3) + colorBank, sx, sy + (flipY ? 8 : -8), true);
            Tile(code * 2 + 1, (attr >> 3) + colorBank, sx, sy, true);
        }
    }
    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    { width = 224; height = 256; stride = 896; return frame; }
    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    { sampleRate = this.sampleRate; channels = 2; return audio.AsSpan(0, audioCount); }
    public void SetInputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c,
        bool start, bool x, bool y, bool z, bool mode, PadType padType)
    { this.left = left; this.right = right; fire = a || b || c; this.start = start; coin = mode; }
}
