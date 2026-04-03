using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ProjectPSX.IO;

namespace ePceCD
{
    [Serializable]
    public class BUS : MemoryBank, IDisposable
    {
        private static readonly bool TraceVdcBusWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VDC_BUS_TRACE"), "1", StringComparison.Ordinal);
        private static readonly int TraceVdcBusLimit =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VDC_BUS_TRACE_LIMIT"), out int vdcBusLim) && vdcBusLim > 0 ? vdcBusLim : 4000;
        private static readonly string? TraceVdcBusFile =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VDC_BUS_TRACE_FILE");
        private static readonly bool TraceVdcBusStdout =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_VDC_BUS_TRACE_STDOUT"), "1", StringComparison.Ordinal);
        private static readonly object TraceVdcBusFileLock = new object();
        [NonSerialized]
        private MemoryBank[] m_BankList = Array.Empty<MemoryBank>();
        [NonSerialized]
        private MemoryBank nullMemory = new MemoryBank();
        [NonSerialized]
        private MemoryBank? _serializedBankSelf;
        private byte[][]? _romPages;
        public RamBank[] memory;
        public SaveMemoryBank BRAM;

        public HuC6280 CPU;
        public PPU PPU;
        public APU APU;
        public Controller JoyPort;
        public CDRom CDRom;
        public ArcadeCard? ArcadeCard;
        public PceCdBiosRuntimeState BiosRuntimeState;

        [NonSerialized]
        private PceCdBiosDispatcher _biosDispatcher = null!;
        [NonSerialized]
        private PceCdBiosTrace _biosTrace = null!;
        [NonSerialized]
        private PceCdBiosCallCatalog _biosCatalog = null!;
        private PceCdBiosMode _biosMode = PceCdBiosMode.Rom;
        private bool _useHleBios = false;

        private bool m_EnableTIMER;
        private bool m_EnableIRQ1;
        private bool m_EnableIRQ2;
        private bool m_FiredTIMER;

        private int m_TimerValue;
        private int m_TimerOverflow;
        private bool m_TimerCounting;

        private byte m_BusCap;
        [NonSerialized]
        private int _irqTraceCount;
        [NonSerialized]
        private bool _irqTraceSuppressed;
        [NonSerialized]
        private int _traceVdcBusCount;

        private int m_OverFlowCycles;
        private int m_DeadClocks;
        private int m_PpuCycleAccumulator;
        private long m_MasterClockCycles;

        public string RomName = "";
        public string CDfile = "";
        public string GameID = "";

        public BUS(IRenderHandler render, IAudioHandler audio)
        {
            memory = new RamBank[33];
            for (int i = 0; i < memory.Length; i++)
                memory[i] = new RamBank();

            CDRom = new CDRom(this);

            CPU = new HuC6280(this);
            PPU = new PPU(render);
            JoyPort = new Controller();
            APU = new APU(audio, CDRom);

            InitializeBiosSupport();

            InitBankList();

            m_TimerOverflow = 0x10000 << 10;
            m_OverFlowCycles = 0;
        }

        public void ReadySerializable()
        {
            if (m_BankList == null || m_BankList.Length <= 0xFF)
                return;
            _serializedBankSelf = m_BankList[0xFF];
            m_BankList[0xFF] = nullMemory;
        }

        public void RestoreSerializable()
        {
            if (_serializedBankSelf == null || m_BankList == null || m_BankList.Length <= 0xFF)
                return;
            m_BankList[0xFF] = _serializedBankSelf;
            _serializedBankSelf = null;
        }

        private void InitBankList()
        {
            nullMemory = new MemoryBank();
            m_BankList = new MemoryBank[0x100];

            for (int i = 0; i < 0x100; i++)
                m_BankList[i] = nullMemory;

            m_BankList[0xF8] = memory[0];
            m_BankList[0xF9] = memory[0];
            m_BankList[0xFA] = memory[0];
            m_BankList[0xFB] = memory[0];

            // CD-ROM BRAM
            m_BankList[0xF7] = nullMemory;

            // CD-ROM RAM
            for (int i = 0; i < 8; i++)
                m_BankList[0x80 + i] = memory[i + 1];

            m_BankList[0xFF] = this;

            if (_romPages != null && _romPages.Length > 0)
                MapRomPages(_romPages);

            ApplyArcadeCardMappings();
        }

