using System.Collections;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using EutherDrive.Core;
using EutherDrive.Core.Arcade.Vegas;

string romPath = args.Length > 0 ? args[0] : "/home/nichlas/roms/MAME/Midway/Vegas/gauntd";
ConfigureRawDiskSidecar(romPath);
int frames = args.Length > 1 && int.TryParse(args[1], out int parsedFrames) ? parsedFrames : 600;
int cpuStepsPerFrameConfig = args.Length > 2 && int.TryParse(args[2], out int cpuStepsPerFrame) && cpuStepsPerFrame > 0
    ? cpuStepsPerFrame
    : 0;
if (cpuStepsPerFrameConfig > 0)
    Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME", cpuStepsPerFrameConfig.ToString());
int extraStepsArg = args.Length > 3 && int.TryParse(args[3], out int parsedExtraStepsArg) ? parsedExtraStepsArg : 0;
if (args.Length > 4 && !string.IsNullOrWhiteSpace(args[4]))
    Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_CPU", "1");
if (args.Length > 4 && !string.IsNullOrWhiteSpace(args[4]))
    Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN", args[4]);
if (args.Length > 5 && !string.IsNullOrWhiteSpace(args[5]))
    Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX", args[5]);
if (args.Length > 6 && !string.IsNullOrWhiteSpace(args[6]))
    Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_CPU_LIMIT", args[6]);
if (args.Length > 7 && args[7] == "dumpcode")
    Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_CODE", "1");

var totalStopwatch = Stopwatch.StartNew();
var loadStopwatch = Stopwatch.StartNew();
var adapter = new GauntletDarkLegacyAdapter();
adapter.LoadRom(romPath);
ApplyInputFromEnvironment(adapter, frame: null);
loadStopwatch.Stop();

ulong? stopPc = ParseOptionalHexUlong(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_STOP_PC"));
int[] frameCheckpoints = ParseFrameCheckpoints(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS"));
string? warmupSnapshotPath = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_WARMUP_STATE");
int warmupFrames = ParseWarmupFrames(frames);
warmupSnapshotPath = ResolveWarmupSnapshotPath(warmupSnapshotPath, adapter, warmupFrames, cpuStepsPerFrameConfig);
bool forceSaveWarmupSnapshot = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SAVE_WARMUP") == "1";
bool allowLoadWarmupSnapshot = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_LOAD_WARMUP") != "0";
bool ignoreWarmupCpuStepMismatch = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_LOAD_WARMUP_IGNORE_CPU_STEPS") == "1";
bool loadedWarmupSnapshot = false;
var summaryContext = new ProbeSummaryContext
{
    Enabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SUMMARY") == "1",
    ModuleId = GetGauntletModuleId(),
    WarmupSnapshotPath = warmupSnapshotPath,
    WarmupState = string.IsNullOrWhiteSpace(warmupSnapshotPath) ? "none" : "cold"
};
if (!string.IsNullOrWhiteSpace(warmupSnapshotPath) &&
    allowLoadWarmupSnapshot &&
    !forceSaveWarmupSnapshot &&
    File.Exists(warmupSnapshotPath))
{
    LoadWarmupSnapshot(adapter, warmupSnapshotPath, warmupFrames, cpuStepsPerFrameConfig, ignoreWarmupCpuStepMismatch);
    loadedWarmupSnapshot = true;
    summaryContext.WarmupState = "loaded";
    Console.Error.WriteLine($"warmupSnapshotLoaded={warmupSnapshotPath}");
}

long runStartFrame = adapter.FrameCounter.GetValueOrDefault();
var runStopwatch = Stopwatch.StartNew();
if (!loadedWarmupSnapshot)
{
    if (!string.IsNullOrWhiteSpace(warmupSnapshotPath))
        summaryContext.WarmupState = "building";

    RunUntilFrame(adapter, string.IsNullOrWhiteSpace(warmupSnapshotPath) ? frames : warmupFrames, stopPc, frameCheckpoints, summaryContext);

    if (!string.IsNullOrWhiteSpace(warmupSnapshotPath))
    {
        SaveWarmupSnapshot(adapter, warmupSnapshotPath, warmupFrames, cpuStepsPerFrameConfig);
        summaryContext.WarmupState = "saved";
        Console.Error.WriteLine($"warmupSnapshotSaved={warmupSnapshotPath}");
    }
}

RunUntilFrame(adapter, frames, stopPc, frameCheckpoints, summaryContext);
runStopwatch.Stop();

int extraSteps = int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_EXTRA_CPU_STEPS"), out int parsedExtraSteps)
    ? parsedExtraSteps
    : extraStepsArg;
int[] extraSeries = ParseExtraSeries(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_EXTRA_SERIES"));
if (extraSeries.Length > 0)
{
    object probeMachine = GetField(adapter, "_machine");
    object probeCpu = GetProperty(probeMachine, "Cpu");
    object probeVoodoo = GetProperty(probeMachine, "Voodoo");
    Action step = GetStepAction(probeCpu);
    ulong? extraStopPc = ParseOptionalHexUlong(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_EXTRA_STOP_PC"));
    int currentExtra = 0;
    foreach (int targetExtra in extraSeries)
    {
        if (targetExtra < currentExtra)
            continue;

        int stepped = StepCpu(step, targetExtra - currentExtra, probeCpu, extraStopPc);
        currentExtra += stepped;
        int drained = DrainHelperPcs(probeCpu, step, 4096);
        PrintCheckpoint(currentExtra, drained, probeCpu, probeVoodoo);
        if (extraStopPc.HasValue && (ulong)GetProperty(probeCpu, "Pc") == extraStopPc.Value)
            break;
    }
}
else if (extraSteps > 0)
{
    object probeMachine = GetField(adapter, "_machine");
    object probeCpu = GetProperty(probeMachine, "Cpu");
    Action step = GetStepAction(probeCpu);
    ulong? extraStopPc = ParseOptionalHexUlong(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_EXTRA_STOP_PC"));
    StepCpu(step, extraSteps, probeCpu, extraStopPc);
    int drained = DrainHelperPcs(probeCpu, step, 4096);
    Console.WriteLine($"extraCpuSteps={extraSteps}");
    if (drained > 0)
        Console.WriteLine($"drainedHelperSteps={drained}");
}

Console.WriteLine($"rom={adapter.RomIdentity?.Name ?? "unknown"}");
Console.WriteLine($"frame={adapter.FrameCounter}");
PrintScoreboard(adapter, frames, runStartFrame, loadStopwatch.Elapsed, runStopwatch.Elapsed, totalStopwatch.Elapsed);
SaveRequestedFinalSnapshot(adapter, frames, cpuStepsPerFrameConfig);
Console.WriteLine($"debug={adapter.DebugStatus}");

object machine = GetField(adapter, "_machine");
object cpu = GetProperty(machine, "Cpu");
Console.WriteLine($"pc=0x{GetProperty(cpu, "Pc"):x16}");
Console.WriteLine($"lastOp=0x{GetProperty(cpu, "LastFetchedInstruction"):x8}");
if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS") == "1")
    Console.WriteLine(GetProperty(cpu, "HotPcStatus"));

if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_GPRS") == "1")
    DumpCpuState(cpu);

object disk = GetProperty(machine, "Disk");
Console.WriteLine($"attached={GetProperty(disk, "Attached")}");

if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_CMD_STATE") == "1")
    DumpCommandState(GetProperty(machine, "MemoryMap"));

if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_CODE") == "1")
    DumpCode(GetProperty(machine, "MemoryMap"));
DumpRequestedCodeRanges(GetProperty(machine, "MemoryMap"));
DumpRequestedByteRanges(GetProperty(machine, "MemoryMap"));
DumpRenderRecords(GetProperty(machine, "MemoryMap"));
ScanRequestedAscii(GetProperty(machine, "MemoryMap"));
ScanRequestedPointers(GetProperty(machine, "MemoryMap"));
ScanRequestedAddressLoads(GetProperty(machine, "MemoryMap"));
ScanRequestedMemoryRefs(GetProperty(machine, "MemoryMap"));
if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SCAN_FIFO_BUILDERS") == "1")
    ScanFifoCommandBuilders(GetProperty(machine, "MemoryMap"));

object voodoo = GetProperty(machine, "Voodoo");
DumpVoodoo(voodoo);
if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFERS_BEFORE_FRAME") == "1")
    DumpVoodooColorBuffers(voodoo);
DumpFrame(adapter);
Console.WriteLine($"debugAfterFrame={adapter.DebugStatus}");
if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFERS_BEFORE_FRAME") != "1")
    DumpVoodooColorBuffers(voodoo);
DumpVoodooTextureSurfaces(voodoo);
DumpKnownTexturePayloadSurfaces(GetProperty(machine, "MemoryMap"));
DumpRamSurfaceCandidates(GetProperty(machine, "MemoryMap"));

static object GetField(object instance, string name)
{
    FieldInfo? field = FindField(instance.GetType(), name);
    if (field is null)
        throw new MissingFieldException(instance.GetType().FullName, name);
    return field.GetValue(instance) ?? throw new InvalidOperationException($"{name} is null");
}

static object GetProperty(object instance, string name)
{
    PropertyInfo? property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (property is null)
        throw new MissingMemberException(instance.GetType().FullName, name);
    return property.GetValue(instance) ?? throw new InvalidOperationException($"{name} is null");
}

static FieldInfo? FindField(Type type, string name)
{
    for (Type? cursor = type; cursor is not null; cursor = cursor.BaseType)
    {
        FieldInfo? field = cursor.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
            return field;
    }

    return null;
}

static Action GetStepAction(object cpu)
{
    MethodInfo method = cpu.GetType().GetMethod("Step", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(cpu.GetType().FullName, "Step");
    return (Action)method.CreateDelegate(typeof(Action), cpu);
}

static int StepCpu(Action step, int count, object? cpu = null, ulong? stopPc = null)
{
    int stepped = 0;
    for (; stepped < count; stepped++)
    {
        if (stopPc.HasValue && cpu is not null && (ulong)GetProperty(cpu, "Pc") == stopPc.Value)
            break;
        step();
    }
    return stepped;
}

static int DrainHelperPcs(object cpu, Action step, int limit)
{
    int drained = 0;
    while (drained < limit && IsDrainableHelperPc((ulong)GetProperty(cpu, "Pc")))
    {
        step();
        drained++;
    }

    return drained;
}

static int[] ParseExtraSeries(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return [];

    return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => int.TryParse(item, out int parsed) ? parsed : -1)
        .Where(item => item >= 0)
        .Distinct()
        .Order()
        .ToArray();
}

static ulong? ParseOptionalHexUlong(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;

    string trimmed = value.Trim();
    if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        trimmed = trimmed[2..];
    return ulong.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out ulong parsed)
        ? parsed
        : null;
}

static void ApplyInputFromEnvironment(GauntletDarkLegacyAdapter adapter, long? frame)
{
    bool hasInputConfiguration =
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_UP") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_DOWN") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_LEFT") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_RIGHT") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_FIGHT") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_A") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_MAGIC") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_B") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_TURBO") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_C") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_START") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_SERVICE") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_TEST") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_VOLUME_DOWN") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_VOLUME_UP") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_COIN") ||
        HasEnvValue("EUTHERDRIVE_GAUNTDL_INPUT_MODE");

    if (!hasInputConfiguration)
        return;

    bool active = IsInputFrameActive(frame);
    bool up = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_UP");
    bool down = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_DOWN");
    bool left = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_LEFT");
    bool right = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_RIGHT");
    bool fight = active && (IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_FIGHT") || IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_A"));
    bool magic = active && (IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_MAGIC") || IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_B"));
    bool turbo = active && (IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_TURBO") || IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_C"));
    bool start = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_START");
    bool service = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_SERVICE");
    bool test = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_TEST");
    bool volumeDown = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_VOLUME_DOWN");
    bool volumeUp = active && IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_VOLUME_UP");
    bool coin = active && (IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_COIN") || IsEnvEnabled("EUTHERDRIVE_GAUNTDL_INPUT_MODE"));

    if (active || frame.HasValue)
    {
        adapter.SetInputState(up, down, left, right, fight, magic, turbo, start, service, test, volumeUp, coin, PadType.SixButton);
        adapter.SetOperatorInputState(service, test, volumeDown, volumeUp);
    }
}

static bool HasEnvValue(string name)
    => Environment.GetEnvironmentVariable(name) is { Length: > 0 };

static bool IsInputFrameActive(long? frame)
{
    if (!frame.HasValue)
        return true;

    int startFrame = ParseOptionalInt("EUTHERDRIVE_GAUNTDL_INPUT_PRESS_FRAME", 0);
    int releaseFrame = ParseOptionalInt("EUTHERDRIVE_GAUNTDL_INPUT_RELEASE_FRAME", -1);
    return frame.Value >= startFrame && (releaseFrame < 0 || frame.Value < releaseFrame);
}

static int ParseOptionalInt(string name, int fallback)
    => int.TryParse(Environment.GetEnvironmentVariable(name), out int parsed) ? parsed : fallback;

static bool IsEnvEnabled(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return value == "1" || value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
}

static void ConfigureRawDiskSidecar(string romPath)
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_RAW_DISK")))
        return;

    string? directory = Directory.Exists(romPath)
        ? Path.GetFullPath(romPath)
        : Path.GetDirectoryName(Path.GetFullPath(romPath));
    if (string.IsNullOrWhiteSpace(directory))
        return;

    string[] candidates =
    [
        Path.Combine(directory, "gauntd24.raw"),
        Path.Combine(directory, "gauntdl.raw")
    ];

    foreach (string candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_RAW_DISK", candidate);
            return;
        }
    }
}

