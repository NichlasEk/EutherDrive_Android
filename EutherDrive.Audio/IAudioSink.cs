using System;

namespace EutherDrive.Audio;

public interface IAudioSink : IDisposable
{
    void Start(int sampleRate, int channels);
    void Submit(ReadOnlySpan<short> interleaved);
    void Stop();
}