        private void RebuildBankList()
        {
            // After state-load, `memory` entries may be replaced with newly deserialized RamBank
            // objects. Recreate the full bank table from `memory` so MPR mappings point to loaded RAM.
            InitBankList();
        }


        public void DeSerializable(IRenderHandler render, IAudioHandler audio)
        {
            InitializeBiosSupport();
            RebuildBankList();
            CDRom.RebindAfterDeserialize(this);
            if (string.IsNullOrEmpty(CDfile))
                CDRom.EnterIdleState();
            CPU.BUS = this;

            PPU.host = render;
            PPU._screenBufPtr = Marshal.AllocHGlobal(1024 * 1024 * sizeof(int));

            APU.host = audio;
            APU.BindCdRom(CDRom);
            APU.RebindSelectedChannel();
            FixBankMirrors();
            CPU.RebindBanks();

            if (CDfile != "")
            {
                foreach (CDRom.CDTrack track in CDRom.tracks)
                {
                    if (!string.IsNullOrWhiteSpace(track.FileName))
                        track.File = VirtualFileSystem.OpenRead(track.FileName);
                }

                if (CDRom.FileTrack != null && !string.IsNullOrWhiteSpace(CDRom.FileTrack.FileName))
                    CDRom.FileTrack.File = VirtualFileSystem.OpenRead(CDRom.FileTrack.FileName);

                if (CDRom.currentTrack != null && !string.IsNullOrWhiteSpace(CDRom.currentTrack.FileName))
                    CDRom.currentTrack.File = VirtualFileSystem.OpenRead(CDRom.currentTrack.FileName);

                CDRom.RestoreExternalFilesAfterDeserialize();
            }

            EnsureArcadeCardConfigured();
            ApplyArcadeCardMappings();
            CPU.RebindBanks();
        }

        private void InitializeBiosSupport()
        {
            BiosRuntimeState ??= new PceCdBiosRuntimeState();
            _biosTrace = new PceCdBiosTrace(this, forceEnable: false);
            _biosCatalog = new PceCdBiosCallCatalog();
            _biosDispatcher = new PceCdBiosDispatcher(this, _biosCatalog, _biosTrace);
            _biosDispatcher.Configure(_biosMode, _useHleBios);
        }

        private void FixBankMirrors()
        {
            if (m_BankList == null || m_BankList.Length < 0x100)
                return;

            var ram0 = m_BankList[0xF8] as RamBank;
            if (ram0 != null)
            {
                m_BankList[0xF9] = ram0;
                m_BankList[0xFA] = ram0;
                m_BankList[0xFB] = ram0;
                if (memory != null && memory.Length > 0)
                    memory[0] = ram0;
            }

            if (memory != null)
            {
                for (int i = 0; i < 8 && (0x80 + i) < m_BankList.Length && (i + 1) < memory.Length; i++)
                {
                    if (m_BankList[0x80 + i] is RamBank bank)
                        memory[i + 1] = bank;
                }

                for (int i = 0; i < 24 && (0x68 + i) < m_BankList.Length && (i + 9) < memory.Length; i++)
                {
                    if (m_BankList[0x68 + i] is RamBank bank)
                        memory[i + 9] = bank;
                }
            }

            if (BRAM != null)
                m_BankList[0xF7] = BRAM;
        }

        public void Dispose()
        {

        }

        public void Reset()
        {
            m_FiredTIMER = false;
            m_TimerCounting = false;
            m_EnableIRQ1 = true;
            m_EnableIRQ2 = true;
            m_EnableTIMER = true;
            if (string.IsNullOrEmpty(CDfile))
                CDRom.EnterIdleState();
            ArcadeCard?.Reset();

            PPU.Reset();
            m_DeadClocks = 0;
            m_PpuCycleAccumulator = 0;

            CPU.Reset();
        }

