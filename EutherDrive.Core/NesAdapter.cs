using System;
using System.IO;
using XamariNES.Cartridge;
using XamariNES.Controller;
using XamariNES.Controller.Enums;
using CpuCore = XamariNES.CPU.Core;
using PpuCore = XamariNES.PPU.Core;

namespace EutherDrive.Core;

public sealed class NesAdapter : IEmulatorCore
{
    private const int DefaultWidth = 256;
    private const int DefaultHeight = 240;
    private const int DefaultStride = DefaultWidth * 4;

    private NESCartridge? _cartridge;
    private CpuCore? _cpu;
    private PpuCore? _ppu;
    private NESController? _controller;
    private byte[] _frameBuffer = new byte[DefaultHeight * DefaultStride];
    private short[] _audioBuffer = Array.Empty<short>();
    private int _cpuIdleCycles;
    private string? _romSummary;

    public string? RomSummary => _romSummary;

    public void LoadRom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ROM not found", path);

        byte[] rom = File.ReadAllBytes(path);
        _cartridge = new NESCartridge(rom);
        _controller = new NESController();
        _ppu = new PpuCore(_cartridge.MemoryMapper, DmaTransfer);
        _cpu = new CpuCore(_cartridge.MemoryMapper, _controller);
        Reset();
        _romSummary = BuildRomSummary(path, rom);
    }

    public void Reset()
    {
        if (_cpu == null || _ppu == null)
            return;
        _cpu.Reset();
        _ppu.Reset();
        _cpu.Cycles = 4;
        _cpuIdleCycles = 0;
    }

    public void RunFrame()
    {
        if (_cpu == null || _ppu == null)
            return;

        while (true)
        {
            int cpuTicks;
            if (_cpuIdleCycles == 0)
            {
                cpuTicks = _cpu.Tick();
            }
            else
            {
                _cpuIdleCycles--;
                _cpu.Instruction.Cycles = 1;
                _cpu.Cycles++;
                cpuTicks = 1;
            }

            for (int i = 0; i < cpuTicks * 3; i++)
            {
                _ppu.Tick();
            }

            if (_ppu.NMI)
            {
                _ppu.NMI = false;
                _cpu.NMI = true;
            }

            if (_ppu.FrameReady)
            {
                ConvertFrameBuffer(_ppu.FrameBuffer, _frameBuffer);
                _ppu.FrameReady = false;
                break;
            }
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = DefaultWidth;
        height = DefaultHeight;
        stride = DefaultStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 2;
        return _audioBuffer;
    }

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        _ = c;
        _ = x;
        _ = y;
        _ = z;
        _ = padType;

        if (_controller == null)
            return;

        SetButton(enumButtons.Up, up);
        SetButton(enumButtons.Down, down);
        SetButton(enumButtons.Left, left);
        SetButton(enumButtons.Right, right);
        SetButton(enumButtons.A, a);
        SetButton(enumButtons.B, b);
        SetButton(enumButtons.Start, start);
        SetButton(enumButtons.Select, mode);
    }

    private void SetButton(enumButtons button, bool pressed)
    {
        if (_controller == null)
            return;
        if (pressed)
            _controller.ButtonPress(button);
        else
            _controller.ButtonRelease(button);
    }

    private byte[] DmaTransfer(byte[] oam, int oamOffset, int offset)
    {
        if (_cpu == null)
            return oam;

        for (int i = 0; i < 256; i++)
        {
            oam[(oamOffset + i) % 256] = _cpu.CPUMemory.ReadByte(offset + i);
        }

        _cpuIdleCycles = 513;
        if (_cpu.Cycles % 2 == 1)
            _cpuIdleCycles++;

        return oam;
    }

    private static void ConvertFrameBuffer(byte[] src, byte[] dst)
    {
        int count = DefaultWidth * DefaultHeight;
        if (dst.Length < count * 4)
            return;

        for (int i = 0; i < count; i++)
        {
            int color = (src[i] & 0x3F) * 4;
            int o = i * 4;
            dst[o + 0] = PaletteBgra[color + 0];
            dst[o + 1] = PaletteBgra[color + 1];
            dst[o + 2] = PaletteBgra[color + 2];
            dst[o + 3] = 0xFF;
        }
    }

    private static string BuildRomSummary(string path, byte[] data)
    {
        string name = Path.GetFileName(path);
        if (data.Length < 16)
            return $"NES: {name}";

        int prgBanks = data[4];
        int chrBanks = data[5];
        int mapper = (data[7] & 0xF0) | ((data[6] >> 4) & 0x0F);
        int prgSize = prgBanks * 16;
        int chrSize = chrBanks * 8;
        return $"NES: {name} | PRG {prgSize}KB | CHR {chrSize}KB | Mapper {mapper}";
    }

    private static readonly byte[] PaletteBgra = BuildPaletteBgra();

    private static byte[] BuildPaletteBgra()
    {
        uint[] rgb =
        {
            0x7C7C7C, 0x0000FC, 0x0000BC, 0x4428BC, 0x940084, 0xA80020, 0xA81000, 0x881400,
            0x503000, 0x007800, 0x006800, 0x005800, 0x004058, 0x000000, 0x000000, 0x000000,
            0xBCBCBC, 0x0078F8, 0x0058F8, 0x6844FC, 0xD800CC, 0xE40058, 0xF83800, 0xE45C10,
            0xAC7C00, 0x00B800, 0x00A800, 0x00A844, 0x008888, 0x000000, 0x000000, 0x000000,
            0xF8F8F8, 0x3CBCFC, 0x6888FC, 0x9878F8, 0xF878F8, 0xF85898, 0xF87858, 0xFCA044,
            0xF8B800, 0xB8F818, 0x58D854, 0x58F898, 0x00E8D8, 0x787878, 0x000000, 0x000000,
            0xFCFCFC, 0xA4E4FC, 0xB8B8F8, 0xD8B8F8, 0xF8B8F8, 0xF8A4C0, 0xF0D0B0, 0xFCE0A8,
            0xF8D878, 0xD8F878, 0xB8F8B8, 0xB8F8D8, 0x00FCFC, 0xF8D8F8, 0x000000, 0x000000,
        };

        var palette = new byte[64 * 4];
        for (int i = 0; i < 64; i++)
        {
            uint c = rgb[i];
            int o = i * 4;
            palette[o + 0] = (byte)(c & 0xFF);
            palette[o + 1] = (byte)((c >> 8) & 0xFF);
            palette[o + 2] = (byte)((c >> 16) & 0xFF);
            palette[o + 3] = 0xFF;
        }

        return palette;
    }
}
