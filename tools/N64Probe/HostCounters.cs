#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;

// Optional Linux x64 counters for the measured interval only, including CPU
// threads created during it. Never change host-wide perf or runtime settings.
internal sealed class HostCounters : IDisposable
{
    private readonly int cycles;
    private readonly int instructions;
    private readonly List<(Reading Cycles, Reading Instructions)> samples = new();
    private Reading startCycles, startInstructions;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Attributes
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Size;
        [FieldOffset(8)] public ulong Config;
        [FieldOffset(32)] public ulong ReadFormat;
        [FieldOffset(40)] public ulong Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Reading { public ulong Value, Enabled, Running; }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern int PerfEventOpen(long number, ref Attributes attributes, int pid, int cpu, int group, uint flags);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, ulong request, ulong argument);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fd, out Reading reading, nuint size);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);

    internal static HostCounters? Create()
    {
        if (Environment.GetEnvironmentVariable("N64_PROBE_HOST_COUNTERS") != "1") return null;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Host counters require Linux x64.");
        return new HostCounters();
    }

    private static int Open(ulong config)
    {
        // PERF_TYPE_HARDWARE, inherit, disabled, user-space only. Linux x64
        // __NR_perf_event_open=298; the kernel accepts the v0 64-byte structure.
        var attributes = new Attributes { Size = 64, Config = config, ReadFormat = 3, Flags = 1 | 2 | 32 | 64 };
        int fd = PerfEventOpen(298, ref attributes, 0, -1, -1, 0);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "perf_event_open");
        return fd;
    }

    private HostCounters()
    {
        cycles = Open(0);
        try { instructions = Open(1); }
        catch { Close(cycles); throw; }
    }

    private static void Control(int fd, ulong request)
    {
        if (Ioctl(fd, request, 0) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "perf ioctl");
    }

    internal void Start()
    {
        // Inherited child totals survive RESET on the parent event. Difference
        // cumulative readings instead, including for warmup runs.
        startCycles = Capture(cycles);
        startInstructions = Capture(instructions);
        Control(cycles, 0x2400); Control(instructions, 0x2400); // ENABLE
    }

    internal void Stop(bool record)
    {
        Control(instructions, 0x2401); Control(cycles, 0x2401); // DISABLE
        if (!record) return;
        var c = Difference(Capture(cycles), startCycles);
        var i = Difference(Capture(instructions), startInstructions);
        if (c.Running == 0 || i.Running == 0) throw new InvalidOperationException("Host counters did not run.");
        if (c.Enabled != c.Running || i.Enabled != i.Running)
            throw new InvalidOperationException("Host counters were multiplexed; raw totals are not comparable.");
        samples.Add((c, i));
        Console.WriteLine($"hostCounterSample cycles={c.Value} instructions={i.Value} cycleEnabled={c.Enabled} cycleRunning={c.Running} instEnabled={i.Enabled} instRunning={i.Running}");
    }

    private static Reading Capture(int fd)
    {
        if (Read(fd, out var value, 24) != 24)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "perf read");
        return value;
    }

    private static Reading Difference(Reading end, Reading start)
        => new() { Value = checked(end.Value - start.Value),
            Enabled = checked(end.Enabled - start.Enabled), Running = checked(end.Running - start.Running) };

    internal void Report()
    {
        if (samples.Count == 0) return;
        var c = samples.Select(s => s.Cycles.Value).Order().ToArray();
        var i = samples.Select(s => s.Instructions.Value).Order().ToArray();
        Console.WriteLine($"hostCounterMedian cycles={c[c.Length / 2]} instructions={i[i.Length / 2]} samples={samples.Count}");
    }

    public void Dispose() { Close(instructions); Close(cycles); }
}