        public int tick()
        {
            int cycles = PPU.CYCLES_PER_LINE / (int)DotClock.MHZ_7;
            Clock(cycles);
            return cycles;
        }

        public void Clock(int cycles)
        {
            if (cycles <= 0)
                return;

            m_MasterClockCycles += cycles;
            ClockTimer(cycles);
            ClockVideo(cycles);
            APU.Clock(cycles);
            CDRom.ClockAudio(cycles);

            if (m_DeadClocks > 0)
            {
                if (m_DeadClocks > cycles)
                    m_DeadClocks -= cycles;
                else
                    m_DeadClocks = 0;
            }
        }

        public long GetMasterClockCycles()
        {
            return m_MasterClockCycles;
        }

        private void ClockTimer(int cycles)
        {
            if (!m_TimerCounting || cycles <= 0)
                return;

            while (cycles > 0)
            {
                if (cycles >= m_TimerValue)
                {
                    cycles -= m_TimerValue;
                    m_TimerValue = m_TimerOverflow;
                    m_FiredTIMER = true;
                }
                else
                {
                    m_TimerValue -= cycles;
                    break;
                }
            }
        }

        private void ClockVideo(int cycles)
        {
            m_PpuCycleAccumulator += cycles;
            int cyclesPerLine = PPU.CYCLES_PER_LINE / (int)DotClock.MHZ_7;
            while (m_PpuCycleAccumulator >= cyclesPerLine)
            {
                PPU.tick();
                m_PpuCycleAccumulator -= cyclesPerLine;
            }
        }

        public bool TimerWaiting()
        {
            // DESTICKY IRQS
            bool sticky = m_FiredTIMER && m_EnableTIMER;
            m_FiredTIMER = false;
            return sticky;
        }

        public bool IRQ1Waiting()
        {
            return PPU.IRQPending() && m_EnableIRQ1;
        }

        public bool IRQ2Waiting()
        {
            return CDRom.IRQPending() && m_EnableIRQ2;
        }

        private void WriteTimer(int address, byte data)
        {
            switch (address)
            {
                case 0: // TIMER CODE
                    data &= 0x7F;
                    m_TimerOverflow = (data << 10) | 0x3FF;
                    m_TimerValue = m_TimerOverflow;
                    break;
                case 1:
                    m_TimerCounting = (data & 1) != 0;

                    if (m_TimerCounting)
                    {
                        m_TimerValue = m_TimerOverflow; // ???
                    }
                    else
                    {
                        m_FiredTIMER = false; // Auto clear the timer if it is disabled
                    }
                    break;
            }
        }

        public void DumpDebugSnapshot(string directory, string prefix)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Directory cannot be empty.", nameof(directory));
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Prefix cannot be empty.", nameof(prefix));

            Directory.CreateDirectory(directory);

            for (int i = 0; i < memory.Length; i++)
            {
                if (memory[i] is not RamBank ram)
                    continue;

                string path = Path.Combine(directory, $"{prefix}_ram_bank_{i:D2}.bin");
                File.WriteAllBytes(path, ram.GetSnapshot());
            }

            if (BRAM != null)
            {
                File.WriteAllBytes(
                    Path.Combine(directory, $"{prefix}_bram.bin"),
                    BRAM.GetSnapshot());
            }

            var sb = new StringBuilder();
            sb.AppendLine($"rom={RomName}");
            sb.AppendLine($"cd={CDfile}");
            sb.AppendLine($"game_id={GameID}");
            sb.AppendLine($"cpu_clock={CPU.m_Clock}");
            sb.AppendLine($"irq_timer={m_FiredTIMER}");
            sb.AppendLine($"irq1_enabled={m_EnableIRQ1}");
            sb.AppendLine($"irq2_enabled={m_EnableIRQ2}");
            sb.AppendLine($"timer_enabled={m_EnableTIMER}");
            sb.AppendLine($"timer_counting={m_TimerCounting}");
            sb.AppendLine($"timer_value={m_TimerValue}");
            sb.AppendLine($"timer_overflow={m_TimerOverflow}");
            sb.AppendLine($"overflow_cycles={m_OverFlowCycles}");
            sb.AppendLine($"dead_clocks={m_DeadClocks}");
            sb.AppendLine("mpr_map:");
            for (int i = 0; i < 8; i++)
                sb.AppendLine($"  mpr{i}=0x{CPU.PeekMpr(i):X2}");
            File.WriteAllText(Path.Combine(directory, $"{prefix}_bus.txt"), sb.ToString());

