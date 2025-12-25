using System;

namespace EutherDrive.Audio;

public static class TestToneGenerator
{
    public static void FillSine(int sampleRate, double frequency, int frames, int channels, ref double phase, Span<short> dest)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        if (frames <= 0)
            throw new ArgumentOutOfRangeException(nameof(frames));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));

        int sampleCount = frames * channels;
        if (dest.Length < sampleCount)
            throw new ArgumentException("Destination buffer is too small.", nameof(dest));

        var amplitude = short.MaxValue * 0.4;
        var phaseStep = 2.0 * Math.PI * frequency / sampleRate;

        for (var sample = 0; sample < frames; sample++)
        {
            var value = (short)(Math.Sin(phase) * amplitude);
            for (var channel = 0; channel < channels; channel++)
            {
                dest[sample * channels + channel] = value;
            }

            phase += phaseStep;
            if (phase >= 2.0 * Math.PI)
                phase -= 2.0 * Math.PI;
        }
    }

    public static short[] GenerateSine(int sampleRate, double frequency, double durationSeconds, int channels = 2)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));

        var sampleCount = (int)Math.Round(sampleRate * durationSeconds);
        var output = new short[sampleCount * channels];
        var amplitude = short.MaxValue * 0.4;
        var phaseStep = 2.0 * Math.PI * frequency / sampleRate;
        var phase = 0.0;

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var value = (short)(Math.Sin(phase) * amplitude);
            for (var channel = 0; channel < channels; channel++)
            {
                output[sample * channels + channel] = value;
            }

            phase += phaseStep;
            if (phase >= 2.0 * Math.PI)
                phase -= 2.0 * Math.PI;
        }

        return output;
    }
}
