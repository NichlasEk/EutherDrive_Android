using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EutherDrive.Core.Savestates;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core;

public sealed class MdTracerAdapter : IEmulatorCore, ISavestateCapable
{
    private const int DefaultW = 320;
    private const int DefaultH = 224;
    private readonly md_vdp _vdp = new md_vdp();

    private byte[] _frameBufferFront = Array.Empty<byte>(); // BGRA till UI (read)
    private byte[] _frameBufferBack = Array.Empty<byte>(); // BGRA till UI (write)
    private int _fbW, _fbH, _fbStride;
    private int _fbLogW = -1;
    private int _fbLogH = -1;
    private int _fbLogStride = -1;
    private int _fbIdentityLogCount;
    private long _fbPresentCount;
    private const int PresentSampleEveryFrames = 60;
    private long _lastVdpLogTicks;
    private long _lastPresentLogTicks;
    private long _lastSampleLogTicks;
    private long _lastAudioLogTicks;
    private long _lastAudioLevelTicks;
    private long _lastAudioCoreLogTicks;
    private long _lastYmInternalLogTicks;
    private bool _ymInternalLoggedOnce;
    private bool _ymInternalEnterLoggedOnce;
    private long _audioPathForceTicks;
    private int _audioPathForceCount;
    private bool _ymInternalForcedLoggedOnce;
    private bool _ymInternalForcedSummaryOnce;
    private bool _ymInternalDacStateLoggedOnce;
    private bool _ymResampleDebugOnce;
    private bool _ymResampleDebugFramesOnce;
    private bool _ymResampleOutLoggedOnce;
    private bool _ymResampleOutForcedOnce;
    private bool _ymResampleOutForcedDacOnce;

    private int _tick;
    private int _bootRecoverStallCount;
    private int _bootRecoverFrameCount;
    private bool _bootRecoverCompleted;
    private ushort _bootRecoverLastPc;
    private const int VLINES_NTSC = 262;
    private const int VLINES_PAL = 312;
    private const int SMS_VLINES_NTSC = 262;
    private const int SMS_VLINES_PAL = 313;
    private const double FPS_NTSC = 60.0;
    private const double FPS_PAL = 50.0;
    private uint _lastPc;
    private int _pcStallFrames;
    private FrameRateMode _frameRateMode = FrameRateMode.Auto;
    private FrameRateMode _lastAppliedFrameRateMode = FrameRateMode.Auto;
    private int _cpuCyclesPerLine;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;

    private int _bootRecoverStablePcFrames;
    private long _bootRecoverLastBusReqToggles;
    private long _bootRecoverLastResetToggles;
    private long _bootRecoverToggleAccum;
    private bool _bootRecoverSigInit;
    private int _sonic2AudioRecoveryFramesRemaining;
    private int _sonic2RuntimeStallFrames;
    private int _sonic2RuntimeRecoveryCount;
    private int _captainAmericaMailboxWaitGroup;
    private int _captainAmericaMailboxStableFrames;
    private uint _captainAmericaMailboxLastSemState;

    // Framebuffer analyzer for live debugging
    public FramebufferAnalyzer FbAnalyzer { get; } = null!;

