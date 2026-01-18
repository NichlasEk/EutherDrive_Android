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

        // Centraliserad felhantering (strict by default; disable with EUTHERDRIVE_VDP_STRICT=0).
        private static void Error(string where)
        {
            if (StrictVdpAccess)
                throw new InvalidOperationException($"VDP: {where}");
            Debug.WriteLine($"[VDP] {where}");
        }

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
                    ushort status = get_vdp_status();
                    md_m68k.RecordVdpStatusRead(status);
                    ushort postStatus = (ushort)(status & ~VDP_STATUS_VBLANK_MASK);
                    LogStatusRead(status, postStatus);
                    g_vdp_status_7_vinterrupt = 0;
                    md_m68k.g_interrupt_V_req = false;
                    if (md_main.g_masterSystemMode)
                        md_main.g_md_z80?.irq_request(false, "VDP", 0);
                    return status;
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
                g_vdp_status_7_vinterrupt = 0; // ack on status read
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

            // MD VDP-portarna är 16-bit, men 8-bitars writes mappas ofta som repeterat värde
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

                if (!g_command_select)
                {
                    if ((in_data & 0xc000) == 0x8000)
                    {
                        // register write
                        byte rs   = (byte)((in_data >> 8) & 0x1f);
                        byte data = (byte)(in_data & 0xff);
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

                    if (g_vdp_interlace_mode == 2 && _firstInterlace2Frame > 0 && _frameCounter >= _firstInterlace2Frame)
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
                    }

                    if ((g_vdp_reg_code & 0x20) != 0 && g_vdp_reg_1_4_dma == 1)
                    {
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
                    if (!_smsCramWriteLogged)
                    {
                        _smsCramWriteLogged = true;
                        SmsLog("[SMS VDP] CRAM writes currently stubbed");
                    }
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

            if (code == 2)
            {
                int reg = (cmd >> 8) & 0x0F;
                byte data = (byte)(cmd & 0xFF);
                _smsRegs[reg] = data;
                if (reg == 1 && !_smsDisplayOnLogged && (data & 0x40) != 0)
                {
                    _smsDisplayOnLogged = true;
                    MdTracerCore.MdLog.WriteLine("[SMS VDP] display enabled (reg1 bit6)");
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
            g_cram[idx] = data;
            if (TraceCramWrites)
                Console.WriteLine($"[CRAM] frame={_frameCounter} index=0x{(idx & 0x3f):X2} data=0x{data:X4}");

            int r = (data & 0x000e) >> 1;
            int g = (data & 0x00e0) >> 5;
            int b = (data & 0x0e00) >> 9;

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

            // Log VRAM writes in scroll areas for debugging
            int scrollA_base = g_vdp_reg_2_scrolla & 0xFFFE;
            int scrollB_base = g_vdp_reg_4_scrollb & 0xFFFE;

            if ((w_address >= scrollA_base && w_address < scrollA_base + 0x2000) ||
                (w_address >= scrollB_base && w_address < scrollB_base + 0x1000))
            {
                var logLine = $"[VRAM-WRITE] addr=0x{w_address:X4} val=0x{w_val:X4} scrollA=0x{scrollA_base:X4} scrollB=0x{scrollB_base:X4} interlace={g_vdp_interlace_mode}\n";
                System.IO.File.AppendAllText("/tmp/eutherdrive.log", logLine);
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
            if (w_address < 0x8000 && _frameCounter < 100 && w_val != 0)
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
