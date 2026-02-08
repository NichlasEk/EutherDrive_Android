using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        private static readonly bool TraceVramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceCramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CRAM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpCtrlAll =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_CTRL"), "1", StringComparison.Ordinal);
        private static readonly int TraceVdpCtrlLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_CTRL_LIMIT", 200);
        private static readonly bool TracePatternWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES"), "1", StringComparison.Ordinal);
        private static readonly bool GateCpuWritesDuringDma =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DMA_WRITE_GATE"), "1", StringComparison.Ordinal);
        private static readonly bool DisableDmaWriteGate =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DMA_WRITE_GATE_DISABLE"), "1", StringComparison.Ordinal);
        private static readonly bool StrictVdpAccess =
            ReadEnvDefaultOn("EUTHERDRIVE_VDP_STRICT");

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

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
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
                g_command_select = false;
                switch (g_vdp_reg_code)
                {
                    case 0:
                        w_out = vram_read_w(g_vdp_reg_dest_address);
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
                w_out = get_vdp_status();
                md_m68k.RecordVdpStatusRead(w_out);
                ushort postStatus = (ushort)(w_out & ~VDP_STATUS_VBLANK_MASK);
                LogStatusRead(w_out, postStatus);
                if (TraceStatusRead)
                {
                    Console.WriteLine($"[VDP-STATUS-RD] frame={_frameCounter} status=0x{w_out:X4} vblank={((w_out & VDP_STATUS_VBLANK_MASK) != 0 ? 1 : 0)} overflow={((w_out & 0x0040) != 0 ? 1 : 0)} collision={((w_out & 0x0020) != 0 ? 1 : 0)}");
                }
                g_vdp_status_7_vinterrupt = 0; // ack on status read
                g_vdp_status_6_sprite = 0;
                g_vdp_status_5_collision = 0;
                md_m68k.g_interrupt_V_req = false;
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
            byte status = 0;
            if (g_vdp_status_3_vbrank != 0)
                status |= 0x80;
            if (g_vdp_status_6_sprite != 0)
                status |= 0x40;
            if (g_vdp_status_5_collision != 0)
                status |= 0x20;

            // Reading status clears VBlank + collision (SMS behavior).
            g_vdp_status_3_vbrank = 0;
            g_vdp_status_5_collision = 0;
            g_vdp_status_7_vinterrupt = 0;
            md_m68k.g_interrupt_V_req = false;
            md_main.g_md_vdp?.OnSmsStatusRead();
            return status;
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
                    SmsWriteData(in_data);
                    return;
                }
                if (port == 0x04)
                {
                    SmsWriteControl(in_data);
                    return;
                }
            }

            in_address &= 0x00FF_FFFF;

            if (in_address <= 0xc00003)
            {
                _mdDataWritesThisFrame++;
                RecordDataPortWriteCode(g_vdp_reg_code);

                bool dmaActive = g_vdp_status_1_dma != 0 || g_dma_mode != 0 || g_dma_leng > 0;
                if (GateCpuWritesDuringDma && !DisableDmaWriteGate && dmaActive)
                {
                    _mdDataWritesDroppedThisFrame++;
                    if ((g_vdp_reg_code & 0x0f) == 1)
                        _mdVramWritesDroppedThisFrame++;
                    return;
                }

                if (g_dma_fill_req)
                {
                    g_dma_fill_req = false;
                    ushort fillWord = (ushort)((in_data << 8) | in_data);
                    dma_run_fill_req(fillWord);
                    return;
                }

                int writeAddr = g_vdp_reg_dest_address;

                switch (g_vdp_reg_code & 0x0f)
                {
                    case 1: // VRAM byte write
                    {
                        int vramIndex = ((writeAddr & 1) == 0) ? writeAddr : (writeAddr ^ 1);
                        g_vram[vramIndex & 0xFFFF] = in_data;
                        pattern_chk(writeAddr, in_data);

                        int wordAddr = writeAddr & 0xFFFE;
                        ushort wordVal = vram_read_w(wordAddr);
                        TraceVdpDataWrite(in_address, wordVal, g_vdp_reg_code, writeAddr, g_vdp_reg_15_autoinc);
                        TraceDataPortWindowWrite(in_address, wordVal);
                        this.RecordVramWriteForTracking(wordAddr, wordVal);
                        this.TrackScrollRegionWrite(wordAddr & 0xFFFF);
                        this.LogVramWrite("CPU8", wordAddr & 0xFFFF, wordVal, g_vdp_reg_15_autoinc, g_vdp_reg_code);
                        g_vdp_reg_dest_address = (ushort)((writeAddr + g_vdp_reg_15_autoinc) & 0xffff);
                        break;
                    }

                    case 3: // CRAM byte write
                    {
                        _mdCramWritesThisFrame++;
                        TraceVdpDataWrite(in_address, (ushort)((in_data << 8) | in_data), g_vdp_reg_code, writeAddr, g_vdp_reg_15_autoinc);
                        TraceDataPortWindowWrite(in_address, (ushort)((in_data << 8) | in_data));
                        int col = (writeAddr >> 1) & 0x3f;
                        ushort existing = g_cram[col];
                        ushort next = (ushort)((writeAddr & 1) == 0
                            ? ((in_data << 8) | (existing & 0x00ff))
                            : ((existing & 0xff00) | in_data));
                        cram_set(col, next);
                        g_vdp_reg_dest_address = (ushort)((writeAddr + g_vdp_reg_15_autoinc) & 0xffff);
                        break;
                    }

                    case 5: // VSRAM byte write
                    {
                        TraceVdpDataWrite(in_address, (ushort)((in_data << 8) | in_data), g_vdp_reg_code, writeAddr, g_vdp_reg_15_autoinc);
                        TraceDataPortWindowWrite(in_address, (ushort)((in_data << 8) | in_data));
                        int waddr = (writeAddr >> 1) & 0x3f;
                        if (waddr < 40)
                        {
                            ushort existing = g_vsram[waddr];
                            g_vsram[waddr] = (ushort)((writeAddr & 1) == 0
                                ? ((in_data << 8) | (existing & 0x00ff))
                                : ((existing & 0xff00) | in_data));
                        }
                        g_vdp_reg_dest_address = (ushort)((writeAddr + g_vdp_reg_15_autoinc) & 0xffff);
                        break;
                    }

                    default:
                        break;
                }

                g_vdp_reg_dest_address = (ushort)((writeAddr + g_vdp_reg_15_autoinc) & 0xffff);
                return;
            }

            // Default: mirror byte to both halves and use 16-bit path.
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
                _mdDataWritesThisFrame++;
                RecordDataPortWriteCode(g_vdp_reg_code);

                bool dmaActive = g_vdp_status_1_dma != 0 || g_dma_mode != 0 || g_dma_leng > 0;
                if (GateCpuWritesDuringDma && !DisableDmaWriteGate && dmaActive)
                {
                    _mdDataWritesDroppedThisFrame++;
                    if ((g_vdp_reg_code & 0x0f) == 1)
                        _mdVramWritesDroppedThisFrame++;
                    return;
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

                switch (g_vdp_reg_code & 0x0f)
                {
                    case 1: // VRAM write
                    {
                        int writeAddr = g_vdp_reg_dest_address;
                        vram_write_w(writeAddr, in_data);
                        pattern_chk(writeAddr, (byte)(in_data >> 8));
                        pattern_chk(writeAddr + 1, (byte)(in_data & 0xff));
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
                    int codeMask = g_vdp_reg_1_4_dma == 1 ? 0x3C : 0x1C;
                    g_vdp_reg_dest_address = (ushort)((g_vdp_reg_dest_address & 0x3fff) | ((in_data & 0x0007) << 14));
                    g_vdp_reg_code = (g_vdp_reg_code & ~codeMask) | ((in_data >> 2) & codeMask);

                    // Log VDP command decoding (gated)
                    if (TraceVdpCtrlAll && _vdpCtrlLogRemaining > 0 && _frameCounter < 100) // Only log early frames
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

                    if ((g_vdp_reg_code & 0x20) != 0 && g_vdp_reg_1_4_dma == 1)
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
            byte value = _smsVram[_smsVdpAddr & 0x3FFF];
            if (_smsVdpCode == 1)
            {
                _smsVdpAddr = (_smsVdpAddr + 1) & 0x3FFF;
            }
            return value;
        }

        private void SmsWriteControl(byte value)
        {
            if (!_smsCommandPending)
            {
                _smsCommandLow = value;
                _smsCommandPending = true;
                return;
            }

            _smsCommandPending = false;
            ushort cmd = (ushort)(_smsCommandLow | (value << 8));
            SmsLogControl(_smsCommandLow, value, cmd);
            SmsDecodeCommand(cmd);
        }

        private void SmsWriteData(byte value)
        {
            switch (_smsVdpCode)
            {
                case 0:
                case 1:
                    _smsVramWritesTotal++;
                    _smsVram[_smsVdpAddr & 0x3FFF] = value;
                    _smsVdpAddr = (_smsVdpAddr + 1) & 0x3FFF;
                    return;

                case 3:
                    _smsCramWritesTotal++;
                    int cramAddr = _smsVdpAddr & 0x1F;
                    _smsCram[cramAddr] = value;
                    _smsVdpAddr = (_smsVdpAddr + 1) & 0x3FFF;
                    SmsUpdatePalette(cramAddr, value);
                    return;

                default:
                    if (!_smsDataIgnoredLogged || _smsVdpCode != 0)
                    {
                        _smsDataIgnoredLogged = true;
                        SmsLog($"[SMS VDP] data write ignored code={_smsVdpCode} val=0x{value:X2}");
                    }
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
                _smsRegs[reg] = data;
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
                if (reg == 1 && md_main.g_masterSystemMode)
                {
                    bool mode224 = (data & 0x08) != 0;
                    g_display_ysize = mode224 ? 224 : 192;
                    g_display_ycell = mode224 ? 28 : 24;
                    g_vertical_line_max = 262;
                    UpdateOutputWidth();
                }
                SmsLog($"[SMS VDP] REG r{reg:X}={data:X2}", reg == 1);
                return;
            }

            _smsVdpCode = code;
            _smsVdpAddr = cmd & 0x3FFF;
            SmsLog($"[SMS VDP] CMD code={code} addr=0x{_smsVdpAddr:X4} raw=0x{cmd:X4}");
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
        (ushort)((g_vram[addr] << 8) | g_vram[(addr + 1) & 0xffff]);

        private void vram_write_w(int addr, ushort data)
        {
            // MDs byte-swap på VRAM-porten: lågbyte går till “addr ^ 1”
            g_vram[addr] = (byte)(data >> 8);
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
            int spriteWordAddr = addr & 0xFFFE;
            if (spriteWordAddr >= spriteBase && spriteWordAddr < spriteBase + spriteSize)
            {
                EnsureSpriteTableCache();
                int offset = spriteWordAddr - g_sprite_cache_base;
                if ((uint)offset < (uint)g_sprite_table_cache.Length)
                {
                    g_sprite_table_cache[offset] = (byte)(data >> 8);
                    if ((uint)(offset + 1) < (uint)g_sprite_table_cache.Length)
                        g_sprite_table_cache[offset + 1] = (byte)(data & 0xff);
                }
                InvalidateSpriteRowCache();
            }
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
            
            if (TraceCramWrites || _frameCounter < 10) // Always log first 10 frames
                Console.WriteLine($"[CRAM] frame={_frameCounter} index=0x{(idx & 0x3f):X2} raw=0x{data:X4} masked=0x{cramData:X4} B=0x{bNib:X1} G=0x{gNib:X1} R=0x{rNib:X1}");
            
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
