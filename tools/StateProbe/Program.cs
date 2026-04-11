using System.Globalization;
using System.Reflection;
using EutherDrive.Core;
using EutherDrive.Core.Savestates;
using ePceCD;

static object? GetFieldValue(object obj, string name)
{
    return obj.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?
        .GetValue(obj);
}

static T? GetFieldTyped<T>(object obj, string name)
{
    object? value = GetFieldValue(obj, name);
    return value is T typed ? typed : default;
}

static void SetFieldValue(object obj, string name, object? value)
{
    obj.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?
        .SetValue(obj, value);
}

static int? ParseOptionalHexEnv(string name)
{
    string? raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
        return null;

    raw = raw.Trim();
    if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        raw = raw[2..];

    return int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : null;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: StateProbe <rom_path> <slot_index|-1> [frames] [snapshot_frames]");
    return 1;
}

string romPath = args[0];
if (!int.TryParse(args[1], out int slotIndex))
{
    Console.Error.WriteLine("slot_index must be an integer.");
    return 1;
}

int framesToRun = 0;
if (args.Length >= 3 && !int.TryParse(args[2], out framesToRun))
{
    Console.Error.WriteLine("frames must be an integer.");
    return 1;
}

HashSet<int> snapshotFrames = new();
if (args.Length >= 4)
{
    foreach (string token in args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame) || frame < 0)
        {
            Console.Error.WriteLine("snapshot_frames must be a comma-separated list of non-negative integers.");
            return 1;
        }

        snapshotFrames.Add(frame);
    }
}

var core = new PceCdAdapter();
core.LoadRom(romPath);
if (core.RomIdentity is RomIdentity romIdentity)
{
    string? romDirectory = Path.GetDirectoryName(romPath);
    if (!string.IsNullOrWhiteSpace(romDirectory))
    {
        SetFieldValue(core, "_romIdentity", new RomIdentity(romIdentity.Name, romIdentity.Hash, romDirectory));
    }
}
if (slotIndex >= 0)
{
    var service = new SavestateService(Path.GetDirectoryName(romPath));
    service.Load(core, slotIndex);
}

var bus = GetFieldValue(core, "_bus");
if (bus is null)
{
    Console.Error.WriteLine("Failed to access PCE bus.");
    return 1;
}

if (bus is not BUS pceBus)
{
    Console.Error.WriteLine("Unexpected PCE bus type.");
    return 1;
}

var ppu = pceBus.PPU;
int? ppuMwrOverride = ParseOptionalHexEnv("STATEPROBE_PPU_MWR");
if (ppuMwrOverride.HasValue)
{
    int mwr = ppuMwrOverride.Value & 0xFF;
    int batWidth = (mwr & 0x30) switch
    {
        0x00 => 32,
        0x10 => 64,
        _ => 128,
    };
    int batHeight = (mwr & 0x40) == 0 ? 32 : 64;
    SetFieldValue(ppu, "m_VDC_MWR", mwr);
    SetFieldValue(ppu, "m_LatchedMWR", mwr);
    SetFieldValue(ppu, "m_VDC_BAT_Width", batWidth);
    SetFieldValue(ppu, "m_VDC_BAT_Height", batHeight);
    Console.WriteLine($"PPU override mwr=0x{mwr:X2} bat={batWidth}x{batHeight}");
}
Console.WriteLine($"PPU render={ppu.PeekRenderLine()} display={ppu.PeekDisplayCounter()} frame={ppu.PeekFrameCounter()} regsel=0x{ppu.PeekSelectedVdcRegister():X2}");
Console.WriteLine($"PPU mwr=0x{GetFieldTyped<int>(ppu, "m_VDC_MWR"):X4} latched_mwr=0x{GetFieldTyped<int>(ppu, "m_LatchedMWR"):X4} bxr_latched=0x{GetFieldTyped<int>(ppu, "m_LatchedBxr"):X4} bg_counter_y=0x{GetFieldTyped<int>(ppu, "m_BgCounterY"):X4} bg_offset_y=0x{GetFieldTyped<int>(ppu, "m_BgOffsetY"):X4} latched_vds={GetFieldTyped<int>(ppu, "m_LatchedVDS")} latched_vdw={GetFieldTyped<int>(ppu, "m_LatchedVDW")} byr_offset=0x{GetFieldTyped<int>(ppu, "m_VDC_BYR_Offset"):X4}");
string probeDir = Path.Combine(Path.GetTempPath(), "kaze_slot_probe");
pceBus.DumpDebugSnapshot(probeDir, $"slot{slotIndex}");
Console.WriteLine($"PPU snapshot dir={probeDir}");

