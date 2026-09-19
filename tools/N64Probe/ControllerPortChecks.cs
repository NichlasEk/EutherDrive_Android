using System.Reflection;
using Ryu64.MIPS;

internal static class ControllerPortChecks
{
    internal static void Run()
    {
        var memory = new Memory(new byte[4096]);
        var process = typeof(Memory).GetMethod("ProcessPifJoybusCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(memory);
        var pif = memory.PIFRAM;
        var pak = (byte[])typeof(Memory).GetField("_controllerPak", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
        memory.SetControllerState(0x9080, -64, 80);
        int cases = 0;
        foreach (int port in new[] { 0, 1, 2, 3 })
        foreach (var (cmd, tx, rx) in new (byte, int, int)[] { (0, 1, 3), (0xff, 1, 3), (1, 1, 4), (2, 3, 33), (3, 35, 1) })
        {
            Array.Clear(pif);
            pif[port] = (byte)tx;
            pif[port + 1] = (byte)(rx | 0x80); // Prior NoResponse must be cleared for an attached port.
            pif[port + 2] = cmd;
            int response = port + 2 + tx;
            pif[response + rx] = 0xfe;
            pif[63] = 1;
            if (cmd == 3) for (int i = 0; i < 32; i++) pif[port + 5 + i] = (byte)(0x80 + i);
            Array.Fill(pif, (byte)0x5a, response, rx);
            var pakBefore = (byte[])pak.Clone();
            process();
            if (port != 0)
            {
                if (pif[port + 1] != (rx | 0x80) || pif.AsSpan(response, rx).ContainsAnyExcept((byte)0x5a)
                    || !pakBefore.SequenceEqual(pak)) throw new Exception($"Phantom controller port {port + 1} responded/changed pak");
            }
            else
            {
                if (pif[port + 1] != rx) throw new Exception("Attached controller retained error flag");
                if ((cmd == 0 || cmd == 0xff) && !pif.AsSpan(response, 3).SequenceEqual(new byte[] { 5, 0, 1 })) throw new Exception("Wrong controller ID");
                if (cmd == 1 && !pif.AsSpan(response, 4).SequenceEqual(new byte[] { 0x90, 0x80, 0xc0, 80 })) throw new Exception("Wrong controller input");
                if (cmd == 2 && !pif.AsSpan(response, 32).SequenceEqual(pak.AsSpan(0, 32))) throw new Exception("Pak read changed");
                if (cmd == 3 && !pak.AsSpan(0, 32).SequenceEqual(Enumerable.Range(0x80, 32).Select(x => (byte)x).ToArray())) throw new Exception("Pak write failed");
            }
            if (pif[63] != 0) throw new Exception("PIF control handshake not cleared");
            cases++;
        }
        // Four controller status requests followed by cartridge EEPROM status.
        Array.Clear(pif);
        for (int port = 0; port < 5; port++)
        { int at = port * 6; pif[at] = 1; pif[at + 1] = 3; pif[at + 2] = 0; }
        pif[30] = 0xfe; process();
        if (pif[1] != 3 || pif[7] != 0x83 || pif[13] != 0x83 || pif[19] != 0x83
            || pif[25] != 3 || pif[27] != 0 || pif[28] != 0xc0)
            throw new Exception("Port absence broke subsequent EEPROM channel parsing");
        Console.WriteLine($"controllerPortCases={cases + 1} input=passed pakIsolation=passed channelParsing=passed");
    }
}
