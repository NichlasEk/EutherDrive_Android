using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Run in separate processes with EUTHERDRIVE_TRACE_N64_RSP_FLOW=0 and =1.
// Compare both effects and exact debug text, not benchmark timings across ALCs.
internal static class RspScalarChecks
{
    internal static void Run(string? reference)
    {
        string actual = Check(typeof(Memory).Assembly);
        Console.WriteLine($"rspScalarCases=2048 trace={Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RSP_FLOW")} sha256={actual}");
        if (reference == null) return;
        var context = new AssemblyLoadContext("reference-rsp-scalar", isCollectible: true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        if (actual != expected) throw new Exception($"RSP scalar state/trace differs: {expected}");
        context.Unload();
        Console.WriteLine("rspScalarDifferential=passed");
    }

    private static string Check(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
        Type rspType = assembly.GetType("Ryu64.MIPS.RspInterpreter")!;
        object rsp = Activator.CreateInstance(rspType, new[] { memory })!;
        var gpr = (uint[])rspType.GetField("_gpr", flags)!.GetValue(rsp)!;
        var pc = rspType.GetField("_pc", flags)!;
        var writeGpr = rspType.GetMethod("WriteGpr", flags)!.CreateDelegate<Action<uint, uint>>(rsp);
        var readWord = rspType.GetMethod("ReadWord", flags)!.CreateDelegate<Func<uint, uint>>(rsp);
        var readHalf = rspType.GetMethod("ReadHalf", flags)!.CreateDelegate<Func<uint, ushort>>(rsp);
        var readByte = rspType.GetMethod("ReadByte", flags)!.CreateDelegate<Func<uint, byte>>(rsp);
        var writeWord = rspType.GetMethod("WriteWord", flags)!.CreateDelegate<Action<uint, uint>>(rsp);
        var writeHalf = rspType.GetMethod("WriteHalf", flags)!.CreateDelegate<Action<uint, ushort>>(rsp);
        var writeByte = rspType.GetMethod("WriteByte", flags)!.CreateDelegate<Action<uint, byte>>(rsp);
        var random = new Random(64919);
        random.NextBytes((byte[])memoryType.GetField("SP_MEM_RW")!.GetValue(memory)!);
        using var state = new MemoryStream();
        using var writer = new BinaryWriter(state);
        using var trace = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        TextWriter previous = Console.Out;
        try
        {
            Console.SetOut(trace);
            uint[] edges = { 0, 1, 2, 3, 0x2b0, 0x2d8, 0x400, 0x430, 0xffc, 0xffd, 0xffe, 0xfff, 0x1000, 0xffffffff };
            for (int i = 0; i < 2048; i++)
            {
                pc.SetValue(rsp, i % 3 == 0 ? 0x2d0u : i % 3 == 1 ? 0xfb0u : 0x100u);
                uint address = i % 2 == 0 ? edges[(i / 2) % edges.Length] : (uint)random.NextInt64(1L << 32);
                uint value = (uint)random.NextInt64(1L << 32);
                uint reg = (uint)i & 31;
                writeGpr(reg, value);
                writeGpr(reg, value); // unchanged-value dirty tracking
                if (gpr[0] != 0) throw new Exception("RSP zero register changed");
                writer.Write(readWord(address)); writer.Write(readHalf(address)); writer.Write(readByte(address));
                writeWord(address, value);
                writer.Write(readWord(address));
                writeHalf(address, (ushort)(value >> 16));
                writer.Write(readHalf(address));
                writeByte(address, (byte)value);
                writer.Write(readByte(address));
                writer.Write((bool)rspType.GetField("_progressRegistersDirty", flags)!.GetValue(rsp)!);
                rspType.GetField("_progressRegistersDirty", flags)!.SetValue(rsp, false);
            }
        }
        finally { Console.SetOut(previous); }
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RSP_FLOW") == "1" && trace.GetStringBuilder().Length == 0)
            throw new Exception("Enabled RSP flow tracing produced no text");
        foreach (uint value in gpr) writer.Write(value);
        memoryType.GetMethod("SaveState")!.Invoke(memory, new object[] { writer });
        writer.Write(trace.ToString());
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(state.GetBuffer().AsSpan(0, (int)state.Length)));
    }
}
