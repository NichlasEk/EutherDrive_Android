using System;

namespace EutherDrive.Core;

internal sealed class GenesisAudioFilterPort
{
    private readonly bool _enableGenesisLpf;
    private readonly bool _enableYm2ndLpf;
    private readonly bool _enableDcBlock;
    private readonly FirstOrderHighPass _ymDcL;
    private readonly FirstOrderHighPass _ymDcR;
    private readonly FirstOrderHighPass _psgDc;
    private readonly FirstOrderLowPass _ymGenLpfL;
    private readonly FirstOrderLowPass _ymGenLpfR;
    private readonly FirstOrderLowPass _psgGenLpf;
    private readonly BiquadLowPass _ym2ndLpfL;
    private readonly BiquadLowPass _ym2ndLpfR;

    public GenesisAudioFilterPort(
        int ymSampleRate,
        int psgSampleRate,
        bool enableDcBlock,
        bool enableGenesisLpf,
        double genesisLpfCutoffHz,
        bool enableYm2ndLpf,
        double ym2ndLpfCutoffHz)
    {
        _enableDcBlock = enableDcBlock;
        _enableGenesisLpf = enableGenesisLpf;
        _enableYm2ndLpf = enableYm2ndLpf;

        _ymDcL = new FirstOrderHighPass(ymSampleRate, 5.0);
        _ymDcR = new FirstOrderHighPass(ymSampleRate, 5.0);
        _psgDc = new FirstOrderHighPass(psgSampleRate, 5.0);
        _ymGenLpfL = new FirstOrderLowPass(ymSampleRate, genesisLpfCutoffHz);
        _ymGenLpfR = new FirstOrderLowPass(ymSampleRate, genesisLpfCutoffHz);
        _psgGenLpf = new FirstOrderLowPass(psgSampleRate, genesisLpfCutoffHz);
        _ym2ndLpfL = new BiquadLowPass(ymSampleRate, ym2ndLpfCutoffHz);
        _ym2ndLpfR = new BiquadLowPass(ymSampleRate, ym2ndLpfCutoffHz);
    }

    public void Reset()
    {
        _ymDcL.Reset();
        _ymDcR.Reset();
        _psgDc.Reset();
        _ymGenLpfL.Reset();
        _ymGenLpfR.Reset();
        _psgGenLpf.Reset();
        _ym2ndLpfL.Reset();
        _ym2ndLpfR.Reset();
    }

    public int FilterYm(int sample, bool rightChannel)
    {
        double filtered = sample;
        if (_enableDcBlock)
            filtered = rightChannel ? _ymDcR.Apply(filtered) : _ymDcL.Apply(filtered);
        if (_enableYm2ndLpf)
            filtered = rightChannel ? _ym2ndLpfR.Apply(filtered) : _ym2ndLpfL.Apply(filtered);
        if (_enableGenesisLpf)
            filtered = rightChannel ? _ymGenLpfR.Apply(filtered) : _ymGenLpfL.Apply(filtered);
        return ClampToInt16(filtered);
    }

    public int FilterPsg(int sample)
    {
        double filtered = sample;
        if (_enableDcBlock)
            filtered = _psgDc.Apply(filtered);
        if (_enableGenesisLpf)
            filtered = _psgGenLpf.Apply(filtered);
        return ClampToInt16(filtered);
    }

    private static int ClampToInt16(double value)
    {
        int v = (int)Math.Round(value);
        if (v > short.MaxValue) return short.MaxValue;
        if (v < short.MinValue) return short.MinValue;
        return v;
    }

    private sealed class FirstOrderLowPass
    {
        private readonly double _alpha;
        private double _state;

        public FirstOrderLowPass(int sampleRate, double cutoffHz)
        {
            if (sampleRate <= 0) sampleRate = 44100;
            if (cutoffHz <= 0) cutoffHz = 1.0;
            double dt = 1.0 / sampleRate;
            double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
            _alpha = dt / (rc + dt);
        }

        public double Apply(double sample)
        {
            _state += _alpha * (sample - _state);
            return _state;
        }

        public void Reset()
        {
            _state = 0.0;
        }
    }

    private sealed class FirstOrderHighPass
    {
        private readonly double _alpha;
        private double _prevInput;
        private double _prevOutput;

        public FirstOrderHighPass(int sampleRate, double cutoffHz)
        {
            if (sampleRate <= 0) sampleRate = 44100;
            if (cutoffHz <= 0) cutoffHz = 1.0;
            double dt = 1.0 / sampleRate;
            double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
            _alpha = rc / (rc + dt);
        }

        public double Apply(double sample)
        {
            double output = _alpha * (_prevOutput + sample - _prevInput);
            _prevInput = sample;
            _prevOutput = output;
            return output;
        }

        public void Reset()
        {
            _prevInput = 0.0;
            _prevOutput = 0.0;
        }
    }

    private sealed class BiquadLowPass
    {
        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;
        private double _z1;
        private double _z2;

        public BiquadLowPass(int sampleRate, double cutoffHz)
        {
            if (sampleRate <= 0) sampleRate = 44100;
            if (cutoffHz <= 0) cutoffHz = 1.0;
            double nyquist = sampleRate * 0.5;
            if (cutoffHz > nyquist - 1.0)
                cutoffHz = nyquist - 1.0;

            double q = 1.0 / Math.Sqrt(2.0);
            double omega = 2.0 * Math.PI * cutoffHz / sampleRate;
            double sin = Math.Sin(omega);
            double cos = Math.Cos(omega);
            double alpha = sin / (2.0 * q);

            double b0 = (1.0 - cos) * 0.5;
            double b1 = 1.0 - cos;
            double b2 = (1.0 - cos) * 0.5;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cos;
            double a2 = 1.0 - alpha;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public double Apply(double sample)
        {
            double output = _b0 * sample + _z1;
            _z1 = _b1 * sample - _a1 * output + _z2;
            _z2 = _b2 * sample - _a2 * output;
            return output;
        }

        public void Reset()
        {
            _z1 = 0.0;
            _z2 = 0.0;
        }
    }
}
