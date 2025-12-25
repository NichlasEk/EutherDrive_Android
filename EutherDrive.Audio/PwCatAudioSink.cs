using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;

namespace EutherDrive.Audio;

public sealed class PwCatAudioSink : IAudioSink, IDisposable
{
    private const int QueueCapacity = 16;
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private Process? _process;
    private Thread? _writerThread;
    private Thread? _stderrThread;
    private Thread? _stdoutThread;
    private bool _disposed;
    private long _overflows;
    private bool _fallback;
    private NullAudioSink? _nullSink;

    public bool IsFallback => _fallback;

    public void Start(int sampleRate, int channels)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PwCatAudioSink));

        var psi = new ProcessStartInfo("pw-cat")
        {
            Arguments = $"--playback --raw --rate {sampleRate} --channels {channels} --format s16 -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            _process = Process.Start(psi);
            if (_process == null)
                throw new InvalidOperationException("pw-cat process failed to start.");
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is InvalidOperationException)
        {
            Console.WriteLine("[PwCatAudioSink] pw-cat not available, falling back to NullAudioSink.");
            _fallback = true;
            _nullSink ??= new NullAudioSink();
            _nullSink.Start(sampleRate, channels);
            return;
        }

        Console.WriteLine("[PwCatAudioSink] Starting pw-cat session.");
        _stderrThread = new Thread(() => DrainProcessStream(_process.StandardError, "stderr"))
        {
            IsBackground = true,
            Name = "PwCatAudioStderr"
        };
        _stderrThread.Start();

        _stdoutThread = new Thread(() => DrainProcessStream(_process.StandardOutput, "stdout"))
        {
            IsBackground = true,
            Name = "PwCatAudioStdout"
        };
        _stdoutThread.Start();

        _writerThread = new Thread(WriterThread) { IsBackground = true, Name = "PwCatAudioWriter" };
        _writerThread.Start();

        if (_process.WaitForExit(100))
        {
            Console.WriteLine($"[PwCatAudioSink] pw-cat exited early (code={_process.ExitCode}); falling back to NullAudioSink.");
            _fallback = true;
            _nullSink ??= new NullAudioSink();
            _nullSink.Start(sampleRate, channels);
            return;
        }
    }

    public void Submit(ReadOnlySpan<short> interleaved)
    {
        if (_fallback)
        {
            _nullSink?.Submit(interleaved);
            return;
        }

        if (_process == null || _process.HasExited)
            return;

        var bytes = new byte[interleaved.Length * sizeof(short)];
        MemoryMarshal.Cast<short, byte>(interleaved).CopyTo(bytes);

        if (!_channel.Writer.TryWrite(bytes))
        {
            Interlocked.Increment(ref _overflows);
#if DEBUG
            Console.WriteLine($"[PwCatAudioSink] overflow count={_overflows}");
#endif
        }
    }

    public void Stop()
    {
        if (_fallback)
        {
            _nullSink?.Stop();
            return;
        }

        if (_process == null)
            return;

        _channel.Writer.TryComplete();

        _writerThread?.Join(1000);
        _stderrThread?.Join(1000);
        _stdoutThread?.Join(1000);

        try
        {
            _process.StandardInput?.Close();
            _process.WaitForExit(1000);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PwCatAudioSink] stop error: " + ex.Message);
        }

        Console.WriteLine($"[PwCatAudioSink] stopped (overflows={_overflows}).");
    }

    private void WriterThread()
    {
        if (_process?.StandardInput == null)
            return;

        var stdin = _process.StandardInput.BaseStream;

        while (_channel.Reader.WaitToReadAsync().AsTask().Result)
        {
            while (_channel.Reader.TryRead(out var chunk))
            {
                if (_process.HasExited)
                {
                    Console.WriteLine($"[PwCatAudioSink] pw-cat exited (code={_process.ExitCode}).");
                    return;
                }
                try
                {
                    stdin.Write(chunk, 0, chunk.Length);
                    stdin.Flush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PwCatAudioSink] write error: {ex.Message}");
                    return;
                }
            }
        }
    }

    private static void DrainProcessStream(StreamReader reader, string tag)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                    continue;
                Console.WriteLine($"[PwCatAudioSink][{tag}] {line}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PwCatAudioSink] {tag} read error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
