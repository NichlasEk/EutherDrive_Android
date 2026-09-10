using System.Reflection;

// A ROM-free regression check. Bind delegates before measuring so reflection
// allocations are not confused with allocations in the disabled trace path.
internal static class VoodooPciTraceChecks
{
    private delegate bool ReadMemory(uint address, out uint value);

    internal static void Run(Assembly assembly)
    {
        const string traceVariable = "EUTHERDRIVE_GAUNTDL_TRACE_VOODOO";
        const string limitVariable = "EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_PCI_LIMIT";
        string? oldTrace = Environment.GetEnvironmentVariable(traceVariable);
        string? oldLimit = Environment.GetEnvironmentVariable(limitVariable);
        TextWriter oldOutput = Console.Out;
        try
        {
            Type type = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VegasVoodooPciDevice", true)!;
            object NewDevice()
            {
                object device = Activator.CreateInstance(type, nonPublic: true)!;
                type.GetMethod("Reset")!.Invoke(device, null);
                return device;
            }
            Environment.SetEnvironmentVariable(traceVariable, "0");
            object quiet = NewDevice();
            var config = type.GetMethod("ReadConfig32")!.CreateDelegate<Func<uint, uint>>(quiet);
            var write = type.GetMethod("TryWriteMemory32")!.CreateDelegate<Func<uint, uint, bool>>(quiet);
            var read = type.GetMethod("TryReadMemory32")!.CreateDelegate<ReadMemory>(quiet);
            void Exercise()
            {
                for (int i = 0; i < 10000; i++)
                {
                    _ = config(0);
                    if (!write(0xff400000, (uint)i) || !write(0xff800000, (uint)i) ||
                        !read(0xff400000, out _))
                        throw new InvalidOperationException("PCI BAR access was rejected");
                }
            }
            Exercise();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Exercise();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"pciTrace disabledCalls=40000 allocatedBytes={allocated}");
            if (allocated != 0)
                throw new InvalidOperationException("Disabled PCI tracing allocated memory");

            Environment.SetEnvironmentVariable(traceVariable, "1");
            Environment.SetEnvironmentVariable(limitVariable, "2");
            object traced = NewDevice();
            var tracedConfig = type.GetMethod("ReadConfig32")!.CreateDelegate<Func<uint, uint>>(traced);
            using var capture = new StringWriter();
            Console.SetOut(capture);
            _ = tracedConfig(0x40);
            _ = tracedConfig(0x40);
            _ = tracedConfig(0x40);
            Console.SetOut(oldOutput);
            string expected = "[GAUNTDL:VOODOO-PCI] pci cfg read off=40 value=00044000" + Environment.NewLine;
            if (capture.ToString() != expected + expected ||
                (int)type.GetField("_traceCount", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(traced)! != 3)
                throw new InvalidOperationException("Enabled PCI trace output or counter changed");
            Console.WriteLine("pciTrace enabled output/limit/counter PASS");
        }
        finally
        {
            Console.SetOut(oldOutput);
            Environment.SetEnvironmentVariable(traceVariable, oldTrace);
            Environment.SetEnvironmentVariable(limitVariable, oldLimit);
        }
    }
}