static int ParseWarmupFrames(int targetFrames)
{
    string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES");
    if (!int.TryParse(raw, out int parsed) || parsed <= 0)
        return targetFrames;
    return Math.Min(parsed, targetFrames);
}

static void RunUntilFrame(GauntletDarkLegacyAdapter adapter, int targetFrames, ulong? stopPc, int[] frameCheckpoints, ProbeSummaryContext summaryContext)
{
    while (adapter.FrameCounter.GetValueOrDefault() < targetFrames)
    {
        long frame = adapter.FrameCounter.GetValueOrDefault();
        ApplyInputFromEnvironment(adapter, frame);
        adapter.RunFrame();
        PrintFrameCheckpointIfRequested(adapter, frameCheckpoints, summaryContext);
        if (stopPc.HasValue && TryGetCpuPc(adapter, out ulong pc) && pc == stopPc.Value)
        {
            Console.Error.WriteLine($"stopPc=0x{pc:x16} frame={adapter.FrameCounter.GetValueOrDefault()}");
            return;
        }
        if (frame > 0 && frame % ParseProgressInterval() == 0)
            Console.Error.WriteLine($"progress frame={frame}");
    }
}

static int[] ParseFrameCheckpoints(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return [];

    return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => int.TryParse(item, out int value) ? value : -1)
        .Where(value => value > 0)
        .Distinct()
        .Order()
        .ToArray();
}

static void PrintFrameCheckpointIfRequested(GauntletDarkLegacyAdapter adapter, int[] frameCheckpoints, ProbeSummaryContext summaryContext)
{
    if (frameCheckpoints.Length == 0)
        return;

    long frame = adapter.FrameCounter.GetValueOrDefault();
    if (Array.BinarySearch(frameCheckpoints, (int)frame) < 0)
        return;

    ReadOnlySpan<byte> frameBuffer = adapter.GetFrameBuffer(out int width, out int height, out int stride);
    uint frameHash = HashFrame(frameBuffer, width, height, stride);
    object machine = GetField(adapter, "_machine");
    object cpu = GetProperty(machine, "Cpu");
    object voodoo = GetProperty(machine, "Voodoo");
    object backend = GetField(voodoo, "_backend");
    var packetTypes = (int[])GetField(backend, "_fifoPacketTypeCounts");

    Console.WriteLine(
        $"checkpoint frame={frame} pc=0x{GetProperty(cpu, "Pc"):x16} frameHash=0x{frameHash:x8} " +
        $"drawPackets={GetIntField(backend, "_fifoDrawPacketCount")} " +
        $"directTriangles={GetIntField(backend, "_directTriangleCommandCount")} " +
        $"setupTriangles={GetIntField(backend, "_setupTriangleCommandCount")} " +
        $"lfbWrites={GetLongField(backend, "_lfbWriteCount")} " +
        $"texWrites={GetIntField(backend, "_textureWriteCount")} " +
        $"fastFills={GetIntField(backend, "_fastFillCount")} " +
        $"swaps={GetIntField(backend, "_swapBufferCount")} " +
        $"packetTypes={string.Join(",", packetTypes.Select((count, type) => $"{type}:{count}"))}");

    PrintFrameSummaryIfRequested(summaryContext, frame, frameHash, frameBuffer, width, height, stride, cpu, backend, packetTypes);
}

static string GetGauntletModuleId()
    => typeof(GauntletDarkLegacyAdapter).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

static void PrintFrameSummaryIfRequested(
    ProbeSummaryContext context,
    long frame,
    uint frameHash,
    ReadOnlySpan<byte> frameBuffer,
    int width,
    int height,
    int stride,
    object cpu,
    object backend,
    int[] packetTypes)
{
    if (!context.Enabled)
        return;

    (int nonBlack, int colored) = CountFramePixels(frameBuffer, width, height, stride);
    string frameSha256 = HashFrameRgbSha256(frameBuffer, width, height, stride);
    string snapshot = string.IsNullOrWhiteSpace(context.WarmupSnapshotPath) ? "none" : context.WarmupSnapshotPath;

    Console.WriteLine(
        $"summary gauntdl frame={frame} module={context.ModuleId} snapshot={snapshot} warmup={context.WarmupState} " +
        $"pc=0x{GetProperty(cpu, "Pc"):x16} frameHash=0x{frameHash:x8} frameSha256={frameSha256} " +
        $"framebuffer={width}x{height}:{nonBlack}:{colored} " +
        $"drawPackets={GetIntField(backend, "_fifoDrawPacketCount")} " +
        $"directTriangles={GetIntField(backend, "_directTriangleCommandCount")} " +
        $"setupTriangles={GetIntField(backend, "_setupTriangleCommandCount")} " +
        $"texWrites={GetIntField(backend, "_textureWriteCount")} " +
        $"textureMap={FormatTextureMapSummary(backend)} " +
        $"cmdstop={FormatCommandFifoStopSummary(backend)} " +
        $"packetTypes={string.Join(",", packetTypes.Select((count, type) => $"{type}:{count}"))}");
}

static (int NonBlack, int Colored) CountFramePixels(ReadOnlySpan<byte> frame, int width, int height, int stride)
{
    int nonBlack = 0;
    int colored = 0;
    for (int y = 0; y < height; y++)
    {
        int row = y * stride;
        for (int x = 0; x < width; x++)
        {
            byte b = frame[row + x * 4 + 0];
            byte g = frame[row + x * 4 + 1];
            byte r = frame[row + x * 4 + 2];
            if ((r | g | b) != 0)
                nonBlack++;
            if (r != g || g != b)
                colored++;
        }
    }

    return (nonBlack, colored);
}

static string HashFrameRgbSha256(ReadOnlySpan<byte> frame, int width, int height, int stride)
{
    byte[] rgb = new byte[checked(width * height * 3)];
    int destination = 0;
    for (int y = 0; y < height; y++)
    {
        int row = y * stride;
        for (int x = 0; x < width; x++)
        {
            rgb[destination++] = frame[row + x * 4 + 2];
            rgb[destination++] = frame[row + x * 4 + 1];
            rgb[destination++] = frame[row + x * 4 + 0];
        }
    }

    return Convert.ToHexString(SHA256.HashData(rgb)).ToLowerInvariant();
}

static string FormatTextureMapSummary(object backend)
    => $"{GetLongField(backend, "_textureMappedWriteCount")}:" +
       $"{GetLongField(backend, "_textureMappedNonZeroWriteCount")}:" +
       $"{GetLongField(backend, "_textureMappedZeroWriteCount")}:" +
       $"{GetIntField(backend, "_textureTouchedWordCount")}:" +
       $"0x{Math.Max(GetIntField(backend, "_textureTouchedFirstWord"), 0) * 4:x6}:" +
       $"0x{Math.Max(GetIntField(backend, "_textureTouchedLastWord"), 0) * 4:x6}";

static string FormatCommandFifoStopSummary(object backend)
{
    int count = GetIntField(backend, "_commandFifoDecodeStopCount");
    if (count == 0)
        return "none";

    string reason = (string)GetField(backend, "_lastCommandFifoDecodeStopReason");
    uint command = (uint)GetField(backend, "_lastCommandFifoDecodeStopCommand");
    int wordsNeeded = GetIntField(backend, "_lastCommandFifoDecodeStopWordsNeeded");
    int depth = GetIntField(backend, "_lastCommandFifoDecodeStopDepth");
    int readIndex = GetIntField(backend, "_lastCommandFifoDecodeStopReadIndex");
    int storageIndex = GetIntField(backend, "_lastCommandFifoDecodeStopStorageIndex");
    uint next1 = (uint)GetField(backend, "_lastCommandFifoDecodeStopNext1");
    uint next2 = (uint)GetField(backend, "_lastCommandFifoDecodeStopNext2");
    ulong pc = (ulong)GetField(backend, "_lastCommandFifoDecodeStopPc");
    uint lastCommand = (uint)GetField(backend, "_lastDecodedCommandFifoCommand");
    int lastWords = GetIntField(backend, "_lastDecodedCommandFifoWords");
    int lastPacketStart = GetIntField(backend, "_lastDecodedCommandFifoPacketStart");
    int lastReadAfter = GetIntField(backend, "_lastDecodedCommandFifoReadAfter");

    string pcStatus = pc == 0 ? "" : $"/pc=0x{pc:x16}";
    return
        $"{reason}/0x{command:x8}/{wordsNeeded}/{depth}/" +
        $"0x{readIndex * 4:x}/0x{storageIndex * 4:x}/" +
        $"0x{next1:x8}/0x{next2:x8}{pcStatus}/" +
        $"last=0x{lastCommand:x8}:{lastWords}:0x{lastPacketStart * 4:x}:0x{lastReadAfter * 4:x}/{count}";
}

static bool TryGetCpuPc(GauntletDarkLegacyAdapter adapter, out ulong pc)
{
    pc = 0;
    try
    {
        object machine = GetField(adapter, "_machine");
        object cpu = GetProperty(machine, "Cpu");
        pc = (ulong)GetProperty(cpu, "Pc");
        return true;
    }
    catch
    {
        return false;
    }
}

static int ParseProgressInterval()
{
    string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL");
    return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : 100;
}

