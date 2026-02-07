using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        private static readonly bool TraceDmaSourceReads =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DMA_SRC"), "1", StringComparison.Ordinal);
        private static readonly int TraceDmaSourceReadLimit =
            ParseDmaTraceLimit("EUTHERDRIVE_TRACE_DMA_SRC_LIMIT", 128);
        [NonSerialized] private int _traceDmaSourceRemaining;
        [NonSerialized] private long _traceDmaSourceFrame = -1;
        private int g_dma_mode;
        private uint g_dma_src_addr;
        private int g_dma_leng;
        private bool g_dma_fill_req;
        private ushort g_dma_fill_data;
        private bool g_dma_immediate_complete = true;

        private static int ParseDmaTraceLimit(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            return value <= 0 ? int.MaxValue : value;
        }

        private static string ClassifyDmaSourceRegion(uint address)
        {
            if (address <= 0x3FFFFF)
                return "ROM";
            if (address >= 0xE00000)
                return "RAM";
            if (address >= 0xA00000 && address <= 0xA0FFFF)
                return "Z80";
            if (address >= 0xC00000 && address <= 0xDFFFFF)
                return "VDP";
            return "OTHER";
        }

        private void FinishDmaTransfer(int mode)
        {
            g_dma_mode = 0;
            g_dma_leng = 0;
            g_vdp_status_1_dma = 0;
            g_vdp_status_8_full = 0;
            write_dma_leng();
            switch (mode)
            {
                case 1:
                    write_dma_src_addr(g_dma_src_addr >> 1);
                    break;
                case 3:
                    write_dma_src_addr(g_dma_src_addr);
                    break;
            }
        }

        public int dma_status_update()
        {
            int w_clock = 0;
            int w_tran = 0;
            
            // With immediate DMA (otheremumdemu style), dma_status_update()
            // only needs to handle DMA timing/cleanup if DMA is still active
            // But with our immediate DMA implementation, DMA completes immediately
            // So this function should return 0 clock cost
            
            // However, we need to handle the case where g_dma_immediate_complete = false
            // (DMA runs over time). But we're using immediate DMA style.
            
            // For compatibility, if DMA is still active (shouldn't happen with immediate DMA),
            // we'll just clear it
            if (g_dma_mode != 0 && g_dma_leng > 0)
            {
                // DMA should have completed immediately, but if not, finish it
                Console.WriteLine($"[DMA-WARNING] DMA still active in dma_status_update: mode={g_dma_mode} len={g_dma_leng}");
                FinishDmaTransfer(g_dma_mode);
            }
            
            return w_clock;
        }

        private void dma_run_memory_req()
        {
            uint srcWordAddr = read_dma_src_addr();
            uint srcHigh = g_vdp_reg_23_5_dma_high;
            ushort srcLow = (ushort)((g_vdp_reg_22_dma_source_mid << 8) | g_vdp_reg_21_dma_source_low);
            g_dma_src_addr = (srcHigh << 17) | ((uint)srcLow << 1);
            g_dma_leng = read_dma_leng();
            g_dma_mode = 1;
            g_vdp_status_1_dma = 1;
            g_vdp_status_8_full = 1;
            
            // Log DMA start in exact format for analysis
            string startTarget = "UNKNOWN";
            switch (g_vdp_reg_code & 0x0f)
            {
                case 1: startTarget = "VRAM"; break;
                case 3: startTarget = "CRAM"; break;
                case 5: startTarget = "VSRAM"; break;
            }

            if (TraceDmaSourceReads)
            {
                _traceDmaSourceRemaining = TraceDmaSourceReadLimit;
                _traceDmaSourceFrame = _frameCounter;
                string region = ClassifyDmaSourceRegion(g_dma_src_addr);
                Console.WriteLine(
                    $"[DMA-SRC-TRACE-START] frame={_frameCounter} srcWord=0x{srcWordAddr:X6} srcByte=0x{g_dma_src_addr:X6} " +
                    $"region={region} len=0x{g_dma_leng:X4} dest=0x{g_vdp_reg_dest_address:X4} code=0x{g_vdp_reg_code:X2}");
            }
            
            if (TraceDmaRegs)
            {
                Console.WriteLine($"[DMA-START] reason=CMD addr=0x{g_vdp_reg_dest_address:X4} cd=0x{g_vdp_reg_code:X2} " +
                                 $"len=0x{g_dma_leng:X4} srcRegs=(15=0x{g_vdp_reg_21_dma_source_low:X2} 16=0x{g_vdp_reg_22_dma_source_mid:X2} 17=0x{g_vdp_reg_23_5_dma_high:X2}) " +
                                 $"srcWord=0x{srcWordAddr:X6} srcByte=0x{g_dma_src_addr:X6} inc=0x{g_vdp_reg_15_autoinc:X2} mode={g_vdp_reg_23_dma_mode} target={startTarget}");
            }

            // Log DMA copy operation with detailed info
            string target = "UNKNOWN";
            switch (g_vdp_reg_code & 0x0f)
            {
                case 1: target = "VRAM"; break;
                case 3: target = "CRAM"; break;
                case 5: target = "VSRAM"; break;
            }
            
            // Enhanced logging for Predator 2 debugging
            ushort reg21 = g_vdp_reg_21_dma_source_low;
            ushort reg22 = g_vdp_reg_22_dma_source_mid;
            ushort reg23_5 = g_vdp_reg_23_5_dma_high;
            
            if (TraceDmaRegs)
                LogDmaStatusLine($"[DMA-COPY-DETAIL] frame={_frameCounter} len={g_dma_leng} srcWord=0x{srcWordAddr:X6} srcByte=0x{g_dma_src_addr:X6} dest=0x{g_vdp_reg_dest_address:X4} target={target} reg15=0x{g_vdp_reg_15_autoinc:X2} regCode=0x{g_vdp_reg_code:X2} regs=0x{reg23_5:X2}{reg22:X2}{reg21:X2}");
            
            // Enhanced Predator 2 debugging: log raw register values and computed addresses
            if (TraceDmaRegs)
                Console.WriteLine($"[DMA-DEBUG-RAW] frame={_frameCounter} reg21=0x{reg21:X2} reg22=0x{reg22:X2} reg23_5=0x{reg23_5:X2} srcWordAddr=0x{srcWordAddr:X6} srcByteAddr=0x{g_dma_src_addr:X6} (srcWord<<1)");
            
            // Special handling for length=0 (means 0x10000 words on real VDP)
            int actualLength = g_dma_leng;
            if (actualLength == 0)
            {
                actualLength = 0x10000;
                if (TraceDmaRegs)
                    Console.WriteLine($"[DMA-LENGTH-ZERO] frame={_frameCounter} Using length=0x{actualLength:X4} instead of 0");
            }
            
            // Debug: dump first 32 bytes from source AND destination before/after
            if (TraceDmaRegs && _frameCounter >= 560 && _frameCounter < 600) // Log frames around title screen transition
            {
                // Dump source data
                Console.Write($"[DMA-SRC-DUMP] srcByte=0x{g_dma_src_addr:X6}: ");
                for (int i = 0; i < 32 && i < actualLength * 2; i += 2)
                {
                    ushort val = ReadDmaSourceWord((uint)(g_dma_src_addr + i));
                    Console.Write($"{val:X4} ");
                }
                Console.WriteLine();
                
                // Dump destination BEFORE DMA (if VRAM/CRAM/VSRAM)
                if (target == "VRAM")
                {
                    Console.Write($"[DMA-DEST-BEFORE] VRAM 0x{g_vdp_reg_dest_address:X4}: ");
                    for (int i = 0; i < 16 && i < actualLength; i++)
                    {
                        ushort val = vram_read_w(g_vdp_reg_dest_address + i * 2);
                        Console.Write($"{val:X4} ");
                    }
                    Console.WriteLine();
                }
            }
            
            // Debug DMA window check for name table overlap
            DebugDmaWindow((uint)g_vdp_reg_dest_address, (uint)g_dma_leng, (byte)g_vdp_reg_code, "DMA-COPY");

            // Like otheremumdemu: DMA runs immediately
            // Handle length=0 special case (means 0x10000 words on real VDP)
            int w_loop_cnt = g_dma_leng;
            if (w_loop_cnt == 0)
            {
                w_loop_cnt = 0x10000;
            }
            int debugWriteCount = 0;
            
            while (true)
            {
                // Read from source (byte address) with 128KiB wrap bug (low word wraps, high stays)
                uint srcByteAddr = (srcHigh << 17) | ((uint)srcLow << 1);
                g_dma_src_addr = srcByteAddr;
                ushort w_val = ReadDmaSourceWord(srcByteAddr);
                int writeAddr = g_vdp_reg_dest_address;
                
                switch (g_vdp_reg_code & 0x0f)
                {
                    case 1: // VRAM
                        vram_write_w(writeAddr, w_val);
                        pattern_chk(writeAddr, (byte)(w_val >> 8));
                        pattern_chk(writeAddr + 1, (byte)(w_val & 0xff));
                        this.RecordVramWriteForTracking(writeAddr, w_val);
                        this.TrackScrollRegionWrite(writeAddr & 0xFFFF);
                        this.LogVramWrite("DMA", writeAddr, w_val, g_vdp_reg_15_autoinc, g_vdp_reg_code);
                        
                        // Debug logging
                        if (debugWriteCount < 8 && _frameCounter < 100)
                        {
                            Console.WriteLine($"[DMA-WRITE-{debugWriteCount}] addr=0x{writeAddr:X4} val=0x{w_val:X4} reg15=0x{g_vdp_reg_15_autoinc:X2} nextAddr=0x{(writeAddr + g_vdp_reg_15_autoinc):X4} src=0x{g_dma_src_addr:X6}");
                            debugWriteCount++;
                        }
                        break;
                    case 3: // CRAM
                        cram_set((writeAddr >> 1) & 0x3f, w_val);
                        // Debug logging for CRAM
                        if (debugWriteCount < 8 && _frameCounter < 100)
                        {
                            Console.WriteLine($"[DMA-CRAM-WRITE-{debugWriteCount}] color={(writeAddr >> 1) & 0x3f} val=0x{w_val:X4} masked=0x{(w_val & 0x0FFF):X4} reg15=0x{g_vdp_reg_15_autoinc:X2}");
                            debugWriteCount++;
                        }
                        break;
                    case 5: // VSRAM
                        int waddr = (writeAddr >> 1) & 0x3f;
                        if (waddr < 40)
                            g_vsram[waddr] = w_val;
                        break;
                }
                
                srcLow = (ushort)(srcLow + 1);
                g_vdp_reg_dest_address = (ushort)(writeAddr + g_vdp_reg_15_autoinc);
                
                // Decrement and wrap: --length, length &= 0xFFFF
                w_loop_cnt--;
                w_loop_cnt &= 0xFFFF;
                
                // Check if done: length != 0
                if (w_loop_cnt == 0)
                    break;
            }

            g_dma_src_addr = (srcHigh << 17) | ((uint)srcLow << 1);
            
            // DMA complete
            FinishDmaTransfer(1);
        }

        private void dma_run_fill_req(ushort in_data)
        {
            g_dma_leng = read_dma_leng();
            g_dma_fill_data = in_data;
            g_dma_mode = 2;
            g_vdp_status_1_dma = 1;
            g_vdp_status_8_full = 1;

            // Log DMA fill operation with detailed address info
            byte w_fill_data = (byte)((g_dma_fill_data >> 8) & 0x00ff);
            ushort startAddr = (ushort)(g_vdp_reg_dest_address & 0xffff);
            ushort endAddr = (ushort)((startAddr + (g_dma_leng - 1) * g_vdp_reg_15_autoinc) & 0xffff);
            
            // Enhanced logging for DMA FILL debugging
            LogDmaStatusLine($"[DMA-FILL-DETAIL] frame={_frameCounter} len={g_dma_leng} fillWord=0x{g_dma_fill_data:X4} dest=0x{startAddr:X4} inc={g_vdp_reg_15_autoinc} end=0x{endAddr:X4} regCode=0x{g_vdp_reg_code:X2}");
            
            // Debug DMA window check for name table overlap
            DebugDmaWindow((uint)startAddr, (uint)g_dma_leng, (byte)g_vdp_reg_code, "DMA-FILL");

            // Like otheremumdemu: DMA FILL runs immediately
            int w_loop_cnt = g_dma_leng;
            int debugWriteCount = 0;
            ushort currentAddr = startAddr;
            
            while (true)
            {
                // Write fill word to destination
                switch (g_vdp_reg_code & 0x0f)
                {
                    case 1: // VRAM
                        vram_write_w(currentAddr, g_dma_fill_data);
                        pattern_chk(currentAddr, (byte)(g_dma_fill_data >> 8));
                        pattern_chk(currentAddr + 1, (byte)(g_dma_fill_data & 0xff));
                        this.RecordVramWriteForTracking(currentAddr, g_dma_fill_data);
                        this.TrackScrollRegionWrite(currentAddr & 0xFFFF);
                        this.LogVramWrite("DMA-FILL", currentAddr, g_dma_fill_data, g_vdp_reg_15_autoinc, g_vdp_reg_code);
                        
                        // Debug logging for first 8 writes
                        if (debugWriteCount < 8 && _frameCounter < 100)
                        {
                            Console.WriteLine($"[DMA-FILL-WRITE-{debugWriteCount}] addr=0x{currentAddr:X4} val=0x{g_dma_fill_data:X4} reg15=0x{g_vdp_reg_15_autoinc:X2} nextAddr=0x{(currentAddr + g_vdp_reg_15_autoinc):X4}");
                            debugWriteCount++;
                        }
                        break;
                    case 3: // CRAM
                        cram_set((currentAddr >> 1) & 0x3f, g_dma_fill_data);
                        break;
                    case 5: // VSRAM
                        int waddr = (currentAddr >> 1) & 0x3f;
                        if (waddr < 40)
                            g_vsram[waddr] = g_dma_fill_data;
                        break;
                }
                
                // Increment address with auto-increment
                currentAddr = (ushort)((currentAddr + g_vdp_reg_15_autoinc) & 0xffff);
                
                // Decrement and wrap: --length, length &= 0xFFFF
                w_loop_cnt--;
                w_loop_cnt &= 0xFFFF;
                
                // Check if done: length != 0
                if (w_loop_cnt == 0)
                    break;
            }
            
            // DMA complete
            FinishDmaTransfer(2);
        }

        private void dma_run_copy_req()
        {
            g_dma_src_addr = read_dma_src_addr() & 0xffff;
            g_dma_leng = read_dma_leng();
            g_dma_mode = 3;
            g_vdp_status_1_dma = 1;
            g_vdp_status_8_full = 1;
            
            // Enhanced logging for DMA COPY debugging
            ushort startAddr = (ushort)(g_vdp_reg_dest_address & 0xffff);
            ushort endAddr = (ushort)((startAddr + (g_dma_leng - 1) * g_vdp_reg_15_autoinc) & 0xffff);
            LogDmaStatusLine($"[DMA-COPY-VRAM-DETAIL] frame={_frameCounter} len={g_dma_leng} src=0x{g_dma_src_addr:X4} dest=0x{startAddr:X4} inc={g_vdp_reg_15_autoinc} end=0x{endAddr:X4} regCode=0x{g_vdp_reg_code:X2}");
            
            // Debug: dump first 16 words from source
            if (_frameCounter < 100) // Only log early frames
            {
                Console.Write($"[DMA-COPY-SRC-DUMP] src=0x{g_dma_src_addr:X4}: ");
                for (int i = 0; i < 32 && i < g_dma_leng * 2; i += 2)
                {
                    ushort val = g_vram[(g_dma_src_addr + i) & 0xFFFF];
                    Console.Write($"{val:X4} ");
                }
                Console.WriteLine();
            }
            
            // Debug DMA window check for name table overlap
            DebugDmaWindow((uint)startAddr, (uint)g_dma_leng, (byte)g_vdp_reg_code, "DMA-COPY-VRAM");

            // Like otheremumdemu: DMA COPY runs immediately
            int w_loop_cnt = g_dma_leng;
            int debugWriteCount = 0;
            ushort currentSrc = (ushort)g_dma_src_addr;
            ushort currentDest = startAddr;
            
            while (true)
            {
                // Read from source VRAM
                ushort w_val = g_vram[currentSrc & 0xFFFF];
                
                // Write to destination VRAM
                vram_write_w(currentDest, w_val);
                pattern_chk(currentDest, (byte)(w_val >> 8));
                pattern_chk(currentDest + 1, (byte)(w_val & 0xff));
                this.RecordVramWriteForTracking(currentDest, w_val);
                this.TrackScrollRegionWrite(currentDest & 0xFFFF);
                this.LogVramWrite("DMA-COPY-VRAM", currentDest, w_val, g_vdp_reg_15_autoinc, g_vdp_reg_code);
                
                // Debug logging for first 8 writes
                if (debugWriteCount < 8 && _frameCounter < 100)
                {
                    Console.WriteLine($"[DMA-COPY-WRITE-{debugWriteCount}] src=0x{currentSrc:X4} dest=0x{currentDest:X4} val=0x{w_val:X4} reg15=0x{g_vdp_reg_15_autoinc:X2}");
                    debugWriteCount++;
                }
                
                // Increment source and destination with auto-increment
                currentSrc = (ushort)((currentSrc + g_vdp_reg_15_autoinc) & 0xffff);
                currentDest = (ushort)((currentDest + g_vdp_reg_15_autoinc) & 0xffff);
                
                // Decrement and wrap: --length, length &= 0xFFFF
                w_loop_cnt--;
                w_loop_cnt &= 0xFFFF;
                
                // Check if done: length != 0
                if (w_loop_cnt == 0)
                    break;
            }
            
            // DMA complete
            FinishDmaTransfer(3);
        }

        private uint read_dma_src_addr()
        {
            return (uint)(g_vdp_reg_21_dma_source_low
                        + (g_vdp_reg_22_dma_source_mid << 8)
                        + (g_vdp_reg_23_5_dma_high << 16));
        }

        private ushort ReadDmaSourceWord(uint address)
        {
            var bus = md_main.g_md_bus;
            ushort value = bus != null ? bus.read16(address) : md_m68k.read16(address);
            if (TraceDmaSourceReads && _traceDmaSourceRemaining > 0)
            {
                if (_traceDmaSourceRemaining != int.MaxValue)
                    _traceDmaSourceRemaining--;
                string region = ClassifyDmaSourceRegion(address);
                Console.WriteLine(
                    $"[DMA-SRC-READ] frame={_frameCounter} addr=0x{address:X6} region={region} val=0x{value:X4} " +
                    $"dest=0x{g_vdp_reg_dest_address:X4} code=0x{g_vdp_reg_code:X2} dmaMode={g_dma_mode}");
            }
            return value;
        }

        private void write_dma_src_addr(uint in_addr)
        {
            g_vdp_reg_21_dma_source_low = (byte)(in_addr & 0x00ff);
            g_vdp_reg_22_dma_source_mid = (byte)(in_addr >> 8);
            g_vdp_reg_23_5_dma_high = (byte)(in_addr >> 16);
        }

        private int read_dma_leng()
        {
            int out_ling = (g_vdp_reg_19_dma_counter_low
                    + (g_vdp_reg_20_dma_counter_high << 8));
            return out_ling;
        }

        private void write_dma_leng()
        {
            g_vdp_reg_19_dma_counter_low = (byte)(g_dma_leng & 0x00ff);
            g_vdp_reg_20_dma_counter_high = (byte)(g_dma_leng >> 8);
        }
    }
}
