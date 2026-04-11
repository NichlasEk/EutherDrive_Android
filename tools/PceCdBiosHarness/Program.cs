using System.Globalization;
using System.Text;
using System.Text.Json;
using EutherDrive.Core;
using ePceCD;

internal static class Program
{
    private const int DefaultFrames = 240;
    private const double DefaultFps = 60.0;
    private const int DefaultLoopThreshold = 90;
    private const int LoopDetectionMaxPeriod = 8;
    private const int LoopDetectionMinRepeats = 8;

    private static readonly HashSet<string> VolatileTraceKeys = new(StringComparer.Ordinal)
    {
        "frame",
        "cpu_clk",
        "bus_timer",
        "bus_tov",
        "bus_ovf",
        "bus_dead",
        "ppu_line"
    };

    private static int Main(string[] args)
    {
        try
        {
            Options options = ParseArguments(args);
            string outputDir = ResolveOutputDirectory(options);
            Directory.CreateDirectory(outputDir);
            ConfigureEnvironment(options, outputDir);

            PceCdAdapter.BiosMode = options.BiosMode;
            if (!string.IsNullOrWhiteSpace(options.BiosPath))
                PceCdAdapter.BiosPath = options.BiosPath;

            string tracePath = Path.Combine(outputDir, "pce_trace.log");
            string biosTracePath = Path.Combine(outputDir, "pce_bios_trace.log");
            string saveDir = Path.Combine(outputDir, "save");

            Console.WriteLine($"[PCE-HLE] rom={options.RomPath}");
            Console.WriteLine($"[PCE-HLE] mode={options.BiosMode} frames={options.Frames} output={outputDir}");

            using var pce = new PceCdAdapter();
            pce.LoadRom(options.RomPath);

            var snapshotFiles = new List<string>();
            snapshotFiles.Add(Path.GetFileName(pce.CaptureDebugSnapshot(outputDir)));

            string[]? compareLines = LoadComparisonTrace(options.CompareTracePath);
            int? firstMismatchFrame = null;
            string? expectedMismatchLine = null;
            string? actualMismatchLine = null;
            string? compareSummary = null;

            int? firstProgramFrame = null;
            string? firstProgramPc = null;
            int? firstVideoFrame = null;
            int maxRepeatWindow = 0;
            int repeatWindow = 0;
            int? suspectedHangFrame = null;
            int? suspectedLoopFrame = null;
            int? suspectedLoopPeriod = null;
            string? lastFingerprint = null;
            var loopHistory = new List<string>(options.Frames);

            var snapshotFrames = new HashSet<int>(options.SnapshotFrames);

            using (var traceWriter = new StreamWriter(tracePath, append: false, new UTF8Encoding(false)))
            {
                for (int frame = 0; frame < options.Frames; frame++)
                {
                    bool startPressed = options.AutoRun &&
                                        ShouldPressStartPulse(
                                            frame,
                                            options.AutoRunDelayFrames,
                                            options.AutoRunPulseFrames,
                                            options.AutoRunPeriodFrames,
                                            options.AutoRunPulseCount);

                    pce.SetInputState(
                        up: false,
                        down: false,
                        left: false,
                        right: false,
                        a: false,
                        b: false,
                        c: false,
                        start: startPressed,
                        x: false,
                        y: false,
                        z: false,
                        mode: false,
                        padType: PadType.SixButton);

                    pce.RunFrame();

                    if (snapshotFrames.Contains(frame))
                        snapshotFiles.Add(Path.GetFileName(pce.CaptureDebugSnapshot(outputDir)));

                    ReadOnlySpan<byte> frameBuffer = pce.GetFrameBuffer(out int width, out int height, out int stride);
                    FrameStats stats = GetFrameStats(frameBuffer, width, height, stride);
                    if (!firstVideoFrame.HasValue && stats.NonZeroPixels > 0)
                        firstVideoFrame = frame;

                    string traceLine = pce.BuildDeterminismTraceLine(frame);
                    traceWriter.WriteLine(traceLine);

                    Dictionary<string, string> fields = ParseTraceFields(traceLine);
                    if (!firstProgramFrame.HasValue &&
                        TryGetHex(fields, "cpu_pc", out int pc) &&
                        !IsBiosAddress(pc))
                    {
                        firstProgramFrame = frame;
                        firstProgramPc = pc.ToString("X4", CultureInfo.InvariantCulture);
                    }

                    string fingerprint = BuildFingerprint(fields);
                    string loopFingerprint = BuildLoopFingerprint(fields);
                    loopHistory.Add(loopFingerprint);
                    if (string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
                    {
                        repeatWindow++;
                    }
                    else
                    {
                        repeatWindow = 1;
                        lastFingerprint = fingerprint;
                    }

                    if (repeatWindow > maxRepeatWindow)
                        maxRepeatWindow = repeatWindow;

                    if (!suspectedHangFrame.HasValue && repeatWindow >= options.LoopThresholdFrames)
                        suspectedHangFrame = frame - repeatWindow + 1;

                    if (!suspectedLoopFrame.HasValue &&
                        TryDetectLoop(loopHistory, LoopDetectionMaxPeriod, LoopDetectionMinRepeats, out int loopPeriod))
                    {
                        suspectedLoopPeriod = loopPeriod;
                        suspectedLoopFrame = frame - (loopPeriod * LoopDetectionMinRepeats) + 1;
                    }

                    if (compareLines != null && !firstMismatchFrame.HasValue)
                    {
                        if (frame >= compareLines.Length)
                        {
                            firstMismatchFrame = frame;
                            expectedMismatchLine = "<comparison trace ended>";
                            actualMismatchLine = traceLine;
                        }
                        else if (!string.Equals(compareLines[frame], traceLine, StringComparison.Ordinal))
                        {
                            firstMismatchFrame = frame;
                            expectedMismatchLine = compareLines[frame];
                            actualMismatchLine = traceLine;
                        }
                    }
                }
            }

            if (compareLines != null)
            {
                if (!firstMismatchFrame.HasValue && compareLines.Length != options.Frames)
                {
                    firstMismatchFrame = Math.Min(compareLines.Length, options.Frames);
                    expectedMismatchLine = compareLines.Length > options.Frames ? compareLines[firstMismatchFrame.Value] : "<comparison trace ended>";
                    actualMismatchLine = compareLines.Length > options.Frames ? "<current trace ended>" : "<current trace ended>";
                }

                compareSummary = firstMismatchFrame.HasValue
                    ? $"first mismatch at frame {firstMismatchFrame.Value}"
                    : "exact match";
            }

            pce.WriteBiosArtifacts(outputDir);
            snapshotFiles.Add(Path.GetFileName(pce.CaptureDebugSnapshot(outputDir)));

            var summary = new HarnessSummary
            {
                RomPath = options.RomPath,
                BiosMode = options.BiosMode.ToString(),
                Frames = options.Frames,
                OutputDirectory = outputDir,
                TracePath = tracePath,
                BiosTracePath = biosTracePath,
                SaveDirectory = saveDir,
                CompareTracePath = options.CompareTracePath,
                CompareSummary = compareSummary,
                FirstMismatchFrame = firstMismatchFrame,
                FirstMismatchExpected = expectedMismatchLine,
                FirstMismatchActual = actualMismatchLine,
                FirstProgramFrame = firstProgramFrame,
                FirstProgramPc = firstProgramPc,
                FirstVideoFrame = firstVideoFrame,
                SuspectedHangFrame = suspectedHangFrame,
                SuspectedLoopFrame = suspectedLoopFrame,
                SuspectedLoopPeriod = suspectedLoopPeriod,
                MaxRepeatWindow = maxRepeatWindow,
                SnapshotFiles = snapshotFiles,
                AutoRun = options.AutoRun,
                AutoRunDelayFrames = options.AutoRunDelayFrames,
                AutoRunPulseFrames = options.AutoRunPulseFrames,
                AutoRunPeriodFrames = options.AutoRunPeriodFrames,
                AutoRunPulseCount = options.AutoRunPulseCount
            };

            string summaryJsonPath = Path.Combine(outputDir, "summary.json");
            string summaryMarkdownPath = Path.Combine(outputDir, "summary.md");
            File.WriteAllText(summaryJsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(summaryMarkdownPath, BuildSummaryMarkdown(summary));

            Console.WriteLine($"[PCE-HLE] trace={tracePath}");
            Console.WriteLine($"[PCE-HLE] bios-trace={biosTracePath}");
            Console.WriteLine($"[PCE-HLE] first-program-frame={(summary.FirstProgramFrame.HasValue ? summary.FirstProgramFrame.Value.ToString(CultureInfo.InvariantCulture) : "-")} pc={(summary.FirstProgramPc ?? "-")}");
            Console.WriteLine($"[PCE-HLE] first-video-frame={(summary.FirstVideoFrame.HasValue ? summary.FirstVideoFrame.Value.ToString(CultureInfo.InvariantCulture) : "-")}");
            Console.WriteLine($"[PCE-HLE] suspected-hang-frame={(summary.SuspectedHangFrame.HasValue ? summary.SuspectedHangFrame.Value.ToString(CultureInfo.InvariantCulture) : "-")} repeat-window={summary.MaxRepeatWindow}");
            Console.WriteLine($"[PCE-HLE] suspected-loop-frame={(summary.SuspectedLoopFrame.HasValue ? summary.SuspectedLoopFrame.Value.ToString(CultureInfo.InvariantCulture) : "-")} period={(summary.SuspectedLoopPeriod.HasValue ? summary.SuspectedLoopPeriod.Value.ToString(CultureInfo.InvariantCulture) : "-")}");
            if (!string.IsNullOrWhiteSpace(summary.CompareSummary))
                Console.WriteLine($"[PCE-HLE] compare={summary.CompareSummary}");
            Console.WriteLine($"[PCE-HLE] artifacts={outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PCE-HLE] ERROR {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static Options ParseArguments(string[] args)
    {
        var options = new Options();
        double? seconds = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--rom":
                    options.RomPath = ReadRequiredValue(args, ref i, "--rom");
                    break;
                case "--frames":
                    options.Frames = ParsePositiveInt(ReadRequiredValue(args, ref i, "--frames"), "--frames");
                    break;
                case "--seconds":
                    seconds = ParsePositiveDouble(ReadRequiredValue(args, ref i, "--seconds"), "--seconds");
                    break;
                case "--bios-mode":
                    options.BiosMode = ParseMode(ReadRequiredValue(args, ref i, "--bios-mode"));
                    break;
                case "--bios-path":
                    options.BiosPath = ReadRequiredValue(args, ref i, "--bios-path");
                    break;
                case "--output-dir":
                    options.OutputDirectory = ReadRequiredValue(args, ref i, "--output-dir");
                    break;
                case "--compare-trace":
                    options.CompareTracePath = ReadRequiredValue(args, ref i, "--compare-trace");
                    break;
                case "--snapshot-frames":
                    options.SnapshotFrames = ParseFrameList(ReadRequiredValue(args, ref i, "--snapshot-frames"));
                    break;
                case "--no-auto-run":
                    options.AutoRun = false;
                    break;
                case "--auto-run-delay":
                    options.AutoRunDelayFrames = ParseNonNegativeInt(ReadRequiredValue(args, ref i, "--auto-run-delay"), "--auto-run-delay");
                    break;
                case "--auto-run-pulse":
                    options.AutoRunPulseFrames = ParsePositiveInt(ReadRequiredValue(args, ref i, "--auto-run-pulse"), "--auto-run-pulse");
                    break;
                case "--auto-run-period":
                    options.AutoRunPeriodFrames = ParsePositiveInt(ReadRequiredValue(args, ref i, "--auto-run-period"), "--auto-run-period");
                    break;
                case "--auto-run-count":
                    options.AutoRunPulseCount = ParsePositiveInt(ReadRequiredValue(args, ref i, "--auto-run-count"), "--auto-run-count");
                    break;
                case "--loop-threshold":
                    options.LoopThresholdFrames = ParsePositiveInt(ReadRequiredValue(args, ref i, "--loop-threshold"), "--loop-threshold");
                    break;
                case "--cd-command-log":
                    options.EnableCdCommandLog = true;
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(options.RomPath))
                    {
                        options.RomPath = arg;
                    }
                    else if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames))
                    {
                        options.Frames = frames;
                    }
                    else
                    {
                        throw new ArgumentException($"Unexpected argument '{arg}'.");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(options.RomPath))
            throw new ArgumentException("Missing ROM path. Use --rom <path>.");

        if (seconds.HasValue)
            options.Frames = Math.Max(1, (int)Math.Ceiling(seconds.Value * DefaultFps));

        return options;
    }

    private static void ConfigureEnvironment(Options options, string outputDir)
    {
        string saveDir = Path.Combine(outputDir, "save");
        string biosTracePath = Path.Combine(outputDir, "pce_bios_trace.log");

        Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_SAVE_DIR", saveDir);
        Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_CD_BIOS_MODE", options.BiosMode.ToString().ToLowerInvariant());
        Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_BIOS_TRACE", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_BIOS_TRACE_FILE", biosTracePath);
        Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_BIOS_TRACE_STDOUT", "0");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_BIOS_TRACE_LIMIT", "8000");

        if (options.EnableCdCommandLog)
        {
            Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_CMD_LOG", "1");
            Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG", "1");
            Environment.SetEnvironmentVariable("EUTHERDRIVE_PCE_CDREG_LOG", "1");
        }
    }

