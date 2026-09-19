using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EutherDrive.Core;
using EutherDrive.Core.Arcade.Taito;

static class Checks
{
    static void Require(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static byte[] Save(ArkanoidAdapter c)
    { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); c.SaveState(writer); return stream.ToArray(); }
    static void Load(ArkanoidAdapter c, byte[] state)
    { using var stream = new MemoryStream(state); using var reader = new BinaryReader(stream); c.LoadState(reader); }
    static void Input(ArkanoidAdapter c, bool left = false, bool right = false, bool fire = false, bool coin = false, bool start = false)
        => c.SetInputState(false, false, left, right, fire, false, false, start, false, false, false, coin, PadType.SixButton);
    static byte[] Replay(ArkanoidAdapter c)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < 180; i++)
        {
            Input(c, left: i % 80 < 40, right: i % 80 >= 40, fire: i % 30 == 0);
            c.RunFrame(); hash.AppendData(c.GetFrameBuffer(out _, out _, out _));
            hash.AppendData(MemoryMarshal.AsBytes(c.GetAudioBuffer(out _, out _)));
        }
        hash.AppendData(Save(c)); return hash.GetHashAndReset();
    }
    public static void Run(string path)
    {
        CheckMcu();
        var c = new ArkanoidAdapter(); c.LoadRom(path);
        c.WriteMemory(0xc123, 0x5a);
        Require(c.ReadMemory(0xc923) == 0x5a, "work RAM mirror");
        Require(c.ReadMemory(0xf123) == 0, "final-boss open bus reads zero");
        c.Reset();
        byte[] boot = Save(c);
        byte[] a = Replay(c); c.Reset();
        Require(Save(c).SequenceEqual(boot), "reset restores initial machine state");
        Require(a.SequenceEqual(Replay(c)), "deterministic cold boot");
        c.Reset();
        long samples = 0;
        for (int f = 0; f < 1260; f++)
        {
            Input(c, coin: f is >= 560 and < 565, start: f is >= 620 and < 625);
            c.RunFrame(); samples += c.GetAudioBuffer(out int rate, out int channels).Length / channels;
        }
        c.GetAudioBuffer(out int sr, out _);
        Require(Math.Abs(samples - 1260 * 384.0 * 264 * sr / 6_000_000) <= 1, "audio sample clock tracks board clock");
        byte[] saved = Save(c), pixels = c.GetFrameBuffer(out _, out _, out _).ToArray();
        a = Replay(c); Load(c, saved);
        Require(pixels.SequenceEqual(c.GetFrameBuffer(out _, out _, out _).ToArray()), "savestate restores current image");
        Require(a.SequenceEqual(Replay(c)), "savestate replay video/audio/full state exact");
        Load(c, saved); Input(c, left: true);
        for (int i = 0; i < 12; i++) c.RunFrame();
        var leftSprites = Enumerable.Range(0, 64).Select(i => c.ReadMemory((ushort)(0xe800 + i))).ToArray();
        Load(c, saved); Input(c, right: true);
        for (int i = 0; i < 12; i++) c.RunFrame();
        var rightSprites = Enumerable.Range(0, 64).Select(i => c.ReadMemory((ushort)(0xe800 + i))).ToArray();
        Console.WriteLine("left sprites=" + Convert.ToHexString(leftSprites));
        Console.WriteLine("right sprites=" + Convert.ToHexString(rightSprites));
        Require(leftSprites[1] < rightSprites[1], "left/right paddle direction reaches game via MCU");
        Load(c, saved); c.SetMasterVolumePercent(0); c.RunFrame();
        Require(c.GetAudioBuffer(out _, out _).ToArray().All(s => s == 0), "master volume mute");
        c.SetMasterVolumePercent(100);
        Require(c.McuInstructions > 0 && c.Control != 0, "MCU executed and board enabled");
    }
    static ArkanoidMcu Mcu(params byte[] program)
    {
        byte[] rom = new byte[2048]; program.CopyTo(rom, 128); rom[2047] = 128;
        var mcu = new ArkanoidMcu(rom); mcu.Enable(true); return mcu;
    }
    static void CheckMcu()
    {
        // ADC 0x0f + 0 + carry must set H. A wrong half-carry falls through to 0xee.
        var c = Mcu(0xa6,0x0f, 0x99, 0xa9,0x00, 0x29,0x02, 0xa6,0xee,
            0xb7,0x00, 0xa6,0xff, 0xb7,0x04, 0xa6,0x0c, 0xb7,0x06,
            0xa6,0x04, 0xb7,0x02, 0x20,0xfe);
        for (int i = 0; i < 40; i++) c.Step();
        Require(c.ReplyPending && c.HostRead() == 0x10, "MCU ADC half carry and output latch");
        Require(c.ReplyPending, "MCU reply semaphore stays set while PC3 low");
        // Read a host byte through open-drain PA, clear host flag on PC2 rising edge, reply.
        c = Mcu(0xa6,0x0c, 0xb7,0x06, 0x15,0x02, 0xb6,0x00, 0xb7,0x10,
            0x14,0x02, 0xa6,0xff, 0xb7,0x04, 0xb6,0x10, 0xb7,0x00,
            0x17,0x02, 0x16,0x02, 0x20,0xfe);
        c.HostWrite(0xa5);
        Require(c.HostPending, "host write asserts MCU semaphore");
        for (int i = 0; i < 40; i++) c.Step();
        Require(!c.HostPending && c.ReplyPending && c.HostRead() == 0xa5 && !c.ReplyPending,
            "MCU bidirectional mailbox and semaphore edges");
        c.Enable(false);
        Require(!c.HostPending && !c.ReplyPending && !c.Enabled, "MCU reset clears semaphores");
    }
}
