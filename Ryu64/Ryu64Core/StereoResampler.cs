using System;
using System.Collections.Generic;

namespace Ryu64Core
{
    // Continuous linear interpolation; retain the last frame and fractional
    // phase across DMA blocks, including empty frontend polls.
    public sealed class StereoResampler
    {
        private uint _sourceRate;
        private int _targetRate;
        private long _position;
        private bool _hasPrevious;
        private short _left, _right;

        public void Reset()
        {
            _hasPrevious = false;
            _position = 0;
            _sourceRate = 0;
        }

        public short[] Convert(short[] samples, uint sourceRate, int targetRate)
        {
            if (sourceRate == 0 || targetRate <= 0 || (samples.Length & 1) != 0)
                throw new ArgumentException("Invalid stereo audio format");
            if (samples.Length == 0) return Array.Empty<short>();
            if (_sourceRate != sourceRate || _targetRate != targetRate) Reset();
            _sourceRate = sourceRate;
            _targetRate = targetRate;
            if (sourceRate == targetRate) return samples;
            int frames = samples.Length / 2;
            int offset = _hasPrevious ? 1 : 0;
            int total = frames + offset;
            var output = new List<short>();
            while (_position < (long)(total - 1) * targetRate)
            {
                int frame = (int)(_position / targetRate);
                double fraction = (double)(_position % targetRate) / targetRate;
                for (int channel = 0; channel < 2; channel++)
                {
                    short a = offset == 1 && frame == 0
                        ? (channel == 0 ? _left : _right)
                        : samples[(frame - offset) * 2 + channel];
                    short b = samples[(frame + 1 - offset) * 2 + channel];
                    output.Add((short)Math.Round(a + (b - a) * fraction));
                }
                _position += sourceRate;
            }
            _position -= (long)(total - 1) * targetRate;
            _left = samples[samples.Length - 2];
            _right = samples[samples.Length - 1];
            _hasPrevious = true;
            return output.ToArray();
        }
    }
}
