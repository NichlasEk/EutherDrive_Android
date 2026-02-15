using System;
using System.IO;

namespace KSNES.Tracing;

internal static class Sa1Trace
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1"), "1", StringComparison.Ordinal);
    private static readonly int Limit = ParseLimit("EUTHERDRIVE_TRACE_SA1_LIMIT", 200000);
    private static readonly int HashEvery = ParseLimit("EUTHERDRIVE_TRACE_SA1_HASH_EVERY", 10000);
    private static readonly string TracePath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_PATH") ?? string.Empty;
    private static readonly object TraceLock = new();
    private static StreamWriter? _writer;
    private static int _budget = Limit;
    private static ulong _eventId;
    private static ulong _hash = 1469598103934665603UL;

    public static bool IsEnabled => Enabled;

    public static void Log(
        string cpu,
        int pc,
        int op,
        uint address,
        string rw,
        byte value,
        string region,
        uint? resolved)
    {
        if (!Enabled || _budget-- <= 0)
            return;

        ulong ev = ++_eventId;
        UpdateHash(ev);
        UpdateHash((ulong)pc);
        UpdateHash((ulong)(op & 0xFF));
        UpdateHash(address);
        UpdateHash((ulong)rw.GetHashCode());
        UpdateHash(value);
        UpdateHash((ulong)region.GetHashCode());
        if (resolved.HasValue)
            UpdateHash(resolved.Value);

        string opText = op < 0 ? "--" : $"{op:X2}";
        string resolvedText = resolved.HasValue ? $"0x{resolved.Value:X6}" : "--";
        string line =
            $"[SA1-TRACE] ev={ev} cpu={cpu} pc=0x{pc:X6} op={opText} rw={rw} addr=0x{address:X6} val=0x{value:X2} region={region} res={resolvedText}";

        lock (TraceLock)
        {
            StreamWriter writer = GetWriter();
            writer.WriteLine(line);
            if (HashEvery > 0 && (ev % (ulong)HashEvery) == 0)
            {
                writer.WriteLine($"[SA1-TRACE-HASH] ev={ev} hash=0x{_hash:X16}");
            }
        }
    }

    private static void UpdateHash(ulong value)
    {
        _hash ^= value;
        _hash *= 1099511628211UL;
    }

    private static StreamWriter GetWriter()
    {
        if (_writer != null)
            return _writer;

        string baseDir = Environment.CurrentDirectory;
        string logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logDir);
        string path = string.IsNullOrWhiteSpace(TracePath)
            ? Path.Combine(logDir, "sa1_trace.log")
            : TracePath;

        _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
        return _writer;
    }

    private static int ParseLimit(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, out int value))
            return value;
        return fallback;
    }
}