static string? ResolveWarmupSnapshotPath(string? configuredPath, GauntletDarkLegacyAdapter adapter, int frames, int cpuStepsPerFrame)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
        return null;
    if (!string.Equals(configuredPath.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        return configuredPath;

    string romName = adapter.RomIdentity?.Name ?? "unknown";
    string moduleId = typeof(GauntletDarkLegacyAdapter).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
    string modeId = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_BRINGUP_FAST") == "1" ? "fast" : "base";
    string diskId = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_RAW_DISK")) ? "chd" : "raw";
    string fileName = $"gauntdl-{SanitizeFileName(romName)}-{modeId}-{diskId}-f{frames}-s{cpuStepsPerFrame}-{moduleId}.warm";
    return Path.Combine(Path.GetTempPath(), "eutherdrive-gauntlet-probe", fileName);
}

static string SanitizeFileName(string value)
{
    char[] invalid = Path.GetInvalidFileNameChars();
    Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
    for (int i = 0; i < value.Length; i++)
        buffer[i] = invalid.Contains(value[i]) ? '_' : value[i];
    return new string(buffer);
}

static void PrintCheckpoint(int extraSteps, int drained, object cpu, object voodoo)
{
    object backend = GetField(voodoo, "_backend");
    int regs = GetIntField(backend, "_registerWriteCount");
    int fifoWords = GetIntField(backend, "_fifoWriteCount");
    int fifoPackets = GetIntField(backend, "_fifoPacketCount");
    int drawPackets = GetIntField(backend, "_fifoDrawPacketCount");
    int directTriangles = GetIntField(backend, "_directTriangleCommandCount");
    int setupTriangles = GetIntField(backend, "_setupTriangleCommandCount");
    int fastFills = GetIntField(backend, "_fastFillCount");
    int swaps = GetIntField(backend, "_swapBufferCount");
    var packetTypes = (int[])GetField(backend, "_fifoPacketTypeCounts");

    Console.WriteLine(
        $"checkpoint extra={extraSteps} drained={drained} pc=0x{GetProperty(cpu, "Pc"):x16} " +
        $"lastOp=0x{GetProperty(cpu, "LastFetchedInstruction"):x8} regs={regs} fifoWords={fifoWords} " +
        $"fifoPackets={fifoPackets} drawPackets={drawPackets} directTriangles={directTriangles} " +
        $"setupTriangles={setupTriangles} fastFills={fastFills} swaps={swaps} " +
        $"packetTypes={string.Join(",", packetTypes.Select((count, type) => $"{type}:{count}"))}");
}

static void DumpCpuState(object cpu)
{
    var gpr = (ulong[])GetField(cpu, "_gpr");
    var cp0 = (ulong[])GetField(cpu, "_cp0");
    string[] names =
    [
        "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3",
        "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
        "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7",
        "t8", "t9", "k0", "k1", "gp", "sp", "s8", "ra"
    ];

    for (int i = 0; i < gpr.Length && i < names.Length; i++)
        Console.WriteLine($"{names[i]}=0x{gpr[i]:x16}");

    Console.WriteLine($"cp0 status=0x{cp0[12]:x16} cause=0x{cp0[13]:x16} epc=0x{cp0[14]:x16} errorepc=0x{cp0[30]:x16}");
}

static void DumpVoodoo(object facade)
{
    object backend = GetField(facade, "_backend");
    int regs = GetIntField(backend, "_registerWriteCount");
    int fifoWords = GetIntField(backend, "_fifoWriteCount");
    int fifoPackets = GetIntField(backend, "_fifoPacketCount");
    int drawPackets = GetIntField(backend, "_fifoDrawPacketCount");
    int directTriangles = GetIntField(backend, "_directTriangleCommandCount");
    int setupTriangles = GetIntField(backend, "_setupTriangleCommandCount");
    int texturedTriangles = GetIntField(backend, "_texturedTriangleCount");
    int texturedCovered = GetIntField(backend, "_texturedTriangleCoveredCount");
    int texturedRejected = GetIntField(backend, "_texturedTriangleRejectedCount");
    long texturedPixels = GetLongField(backend, "_texturedPixelCount");
    long texturedZeroPixels = GetLongField(backend, "_texturedZeroPixelCount");
    int texturedRejectNonFinite = GetIntField(backend, "_texturedRejectNonFiniteCount");
    int texturedRejectDegenerate = GetIntField(backend, "_texturedRejectDegenerateCount");
    int texturedRejectClip = GetIntField(backend, "_texturedRejectClipCount");
    int texturedRejectEmptyRaster = GetIntField(backend, "_texturedRejectEmptyRasterCount");
    long lfbWrites = GetLongField(backend, "_lfbWriteCount");
    int texWrites = GetIntField(backend, "_textureWriteCount");
    int fastFills = GetIntField(backend, "_fastFillCount");
    int swaps = GetIntField(backend, "_swapBufferCount");
    var packetTypes = (int[])GetField(backend, "_fifoPacketTypeCounts");

    Console.WriteLine(
        $"voodoo regs={regs} fifoWords={fifoWords} fifoPackets={fifoPackets} drawPackets={drawPackets} " +
        $"directTriangles={directTriangles} setupTriangles={setupTriangles} lfbWrites={lfbWrites} texWrites={texWrites} " +
        $"fastFills={fastFills} swaps={swaps}");
    Console.WriteLine("voodoo packetTypes=" + string.Join(",", packetTypes.Select((count, type) => $"{type}:{count}")));
    Console.WriteLine(
        $"voodoo textured=tri:{texturedTriangles}:covered:{texturedCovered}:rejected:{texturedRejected}:" +
        $"pixels:{texturedPixels}:zero:{texturedZeroPixels}:" +
        $"rejects:nf:{texturedRejectNonFinite}:deg:{texturedRejectDegenerate}:clip:{texturedRejectClip}:empty:{texturedRejectEmptyRaster}");
    Console.WriteLine("voodoo buffers=" + FormatVoodooBufferStats((ushort[][])GetField(backend, "_colorBuffers")));
    Console.WriteLine("voodoo texture=" + FormatTextureStats((uint[])GetField(backend, "_textureMemory")));
    Console.WriteLine(
        "voodoo textureMap=" +
        $"writes={GetField(backend, "_textureMappedWriteCount")}:" +
        $"nz={GetField(backend, "_textureMappedNonZeroWriteCount")}:" +
        $"zero={GetField(backend, "_textureMappedZeroWriteCount")}:" +
        $"touched={GetField(backend, "_textureTouchedWordCount")}:" +
        $"first=0x{Math.Max((int)GetField(backend, "_textureTouchedFirstWord"), 0) * 4:x6}:" +
        $"last=0x{Math.Max((int)GetField(backend, "_textureTouchedLastWord"), 0) * 4:x6}");
    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_EVENTS") == "1")
        Console.WriteLine("voodoo recentEvents=" + GetProperty(facade, "RecentEventStatus"));
    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_STATUS_PCS") == "1")
        Console.WriteLine("voodoo statusPcs=" + GetProperty(backend, "StatusPcProfile"));

    var registers = (uint[])GetField(backend, "_registers");
    foreach (int reg in new[] { 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x40, 0x41, 0x43, 0x44, 0x45, 0x46, 0x47, 0x49, 0x4a, 0x51, 0x52, 0x83, 0x98, 0x99, 0x9a, 0x9b, 0x9c, 0x9d, 0x9e, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xc0, 0xc1, 0xc3 })
        Console.WriteLine($"voodoo reg[{reg:x3}]=0x{registers[reg]:x8}");
}

static string FormatTextureStats(uint[] texture)
{
    int nonZero = 0;
    int first = -1;
    int last = -1;
    int low64k = 0;
    int nearFb00 = 0;
    for (int i = 0; i < texture.Length; i++)
    {
        if (texture[i] == 0)
            continue;

        nonZero++;
        if (first < 0)
            first = i;
        last = i;
        uint byteOffset = (uint)i << 2;
        if (byteOffset < 0x10000u)
            low64k++;
        if (byteOffset is >= 0x0000f000u and < 0x00011000u)
            nearFb00++;
    }

    return $"nzWords={nonZero}:first=0x{Math.Max(first, 0) * 4:x6}:last=0x{Math.Max(last, 0) * 4:x6}:low64k={low64k}:nearFb00={nearFb00}";
}

static string FormatVoodooBufferStats(ushort[][] buffers)
{
    return string.Join(" ", buffers.Select((buffer, index) =>
    {
        int nonZero = 0;
        int colored = 0;
        int white = 0;
        foreach (ushort pixel in buffer)
        {
            if (pixel == 0)
                continue;

            nonZero++;
            if (pixel == 0xffff)
                white++;
            int r = ((pixel >> 11) & 0x1f) << 3;
            int g = ((pixel >> 5) & 0x3f) << 2;
            int b = (pixel & 0x1f) << 3;
            if (r != g || r != b)
                colored++;
        }

        return $"{index}:nz={nonZero}:white={white}:colored={colored}";
    }));
}

static void PrintScoreboard(GauntletDarkLegacyAdapter adapter, int targetFrames, long runStartFrame, TimeSpan loadElapsed, TimeSpan runElapsed, TimeSpan totalElapsed)
{
    long frameCounter = adapter.FrameCounter.GetValueOrDefault();
    long framesRan = Math.Max(0, frameCounter - runStartFrame);
    double frameRate = runElapsed.TotalSeconds > 0 ? framesRan / runElapsed.TotalSeconds : 0;
    uint frameHash = HashFrame(adapter.GetFrameBuffer(out int width, out int height, out int stride), width, height, stride);
    Console.WriteLine(
        $"score targetFrames={targetFrames} frameCounter={frameCounter} ranFrames={framesRan} " +
        $"loadMs={loadElapsed.TotalMilliseconds:F1} runMs={runElapsed.TotalMilliseconds:F1} totalMs={totalElapsed.TotalMilliseconds:F1} " +
        $"fps={frameRate:F2} frameHash=0x{frameHash:x8}");
}

static uint HashFrame(ReadOnlySpan<byte> frame, int width, int height, int stride)
{
    const uint fnvOffset = 2166136261u;
    const uint fnvPrime = 16777619u;
    uint hash = fnvOffset;
    for (int y = 0; y < height; y++)
    {
        int row = y * stride;
        int bytes = width * 4;
        for (int i = 0; i < bytes; i++)
        {
            hash ^= frame[row + i];
            hash *= fnvPrime;
        }
    }

    return hash;
}

static int GetIntField(object instance, string name)
    => (int)GetField(instance, name);

static long GetLongField(object instance, string name)
    => (long)GetField(instance, name);

static void SetField(object instance, string name, object value)
{
    FieldInfo? field = FindField(instance.GetType(), name);
    if (field is null)
        throw new MissingFieldException(instance.GetType().FullName, name);
    field.SetValue(instance, value);
}

static void SetProperty(object instance, string name, object value)
{
    PropertyInfo? property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (property is not null)
    {
        property.SetValue(instance, value);
        return;
    }

    SetField(instance, $"<{name}>k__BackingField", value);
}

static T GetFieldValue<T>(object instance, string name)
    => (T)GetField(instance, name);

static void SaveWarmupSnapshot(GauntletDarkLegacyAdapter adapter, string path, int frames, int cpuStepsPerFrame)
{
    string? directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);

    string tempPath = $"{path}.tmp";
    using (var stream = File.Create(tempPath))
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write(0x314d5241574c4447UL);
        writer.Write(4);
        writer.Write(frames);
        writer.Write(cpuStepsPerFrame);
        writer.Write(adapter.FrameCounter.GetValueOrDefault());
        WriteByteArray(writer, GetFieldValue<byte[]>(adapter, "_frameBuffer"));

        object machine = GetField(adapter, "_machine");
        SaveCpu(writer, GetProperty(machine, "Cpu"));
        SaveMemoryMap(writer, GetProperty(machine, "MemoryMap"));
        SaveDisk(writer, GetProperty(machine, "Disk"));
        SaveSio(writer, GetProperty(machine, "Sio"));
        SaveVoodoo(writer, GetProperty(machine, "Voodoo"));
    }

    File.Move(tempPath, path, overwrite: true);
}

static void SaveRequestedFinalSnapshot(GauntletDarkLegacyAdapter adapter, int frames, int cpuStepsPerFrame)
{
    string? path = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE");
    if (string.IsNullOrWhiteSpace(path))
        return;

    SaveWarmupSnapshot(adapter, path, frames, cpuStepsPerFrame);
    Console.Error.WriteLine($"finalSnapshotSaved={path}");
}

static void LoadWarmupSnapshot(GauntletDarkLegacyAdapter adapter, string path, int frames, int cpuStepsPerFrame, bool ignoreCpuStepMismatch)
{
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    ulong magic = reader.ReadUInt64();
    int version = reader.ReadInt32();
    if (magic != 0x314d5241574c4447UL || version is not (1 or 2 or 3 or 4))
        throw new InvalidDataException($"Unsupported warmup snapshot: magic=0x{magic:x16} version={version}");

    int savedFrames = reader.ReadInt32();
    int savedCpuStepsPerFrame = reader.ReadInt32();
    if (savedFrames != frames || (!ignoreCpuStepMismatch && savedCpuStepsPerFrame != cpuStepsPerFrame))
        throw new InvalidDataException(
            $"Warmup snapshot mismatch: saved frames={savedFrames} cpuStepsPerFrame={savedCpuStepsPerFrame}, " +
            $"requested frames={frames} cpuStepsPerFrame={cpuStepsPerFrame}");
    if (savedCpuStepsPerFrame != cpuStepsPerFrame)
    {
        Console.Error.WriteLine(
            $"warmupSnapshotCpuStepsIgnored=saved:{savedCpuStepsPerFrame}:requested:{cpuStepsPerFrame}");
    }

    SetField(adapter, "_frameCounter", reader.ReadInt64());
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(adapter, "_frameBuffer"));

    object machine = GetField(adapter, "_machine");
    LoadCpu(reader, GetProperty(machine, "Cpu"));
    LoadMemoryMap(reader, GetProperty(machine, "MemoryMap"), version);
    LoadDisk(reader, GetProperty(machine, "Disk"));
    LoadSio(reader, GetProperty(machine, "Sio"));
    LoadVoodoo(reader, GetProperty(machine, "Voodoo"), version);

    if (stream.Position != stream.Length)
        throw new InvalidDataException($"Warmup snapshot has {stream.Length - stream.Position} trailing bytes");
}

static void SaveCpu(BinaryWriter writer, object cpu)
{
    WriteULongArray(writer, GetFieldValue<ulong[]>(cpu, "_gpr"));
    WriteULongArray(writer, GetFieldValue<ulong[]>(cpu, "_cp0"));
    WriteULongArray(writer, GetFieldValue<ulong[]>(cpu, "_fpr"));
    WriteUIntArray(writer, GetFieldValue<uint[]>(cpu, "_fcr"));
    writer.Write(GetFieldValue<bool>(cpu, "_halted"));
    writer.Write(GetFieldValue<bool>(cpu, "_hasPendingBranch"));
    writer.Write(GetFieldValue<ulong>(cpu, "_pendingBranchTarget"));
    writer.Write(GetFieldValue<bool>(cpu, "_hasImmediatePcOverride"));
    writer.Write(GetFieldValue<ulong>(cpu, "_immediatePcOverride"));
    writer.Write(GetFieldValue<ulong>(cpu, "_instructionCounter"));
    writer.Write(GetFieldValue<int>(cpu, "_traceInstructionCount"));
    writer.Write(GetFieldValue<bool>(cpu, "_timerInterruptPending"));
    writer.Write(GetFieldValue<ulong>(cpu, "_hi"));
    writer.Write(GetFieldValue<ulong>(cpu, "_lo"));
    writer.Write((ulong)GetProperty(cpu, "Pc"));
    writer.Write((uint)GetProperty(cpu, "LastFetchedInstruction"));
}

static void LoadCpu(BinaryReader reader, object cpu)
{
    ReadULongArrayInto(reader, GetFieldValue<ulong[]>(cpu, "_gpr"));
    ReadULongArrayInto(reader, GetFieldValue<ulong[]>(cpu, "_cp0"));
    ReadULongArrayInto(reader, GetFieldValue<ulong[]>(cpu, "_fpr"));
    ReadUIntArrayInto(reader, GetFieldValue<uint[]>(cpu, "_fcr"));
    SetField(cpu, "_halted", reader.ReadBoolean());
    SetField(cpu, "_hasPendingBranch", reader.ReadBoolean());
    SetField(cpu, "_pendingBranchTarget", reader.ReadUInt64());
    SetField(cpu, "_hasImmediatePcOverride", reader.ReadBoolean());
    SetField(cpu, "_immediatePcOverride", reader.ReadUInt64());
    SetField(cpu, "_instructionCounter", reader.ReadUInt64());
    SetField(cpu, "_traceInstructionCount", reader.ReadInt32());
    SetField(cpu, "_timerInterruptPending", reader.ReadBoolean());
    SetField(cpu, "_hi", reader.ReadUInt64());
    SetField(cpu, "_lo", reader.ReadUInt64());
    SetProperty(cpu, "Pc", reader.ReadUInt64());
    SetProperty(cpu, "LastFetchedInstruction", reader.ReadUInt32());
}

