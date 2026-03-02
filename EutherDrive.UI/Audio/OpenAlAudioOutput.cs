using System;
using System.Runtime.InteropServices;
using EutherDrive.Audio;

namespace EutherDrive.UI.Audio;

internal sealed class OpenAlAudioOutput : IDisposable, IAudioSink
{
    private const string LibOpenAl = "openal";
    private const int AL_FORMAT_STEREO16 = 0x1103;
    private const int AL_SOURCE_STATE = 0x1010;
    private const int AL_PLAYING = 0x1012;
    private const int AL_BUFFERS_QUEUED = 0x1015;
    private const int AL_BUFFERS_PROCESSED = 0x1016;
    private const int AL_NO_ERROR = 0;

    private readonly IntPtr _device;
    private readonly IntPtr _context;
    private readonly int _source;
    private readonly int[] _buffers = new int[4]; // Increased from 2 to 4 for better buffering
    private readonly int[] _scratch = new int[1];
    private byte[] _tempBuffer = Array.Empty<byte>();
    private int _nextBuffer;
    private int _sampleRate;
    private int _channels = 2;
    private bool _started;

    public static OpenAlAudioOutput? TryCreate()
    {
        try
        {
            var device = alcOpenDevice(null);
            if (device == IntPtr.Zero)
            {
                Console.WriteLine("[OpenAlAudioOutput] alcOpenDevice failed");
                return null;
            }

            var context = alcCreateContext(device, IntPtr.Zero);
            if (context == IntPtr.Zero || !alcMakeContextCurrent(context))
            {
                Console.WriteLine("[OpenAlAudioOutput] alcCreateContext or make current failed");
                alcDestroyContext(context);
                alcCloseDevice(device);
                return null;
            }

            var sourceArray = new int[1];
            alGenSources(1, sourceArray);
            LogAlError("alGenSources");
            var buffers = new int[4];
            alGenBuffers(4, buffers);
            LogAlError("alGenBuffers");
            var output = new OpenAlAudioOutput(device, context, sourceArray[0], buffers);
            return output;
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[OpenAlAudioOutput] OpenAL library missing: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenAlAudioOutput] Failed to initialize: {ex.Message}");
            return null;
        }
    }

    private OpenAlAudioOutput(IntPtr device, IntPtr context, int source, int[] buffers)
    {
        _device = device;
        _context = context;
        _source = source;
        Array.Copy(buffers, _buffers, Math.Min(buffers.Length, _buffers.Length));
    }

    public void Start(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
        _started = true;
    }

    public void Submit(ReadOnlySpan<short> samples)
    {
        if (!_started || samples.Length == 0)
            return;

        // Reclaim processed buffers before queueing a new one.
        ProcessBuffers();
        alGetSourcei(_source, AL_BUFFERS_QUEUED, out int queued);
        if (queued >= _buffers.Length)
            return;

        ReadOnlySpan<short> span = samples;
        if (_channels < 2)
        {
            var expanded = new short[samples.Length * 2];
            for (int i = 0, j = 0; i < samples.Length; i++, j += 2)
            {
                var sample = samples[i];
                expanded[j + 0] = sample;
                expanded[j + 1] = sample;
            }
            span = expanded;
        }

        int bytes = span.Length * sizeof(short);
        EnsureTempBuffer(bytes);

        unsafe
        {
            fixed (byte* dest = _tempBuffer)
            fixed (short* src = span)
            {
                Buffer.MemoryCopy(src, dest, bytes, bytes);
                alBufferData(_buffers[_nextBuffer], AL_FORMAT_STEREO16, (IntPtr)dest, bytes, _sampleRate);
                LogAlError("alBufferData");

                _scratch[0] = _buffers[_nextBuffer];
                alSourceQueueBuffers(_source, 1, _scratch);
                LogAlError("alSourceQueueBuffers");
                _nextBuffer = (_nextBuffer + 1) % _buffers.Length;
            }
        }

        alGetSourcei(_source, AL_SOURCE_STATE, out int state);
        if (state != AL_PLAYING)
        {
            alSourcePlay(_source);
            LogAlError("alSourcePlay");
        }
        else
        {
            LogAlError("alGetSourcei");
        }
    }

    public void Stop()
    {
        if (!_started)
            return;

        _started = false;
        alSourceStop(_source);
        ProcessBuffers();
    }

    private void EnsureTempBuffer(int bytes)
    {
        if (_tempBuffer.Length < bytes)
            _tempBuffer = new byte[bytes];
    }

    private void ProcessBuffers()
    {
        alGetSourcei(_source, AL_BUFFERS_PROCESSED, out int processed);
        while (processed-- > 0)
        {
            alSourceUnqueueBuffers(_source, 1, _scratch);
            LogAlError("alSourceUnqueueBuffers");
        }
    }

    public void Dispose()
    {
        Stop();
        alSourcei(_source, AL_BUFFER, 0);
        alDeleteSources(1, new[] { _source });

        alDeleteBuffers(4, _buffers);
        alcMakeContextCurrent(IntPtr.Zero);
        alcDestroyContext(_context);
        alcCloseDevice(_device);
    }

    private static void LogAlError(string context)
    {
        int error = alGetError();
        if (error != AL_NO_ERROR)
            Console.WriteLine($"[OpenAlAudioOutput] AL error after {context}: 0x{error:X4}");
    }

    [DllImport(LibOpenAl)]
    private static extern IntPtr alcOpenDevice(string? devicename);

    [DllImport(LibOpenAl)]
    private static extern IntPtr alcCreateContext(IntPtr device, IntPtr attrlist);

    [DllImport(LibOpenAl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool alcMakeContextCurrent(IntPtr context);

    [DllImport(LibOpenAl)]
    private static extern void alcDestroyContext(IntPtr context);

    [DllImport(LibOpenAl)]
    private static extern void alcCloseDevice(IntPtr device);

    [DllImport(LibOpenAl)]
    private static extern void alGenSources(int n, int[] sources);

    [DllImport(LibOpenAl)]
    private static extern void alGenBuffers(int n, int[] buffers);

    [DllImport(LibOpenAl)]
    private static extern void alBufferData(int buffer, int format, IntPtr data, int size, int freq);

    [DllImport(LibOpenAl)]
    private static extern void alSourceQueueBuffers(int source, int nb, int[] buffers);

    [DllImport(LibOpenAl)]
    private static extern void alSourceUnqueueBuffers(int source, int nb, int[] buffers);

    [DllImport(LibOpenAl)]
    private static extern void alSourcePlay(int source);

    [DllImport(LibOpenAl)]
    private static extern void alSourceStop(int source);

    [DllImport(LibOpenAl)]
    private static extern void alDeleteSources(int n, int[] sources);

    [DllImport(LibOpenAl)]
    private static extern void alDeleteBuffers(int n, int[] buffers);

    [DllImport(LibOpenAl)]
    private static extern void alGetSourcei(int source, int param, out int value);

    [DllImport(LibOpenAl)]
    private static extern void alSourcei(int source, int param, int value);

    [DllImport(LibOpenAl)]
    private static extern int alGetError();

    private const int AL_BUFFER = 0x1009;

}
