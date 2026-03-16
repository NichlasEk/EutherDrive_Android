using System;
using EutherDrive.Audio;

namespace EutherDrive.Android;

internal sealed class AndroidAudioSink : IAudioSink
{
    private global::Android.Media.AudioTrack? _audioTrack;
    private short[] _writeBuffer = Array.Empty<short>();
    private readonly object _lock = new();

    public void Start(int sampleRate, int channels)
    {
        lock (_lock)
        {
            StopTrackNoLock();

            global::Android.Media.ChannelOut channelMask = channels switch
            {
                1 => global::Android.Media.ChannelOut.Mono,
                _ => global::Android.Media.ChannelOut.Stereo
            };

            int minBufferBytes = global::Android.Media.AudioTrack.GetMinBufferSize(
                sampleRate,
                channelMask,
                global::Android.Media.Encoding.Pcm16bit);

            if (minBufferBytes <= 0)
            {
                throw new InvalidOperationException($"AudioTrack.GetMinBufferSize failed: {minBufferBytes}");
            }

            int targetBufferBytes = Math.Max(minBufferBytes * 4, sampleRate * channels * sizeof(short) / 4);

            var attributes = new global::Android.Media.AudioAttributes.Builder()
                .SetUsage(global::Android.Media.AudioUsageKind.Game)
                .SetContentType(global::Android.Media.AudioContentType.Music)
                .Build();

            var format = new global::Android.Media.AudioFormat.Builder()
                .SetSampleRate(sampleRate)
                .SetEncoding(global::Android.Media.Encoding.Pcm16bit)
                .SetChannelMask(channelMask)
                .Build();

            _audioTrack = new global::Android.Media.AudioTrack(
                attributes,
                format,
                targetBufferBytes,
                global::Android.Media.AudioTrackMode.Stream,
                global::Android.Media.AudioManager.AudioSessionIdGenerate);

            if (_audioTrack.State != global::Android.Media.AudioTrackState.Initialized)
            {
                StopTrackNoLock();
                throw new InvalidOperationException("AudioTrack failed to initialize.");
            }

            _audioTrack.Play();
        }
    }

    public void Submit(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            if (_audioTrack == null)
            {
                return;
            }

            if (_writeBuffer.Length < interleaved.Length)
            {
                _writeBuffer = new short[interleaved.Length];
            }

            interleaved.CopyTo(_writeBuffer);

            int offset = 0;
            while (offset < interleaved.Length)
            {
                int written = _audioTrack.Write(
                    _writeBuffer,
                    offset,
                    interleaved.Length - offset);

                if (written <= 0)
                {
                    break;
                }

                offset += written;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopTrackNoLock();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopTrackNoLock();
        }
    }

    private void StopTrackNoLock()
    {
        if (_audioTrack == null)
        {
            return;
        }

        try
        {
            if (_audioTrack.PlayState == global::Android.Media.PlayState.Playing)
            {
                _audioTrack.Stop();
            }
        }
        catch
        {
            // Ignore teardown errors during shutdown/restart.
        }

        _audioTrack.Release();
        _audioTrack.Dispose();
        _audioTrack = null;
    }
}