static void SaveMemoryMap(BinaryWriter writer, object memory)
{
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_mainRam"));
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_nileRegisters"));
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_fpgaConfigRegisters"));
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_cpuIoRegisters"));
    WriteUShortArray(writer, GetFieldValue<ushort[]>(memory, "_ioasicRegisters"));
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_ioasicPicSerialData"));
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_ioasicPicBuffer"));
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_ioasicPicNvram"));
    WriteByteArray(writer, GetFieldValue<byte[]>(memory, "_ioasicPicTimeBuffer"));
    writer.Write(GetFieldValue<bool>(memory, "_ioasicShuffleActive"));
    writer.Write(GetFieldValue<ushort>(memory, "_ioasicSoundIrqState"));
    writer.Write(GetFieldValue<ushort>(memory, "_ioasicPicLatch"));
    writer.Write(GetFieldValue<byte>(memory, "_ioasicPicState"));
    writer.Write(GetFieldValue<byte>(memory, "_ioasicPicIndex"));
    writer.Write(GetFieldValue<byte>(memory, "_ioasicPicTotal"));
    writer.Write(GetFieldValue<byte>(memory, "_ioasicPicNvramAddress"));
    writer.Write(GetFieldValue<byte>(memory, "_ioasicPicTimeIndex"));
    writer.Write(GetFieldValue<bool>(memory, "_ioasicPicTimeJustWritten"));
    writer.Write(GetFieldValue<ushort>(memory, "_nileIrqState"));
    writer.Write(GetFieldValue<byte>(memory, "_nileIrqPins"));
    writer.Write(GetFieldValue<bool>(memory, "_fpgaConfigSeenLow"));
    writer.Write(GetFieldValue<bool>(memory, "_fpgaConfigStatusHigh"));
    writer.Write(GetFieldValue<bool>(memory, "_fpgaConfigDone"));
    SaveIdePci(writer, GetField(memory, "_idePci"));
    SaveVoodooPci(writer, GetField(memory, "_voodooPci"));
}

static void LoadMemoryMap(BinaryReader reader, object memory, int version)
{
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_mainRam"));
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_nileRegisters"));
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_fpgaConfigRegisters"));
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_cpuIoRegisters"));
    ReadUShortArrayInto(reader, GetFieldValue<ushort[]>(memory, "_ioasicRegisters"));
    if (version >= 2)
    {
        ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_ioasicPicSerialData"));
        ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_ioasicPicBuffer"));
        ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_ioasicPicNvram"));
        ReadByteArrayInto(reader, GetFieldValue<byte[]>(memory, "_ioasicPicTimeBuffer"));
        SetField(memory, "_ioasicShuffleActive", reader.ReadBoolean());
        SetField(memory, "_ioasicSoundIrqState", reader.ReadUInt16());
        SetField(memory, "_ioasicPicLatch", reader.ReadUInt16());
        SetField(memory, "_ioasicPicState", reader.ReadByte());
        SetField(memory, "_ioasicPicIndex", reader.ReadByte());
        SetField(memory, "_ioasicPicTotal", reader.ReadByte());
        SetField(memory, "_ioasicPicNvramAddress", reader.ReadByte());
        SetField(memory, "_ioasicPicTimeIndex", reader.ReadByte());
        SetField(memory, "_ioasicPicTimeJustWritten", reader.ReadBoolean());
    }
    SetField(memory, "_nileIrqState", reader.ReadUInt16());
    SetField(memory, "_nileIrqPins", reader.ReadByte());
    SetField(memory, "_fpgaConfigSeenLow", reader.ReadBoolean());
    SetField(memory, "_fpgaConfigStatusHigh", reader.ReadBoolean());
    SetField(memory, "_fpgaConfigDone", reader.ReadBoolean());
    LoadIdePci(reader, GetField(memory, "_idePci"));
    LoadVoodooPci(reader, GetField(memory, "_voodooPci"));
}

static void SaveIdePci(BinaryWriter writer, object idePci)
{
    WriteUIntArray(writer, GetFieldValue<uint[]>(idePci, "_bars"));
    WriteByteArray(writer, GetFieldValue<byte[]>(idePci, "_config"));
    WriteByteArray(writer, GetFieldValue<byte[]>(idePci, "_busMaster"));
}

static void LoadIdePci(BinaryReader reader, object idePci)
{
    ReadUIntArrayInto(reader, GetFieldValue<uint[]>(idePci, "_bars"));
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(idePci, "_config"));
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(idePci, "_busMaster"));
}

static void SaveVoodooPci(BinaryWriter writer, object voodooPci)
{
    WriteByteArray(writer, GetFieldValue<byte[]>(voodooPci, "_config"));
    WriteUIntArray(writer, GetFieldValue<uint[]>(voodooPci, "_pciControl"));
    WriteUIntArray(writer, GetFieldValue<uint[]>(voodooPci, "_registers"));
    writer.Write(GetFieldValue<uint>(voodooPci, "_bar0"));
    writer.Write(GetFieldValue<bool>(voodooPci, "_bar0Probe"));
    writer.Write(GetFieldValue<uint>(voodooPci, "_statusReadCounter"));
    writer.Write(GetFieldValue<uint>(voodooPci, "_swapStatusCounter"));
    writer.Write(GetFieldValue<uint>(voodooPci, "_vRetraceCounter"));
}

static void LoadVoodooPci(BinaryReader reader, object voodooPci)
{
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(voodooPci, "_config"));
    ReadUIntArrayInto(reader, GetFieldValue<uint[]>(voodooPci, "_pciControl"));
    ReadUIntArrayInto(reader, GetFieldValue<uint[]>(voodooPci, "_registers"));
    SetField(voodooPci, "_bar0", reader.ReadUInt32());
    SetField(voodooPci, "_bar0Probe", reader.ReadBoolean());
    SetField(voodooPci, "_statusReadCounter", reader.ReadUInt32());
    SetField(voodooPci, "_swapStatusCounter", reader.ReadUInt32());
    SetField(voodooPci, "_vRetraceCounter", reader.ReadUInt32());
}

static void SaveDisk(BinaryWriter writer, object disk)
{
    WriteByteArray(writer, GetFieldValue<byte[]>(disk, "_transferBuffer"));
    writer.Write(GetFieldValue<int>(disk, "_transferOffset"));
    writer.Write(GetFieldValue<byte>(disk, "_features"));
    writer.Write(GetFieldValue<byte>(disk, "_error"));
    writer.Write(GetFieldValue<byte>(disk, "_sectorCount"));
    writer.Write(GetFieldValue<byte>(disk, "_sectorNumber"));
    writer.Write(GetFieldValue<byte>(disk, "_cylinderLow"));
    writer.Write(GetFieldValue<byte>(disk, "_cylinderHigh"));
    writer.Write(GetFieldValue<byte>(disk, "_driveHead"));
    writer.Write(GetFieldValue<byte>(disk, "_status"));
}

static void LoadDisk(BinaryReader reader, object disk)
{
    ReadByteArrayInto(reader, GetFieldValue<byte[]>(disk, "_transferBuffer"));
    SetField(disk, "_transferOffset", reader.ReadInt32());
    SetField(disk, "_features", reader.ReadByte());
    SetField(disk, "_error", reader.ReadByte());
    SetField(disk, "_sectorCount", reader.ReadByte());
    SetField(disk, "_sectorNumber", reader.ReadByte());
    SetField(disk, "_cylinderLow", reader.ReadByte());
    SetField(disk, "_cylinderHigh", reader.ReadByte());
    SetField(disk, "_driveHead", reader.ReadByte());
    SetField(disk, "_status", reader.ReadByte());
}

static void SaveSio(BinaryWriter writer, object sio)
{
    writer.Write(GetFieldValue<byte>(sio, "_resetControl"));
    writer.Write(GetFieldValue<byte>(sio, "_irqEnable"));
    writer.Write(GetFieldValue<byte>(sio, "_irqState"));
    writer.Write(GetFieldValue<byte>(sio, "_ledState"));
}

static void LoadSio(BinaryReader reader, object sio)
{
    SetField(sio, "_resetControl", reader.ReadByte());
    SetField(sio, "_irqEnable", reader.ReadByte());
    SetField(sio, "_irqState", reader.ReadByte());
    SetField(sio, "_ledState", reader.ReadByte());
}

static void SaveVoodoo(BinaryWriter writer, object facade)
{
    object backend = GetField(facade, "_backend");
    WriteUIntArray(writer, GetFieldValue<uint[]>(backend, "_registers"));
    WriteUShortArrayArray(writer, GetFieldValue<ushort[][]>(backend, "_colorBuffers"));
    WriteUIntList(writer, GetFieldValue<IList>(backend, "_fifoBuffer"));
    WriteUIntArray(writer, GetFieldValue<uint[]>(backend, "_textureMemory"));
    WriteUIntArray(writer, GetFieldValue<uint[]>(backend, "_cmdFifoRam"));
    WriteBoolArray(writer, GetFieldValue<bool[]>(backend, "_cmdFifoValid"));
    WriteSetupVertices(writer, (Array)GetField(backend, "_setupVertices"));
    WriteIntArray(writer, GetFieldValue<int[]>(backend, "_fifoPacketTypeCounts"));
    writer.Write(GetFieldValue<int>(backend, "_registerWriteCount"));
    writer.Write(GetFieldValue<int>(backend, "_fifoWriteCount"));
    writer.Write(GetFieldValue<int>(backend, "_fifoPacketCount"));
    writer.Write(GetFieldValue<int>(backend, "_fifoDrawPacketCount"));
    writer.Write(GetFieldValue<int>(backend, "_directTriangleCommandCount"));
    writer.Write(GetFieldValue<int>(backend, "_setupTriangleCommandCount"));
    writer.Write((int)Math.Clamp(GetFieldValue<long>(backend, "_lfbWriteCount"), int.MinValue, int.MaxValue));
    writer.Write(GetFieldValue<int>(backend, "_textureWriteCount"));
    writer.Write(GetFieldValue<int>(backend, "_fastFillCount"));
    writer.Write(GetFieldValue<int>(backend, "_swapBufferCount"));
    writer.Write(GetFieldValue<int>(backend, "_pendingSwapCount"));
    writer.Write(GetFieldValue<int>(backend, "_renderFrame"));
    writer.Write(GetFieldValue<int>(backend, "_setupVertexCount"));
    writer.Write(GetFieldValue<int>(backend, "_frontBufferIndex"));
    writer.Write(GetFieldValue<int>(backend, "_backBufferIndex"));
    writer.Write(GetFieldValue<int>(backend, "_cmdFifoReadIndex"));
    writer.Write(GetFieldValue<int>(backend, "_cmdFifoDepth"));
    writer.Write(GetFieldValue<int>(backend, "_cmdFifoHoles"));
    writer.Write(GetFieldValue<bool>(backend, "_cmdFifoReadPointerWritten"));
    writer.Write(GetFieldValue<bool>(backend, "_cmdFifoJumped"));
    writer.Write(GetFieldValue<int>(backend, "_cmdFifoRamBase"));
    writer.Write(GetFieldValue<int>(backend, "_cmdFifoRamEnd"));
    writer.Write(GetFieldValue<int>(backend, "_cmdFifoAddressMin"));
    writer.Write(GetFieldValue<int>(backend, "_cmdFifoAddressMax"));
    WriteUShortArray(writer, GetFieldValue<ushort[]>(backend, "_auxBuffer"));
}

static void LoadVoodoo(BinaryReader reader, object facade, int version)
{
    object backend = GetField(facade, "_backend");
    ReadUIntArrayInto(reader, GetFieldValue<uint[]>(backend, "_registers"));
    ReadUShortArrayArrayInto(reader, GetFieldValue<ushort[][]>(backend, "_colorBuffers"));
    ReadUIntList(reader, GetFieldValue<IList>(backend, "_fifoBuffer"));
    ReadUIntArrayInto(reader, GetFieldValue<uint[]>(backend, "_textureMemory"));
    ReadUIntArrayPrefixInto(reader, GetFieldValue<uint[]>(backend, "_cmdFifoRam"));
    ReadBoolArrayPrefixInto(reader, GetFieldValue<bool[]>(backend, "_cmdFifoValid"));
    ReadSetupVertices(reader, (Array)GetField(backend, "_setupVertices"), version);
    ReadIntArrayInto(reader, GetFieldValue<int[]>(backend, "_fifoPacketTypeCounts"));
    SetField(backend, "_registerWriteCount", reader.ReadInt32());
    SetField(backend, "_fifoWriteCount", reader.ReadInt32());
    SetField(backend, "_fifoPacketCount", reader.ReadInt32());
    SetField(backend, "_fifoDrawPacketCount", reader.ReadInt32());
    SetField(backend, "_directTriangleCommandCount", reader.ReadInt32());
    SetField(backend, "_setupTriangleCommandCount", reader.ReadInt32());
    SetField(backend, "_lfbWriteCount", (long)reader.ReadInt32());
    SetField(backend, "_textureWriteCount", reader.ReadInt32());
    SetField(backend, "_fastFillCount", reader.ReadInt32());
    SetField(backend, "_swapBufferCount", reader.ReadInt32());
    SetField(backend, "_pendingSwapCount", reader.ReadInt32());
    SetField(backend, "_renderFrame", reader.ReadInt32());
    SetField(backend, "_setupVertexCount", reader.ReadInt32());
    SetField(backend, "_frontBufferIndex", reader.ReadInt32());
    SetField(backend, "_backBufferIndex", reader.ReadInt32());
    SetField(backend, "_cmdFifoReadIndex", reader.ReadInt32());
    SetField(backend, "_cmdFifoDepth", reader.ReadInt32());
    SetField(backend, "_cmdFifoHoles", reader.ReadInt32());
    SetField(backend, "_cmdFifoReadPointerWritten", reader.ReadBoolean());
    SetField(backend, "_cmdFifoJumped", reader.ReadBoolean());
    if (version >= 3)
    {
        SetField(backend, "_cmdFifoRamBase", reader.ReadInt32());
        SetField(backend, "_cmdFifoRamEnd", reader.ReadInt32());
        SetField(backend, "_cmdFifoAddressMin", reader.ReadInt32());
        SetField(backend, "_cmdFifoAddressMax", reader.ReadInt32());
    }
    if (reader.BaseStream.CanSeek && reader.BaseStream.Position < reader.BaseStream.Length)
        ReadUShortArrayInto(reader, GetFieldValue<ushort[]>(backend, "_auxBuffer"));
}

