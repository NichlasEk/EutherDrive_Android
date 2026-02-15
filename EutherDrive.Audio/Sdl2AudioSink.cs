using System;
using System.Runtime.InteropServices;

namespace EutherDrive.Audio;

public sealed class Sdl2AudioSink : IAudioSink
{
    private const string LibSdl2 = "SDL2";
    private const uint SdlInitAudio = 0x00000010;
    private const ushort AudioS16Sys = 0x8010; // AUDIO_S16SYS

    private static readonly object InitLock = new();
    private static int _initCount;

    private uint _deviceId;
    private int _sampleRate;
    private int _channels = 2;
    private bool _started;
    private byte[] _tempBuffer = Array.Empty<byte>();
    private NullAudioSink? _nullSink;
    private bool _logOverflow;

    public static Sdl2AudioSink? TryCreate()
    {
        try
        {
            EnsureSdlInit();
            return new Sdl2AudioSink();
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[Sdl2AudioSink] SDL2 library missing: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sdl2AudioSink] Failed to initialize: {ex.Message}");
            return null;
        }
    }

    public void Start(int sampleRate, int channels)
    {
        if (_started)
            return;

        _sampleRate = sampleRate;
        _channels = Math.Clamp(channels, 1, 2);
        if (!TryOpenDevice())
        {
            Console.WriteLine("[Sdl2AudioSink] Falling back to NullAudioSink.");
            _nullSink ??= new NullAudioSink();
            _nullSink.Start(sampleRate, channels);
            _started = true;
            return;
        }

        SDL_PauseAudioDevice(_deviceId, 0);
        _started = true;
    }

    public void Submit(ReadOnlySpan<short> interleaved)
    {
        if (!_started || interleaved.Length == 0)
            return;

        if (_nullSink != null)
        {
            _nullSink.Submit(interleaved);
            return;
        }

        ReadOnlySpan<short> span = interleaved;
        if (_channels < 2)
        {
            var expanded = new short[interleaved.Length * 2];
            for (int i = 0, j = 0; i < interleaved.Length; i++, j += 2)
            {
                short sample = interleaved[i];
                expanded[j] = sample;
                expanded[j + 1] = sample;
            }
            span = expanded;
        }

        int bytes = span.Length * sizeof(short);
        EnsureTempBuffer(bytes);
        Buffer.BlockCopy(span.ToArray(), 0, _tempBuffer, 0, bytes);

        uint queuedBytes = SDL_GetQueuedAudioSize(_deviceId);
        uint maxQueued = (uint)(_sampleRate * Math.Max(1, _channels) * sizeof(short) / 5); // ~200ms
        if (queuedBytes > maxQueued)
        {
            if (!_logOverflow)
            {
                Console.WriteLine($"[Sdl2AudioSink] queue overflow (queued={queuedBytes} bytes), dropping.");
                _logOverflow = true;
            }
            return;
        }

        GCHandle handle = GCHandle.Alloc(_tempBuffer, GCHandleType.Pinned);
        int rc;
        try
        {
            rc = SDL_QueueAudio(_deviceId, handle.AddrOfPinnedObject(), (uint)bytes);
        }
        finally
        {
            handle.Free();
        }
        if (rc != 0)
        {
            Console.WriteLine($"[Sdl2AudioSink] SDL_QueueAudio failed: {SDL_GetErrorString()}");
        }
    }

    public void Stop()
    {
        if (!_started)
            return;

        if (_nullSink != null)
        {
            _nullSink.Stop();
            return;
        }

        SDL_ClearQueuedAudio(_deviceId);
        SDL_PauseAudioDevice(_deviceId, 1);
        _started = false;
    }

    public void Dispose()
    {
        Stop();
        _nullSink?.Dispose();
        _nullSink = null;
        if (_deviceId != 0)
        {
            SDL_CloseAudioDevice(_deviceId);
            _deviceId = 0;
        }
        ReleaseSdl();
    }

    private bool TryOpenDevice()
    {
        SDL_AudioSpec desired = new SDL_AudioSpec
        {
            freq = _sampleRate,
            format = AudioS16Sys,
            channels = (byte)_channels,
            samples = 1024,
            callback = IntPtr.Zero,
            userdata = IntPtr.Zero
        };

        SDL_AudioSpec obtained;
        _deviceId = SDL_OpenAudioDevice(null, 0, ref desired, out obtained, 0);
        if (_deviceId == 0)
        {
            Console.WriteLine($"[Sdl2AudioSink] SDL_OpenAudioDevice failed: {SDL_GetErrorString()}");
            return false;
        }

        if (obtained.format != AudioS16Sys)
        {
            Console.WriteLine($"[Sdl2AudioSink] Unsupported format: 0x{obtained.format:X4}");
            SDL_CloseAudioDevice(_deviceId);
            _deviceId = 0;
            return false;
        }

        if (obtained.channels != _channels)
        {
            Console.WriteLine($"[Sdl2AudioSink] Channel mismatch: requested={_channels} obtained={obtained.channels}");
            _channels = obtained.channels;
        }

        if (obtained.freq != _sampleRate)
        {
            Console.WriteLine($"[Sdl2AudioSink] Sample rate mismatch: requested={_sampleRate} obtained={obtained.freq}");
            _sampleRate = obtained.freq;
        }

        return true;
    }

    private void EnsureTempBuffer(int bytes)
    {
        if (_tempBuffer.Length < bytes)
            _tempBuffer = new byte[bytes];
    }

    private static void EnsureSdlInit()
    {
        lock (InitLock)
        {
            if (_initCount == 0)
            {
                if (SDL_Init(SdlInitAudio) != 0)
                {
                    throw new InvalidOperationException($"SDL_Init failed: {SDL_GetErrorString()}");
                }
            }
            _initCount++;
        }
    }

    private static void ReleaseSdl()
    {
        lock (InitLock)
        {
            if (_initCount == 0)
                return;
            _initCount--;
            if (_initCount == 0)
            {
                SDL_QuitSubSystem(SdlInitAudio);
            }
        }
    }

    private static string SDL_GetErrorString()
    {
        IntPtr ptr = SDL_GetError();
        return ptr == IntPtr.Zero ? "<no error>" : Marshal.PtrToStringUTF8(ptr) ?? "<unknown>";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SDL_AudioSpec
    {
        public int freq;
        public ushort format;
        public byte channels;
        public byte silence;
        public ushort samples;
        public uint size;
        public IntPtr callback;
        public IntPtr userdata;
    }

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_Init(uint flags);

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetError();

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_OpenAudioDevice(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? device,
        int iscapture,
        ref SDL_AudioSpec desired,
        out SDL_AudioSpec obtained,
        int allowed_changes);

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_CloseAudioDevice(uint device);

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_PauseAudioDevice(uint device, int pause_on);

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_QueueAudio(uint device, IntPtr data, uint len);

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetQueuedAudioSize(uint device);

    [DllImport(LibSdl2, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_ClearQueuedAudio(uint device);
}
