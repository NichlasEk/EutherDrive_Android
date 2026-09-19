using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class CpuDiagnosticsChecks
{
    internal static void Run(string reference)
    {
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SM64_DISPATCH_WINDOW") != "1")
            throw new ArgumentException("Run with EUTHERDRIVE_TRACE_N64_SM64_DISPATCH_WINDOW=1");
        var context = new AssemblyLoadContext("reference-cpu-diagnostics", isCollectible: true);
        try
        {
            var expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
            var actual = Check(typeof(Memory).Assembly);
            if (actual != expected) throw new Exception("CPU diagnostic output or full state differs from reference");
            Console.WriteLine($"CPU diagnostics: trace boundaries, debug on/off and unsupported instructions match; fullStateSha256={actual.Hash}");
        }
        finally { context.Unload(); }
    }

    private static (string Text, string Hash) Check(Assembly assembly)
    {
        Type cpu = assembly.GetType("Ryu64.MIPS.R4300")!;
        Type memory = assembly.GetType("Ryu64.MIPS.Memory")!;
        cpu.GetField("memory")!.SetValue(null, Activator.CreateInstance(memory, new object[] { new byte[4096] }));
        assembly.GetType("Ryu64.MIPS.OpcodeTable")!.GetMethod("Init")!.Invoke(null, null);
        var pc = assembly.GetType("Ryu64.MIPS.Registers+R4300")!.GetField("PC")!;
        var interpret = cpu.GetMethod("InterpretOpcode")!.CreateDelegate<Action<uint>>();
        var save = cpu.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>();
        TextWriter previous = Console.Out;
        bool previousDebug = Ryu64.Common.Variables.Debug;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            foreach (bool debug in new[] { false, true })
            {
                Ryu64.Common.Variables.Debug = debug;
                foreach (uint address in new uint[] { 0x80322e0c, 0x80322e10, 0x80322e20, 0x80322e24 })
                {
                    pc.SetValue(null, address);
                    foreach (uint instruction in new uint[] { 0, 0x1000ffff, 0x24420001, 0x00431826, 0x34645678, 0x00042880 })
                        interpret(instruction);
                }
                foreach (uint instruction in new uint[] { 0x70000000, 0x00200000 })
                {
                    try { interpret(instruction); }
                    catch (NotImplementedException ex) { Console.WriteLine(ex.GetType().FullName + ": " + ex.Message); continue; }
                    throw new Exception($"Unsupported opcode unexpectedly accepted: {instruction:x8}");
                }
            }
        }
        finally
        {
            Console.SetOut(previous);
            Ryu64.Common.Variables.Debug = previousDebug;
        }
        string text = captured.ToString();
        if (!text.Contains("[N64DISPATCH]") || !text.Contains("0x80322e0c:"))
            throw new Exception("Diagnostic paths were not exercised");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        save(writer);
        writer.Flush();
        return (text, Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length))));
    }
}