static void WriteByteArray(BinaryWriter writer, byte[] values)
{
    writer.Write(values.Length);
    writer.Write(values);
}

static void ReadByteArrayInto(BinaryReader reader, byte[] values)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"Byte array length mismatch: snapshot={length} runtime={values.Length}");
    int offset = 0;
    while (offset < values.Length)
    {
        int read = reader.Read(values, offset, values.Length - offset);
        if (read <= 0)
            throw new EndOfStreamException("Unexpected end of warmup snapshot");
        offset += read;
    }
}

static void WriteUShortArray(BinaryWriter writer, ushort[] values)
{
    writer.Write(values.Length);
    foreach (ushort value in values)
        writer.Write(value);
}

static void ReadUShortArrayInto(BinaryReader reader, ushort[] values)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"UInt16 array length mismatch: snapshot={length} runtime={values.Length}");
    for (int i = 0; i < values.Length; i++)
        values[i] = reader.ReadUInt16();
}

static void WriteUShortArrayArray(BinaryWriter writer, ushort[][] values)
{
    writer.Write(values.Length);
    foreach (ushort[] value in values)
        WriteUShortArray(writer, value);
}

static void ReadUShortArrayArrayInto(BinaryReader reader, ushort[][] values)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"UInt16 array-array length mismatch: snapshot={length} runtime={values.Length}");
    for (int i = 0; i < values.Length; i++)
        ReadUShortArrayInto(reader, values[i]);
}

static void WriteUIntArray(BinaryWriter writer, uint[] values)
{
    writer.Write(values.Length);
    foreach (uint value in values)
        writer.Write(value);
}

static void ReadUIntArrayInto(BinaryReader reader, uint[] values)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"UInt32 array length mismatch: snapshot={length} runtime={values.Length}");
    for (int i = 0; i < values.Length; i++)
        values[i] = reader.ReadUInt32();
}

static void ReadUIntArrayPrefixInto(BinaryReader reader, uint[] values)
{
    int length = reader.ReadInt32();
    if (length < 0)
        throw new InvalidDataException($"UInt32 array length mismatch: snapshot={length} runtime={values.Length}");

    Array.Clear(values);
    int copyLength = Math.Min(length, values.Length);
    for (int i = 0; i < copyLength; i++)
        values[i] = reader.ReadUInt32();
    for (int i = copyLength; i < length; i++)
        _ = reader.ReadUInt32();
}

static void WriteULongArray(BinaryWriter writer, ulong[] values)
{
    writer.Write(values.Length);
    foreach (ulong value in values)
        writer.Write(value);
}

static void ReadULongArrayInto(BinaryReader reader, ulong[] values)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"UInt64 array length mismatch: snapshot={length} runtime={values.Length}");
    for (int i = 0; i < values.Length; i++)
        values[i] = reader.ReadUInt64();
}

static void WriteIntArray(BinaryWriter writer, int[] values)
{
    writer.Write(values.Length);
    foreach (int value in values)
        writer.Write(value);
}

static void ReadIntArrayInto(BinaryReader reader, int[] values)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"Int32 array length mismatch: snapshot={length} runtime={values.Length}");
    for (int i = 0; i < values.Length; i++)
        values[i] = reader.ReadInt32();
}

static void WriteBoolArray(BinaryWriter writer, bool[] values)
{
    writer.Write(values.Length);
    foreach (bool value in values)
        writer.Write(value);
}

static void ReadBoolArrayInto(BinaryReader reader, bool[] values)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"Boolean array length mismatch: snapshot={length} runtime={values.Length}");
    for (int i = 0; i < values.Length; i++)
        values[i] = reader.ReadBoolean();
}

static void ReadBoolArrayPrefixInto(BinaryReader reader, bool[] values)
{
    int length = reader.ReadInt32();
    if (length < 0)
        throw new InvalidDataException($"Boolean array length mismatch: snapshot={length} runtime={values.Length}");

    Array.Clear(values);
    int copyLength = Math.Min(length, values.Length);
    for (int i = 0; i < copyLength; i++)
        values[i] = reader.ReadBoolean();
    for (int i = copyLength; i < length; i++)
        _ = reader.ReadBoolean();
}

static void WriteUIntList(BinaryWriter writer, IList values)
{
    writer.Write(values.Count);
    foreach (object? value in values)
        writer.Write((uint)(value ?? 0u));
}

static void ReadUIntList(BinaryReader reader, IList values)
{
    values.Clear();
    int count = reader.ReadInt32();
    for (int i = 0; i < count; i++)
        values.Add(reader.ReadUInt32());
}

static void WriteSetupVertices(BinaryWriter writer, Array values)
{
    writer.Write(values.Length);
    for (int i = 0; i < values.Length; i++)
    {
        object vertex = values.GetValue(i) ?? throw new InvalidDataException("Null setup vertex");
        writer.Write(Convert.ToSingle(GetProperty(vertex, "X")));
        writer.Write(Convert.ToSingle(GetProperty(vertex, "Y")));
        writer.Write(Convert.ToUInt16(GetProperty(vertex, "Color")));
        writer.Write(Convert.ToSingle(GetProperty(vertex, "Q")));
    }
}

static void ReadSetupVertices(BinaryReader reader, Array values, int version)
{
    int length = reader.ReadInt32();
    if (length != values.Length)
        throw new InvalidDataException($"Setup vertex array length mismatch: snapshot={length} runtime={values.Length}");
    Type elementType = values.GetType().GetElementType() ?? throw new InvalidDataException("Missing setup vertex element type");
    for (int i = 0; i < values.Length; i++)
    {
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        ushort color = reader.ReadUInt16();
        float q = version >= 4 ? reader.ReadSingle() : 0.0f;
        object vertex = Activator.CreateInstance(elementType, x, y, color, 0.0f, 0.0f, q, false)
            ?? throw new InvalidDataException("Could not construct setup vertex");
        values.SetValue(vertex, i);
    }
}

static void DumpCommandState(object memory)
{
    Console.WriteLine("cmdState:");
    foreach (ulong state in new[] { 0xffffffff800b4e04UL, 0xffffffff800e4e04UL })
    {
        Console.WriteLine($" state=0x{state:x16}");
        foreach (int offset in new[]
        {
            0x000, 0x004, 0x008, 0x00c, 0x010, 0x014, 0x018, 0x01c,
            0x024, 0x028, 0x02c, 0x030, 0x034, 0x038, 0x03c,
            0x190, 0x194, 0x198, 0x19c,
            0x258, 0x354, 0x358, 0x35c,
            0x370, 0x374, 0x378, 0x37c, 0x380, 0x384
        })
        {
            uint value = ReadMem32(memory, state + (uint)offset);
            Console.WriteLine($"  +0x{offset:x3}=0x{value:x8}");
        }
    }

    foreach (ulong address in new[]
    {
        0xffffffff800b4e04UL,
        0xffffffff800b4e1cUL,
        0xffffffff800b4e28UL,
        0xffffffff800b4f94UL,
        0xffffffff800b5004UL,
        0xffffffff800b5050UL,
        0xffffffff800b5090UL,
        0xffffffff800b50d0UL,
        0xffffffff800b5110UL,
        0xffffffff800b5164UL,
        0xffffffff800b5178UL,
        0xffffffff800b517cUL,
        0xffffffff800e4e04UL,
        0xffffffff800e4e1cUL,
        0xffffffff800e4f94UL,
        0xffffffff800e5164UL,
        0xffffffff800e5178UL,
        0xffffffff800e517cUL
    })
    {
        DumpWords(memory, address, 16);
    }
}

static void DumpWords(object memory, ulong address, int words)
{
    Console.WriteLine($" mem[0x{address:x16}]:");
    for (int i = 0; i < words; i += 4)
    {
        Console.Write($"  +0x{i * 4:x3}:");
        for (int j = 0; j < 4 && i + j < words; j++)
            Console.Write($" {ReadMem32(memory, address + (uint)((i + j) * 4)):x8}");
        Console.WriteLine();
    }
}

static uint ReadMem32(object memory, ulong address)
{
    MethodInfo? method = memory.GetType().GetMethod("Read32", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (method is null)
        throw new MissingMethodException(memory.GetType().FullName, "Read32");
    return (uint)(method.Invoke(memory, [address]) ?? 0u);
}

static byte ReadMem8(object memory, ulong address)
{
    uint aligned = ReadMem32(memory, address & ~3UL);
    return (byte)(aligned >> (int)((address & 3UL) * 8));
}

static ushort ReadMem16(object memory, ulong address)
    => (ushort)(ReadMem8(memory, address) | (ReadMem8(memory, address + 1UL) << 8));

static void DumpBytes(object memory, ulong address, int bytes)
{
    Console.WriteLine($" bytes[0x{address:x16}]:");
    for (int offset = 0; offset < bytes; offset += 16)
    {
        int count = Math.Min(16, bytes - offset);
        Span<byte> line = stackalloc byte[16];
        for (int i = 0; i < count; i++)
            line[i] = ReadMem8(memory, address + (uint)(offset + i));

        Console.Write($"  +0x{offset:x3}:");
        for (int i = 0; i < count; i++)
            Console.Write($" {line[i]:x2}");
        for (int i = count; i < 16; i++)
            Console.Write("   ");

        Console.Write("  ");
        for (int i = 0; i < count; i++)
        {
            byte ch = line[i];
            Console.Write(ch is >= 0x20 and <= 0x7e ? (char)ch : '.');
        }
        Console.WriteLine();
    }
}

static void DumpRenderRecords(object memory)
{
    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_RENDER_RECORDS") != "1")
        return;

    const ulong listBase = 0xffffffff80210270UL;
    const ulong listCount = 0xffffffff80213600UL;
    const ulong allocCountAddress = 0xffffffff80228088UL;
    const ulong allocBase = 0xffffffff80255f20UL;
    const ulong recordStride = 0x2cUL;
    const ulong allocStride = 0x50UL;

    uint count = ReadMem32(memory, listCount);
    uint countLimit = Math.Min(count, 0x12aU);
    int flag40 = 0;
    int nullBody = 0;
    int nonZeroToken = 0;
    int outOfRangeBody = 0;
    Dictionary<uint, int> slotCounts = [];
    List<string> firstRecords = [];
    List<string> nonSlotZeroRecords = [];

    for (uint index = 0; index < countLimit; index++)
    {
        ulong record = listBase + index * recordStride;
        uint s1 = ReadMem32(memory, record + 0x00UL);
        uint s7 = ReadMem32(memory, record + 0x04UL);
        uint s2 = ReadMem32(memory, record + 0x0cUL);
        uint slot = ReadMem32(memory, record + 0x20UL);
        ushort flags = ReadMem16(memory, record + 0x2aUL);
        if ((flags & 0x40U) != 0)
            flag40++;

        uint token = 0xffffffffU;
        if (s2 is >= 0x80000000U and < 0x80800000U)
        {
            token = ReadMem8(memory, 0xffffffff00000000UL | s2);
            if (token == 0)
                nullBody++;
            else
                nonZeroToken++;
        }
        else
        {
            outOfRangeBody++;
        }

        slotCounts[slot] = slotCounts.TryGetValue(slot, out int current) ? current + 1 : 1;
        string summary =
            $"{index}:{record:x16}/s1={s1:x8}/s2={s2:x8}/tok={token:x2}/slot={slot:x}/flags={flags:x4}/s7={s7:x8}";
        if (firstRecords.Count < 16)
        {
            firstRecords.Add(summary);
        }

        if (slot != 0 && nonSlotZeroRecords.Count < 48)
        {
            nonSlotZeroRecords.Add(summary);
        }
    }

    uint allocCount = ReadMem32(memory, allocCountAddress);
    uint allocLimit = Math.Min(allocCount, 0x17fU);
    int allocActive2 = 0;
    int allocFree = 0;
    Dictionary<uint, int> allocBodyCounts = [];
    for (uint index = 0; index < allocLimit; index++)
    {
        ulong record = allocBase + index * allocStride;
        byte status = ReadMem8(memory, record + 0x04UL);
        if (status == 2)
            allocActive2++;
        if (status == 0)
            allocFree++;

        uint body = ReadMem32(memory, record + 0x4cUL);
        allocBodyCounts[body] = allocBodyCounts.TryGetValue(body, out int current) ? current + 1 : 1;
    }

    Console.WriteLine(
        "renderRecords " +
        $"count={count} scanned={countLimit} flag40={flag40} nullBody={nullBody} " +
        $"nonZeroToken={nonZeroToken} outOfRangeBody={outOfRangeBody} " +
        $"allocCount={allocCount} allocActive2={allocActive2} allocFree={allocFree}");
    Console.WriteLine("renderRecords slots=" + string.Join(",", slotCounts.OrderBy(item => item.Key).Select(item => $"{item.Key:x}:{item.Value}")));
    Console.WriteLine("renderRecords first=" + string.Join(";", firstRecords));
    Console.WriteLine("renderRecords nonSlot0=" + string.Join(";", nonSlotZeroRecords));
    Console.WriteLine("renderRecords allocBodies=" + string.Join(",", allocBodyCounts.OrderByDescending(item => item.Value).Take(12).Select(item => $"{item.Key:x8}:{item.Value}")));
}

static bool IsDrainableHelperPc(ulong pc)
{
    ulong offset = pc & 0x1fffffffUL;
    return offset is >= 0x0005d230UL and <= 0x0005d354UL or
        >= 0x0005dbecUL and <= 0x0005dd20UL or
        >= 0x0005df40UL and <= 0x0005e220UL or
        >= 0x0005ec0cUL and <= 0x0005ed80UL or
        >= 0x0005ed40UL and <= 0x0005ed80UL or
        >= 0x0005f9d0UL and <= 0x0005fab0UL or
        >= 0x0005fab4UL and <= 0x0005fbc8UL;
}

static void DumpCode(object memory)
{
    foreach (ulong address in new[]
    {
        0xffffffff80019200UL,
        0xffffffff800151c0UL,
        0xffffffff80015240UL,
        0xffffffff800152c0UL,
        0xffffffff80018e80UL,
        0xffffffff80018f00UL,
        0xffffffff80018f80UL,
        0xffffffff80019280UL,
        0xffffffff80019300UL,
        0xffffffff80019580UL,
        0xffffffff8001fb40UL,
        0xffffffff8001fbc0UL,
        0xffffffff8001fc40UL,
        0xffffffff8003ce80UL,
        0xffffffff8003cf00UL,
        0xffffffff8004cd80UL,
        0xffffffff8004cbc0UL,
        0xffffffff8004cc40UL,
        0xffffffff8004ccc0UL,
        0xffffffff8004ce00UL,
        0xffffffff8004ce80UL,
        0xffffffff8004cf00UL,
        0xffffffff80052680UL,
        0xffffffff80052700UL,
        0xffffffff80052780UL,
        0xffffffff80052b60UL,
        0xffffffff80052bc0UL,
        0xffffffff80052c1cUL,
        0xffffffff80052c9cUL,
        0xffffffff80052d00UL,
        0xffffffff80052d80UL,
        0xffffffff80052e00UL,
        0xffffffff80052e80UL,
        0xffffffff80052ec0UL,
        0xffffffff800532c0UL,
        0xffffffff80053340UL,
        0xffffffff800533c0UL,
        0xffffffff8005d200UL,
        0xffffffff8005d280UL,
        0xffffffff8005d300UL,
        0xffffffff8005d380UL,
        0xffffffff8005dbc0UL,
        0xffffffff8005dc40UL,
        0xffffffff8005df40UL,
        0xffffffff8005dfc0UL,
        0xffffffff8005e140UL,
        0xffffffff8005e1c0UL,
        0xffffffff8005e240UL,
        0xffffffff8005e2c0UL,
        0xffffffff8005e340UL,
        0xffffffff8005e3c0UL,
        0xffffffff8005ebc0UL,
        0xffffffff8005ec00UL,
        0xffffffff8005ec40UL,
        0xffffffff8005ecc0UL,
        0xffffffff8005ed40UL,
        0xffffffff8005edc0UL,
        0xffffffff8005ee40UL,
        0xffffffff8005f9c0UL,
        0xffffffff8005fa40UL,
        0xffffffff8005fac0UL,
        0xffffffff8005fb40UL,
        0xffffffff8005fbc0UL
    })
    {
        DumpWords(memory, address, 32);
    }
}

static void DumpRequestedCodeRanges(object memory)
{
    string? ranges = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_CODE_RANGES");
    if (string.IsNullOrWhiteSpace(ranges))
        return;

    foreach (string item in ranges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] parts = item.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !TryParseHexUlong(parts[0], out ulong address))
            continue;

        int words = 32;
        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedWords) && parsedWords > 0)
            words = parsedWords;
        DumpWords(memory, address, words);
    }
}

