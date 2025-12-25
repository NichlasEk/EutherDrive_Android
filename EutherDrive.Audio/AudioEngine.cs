using System;
using System.Threading;

namespace EutherDrive.Audio;

public sealed class AudioEngine : IDisposable
{
    private readonly IAudioSink _sink;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly short[] _ring;
    private readonly short[] _batch;
    private readonly object _lock = new();
    private readonly AutoResetEvent _dataEvent = new(false);
    private Thread? _thread;
    private bool _running;
    private int _readIndex;
    private int _writeIndex;
    private int _count;
    private long _droppedSamples;

    public AudioEngine(IAudioSink sink, int sampleRate, int channels, int framesPerBatch = 1024, int bufferFrames = 8192)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (framesPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerBatch));
        if (bufferFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferFrames));

        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _sampleRate = sampleRate;
        _channels = channels;

        _ring = new short[bufferFrames * channels];
        _batch = new short[framesPerBatch * channels];
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running)
            return;

        _sink.Start(_sampleRate, _channels);
        _running = true;
        _thread = new Thread(DrainLoop) { IsBackground = true, Name = "AudioEngine" };
        _thread.Start();
    }

    public void Submit(ReadOnlySpan<short> interleaved)
    {
        if (!_running || interleaved.IsEmpty)
            return;

        int toWrite;
        int dropped = 0;

        lock (_lock)
        {
            int available = _ring.Length - _count;
            toWrite = interleaved.Length <= available ? interleaved.Length : available;
            if (toWrite == 0)
            {
                dropped = interleaved.Length;
            }
            else
            {
                int first = Math.Min(toWrite, _ring.Length - _writeIndex);
                interleaved.Slice(0, first).CopyTo(_ring.AsSpan(_writeIndex));
                _writeIndex = (_writeIndex + first) % _ring.Length;
                int remaining = toWrite - first;
                if (remaining > 0)
                {
                    interleaved.Slice(first, remaining).CopyTo(_ring.AsSpan(0));
                    _writeIndex = remaining;
                }

                _count += toWrite;
                dropped = interleaved.Length - toWrite;
            }
        }

        if (toWrite > 0)
            _dataEvent.Set();

        if (dropped > 0)
        {
            long total = Interlocked.Add(ref _droppedSamples, dropped);
            if (total == dropped || total % 4096 == 0)
                Console.WriteLine($"[AudioEngine] dropped {dropped} samples (total={total}).");
        }
    }

    public void Stop()
    {
        if (!_running)
            return;

        _running = false;
        _dataEvent.Set();
        _thread?.Join(500);
        _sink.Stop();
    }

    private void DrainLoop()
    {
        while (_running)
        {
            int toRead = 0;

            lock (_lock)
            {
                if (_count > 0)
                {
                    toRead = Math.Min(_count, _batch.Length);
                    int first = Math.Min(toRead, _ring.Length - _readIndex);
                    _ring.AsSpan(_readIndex, first).CopyTo(_batch);
                    _readIndex = (_readIndex + first) % _ring.Length;
                    int remaining = toRead - first;
                    if (remaining > 0)
                    {
                        _ring.AsSpan(0, remaining).CopyTo(_batch.AsSpan(first));
                        _readIndex = remaining;
                    }

                    _count -= toRead;
                }
            }

            if (toRead > 0)
            {
                _sink.Submit(_batch.AsSpan(0, toRead));
            }
            else
            {
                _dataEvent.WaitOne(10);
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