    private static string ResolveOutputDirectory(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return Path.IsPathRooted(options.OutputDirectory)
                ? options.OutputDirectory
                : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, options.OutputDirectory));
        }

        string stem = SanitizePathSegment(Path.GetFileNameWithoutExtension(options.RomPath));
        string mode = options.BiosMode.ToString().ToLowerInvariant();
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(Environment.CurrentDirectory, "artifacts", "pcecd_bios", $"{stamp}_{stem}_{mode}");
    }

    private static string BuildSummaryMarkdown(HarnessSummary summary)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("# PCE CD BIOS Harness Summary");
        sb.AppendLine();
        sb.AppendLine($"- Rom: `{summary.RomPath}`");
        sb.AppendLine($"- BIOS mode: `{summary.BiosMode}`");
        sb.AppendLine($"- Frames: `{summary.Frames}`");
        sb.AppendLine($"- Trace: `{summary.TracePath}`");
        sb.AppendLine($"- BIOS trace: `{summary.BiosTracePath}`");
        sb.AppendLine($"- First non-BIOS PC: `{FormatFrame(summary.FirstProgramFrame, summary.FirstProgramPc)}`");
        sb.AppendLine($"- First video frame: `{FormatNullable(summary.FirstVideoFrame)}`");
        sb.AppendLine($"- Suspected hang frame: `{FormatNullable(summary.SuspectedHangFrame)}`");
        sb.AppendLine($"- Suspected loop: `{FormatLoop(summary.SuspectedLoopFrame, summary.SuspectedLoopPeriod)}`");
        sb.AppendLine($"- Max repeat window: `{summary.MaxRepeatWindow}`");
        sb.AppendLine($"- Compare: `{summary.CompareSummary ?? "-"}`");
        sb.AppendLine();
        sb.AppendLine("## Snapshots");
        foreach (string file in summary.SnapshotFiles)
            sb.AppendLine($"- `{file}`");

        if (summary.FirstMismatchFrame.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine("## Compare Mismatch");
            sb.AppendLine($"- Frame: `{summary.FirstMismatchFrame.Value}`");
            sb.AppendLine($"- Expected: `{summary.FirstMismatchExpected ?? "-"}`");
            sb.AppendLine($"- Actual: `{summary.FirstMismatchActual ?? "-"}`");
        }

        return sb.ToString();
    }

    private static string[]? LoadComparisonTrace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
        return File.Exists(fullPath) ? File.ReadAllLines(fullPath) : null;
    }

    private static Dictionary<string, string> ParseTraceFields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1)
                continue;

            fields[token[..separator]] = token[(separator + 1)..];
        }

        return fields;
    }

    private static string BuildFingerprint(Dictionary<string, string> fields)
    {
        return string.Join(
            " ",
            fields
                .Where(pair => !VolatileTraceKeys.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string BuildLoopFingerprint(Dictionary<string, string> fields)
    {
        string[] keys =
        {
            "cpu_pc",
            "cpu_a",
            "cpu_x",
            "cpu_y",
            "cpu_p",
            "bus_mpr",
            "fb_hash",
            "vram_hash",
            "sat_hash",
            "vce_hash",
            "cd_phase",
            "cd_cmd",
            "cd_cmdlen",
            "cd_bufpos",
            "cd_buflen",
            "cd_irqa",
            "cd_pending",
            "cd_sig_req",
            "cd_sig_ack",
            "cd_sig_bsy",
            "cd_sig_cd",
            "cd_sig_io",
            "cd_sig_msg"
        };

        var sb = new StringBuilder(256);
        foreach (string key in keys)
        {
            if (!fields.TryGetValue(key, out string? value))
                continue;

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(key).Append('=').Append(value);
        }

        return sb.ToString();
    }

    private static bool TryDetectLoop(List<string> history, int maxPeriod, int minRepeats, out int period)
    {
        period = 0;
        if (history.Count < minRepeats)
            return false;

        int cappedPeriod = Math.Min(maxPeriod, history.Count / minRepeats);
        for (int candidatePeriod = 1; candidatePeriod <= cappedPeriod; candidatePeriod++)
        {
            int window = candidatePeriod * minRepeats;
            bool matches = true;
            for (int i = 0; i < window - candidatePeriod; i++)
            {
                if (!string.Equals(
                        history[history.Count - 1 - i],
                        history[history.Count - 1 - candidatePeriod - i],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                period = candidatePeriod;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetHex(Dictionary<string, string> fields, string key, out int value)
    {
        value = 0;
        return fields.TryGetValue(key, out string? raw) &&
               int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsBiosAddress(int pc)
    {
        return pc == 0xFFF0 || (pc >= 0xE000 && pc <= 0xFFFF);
    }

    private static FrameStats GetFrameStats(ReadOnlySpan<byte> buffer, int width, int height, int stride)
    {
        int nonZeroPixels = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + x * 4;
                if ((buffer[pixel] | buffer[pixel + 1] | buffer[pixel + 2]) == 0)
                    continue;

                nonZeroPixels++;
            }
        }

        return new FrameStats(nonZeroPixels);
    }

    private static bool ShouldPressStartPulse(int frame, int delay, int pulse, int period, int count)
    {
        if (frame < delay || pulse <= 0 || period <= 0 || count <= 0)
            return false;

        int relative = frame - delay;
        int window = relative / period;
        if (window < 0 || window >= count)
            return false;

        return (relative % period) < pulse;
    }

    private static List<int> ParseFrameList(string raw)
    {
        var frames = new List<int>();
        foreach (string part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            frames.Add(ParseNonNegativeInt(part, "--snapshot-frames"));
        return frames;
    }

    private static PceCdBiosMode ParseMode(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "rom" => PceCdBiosMode.Rom,
            "auto" => PceCdBiosMode.Auto,
            "hle" => PceCdBiosMode.Hle,
            _ => throw new ArgumentException($"Unknown BIOS mode '{raw}'.")
        };
    }

    private static string ReadRequiredValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private static int ParsePositiveInt(string raw, string option)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return value;
    }

    private static int ParseNonNegativeInt(string raw, string option)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
            throw new ArgumentException($"{option} must be a non-negative integer.");
        return value;
    }

    private static double ParsePositiveDouble(string raw, string option)
    {
        if (!double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double value) || value <= 0)
            throw new ArgumentException($"{option} must be a positive number.");
        return value;
    }

    private static string SanitizePathSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "disc" : sb.ToString();
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
    }

    private static string FormatFrame(int? frame, string? pc)
    {
        if (!frame.HasValue)
            return "-";
        return $"frame={frame.Value} pc=0x{pc ?? "0000"}";
    }

    private static string FormatLoop(int? frame, int? period)
    {
        if (!frame.HasValue || !period.HasValue)
            return "-";
        return $"frame={frame.Value} period={period.Value}";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project tools/PceCdBiosHarness/PceCdBiosHarness.csproj -- --rom <disc.cue> [--bios-mode rom|auto|hle] [--frames N]");
        Console.WriteLine("       dotnet run --project tools/PceCdBiosHarness/PceCdBiosHarness.csproj -- <disc.cue> 240");
    }

    private sealed class Options
    {
        public string RomPath { get; set; } = string.Empty;
        public int Frames { get; set; } = DefaultFrames;
        public PceCdBiosMode BiosMode { get; set; } = PceCdBiosMode.Auto;
        public string? BiosPath { get; set; }
        public string? OutputDirectory { get; set; }
        public string? CompareTracePath { get; set; }
        public List<int> SnapshotFrames { get; set; } = new();
        public bool AutoRun { get; set; } = true;
        public int AutoRunDelayFrames { get; set; } = 90;
        public int AutoRunPulseFrames { get; set; } = 3;
        public int AutoRunPeriodFrames { get; set; } = 90;
        public int AutoRunPulseCount { get; set; } = 8;
        public int LoopThresholdFrames { get; set; } = DefaultLoopThreshold;
        public bool EnableCdCommandLog { get; set; }
    }

    private sealed class HarnessSummary
    {
        public string RomPath { get; set; } = string.Empty;
        public string BiosMode { get; set; } = string.Empty;
        public int Frames { get; set; }
        public string OutputDirectory { get; set; } = string.Empty;
        public string TracePath { get; set; } = string.Empty;
        public string BiosTracePath { get; set; } = string.Empty;
        public string SaveDirectory { get; set; } = string.Empty;
        public string? CompareTracePath { get; set; }
        public string? CompareSummary { get; set; }
        public int? FirstMismatchFrame { get; set; }
        public string? FirstMismatchExpected { get; set; }
        public string? FirstMismatchActual { get; set; }
        public int? FirstProgramFrame { get; set; }
        public string? FirstProgramPc { get; set; }
        public int? FirstVideoFrame { get; set; }
        public int? SuspectedHangFrame { get; set; }
        public int? SuspectedLoopFrame { get; set; }
        public int? SuspectedLoopPeriod { get; set; }
        public int MaxRepeatWindow { get; set; }
        public List<string> SnapshotFiles { get; set; } = new();
        public bool AutoRun { get; set; }
        public int AutoRunDelayFrames { get; set; }
        public int AutoRunPulseFrames { get; set; }
        public int AutoRunPeriodFrames { get; set; }
        public int AutoRunPulseCount { get; set; }
    }

    private readonly record struct FrameStats(int NonZeroPixels);
}