static void DumpRequestedByteRanges(object memory)
{
    string? ranges = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_BYTES_RANGES");
    if (string.IsNullOrWhiteSpace(ranges))
        return;

    foreach (string item in ranges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] parts = item.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !TryParseHexUlong(parts[0], out ulong address))
            continue;

        int bytes = 128;
        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedBytes) && parsedBytes > 0)
            bytes = parsedBytes;
        DumpBytes(memory, address, bytes);
    }
}

static void ScanRequestedAscii(object memory)
{
    string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SCAN_ASCII");
    if (string.IsNullOrWhiteSpace(raw))
        return;

    byte[] mainRam = GetFieldValue<byte[]>(memory, "_mainRam");
    byte[] needle = System.Text.Encoding.ASCII.GetBytes(raw);
    if (needle.Length == 0 || needle.Length > mainRam.Length)
        return;

    Console.WriteLine($"asciiScan needle=\"{raw}\"");
    int matches = 0;
    for (int offset = 0; offset <= mainRam.Length - needle.Length; offset++)
    {
        if (!mainRam.AsSpan(offset, needle.Length).SequenceEqual(needle))
            continue;

        ulong address = 0xffffffff80000000UL + (uint)offset;
        Console.WriteLine($" ascii 0x{address:x16}");
        matches++;
        if (matches >= 64)
        {
            Console.WriteLine(" asciiScan truncated=64");
            break;
        }
    }

    Console.WriteLine($"asciiScan matches={matches}");
}

static void ScanRequestedPointers(object memory)
{
    string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SCAN_POINTERS");
    if (string.IsNullOrWhiteSpace(raw))
        return;

    uint[] needles = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => TryParseHexUlong(item, out ulong parsed) ? (uint)parsed : 0u)
        .Where(item => item != 0)
        .Distinct()
        .ToArray();
    if (needles.Length == 0)
        return;

    byte[] mainRam = GetFieldValue<byte[]>(memory, "_mainRam");
    var lookup = needles.ToHashSet();
    Console.WriteLine("pointerScan needles=" + string.Join(",", needles.Select(item => $"0x{item:x8}")));

    int matches = 0;
    for (int offset = 0; offset + 3 < mainRam.Length; offset += 4)
    {
        uint value = BitConverter.ToUInt32(mainRam, offset);
        if (!lookup.Contains(value))
            continue;

        Console.WriteLine($" pointer 0xffffffff{0x80000000u + (uint)offset:x8} -> 0x{value:x8}");
        matches++;
        if (matches >= 256)
        {
            Console.WriteLine(" pointerScan truncated=256");
            break;
        }
    }

    Console.WriteLine($"pointerScan matches={matches}");
}

static void ScanRequestedAddressLoads(object memory)
{
    string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SCAN_ADDR_LOADS");
    if (string.IsNullOrWhiteSpace(raw))
        return;

    uint[] needles = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => TryParseHexUlong(item, out ulong parsed) ? (uint)parsed : 0u)
        .Where(item => item != 0)
        .Distinct()
        .ToArray();
    if (needles.Length == 0)
        return;

    byte[] mainRam = GetFieldValue<byte[]>(memory, "_mainRam");
    Console.WriteLine("addrLoadScan needles=" + string.Join(",", needles.Select(item => $"0x{item:x8}")));

    int matches = 0;
    for (int offset = 0; offset + 3 < mainRam.Length; offset += 4)
    {
        uint op = BinaryPrimitives.ReadUInt32LittleEndian(mainRam.AsSpan(offset, 4));
        if ((op >> 26) != 0x0f)
            continue;

        int rt = (int)((op >> 16) & 31u);
        ushort upper = (ushort)op;
        for (int lookAhead = 1; lookAhead <= 12 && offset + lookAhead * 4 + 3 < mainRam.Length; lookAhead++)
        {
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(mainRam.AsSpan(offset + lookAhead * 4, 4));
            uint opcode = next >> 26;
            int rs = (int)((next >> 21) & 31u);
            int nextRt = (int)((next >> 16) & 31u);
            if (rs != rt || nextRt != rt || (opcode != 0x09 && opcode != 0x0d))
                continue;

            ushort lower = (ushort)next;
            uint candidate = opcode == 0x09
                ? (uint)(((int)upper << 16) + (short)lower)
                : ((uint)upper << 16) | lower;
            if (!needles.Contains(candidate))
                continue;

            ulong address = 0xffffffff80000000UL + (uint)offset;
            string kind = opcode == 0x09 ? "addiu" : "ori";
            Console.WriteLine($" addrload 0x{address:x16} +{lookAhead * 4:x2} r{rt} {kind} -> 0x{candidate:x8}");
            matches++;
            if (matches >= 256)
            {
                Console.WriteLine("addrLoadScan truncated=256");
                Console.WriteLine($"addrLoadScan matches={matches}");
                return;
            }
        }
    }

    Console.WriteLine($"addrLoadScan matches={matches}");
}

static void ScanRequestedMemoryRefs(object memory)
{
    string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SCAN_MEM_REFS");
    if (string.IsNullOrWhiteSpace(raw))
        return;

    uint[] needles = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => TryParseHexUlong(item, out ulong parsed) ? (uint)parsed : 0u)
        .Where(item => item != 0)
        .Distinct()
        .ToArray();
    if (needles.Length == 0)
        return;

    byte[] mainRam = GetFieldValue<byte[]>(memory, "_mainRam");
    Console.WriteLine("memRefScan needles=" + string.Join(",", needles.Select(item => $"0x{item:x8}")));

    int matches = 0;
    for (int offset = 0; offset + 3 < mainRam.Length; offset += 4)
    {
        uint op = BinaryPrimitives.ReadUInt32LittleEndian(mainRam.AsSpan(offset, 4));
        if ((op >> 26) != 0x0f)
            continue;

        int baseRegister = (int)((op >> 16) & 31u);
        ushort upper = (ushort)op;
        for (int lookAhead = 1; lookAhead <= 24 && offset + lookAhead * 4 + 3 < mainRam.Length; lookAhead++)
        {
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(mainRam.AsSpan(offset + lookAhead * 4, 4));
            uint opcode = next >> 26;
            int rs = (int)((next >> 21) & 31u);
            if (rs != baseRegister || !IsMemoryReferenceOpcode(opcode))
                continue;

            ushort lower = (ushort)next;
            uint candidate = (uint)(((int)upper << 16) + (short)lower);
            if (!needles.Contains(candidate))
                continue;

            ulong address = 0xffffffff80000000UL + (uint)offset;
            Console.WriteLine($" memref 0x{address:x16} +{lookAhead * 4:x2} r{baseRegister} {MemoryReferenceMnemonic(opcode)} -> 0x{candidate:x8}");
            matches++;
            if (matches >= 512)
            {
                Console.WriteLine("memRefScan truncated=512");
                Console.WriteLine($"memRefScan matches={matches}");
                return;
            }
        }
    }

    Console.WriteLine($"memRefScan matches={matches}");
}

static bool IsMemoryReferenceOpcode(uint opcode)
    => opcode is 0x20 or 0x21 or 0x23 or 0x24 or 0x25 or 0x27 or 0x28 or 0x29 or 0x2b or 0x2c or 0x2d or 0x2f or 0x37 or 0x3f;

static string MemoryReferenceMnemonic(uint opcode)
    => opcode switch
    {
        0x20 => "lb",
        0x21 => "lh",
        0x23 => "lw",
        0x24 => "lbu",
        0x25 => "lhu",
        0x27 => "lwu",
        0x28 => "sb",
        0x29 => "sh",
        0x2b => "sw",
        0x2c => "sdl",
        0x2d => "sdr",
        0x2f => "cache",
        0x37 => "ld",
        0x3f => "sd",
        _ => $"op{opcode:x2}"
    };

static bool TryParseHexUlong(string value, out ulong parsed)
{
    value = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    return ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out parsed);
}

static void ScanFifoCommandBuilders(object memory)
{
    string? ranges = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SCAN_CODE_RANGES");
    (ulong Start, int Words)[] scanRanges = string.IsNullOrWhiteSpace(ranges)
        ? [(0xffffffff80010000UL, 0x70000 / 4)]
        : ranges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseScanRange)
            .Where(item => item.Words > 0)
            .ToArray();

    foreach ((ulong start, int words) in scanRanges)
    {
        Console.WriteLine($"fifoBuilderScan start=0x{start:x16} words={words}");
        for (int i = 0; i < words; i++)
        {
            ulong address = start + (uint)(i * 4);
            uint op = ReadMem32(memory, address);
            if ((op >> 26) != 0x0fu)
                continue;

            int rt = (int)((op >> 16) & 31u);
            uint upper = op << 16;
            for (int lookAhead = 1; lookAhead <= 8 && i + lookAhead < words; lookAhead++)
            {
                uint next = ReadMem32(memory, address + (uint)(lookAhead * 4));
                if ((next >> 26) != 0x0du)
                    continue;
                int rs = (int)((next >> 21) & 31u);
                int nextRt = (int)((next >> 16) & 31u);
                if (rs != rt || nextRt != rt)
                    continue;

                uint command = upper | (next & 0xffffu);
                if (TryDescribeInterestingFifoCommand(command, out string description))
                    Console.WriteLine($"  pc=0x{address:x16} lookAhead={lookAhead} r{rt} cmd=0x{command:x8} {description}");
            }
        }
    }
}

