using System;
using System.IO;
using System.Text;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core;

public sealed class MdTracerAdapter : IEmulatorCore
{
    private readonly md_vdp _vdp = new md_vdp();

    private byte[] _frameBuffer = Array.Empty<byte>(); // BGRA till UI
    private int _fbW, _fbH, _fbStride;

    private int _tick;
    private const int VLINES_NTSC = 262;
    private uint _lastPc;
    private int _pcStallFrames;

    // ROM + BUS
    private byte[]? _rom;
    private MegaDriveBus? _bus;

    // CPU runner (MDTracer m68k via reflection)
    private MdTracerM68kRunner? _cpu;
    private bool _cpuReady;

    public string RomInfo { get; private set; } = "(no rom)";

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ROM path is empty.", nameof(path));

        _rom = File.ReadAllBytes(path);
        _bus = new MegaDriveBus(_rom);

        // Koppla vår bus-bridge så MDTracer CPU-kod kan läsa/skriva
        md_bus.Current = _bus;
        EutherDrive.Core.MdTracerCore.md_bus.Current = _bus;

        // Initiera MDTracer-kärnan så att g_md_* inte är null.
        md_main.initialize();
        md_main.g_md_vdp = _vdp;
        md_main.g_md_cartridge?.load(path);

        // Steg A/B proof
        string header = TryReadSegaString(_bus);
        _bus.Write32(0xFF0000, 0x1234ABCD);
        uint wramProbe = _bus.Read32(0xFF0000);

        // Vector proof
        uint sp = _bus.Read32(0x000000);
        uint pc = _bus.Read32(0x000004);
        ushort op = _bus.Read16(pc);

        // CPU runner init (tål om md_m68k inte finns än)
        try
        {
            _cpu = new MdTracerM68kRunner();
            _cpuReady = true;
        }
        catch (Exception ex)
        {
            _cpuReady = false;
            _cpu = null;
            RomInfo = $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU: {ex.Message}";
            Reset();
            return;
        }

        RomInfo =
        $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | " +
        $"VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU API ok";

        Reset();
    }

    private static string TryReadSegaString(MegaDriveBus bus)
    {
        Span<byte> s = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            s[i] = bus.Read8((uint)(0x100 + i));

        var text = Encoding.ASCII.GetString(s);
        return $"Header@0x100: '{text}'";
    }

    public void Reset()
    {
        _tick = 0;

        // Nollställ RAM
        _bus?.Reset();

        // Stub (så VDP-testet fortsätter)
        md_main.EnsureCpuStubs();

        // Init/reset CPU om vi har den
        if (_cpuReady && _cpu != null)
        {
            // md_bus.Current är redan satt i LoadRom
            _cpu.EnsureInitAndReset();

            // Bra att skriva en gång i terminalen (kan tas bort sen)
            Console.WriteLine("m68k runner ok. Runner: " + _cpu.SelectedRunApi);
            Console.WriteLine("Methods:\n" + _cpu.DebugApi);
        }

        _vdp.SetFrameSize(320, 224);

        _fbW = _vdp.FrameWidth;
        _fbH = _vdp.FrameHeight;
        _fbStride = _fbW * 4;
        _frameBuffer = new byte[_fbStride * _fbH];
    }

    public void RunFrame()
    {
        _tick++;

        // Kör CPU “lite grann”
        // Budget är avsiktligt liten för att inte låsa UI.
        if (_cpuReady && _cpu != null)
        {
            uint pcBefore = md_m68k.g_reg_PC;
            // Om din md_m68k har run(int cycles) blir detta “cycles-ish”.
            // Om den bara har step() blir det “steps-ish”.
            _cpu.RunSome(budget: 2000);
            uint pcAfter = md_m68k.g_reg_PC;
            if (pcAfter == pcBefore)
                _pcStallFrames++;
            else
                _pcStallFrames = 0;

            if ((_tick % 60) == 0)
                Console.WriteLine($"m68k PC=0x{pcAfter:X6} stall={_pcStallFrames}");
        }

        // Fortfarande VDP-test tills vi kopplar VDP-register/IO
        for (int v = 0; v < VLINES_NTSC; v++)
            _vdp.run(v);

        // RGBA -> BGRA
        var src = _vdp.RgbaFrame; // RGBA
        int w = _vdp.FrameWidth;
        int h = _vdp.FrameHeight;

        int need = w * h * 4;
        if (w != _fbW || h != _fbH || _frameBuffer.Length != need)
        {
            _fbW = w;
            _fbH = h;
            _fbStride = _fbW * 4;
            _frameBuffer = new byte[need];
        }

        int n = Math.Min(src.Length, _frameBuffer.Length);
        for (int i = 0; i < n; i += 4)
        {
            byte r = src[i + 0];
            byte g = src[i + 1];
            byte b = src[i + 2];
            byte a = src[i + 3];

            _frameBuffer[i + 0] = b;
            _frameBuffer[i + 1] = g;
            _frameBuffer[i + 2] = r;
            _frameBuffer[i + 3] = a;
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = _fbW;
        height = _fbH;
        stride = _fbStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 1;
        return ReadOnlySpan<short>.Empty;
    }

    public void SetInputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start)
    {
        // ännu ingen input wiring för CPU/IO
    }
}
