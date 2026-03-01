using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.MdTracerCore
{
    internal sealed class JgYm2612
    {
        private static readonly bool HoldLastSampleOnUnderflow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_HOLD_LAST_ON_UNDERFLOW"), "1", StringComparison.Ordinal);
        private static readonly bool TraceDacRate =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DACRATE"), "1", StringComparison.Ordinal);
        private readonly Ym2612 _ym;
        private byte _lastYmAddr;
        private byte _lastYmVal;
        private string _lastYmSource = "none";
        private long _systemCycleRemainder;
        private static readonly int LegacySystemCyclesPerYmTick = ParseLegacySystemCyclesPerYmTick();
        private static bool _timingConfigLogged;
        private short[] _ringBuffer = new short[RingFramesDefault * 2];
        private int _ringRead;
        private int _ringWrite;
        private int _ringCountFrames;
        private short _lastOutL;
        private short _lastOutR;
        private long _dacRateWindowStartTicks;
        private int _dacRateWindowWrites;
        private long _dacRateDeltaTicksSum;
        private long _dacRateDeltaTicksMin = long.MaxValue;
        private long _dacRateDeltaTicksMax;
        private long _dacRateLastWriteTicks;

        public JgYm2612()
        {
            bool quantize = ReadEnvDefaultOn("EUTHERDRIVE_YM_QUANTIZE", defaultValue: true);
            bool ladder = ReadEnvDefaultOn("EUTHERDRIVE_YM_LADDER", defaultValue: true);
            Opn2BusyBehavior busy = ParseOpn2BusyBehavior();

            _ym = new Ym2612(new bool[6], quantize, ladder, busy);
        }

        public byte Read(uint address)
        {
            return _ym.ReadRegister((ushort)(address & 0x0003));
        }

        public void Write(uint address, byte value, string source)
        {
            int port = (int)(address & 0x0003);
            switch (port)
            {
                case 0:
                    _lastYmAddr = value;
                    _ym.WriteAddress1(value);
                    break;
                case 1:
                    _lastYmVal = value;
                    _lastYmSource = source;
                    _ym.WriteData(value);
                    if (_lastYmAddr == 0x2A)
                        RecordDacWriteRate();
                    break;
                case 2:
                    _lastYmAddr = value;
                    _ym.WriteAddress2(value);
                    break;
                case 3:
                    _lastYmVal = value;
                    _lastYmSource = source;
                    _ym.WriteData(value);
                    break;
            }
        }

        public void Start()
        {
            _ym.Reset();
            _systemCycleRemainder = 0;
            _ringRead = 0;
            _ringWrite = 0;
            _ringCountFrames = 0;
            _lastOutL = 0;
            _lastOutR = 0;
            _dacRateWindowStartTicks = 0;
            _dacRateWindowWrites = 0;
            _dacRateDeltaTicksSum = 0;
            _dacRateDeltaTicksMin = long.MaxValue;
            _dacRateDeltaTicksMax = 0;
            _dacRateLastWriteTicks = 0;
            if (!_timingConfigLogged)
            {
                _timingConfigLogged = true;
                Console.WriteLine($"[JGYM-TIMING] LegacySystemCyclesPerYmTick={LegacySystemCyclesPerYmTick} FmSampleDivider={FmSampleDivider}");
            }
        }

        public void FullReset()
        {
            _ym.Reset();
            _systemCycleRemainder = 0;
            _ringRead = 0;
            _ringWrite = 0;
            _ringCountFrames = 0;
            _lastOutL = 0;
            _lastOutR = 0;
            _dacRateWindowStartTicks = 0;
            _dacRateWindowWrites = 0;
            _dacRateDeltaTicksSum = 0;
            _dacRateDeltaTicksMin = long.MaxValue;
            _dacRateDeltaTicksMax = 0;
            _dacRateLastWriteTicks = 0;
        }

        public void MarkZ80SafeBootComplete()
        {
            // No-op for jgenesis core
        }

        public void Update()
        {
            // Generate one YM sample and discard it
            _ym.Tick(FmSampleDivider, null);
        }

        public void UpdateBatch(Span<short> dst, int frames)
        {
            int maxFrames = dst.Length / 2;
            if (frames > maxFrames)
                frames = maxFrames;

            int write = 0;
            for (int i = 0; i < frames; i++)
            {
                if (_ringCountFrames > 0)
                {
                    int idx = _ringRead * 2;
                    short l = _ringBuffer[idx];
                    short r = _ringBuffer[idx + 1];
                    _lastOutL = l;
                    _lastOutR = r;
                    dst[write++] = l;
                    dst[write++] = r;
                    _ringRead++;
                    if (_ringRead >= RingFramesCapacity)
                        _ringRead = 0;
                    _ringCountFrames--;
                    continue;
                }

                if (HoldLastSampleOnUnderflow)
                {
                    // Opt-in: hold last sample to reduce zipper/noise bursts.
                    dst[write++] = _lastOutL;
                    dst[write++] = _lastOutR;
                }
                else
                {
                    // Default/legacy behavior.
                    dst[write++] = 0;
                    dst[write++] = 0;
                }
            }
        }

        public void EnsureAdvanceEachFrame()
        {
            // YM timing is driven by AdvanceSystemCycles()
        }

        public void TickTimersFromZ80Cycles(int z80Cycles)
        {
            _ = z80Cycles;
            // No-op; YM timing is driven by audio sample generation
        }

        public void FlushDacRateFrame(long frame)
        {
            _ = frame;
            MaybeLogDacRate(force: false);
        }

        public void ConsumeAudStatCounters(
            out int keyOn,
            out int fnum,
            out int param,
            out int dacCmd,
            out int dacDat)
        {
            keyOn = 0;
            fnum = 0;
            param = 0;
            dacCmd = 0;
            dacDat = 0;
        }

        public void FlushTimerStats(long frame)
        {
            _ = frame;
        }

        public int DebugDacEnabled => _ym.DacEnabled ? 0x80 : 0;
        public int DebugDacData => _ym.DacSample;
        public byte DebugLastYmAddr => _lastYmAddr;
        public byte DebugLastYmVal => _lastYmVal;
        public string DebugLastYmSource => _lastYmSource;

        public void DumpRecentYmWrites(string tag, int limit)
        {
            _ = tag;
            _ = limit;
        }

        public bool AudStatEnabled => false;

        private static bool ReadEnvDefaultOn(string name, bool defaultValue)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return defaultValue;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static Opn2BusyBehavior ParseOpn2BusyBehavior()
        {
            // jgenesis default in genesis-config is Ym3438.
            string? mode = Environment.GetEnvironmentVariable("EUTHERDRIVE_OPN2_BUSY_BEHAVIOR");
            if (!string.IsNullOrWhiteSpace(mode))
            {
                string m = mode.Trim().ToLowerInvariant();
                if (m == "ym2612")
                    return Opn2BusyBehavior.Ym2612;
                if (m == "ym3438" || m == "default")
                    return Opn2BusyBehavior.Ym3438;
                if (m == "alwayszero" || m == "zero" || m == "off")
                    return Opn2BusyBehavior.AlwaysZero;
            }

            // Backward-compatible override:
            // EUTHERDRIVE_EMULATE_YM_BUSY=1 used to mean YM2612 busy model.
            if (ReadEnvDefaultOn("EUTHERDRIVE_EMULATE_YM_BUSY", defaultValue: false))
                return Opn2BusyBehavior.Ym2612;

            return Opn2BusyBehavior.Ym3438;
        }

        private static short ToSample(double value)
        {
            double scaled = value * short.MaxValue;
            if (scaled > short.MaxValue) return short.MaxValue;
            if (scaled < short.MinValue) return short.MinValue;
            return (short)Math.Round(scaled);
        }

        private const int FmSampleDivider = 24;
        private const int RingFramesDefault = 16384;

        private static int ParseLegacySystemCyclesPerYmTick()
        {
            // Legacy fallback path only (used when explicit MCLK YM tick drive is disabled).
            // Keep previous compatibility default.
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_LEGACY_SYSTEM_CYCLES_PER_TICK");
            if (string.IsNullOrWhiteSpace(raw))
                raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_SYSTEM_CYCLES_PER_TICK");
            if (!string.IsNullOrWhiteSpace(raw) &&
                int.TryParse(raw.Trim(), out int value) &&
                value > 0)
            {
                return value;
            }
            return 6;
        }

        private int RingFramesCapacity => _ringBuffer.Length / 2;

        private void WriteSample(short left, short right)
        {
            if (_ringBuffer.Length == 0)
                return;

            int idx = _ringWrite * 2;
            _ringBuffer[idx] = left;
            _ringBuffer[idx + 1] = right;
            _ringWrite++;
            if (_ringWrite >= RingFramesCapacity)
                _ringWrite = 0;

            if (_ringCountFrames == RingFramesCapacity)
            {
                _ringRead++;
                if (_ringRead >= RingFramesCapacity)
                    _ringRead = 0;
            }
            else
            {
                _ringCountFrames++;
            }
        }

        public void AdvanceSystemCycles(long cycles)
        {
            if (cycles <= 0)
                return;

            long totalCycles = cycles + _systemCycleRemainder;
            long ymTicks = totalCycles / LegacySystemCyclesPerYmTick;
            _systemCycleRemainder = totalCycles % LegacySystemCyclesPerYmTick;

            if (ymTicks <= 0)
                return;

            if (ymTicks > int.MaxValue)
                ymTicks = int.MaxValue;

            AdvanceYmTicks((int)ymTicks);
        }

        public void AdvanceYmTicks(int ymTicks)
        {
            if (ymTicks <= 0)
                return;

            _ym.Tick(ymTicks, (left, right) =>
            {
                WriteSample(ToSample(left), ToSample(right));
            });
        }

        private void RecordDacWriteRate()
        {
            if (!TraceDacRate)
                return;

            long now = Stopwatch.GetTimestamp();
            if (_dacRateWindowStartTicks == 0)
                _dacRateWindowStartTicks = now;

            if (_dacRateLastWriteTicks != 0)
            {
                long delta = now - _dacRateLastWriteTicks;
                _dacRateDeltaTicksSum += delta;
                if (delta < _dacRateDeltaTicksMin) _dacRateDeltaTicksMin = delta;
                if (delta > _dacRateDeltaTicksMax) _dacRateDeltaTicksMax = delta;
            }

            _dacRateLastWriteTicks = now;
            _dacRateWindowWrites++;
            MaybeLogDacRate(force: false);
        }

        private void MaybeLogDacRate(bool force)
        {
            if (!TraceDacRate || _dacRateWindowStartTicks == 0)
                return;

            long now = Stopwatch.GetTimestamp();
            long elapsedTicks = now - _dacRateWindowStartTicks;
            if (!force && elapsedTicks < Stopwatch.Frequency)
                return;

            double elapsedSec = elapsedTicks <= 0 ? 0.0 : elapsedTicks / (double)Stopwatch.Frequency;
            double rateHz = elapsedSec > 0.0 ? _dacRateWindowWrites / elapsedSec : 0.0;
            string avgCycles = "n/a";
            string minCycles = "n/a";
            string maxCycles = "n/a";
            if (_dacRateWindowWrites > 1)
            {
                double avgTicks = _dacRateDeltaTicksSum / (double)(_dacRateWindowWrites - 1);
                avgCycles = avgTicks.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                minCycles = _dacRateDeltaTicksMin.ToString(System.Globalization.CultureInfo.InvariantCulture);
                maxCycles = _dacRateDeltaTicksMax.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            Console.WriteLine(
                $"[JGYM-DACRATE] writes={_dacRateWindowWrites} hz={rateHz:0.0} avgTicks={avgCycles} minTicks={minCycles} maxTicks={maxCycles}");

            _dacRateWindowStartTicks = now;
            _dacRateWindowWrites = 0;
            _dacRateDeltaTicksSum = 0;
            _dacRateDeltaTicksMin = long.MaxValue;
            _dacRateDeltaTicksMax = 0;
            _dacRateLastWriteTicks = 0;
        }
    }

    internal enum Opn2BusyBehavior
    {
        Ym2612,
        Ym3438,
        AlwaysZero,
    }

    internal enum FrequencyMode
    {
        Single,
        Multiple,
    }

    internal enum RegisterGroup
    {
        One,
        Two,
    }

    internal enum TimerTickEffect
    {
        None,
        Overflowed,
    }

    internal struct TimerControl
    {
        public bool Enabled;
        public bool OverflowFlagEnabled;
        public bool ClearOverflowFlag;
    }

    internal sealed class TimerA
    {
        private bool _enabled;
        private bool _enabledNext;
        private bool _overflowFlagEnabled;
        private bool _overflowFlag;
        private ushort _interval;
        private ushort _counter;

        public TimerA()
        {
            _enabled = false;
            _enabledNext = false;
            _overflowFlagEnabled = false;
            _overflowFlag = false;
            _interval = 0;
            _counter = 0;
        }

        public TimerTickEffect Tick()
        {
            const ushort Overflow = 1024;

            if (!_enabled)
            {
                if (_enabledNext)
                {
                    _enabled = true;
                    _counter = _interval;
                }
                return TimerTickEffect.None;
            }

            _enabled = _enabledNext;

            _counter++;
            if (_counter == Overflow)
            {
                _overflowFlag |= _overflowFlagEnabled;
                _counter = _interval;
                return TimerTickEffect.Overflowed;
            }

            return TimerTickEffect.None;
        }

        public bool OverflowFlag => _overflowFlag;
        public ushort Interval => _interval;

        public void WriteControl(TimerControl control)
        {
            _enabledNext = control.Enabled;
            _overflowFlagEnabled = control.OverflowFlagEnabled;
            if (control.ClearOverflowFlag)
                _overflowFlag = false;
        }

        public void WriteIntervalHigh(byte value)
        {
            _interval = (ushort)((_interval & 3) | (value << 2));
        }

        public void WriteIntervalLow(byte value)
        {
            _interval = (ushort)((_interval & ~3) | (value & 3));
        }
    }

    internal sealed class TimerB
    {
        private const byte Divider = 16;
        private bool _enabled;
        private bool _enabledNext;
        private bool _overflowFlagEnabled;
        private bool _overflowFlag;
        public byte Interval;
        private byte _counter;
        private byte _divider;

        public TimerB()
        {
            _enabled = false;
            _enabledNext = false;
            _overflowFlagEnabled = false;
            _overflowFlag = false;
            Interval = 0;
            _counter = 0;
            _divider = Divider;
        }

        public void Tick()
        {
            _divider--;
            if (_divider == 0)
            {
                _divider = Divider;

                if (_enabled)
                {
                    _counter++;
                    if (_counter == 0)
                    {
                        _overflowFlag |= _overflowFlagEnabled;
                        _counter = Interval;
                    }
                }
            }

            if (!_enabled && _enabledNext)
                _counter = Interval;
            _enabled = _enabledNext;
        }

        public bool OverflowFlag => _overflowFlag;

        public void WriteControl(TimerControl control)
        {
            _enabledNext = control.Enabled;
            _overflowFlagEnabled = control.OverflowFlagEnabled;
            if (control.ClearOverflowFlag)
                _overflowFlag = false;
        }
    }

    internal sealed class LowFrequencyOscillator
    {
        private const byte LfoCounterMask = 0x7F;
        private static readonly byte[] LfoDividers =
        {
            108, 77, 71, 67, 62, 44, 8, 5,
        };

        private static readonly ushort[,] FmIncrementTable =
        {
            {0,0,0,0,0,0,0,0},
            {0,0,0,0,4,4,4,4},
            {0,0,0,4,4,4,8,8},
            {0,0,4,4,8,8,12,12},
            {0,0,4,8,8,8,12,16},
            {0,0,8,12,16,16,20,24},
            {0,0,16,24,32,32,40,48},
            {0,0,32,48,64,64,80,96},
        };

        private bool _enabled;
        private byte _counter;
        private byte _divider;
        private byte _frequency;

        public LowFrequencyOscillator()
        {
            _enabled = false;
            _counter = 0;
            _divider = 0;
            _frequency = LfoDividers[0];
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
                _counter = 0;
        }

        public void SetFrequency(byte frequency)
        {
            _frequency = LfoDividers[frequency];
        }

        public byte Counter => _counter;

        public void Tick()
        {
            _divider++;
            if (_divider >= _frequency)
            {
                _divider = 0;
                if (_enabled)
                    _counter = (byte)((_counter + 1) & LfoCounterMask);
            }
        }

        public static ushort FrequencyModulation(byte lfoCounter, byte fmSensitivity, ushort fNumber)
        {
            if (fmSensitivity == 0)
                return (ushort)(fNumber << 1);

            int fmTableIdx = ((lfoCounter & 0x20) != 0)
                ? (0x1F - (lfoCounter & 0x1F)) >> 2
                : (lfoCounter & 0x1F) >> 2;

            ushort rawIncrement = FmIncrementTable[fmSensitivity, fmTableIdx];
            ushort fmIncrement = 0;
            for (int i = 4; i <= 10; i++)
            {
                int bit = (fNumber >> i) & 1;
                fmIncrement += (ushort)(bit * (rawIncrement >> (10 - i)));
            }

            if ((lfoCounter & 0x40) != 0)
                return (ushort)(((fNumber << 1) - fmIncrement) & 0x0FFF);

            return (ushort)(((fNumber << 1) + fmIncrement) & 0x0FFF);
        }

        public static ushort AmplitudeModulation(byte lfoCounter, byte amSensitivity)
        {
            byte amAttenuation = (lfoCounter & 0x40) != 0
                ? (byte)(lfoCounter & 0x3F)
                : (byte)(0x3F - lfoCounter);

            ushort attenuation = (ushort)(amAttenuation << 1);

            return amSensitivity switch
            {
                0 => 0,
                1 => (ushort)(attenuation >> 3),
                2 => (ushort)(attenuation >> 1),
                3 => attenuation,
                _ => 0,
            };
        }
    }

    internal sealed class PhaseGenerator
    {
        private const uint ShiftedFNumMask = 0x1FFFF;
        private const uint PhaseCounterMask = 0xFFFFF;

        private static readonly byte[,] DetuneTable =
        {
            {0,0,1,2},  {0,0,1,2},  {0,0,1,2},  {0,0,1,2},
            {0,1,2,2},  {0,1,2,3},  {0,1,2,3},  {0,1,2,3},
            {0,1,2,4},  {0,1,3,4},  {0,1,3,4},  {0,1,3,5},
            {0,2,4,5},  {0,2,4,6},  {0,2,4,6},  {0,2,5,7},
            {0,2,5,8},  {0,3,6,8},  {0,3,6,9},  {0,3,7,10},
            {0,4,8,11}, {0,4,8,12}, {0,4,9,13}, {0,5,10,14},
            {0,5,11,16}, {0,6,12,17}, {0,6,13,19}, {0,7,14,20},
            {0,8,16,22}, {0,8,16,22}, {0,8,16,22}, {0,8,16,22},
        };

        public ushort FNumber;
        public byte Block;
        public byte Multiple;
        public byte Detune;

        private uint _counter;
        private ushort _currentOutput;

        public void Reset()
        {
            _counter = 0;
        }

        public void Clock(byte lfoCounter, byte fmSensitivity)
        {
            uint phaseIncrement = ComputePhaseIncrement(lfoCounter, fmSensitivity);
            _counter = (_counter + phaseIncrement) & PhaseCounterMask;
            _currentOutput = (ushort)(_counter >> 10);
        }

        private uint ComputePhaseIncrement(byte lfoCounter, byte fmSensitivity)
        {
            ushort modulatedFNum = LowFrequencyOscillator.FrequencyModulation(lfoCounter, fmSensitivity, FNumber);
            uint shiftedFNum = ((uint)modulatedFNum << Block) >> 2;

            byte keyCode = Ym2612.ComputeKeyCode(FNumber, Block);
            int detuneMagnitude = Detune & 3;
            uint detuneIncrement = DetuneTable[keyCode, detuneMagnitude];
            uint detunedFNum = (Detune & 4) != 0
                ? (shiftedFNum - detuneIncrement) & ShiftedFNumMask
                : (shiftedFNum + detuneIncrement) & ShiftedFNumMask;

            return Multiple switch
            {
                0 => detunedFNum >> 1,
                _ => detunedFNum * Multiple,
            };
        }

        public ushort CurrentPhase => _currentOutput;
    }

    internal sealed class EnvelopeGenerator
    {
        private const byte EnvelopeDivider = 3;
        private const ushort SsgAttenuationThreshold = 0x200;
        public const ushort AttenuationMask = 0x03FF;
        public const ushort MaxAttenuation = AttenuationMask;

        private static readonly byte[,] AttenuationIncrements =
        {
            {0,0,0,0,0,0,0,0}, {0,0,0,0,0,0,0,0}, {0,1,0,1,0,1,0,1}, {0,1,0,1,0,1,0,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,0,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,0,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {0,1,0,1,0,1,0,1}, {0,1,0,1,1,1,0,1}, {0,1,1,1,0,1,1,1}, {0,1,1,1,1,1,1,1},
            {1,1,1,1,1,1,1,1}, {1,1,1,2,1,1,1,2}, {1,2,1,2,1,2,1,2}, {1,2,2,2,1,2,2,2},
            {2,2,2,2,2,2,2,2}, {2,2,2,4,2,2,2,4}, {2,4,2,4,2,4,2,4}, {2,4,4,4,2,4,4,4},
            {4,4,4,4,4,4,4,4}, {4,4,4,8,4,4,4,8}, {4,8,4,8,4,8,4,8}, {4,8,8,8,4,8,8,8},
            {8,8,8,8,8,8,8,8}, {8,8,8,8,8,8,8,8}, {8,8,8,8,8,8,8,8}, {8,8,8,8,8,8,8,8},
        };

        private enum EnvelopePhase
        {
            Attack,
            Decay,
            Sustain,
            Release,
        }

        public byte AttackRate;
        public byte DecayRate;
        public byte SustainRate;
        public byte ReleaseRate;
        public byte TotalLevel;
        public byte SustainLevel;
        public byte KeyScale;

        private EnvelopePhase _phase;
        private ushort _attenuation;
        public byte KeyScaleRate;
        private ushort _cycleCount;
        private byte _divider;
        private bool _ssgEnabled;
        private bool _ssgAttack;
        private bool _ssgAlternate;
        private bool _ssgHold;
        private bool _ssgInvertOutput;

        public EnvelopeGenerator()
        {
            AttackRate = 0;
            DecayRate = 0;
            SustainRate = 0;
            ReleaseRate = 0;
            TotalLevel = 0;
            SustainLevel = 0;
            KeyScale = 0;
            _phase = EnvelopePhase.Release;
            _attenuation = MaxAttenuation;
            KeyScaleRate = 0;
            _cycleCount = 1;
            _divider = EnvelopeDivider;
            _ssgEnabled = false;
            _ssgAttack = false;
            _ssgAlternate = false;
            _ssgHold = false;
            _ssgInvertOutput = false;
        }

        public void Clock(PhaseGenerator phaseGenerator)
        {
            if (_ssgEnabled)
                SsgClock(phaseGenerator);

            _divider--;
            if (_divider == 0)
            {
                _divider = EnvelopeDivider;
                EnvelopeClock();
            }
        }

        private void EnvelopeClock()
        {
            _cycleCount++;
            _cycleCount = (ushort)((_cycleCount & 0x0FFF) + (_cycleCount >> 12));

            ushort sustainLevel = SustainLevel == 15
                ? (ushort)((MaxAttenuation >> 5) << 5)
                : (ushort)(SustainLevel << 5);

            if (_phase == EnvelopePhase.Attack && _attenuation == 0)
                _phase = EnvelopePhase.Decay;

            if (_phase == EnvelopePhase.Decay && _attenuation >= sustainLevel)
                _phase = EnvelopePhase.Sustain;

            int r = _phase switch
            {
                EnvelopePhase.Attack => AttackRate,
                EnvelopePhase.Decay => DecayRate,
                EnvelopePhase.Sustain => SustainRate,
                EnvelopePhase.Release => (ReleaseRate << 1) | 1,
                _ => 0,
            };

            int rate = r == 0 ? 0 : Math.Min(63, 2 * r + KeyScaleRate);
            int updateFrequencyShift = 11 - (rate >> 2);
            if ((_cycleCount & ((1 << updateFrequencyShift) - 1)) == 0)
            {
                int incrementIdx = (_cycleCount >> updateFrequencyShift) & 7;
                ushort increment = AttenuationIncrements[rate, incrementIdx];

                switch (_phase)
                {
                    case EnvelopePhase.Attack:
                        if (rate <= 61)
                        {
                            _attenuation = (ushort)((_attenuation + (((~_attenuation) * increment) >> 4)) & AttenuationMask);
                        }
                        break;
                    case EnvelopePhase.Decay:
                    case EnvelopePhase.Sustain:
                    case EnvelopePhase.Release:
                        if (_ssgEnabled)
                        {
                            if (_attenuation < SsgAttenuationThreshold)
                                _attenuation = (ushort)Math.Min(MaxAttenuation, _attenuation + 4 * increment);
                        }
                        else
                        {
                            _attenuation = (ushort)Math.Min(MaxAttenuation, _attenuation + increment);
                        }
                        break;
                }
            }
        }

        private void SsgClock(PhaseGenerator phaseGenerator)
        {
            if (_attenuation < SsgAttenuationThreshold)
                return;

            if (_ssgAlternate)
            {
                if (_ssgHold)
                {
                    _ssgInvertOutput = true;
                }
                else
                {
                    _ssgInvertOutput = !_ssgInvertOutput;
                }
            }

            if (!_ssgAlternate && !_ssgHold)
            {
                phaseGenerator.Reset();
            }

            if ((_phase == EnvelopePhase.Decay || _phase == EnvelopePhase.Sustain) && !_ssgHold)
            {
                if (2 * AttackRate + KeyScaleRate >= 62)
                {
                    _attenuation = 0;
                    _phase = EnvelopePhase.Decay;
                }
                else
                {
                    _phase = EnvelopePhase.Attack;
                }
            }
            else if (_phase == EnvelopePhase.Release
                     || (_phase != EnvelopePhase.Attack && _ssgInvertOutput == _ssgAttack))
            {
                _attenuation = MaxAttenuation;
            }
        }

        public bool IsKeyOn => _phase != EnvelopePhase.Release;

        public void KeyOn()
        {
            if (IsKeyOn)
                return;

            int rate = 2 * AttackRate + KeyScaleRate;
            if (rate >= 62)
            {
                _phase = EnvelopePhase.Decay;
                _attenuation = 0;
            }
            else
            {
                _phase = EnvelopePhase.Attack;
            }

            _ssgInvertOutput = false;
        }

        public void KeyOff()
        {
            if (_ssgEnabled && _phase != EnvelopePhase.Release && _ssgInvertOutput != _ssgAttack)
            {
                _attenuation = (ushort)((SsgAttenuationThreshold - _attenuation) & AttenuationMask);
            }
            _phase = EnvelopePhase.Release;
        }

        public void UpdateKeyScaleRate(ushort fNumber, byte block)
        {
            byte keyCode = Ym2612.ComputeKeyCode(fNumber, block);
            KeyScaleRate = (byte)(keyCode >> (3 - KeyScale));
        }

        public ushort CurrentAttenuation()
        {
            ushort attenuation = _ssgEnabled
                                 && _phase != EnvelopePhase.Release
                                 && _ssgInvertOutput != _ssgAttack
                ? (ushort)((SsgAttenuationThreshold - _attenuation) & AttenuationMask)
                : _attenuation;

            ushort totalLevel = (ushort)(TotalLevel << 3);
            return (ushort)Math.Min(MaxAttenuation, attenuation + totalLevel);
        }

        public void WriteSsgRegister(byte value)
        {
            _ssgEnabled = (value & 0x08) != 0;
            _ssgAttack = (value & 0x04) != 0;
            _ssgAlternate = (value & 0x02) != 0;
            _ssgHold = (value & 0x01) != 0;
        }
    }

    internal sealed class FmOperator
    {
        public PhaseGenerator Phase = new PhaseGenerator();
        public EnvelopeGenerator Envelope = new EnvelopeGenerator();
        public bool AmEnabled;
        public int CurrentOutput;
        public int LastOutput;
        public byte LfoCounter;
        public byte AmSensitivity;

        public void UpdateFrequency(ushort fNumber, byte block)
        {
            Phase.FNumber = fNumber;
            Phase.Block = block;
            Envelope.UpdateKeyScaleRate(fNumber, block);
        }

        public void UpdateKeyScale(byte keyScale)
        {
            Envelope.KeyScale = keyScale;
            Envelope.UpdateKeyScaleRate(Phase.FNumber, Phase.Block);
        }

        public void KeyOnOrOff(bool value)
        {
            if (value)
            {
                if (!Envelope.IsKeyOn)
                {
                    Phase.Reset();
                    Envelope.KeyOn();
                }
            }
            else
            {
                Envelope.KeyOff();
            }
        }

        public int SampleClock(int modulationInput)
        {
            int phase = (Phase.CurrentPhase + modulationInput) & Ym2612.PhaseMask;
            bool sign = (phase & 0x200) != 0;
            ushort sineAttenuation = Ym2612.PhaseToAttenuation((ushort)phase);

            ushort envelopeAttenuation = Envelope.CurrentAttenuation();
            ushort envelopeAmAttenuation = envelopeAttenuation;
            if (AmEnabled)
            {
                ushort amAttenuation = LowFrequencyOscillator.AmplitudeModulation(LfoCounter, AmSensitivity);
                int combined = envelopeAttenuation + amAttenuation;
                if (combined > EnvelopeGenerator.MaxAttenuation)
                    combined = EnvelopeGenerator.MaxAttenuation;
                envelopeAmAttenuation = (ushort)combined;
            }

            int totalAttenuation = sineAttenuation + (envelopeAmAttenuation << 2);
            ushort amplitude = Ym2612.AttenuationToAmplitude((ushort)totalAttenuation);
            int output = sign ? -(short)amplitude : (short)amplitude;

            LastOutput = CurrentOutput;
            CurrentOutput = output;
            return output;
        }
    }

    internal sealed class FmChannel
    {
        public readonly FmOperator[] Operators =
        {
            new FmOperator(),
            new FmOperator(),
            new FmOperator(),
            new FmOperator(),
        };

        public FrequencyMode Mode = FrequencyMode.Single;
        public byte PendingChFNumberHigh;
        public ushort ChannelFNumber;
        public byte PendingChBlock;
        public byte ChannelBlock;
        public readonly byte[] PendingOpFNumbersHigh = new byte[3];
        public readonly ushort[] OperatorFNumbers = new ushort[3];
        public readonly byte[] PendingOpBlocks = new byte[3];
        public readonly byte[] OperatorBlocks = new byte[3];
        public byte Algorithm;
        public byte FeedbackLevel;
        public byte AmSensitivity;
        public byte FmSensitivity;
        public bool LOutput = true;
        public bool ROutput = true;
        public int CurrentOutput;

        public void Clock(byte lfoCounter, int quantizationMask)
        {
            foreach (var op in Operators)
            {
                op.Phase.Clock(lfoCounter, FmSensitivity);
                op.Envelope.Clock(op.Phase);
                op.LfoCounter = lfoCounter;
                op.AmSensitivity = AmSensitivity;
            }

            GenerateSample(quantizationMask);
        }

        public void GenerateSample(int outMask)
        {
            int op1Feedback = FeedbackLevel == 0
                ? 0
                : (Operators[0].CurrentOutput + Operators[0].LastOutput) >> (10 - FeedbackLevel);

            int sample;
            switch (Algorithm)
            {
                case 0:
                {
                    int m1 = Operators[0].SampleClock(op1Feedback);
                    int m2Old = Operators[1].CurrentOutput;
                    Operators[1].SampleClock(m1 >> 1);
                    int m3 = Operators[2].SampleClock(m2Old >> 1);
                    int c4 = Operators[3].SampleClock(m3 >> 1);
                    sample = c4 & outMask;
                    break;
                }
                case 1:
                {
                    int m1Old = Operators[0].CurrentOutput;
                    Operators[0].SampleClock(op1Feedback);
                    int m2Old = Operators[1].CurrentOutput;
                    Operators[1].SampleClock(0);
                    int m3 = Operators[2].SampleClock((m1Old + m2Old) >> 1);
                    int c4 = Operators[3].SampleClock(m3 >> 1);
                    sample = c4 & outMask;
                    break;
                }
                case 2:
                {
                    int m1 = Operators[0].SampleClock(op1Feedback);
                    int m2Old = Operators[1].CurrentOutput;
                    Operators[1].SampleClock(0);
                    int m3 = Operators[2].SampleClock(m2Old >> 1);
                    int c4 = Operators[3].SampleClock((m1 + m3) >> 1);
                    sample = c4 & outMask;
                    break;
                }
                case 3:
                {
                    int m1 = Operators[0].SampleClock(op1Feedback);
                    int m2Old = Operators[1].CurrentOutput;
                    Operators[1].SampleClock(m1 >> 1);
                    int m3 = Operators[2].SampleClock(0);
                    int c4 = Operators[3].SampleClock((m2Old + m3) >> 1);
                    sample = c4 & outMask;
                    break;
                }
                case 4:
                {
                    int m1 = Operators[0].SampleClock(op1Feedback);
                    int c2 = Operators[1].SampleClock(m1 >> 1);
                    int m3 = Operators[2].SampleClock(0);
                    int c4 = Operators[3].SampleClock(m3 >> 1);
                    sample = CarrierSum(outMask, c2, c4);
                    break;
                }
                case 5:
                {
                    int m1Old = Operators[0].CurrentOutput;
                    int m1 = Operators[0].SampleClock(op1Feedback);
                    int c2 = Operators[1].SampleClock(m1 >> 1);
                    int c3 = Operators[2].SampleClock(m1Old >> 1);
                    int c4 = Operators[3].SampleClock(m1 >> 1);
                    sample = CarrierSum(outMask, c2, c3, c4);
                    break;
                }
                case 6:
                {
                    int m1 = Operators[0].SampleClock(op1Feedback);
                    int c2 = Operators[1].SampleClock(m1 >> 1);
                    int c3 = Operators[2].SampleClock(0);
                    int c4 = Operators[3].SampleClock(0);
                    sample = CarrierSum(outMask, c2, c3, c4);
                    break;
                }
                case 7:
                {
                    int c1 = Operators[0].SampleClock(op1Feedback);
                    int c2 = Operators[1].SampleClock(0);
                    int c3 = Operators[2].SampleClock(0);
                    int c4 = Operators[3].SampleClock(0);
                    sample = CarrierSum(outMask, c1, c2, c3, c4);
                    break;
                }
                default:
                    sample = 0;
                    break;
            }

            CurrentOutput = sample;
        }

        private static int CarrierSum(int outMask, params int[] carriers)
        {
            int sum = 0;
            for (int i = 0; i < carriers.Length; i++)
                sum += carriers[i] & outMask;

            int min = Ym2612.OperatorOutputMin & outMask;
            int max = Ym2612.OperatorOutputMax & outMask;
            if (sum < min) sum = min;
            else if (sum > max) sum = max;
            return sum;
        }

        public void UpdatePhaseGenerators()
        {
            if (Mode == FrequencyMode.Single)
            {
                ushort fNumber = ChannelFNumber;
                byte block = ChannelBlock;
                foreach (var op in Operators)
                    op.UpdateFrequency(fNumber, block);
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    Operators[i].UpdateFrequency(OperatorFNumbers[i], OperatorBlocks[i]);
                }
                Operators[3].UpdateFrequency(ChannelFNumber, ChannelBlock);
            }
        }
    }

    internal sealed class Ym2612
    {
        private const int FmSampleDivider = 24;
        private const byte WriteBusyCycles = 32;
        private const int Group1BaseChannel = 0;
        private const int Group2BaseChannel = 3;

        public const int PhaseMask = 0x03FF;
        public const int OperatorOutputMin = -0x2000;
        public const int OperatorOutputMax = 0x1FFF;

        private readonly FmChannel[] _channels =
        {
            new FmChannel(), new FmChannel(), new FmChannel(),
            new FmChannel(), new FmChannel(), new FmChannel(),
        };

        private readonly bool[] _channelsMuted;
        private bool _dacChannelEnabled;
        private byte _dacChannelSample;
        private readonly LowFrequencyOscillator _lfo = new LowFrequencyOscillator();
        private byte _selectedRegister;
        private RegisterGroup _selectedRegisterGroup;
        private byte _sampleDivider;
        private byte _busyCyclesRemaining;
        private readonly TimerA _timerA = new TimerA();
        private readonly TimerB _timerB = new TimerB();
        private bool _csmEnabled;
        private bool _quantizeOutput;
        private bool _emulateLadderEffect;
        private Opn2BusyBehavior _busyBehavior;
        private byte _lastStatusRead;
        private uint _statusDecaySamplesRemaining;

        public Ym2612(bool[] channelsMuted, bool quantizeOutput, bool emulateLadderEffect, Opn2BusyBehavior busyBehavior)
        {
            _channelsMuted = channelsMuted;
            _quantizeOutput = quantizeOutput;
            _emulateLadderEffect = emulateLadderEffect;
            _busyBehavior = busyBehavior;
            _selectedRegister = 0;
            _selectedRegisterGroup = RegisterGroup.One;
            _sampleDivider = FmSampleDivider;
            _busyCyclesRemaining = 0;
        }

        public void Reset()
        {
            Array.Copy(new FmChannel[]
            {
                new FmChannel(), new FmChannel(), new FmChannel(),
                new FmChannel(), new FmChannel(), new FmChannel(),
            }, _channels, _channels.Length);

            _dacChannelEnabled = false;
            _dacChannelSample = 0;
            _lfo.SetEnabled(false);
            _selectedRegister = 0;
            _selectedRegisterGroup = RegisterGroup.One;
            _sampleDivider = FmSampleDivider;
            _busyCyclesRemaining = 0;
            _timerA.WriteControl(new TimerControl { Enabled = false, OverflowFlagEnabled = false, ClearOverflowFlag = true });
            _timerB.WriteControl(new TimerControl { Enabled = false, OverflowFlagEnabled = false, ClearOverflowFlag = true });
            _csmEnabled = false;
            _lastStatusRead = 0;
            _statusDecaySamplesRemaining = 0;
        }

        public void WriteAddress1(byte value)
        {
            _selectedRegister = value;
            _selectedRegisterGroup = RegisterGroup.One;
        }

        public void WriteAddress2(byte value)
        {
            _selectedRegister = value;
            _selectedRegisterGroup = RegisterGroup.Two;
        }

        public void WriteData(byte value)
        {
            switch (_selectedRegisterGroup)
            {
                case RegisterGroup.One:
                    WriteGroup1Register(_selectedRegister, value);
                    break;
                case RegisterGroup.Two:
                    WriteGroup2Register(_selectedRegister, value);
                    break;
            }
        }

        private void WriteGroup1Register(byte register, byte value)
        {
            _busyCyclesRemaining = WriteBusyCycles;

            switch (register)
            {
                case 0x22:
                    _lfo.SetEnabled((value & 0x08) != 0);
                    _lfo.SetFrequency((byte)(value & 0x07));
                    break;
                case 0x24:
                    _timerA.WriteIntervalHigh(value);
                    break;
                case 0x25:
                    _timerA.WriteIntervalLow(value);
                    break;
                case 0x26:
                    _timerB.Interval = value;
                    break;
                case 0x27:
                {
                    FrequencyMode mode = (value & 0xC0) != 0 ? FrequencyMode.Multiple : FrequencyMode.Single;
                    _csmEnabled = (value & 0xC0) == 0x80;
                    var channel = _channels[2];
                    channel.Mode = mode;
                    channel.UpdatePhaseGenerators();

                    _timerA.WriteControl(new TimerControl
                    {
                        Enabled = (value & 0x01) != 0,
                        OverflowFlagEnabled = (value & 0x04) != 0,
                        ClearOverflowFlag = (value & 0x10) != 0,
                    });
                    _timerB.WriteControl(new TimerControl
                    {
                        Enabled = (value & 0x02) != 0,
                        OverflowFlagEnabled = (value & 0x08) != 0,
                        ClearOverflowFlag = (value & 0x20) != 0,
                    });
                    break;
                }
                case 0x28:
                {
                    int baseChannel = (value & 0x04) != 0 ? Group2BaseChannel : Group1BaseChannel;
                    int offset = value & 0x03;
                    if (offset < 3)
                    {
                        int channelIdx = baseChannel + offset;
                        var channel = _channels[channelIdx];
                        channel.Operators[0].KeyOnOrOff((value & 0x10) != 0);
                        channel.Operators[1].KeyOnOrOff((value & 0x20) != 0);
                        channel.Operators[2].KeyOnOrOff((value & 0x40) != 0);
                        channel.Operators[3].KeyOnOrOff((value & 0x80) != 0);
                    }
                    break;
                }
                case 0x2A:
                    _dacChannelSample = value;
                    break;
                case 0x2B:
                    _dacChannelEnabled = (value & 0x80) != 0;
                    break;
                default:
                    if (register >= 0x30 && register <= 0x9F)
                        WriteOperatorLevelRegister(register, value, Group1BaseChannel);
                    else if (register >= 0xA0 && register <= 0xBF)
                        WriteChannelLevelRegister(register, value, Group1BaseChannel);
                    break;
            }
        }

        private void WriteGroup2Register(byte register, byte value)
        {
            _busyCyclesRemaining = WriteBusyCycles;
            if (register >= 0x30 && register <= 0x9F)
                WriteOperatorLevelRegister(register, value, Group2BaseChannel);
            else if (register >= 0xA0 && register <= 0xBF)
                WriteChannelLevelRegister(register, value, Group2BaseChannel);
        }

        private void WriteOperatorLevelRegister(byte register, byte value, int baseChannelIdx)
        {
            int channelOffset = register & 0x03;
            if (channelOffset == 3)
                return;

            int channelIdx = baseChannelIdx + channelOffset;
            int operatorIdx = ((register & 0x08) >> 3) | ((register & 0x04) >> 1);

            var op = _channels[channelIdx].Operators[operatorIdx];
            switch (register >> 4)
            {
                case 0x03:
                    op.Phase.Multiple = (byte)(value & 0x0F);
                    op.Phase.Detune = (byte)((value >> 4) & 0x07);
                    break;
                case 0x04:
                    op.Envelope.TotalLevel = (byte)(value & 0x7F);
                    break;
                case 0x05:
                    op.Envelope.AttackRate = (byte)(value & 0x1F);
                    op.UpdateKeyScale((byte)(value >> 6));
                    break;
                case 0x06:
                    op.Envelope.DecayRate = (byte)(value & 0x1F);
                    op.AmEnabled = (value & 0x80) != 0;
                    break;
                case 0x07:
                    op.Envelope.SustainRate = (byte)(value & 0x1F);
                    break;
                case 0x08:
                    op.Envelope.ReleaseRate = (byte)(value & 0x0F);
                    op.Envelope.SustainLevel = (byte)(value >> 4);
                    break;
                case 0x09:
                    op.Envelope.WriteSsgRegister(value);
                    break;
            }
        }

        private void WriteChannelLevelRegister(byte register, byte value, int baseChannelIdx)
        {
            switch (register)
            {
                case 0xA0:
                case 0xA1:
                case 0xA2:
                {
                    int channelIdx = baseChannelIdx + (register & 0x03);
                    var channel = _channels[channelIdx];
                    channel.ChannelFNumber = (ushort)(value | (channel.PendingChFNumberHigh << 8));
                    channel.ChannelBlock = channel.PendingChBlock;
                    channel.UpdatePhaseGenerators();
                    break;
                }
                case 0xA4:
                case 0xA5:
                case 0xA6:
                {
                    int channelIdx = baseChannelIdx + (register & 0x03);
                    var channel = _channels[channelIdx];
                    channel.PendingChFNumberHigh = (byte)(value & 7);
                    channel.PendingChBlock = (byte)((value >> 3) & 7);
                    break;
                }
                case 0xA8:
                case 0xA9:
                case 0xAA:
                {
                    int channelIdx = baseChannelIdx + 2;
                    int operatorIdx = register switch
                    {
                        0xA8 => 2,
                        0xA9 => 0,
                        _ => 1,
                    };
                    var channel = _channels[channelIdx];
                    byte fNumHigh = channel.PendingOpFNumbersHigh[operatorIdx];
                    channel.OperatorFNumbers[operatorIdx] = (ushort)(value | (fNumHigh << 8));
                    channel.OperatorBlocks[operatorIdx] = channel.PendingOpBlocks[operatorIdx];
                    if (channel.Mode == FrequencyMode.Multiple)
                        channel.UpdatePhaseGenerators();
                    break;
                }
                case 0xAC:
                case 0xAD:
                case 0xAE:
                {
                    int channelIdx = baseChannelIdx + 2;
                    int operatorIdx = register switch
                    {
                        0xAC => 2,
                        0xAD => 0,
                        _ => 1,
                    };
                    var channel = _channels[channelIdx];
                    channel.PendingOpFNumbersHigh[operatorIdx] = (byte)(value & 7);
                    channel.PendingOpBlocks[operatorIdx] = (byte)((value >> 3) & 7);
                    break;
                }
                case 0xB0:
                case 0xB1:
                case 0xB2:
                {
                    int channelIdx = baseChannelIdx + (register & 0x03);
                    var channel = _channels[channelIdx];
                    channel.Algorithm = (byte)(value & 0x07);
                    channel.FeedbackLevel = (byte)((value >> 3) & 0x07);
                    break;
                }
                case 0xB4:
                case 0xB5:
                case 0xB6:
                {
                    int channelIdx = baseChannelIdx + (register & 0x03);
                    var channel = _channels[channelIdx];
                    channel.LOutput = (value & 0x80) != 0;
                    channel.ROutput = (value & 0x40) != 0;
                    channel.AmSensitivity = (byte)((value >> 4) & 0x03);
                    channel.FmSensitivity = (byte)(value & 0x07);
                    break;
                }
            }
        }

        public byte ReadRegister(ushort address)
        {
            if (_busyBehavior == Opn2BusyBehavior.Ym2612 && (address & 3) != 0)
            {
                return _statusDecaySamplesRemaining != 0 ? _lastStatusRead : (byte)0;
            }

            bool busyFlag = _busyBehavior switch
            {
                Opn2BusyBehavior.AlwaysZero => false,
                Opn2BusyBehavior.Ym2612 => _busyCyclesRemaining != 0,
                Opn2BusyBehavior.Ym3438 => _busyCyclesRemaining != 0,
                _ => false,
            };

            byte status = (byte)((busyFlag ? 0x80 : 0x00)
                                | (_timerB.OverflowFlag ? 0x02 : 0x00)
                                | (_timerA.OverflowFlag ? 0x01 : 0x00));

            _statusDecaySamplesRemaining = 12000;
            _lastStatusRead = status;
            return status;
        }

        public void Tick(int ticks, Action<double, double>? output)
        {
            for (int i = 0; i < ticks; i++)
            {
                if (_busyCyclesRemaining > 0)
                    _busyCyclesRemaining--;

                _sampleDivider--;
                if (_sampleDivider == 0)
                {
                    _sampleDivider = FmSampleDivider;
                    if (_statusDecaySamplesRemaining > 0)
                        _statusDecaySamplesRemaining--;

                    _lfo.Tick();
                    _timerB.Tick();
                    TimerTickEffect timerAEffect = _timerA.Tick();

                    if (_csmEnabled && timerAEffect == TimerTickEffect.Overflowed)
                    {
                        var channel = _channels[2];
                        foreach (var op in channel.Operators)
                        {
                            if (!op.Envelope.IsKeyOn)
                            {
                                op.KeyOnOrOff(true);
                                op.KeyOnOrOff(false);
                            }
                        }
                    }

                    Clock();
                    if (output != null)
                    {
                        var (l, r) = Sample();
                        output(l, r);
                    }
                }
            }
        }

        private void Clock()
        {
            byte lfoCounter = _lfo.Counter;
            int quantizationMask = _quantizeOutput ? ~((1 << 5) - 1) : -1;
            foreach (var channel in _channels)
                channel.Clock(lfoCounter, quantizationMask);
        }

        private (double left, double right) Sample()
        {
            int sumL = 0;
            int sumR = 0;
            for (int i = 0; i < _channels.Length; i++)
            {
                if (_channelsMuted[i])
                    continue;

                int sample = (i == 5 && _dacChannelEnabled)
                    ? ((short)(_dacChannelSample - 128) << 6)
                    : _channels[i].CurrentOutput;

                int sampleL = ApplyPanning(sample, _channels[i].LOutput);
                int sampleR = ApplyPanning(sample, _channels[i].ROutput);
                sumL += sampleL;
                sumR += sampleR;
            }

            return (sumL / 49152.0, sumR / 49152.0);
        }

        private int ApplyPanning(int sample, bool panEnabled)
        {
            int enabled = panEnabled ? 1 : 0;
            if (!_emulateLadderEffect)
                return sample * enabled;

            int adjustment = sample >= 0 ? 4 : -(4 - enabled);
            return sample * enabled + (adjustment << 5);
        }

        public bool DacEnabled => _dacChannelEnabled;
        public byte DacSample => _dacChannelSample;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ComputeKeyCode(ushort fNumber, byte block)
        {
            bool f11 = (fNumber & 0x400) != 0;
            bool f10 = (fNumber & 0x200) != 0;
            bool f9 = (fNumber & 0x100) != 0;
            bool f8 = (fNumber & 0x080) != 0;

            int bit0 = (f11 && (f10 || f9 || f8)) || (!f11 && f10 && f9 && f8) ? 1 : 0;
            return (byte)((block << 2) | (f11 ? 0x02 : 0x00) | bit0);
        }

        private static readonly ushort[] LogSineTable = BuildLogSineTable();
        private static readonly ushort[] Pow2Table = BuildPow2Table();

        public static ushort PhaseToAttenuation(ushort phase)
        {
            return LogSineTable[phase & (PhaseMask >> 1)];
        }

        public static ushort AttenuationToAmplitude(ushort attenuation)
        {
            int intPart = (attenuation >> 8) & 0x1F;
            if (intPart >= 13)
                return 0;

            int fractPart = attenuation & 0xFF;
            ushort fractPow2 = Pow2Table[fractPart];
            return (ushort)(((fractPow2 << 2) & 0xFFFF) >> intPart);
        }

        private static ushort[] BuildLogSineTable()
        {
            ushort[] table = new ushort[512];
            for (int i = 0; i < table.Length; i++)
            {
                int idx = i;
                if ((idx & 0x100) != 0)
                    idx = (~idx) & 0xFF;

                double n = ((idx << 1) | 1) / 512.0;
                double sine = Math.Sin(n * Math.PI / 2.0);
                double attenuation = -Math.Log(sine, 2.0);
                table[i] = (ushort)Math.Round(attenuation * (1 << 8));
            }
            return table;
        }

        private static ushort[] BuildPow2Table()
        {
            ushort[] table = new ushort[256];
            for (int i = 0; i < table.Length; i++)
            {
                double n = (i + 1) / 256.0;
                double inverse = Math.Pow(2.0, -n);
                table[i] = (ushort)Math.Round(inverse * (1 << 11));
            }
            return table;
        }
    }
}
