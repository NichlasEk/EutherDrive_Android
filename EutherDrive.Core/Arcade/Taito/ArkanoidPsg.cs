namespace EutherDrive.Core.Arcade.Taito;

// YM2149, 3 MHz with pin 26 low: 1.5 MHz internal clock. Mono output.
internal sealed class ArkanoidPsg
{
    private readonly byte[] registers = new byte[16];
    private readonly int[] toneCount = new int[3];
    private readonly bool[] toneHigh = new bool[3];
    private int selected, noiseCount, noise = 1, envelopeCount, envelopeStep, attack;
    private bool holding;
    private double ticks, previousInput, previousOutput;
    public void Reset()
    {
        Array.Clear(registers); Array.Clear(toneCount); Array.Clear(toneHigh);
        selected = noiseCount = envelopeCount = envelopeStep = attack = 0;
        noise = 1; holding = false; ticks = previousInput = previousOutput = 0;
    }
    public void Address(byte value) => selected = value & 15;
    public byte Read() => selected == 14 ? (byte)255 : selected == 15 ? (byte)254 : registers[selected];
    public void Write(byte value)
    {
        registers[selected] = (byte)(value & (selected is 1 or 3 or 5 or 13 ? 15 : selected is 6 or 8 or 9 or 10 ? 31 : 255));
        if (selected == 13) { envelopeStep = 31; attack = (value & 4) != 0 ? 31 : 0; holding = false; envelopeCount = 0; }
    }
    private void Tick()
    {
        for (int c = 0; c < 3; c++)
            if (++toneCount[c] >= Math.Max(1, registers[c * 2] | registers[c * 2 + 1] << 8))
            { toneCount[c] = 0; toneHigh[c] = !toneHigh[c]; }
        if (++noiseCount >= Math.Max(1, (int)registers[6]) * 2)
        {
            noiseCount = 0; noise = (noise >> 1) | (((noise ^ (noise >> 3)) & 1) << 16);
        }
        if (!holding && ++envelopeCount >= Math.Max(1, registers[11] | registers[12] << 8))
        {
            envelopeCount = 0;
            if (--envelopeStep < 0)
            {
                int shape = registers[13];
                if ((shape & 8) == 0) { holding = true; envelopeStep = 0; attack = 0; }
                else if ((shape & 1) != 0)
                { if ((shape & 2) != 0) attack ^= 31; holding = true; envelopeStep = 0; }
                else { if ((shape & 2) != 0) attack ^= 31; envelopeStep = 31; }
            }
        }
    }
    private static readonly double[] Levels = Enumerable.Range(0, 32)
        .Select(i => i == 0 ? 0.0 : Math.Pow(10, (i - 31) * 1.5 / 20)).ToArray();
    private double Mix()
    {
        double sum = 0;
        for (int c = 0; c < 3; c++)
            if ((toneHigh[c] || (registers[7] & (1 << c)) != 0) &&
                ((noise & 1) != 0 || (registers[7] & (8 << c)) != 0))
            {
                int v = registers[8 + c];
                sum += Levels[(v & 16) != 0 ? envelopeStep ^ attack : (v & 15) == 0 ? 0 : (v & 15) * 2 + 1];
            }
        return sum;
    }
    public short Sample(int volume, int sampleRate)
    {
        // Integrate all PSG ticks in each sample rather than aliasing a single endpoint.
        ticks += 1_500_000.0 / 8 / sampleRate;
        double sum = 0; int count = 0;
        while (ticks >= 1)
        {
            ticks--; Tick(); count++;
            sum += Mix();
        }
        double input = (count == 0 ? Mix() : sum / count) * 6500;
        double output = input - previousInput + 0.995 * previousOutput;
        previousInput = input; previousOutput = output;
        return (short)Math.Clamp(output * volume / 100, short.MinValue, short.MaxValue);
    }
}
