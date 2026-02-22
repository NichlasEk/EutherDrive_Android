using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Ryu64.MIPS
{
    public class R4300
    {
        public static bool R4300_ON = false;
        private static Thread CpuThread;

        public static Memory memory;
        public static ulong CycleCounter = 0;
        private static ulong Count = 0;
        private static long UnknownOpcodeCount = 0;
        private static readonly Dictionary<uint, long> UnknownOpcodeByPc = new Dictionary<uint, long>();
        private static readonly Dictionary<uint, long> UnknownOpcodeByValue = new Dictionary<uint, long>();
        private static readonly object UnknownOpcodeLock = new object();
        private struct RecentInst
        {
            public uint Pc;
            public uint Op;
        }
        private static readonly RecentInst[] _recentInst = new RecentInst[32];
        private static int _recentInstPos = 0;
        private static ulong _stuckPcLogCount = 0;
        private static readonly bool TraceBootWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_BOOT_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceBootWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_BOOT_WINDOW_LIMIT", 4000);
        private static int _traceBootWindowCount = 0;
        private const ulong StatusExlBit = 1UL << 1;
        private const ulong StatusBevBit = 1UL << 22;
        private const ulong CauseExcCodeMask = 0x7CUL;
        private const ulong CauseExcCodeTlbLoad = 2UL << 2;

        private static int ParseTraceLimit(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (int.TryParse(raw, out int parsed) && parsed > 0)
                return parsed;

            return fallback;
        }

        private static void TrackUnknownOpcode(uint pc, uint opcode)
        {
            lock (UnknownOpcodeLock)
            {
                if (!UnknownOpcodeByPc.TryGetValue(pc, out long pcCount))
                    pcCount = 0;
                UnknownOpcodeByPc[pc] = pcCount + 1;

                if (!UnknownOpcodeByValue.TryGetValue(opcode, out long opCount))
                    opCount = 0;
                UnknownOpcodeByValue[opcode] = opCount + 1;
            }
        }

        private static void RaiseTlbRefillException(uint badAddress, uint faultingPc)
        {
            ulong status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
            ulong cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
            ulong entryHi = Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG];

            Registers.COP0.Reg[Registers.COP0.BADVADDR_REG] = badAddress;
            Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] = (badAddress & 0xFFFFE000u) | (entryHi & 0xFFu);
            Registers.COP0.Reg[Registers.COP0.EPC_REG] = faultingPc & 0xFFFFFFFCu;
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = (cause & ~CauseExcCodeMask) | CauseExcCodeTlbLoad;
            Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status | StatusExlBit;

            Registers.R4300.PC = (status & StatusBevBit) != 0
                ? 0xBFC00200u
                : 0x80000000u;
        }

        private static string FormatTopUnknownSummary(Dictionary<uint, long> source, string prefix, int topN)
        {
            List<KeyValuePair<uint, long>> list = new List<KeyValuePair<uint, long>>(source);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            if (list.Count > topN)
                list.RemoveRange(topN, list.Count - topN);

            string[] chunks = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                chunks[i] = $"{prefix}=0x{list[i].Key:x8}:{list[i].Value}";

            return string.Join(", ", chunks);
        }

        private static uint CRC32(uint StartAddress, uint Length)
        {
            uint[] Table = new uint[256];
            ulong n, k;
            uint c;

            for (n = 0; n < 256; ++n)
            {
                c = (uint)n;

                for (k = 0; k < 8; ++k)
                {
                    if ((c & 1) == 1)
                        c = 0xEDB88320 ^ (c >> 1);
                    else
                        c >>= 1;
                }

                Table[n] = c;
            }

            c = 0 ^ 0xFFFFFFFF;

            for (n = 0; n < Length; ++n)
            {
                c = Table[(c ^ memory.ReadUInt8(StartAddress + (uint)n)) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFF;
        }

        // All values from Cen64: https://github.com/tj90241/cen64/blob/master/si/cic.c
        private const uint CIC_SEED_NUS_5101 = 0x0000AC00;
        private const uint CIC_SEED_NUS_6101 = 0x00043F3F;
        private const uint CIC_SEED_NUS_6102 = 0x00003F3F;
        private const uint CIC_SEED_NUS_6103 = 0x0000783F;
        private const uint CIC_SEED_NUS_6105 = 0x0000913F;
        private const uint CIC_SEED_NUS_6106 = 0x0000853F;
        private const uint CIC_SEED_NUS_8303 = 0x0000DD00;

        private const uint CRC_NUS_5101 = 0x587BD543;
        private const uint CRC_NUS_6101 = 0x6170A4A1;
        private const uint CRC_NUS_7102 = 0x009E9EA3;
        private const uint CRC_NUS_6102 = 0x90BB6CB5;
        private const uint CRC_NUS_6103 = 0x0B050EE0;
        private const uint CRC_NUS_6105 = 0x98BC2C86;
        private const uint CRC_NUS_6106 = 0xACC8580A;
        private const uint CRC_NUS_8303 = 0x0E018159;
        private const uint CRC_iQue_1   = 0xCD19FEF1;
        private const uint CRC_iQue_2   = 0xB98CED9A;
        private const uint CRC_iQue_3   = 0xE71C2766;


        private static uint GetCICSeed()
        {
            uint CRC        = CRC32(0x10000040, 0xFC0);
            uint Aleck64CRC = CRC32(0x10000040, 0xBC0);

            if (Aleck64CRC == CRC_NUS_5101) return CIC_SEED_NUS_5101;
            switch (CRC)
            {
                default:
                    Common.Logger.PrintWarningLine("Unknown CIC, defaulting to seed CIC-6101.");
                    return CIC_SEED_NUS_6101;

                case CRC_NUS_6101:
                case CRC_NUS_7102:
                case CRC_iQue_1:
                case CRC_iQue_2:
                case CRC_iQue_3:
                    return CIC_SEED_NUS_6101;

                case CRC_NUS_6102:
                    return CIC_SEED_NUS_6102;

                case CRC_NUS_6103:
                    return CIC_SEED_NUS_6103;

                case CRC_NUS_6105:
                    return CIC_SEED_NUS_6105;

                case CRC_NUS_6106:
                    return CIC_SEED_NUS_6106;

                case CRC_NUS_8303:
                    return CIC_SEED_NUS_8303;
            }
        }

        private static byte GetInitialPifControlByte(uint cicSeed)
        {
            // Boot ROM/IPL paths are sensitive to the initial PIF control byte.
            // Keep a pragmatic mapping for commonly used CICs during bring-up.
            switch (cicSeed)
            {
                case CIC_SEED_NUS_6102:
                    return 0x3D;
                case CIC_SEED_NUS_6105:
                case CIC_SEED_NUS_6106:
                    return 0xD2;
                default:
                    return 0x3D;
            }
        }

        public static void InterpretOpcode(uint Opcode)
        {
            if (Registers.R4300.Reg[0] != 0) Registers.R4300.Reg[0] = 0;

            if (Registers.COP0.Reg[Registers.COP0.COUNT_REG] >= 0xFFFFFFFF)
            {
                Registers.COP0.Reg[Registers.COP0.COUNT_REG] = 0x0;
                Count = 0x0;
            }

            OpcodeTable.OpcodeDesc Desc = new OpcodeTable.OpcodeDesc(Opcode);
            OpcodeTable.InstInfo   Info = OpcodeTable.GetOpcodeInfo(Opcode);

            if (Common.Variables.Debug)
            {
                string ASM = string.Format(
                    Info.FormattedASM,
                    Desc.op1, Desc.op2, Desc.op3, Desc.op4,
                    Desc.Imm, Desc.Target);
                Common.Logger.PrintInfoLine($"0x{Registers.R4300.PC:x}: {Convert.ToString(Opcode, 2).PadLeft(32, '0')}: {ASM}");
            }

            Info.Interpret(Desc);
            CycleCounter += Info.Cycles;
            Count        += Info.Cycles;
            memory?.Tick(Info.Cycles);
            Registers.COP0.Reg[Registers.COP0.COUNT_REG] = Count >> 1;
            --Registers.COP0.Reg[Registers.COP0.RANDOM_REG];
            if (Registers.COP0.Reg[Registers.COP0.RANDOM_REG] < Registers.COP0.Reg[Registers.COP0.WIRED_REG])
                Registers.COP0.Reg[Registers.COP0.RANDOM_REG] = 0x1F; // TODO: Reset the Random Register to 0x1F after writing to the Wired Register.

            Common.Measure.InstructionCount += 1;
            Common.Measure.CycleCounter = CycleCounter;
        }

        public static void PowerOnR4300()
        {
            StopR4300();

            for (int i = 0; i < Registers.R4300.Reg.Length; ++i)
                Registers.R4300.Reg[i] = 0; // Clear Registers.

            uint RomType   = 0; // 0 = Cart, 1 = DD
            uint ResetType = 0; // 0 = Cold Reset, 1 = NMI, 2 = Reset to boot disk
            uint osVersion = 0; // 00 = 1.0, 15 = 2.5, etc.
            uint TVType    = 1; // 0 = PAL, 1 = NTSC, 2 = MPAL

            Registers.R4300.Reg[1]  = 0x0000000000000001;
            Registers.R4300.Reg[2]  = 0x000000000EBDA536;
            Registers.R4300.Reg[3]  = 0x000000000EBDA536;
            Registers.R4300.Reg[4]  = 0x000000000000A536;
            Registers.R4300.Reg[5]  = 0xFFFFFFFFC0F1D859;
            Registers.R4300.Reg[6]  = 0xFFFFFFFFA4001F0C;
            Registers.R4300.Reg[7]  = 0xFFFFFFFFA4001F08;
            Registers.R4300.Reg[8]  = 0x00000000000000C0;
            Registers.R4300.Reg[10] = 0x0000000000000040;
            Registers.R4300.Reg[11] = 0xFFFFFFFFA4000040;
            Registers.R4300.Reg[12] = 0xFFFFFFFFED10D0B3;
            Registers.R4300.Reg[13] = 0x000000001402A4CC;
            Registers.R4300.Reg[14] = 0x000000002DE108EA;
            Registers.R4300.Reg[15] = 0x000000003103E121;
            Registers.R4300.Reg[19] = RomType;
            Registers.R4300.Reg[20] = TVType;
            Registers.R4300.Reg[21] = ResetType;
            uint cicSeed = GetCICSeed();
            Registers.R4300.Reg[22] = (cicSeed >> 8) & 0xFF;
            Registers.R4300.Reg[23] = osVersion;
            Registers.R4300.Reg[25] = 0xFFFFFFFF9DEBB54F;
            Registers.R4300.Reg[29] = 0xFFFFFFFFA4001FF0;
            Registers.R4300.Reg[31] = 0xFFFFFFFFA4001550;
            Registers.R4300.HI      = 0x000000003FC18657;
            Registers.R4300.LO      = 0x000000003103E121;
            Registers.R4300.PC      = 0xA4000040;

            memory.FastMemoryCopy(0xA4000000, 0xB0000000, 0x1000); // Load the 4 KiB IPL3 boot code into SP memory.
            memory.WriteUInt8(0x1FC007FF, GetInitialPifControlByte(cicSeed));

            COP0.PowerOnCOP0();
            COP1.PowerOnCOP1();

            R4300_ON = true;
            UnknownOpcodeCount = 0;
            lock (UnknownOpcodeLock)
            {
                UnknownOpcodeByPc.Clear();
                UnknownOpcodeByValue.Clear();
            }
            _stuckPcLogCount = 0;

            OpcodeTable.Init();

            CpuThread =
            new Thread(() =>
            {
                Common.Measure.MeasureTime.Start();
                try
                {
                    uint lastPc = Registers.R4300.PC;
                    ulong samePcIterations = 0;
                    while (R4300_ON)
                    {
                        uint pc = Registers.R4300.PC;
                        if ((pc & 0x3) != 0)
                        {
                            uint alignedPc = pc & 0xFFFFFFFCu;
                            Common.Logger.PrintWarningLine(
                                $"Misaligned PC detected: 0x{pc:x8}, aligning to 0x{alignedPc:x8}.");
                            Registers.R4300.PC = alignedPc;
                            pc = alignedPc;
                        }
                        if (pc == lastPc)
                        {
                            samePcIterations++;
                            if (samePcIterations == 5_000_000 || (samePcIterations % 20_000_000) == 0)
                            {
                                _stuckPcLogCount++;
                                Common.Logger.PrintWarningLine(
                                    $"R4300 watchdog: PC appears stuck at 0x{pc:x8} for {samePcIterations} iterations " +
                                    $"(report #{_stuckPcLogCount}).");
                            }
                        }
                        else
                        {
                            samePcIterations = 0;
                            lastPc = pc;
                        }

                        uint fetchAddress = pc;
                        if ((pc & 0xE0000000u) < 0x80000000u)
                        {
                            uint translated = TLB.TranslateAddress(pc, throwOnMiss: true) & 0x1FFFFFFFu;
                            fetchAddress = 0xA0000000u | translated;
                        }

                        uint Opcode = memory.ReadUInt32(fetchAddress);
                        _recentInst[_recentInstPos] = new RecentInst { Pc = pc, Op = Opcode };
                        _recentInstPos = (_recentInstPos + 1) & 31;
                        if (TraceBootWindow
                            && _traceBootWindowCount < TraceBootWindowLimit
                            && pc >= 0x80000000
                            && pc <= 0x80000200)
                        {
                            _traceBootWindowCount++;
                            Console.WriteLine(
                                $"[N64BOOT] #{_traceBootWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                $"piStatus=0x{memory.ReadUInt32(0x04600010):x8} piWrLen=0x{memory.ReadUInt32(0x0460000C):x8} " +
                                $"t0=0x{Registers.R4300.Reg[8]:x16} t1=0x{Registers.R4300.Reg[9]:x16} " +
                                $"t8=0x{Registers.R4300.Reg[24]:x16} t9=0x{Registers.R4300.Reg[25]:x16} " +
                                $"ra=0x{Registers.R4300.Reg[31]:x16}");
                        }
                        try
                        {
                            InterpretOpcode(Opcode);
                        }
                        catch (Common.Exceptions.TLBMissException tlbMiss)
                        {
                            RaiseTlbRefillException(tlbMiss.Address, pc);
                            continue;
                        }
                        catch (NotImplementedException ex)
                        {
                            // Keep emulator alive by treating unknown instructions as NOPs.
                            // This is imperfect but allows wider ROM coverage during bring-up.
                            UnknownOpcodeCount++;
                            TrackUnknownOpcode(pc, Opcode);
                            if (UnknownOpcodeCount <= 32 || (UnknownOpcodeCount % 256) == 0)
                            {
                                Common.Logger.PrintWarningLine(
                                    $"Unknown opcode treated as NOP (count={UnknownOpcodeCount}) " +
                                    $"pc=0x{pc:x8} op=0x{Opcode:x8}: {ex.Message}");
                                string recent = "";
                                for (int h = 0; h < 20; h++)
                                {
                                    int idx = (_recentInstPos - 1 - h) & 31;
                                    recent += $"[{h}]pc=0x{_recentInst[idx].Pc:x8}/op=0x{_recentInst[idx].Op:x8} ";
                                }
                                Common.Logger.PrintWarningLine($"Recent PCs before unknown: {recent.TrimEnd()}");
                            }
                            if ((UnknownOpcodeCount % 1024) == 0)
                            {
                                string topPc;
                                string topOp;
                                lock (UnknownOpcodeLock)
                                {
                                    topPc = FormatTopUnknownSummary(UnknownOpcodeByPc, "pc", 5);
                                    topOp = FormatTopUnknownSummary(UnknownOpcodeByValue, "op", 5);
                                }

                                Common.Logger.PrintWarningLine($"Unknown opcode hot PCs: {topPc}");
                                Common.Logger.PrintWarningLine($"Unknown opcode hot ops: {topOp}");
                            }

                            Registers.R4300.PC = pc + 4;
                            CycleCounter += 1;
                            Count += 1;
                            Registers.COP0.Reg[Registers.COP0.COUNT_REG] = Count >> 1;
                            continue;
                        }

                        while (Common.Settings.STEP_MODE && !Common.Variables.Step);
                        if (Common.Settings.STEP_MODE)
                        {
                            Registers.R4300.PrintRegisterInfo();
                            Registers.COP0.PrintRegisterInfo();
                            Registers.COP1.PrintRegisterInfo();
                            Thread.Sleep(250);
                            Common.Variables.Step = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Common.Logger.PrintErrorLine($"R4300 halted due to exception at PC=0x{Registers.R4300.PC:x8}: {ex.Message}");
                    Common.Logger.PrintErrorLine(ex.ToString());
                    R4300_ON = false;
                }
                Common.Measure.MeasureTime.Stop();
            });
            CpuThread.Name = "R4300";
            CpuThread.Start();
        }

        public static void StopR4300()
        {
            R4300_ON = false;
            Thread thread = CpuThread;
            if (thread != null && thread.IsAlive)
            {
                if (!thread.Join(200))
                    thread.Interrupt();
            }
            CpuThread = null;
        }

        public static uint GetCurrentPc()
        {
            return Registers.R4300.PC;
        }

        public static ulong GetCycleCounter()
        {
            return CycleCounter;
        }

        public static long GetUnknownOpcodeCount()
        {
            return UnknownOpcodeCount;
        }
    }
}
