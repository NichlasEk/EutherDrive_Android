using System;
using System.IO;
using System.Reflection;

namespace EutherDrive.Core.MdTracerCore
{
internal static class MdLog
{
    internal static bool Enabled
    {
        get => _enabled || TraceVdpLogging;
        set => _enabled = value;
    }

    private static bool _enabled;
    private static bool _traceBuildLogged;

    private static bool EnvFlag(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    internal static readonly bool TraceZ80InstructionLogging =
        EnvFlag("EUTHERDRIVE_TRACE_Z80");

    internal static readonly bool TraceZ80Sig =
        EnvFlag("EUTHERDRIVE_TRACE_Z80SIG") || TraceZ80InstructionLogging;

    internal static readonly bool TraceZ80Step =
        EnvFlag("EUTHERDRIVE_TRACE_Z80STEP") || TraceZ80InstructionLogging;

    internal static readonly bool TraceZ80Ym =
        EnvFlag("EUTHERDRIVE_TRACE_Z80YM") || TraceZ80InstructionLogging;

    internal static readonly bool TraceZ80Int =
        EnvFlag("EUTHERDRIVE_TRACE_Z80INT") || TraceZ80InstructionLogging;

    internal static readonly bool TraceZ80Win =
        EnvFlag("EUTHERDRIVE_TRACE_Z80WIN");

    internal static readonly bool TraceVdpLogging =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MD_VDP"), "1", StringComparison.Ordinal);

    internal static bool AnyTraceEnabled =>
        TraceZ80InstructionLogging || TraceZ80Sig || TraceZ80Step || TraceZ80Ym || TraceZ80Int || TraceZ80Win ||
        TraceVdpLogging;

    internal static void WriteLine(string message)
    {
        if (Enabled)
            Console.WriteLine(message);
    }

    internal static void MaybeLogTraceBuildStamp()
    {
        if (_traceBuildLogged || !AnyTraceEnabled)
            return;
        _traceBuildLogged = true;

        string coreVersion = GetCoreVersion();
        Console.WriteLine(
            $"[TRACEBUILD] core={coreVersion} flags: Z80={(TraceZ80InstructionLogging ? 1 : 0)} " +
            $"SIG={(TraceZ80Sig ? 1 : 0)} STEP={(TraceZ80Step ? 1 : 0)} YM={(TraceZ80Ym ? 1 : 0)} " +
            $"INT={(TraceZ80Int ? 1 : 0)} WIN={(TraceZ80Win ? 1 : 0)}");
    }

    private static string GetCoreVersion()
    {
        Assembly asm = typeof(MdLog).Assembly;
        string version =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
        string? git = TryReadGitShortHash();
        if (!string.IsNullOrEmpty(git) && !version.Contains(git, StringComparison.OrdinalIgnoreCase))
            version = $"{version}+{git}";
        return version;
    }

    private static string? TryReadGitShortHash()
    {
        try
        {
            DirectoryInfo? dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                string gitDir = Path.Combine(dir.FullName, ".git");
                if (!Directory.Exists(gitDir))
                    continue;
                string headPath = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(headPath))
                    return null;
                string head = File.ReadAllText(headPath).Trim();
                if (head.StartsWith("ref:", StringComparison.Ordinal))
                {
                    string refPath = head.Substring(4).Trim();
                    string refFile = Path.Combine(gitDir, refPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(refFile))
                        return ReadHashPrefix(refFile);
                    string packed = Path.Combine(gitDir, "packed-refs");
                    if (File.Exists(packed))
                        return ReadPackedRefs(packed, refPath);
                    return null;
                }
                return head.Length >= 7 ? head.Substring(0, 7) : head;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private static string? ReadHashPrefix(string path)
    {
        string line = File.ReadAllText(path).Trim();
        return line.Length >= 7 ? line.Substring(0, 7) : line;
    }

    private static string? ReadPackedRefs(string path, string refPath)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                continue;
            if (line.StartsWith("^", StringComparison.Ordinal))
                continue;
            int space = line.IndexOf(' ');
            if (space <= 0)
                continue;
            string hash = line.Substring(0, space);
            string name = line.Substring(space + 1).Trim();
            if (string.Equals(name, refPath, StringComparison.Ordinal))
                return hash.Length >= 7 ? hash.Substring(0, 7) : hash;
        }
        return null;
    }
}
}
