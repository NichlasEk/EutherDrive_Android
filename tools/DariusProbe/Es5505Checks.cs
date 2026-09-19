using System.Reflection;
using EutherDrive.Core.Arcade.Taito;

internal static class Es5505Checks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Type sound = typeof(DariusGaidenAdapter).Assembly.GetType("EutherDrive.Core.Arcade.Taito.TaitoF3SoundSystem")!;
        Type chip = sound.GetNestedType("TaitoF3Es5505", BindingFlags.NonPublic)!;
        var scale = chip.GetMethod("ScaleVoiceSample", flags)?.CreateDelegate<Func<int, byte, int>>()
            ?? throw new InvalidOperationException("ES5505 logarithmic volume scaling is missing");
        int checks = 0;
        for (int volume = 0; volume < 256; volume++)
        foreach (int sample in new[] { -98301, -32768, -17001, -1, 0, 1, 12345, 32767, 98301 })
        {
            // ES5505: 4-bit exponent and 4-bit mantissa. Convert the
            // reference chip's native 20-bit mixer scale, before PCM16 conversion.
            int gain = ((16 + volume % 16) * 2048) / (1 << (16 - volume / 16));
            int expected = (int)(((long)sample * gain) >> 11);
            if (scale(sample, (byte)volume) != expected)
                throw new InvalidOperationException($"Volume mismatch sample={sample} volume={volume:x2}");
            checks++;
        }
        int filterChecks = CheckFilters(chip);
        CheckBassResponse(chip);
        CheckInterruptReads(sound, chip);
        Console.WriteLine($"es5505Checks=passed volumeCases={checks} filterCases={filterChecks} bass60Hz=passed irqWordRead=passed pendingStoppedVoiceIrq=passed");
    }

    private static void CheckBassResponse(Type chip)
    {
        Type voiceType = chip.GetNestedType("Voice", BindingFlags.NonPublic)!;
        var apply = chip.GetMethod("ApplyFilters", BindingFlags.Static | BindingFlags.NonPublic)!;
        object voice = Activator.CreateInstance(voiceType, new object[] { 0 })!;
        voiceType.GetField("Control")!.SetValue(voice, (ushort)(2 << 10));
        voiceType.GetField("K1")!.SetValue(voice, (ushort)0x8000);
        voiceType.GetField("K2")!.SetValue(voice, (ushort)0x8000);
        double inputEnergy = 0, outputEnergy = 0;
        for (int i = 0; i < 44100; i++)
        {
            int input = (int)(10000 * Math.Sin(2 * Math.PI * 60 * i / 44100));
            object[] args = [voice, input];
            apply.Invoke(null, args);
            if (i < 4410) continue; // Discard the filter's startup transient.
            inputEnergy += (double)input * input;
            outputEnergy += (double)(int)args[1] * (int)args[1];
        }
        double gain = Math.Sqrt(outputEnergy / inputEnergy);
        // Four lowpass poles at this cutoff must preserve a 60 Hz bass tone.
        // The old erroneous highpass third pole almost eliminated it.
        if (gain < 0.98 || gain > 1.01)
            throw new InvalidOperationException($"LP4-only mode lost bass: 60 Hz gain={gain:F6}");
        Console.WriteLine($"es5505Bass60HzGain={gain:F6}");
    }

    private static int CheckFilters(Type chip)
    {
        Type voiceType = chip.GetNestedType("Voice", BindingFlags.NonPublic)!;
        var apply = chip.GetMethod("ApplyFilters", BindingFlags.Static | BindingFlags.NonPublic)!;
        int checks = 0;
        foreach (int mode in Enumerable.Range(0, 4))
        foreach (ushort k1 in new ushort[] { 0, 0x4560, 0xfff0 })
        foreach (ushort k2 in new ushort[] { 0, 0x8120, 0xfff0 })
        foreach (int input in new[] { -2000, -1, 0, 1, 2000 })
        {
            object voice = Activator.CreateInstance(voiceType, new object[] { 0 })!;
            void Set(string name, object value) => voiceType.GetField(name)!.SetValue(voice, value);
            Set("Control", (ushort)(mode << 10)); Set("K1", k1); Set("K2", k2);
            Set("O1", (short)-11); Set("O2", (short)17); Set("O3", (short)-29); Set("O4", (short)41);
            static int Low(int sample, int k, int previous) => previous + (k / 16 * (sample - previous)) / 4096;
            static int High(int sample, int k, int previous, int priorPole) => sample - priorPole + (k / 16 * previous) / 8192 + previous / 2;
            int p1 = Low(input, k1, -11), p2 = Low(p1, k1, 17);
            int p3 = mode switch { 0 => High(p2, k2, -29, 17), 2 => Low(p2, k2, -29), _ => Low(p2, k1, -29) };
            int p4 = mode < 2 ? High(p3, k2, 41, -29) : Low(p3, k2, 41);
            object[] args = [voice, input];
            apply.Invoke(null, args);
            if ((int)args[1] != p4 || (short)voiceType.GetField("O3")!.GetValue(voice)! != p3)
                throw new InvalidOperationException($"Filter mismatch mode={mode} k1={k1:x4} k2={k2:x4} input={input}");
            checks++;
        }
        return checks;
    }

    private static void CheckInterruptReads(Type sound, Type chipType)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        object chip = Activator.CreateInstance(chipType, nonPublic: true)!;
        Type busType = sound.GetNestedType("TaitoF3SoundBus", BindingFlags.NonPublic)!;
        object bus = Activator.CreateInstance(busType, nonPublic: true)!;
        busType.GetField("_otis", fields)!.SetValue(bus, chip);
        var read = busType.GetMethod("ReadWord")!.CreateDelegate<Func<uint, ushort>>(bus);
        var irq = chipType.GetField("_irqv", fields)!;
        for (byte voice = 0; voice < 32; voice++)
        {
            irq.SetValue(chip, voice);
            if (read(0x20001c) != voice || read(0x20001c) != 0x80)
                throw new InvalidOperationException($"IRQV must acknowledge once per word: voice={voice}");
        }
        if ((int)busType.GetProperty("Es5505Reads")!.GetValue(bus)! != 64)
            throw new InvalidOperationException("Word reads must be single ES5505 transactions");
        Array voices = (Array)chipType.GetField("_voices", fields)!.GetValue(chip)!;
        chipType.GetField("_activeVoices", fields)!.SetValue(chip, (byte)1);
        foreach (int index in new[] { 0, 1 })
        {
            object voice = voices.GetValue(index)!;
            voice.GetType().GetField("Control")!.SetValue(voice, (ushort)0x0083);
        }
        var render = chipType.GetMethod("RenderStereo")!.CreateDelegate<Action<short[], int>>(chip);
        render(new short[2], 1);
        if (read(0x20001c) != 0) throw new InvalidOperationException("First stopped voice IRQ lost");
        render(new short[2], 1);
        if (read(0x20001c) != 1) throw new InvalidOperationException("Queued stopped voice IRQ lost");
    }
}
