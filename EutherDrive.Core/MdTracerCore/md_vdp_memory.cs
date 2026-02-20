using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        private static readonly bool TraceConsoleEnabledMemory =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CONSOLE"), "0", StringComparison.Ordinal)
            && !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_RAW_TIMING"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceCramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CRAM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceCramWritesPc =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CRAM_PC"), "1", StringComparison.Ordinal);
        private static readonly int TraceCramWritesPcLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_CRAM_PC_LIMIT", 256);
        private static readonly int TraceCramWritesPcFrames =
            ParseTraceLimit("EUTHERDRIVE_TRACE_CRAM_PC_FRAMES", 0);
        private static readonly List<(uint Start, uint End)> TraceCramWritesPcRanges =
            md_m68k.ParseWatchRangeList("EUTHERDRIVE_TRACE_CRAM_PC_RANGE");
        private static readonly bool TraceVdpCtrlAll =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_CTRL"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpCtrlAllNoLimit =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_CTRL_ALL"), "1", StringComparison.Ordinal);
        private static readonly int TraceVdpCtrlLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_CTRL_LIMIT", 200);
        private static readonly bool TraceVdpCtrlPc =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_CTRL_PC"), "1", StringComparison.Ordinal);
        private static readonly int TraceVdpCtrlPcLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_CTRL_PC_LIMIT", 256);
        private static readonly List<(uint Start, uint End)> TraceVdpCtrlPcRanges =
            md_m68k.ParseWatchRangeList("EUTHERDRIVE_TRACE_VDP_CTRL_PC_RANGE");
        private static readonly bool TraceVsramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VSRAM"), "1", StringComparison.Ordinal);
        private static readonly int TraceVsramWritesLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VSRAM_LIMIT", 256);
        private static readonly bool TraceVdpData8 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_DATA8"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpCtrl8 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_CTRL8"), "1", StringComparison.Ordinal);
        private static readonly bool TracePatternWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES"), "1", StringComparison.Ordinal);
        private static readonly bool TracePatternWritesPc =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC"), "1", StringComparison.Ordinal);
        private static readonly int TracePatternWritesPcLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC_LIMIT", 128);
        private static readonly int TracePatternWritesPcFrames =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC_FRAMES", 0);
        private static readonly List<(uint Start, uint End)> TracePatternWritesPcRanges =
            md_m68k.ParseWatchRangeList("EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC_RANGE");
        private static readonly bool GateCpuWritesDuringDma =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DMA_WRITE_GATE"), "1", StringComparison.Ordinal);
        private static readonly bool DisableDmaWriteGate =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DMA_WRITE_GATE_DISABLE"), "1", StringComparison.Ordinal);
        private static readonly bool StrictVdpDmaWriteGate =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DMA_WRITE_GATE_STRICT"), "1", StringComparison.Ordinal);
        private static readonly bool StrictVdpAccess =
            ReadEnvDefaultOn("EUTHERDRIVE_VDP_STRICT");
        private static readonly bool DmaIgnoreEnable =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DMA_IGNORE_ENABLE"), "1", StringComparison.Ordinal);
        private static readonly bool UseVdpFifo =
            ReadEnvDefaultOn("EUTHERDRIVE_VDP_FIFO");

        private const int VdpFifoCapacity = 4;
        private const int VdpFifoInitialLatency = 3;

        private byte[]   g_vram = Array.Empty<byte>();
        private ushort[] g_cram = Array.Empty<ushort>();
        public  ushort[] g_vsram = Array.Empty<ushort>();
        public  uint[]   g_color = Array.Empty<uint>();
        public  uint[]   g_color_shadow = Array.Empty<uint>();
        public  uint[]   g_color_highlight = Array.Empty<uint>();

        private int    g_vdp_reg_code;
        private ushort g_vdp_reg_dest_address;

        // work
        private bool   g_command_select;
        private ushort g_command_word;

        private readonly int[] COLOR_NORMAL    = { 0, 52, 87, 116, 144, 172, 206, 255 };
        private readonly int[] COLOR_SHADOW    = { 0, 29, 52, 70, 87, 101, 116, 130 };
        private readonly int[] COLOR_HIGHLIGHT = { 130, 144, 158, 172, 187, 206, 228, 255 };
        private int _vdpCtrlLogRemaining = TraceVdpCtrlLimit;
        private int _vdpCtrlPcRemaining = TraceVdpCtrlPcLimit;
        private int _cramPcRemaining = TraceCramWritesPcLimit;
        private int _patternPcRemaining = TracePatternWritesPcLimit;
        private int _vsramLogRemaining = TraceVsramWritesLimit;

        private readonly List<VdpFifoEntry> _vdpFifo = new List<VdpFifoEntry>(VdpFifoCapacity);
        private readonly Queue<VdpFifoEntry> _vdpFifoPending = new Queue<VdpFifoEntry>(8);

        private struct VdpFifoEntry
        {
            public ushort Word;
            public ushort Address;
            public byte Code;
            public byte AutoInc;
            public int Latency;
        }

        private void EnqueueVdpFifo(ushort word, byte code, ushort address, byte autoinc)
        {
            var entry = new VdpFifoEntry
            {
                Word = word,
                Address = address,
                Code = code,
                AutoInc = autoinc,
                Latency = VdpFifoInitialLatency
            };

            // Match jgenesis timing: update sprite cache on FIFO push for VRAM writes.
            // This avoids rendering glitches when sprite attributes are updated and read back quickly.
            if ((code & 0x0f) == 1)
            {
                int addr0 = address & 0xFFFF;
                UpdateSpriteCacheByte(addr0, (byte)(word >> 8));
                UpdateSpriteCacheByte(addr0 ^ 1, (byte)(word & 0xFF));
            }

            if (_vdpFifo.Count >= VdpFifoCapacity)
            {
                _vdpFifoPending.Enqueue(entry);
                return;
            }

            _vdpFifo.Add(entry);
        }

        private void TryFlushVdpFifo(bool slotAllowed)
        {
            if (!UseVdpFifo)
                return;

            if (_vdpFifo.Count == 0)
                return;

            var front = _vdpFifo[0];
            if (front.Latency > 0)
            {
                front.Latency--;
                _vdpFifo[0] = front;
                return;
            }

            if (!slotAllowed)
                return;

            _vdpFifo.RemoveAt(0);
            ApplyVdpFifoWrite(front);
            if (_vdpFifoPending.Count > 0 && _vdpFifo.Count < VdpFifoCapacity)
            {
                _vdpFifo.Add(_vdpFifoPending.Dequeue());
            }
        }

        private void DecrementFifoLatencyAll()
        {
            if (_vdpFifo.Count == 0)
                return;
            for (int i = 0; i < _vdpFifo.Count; i++)
            {
                var entry = _vdpFifo[i];
                if (entry.Latency > 0)
                {
                    entry.Latency--;
                    _vdpFifo[i] = entry;
                }
            }
        }

        private void PopFifoEntry()
        {
            if (_vdpFifo.Count == 0)
                return;

            _vdpFifo.RemoveAt(0);
            if (_vdpFifoPending.Count > 0 && _vdpFifo.Count < VdpFifoCapacity)
                _vdpFifo.Add(_vdpFifoPending.Dequeue());
        }

        private void ApplyVdpFifoWrite(VdpFifoEntry entry)
        {
            int code = entry.Code & 0x0f;
            int writeAddr = entry.Address;
            switch (code)
            {
                case 1:
                    vram_write_w(writeAddr, entry.Word);
                    pattern_chk(writeAddr, (byte)(entry.Word >> 8));
                    pattern_chk(writeAddr ^ 1, (byte)(entry.Word & 0xff));
                    this.RecordVramWriteForTracking(writeAddr, entry.Word);
                    this.TrackScrollRegionWrite(writeAddr & 0xFFFF);
                    this.LogVramWrite("FIFO", writeAddr & 0xFFFF, entry.Word, entry.AutoInc, entry.Code);
                    break;
                case 3:
                    _mdCramWritesThisFrame++;
                    cram_set((writeAddr >> 1) & 0x3f, entry.Word);
                    break;
                case 5:
                    if (writeAddr < 80)
                    {
                        g_vsram[writeAddr >> 1] = entry.Word;
                    }
                    break;
            }
        }

        private void ProcessVdpFifoForScanline()
        {
            if (!UseVdpFifo)
                return;

            if (_vdpFifo.Count == 0 && _vdpFifoPending.Count == 0)
                return;

            bool isH40 = IsH40Mode();
            bool blank = _vblankActive || g_vdp_reg_1_6_display == 0 || g_scanline >= g_display_ysize;
            var blankRefresh = isH40 ? H40BlankRefreshSlots : H32BlankRefreshSlots;

            for (int slotIdx = 0; slotIdx < AccessSlotsTableSize; slotIdx++)
            {
                if (_vdpFifo.Count == 0)
                {
                    if (_vdpFifoPending.Count == 0)
                        break;
                    _vdpFifo.Add(_vdpFifoPending.Dequeue());
                }

                bool inBlankRefresh = blankRefresh[slotIdx];
                if (!inBlankRefresh)
                {
                    if (g_dma_mode != 0 && g_dma_leng > 0 && _vdpFifo.Count == 0)
                    {
                        switch (g_dma_mode)
                        {
                            case 1:
                                DmaStepMemory();
                                break;
                            case 2:
                                DmaStepFill();
                                break;
                            case 3:
                                DmaStepCopy();
                                break;
                        }
                        write_dma_leng();
                        if (g_dma_leng <= 0)
                            FinishDmaTransfer(g_dma_mode);
                    }
                    DecrementFifoLatencyAll();
                }

                bool slotAllowed = IsSlotAllowed(slotIdx, blank, isH40);
                if (!slotAllowed)
                    continue;

                if (_vdpFifo.Count == 0)
                    continue;

                var front = _vdpFifo[0];
                if (front.Latency > 0)
                    continue;

                ApplyVdpFifoWrite(front);
                PopFifoEntry();
            }
        }

        // Centraliserad felhantering (strict by default; disable with EUTHERDRIVE_VDP_STRICT=0).
        private static void Error(string where)
        {
            if (StrictVdpAccess)
                throw new InvalidOperationException($"VDP: {where}");
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP"), "1", StringComparison.Ordinal))
                Console.WriteLine($"[VDP] {where}");
        }

        private static bool TraceStatusRead =>
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_STATUS_READ"), "1", StringComparison.Ordinal);
        private static readonly bool TraceStatusLoop =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_STATUS_LOOP"), "1", StringComparison.Ordinal);
        private static readonly int TraceStatusLoopThreshold =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_STATUS_LOOP_LIMIT", 2000);
        private static readonly bool TraceSmsStatusFile =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_STATUS_FILE"), "1", StringComparison.Ordinal);
        private const int SmsStatusFileLogLimit = 5000;
        [NonSerialized] private int _smsStatusFileLogCount;
        [NonSerialized] private string? _smsStatusFileLogPath;
        [NonSerialized] private int _statusLoopCount;
        [NonSerialized] private int _statusLoopLastPc;
        [NonSerialized] private long _statusLoopLastFrame = -1;
        private static readonly bool TraceSmsDataPort =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_DATA"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSmsControlPort =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_CTRL"), "1", StringComparison.Ordinal);
        private int _smsDataPortLogCount;
        private int _smsControlPortLogCount;

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private int GetSmsAutoIncrement()
        {
            // SMS VDP can use register 0x0F as auto-increment; default to 1 when unset.
            int inc = _smsRegs[0x0F];
            return inc == 0 ? 1 : inc;
        }

        // ----------------------------------------------------------------
        // read
        // ----------------------------------------------------------------
        public byte read8(uint in_address)
        {
            if (md_main.g_masterSystemMode && (in_address & 0x00000E) == 0x00)
            {
                return SmsReadData();
            }
            if (md_main.g_masterSystemMode && (in_address & 0x00000E) == 0x04)
            {
                return SmsReadStatus();
            }

            ushort w = read16(in_address);
            return ((in_address & 1) == 0) ? (byte)(w >> 8) : (byte)w;
        }

        public ushort read16(uint in_address)
        {
            if (md_main.g_masterSystemMode)
            {
                uint port = in_address & 0x00000E;

                if (port == 0x00)
                {
                    byte data = SmsReadData();
                    return (ushort)((data << 8) | data);
                }

                if (port == 0x04)
                {
                    byte status = SmsReadStatus();
                    return (ushort)((status << 8) | status);
                }

                if (port == 0x08)
                {
                    return get_vdp_hvcounter();
                }

                return 0xFFFF;
            }

            ushort w_out = 0;
            in_address &= 0xfffffe;

            if (in_address <= 0xc00003)
            {
                ApplyDataPortAccessSlotDelay(md_m68k.g_opcode);
                g_command_select = false;
                switch (g_vdp_reg_code)
                {
                    case 0:
                        w_out = vram_read_w(g_vdp_reg_dest_address & 0xFFFE);
                        break;
                    case 8:
                        w_out = g_cram[(g_vdp_reg_dest_address >> 1) & 0x3f];
                        break;
                    case 4:
                        w_out = g_vsram[(g_vdp_reg_dest_address >> 1) % 40];
                        break;
                    default:
                        w_out = 0xFFFF;
                        break;
                }
                g_vdp_reg_dest_address = (ushort)(g_vdp_reg_dest_address + g_vdp_reg_15_autoinc);
            }
            else if (in_address <= 0xc00007)
            {
                g_command_select = false;
                w_out = ReadStatusWord(md_m68k.g_opcode);
                md_m68k.RecordVdpStatusRead(w_out);
                ushort postStatus = (ushort)(w_out & ~VDP_STATUS_VBLANK_MASK);
                LogStatusRead(w_out, postStatus);
                if (TraceStatusRead)
                {
                    int dmaBit = (w_out & 0x0002) != 0 ? 1 : 0;
                    Console.WriteLine(
                        $"[VDP-STATUS-RD] frame={_frameCounter} status=0x{w_out:X4} vblank={((w_out & VDP_STATUS_VBLANK_MASK) != 0 ? 1 : 0)} " +
                        $"overflow={((w_out & 0x0040) != 0 ? 1 : 0)} collision={((w_out & 0x0020) != 0 ? 1 : 0)} dmaBit={dmaBit} " +
                        $"dmaMode={g_dma_mode} dmaLen={g_dma_leng}");
                }
                if (TraceStatusLoop)
                {
                    int pc = (int)md_m68k.g_reg_PC;
                    if (_statusLoopLastFrame != _frameCounter || pc != _statusLoopLastPc)
                    {
                        _statusLoopLastFrame = _frameCounter;
                        _statusLoopLastPc = pc;
                        _statusLoopCount = 1;
                    }
                    else
                    {
                        _statusLoopCount++;
                        if (_statusLoopCount == TraceStatusLoopThreshold)
                        {
                    ushort hv = get_vdp_hvcounter(md_m68k.g_opcode);
                            Console.WriteLine(
                                $"[VDP-STATUS-LOOP] frame={_frameCounter} pc=0x{pc:X6} count={_statusLoopCount} " +
                                $"status=0x{w_out:X4} hv=0x{hv:X4}");
                        }
                    }
                }
                g_vdp_status_7_vinterrupt = 0; // ack on status read
                g_vdp_status_6_sprite = 0;
                g_vdp_status_5_collision = 0;
                if (md_main.g_masterSystemMode)
                    md_main.g_md_z80?.irq_request(false, "VDP", 0);
            }
            else if (in_address <= 0xc0000e)
            {
                w_out = get_vdp_hvcounter();
            }
            else
            {
                w_out = 0xFFFF;
            }

            return w_out;
        }

        private byte SmsReadStatus()
        {
            _smsCommandPending = false;
            // If the scanline has reached vblank but the flag was missed, set it here so polling works.
            if (md_main.g_masterSystemMode && g_vdp_status_3_vbrank == 0 && g_scanline >= g_display_ysize)
            {
                g_vdp_status_3_vbrank = 1;
                md_main.g_md_vdp?.UpdateSmsIrqLine();
            }
            byte status = 0;
            if (g_vdp_status_3_vbrank != 0)
                status |= 0x80;
            if (g_vdp_status_6_sprite != 0)
                status |= 0x40;
            if (g_vdp_status_5_collision != 0)
                status |= 0x20;

            LogSmsStatusFile(status);

            // Reading status clears VBlank + sprite flags (SMS behavior).
            g_vdp_status_3_vbrank = 0;
            g_vdp_status_6_sprite = 0;
            g_vdp_status_5_collision = 0;
            g_vdp_status_7_vinterrupt = 0;
            md_m68k.g_interrupt_V_req = false;
            md_main.g_md_vdp?.OnSmsStatusRead();
            return status;
        }

        private void LogSmsStatusFile(byte status)
        {
            if (!md_main.g_masterSystemMode || !TraceSmsStatusFile)
                return;
            if (_smsStatusFileLogCount >= SmsStatusFileLogLimit)
                return;
            if (_smsStatusFileLogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                _smsStatusFileLogPath = Path.Combine(dir, "sms_status.log");
                File.WriteAllText(_smsStatusFileLogPath, "SMS status log\n");
            }
            long frame = _frameCounter;
            int line = g_scanline;
            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            string lineText = $"frame={frame} line={line} pc=0x{pc:X4} status=0x{status:X2}\n";
            File.AppendAllText(_smsStatusFileLogPath, lineText);
            _smsStatusFileLogCount++;
        }

        public uint read32(uint in_address)
        {
            if (in_address <= 0xc0001e)
            {
                return ((uint)read16(in_address) << 16) | read16(in_address + 2);
            }
            Error($"read32: invalid address 0x{in_address:X6}");
            return 0;
        }

        // ----------------------------------------------------------------
        // write
        // ----------------------------------------------------------------
        public void write8(uint in_address, byte in_data)
        {
            if (md_main.g_masterSystemMode)
            {
                uint port = in_address & 0x00000E;
                if (port == 0x00)
                {
                    if (TraceSmsDataPort && _smsDataPortLogCount < 16)
                    {
                        _smsDataPortLogCount++;
                        ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
                        Console.WriteLine($"[SMS VDP] data port write pc=0x{pc:X4} val=0x{in_data:X2} code={_smsVdpCode} addr=0x{_smsVdpAddr:X4}");
                    }
                    SmsWriteData(in_data);
                    return;
                }
                if (port == 0x04)
                {
                    if (TraceSmsControlPort && _smsControlPortLogCount < 16)
                    {
                        _smsControlPortLogCount++;
                        ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
                        Console.WriteLine($"[SMS VDP] ctrl port write pc=0x{pc:X4} val=0x{in_data:X2} pending={(_smsCommandPending ? 1 : 0)} addr=0x{_smsVdpAddr:X4}");
                    }
                    SmsWriteControl(in_data);
                    return;
                }
            }

            in_address &= 0x00FF_FFFF;

            if (in_address <= 0xc00003)
            {
                if (TraceVdpData8 && TraceConsoleEnabledMemory)
                {
                    uint pc = md_m68k.g_reg_PC;
                    Console.WriteLine($"[VDP-DATA8] frame={_frameCounter} addr=0x{in_address:X6} val=0x{in_data:X2} pc=0x{pc:X6}");
                }
                // MD VDP data port byte writes mirror the byte into a word.
                ushort mirrored = (ushort)((in_data << 8) | in_data);
                write16(in_address, mirrored);
                return;
            }

            // Default: mirror byte to both halves and use 16-bit path.
            if ((in_address & 0x00FF_FFFF) == 0xC00004 && TraceVdpCtrl8 && TraceConsoleEnabledMemory)
            {
                uint pc = md_m68k.g_reg_PC;
                Console.WriteLine($"[VDP-CTRL8] frame={_frameCounter} addr=0x{in_address:X6} val=0x{in_data:X2} pc=0x{pc:X6}");
            }
            ushort w = (ushort)((in_data << 8) | in_data);
            write16(in_address, w);
        }

        public void write16(uint in_address, ushort in_data)
        {
            if (md_main.g_masterSystemMode)
            {
                uint port = in_address & 0x00000E;
                if (port == 0x00)
                {
                    SmsWriteControl((byte)(in_data >> 8));
                    SmsWriteData((byte)(in_data & 0xff));
                    return;
                }
                if (port == 0x04)
                {
                    SmsWriteControl((byte)(in_data >> 8));
                    SmsWriteControl((byte)(in_data & 0xff));
                    return;
                }
            }

            in_address &= 0xfffffe;

            if (in_address <= 0xc00003)
            {
                ApplyDataPortAccessSlotDelay(md_m68k.g_opcode);
                _mdDataWritesThisFrame++;
                RecordDataPortWriteCode(g_vdp_reg_code);

                bool dmaActive = g_vdp_status_1_dma != 0 || g_dma_mode != 0 || g_dma_leng > 0;
                if (GateCpuWritesDuringDma && !DisableDmaWriteGate && dmaActive)
                {
                    if (StrictVdpDmaWriteGate)
                    {
                        _mdDataWritesDroppedThisFrame++;
                        if ((g_vdp_reg_code & 0x0f) == 1)
                            _mdVramWritesDroppedThisFrame++;
                        return;
                    }
                    // Default: add wait cycles to simulate bus stall instead of dropping writes
                    md_m68k.g_clock += 12;
                }

                // Comprehensive VDP data-port write tracing
                TraceVdpDataWrite(in_address, in_data, g_vdp_reg_code, g_vdp_reg_dest_address, g_vdp_reg_15_autoinc);
                TraceDataPortWindowWrite(in_address, in_data);

                if (MdTracerCore.MdLog.Enabled && !_mdDataPortLogged)
                {
                    _mdDataPortLogged = true;
                    MdTracerCore.MdLog.WriteLine($"[VDP] MD data port write addr=0x{in_address:X6}");
                }
                g_command_select = false;

                if (g_dma_fill_req)
                {
                    g_dma_fill_req = false;
                    dma_run_fill_req(in_data);
                    return;
                }

                if (UseVdpFifo)
                {
                    ushort addr = g_vdp_reg_dest_address;
                    byte code = (byte)g_vdp_reg_code;
                    byte autoinc = (byte)g_vdp_reg_15_autoinc;
                    g_vdp_reg_dest_address = (ushort)((addr + autoinc) & 0xffff);
                    EnqueueVdpFifo(in_data, code, addr, autoinc);
                    return;
                }

                switch (g_vdp_reg_code & 0x0f)
                {
                    case 1: // VRAM write
                    {
                        int writeAddr = g_vdp_reg_dest_address;
                        vram_write_w(writeAddr, in_data);
                        pattern_chk(writeAddr, (byte)(in_data >> 8));
                        pattern_chk(writeAddr ^ 1, (byte)(in_data & 0xff));
                        // Track the write using VDP's tracking method
                        this.RecordVramWriteForTracking(writeAddr, in_data);
                        // Also track scroll region writes
                        this.TrackScrollRegionWrite(writeAddr & 0xFFFF);
                        // Log detailed write info for scroll regions
                        this.LogVramWrite("CPU", writeAddr & 0xFFFF, in_data, g_vdp_reg_15_autoinc, g_vdp_reg_code);
                        g_vdp_reg_dest_address = (ushort)((writeAddr + g_vdp_reg_15_autoinc) & 0xffff);
                        break;
                    }

                    case 3: // CRAM write
                    {
                        _mdCramWritesThisFrame++;
                        int col = (g_vdp_reg_dest_address >> 1) & 0x3f;
                        cram_set(col, in_data);
                        g_vdp_reg_dest_address = (ushort)((g_vdp_reg_dest_address + g_vdp_reg_15_autoinc) & 0xffff);
                        break;
                    }

                    case 5: // VSRAM write
                        if (g_vdp_reg_dest_address < 80)
                        {
                            if (TraceVsramWrites && _vsramLogRemaining > 0)
                            {
                                if (_vsramLogRemaining != int.MaxValue)
                                    _vsramLogRemaining--;
                                Console.WriteLine(
                                    $"[VSRAM-WR] frame={_frameCounter} scanline={g_scanline} pc=0x{md_m68k.g_reg_PC:X6} " +
                                    $"addr=0x{g_vdp_reg_dest_address:X4} idx={(g_vdp_reg_dest_address >> 1)} val=0x{in_data:X4} " +
                                    $"autoinc=0x{g_vdp_reg_15_autoinc:X2} code=0x{g_vdp_reg_code:X2}");
                            }
                            g_vsram[g_vdp_reg_dest_address >> 1] = in_data;
                            g_vdp_reg_dest_address = (ushort)((g_vdp_reg_dest_address + g_vdp_reg_15_autoinc) & 0xffff);
                        }
                        break;

                    default:
                        // tysta “övrigt” – vissa spel skriver konstigheter
                        break;
                }
            }
            else if (in_address <= 0xc00007)
            {
                _mdCtrlWritesThisFrame++;
                if (MdTracerCore.MdLog.Enabled && !_mdCtrlPortLogged)
                {
                    _mdCtrlPortLogged = true;
                    MdTracerCore.MdLog.WriteLine($"[VDP] MD control port write addr=0x{in_address:X6}");
                }

                // Trace control port writes in interlace mode 2
                TraceVdpControlWrite(in_address, in_data, g_command_select, g_command_word);
                
                // Log ALL control port writes (gated)
                if (TraceVdpCtrlAll && _vdpCtrlLogRemaining > 0)
                {
                    Console.WriteLine($"[VDP-CTRL] frame={_frameCounter} addr=0x{in_address:X6} raw=0x{in_data:X4}");
                    if (_vdpCtrlLogRemaining != int.MaxValue)
                        _vdpCtrlLogRemaining--;
                }
                if (TraceVdpCtrlPc && _vdpCtrlPcRemaining > 0)
                {
                    uint pcFilter = md_m68k.g_reg_PC;
                    if (TraceVdpCtrlPcRanges.Count == 0)
                    {
                        _vdpCtrlPcRemaining--;
                        Console.WriteLine($"[VDP-CTRL-PC] frame={_frameCounter} pc=0x{pcFilter:X6} addr=0x{in_address:X6} raw=0x{in_data:X4}");
                    }
                    else
                    {
                        foreach ((uint start, uint end) in TraceVdpCtrlPcRanges)
                        {
                            if (pcFilter >= start && pcFilter <= end)
                            {
                                _vdpCtrlPcRemaining--;
                                Console.WriteLine($"[VDP-CTRL-PC] frame={_frameCounter} pc=0x{pcFilter:X6} addr=0x{in_address:X6} raw=0x{in_data:X4}");
                                break;
                            }
                        }
                    }
                }

                if (!g_command_select)
                {
                    if ((in_data & 0xc000) == 0x8000)
                    {
                        // register write
                        byte rs   = (byte)((in_data >> 8) & 0x1f);
                        byte data = (byte)(in_data & 0xff);
                        
                        // Log DMA register writes (gated)
                        if (TraceVdpCtrlAll && _vdpCtrlLogRemaining > 0 && rs >= 0x13 && rs <= 0x17) // Registers 19-23
                        {
                            Console.WriteLine($"[VDP-MEM-REG] frame={_frameCounter} raw=0x{in_data:X4} reg=0x{rs:X2} data=0x{data:X2}");
                            // Also log as VDP-CTRL for consistency
                            Console.WriteLine($"[VDP-CTRL-REG] frame={_frameCounter} addr=0x{in_address:X6} raw=0x{in_data:X4} reg=0x{rs:X2} data=0x{data:X2}");
                            if (_vdpCtrlLogRemaining != int.MaxValue)
                                _vdpCtrlLogRemaining--;
                        }
                        
                        set_vdp_register(rs, data);
                    }
                    else
                    {
                        // address set (1st word)
                        g_command_select = true;
                        g_command_word   = in_data;
                        g_vdp_reg_dest_address = (ushort)((in_data & 0x3fff) | (g_vdp_reg_dest_address & (3 << 14)));
                        g_vdp_reg_code = ((in_data >> 14) & 0x3) | (g_vdp_reg_code & 0x3C);
                    }
                }
                else
                {
                    // address set (2nd word)
                    g_command_select   = false;
                    // Always decode the full code field (including DMA request bit).
                    // DMA enable only gates execution, not decoding.
                    int codeMask = 0x3C;
                    g_vdp_reg_dest_address = (ushort)((g_vdp_reg_dest_address & 0x3fff) | ((in_data & 0x0007) << 14));
                    g_vdp_reg_code = (g_vdp_reg_code & ~codeMask) | ((in_data >> 2) & codeMask);

                    // Log VDP command decoding (gated)
                    if (TraceVdpCtrlAll && _vdpCtrlLogRemaining > 0 && (_frameCounter < 100 || TraceVdpCtrlAllNoLimit))
                    {
                        int codeLow = g_vdp_reg_code & 0x0f;
                        string target = codeLow switch
                        {
                            0x01 => "VRAM",
                            0x03 => "CRAM",
                            0x05 => "VSRAM",
                            _ => $"UNK({codeLow})"
                        };
                        bool dmaRequested = (g_vdp_reg_code & 0x20) != 0;
                        Console.WriteLine(
                            $"[VDP-CTRL-DECODE] frame={_frameCounter} target={target} addr=0x{g_vdp_reg_dest_address:X4} " +
                            $"code=0x{g_vdp_reg_code:X2} autoinc=0x{g_vdp_reg_15_autoinc:X2} dmaReq={(dmaRequested ? 1 : 0)} " +
                            $"dmaEn={g_vdp_reg_1_4_dma} dmaMode={g_vdp_reg_23_dma_mode} word1=0x{g_command_word:X4} word2=0x{in_data:X4}");
                        if (_vdpCtrlLogRemaining != int.MaxValue)
                            _vdpCtrlLogRemaining--;
                    }

                    if (TraceVdpCtrlAll && (_frameCounter < 100 || TraceVdpCtrlAllNoLimit) && (g_vdp_reg_code & 0x20) != 0 && g_vdp_reg_1_4_dma == 0)
                    {
                        Console.WriteLine(
                            $"[VDP-DMA-IGNORE] frame={_frameCounter} ignore={(DmaIgnoreEnable ? 1 : 0)} code=0x{g_vdp_reg_code:X2} addr=0x{g_vdp_reg_dest_address:X4}");
                    }

                    if ((g_vdp_reg_code & 0x20) != 0 && (g_vdp_reg_1_4_dma == 1 || DmaIgnoreEnable))
                    {
                        // Log DMA trigger - will be logged in dma_run_memory_req
                        switch (g_vdp_reg_23_dma_mode)
                        {
                            case 0:
                            case 1:
                                dma_run_memory_req(); break;
                            case 2:
                                g_dma_fill_req = true; break;
                            case 3:
                                dma_run_copy_req(); break;
                        }
                    }
                }
            }
            else if (in_address <= 0xc0001e)
            {
                // H/V counter + mirrors: writes are ignored on real hardware.
                return;
            }
            else
            {
                Error($"write16: invalid address 0x{in_address:X6}");
            }
        }

        public void write32(uint in_address, uint in_data)
        {
            if (md_main.g_masterSystemMode)
            {
                write16(in_address, (ushort)(in_data >> 16));
                write16(in_address, (ushort)(in_data & 0xffff));
                return;
            }

            if (in_address <= 0xc0001e)
            {
                write16(in_address, (ushort)(in_data >> 16));
                write16(in_address, (ushort)(in_data & 0xffff));
                return;
            }
            Error($"write32: invalid address 0x{in_address:X6}");
        }

        private byte SmsReadData()
        {
            _smsCommandPending = false;
            int inc = GetSmsAutoIncrement();
            byte value = _smsReadBuffer;
            _smsReadBuffer = _smsVram[_smsVdpAddr & 0x3FFF];
            _smsVdpAddr = (_smsVdpAddr + inc) & 0x3FFF;
            return value;
        }

        private void SmsWriteControl(byte value)
        {
            if (!_smsCommandPending)
            {
                _smsCommandLow = value;
                _smsVdpAddrBeforeCtrl = _smsVdpAddr;
                // First control write sets low byte of VRAM address.
                _smsVdpAddr = (_smsVdpAddr & 0x3F00) | value;
                _smsCommandPending = true;
                return;
            }

            _smsCommandPending = false;
            ushort cmd = (ushort)(_smsCommandLow | (value << 8));
            int code = (cmd >> 14) & 0x3;
            // Second control write sets MSB of VRAM address (applies to all commands, including register writes).
            _smsVdpAddr = (_smsVdpAddr & 0x00FF) | ((value & 0x3F) << 8);
            LogSmsControlFileIfNeeded(_smsCommandLow, value, cmd, code);
            SmsLogControl(_smsCommandLow, value, cmd);
            SmsDecodeCommand(cmd);
        }

        private void SmsWriteData(byte value)
        {
            _smsCommandPending = false;
            switch (_smsVdpCode)
            {
                case 0:
                case 1:
                    _smsVramWritesTotal++;
                    int vramAddr = _smsVdpAddr & 0x3FFF;
                    _smsVram[vramAddr] = value;
                    LogSmsNtWriteIfNeeded(vramAddr, value);
                    LogSmsVramWriteIfNeeded(vramAddr, value);
                    _smsVdpAddr = (_smsVdpAddr + GetSmsAutoIncrement()) & 0x3FFF;
                    _smsReadBuffer = value;
                    return;

                case 3:
                    _smsCramWritesTotal++;
                    int cramAddr = _smsVdpAddr & 0x1F;
                    _smsCram[cramAddr] = value;
                    LogSmsCramWriteIfNeeded(cramAddr, value);
                    _smsVdpAddr = (_smsVdpAddr + GetSmsAutoIncrement()) & 0x3FFF;
                    SmsUpdatePalette(cramAddr, value);
                    _smsReadBuffer = value;
                    return;

                default:
                    if (!_smsDataIgnoredLogged || _smsVdpCode != 0)
                    {
                        _smsDataIgnoredLogged = true;
                        SmsLog($"[SMS VDP] data write ignored code={_smsVdpCode} val=0x{value:X2}");
                    }
                    _smsReadBuffer = value;
                    return;
            }
        }

        private void SmsDecodeCommand(ushort cmd)
        {
            int code = (cmd >> 14) & 0x3;
            bool traceSmsReg = string.Equals(
                Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_REG"), "1",
                StringComparison.Ordinal);

            if (code == 2)
            {
                int reg = (cmd >> 8) & 0x0F;
                byte data = (byte)(cmd & 0xFF);
                if (reg == 0 && string.Equals(
                        Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DISABLE_LINEIRQ"), "1",
                        StringComparison.Ordinal))
                {
                    // Force line IRQ disable (bit4) for debugging.
                    data = (byte)(data & ~0x10);
                }
                _smsRegs[reg] = data;
                if (reg == 0 || reg == 1 || reg == 2 || reg == 8 || reg == 9 || reg == 0x0A)
                    LogSmsRegWriteIfNeeded(reg, data);
                // After register write, VDP data port writes should target VRAM by default.
                _smsVdpCode = 0;
                if (traceSmsReg)
                {
                    ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
                    Console.WriteLine($"[SMS REG] pc=0x{pc:X4} r{reg:X}=0x{data:X2}");
                }
                if (reg == 1 && !_smsDisplayOnLogged && (data & 0x40) != 0)
                {
                    _smsDisplayOnLogged = true;
                    MdTracerCore.MdLog.WriteLine("[SMS VDP] display enabled (reg1 bit6)");
                }
                if (reg == 1)
                    g_vdp_reg_1_6_display = (byte)((data & 0x40) != 0 ? 1 : 0);
                if (reg == 0x0A)
                    md_main.g_md_vdp?.SetSmsLineCounterReload(data);
                if (reg == 0 || reg == 1)
                    md_main.g_md_vdp?.UpdateSmsIrqLine();
                if ((reg == 0 || reg == 1) && md_main.g_masterSystemMode)
                {
                    bool mode224 = SmsMode224(_smsRegs[0], _smsRegs[1]);
                    g_display_ysize = mode224 ? 224 : 192;
                    g_display_ycell = mode224 ? 28 : 24;
                    g_vertical_line_max = 262;
                    UpdateOutputWidth();
                }
                SmsLog($"[SMS VDP] REG r{reg:X}={data:X2}", reg == 1);
                return;
            }

            _smsVdpCode = code;
            int cmdAddr = _smsVdpAddr & 0x3FFF;
            if (code == 0)
            {
                _smsReadBuffer = _smsVram[_smsVdpAddr & 0x3FFF];
                _smsVdpAddr = (_smsVdpAddr + GetSmsAutoIncrement()) & 0x3FFF;
            }
            SmsLog($"[SMS VDP] CMD code={code} addr=0x{cmdAddr:X4} raw=0x{cmd:X4}");
        }

        private void LogSmsNtWriteIfNeeded(int addr, byte value)
        {
            if (!md_main.g_masterSystemMode)
                return;

            if (_smsNtWriteLogStartFrame < 0)
            {
                int start = ParseTraceLimit("EUTHERDRIVE_SMS_NT_WRITE_LOG_START_FRAME", -1);
                int count = ParseTraceLimit("EUTHERDRIVE_SMS_NT_WRITE_LOG_FRAME_COUNT", 1);
                int maxLines = ParseTraceLimit("EUTHERDRIVE_SMS_NT_WRITE_LOG_MAX_LINES", 20000);
                _smsNtWriteLogStartFrame = start;
                _smsNtWriteLogEndFrame = (start >= 0 && count > 0) ? (start + count - 1) : -1;
                _smsNtWriteLogMaxLines = maxLines;
                _smsNtWriteLogStartAddr = ParseSmsAddr("EUTHERDRIVE_SMS_NT_WRITE_LOG_START_ADDR", 0x0000);
                _smsNtWriteLogEndAddr = ParseSmsAddr("EUTHERDRIVE_SMS_NT_WRITE_LOG_END_ADDR", 0x3FFF);
            }

            int targetFrame = ParseTraceLimit("EUTHERDRIVE_SMS_NT_WRITE_LOG_FRAME", -1);
            bool inSingleFrame = targetFrame >= 0 && _frameCounter >= targetFrame;
            bool inRange = _smsNtWriteLogStartFrame >= 0
                && _smsNtWriteLogEndFrame >= _smsNtWriteLogStartFrame
                && _frameCounter >= _smsNtWriteLogStartFrame
                && _frameCounter <= _smsNtWriteLogEndFrame;
            if (!inSingleFrame && !inRange)
                return;

            if (_smsNtWriteLogLines >= _smsNtWriteLogMaxLines)
                return;
            if (addr < _smsNtWriteLogStartAddr || addr > _smsNtWriteLogEndAddr)
                return;


            if (_smsNtWriteLogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                if (inRange)
                {
                    _smsNtWriteLogPath = Path.Combine(
                        dir,
                        $"sms_nt_writes_{_smsNtWriteLogStartFrame}_{_smsNtWriteLogEndFrame}.log");
                    File.WriteAllText(_smsNtWriteLogPath,
                        $"SMS NT write log frames={_smsNtWriteLogStartFrame}-{_smsNtWriteLogEndFrame}\n");
                }
                else
                {
                    _smsNtWriteLogPath = Path.Combine(dir, $"sms_nt_writes_{_frameCounter}.log");
                    File.WriteAllText(_smsNtWriteLogPath, $"SMS NT write log frame={_frameCounter}\n");
                }
            }

            if (_smsNtWriteLogFrame != (int)_frameCounter && !inRange)
                _smsNtWriteLogFrame = (int)_frameCounter;

            if ((addr & 0x3FFF) < 0x3800 || (addr & 0x3FFF) > 0x3EFF)
                return;

            string outPath = _smsNtWriteLogPath ?? "/home/nichlas/EutherDrive/logs/sms_nt_writes_unknown.log";
            int inc = GetSmsAutoIncrement();
            int code = _smsVdpCode;
            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            md_z80? z80 = md_main.g_md_z80;
            string bankInfo = string.Empty;
            if (z80 != null)
            {
                byte bank0 = z80.SmsBank0;
                byte bank1 = z80.SmsBank1;
                byte bank2 = z80.SmsBank2;
                bool lastWasBanked = z80.LastReadWasBanked;
                ushort lastReadAddr = z80.LastReadAddr;
                byte lastReadVal = z80.LastReadValue;
                ushort lastReadPc = z80.LastReadPc;
                uint lastReadM68k = z80.LastReadM68kAddr;
                bankInfo = $" bank0=0x{bank0:X2} bank1=0x{bank1:X2} bank2=0x{bank2:X2}" +
                           $" lastAddr=0x{lastReadAddr:X4} lastVal=0x{lastReadVal:X2} lastPc=0x{lastReadPc:X4}" +
                           (lastWasBanked ? $" lastM68k=0x{lastReadM68k:X6}" : "");
            }
            string line = $"frame={_frameCounter} addr=0x{addr:X4} val=0x{value:X2} code={code} inc=0x{inc:X2} reg2=0x{_smsRegs[2]:X2} regF=0x{_smsRegs[0x0F]:X2} pc=0x{pc:X4}{bankInfo}\n";
            File.AppendAllText(outPath, line);
            _smsNtWriteLogLines++;
        }

        private static int ParseSmsAddr(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
                return hex & 0x3FFF;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec))
                return dec & 0x3FFF;
            return fallback;
        }

        private void LogSmsVramWriteIfNeeded(int addr, byte value)
        {
            if (!md_main.g_masterSystemMode)
                return;

            if (_smsVramWriteLogStartFrame < 0)
            {
                int start = ParseTraceLimit("EUTHERDRIVE_SMS_VRAM_WRITE_LOG_START_FRAME", -1);
                int count = ParseTraceLimit("EUTHERDRIVE_SMS_VRAM_WRITE_LOG_FRAME_COUNT", 1);
                int maxLines = ParseTraceLimit("EUTHERDRIVE_SMS_VRAM_WRITE_LOG_MAX_LINES", 20000);
                _smsVramWriteLogStartFrame = start;
                _smsVramWriteLogEndFrame = (start >= 0 && count > 0) ? (start + count - 1) : -1;
                _smsVramWriteLogMaxLines = maxLines;
            }

            if (_smsVramWriteLogStartFrame < 0 || _smsVramWriteLogEndFrame < _smsVramWriteLogStartFrame)
                return;

            if (_frameCounter < _smsVramWriteLogStartFrame || _frameCounter > _smsVramWriteLogEndFrame)
                return;

            if (_smsVramWriteLogLines >= _smsVramWriteLogMaxLines)
                return;

            int startAddr = ParseSmsAddr("EUTHERDRIVE_SMS_VRAM_WRITE_LOG_START_ADDR", 0x0000);
            int endAddr = ParseSmsAddr("EUTHERDRIVE_SMS_VRAM_WRITE_LOG_END_ADDR", 0x3FFF);
            if (addr < startAddr || addr > endAddr)
                return;

            if (_smsVramWriteLogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                _smsVramWriteLogPath = Path.Combine(
                    dir,
                    $"sms_vram_writes_{_smsVramWriteLogStartFrame}_{_smsVramWriteLogEndFrame}.log");
                File.WriteAllText(_smsVramWriteLogPath,
                    $"SMS VRAM write log frames={_smsVramWriteLogStartFrame}-{_smsVramWriteLogEndFrame}\n");
            }

            int inc = GetSmsAutoIncrement();
            int code = _smsVdpCode;
            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            md_z80? z80 = md_main.g_md_z80;
            string bankInfo = string.Empty;
            if (z80 != null)
            {
                byte bank0 = z80.SmsBank0;
                byte bank1 = z80.SmsBank1;
                byte bank2 = z80.SmsBank2;
                bool lastWasBanked = z80.LastReadWasBanked;
                ushort lastReadAddr = z80.LastReadAddr;
                byte lastReadVal = z80.LastReadValue;
                ushort lastReadPc = z80.LastReadPc;
                uint lastReadM68k = z80.LastReadM68kAddr;
                bankInfo = $" bank0=0x{bank0:X2} bank1=0x{bank1:X2} bank2=0x{bank2:X2}" +
                           $" lastAddr=0x{lastReadAddr:X4} lastVal=0x{lastReadVal:X2} lastPc=0x{lastReadPc:X4}" +
                           (lastWasBanked ? $" lastM68k=0x{lastReadM68k:X6}" : "");
            }
            string line = $"frame={_frameCounter} line={g_scanline} addr=0x{addr:X4} val=0x{value:X2} code={code} inc=0x{inc:X2} pc=0x{pc:X4}{bankInfo}\n";
            File.AppendAllText(_smsVramWriteLogPath, line);
            _smsVramWriteLogLines++;
        }

        private void LogSmsCramWriteIfNeeded(int addr, byte value)
        {
            if (!md_main.g_masterSystemMode)
                return;

            if (_smsCramWriteLogStartFrame < 0)
            {
                int start = ParseTraceLimit("EUTHERDRIVE_SMS_CRAM_WRITE_LOG_START_FRAME", -1);
                int count = ParseTraceLimit("EUTHERDRIVE_SMS_CRAM_WRITE_LOG_FRAME_COUNT", 1);
                int maxLines = ParseTraceLimit("EUTHERDRIVE_SMS_CRAM_WRITE_LOG_MAX_LINES", 20000);
                _smsCramWriteLogStartFrame = start;
                _smsCramWriteLogEndFrame = (start >= 0 && count > 0) ? (start + count - 1) : -1;
                _smsCramWriteLogMaxLines = maxLines;
            }

            if (_smsCramWriteLogStartFrame < 0 || _smsCramWriteLogEndFrame < _smsCramWriteLogStartFrame)
                return;

            long frame = _frameCounter;
            if (frame < _smsCramWriteLogStartFrame || frame > _smsCramWriteLogEndFrame)
                return;

            if (_smsCramWriteLogLines >= _smsCramWriteLogMaxLines)
                return;

            if (_smsCramWriteLogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                _smsCramWriteLogPath = Path.Combine(
                    dir,
                    $"sms_cram_writes_{_smsCramWriteLogStartFrame}_{_smsCramWriteLogEndFrame}.log");
                File.WriteAllText(_smsCramWriteLogPath,
                    $"SMS CRAM write log frames={_smsCramWriteLogStartFrame}-{_smsCramWriteLogEndFrame}\n");
            }

            int inc = GetSmsAutoIncrement();
            int code = _smsVdpCode;
            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            string line = $"frame={frame} addr=0x{addr:X2} val=0x{value:X2} code={code} inc=0x{inc:X2} pc=0x{pc:X4}\n";
            File.AppendAllText(_smsCramWriteLogPath, line);
            _smsCramWriteLogLines++;
        }

        private static bool SmsMode224(byte reg0, byte reg1)
        {
            // Mode bits mapping (from jgenesis): [reg1 bit4, reg0 bit1, reg1 bit3, reg0 bit2]
            bool m1 = (reg1 & 0x10) != 0;
            bool m2 = (reg0 & 0x02) != 0;
            bool m3 = (reg1 & 0x08) != 0;
            bool m4 = (reg0 & 0x04) != 0;
            return m1 && m2 && !m3 && m4;
        }

        private void SmsLogControl(byte low, byte high, ushort cmd)
        {
            int code = (cmd >> 14) & 0x3;
            int addr = cmd & 0x3FFF;
            SmsLog($"[SMS VDP] CTL lo=0x{low:X2} hi=0x{high:X2} => cmd=0x{cmd:X4} code={code} addr=0x{addr:X4}");
        }

        private void SmsLog(string message, bool bypassLimit = false)
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;

            if (!bypassLimit && _smsCommandLogCount >= SmsCommandLogLimit)
                return;

            MdTracerCore.MdLog.WriteLine(message);
            if (!bypassLimit)
                _smsCommandLogCount++;
        }

        private void LogSmsControlFileIfNeeded(byte low, byte high, ushort cmd, int code)
        {
            if (!md_main.g_masterSystemMode || !TraceSmsCtlFile)
                return;

            if (_smsCtlFileLogCount >= SmsCtlFileLogLimit)
                return;

            if (_smsCtlFileLogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                _smsCtlFileLogPath = Path.Combine(dir, "sms_vdp_ctl.log");
                File.WriteAllText(_smsCtlFileLogPath, "SMS VDP control log\n");
            }

            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            long frame = _frameCounter;
            int addr = cmd & 0x3FFF;
            if (code == 2)
            {
                int reg = (cmd >> 8) & 0x0F;
                byte data = (byte)(cmd & 0xFF);
                string line =
                    $"frame={frame} pc=0x{pc:X4} CTL lo=0x{low:X2} hi=0x{high:X2} " +
                    $"cmd=0x{cmd:X4} REG r{reg:X}=0x{data:X2}\n";
                File.AppendAllText(_smsCtlFileLogPath, line);
            }
            else
            {
                string line =
                    $"frame={frame} pc=0x{pc:X4} CTL lo=0x{low:X2} hi=0x{high:X2} " +
                    $"cmd=0x{cmd:X4} code={code} addr=0x{addr:X4}\n";
                File.AppendAllText(_smsCtlFileLogPath, line);
            }
            _smsCtlFileLogCount++;
        }

        private void SmsUpdatePalette(int index, byte value)
        {
            if ((uint)index >= (uint)_smsPalette.Length)
                return;

            int r = value & 0x03;
            int g = (value >> 2) & 0x03;
            int b = (value >> 4) & 0x03;
            uint rr = (uint)(r * 0x55);
            uint gg = (uint)(g * 0x55);
            uint bb = (uint)(b * 0x55);
            _smsPalette[index] = 0xFF000000u | (rr << 16) | (gg << 8) | bb;
        }

        // ----------------------------------------------------------------
        // sub
        // ----------------------------------------------------------------
        private ushort vram_read_w(int addr) =>
        (ushort)((g_vram[addr & 0xffff] << 8) | g_vram[(addr ^ 1) & 0xffff]);

        private void vram_write_w(int addr, ushort data)
        {
            // MD VDP VRAM word write: low byte stored at addr ^ 1 (matches hardware/jgenesis).
            g_vram[addr & 0xffff] = (byte)(data >> 8);
            g_vram[(addr ^ 1) & 0xffff] = (byte)(data & 0xff);
            if (TraceVramWrites)
                Console.WriteLine($"[VRAM] frame={_frameCounter} addr=0x{(addr & 0xffff):X4} data=0x{data:X4}");

            if (TraceSatWrites)
            {
                int baseAddr = g_vdp_reg_5_sprite & (IsH40Mode() ? ~0x3FF : ~0x1FF);
                int tableSize = IsH40Mode() ? 0x400 : 0x200;
                int wordAddr = addr & 0xFFFE;
                if (wordAddr >= baseAddr && wordAddr < baseAddr + tableSize)
                {
                    Console.WriteLine(
                        $"[SAT-WRITE] frame={_frameCounter} scanline={g_scanline} addr=0x{wordAddr:X4} " +
                        $"val=0x{data:X4} base=0x{baseAddr:X4} size=0x{tableSize:X3}");
                }
            }

            int spriteBase = GetSpriteTableBase();
            int spriteSize = GetSpriteTableSize();
            int addr0 = addr & 0xFFFF;
            int addr1 = (addr ^ 1) & 0xFFFF;
            bool spriteTouched = false;
            if ((uint)(addr0 - spriteBase) < (uint)spriteSize)
            {
                EnsureSpriteTableCache();
                int offset = addr0 - g_sprite_cache_base;
                if ((uint)offset < (uint)g_sprite_table_cache.Length)
                {
                    g_sprite_table_cache[offset] = (byte)(data >> 8);
                    spriteTouched = true;
                }
            }
            if ((uint)(addr1 - spriteBase) < (uint)spriteSize)
            {
                EnsureSpriteTableCache();
                int offset = addr1 - g_sprite_cache_base;
                if ((uint)offset < (uint)g_sprite_table_cache.Length)
                {
                    g_sprite_table_cache[offset] = (byte)(data & 0xff);
                    spriteTouched = true;
                }
            }
            if (spriteTouched)
                InvalidateSpriteRowCache();
        }

        private void UpdateSpriteCacheByte(int addr, byte value)
        {
            int spriteBase = GetSpriteTableBase();
            int spriteSize = GetSpriteTableSize();
            int spriteByteAddr = addr & 0xFFFF;
            if (spriteByteAddr < spriteBase || spriteByteAddr >= spriteBase + spriteSize)
                return;

            EnsureSpriteTableCache();
            int offset = spriteByteAddr - g_sprite_cache_base;
            if ((uint)offset < (uint)g_sprite_table_cache.Length)
                g_sprite_table_cache[offset] = value;
            InvalidateSpriteRowCache();
        }

        private void cram_set(int idx, ushort data)
        {
            // CRAM: only low 12 bits matter (bits 0-11) for 0x0BGR format
            // Mask out high bits to handle byte-write duplication
            ushort cramData = (ushort)(data & 0x0FFF);
            g_cram[idx] = cramData;

            // CRAM format according to Genesis documentation:
            // Bits 0-3: Red (4 bits, but only bits 1-3 used = 3-bit intensity)
            // Bits 4-7: Green (4 bits, but only bits 5-7 used = 3-bit intensity)  
            // Bits 8-11: Blue (4 bits, but only bits 9-11 used = 3-bit intensity)
            // Format: 0x0BGR (Blue in high nibble, Green in middle, Red in low nibble)
            int rNib = (cramData & 0x000F);       // Bits 0-3: Red nibble
            int gNib = (cramData & 0x00F0) >> 4;  // Bits 4-7: Green nibble
            int bNib = (cramData & 0x0F00) >> 8;  // Bits 8-11: Blue nibble
            
            if (TraceConsoleEnabledMemory && TraceCramWrites)
                Console.WriteLine($"[CRAM] frame={_frameCounter} index=0x{(idx & 0x3f):X2} raw=0x{data:X4} masked=0x{cramData:X4} B=0x{bNib:X1} G=0x{gNib:X1} R=0x{rNib:X1}");

            if (TraceConsoleEnabledMemory && TraceCramWritesPc && _cramPcRemaining > 0)
            {
                if (TraceCramWritesPcFrames > 0 && _frameCounter > TraceCramWritesPcFrames)
                    return;
                if (TraceCramWritesPcRanges.Count > 0)
                {
                    uint pcFilter = md_m68k.g_reg_PC;
                    bool inRange = false;
                    foreach ((uint start, uint end) in TraceCramWritesPcRanges)
                    {
                        if (pcFilter >= start && pcFilter <= end)
                        {
                            inRange = true;
                            break;
                        }
                    }
                    if (!inRange)
                        return;
                }
                _cramPcRemaining--;
                uint pc = md_m68k.g_reg_PC;
                Console.WriteLine($"[CRAM-PC] frame={_frameCounter} pc=0x{pc:X6} index=0x{(idx & 0x3f):X2} raw=0x{data:X4} masked=0x{cramData:X4}");
            }
            
            // MD uses 3-bit intensity stored in bits 1..3 of each nibble (values 0,2,4..E)
            int r = rNib >> 1;   // 0..7
            int g = gNib >> 1;   // 0..7
            int b = bNib >> 1;   // 0..7

            g_color[idx] =
            0xff000000u |
            (uint)(COLOR_NORMAL[r]   << 16) |
            (uint)(COLOR_NORMAL[g]   << 8)  |
            (uint)(COLOR_NORMAL[b]);

            g_color_shadow[idx] =
            0xff000000u |
            (uint)(COLOR_SHADOW[r]   << 16) |
            (uint)(COLOR_SHADOW[g]   << 8)  |
            (uint)(COLOR_SHADOW[b]);

            g_color_highlight[idx] =
            0xff000000u |
            (uint)(COLOR_HIGHLIGHT[r] << 16) |
            (uint)(COLOR_HIGHLIGHT[g] << 8)  |
            (uint)(COLOR_HIGHLIGHT[b]);
        }

        private void pattern_chk(int in_address, byte new_byte)
        {
            int  w_address = in_address & 0xfffe;
            // Konstruera nya word-värdet baserat på vilken byte som skrivs
            uint w_val;
            
            if ((in_address & 1) == 0)
            {
                // Writing high byte (even address)
                byte low_byte = g_vram[w_address + 1];
                w_val = (uint)((new_byte << 8) | low_byte);
            }
            else
            {
                // Writing low byte (odd address)  
                byte high_byte = g_vram[w_address];
                w_val = (uint)((high_byte << 8) | new_byte);
            }

            // Log VRAM writes in scroll areas for debugging (gated)
            int scrollA_base = g_vdp_reg_2_scrolla & 0xFFFE;
            int scrollB_base = g_vdp_reg_4_scrollb & 0xFFFE;

            if (TraceVramWrites &&
                ((w_address >= scrollA_base && w_address < scrollA_base + 0x2000) ||
                 (w_address >= scrollB_base && w_address < scrollB_base + 0x1000)))
            {
                MdTracerCore.MdLog.WriteLine(
                    $"[VRAM-WRITE] addr=0x{w_address:X4} val=0x{w_val:X4} " +
                    $"scrollA=0x{scrollA_base:X4} scrollB=0x{scrollB_base:X4} interlace={g_vdp_interlace_mode}");
            }

            // In interlace mode 2, also update the cache
            if (g_vdp_interlace_mode == 2)
            {
                if (w_address >= scrollA_base && w_address < scrollA_base + 0x2000)
                {
                    int scroll_offset = (w_address - scrollA_base) >> 1;
                    g_renderer_vram[0x6000 + scroll_offset] = w_val;
                }
                else if (w_address >= scrollB_base && w_address < scrollB_base + 0x1000)
                {
                    int scroll_offset = (w_address - scrollB_base) >> 1;
                    g_renderer_vram[0x6800 + scroll_offset] = w_val;
                }
            }

            uint w_val_h = ((w_val >> 12) & 0x000f)
            | ((w_val >>  4) & 0x00f0)
            | ((w_val <<  4) & 0x0f00)
            | ((w_val << 12) & 0xf000);

            int w_char;
            int w_addr;
            int wx;
            int wy;

            if (g_vdp_interlace_mode == 2)
            {
                // Interlace mode 2: 64-byte patterns (8x16 cells)
                // Each pattern has 16 rows of 4 bytes
                w_char = (in_address & 0xffc0) >> 6;  // Divide by 64
                w_addr = (in_address & 0xffc0) >> 1;  // 64-byte aligned base address
                wx = (in_address & 0x0002) >> 1;
                wy = (in_address & 0x003c) >> 2;  // 16 rows (0-15)
            }
            else
            {
                // Normal mode: 32-byte patterns (8x8 cells)
                w_char = (in_address & 0xffe0) >> 5;
                w_addr = (in_address & 0xffe0) >> 1;
                wx = (in_address & 0x0002) >> 1;
                wy = (in_address & 0x001f) >> 2;
            }

            // Store pattern data at correct renderer index
            g_renderer_vram[w_address >> 1] = w_val;

            // Debug: log ALL writes to pattern region (0x0000-0x7FFF) for first 100 frames
            if (TracePatternWrites && w_address < 0x8000 && _frameCounter < 100 && w_val != 0)
            {
                int tileIdx = (g_vdp_interlace_mode == 2) ? (w_address >> 6) & 0x1FF : (w_address >> 5) & 0x3FF;
                int rowIdx = (g_vdp_interlace_mode == 2) ? (w_address >> 2) & 0x0F : (w_address >> 2) & 0x07;
                Console.WriteLine($"[PATTERN-WRITE] frame={_frameCounter} vram_addr=0x{w_address:X4} val=0x{w_val:X4} renderer_idx={w_address >> 1} tile={tileIdx} row={rowIdx}");
            }

            if (TracePatternWritesPc && _patternPcRemaining > 0 && w_address < 0x8000 && w_val != 0)
            {
                if (TracePatternWritesPcFrames > 0 && _frameCounter > TracePatternWritesPcFrames)
                    return;
                if (TracePatternWritesPcRanges.Count > 0)
                {
                    uint pcFilter = md_m68k.g_reg_PC;
                    bool inRange = false;
                    foreach ((uint start, uint end) in TracePatternWritesPcRanges)
                    {
                        if (pcFilter >= start && pcFilter <= end)
                        {
                            inRange = true;
                            break;
                        }
                    }
                    if (!inRange)
                        return;
                }
                _patternPcRemaining--;
                uint pc = md_m68k.g_reg_PC;
                int tileIdx = (g_vdp_interlace_mode == 2) ? (w_address >> 6) & 0x1FF : (w_address >> 5) & 0x3FF;
                int rowIdx = (g_vdp_interlace_mode == 2) ? (w_address >> 2) & 0x0F : (w_address >> 2) & 0x07;
                Console.WriteLine($"[PATTERN-PC] frame={_frameCounter} pc=0x{pc:X6} vram_addr=0x{w_address:X4} val=0x{w_val:X4} tile={tileIdx} row={rowIdx}");
            }

            // Store pattern data for reverse mode (normal mode)
            int wy_flip = (g_vdp_interlace_mode == 2) ? (15 - wy) : (7 - wy);
            if (wx == 0)
            {
                g_renderer_vram[VRAM_DATASIZE + w_addr + (wy << 1) + 1]      = w_val_h;
                g_renderer_vram[(VRAM_DATASIZE * 2) + w_addr + (wy_flip << 1)]     = w_val;
                g_renderer_vram[(VRAM_DATASIZE * 3) + w_addr + (wy_flip << 1) + 1] = w_val_h;
            }
            else
            {
                g_renderer_vram[VRAM_DATASIZE + w_addr + (wy << 1)]          = w_val_h;
                g_renderer_vram[(VRAM_DATASIZE * 2) + w_addr + (wy_flip << 1) + 1] = w_val;
                g_renderer_vram[(VRAM_DATASIZE * 3) + w_addr + (wy_flip << 1)]     = w_val_h;
            }

            g_pattern_chk[w_char] = true;
        }
    }
}
