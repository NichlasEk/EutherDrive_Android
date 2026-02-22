using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryu64.MIPS
{
    public class Memory
    {
        private delegate void MemoryEvent();
        private static readonly bool StrictDataTlb =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_LOOSE_DATA_TLB"), "1", StringComparison.Ordinal);
        private static readonly bool AllowDirectLowPhysicalWindow =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_STRICT_LOWSEG"), "1", StringComparison.Ordinal);
        private static readonly bool TraceN64Io =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_IO"), "1", StringComparison.Ordinal);

        public readonly byte[] SP_DMEM_RW         = new byte[0x1000];
        public readonly byte[] SP_IMEM_RW         = new byte[0x1000];
        public readonly byte[] SP_STATUS_REG_R    = new byte[4];
        public readonly byte[] SP_STATUS_REG_W    = new byte[4];
        public readonly byte[] SP_DMA_BUSY_REG_R  = new byte[4];
        public readonly byte[] SP_DMA_BUSY_REG_W  = new byte[4];
        public readonly byte[] SP_SEMAPHORE_REG_R = new byte[4];
        public readonly byte[] SP_SEMAPHORE_REG_W = new byte[4];
        public readonly byte[] SP_PC_REG_RW       = new byte[4];

        public readonly byte[] DPC_STATUS_REG_R = new byte[4];
        public readonly byte[] DPC_STATUS_REG_W = new byte[4];

        public readonly byte[] MI_INIT_MODE_REG_R = new byte[4];
        public readonly byte[] MI_INIT_MODE_REG_W = new byte[4];
        public readonly byte[] MI_VERSION_REG_RW  = new byte[4];
        public readonly byte[] MI_INTR_REG_R      = new byte[4];
        public readonly byte[] MI_INTR_MASK_REG_R = new byte[4];
        public readonly byte[] MI_INTR_MASK_REG_W = new byte[4];

        public readonly byte[] VI_STATUS_REG_RW  = new byte[4];
        public readonly byte[] VI_ORIGIN_REG_RW  = new byte[4];
        public readonly byte[] VI_WIDTH_REG_RW   = new byte[4];
        public readonly byte[] VI_INTR_REG_RW    = new byte[4];
        public readonly byte[] VI_CURRENT_REG_RW = new byte[4];
        public readonly byte[] VI_BURST_REG_RW   = new byte[4];
        public readonly byte[] VI_V_SYNC_REG_RW  = new byte[4];
        public readonly byte[] VI_H_SYNC_REG_RW  = new byte[4];
        public readonly byte[] VI_LEAP_REG_RW    = new byte[4];
        public readonly byte[] VI_H_START_REG_RW = new byte[4];
        public readonly byte[] VI_V_START_REG_RW = new byte[4];
        public readonly byte[] VI_V_BURST_REG_RW = new byte[4];
        public readonly byte[] VI_X_SCALE_REG_RW = new byte[4];
        public readonly byte[] VI_Y_SCALE_REG_RW = new byte[4];

        public readonly byte[] AI_DRAM_ADDR_REG_W = new byte[4];
        public readonly byte[] AI_LEN_REG_RW      = new byte[4];
        public readonly byte[] AI_CONTROL_REG_W   = new byte[4];
        public readonly byte[] AI_STATUS_REG_R    = new byte[4];
        public readonly byte[] AI_STATUS_REG_W    = new byte[4];
        public readonly byte[] AI_DACRATE_REG_W   = new byte[4];
        public readonly byte[] AI_BITRATE_REG_W   = new byte[4];

        public readonly byte[] PI_DRAM_ADDR_REG_RW    = new byte[4];
        public readonly byte[] PI_CART_ADDR_REG_RW    = new byte[4];
        public readonly byte[] PI_RD_LEN_REG_RW       = new byte[4];
        public readonly byte[] PI_WR_LEN_REG_RW       = new byte[4];
        public readonly byte[] PI_STATUS_REG_R        = new byte[4];
        public readonly byte[] PI_STATUS_REG_W        = new byte[4];
        public readonly byte[] PI_BSD_DOM1_LAT_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM1_PWD_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM1_PGS_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM1_RLS_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_LAT_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_PWD_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_PGS_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_RLS_REG_RW = new byte[4];

        public readonly byte[] SI_DRAM_ADDR_REG_RW      = new byte[4];
        public readonly byte[] SI_PIF_ADDR_RD64B_REG_RW = new byte[4];
        public readonly byte[] SI_PIF_ADDR_WR64B_REG_RW = new byte[4];
        public readonly byte[] SI_STATUS_REG_R          = new byte[4];
        public readonly byte[] SI_STATUS_REG_W          = new byte[4];
        private readonly byte[] SI_MIRROR_RAM           = new byte[0x20000];

        public readonly byte[] RI_SELECT_REG_RW = new byte[4];

        public readonly byte[] RDRAM     = new byte[8388608];
        public readonly byte[] RDRAMReg  = new byte[1048576];
        public readonly byte[] PIFROM    = new byte[1984];
        public readonly byte[] PIFRAM    = new byte[64];
        private readonly byte[] OpenBus  = new byte[4];
        private uint _openBusMissCount;
        private bool _piDmaBusy;
        private uint _viCurrentLine;
        private uint _viLineCycleAccum;

        // VI_CURRENT is a 10-bit scan counter on N64 (0..1023).
        private const uint ViLinesPerFrame = 1024;
        private const uint CpuCyclesPerViFrame = 1_562_500; // 93.75 MHz / 60 Hz
        private const uint CpuCyclesPerViLine = CpuCyclesPerViFrame / ViLinesPerFrame;

        private List<MemEntry> MemoryMapList = new List<MemEntry>();
        private MemEntry[]     MemoryMap;

        public Memory(byte[] Rom)
        {
            // RDRAM (base + expansion/mirror window).
            // Keep backing array at 8 MiB for now; accesses beyond that window mirror via ResolveArrayOffset().
            MemoryMapList.Add(new MemEntry(0x00000000, 0x00FFFFFF, RDRAM, RDRAM,       "RDRAM"));
            MemoryMapList.Add(new MemEntry(0x03F00000, 0x03FFFFFF, RDRAMReg, RDRAMReg, "RDRAM Registers"));

            // SP Registers
            MemoryMapList.Add(new MemEntry(0x04000000, 0x04000FFF, SP_DMEM_RW,         SP_DMEM_RW,          "SP_DMEM"));
            MemoryMapList.Add(new MemEntry(0x04001000, 0x04001FFF, SP_IMEM_RW,         SP_IMEM_RW,          "SP_IMEM"));
            MemoryMapList.Add(new MemEntry(0x04040010, 0x04040013, SP_STATUS_REG_R,    SP_STATUS_REG_W,     "SP_STATUS_REG"));
            MemoryMapList.Add(new MemEntry(0x04040018, 0x0404001B, SP_DMA_BUSY_REG_R,  SP_DMA_BUSY_REG_W,   "SP_DMA_BUSY_REG"));
            MemoryMapList.Add(new MemEntry(0x0404001C, 0x0404001F, SP_SEMAPHORE_REG_R, SP_SEMAPHORE_REG_W,  "SP_SEMAPHORE_REG"));
            MemoryMapList.Add(new MemEntry(0x04080000, 0x04080003, SP_PC_REG_RW,       SP_PC_REG_RW,        "SP_PC_REG"));

            // DPC Registers
            MemoryMapList.Add(new MemEntry(0x0410000C, 0x0410000F, DPC_STATUS_REG_R, DPC_STATUS_REG_W, "DPC_STATUS_REG"));

            // MI Registers
            MemoryMapList.Add(new MemEntry(0x04300000, 0x04300003, MI_INIT_MODE_REG_R, MI_INIT_MODE_REG_W, "MI_INIT_MODE_REG"));
            MemoryMapList.Add(new MemEntry(0x04300004, 0x04300007, MI_VERSION_REG_RW,  MI_VERSION_REG_RW,  "MI_VERSION_REG"));
            MemoryMapList.Add(new MemEntry(0x04300008, 0x0430000B, MI_INTR_REG_R,      null,               "MI_INTR_REG"));
            MemoryMapList.Add(new MemEntry(0x0430000C, 0x0430000F, MI_INTR_MASK_REG_R, MI_INTR_MASK_REG_W, "MI_INTR_MASK_REG",
                null, MI_INTR_MASK_WRITE_EVENT));

            // VI Registers
            MemoryMapList.Add(new MemEntry(0x04400000, 0x04400003, VI_STATUS_REG_RW,  VI_STATUS_REG_RW,  "VI_STATUS_REG"));
            MemoryMapList.Add(new MemEntry(0x04400004, 0x04400007, VI_ORIGIN_REG_RW,  VI_ORIGIN_REG_RW,  "VI_ORIGIN_REG"));
            MemoryMapList.Add(new MemEntry(0x04400008, 0x0440000B, VI_WIDTH_REG_RW,   VI_WIDTH_REG_RW,   "VI_WIDTH_REG"));
            MemoryMapList.Add(new MemEntry(0x0440000C, 0x0440000F, VI_INTR_REG_RW,    VI_INTR_REG_RW,    "VI_INTR_REG"));
            MemoryMapList.Add(new MemEntry(0x04400010, 0x04400013, VI_CURRENT_REG_RW, VI_CURRENT_REG_RW, "VI_CURRENT_REG",
                null, VI_CURRENT_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400014, 0x04400017, VI_BURST_REG_RW,   VI_BURST_REG_RW,   "VI_BURST_REG"));
            MemoryMapList.Add(new MemEntry(0x04400018, 0x0440001B, VI_V_SYNC_REG_RW,  VI_V_SYNC_REG_RW,  "VI_V_SYNC_REG"));
            MemoryMapList.Add(new MemEntry(0x0440001C, 0x0440001F, VI_H_SYNC_REG_RW,  VI_H_SYNC_REG_RW,  "VI_H_SYNC_REG"));
            MemoryMapList.Add(new MemEntry(0x04400020, 0x04400023, VI_LEAP_REG_RW,    VI_LEAP_REG_RW,    "VI_LEAP_REG"));
            MemoryMapList.Add(new MemEntry(0x04400024, 0x04400027, VI_H_START_REG_RW, VI_H_START_REG_RW, "VI_H_START_REG"));
            MemoryMapList.Add(new MemEntry(0x04400028, 0x0440002B, VI_V_START_REG_RW, VI_V_START_REG_RW, "VI_V_START_REG"));
            MemoryMapList.Add(new MemEntry(0x0440002C, 0x0440002F, VI_V_BURST_REG_RW, VI_V_BURST_REG_RW, "VI_V_BURST_REG"));
            MemoryMapList.Add(new MemEntry(0x04400030, 0x04400033, VI_X_SCALE_REG_RW, VI_X_SCALE_REG_RW, "VI_X_SCALE_REG"));
            MemoryMapList.Add(new MemEntry(0x04400034, 0x04400037, VI_Y_SCALE_REG_RW, VI_Y_SCALE_REG_RW, "VI_Y_SCALE_REG"));

            // AI Registers
            MemoryMapList.Add(new MemEntry(0x04500000, 0x04500003, AI_DRAM_ADDR_REG_W, AI_DRAM_ADDR_REG_W, "AI_DRAM_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04500004, 0x04500007, AI_LEN_REG_RW,   AI_LEN_REG_RW,    "AI_LEN_REG"));
            MemoryMapList.Add(new MemEntry(0x04500008, 0x0450000B, AI_CONTROL_REG_W, AI_CONTROL_REG_W, "AI_CONTROL_REG"));
            MemoryMapList.Add(new MemEntry(0x0450000C, 0x0450000F, AI_STATUS_REG_R, AI_STATUS_REG_W,  "AI_STATUS_REG"));
            MemoryMapList.Add(new MemEntry(0x04500010, 0x04500013, AI_DACRATE_REG_W, AI_DACRATE_REG_W, "AI_DACRATE_REG"));
            MemoryMapList.Add(new MemEntry(0x04500014, 0x04500017, AI_BITRATE_REG_W, AI_BITRATE_REG_W, "AI_BITRATE_REG"));

            // PI Registers
            MemoryMapList.Add(new MemEntry(0x04600000, 0x04600003, PI_DRAM_ADDR_REG_RW, PI_DRAM_ADDR_REG_RW, "PI_DRAM_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04600004, 0x04600007, PI_CART_ADDR_REG_RW, PI_CART_ADDR_REG_RW, "PI_CART_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04600008, 0x0460000B, PI_RD_LEN_REG_RW, PI_RD_LEN_REG_RW,       "PI_RD_LEN_REG",
                null, PI_RD_LEN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x0460000C, 0x0460000F, PI_WR_LEN_REG_RW, PI_WR_LEN_REG_RW,       "PI_WR_LEN_REG", 
                null, PI_WR_LEN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04600010, 0x04600013, PI_STATUS_REG_R, PI_STATUS_REG_W,               "PI_STATUS_REG",
                PI_STATUS_READ_EVENT, PI_STATUS_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04600014, 0x04600017, PI_BSD_DOM1_LAT_REG_RW, PI_BSD_DOM1_LAT_REG_RW, "PI_BSD_DOM1_LAT_REG"));
            MemoryMapList.Add(new MemEntry(0x04600018, 0x0460001B, PI_BSD_DOM1_PWD_REG_RW, PI_BSD_DOM1_PWD_REG_RW, "PI_BSD_DOM1_PWD_REG"));
            MemoryMapList.Add(new MemEntry(0x0460001C, 0x0460001F, PI_BSD_DOM1_PGS_REG_RW, PI_BSD_DOM1_PGS_REG_RW, "PI_BSD_DOM1_PGS_REG"));
            MemoryMapList.Add(new MemEntry(0x04600020, 0x04600023, PI_BSD_DOM1_RLS_REG_RW, PI_BSD_DOM1_RLS_REG_RW, "PI_BSD_DOM1_RLS_REG"));
            MemoryMapList.Add(new MemEntry(0x04600024, 0x04600027, PI_BSD_DOM2_LAT_REG_RW, PI_BSD_DOM2_LAT_REG_RW, "PI_BSD_DOM2_LAT_REG"));
            MemoryMapList.Add(new MemEntry(0x04600028, 0x0460002B, PI_BSD_DOM2_PWD_REG_RW, PI_BSD_DOM2_PWD_REG_RW, "PI_BSD_DOM2_PWD_REG"));
            MemoryMapList.Add(new MemEntry(0x0460002C, 0x0460002F, PI_BSD_DOM2_PGS_REG_RW, PI_BSD_DOM2_PGS_REG_RW, "PI_BSD_DOM2_PGS_REG"));
            MemoryMapList.Add(new MemEntry(0x04600030, 0x04600033, PI_BSD_DOM2_RLS_REG_RW, PI_BSD_DOM2_RLS_REG_RW, "PI_BSD_DOM2_RLS_REG"));

            // SI Registers
            MemoryMapList.Add(new MemEntry(0x04800000, 0x04800003, SI_DRAM_ADDR_REG_RW, SI_DRAM_ADDR_REG_RW, "SI_DRAM_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04800004, 0x04800007, SI_PIF_ADDR_RD64B_REG_RW, SI_PIF_ADDR_RD64B_REG_RW, "SI_PIF_ADDR_RD64B_REG",
                null, SI_PIF_ADDR_RD64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04800010, 0x04800013, SI_PIF_ADDR_WR64B_REG_RW, SI_PIF_ADDR_WR64B_REG_RW, "SI_PIF_ADDR_WR64B_REG",
                null, SI_PIF_ADDR_WR64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04800018, 0x0480001B, SI_STATUS_REG_R, SI_STATUS_REG_W, "SI_STATUS_REG",
                null, SI_STATUS_WRITE_EVENT));
            // SI register alias window used by some boot/runtime paths.
            MemoryMapList.Add(new MemEntry(0x04818000, 0x04818003, SI_DRAM_ADDR_REG_RW, SI_DRAM_ADDR_REG_RW, "SI_DRAM_ADDR_REG_ALIAS"));
            MemoryMapList.Add(new MemEntry(0x04818004, 0x04818007, SI_PIF_ADDR_RD64B_REG_RW, SI_PIF_ADDR_RD64B_REG_RW, "SI_PIF_ADDR_RD64B_REG_ALIAS",
                null, SI_PIF_ADDR_RD64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04818010, 0x04818013, SI_PIF_ADDR_WR64B_REG_RW, SI_PIF_ADDR_WR64B_REG_RW, "SI_PIF_ADDR_WR64B_REG_ALIAS",
                null, SI_PIF_ADDR_WR64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04818018, 0x0481801B, SI_STATUS_REG_R, SI_STATUS_REG_W, "SI_STATUS_REG_ALIAS",
                null, SI_STATUS_WRITE_EVENT));

            // RI Registers
            MemoryMapList.Add(new MemEntry(0x0470000C, 0x0470000F, RI_SELECT_REG_RW, RI_SELECT_REG_RW, "RI_SELECT_REG"));

            // Cartridge domains on PI bus.
            // For bring-up compatibility, map all domains to ROM data with mirroring.
            // This avoids boot/runtime loops on OpenBus when games probe alternate domains.
            MemoryMapList.Add(new MemEntry(0x05000000, 0x05FFFFFF, Rom, Rom, "Cartridge Domain 2 (Address 1)"));
            MemoryMapList.Add(new MemEntry(0x06000000, 0x07FFFFFF, Rom, Rom, "Cartridge Domain 1 (Address 1)"));
            MemoryMapList.Add(new MemEntry(0x08000000, 0x0FFFFFFF, Rom, Rom, "Cartridge Domain 2 (Address 2)"));
            MemoryMapList.Add(new MemEntry(0x10000000, 0x1FBFFFFF, Rom, Rom, "Cartridge Domain 1 (Address 2)"));
            MemoryMapList.Add(new MemEntry(0x1FC00800, 0x1FFFFFFF, Rom, Rom, "Cartridge Domain 1 (Address 2) Mirror"));

            // PIF
            MemoryMapList.Add(new MemEntry(0x1FC00000, 0x1FC007BF, PIFROM, PIFROM, "PIF Rom"));
            MemoryMapList.Add(new MemEntry(0x1FC007C0, 0x1FC007FF, PIFRAM, PIFRAM, "PIF Ram"));

            MemoryMap = MemoryMapList.ToArray();
            MemoryMapList.Clear();

            // Setup Environment

            // MI Registers
            WriteUInt32Physical(0x04300004, 0x02020102); // MI_VERSION_REG (Same value as Pj64 1.4)

            // VI Registers
            WriteUInt32Physical(0x0440000C, 1023); // VI_INTR_REG

            // PI Registers
            uint BSD_DOM1_CONFIG = ReadUInt32Physical(0x10000000);

            WriteUInt32Physical(0x04600014, (BSD_DOM1_CONFIG      ) & 0xFF); // PI_BSD_DOM1_LAT_REG
            WriteUInt32Physical(0x04600018, (BSD_DOM1_CONFIG >> 8 ) & 0xFF); // PI_BSD_DOM1_PWD_REG
            WriteUInt32Physical(0x0460001C, (BSD_DOM1_CONFIG >> 16) & 0x0F); // PI_BSD_DOM1_PGS_REG
            WriteUInt32Physical(0x04600020, (BSD_DOM1_CONFIG >> 20) & 0x03); // PI_BSD_DOM1_RLS_REG
            // Keep DOM2 initialized to sane defaults (same profile as DOM1 during bring-up).
            WriteUInt32Physical(0x04600024, (BSD_DOM1_CONFIG      ) & 0xFF); // PI_BSD_DOM2_LAT_REG
            WriteUInt32Physical(0x04600028, (BSD_DOM1_CONFIG >> 8 ) & 0xFF); // PI_BSD_DOM2_PWD_REG
            WriteUInt32Physical(0x0460002C, (BSD_DOM1_CONFIG >> 16) & 0x0F); // PI_BSD_DOM2_PGS_REG
            WriteUInt32Physical(0x04600030, (BSD_DOM1_CONFIG >> 20) & 0x03); // PI_BSD_DOM2_RLS_REG

            SP_STATUS_REG_R[3] = 0x1;

            // RI Registers
            WriteUInt32Physical(0x0470000C, 0b1110); // RI_SELECT_REG

            // Copy the boot code to SP_DMEM using physical addresses.
            // This must bypass data-side TLB translation during early boot.
            DmaCopyPhysical(0x04000040, 0x10000040, 0xFC0);

            // Required by CIC x105
            WriteUInt32Physical(0x40001000, 0x3C0DBFC0);
            WriteUInt32Physical(0x40001004, 0x8DA807FC);
            WriteUInt32Physical(0x40001008, 0x25AD07C0);
            WriteUInt32Physical(0x40001010, 0x5500FFFC);
            WriteUInt32Physical(0x40001018, 0x8DA80024);
            WriteUInt32Physical(0x4000101C, 0x3C0BB000);
        }

        public void Tick(uint cpuCycles)
        {
            if (CpuCyclesPerViLine == 0)
                return;

            _viLineCycleAccum += cpuCycles;
            while (_viLineCycleAccum >= CpuCyclesPerViLine)
            {
                _viLineCycleAccum -= CpuCyclesPerViLine;
                _viCurrentLine++;
                if (_viCurrentLine >= ViLinesPerFrame)
                    _viCurrentLine = 0;

                WriteBigEndianWord(VI_CURRENT_REG_RW, _viCurrentLine);

                uint viIntrLine = ReadBigEndianWord(VI_INTR_REG_RW) & 0x03FFu;
                if (_viCurrentLine == viIntrLine)
                    SetMiViInterrupt();
            }
        }

        public void PI_WR_LEN_WRITE_EVENT()
        {
            uint WriteLength = ReadUInt32Physical(0x0460000C) & 0x00FFFFFF; // PI_WR_LEN_REG
            uint CartAddr    = ReadUInt32Physical(0x04600004) & 0x1FFFFFFE; // PI_CART_ADDR_REG
            uint DramAddr    = ReadUInt32Physical(0x04600000) & 0x00FFFFFE; // PI_DRAM_ADDR_REG
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI_WR_LEN write len=0x{WriteLength:x6} cart=0x{CartAddr:x8} dram=0x{DramAddr:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            _piDmaBusy = true;
            PI_STATUS_REG_R[3] |= 0b0001; // Set DMA Busy

            int transferSize = ComputePiTransferSize(WriteLength, DramAddr, CartAddr);
            if (transferSize > 0)
                DmaCopyPhysical(DramAddr, CartAddr, transferSize);

            PI_STATUS_REG_R[3] &= 0b1110; // Clear DMA Busy
            _piDmaBusy = false;
            SetMiPiInterrupt();
        }

        public void PI_RD_LEN_WRITE_EVENT()
        {
            // Bring-up compatibility path:
            // Some boot paths may poke PI_RD_LEN while expecting cart->RDRAM transfer semantics.
            // Mirror PI_WR_LEN behavior so IPL can progress instead of stalling before VI init.
            uint ReadLength = ReadUInt32Physical(0x04600008) & 0x00FFFFFF; // PI_RD_LEN_REG
            uint CartAddr   = ReadUInt32Physical(0x04600004) & 0x1FFFFFFE; // PI_CART_ADDR_REG
            uint DramAddr   = ReadUInt32Physical(0x04600000) & 0x00FFFFFE; // PI_DRAM_ADDR_REG
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI_RD_LEN write len=0x{ReadLength:x6} cart=0x{CartAddr:x8} dram=0x{DramAddr:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            _piDmaBusy = true;
            PI_STATUS_REG_R[3] |= 0b0001; // Set DMA Busy

            int transferSize = ComputePiTransferSize(ReadLength, DramAddr, CartAddr);
            if (transferSize > 0)
                DmaCopyPhysical(DramAddr, CartAddr, transferSize);

            PI_STATUS_REG_R[3] &= 0b1110; // Clear DMA Busy
            _piDmaBusy = false;
            SetMiPiInterrupt();
        }

        public void PI_STATUS_READ_EVENT()
        {
            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            if (_piDmaBusy)
                piStatus |= 0x00000001u;
            else
                piStatus &= ~0x00000001u;
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);
        }

        private int ComputePiTransferSize(uint lengthReg, uint dramAddr, uint cartAddr)
        {
            // Hardware uses (length + 1); keep bring-up robust by clamping.
            long requested = (long)lengthReg + 1L;
            if (requested <= 0)
                return 0;

            long rdramLeft = RDRAM.Length - (dramAddr & 0x007FFFFF);
            long cartLeft = int.MaxValue;
            if (cartAddr >= 0x10000000 && cartAddr <= 0x1FFFFFFF)
                cartLeft = Math.Max(0L, (long)GetEntry(cartAddr).ReadArray.Length - (cartAddr - 0x10000000));

            long maxSafe = Math.Min(requested, Math.Min(rdramLeft, cartLeft));
            const int MaxPiTransferPerOp = 1 << 20; // 1 MiB safety cap for bring-up
            if (maxSafe > MaxPiTransferPerOp)
                maxSafe = MaxPiTransferPerOp;

            if (maxSafe < 0)
                return 0;

            return (int)maxSafe;
        }

        public void PI_STATUS_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(PI_STATUS_REG_W);
            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI_STATUS write value=0x{value:x8} old=0x{piStatus:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            // PI status write behavior (bring-up subset):
            // bit0: reset/clear DMA+IO busy, bit1: clear PI interrupt.
            if ((value & 0x00000001) != 0)
            {
                // Clear DMA/IO busy style flags used by boot polling loops.
                piStatus &= ~0x00000003u;
            }

            if ((value & 0x00000002) != 0)
            {
                piStatus &= ~0x00000008u; // clear PI interrupt pending
                ClearMiPiInterrupt();
            }

            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine($"[N64IO] PI_STATUS new=0x{piStatus:x8}");
            }
        }

        public void SI_PIF_ADDR_RD64B_WRITE_EVENT()
        {
            // PIF RAM -> RDRAM (64 bytes)
            uint dramAddr = ReadUInt32Physical(0x04800000) & 0x00FFFFF8;
            uint dramKseg1 = PhysicalToKseg1(dramAddr);
            const int size = 64;

            SetSiBusy(true);
            for (uint i = 0; i < size; i++)
                WriteUInt8(dramKseg1 + i, PIFRAM[i]);
            SetSiBusy(false);
            WriteBigEndianWord(SI_PIF_ADDR_RD64B_REG_RW, 0);
            WriteBigEndianWord(SI_DRAM_ADDR_REG_RW, 0);
            SetMiSiInterrupt();
        }

        public void SI_PIF_ADDR_WR64B_WRITE_EVENT()
        {
            // RDRAM -> PIF RAM (64 bytes)
            uint dramAddr = ReadUInt32Physical(0x04800000) & 0x00FFFFF8;
            uint dramKseg1 = PhysicalToKseg1(dramAddr);
            const int size = 64;

            SetSiBusy(true);
            for (uint i = 0; i < size; i++)
                PIFRAM[i] = ReadUInt8(dramKseg1 + i);
            ProcessPifJoybusCommands();
            SetSiBusy(false);
            WriteBigEndianWord(SI_PIF_ADDR_WR64B_REG_RW, 0);
            WriteBigEndianWord(SI_DRAM_ADDR_REG_RW, 0);
            SetMiSiInterrupt();
        }

        public void SI_STATUS_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(SI_STATUS_REG_W);

            // SI status write behavior (bring-up subset):
            // bit0: clear SI interrupt.
            if ((value & 0x00000001) != 0)
            {
                uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R);
                siStatus &= ~0x00001000u; // clear SI interrupt pending
                WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
                ClearMiSiInterrupt();
            }
        }

        public void MI_INTR_MASK_WRITE_EVENT()
        {
            // MIPS Interface interrupt mask write semantics:
            // pairs of bits clear/set individual masks.
            uint value = ReadBigEndianWord(MI_INTR_MASK_REG_W);
            uint mask = ReadBigEndianWord(MI_INTR_MASK_REG_R) & 0x3Fu;
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] MI_INTR_MASK write value=0x{value:x8} oldMask=0x{mask:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            ApplyMiMaskPair(ref mask, value, 0, 1, 0); // SP
            ApplyMiMaskPair(ref mask, value, 2, 3, 1); // SI
            ApplyMiMaskPair(ref mask, value, 4, 5, 2); // AI
            ApplyMiMaskPair(ref mask, value, 6, 7, 3); // VI
            ApplyMiMaskPair(ref mask, value, 8, 9, 4); // PI
            ApplyMiMaskPair(ref mask, value, 10, 11, 5); // DP

            WriteBigEndianWord(MI_INTR_MASK_REG_R, mask);
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine($"[N64IO] MI_INTR_MASK new=0x{mask:x8}");
            }
        }

        private static uint ReadBigEndianWord(byte[] arr)
        {
            if (arr == null || arr.Length < 4)
                return 0;

            return ((uint)arr[0] << 24)
                 | ((uint)arr[1] << 16)
                 | ((uint)arr[2] << 8)
                 | arr[3];
        }

        private static void WriteBigEndianWord(byte[] arr, uint value)
        {
            if (arr == null || arr.Length < 4)
                return;

            arr[0] = (byte)((value >> 24) & 0xFF);
            arr[1] = (byte)((value >> 16) & 0xFF);
            arr[2] = (byte)((value >> 8) & 0xFF);
            arr[3] = (byte)(value & 0xFF);
        }

        private static void ApplyMiMaskPair(ref uint mask, uint value, int clearBit, int setBit, int targetBit)
        {
            uint targetMask = 1u << targetBit;
            if ((value & (1u << clearBit)) != 0)
                mask &= ~targetMask;
            if ((value & (1u << setBit)) != 0)
                mask |= targetMask;
        }

        private void SetMiPiInterrupt()
        {
            const byte MiPiIntrBit = 0x10; // MI_INTR_REG bit for PI
            MI_INTR_REG_R[3] |= MiPiIntrBit;

            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            piStatus |= 0x00000008u; // PI interrupt pending
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);
        }

        private void ClearMiPiInterrupt()
        {
            const byte MiPiIntrBit = 0x10; // MI_INTR_REG bit for PI
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiPiIntrBit);
        }

        private void SetMiSiInterrupt()
        {
            const byte MiSiIntrBit = 0x02; // MI_INTR_REG bit for SI
            MI_INTR_REG_R[3] |= MiSiIntrBit;

            uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R);
            siStatus |= 0x00001000u; // SI interrupt pending
            WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
        }

        private void ClearMiSiInterrupt()
        {
            const byte MiSiIntrBit = 0x02; // MI_INTR_REG bit for SI
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiSiIntrBit);
        }

        private void SetMiViInterrupt()
        {
            const byte MiViIntrBit = 0x08; // MI_INTR_REG bit for VI
            MI_INTR_REG_R[3] |= MiViIntrBit;
        }

        private void ClearMiViInterrupt()
        {
            const byte MiViIntrBit = 0x08; // MI_INTR_REG bit for VI
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiViIntrBit);
        }

        public void VI_CURRENT_WRITE_EVENT()
        {
            // Writing VI_CURRENT acknowledges/clears VI interrupt on real hardware.
            ClearMiViInterrupt();
        }

        private void SetSiBusy(bool busy)
        {
            uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R);
            const uint SiDmaBusyBit = 0x00000001u;
            if (busy)
                siStatus |= SiDmaBusyBit;
            else
                siStatus &= ~SiDmaBusyBit;
            WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
        }

        private void ProcessPifJoybusCommands()
        {
            // Minimal Joybus handling for bring-up:
            // enough to satisfy common controller probe/read loops.
            int i = 0;
            while (i < 63)
            {
                byte tx = PIFRAM[i];
                if (tx == 0xFE)
                    break; // end marker

                if (tx == 0xFF || tx == 0xFD || tx == 0xB4)
                {
                    i++;
                    continue;
                }

                if (tx == 0x00)
                {
                    i++;
                    continue;
                }

                if (i + 2 >= 64)
                    break;

                int txLen = tx & 0x3F;
                int rxLen = PIFRAM[i + 1] & 0x3F;
                int cmdIndex = i + 2;
                int rxIndex = cmdIndex + txLen;

                if (txLen <= 0 || rxIndex >= 64)
                    break;

                byte cmd = PIFRAM[cmdIndex];
                switch (cmd)
                {
                    case 0x00: // INFO
                    case 0xFF: // RESET/INFO
                        // Standard controller signature.
                        if (rxLen >= 3 && rxIndex + 2 < 64)
                        {
                            PIFRAM[rxIndex + 0] = 0x05;
                            PIFRAM[rxIndex + 1] = 0x00;
                            PIFRAM[rxIndex + 2] = 0x01;
                        }
                        break;
                    case 0x01: // READ BUTTONS
                        if (rxLen >= 4 && rxIndex + 3 < 64)
                        {
                            PIFRAM[rxIndex + 0] = 0x00;
                            PIFRAM[rxIndex + 1] = 0x00;
                            PIFRAM[rxIndex + 2] = 0x00;
                            PIFRAM[rxIndex + 3] = 0x00;
                        }
                        break;
                    default:
                        // Unknown command: leave response bytes as-is/open.
                        break;
                }

                i = rxIndex + rxLen;
            }
        }

        struct MemEntry
        {
            public uint StartAddress;
            public uint EndAddress;
            public byte[] ReadArray;
            public byte[] WriteArray;
            public string Name;

            public MemoryEvent ReadEvent;
            public MemoryEvent WriteEvent;

            public MemEntry(uint StartAddress, uint EndAddress, byte[] ReadArray, byte[] WriteArray, string Name, MemoryEvent ReadEvent = null, MemoryEvent WriteEvent = null)
            {
                this.StartAddress = StartAddress;
                this.EndAddress   = EndAddress;
                this.ReadArray    = ReadArray;
                this.WriteArray   = WriteArray;
                this.Name         = Name;
                this.ReadEvent    = ReadEvent;
                this.WriteEvent   = WriteEvent;
            }
        }

        private MemEntry GetEntry(uint index)
        {
            if (TryGetSiAliasedEntry(index, out MemEntry siEntry))
                return siEntry;

            // Robust fallback: treat the full RDRAM window as mapped even if table lookup
            // would miss for any reason. This prevents runaway OpenBus loops on normal RAM.
            if (index <= 0x00FFFFFF)
                return new MemEntry(0x00000000, 0x00FFFFFF, RDRAM, RDRAM, "RDRAM_FALLBACK");

            bool FoundEntry = false;
            MemEntry Result = new MemEntry();

            for (int i = 0; i < MemoryMap.Length; ++i)
            {
                MemEntry CurrentEntry = MemoryMap[i];
                if (index < CurrentEntry.StartAddress || index > CurrentEntry.EndAddress) continue;

                FoundEntry = true;
                Result = CurrentEntry;
                break;
            }

            if (!FoundEntry)
            {
                _openBusMissCount++;
                if (_openBusMissCount <= 64 || (_openBusMissCount % 256) == 0)
                {
                    Common.Logger.PrintWarningLine(
                        $"OpenBus read/write for unmapped address 0x{index:x8} at pc=0x{Registers.R4300.PC:x8} " +
                        $"(count={_openBusMissCount}).");
                }

                return new MemEntry(index & 0xFFFFFFFC, (index & 0xFFFFFFFC) + 3, OpenBus, OpenBus, "OPEN_BUS");
            }

            return Result;
        }

        private bool TryGetSiAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();

            if (index < 0x04800000 || index > 0x048FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFE0;
            uint regOffset = index & 0x1F;
            uint regAddr = 0x04800000 + regOffset;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFC);

            if (regAddr >= 0x04800000 && regAddr <= 0x04800003)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_DRAM_ADDR_REG_RW, SI_DRAM_ADDR_REG_RW, "SI_DRAM_ADDR_REG_MIRROR");
                return true;
            }

            if (regAddr >= 0x04800004 && regAddr <= 0x04800007)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_PIF_ADDR_RD64B_REG_RW, SI_PIF_ADDR_RD64B_REG_RW, "SI_PIF_ADDR_RD64B_REG_MIRROR",
                    null, SI_PIF_ADDR_RD64B_WRITE_EVENT);
                return true;
            }

            if (regAddr >= 0x04800010 && regAddr <= 0x04800013)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_PIF_ADDR_WR64B_REG_RW, SI_PIF_ADDR_WR64B_REG_RW, "SI_PIF_ADDR_WR64B_REG_MIRROR",
                    null, SI_PIF_ADDR_WR64B_WRITE_EVENT);
                return true;
            }

            if (regAddr >= 0x04800018 && regAddr <= 0x0480001B)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_STATUS_REG_R, SI_STATUS_REG_W, "SI_STATUS_REG_MIRROR",
                    null, SI_STATUS_WRITE_EVENT);
                return true;
            }

            // Bring-up fallback: allow unknown SI alias offsets to behave as RAM-like mirrors
            // instead of OpenBus to avoid lockups in early exception/controller loops.
            if (index <= 0x0481FFFF)
            {
                entry = new MemEntry(0x04800000, 0x0481FFFF, SI_MIRROR_RAM, SI_MIRROR_RAM, "SI_MIRROR_RAM");
                return true;
            }

            return false;
        }

        public byte this[uint index]
        {
            get
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: false);
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.ReadArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not readable.");

                int offset = ResolveArrayOffset(Entry.ReadArray, nonCachedIndex - Entry.StartAddress);
                byte value = Entry.ReadArray[offset];
                uint regOffset = nonCachedIndex - Entry.StartAddress;
                if ((regOffset & 0x3) == 0x3)
                    Entry.ReadEvent?.Invoke();
                return value;
            }
            set
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: true);
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.WriteArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not writable.");

                int offset = ResolveArrayOffset(Entry.WriteArray, nonCachedIndex - Entry.StartAddress);
                Entry.WriteArray[offset] = value;
                uint regOffset = nonCachedIndex - Entry.StartAddress;
                if ((regOffset & 0x3) == 0x3)
                    Entry.WriteEvent?.Invoke();
            }
        }

        public byte[] this[uint index, int size]
        {
            get
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: false);
                byte[] result = new byte[size];
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.ReadArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not readable.");

                // Read byte-by-byte to safely handle accesses that span memory map boundaries.
                for (int i = 0; i < size; i++)
                    result[i] = this[index + (uint)i];

                return result;
            }
            set
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: true);
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.WriteArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not writable.");

                // Write byte-by-byte to safely handle accesses that span memory map boundaries.
                for (int i = 0; i < size; i++)
                    this[index + (uint)i] = value[i];
            }
        }

        public void FastMemoryWrite(uint Dest, byte[] ToWrite)
        {
            this[Dest, ToWrite.Length] = ToWrite;
        }

        public byte[] FastMemoryRead(uint Source, int Length)
        {
            return this[Source, Length];
        }

        public void FastMemoryWrite(uint Dest, byte[] ToWrite, int Length)
        {
            if (ToWrite.Length < Length)
                throw new InvalidOperationException("Cannot write to memory an Array that is less than the input size.");
            this[Dest, Length] = ToWrite;
        }

        public void FastMemoryCopy(uint Dest, uint Source, int Size)
        {
            FastMemoryWrite(Dest, FastMemoryRead(Source, Size));
        }

        public void SafeMemoryCopy(uint Dest, uint Source, int Size)
        {
            if (GetEntry(Source & 0x1FFFFFFF).StartAddress != GetEntry((Source + (uint)Size) & 0x1FFFFFFF).StartAddress 
                || GetEntry(Dest & 0x1FFFFFFF).StartAddress != GetEntry((Dest + (uint)Size) & 0x1FFFFFFF).StartAddress)
                throw new NotImplementedException("Copying over multiple Memory Regions isn't implemented.");
            FastMemoryCopy(Dest, Source, Size);
        }

        public byte ReadUInt8(uint index)
        {
            byte value = this[index];
            InvokeMappedReadEvent(index);
            return value;
        }

        public void WriteUInt8(uint index, byte value)
        {
            this[index] = value;
            InvokeMappedWriteEvent(index);
        }

        public sbyte ReadInt8(uint index)
        {
            return (sbyte)ReadUInt8(index);
        }

        public void WriteInt8(uint index, sbyte value)
        {
            WriteUInt8(index, (byte)value);
        }

        public ushort ReadUInt16(uint index)
        {
            byte[] Res = this[index, 2];
            Array.Reverse(Res);
            InvokeMappedReadEvent(index);
            unsafe
            {
                fixed (byte* point = &Res[0])
                {
                    ushort* shortPoint = (ushort*)point;
                    return *shortPoint;
                }
            }
        }

        public void WriteUInt16(uint index, ushort value)
        {
            unsafe
            {
                ushort* point = &value;
                byte[] PointArray = new byte[2];
                Marshal.Copy(new IntPtr(point), PointArray, 0, 2);

                Array.Reverse(PointArray);

                this[index, 2] = PointArray;
            }
            InvokeMappedWriteEvent(index);
        }

        public short ReadInt16(uint index)
        {
            return (short)ReadUInt16(index);
        }

        public void WriteInt16(uint index, short value)
        {
            WriteUInt16(index, (ushort)value);
        }

        public uint ReadUInt32(uint index)
        {
            byte[] Res = this[index, 4];
            Array.Reverse(Res);
            unsafe
            {
                fixed (byte* point = &Res[0])
                {
                    uint* intPoint = (uint*)point;
                    return *intPoint;
                }
            }
        }

        public void WriteUInt32(uint index, uint value)
        {
            unsafe
            {
                uint* point = &value;
                byte[] PointArray = new byte[4];
                Marshal.Copy(new IntPtr(point), PointArray, 0, 4);

                Array.Reverse(PointArray);

                this[index, 4] = PointArray;
            }
        }

        public int ReadInt32(uint index)
        {
            return (int)ReadUInt32(index);
        }

        public void WriteInt32(uint index, int value)
        {
            WriteUInt32(index, (uint)value);
        }

        public ulong ReadUInt64(uint index)
        {
            byte[] Res = this[index, 8];
            Array.Reverse(Res);
            unsafe
            {
                fixed (byte* point = &Res[0])
                {
                    ulong* longPoint = (ulong*)point;
                    return *longPoint;
                }
            }
        }

        public void WriteUInt64(uint index, ulong value)
        {
            unsafe
            {
                ulong* point = &value;
                byte[] PointArray = new byte[8];
                Marshal.Copy(new IntPtr(point), PointArray, 0, 8);

                Array.Reverse(PointArray);

                this[index, 8] = PointArray;
            }
        }

        public long ReadInt64(uint index)
        {
            return (long)ReadUInt64(index);
        }

        public void WriteInt64(uint index, long value)
        {
            WriteUInt64(index, (ulong)value);
        }

        private static int ResolveArrayOffset(byte[] array, uint logicalOffset)
        {
            if (array.Length == 0)
                throw new IndexOutOfRangeException("Mapped memory region has zero length.");

            if (logicalOffset < (uint)array.Length)
                return (int)logicalOffset;

            // Cartridge and some register regions are mirrored over larger address windows.
            return (int)(logicalOffset % (uint)array.Length);
        }

        private static uint ToPhysicalAddress(uint virtualAddress, bool isWrite = false)
        {
            // VR4300 virtual address segments:
            // kseg0: 0x8000_0000..0x9FFF_FFFF (direct-mapped, cached)
            // kseg1: 0xA000_0000..0xBFFF_FFFF (direct-mapped, uncached)
            // Others use TLB translation.
            //
            // Bring-up compatibility:
            // Some N64 boot/runtime paths occasionally hit low (physical-looking) addresses
            // before full TLB state is established. Allow that window to pass through unless
            // strict mode is explicitly requested.
            if (AllowDirectLowPhysicalWindow && virtualAddress < 0x20000000u)
                return virtualAddress;

            uint segment = virtualAddress & 0xE0000000u;
            if (segment == 0x80000000u || segment == 0xA0000000u)
                return virtualAddress & 0x1FFFFFFFu;

            return TLB.TranslateAddress(virtualAddress, throwOnMiss: StrictDataTlb, isStore: isWrite) & 0x1FFFFFFFu;
        }

        private static uint PhysicalToKseg1(uint physicalAddress)
        {
            return 0xA0000000u | (physicalAddress & 0x1FFFFFFFu);
        }

        private uint ReadUInt32Physical(uint physicalAddress)
        {
            return ReadUInt32(PhysicalToKseg1(physicalAddress));
        }

        private void WriteUInt32Physical(uint physicalAddress, uint value)
        {
            WriteUInt32(PhysicalToKseg1(physicalAddress), value);
        }

        private void DmaCopyPhysical(uint destPhysical, uint sourcePhysical, int size)
        {
            uint dest = destPhysical & 0x1FFFFFFFu;
            uint src = sourcePhysical & 0x1FFFFFFFu;
            int remaining = size;

            while (remaining > 0)
            {
                MemEntry srcEntry = GetEntry(src);
                MemEntry dstEntry = GetEntry(dest);

                if (srcEntry.ReadArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"DMA source 0x{src:x8} not readable.");
                if (dstEntry.WriteArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"DMA destination 0x{dest:x8} not writable.");

                int srcOff = ResolveArrayOffset(srcEntry.ReadArray, src - srcEntry.StartAddress);
                int dstOff = ResolveArrayOffset(dstEntry.WriteArray, dest - dstEntry.StartAddress);

                int srcContig = srcEntry.ReadArray.Length - srcOff;
                int dstContig = dstEntry.WriteArray.Length - dstOff;
                int chunk = Math.Min(remaining, Math.Min(srcContig, dstContig));

                Buffer.BlockCopy(srcEntry.ReadArray, srcOff, dstEntry.WriteArray, dstOff, chunk);

                remaining -= chunk;
                src += (uint)chunk;
                dest += (uint)chunk;
            }
        }

        private void InvokeMappedReadEvent(uint index)
        {
            uint physical = ToPhysicalAddress(index, isWrite: false);
            MemEntry entry = GetEntry(physical);
            entry.ReadEvent?.Invoke();
        }

        private void InvokeMappedWriteEvent(uint index)
        {
            uint physical = ToPhysicalAddress(index, isWrite: true);
            MemEntry entry = GetEntry(physical);
            entry.WriteEvent?.Invoke();
        }
    }
}