static (ulong Start, int Words) ParseScanRange(string item)
{
    string[] parts = item.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0 || !TryParseHexUlong(parts[0], out ulong start))
        return (0, 0);

    int words = 0x70000 / 4;
    if (parts.Length > 1 && int.TryParse(parts[1], out int parsedWords) && parsedWords > 0)
        words = parsedWords;
    return (start, words);
}

static bool TryDescribeInterestingFifoCommand(uint command, out string description)
{
    uint type = command & 7u;
    description = "";
    switch (type)
    {
        case 1:
        {
            int count = (int)(command >> 16);
            uint target = (command >> 3) & 0xfffu;
            if (count <= 0 || count > 128)
                return false;
            description = $"type1 count={count} inc={(command >> 15) & 1u} target=0x{target:x3}";
            return IsInterestingRegisterRange(target, count);
        }
        case 3:
            description = $"type3 draw count={(command >> 6) & 0xfu} code={(command >> 3) & 7u}";
            return true;
        case 4:
        {
            uint target = (command >> 3) & 0xfffu;
            uint mask = (command >> 15) & 0x3fffu;
            uint extra = command >> 29;
            List<uint> registers = [];
            for (int bit = 0; bit < 14; bit++)
            {
                if (((mask >> bit) & 1u) != 0)
                    registers.Add(target + (uint)bit);
            }
            if (registers.Count == 0)
                return false;
            description = $"type4 target=0x{target:x3} mask=0x{mask:x4} extra={extra} regs={string.Join(",", registers.Select(reg => $"0x{reg:x3}"))}";
            return registers.Any(IsInterestingRegister);
        }
        case 5:
            description = $"type5 count={(command >> 3) & 0x7ffffu} space={command >> 30}";
            return true;
        default:
            return false;
    }
}

static bool IsInterestingRegisterRange(uint start, int count)
{
    for (int i = 0; i < count; i++)
    {
        if (IsInterestingRegister(start + (uint)i))
            return true;
    }

    return false;
}

static bool IsInterestingRegister(uint register)
    => register is 0x20u or 0x40u or 0x49u or 0x4au or 0xa8u or 0xa9u or
        (>= 0x00u and <= 0x05u) or
        (>= 0x22u and <= 0x2au) or
        (>= 0x98u and <= 0x9eu);

static void DumpFrame(GauntletDarkLegacyAdapter adapter)
{
    ReadOnlySpan<byte> frame = adapter.GetFrameBuffer(out int width, out int height, out int stride);
    int nonBlack = 0;
    int colored = 0;
    for (int y = 0; y < height; y++)
    {
        int row = y * stride;
        for (int x = 0; x < width; x++)
        {
            byte b = frame[row + x * 4 + 0];
            byte g = frame[row + x * 4 + 1];
            byte r = frame[row + x * 4 + 2];
            if ((r | g | b) != 0)
                nonBlack++;
            if (r != g || g != b)
                colored++;
        }
    }

    Console.WriteLine($"framebuffer={width}x{height} stride={stride} nonBlack={nonBlack} colored={colored}");

    string? ppmPath = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_FRAME");
    if (string.IsNullOrWhiteSpace(ppmPath))
        return;

    using var stream = File.Create(ppmPath);
    using var writer = new StreamWriter(stream, leaveOpen: true);
    writer.Write($"P6\n{width} {height}\n255\n");
    writer.Flush();
    for (int y = 0; y < height; y++)
    {
        int row = y * stride;
        for (int x = 0; x < width; x++)
        {
            stream.WriteByte(frame[row + x * 4 + 2]);
            stream.WriteByte(frame[row + x * 4 + 1]);
            stream.WriteByte(frame[row + x * 4 + 0]);
        }
    }

    Console.WriteLine($"frameDump={ppmPath}");
}

static void DumpVoodooColorBuffers(object voodoo)
{
    string? prefix = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFERS_PREFIX");
    if (string.IsNullOrWhiteSpace(prefix))
        return;

    object backend = GetField(voodoo, "_backend");
    ushort[][] colorBuffers = GetFieldValue<ushort[][]>(backend, "_colorBuffers");
    int width = 640;
    int height = 480;
    int stridePixels = 1024;
    for (int index = 0; index < colorBuffers.Length; index++)
    {
        ushort[] buffer = colorBuffers[index];
        if (buffer.Length < stridePixels * height)
            continue;

        string path = $"{prefix}_buf{index}.ppm";
        DumpRgb565Buffer(buffer, width, height, stridePixels, path);
        RamSurfaceScore score = ScoreRgb565Buffer(buffer, width, height, stridePixels);
        Console.WriteLine($"voodooBufferDump={path} nz={score.NonZero} colored={score.Colored} unique={score.UniqueColors}");
        DumpBestVoodooBufferWindow(buffer, index, width, height, stridePixels, prefix);
    }
}