            PPU.DumpDebugSnapshot(directory, prefix);
        }

        public string BuildDeterminismTraceLine(long frameIndex, ulong frameHash)
        {
            var sb = new StringBuilder(512);
            sb.Append("frame=").Append(frameIndex);
            sb.Append(" fb_hash=").Append(frameHash.ToString("X16"));
            sb.Append(" cpu_pc=").Append(CPU.PeekProgramCounter().ToString("X4"));
            sb.Append(" cpu_a=").Append(CPU.PeekA().ToString("X2"));
            sb.Append(" cpu_x=").Append(CPU.PeekX().ToString("X2"));
            sb.Append(" cpu_y=").Append(CPU.PeekY().ToString("X2"));
            sb.Append(" cpu_s=").Append(CPU.PeekS().ToString("X2"));
            sb.Append(" cpu_p=").Append(CPU.PeekP().ToString("X2"));
            sb.Append(" cpu_clk=").Append(CPU.m_Clock);
            sb.Append(" bus_mpr=").Append(ComputeMprHash().ToString("X16"));
            sb.Append(" bus_ram=").Append(ComputeRamHash().ToString("X16"));
            sb.Append(" bus_bram=").Append(ComputeBramHash().ToString("X16"));
            sb.Append(" bus_timer=").Append((m_TimerValue & 0xFFFFFFFFu).ToString("X8"));
            sb.Append(" bus_tov=").Append((m_TimerOverflow & 0xFFFFFFFFu).ToString("X8"));
            sb.Append(" bus_tcnt=").Append(m_TimerCounting ? 1 : 0);
            sb.Append(" bus_irq1=").Append(m_EnableIRQ1 ? 1 : 0);
            sb.Append(" bus_irq2=").Append(m_EnableIRQ2 ? 1 : 0);
            sb.Append(" bus_tirq=").Append(m_EnableTIMER ? 1 : 0);
            sb.Append(" bus_tfire=").Append(m_FiredTIMER ? 1 : 0);
            sb.Append(" bus_ovf=").Append(m_OverFlowCycles);
            sb.Append(" bus_dead=").Append(m_DeadClocks);
            PPU.AppendDeterminismTrace(sb);
            CDRom.AppendDeterminismTrace(sb);
            return sb.ToString();
        }