    private static readonly bool DumpVectorsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DUMP_VECTORS"), "1", StringComparison.Ordinal);
    private static readonly bool FrameBufferTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FB_TRACE"), "1", StringComparison.Ordinal);
    private static bool TraceAudioEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO"), "1", StringComparison.Ordinal);
    private static readonly bool TraceAudioLevel =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDLVL"), "1", StringComparison.Ordinal);
    private static readonly bool TraceAudioDebug =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_DEBUG"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ALL"), "1", StringComparison.Ordinal);
    private static bool TraceYmInternal =>
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_INTERNAL"), "1", StringComparison.Ordinal);
    private static readonly bool TracePerf =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PERF"), "1", StringComparison.Ordinal);
    private static readonly bool TraceAladdinDebug =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ALADDIN_DEBUG"), "1", StringComparison.Ordinal);
    private static readonly bool TraceYmSilence =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_ON_SILENCE"), "1", StringComparison.Ordinal);
    private static readonly int TraceYmSilenceFrames = ParseNonNegativeInt("EUTHERDRIVE_TRACE_YM_ON_SILENCE_FRAMES", 60);
    private static readonly int TraceYmSilenceDump = ParseNonNegativeInt("EUTHERDRIVE_TRACE_YM_ON_SILENCE_DUMP", 128);
    private static readonly bool TraceAll =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ALL"), "1", StringComparison.Ordinal);
    private static readonly bool GenesisDcFilterEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_GENESIS_DC_FILTER"), "0", StringComparison.Ordinal);
    private static readonly bool GenesisLowPassFilterEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_GENESIS_LPF"), "0", StringComparison.Ordinal);
    private static readonly double GenesisLowPassCutoffHz =
        ParsePositiveDouble("EUTHERDRIVE_GENESIS_LPF_CUTOFF_HZ", 3390.0);
    private static readonly bool YmSecondLowPassFilterEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_2ND_LPF"), "1", StringComparison.Ordinal);
    private static readonly double YmSecondLowPassCutoffHz =
        ParsePositiveDouble("EUTHERDRIVE_YM_2ND_LPF_CUTOFF_HZ", 8000.0);
    private static readonly bool GenesisFloatMixEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_GENESIS_FLOAT_MIX"), "0", StringComparison.Ordinal);
    private static readonly bool YmResampleLinear = ParseYmResampleLinear();
    private static readonly bool YmResampleSimple =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_RESAMPLE_SIMPLE"), "1", StringComparison.OrdinalIgnoreCase);
    private static readonly bool PsgResampleLinear = ParsePsgResampleLinear();
    private static readonly bool PsgResampleSimple =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PSG_RESAMPLE_SIMPLE"), "1", StringComparison.OrdinalIgnoreCase);
    private static readonly bool PsgResampleSinc =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PSG_RESAMPLE_SINC"), "0", StringComparison.OrdinalIgnoreCase);
    private static readonly bool YmFilterPreResampleEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_FILTER_PRE_RESAMPLE"), "0", StringComparison.OrdinalIgnoreCase);
    private static readonly int PsgSincTaps = NormalizeEvenTaps(ParseNonNegativeInt("EUTHERDRIVE_PSG_RESAMPLE_SINC_TAPS", 24));
    private static readonly double[] PsgSincWindow = BuildBlackmanWindow(PsgSincTaps);
    private static readonly bool SkipVdpRenderEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SKIP_VDP_RENDER"), "1", StringComparison.Ordinal);
    private double _z80CycleMultiplier = ParseZ80CycleMultiplier();
    private static readonly int BootRecoverStallFrames = ParseBootRecoverStallFrames();
    private static readonly int BootRecoverWindowFrames = ParseBootRecoverWindowFrames();
    private static readonly int BootRecoverEdgeToggleThreshold = ParseBootRecoverEdgeToggleThreshold();
    private static readonly int BootRecoverEdgeStableFrames = ParseBootRecoverEdgeStableFrames();
    private static readonly bool BootRecoverLog =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_LOG"), "1", StringComparison.Ordinal);
    private static readonly int Sonic2AudioRecoveryFrames =
        ParseNonNegativeInt("EUTHERDRIVE_SONIC2_AUDIO_RECOVERY_FRAMES", 180);
    private static readonly bool Sonic2AudioRecoveryEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SONIC2_AUDIO_RECOVERY"), "1", StringComparison.Ordinal);
    private static readonly bool Sonic2ForceIffAfterLoad =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SONIC2_FORCE_IFF"), "1", StringComparison.Ordinal);
    private static readonly bool Sonic2RuntimeRecoveryEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SONIC2_RUNTIME_RECOVERY"), "1", StringComparison.Ordinal);
    private static readonly int Sonic2RuntimeRecoveryStallFrames =
        ParseNonNegativeInt("EUTHERDRIVE_SONIC2_RUNTIME_RECOVERY_STALL_FRAMES", 90);
    private static readonly int Sonic2RuntimeRecoveryMaxPerSession =
        ParseNonNegativeInt("EUTHERDRIVE_SONIC2_RUNTIME_RECOVERY_MAX", 0);
    private static readonly bool CaptainAmericaMailboxRecoveryEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_CAPAMERICA_MAILBOX_RECOVERY"), "0", StringComparison.Ordinal);
    private static readonly bool CaptainAmericaMailboxRecoveryDebug =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_CAPAMERICA_MAILBOX_RECOVERY_DEBUG"), "1", StringComparison.Ordinal);
    private static readonly int CaptainAmericaMailboxRecoveryFrames =
        ParseNonNegativeInt("EUTHERDRIVE_CAPAMERICA_MAILBOX_RECOVERY_FRAMES", 120);
    private static readonly bool TracePcPerFrame =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC_FRAME"), "1", StringComparison.Ordinal);
    private static readonly int TracePcEveryFrames = ParseNonNegativeInt("EUTHERDRIVE_TRACE_PC_FRAME_EVERY", 60);
    private static readonly bool TraceReset =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_RESET"), "1", StringComparison.Ordinal);
    private static readonly bool TraceSonic2Timing =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SONIC2_TIMING"), "1", StringComparison.Ordinal);
    private static readonly int TraceSonic2TimingEvery =
        ParseNonNegativeInt("EUTHERDRIVE_TRACE_SONIC2_TIMING_EVERY", 1);
    private static readonly int TraceSonic2TimingStartFrame =
        ParseNonNegativeInt("EUTHERDRIVE_TRACE_SONIC2_TIMING_START_FRAME", 0);

    // ASCII streaming mode for live viewer
    private static readonly bool AsciiStreamEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_ASCII_STREAM"), "1", StringComparison.Ordinal);
    private static readonly string AsciiSharedMemoryName =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_ASCII_SHARED") ?? "EutherDrive_AsciiViewer_FB";

    // Debug logging for ASCII stream
    private static readonly bool AsciiStreamDebug =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_ASCII_DEBUG"), "1", StringComparison.Ordinal);

    // DIAG-FRAME tracking
    private static long _lastDiagnosticFrame = -1;

    // ROM + BUS
    private byte[]? _rom;
    private MegaDriveBus? _bus;
    private readonly object _loadLock = new();
    private readonly object _stateLock = new();
    private RomIdentity? _romIdentity;

    // CPU runner (MDTracer m68k via reflection)
    private MdTracerM68kRunner? _cpu;
    private bool _cpuReady;

    // Simple perf tracking
    private long _accCpuTicks;
    private long _accVdpTicks;
    private long _accFrameTicks;
    private int _perfFrameCount;
    private int _lastGc0;
    private int _lastGc1;
    private int _lastGc2;
    private readonly long[] _hotspotTicks = new long[(int)PerfHotspot.Count];

    private static readonly int OutputSampleRate = ParseOutputSampleRate();
    private const int PsgChannels = 2;
    // jgenesis PSG coefficient (-7 dB) used in final Genesis mix path.
    private const double JgenesisPsgCoefficient = 0.44668359215096315;
    private static readonly double YmResampleScale = GetYmResampleScale();
    private static readonly double FmMixGain = GetFmMixGain();
    private static readonly double YmGainDb = ParseGainDb("EUTHERDRIVE_YM_GAIN_DB", 0.0);
    private static readonly double PsgGainDb = ParseGainDb("EUTHERDRIVE_PSG_GAIN_DB", 0.0);
    private static readonly double YmLinearGain = DbToLinear(YmGainDb);
    private static readonly double PsgLinearGain = JgenesisPsgCoefficient * DbToLinear(PsgGainDb);
    private bool _ymEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "0", StringComparison.Ordinal);
    private readonly bool _psgDisabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DISABLE_PSG"), "1", StringComparison.Ordinal);
    private double _psgFrameAccumulator;
    private long _psgLastFrame = -1;
    private bool _audioGeneratedThisFrame = false;
    private volatile int _masterVolumePercent = 50;
    private volatile int _psgMixPercent = 100;
    private volatile int _ymMixPercent = 100;
    private volatile int _psgNoisePercent = 100;
    private int _ymSilentFrames;
    private bool _ymSilenceLogged;
    private bool _audioAllSourcesDisabledLogged;
    private SimpleLowPassFilter? _mixLowPass;
    private int _psgFrameSamples;
    private double _psgResamplePhase;
    private bool _psgResampleHasCarry;
    private short _psgResampleCarry;
    private double _ymResamplePhase;
    private bool _ymResampleHasCarry;
    private short[] _psgFrameBuffer = Array.Empty<short>();
    private short[] _psgInternalBuffer = Array.Empty<short>();
    private short[] _ymFrameBuffer = Array.Empty<short>();
    private short[] _ymInternalBuffer = Array.Empty<short>();
    private short _ymResampleCarryL;
    private short _ymResampleCarryR;
    private bool _ymResampleLinear;
    private bool _psgResampleLinear;
    private bool _audioSystemReady = false;
    private bool _svpTickLoggedInAdapter;
    private int _audioWarmupFrames = ParseAudioWarmupFrames();
    private readonly SimpleHighPassFilter _ymDcFilterL = new(OutputSampleRate, 5.0);
    private readonly SimpleHighPassFilter _ymDcFilterR = new(OutputSampleRate, 5.0);
    private readonly SimpleHighPassFilter _psgDcFilter = new(OutputSampleRate, 5.0);
    private readonly SimpleLowPassSampleFilter _ymLowPassL = new(OutputSampleRate, GenesisLowPassCutoffHz);
    private readonly SimpleLowPassSampleFilter _ymLowPassR = new(OutputSampleRate, GenesisLowPassCutoffHz);
    private readonly SimpleLowPassSampleFilter _psgLowPass = new(OutputSampleRate, GenesisLowPassCutoffHz);
    private readonly SimpleLowPassSampleFilter _ymLowPass2L = new(OutputSampleRate, YmSecondLowPassCutoffHz);
    private readonly SimpleLowPassSampleFilter _ymLowPass2R = new(OutputSampleRate, YmSecondLowPassCutoffHz);
    private readonly GenesisAudioFilterPort _jgAudioFilter = new(
        OutputSampleRate,
        OutputSampleRate,
        GenesisDcFilterEnabled,
        GenesisLowPassFilterEnabled,
        GenesisLowPassCutoffHz,
        YmSecondLowPassFilterEnabled,
        YmSecondLowPassCutoffHz);
    private GenesisAudioFilterPort? _jgPsgPreResampleFilter;
    private int _jgPsgPreResampleRateHz;
    private GenesisAudioFilterPort? _jgYmPreResampleFilter;
    private int _jgYmPreResampleRateHz;

    private static int ParseAudioWarmupFrames()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_WARMUP_FRAMES");
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int value) && value > 0)
            return value;
        return 6;
    }

    private static int ParseOutputSampleRate()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_OUTPUT_HZ");
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int value) && value >= 22050 && value <= 192000)
            return value;
        return 44100;
    }

    private static double ParsePositiveDouble(string name, double defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0.0)
            return value;
        return defaultValue;
    }

    private static double ParseGainDb(string name, double defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return value;
        return defaultValue;
    }

    private static double DbToLinear(double db)
    {
        return Math.Pow(10.0, db / 20.0);
    }

    private static int ApplyLinearGain(int sample, double gain)
    {
        if (gain == 1.0)
            return sample;
        double scaled = sample * gain;
        if (scaled > short.MaxValue) return short.MaxValue;
        if (scaled < short.MinValue) return short.MinValue;
        return (int)Math.Round(scaled);
    }

    private static int MixGenesisSamples(int psgSample, int ymSample)
    {
        if (!GenesisFloatMixEnabled)
        {
            int mixedInt = psgSample + ymSample;
            if (mixedInt > short.MaxValue) return short.MaxValue;
            if (mixedInt < short.MinValue) return short.MinValue;
            return mixedInt;
        }

        // jgenesis mixes in normalized domain and clamps to [-1, 1].
        double psgNorm = psgSample / (double)short.MaxValue;
        double ymNorm = ymSample / (double)short.MaxValue;
        double mixed = psgNorm + ymNorm;
        if (mixed > 1.0) mixed = 1.0;
        else if (mixed < -1.0) mixed = -1.0;
        return (int)Math.Round(mixed * short.MaxValue);
    }

    private readonly struct AudioMixStats
    {
        public AudioMixStats(int min, int max, int peak, int nonZero)
        {
            Min = min;
            Max = max;
            Peak = peak;
            NonZero = nonZero;
        }

        public int Min { get; }
        public int Max { get; }
        public int Peak { get; }
        public int NonZero { get; }
    }

    private AudioMixStats MixCollectedChipBuffers(int samples, bool trackAudioLevel, ref long mixSumSq)
    {
        int mixMin = 0;
        int mixMax = 0;
        int mixPeak = 0;
        int mixNonZero = 0;
        bool mixInit = false;

        for (int i = 0; i < samples; i++)
        {
            int mixed = MixGenesisSamples(_psgFrameBuffer[i], _ymFrameBuffer[i]);
            _psgFrameBuffer[i] = (short)mixed;

            if (!mixInit)
            {
                mixInit = true;
                mixMin = mixed;
                mixMax = mixed;
            }
            else
            {
                if (mixed < mixMin) mixMin = mixed;
                if (mixed > mixMax) mixMax = mixed;
            }

            int mixAbs = mixed < 0 ? -mixed : mixed;
            if (mixAbs > mixPeak) mixPeak = mixAbs;
            if (mixed != 0) mixNonZero++;
            if (trackAudioLevel)
                mixSumSq += (long)mixed * mixed;
        }

        return new AudioMixStats(mixMin, mixMax, mixPeak, mixNonZero);
    }

    private AudioMixStats ProcessYmBufferAndMix(
        int samples,
        int ymMixPercent,
        bool trackAudioLevel,
        ref long mixSumSq,
        out int ymMin,
        out int ymMax,
        out int ymPeak,
        out int ymNonZero)
    {
        ymMin = 0;
        ymMax = 0;
        ymPeak = 0;
        ymNonZero = 0;
        bool ymInit = false;

        for (int i = 0; i < samples; i++)
        {
            int ymSample = _ymFrameBuffer[i];
            if (FmMixGain != 1.0)
            {
                double scaled = ymSample * FmMixGain;
                if (scaled > short.MaxValue) ymSample = short.MaxValue;
                else if (scaled < short.MinValue) ymSample = short.MinValue;
                else ymSample = (int)Math.Round(scaled);
                _ymFrameBuffer[i] = (short)ymSample;
            }
            ymSample = ApplyMixPercent(ymSample, ymMixPercent);
            ymSample = ApplyLinearGain(ymSample, YmLinearGain);
            if (!YmFilterPreResampleEnabled)
                ymSample = _jgAudioFilter.FilterYm(ymSample, rightChannel: (i & 1) != 0);
            _ymFrameBuffer[i] = (short)ymSample;

            if (!ymInit)
            {
                ymInit = true;
                ymMin = ymSample;
                ymMax = ymSample;
            }
            else
            {
                if (ymSample < ymMin) ymMin = ymSample;
                if (ymSample > ymMax) ymMax = ymSample;
            }

            int ymAbs = ymSample < 0 ? -ymSample : ymSample;
            if (ymAbs > ymPeak) ymPeak = ymAbs;
            if (ymSample != 0) ymNonZero++;
        }

        return MixCollectedChipBuffers(samples, trackAudioLevel, ref mixSumSq);
    }

    private void PrepareYmInternalBuffer(int neededInternal, out int writeOffsetFrames, out int genFrames)
    {
        int internalSamples = neededInternal * PsgChannels;
        if (_ymInternalBuffer.Length < internalSamples)
            _ymInternalBuffer = new short[internalSamples];

        writeOffsetFrames = 0;
        if (_ymResampleHasCarry)
        {
            _ymInternalBuffer[0] = _ymResampleCarryL;
            _ymInternalBuffer[1] = _ymResampleCarryR;
            writeOffsetFrames = 1;
        }

        genFrames = neededInternal - writeOffsetFrames;
    }

    private void ResampleYmOutputFrames(int frames, int neededInternal, double ratio, ref double phase)
    {
        for (int i = 0; i < frames; i++)
        {
            int baseIndex = (int)phase;
            if (baseIndex < 0)
                baseIndex = 0;
            int maxBase = neededInternal - 2;
            if (maxBase < 0)
                maxBase = 0;
            if (baseIndex > maxBase)
                baseIndex = maxBase;
            double frac = phase - baseIndex;
            if (frac < 0)
                frac = 0;
            else if (frac > 1.0)
                frac = 1.0;

            int ymL;
            int ymR;
            if (YmResampleSimple)
            {
                int f = baseIndex;
                if (f >= neededInternal) f = neededInternal - 1;
                ymL = ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f, 0);
                ymR = ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f, 1);
            }
            else
            {
                int f0 = baseIndex - 1;
                int f1 = baseIndex;
                int f2 = baseIndex + 1;
                int f3 = baseIndex + 2;
                if (f0 < 0) f0 = 0;
                if (f2 >= neededInternal) f2 = neededInternal - 1;
                if (f3 >= neededInternal) f3 = neededInternal - 1;

                ymL = _ymResampleLinear
                    ? LinearInterpolate(
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f1, 0),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f2, 0),
                        frac)
                    : CubicInterpolate(
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f0, 0),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f1, 0),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f2, 0),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f3, 0),
                        frac);
                ymR = _ymResampleLinear
                    ? LinearInterpolate(
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f1, 1),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f2, 1),
                        frac)
                    : CubicInterpolate(
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f0, 1),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f1, 1),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f2, 1),
                        ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f3, 1),
                        frac);
            }

            int dst = i * PsgChannels;
            _ymFrameBuffer[dst] = (short)ymL;
            _ymFrameBuffer[dst + 1] = (short)ymR;

            phase += ratio;
        }
    }

    private void CommitYmResampleState(int neededInternal, double phase)
    {
        int lastIndex = (neededInternal - 1) * PsgChannels;
        _ymResampleCarryL = _ymInternalBuffer[lastIndex];
        _ymResampleCarryR = _ymInternalBuffer[lastIndex + 1];
        _ymResampleHasCarry = true;
        _ymResamplePhase = phase - (neededInternal - 1);
        if (_ymResamplePhase < 0)
            _ymResamplePhase = 0;
        else if (_ymResamplePhase > neededInternal)
            _ymResamplePhase = 0;
    }

    private static int ReadMonoSample(short[] buffer, int frames, int frame)
    {
        if (frames <= 0)
            return 0;
        if (frame < 0)
            frame = 0;
        else if (frame >= frames)
            frame = frames - 1;
        if ((uint)frame >= (uint)buffer.Length)
            return 0;
        return buffer[frame];
    }

    private void EnsurePsgPreResampleFilter(int psgSampleRateHz)
    {
        if (psgSampleRateHz < 1000)
            psgSampleRateHz = 1000;

        if (_jgPsgPreResampleFilter != null && _jgPsgPreResampleRateHz == psgSampleRateHz)
            return;

        _jgPsgPreResampleFilter = new GenesisAudioFilterPort(
            psgSampleRateHz,
            psgSampleRateHz,
            GenesisDcFilterEnabled,
            GenesisLowPassFilterEnabled,
            GenesisLowPassCutoffHz,
            enableYm2ndLpf: false,
            ym2ndLpfCutoffHz: YmSecondLowPassCutoffHz);
        _jgPsgPreResampleRateHz = psgSampleRateHz;
    }

    private void EnsureYmPreResampleFilter(int ymSampleRateHz)
    {
        if (ymSampleRateHz < 1000)
            ymSampleRateHz = 1000;

        if (_jgYmPreResampleFilter != null && _jgYmPreResampleRateHz == ymSampleRateHz)
            return;

        _jgYmPreResampleFilter = new GenesisAudioFilterPort(
            ymSampleRateHz,
            ymSampleRateHz,
            GenesisDcFilterEnabled,
            GenesisLowPassFilterEnabled,
            GenesisLowPassCutoffHz,
            YmSecondLowPassFilterEnabled,
            YmSecondLowPassCutoffHz);
        _jgYmPreResampleRateHz = ymSampleRateHz;
    }

    private void FilterYmPreResampleBuffer(int writeOffsetFrames, int genFrames)
    {
        if (_jgYmPreResampleFilter == null || genFrames <= 0)
            return;

        for (int i = 0; i < genFrames; i++)
        {
            int frame = writeOffsetFrames + i;
            int idx = frame * PsgChannels;
            int l = _ymInternalBuffer[idx];
            int r = _ymInternalBuffer[idx + 1];
            l = _jgYmPreResampleFilter.FilterYm(l, rightChannel: false);
            r = _jgYmPreResampleFilter.FilterYm(r, rightChannel: true);
            _ymInternalBuffer[idx] = (short)l;
            _ymInternalBuffer[idx + 1] = (short)r;
        }
    }

    private void GeneratePsgResampledFrames(md_music music, int frames)
    {
        if (frames <= 0)
            return;

        double psgInternalSampleRate = md_main.GetGenesisMasterClockHz() / (15.0 * 16.0);
        EnsurePsgPreResampleFilter((int)Math.Round(psgInternalSampleRate));
        double ratio = psgInternalSampleRate / OutputSampleRate;
        double phase = _psgResamplePhase;
        int neededInternal = (int)Math.Floor(phase + ((frames - 1) * ratio)) + 2;
        if (neededInternal < 2)
            neededInternal = 2;

        if (_psgInternalBuffer.Length < neededInternal)
            _psgInternalBuffer = new short[neededInternal];

        int writeOffset = 0;
        if (_psgResampleHasCarry)
        {
            _psgInternalBuffer[0] = _psgResampleCarry;
            writeOffset = 1;
        }

        for (int i = writeOffset; i < neededInternal; i++)
        {
            int sample = music.PsgUpdateSample();
            if (_jgPsgPreResampleFilter != null)
                sample = _jgPsgPreResampleFilter.FilterPsg(sample);
            _psgInternalBuffer[i] = (short)sample;
        }

        for (int i = 0; i < frames; i++)
        {
            int baseIndex = (int)phase;
            if (baseIndex < 0)
                baseIndex = 0;
            int maxBase = neededInternal - 2;
            if (maxBase < 0)
                maxBase = 0;
            if (baseIndex > maxBase)
                baseIndex = maxBase;

            double frac = phase - baseIndex;
            if (frac < 0)
                frac = 0;
            else if (frac > 1.0)
                frac = 1.0;

            int sample;
            if (PsgResampleSimple)
            {
                int f = baseIndex;
                if (f >= neededInternal) f = neededInternal - 1;
                sample = ReadMonoSample(_psgInternalBuffer, neededInternal, f);
            }
            else
            {
                int f0 = baseIndex - 1;
                int f1 = baseIndex;
                int f2 = baseIndex + 1;
                int f3 = baseIndex + 2;
                if (f0 < 0) f0 = 0;
                if (f2 >= neededInternal) f2 = neededInternal - 1;
                if (f3 >= neededInternal) f3 = neededInternal - 1;

                if (PsgResampleSinc)
                {
                    int taps = PsgSincTaps;
                    int half = taps / 2;
                    double cutoff = 0.5 * Math.Min(1.0, OutputSampleRate / psgInternalSampleRate);
                    double sum = 0.0;
                    double norm = 0.0;

                    for (int t = 0; t < taps; t++)
                    {
                        int k = t - half + 1;
                        int src = baseIndex + k;
                        if (src < 0) src = 0;
                        else if (src >= neededInternal) src = neededInternal - 1;

                        double x = k - frac;
                        double coeff = Sinc(2.0 * cutoff * x) * PsgSincWindow[t] * (2.0 * cutoff);
                        sum += ReadMonoSample(_psgInternalBuffer, neededInternal, src) * coeff;
                        norm += coeff;
                    }

                    if (Math.Abs(norm) > 1e-18)
                        sum /= norm;

                    if (sum > short.MaxValue) sample = short.MaxValue;
                    else if (sum < short.MinValue) sample = short.MinValue;
                    else sample = (int)Math.Round(sum);
                }
                else
                {
                    sample = _psgResampleLinear
                        ? LinearInterpolate(
                            ReadMonoSample(_psgInternalBuffer, neededInternal, f1),
                            ReadMonoSample(_psgInternalBuffer, neededInternal, f2),
                            frac)
                        : CubicInterpolate(
                            ReadMonoSample(_psgInternalBuffer, neededInternal, f0),
                            ReadMonoSample(_psgInternalBuffer, neededInternal, f1),
                            ReadMonoSample(_psgInternalBuffer, neededInternal, f2),
                            ReadMonoSample(_psgInternalBuffer, neededInternal, f3),
                            frac);
                }
            }

            int dst = i * PsgChannels;
            _psgFrameBuffer[dst] = (short)sample;
            _psgFrameBuffer[dst + 1] = (short)sample;
            phase += ratio;
        }

        _psgResampleCarry = _psgInternalBuffer[neededInternal - 1];
        _psgResampleHasCarry = true;
        _psgResamplePhase = phase - (neededInternal - 1);
        if (_psgResamplePhase < 0)
            _psgResamplePhase = 0;
        else if (_psgResamplePhase > neededInternal)
            _psgResamplePhase = 0;
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        _masterVolumePercent = percent;
    }

    public void SetPsgMixPercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 200) percent = 200;
        _psgMixPercent = percent;
    }

    public void SetYmMixPercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 200) percent = 200;
        _ymMixPercent = percent;
    }

    public void SetPsgNoiseMixPercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 200) percent = 200;
        _psgNoisePercent = percent;
        md_main.g_md_music?.SetPsgNoiseGainPercent(_psgNoisePercent);
    }

    private void ApplyMasterVolume(short[] buffer, int samples)
    {
        if (samples <= 0)
            return;

        int percent = Volatile.Read(ref _masterVolumePercent);
        if (percent >= 100)
            return;

        for (int i = 0; i < samples; i++)
        {
            int scaled = buffer[i] * percent / 100;
        if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            buffer[i] = (short)scaled;
        }
    }

    private static int ApplyMixPercent(int sample, int percent)
    {
        if (percent == 100)
            return sample;
        int scaled = sample * percent / 100;
        if (scaled > short.MaxValue) return short.MaxValue;
        if (scaled < short.MinValue) return short.MinValue;
        return scaled;
    }

    private sealed class SimpleLowPassFilter
    {
        private readonly double _alpha;
        private double _left;
        private double _right;

        public SimpleLowPassFilter(int sampleRate, double cutoffHz)
        {
            if (cutoffHz <= 0)
                cutoffHz = 8000;
            double dt = 1.0 / sampleRate;
            double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
            _alpha = dt / (rc + dt);
        }

        public void Reset()
        {
            _left = 0;
            _right = 0;
        }

        public void Apply(short[] buffer, int samples)
        {
            for (int i = 0; i < samples; i += 2)
            {
                double l = _left + _alpha * (buffer[i] - _left);
                double r = _right + _alpha * (buffer[i + 1] - _right);
                _left = l;
                _right = r;
                buffer[i] = (short)Math.Clamp((int)Math.Round(l), short.MinValue, short.MaxValue);
                buffer[i + 1] = (short)Math.Clamp((int)Math.Round(r), short.MinValue, short.MaxValue);
            }
        }
    }

    private sealed class SimpleHighPassFilter
    {
        private readonly double _alpha;
        private double _x1;
        private double _y1;

        public SimpleHighPassFilter(int sampleRate, double cutoffHz)
        {
            if (cutoffHz <= 0)
                cutoffHz = 5.0;
            double dt = 1.0 / sampleRate;
            double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
            _alpha = rc / (rc + dt);
        }

        public void Reset()
        {
            _x1 = 0.0;
            _y1 = 0.0;
        }

        public int Apply(int sample)
        {
            double x = sample;
            double y = _alpha * (_y1 + x - _x1);
            _x1 = x;
            _y1 = y;
            if (y > short.MaxValue) return short.MaxValue;
            if (y < short.MinValue) return short.MinValue;
            return (int)Math.Round(y);
        }
    }

    private sealed class SimpleLowPassSampleFilter
    {
        private readonly double _alpha;
        private double _y1;

        public SimpleLowPassSampleFilter(int sampleRate, double cutoffHz)
        {
            if (cutoffHz <= 0)
                cutoffHz = 3390.0;
            double dt = 1.0 / sampleRate;
            double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
            _alpha = dt / (rc + dt);
        }

        public void Reset()
        {
            _y1 = 0.0;
        }

        public int Apply(int sample)
        {
            double y = _y1 + _alpha * (sample - _y1);
            _y1 = y;
            if (y > short.MaxValue) return short.MaxValue;
            if (y < short.MinValue) return short.MinValue;
            return (int)Math.Round(y);
        }
    }

    private static SimpleLowPassFilter? CreateLowPassIfEnabled(int sampleRate)
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_LP_HZ");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cutoff))
            return null;
        if (cutoff <= 0)
            return null;
        return new SimpleLowPassFilter(sampleRate, cutoff);
    }

    private static int ReadInterleavedSample(short[] buffer, int frames, int channels, int frame, int channel)
    {
        if (frames <= 0)
            return 0;
        if (frame < 0)
            frame = 0;
        else if (frame >= frames)
            frame = frames - 1;
        int idx = (frame * channels) + channel;
        if ((uint)idx >= (uint)buffer.Length)
            return 0;
        return buffer[idx];
    }

    private static int CubicInterpolate(int p0, int p1, int p2, int p3, double t)
    {
        double a0 = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3;
        double a1 = p0 - 2.5 * p1 + 2.0 * p2 - 0.5 * p3;
        double a2 = -0.5 * p0 + 0.5 * p2;
        double a3 = p1;
        double v = ((a0 * t + a1) * t + a2) * t + a3;
        if (v > short.MaxValue) v = short.MaxValue;
        else if (v < short.MinValue) v = short.MinValue;
        return (int)Math.Round(v);
    }

    private static int LinearInterpolate(int s0, int s1, double t)
    {
        double v = s0 + (s1 - s0) * t;
        if (v > short.MaxValue) v = short.MaxValue;
        else if (v < short.MinValue) v = short.MinValue;
        return (int)Math.Round(v);
    }

    private static int NormalizeEvenTaps(int taps)
    {
        if (taps < 8) taps = 8;
        if ((taps & 1) != 0) taps++;
        return taps;
    }

    private static double[] BuildBlackmanWindow(int taps)
    {
        double[] w = new double[taps];
        if (taps == 1)
        {
            w[0] = 1.0;
            return w;
        }

        double denom = taps - 1;
        for (int n = 0; n < taps; n++)
        {
            double a = 2.0 * Math.PI * n / denom;
            w[n] = 0.42 - 0.5 * Math.Cos(a) + 0.08 * Math.Cos(2.0 * a);
        }
        return w;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12)
            return 1.0;
        double pix = Math.PI * x;
        return Math.Sin(pix) / pix;
    }

    private static double GetYmResampleScale()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_RESAMPLE_SCALE");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        return 1.0;
    }

    private static bool ParsePsgResampleLinear()
    {
        // PSG is rich in sharp edges; cubic interpolation can introduce audible ringing.
        // Default to linear unless explicitly forced to cubic/0.
        string? mode = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSG_RESAMPLE");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (string.Equals(mode, "cubic", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(mode, "linear", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string? legacy = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSG_RESAMPLE_LINEAR");
        if (!string.IsNullOrWhiteSpace(legacy))
            return !string.Equals(legacy, "0", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static bool ParseYmResampleLinear()
    {
        // YM defaulting to cubic can introduce audible ringing/zipper artifacts
        // on DAC-heavy titles. Default to linear unless explicitly forced to cubic.
        string? mode = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_RESAMPLE");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (string.Equals(mode, "cubic", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(mode, "linear", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string? legacy = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_RESAMPLE_LINEAR");
        if (!string.IsNullOrWhiteSpace(legacy))
            return !string.Equals(legacy, "0", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static double GetFmMixGain()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_FM_MIX_GAIN");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        return 1.0;
    }

    public RomInfo RomInfo { get; private set; } = new RomInfo();

    /// <summary>
    /// Constructor - initializes framebuffer analyzer
    /// </summary>
    public MdTracerAdapter()
    {
        FbAnalyzer = new FramebufferAnalyzer(this);
        _mixLowPass = CreateLowPassIfEnabled(OutputSampleRate);
        _ymResampleLinear = YmResampleLinear;
        _psgResampleLinear = PsgResampleLinear;

        bool showAudioConfig = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_CONFIG"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DISABLE_PSG"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "0", StringComparison.Ordinal);
        if (showAudioConfig)
        {
            string ymRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM") ?? "<unset>";
            string psgRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_DISABLE_PSG") ?? "<unset>";
            string ymResampleRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_RESAMPLE") ?? "<unset>";
            Console.WriteLine($"[AUDIO-CONFIG] YM(raw)={ymRaw} YM(enabled)={(_ymEnabled ? 1 : 0)} PSG_DISABLE(raw)={psgRaw} PSG(enabled)={(_psgDisabled ? 0 : 1)} YM_RESAMPLE(raw)={ymResampleRaw} YM_LINEAR={(_ymResampleLinear ? 1 : 0)} PSG_LINEAR={(_psgResampleLinear ? 1 : 0)}");
        }

        // Auto-enable if env var is set
        if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FB_ANALYZER"), "1", StringComparison.Ordinal))
        {
            FbAnalyzer.Enabled = true;
            FbAnalyzer.ConfigureGrid(8, 6);
            FbAnalyzer.SetSampleRate(1);
        }
    }

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => md_main.g_md_vdp?.FrameCounter;

    public readonly struct MainInterruptDebugState
    {
        public readonly bool UsingM68kEmu;
        public readonly bool M68kEmuConfigured;
        public readonly bool M68kEmuReady;
        public readonly ushort Sr;
        public readonly byte InterruptMask;
        public readonly int PendingInterruptLevel;
        public readonly byte BusInterruptLevel;
        public readonly bool WillTakeInterrupt;
        public readonly bool CpuStopped;
        public readonly bool CpuFrozen;
        public readonly ushort NextOpcode;
        public readonly bool HintReq;
        public readonly bool VintReq;
        public readonly bool ExtReq;
        public readonly bool HintEnabled;
        public readonly bool VintEnabled;
        public readonly long HintReqCount;
        public readonly long VintReqCount;
        public readonly long ExtReqCount;
        public readonly long HintAckCount;
        public readonly long VintAckCount;
        public readonly long ExtAckCount;
        public readonly int M68kEmuSliceExecInstructions;
        public readonly int M68kEmuSliceDmaWaitCycles;
        public readonly int M68kEmuSliceDmaWaitEvents;
        public readonly int M68kEmuSliceRefreshWaitCycles;
        public readonly int M68kEmuSliceRefreshWaitEvents;
        public readonly int M68kEmuNoExecSliceStreak;
        public readonly long AutoUnmaskFireCount;
        public readonly long RunFrameEnterCount;
        public readonly long RunFrameCompleteCount;
        public readonly long RunFrameLastFrame;
        public readonly int RunFrameLastLines;
        public readonly int RunFrameLastM68kCalls;
        public readonly int RunFrameLastM68kBudget;

        public MainInterruptDebugState(
            bool usingM68kEmu,
            bool m68kEmuConfigured,
            bool m68kEmuReady,
            ushort sr,
            byte interruptMask,
            int pendingInterruptLevel,
            byte busInterruptLevel,
            bool willTakeInterrupt,
            bool cpuStopped,
            bool cpuFrozen,
            ushort nextOpcode,
            bool hintReq,
            bool vintReq,
            bool extReq,
            bool hintEnabled,
            bool vintEnabled,
            long hintReqCount,
            long vintReqCount,
            long extReqCount,
            long hintAckCount,
            long vintAckCount,
            long extAckCount,
            int m68kEmuSliceExecInstructions,
            int m68kEmuSliceDmaWaitCycles,
            int m68kEmuSliceDmaWaitEvents,
            int m68kEmuSliceRefreshWaitCycles,
            int m68kEmuSliceRefreshWaitEvents,
            int m68kEmuNoExecSliceStreak,
            long autoUnmaskFireCount,
            long runFrameEnterCount,
            long runFrameCompleteCount,
            long runFrameLastFrame,
            int runFrameLastLines,
            int runFrameLastM68kCalls,
            int runFrameLastM68kBudget)
        {
            UsingM68kEmu = usingM68kEmu;
            M68kEmuConfigured = m68kEmuConfigured;
            M68kEmuReady = m68kEmuReady;
            Sr = sr;
            InterruptMask = interruptMask;
            PendingInterruptLevel = pendingInterruptLevel;
            BusInterruptLevel = busInterruptLevel;
            WillTakeInterrupt = willTakeInterrupt;
            CpuStopped = cpuStopped;
            CpuFrozen = cpuFrozen;
            NextOpcode = nextOpcode;
            HintReq = hintReq;
            VintReq = vintReq;
            ExtReq = extReq;
            HintEnabled = hintEnabled;
            VintEnabled = vintEnabled;
            HintReqCount = hintReqCount;
            VintReqCount = vintReqCount;
            ExtReqCount = extReqCount;
            HintAckCount = hintAckCount;
            VintAckCount = vintAckCount;
            ExtAckCount = extAckCount;
            M68kEmuSliceExecInstructions = m68kEmuSliceExecInstructions;
            M68kEmuSliceDmaWaitCycles = m68kEmuSliceDmaWaitCycles;
            M68kEmuSliceDmaWaitEvents = m68kEmuSliceDmaWaitEvents;
            M68kEmuSliceRefreshWaitCycles = m68kEmuSliceRefreshWaitCycles;
            M68kEmuSliceRefreshWaitEvents = m68kEmuSliceRefreshWaitEvents;
            M68kEmuNoExecSliceStreak = m68kEmuNoExecSliceStreak;
            AutoUnmaskFireCount = autoUnmaskFireCount;
            RunFrameEnterCount = runFrameEnterCount;
            RunFrameCompleteCount = runFrameCompleteCount;
            RunFrameLastFrame = runFrameLastFrame;
            RunFrameLastLines = runFrameLastLines;
            RunFrameLastM68kCalls = runFrameLastM68kCalls;
            RunFrameLastM68kBudget = runFrameLastM68kBudget;
        }
    }

    public MainInterruptDebugState GetMainInterruptDebugState()
    {
        var s = md_main.CaptureMainInterruptDebug();
        return new MainInterruptDebugState(
            usingM68kEmu: s.UsingM68kEmu,
            m68kEmuConfigured: s.M68kEmuConfigured,
            m68kEmuReady: s.M68kEmuReady,
            sr: s.Sr,
            interruptMask: s.InterruptMask,
            pendingInterruptLevel: s.PendingInterruptLevel,
            busInterruptLevel: s.BusInterruptLevel,
            willTakeInterrupt: s.WillTakeInterrupt,
            cpuStopped: s.CpuStopped,
            cpuFrozen: s.CpuFrozen,
            nextOpcode: s.NextOpcode,
            hintReq: s.HintReq,
            vintReq: s.VintReq,
            extReq: s.ExtReq,
            hintEnabled: s.HintEnabled,
            vintEnabled: s.VintEnabled,
            hintReqCount: s.HintReqCount,
            vintReqCount: s.VintReqCount,
            extReqCount: s.ExtReqCount,
            hintAckCount: s.HintAckCount,
            vintAckCount: s.VintAckCount,
            extAckCount: s.ExtAckCount,
            m68kEmuSliceExecInstructions: s.M68kEmuSliceExecInstructions,
            m68kEmuSliceDmaWaitCycles: s.M68kEmuSliceDmaWaitCycles,
            m68kEmuSliceDmaWaitEvents: s.M68kEmuSliceDmaWaitEvents,
            m68kEmuSliceRefreshWaitCycles: s.M68kEmuSliceRefreshWaitCycles,
            m68kEmuSliceRefreshWaitEvents: s.M68kEmuSliceRefreshWaitEvents,
            m68kEmuNoExecSliceStreak: s.M68kEmuNoExecSliceStreak,
            autoUnmaskFireCount: s.AutoUnmaskFireCount,
            runFrameEnterCount: s.RunFrameEnterCount,
            runFrameCompleteCount: s.RunFrameCompleteCount,
            runFrameLastFrame: s.RunFrameLastFrame,
            runFrameLastLines: s.RunFrameLastLines,
            runFrameLastM68kCalls: s.RunFrameLastM68kCalls,
            runFrameLastM68kBudget: s.RunFrameLastM68kBudget);
    }

    public string BuildMainTripwireSnapshot()
    {
        MainInterruptDebugState s = GetMainInterruptDebugState();
        uint m68k = GetM68kPc();
        ushort z80 = GetZ80Pc();
        long cycles = GetSystemCycles();
        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
        int line = md_main.g_md_vdp?.g_scanline ?? -1;
        int display = md_main.g_md_vdp?.g_vdp_reg_1_6_display ?? 0;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"frame={frame} line={line} display={display} m68k=0x{m68k:X6} z80=0x{z80:X4} cycles={cycles} " +
            $"irq_src={(s.UsingM68kEmu ? "m68kemu" : "legacy")} cfg={(s.M68kEmuConfigured ? 1 : 0)} ready={(s.M68kEmuReady ? 1 : 0)} " +
            $"sr=0x{s.Sr:X4} mask={s.InterruptMask} pending={s.PendingInterruptLevel} bus={s.BusInterruptLevel} take={(s.WillTakeInterrupt ? 1 : 0)} " +
            $"stopped={(s.CpuStopped ? 1 : 0)} frozen={(s.CpuFrozen ? 1 : 0)} op=0x{s.NextOpcode:X4} " +
            $"hreq={(s.HintReq ? 1 : 0)} vreq={(s.VintReq ? 1 : 0)} extreq={(s.ExtReq ? 1 : 0)} hen={(s.HintEnabled ? 1 : 0)} ven={(s.VintEnabled ? 1 : 0)} " +
            $"req[h={s.HintReqCount},v={s.VintReqCount},e={s.ExtReqCount}] ack[h={s.HintAckCount},v={s.VintAckCount},e={s.ExtAckCount}] " +
            $"slice[exec={s.M68kEmuSliceExecInstructions},dma={s.M68kEmuSliceDmaWaitCycles}/{s.M68kEmuSliceDmaWaitEvents},wait={s.M68kEmuSliceRefreshWaitCycles}/{s.M68kEmuSliceRefreshWaitEvents},noexec={s.M68kEmuNoExecSliceStreak}] autounmask={s.AutoUnmaskFireCount} " +
            $"runframe[e={s.RunFrameEnterCount},c={s.RunFrameCompleteCount},f={s.RunFrameLastFrame},lines={s.RunFrameLastLines},m68k={s.RunFrameLastM68kCalls}/{s.RunFrameLastM68kBudget}]");
    }

    public void SetYmEnabled(bool enabled)
    {
        _ymEnabled = enabled;
        md_bus.SetYmEnabled(enabled);
    }

    public void SetYmResampleLinear(bool linear)
    {
        _ymResampleLinear = linear;
    }

    public void SetZ80CycleMultiplier(double multiplier)
    {
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0.0)
            return;
        _z80CycleMultiplier = multiplier;
        md_main.SetZ80CycleMultiplier(multiplier);
    }

    public void SetRegionOverride(ConsoleRegion region)
    {
        _regionOverride = region;
        if (md_main.g_md_io != null)
            md_main.g_md_io.SetRegionOverride(region);
    }

    public void RunInterlaceMode2Test()
    {
        MdVdpInterlaceMode2PatternTest.Run();
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ROM path is empty.", nameof(path));

        Console.WriteLine($"[MdTracerAdapter] LoadRom request: path='{path}' ext='{Path.GetExtension(path)}' thread={Thread.CurrentThread.ManagedThreadId}");

        bool lockTaken = Monitor.TryEnter(_loadLock);
        const int maxAttempts = 50;
        int attempts = 0;
        while (!lockTaken && attempts++ < maxAttempts)
        {
            Thread.Sleep(10);
            lockTaken = Monitor.TryEnter(_loadLock);
        }

        if (!lockTaken)
        {
            Console.WriteLine("[MdTracerAdapter] LoadRom: lock timeout (500ms) – aborting load");
            return;
        }

        try
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            bool isSms = RomArchiveExtractor.IsSmsExtension(ext);
            string romDisplayName = Path.GetFileNameWithoutExtension(path);
            string romSourceName = path;

            if (!File.Exists(path))
            {
                string? fallback = TryResolveMissingRomPath(path);
                if (fallback == null)
                {
                    Console.WriteLine($"[MdTracerAdapter] LoadRom: file '{path}' not found.");
                    return;
                }

                Console.WriteLine($"[MdTracerAdapter] LoadRom: file not found, using '{fallback}'.");
                path = fallback;
                ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
                isSms = RomArchiveExtractor.IsSmsExtension(ext);
                romDisplayName = Path.GetFileNameWithoutExtension(path);
                romSourceName = path;
            }

            byte[] rawData;
            if (RomArchiveExtractor.IsArchivePath(path) || RomArchiveExtractor.HasArchiveHeader(path))
            {
                if (!RomArchiveExtractor.TryExtractRom(path, out rawData, out string entryName, out bool entryIsSms, out string? error))
                {
                    Console.WriteLine($"[MdTracerAdapter] LoadRom: failed to read archive '{path}': {error}");
                    return;
                }

                romSourceName = entryName;
                romDisplayName = Path.GetFileNameWithoutExtension(entryName);
                isSms = entryIsSms;
            }
            else
            {
                rawData = File.ReadAllBytes(path);
            }

            if (rawData.Length == 0)
            {
                Console.WriteLine($"[MdTracerAdapter] LoadRom: file '{path}' is empty.");
                return;
            }

            if (!isSms && LooksLikeSmsRom(rawData))
            {
                isSms = true;
                Console.WriteLine("[MdTracerAdapter] LoadRom: SMS header detected in raw data.");
            }

            _romIdentity = new RomIdentity(romDisplayName, RomIdentity.ComputeSha256(rawData));

            md_main.PowerCycleReset();
            md_main.initialize();
            MdTracerCore.MdLog.MaybeLogTraceBuildStamp();
            md_main.g_md_vdp = _vdp;

            if (isSms)
            {
                byte[] smsRom = NormalizeSmsRom(rawData);
                var smsHeader = TryParseSmsHeader(smsRom);
                SmsMapperType mapper = DetectSmsMapper(smsRom, smsHeader.Found);
                SmsMapperType? forcedMapper = TryParseSmsMapperOverride();
                if (forcedMapper.HasValue)
                {
                    Console.WriteLine($"[MdTracerAdapter] SMS mapper override: {forcedMapper.Value}");
                    mapper = forcedMapper.Value;
                }
                _rom = smsRom;
                md_main.g_masterSystemMode = true;
                md_main.g_masterSystemRom = smsRom;
                md_main.g_masterSystemRomSize = smsRom.Length;
                md_main.g_masterSystemRomPath = path;
                md_main.g_masterSystemMapper = mapper;
                if (md_main.g_md_io != null)
                    md_main.g_md_io.SetRomRegionHint(smsHeader.RegionHint);
                if (md_main.g_md_cartridge != null)
                {
                    md_main.g_md_cartridge.g_file = Array.Empty<byte>();
                    md_main.g_md_cartridge.g_file_size = 0;
                }
                _bus = null;
                md_bus.Current = null;
                EutherDrive.Core.MdTracerCore.md_bus.Current = null;
                if (md_main.g_md_bus != null)
                    md_main.g_md_bus.OverrideBus = null;
                _cpuReady = false;
                _cpu = null;

                RomInfo.Summary = BuildSmsSummary(smsRom.Length);
                RomInfo.ExtraInfo = BuildSmsExtraInfo(smsHeader, mapper);
                RomInfo.RegionHint = smsHeader.RegionHint;
                RomInfo.RegionHeaderRaw = smsHeader.RegionRaw ?? string.Empty;
                RomInfo.SerialNumber = smsHeader.ProductCode ?? string.Empty;
                Console.WriteLine($"[MdTracerAdapter] SMS headerFound={smsHeader.Found} mapper={mapper}");
            }
            else
            {
                md_main.g_masterSystemMode = false;
                md_main.g_masterSystemRom = Array.Empty<byte>();
                md_main.g_masterSystemRomSize = 0;

                md_main.g_md_cartridge ??= new md_cartridge();
                bool loaded = md_main.g_md_cartridge.load_from_bytes(rawData, romSourceName);
                if (!loaded)
                {
                    Console.WriteLine("[MdTracerAdapter] LoadRom: md_cartridge.load failed, using fallback ROM data.");
                    byte[] normalized = md_rom_utils.NormalizeMegaDriveRom(rawData).Data;
                    md_main.g_md_cartridge.g_file = normalized;
                    md_main.g_md_cartridge.g_file_size = normalized.Length;
                    md_main.g_md_cartridge.g_file_path = path;
                }

                // Always execute from the cartridge-normalized image when it differs from raw bytes.
                // This covers copier-header removal, SMD deinterleave, and word-swapped signatures.
                bool useNormalizedForBus = !ReferenceEquals(md_main.g_md_cartridge.g_file, rawData);
                _rom = useNormalizedForBus ? md_main.g_md_cartridge.g_file : rawData;
                Console.WriteLine(
                    $"[MdTracerAdapter] ROM source for bus: {(useNormalizedForBus ? "normalized" : "raw")} " +
                    $"(raw={rawData.Length} bytes, cart={md_main.g_md_cartridge.g_file.Length} bytes, " +
                    $"header={md_main.g_md_cartridge.g_smd_header_size}, deint={(md_main.g_md_cartridge.g_smd_deinterleaved ? 1 : 0)})");
                _bus = new MegaDriveBus(_rom);
                md_bus.Current = _bus;
                EutherDrive.Core.MdTracerCore.md_bus.Current = _bus;
                if (md_main.g_md_bus != null)
                {
                    if (md_main.g_md_cartridge?.g_mapper_is_svp == true)
                    {
                        md_main.g_md_bus.OverrideBus = new SvpBusOverride(md_main.g_md_cartridge.g_file);
                        Console.WriteLine("[MdTracerAdapter] Mapper: SVP override enabled.");
                    }
                    else
                    {
                        md_main.g_md_bus.OverrideBus = null;
                    }
                }

                DumpVectors();

                byte[] vecRom = md_main.g_md_cartridge?.g_file ?? _rom ?? rawData;
                string header = TryReadSegaString(vecRom);
                _bus.Write32(0xFF0000, 0x1234ABCD);
                uint wramProbe = _bus.Read32(0xFF0000);
                ConsoleRegion? regionHint = md_rom_utils.DetectRegionFromHeader(vecRom, out string regionRaw);
                string serial = md_main.g_md_cartridge?.g_serial_number ?? string.Empty;
                regionHint = AdjustRegionHint(regionHint, regionRaw, serial);
                RomInfo.RegionHint = regionHint;
                RomInfo.RegionHeaderRaw = regionRaw;
                RomInfo.SerialNumber = serial;
                string regionLabel = regionHint?.ToString() ?? ConsoleRegion.Auto.ToString();
                Console.WriteLine($"[MdTracerAdapter] Detected ROM region: {regionLabel} (raw='{regionRaw}')");
                if (md_main.g_md_io != null)
                    md_main.g_md_io.SetRomRegionHint(regionHint);

                uint sp = ReadBe32(vecRom, 0x000000);
                uint pc = ReadBe32(vecRom, 0x000004);
                if (!ValidateVectors(sp, pc, vecRom.Length))
                {
                    Console.WriteLine("[MdTracerAdapter] LoadRom: invalid vector table detected; aborting.");
                    return;
                }

                ushort op = ReadBe16(vecRom, (int)pc);

                try
                {
                    _cpu = new MdTracerM68kRunner();
                    _cpuReady = true;
                }
                catch (Exception ex)
                {
                    _cpuReady = false;
                    _cpu = null;
                    RomInfo.Summary = $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU: {ex.Message}";
                    Reset();
                    return;
                }

                RomInfo.Summary =
                $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | " +
                $"VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU API ok";
            }

            Console.WriteLine($"[ROMMODE] type={(isSms ? "SMS" : "MD")} masterSystemMode={(md_main.g_masterSystemMode ? 1 : 0)}");
            ArmBootRecover();
            Reset();
            LogFrameBufferIdentity("LoadRom");
        }
        finally
        {
            Monitor.Exit(_loadLock);
        }
    }

    private static string? TryResolveMissingRomPath(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        string file = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
            return null;

        string[] exts = { ".sms", ".bin", ".gg", ".sg" };
        foreach (string ext in exts)
        {
            string candidate = Path.Combine(dir, file + ext);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool LooksLikeSmsRom(byte[] data)
    {
        ReadOnlySpan<byte> mdMagic1 = "SEGA MEGA DRIVE"u8;
        ReadOnlySpan<byte> mdMagic2 = "SEGA GENESIS"u8;
        if (data.Length >= 0x110)
        {
            var header = data.AsSpan(0x100, 0x20);
            if (header.IndexOf(mdMagic1) >= 0 || header.IndexOf(mdMagic2) >= 0)
                return false;
        }

        ReadOnlySpan<byte> magic = stackalloc byte[] { 0x54, 0x4D, 0x52, 0x20, 0x53, 0x45, 0x47, 0x41 }; // "TMR SEGA"

        static bool MatchAt(byte[] src, int offset, ReadOnlySpan<byte> sig)
        {
            if ((uint)offset > (uint)(src.Length - sig.Length))
                return false;
            for (int i = 0; i < sig.Length; i++)
            {
                if (src[offset + i] != sig[i])
                    return false;
            }
            return true;
        }

        int[] bases32k = { 0x7FF0 };
        for (int baseOffset = 0; baseOffset + 0x7FF0 + magic.Length <= data.Length; baseOffset += 0x8000)
        {
            if (MatchAt(data, baseOffset + 0x7FF0, magic))
                return true;
        }

        for (int baseOffset = 0; baseOffset + 0x3FF0 + magic.Length <= data.Length; baseOffset += 0x4000)
        {
            if (MatchAt(data, baseOffset + 0x3FF0, magic))
                return true;
        }

        for (int baseOffset = 0; baseOffset + 0x1FF0 + magic.Length <= data.Length; baseOffset += 0x2000)
        {
            if (MatchAt(data, baseOffset + 0x1FF0, magic))
                return true;
        }

        if (data.Length >= 0x1000 && MatchAt(data, 0x0FF0, magic))
            return true;

        return false;
    }

    private static SmsHeaderInfo TryParseSmsHeader(byte[] data)
    {
        int[] headerOffsets = { 0x7FF0, 0x3FF0, 0x1FF0, 0x0FF0 };
        ReadOnlySpan<byte> magic = "TMR SEGA"u8;

        foreach (int start in headerOffsets)
        {
            if (data.Length < start + 16)
                continue;

            bool match = true;
            for (int i = 0; i < magic.Length; i++)
            {
                if (data[start + i] != magic[i])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;

            ushort checksum = (ushort)((data[start + 8] << 8) | data[start + 9]);
            string product = $"{data[start + 10]:X2}{data[start + 11]:X2}{data[start + 12]:X2}";
            byte version = data[start + 13];
            byte regionSize = data[start + 15];
            int regionCode = (regionSize >> 4) & 0x0F;
            int sizeCode = regionSize & 0x0F;

            string regionText = regionCode switch
            {
                3 => "Domestic/Japan",
                5 => "Domestic/Japan",
                4 => "Export/International",
                6 => "Export/International",
                7 => "Export/International",
                _ => $"Unknown(0x{regionCode:X})"
            };

            ConsoleRegion? regionHint = regionCode switch
            {
                3 => ConsoleRegion.JP,
                5 => ConsoleRegion.JP,
                4 => ConsoleRegion.US,
                6 => ConsoleRegion.US,
                7 => ConsoleRegion.US,
                _ => null
            };

            string sizeText = SmsSizeCodeToString(sizeCode, data.Length);
            string regionRaw = $"region=0x{regionCode:X} size=0x{sizeCode:X} ver=0x{version:X2} checksum=0x{checksum:X4}";
            string summary = $"Header@0x{start:X4} prod={product} {regionText} size={sizeText}";

            return new SmsHeaderInfo(true, summary, regionHint, regionRaw, product);
        }

        return new SmsHeaderInfo(false, "No SMS header found", null, null, null);
    }

    private static string SmsSizeCodeToString(int sizeCode, int actualBytes)
    {
        string sizeFromHeader = sizeCode switch
        {
            0x0 => "256KB",
            0x1 => "512KB",
            0x2 => "1MB",
            0x3 => "2MB",
            0x4 => "4MB",
            0x5 => "8MB",
            0x6 => "16MB",
            _ => $"Unknown(0x{sizeCode:X})"
        };

        if (actualBytes > 0)
            return $"{sizeFromHeader} (actual {actualBytes / 1024}KB)";

        return sizeFromHeader;
    }

    private static SmsMapperType DetectSmsMapper(byte[] rom, bool headerFound)
    {
        const int codemastersChecksumAddr = 0x7FE6;
        if (rom.Length < 32 * 1024)
            return SmsMapperType.Sega;

        if (rom.Length <= codemastersChecksumAddr + 1)
            return SmsMapperType.Sega;

        ushort expected = (ushort)(rom[codemastersChecksumAddr] | (rom[codemastersChecksumAddr + 1] << 8));
        ushort checksum = 0;
        for (int address = 0; address + 1 < rom.Length; address += 2)
        {
            if (address >= 0x7FF0 && address <= 0x7FFF)
                continue;
            ushort word = (ushort)(rom[address] | (rom[address + 1] << 8));
            checksum = (ushort)(checksum + word);
        }

        if (checksum == expected)
            return SmsMapperType.Codemasters;

        // Heuristics for headerless or ambiguous ROMs (Korean mappers are common here).
        if (LooksLikeKoreanA000Mapper(rom))
            return SmsMapperType.KoreanA000;
        if (LooksLikeKorean6000RamMapper(rom))
            return SmsMapperType.Korean6000Ram;
        if (LooksLikeSegaMapper(rom))
            return SmsMapperType.Sega;
        if (!headerFound && LooksLikeCodemastersMapper(rom))
            return SmsMapperType.Codemasters;

        return SmsMapperType.Sega;
    }

    private static bool LooksLikeCodemastersMapper(byte[] rom)
    {
        // Heuristic for unlicensed/no-header ROMs: look for repeated LD (nn),A targeting
        // typical Codemasters-style bank registers (0x0000..0x3FFF and 0x8000..0xBFFF).
        int writesToA000 = 0;
        int writesTo0000 = 0;
        int writesTo4000 = 0;
        int writesTo8000 = 0;
        for (int i = 0; i + 2 < rom.Length; i++)
        {
            if (rom[i] != 0x32) // LD (nn),A
                continue;
            ushort addr = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
            if (addr == 0xA000)
                writesToA000++;
            else if (addr == 0x0000)
                writesTo0000++;
            else if (addr == 0x4000)
                writesTo4000++;
            else if (addr == 0x8000)
                writesTo8000++;
        }

        // Codemasters typically uses writes at 0x0000, 0x4000, and 0x8000 for bank switches.
        bool hasCodemastersTriplet =
            writesTo0000 >= 2 && writesTo4000 >= 2 && writesTo8000 >= 2;
        // Some titles poke 0xA000 for RAM enable, but don't use it as the sole signal.
        return hasCodemastersTriplet || (writesToA000 >= 8 && writesTo8000 >= 2);
    }

    private static bool LooksLikeSegaMapper(byte[] rom)
    {
        int writesToFffc = 0;
        int writesToFffd = 0;
        int writesToFffe = 0;
        int writesToFfff = 0;
        for (int i = 0; i + 2 < rom.Length; i++)
        {
            if (rom[i] != 0x32) // LD (nn),A
                continue;
            ushort addr = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
            switch (addr)
            {
                case 0xFFFC: writesToFffc++; break;
                case 0xFFFD: writesToFffd++; break;
                case 0xFFFE: writesToFffe++; break;
                case 0xFFFF: writesToFfff++; break;
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_MAPPER_DETECT"), "1", StringComparison.Ordinal))
        {
            Console.WriteLine($"[SMS MAP DETECT] Sega: FFFC={writesToFffc} FFFD={writesToFffd} FFFE={writesToFffe} FFFF={writesToFfff}");
        }

        int total = writesToFffc + writesToFffd + writesToFffe + writesToFfff;
        return total >= 2;
    }

    private static bool LooksLikeKoreanA000Mapper(byte[] rom)
    {
        // Heuristic for some Korean 1MB/2MB games: heavy use of LD (0xA000),A without header.
        int writesToA000 = 0;
        int writesTo0000 = 0;
        int writesTo4000 = 0;
        for (int i = 0; i + 2 < rom.Length; i++)
        {
            if (rom[i] != 0x32) // LD (nn),A
                continue;
            ushort addr = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
            if (addr == 0xA000)
                writesToA000++;
            else if (addr == 0x0000)
                writesTo0000++;
            else if (addr == 0x4000)
                writesTo4000++;
        }

        bool midRom = rom.Length >= 256 * 1024;
        bool bigRom = rom.Length >= 512 * 1024;
        bool hugeRom = rom.Length >= 1024 * 1024;
        if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_MAPPER_DETECT"), "1", StringComparison.Ordinal))
        {
            Console.WriteLine($"[SMS MAP DETECT] KoreanA000: A000={writesToA000} 0000={writesTo0000} 4000={writesTo4000} size={rom.Length}");
        }

        if (writesToA000 >= 16)
            return true;
        if (writesToA000 >= 8 && midRom)
            return true;
        if (writesToA000 >= 6 && bigRom)
            return true;
        if (hugeRom && writesToA000 >= 2)
            return true;
        return writesToA000 >= 4 && (writesTo0000 > 0 || writesTo4000 > 0) && bigRom;
    }

    private static bool LooksLikeKorean6000RamMapper(byte[] rom)
    {
        int writesTo6000 = 0;
        int writesToA000 = 0;
        for (int i = 0; i + 2 < rom.Length; i++)
        {
            if (rom[i] != 0x32)
                continue;
            ushort addr = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
            if (addr >= 0x6000 && addr <= 0x7FFF)
                writesTo6000++;
            else if (addr == 0xA000)
                writesToA000++;
        }

        return writesTo6000 >= 16 && writesToA000 > 0 && rom.Length >= 512 * 1024;
    }

    private static SmsMapperType? TryParseSmsMapperOverride()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_FORCE_MAPPER");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        raw = raw.Trim();
        return raw.ToLowerInvariant() switch
        {
            "sega" => SmsMapperType.Sega,
            "codemasters" => SmsMapperType.Codemasters,
            "koreana000" => SmsMapperType.KoreanA000,
            "korean_a000" => SmsMapperType.KoreanA000,
            "korean6000ram" => SmsMapperType.Korean6000Ram,
            "korean_6000_ram" => SmsMapperType.Korean6000Ram,
            "korean6000ramwide" => SmsMapperType.Korean6000RamWide,
            "korean_6000_ram_wide" => SmsMapperType.Korean6000RamWide,
            "msx" => SmsMapperType.Msx8k,
            "msx8k" => SmsMapperType.Msx8k,
            "nemesis" => SmsMapperType.Nemesis,
            _ => null
        };
    }

    private static string BuildSmsSummary(int length)
    {
        return $"SMS ROM bytes: {length}";
    }

    private static string BuildSmsExtraInfo(SmsHeaderInfo header, SmsMapperType mapper)
    {
        string headerInfo = header.Found ? header.Summary : "No SMS header";
        return $"{headerInfo} | mapper={mapper}";
    }

    private readonly record struct SmsHeaderInfo(
        bool Found,
        string Summary,
        ConsoleRegion? RegionHint,
        string? RegionRaw,
        string? ProductCode);

    private static string TryReadSegaString(MegaDriveBus bus)
    {
        Span<byte> s = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            s[i] = bus.Read8((uint)(0x100 + i));

        var text = Encoding.ASCII.GetString(s);
        return $"Header@0x100: '{text}'";
    }

    private static string TryReadSegaString(byte[] rom)
    {
        if (rom.Length < 0x104)
            return "Header@0x100: '(too small)'";

        Span<byte> s = stackalloc byte[4];
        s[0] = rom[0x100];
        s[1] = rom[0x101];
        s[2] = rom[0x102];
        s[3] = rom[0x103];
        var text = Encoding.ASCII.GetString(s);
        return $"Header@0x100: '{text}'";
    }

    private static double ParseZ80CycleMultiplier()
    {
        const double fallback = 1.0; // Z80 runs at full 3.58MHz, bus contention handled via stalls
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_CYCLES_MULT");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0.0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverStallFrames()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_STALL_FRAMES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverWindowFrames()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_WINDOW_FRAMES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverEdgeToggleThreshold()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_EDGE_TOGGLES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverEdgeStableFrames()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_EDGE_STABLE_FRAMES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseNonNegativeInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0)
            return parsed;
        return fallback;
    }

    private static bool IsEnvFlagSet(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        raw = raw.Trim();
        return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly int TraceZ80FrameCyclesEvery =
        ParseNonNegativeInt("EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES_EVERY", 60);

    private static ConsoleRegion? ParseRegionOverrideEnv()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_REGION");
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "jp":
            case "japan":
                return ConsoleRegion.JP;
            case "us":
            case "usa":
                return ConsoleRegion.US;
            case "eu":
            case "europe":
                return ConsoleRegion.EU;
            case "auto":
                return null;
            default:
                return null;
        }
    }

    private static ConsoleRegion? AdjustRegionHint(ConsoleRegion? hint, string regionRaw, string serial)
    {
        string upper = regionRaw?.Trim().ToUpperInvariant() ?? string.Empty;
        string serialUpper = serial?.Trim().ToUpperInvariant() ?? string.Empty;
        bool hasJ = upper.Contains('J');
        bool hasU = upper.Contains('U');
        bool hasE = upper.Contains('E');
        bool serialPal = serialUpper.EndsWith("-50", StringComparison.Ordinal);

        if (serialPal && hasE)
            return ConsoleRegion.EU;
        if (hint.HasValue)
            return hint;
        if (hasE && !hasJ && !hasU)
            return ConsoleRegion.EU;
        if (hasU && !hasJ && !hasE)
            return ConsoleRegion.US;
        if (hasJ && !hasU && !hasE)
            return ConsoleRegion.JP;
        if (serialPal)
            return ConsoleRegion.EU;
        return hint;
    }

    private static ushort ReadBe16(byte[] rom, int offset)
    {
        if ((uint)(offset + 1) >= (uint)rom.Length)
            return 0;
        return (ushort)((rom[offset] << 8) | rom[offset + 1]);
    }

    private static uint ReadBe32(byte[] rom, int offset)
    {
        if ((uint)(offset + 3) >= (uint)rom.Length)
            return 0;
        return (uint)((rom[offset] << 24) | (rom[offset + 1] << 16) | (rom[offset + 2] << 8) | rom[offset + 3]);
    }
    private static byte[] NormalizeSmsRom(byte[] rawData)
    {
        if (rawData.Length >= 0x200 && (rawData.Length % 0x4000) == 0x200)
        {
            byte[] trimmed = new byte[rawData.Length - 0x200];
            Buffer.BlockCopy(rawData, 0x200, trimmed, 0, trimmed.Length);
            rawData = trimmed;
        }

        // Some dumps are shifted by 0x180 bytes (header appears at 0x3E70 instead of 0x3FF0).
        // Detect and rotate so the "TMR SEGA" header lands on the standard boundary.
        int headerIndex = IndexOfSmsHeader(rawData);
        if (headerIndex >= 0 && (headerIndex & 0x3FFF) == 0x3E70)
        {
            const int shift = 0x180;
            byte[] rotated = new byte[rawData.Length];
            int tailStart = rawData.Length - shift;
            Buffer.BlockCopy(rawData, tailStart, rotated, 0, shift);
            Buffer.BlockCopy(rawData, 0, rotated, shift, tailStart);
            return rotated;
        }

        return rawData;
    }

    private static int IndexOfSmsHeader(byte[] data)
    {
        ReadOnlySpan<byte> magic = "TMR SEGA"u8;
        for (int i = 0; i + magic.Length <= data.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < magic.Length; j++)
            {
                if (data[i + j] != magic[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private void ArmBootRecover()
    {
        _bootRecoverStallCount = 0;
        _bootRecoverFrameCount = 0;
        _bootRecoverCompleted = false;
        _bootRecoverLastPc = 0;
        _bootRecoverStablePcFrames = 0;
        _bootRecoverLastBusReqToggles = 0;
        _bootRecoverLastResetToggles = 0;
        _bootRecoverToggleAccum = 0;
        _bootRecoverSigInit = false;
        _sonic2AudioRecoveryFramesRemaining = 0;
        _sonic2RuntimeStallFrames = 0;
        _sonic2RuntimeRecoveryCount = 0;
        _captainAmericaMailboxWaitGroup = 0;
        _captainAmericaMailboxStableFrames = 0;
        _captainAmericaMailboxLastSemState = 0;
    }

    private void ResetZ80Only()
    {
        if (md_main.g_md_z80 == null)
            return;
        if (md_z80.IsJgenesisCoreEnabled)
        {
            // Keep startup deterministic in jgenesis mode across ROM swaps/reset paths:
            // reset core state + clear RAM, but do not add post-reset hold delays here.
            md_main.BeginZ80ResetCycle();
            md_main.g_md_z80.reset();
            md_main.g_md_z80.ClearZ80Ram();
            bool z80BusReq = md_main.g_md_bus?.Z80BusGranted ?? false;
            bool z80Reset = md_main.g_md_bus?.Z80Reset ?? false;
            md_main.g_md_z80.g_active = !z80BusReq && !z80Reset;
            return;
        }
        md_main.BeginZ80ResetCycle();
        md_main.g_md_z80.reset();
        md_main.g_md_z80.ArmPostResetHold();
        bool fallbackBusReq = md_main.g_md_bus?.Z80BusGranted ?? false;
        bool fallbackReset = md_main.g_md_bus?.Z80Reset ?? false;
        md_main.g_md_z80.g_active = !fallbackBusReq && !fallbackReset;
    }

    public void Reset()
    {
        Console.WriteLine("[MdTracerAdapter] Reset begin - SHINOBI DEBUG");
        if (TraceReset)
            Console.WriteLine($"[MdTracerAdapter] Reset stack:\n{Environment.StackTrace}");
        Console.WriteLine($"[MdTracerAdapter] Reset _cpuReady={_cpuReady} _cpu={(_cpu != null ? "ok" : "null")}");
        _tick = 0;

        // Nollställ RAM
        _bus?.Reset();
        md_main.g_md_bus?.Reset();
        md_main.ResetZ80WaitState();
        _vdp.reset();
        _lastAppliedFrameRateMode = GetEffectiveFrameRateMode();
        ApplyFrameRateMode(_lastAppliedFrameRateMode);
        ResetAudioFrameState();
        // Reset YM2612 state including Z80 safe boot flags
        // Ensure music system is initialized before resetting
        md_main.g_md_music ??= new md_music();
        
        // CRITICAL FIX: Always call reset() on music system to ensure YM2612_Start() is called
        // FullReset() assumes YM2612_Start() has already initialized arrays
        md_main.g_md_music.reset();
        
        Console.WriteLine($"[RESET-DEBUG] Calling YM2612.FullReset()");
        md_main.g_md_music.YmFullReset();
        
        // CRITICAL FIX: Reset Z80 when system resets to ensure proper timing synchronization
        // Z80 must start from address 0 at the same time as M68K and YM2612
        if (md_main.g_md_z80 != null)
        {
            Console.WriteLine($"[RESET-DEBUG] Resetting Z80 for timing synchronization");
            ResetZ80Only();
            md_main.g_md_z80.ForceSmsStackDefault();
        }
        
        // CRITICAL FIX: Generate initial audio samples to avoid underrun
        // YM2612 and PSG need to generate samples before first GetAudioBuffer() call
        // Note: This must be called AFTER ResetAudioFrameState() to avoid being reset
        // Actually, let's call it at the end of Reset()

        // Stub (så VDP-testet fortsätter)
        md_main.EnsureCpuStubs();

        // Init/reset CPU om vi har den
        if (_cpuReady && _cpu != null)
        {
            // md_bus.Current är redan satt i LoadRom
            _cpu.EnsureInitAndReset();

            // Bra att skriva en gång i terminalen (kan tas bort sen)
            MdTracerCore.MdLog.WriteLine("m68k runner ok. Runner: " + _cpu.SelectedRunApi);
            MdTracerCore.MdLog.WriteLine("Methods:\n" + _cpu.DebugApi);
        }

        _vdp.SetFrameSize(DefaultW, DefaultH);
        EnsureFramebufferInitialized("Reset");
        Array.Clear(_frameBufferFront, 0, _frameBufferFront.Length);
        Array.Clear(_frameBufferBack, 0, _frameBufferBack.Length);
        Console.WriteLine($"[MdTracerAdapter] Reset framebuffer { _fbW }x{ _fbH } stride={ _fbStride }");
        LogFrameBufferIdentity("Reset");
        
        // CRITICAL FIX: Generate initial audio samples to avoid underrun
        // YM2612 and PSG need to generate samples before first GetAudioBuffer() call
        // Called here at the end of Reset() after ResetAudioFrameState()
        Console.WriteLine($"[RESET-DEBUG] Calling GenerateInitialAudioSamples()...");
        GenerateInitialAudioSamples();
        Console.WriteLine($"[RESET-DEBUG] GenerateInitialAudioSamples() completed");
    }

    public void TriggerSmsDump()
    {
        md_main.g_md_vdp?.TriggerSmsDump();
    }

    public string CaptureDebugSnapshot(string? directory = null)
    {
        string dir = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.CurrentDirectory, "logs", "snapshots")
            : directory;
        Directory.CreateDirectory(dir);

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string prefix = $"mdsnap_{stamp}";

        string ppmPath = Path.Combine(dir, $"{prefix}_screen.ppm");
        DumpFrameBufferToPpm(ppmPath);

        byte[]? mem = md_m68k.g_memory;
        if (mem != null && mem.Length >= 0x1000000)
        {
            const int workRamStart = 0xFF0000;
            const int workRamLength = 0x10000;
            var workRam = new byte[workRamLength];
            Buffer.BlockCopy(mem, workRamStart, workRam, 0, workRamLength);
            File.WriteAllBytes(Path.Combine(dir, $"{prefix}_ram_ff0000.bin"), workRam);
        }

        md_main.g_md_vdp?.DumpMdMemorySnapshot(dir, prefix);
        ForceDumpZ80("debug snapshot", extra: true, dumpPath: Path.Combine(dir, $"{prefix}_z80_ram.txt"));
        string rawStatePath = Path.Combine(dir, $"{prefix}_state_raw.bin");
        using (var fs = new FileStream(rawStatePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
        {
            SaveState(writer);
            writer.Flush();
        }

        string metaPath = Path.Combine(dir, $"{prefix}_meta.txt");
        using (var writer = new StreamWriter(metaPath, false, Encoding.UTF8))
        {
            writer.WriteLine($"stamp={stamp}");
            writer.WriteLine($"frame={(md_main.g_md_vdp?.FrameCounter ?? -1)}");
            writer.WriteLine($"pc=0x{md_m68k.g_reg_PC:X6}");
            writer.WriteLine($"rom={md_main.g_md_cartridge?.g_file_path ?? string.Empty}");
            writer.WriteLine($"screen={Path.GetFileName(ppmPath)}");
            writer.WriteLine($"raw_state={Path.GetFileName(rawStatePath)}");
        }

        return Path.Combine(dir, prefix);
    }

    public static string? GetPadUiText()
    {
        return MdTracerCore.md_io.PadUiText;
    }

    public static void SetPad2Mirror(bool enabled)
    {
        MdTracerCore.md_io.Pad2MirrorEnabled = enabled;
    }

    public static void SetPadUiTrace(bool enabled)
    {
        MdTracerCore.md_io.PadUiTraceEnabled = enabled;
    }

    public void SetFrameRateMode(FrameRateMode mode)
    {
        _frameRateMode = mode;
    }

    public double GetTargetFps()
    {
        return GetEffectiveFrameRateMode() == FrameRateMode.Hz50 ? FPS_PAL : FPS_NTSC;
    }

    public void SetCpuCyclesPerLine(int cycles)
    {
        if (cycles <= 0)
            throw new ArgumentOutOfRangeException(nameof(cycles), "Cycles must be positive.");
        _cpuCyclesPerLine = cycles;
    }

    private FrameRateMode GetEffectiveFrameRateMode()
    {
        if (_frameRateMode != FrameRateMode.Auto)
            return _frameRateMode;

        if (RomInfo.RegionHint == ConsoleRegion.EU)
            return FrameRateMode.Hz50;
        if (RomInfo.RegionHint == ConsoleRegion.JP || RomInfo.RegionHint == ConsoleRegion.US)
            return FrameRateMode.Hz60;

        ConsoleRegion region = GetEffectiveRegion();
        return region == ConsoleRegion.EU ? FrameRateMode.Hz50 : FrameRateMode.Hz60;
    }

    private ConsoleRegion GetEffectiveRegion()
    {
        ConsoleRegion? envOverride = ParseRegionOverrideEnv();
        if (envOverride.HasValue)
            return envOverride.Value;
        if (_regionOverride != ConsoleRegion.Auto)
            return _regionOverride;
        if (RomInfo.RegionHint.HasValue)
            return RomInfo.RegionHint.Value;
        return ConsoleRegion.US;
    }

    private int ApplyFrameRateMode(FrameRateMode mode)
    {
        int lines;
        bool isPal = mode == FrameRateMode.Hz50;
        if (md_main.g_masterSystemMode)
            lines = isPal ? SMS_VLINES_PAL : SMS_VLINES_NTSC;
        else
            lines = isPal ? VLINES_PAL : VLINES_NTSC;
        if (_vdp.g_vertical_line_max != lines)
        {
            _vdp.g_vertical_line_max = lines;
            _vdp.g_vdp_reg_1_3_cellmode = (byte)(isPal ? 1 : 0);
            _vdp.g_vdp_status_0_tvmode = (byte)(isPal ? 1 : 0);
        }
        return lines;
    }

    private void ResetAudioFrameState()
    {
        // TEST: Try NEGATIVE accumulator to see if audio starts earlier
        // Original: 0.5 frames (367.5 samples) to avoid underflow
        // Previous test: 0.0 frames
        // New test: -0.5 frames (-367.5 samples) - start "behind" to catch up?
        // GEMS games need negative audio accumulator for correct timing
        // Use EUTHERDRIVE_GEMS_TIMING=1 to force GEMS timing without pitch change
        string? gemsTimingEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_GEMS_TIMING");
        bool isGemsTiming = gemsTimingEnv != null && gemsTimingEnv.Equals("1", StringComparison.OrdinalIgnoreCase);

        // Universal override: EUTHERDRIVE_AUDIO_ACCUM_FRAMES=<float>
        // When set, it overrides any GEMS/non-GEMS default.
        double? accumulatorOverride = null;
        string? accumulatorEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_ACCUM_FRAMES");
        if (!string.IsNullOrWhiteSpace(accumulatorEnv)
            && double.TryParse(accumulatorEnv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double overrideFrames))
        {
            accumulatorOverride = overrideFrames;
        }

        double accumulatorFrames = accumulatorOverride ?? (isGemsTiming ? -5.0 : 0.5);
        _psgFrameAccumulator = (double)OutputSampleRate / GetTargetFps() * accumulatorFrames;
        _psgFrameSamples = 0;
        _psgLastFrame = -1;
        _psgResamplePhase = 0;
        _psgResampleHasCarry = false;
        _psgResampleCarry = 0;
        _ymResamplePhase = 0;
        _ymResampleHasCarry = false;
        _ymDcFilterL.Reset();
        _ymDcFilterR.Reset();
        _psgDcFilter.Reset();
        _ymLowPassL.Reset();
        _ymLowPassR.Reset();
        _psgLowPass.Reset();
        _ymLowPass2L.Reset();
        _ymLowPass2R.Reset();
        _jgAudioFilter.Reset();
        _jgPsgPreResampleFilter?.Reset();
        _jgYmPreResampleFilter?.Reset();
        // Note: _audioSystemReady is managed separately
        
        if (TraceAudioDebug)
        {
            Console.WriteLine($"[AUDIO-TIMING] ResetAudioFrameState: _psgFrameAccumulator={_psgFrameAccumulator:F2} (isGemsTiming={isGemsTiming}, accumulatorFrames={accumulatorFrames:F1}, override={(accumulatorOverride.HasValue ? 1 : 0)})");
        }
    }

    public void PowerCycleAndLoadRom(string path) => LoadRom(path);

    public void HardFlushAudioState()
    {
        // Also reset Z80-side timing/latches to avoid stale startup tone patterns.
        md_main.ResetZ80WaitState();
        if (md_main.g_md_z80 != null)
        {
            ResetZ80Only();
            md_main.g_md_z80.ForceSmsStackDefault();
        }

        // Hard flush inside sound core as well (YM/PSG internals), not just UI/audio engine state.
        md_main.g_md_music ??= new md_music();
        md_main.g_md_music.reset();
        md_main.g_md_music.YmFullReset();

        ResetAudioFrameState();
        GenerateInitialAudioSamples();
        if (TraceAudioDebug)
            Console.WriteLine("[AUDIO-TIMING] HardFlushAudioState completed.");
    }
    
    private void GenerateInitialAudioSamples()
    {
        // Do not synthesize startup audio here.
        // Pre-generating samples before any emulation time has passed can create
        // audible overlays/double-voice artifacts during first seconds of boot.
        _audioSystemReady = true;
        _audioWarmupFrames = 0;
        _audioGeneratedThisFrame = false;
        _psgFrameSamples = 0;
        _psgLastFrame = -1;
        _psgResamplePhase = 0;
        _psgResampleHasCarry = false;
        _psgResampleCarry = 0;
        _ymResamplePhase = 0;
        _ymResampleHasCarry = false;
        _ymResampleCarryL = 0;
        _ymResampleCarryR = 0;
        if (_psgFrameBuffer.Length > 0)
            Array.Clear(_psgFrameBuffer, 0, _psgFrameBuffer.Length);
        if (_ymFrameBuffer.Length > 0)
            Array.Clear(_ymFrameBuffer, 0, _ymFrameBuffer.Length);
        if (_psgInternalBuffer.Length > 0)
            Array.Clear(_psgInternalBuffer, 0, _psgInternalBuffer.Length);
        if (_ymInternalBuffer.Length > 0)
            Array.Clear(_ymInternalBuffer, 0, _ymInternalBuffer.Length);
        _jgAudioFilter.Reset();
        _jgPsgPreResampleFilter?.Reset();
        _jgYmPreResampleFilter?.Reset();

        if (TraceAudioDebug)
            Console.WriteLine("[AUDIO-TIMING] Audio startup state initialized (no synthetic prefill).");
    }

    private void EnsureFramebufferInitialized(string reason)
    {
        int w = _vdp.FrameWidth;
        int h = _vdp.FrameHeight;
        if (w <= 0 || h <= 0)
        {
            w = DefaultW;
            h = DefaultH;
            _vdp.SetFrameSize(w, h);
        }

        int stride = Math.Max(4, w * 4);
        if (_fbW != w || _fbH != h || _fbStride != stride)
        {
            _fbW = w;
            _fbH = h;
            _fbStride = stride;
            MdTracerCore.MdLog.WriteLine($"[MdTracerAdapter] EnsureFramebufferInitialized({reason}) -> {_fbW}x{_fbH} stride={_fbStride}");
        }

        int needed = _fbW * _fbH * 4;
        if (_frameBufferFront.Length != needed)
            _frameBufferFront = new byte[needed];
        if (_frameBufferBack.Length != needed)
            _frameBufferBack = new byte[needed];

        if (FrameBufferTraceEnabled && _fbIdentityLogCount++ < 10)
        {
            var vdpBuffer = _vdp.GetFrameBuffer();
            int vdpId = vdpBuffer.Length == 0 ? 0 : RuntimeHelpers.GetHashCode(vdpBuffer);
            Console.WriteLine($"[MdTracerAdapter] Framebuffer source at {reason}: vdp=0x{vdpId:X8} length={vdpBuffer.Length}");
        }
    }

    private void DumpVectors()
    {
        if (!DumpVectorsEnabled || _rom == null || _bus == null)
            return;

        int count = Math.Min(_rom.Length, 32);
        var sb = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(_rom[i].ToString("X2"));
        }

        Console.WriteLine($"[ROM0] {sb}");
        uint sp = _bus.Read32(0x000000);
        uint pc = _bus.Read32(0x000004);
        Console.WriteLine($"[VECTORS] SP=0x{sp:X8} PC=0x{pc:X8}");
    }

    private void ValidateVectors(uint sp, uint pc)
    {
        if (_rom == null)
            return;

        bool spReasonable = sp != 0 && (sp >= 0x00F00000 || sp >= 0xFFF00000);
        bool pcReasonable = pc < Math.Min(_rom.Length, 0x400000);
        Console.WriteLine($"[MdTracerAdapter] Vector check SP=0x{sp:X8} ({(spReasonable ? "ok" : "sus")}) PC=0x{pc:X8} ({(pcReasonable ? "ok" : "sus")})");
    }

    private bool ValidateVectors(uint sp, uint pc, int romLength)
    {
        Console.WriteLine($"[MdTracerAdapter] Vector check SP=0x{sp:X8} PC=0x{pc:X8}");
        return pc > 0 && pc < romLength && pc < 0x400000;
    }

    public void RunFrame()
    {
        lock (_stateLock)
        {
            if (_tick == 0)
            {
                MdTracerCore.MdLog.WriteLine("[MdTracerAdapter] RunFrame start");
                if (AsciiStreamEnabled)
                    Console.WriteLine("[MdTracerAdapter] ASCII stream ENABLED");
            }

        _tick++;
        if (!_bootRecoverCompleted && (BootRecoverStallFrames > 0 || BootRecoverEdgeToggleThreshold > 0))
        {
            _bootRecoverFrameCount++;
            if (BootRecoverWindowFrames > 0 && _bootRecoverFrameCount > BootRecoverWindowFrames)
            {
                _bootRecoverCompleted = true;
            }
            else
            {
                var z80 = md_main.g_md_z80;
                var bus = md_main.g_md_bus;
                bool busReq = bus?.Z80BusGranted ?? false;
                bool reset = bus?.Z80Reset ?? false;
                bool active = z80?.g_active ?? false;
                ushort pc = z80?.CpuPc ?? (ushort)0;

                bool pcStable = pc == _bootRecoverLastPc;
                if (pcStable)
                {
                    _bootRecoverStablePcFrames++;
                }
                else
                {
                    _bootRecoverStablePcFrames = 0;
                    _bootRecoverToggleAccum = 0;
                    _bootRecoverLastPc = pc;
                }

                if (BootRecoverEdgeToggleThreshold > 0 && bus != null)
                {
                    if (!_bootRecoverSigInit)
                    {
                        bus.PeekZ80SignalStats(out _, out long busReqToggles, out _, out long resetToggles);
                        _bootRecoverLastBusReqToggles = busReqToggles;
                        _bootRecoverLastResetToggles = resetToggles;
                        _bootRecoverSigInit = true;
                    }
                    else
                    {
                        bus.PeekZ80SignalStats(out _, out long busReqToggles, out _, out long resetToggles);
                        long deltaBusReqToggles = busReqToggles - _bootRecoverLastBusReqToggles;
                        long deltaResetToggles = resetToggles - _bootRecoverLastResetToggles;
                        if (deltaBusReqToggles < 0)
                            deltaBusReqToggles = 0;
                        if (deltaResetToggles < 0)
                            deltaResetToggles = 0;
                        _bootRecoverLastBusReqToggles = busReqToggles;
                        _bootRecoverLastResetToggles = resetToggles;

                        if (pcStable)
                            _bootRecoverToggleAccum += deltaBusReqToggles + deltaResetToggles;

                        int stableFramesTarget = BootRecoverEdgeStableFrames > 0
                            ? BootRecoverEdgeStableFrames
                            : (BootRecoverStallFrames > 0 ? BootRecoverStallFrames : 1);
                        if (stableFramesTarget > 0 &&
                            _bootRecoverStablePcFrames >= stableFramesTarget &&
                            _bootRecoverToggleAccum >= BootRecoverEdgeToggleThreshold &&
                            !active)
                        {
                            _bootRecoverCompleted = true;
                            if (BootRecoverLog)
                            {
                                Console.WriteLine(
                                    $"[BOOTRECOVER] reset after edge toggles={_bootRecoverToggleAccum} " +
                                    $"stable={_bootRecoverStablePcFrames} pc=0x{pc:X4} " +
                                    $"busReqT={deltaBusReqToggles} resetT={deltaResetToggles} " +
                                    $"busReq={(busReq ? 1 : 0)} reset={(reset ? 1 : 0)}");
                            }
                            ResetZ80Only();
                            return;
                        }
                    }
                }

                bool stalled = busReq && !reset && !active && pcStable;
                if (stalled)
                {
                    _bootRecoverStallCount++;
                }
                else
                {
                    _bootRecoverStallCount = 0;
                }
                if (BootRecoverStallFrames > 0 && _bootRecoverStallCount >= BootRecoverStallFrames)
                {
                    _bootRecoverCompleted = true;
                    if (BootRecoverLog)
                    {
                        Console.WriteLine(
                            $"[BOOTRECOVER] reset after stall frames={_bootRecoverStallCount} " +
                            $"pc=0x{pc:X4} busReq={(busReq ? 1 : 0)} reset={(reset ? 1 : 0)}");
                    }
                    ResetZ80Only();
                    return;
                }
            }
        }
        long frameStart = TracePerf ? Stopwatch.GetTimestamp() : 0;

        if (md_main.g_masterSystemMode)
        {
            md_main.RunFrame();
        }
        else
        {
            var effectiveFrameRateMode = GetEffectiveFrameRateMode();
            if (effectiveFrameRateMode != _lastAppliedFrameRateMode)
            {
                _lastAppliedFrameRateMode = effectiveFrameRateMode;
                ResetAudioFrameState();
            }
            int vlines = ApplyFrameRateMode(effectiveFrameRateMode);
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            long systemCyclesBeforeFrame = md_main.SystemCycles;
            MaybeCaptainAmericaMailboxRecovery(frame);
            MaybeSonic2AudioRecovery(frame);
            if (TracePcPerFrame && TracePcEveryFrames >= 0)
            {
                if (TracePcEveryFrames == 0 || (frame >= 0 && frame % TracePcEveryFrames == 0))
                    Console.WriteLine($"[PCFRAME] frame={frame} pc=0x{md_m68k.g_reg_PC:X6}");
            }
                bool allowZ80 = md_main.ShouldRunZ80(frame);
                
                // CRITICAL: Tick Z80 safe boot timer BEFORE Z80 runs
                // This allows Z80 to run immediately when reset is released
                md_main.g_md_bus?.TickZ80SafeBoot(frame);
                
                uint pcAfter = md_m68k.g_reg_PC;
            if (_cpuReady && _cpu != null)
            {
                long cpuTicks = 0;
                long vdpTicks = 0;
                int z80Budget = md_main.GetZ80CyclesPerLine();
                int cpuBudget = _cpuCyclesPerLine > 0 ? _cpuCyclesPerLine : md_main.VDL_LINE_RENDER_MC68_CLOCK;
                bool useCycleCounterZ80Scheduling = md_main.IsCycleCounterZ80SchedulingEnabled();
                bool svpActive = md_main.g_md_bus?.OverrideBus is SvpBusOverride;

                for (int v = 0; v < vlines; v++)
                {
                    if (!SkipVdpRenderEnabled)
                    {
                        if (TracePerf)
                        {
                            long start = Stopwatch.GetTimestamp();
                            _vdp.run(v);
                            vdpTicks += Stopwatch.GetTimestamp() - start;
                        }
                        else
                        {
                            _vdp.run(v);
                        }
                    }

                    if (svpActive && md_main.g_md_bus?.OverrideBus is SvpBusOverride svpOverride)
                    {
                        if (!_svpTickLoggedInAdapter)
                        {
                            _svpTickLoggedInAdapter = true;
                            Console.WriteLine("[SVP] tick active (MdTracerAdapter slice path)");
                        }

                        int remaining = cpuBudget;
                        const int svpSliceCycles = 16;
                        while (remaining > 0)
                        {
                            int slice = Math.Min(svpSliceCycles, remaining);
                            int beforeCycles = md_m68k.g_clock_now;
                            if (TracePerf)
                            {
                                long cpuStart = Stopwatch.GetTimestamp();
                                _cpu.RunSome(budget: slice);
                                cpuTicks += Stopwatch.GetTimestamp() - cpuStart;
                            }
                            else
                            {
                                _cpu.RunSome(budget: slice);
                            }

                            int ranCycles = md_m68k.g_clock_now - beforeCycles;
                            if (ranCycles <= 0)
                                ranCycles = slice;

                            svpOverride.Tick((uint)ranCycles);
                            md_main.AdvanceSystemCycles(ranCycles, flushAudio: false);
                            remaining -= slice;
                        }
                    }
                    else
                    {
                        if (TracePerf)
                        {
                            long cpuStart = Stopwatch.GetTimestamp();
                            _cpu.RunSome(budget: cpuBudget);
                            cpuTicks += Stopwatch.GetTimestamp() - cpuStart;
                        }
                        else
                        {
                            _cpu.RunSome(budget: cpuBudget);
                        }

                        md_main.AdvanceSystemCycles(cpuBudget, flushAudio: false);
                    }

                    // Captain America can wedge in semaphore wait loops mid-frame;
                    // detect/recover in slice-time instead of only frame boundaries.
                    MaybeCaptainAmericaMailboxRecovery(frame);

                    if (allowZ80)
                    {
                        int z80CyclesToRun = z80Budget;
                        if (useCycleCounterZ80Scheduling)
                            z80CyclesToRun = md_main.TakeZ80TicksForScheduling();

                        if (z80CyclesToRun > 0)
                        {
                            md_main.g_md_z80?.BeginSystemCycleSlice();
                            md_main.g_md_z80?.run(z80CyclesToRun);
                            md_main.g_md_z80?.EndSystemCycleSlice();
                            // Track Z80 cycles for Aladdin debug
                            md_main.AddZ80Cycles(z80CyclesToRun);
                        }
                    }

                    md_main.FlushScheduledAudio();
                }

                if (TracePerf)
                {
                    _accCpuTicks += cpuTicks;
                    _accVdpTicks += vdpTicks;
                    PerfHotspots.Add(PerfHotspot.CpuStep, cpuTicks);
                    PerfHotspots.Add(PerfHotspot.VdpRender, vdpTicks);
                }

                pcAfter = md_m68k.g_reg_PC;
                if (pcAfter == _lastPc)
                    _pcStallFrames++;
                else
                    _pcStallFrames = 0;
                _lastPc = pcAfter;

                if ((_tick % 60) == 0)
                {
                    ushort vdpStatus = md_main.g_md_vdp.read16(0xC00004);
                    ushort hv = md_main.g_md_vdp.read16(0xC00008);
                    ushort op0 = md_m68k.read16(pcAfter);
                    ushort op1 = md_m68k.read16(pcAfter + 2);
                    uint d1 = md_m68k.g_reg_data[1].l;
                    MdTracerCore.MdLog.WriteLine($"m68k PC=0x{pcAfter:X6} stall={_pcStallFrames} VDP=0x{vdpStatus:X4} HV=0x{hv:X4} D1=0x{d1:X8} OP=0x{op0:X4} NXT=0x{op1:X4}");
                }
            }
            else
            {
                // Fortfarande VDP-test tills vi kopplar VDP-register/IO
                if (!SkipVdpRenderEnabled)
                {
                    for (int v = 0; v < vlines; v++)
                        _vdp.run(v);
                }
            }

            md_main.MaybeInjectMbx(frame);
            MaybeCaptainAmericaMailboxRecovery(frame);
            MaybeSonic2AudioRecovery(frame);
            md_main.g_md_music?.FlushDacRateFrame(frame);
            md_main.g_md_music?.FlushAudioStats(frame);
            md_main.g_md_bus?.FlushZ80WinHist(frame);
            md_main.g_md_bus?.FlushZ80WinStat(frame);
            md_main.g_md_bus?.FlushMbx68kStat(frame);
            md_main.g_md_bus?.TickZ80SafeBoot(frame);
             md_main.g_md_z80?.FlushZ80MbxPoll(frame);
            md_main.g_md_z80?.FlushPcHist(frame);
            if (md_main.g_md_z80 != null)
            {
                var (actual, budget) = md_main.g_md_z80.ConsumeFrameCycleStats();
                MdCycleCounterFrameStats cycleStats = md_main.ConsumeCycleCounterFrameStats(actual);
                if (IsEnvFlagSet("EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES") &&
                    (TraceZ80FrameCyclesEvery <= 0 || frame % TraceZ80FrameCyclesEvery == 0))
                {
                    Console.WriteLine($"[Z80-CYCLES] frame={frame} actual={actual} budget={budget}");
                }
                md_main.g_md_z80.FlushZ80AudioRate(frame);
                md_main.MaybeLogCycleCounterFrame(frame, cycleStats, actual);
                MaybeTraceSonic2Timing(frame, vlines, systemCyclesBeforeFrame, actual, budget);
            }
            
            // Per-frame diagnostic logging for Genesis mode (when md_main.RunFrame() is not called)
            if (md_main.ReadEnvFlag("EUTHERDRIVE_DIAG_FRAME"))
            {
                long currentFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
                if (currentFrame != _lastDiagnosticFrame)
                {
                    _lastDiagnosticFrame = currentFrame;
                    // Get Z80 cycles from md_main
                    long z80Cycles = md_main.Z80TotalCycles;
                     // Get M68K cycles and YM advance calls
                     long m68kCyclesThisFrame = md_main.M68kCyclesThisFrame;
                     Console.WriteLine($"[DIAG-FRAME] frame={currentFrame} z80Cycles={z80Cycles} m68kCycles={m68kCyclesThisFrame} ymAdvanceCalls={md_main.YmAdvanceCallsLastFrame}");
                     
                     if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1" && md_main.YmAdvanceCallsLastFrame == 0)
                     {
                         Console.WriteLine($"[YM-TIMING] No YM advances recorded for frame {currentFrame}");
                     }
                     
                     // Reset for next frame
                     md_main.ResetYmAdvanceCalls();
                     md_main.ResetM68kCyclesThisFrame();
                 }
             }

            // ALADDIN-DEBUG logging for Z80 cycle analysis (gated)
            if (TraceAladdinDebug && frame % 1 == 0)
            {
                int lines = md_main.g_md_vdp?.g_vertical_line_max ?? 262;
                int expectedZ80PerFrame = md_main.GetZ80CyclesPerLine() * lines;
                Console.WriteLine($"[ALADDIN-DEBUG] frame={frame}: Z80={md_main.Z80TotalCycles} (expected={expectedZ80PerFrame}) slices={md_main.Z80SliceCount}");
                Console.WriteLine($"[ALADDIN-DEBUG] frame={frame}: IRQs={md_main.Z80IrqCount} TimerA={md_main.YmTimerAOverflows} TimerB={md_main.YmTimerBOverflows} BusySets={md_main.YmBusySets} BusyClears={md_main.YmBusyClears}");
                md_main.ResetZ80Telemetry();
                md_main.ResetAladdinDebug();
            }
        }

            if (TracePerf && _cpuReady && _cpu != null)
            {
                _accFrameTicks += Stopwatch.GetTimestamp() - frameStart;
                _perfFrameCount++;
                MaybeLogPerformance();
            }

        // Safety net for YM timer cadence (helps avoid tempo/jitter artifacts).
        md_main.g_md_music?.YmEnsureAdvanceEachFrame();

        // Blitta VDP RGB555 -> UI BGRA staging buffer
        EnsureFramebufferInitialized("RunFrame");
             var vdpBuffer = _vdp.GetFrameBuffer();
            if (vdpBuffer.Length > 0)
            {
            int vdpWidth = _vdp.FrameWidth;
            int vdpHeight = _vdp.FrameHeight;
            if (vdpWidth <= 0)
                vdpWidth = 320;
            if (vdpHeight <= 0)
                vdpHeight = 224;

                if (FrameBufferTraceEnabled && ShouldLogPerSecond(ref _lastVdpLogTicks))
                {
                int id = RuntimeHelpers.GetHashCode(vdpBuffer);
                uint p0 = vdpBuffer.Length > 0 ? vdpBuffer[0] : 0;
                uint p1 = vdpBuffer.Length > 1 ? vdpBuffer[1] : 0;
                uint p2 = vdpBuffer.Length > 2 ? vdpBuffer[2] : 0;
                uint p3 = vdpBuffer.Length > 3 ? vdpBuffer[3] : 0;
                Console.WriteLine($"[MdTracerAdapter] VDP output fbId=0x{id:X8} words={vdpBuffer.Length} p0=0x{p0:X8} p1=0x{p1:X8} p2=0x{p2:X8} p3=0x{p3:X8}");

                int renderPixels = Math.Min(vdpBuffer.Length, vdpWidth * vdpHeight);
                uint baseColor = vdpBuffer[0];
                int diffCount = 0;
                int firstDiff = -1;
                uint firstDiffValue = 0;
                for (int i = 0; i < renderPixels; i++)
                {
                    uint val = vdpBuffer[i];
                    if (val == baseColor)
                        continue;
                    diffCount++;
                    if (firstDiff < 0)
                    {
                        firstDiff = i;
                        firstDiffValue = val;
                    }
                }

                if (diffCount == 0)
                {
                    Console.WriteLine($"[MdTracerAdapter] VDP summary base=0x{baseColor:X8} diff=0 size={vdpWidth}x{vdpHeight}");
                }
                else
                {
                    int fx = firstDiff % vdpWidth;
                    int fy = firstDiff / vdpWidth;
                    Console.WriteLine($"[MdTracerAdapter] VDP summary base=0x{baseColor:X8} diff={diffCount} first=({fx},{fy}) val=0x{firstDiffValue:X8} size={vdpWidth}x{vdpHeight}");
                }
            }

                ReadOnlySpan<uint> vdpSpan = vdpBuffer;
                long blitStart = TracePerf ? Stopwatch.GetTimestamp() : 0;
                BlitArgbToBgra8888(vdpSpan, _frameBufferBack, srcStridePixels: vdpWidth, srcWidth: vdpWidth, srcHeight: vdpHeight);
                _frameBufferBack = Interlocked.Exchange(ref _frameBufferFront, _frameBufferBack);
                if (TracePerf)
                    PerfHotspots.Add(PerfHotspot.VdpBlit, Stopwatch.GetTimestamp() - blitStart);
            }
        }

        // Analyze framebuffer if enabled
        FbAnalyzer.AnalyzeFrame();

        // Stream framebuffer for ASCII viewer (runtime toggle)
        if (_asciiStreamEnabled)
        {
            StreamFrameToAsciiViewer();
        }
    }

    private System.IO.FileStream? _asciiFileStream;
    private readonly object _asciiStreamLock = new();
    private long _asciiFrameNumber;

    private void StreamFrameToAsciiViewer()
    {
        // Debug: log when this is called
        bool emptyBuffer = _frameBufferFront.Length == 0;
        bool zeroWidth = _fbW <= 0;
        bool zeroHeight = _fbH <= 0;

        if (emptyBuffer || zeroWidth || zeroHeight)
        {
            // Only log occasionally to avoid spam
            if (_asciiFrameNumber % 120 == 0)
            {
                LogToAsciiDebug($"[ADAPTER] Skipped: bufferLen={_frameBufferFront.Length} w={_fbW} h={_fbH} frameNum={_asciiFrameNumber}");
            }
            return;
        }

        try
        {
            lock (_asciiStreamLock)
            {
                // Create file on first frame
                if (_asciiFileStream == null)
                {
                    try
                    {
                        // Use temp directory for the framebuffer file
                        _asciiFilePath = Path.Combine(Path.GetTempPath(), "eutherdrive_ascii_fb.dat");
                        _asciiFileStream = new System.IO.FileStream(_asciiFilePath,
                            System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite,
                            System.IO.FileShare.ReadWrite, 4096);

                        LogToAsciiDebug($"[ADAPTER] File created: {_asciiFilePath}");
                    }
                    catch (Exception ex)
                    {
                        LogToAsciiDebug($"[ADAPTER] Failed to create ASCII file: {ex.Message}");
                        return;
                    }
                }

                if (_asciiFileStream == null)
                    return;

                // Write header: width(4) + height(4) + size(4) + frame(4)
                byte[] header = new byte[16];
                BitConverter.GetBytes(_fbW).CopyTo(header, 0);
                BitConverter.GetBytes(_fbH).CopyTo(header, 4);
                BitConverter.GetBytes(_fbStride * _fbH).CopyTo(header, 8);
                BitConverter.GetBytes((int)++_asciiFrameNumber).CopyTo(header, 12);

                _asciiFileStream.SetLength(16 + (uint)_fbStride * _fbH);
                _asciiFileStream.Position = 0;
                _asciiFileStream.Write(header, 0, 16);
                _asciiFileStream.Flush();
                _asciiFileStream.Flush(); // Double flush for safety

                // Write framebuffer
                _asciiFileStream.Write(_frameBufferFront, 0, Math.Min(_frameBufferFront.Length, _fbStride * _fbH));
                _asciiFileStream.Flush();
                _asciiFileStream.Flush(); // Double flush for safety

                // Debug log for every 10th frame (reduced for debugging)
                if (_asciiFrameNumber % 10 == 0)
                {
                    LogToAsciiDebug($"[ADAPTER] Wrote frame {_asciiFrameNumber} ({_fbW}x{_fbH})");
                }
            }
        }
        catch (Exception ex)
        {
            LogToAsciiDebug($"[ADAPTER] Error: {ex.Message}");
        }
    }

    private void LogToAsciiDebug(string message)
    {
        try
        {
            if (!AsciiStreamDebug && !TraceAll)
                return;

            string logPath = "/tmp/eutherdrive_ascii_adapter.log";
            lock (_asciiStreamLock)
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
        }
        catch
        {
            // Ignore log errors
        }
    }

    /// <summary>
    /// Enable/disable ASCII streaming at runtime (for UI toggle)
    /// </summary>
    public void SetAsciiStreamEnabled(bool enabled)
    {
        if (_asciiStreamEnabled == enabled)
            return;

        _asciiStreamEnabled = enabled;
        if (enabled)
        {
            // Initialize on first enable
            try
            {
                _asciiFilePath = Path.Combine(Path.GetTempPath(), "eutherdrive_ascii_fb.dat");
                _asciiFileStream = new System.IO.FileStream(_asciiFilePath,
                    System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite,
                    System.IO.FileShare.ReadWrite, 4096);
                Console.WriteLine($"[MdTracerAdapter] ASCII viewer file: {_asciiFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MdTracerAdapter] Failed to create ASCII file: {ex.Message}");
                _asciiStreamEnabled = false;
            }
        }
        else
        {
            // Clean up when disabling
            lock (_asciiStreamLock)
            {
                if (_asciiFileStream != null)
                {
                    _asciiFileStream.Dispose();
                    _asciiFileStream = null;
                }
                _asciiFilePath = null;
                _asciiFrameNumber = 0;
            }
        }
    }

    private bool _asciiStreamEnabled;
    private string? _asciiFilePath;

    /// <summary>
    /// Step one frame - for headless testing
    /// </summary>
    public void StepFrame()
    {
        RunFrame();
    }

    /// <summary>
    /// Get current Z80 PC - for debugging
    /// </summary>
    public ushort GetZ80Pc()
    {
        return md_main.g_md_z80?.CpuPc ?? (ushort)0;
    }

    /// <summary>
    /// Get current M68K PC - for debugging
    /// </summary>
    public uint GetM68kPc()
    {
        return MdTracerCore.md_m68k.g_reg_PC;
    }

    public void ForceDumpZ80(string reason, bool extra, string? dumpPath)
    {
        md_main.g_md_z80?.ForceDumpRam(reason, extra, dumpPath);
    }

    public void DumpYmDebugState(string reason, int recentWriteCount = 64)
    {
        var music = md_main.g_md_music;
        if (music == null)
            return;

        Console.WriteLine(
            $"[YM-DUMP] reason={reason} m68kPc=0x{MdTracerCore.md_m68k.g_reg_PC:X6} z80Pc=0x{GetZ80Pc():X4} " +
            $"dacEn=0x{music.DebugDacEnabled:X2} dac=0x{music.DebugDacData:X2} " +
            $"lastAddr=0x{music.DebugLastYmAddr:X2} lastVal=0x{music.DebugLastYmVal:X2} src={music.DebugLastYmSource}");
        music.DumpChannel3State(reason);
        music.DumpAllChannelStates(reason);
        music.DumpRecentYmWrites(reason, recentWriteCount);
    }

    public void SaveState(BinaryWriter writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));
        lock (_stateLock)
        {
            var serializer = new MdTracerStateSerializer();
            serializer.Save(writer);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));
        lock (_stateLock)
        {
            var serializer = new MdTracerStateSerializer();
            serializer.Load(reader);

            // Keep loaded BUSREQ/RESET state from savestate; just resync Z80 active bit.
            // Forcing bus lines after load can desync audio driver state (e.g. Sonic 2).
            if (md_main.g_md_bus != null && md_main.g_md_z80 != null)
            {
                md_main.g_md_bus.ApplyZ80BusReqLatch();
                bool z80BusGranted = md_main.g_md_bus.Z80BusGranted;
                bool reset = md_main.g_md_bus.Z80Reset;
                // Z80 runs when 68k has NOT requested the bus and reset is deasserted.
                md_main.g_md_z80.g_active = !z80BusGranted && !reset;
            }

            TryRecoverSonic2MailboxAfterLoad();
            if (Sonic2ForceIffAfterLoad && _romIdentity != null && md_main.g_md_z80 != null)
            {
                string n = _romIdentity.Name ?? string.Empty;
                if (n.IndexOf("sonic the hedgehog 2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("sonic2", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    md_main.g_md_z80.ForceEnableInterruptState();
                    Console.WriteLine("[Savestate] Sonic2 forced Z80 IFF/IM after load");
                }
            }
        }
    }

    private void TryRecoverSonic2MailboxAfterLoad()
    {
        if (!Sonic2AudioRecoveryEnabled)
            return;

        if (_romIdentity == null || md_main.g_md_z80 == null)
            return;

        string name = _romIdentity.Name ?? string.Empty;
        if (name.IndexOf("sonic the hedgehog 2", StringComparison.OrdinalIgnoreCase) < 0 &&
            name.IndexOf("sonic2", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        ushort pc = md_main.g_md_z80.CpuPc;
        if (pc < 0x0174 || pc > 0x0176)
            return;

        md_main.g_md_z80.ForceEnableInterruptState();

        byte mbx88 = md_main.g_md_z80.PeekZ80Ram(0x1B88);
        byte mbx89 = md_main.g_md_z80.PeekZ80Ram(0x1B89);
        byte fixed88 = (byte)(mbx88 & 0x7F);
        byte fixed89 = (byte)(mbx89 & 0x7F);
        if (fixed88 != mbx88 || fixed89 != mbx89)
        {
            md_main.g_md_z80.write8(0x1B88, fixed88);
            md_main.g_md_z80.write8(0x1B89, fixed89);
        }

        // Sonic 2 can get stranded in the boot NOP/JR loop (0x0174..0x0176)
        // after savestate load; jump back to driver entry so audio progresses.
        md_main.g_md_z80.ForceJumpToDriver();
        _sonic2AudioRecoveryFramesRemaining = Sonic2AudioRecoveryFrames;
        Console.WriteLine(
            $"[Savestate] Sonic2 audio recovery applied: pc=0x{pc:X4} " +
            $"1B88 {mbx88:X2}->{fixed88:X2} 1B89 {mbx89:X2}->{fixed89:X2} jump=0x0167");
    }

    private void MaybeSonic2AudioRecovery(long frame)
    {
        if (!Sonic2AudioRecoveryEnabled)
            return;

        if (_sonic2AudioRecoveryFramesRemaining > 0 && _romIdentity != null && md_main.g_md_z80 != null)
        {
            ushort pc = md_main.g_md_z80.CpuPc;
            if (pc < 0x0174 || pc > 0x0176)
            {
                _sonic2AudioRecoveryFramesRemaining--;
            }
            else
            {
                byte mbx88 = md_main.g_md_z80.PeekZ80Ram(0x1B88);
                byte mbx89 = md_main.g_md_z80.PeekZ80Ram(0x1B89);
                byte fixed88 = (byte)(mbx88 & 0x7F);
                byte fixed89 = (byte)(mbx89 & 0x7F);
                if (fixed88 != mbx88 || fixed89 != mbx89)
                {
                    md_main.g_md_z80.write8(0x1B88, fixed88);
                    md_main.g_md_z80.write8(0x1B89, fixed89);
                }

                md_main.g_md_z80.ForceJumpToDriver();
                Console.WriteLine(
                    $"[Savestate] Sonic2 audio recovery tick: frame={frame} pc=0x{pc:X4} " +
                    $"1B88 {mbx88:X2}->{fixed88:X2} 1B89 {mbx89:X2}->{fixed89:X2} left={_sonic2AudioRecoveryFramesRemaining}");
                _sonic2AudioRecoveryFramesRemaining--;
            }
        }

        MaybeSonic2RuntimeAudioRecovery(frame);
    }

    private bool IsSonic2RomLoaded()
    {
        if (_romIdentity == null)
            return false;
        string name = _romIdentity.Name ?? string.Empty;
        return name.IndexOf("sonic the hedgehog 2", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("sonic2", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsCaptainAmericaAvengersRomLoaded()
    {
        if (_romIdentity == null)
            return false;
        string name = _romIdentity.Name ?? string.Empty;
        return name.IndexOf("captain america", StringComparison.OrdinalIgnoreCase) >= 0
            && name.IndexOf("avengers", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void MaybeCaptainAmericaMailboxRecovery(long frame)
    {
        if (!CaptainAmericaMailboxRecoveryEnabled)
            return;

        uint pc = md_m68k.g_reg_PC & 0x00FF_FFFF;
        int waitGroup = 0;
        if (pc == 0x001706 || pc == 0x00170C)
            waitGroup = 1; // wait until FF0E0E != 0
        else if (pc == 0x001724 || pc == 0x031B7C || pc == 0x031B82 || pc == 0x031C60)
            waitGroup = 2; // wait until FF0E0C == 0
        else if (pc == 0x031B5A || pc == 0x031B62 || pc == 0x031C46)
            waitGroup = 3; // wait until FF0E0E >= 4

        if (waitGroup == 0)
        {
            _captainAmericaMailboxWaitGroup = 0;
            _captainAmericaMailboxStableFrames = 0;
            return;
        }

        byte sem0c = md_m68k.read8(0x00FF0E0C);
        ushort sem0e = md_m68k.read16(0x00FF0E0E);
        uint semState = ((uint)sem0c << 16) | sem0e;

        // Track stability by semantic wait-group only. Semaphore bytes can flap in
        // this game's deadlock loops, which made state-equality too strict.
        if (waitGroup == _captainAmericaMailboxWaitGroup)
            _captainAmericaMailboxStableFrames++;
        else
            _captainAmericaMailboxStableFrames = 1;

        _captainAmericaMailboxWaitGroup = waitGroup;
        _captainAmericaMailboxLastSemState = semState;

        int threshold = CaptainAmericaMailboxRecoveryFrames > 0 ? CaptainAmericaMailboxRecoveryFrames : 120;
        if (_captainAmericaMailboxStableFrames < threshold)
            return;

        bool changed = false;

        // Kick semaphore waits only when the loop has been stable for a long time.
        if (waitGroup == 2 && sem0c != 0)
        {
            md_m68k.write8(0x00FF0E0C, 0x00);
            changed = true;
        }
        if (waitGroup == 3 && sem0e < 4)
        {
            md_m68k.write16(0x00FF0E0E, 0x0004);
            changed = true;
        }
        if (waitGroup == 1 && sem0e == 0)
        {
            md_m68k.write16(0x00FF0E0E, 0x0001);
            changed = true;
        }

        if (!changed)
            return;

        if (CaptainAmericaMailboxRecoveryDebug)
        {
            Console.WriteLine(
                $"[CAP-MAILBOX-RECOVER] frame={frame} pc=0x{pc:X6} group={waitGroup} stable={_captainAmericaMailboxStableFrames} " +
                $"FF0E0C=0x{sem0c:X2} FF0E0E=0x{sem0e:X4}");
        }

        // Avoid hammering; require the loop to stabilize again before another kick.
        _captainAmericaMailboxStableFrames = 0;
        _captainAmericaMailboxLastSemState = 0;
    }

    private void MaybeTraceSonic2Timing(long frame, int vlines, long systemCyclesBeforeFrame, long z80ActualCycles, long z80BudgetCycles)
    {
        if (!TraceSonic2Timing || !IsSonic2RomLoaded() || md_main.g_md_z80 == null)
            return;
        if (frame < TraceSonic2TimingStartFrame)
            return;
        if (TraceSonic2TimingEvery > 0 && frame % TraceSonic2TimingEvery != 0)
            return;

        int cpuBudget = _cpuCyclesPerLine > 0 ? _cpuCyclesPerLine : md_main.VDL_LINE_RENDER_MC68_CLOCK;
        long m68kBudgetCycles = (long)cpuBudget * vlines;
        long systemDelta = md_main.SystemCycles - systemCyclesBeforeFrame;
        ushort z80Pc = md_main.g_md_z80.DebugPc;
        byte mbx88 = md_main.g_md_z80.PeekZ80Ram(0x1B88);
        byte mbx89 = md_main.g_md_z80.PeekZ80Ram(0x1B89);

        int dacEnabled = md_main.g_md_music?.DebugDacEnabled ?? 0;
        int dacData = md_main.g_md_music?.DebugDacData ?? 0;
        byte ymAddr = md_main.g_md_music?.DebugLastYmAddr ?? 0;
        byte ymVal = md_main.g_md_music?.DebugLastYmVal ?? 0;
        string ymSrc = md_main.g_md_music?.DebugLastYmSource ?? "none";

        Console.WriteLine(
            $"[S2-TIMING] frame={frame} sysDelta={systemDelta} m68kBudget={m68kBudgetCycles} " +
            $"z80Actual={z80ActualCycles} z80Budget={z80BudgetCycles} z80pc=0x{z80Pc:X4} " +
            $"mbx88=0x{mbx88:X2} mbx89=0x{mbx89:X2} ymLast=0x{ymAddr:X2}:0x{ymVal:X2}/{ymSrc} " +
            $"dacEn={(dacEnabled != 0 ? 1 : 0)} dacData=0x{dacData:X2}");
    }

    private void MaybeSonic2RuntimeAudioRecovery(long frame)
    {
        if (!Sonic2RuntimeRecoveryEnabled || !IsSonic2RomLoaded() || md_main.g_md_z80 == null)
            return;

        ushort pc = md_main.g_md_z80.CpuPc;
        if (pc < 0x0174 || pc > 0x0176)
        {
            _sonic2RuntimeStallFrames = 0;
            return;
        }

        byte mbx88 = md_main.g_md_z80.PeekZ80Ram(0x1B88);
        byte mbx89 = md_main.g_md_z80.PeekZ80Ram(0x1B89);
        if ((mbx88 & 0x80) == 0 && (mbx89 & 0x80) == 0)
        {
            _sonic2RuntimeStallFrames = 0;
            return;
        }

        _sonic2RuntimeStallFrames++;
        if (_sonic2RuntimeStallFrames < Sonic2RuntimeRecoveryStallFrames)
            return;

        if (Sonic2RuntimeRecoveryMaxPerSession > 0 &&
            _sonic2RuntimeRecoveryCount >= Sonic2RuntimeRecoveryMaxPerSession)
            return;

        byte fixed88 = (byte)(mbx88 & 0x7F);
        byte fixed89 = (byte)(mbx89 & 0x7F);
        if (fixed88 != mbx88 || fixed89 != mbx89)
        {
            md_main.g_md_z80.write8(0x1B88, fixed88);
            md_main.g_md_z80.write8(0x1B89, fixed89);
        }

        md_main.g_md_z80.ForceEnableInterruptState();

        md_main.g_md_z80.ForceJumpToDriver();
        _sonic2RuntimeRecoveryCount++;
        _sonic2RuntimeStallFrames = 0;
        Console.WriteLine(
            $"[Sonic2] Runtime audio watchdog recovery frame={frame} pc=0x{pc:X4} " +
            $"1B88 {mbx88:X2}->{fixed88:X2} 1B89 {mbx89:X2}->{fixed89:X2} count={_sonic2RuntimeRecoveryCount}");
    }

    /// <summary>
    /// Check if VDP display is enabled (reg 1, bit 6)
    /// </summary>
    public bool IsVdpDisplayOn()
    {
        if (_vdp is Core.MdTracerCore.md_vdp vdp)
        {
            // reg 1 bit 6 = display enable
            return vdp.g_vdp_reg_1_6_display != 0;
        }
        return false;
    }

    /// <summary>
    /// Get VDP display status (for debugging)
    /// </summary>
    public int GetVdpDisplayStatus()
    {
        if (_vdp is Core.MdTracerCore.md_vdp vdp)
        {
            return vdp.g_vdp_reg_1_6_display;
        }
        return -1;
    }

    private void MaybeLogPerformance()
    {
        if (_perfFrameCount < 60)
            return;

        double invFreq = 1000.0 / Stopwatch.Frequency;
        double avgCpuMs = (_accCpuTicks / (double)_perfFrameCount) * invFreq;
        double avgVdpMs = (_accVdpTicks / (double)_perfFrameCount) * invFreq;
        double avgFrameMs = (_accFrameTicks / (double)_perfFrameCount) * invFreq;

        int gc0 = GC.CollectionCount(0);
        int gc1 = GC.CollectionCount(1);
        int gc2 = GC.CollectionCount(2);

        Console.WriteLine($"[MdTracerAdapter] perf avg/frame={avgFrameMs:0.00}ms CPU={avgCpuMs:0.00}ms VDP={avgVdpMs:0.00}ms GC0={gc0 - _lastGc0} GC1={gc1 - _lastGc1} GC2={gc2 - _lastGc2}");
        PerfHotspots.SnapshotAndReset(_hotspotTicks);
        const int maxTop = 5;
        Span<int> topIdx = stackalloc int[maxTop];
        Span<long> topTicks = stackalloc long[maxTop];
        for (int i = 0; i < maxTop; i++)
        {
            topIdx[i] = -1;
            topTicks[i] = 0;
        }

        for (int i = 0; i < _hotspotTicks.Length; i++)
        {
            long ticks = _hotspotTicks[i];
            if (ticks <= 0)
                continue;

            for (int slot = 0; slot < maxTop; slot++)
            {
                if (ticks <= topTicks[slot])
                    continue;

                for (int shift = maxTop - 1; shift > slot; shift--)
                {
                    topTicks[shift] = topTicks[shift - 1];
                    topIdx[shift] = topIdx[shift - 1];
                }

                topTicks[slot] = ticks;
                topIdx[slot] = i;
                break;
            }
        }

        var sb = new StringBuilder(96);
        bool anyHotspot = false;
        for (int i = 0; i < maxTop; i++)
        {
            int idx = topIdx[i];
            if (idx < 0)
                continue;

            double ms = (topTicks[i] / (double)_perfFrameCount) * invFreq;
            if (!anyHotspot)
            {
                sb.Append("[MdTracerAdapter] hotspots");
                anyHotspot = true;
            }

            sb.Append(' ');
            sb.Append(PerfHotspots.GetName((PerfHotspot)idx));
            sb.Append('=');
            sb.Append(ms.ToString("0.00"));
            sb.Append("ms");
        }

        if (anyHotspot)
            Console.WriteLine(sb.ToString());

        _accCpuTicks = 0;
        _accVdpTicks = 0;
        _accFrameTicks = 0;
        _perfFrameCount = 0;
        _lastGc0 = gc0;
        _lastGc1 = gc1;
        _lastGc2 = gc2;
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        EnsureFramebufferInitialized("GetFrameBuffer");
        width = _fbW;
        height = _fbH;
        stride = _fbStride;
        if (_fbLogW != width || _fbLogH != height || _fbLogStride != stride)
        {
            _fbLogW = width;
            _fbLogH = height;
            _fbLogStride = stride;
            MdTracerCore.MdLog.WriteLine($"[MdTracerAdapter] GetFrameBuffer size={width}x{height} stride={stride}");
        }

        if (FrameBufferTraceEnabled)
        {
            _fbPresentCount++;
            int id = RuntimeHelpers.GetHashCode(_frameBufferFront);
            if (ShouldLogPerSecond(ref _lastPresentLogTicks))
                Console.WriteLine($"[MdTracerAdapter] Present fbId=0x{id:X8} size={width}x{height} stride={stride} bytes={_frameBufferFront.Length}");
            if ((_fbPresentCount % PresentSampleEveryFrames) == 0 && _frameBufferFront.Length >= 4)
            {
                Console.WriteLine($"[MdTracerAdapter] Present sample frame={_fbPresentCount} bytes={_frameBufferFront[0]:X2} {_frameBufferFront[1]:X2} {_frameBufferFront[2]:X2} {_frameBufferFront[3]:X2}");
            }
        }
        return _frameBufferFront;
    }

    private void LogFrameBufferIdentity(string reason)
    {
        if (!FrameBufferTraceEnabled)
            return;

        if (_frameBufferFront.Length == 0)
        {
            Console.WriteLine($"[MdTracerAdapter] {reason} framebuffer empty");
            return;
        }

        int id = RuntimeHelpers.GetHashCode(_frameBufferFront);
        Console.WriteLine($"[MdTracerAdapter] {reason} framebuffer id=0x{id:X8} size={_fbW}x{_fbH} stride={_fbStride} bytes={_frameBufferFront.Length}");
    }

    private void BlitRgb555ToBgra8888(Span<ushort> vdpSrc, Span<byte> dst, int srcStridePixels)
    {
        if (vdpSrc.Length == 0 || dst.Length == 0)
            return;

        int srcWidth = 320;
        // Use full visible MD height in normal modes (224/240).
        // A hard 224 cap leaves stale pixels in the bottom rows for 240-line scenes
        // (e.g. menu/UI screens in some Sonic titles).
        int copyHeight = _fbH <= 240 ? _fbH : Math.Min(_fbH, 224);
        int copyWidth = Math.Min(_fbW, srcWidth);
        int dstStride = 1280;

        int requiredDst = (copyHeight - 1) * dstStride + (copyWidth * 4);
        if (dst.Length < requiredDst)
            return;

        bool interleaved = vdpSrc.Length >= srcStridePixels * copyHeight * 2;
        int requiredSrc = interleaved ? (srcStridePixels * copyHeight * 2) : (srcStridePixels * copyHeight);
        if (vdpSrc.Length < requiredSrc)
            return;

        bool logSamples = FrameBufferTraceEnabled && ShouldLogPerSecond(ref _lastSampleLogTicks);
        if (logSamples)
        {
            ushort p0 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 0, 0);
            ushort p1 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 1, 0);
            ushort p2 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 2, 0);
            ushort p3 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 3, 0);
            Console.WriteLine($"[MdTracerAdapter] VDP src sample p0=0x{p0:X4} p1=0x{p1:X4} p2=0x{p2:X4} p3=0x{p3:X4}");
        }

        for (int y = 0; y < copyHeight; y++)
        {
            int srcRow = interleaved ? (y * srcStridePixels * 2) : (y * srcStridePixels);
            int dstRow = y * dstStride;
            for (int x = 0; x < copyWidth; x++)
            {
                int srcIndex = interleaved ? (srcRow + (x * 2)) : (srcRow + x);
                ushort rgb555 = (ushort)(vdpSrc[srcIndex] & 0x7FFF);
                int r5 = (rgb555 >> 10) & 0x1F;
                int g5 = (rgb555 >> 5) & 0x1F;
                int b5 = rgb555 & 0x1F;

                byte r8 = (byte)((r5 << 3) | (r5 >> 2));
                byte g8 = (byte)((g5 << 3) | (g5 >> 2));
                byte b8 = (byte)((b5 << 3) | (b5 >> 2));

                int di = dstRow + (x * 4);
                dst[di + 0] = b8;
                dst[di + 1] = g8;
                dst[di + 2] = r8;
                dst[di + 3] = 255;
            }
        }

        if (logSamples && dst.Length >= 16)
        {
            Console.WriteLine($"[MdTracerAdapter] DST sample b0={dst[0]:X2} g0={dst[1]:X2} r0={dst[2]:X2} a0={dst[3]:X2} " +
                              $"b1={dst[4]:X2} g1={dst[5]:X2} r1={dst[6]:X2} a1={dst[7]:X2}");
        }
    }

    private void BlitArgbToBgra8888(ReadOnlySpan<uint> vdpSrc, Span<byte> dst, int srcStridePixels, int srcWidth, int srcHeight)
    {
        if (vdpSrc.Length == 0 || dst.Length == 0)
            return;

        int copyHeight = Math.Min(_fbH, srcHeight);
        int copyWidth = Math.Min(_fbW, srcWidth);
        int dstStride = _fbStride;
        int srcStrideBytes = srcStridePixels * 4;
        int copyBytesPerRow = copyWidth * 4;

        int requiredSrc = srcStridePixels * copyHeight;
        int requiredDst = (copyHeight - 1) * dstStride + copyBytesPerRow;
        if (vdpSrc.Length < requiredSrc || dst.Length < requiredDst)
            return;

        bool logSamples = FrameBufferTraceEnabled && ShouldLogPerSecond(ref _lastSampleLogTicks);
        ReadOnlySpan<byte> srcBytes = MemoryMarshal.AsBytes(vdpSrc);
        int requiredSrcBytes = srcStrideBytes * copyHeight;
        if (srcBytes.Length < requiredSrcBytes)
            return;

        if (srcStrideBytes == copyBytesPerRow && dstStride == copyBytesPerRow)
        {
            int totalBytes = copyBytesPerRow * copyHeight;
            srcBytes.Slice(0, totalBytes).CopyTo(dst);
        }
        else
        {
            for (int y = 0; y < copyHeight; y++)
            {
                int srcRow = y * srcStrideBytes;
                int dstRow = y * dstStride;
                srcBytes.Slice(srcRow, copyBytesPerRow).CopyTo(dst.Slice(dstRow, copyBytesPerRow));
            }
        }

        if (logSamples && dst.Length >= 16)
        {
            Console.WriteLine($"[MdTracerAdapter] DST sample b0={dst[0]:X2} g0={dst[1]:X2} r0={dst[2]:X2} a0={dst[3]:X2} " +
                              $"b1={dst[4]:X2} g1={dst[5]:X2} r1={dst[6]:X2} a1={dst[7]:X2}");
        }
    }

    private static bool ShouldLogPerSecond(ref long lastTicks)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - lastTicks < Stopwatch.Frequency)
            return false;
        lastTicks = now;
        return true;
    }

    private bool ShouldForceLogAudioPath()
    {
        if (_audioPathForceCount >= 2)
            return false;
        long now = Stopwatch.GetTimestamp();
        if (_audioPathForceTicks == 0)
            _audioPathForceTicks = now;
        if (now - _audioPathForceTicks <= Stopwatch.Frequency * 2)
        {
            _audioPathForceCount++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Check if framebuffer has non-black content
    /// </summary>
    public bool FrameBufferHasContent()
    {
        if (_frameBufferFront.Length == 0)
            return false;

        // Check if any pixel is not transparent (alpha > 0) or has color
        int checkCount = 0;
        foreach (byte b in _frameBufferFront)
        {
            if (b != 0)
            {
                Console.WriteLine($"[MdTracerAdapter] Framebuffer HAS content (first non-zero byte at offset ~{checkCount})");
                return true;
            }
            if (++checkCount > 10000)  // Check first 10K bytes
                break;
        }
        Console.WriteLine("[MdTracerAdapter] Framebuffer is empty/transparent");
        return false;
    }

    /// <summary>
    /// Dump framebuffer to PPM file (for debugging)
    /// </summary>
    public void DumpFrameBufferToPpm(string filePath)
    {
        if (_frameBufferFront.Length == 0)
        {
            Console.WriteLine("[MdTracerAdapter] Cannot dump - framebuffer is empty");
            return;
        }

        int width = _fbW;
        int height = _fbH;

        try
        {
            using var writer = new StreamWriter(filePath);
            writer.WriteLine("P3");
            writer.WriteLine($"{width} {height}");
            writer.WriteLine("255");

             // _frameBufferFront is BGRA, so we need to convert back to RGB for PPM
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * _fbStride;
                for (int x = 0; x < width; x++)
                {
                    int colOffset = rowOffset + (x * 4);
                    byte b = _frameBufferFront[colOffset + 0];
                    byte g = _frameBufferFront[colOffset + 1];
                    byte r = _frameBufferFront[colOffset + 2];
                    // Skip alpha (colOffset + 3)
                    writer.Write($"{r} {g} {b} ");
                }
                writer.WriteLine();
            }

            Console.WriteLine($"[MdTracerAdapter] Dumped framebuffer to {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MdTracerAdapter] Failed to dump framebuffer: {ex.Message}");
        }
    }

    private static ushort ReadVdpPixel(Span<ushort> vdpSrc, int srcStridePixels, bool interleaved, int x, int y)
    {
        int srcRow = interleaved ? (y * srcStridePixels * 2) : (y * srcStridePixels);
        int srcIndex = interleaved ? (srcRow + (x * 2)) : (srcRow + x);
        if ((uint)srcIndex >= (uint)vdpSrc.Length)
            return 0;
        return (ushort)(vdpSrc[srcIndex] & 0x7FFF);
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = PsgChannels;
        if (!_audioSystemReady)
        {
            if (TraceAudioDebug)
                Console.WriteLine("[AUDIO-TIMING-WARNING] Audio system not ready in GetAudioBuffer()!");
            return ReadOnlySpan<short>.Empty;
        }

        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
        int frames;
        lock (_stateLock)
        {
            _psgFrameAccumulator += (double)OutputSampleRate / GetTargetFps();
            frames = (int)_psgFrameAccumulator;
            if (frames <= 0)
            {
                _psgFrameSamples = 0;
                return ReadOnlySpan<short>.Empty;
            }

            _psgFrameAccumulator -= frames;
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_BUFFER") == "1")
            {
                int samples = frames * PsgChannels;
                Console.WriteLine($"[AUDIO-BUFFER] GetAudioBuffer: frame={frame} frames={frames} samples={samples} _psgFrameAccumulator={_psgFrameAccumulator:F2} targetFps={GetTargetFps()}");
            }
        }

        return GetAudioBufferForFrames(frames, out sampleRate, out channels);
    }

    public ReadOnlySpan<short> GetAudioBufferForFrames(int frames, out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = PsgChannels;

        if (frames <= 0)
            return ReadOnlySpan<short>.Empty;

        var music = md_main.g_md_music;
        if (music == null)
            return ReadOnlySpan<short>.Empty;

        music.SetPsgNoiseGainPercent(Volatile.Read(ref _psgNoisePercent));
        bool wantPsg = !_psgDisabled;
        bool wantYm = _ymEnabled;
        int psgMixPercent = Volatile.Read(ref _psgMixPercent);
        int ymMixPercent = Volatile.Read(ref _ymMixPercent);
        if (TraceAudioDebug && ShouldForceLogAudioPath())
        {
            Console.Error.WriteLine($"[AUDIO-PATH] GetAudioBufferForFrames enter frames={frames} wantPsg={(wantPsg ? 1 : 0)} wantYm={(wantYm ? 1 : 0)} ymEnabled={(_ymEnabled ? 1 : 0)}");
        }
        if (!wantPsg && !wantYm)
        {
            if (!_audioAllSourcesDisabledLogged)
            {
                Console.WriteLine($"[AUDIO-MUTE] All MD sources disabled (wantPsg=0 wantYm=0) at frame={md_main.g_md_vdp?.FrameCounter ?? -1}");
                _audioAllSourcesDisabledLogged = true;
            }
            return ReadOnlySpan<short>.Empty;
        }
        if (_audioAllSourcesDisabledLogged)
            _audioAllSourcesDisabledLogged = false;

        // Audio system should be ready after GenerateInitialAudioSamples()
        // If not, something went wrong
        if (!_audioSystemReady)
        {
            if (TraceAudioDebug)
                Console.WriteLine($"[AUDIO-TIMING-WARNING] Audio system not ready in GetAudioBufferForFrames()!");
            return ReadOnlySpan<short>.Empty;
        }

        // Lock to prevent concurrent audio generation
        lock (_stateLock)
        {
            if (TraceAudioDebug && ShouldForceLogAudioPath())
            {
                Console.Error.WriteLine($"[AUDIO-PATH] GetAudioBufferForFrames lock frame={md_main.g_md_vdp?.FrameCounter ?? -1} _audioSystemReady={(_audioSystemReady ? 1 : 0)}");
            }
            _psgLastFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
            _audioGeneratedThisFrame = true;
            
            int samples = frames * PsgChannels;
            bool trackAudioLevel = TraceAudioLevel;
            long mixSumSq = 0;
            if (_psgFrameBuffer.Length < samples)
                _psgFrameBuffer = new short[samples];

        int psgMin = 0;
        int psgMax = 0;
        bool psgMinMaxInit = false;
        int psgPeak = 0;
        int psgNonZero = 0;
        if (wantPsg)
        {
            GeneratePsgResampledFrames(music, frames);
            for (int i = 0; i < frames; i++)
            {
                int idx = i * PsgChannels;
                int s = _psgFrameBuffer[idx];
                s = ApplyMixPercent(s, psgMixPercent);
                s = ApplyLinearGain(s, PsgLinearGain);
                short sample = (short)s;
                _psgFrameBuffer[idx] = sample;
                _psgFrameBuffer[idx + 1] = sample;

                if (!psgMinMaxInit)
                {
                    psgMinMaxInit = true;
                    psgMin = sample;
                    psgMax = sample;
                }
                else
                {
                    if (sample < psgMin) psgMin = sample;
                    if (sample > psgMax) psgMax = sample;
                }
                int abs = sample < 0 ? -sample : sample;
                if (abs > psgPeak) psgPeak = abs;
                if (sample != 0) psgNonZero += PsgChannels;
                if (trackAudioLevel && !wantYm)
                    mixSumSq += (long)sample * sample * PsgChannels;
            }
        }
        else
        {
            Array.Clear(_psgFrameBuffer, 0, samples);
        }

        int ymMin = 0;
        int ymMax = 0;
        int ymPeak = 0;
        int ymNonZero = 0;
        int mixMin = 0;
        int mixMax = 0;
        int mixPeak = 0;
        int mixNonZero = 0;
        if (wantYm)
        {
            long frameNow = md_main.g_md_vdp?.FrameCounter ?? -1;
            bool dacEnabledNow = music.DebugDacEnabled != 0;
            if (TraceAudioDebug && !_ymInternalForcedLoggedOnce)
            {
                Console.Error.WriteLine($"[YM-INT-FORCE] enter wantYm=1 frames={frames} samples={samples}");
            }
            if (_ymFrameBuffer.Length < samples)
                _ymFrameBuffer = new short[samples];

            // Resample YM from internal rate (~53.2kHz) to output rate (44.1kHz).
            double ymInternalSampleRate = md_main.GetYmSampleRateHzFromTiming();
            if (YmFilterPreResampleEnabled)
                EnsureYmPreResampleFilter((int)Math.Round(ymInternalSampleRate));
            double ratio = (ymInternalSampleRate * YmResampleScale) / OutputSampleRate;
            double phase = _ymResamplePhase;
            int neededInternal = (int)Math.Floor(phase + ((frames - 1) * ratio)) + 2;
            if (neededInternal < 2)
                neededInternal = 2;
            PrepareYmInternalBuffer(neededInternal, out int writeOffsetFrames, out int genFrames);
            if (genFrames > 0)
            {
                var dst = _ymInternalBuffer.AsSpan(writeOffsetFrames * PsgChannels, genFrames * PsgChannels);
                music.YmUpdateBatch(dst, genFrames);
                if (YmFilterPreResampleEnabled)
                    FilterYmPreResampleBuffer(writeOffsetFrames, genFrames);
            }
            if (TraceAudioDebug && !_ymInternalForcedLoggedOnce)
            {
                Console.Error.WriteLine($"[YM-INT-FORCE] after UpdateBatch genFrames={genFrames} writeOffset={writeOffsetFrames}");
                _ymInternalForcedLoggedOnce = true;
            }
            if (TraceAudioDebug && !_ymInternalForcedSummaryOnce && (frameNow >= 200 || dacEnabledNow))
            {
                int min = 0;
                int max = 0;
                bool init = false;
                int firstNz = -1;
                int firstNzVal = 0;
                int lastNz = -1;
                int count = neededInternal * PsgChannels;
                for (int i = 0; i < count; i++)
                {
                    int v = _ymInternalBuffer[i];
                    if (!init)
                    {
                        init = true;
                        min = v;
                        max = v;
                    }
                    else
                    {
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    if (v != 0)
                    {
                        if (firstNz < 0)
                        {
                            firstNz = i;
                            firstNzVal = v;
                        }
                        lastNz = i;
                    }
                }
                int s0 = _ymInternalBuffer.Length > 0 ? _ymInternalBuffer[0] : 0;
                int s1 = _ymInternalBuffer.Length > 1 ? _ymInternalBuffer[1] : 0;
                int s2 = _ymInternalBuffer.Length > 2 ? _ymInternalBuffer[2] : 0;
                int s3 = _ymInternalBuffer.Length > 3 ? _ymInternalBuffer[3] : 0;
                Console.Error.WriteLine(
                    $"[YM-INT-FORCE] frame={frameNow} dacEnabled={(dacEnabledNow ? 1 : 0)} samples={count} min={min} max={max} genFrames={genFrames} writeOffset={writeOffsetFrames} " +
                    $"s0={s0} s1={s1} s2={s2} s3={s3} firstNz={firstNz} firstNzVal={firstNzVal} lastNz={lastNz}");
                _ymInternalForcedSummaryOnce = true;
            }
            if (TraceAudioDebug && !_ymInternalDacStateLoggedOnce && (frameNow >= 200 || dacEnabledNow))
            {
                int dacEnabled = dacEnabledNow ? 1 : 0;
                int dacData = music.DebugDacData;
                byte lastAddr = music.DebugLastYmAddr;
                byte lastVal = music.DebugLastYmVal;
                string lastSrc = music.DebugLastYmSource;
                Console.Error.WriteLine(
                    $"[YM-DAC-STATE] frame={frameNow} enabled={dacEnabled} dacData=0x{dacData:X3} lastAddr=0x{lastAddr:X2} lastVal=0x{lastVal:X2} src={lastSrc}");
                _ymInternalDacStateLoggedOnce = true;
            }

            if (TraceAudioDebug && !_ymResampleDebugFramesOnce && (frameNow >= 200 || dacEnabledNow))
            {
                int baseIndex0 = (int)phase;
                if (baseIndex0 < 0)
                    baseIndex0 = 0;
                int maxBase0 = neededInternal - 2;
                if (maxBase0 < 0)
                    maxBase0 = 0;
                if (baseIndex0 > maxBase0)
                    baseIndex0 = maxBase0;
                double frac0 = phase - baseIndex0;
                if (frac0 < 0)
                    frac0 = 0;
                else if (frac0 > 1.0)
                    frac0 = 1.0;
                int f1 = baseIndex0;
                int f2 = baseIndex0 + 1;
                if (f2 >= neededInternal) f2 = neededInternal - 1;
                int sF1L = ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f1, 0);
                int sF2L = ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f2, 0);
                int sF1R = ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f1, 1);
                int sF2R = ReadInterleavedSample(_ymInternalBuffer, neededInternal, PsgChannels, f2, 1);
                Console.Error.WriteLine(
                    $"[YM-RESAMPLE-DBG-FRAMES] frame={frameNow} phase={phase:0.000} base={baseIndex0} frac={frac0:0.000} f1={f1} f2={f2} " +
                    $"sF1L={sF1L} sF2L={sF2L} sF1R={sF1R} sF2R={sF2R} neededInternal={neededInternal}");
                _ymResampleDebugFramesOnce = true;
            }

            ResampleYmOutputFrames(frames, neededInternal, ratio, ref phase);

            bool forceResampleOut = !_ymResampleOutForcedOnce || (dacEnabledNow && !_ymResampleOutForcedDacOnce);
            if (TraceAudioDebug && forceResampleOut)
            {
                int outMin = 0;
                int outMax = 0;
                bool outInit = false;
                int outCount = Math.Min(samples, _ymFrameBuffer.Length);
                for (int i = 0; i < outCount; i++)
                {
                    int v = _ymFrameBuffer[i];
                    if (!outInit)
                    {
                        outInit = true;
                        outMin = v;
                        outMax = v;
                    }
                    else
                    {
                        if (v < outMin) outMin = v;
                        if (v > outMax) outMax = v;
                    }
                }
                int o0 = _ymFrameBuffer.Length > 0 ? _ymFrameBuffer[0] : 0;
                int o1 = _ymFrameBuffer.Length > 1 ? _ymFrameBuffer[1] : 0;
                int o2 = _ymFrameBuffer.Length > 2 ? _ymFrameBuffer[2] : 0;
                int o3 = _ymFrameBuffer.Length > 3 ? _ymFrameBuffer[3] : 0;
                int o4 = _ymFrameBuffer.Length > 4 ? _ymFrameBuffer[4] : 0;
                int o5 = _ymFrameBuffer.Length > 5 ? _ymFrameBuffer[5] : 0;
                Console.Error.WriteLine(
                    $"[YM-RESAMPLE-OUT-FORCE] frame={frameNow} dacEnabled={(dacEnabledNow ? 1 : 0)} min={outMin} max={outMax} samples={outCount} phase={phase:0.000} ratio={ratio:0.000} " +
                    $"o0={o0} o1={o1} o2={o2} o3={o3} o4={o4} o5={o5}");
                if (!_ymResampleOutForcedOnce)
                    _ymResampleOutForcedOnce = true;
                if (dacEnabledNow)
                    _ymResampleOutForcedDacOnce = true;
            }

            CommitYmResampleState(neededInternal, phase);

            AudioMixStats mixStats = ProcessYmBufferAndMix(
                samples,
                ymMixPercent,
                trackAudioLevel,
                ref mixSumSq,
                out ymMin,
                out ymMax,
                out ymPeak,
                out ymNonZero);
            mixMin = mixStats.Min;
            mixMax = mixStats.Max;
            mixPeak = mixStats.Peak;
            mixNonZero = mixStats.NonZero;

            if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
            {
                Console.WriteLine(
                    $"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin={ymMin} ymMax={ymMax} mixMin={mixMin} mixMax={mixMax} samples={samples} " +
                    $"wantPsg={(wantPsg ? 1 : 0)} wantYm={(wantYm ? 1 : 0)} ymEnabled={(_ymEnabled ? 1 : 0)}");
            }

            if (trackAudioLevel && ShouldLogPerSecond(ref _lastAudioLevelTicks))
            {
                double rms = samples > 0 ? Math.Sqrt(mixSumSq / (double)samples) : 0;
                Console.WriteLine($"[AUDLVL] min={mixMin} max={mixMax} rms={rms:F1} samples={samples}");
            }

            if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioCoreLogTicks))
            {
                if (trackAudioLevel)
                {
                    if (wantPsg)
                        Console.WriteLine($"[PSGLVL] peak={psgPeak} samples={samples}");
                    Console.WriteLine($"[YMLVL] peak={ymPeak} samples={samples}");
                }
                int outVolMin = 0;
                int outVolMax = 0;
                if (music.g_out_vol != null && music.g_out_vol.Length > 0)
                {
                    outVolMin = music.g_out_vol[0];
                    outVolMax = music.g_out_vol[0];
                    for (int i = 1; i < music.g_out_vol.Length; i++)
                    {
                        int v = music.g_out_vol[i];
                        if (v < outVolMin) outVolMin = v;
                        if (v > outVolMax) outVolMax = v;
                    }
                }
                Console.WriteLine(
                    $"[AUDIOCORE] frames={frames} psgPeak={psgPeak} ymPeak={ymPeak} mixPeak={mixPeak} " +
                    $"psgNZ={psgNonZero} ymNZ={ymNonZero} mixNZ={mixNonZero} outVolMin={outVolMin} outVolMax={outVolMax} " +
                    $"wantPsg={(wantPsg ? 1 : 0)} wantYm={(wantYm ? 1 : 0)} ymEnabled={(_ymEnabled ? 1 : 0)}");
            }
        }
        else if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
        {
            Console.WriteLine(
                $"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin=NA ymMax=NA mixMin=NA mixMax=NA samples={samples} " +
                $"wantPsg={(wantPsg ? 1 : 0)} wantYm={(wantYm ? 1 : 0)} ymEnabled={(_ymEnabled ? 1 : 0)}");
        }

        if (trackAudioLevel && !wantYm && ShouldLogPerSecond(ref _lastAudioLevelTicks))
        {
            double rms = samples > 0 ? Math.Sqrt(mixSumSq / (double)samples) : 0;
            Console.WriteLine($"[AUDLVL] min={psgMin} max={psgMax} rms={rms:F1} samples={samples}");
        }

        if (TraceAudioEnabled && !wantYm && ShouldLogPerSecond(ref _lastAudioCoreLogTicks))
        {
            if (trackAudioLevel && wantPsg)
                Console.WriteLine($"[PSGLVL] peak={psgPeak} samples={samples}");
            int outVolMin = 0;
            int outVolMax = 0;
            if (music.g_out_vol != null && music.g_out_vol.Length > 0)
            {
                outVolMin = music.g_out_vol[0];
                outVolMax = music.g_out_vol[0];
                for (int i = 1; i < music.g_out_vol.Length; i++)
                {
                    int v = music.g_out_vol[i];
                    if (v < outVolMin) outVolMin = v;
                    if (v > outVolMax) outVolMax = v;
                }
            }
            Console.WriteLine(
                $"[AUDIOCORE] frames={frames} psgPeak={psgPeak} ymPeak=0 mixPeak={psgPeak} " +
                $"psgNZ={psgNonZero} ymNZ=0 mixNZ={psgNonZero} outVolMin={outVolMin} outVolMax={outVolMax} " +
                $"wantPsg={(wantPsg ? 1 : 0)} wantYm={(wantYm ? 1 : 0)} ymEnabled={(_ymEnabled ? 1 : 0)}");
        }

        MaybeDumpYmSilence(music, wantYm, ymPeak, ymNonZero);

        ApplyMasterVolume(_psgFrameBuffer, samples);
        _mixLowPass?.Apply(_psgFrameBuffer, samples);
        _psgFrameSamples = samples;
        return _psgFrameBuffer.AsSpan(0, _psgFrameSamples);
        } // End of lock(_stateLock)
    }

    private void MaybeDumpYmSilence(md_music music, bool wantYm, int ymPeak, int ymNonZero)
    {
        if (!TraceYmSilence || !wantYm)
            return;

        if (ymPeak == 0 && ymNonZero == 0)
        {
            _ymSilentFrames++;
            if (!_ymSilenceLogged && _ymSilentFrames >= TraceYmSilenceFrames)
            {
                music.DumpRecentYmWrites("silence", TraceYmSilenceDump);
                _ymSilenceLogged = true;
            }
        }
        else
        {
            _ymSilentFrames = 0;
            _ymSilenceLogged = false;
        }
    }

    public long GetSystemCycles()
    {
        return md_main.SystemCycles;
    }

    public double GetM68kCyclesPerSecond()
    {
        int cyclesPerLine = _cpuCyclesPerLine > 0 ? _cpuCyclesPerLine : md_main.VDL_LINE_RENDER_MC68_CLOCK;
        int lines = GetEffectiveFrameRateMode() == FrameRateMode.Hz50 ? VLINES_PAL : VLINES_NTSC;
        return cyclesPerLine * lines * GetTargetFps();
    }

    public double GetM68kClockHz()
    {
        return md_main.GetM68kClockHzFromTiming();
    }

    public bool WritePsg(byte value)
    {
        var bus = md_main.g_md_bus;
        if (bus == null)
            return false;

        bus.write8(0xC00011, value);
        return true;
    }

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        var io = md_main.g_md_io;
        if (io == null)
            return;

        var state = new MdPadState
        {
            Up = up,
            Down = down,
            Left = left,
            Right = right,
            A = a,
            B = b,
            C = c,
            Start = start,
            X = x,
            Y = y,
            Z = z,
            Mode = mode
        };

        io.SetPad1Input(state, padType);
        md_sms_io.SetPad1Input(state);
    }

    public bool IsMasterSystemMode => md_main.g_masterSystemMode;

    public void SetPad2InputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        var io = md_main.g_md_io;
        if (io == null)
            return;

        var state = new MdPadState
        {
            Up = up,
            Down = down,
            Left = left,
            Right = right,
            A = a,
            B = b,
            C = c,
            Start = start,
            X = x,
            Y = y,
            Z = z,
            Mode = mode
        };

        io.SetPad2Input(state, padType);
        md_sms_io.SetPad2Input(state);
    }

    public void SetShowSmsOverscan(bool enabled)
    {
        _vdp?.SetShowOverscan(enabled);
    }

    public bool GetShowSmsOverscan()
    {
        return _vdp?.GetShowOverscan() ?? false;
    }

    #region Framebuffer Analyzer

    /// <summary>
    /// Framebuffer analyzer for live debugging - samples pixels from grid regions
    /// </summary>
    public class FramebufferAnalyzer
    {
        private readonly MdTracerAdapter _adapter;
        private int _cols = 8;
        private int _rows = 6;
        private int _sampleEveryNFrames = 1;
        private int _frameCounter;
        private long _lastLogTicks;

        public bool Enabled { get; set; }
        public string OutputPrefix { get; set; } = "[FB-ANALYZER]";

        public FramebufferAnalyzer(MdTracerAdapter adapter)
        {
            _adapter = adapter;
        }

        /// <summary>
        /// Configure grid size (cols x rows)
        /// </summary>
        public void ConfigureGrid(int cols, int rows)
        {
            _cols = Math.Max(1, Math.Min(16, cols));
            _rows = Math.Max(1, Math.Min(16, rows));
        }

        /// <summary>
        /// Set sampling rate (sample every N frames)
        /// </summary>
        public void SetSampleRate(int everyNFrames)
        {
            _sampleEveryNFrames = Math.Max(1, everyNFrames);
        }

        /// <summary>
        /// Analyze and log framebuffer content at regular intervals
        /// </summary>
        public void AnalyzeFrame()
        {
            if (!Enabled || _adapter._frameBufferFront.Length == 0)
                return;

            _frameCounter++;
            if (_frameCounter % _sampleEveryNFrames != 0)
                return;

            // When sampling every frame, log every 60 frames (~1 sec at 60fps)
            // Otherwise log every sample
            if (_sampleEveryNFrames > 1)
            {
                long now = Stopwatch.GetTimestamp();
                long intervalTicks = Stopwatch.Frequency; // 1 second
                if (now - _lastLogTicks < intervalTicks)
                    return;
                _lastLogTicks = now;
            }
            else if (_frameCounter % 60 != 1)
            {
                // Only log every 60th frame when sampling every frame
                return;
            }

            var fb = _adapter._frameBufferFront;
            int w = _adapter._fbW;
            int h = _adapter._fbH;
            int stride = _adapter._fbStride;

            if (w <= 0 || h <= 0 || stride <= 0)
                return;

            Console.Error.WriteLine($"{OutputPrefix} Frame {_frameCounter} - {w}x{h} framebuffer analysis:");
            Console.Error.WriteLine(new string('=', 70));

            int cellW = Math.Max(1, w / _cols);
            int cellH = Math.Max(1, h / _rows);

            for (int row = 0; row < _rows; row++)
            {
                int cy = Math.Min(h - 1, row * cellH + cellH / 2);
                Console.Error.Write($"Row {row:D2}: ");

                for (int col = 0; col < _cols; col++)
                {
                    int cx = Math.Min(w - 1, col * cellW + cellW / 2);
                    int offset = cy * stride + (cx * 4);

                    if (offset + 3 < fb.Length)
                    {
                        byte b = fb[offset + 0];
                        byte g = fb[offset + 1];
                        byte r = fb[offset + 2];
                        byte a = fb[offset + 3];

                        // Determine color name for quick reference
                        string colorName = GetColorName(r, g, b);

                        Console.Error.Write($"({cx:D3},{cy:D3}){colorName,-8} ");
                    }
                    else
                    {
                        Console.Error.Write("   OUT OF BOUNDS   ");
                    }
                }
                Console.Error.WriteLine();
            }

            Console.Error.WriteLine($"{OutputPrefix} Pixel format: BGRA (byte order: B={fb[0]:X2}, G={fb[1]:X2}, R={fb[2]:X2}, A={fb[3]:X2})");
            Console.Error.WriteLine(new string('=', 70));
        }

        private static string GetColorName(byte r, byte g, byte b)
        {
            // Simple color classification
            int gray = (r + g + b) / 3;
            if (gray < 20) return "BLACK";
            if (gray > 235) return "WHITE";
            if (r > 200 && g > 200 && b < 50) return "YELLOW";
            if (r > 200 && g < 50 && b > 200) return "MAGENTA";
            if (r < 50 && g > 200 && b > 200) return "CYAN";
            if (r > 200 && g < 50 && b < 50) return "RED";
            if (r < 50 && g > 200 && b < 50) return "GREEN";
            if (r < 50 && g < 50 && b > 200) return "BLUE";
            if (r > 150 && g > 100 && b < 50) return "ORANGE";
            if (r > 150 && g > 50 && b > 150) return "PINK";
            if (r > 100 && g > 100 && b > 200) return "LAVENDER";
            if (gray < 80) return "DARKGRAY";
            if (gray < 160) return "GRAY";
            return "LTGRAY";
        }

        /// <summary>
        /// Dump hex representation of a specific region
        /// </summary>
        public void DumpRegionHex(int x, int y, int width, int height)
        {
            var fb = _adapter._frameBufferFront;
            int stride = _adapter._fbStride;

            Console.WriteLine($"{OutputPrefix} Hex dump region ({x},{y}) {width}x{height}:");

            for (int dy = 0; dy < height; dy++)
            {
                int py = y + dy;
                if (py >= _adapter._fbH) break;

                int offset = py * stride + (x * 4);
                var line = new StringBuilder();

                for (int dx = 0; dx < width && offset + 3 < fb.Length; dx++)
                {
                    byte b = fb[offset + 0];
                    byte g = fb[offset + 1];
                    byte r = fb[offset + 2];
                    byte a = fb[offset + 3];

                    line.Append($"{b:X2}{g:X2}{r:X2}{a:X2} ");
                    offset += 4;

                    if (dx > 0 && (dx + 1) % 8 == 0)
                        line.Append(" ");
                }
                Console.WriteLine($"  {py:D3}: {line}");
            }
        }
    }

    #endregion
}