static void DumpBestVoodooBufferWindow(ushort[] buffer, int index, int width, int height, int stridePixels, string prefix)
{
    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFER_SCAN") != "1")
        return;

    int maxY = Math.Max(0, (buffer.Length / stridePixels) - height);
    int stepY = ParsePositiveInt(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFER_SCAN_STEP"), 16);
    int bestY = 0;
    RamSurfaceScore bestScore = default;
    for (int y = 0; y <= maxY; y += stepY)
    {
        RamSurfaceScore score = ScoreRgb565BufferWindow(buffer, width, height, stridePixels, y);
        if (score.Score > bestScore.Score)
        {
            bestScore = score;
            bestY = y;
        }
    }

    Console.WriteLine($"voodooBufferBestWindow=buf{index}:y={bestY}:nz={bestScore.NonZero}:colored={bestScore.Colored}:unique={bestScore.UniqueColors}:score={bestScore.Score}");
    string path = $"{prefix}_buf{index}_best_y{bestY}.ppm";
    DumpRgb565BufferWindow(buffer, width, height, stridePixels, bestY, path);
    Console.WriteLine($"voodooBufferBestDump={path}");
}

static void DumpRamSurfaceCandidates(object memory)
{
    string? prefix = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_RAM_SURFACE_PREFIX");
    if (string.IsNullOrWhiteSpace(prefix))
        return;

    EnsureOutputDirectory(prefix);
    byte[] mainRam = GetFieldValue<byte[]>(memory, "_mainRam");
    int maxCandidates = ParsePositiveInt(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_RAM_SURFACE_COUNT"), 6);
    var formats = new[]
    {
        new RamSurfaceFormat(512, 256, 1024),
        new RamSurfaceFormat(640, 480, 1280),
        new RamSurfaceFormat(320, 240, 640)
    };

    List<RamSurfaceCandidate> candidates = [];
    foreach (RamSurfaceFormat format in formats)
    {
        int bytes = format.Stride * format.Height;
        if (bytes <= 0 || bytes > mainRam.Length)
            continue;

        for (int offset = 0; offset + bytes <= mainRam.Length; offset += 0x1000)
        {
            RamSurfaceScore score = ScoreRgb565Surface(mainRam, offset, format);
            if (score.NonZero < 512 || score.UniqueColors < 8)
                continue;

            candidates.Add(new RamSurfaceCandidate(offset, format, score));
        }
    }

    candidates = candidates
        .OrderByDescending(candidate => candidate.Score.Score)
        .ThenBy(candidate => candidate.Offset)
        .Take(maxCandidates)
        .ToList();

    Console.WriteLine("ramSurfaceCandidates=" + (candidates.Count == 0
        ? "none"
        : string.Join(",", candidates.Select(candidate =>
            $"0x{0x80000000u + (uint)candidate.Offset:x8}:{candidate.Format.Width}x{candidate.Format.Height}/s{candidate.Format.Stride}/nz{candidate.Score.NonZero}/u{candidate.Score.UniqueColors}"))));

    for (int i = 0; i < candidates.Count; i++)
    {
        RamSurfaceCandidate candidate = candidates[i];
        string path = $"{prefix}_{i}_{0x80000000u + (uint)candidate.Offset:x8}_{candidate.Format.Width}x{candidate.Format.Height}.ppm";
        DumpRgb565Surface(mainRam, candidate.Offset, candidate.Format, path);
        Console.WriteLine($"ramSurfaceDump={path}");
    }

    DumpRequestedRamSurfaces(mainRam, prefix);
}

static void DumpVoodooTextureSurfaces(object voodoo)
{
    string? prefix = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_TEXTURE_PREFIX");
    if (string.IsNullOrWhiteSpace(prefix))
        return;

    EnsureOutputDirectory(prefix);
    object backend = GetField(voodoo, "_backend");
    uint[] textureWords = GetFieldValue<uint[]>(backend, "_textureMemory");
    byte[] textureBytes = new byte[textureWords.Length * 4];
    for (int i = 0; i < textureWords.Length; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(textureBytes.AsSpan(i * 4, 4), textureWords[i]);

    int maxCandidates = ParsePositiveInt(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_TEXTURE_COUNT"), 10);
    var formats = new[]
    {
        new ByteSurfaceFormat(128, 128, 128),
        new ByteSurfaceFormat(256, 128, 256),
        new ByteSurfaceFormat(256, 256, 256),
        new ByteSurfaceFormat(512, 256, 512)
    };

    List<ByteSurfaceCandidate> candidates = [];
    foreach (ByteSurfaceFormat format in formats)
    {
        int bytes = format.Stride * format.Height;
        if (bytes <= 0 || bytes > textureBytes.Length)
            continue;

        for (int offset = 0; offset + bytes <= textureBytes.Length; offset += 0x1000)
        {
            ByteSurfaceScore score = ScoreByteSurface(textureBytes, offset, format);
            if (score.NonZero < 256 || score.UniqueBytes < 12)
                continue;

            candidates.Add(new ByteSurfaceCandidate(offset, format, score));
        }
    }

    candidates = candidates
        .OrderByDescending(candidate => candidate.Score.Score)
        .ThenBy(candidate => candidate.Offset)
        .Take(maxCandidates)
        .ToList();

    Console.WriteLine("voodooTextureCandidates=" + (candidates.Count == 0
        ? "none"
        : string.Join(",", candidates.Select(candidate =>
            $"0x{candidate.Offset:x6}:{candidate.Format.Width}x{candidate.Format.Height}/s{candidate.Format.Stride}/nz{candidate.Score.NonZero}/u{candidate.Score.UniqueBytes}/t{candidate.Score.Transitions}"))));

    for (int i = 0; i < candidates.Count; i++)
    {
        ByteSurfaceCandidate candidate = candidates[i];
        string rgbPath = $"{prefix}_{i}_0x{candidate.Offset:x6}_{candidate.Format.Width}x{candidate.Format.Height}_rgb332.ppm";
        string grayPath = $"{prefix}_{i}_0x{candidate.Offset:x6}_{candidate.Format.Width}x{candidate.Format.Height}_gray.ppm";
        DumpByteSurface(textureBytes, candidate.Offset, candidate.Format, rgbPath, rgb332: true);
        DumpByteSurface(textureBytes, candidate.Offset, candidate.Format, grayPath, rgb332: false);
        Console.WriteLine($"voodooTextureDump={rgbPath}");
        Console.WriteLine($"voodooTextureDump={grayPath}");
    }
}

static void DumpKnownTexturePayloadSurfaces(object memory)
{
    string? prefix = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_KNOWN_TEXTURE_PAYLOAD_PREFIX");
    if (string.IsNullOrWhiteSpace(prefix))
        return;

    EnsureOutputDirectory(prefix);
    int maxPayloads = ParsePositiveInt(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_KNOWN_TEXTURE_PAYLOAD_COUNT"), 12);
    int maxSurfacesPerPayload = ParsePositiveInt(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_KNOWN_TEXTURE_PAYLOAD_SURFACES"), 2);
    ulong[] filterIndexes = ParseHexList(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_KNOWN_TEXTURE_PAYLOAD_INDEXES"));
    List<KnownTexturePayload> payloads = GetKnownTexturePayloads()
        .Where(payload => filterIndexes.Length == 0 || filterIndexes.Contains(payload.Index))
        .Take(maxPayloads)
        .ToList();

    foreach (KnownTexturePayload payload in payloads)
    {
        byte[] bytes = ReadDiskBytes(memory, payload.ByteOffset, payload.ByteLength);
        if (bytes.Length == 0)
        {
            Console.WriteLine($"knownTexturePayload={payload.Index}:{payload.Code} read=failed byte=0x{payload.ByteOffset:x8}");
            continue;
        }

        var formats = new[]
        {
            new ByteSurfaceFormat(64, 64, 64),
            new ByteSurfaceFormat(128, 64, 128),
            new ByteSurfaceFormat(128, 128, 128),
            new ByteSurfaceFormat(256, 128, 256)
        };
        List<ByteSurfaceCandidate> candidates = [];
        foreach (ByteSurfaceFormat format in formats)
        {
            int surfaceBytes = format.Stride * format.Height;
            if (surfaceBytes <= 0 || surfaceBytes > bytes.Length)
                continue;

            for (int offset = 0; offset + surfaceBytes <= bytes.Length; offset += 0x400)
            {
                ByteSurfaceScore score = ScoreByteSurface(bytes, offset, format);
                if (score.NonZero < 256 || score.UniqueBytes < 12)
                    continue;
                candidates.Add(new ByteSurfaceCandidate(offset, format, score));
            }
        }

        candidates = candidates
            .OrderByDescending(candidate => candidate.Score.Score)
            .ThenBy(candidate => candidate.Offset)
            .Take(maxSurfacesPerPayload)
            .ToList();

        Console.WriteLine("knownTexturePayload=" +
            $"{payload.Index}:{payload.Code}:byte=0x{payload.ByteOffset:x8}:len=0x{payload.ByteLength:x}:candidates=" +
            (candidates.Count == 0
                ? "none"
                : string.Join(",", candidates.Select(candidate =>
                    $"0x{candidate.Offset:x}:{candidate.Format.Width}x{candidate.Format.Height}/u{candidate.Score.UniqueBytes}/t{candidate.Score.Transitions}"))));

        for (int i = 0; i < candidates.Count; i++)
        {
            ByteSurfaceCandidate candidate = candidates[i];
            string stem = $"{prefix}_{payload.Index:D2}_{payload.Code}_{i}_0x{candidate.Offset:x}_{candidate.Format.Width}x{candidate.Format.Height}";
            DumpByteSurface(bytes, candidate.Offset, candidate.Format, $"{stem}_rgb332.ppm", rgb332: true);
            DumpByteSurface(bytes, candidate.Offset, candidate.Format, $"{stem}_gray.ppm", rgb332: false);
            Console.WriteLine($"knownTexturePayloadDump={stem}_rgb332.ppm");
            Console.WriteLine($"knownTexturePayloadDump={stem}_gray.ppm");
        }
    }
}

static byte[] ReadDiskBytes(object memory, ulong byteOffset, uint byteLength)
{
    byte[] bytes = new byte[byteLength];
    for (uint offset = 0; offset < byteLength; offset += 4)
    {
        if (!TryReadDiskByteOffsetWord(memory, byteOffset + offset, out uint word))
            return [];

        int remaining = (int)Math.Min(4U, byteLength - offset);
        for (int lane = 0; lane < remaining; lane++)
            bytes[offset + lane] = (byte)(word >> (lane * 8));
    }

    return bytes;
}

static bool TryReadDiskByteOffsetWord(object memory, ulong byteOffset, out uint word)
{
    MethodInfo? method = memory.GetType().GetMethod("TryReadDiskByteOffsetWord", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (method is null)
        throw new MissingMethodException(memory.GetType().FullName, "TryReadDiskByteOffsetWord");

    object?[] args = [byteOffset, 0u];
    bool ok = (bool)(method.Invoke(memory, args) ?? false);
    word = ok ? (uint)(args[1] ?? 0u) : 0u;
    return ok;
}

static List<KnownTexturePayload> GetKnownTexturePayloads() =>
[
    new(1, "gei", 0x14a6f600UL, 0x0000a13cU),
    new(2, "snm", 0x14a54800UL, 0x000091b0U),
    new(3, "stk", 0x15117a00UL, 0x0000a434U),
    new(4, "kjh", 0x15130e00UL, 0x00009ae8U),
    new(5, "pnk", 0x1514da00UL, 0x00009e80U),
    new(6, "geb", 0x15781600UL, 0x0000b130U),
    new(7, "nin", 0x15896000UL, 0x0000b130U),
    new(8, "stg", 0x1585d800UL, 0x0000acccU),
    new(9, "wtr", 0x158b0600UL, 0x0000bca4U),
    new(10, "css", 0x158cc800UL, 0x00009194U),
    new(11, "riz", 0x158ea800UL, 0x00009a00U),
    new(16, "get", 0x13380000UL, 0x00008e9cU),
    new(17, "sch", 0x13458e00UL, 0x0000b528U),
    new(18, "cel", 0x1339d800UL, 0x00014b98U),
    new(19, "gec", 0x12cbb200UL, 0x0000a3b8U),
    new(20, "gem", 0x12cdb000UL, 0x0000a460U),
    new(21, "rat", 0x12cfa600UL, 0x0000a5fcU),
    new(22, "ga2", 0x13bb9400UL, 0x00008ad8U),
    new(23, "gam", 0x13b9ce00UL, 0x0000b2c4U),
    new(24, "ged", 0x13b6ac00UL, 0x000142dcU),
    new(25, "gep", 0x13b82600UL, 0x00009550U),
    new(26, "sum", 0x13b4b600UL, 0x0000a37cU)
];

static ByteSurfaceScore ScoreByteSurface(byte[] bytes, int offset, ByteSurfaceFormat format)
{
    int nonZero = 0;
    int transitions = 0;
    int previous = -1;
    HashSet<byte> unique = [];
    int stepX = Math.Max(1, format.Width / 128);
    int stepY = Math.Max(1, format.Height / 96);
    for (int y = 0; y < format.Height; y += stepY)
    {
        int row = offset + y * format.Stride;
        for (int x = 0; x < format.Width; x += stepX)
        {
            byte value = bytes[row + x];
            if (value != 0)
                nonZero++;
            if (previous >= 0 && value != previous)
                transitions++;
            previous = value;
            unique.Add(value);
        }
    }

    long score = (long)nonZero * 8L + unique.Count * 512L + transitions * 2L;
    return new ByteSurfaceScore(nonZero, unique.Count, transitions, score);
}

static void DumpByteSurface(byte[] bytes, int offset, ByteSurfaceFormat format, string path, bool rgb332)
{
    EnsureOutputDirectory(path);
    using var stream = File.Create(path);
    using var writer = new StreamWriter(stream, leaveOpen: true);
    writer.Write($"P6\n{format.Width} {format.Height}\n255\n");
    writer.Flush();
    for (int y = 0; y < format.Height; y++)
    {
        int row = offset + y * format.Stride;
        for (int x = 0; x < format.Width; x++)
        {
            byte value = bytes[row + x];
            if (rgb332)
            {
                int r = ((value >> 5) & 0x07) * 255 / 7;
                int g = ((value >> 2) & 0x07) * 255 / 7;
                int b = (value & 0x03) * 255 / 3;
                stream.WriteByte((byte)r);
                stream.WriteByte((byte)g);
                stream.WriteByte((byte)b);
            }
            else
            {
                stream.WriteByte(value);
                stream.WriteByte(value);
                stream.WriteByte(value);
            }
        }
    }
}

static void DumpRequestedRamSurfaces(byte[] mainRam, string prefix)
{
    string? specs = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DUMP_RAM_SURFACE_SPECS");
    if (string.IsNullOrWhiteSpace(specs))
        return;

    int index = 0;
    foreach (string item in specs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] parts = item.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4 ||
            !TryParseHexUlong(parts[0], out ulong address) ||
            !int.TryParse(parts[1], out int width) ||
            !int.TryParse(parts[2], out int height) ||
            !int.TryParse(parts[3], out int stride) ||
            width <= 0 || height <= 0 || stride < width * 2)
        {
            continue;
        }

        int offset = (int)(address & 0x1fffffffUL);
        var format = new RamSurfaceFormat(width, height, stride);
        int bytes = stride * height;
        if (offset < 0 || offset + bytes > mainRam.Length)
            continue;

        string path = $"{prefix}_spec{index}_{0x80000000u + (uint)offset:x8}_{width}x{height}_s{stride}.ppm";
        DumpRgb565Surface(mainRam, offset, format, path);
        RamSurfaceScore score = ScoreRgb565Surface(mainRam, offset, format);
        Console.WriteLine($"ramSurfaceSpecDump={path} nz={score.NonZero} colored={score.Colored} unique={score.UniqueColors}");
        index++;
    }
}

static RamSurfaceScore ScoreRgb565Surface(byte[] ram, int offset, RamSurfaceFormat format)
{
    int nonZero = 0;
    int colored = 0;
    HashSet<ushort> colors = [];
    int stepX = Math.Max(1, format.Width / 128);
    int stepY = Math.Max(1, format.Height / 96);
    for (int y = 0; y < format.Height; y += stepY)
    {
        int row = offset + y * format.Stride;
        for (int x = 0; x < format.Width; x += stepX)
        {
            ushort rgb = BinaryPrimitives.ReadUInt16LittleEndian(ram.AsSpan(row + x * 2, 2));
            if (rgb == 0)
                continue;

            nonZero++;
            colors.Add(rgb);
            int r = (rgb >> 11) & 0x1f;
            int g = (rgb >> 5) & 0x3f;
            int b = rgb & 0x1f;
            if (Math.Abs((r << 1) - g) > 3 || Math.Abs(r - b) > 2)
                colored++;
        }
    }

    int unique = colors.Count;
    long score = (long)nonZero * 16L + colored * 8L + unique * 256L;
    return new RamSurfaceScore(nonZero, colored, unique, score);
}

static RamSurfaceScore ScoreRgb565Buffer(ushort[] buffer, int width, int height, int stridePixels)
    => ScoreRgb565BufferWindow(buffer, width, height, stridePixels, startY: 0);

static RamSurfaceScore ScoreRgb565BufferWindow(ushort[] buffer, int width, int height, int stridePixels, int startY)
{
    int nonZero = 0;
    int colored = 0;
    HashSet<ushort> colors = [];
    int stepX = Math.Max(1, width / 128);
    int stepY = Math.Max(1, height / 96);
    for (int y = 0; y < height; y += stepY)
    {
        int row = (startY + y) * stridePixels;
        for (int x = 0; x < width; x += stepX)
        {
            ushort rgb = buffer[row + x];
            if (rgb == 0)
                continue;

            nonZero++;
            colors.Add(rgb);
            int r = (rgb >> 11) & 0x1f;
            int g = (rgb >> 5) & 0x3f;
            int b = rgb & 0x1f;
            if (Math.Abs((r << 1) - g) > 3 || Math.Abs(r - b) > 2)
                colored++;
        }
    }

    int unique = colors.Count;
    long score = (long)nonZero * 16L + colored * 8L + unique * 256L;
    return new RamSurfaceScore(nonZero, colored, unique, score);
}

static void DumpRgb565Surface(byte[] ram, int offset, RamSurfaceFormat format, string path)
{
    EnsureOutputDirectory(path);
    using var stream = File.Create(path);
    using var writer = new StreamWriter(stream, leaveOpen: true);
    writer.Write($"P6\n{format.Width} {format.Height}\n255\n");
    writer.Flush();
    for (int y = 0; y < format.Height; y++)
    {
        int row = offset + y * format.Stride;
        for (int x = 0; x < format.Width; x++)
        {
            ushort rgb = BinaryPrimitives.ReadUInt16LittleEndian(ram.AsSpan(row + x * 2, 2));
            WriteRgb565(stream, rgb);
        }
    }
}

static void DumpRgb565Buffer(ushort[] buffer, int width, int height, int stridePixels, string path)
    => DumpRgb565BufferWindow(buffer, width, height, stridePixels, startY: 0, path);

static void DumpRgb565BufferWindow(ushort[] buffer, int width, int height, int stridePixels, int startY, string path)
{
    EnsureOutputDirectory(path);
    using var stream = File.Create(path);
    using var writer = new StreamWriter(stream, leaveOpen: true);
    writer.Write($"P6\n{width} {height}\n255\n");
    writer.Flush();
    for (int y = 0; y < height; y++)
    {
        int row = (startY + y) * stridePixels;
        for (int x = 0; x < width; x++)
        {
            ushort rgb = buffer[row + x];
            WriteRgb565(stream, rgb);
        }
    }
}

static void WriteRgb565(Stream stream, ushort rgb)
{
    int r = (rgb >> 11) & 0x1f;
    int g = (rgb >> 5) & 0x3f;
    int b = rgb & 0x1f;
    stream.WriteByte((byte)((r << 3) | (r >> 2)));
    stream.WriteByte((byte)((g << 2) | (g >> 4)));
    stream.WriteByte((byte)((b << 3) | (b >> 2)));
}

static void EnsureOutputDirectory(string pathOrPrefix)
{
    string? directory = Path.GetDirectoryName(pathOrPrefix);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);
}

static int ParsePositiveInt(string? value, int fallback)
{
    if (string.IsNullOrWhiteSpace(value))
        return fallback;

    value = value.Trim();
    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        return int.TryParse(
                value[2..],
                System.Globalization.NumberStyles.HexNumber,
                null,
                out int hexParsed) &&
            hexParsed > 0
                ? hexParsed
                : fallback;
    }

    return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
}

static ulong[] ParseHexList(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return [];

    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => TryParseHexUlong(item, out ulong parsed) ? parsed : ulong.MaxValue)
        .Where(parsed => parsed != ulong.MaxValue)
        .ToArray();
}

readonly record struct RamSurfaceFormat(int Width, int Height, int Stride);
readonly record struct RamSurfaceScore(int NonZero, int Colored, int UniqueColors, long Score);
readonly record struct RamSurfaceCandidate(int Offset, RamSurfaceFormat Format, RamSurfaceScore Score);
readonly record struct ByteSurfaceFormat(int Width, int Height, int Stride);
readonly record struct ByteSurfaceScore(int NonZero, int UniqueBytes, int Transitions, long Score);
readonly record struct ByteSurfaceCandidate(int Offset, ByteSurfaceFormat Format, ByteSurfaceScore Score);
readonly record struct KnownTexturePayload(ulong Index, string Code, ulong ByteOffset, uint ByteLength);

sealed class ProbeSummaryContext
{
    public bool Enabled { get; init; }
    public string ModuleId { get; init; } = "";
    public string? WarmupSnapshotPath { get; init; }
    public string WarmupState { get; set; } = "none";
}
