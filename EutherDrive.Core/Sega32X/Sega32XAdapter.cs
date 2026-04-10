using System.IO;
using System.Text;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Core;

public sealed class Sega32XAdapter : IEmulatorCore, ISavestateCapable
{
    private const int FrameWidth = 320;
    private const int FrameHeight = Sega32X.Sega32XVdp.FrameHeight;
    private const int FrameStride = FrameWidth * 4;
    private const int OutputSampleRate = 44100;
    private const int OutputChannels = 2;

    private readonly object _stateLock = new();
    private readonly byte[] _frameBuffer = new byte[FrameStride * FrameHeight];
    private byte[]? _romData;
    private Sega32X.Sega32XScaffoldCore? _core;
    private string? _romPath;
    private RomIdentity? _romIdentity;
    private string? _romSummary;
    private long _frameCounter;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;

    public string? RomSummary => _romSummary;
    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _romData == null ? null : _frameCounter;
    public uint? DebugMasterProgramCounter => _core?.MasterSh2.Registers.ProgramCounter;
    public uint? DebugSlaveProgramCounter => _core?.SlaveSh2.Registers.ProgramCounter;

    public double GetTargetFps() => _regionOverride == ConsoleRegion.EU ? 50.0 : 60.0;

    public void SetRegionOverride(ConsoleRegion region)
    {
        _regionOverride = region;
        if (_core != null)
        {
            _core.SetRegionOverride(region);
        }
    }

    public void LoadRom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ROM not found.", path);

        byte[] romData = File.ReadAllBytes(path);
        if (!Sega32X.Sega32XRomDetector.IsSega32XRom(romData, path))
            throw new InvalidOperationException("ROM does not appear to be a Sega 32X cartridge.");

