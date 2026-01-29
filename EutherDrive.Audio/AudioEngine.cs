using System;
using System.Diagnostics;
using System.Threading;

namespace EutherDrive.Audio;

public sealed class AudioEngine : IDisposable
{
    public delegate ReadOnlySpan<short> AudioProducer(int frames);

    private static readonly bool TraceStats =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_STATS"), "1", StringComparison.Ordinal);

    private readonly IAudioSink _sink;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _framesPerBatch;
    private readonly int _bufferFrames;
    private readonly short[] _ring;
    private readonly short[] _batch;
    private AudioProducer? _producer;
    private int _targetBufferedFrames;
    private int _maxProduceFrames;
    private readonly object _lock = new();
    private readonly AutoResetEvent _dataEvent = new(false);
    private Thread? _thread;
    private bool _running;
    private int _readIndex;
    private int _writeIndex;
    private int _count;
    private long _droppedSamples;
    private long _producedFramesTotal;
    private long _consumedFramesTotal;
    private long _droppedFramesTotal;
    private long _underrunEventsTotal;
    private long _underrunFramesTotal;
    private long _drainBatchesTotal;
    private long _drainBatchFramesTotal;
    private long _audioGenTicksTotal;
    private long _audioGenFramesTotal;
    private long _timedTicksTotal;
    private long _frameTicksTotal;
    private long _framesPerTickSum;
    private int _framesPerTickMin = int.MaxValue;
    private int _framesPerTickMax;
    private long _timedClampCount;
    private int _currentBufferedFrames;
    private int _maxBufferedFrames;
    private int _minBufferedFrames = int.MaxValue;
    private long _statsStartTicks;
    private long _lastStatsTicks;
    private long _lastProducedFrames;
    private long _lastConsumedFrames;
    private long _lastDroppedFrames;
    private long _lastUnderrunEvents;
    private long _lastUnderrunFrames;
    private long _lastGenTicks;
    private long _lastGenFrames;
    private long _lastDrainBatches;
    private long _lastDrainBatchFrames;
    private long _pullLastTicks;
    private double _pullFrameAccumulator;
    private long _drainNextTicks;
    private double _drainTicksPerFrame;
    private long _primingUntilTicks;
    private static readonly bool TimedDrainEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_TIMED_DRAIN"), "0", StringComparison.Ordinal)
            ? false
            : true;

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
        _framesPerBatch = framesPerBatch;
        _bufferFrames = bufferFrames;
        _drainTicksPerFrame = Stopwatch.Frequency / (double)_sampleRate;

