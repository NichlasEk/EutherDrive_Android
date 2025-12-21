namespace EutherDrive.Core;

public interface IEmulatorCore
{
    void LoadRom(string path);
    void Reset();

    /// Kör en hel bildruta (≈60 Hz) och uppdaterar intern framebuffer/ljudbuffert.
    void RunFrame();

    /// Returnerar pekare till intern BGRA32-buffer.
    ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride);

    /// 16-bit PCM (börja med tom buffert i dummy).
    ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels);

    /// Enkel input
    void SetInputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start);
}