var cdrom = GetFieldValue(bus, "CDRom");
if (cdrom is null)
{
    Console.Error.WriteLine("Failed to access CD-ROM.");
    return 1;
}

var adpcm = GetFieldValue(cdrom, "_ADPCM");
if (adpcm is null)
{
    Console.Error.WriteLine("Failed to access ADPCM.");
    return 1;
}

byte[]? ram = GetFieldTyped<byte[]>(adpcm, "_ram");
uint readAddr = GetFieldTyped<uint>(adpcm, "_readAddress");
uint writeAddr = GetFieldTyped<uint>(adpcm, "_writeAddress");
uint len = GetFieldTyped<uint>(adpcm, "_adpcmLength");
byte ctl = GetFieldTyped<byte>(adpcm, "_control");
byte dma = GetFieldTyped<byte>(adpcm, "_dmaControl");
byte rate = GetFieldTyped<byte>(adpcm, "_playbackRate");
bool play = GetFieldTyped<bool>(adpcm, "_isPlaying");
bool pend = GetFieldTyped<bool>(adpcm, "_playPending");
bool end = GetFieldTyped<bool>(adpcm, "_endReached");
bool half = GetFieldTyped<bool>(adpcm, "_halfReached");

Console.WriteLine($"ADPCM ctl=0x{ctl:X2} dma=0x{dma:X2} rate=0x{rate:X2} len=0x{len:X5} play={(play ? 1 : 0)} pend={(pend ? 1 : 0)} end={(end ? 1 : 0)} half={(half ? 1 : 0)} read=0x{readAddr:X4} write=0x{writeAddr:X4}");

if (ram is null)
{
    Console.Error.WriteLine("Failed to access ADPCM RAM.");
    return 1;
}

int nonZero = 0;
int maxVal = 0;
ulong sum = 0;
for (int i = 0; i < ram.Length; i++)
{
    int v = ram[i];
    if (v != 0)
        nonZero++;
    if (v > maxVal)
        maxVal = v;
    sum += (uint)v;
}

Console.WriteLine($"ADPCM-RAM nonZero={nonZero} max=0x{maxVal:X2} sum=0x{sum:X}");

static string DumpSlice(byte[] data, int start, int count)
{
    start = Math.Clamp(start, 0, data.Length);
    int end = Math.Clamp(start + count, 0, data.Length);
    return BitConverter.ToString(data[start..end]);
}

static void DumpSegmentMap(byte[] data, int segmentSize)
{
    Console.WriteLine($"ADPCM-RAM segments (size=0x{segmentSize:X}):");
    for (int start = 0; start < data.Length; start += segmentSize)
    {
        int end = Math.Min(start + segmentSize, data.Length);
        int nonZeroCount = 0;
        int firstNonZero = -1;
        int lastNonZero = -1;
        ulong segmentSum = 0;
        for (int i = start; i < end; i++)
        {
            byte value = data[i];
            segmentSum += value;
            if (value == 0)
                continue;

            nonZeroCount++;
            if (firstNonZero < 0)
                firstNonZero = i - start;
            lastNonZero = i - start;
        }

        Console.WriteLine(
            $"  0x{start:X4}-0x{end - 1:X4}: nonZero={nonZeroCount} first={(firstNonZero >= 0 ? $"0x{firstNonZero:X4}" : "--")} last={(lastNonZero >= 0 ? $"0x{lastNonZero:X4}" : "--")} sum=0x{segmentSum:X}");
    }
}