        lock (_stateLock)
        {
            _romData = romData;
            _core = new Sega32X.Sega32XScaffoldCore(romData);
            _core.Reset();
            // Bootstrap standalone adapter into enabled state since there's no M68K to do it
            _core.Registers.M68kWrite(0xA15100, 0x0003);
            _romPath = path;
            _romIdentity = new RomIdentity(
                Path.GetFileName(path),
                RomIdentity.ComputeSha256(romData),
                PersistentStoragePath.ResolveSavestateDirectory(path, "32x"));
            _romSummary = Sega32X.Sega32XRomDetector.BuildSummary(romData, path);
            _frameCounter = 0;
            ClearFrameBuffer();
            RenderFrame();
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            _frameCounter = 0;
            _core?.Reset();
            // Bootstrap standalone adapter into enabled state since there's no M68K to do it
            _core?.Registers.M68kWrite(0xA15100, 0x0003);
            ClearFrameBuffer();
            if (_romData != null)
                RenderFrame();
        }
    }

    public void RunFrame()
    {
        lock (_stateLock)
        {
            if (_romData == null)
                return;

            _core?.RunFrame();
            _frameCounter = _core?.FrameCounter ?? (_frameCounter + 1);
            RenderFrame();
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = FrameWidth;
        height = _core?.Bus?.Vdp?.ActiveFrameHeight ?? FrameHeight;
        stride = FrameStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = OutputChannels;
        return ReadOnlySpan<short>.Empty;
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
        _ = up;
        _ = down;
        _ = left;
        _ = right;
        _ = a;
        _ = b;
        _ = c;
        _ = start;
        _ = x;
        _ = y;
        _ = z;
        _ = mode;
        _ = padType;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        lock (_stateLock)
        {
            writer.Write("EUTH32X0");
            writer.Write(_frameCounter);
            writer.Write(_romPath ?? string.Empty);
            writer.Write(_romData?.Length ?? 0);
            if (_romData != null)
                writer.Write(_romData);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_stateLock)
        {
            string magic = reader.ReadString();
            if (!string.Equals(magic, "EUTH32X0", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid Sega 32X savestate header.");

            _frameCounter = reader.ReadInt64();
            _romPath = reader.ReadString();
            int romLength = reader.ReadInt32();
            _romData = romLength > 0 ? reader.ReadBytes(romLength) : null;
            if (_romData != null)
            {
                _core = new Sega32X.Sega32XScaffoldCore(_romData);
                _romIdentity = new RomIdentity(
                    Path.GetFileName(string.IsNullOrWhiteSpace(_romPath) ? "unknown.32x" : _romPath),
                    RomIdentity.ComputeSha256(_romData),
                    PersistentStoragePath.ResolveSavestateDirectory(_romPath, "32x"));
                _romSummary = Sega32X.Sega32XRomDetector.BuildSummary(_romData, _romPath ?? "unknown.32x");
            }
            else
            {
                _core = null;
                _romIdentity = null;
                _romSummary = null;
            }

            ClearFrameBuffer();
            if (_romData != null)
                RenderFrame();
        }
    }

    private void ClearFrameBuffer() => Array.Clear(_frameBuffer);

    private void RenderFrame()
    {
        if (_core?.Bus?.Vdp != null)
        {
            _core.Bus.Vdp.RenderBgra(_frameBuffer, FrameStride);
            if (HasVisiblePixels())
                return;
        }

        DrawSplashFrame();
    }

    private bool HasVisiblePixels()
    {
        for (int i = 0; i < _frameBuffer.Length; i += 4)
        {
            if ((_frameBuffer[i] | _frameBuffer[i + 1] | _frameBuffer[i + 2]) != 0)
                return true;
        }

        return false;
    }

    private void DrawSplashFrame()
    {
        FillRect(0, 0, FrameWidth, FrameHeight, 0xFF101018u);
        FillRect(16, 16, FrameWidth - 32, FrameHeight - 32, 0xFF1A1A28u);
        FillRect(24, 24, FrameWidth - 48, 24, 0xFFBF3B3Bu);
        FillRect(24, 56, FrameWidth - 48, 2, 0xFF4FD1C5u);

        WriteText(34, 30, "SEGA 32X");
        WriteText(34, 68, "Port scaffold active");
        WriteText(34, 84, Path.GetFileName(_romPath ?? "no-rom"));
        WriteText(34, 100, $"Frame {_frameCounter}");

        bool securityMatch = _romData != null && Sega32X.Sega32XBootRom.SecurityProgramMatches(_romData);
        WriteText(34, 132, securityMatch ? "Security program match" : "Security program mismatch");
        if (_core != null)
        {
            WriteText(34, 164, $"ROM bank {_core.Registers.M68kRomBank} VDP {_core.Registers.VdpAccess}");
            WriteText(34, 180, $"MPC {_core.MasterSh2.Registers.ProgramCounter:X8} SPC {_core.SlaveSh2.Registers.ProgramCounter:X8}");
        }

        if (_romIdentity != null)
            WriteText(34, 148, _romIdentity.HashPrefix(12));
    }

    private void FillRect(int x, int y, int width, int height, uint bgra)
    {
        int minX = Math.Max(0, x);
        int minY = Math.Max(0, y);
        int maxX = Math.Min(FrameWidth, x + width);
        int maxY = Math.Min(FrameHeight, y + height);
        if (minX >= maxX || minY >= maxY)
            return;

        for (int py = minY; py < maxY; py++)
        {
            int row = py * FrameStride;
            for (int px = minX; px < maxX; px++)
                WritePixel(row + (px * 4), bgra);
        }
    }

    private void WriteText(int x, int y, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        int cursorX = x;
        foreach (char c in text)
        {
            if (c == ' ')
            {
                cursorX += 6;
                continue;
            }

            DrawGlyph(cursorX, y, c, 0xFFE6E6E6u);
            cursorX += 6;
            if (cursorX >= FrameWidth - 6)
                break;
        }
    }

    private void DrawGlyph(int x, int y, char c, uint color)
    {
        byte[] pattern = GetGlyphPattern(c);
        for (int row = 0; row < 7; row++)
        {
            byte bits = pattern[row];
            for (int col = 0; col < 5; col++)
            {
                if ((bits & (1 << (4 - col))) == 0)
                    continue;

                int px = x + col;
                int py = y + row;
                if ((uint)px >= FrameWidth || (uint)py >= FrameHeight)
                    continue;
                WritePixel((py * FrameStride) + (px * 4), color);
            }
        }
    }

    private static byte[] GetGlyphPattern(char c)
    {
        return c switch
        {
            '0' => [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
            '1' => [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
            '2' => [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
            '3' => [0x1E, 0x01, 0x01, 0x06, 0x01, 0x01, 0x1E],
            '4' => [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
            '5' => [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
            '6' => [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
            '7' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            '8' => [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
            '9' => [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
            'A' => [0x04, 0x0A, 0x11, 0x11, 0x1F, 0x11, 0x11],
            'C' => [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
            'E' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
            'F' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
            'G' => [0x0E, 0x11, 0x10, 0x10, 0x13, 0x11, 0x0F],
            'H' => [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            'I' => [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            'M' => [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
            'O' => [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            'P' => [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
            'R' => [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
            'S' => [0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E],
            'T' => [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            'U' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            'V' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
            'X' => [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
            '-' => [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
            '.' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
            ':' => [0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00],
            _ => [0x1F, 0x11, 0x15, 0x15, 0x15, 0x11, 0x1F],
        };
    }

    private void WritePixel(int offset, uint bgra)
    {
        _frameBuffer[offset] = (byte)(bgra & 0xFF);
        _frameBuffer[offset + 1] = (byte)((bgra >> 8) & 0xFF);
        _frameBuffer[offset + 2] = (byte)((bgra >> 16) & 0xFF);
        _frameBuffer[offset + 3] = (byte)((bgra >> 24) & 0xFF);
    }
}
