using EutherDrive.Core.Cpu.M68000Emu;

internal static class TraceSwitchChecks
{
    internal static void Run()
    {
        bool stackTrace = Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_STACK") == "1";
        bool jumpTrace = Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_JUMP") == "1";
        var bus = new Bus();
        bus.WriteLong(0, 0x2000);
        bus.WriteLong(4, 0x100);
        bus.WriteWord(0x100, 0x4ef9); // JMP to high address, exercises jump trace.
        bus.WriteLong(0x102, 0xffff0100);
        bus.WriteWord(0xff0100, 0x4eb9); // JSR: high return PC triggers push trace.
        bus.WriteLong(0xff0102, 0x200);
        bus.WriteWord(0x200, 0x4e50); // LINK A0,#0 then UNLK A0 exercise pop.
        bus.WriteWord(0x202, 0);
        bus.WriteWord(0x204, 0x4e58);
        bus.WriteWord(0x206, 0x4e75); // RTS has an existing direct fast path.
        bus.WriteWord(0xff0106, 0x4e71);
        var cpu = M68000.CreateBuilder().Name("trace-check").Build();
        cpu.Reset(bus);
        cpu.SetAddressRegister(0, 0xffff2233);
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            for (int i = 0; i < 5; i++) cpu.ExecuteInstruction(bus);
        }
        finally { Console.SetOut(previous); }
        string trace = output.ToString();
        if (cpu.Pc != 0xff0106 || cpu.Ssp != 0x2000 || cpu.IsFrozen || cpu.NextOpcode != 0x4e71 ||
            bus.ReadLong(0x1ffc) != 0xff0106 || cpu.AddressRegister(0) != 0xffff2233 ||
            trace.Contains("push value=") != stackTrace ||
            trace.Contains("pop value=") != stackTrace ||
            trace.Contains("[M68K-JUMP]") != jumpTrace)
            throw new InvalidOperationException($"M68K trace/stack mismatch pc={cpu.Pc:x8} sp={cpu.Ssp:x8}: {trace}");
        Console.WriteLine($"m68kTraceChecks=passed stack={stackTrace} jump={jumpTrace}");
    }

    private sealed class Bus : IBusInterface
    {
        private readonly Dictionary<uint, byte> _bytes = new();
        public byte ReadByte(uint address) => _bytes.GetValueOrDefault(address & 0xffffff);
        public ushort ReadWord(uint address) => (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
        public uint ReadLong(uint address) => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);
        public void WriteByte(uint address, byte value) => _bytes[address & 0xffffff] = value;
        public void WriteWord(uint address, ushort value) { WriteByte(address, (byte)(value >> 8)); WriteByte(address + 1, (byte)value); }
        public void WriteLong(uint address, uint value) { WriteWord(address, (ushort)(value >> 16)); WriteWord(address + 2, (ushort)value); }
        public byte InterruptLevel() => 0;
        public void AcknowledgeInterrupt(byte level) { }
        public bool Reset() => false;
        public bool Halt() => false;
        public BusSignals Signals => new(false);
        public ushort CurrentOpcode => 0;
    }
}