        private static ulong Fnv1a64(ulong hash, ReadOnlySpan<byte> data)
        {
            const ulong prime = 1099511628211ul;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= prime;
            }
            return hash;
        }

        private static ulong Fnv1a64(ulong hash, byte value)
        {
            const ulong prime = 1099511628211ul;
            hash ^= value;
            hash *= prime;
            return hash;
        }

        private ulong ComputeRamHash()
        {
            ulong h = 1469598103934665603ul;
            if (memory == null)
                return h;
            for (int i = 0; i < memory.Length; i++)
            {
                var ram = memory[i];
                if (ram?.m_Ram == null)
                    continue;
                h = Fnv1a64(h, ram.m_Ram);
            }
            return h;
        }

        private ulong ComputeBramHash()
        {
            ulong h = 1469598103934665603ul;
            if (BRAM == null)
                return h;
            return Fnv1a64(h, BRAM.GetSnapshot());
        }

        private ulong ComputeMprHash()
        {
            ulong h = 1469598103934665603ul;
            for (int i = 0; i < 8; i++)
                h = Fnv1a64(h, CPU.PeekMpr(i));
            return h;
        }

        private byte ReadIRQCtrl(int address)
        {
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PCE_IRQ") == "1")
                Console.WriteLine($"[PCE-IRQ] READ addr=0x{address:X}");
            switch (address)
            {
                case 2: // Enables
                    return (byte)(
                        (m_BusCap & 0xF8) |
                        (m_EnableIRQ2 ? 0 : 0x01) |
                        (m_EnableIRQ1 ? 0 : 0x02) |
                        (m_EnableTIMER ? 0 : 0x04));
                case 3: // Pendings
                    return (byte)(
                        (m_BusCap & 0xF8) |
                        (CDRom.IRQPending() ? 0x01 : 0) |
                        (PPU.IRQPending() ? 0x02 : 0) |
                        (m_FiredTIMER ? 0x04 : 0));
                default:
                    return m_BusCap;
            }
        }

        private void WriteIRQCtrl(int address, byte data)
        {
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PCE_IRQ") == "1")
                TraceIrq($"WRITE addr=0x{address:X} data=0x{data:X2}");
            switch (address)
            {
                case 2: // Enables
                    m_EnableIRQ2 = (data & 1) == 0;
                    m_EnableIRQ1 = (data & 2) == 0;
                    m_EnableTIMER = (data & 4) == 0;
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PCE_IRQ") == "1")
                        TraceIrq($"CTRL=0x{data:X2} IRQ1={(m_EnableIRQ1 ? 1 : 0)} IRQ2={(m_EnableIRQ2 ? 1 : 0)} TIMER={(m_EnableTIMER ? 1 : 0)}");
                    break;
                case 3: // Pendings (ack timer)
                    m_FiredTIMER = false;
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PCE_IRQ") == "1")
                        TraceIrq("TIMER ack");
                    break;
            }
        }

        private void TraceIrq(string message)
        {
            if (_irqTraceSuppressed)
                return;
            if (_irqTraceCount < 50)
            {
                Console.WriteLine($"[PCE-IRQ] {message}");
                _irqTraceCount++;
            }
            else
            {
                Console.WriteLine("[PCE-IRQ] logging suppressed.");
                _irqTraceSuppressed = true;
            }
        }

        private void BitSwap(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (byte)(
                    ((buffer[i] & 0x80) >> 7) |
                    ((buffer[i] & 0x40) >> 5) |
                    ((buffer[i] & 0x20) >> 3) |
                    ((buffer[i] & 0x10) >> 1) |
                    ((buffer[i] & 0x08) << 1) |
                    ((buffer[i] & 0x04) << 3) |
                    ((buffer[i] & 0x02) << 5) |
                    ((buffer[i] & 0x01) << 7));
            }
        }

        public void LoseCycles(int cycles)
        {
            CPU.AddWaitCycles(cycles);
        }

        public void LoadCue(string file)
        {
            CDRom.LoadCue(file);
            CDfile = Path.GetFileNameWithoutExtension(file);
            EnsureArcadeCardConfigured();
            m_BankList[0xF7] = BRAM;
            ApplyArcadeCardMappings();
            CPU.RebindBanks();
        }

        public void AttachBram(SaveMemoryBank bram)
        {
            BRAM = bram;
            if (m_BankList != null && m_BankList.Length > 0xF7)
                m_BankList[0xF7] = BRAM;
        }

        public void LoadRom(string fileName, bool swap)
        {
            int i;

            using Stream file = VirtualFileSystem.OpenRead(fileName);
            byte[][] page = new byte[(file.Length - file.Length % 0x400) / 0x2000][];
            RomName = Path.GetFileNameWithoutExtension(fileName);

            //Console.WriteLine("Loading rom {0}...", fileName);

            file.Seek(file.Length % 0x400, SeekOrigin.Begin);
            for (i = 0; i < page.Length; i++)
            {
                page[i] = new byte[0x2000];
                file.Read(page[i], 0, 0x2000);
            }

            // Bit swap the rom if it boots in a page other than MPR7
            if (swap)//page[0][0x1FFF] < 0xE0)
                for (i = 0; i < page.Length; i++)
                    BitSwap(page[i]);
            _romPages = page;
            MapRomPages(page);
        }

        private void MapRomPages(byte[][]? page)
        {
            if (page == null || page.Length == 0)
                return;

            int i;
            if (m_BankList == null || m_BankList.Length != 0x100)
                InitBankList();

            // Super System Card ram only active when there is enough space
            if (page.Length <= 0x68)
            {
                for (i = 0; i < 24; i++)
                    m_BankList[i + 0x68] = memory[i + 9];
            }

            //SF2 MAPPER
            if (page.Length > 128)
            {
                for (i = 0; i < 64; i++)
                {
                    byte[][] p = new byte[4][] {
                        page[i],
                        page[i],
                        page[i],
                        page[i]
                        };

                    m_BankList[i] = new ExtendedRomBank(p);
                }

                for (i = 0; i < 64; i++)
                {
                    byte[][] p = new byte[4][] {
                        page[i+0x40],
                        page[i+0x80],
                        page[i+0xC0],
                        page[i+0x100]
                        };

                    m_BankList[i + 0x40] = new ExtendedRomBank(p);
                }
            }
            else if (page.Length == 48)
            {
                // 384kB games (requires some mirroring
                int b = 0;

                for (i = 0; i < 32; i++)
                    m_BankList[b++] = new RomBank(page[i]);
                for (i = 0; i < 48; i++)
                    m_BankList[b++] = new RomBank(page[i]);
                for (i = 0; i < 48; i++)
                    m_BankList[b++] = new RomBank(page[i]);
            }
            else
            {
                for (i = 0; i < page.Length; i++)
                    m_BankList[i] = new RomBank(page[i]);

                // Mirror remaining banks for standard HuCard ROMs.
                if (page.Length > 0)
                {
                    int mirrorEnd = Math.Min(0x80, 0x100);
                    for (i = page.Length; i < mirrorEnd; i++)
                    {
                        if (ReferenceEquals(m_BankList[i], nullMemory))
                            m_BankList[i] = m_BankList[i % page.Length];
                    }
                }
            }

            for (i = 0; i < 0x100; i++)
                GetBank((byte)i).SetMemoryPage(i);

            ApplyArcadeCardMappings();
        }

        private void EnsureArcadeCardConfigured()
        {
            if (!ShouldEnableArcadeCard())
            {
                ArcadeCard = null;
                return;
            }

            ArcadeCard ??= new ArcadeCard();
        }

        private bool ShouldEnableArcadeCard()
        {
            if (string.IsNullOrEmpty(CDfile))
                return false;

            string? env = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_ARCADE_CARD");
            if (string.IsNullOrWhiteSpace(env))
                return true;

            if (env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private void ApplyArcadeCardMappings()
        {
            if (m_BankList == null || m_BankList.Length != 0x100)
                return;

            if (ArcadeCard == null || !ShouldEnableArcadeCard())
                return;

            for (int i = 0; i < 4; i++)
            {
                var bank = new ArcadeCardDataBank(ArcadeCard, i);
                bank.SetMemoryPage(0x40 + i);
                m_BankList[0x40 + i] = bank;
            }
        }

        public MemoryBank GetBank(byte bank)
        {
            return m_BankList[bank];
        }

        public override byte ReadAt(int address)
        {
            if (address <= 0x03FF)      // VDC
            {
                LoseCycles(1);
                return PPU.ReadVDC(address & 0x3);
            }
            else if (address <= 0x07FF) // VCE
            {
                LoseCycles(1);
                return PPU.ReadVCE(address & 0x7);
            }
            else if (address <= 0x0BFF) // PSG
                return m_BusCap;
            else if (address <= 0x0FFF) // TIMER
                return m_BusCap = (byte)((m_TimerValue >> 10) & 0x7F);    // TIMER CODE
            else if (address <= 0x13FF) // I/O Port
                return m_BusCap = JoyPort.Read();
            else if (address <= 0x17FF) // INTERRUPT CONTROL
                return m_BusCap = ReadIRQCtrl(address & 3);
            else if (address <= 0x1BFF) // CDROM
                return CDRom.ReadAt(address);

            return 0xFF;
        }

        public byte ReadBlockTransferAt(int address)
        {
            if (address <= 0x03FF)      // VDC
            {
                LoseCycles(2);
                return PPU.ReadVDC(address & 0x3);
            }
            else if (address <= 0x07FF) // VCE
            {
                LoseCycles(1);
                return PPU.ReadVCE(address & 0x7);
            }
            else if (address <= 0x0BFF) // PSG
                return 0x00;
            else if (address <= 0x0FFF) // TIMER
                return 0x00;
            else if (address <= 0x13FF) // I/O Port
                return 0x00;
            else if (address <= 0x17FF) // INTERRUPT CONTROL
                return 0x00;
            else if (address <= 0x1BFF) // CDROM
                return CDRom.ReadAt(address);

            return 0xFF;
        }

        public override void WriteAt(int address, byte data)
        {
            if (address <= 0x03FF)      // VDC
            {
                LoseCycles(1);
                TraceVdcBusWrite(address & 0x3, data);
                PPU.WriteVDC(address & 0x3, data);
            }
            else if (address <= 0x07FF) // VCE
            {
                LoseCycles(1);
                PPU.WriteVCE(address & 0x7, data);
            }
            else if (address <= 0x0BFF) // PSG
                APU.Write(address, m_BusCap = data);
            else if (address <= 0x0FFF) // TIMER
                WriteTimer(address & 1, m_BusCap = data);
            else if (address <= 0x13FF) // I/O Port
                JoyPort.Write(m_BusCap = data);
            else if (address <= 0x17FF) // INTERRUPT CONTROL
                WriteIRQCtrl(address & 3, m_BusCap = data);
            else if (address <= 0x1BFF) // CD-ROM ACCESS
                CDRom.WriteAt(address, data);
        }

        public void WriteBlockTransferAt(int address, byte data)
        {
            if (address <= 0x03FF)      // VDC
            {
                LoseCycles(2);
                TraceVdcBusWrite(address & 0x3, data);
                PPU.WriteVDC(address & 0x3, data);
            }
            else if (address <= 0x07FF) // VCE
            {
                LoseCycles(1);
                PPU.WriteVCE(address & 0x7, data);
            }
            else if (address <= 0x0BFF) // PSG
                APU.Write(address, m_BusCap = data);
            else if (address <= 0x0FFF) // TIMER
                WriteTimer(address & 1, m_BusCap = data);
            else if (address <= 0x13FF) // I/O Port
                JoyPort.Write(m_BusCap = data);
            else if (address <= 0x17FF) // INTERRUPT CONTROL
                WriteIRQCtrl(address & 3, m_BusCap = data);
            else if (address <= 0x1BFF) // CD-ROM ACCESS
                CDRom.WriteAt(address, data);
        }

        private void TraceVdcBusWrite(int ioAddress, byte data)
        {
            if (!TraceVdcBusWrites || _traceVdcBusCount >= TraceVdcBusLimit)
                return;

            int reg = ioAddress == 0 ? data & 0x1F : PPU.PeekSelectedVdcRegister();
            if (reg != 0x00 && reg != 0x02 && reg != 0x05 && reg != 0x07 && reg != 0x08 && reg != 0x09 && reg != 0x0A && reg != 0x0B && reg != 0x0C && reg != 0x0D && reg != 0x10 && reg != 0x11 && reg != 0x12 && reg != 0x13)
                return;

            _traceVdcBusCount++;
            WriteVdcBusTrace(
                $"[PCE-VDCBUS] frame={PPU.PeekFrameCounter()} render={PPU.PeekRenderLine()} pc=0x{CPU.PeekProgramCounter():X4} io=0x{ioAddress:X1} reg=0x{reg:X2} data=0x{data:X2}");
        }

        private static void WriteVdcBusTrace(string line)
        {
            bool wroteFile = false;
            if (!string.IsNullOrWhiteSpace(TraceVdcBusFile))
            {
                lock (TraceVdcBusFileLock)
                {
                    File.AppendAllText(TraceVdcBusFile!, line + Environment.NewLine);
                }
                wroteFile = true;
            }

            if (!wroteFile || TraceVdcBusStdout)
                Console.WriteLine(line);
        }
    }
}