static byte ReadCpuAddress(BUS bus, ushort address)
{
    int slot = address >> 13;
    byte mpr = bus.CPU.PeekMpr(slot);
    MemoryBank bank = bus.GetBank(mpr);
    return bank.ReadAt(address & 0x1FFF);
}

static void DumpCpuWindow(BUS bus, ushort start, int count)
{
    byte[] bytes = new byte[count];
    for (int i = 0; i < count; i++)
        bytes[i] = ReadCpuAddress(bus, (ushort)(start + i));

    Console.WriteLine($"CPU[0x{start:X4}..] {BitConverter.ToString(bytes)}");
}

Console.WriteLine($"RAM[0x0000..] {DumpSlice(ram, 0x0000, 32)}");
Console.WriteLine($"RAM[0x4000..] {DumpSlice(ram, 0x4000, 32)}");
Console.WriteLine($"RAM[0x6000..] {DumpSlice(ram, 0x6000, 32)}");
Console.WriteLine($"RAM[0x7FE0..] {DumpSlice(ram, 0x7FE0, 32)}");
Console.WriteLine($"RAM[0x8000..] {DumpSlice(ram, 0x8000, 32)}");
Console.WriteLine($"RAM[read..]   {DumpSlice(ram, (int)readAddr, 32)}");
Console.WriteLine($"RAM[write..]  {DumpSlice(ram, (int)writeAddr, 32)}");
DumpSegmentMap(ram, 0x1000);
Console.WriteLine($"CPU PC=0x{pceBus.CPU.PeekProgramCounter():X4} MPR7=0x{pceBus.CPU.PeekMpr(7):X2}");
DumpCpuWindow(pceBus, 0xF3D0, 64);
DumpCpuWindow(pceBus, 0xF3F0, 32);
DumpCpuWindow(pceBus, 0xF400, 128);
DumpCpuWindow(pceBus, 0xF440, 64);
DumpCpuWindow(pceBus, 0xF610, 64);
DumpCpuWindow(pceBus, 0xF6D0, 64);
DumpCpuWindow(pceBus, 0xF6F0, 64);
DumpCpuWindow(pceBus, 0xF720, 32);

for (int frame = 0; frame < framesToRun; frame++)
{
    core.RunFrame();
    if (snapshotFrames.Contains(frame + 1))
    {
        string snapshotPath = core.CaptureDebugSnapshot(Path.Combine(Path.GetTempPath(), "kaze_cold_probe_frames"));
        Console.WriteLine($"SNAPSHOT frame={frame + 1} path={snapshotPath}");
    }
    readAddr = GetFieldTyped<uint>(adpcm, "_readAddress");
    writeAddr = GetFieldTyped<uint>(adpcm, "_writeAddress");
    len = GetFieldTyped<uint>(adpcm, "_adpcmLength");
    ctl = GetFieldTyped<byte>(adpcm, "_control");
    dma = GetFieldTyped<byte>(adpcm, "_dmaControl");
    rate = GetFieldTyped<byte>(adpcm, "_playbackRate");
    play = GetFieldTyped<bool>(adpcm, "_isPlaying");
    pend = GetFieldTyped<bool>(adpcm, "_playPending");
    end = GetFieldTyped<bool>(adpcm, "_endReached");
    half = GetFieldTyped<bool>(adpcm, "_halfReached");
    Console.WriteLine($"FRAME {frame + 1}: ctl=0x{ctl:X2} dma=0x{dma:X2} rate=0x{rate:X2} len=0x{len:X5} play={(play ? 1 : 0)} pend={(pend ? 1 : 0)} end={(end ? 1 : 0)} half={(half ? 1 : 0)} read=0x{readAddr:X4} write=0x{writeAddr:X4}");
    Console.WriteLine($"FRAME {frame + 1} RAM[0x6000..] {DumpSlice(ram, 0x6000, 16)}");
    Console.WriteLine($"FRAME {frame + 1} RAM[read..]   {DumpSlice(ram, (int)readAddr, 16)}");
}

return 0;
