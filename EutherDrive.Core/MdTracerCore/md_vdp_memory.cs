using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        private static readonly bool TraceVramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceCramWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CRAM"), "1", StringComparison.Ordinal);

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

        // Centraliserad felhantering (byt till Debug.WriteLine om du inte vill kasta)
        private static void Error(string where) =>
        throw new InvalidOperationException($"VDP: {where}");

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
            if (in_address <= 0xc00003)
            {
                return ((uint)read16(in_address) << 16) | read16(in_address);
            }
            Error("read32: invalid address");
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
                        _mdVramWritesThisFrame++;
                        vram_write_w(g_vdp_reg_dest_address, in_data);
                        pattern_chk(g_vdp_reg_dest_address,   (byte)(in_data >> 8));
                        pattern_chk(g_vdp_reg_dest_address+1, (byte)(in_data & 0xff));
                        g_vdp_reg_dest_address = (ushort)((g_vdp_reg_dest_address + g_vdp_reg_15_autoinc) & 0xffff);
                        break;

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
                    }
                }
                else
                {
                    // address set (2nd word)
                    g_command_select   = false;
                    g_vdp_reg_code     = (int)((g_command_word >> 14) | ((in_data >> 2) & 0x3c));
                    g_vdp_reg_dest_address = (ushort)((g_command_word & 0x3fff) | ((in_data & 0x0003) << 14));

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
            else
            {
                Error("write16: invalid address");
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

            if (in_address <= 0xc00007)
            {
                write16(in_address, (ushort)(in_data >> 16));
                write16(in_address, (ushort)(in_data & 0xffff));
                return;
            }
            Error("write32: invalid address");
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

        private void pattern_chk(int in_address, byte _)
        {
            int  w_address = in_address & 0xfffe;
            uint w_val     = vram_read_w(w_address);

            uint w_val_h = ((w_val >> 12) & 0x000f)
            | ((w_val >>  4) & 0x00f0)
            | ((w_val <<  4) & 0x0f00)
            | ((w_val << 12) & 0xf000);

            int w_char = (in_address & 0xffe0) >> 5;
            int w_addr = (in_address & 0xffe0) >> 1;
            int wx     = (in_address & 0x0002) >> 1;
            int wy     = (in_address & 0x001f) >> 2;

            g_renderer_vram[w_address >> 1] = w_val;

            if (wx == 0)
            {
                g_renderer_vram[VRAM_DATASIZE + w_addr + (wy << 1) + 1]      = w_val_h;
                g_renderer_vram[(VRAM_DATASIZE * 2) + w_addr + ((7 - wy) << 1)]     = w_val;
                g_renderer_vram[(VRAM_DATASIZE * 3) + w_addr + ((7 - wy) << 1) + 1] = w_val_h;
            }
            else
            {
                g_renderer_vram[VRAM_DATASIZE + w_addr + (wy << 1)]          = w_val_h;
                g_renderer_vram[(VRAM_DATASIZE * 2) + w_addr + ((7 - wy) << 1) + 1] = w_val;
                g_renderer_vram[(VRAM_DATASIZE * 3) + w_addr + ((7 - wy) << 1)]     = w_val_h;
            }

            g_pattern_chk[w_char] = true;
        }
    }
}