        _ring = new short[bufferFrames * channels];
        _batch = new short[framesPerBatch * channels];

    }

    public bool IsRunning => _running;
    public bool IsPullMode => _producer != null;
    public int BufferedFrames => Volatile.Read(ref _currentBufferedFrames);

    public void Start()
    {
        if (_running)
            return;

        ResetStats();
        _sink.Start(_sampleRate, _channels);
        _drainNextTicks = 0;
        _primingUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.5);
        _running = true;
        _thread = new Thread(DrainLoop) { IsBackground = true, Name = "AudioEngine" };
        _thread.Start();
    }

    public void EnablePullMode(AudioProducer producer, int? targetBufferedFrames = null, int? maxFramesPerPull = null)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        int target = targetBufferedFrames ?? Math.Max(_framesPerBatch, _bufferFrames - _framesPerBatch);
        _targetBufferedFrames = Math.Min(_bufferFrames, Math.Max(_framesPerBatch, target));
        _maxProduceFrames = Math.Max(1, maxFramesPerPull ?? _framesPerBatch);
        _pullLastTicks = 0;
        _pullFrameAccumulator = 0;
    }

    public void DisablePullMode()
    {
        _producer = null;
    }

    public void SetTargetBufferedFrames(int targetFrames)
    {
        if (targetFrames <= 0)
            return;
        _targetBufferedFrames = Math.Min(_bufferFrames, Math.Max(_framesPerBatch, targetFrames));
    }

    public void Submit(ReadOnlySpan<short> interleaved)
    {
        if (!_running || interleaved.IsEmpty)
            return;

        int toWrite;
        int dropped = 0;
        int currentFrames = 0;

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
                currentFrames = _count / _channels;
                dropped = interleaved.Length - toWrite;
            }
        }

        if (toWrite > 0)
            _dataEvent.Set();

        if (toWrite > 0)
        {
            long frames = toWrite / _channels;
            Interlocked.Add(ref _producedFramesTotal, frames);
            UpdateBufferedStats(currentFrames);
        }

        if (dropped > 0)
        {
            long total = Interlocked.Add(ref _droppedSamples, dropped);
            Interlocked.Add(ref _droppedFramesTotal, dropped / _channels);
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
            TryFillFromProducer();

            if (TimedDrainEnabled && _targetBufferedFrames > 0 && _primingUntilTicks != 0)
            {
                int primingBuffered = Volatile.Read(ref _currentBufferedFrames);
                if (primingBuffered < _targetBufferedFrames)
                {
                    if (Stopwatch.GetTimestamp() < _primingUntilTicks)
                    {
                        _dataEvent.WaitOne(5);
                        continue;
                    }
                }
                _primingUntilTicks = 0;
            }

            int toRead = 0;
            int currentFrames = 0;

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
                    currentFrames = _count / _channels;
                }
            }

            if (toRead > 0)
            {
                _sink.Submit(_batch.AsSpan(0, toRead));
                int frames = toRead / _channels;
                Interlocked.Add(ref _consumedFramesTotal, frames);
                Interlocked.Increment(ref _drainBatchesTotal);
                Interlocked.Add(ref _drainBatchFramesTotal, frames);
                UpdateBufferedStats(currentFrames);
                if (TimedDrainEnabled)
                    PaceDrain(frames);
            }
            else
            {
                int missingFrames = _batch.Length / _channels;
                Interlocked.Increment(ref _underrunEventsTotal);
                Interlocked.Add(ref _underrunFramesTotal, missingFrames);
                if (TimedDrainEnabled)
                {
                    PaceDrain(_framesPerBatch);
                }
                else
                {
                    _dataEvent.WaitOne(10);
                }
            }

            if (TraceStats)
                MaybeLogStats();
        }
    }

    private void PaceDrain(int frames)
    {
        if (frames <= 0)
            return;

        long now = Stopwatch.GetTimestamp();
        if (_drainNextTicks == 0 || now - _drainNextTicks > Stopwatch.Frequency)
            _drainNextTicks = now;

        _drainNextTicks += (long)(frames * _drainTicksPerFrame);
        long waitTicks = _drainNextTicks - Stopwatch.GetTimestamp();
        if (waitTicks <= 0)
            return;

        int sleepMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
        if (sleepMs > 1)
        {
            Thread.Sleep(sleepMs - 1);
        }
        while (Stopwatch.GetTimestamp() < _drainNextTicks)
        {
            Thread.Sleep(0);
        }
    }

    private void TryFillFromProducer()
    {
        var producer = _producer;
        if (producer == null)
            return;

        long now = Stopwatch.GetTimestamp();
        if (_pullLastTicks == 0)
        {
            _pullLastTicks = now;
            return;
        }

        double elapsed = (now - _pullLastTicks) / (double)Stopwatch.Frequency;
        _pullLastTicks = now;
        _pullFrameAccumulator += elapsed * _sampleRate;
        int frames = (int)_pullFrameAccumulator;
        if (frames <= 0)
            return;

        int currentFrames = Volatile.Read(ref _currentBufferedFrames);
        int need = _targetBufferedFrames - currentFrames;
        if (need <= 0)
            return;

        if (frames > need)
            frames = need;
        if (frames > _maxProduceFrames)
            frames = _maxProduceFrames;

        long genStart = TraceStats ? Stopwatch.GetTimestamp() : 0;
        ReadOnlySpan<short> data = producer(frames);
        long genTicks = TraceStats ? Stopwatch.GetTimestamp() - genStart : 0;

        int producedFrames = data.Length / _channels;
        if (producedFrames <= 0)
            return;

        _pullFrameAccumulator -= producedFrames;

        Submit(data);
        if (TraceStats)
            ReportGenerateBatch(producedFrames, genTicks, timedMode: true);
    }

    public void ReportGenerateBatch(int framesProduced, long genTicks, bool timedMode)
    {
        if (framesProduced <= 0)
            return;

        Interlocked.Add(ref _audioGenTicksTotal, genTicks);
        Interlocked.Add(ref _audioGenFramesTotal, framesProduced);
        if (timedMode)
            Interlocked.Increment(ref _timedTicksTotal);
        else
            Interlocked.Increment(ref _frameTicksTotal);
        Interlocked.Add(ref _framesPerTickSum, framesProduced);
        UpdateMinMax(ref _framesPerTickMin, framesProduced, true);
        UpdateMinMax(ref _framesPerTickMax, framesProduced, false);
    }

    public void ReportTimedClamp()
    {
        Interlocked.Increment(ref _timedClampCount);
    }

    private void UpdateBufferedStats(int currentFrames)
    {
        Volatile.Write(ref _currentBufferedFrames, currentFrames);
        UpdateMinMax(ref _maxBufferedFrames, currentFrames, false);
        UpdateMinMax(ref _minBufferedFrames, currentFrames, true);
    }

    private static void UpdateMinMax(ref int target, int value, bool isMin)
    {
        int initial;
        while (true)
        {
            initial = Volatile.Read(ref target);
            if (isMin)
            {
                if (value >= initial)
                    return;
            }
            else
            {
                if (value <= initial)
                    return;
            }

            if (Interlocked.CompareExchange(ref target, value, initial) == initial)
                return;
        }
    }

    private void MaybeLogStats()
    {
        long now = Stopwatch.GetTimestamp();
        if (_statsStartTicks == 0)
        {
            _statsStartTicks = now;
            _lastStatsTicks = now;
            return;
        }

        long elapsedTicks = now - _lastStatsTicks;
        if (elapsedTicks < Stopwatch.Frequency)
            return;

        double intervalSec = elapsedTicks / (double)Stopwatch.Frequency;
        double sinceStartSec = (now - _statsStartTicks) / (double)Stopwatch.Frequency;
        _lastStatsTicks = now;

        long produced = Interlocked.Read(ref _producedFramesTotal);
        long consumed = Interlocked.Read(ref _consumedFramesTotal);
        long dropped = Interlocked.Read(ref _droppedFramesTotal);
        long underrunEvents = Interlocked.Read(ref _underrunEventsTotal);
        long underrunFrames = Interlocked.Read(ref _underrunFramesTotal);
        long genTicks = Interlocked.Read(ref _audioGenTicksTotal);
        long genFrames = Interlocked.Read(ref _audioGenFramesTotal);
        long drainBatches = Interlocked.Read(ref _drainBatchesTotal);
        long drainBatchFrames = Interlocked.Read(ref _drainBatchFramesTotal);
        long timedTicks = Interlocked.Exchange(ref _timedTicksTotal, 0);
        long frameTicks = Interlocked.Exchange(ref _frameTicksTotal, 0);
        long framesPerTickSum = Interlocked.Exchange(ref _framesPerTickSum, 0);
        int framesPerTickMin = Interlocked.Exchange(ref _framesPerTickMin, int.MaxValue);
        int framesPerTickMax = Interlocked.Exchange(ref _framesPerTickMax, 0);
        long timedClampCount = Interlocked.Exchange(ref _timedClampCount, 0);

        long producedDelta = produced - _lastProducedFrames;
        long consumedDelta = consumed - _lastConsumedFrames;
        long droppedDelta = dropped - _lastDroppedFrames;
        long underrunEventsDelta = underrunEvents - _lastUnderrunEvents;
        long underrunFramesDelta = underrunFrames - _lastUnderrunFrames;
        long genTicksDelta = genTicks - _lastGenTicks;
        long genFramesDelta = genFrames - _lastGenFrames;
        long drainBatchesDelta = drainBatches - _lastDrainBatches;
        long drainBatchFramesDelta = drainBatchFrames - _lastDrainBatchFrames;
        long timedTicksDelta = timedTicks;
        long frameTicksDelta = frameTicks;
        long framesPerTickSumDelta = framesPerTickSum;
        int framesPerTickMinDelta = framesPerTickMin == int.MaxValue ? 0 : framesPerTickMin;
        int framesPerTickMaxDelta = framesPerTickMax;
        long timedClampDelta = timedClampCount;

        _lastProducedFrames = produced;
        _lastConsumedFrames = consumed;
        _lastDroppedFrames = dropped;
        _lastUnderrunEvents = underrunEvents;
        _lastUnderrunFrames = underrunFrames;
        _lastGenTicks = genTicks;
        _lastGenFrames = genFrames;
        _lastDrainBatches = drainBatches;
        _lastDrainBatchFrames = drainBatchFrames;
        double producedFps = intervalSec > 0 ? producedDelta / intervalSec : 0;
        double consumedFps = intervalSec > 0 ? consumedDelta / intervalSec : 0;
        double genMs = genTicksDelta * 1000.0 / Stopwatch.Frequency;
        double driftFrames = produced - consumed;
        int currentBufferedFrames = Volatile.Read(ref _currentBufferedFrames);
        int maxBufferedFrames = Volatile.Read(ref _maxBufferedFrames);
        int minBufferedFrames = Volatile.Read(ref _minBufferedFrames);
        string mode = timedTicksDelta > 0 && frameTicksDelta == 0
            ? "timed"
            : (frameTicksDelta > 0 && timedTicksDelta == 0 ? "frame" : "mixed");
        double framesPerTickAvg = (timedTicksDelta + frameTicksDelta) > 0
            ? framesPerTickSumDelta / (double)(timedTicksDelta + frameTicksDelta)
            : 0;

        Console.WriteLine(
            "[AudioStats] t={0:F1}s rate={1} ch={2} batch={3} buf={4}/{5} minBuf={6} maxBuf={7} " +
            "prod={8} cons={9} drift={10} drop={11} underruns={12}/{13} " +
            "prodFps={14:F1} consFps={15:F1} genMs={16:F2} genFrames={17} " +
            "ticks={18} mode={19} fpt={20:F2} fptMin={21} fptMax={22} clamp={23}",
            sinceStartSec, _sampleRate, _channels, _framesPerBatch,
            currentBufferedFrames, _bufferFrames, minBufferedFrames, maxBufferedFrames,
            produced, consumed, driftFrames, dropped,
            underrunEventsDelta, underrunFramesDelta,
            producedFps, consumedFps, genMs, genFramesDelta,
            drainBatchesDelta, mode, framesPerTickAvg, framesPerTickMinDelta, framesPerTickMaxDelta, timedClampDelta);
    }

    public void Dispose()
    {
        Stop();
    }

    private void ResetStats()
    {
        _producedFramesTotal = 0;
        _consumedFramesTotal = 0;
        _droppedFramesTotal = 0;
        _underrunEventsTotal = 0;
        _underrunFramesTotal = 0;
        _drainBatchesTotal = 0;
        _drainBatchFramesTotal = 0;
        _audioGenTicksTotal = 0;
        _audioGenFramesTotal = 0;
        _timedTicksTotal = 0;
        _frameTicksTotal = 0;
        _framesPerTickSum = 0;
        _framesPerTickMin = int.MaxValue;
        _framesPerTickMax = 0;
        _timedClampCount = 0;
        _currentBufferedFrames = 0;
        _maxBufferedFrames = 0;
        _minBufferedFrames = int.MaxValue;
        _statsStartTicks = 0;
        _lastStatsTicks = 0;
        _lastProducedFrames = 0;
        _lastConsumedFrames = 0;
        _lastDroppedFrames = 0;
        _lastUnderrunEvents = 0;
        _lastUnderrunFrames = 0;
        _lastGenTicks = 0;
        _lastGenFrames = 0;
        _lastDrainBatches = 0;
        _lastDrainBatchFrames = 0;
        _pullLastTicks = 0;
        _pullFrameAccumulator = 0;
        _drainNextTicks = 0;
        _primingUntilTicks = 0;
    }
}
