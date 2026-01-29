using System;
using System.Diagnostics;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_ym2612
    {
        private static readonly bool TraceDac =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DAC"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmReg =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YMREG"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmIrq =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YMIRQ"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmTimer =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMER"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmKey =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_KEY"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmAttack =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_ATTACK"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmWriteStats =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_WRITE_STATS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmWriteRing =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_WRITE_RING"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmMuteEvent =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_MUTE_EVENT"), "1", StringComparison.Ordinal);
        private static readonly bool EmulateYmBusy =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_EMULATE_YM_BUSY"), "1", StringComparison.Ordinal);
        
        // Track if Z80 safe boot is complete
        private static bool _z80SafeBootComplete = false;
        private static readonly bool Z80SafeBootEnabled =
            ReadEnvDefaultOff("EUTHERDRIVE_Z80_SAFE_BOOT");
        private static long _safeBootCompleteFrame = 0;
        // Short warmup period after safe boot (10ms ≈ 80,000 M68K cycles at 8MHz)
        private static long _ymBusyEnableAtCycle = 0;
        private const long WarmupCycles10ms = 80000; // 10ms at 8MHz
        private static readonly bool TraceYmBusy =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_BUSY"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmStatus =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_STATUS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmDacBank =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_DAC_BANK"), "1", StringComparison.Ordinal);
        private static readonly bool TraceAudStat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDSTAT"), "1", StringComparison.Ordinal);
        private static readonly bool DacInputSigned =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DAC_INPUT_SIGNED"), "1", StringComparison.Ordinal);
        private static readonly bool TraceDacSample =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DAC_SAMPLE"), "1", StringComparison.Ordinal);
        private static readonly string? TraceDacRateRaw =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DACRATE");
        private static readonly bool TraceDacRate =
            string.Equals(TraceDacRateRaw?.Trim(), "1", StringComparison.Ordinal);
        private static readonly int TraceYmDacBankLimit = ParseYmDacBankLimit();
        private static readonly int Key28LogLimit = ParseKey28LogLimit();
        private static readonly int TraceYmBusyLimit = ParseYmBusyLimit();
        private static readonly int TraceYmStatusLimit = ParseYmStatusLimit();
        private static readonly int TraceYmTimerLimit = ParseYmTimerLimit();
        private static readonly int TraceYmKeyLimit = ParseYmKeyLimit();
        private static readonly int TraceYmWriteStatsLimit = ParseYmWriteStatsLimit();
        private static readonly int YmBusyZ80Cycles = ParseYmBusyZ80Cycles();
        private static readonly int YmWriteRingSize = ParseYmWriteRingSize();
        
        // Track previous busy state for transition detection
        private static bool _previousBusy = false;

        private static bool ReadEnvDefaultOff(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return false;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private long _dacWriteCount;
        private long _dacEnableCount;
        private long _dacDisableCount;

        private long _dacSum;
        private long _dacRateWriteCount;
        private long _dacRateDeltaCount;
        private long _dacRateDeltaTotal;
        private long _dacRateDeltaMin = long.MaxValue;
        private long _dacRateDeltaMax;
        private long _dacRateLastCycle;
        private bool _dacRateHasLast;
        private bool _dacRateBannerLogged;
        private byte _dacLastValue;
        private bool _dacEnabled;
        private long _dacLastLogTicks;
        private byte _dacMinValue = 0xFF;
        private byte _dacMaxValue;
        private long _dacCenterCount;
        private long _dacNonCenterCount;
        private readonly int[] _dacHistogram = new int[16];
        private int _ymRegLogRemaining = 256;
        private int _key28LogRemaining = Key28LogLimit;
        private int _ymDacBankLogRemaining = TraceYmDacBankLimit;
        private int _ymBusyLogRemaining = TraceYmBusyLimit;
        private int _ymStatusLogRemaining = TraceYmStatusLimit;
        private int _ymTimerLogRemaining = TraceYmTimerLimit;
        private int _ymKeyLogRemaining = TraceYmKeyLimit;
        private int _ymAttackLogRemaining = 256;
        private int _ymWriteStatLogRemaining = TraceYmWriteStatsLimit;

        private readonly byte[] _ymWriteRingAddr = new byte[YmWriteRingSize];
        private readonly byte[] _ymWriteRingVal = new byte[YmWriteRingSize];
        private readonly byte[] _ymWriteRingPort = new byte[YmWriteRingSize];
        private readonly string?[] _ymWriteRingSrc = new string?[YmWriteRingSize];
        private readonly ushort[] _ymWriteRingPc = new ushort[YmWriteRingSize];
        private readonly long[] _ymWriteRingCycle = new long[YmWriteRingSize];
        private int _ymWriteRingHead;
        private int _ymWriteRingCount;
        private int _dacDebugWriteCount = 0;
        private int _timerAEvents;
        private int _timerBEvents;
        private int _keyOnSlots;
        private int _keyOffSlots;
        private int _keyWrites;
        private int _ymAddrWrites;
        private int _ymDataWrites;
        private byte _ymLastAddr;
        private byte _ymLastVal;
        private string _ymLastSource = "none";
        private long _lastKeyOnCycle = -1;
        private long _ymMuteLastCycle = -1;
        private long _ymBusyUntilCycle;
        private long _ymBusyDropCount;
        private bool _key28KeyOffLogged;
        private bool _ymIrqAsserted;
        private int _audStatKeyOn;
        private int _audStatFnum;
        private int _audStatParam;
        private int _audStatDacCmd;
        private int _audStatDacDat;

        private static int ParseKey28LogLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_KEY28_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseYmDacBankLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_DAC_BANK_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseYmBusyLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_BUSY_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseYmStatusLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_STATUS_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseYmTimerLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMER_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseYmKeyLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_KEY_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseYmWriteStatsLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_WRITE_STATS_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }
        
        private static int ParseYmWriteRingSize()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_WRITE_RING_SIZE");
            if (string.IsNullOrWhiteSpace(raw))
                return 2048;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 2048;
            if (value <= 0)
                return 2048;
            return value < 128 ? 128 : value;
        }

        private static int ParseYmBusyZ80Cycles()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_BUSY_Z80_CYCLES");
            if (string.IsNullOrWhiteSpace(raw))
                return ComputeDefaultYmBusyCycles();
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return ComputeDefaultYmBusyCycles();
            if (value <= 0)
                return ComputeDefaultYmBusyCycles();
            return value;
        }

        private static int ComputeDefaultYmBusyCycles()
        {
            // YM2612 BUSY flag is active for 32 FM cycles
            // FM runs at M68K clock / 7 = 7.67MHz / 7 = 1.095MHz (NTSC)
            // FM_PRESCALER = 6 in clownmdemu, so 32 * 6 = 192 FM cycles
            // 192 FM cycles = 192 * 7 = 1344 M68K cycles (if FM = M68K/7)
            
            // Try different values to find the perfect one
            // Environment variable can override: EUTHERDRIVE_YM_BUSY_M68K_CYCLES=xxx
            string? envValue = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_BUSY_M68K_CYCLES");
            if (!string.IsNullOrWhiteSpace(envValue) && int.TryParse(envValue, out int envCycles) && envCycles > 0)
            {
                return envCycles;
            }
            
            // Default: 1344 M68K cycles (correct YM2612 busy timing)
            // 224 M68K cycles was too short, causing music to play too fast in Aladdin
            // 32 FM cycles * 7 M68K cycles per FM cycle * 6 prescaler = 1344 M68K cycles
            int cycles = 1344; // M68K cycles
            return cycles > 0 ? cycles : 1;
        }

        private bool g_reg_22_lfo_enable;
        private int g_reg_22_lfo_inc;
        private int g_reg_24_timerA;
        private int g_reg_26_timerB;
        private byte g_reg_27_mode;
        private bool g_reg_27_enable_A;
        private bool g_reg_27_enable_B;
        private bool g_reg_27_load_B;
        private bool g_reg_27_load_A;
        private int _timerAReload = 1024;
        private int _timerBReload = 256 << 4;
        private int _timerACount = 1024;
        private int _timerBCount = 256 << 4;
        private double _timerTickFrac;  // Fixed-point accumulator: units = M68K cycles, 72 cycles = 1 timer tick
        private bool _timersDrivenByZ80;
        private long _lastSystemCycles = -1;
        private int _z80CycleAccumulator;  // Accumulate Z80 cycles for batched SyncFM() calls
        private long _lastSyncSystemCycles;  // Last SystemCycles when SyncFM() was called
        private long _operatorLeftoverCycles;  // Leftover M68K cycles for operator timing (clownmdemu-style)
        private int g_reg_2a_dac_data = 0x100;  // Match clownmdemu: 0x100 = silence
        private int g_reg_2b_dac;

        private double[,] g_reg_30_multi = new double[0, 0];
        private int[,] g_reg_30_dt = new int[0, 0];
        private int[,] g_reg_40_tl = new int[0, 0];
        private int[,] g_reg_50_key_scale = new int[0, 0];
        private int[,] g_reg_60_ams_enable = new int[0, 0];
        private int[,] g_reg_80_sl = new int[0, 0];
        private int[,] g_reg_90_ssg = new int[0, 0];
        private int[,] g_reg_a0_fnum = new int[0, 0];
        private int[,] g_reg_a4_fnum = new int[0, 0];
        private int[,] g_reg_a4_block = new int[0, 0];

        private byte[] g_reg_b0_fb = Array.Empty<byte>();
        private int[] g_reg_b0_algo = Array.Empty<int>();
        private bool[] g_reg_b4_l = Array.Empty<bool>();
        private bool[] g_reg_b4_r = Array.Empty<bool>();
        private int[] g_reg_b4_ams = Array.Empty<int>();
        public int[] g_reg_b4_pms = Array.Empty<int>();

        public byte read8(uint in_address)
        {
            return ReadStatus(false);
        }

        public byte ReadStatus(bool clearOnRead)
        {
            // Do not advance YM time here; it is driven by audio rendering only.
            
            byte status = g_com_status;
            if (clearOnRead && (status & 0x03) != 0)
            {
                g_com_status &= 0xFC;
                UpdateYmIrq("statusRead");
            }
            
            // SIMPLE WORKAROUND: If game is stuck, always return not busy
            // This is a temporary fix until we implement proper timing
            // if (EmulateYmBusy)
            // {
            //     // Always return not busy for now
            //     status &= 0x7F;
            //     return status;
            // }
            
            long nowCycle = GetZ80Cycle();
            bool busy = (EmulateYmBusy || TraceYmBusy || TraceYmStatus) && IsYmBusy(nowCycle);
            
            // ALADDIN DEBUG: Track busy clears
            bool wasBusy = _previousBusy;
            if (EmulateYmBusy && busy)
            {
                status |= 0x80;
                if (!wasBusy)
                {
                    // Busy just became set (though SetYmBusy should have tracked this)
                }
            }
            else
            {
                status &= 0x7F; // Clear BUSY flag if not busy or not emulating
                if (wasBusy && !busy && (EmulateYmBusy || TraceYmBusy || TraceYmStatus))
                {
                    // Busy just cleared (transition from busy to not busy)
                    md_main.IncrementYmBusyClear();
                }
            }
            
            // Update previous busy state
            _previousBusy = busy;
            
            // DEBUG: Log status reads for Sonic 2 debugging when at driver entry
            if (md_main.g_md_z80?.DebugPc == 0x0167)
            {
                Console.WriteLine($"[SONIC2-YM-STATUS] pc=0x{md_main.g_md_z80.DebugPc:X4} status=0x{status:X2} busy={(busy ? 1 : 0)} EmulateYmBusy={EmulateYmBusy}");
            }
            
            // Enhanced diagnostic logging for busy counter debugging
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            if (TraceYmBusy && busy)
            {
                Console.WriteLine($"[YM-BUSY-DIAG] pc=0x{pc:X4} status=0x{status:X2} busy={busy}");
                Console.WriteLine($"[YM-BUSY-DIAG]   nowCycle={nowCycle} _ymBusyUntilCycle={_ymBusyUntilCycle}");
                Console.WriteLine($"[YM-BUSY-DIAG]   busy={busy} (nowCycle < _ymBusyUntilCycle: {nowCycle < _ymBusyUntilCycle})");
            }
            
            // Special logging for Aladdin's wait loop addresses
            if (pc == 0x0DC2 || pc == 0x0DF7 || pc == 0x0DFB || pc == 0x0DB5 || pc == 0x0DB7 || pc == 0x0DC7 || pc == 0x0DC9 || pc == 0x0DD9 || pc == 0x0DDB || pc == 0x0DEB || pc == 0x0DED || pc == 0x0DFD)
            {
                Console.WriteLine($"[ALADDIN-YM-STATUS] pc=0x{pc:X4} status=0x{status:X2} busy={(busy ? 1 : 0)} nowCycle={nowCycle} busyUntil={_ymBusyUntilCycle} diff={_ymBusyUntilCycle - nowCycle}");
            }
            
            if (TraceYmStatus)
                LogYmStatusRead(nowCycle, status, clearOnRead, busy);
            if (TraceYmBusy && busy)
                LogYmBusyStatus(nowCycle, status);
            return status;
        }

        public void write8(uint in_address, byte in_val, string source = "Z80")
        {
            int w_mode = -1;
            byte w_addr = 0;
            string _source = source;  // Store source for MaybeLogYmReg
            
            // Do not advance YM time here; it is driven by audio rendering only.
            
            if (TraceYmBusy)
            {
                Console.WriteLine($"[YM-BUSY-SOURCE] write8 called with source={source} addr=0x{in_address:X6} val=0x{in_val:X2}");
            }

            in_address &= 0x0003;

            switch (in_address & 0x00000f)
            {
                case 0:
                {
                    g_reg_addr1 = in_val;
                    _ymAddrWrites++;
                    if (TraceAudStat && in_val == 0x2A)
                        _audStatDacCmd++;
                    if (TraceYmReg && in_val == 0x28 && _key28LogRemaining > 0)
                    {
                        _key28LogRemaining--;
                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} sel=1 val=0x{in_val:X2}");
                    }
                    break;
                }

                case 1:
                {
                    w_mode = 0;
                    w_addr = g_reg_addr1;
                    break;
                }

                case 2:
                {
                    g_reg_addr2 = in_val;
                    _ymAddrWrites++;
                    if (TraceAudStat && in_val == 0x2A)
                        _audStatDacCmd++;
                    if (TraceYmReg && in_val == 0x28 && _key28LogRemaining > 0)
                    {
                        _key28LogRemaining--;
                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} sel=2 val=0x{in_val:X2}");
                    }
                    break;
                }

                case 3:
                {
                    w_mode = 1;
                    w_addr = g_reg_addr2;
                    break;
                }
            }

            if (w_mode == -1)
                return;

             _ymDataWrites++;
            _ymLastAddr = w_addr;
            _ymLastVal = in_val;
            _ymLastSource = source;

            long nowCycle = GetZ80Cycle();
            
            if (TraceYmWriteRing)
            {
                RecordYmWrite(w_mode, w_addr, in_val, source, nowCycle);
            }
             
             // Log YM write timing for debugging elastic music
             if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_WRITE_TIMING") == "1")
             {
                 Console.WriteLine($"[YM-WRITE-TIMING] source={source} addr=0x{w_addr:X2} val=0x{in_val:X2} SystemCycles={md_main.SystemCycles} Z80Cycle={nowCycle}");
             }
            if (TraceYmBusy && nowCycle < 0)
            {
                Console.WriteLine($"[YM-BUSY-CYCLE] nowCycle={nowCycle} (negative!)");
            }
            bool busy = (EmulateYmBusy || TraceYmBusy) && nowCycle >= 0 && IsYmBusy(nowCycle);

            // Don't drop writes when YM is busy - just process them and extend the busy timer
            // This is more accurate than dropping writes which can break audio
            if (busy && TraceYmBusy)
            {
                LogYmBusyWrite("busy", nowCycle, w_mode, w_addr, in_val, -1);
            }

            // Always update the busy timer (extends it if already busy)
            // BUT: Don't set busy timer for M68K writes during Z80 safe boot
            // M68K may initialize YM2612 before Z80 starts, and we don't want
            // Z80 to see busy flags from M68K's initialization
            bool shouldSetBusy = (EmulateYmBusy || TraceYmBusy || TraceYmStatus) && nowCycle >= 0;
            if (shouldSetBusy)
            {
                // Only set busy timer if:
                // 1. Z80 safe boot is complete, OR
                // 2. This is a Z80 write (not M68K), OR  
                // 3. We're not in Z80 safe boot at all
                if (_z80SafeBootComplete || source == "Z80" || !Z80SafeBootEnabled)
                {
                    if (TraceYmBusy && !_z80SafeBootComplete && source == "M68K")
                    {
                        Console.WriteLine($"[YM-BUSY-WRITE8] Setting busy timer for M68K write during Z80 safe boot: _z80SafeBootComplete={_z80SafeBootComplete} source={source} Z80SafeBootEnabled={Z80SafeBootEnabled}");
                    }
                    SetYmBusy(nowCycle, w_mode, w_addr, in_val, source);
                }
                else if (TraceYmBusy)
                {
                    Console.WriteLine($"[YM-BUSY] Skipping busy timer for {source} write during Z80 safe boot: port={w_mode} addr=0x{w_addr:X2} val=0x{in_val:X2} _z80SafeBootComplete={_z80SafeBootComplete} Z80SafeBootEnabled={Z80SafeBootEnabled}");
                }
            }

            // skriv alltid till reg-matrisen
            g_reg[w_mode, w_addr] = in_val;
            if (TraceAudStat)
            {
                if (w_mode == 0 && w_addr == 0x28)
                    _audStatKeyOn++;
                if (w_mode == 0 && w_addr == 0x2A)
                    _audStatDacDat++;
                if (w_addr >= 0xA0 && w_addr <= 0xA6)
                    _audStatFnum++;
                if ((w_addr >= 0x30 && w_addr <= 0x9F) || (w_addr >= 0xB0 && w_addr <= 0xB6))
                    _audStatParam++;
            }
            MaybeLogYmReg(w_mode, w_addr, in_val, _source);
            MaybeLogYmMuteEvent(w_mode, w_addr, in_val, nowCycle);

            // ------------------------------------------------------------
            // 0x20..0x2B (mode 0 only)
            // ------------------------------------------------------------
            if ((0x20 <= w_addr) && (w_addr <= 0x2b))
            {
                if (w_mode == 0)
                {
                    switch (w_addr)
                    {
                        case 0x22:
                        {
                            if ((in_val & 0x08) == 0x08)
                            {
                                g_reg_22_lfo_enable = true;
                                int lfoInc = LFO_INC_MAP[in_val & 0x07];
                                int scaled = (int)Math.Round(lfoInc * YmStepScale);
                                g_reg_22_lfo_inc = Math.Max(1, scaled);
                            }
                            else
                            {
                                g_reg_22_lfo_enable = false;
                                g_reg_22_lfo_inc = 0;
                            }
                            break;
                        }

                        case 0x24:
                        {
                            g_reg_24_timerA = (g_reg_24_timerA & 0x003) | (((int)in_val) << 2);

                            int newTimerA = (1024 - g_reg_24_timerA) << 12;
                            if (g_com_timerA != newTimerA)
                            {
                                g_com_timerA_cnt = g_com_timerA = newTimerA;
                            }
                            UpdateTimerA();
                            MaybeLogYmTimerReg(0x24, in_val);
                            break;
                        }

                        case 0x25:
                        {
                            g_reg_24_timerA = (g_reg_24_timerA & 0x3fc) | (in_val & 3);

                            int newTimerA = (1024 - g_reg_24_timerA) << 12;
                            if (g_com_timerA != newTimerA)
                            {
                                g_com_timerA_cnt = g_com_timerA = newTimerA;
                            }
                            UpdateTimerA();
                            MaybeLogYmTimerReg(0x25, in_val);
                            break;
                        }

                        case 0x26:
                        {
                            g_reg_26_timerB = in_val;

                            int newTimerB = (256 - g_reg_26_timerB) << (4 + 12);
                            if (g_com_timerB != newTimerB)
                            {
                                g_com_timerB = newTimerB;
                                g_com_timerB_cnt = g_com_timerB;
                            }
                            UpdateTimerB();
                            MaybeLogYmTimerReg(0x26, in_val);
                            break;
                        }

                        case 0x27:
                        {
                            if (((in_val ^ g_reg_27_mode) & 0x40) != 0)
                            {
                                g_ch_reg_reflesh[2] = true;
                            }

                            if ((in_val & 0x10) != 0)
                                g_com_status &= 0xFE;
                            if ((in_val & 0x20) != 0)
                                g_com_status &= 0xFD;

                            g_reg_27_mode = in_val;
                            g_reg_27_enable_B = (in_val & 0x08) != 0;
                            g_reg_27_enable_A = (in_val & 0x04) != 0;
                            g_reg_27_load_B = (in_val & 0x02) != 0;
                            g_reg_27_load_A = (in_val & 0x01) != 0;
                            if (g_reg_27_load_A)
                                _timerACount = _timerAReload;
                            if (g_reg_27_load_B)
                                _timerBCount = _timerBReload;
                            UpdateYmIrq("reg27");
                            MaybeLogYmTimerReg(0x27, in_val);
                            break;
                        }

                        case 0x28:
                        {
                            if ((in_val & 0x03) != 0x03)
                            {
                                int slotMask = (in_val >> 4) & 0x0F;
                                int ch = (in_val & 0x07);
                                int onCount = CountBits(slotMask);
                                _keyWrites++;
                                _keyOnSlots += onCount;
                                _keyOffSlots += 4 - onCount;
                                MaybeLogYmKey(in_val, ch, slotMask, source);
                                MaybeLogYmAttack(in_val, ch, slotMask, source);
                                if (TraceYmReg && _key28LogRemaining > 0)
                                {
                                    if (slotMask != 0)
                                    {
                                        _key28LogRemaining--;
                                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} val=0x{in_val:X2} ch={(in_val & 0x07)} slotmask=0x{slotMask:X1}");
                                    }
                                    else if (!_key28KeyOffLogged)
                                    {
                                        _key28KeyOffLogged = true;
                                        _key28LogRemaining--;
                                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} val=0x{in_val:X2} ch={(in_val & 0x07)} slotmask=0x0");
                                    }
                                }
                                int w_ch = KEYON_MAP[in_val & 0x07];
                                if (w_ch < 0)
                                    break;

                                if ((in_val & 0x10) != 0) Slot_Key_on(w_ch, 0); else Slot_Key_off(w_ch, 0);
                                if ((in_val & 0x20) != 0) Slot_Key_on(w_ch, 1); else Slot_Key_off(w_ch, 1);
                                if ((in_val & 0x40) != 0) Slot_Key_on(w_ch, 2); else Slot_Key_off(w_ch, 2);
                                if ((in_val & 0x80) != 0) Slot_Key_on(w_ch, 3); else Slot_Key_off(w_ch, 3);
                            }
                            break;
                        }

                        // case 0x29: Not implemented as processing is not required

                         case 0x2A:
                         {
                             // DEBUG: Log ALL DAC writes
                             _dacDebugWriteCount++;
                             if (_dacDebugWriteCount <= 50 || _dacDebugWriteCount % 100 == 0)
                             {
                                 long cycles = md_main.SystemCycles;
                                 int signedVal = (sbyte)in_val;
                                 int unsignedVal = in_val & 0xFF;
                                 int shiftedVal = DacInputSigned ? 
                                     ((int)(sbyte)in_val + 0x80) & 0xFF : 
                                     in_val & 0xFF;
                                 int dac9bit = ((shiftedVal << 1) & 0x1FE) | (g_reg_2a_dac_data & 1);
                                 int finalSigned = dac9bit - 0x100;
                                 
                                 Console.WriteLine($"[DAC-DEBUG {_dacDebugWriteCount}] val=0x{in_val:X2} ({signedVal}) -> shifted=0x{shiftedVal:X2} -> dac9bit=0x{dac9bit:X3} -> final={finalSigned} enabled={_dacEnabled} g_reg_2b_dac=0x{g_reg_2b_dac:X2}");
                             }
                             
                             if (TraceDac)
                             {
                                 _dacWriteCount++;
                                 _dacLastValue = in_val;
                                 _dacSum += in_val;
                                 _dacHistogram[in_val >> 4]++;
                                 if (in_val < _dacMinValue)
                                     _dacMinValue = in_val;
                                 if (in_val > _dacMaxValue)
                                     _dacMaxValue = in_val;
                                 byte centerValue = DacInputSigned ? (byte)0x00 : (byte)0x80;
                                 if (in_val == centerValue)
                                     _dacCenterCount++;
                                 else
                                 _dacNonCenterCount++;
                                 MaybeLogDac();
                             }
                            if (!_dacRateBannerLogged && TraceDacRateRaw != null)
                            {
                                Console.WriteLine($"[DACRATE] env='{TraceDacRateRaw}' enabled={TraceDacRate}");
                                _dacRateBannerLogged = true;
                            }
                            if (TraceDacRate)
                            {
                                _dacRateWriteCount++;
                                long now = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugTotalCycles : -1;
                                if (now >= 0)
                                {
                                    if (_dacRateHasLast)
                                    {
                                        long delta = now - _dacRateLastCycle;
                                        if (delta >= 0)
                                        {
                                            _dacRateDeltaTotal += delta;
                                            _dacRateDeltaCount++;
                                            if (delta < _dacRateDeltaMin)
                                                _dacRateDeltaMin = delta;
                                            if (delta > _dacRateDeltaMax)
                                                _dacRateDeltaMax = delta;
                                        }
                                    }
                                    _dacRateLastCycle = now;
                                    _dacRateHasLast = true;
                                }
                            }
                            if (TraceYmDacBank && _ymDacBankLogRemaining > 0)
                            {
                                ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                                uint bankBase = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugBankBase : 0;
                                uint bankReg = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugBankRegister : 0;
                                ushort lastAddr = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugLastReadAddr : (ushort)0xFFFF;
                                byte lastVal = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugLastReadValue : (byte)0xFF;
                                ushort lastPc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugLastReadPc : (ushort)0xFFFF;
                                bool lastBanked = md_main.g_md_z80 != null && md_main.g_md_z80.DebugLastReadWasBanked;
                                uint lastM68k = lastBanked && md_main.g_md_z80 != null ? md_main.g_md_z80.DebugLastReadM68kAddr : 0;
                                string lastBankInfo = lastBanked ? $" m68k=0x{lastM68k:X6}" : string.Empty;
                                Console.WriteLine(
                                    $"[YMDACBANK] pc=0x{pc:X4} val=0x{in_val:X2} bank=0x{bankBase:X6} reg=0x{bankReg:X3} " +
                                    $"last=0x{lastAddr:X4} lastVal=0x{lastVal:X2} lastPc=0x{lastPc:X4}{lastBankInfo}");
                                if (_ymDacBankLogRemaining != int.MaxValue)
                                    _ymDacBankLogRemaining--;
                            }
                            // Convert 8-bit input to 9-bit unsigned DAC value
                            // Clownmdemu does: dac_sample &= 1; dac_sample |= data << 1;
                            // This maps input 0x00..0xFF to 9-bit values where:
                            // - Input 0x80 (silence in unsigned 8-bit) -> 0x100 (256, signed 0)
                            // - Input 0x00 -> 0x00 (signed -128)
                            // - Input 0xFF -> 0x1FE (510, signed +254)
                            // For signed input (sbyte), we need to convert -128..+127 to 0..255 first
                            int shiftedData;
                            if (DacInputSigned)
                            {
                                // Signed: convert -128..+127 to 0..255, then shift
                                // -128 -> 0, 0 -> 128, +127 -> 255
                                shiftedData = ((int)(sbyte)in_val + 0x80) & 0xFF;
                            }
                            else
                            {
                                // Already unsigned 0..255
                                shiftedData = in_val & 0xFF;
                            }
                            // Shift left by 1 to get 9-bit value, preserve old bit 0 for compatibility
                            g_reg_2a_dac_data = ((shiftedData << 1) & 0x1FE) | (g_reg_2a_dac_data & 1);
                            if (TraceDacSample)
                            {
                                int signedDac = g_reg_2a_dac_data - 0x100;
                                Console.WriteLine($"[YMDAC] val=0x{in_val:X2} shifted=0x{shiftedData:X2} g_reg_2a_dac_data=0x{g_reg_2a_dac_data:X3} signed={signedDac}");
                            }
                            break;
                        }

                         case 0x2B:
                         {
                             bool enabled = (in_val & 0x80) != 0;
                             // DEBUG: Log DAC enable/disable
                             Console.WriteLine($"[DAC-ENABLE] val=0x{in_val:X2} enabled={enabled} (bit7={((in_val & 0x80) != 0)})");
                             
                             if (TraceDac)
                             {
                                 if (enabled)
                                     _dacEnableCount++;
                                 else
                                     _dacDisableCount++;
                                 _dacEnabled = enabled;
                                 MaybeLogDac();
                             }
                             g_reg_2b_dac = in_val & 0x80;
                             break;
                         }
                    }
                }

                return;
            }

            // ------------------------------------------------------------
            // 0x30..0x9E (slot regs) - NOTE: C# switch case scope fix here
            // ------------------------------------------------------------
            if ((0x30 <= w_addr) && (w_addr <= 0x9e) && ((w_addr & 0x03) != 3))
            {
                int w_ch = 0;
                int w_slot = 0;

                switch (w_addr & 0xf0)
                {
                    case 0x30:
                    {
                        w_ch = ((w_addr - 0x30) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x30) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_30_multi[w_ch, w_slot] = MULTIPLE_TABLE[in_val & 0x0F];
                        g_reg_30_dt[w_ch, w_slot] = (in_val >> 4) & 7;
                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x40:
                    {
                        w_ch = ((w_addr - 0x40) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x40) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_40_tl[w_ch, w_slot] = (int)((in_val & 0x7f) << (CNT_HIGH_BIT - 7));
                        break;
                    }

                    case 0x50:
                    {
                        w_ch = ((w_addr - 0x50) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x50) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_50_key_scale[w_ch, w_slot] = 3 - (in_val >> 6);

                        int low5 = in_val & 0x1f;
                        g_slot_env_indexA[w_ch, w_slot] = (low5 != 0) ? (low5 << 1) : 0;
                        g_slot_env_incA[w_ch, w_slot] =
                        (int)ENV_RATE_A_TABLE[g_slot_env_indexA[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x60:
                    {
                        w_ch = ((w_addr - 0x60) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x60) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        if ((g_reg_60_ams_enable[w_ch, w_slot] = (in_val & 0x80)) != 0)
                            g_slot_ams[w_ch, w_slot] = g_reg_b4_ams[w_ch];
                        else
                            g_slot_ams[w_ch, w_slot] = 31;

                        int low5 = in_val & 0x1f;
                        g_slot_env_indexD[w_ch, w_slot] = (low5 != 0) ? (low5 << 1) : 0;
                        g_slot_env_incD[w_ch, w_slot] =
                        (int)ENV_RATE_D_TABLE[g_slot_env_indexD[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x70:
                    {
                        w_ch = ((w_addr - 0x70) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x70) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        int low5 = in_val & 0x1f;
                        g_slot_env_indexS[w_ch, w_slot] = (low5 != 0) ? (low5 << 1) : 0;
                        g_slot_env_incS[w_ch, w_slot] =
                        (int)ENV_RATE_D_TABLE[g_slot_env_indexS[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x80:
                    {
                        w_ch = ((w_addr - 0x80) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x80) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_80_sl[w_ch, w_slot] = (int)SL_TABLE[in_val >> 4];
                        g_slot_env_indexR[w_ch, w_slot] = ((in_val & 0xF) << 2) + 2;
                        g_slot_env_incR[w_ch, w_slot] =
                        (int)ENV_RATE_D_TABLE[g_slot_env_indexR[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x90:
                    {
                        // ssg_eg no support
                        w_ch = ((w_addr - 0x90) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x90) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        if ((in_val & 0x08) != 0)
                            g_reg_90_ssg[w_ch, w_slot] = in_val & 0x0F;
                        else
                            g_reg_90_ssg[w_ch, w_slot] = 0;

                        break;
                    }
                }

                return;
            }

            // ------------------------------------------------------------
            // 0xA0..0xB6 (fnum/block/algo/pan)
            // ------------------------------------------------------------
            if ((0xa0 <= w_addr) && (w_addr <= 0xb6) && ((w_addr & 0x03) != 3))
            {
                int w_ch = 0;
                int w_slot = 0;
                int wfnum = 0;

                switch (w_addr & 0xfc)
                {
                    case 0xa0:
                    {
                        w_ch = (w_addr - 0xa0) + (w_mode * 3);
                        wfnum = (g_slot_fnum[w_ch, 0] & 0x700) + in_val;
                        g_slot_fnum[w_ch, 0] = wfnum;
                        g_slot_keycode[w_ch, 0] = (int)(((uint)g_reg_a4_block[w_ch, 0] << 2) | KEYCODE_TABLE[g_slot_fnum[w_ch, 0] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        md_main.g_md_music.g_freq_out[w_ch] = (int)((wfnum << (g_reg_a4_block[w_ch, 0] - 1)) * 0.0529819f);
                        break;
                    }

                    case 0xa4:
                    {
                        w_ch = (w_addr - 0xa4) + (w_mode * 3);
                        wfnum = (g_slot_fnum[w_ch, 0] & 0x0FF) + ((int)(in_val & 0x07) << 8);
                        g_slot_fnum[w_ch, 0] = wfnum;
                        g_reg_a4_block[w_ch, 0] = (in_val & 0x38) >> 3;
                        g_slot_keycode[w_ch, 0] = (int)(((uint)g_reg_a4_block[w_ch, 0] << 2) | KEYCODE_TABLE[g_slot_fnum[w_ch, 0] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        md_main.g_md_music.g_freq_out[w_ch] = (int)((wfnum << (g_reg_a4_block[w_ch, 0] - 1)) * 0.0529819f);
                        break;
                    }

                    case 0xa8:
                    {
                        w_slot = ((w_addr - 0xa8) & 0x03) + 1;
                        g_slot_fnum[2, w_slot] = (g_slot_fnum[2, w_slot] & 0x700) + in_val;
                        g_slot_keycode[2, w_slot] = (int)(((uint)g_reg_a4_block[2, w_slot] << 2) |
                        KEYCODE_TABLE[g_slot_fnum[2, w_slot] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0xac:
                    {
                        w_slot = ((w_addr - 0xac) & 0x03) + 1;
                        g_slot_fnum[2, w_slot] = (g_slot_fnum[2, w_slot] & 0x0FF) +
                        ((int)(in_val & 0x07) << 8);
                        g_reg_a4_block[2, w_slot] = (in_val & 0x38) >> 3;
                        g_slot_keycode[2, w_slot] = (int)(((uint)g_reg_a4_block[2, w_slot] << 2) |
                        KEYCODE_TABLE[g_slot_fnum[2, w_slot] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0xb0:
                    {
                        w_ch = (w_addr - 0xb0) + (w_mode * 3);

                        g_reg_b0_fb[w_ch] = (byte)(9 - ((in_val >> 3) & 0x07));
                        if (g_reg_b0_algo[w_ch] != (in_val & 0x07))
                        {
                            g_reg_b0_algo[w_ch] = in_val & 0x07;
                            g_slot_CNT_MASK[w_ch, 0] = false;
                            g_slot_CNT_MASK[w_ch, 1] = false;
                            g_slot_CNT_MASK[w_ch, 2] = false;
                            g_slot_CNT_MASK[w_ch, 3] = false;
                        }
                        break;
                    }

                    case 0xb4:
                    {
                        w_ch = (w_addr - 0xb4) + (w_mode * 3);

                        g_reg_b4_l[w_ch] = (in_val & 0x80) != 0;
                        g_reg_b4_r[w_ch] = (in_val & 0x40) != 0;
                        g_reg_b4_ams[w_ch] = (int)LFO_AMS_MAP[(in_val >> 4) & 3];
                        g_reg_b4_pms[w_ch] = (int)LFO_PMS_MAP[in_val & 7];

                        if (g_reg_60_ams_enable[w_ch, 0] != 0) g_slot_ams[w_ch, 0] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 0] = 31;
                        if (g_reg_60_ams_enable[w_ch, 1] != 0) g_slot_ams[w_ch, 1] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 1] = 31;
                        if (g_reg_60_ams_enable[w_ch, 2] != 0) g_slot_ams[w_ch, 2] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 2] = 31;
                        if (g_reg_60_ams_enable[w_ch, 3] != 0) g_slot_ams[w_ch, 3] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 3] = 31;

                        break;
                    }
                }

                return;
            }
        }

        private void RecordYmWrite(int port, byte addr, byte val, string source, long cycle)
        {
            int idx = _ymWriteRingHead;
            _ymWriteRingAddr[idx] = addr;
            _ymWriteRingVal[idx] = val;
            _ymWriteRingPort[idx] = (byte)port;
            _ymWriteRingSrc[idx] = source;
            _ymWriteRingPc[idx] = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            _ymWriteRingCycle[idx] = cycle;
            _ymWriteRingHead = (idx + 1) % YmWriteRingSize;
            if (_ymWriteRingCount < YmWriteRingSize)
                _ymWriteRingCount++;
        }

        public void DumpRecentYmWrites(string reason, int maxCount)
        {
            if (!TraceYmWriteRing || maxCount <= 0 || _ymWriteRingCount == 0)
                return;

            int count = maxCount > _ymWriteRingCount ? _ymWriteRingCount : maxCount;
            Console.WriteLine($"[YMWRITE-RING] reason={reason} count={count} size={YmWriteRingSize}");
            int start = _ymWriteRingHead - count;
            if (start < 0)
                start += YmWriteRingSize;
            for (int i = 0; i < count; i++)
            {
                int idx = start + i;
                if (idx >= YmWriteRingSize)
                    idx -= YmWriteRingSize;
                Console.WriteLine(
                    $"[YMWRITE-RING] idx={i} port={_ymWriteRingPort[idx]} addr=0x{_ymWriteRingAddr[idx]:X2} " +
                    $"val=0x{_ymWriteRingVal[idx]:X2} src={_ymWriteRingSrc[idx]} pc=0x{_ymWriteRingPc[idx]:X4} " +
                    $"cycle={_ymWriteRingCycle[idx]}");
            }
        }

        private void UpdateTimerA()
        {
            int reload = 1024 - g_reg_24_timerA;
            if (reload <= 0)
                reload = 1024;
            _timerAReload = reload;
            if (g_reg_27_load_A)
                _timerACount = _timerAReload;
        }

        private void UpdateTimerB()
        {
            int reload = (256 - g_reg_26_timerB) << 4;
            if (reload <= 0)
                reload = 256 << 4;
            _timerBReload = reload;
            if (g_reg_27_load_B)
                _timerBCount = _timerBReload;
        }

        private void MaybeLogDac()
        {
            long now = Stopwatch.GetTimestamp();
            if (_dacLastLogTicks == 0)
            {
                _dacLastLogTicks = now;
                return;
            }

            if (now - _dacLastLogTicks < Stopwatch.Frequency)
                return;

            _dacLastLogTicks = now;
            long writes = _dacWriteCount;
            long enables = _dacEnableCount;
            long disables = _dacDisableCount;
            byte minVal = _dacMinValue == 0xFF ? _dacLastValue : _dacMinValue;
            byte maxVal = _dacMaxValue;
            long center = _dacCenterCount;
            long nonCenter = _dacNonCenterCount;
            long sum = _dacSum;
            string histText = string.Join(",", _dacHistogram);
            _dacWriteCount = 0;
            _dacEnableCount = 0;
            _dacDisableCount = 0;
            _dacSum = 0;
            _dacMinValue = 0xFF;
            _dacMaxValue = 0x00;
            _dacCenterCount = 0;
            _dacNonCenterCount = 0;
            Array.Clear(_dacHistogram, 0, _dacHistogram.Length);

            byte centerValue = DacInputSigned ? (byte)0x00 : (byte)0x80;
            double mean = writes > 0 ? (double)sum / writes : 0.0;
            int meanRounded = (int)Math.Round(mean);
            string meanText = mean.ToString("0.##", CultureInfo.InvariantCulture);
            string meanHex = meanRounded.ToString("X2", CultureInfo.InvariantCulture);
            Console.WriteLine(
                "[YM-DAC] writes={0} enable={1} disable={2} enabled={3} last=0x{4:X2} min=0x{5:X2} max=0x{6:X2} mean={7} meanHex=0x{8} center=0x{9:X2} nonCenter={10} hist16={11}",
                writes, enables, disables, _dacEnabled ? 1 : 0, _dacLastValue, minVal, maxVal, meanText, meanHex, centerValue, nonCenter, histText);
        }

        internal void FlushDacRateFrame(long frame)
        {
            if (!_dacRateBannerLogged && TraceDacRateRaw != null)
            {
                Console.WriteLine($"[DACRATE] env='{TraceDacRateRaw}' enabled={TraceDacRate}");
                _dacRateBannerLogged = true;
            }

            if (!TraceDacRate)
                return;

            long writes = _dacRateWriteCount;
            long deltaCount = _dacRateDeltaCount;
            long minCycles = deltaCount > 0 ? _dacRateDeltaMin : 0;
            long maxCycles = deltaCount > 0 ? _dacRateDeltaMax : 0;
            double avgCycles = deltaCount > 0 ? (double)_dacRateDeltaTotal / deltaCount : 0.0;
            double rate = avgCycles > 0 ? Z80_CLOCK / avgCycles : 0.0;
            string avgText = avgCycles > 0 ? avgCycles.ToString("0.##", CultureInfo.InvariantCulture) : "0";
            string rateText = rate > 0 ? rate.ToString("0.##", CultureInfo.InvariantCulture) : "0";

            Console.WriteLine(
                $"[DACRATE] frame={frame} writes={writes} avgCycles={avgText} minCycles={minCycles} maxCycles={maxCycles} estHz={rateText}");

            _dacRateWriteCount = 0;
            _dacRateDeltaCount = 0;
            _dacRateDeltaTotal = 0;
            _dacRateDeltaMin = long.MaxValue;
            _dacRateDeltaMax = 0;
            _dacRateHasLast = false;
        }

        internal static bool AudStatEnabled => TraceAudStat;

        internal void ConsumeAudStatCounters(
            out int keyOn, out int fnum, out int param, out int dacCmd, out int dacDat)
        {
            if (!TraceAudStat)
            {
                keyOn = 0;
                fnum = 0;
                param = 0;
                dacCmd = 0;
                dacDat = 0;
                return;
            }

            keyOn = _audStatKeyOn;
            fnum = _audStatFnum;
            param = _audStatParam;
            dacCmd = _audStatDacCmd;
            dacDat = _audStatDacDat;

            _audStatKeyOn = 0;
            _audStatFnum = 0;
            _audStatParam = 0;
            _audStatDacCmd = 0;
            _audStatDacDat = 0;
        }

        private void MaybeLogYmReg(int port, byte addr, byte val, string source)
        {
            if (!TraceYmReg || _ymRegLogRemaining <= 0)
                return;
            _ymRegLogRemaining--;
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            Console.WriteLine($"[YMREG] {source} pc=0x{pc:X4} port={port} addr=0x{addr:X2} val=0x{val:X2}");
        }

        private void MaybeLogYmMuteEvent(int port, byte addr, byte val, long nowCycle)
        {
            if (!TraceYmMuteEvent)
                return;
            if (addr < 0x40 || addr > 0x4F || (addr & 0x03) == 0x03)
                return;
            if ((val & 0x7F) != 0x7F)
                return;
            if (!IsAllTlMuted())
                return;

            if (nowCycle >= 0 && _ymMuteLastCycle >= 0)
            {
                long minDelta = (long)Z80_CLOCK;
                if (nowCycle - _ymMuteLastCycle < minDelta)
                    return;
            }

            _ymMuteLastCycle = nowCycle;
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            string mbx = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxDump() : string.Empty;
            string lastMbx = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastNonZeroDump() : string.Empty;
            long lastMbxFrame = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastNonZeroFrame() : -1;
            ushort lastAddr = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastWriteAddr() : (ushort)0;
            byte lastVal = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastWriteVal() : (byte)0;
            long lastFrame = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastWriteFrame() : -1;
            ushort lastNzAddr = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastNonZeroAddr() : (ushort)0;
            byte lastNzVal = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastNonZeroVal() : (byte)0;
            long lastNzFrame = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxLastNonZeroWriteFrame() : -1;
            Console.WriteLine(
                $"[YM-MUTE] pc=0x{pc:X4} port={port} addr=0x{addr:X2} val=0x{val:X2} " +
                $"cycle={nowCycle} mailbox={mbx} lastMbx={lastMbx} lastMbxFrame={lastMbxFrame} " +
                $"lastWrite=0x{lastAddr:X4}:0x{lastVal:X2}@{lastFrame} lastNz=0x{lastNzAddr:X4}:0x{lastNzVal:X2}@{lastNzFrame}");
        }

        private bool IsAllTlMuted()
        {
            for (int port = 0; port < 2; port++)
            {
                for (int addr = 0x40; addr <= 0x4E; addr++)
                {
                    if ((addr & 0x03) == 0x03)
                        continue;
                    if (g_reg[port, addr] != 0x7F)
                        return false;
                }
            }
            return true;
        }

        private void MaybeLogYmTimerReg(byte reg, byte val)
        {
            if (!TraceYmTimer || _ymTimerLogRemaining <= 0)
                return;
            if (_ymTimerLogRemaining != int.MaxValue)
                _ymTimerLogRemaining--;
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            Console.WriteLine(
                $"[YMTIMER-REG] pc=0x{pc:X4} reg=0x{reg:X2} val=0x{val:X2} " +
                $"A_reload={_timerAReload} B_reload={_timerBReload} enA={(g_reg_27_enable_A ? 1 : 0)} enB={(g_reg_27_enable_B ? 1 : 0)} " +
                $"mode=0x{g_reg_27_mode:X2}");
        }

        private void MaybeLogYmKey(byte inVal, int channel, int slotMask, string source)
        {
            if (!TraceYmKey || _ymKeyLogRemaining <= 0)
                return;
            if (_ymKeyLogRemaining != int.MaxValue)
                _ymKeyLogRemaining--;
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            Console.WriteLine(
                $"[YMKEY] src={source} pc=0x{pc:X4} val=0x{inVal:X2} ch={channel} slotmask=0x{slotMask:X1}");
        }

        private void MaybeLogYmAttack(byte inVal, int channel, int slotMask, string source)
        {
            if (!TraceYmAttack || _ymAttackLogRemaining <= 0)
                return;
            if (slotMask == 0)
                return;
            if (_ymAttackLogRemaining != int.MaxValue)
                _ymAttackLogRemaining--;
            long nowCycle = md_main.SystemCycles;
            _lastKeyOnCycle = nowCycle;
            int ar0 = GetOpRate(channel, 0, 0x50);
            int ar1 = GetOpRate(channel, 1, 0x50);
            int ar2 = GetOpRate(channel, 2, 0x50);
            int ar3 = GetOpRate(channel, 3, 0x50);
            int dr0 = GetOpRate(channel, 0, 0x60);
            int dr1 = GetOpRate(channel, 1, 0x60);
            int dr2 = GetOpRate(channel, 2, 0x60);
            int dr3 = GetOpRate(channel, 3, 0x60);
            int rr0 = GetOpRate(channel, 0, 0x80);
            int rr1 = GetOpRate(channel, 1, 0x80);
            int rr2 = GetOpRate(channel, 2, 0x80);
            int rr3 = GetOpRate(channel, 3, 0x80);
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            Console.WriteLine(
                $"[YM-ATTACK] cyc={nowCycle} pc=0x{pc:X4} src={source} ch={channel} slotmask=0x{slotMask:X1} " +
                $"AR={ar0:X2},{ar1:X2},{ar2:X2},{ar3:X2} " +
                $"DR={dr0:X2},{dr1:X2},{dr2:X2},{dr3:X2} " +
                $"RR={rr0:X2},{rr1:X2},{rr2:X2},{rr3:X2}");
        }

        private int GetOpRate(int channel, int op, int baseAddr)
        {
            int addr = baseAddr + (op * 4) + channel;
            int high = g_reg[0, addr];
            int low = g_reg[1, addr];
            return ((high & 0x1F) << 5) | (low & 0x1F);
        }

        private static int CountBits(int value)
        {
            int count = 0;
            int v = value & 0x0F;
            while (v != 0)
            {
                count += v & 1;
                v >>= 1;
            }
            return count;
        }

        private long GetZ80Cycle()
        {
            // ALADDIN FIX: Use a timebase that always advances
            // SystemCycles freezes when Z80 waits in tight loops and M68K isn't running
            // We need a monotonic clock for YM2612 busy timing
            
            // Use frame-based time: frames * cycles_per_frame + offset
            long frame = md_main.g_md_vdp?.FrameCounter ?? 0;
            long cyclesPerFrame = 53693175 / 60; // ~894,886 M68K cycles per frame (7.67MHz / 60Hz)
            long baseCycles = frame * cyclesPerFrame;
            
            // Add offset within current frame (scanline-based)
            int scanline = md_main.g_md_vdp?.g_scanline ?? 0;
            long cyclesPerScanline = cyclesPerFrame / 262;
            baseCycles += scanline * cyclesPerScanline;
            
            // Add small offset for within-scanline progress
            // This ensures time always moves forward, even when CPUs are waiting
            baseCycles += md_main.SystemCycles % 1000; // Use modulo to avoid huge numbers
            
            return baseCycles;
        }

        private bool IsYmBusyEnabled(long nowCycle)
        {
            if (!Z80SafeBootEnabled)
            {
                return nowCycle >= 0;
            }
            // Don't emulate YM busy during Z80 safe boot
            // Some games get stuck if YM busy is emulated before Z80 is fully initialized
            if (!_z80SafeBootComplete)
            {
                return false;
            }
            
            // Short warmup period after safe boot (10ms)
            // This gives Z80 time to initialize YM2612 without busy timing interference
            if (nowCycle < _ymBusyEnableAtCycle)
            {
                return false;
            }
            
            return nowCycle >= 0;
        }

        private bool IsYmBusy(long nowCycle)
        {
            return IsYmBusyEnabled(nowCycle) && nowCycle < _ymBusyUntilCycle;
        }
        
        public void MarkZ80SafeBootComplete()
        {
            _z80SafeBootComplete = true;
            _safeBootCompleteFrame = md_main.g_md_vdp?.FrameCounter ?? 0;
            // Enable YM busy emulation after short warmup (10ms)
            _ymBusyEnableAtCycle = md_main.SystemCycles + WarmupCycles10ms;
            if (TraceYmBusy)
                Console.WriteLine($"[YM-BUSY] Z80 safe boot complete at frame={_safeBootCompleteFrame}, YM busy emulation will start after {WarmupCycles10ms} cycles (~10ms)");
        }
        
        public void ResetZ80SafeBootState()
        {
            _z80SafeBootComplete = !Z80SafeBootEnabled;
            _safeBootCompleteFrame = 0;
            _ymBusyEnableAtCycle = _z80SafeBootComplete ? md_main.SystemCycles : 0;
            // DON'T reset _ymBusyUntilCycle here! M68K may have already set it
            // _ymBusyUntilCycle = long.MinValue + 1;
            _ymBusyDropCount = 0;
            if (TraceYmBusy)
                Console.WriteLine($"[YM-BUSY] Z80 safe boot state reset - complete={_z80SafeBootComplete} busyUntil={_ymBusyUntilCycle} enableAt={_ymBusyEnableAtCycle}");
        }
        
        public void FullReset()
        {
            ResetZ80SafeBootState();
            // Reset any other YM2612 state that might persist between runs
            _dacWriteCount = 0;
            _dacEnableCount = 0;
            _dacDisableCount = 0;
            _dacSum = 0;
            _dacRateWriteCount = 0;
            _dacRateDeltaCount = 0;
            _dacRateDeltaTotal = 0;
            
            // Reset YM2612 busy state completely
            // Use long.MinValue + 1 to avoid potential underflow issues
            _ymBusyUntilCycle = long.MinValue + 1;
            _previousBusy = false;
            _ymBusyDropCount = 0;
            
            // Reset frame counter for delayed YM busy enable
            _safeBootCompleteFrame = 0;
            _ymBusyEnableAtCycle = 0;
            
            // Force YM2612 to reinitialize completely on next access
            // This ensures all internal state is fresh
            
            if (TraceYmBusy)
                Console.WriteLine($"[YM-BUSY] Full reset completed - all state cleared, busyUntil={_ymBusyUntilCycle}");
        }

         private void SetYmBusy(long nowCycle, int port, byte addr, byte val, string source = "Z80")
        {
            // CRITICAL: Don't set busy timer if YM busy emulation is disabled
            // This includes during Z80 safe boot and warmup period
            if (!EmulateYmBusy)
            {
                // Still log if tracing is enabled
                if (TraceYmBusy)
                    LogYmBusyWrite("set", nowCycle, port, addr, val, -1);
                return;
            }
            
            // Check if YM busy emulation should be active
            // IsYmBusy() returns false during safe boot and warmup
            if (!IsYmBusyEnabled(nowCycle))
            {
                // YM busy emulation is disabled, don't set timer
                // But still log if tracing is enabled
                if (TraceYmBusy)
                    LogYmBusyWrite("set", nowCycle, port, addr, val, -1);
                return;
            }
            
            // Only set busy timer if YM busy emulation is actually enabled
            // and we're past safe boot and warmup period
            // CRITICAL FIX: Don't extend busy timer if already busy
            // YM busy should be fixed duration from first write, not extended on subsequent writes
            if (IsYmBusy(nowCycle))
                return;
            
            long newUntil = nowCycle + YmBusyZ80Cycles;
            
            // ALADDIN DEBUG: Track busy sets
            bool wasBusy = IsYmBusy(nowCycle);
            _ymBusyUntilCycle = newUntil;
            md_main.IncrementYmBusySet();
            
            if (TraceYmBusy)
                LogYmBusyWrite("set", nowCycle, port, addr, val, -1);
        }

        private void LogYmBusyWrite(string kind, long nowCycle, int port, byte addr, byte val, long dropCount)
        {
            if (_ymBusyLogRemaining <= 0)
                return;
            _ymBusyLogRemaining--;
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            string dacTag = (addr == 0x2A || addr == 0x2B) ? " dac=1" : string.Empty;
            string dropTag = dropCount >= 0 ? $" drops={dropCount}" : string.Empty;
            Console.WriteLine(
                $"[YM-BUSY] {kind} pc=0x{pc:X4} port={port} addr=0x{addr:X2} val=0x{val:X2} cycles={nowCycle} until={_ymBusyUntilCycle}{dacTag}{dropTag}");
        }

        private void LogYmBusyStatus(long nowCycle, byte status)
        {
            if (_ymBusyLogRemaining <= 0)
                return;
            _ymBusyLogRemaining--;
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
            Console.WriteLine($"[YM-BUSY] status pc=0x{pc:X4} cycles={nowCycle} until={_ymBusyUntilCycle} status=0x{status:X2}");
        }

        // Using common synchronization system like other emulators
        // No need for separate clock advancement

        private void LogYmStatusRead(long nowCycle, byte status, bool clearOnRead, bool busy)
        {
            if (_ymStatusLogRemaining <= 0)
                return;
            if (_ymStatusLogRemaining != int.MaxValue)
                _ymStatusLogRemaining--;
            ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.CpuPc : (ushort)0xFFFF;
            int busyFlag = busy ? 1 : 0;
            int clearFlag = clearOnRead ? 1 : 0;
            Console.WriteLine($"[YM-STATUS] pc=0x{pc:X4} cycles={nowCycle} status=0x{status:X2} busy={busyFlag} clear={clearFlag}");
        }

        private void UpdateYmIrq(string reason)
        {
            bool shouldAssert = ((g_com_status & 0x01) != 0 && g_reg_27_enable_A)
                || ((g_com_status & 0x02) != 0 && g_reg_27_enable_B);
            if (shouldAssert != _ymIrqAsserted)
            {
                _ymIrqAsserted = shouldAssert;
                if (TraceYmIrq)
                {
                    string state = shouldAssert ? "assert" : "clear";
                    Console.WriteLine($"[YMIRQ] {state} reason={reason} status=0x{g_com_status:X2}");
                }
            }
            md_main.g_md_z80?.irq_request(shouldAssert, "YM", g_com_status);
        }

        internal void FlushTimerStats(long frame)
        {
            if (TraceYmTimer && (_timerAEvents != 0 || _timerBEvents != 0))
            {
                Console.WriteLine(
                    $"[YMTIMER] frame={frame} A={_timerAEvents} B={_timerBEvents} " +
                    $"A_count={_timerACount} B_count={_timerBCount} A_reload={_timerAReload} B_reload={_timerBReload} " +
                    $"enA={(g_reg_27_enable_A ? 1 : 0)} enB={(g_reg_27_enable_B ? 1 : 0)} mode=0x{g_reg_27_mode:X2}");
            }
            if (TraceYmKey && _keyWrites > 0)
            {
                Console.WriteLine(
                    $"[YMKEYSTAT] frame={frame} writes={_keyWrites} onSlots={_keyOnSlots} offSlots={_keyOffSlots}");
            }
            if (TraceYmWriteStats && _ymWriteStatLogRemaining > 0)
            {
                if (_ymWriteStatLogRemaining != int.MaxValue)
                    _ymWriteStatLogRemaining--;
                Console.WriteLine(
                    $"[YMWRITE] frame={frame} addrWrites={_ymAddrWrites} dataWrites={_ymDataWrites} " +
                    $"lastAddr=0x{_ymLastAddr:X2} lastVal=0x{_ymLastVal:X2} lastSrc={_ymLastSource}");
            }
            _timerAEvents = 0;
            _timerBEvents = 0;
            _keyWrites = 0;
            _keyOnSlots = 0;
            _keyOffSlots = 0;
            _ymAddrWrites = 0;
            _ymDataWrites = 0;
        }
    }
}
