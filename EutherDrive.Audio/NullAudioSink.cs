using System;

namespace EutherDrive.Audio;

public sealed class NullAudioSink : IAudioSink
{
    public void Start(int sampleRate, int channels)
    {
        // no-op
    }

    public void Submit(ReadOnlySpan<short> interleaved)
    {
        // no-op
    }

    public void Stop()
    {
        // no-op
    }

    public void Dispose()
    {
        // no-op
    }
}
