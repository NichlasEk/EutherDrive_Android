using Ryu64.Formats;
using Ryu64.MIPS;
using Ryu64.Common;
using System;
using System.IO;
using System.Threading;

namespace Ryu64Core
{
    public class Ryu64Core
    {
        private const uint ViStatusReg = 0xA4400000;
        private const uint ViOriginReg = 0xA4400004;
        private const uint ViWidthReg = 0xA4400008;
        private const uint ViVStartReg = 0xA4400028;
        private const int RdramSizeBytes = 8 * 1024 * 1024;
        private const uint HeuristicFramebufferOriginFloor = 0x00010000u;
        private const uint UntrackedFramebufferOriginFloor = 0x00020000u;
        private const int MinimumUntrackedFramebufferScore = 12000;
        private const int MinimumTrackedFramebufferScore = 2500;
        private const int MinimumLiveFramebufferVisiblePixels = 512;
        // Keep broad framebuffer scans opt-in; prefer explicit producer hints first.
        private static readonly bool EnableFramebufferOriginScanFallback =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FB_SCAN_FALLBACK"), "1", StringComparison.Ordinal);

        private const uint AiDramAddrReg = 0xA4500000;
        private const uint AiLenReg = 0xA4500004;
        private const uint AiDacRateReg = 0xA4500010;
        private const uint MiIntrReg = 0xA4300008;
        private const uint MiIntrMaskReg = 0xA430000C;
        private const uint SpStatusReg = 0xA4040010;
        private const uint SpPcReg = 0xA4080000;
        private const uint DpcCurrentReg = 0xA4100008;
        private const uint DpcStatusReg = 0xA410000C;
        private const uint DpcStartReg = 0xA4100000;
        private const uint DpcEndReg = 0xA4100004;
        private const uint PiStatusReg = 0xA4600010;
        private const uint SiDramAddrReg = 0xA4800000;
        private const uint SiPifAddrRd64bReg = 0xA4800004;
        private const uint SiPifAddrWr64bReg = 0xA4800010;
        private const uint SiStatusReg = 0xA4800018;
        private const uint PifRamStatusByte = 0xBFC007FF;

        private Z64 rom;
        private bool isRunning = false;
        private bool _resumeLoadedState;

        private uint _lastAudioAddress;
        private uint _lastAudioLength;
        private uint _lastAudioDacrate;
        private string _lastFramebufferStatus = "Not started";
        private uint _lastFallbackFramebufferOrigin;
        private uint _lastTrackedFramebufferOrigin;
        private bool _cachedFramebufferValid;
        private uint _cachedFramebufferOrigin;
        private uint _cachedFramebufferRawViOrigin;
        private int _cachedFramebufferWidth;
        private int _cachedFramebufferHeight;
        private int _cachedFramebufferBytesPerPixel;
        private int _cachedFramebufferViType;
        private bool _cachedFramebufferProducerBacked;
        private bool _cachedFramebufferPreferSnapshot;
        private byte[] _framebufferScratch = Array.Empty<byte>();
        private bool _lastVisibleFramebufferValid;
        private uint _lastVisibleFramebufferOrigin;
        private uint _lastVisibleFramebufferRawViOrigin;
        private int _lastVisibleFramebufferWidth;
        private int _lastVisibleFramebufferHeight;
        private int _lastVisibleFramebufferBytesPerPixel;
        private int _lastVisibleFramebufferViType;
        private byte[] _lastVisibleFramebuffer = Array.Empty<byte>();

        public event EventHandler<FramebufferUpdatedEventArgs> FramebufferUpdated;
        public event EventHandler<AudioBufferEventArgs> AudioBufferReady;
        public event EventHandler<EmulationStateChangedEventArgs> StateChanged;

        public bool IsRunning => isRunning;
        public string GameName => rom?.Name?.Trim() ?? "No ROM loaded";
        public string LastFramebufferStatus => _lastFramebufferStatus;
        public string LastPerformanceStatus => R4300.memory?.PerformanceSummary ?? "perf=unavailable";
        public string LastExecutionStatus
        {
            get
            {
                if (!isRunning)
                    return "Core not running";
                if (R4300.memory == null)
                    return "Memory not initialized";

                try
                {
                    static uint SafeReadWord(uint addr)
                    {
                        try { return R4300.memory.ReadUInt32(addr); }
                        catch { return 0; }
                    }

                    uint pc = R4300.GetCurrentPc();
                    ulong cycles = R4300.GetCycleCounter();
                    long unknown = R4300.GetUnknownOpcodeCount();
                    uint viStatus = R4300.memory.ReadUInt32(ViStatusReg);
                    uint viOrigin = R4300.memory.ReadUInt32(ViOriginReg) & 0x00FFFFFF;
                    uint viWidth = R4300.memory.ReadUInt32(ViWidthReg) & 0x0FFF;
                    uint aiLen = R4300.memory.ReadUInt32(AiLenReg) & 0x3FFF8;
                    uint miIntr = R4300.memory.ReadUInt32(MiIntrReg);
                    uint miMask = R4300.memory.ReadUInt32(MiIntrMaskReg);
                    uint spStatus = R4300.memory.ReadUInt32(SpStatusReg);
                    uint spPc = R4300.memory.ReadUInt32(SpPcReg);
                    uint dpcStatus = R4300.memory.ReadUInt32(DpcStatusReg);
                    uint dpcStart = R4300.memory.ReadUInt32(DpcStartReg);
                    uint dpcEnd = R4300.memory.ReadUInt32(DpcEndReg);
                    uint dpcCurrent = R4300.memory.ReadUInt32(DpcCurrentReg);
                    uint piStatus = R4300.memory.ReadUInt32(PiStatusReg);
                    uint siStatus = R4300.memory.ReadUInt32(SiStatusReg);
                    uint siDram = R4300.memory.ReadUInt32(SiDramAddrReg);
                    uint siRd64 = R4300.memory.ReadUInt32(SiPifAddrRd64bReg);
                    uint siWr64 = R4300.memory.ReadUInt32(SiPifAddrWr64bReg);
                    uint pifCtrl = R4300.memory.ReadUInt8(PifRamStatusByte);
                    uint piDram = R4300.memory.ReadUInt32(0xA4600000);
                    uint piCart = R4300.memory.ReadUInt32(0xA4600004);
                    uint piRdLen = R4300.memory.ReadUInt32(0xA4600008);
                    uint piWrLen = R4300.memory.ReadUInt32(0xA460000C);
                    uint op = 0;
                    try { op = R4300.memory.ReadUInt32(pc); } catch { }
                    ulong cop0Status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
                    ulong cop0Cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
                    ulong cop0BadVaddr = Registers.COP0.Reg[Registers.COP0.BADVADDR_REG];
                    ulong cop0Epc = Registers.COP0.Reg[Registers.COP0.EPC_REG];
                    ulong cop0Count = Registers.COP0.Reg[Registers.COP0.COUNT_REG];
                    ulong cop0Compare = Registers.COP0.Reg[Registers.COP0.COMPARE_REG];
                    uint epc = (uint)cop0Epc;
                    uint epcOp = 0;
                    uint epcPrevOp = 0;
                    uint epcNextOp = 0;
                    try { epcOp = R4300.memory.ReadUInt32(epc); } catch { }
                    try { epcPrevOp = R4300.memory.ReadUInt32(epc - 4); } catch { }
                    try { epcNextOp = R4300.memory.ReadUInt32(epc + 4); } catch { }
                    ulong t6 = Registers.R4300.Reg[14];
                    ulong t7 = Registers.R4300.Reg[15];
                    ulong t8 = Registers.R4300.Reg[24];
                    ulong t9 = Registers.R4300.Reg[25];
                    ulong t0 = Registers.R4300.Reg[8];
                    ulong t1 = Registers.R4300.Reg[9];
                    ulong v0 = Registers.R4300.Reg[2];
                    ulong v1 = Registers.R4300.Reg[3];
                    ulong a0 = Registers.R4300.Reg[4];
                    ulong a1 = Registers.R4300.Reg[5];
                    ulong ra = Registers.R4300.Reg[31];
                    long rspGraphics = R4300.memory.RspGraphicsTaskCount;
                    long rspAudio = R4300.memory.RspAudioTaskCount;
                    long rspOther = R4300.memory.RspOtherTaskCount;
                    long rdpLists = R4300.memory.RdpDisplayListCount;
                    long rdpCommands = R4300.memory.RdpCommandCount;
                    long rdpHandled = R4300.memory.RdpHandledCommandCount;
                    long rdpSetColor = R4300.memory.RdpSetColorImageCommandCount;
                    long rdpTriangles = R4300.memory.RdpTriangleCommandCount;
                    long rdpTexRects = R4300.memory.RdpTextureRectangleCommandCount;
                    long rdpFillRects = R4300.memory.RdpFillRectangleCommandCount;
                    long rdpPixels = R4300.memory.RdpPixelWriteCount;
                    long rdpNonZeroPixels = R4300.memory.RdpNonZeroPixelWriteCount;
                    string hotPc = R4300.GetHotPcSummary();
                    uint a0w = SafeReadWord((uint)a0);
                    uint a0w4 = SafeReadWord((uint)a0 + 4u);
                    uint v0w = SafeReadWord((uint)v0);
                    uint v0w4 = SafeReadWord((uint)v0 + 4u);
                    return $"pc=0x{pc:x8} op=0x{op:x8} epc=0x{epc:x8} epcPrev=0x{epcPrevOp:x8} epcOp=0x{epcOp:x8} epcNext=0x{epcNextOp:x8} cycles={cycles} unk={unknown} viStatus=0x{viStatus:x8} viOrigin=0x{viOrigin:x8} viWidth={viWidth} aiLen=0x{aiLen:x} miIntr=0x{miIntr:x8} miMask=0x{miMask:x8} spStatus=0x{spStatus:x8} spPc=0x{spPc:x8} dpcStatus=0x{dpcStatus:x8} dpc=0x{dpcStart:x8}/0x{dpcCurrent:x8}/0x{dpcEnd:x8} piStatus=0x{piStatus:x8} piDram=0x{piDram:x8} piCart=0x{piCart:x8} piRdLen=0x{piRdLen:x8} piWrLen=0x{piWrLen:x8} siStatus=0x{siStatus:x8} siDram=0x{siDram:x8} siRd=0x{siRd64:x8} siWr=0x{siWr64:x8} pifCtl=0x{pifCtrl:x2} rsp[g={rspGraphics},a={rspAudio},o={rspOther}] rdp[lists={rdpLists},cmds={rdpHandled}/{rdpCommands},ci={rdpSetColor},tri={rdpTriangles},tex={rdpTexRects},fill={rdpFillRects},pix={rdpNonZeroPixels}/{rdpPixels}] hotPc=[{hotPc}] cop0Status=0x{cop0Status:x8} cop0Cause=0x{cop0Cause:x8} count=0x{cop0Count:x8} compare=0x{cop0Compare:x8} badv=0x{cop0BadVaddr:x8} v0=0x{v0:x16} v1=0x{v1:x16} a0=0x{a0:x16} a1=0x{a1:x16} [a0]=0x{a0w:x8} [a0+4]=0x{a0w4:x8} [v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} t0=0x{t0:x16} t1=0x{t1:x16} t6=0x{t6:x16} t7=0x{t7:x16} t8=0x{t8:x16} t9=0x{t9:x16} ra=0x{ra:x16}";
                }
                catch (Exception ex)
                {
                    return $"Execution snapshot failed: {ex.Message}";
                }
            }
        }

        public void LoadROM(string romPath)
        {
            if (isRunning)
            {
                Stop();
            }

            rom = new Z64(romPath);
            rom.Parse();

            if (!rom.HasBeenParsed)
            {
                throw new InvalidOperationException("Can't open ROM, it's either a bad ROM or it is in Little Endian (byte swapping not implemented yet).");
            }

            if (!System.IO.Directory.Exists(Variables.AppdataFolder))
            {
                System.IO.Directory.CreateDirectory(Variables.AppdataFolder);
                System.IO.Directory.CreateDirectory($"{Variables.AppdataFolder}/saves");
            }

            Settings.Parse($"{AppDomain.CurrentDomain.BaseDirectory}/Settings.ini");
            R4300.memory = new Memory(rom.AllData);
            _resumeLoadedState = false;
            _lastAudioAddress = 0;
            _lastAudioLength = 0;
            _lastAudioDacrate = 0;
            _lastFramebufferStatus = "ROM loaded";
            ClearFramebufferCandidateCache();
            _framebufferScratch = Array.Empty<byte>();
            ClearLastVisibleFramebuffer();
        }

        public void Start()
        {
            if (rom == null)
            {
                throw new InvalidOperationException("No ROM loaded. Call LoadROM first.");
            }

            if (isRunning)
            {
                return;
            }

            if (_resumeLoadedState)
            {
                R4300.ResumeR4300();
                _resumeLoadedState = false;
            }
            else
            {
                R4300.PowerOnR4300();
            }
            isRunning = true;
            StateChanged?.Invoke(this, new EmulationStateChangedEventArgs(true));
        }

        public void Stop()
        {
            if (!isRunning)
            {
                return;
            }

            R4300.StopR4300();
            isRunning = false;
            StateChanged?.Invoke(this, new EmulationStateChangedEventArgs(false));
        }

        public ulong GetCycleCounter()
        {
            return R4300.GetCycleCounter();
        }

        public void Pause()
        {
            // Note: Ryu64 doesn't have built-in pause functionality
            // You would need to implement this by controlling the CPU thread
        }

        public void Resume()
        {
            // Resume emulation if paused
        }

        public byte[] GetFramebuffer()
        {
            if (TryGetFramebuffer(out byte[] framebuffer, out int width, out int height, out int bytesPerPixel))
            {
                FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                return framebuffer;
            }

            return null;
        }

        public bool TryGetFramebuffer(out byte[] framebuffer, out int width, out int height, out int bytesPerPixel)
        {
            framebuffer = Array.Empty<byte>();
            width = 0;
            height = 0;
            bytesPerPixel = 0;

            if (!isRunning || R4300.memory == null)
            {
                _lastFramebufferStatus = "Core not running or memory not ready";
                return false;
            }

            try
            {
                uint status = R4300.memory.ReadUInt32(ViStatusReg);
                uint rawOrigin = R4300.memory.ReadUInt32(ViOriginReg) & 0x00FFFFFF;
                uint origin = rawOrigin;
                bool suspiciousViOrigin = rawOrigin < 0x00001000u;
                if (suspiciousViOrigin)
                {
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        if (!Thread.Yield())
                            Thread.Sleep(1);
                        uint retryStatus = R4300.memory.ReadUInt32(ViStatusReg);
                        uint retryOrigin = R4300.memory.ReadUInt32(ViOriginReg) & 0x00FFFFFF;
                        if ((retryStatus & 0x3u) < 2u || retryOrigin < 0x00001000u || retryOrigin >= RdramSizeBytes)
                            continue;

                        status = retryStatus;
                        rawOrigin = retryOrigin;
                        origin = rawOrigin;
                        suspiciousViOrigin = false;
                        break;
                    }
                }

                width = (int)(R4300.memory.ReadUInt32(ViWidthReg) & 0x0FFF);
                if (width <= 0)
                    width = 320;

                uint vStart = R4300.memory.ReadUInt32(ViVStartReg);
                height = InferVideoHeight(vStart);
                if (height <= 0)
                    height = 240;

                int viType = (int)(status & 0x3);
                if (viType == 2)
                    bytesPerPixel = 2;
                else if (viType == 3)
                    bytesPerPixel = 4;
                else
                {
                    _lastFramebufferStatus = $"VI mode not active (status=0x{status:x8}, viType={viType})";
                    return false;
                }

                if (width > 640) width = 640;
                if (height > 480) height = 480;
                if (origin >= RdramSizeBytes)
                {
                    _lastFramebufferStatus = $"VI origin out of RDRAM (origin=0x{origin:x8})";
                    return false;
                }

                bool producerBackedFramebufferSelected = false;
                int selectedVisiblePixels = 0;
                bool selectedVisiblePixelsKnown = false;

                // Prefer a previously written plausible VI origin over arbitrary RDRAM scans.
                uint lastPlausibleOrigin = R4300.memory.LastPlausibleViOriginWriteValue;
                if (suspiciousViOrigin
                    && lastPlausibleOrigin >= 0x00001000u
                    && lastPlausibleOrigin < RdramSizeBytes)
                {
                    origin = lastPlausibleOrigin;
                    producerBackedFramebufferSelected = true;
                    _lastFramebufferStatus =
                        $"Last plausible VI origin used (vi=0x{rawOrigin:x8} -> last=0x{origin:x8}, pc=0x{R4300.memory.LastPlausibleViOriginWritePc:x8})";
                }

                // If VI origin is still bogus, prefer the framebuffer the RDP most recently
                // rendered into. This is a direct producer hint and avoids stale green clears
                // winning over the active color image during early bring-up.
                uint rdpColorImage = R4300.memory.LastRdpColorImageAddress;
                uint rdpColorImageWidth = R4300.memory.LastRdpColorImageWidth;
                uint rdpColorImageBytesPerPixel = R4300.memory.LastRdpColorImageBytesPerPixel;
                uint rdpColorImageWriteEpoch = R4300.memory.LastRdpColorImageWriteEpoch;
                bool rdpColorImageSelected = false;
                bool preferRdpVisibleSnapshot = false;
                if (TryCopyCachedFramebuffer(rawOrigin, width, height, bytesPerPixel, viType, out framebuffer))
                    return true;

                if (rdpColorImageWriteEpoch != 0
                    && rdpColorImage >= HeuristicFramebufferOriginFloor
                    && rdpColorImage < RdramSizeBytes
                    && rdpColorImageBytesPerPixel == bytesPerPixel
                    && (rdpColorImageWidth == 0 || Math.Abs((int)rdpColorImageWidth - width) <= 16))
                {
                    int rdpVisiblePixels = CountVisibleFramebufferPixels(rdpColorImage, width, height, bytesPerPixel);
                    int viVisiblePixels = suspiciousViOrigin ? 0 : CountVisibleFramebufferPixels(origin, width, height, bytesPerPixel);
                    int rdpScore = ScoreFramebufferCandidate(rdpColorImage, width, height, bytesPerPixel);
                    if (suspiciousViOrigin && !IsRecoveredFramebufferCandidateAcceptable(rdpColorImage, rdpScore, producerBacked: true))
                    {
                        rdpVisiblePixels = 0;
                    }

                    uint rdpCandidate = suspiciousViOrigin || (rdpVisiblePixels > 0 && viVisiblePixels == 0)
                        ? rdpColorImage
                        : origin;
                    string rdpCandidateSource = rdpCandidate == rdpColorImage ? "color" : "vi";
                    if (!suspiciousViOrigin)
                    {
                        int viScore = ScoreFramebufferCandidate(origin, width, height, bytesPerPixel);
                        if (viVisiblePixels > 0 && rdpVisiblePixels > 0 && viScore != int.MinValue && (rdpScore == int.MinValue || viScore + 64 >= rdpScore))
                        {
                            rdpCandidate = origin;
                            rdpCandidateSource = "vi";
                            rdpScore = viScore;
                        }
                    }

                    int candidateScore = rdpCandidate == rdpColorImage
                        ? rdpScore
                        : ScoreFramebufferCandidate(rdpCandidate, width, height, bytesPerPixel);
                    if (IsRecoveredFramebufferCandidateAcceptable(rdpCandidate, candidateScore, producerBacked: true))
                    {
                        origin = rdpCandidate;
                        _lastTrackedFramebufferOrigin = origin;
                        selectedVisiblePixels = rdpCandidate == rdpColorImage ? rdpVisiblePixels : viVisiblePixels;
                        selectedVisiblePixelsKnown = true;
                        rdpColorImageSelected = true;
                        preferRdpVisibleSnapshot = rdpCandidate == rdpColorImage && rdpVisiblePixels > 0;
                        producerBackedFramebufferSelected = true;
                        _lastFramebufferStatus =
                            $"RDP {rdpCandidateSource} framebuffer used (vi=0x{rawOrigin:x8} -> fb=0x{origin:x8}, color=0x{rdpColorImage:x8}, width={rdpColorImageWidth}, writeEpoch={rdpColorImageWriteEpoch}, visualScore={candidateScore}, viVisible={viVisiblePixels}, rdpVisible={rdpVisiblePixels})";
                    }
                }

                if (suspiciousViOrigin && bytesPerPixel > 0 && !rdpColorImageSelected)
                {
                    uint tracked = R4300.memory.FindTrackedFramebufferOriginCandidate((uint)width, (uint)height, (uint)bytesPerPixel, origin, out ulong trackedScore, out uint trackedDirtyPages);
                    if (tracked >= 0x00001000u
                        && tracked >= HeuristicFramebufferOriginFloor
                        && tracked < RdramSizeBytes
                        && tracked != origin
                        && trackedScore != 0)
                    {
                        int trackedVisualScore = ScoreFramebufferCandidate(tracked, width, height, bytesPerPixel);
                        if (IsRecoveredFramebufferCandidateAcceptable(tracked, trackedVisualScore, producerBacked: true))
                        {
                            origin = tracked;
                            _lastTrackedFramebufferOrigin = tracked;
                            producerBackedFramebufferSelected = true;
                            selectedVisiblePixelsKnown = false;
                            _lastFramebufferStatus =
                                $"Tracked framebuffer used (vi=0x{rawOrigin:x8} -> fb=0x{origin:x8}, trackedScore={trackedScore}, dirtyPages={trackedDirtyPages}, visualScore={trackedVisualScore})";
                        }
                    }
                }

                bool recentFramebufferSelected = false;
                uint recentFramebufferOrigin = origin;
                ulong recentFramebufferRecencyScore = 0;
                ulong recentFramebufferViRecencyScore = 0;
                int recentFramebufferVisualScore = int.MinValue;

                if (suspiciousViOrigin && bytesPerPixel > 0 && !rdpColorImageSelected)
                {
                    uint bufferSizeHint = (uint)(width * height * bytesPerPixel);
                    uint recent = R4300.memory.FindRecentFramebufferOriginCandidate(bufferSizeHint, origin, out ulong recentBestScore, out ulong recentViScore);
                    if (recent >= 0x00001000u
                        && recent >= HeuristicFramebufferOriginFloor
                        && recent < RdramSizeBytes
                        && recent != origin
                        && recentBestScore > recentViScore + 4096UL)
                    {
                        uint refined = RefineFramebufferOriginNearHint(recent, width, height, bytesPerPixel, 0x10000u, 0x200u, out int refinedScore);
                        int recentScore = ScoreFramebufferCandidate(refined, width, height, bytesPerPixel);
                        if (recentScore == int.MinValue)
                        {
                            refined = recent;
                            recentScore = refinedScore;
                        }

                        bool refinedProducerBacked = false;
                        if (_lastTrackedFramebufferOrigin != 0)
                        {
                            int trackedScore = ScoreFramebufferCandidate(_lastTrackedFramebufferOrigin, width, height, bytesPerPixel);
                            if (trackedScore != int.MinValue && trackedScore + 96 >= recentScore)
                            {
                                refined = _lastTrackedFramebufferOrigin;
                                recentScore = trackedScore;
                                refinedProducerBacked = true;
                            }
                        }

                        int recentVisiblePixels = 0;
                        bool recentVisiblePixelsKnown = false;
                        bool strongRecentCandidate = false;
                        bool recentCandidateAccepted = IsRecoveredFramebufferCandidateAcceptable(refined, recentScore, refinedProducerBacked);
                        if (!recentCandidateAccepted
                            && IsStrongRecentFramebufferCandidateAcceptable(
                                refined,
                                recentScore,
                                recentBestScore,
                                recentViScore,
                                width,
                                height,
                                bytesPerPixel,
                                out recentVisiblePixels))
                        {
                            recentVisiblePixelsKnown = true;
                            strongRecentCandidate = true;
                            recentCandidateAccepted = true;
                        }

                        if (recentCandidateAccepted)
                        {
                            origin = refined;
                            bool recentProducerBacked = refinedProducerBacked || strongRecentCandidate;
                            if (recentProducerBacked)
                                _lastTrackedFramebufferOrigin = origin;
                            else
                                _lastFallbackFramebufferOrigin = origin;
                            selectedVisiblePixelsKnown = false;
                            recentFramebufferSelected = true;
                            recentFramebufferOrigin = origin;
                            recentFramebufferRecencyScore = recentBestScore;
                            recentFramebufferViRecencyScore = recentViScore;
                            recentFramebufferVisualScore = recentScore;
                            producerBackedFramebufferSelected = recentProducerBacked;
                            selectedVisiblePixels = recentVisiblePixels;
                            selectedVisiblePixelsKnown = recentVisiblePixelsKnown;
                            _lastFramebufferStatus =
                                $"Recent RDRAM framebuffer used (vi=0x{rawOrigin:x8} -> fb=0x{origin:x8}, recentScore={recentBestScore}, viScore={recentViScore}, visualScore={recentScore}, visible={recentVisiblePixels})";
                        }
                        else if (!producerBackedFramebufferSelected)
                        {
                            _lastFramebufferStatus =
                                $"No credible framebuffer yet (vi=0x{rawOrigin:x8}, rejectedRecent=0x{refined:x8}, visualScore={recentScore}, recentScore={recentBestScore}, viScore={recentViScore})";
                        }
                    }
                }

                // If a low bogus VI origin forced us onto a recency-picked candidate, let a
                // more image-like full scan override it when the chosen buffer still looks weak.
                if (recentFramebufferSelected
                    && suspiciousViOrigin
                    && EnableFramebufferOriginScanFallback
                    && recentFramebufferVisualScore < 22000)
                {
                    uint best = FindBestFramebufferOrigin(width, height, bytesPerPixel, origin, out int bestScore, out int currentScore);
                    int requiredOverrideMargin = recentFramebufferVisualScore < 12000 ? 32 : 128;
                    if (best != origin
                        && best >= HeuristicFramebufferOriginFloor
                        && IsRecoveredFramebufferCandidateAcceptable(best, bestScore, producerBacked: false)
                        && bestScore > currentScore + requiredOverrideMargin)
                    {
                        _lastFallbackFramebufferOrigin = best;
                        origin = best;
                        producerBackedFramebufferSelected = false;
                        selectedVisiblePixelsKnown = false;
                        _lastFramebufferStatus =
                            $"Visual framebuffer override used (vi=0x{rawOrigin:x8}, recent=0x{recentFramebufferOrigin:x8} -> fb=0x{origin:x8}, " +
                            $"scanScore={bestScore}, recentVisual={currentScore}, recentScore={recentFramebufferRecencyScore}, viScore={recentFramebufferViRecencyScore})";
                    }
                }

                // Bring-up fallback: some paths still produce obviously invalid VI origins
                // (for example 0x0000027f). Scan RDRAM for a stronger framebuffer candidate.
                if (EnableFramebufferOriginScanFallback
                    && suspiciousViOrigin
                    && !rdpColorImageSelected
                    && bytesPerPixel > 0)
                {
                    uint best = FindBestFramebufferOrigin(width, height, bytesPerPixel, origin, out int bestScore, out int viScore);
                    if (best != origin
                        && best >= HeuristicFramebufferOriginFloor
                        && IsRecoveredFramebufferCandidateAcceptable(best, bestScore, producerBacked: false))
                    {
                        _lastFallbackFramebufferOrigin = best;
                        origin = best;
                        producerBackedFramebufferSelected = false;
                        selectedVisiblePixelsKnown = false;
                        _lastFramebufferStatus =
                            $"Fallback VI origin used (vi=0x{rawOrigin:x8} -> fb=0x{origin:x8}, score={bestScore}, viScore={viScore})";
                    }
                }

                if (suspiciousViOrigin
                    && !producerBackedFramebufferSelected
                    && !IsRecoveredFramebufferCandidateAcceptable(origin, ScoreFramebufferCandidate(origin, width, height, bytesPerPixel), producerBacked: false))
                {
                    if (!_lastFramebufferStatus.StartsWith("No credible framebuffer yet", StringComparison.Ordinal))
                        _lastFramebufferStatus = $"No credible framebuffer yet (vi=0x{rawOrigin:x8}, candidate=0x{origin:x8})";
                    return false;
                }

                int bufferSize = checked(width * height * bytesPerPixel);
                if (bufferSize <= 0)
                {
                    _lastFramebufferStatus = "Computed framebuffer size <= 0";
                    return false;
                }
                if ((long)origin + bufferSize > RdramSizeBytes)
                {
                    _lastFramebufferStatus = $"Framebuffer range out of RDRAM (origin=0x{origin:x8}, size=0x{bufferSize:x})";
                    return false;
                }

                if (producerBackedFramebufferSelected && !selectedVisiblePixelsKnown)
                {
                    selectedVisiblePixels = CountVisibleFramebufferPixels(origin, width, height, bytesPerPixel);
                    selectedVisiblePixelsKnown = true;
                }
                byte[] framebufferScratch = GetFramebufferScratch(bufferSize);
                bool copiedVisibleSnapshot = false;
                uint visibleSnapshotOrigin = 0;
                uint visibleSnapshotEpoch = 0;
                if (producerBackedFramebufferSelected
                    && (preferRdpVisibleSnapshot || selectedVisiblePixels < MinimumLiveFramebufferVisiblePixels))
                {
                    copiedVisibleSnapshot = R4300.memory.TryCopyLastVisibleRdpFramebufferSnapshot(
                        origin,
                        (uint)width,
                        (uint)height,
                        (uint)bytesPerPixel,
                        framebufferScratch,
                        out visibleSnapshotOrigin,
                        out visibleSnapshotEpoch)
                        && visibleSnapshotOrigin == origin;

                    if (!copiedVisibleSnapshot && selectedVisiblePixels < MinimumLiveFramebufferVisiblePixels)
                    {
                        copiedVisibleSnapshot = R4300.memory.TryCopyBestVisibleRdpFramebufferSnapshot(
                            (uint)width,
                            (uint)height,
                            (uint)bytesPerPixel,
                            framebufferScratch,
                            out visibleSnapshotOrigin,
                            out visibleSnapshotEpoch);
                    }
                }

                if (copiedVisibleSnapshot)
                {
                    framebuffer = framebufferScratch;
                    _lastTrackedFramebufferOrigin = visibleSnapshotOrigin;
                    RememberFramebufferCandidate(
                        visibleSnapshotOrigin,
                        rawOrigin,
                        width,
                        height,
                        bytesPerPixel,
                        viType,
                        producerBackedFramebufferSelected,
                        preferSnapshot: true);
                    R4300.memory.NotifyFramebufferConsumerRead(visibleSnapshotOrigin, (uint)bufferSize);
                    FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                    _lastFramebufferStatus =
                        $"RDP visible snapshot used (fb=0x{visibleSnapshotOrigin:x8}, size={width}x{height} bpp={bytesPerPixel}, snapshotEpoch={visibleSnapshotEpoch})";
                    RememberLastVisibleFramebuffer(
                        visibleSnapshotOrigin,
                        rawOrigin,
                        width,
                        height,
                        bytesPerPixel,
                        viType,
                        framebuffer,
                        bufferSize);
                    return true;
                }

                framebuffer = framebufferScratch;
                for (int i = 0; i < bufferSize; i++)
                {
                    framebuffer[i] = R4300.memory.ReadUInt8PhysicalUncached(origin + (uint)i);
                }
                R4300.memory.NotifyFramebufferConsumerRead(origin, (uint)bufferSize);
                RememberFramebufferCandidate(
                    origin,
                    rawOrigin,
                    width,
                    height,
                    bytesPerPixel,
                    viType,
                    producerBackedFramebufferSelected,
                    preferRdpVisibleSnapshot);

                FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                bool keepDetailedStatus =
                    !string.IsNullOrEmpty(_lastFramebufferStatus)
                    && (_lastFramebufferStatus.StartsWith("Fallback VI origin used", StringComparison.Ordinal)
                        || _lastFramebufferStatus.StartsWith("Recent RDRAM framebuffer used", StringComparison.Ordinal)
                        || _lastFramebufferStatus.StartsWith("Last plausible VI origin used", StringComparison.Ordinal)
                        || _lastFramebufferStatus.StartsWith("RDP ", StringComparison.Ordinal)
                        || _lastFramebufferStatus.StartsWith("Tracked framebuffer used", StringComparison.Ordinal)
                        || _lastFramebufferStatus.StartsWith("Visual framebuffer override used", StringComparison.Ordinal));
                if (!keepDetailedStatus)
                    _lastFramebufferStatus = $"OK viType={viType} origin=0x{origin:x8} size={width}x{height} bpp={bytesPerPixel}";

                int visiblePixels = selectedVisiblePixelsKnown
                    ? selectedVisiblePixels
                    : CountVisibleFramebufferPixels(framebuffer, width, height, bytesPerPixel);
                if (visiblePixels >= MinimumLiveFramebufferVisiblePixels)
                {
                    RememberLastVisibleFramebuffer(
                        origin,
                        rawOrigin,
                        width,
                        height,
                        bytesPerPixel,
                        viType,
                        framebuffer,
                        bufferSize);
                }
                else if (!producerBackedFramebufferSelected
                    && TryCopyLastVisibleFramebuffer(width, height, bytesPerPixel, viType, out byte[] heldFramebuffer))
                {
                    framebuffer = heldFramebuffer;
                    FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                    _lastFramebufferStatus =
                        $"Held last visible framebuffer over blank VI fallback (vi=0x{rawOrigin:x8} -> fb=0x{_lastVisibleFramebufferOrigin:x8}, blank=0x{origin:x8})";
                }
                return true;
            }
            catch (Exception ex)
            {
                framebuffer = Array.Empty<byte>();
                width = 0;
                height = 0;
                bytesPerPixel = 0;
                _lastFramebufferStatus = $"Exception while reading framebuffer: {ex.Message}";
                return false;
            }
        }

        private byte[] GetFramebufferScratch(int bufferSize)
        {
            if (bufferSize <= 0)
                return Array.Empty<byte>();

            if (_framebufferScratch.Length != bufferSize)
                _framebufferScratch = new byte[bufferSize];

            return _framebufferScratch;
        }

        public short[] GetAudioSamples(out uint sampleRate, out uint channels)
        {
            sampleRate = 44100;
            channels = 2;

            if (!isRunning || R4300.memory == null)
                return Array.Empty<short>();

            try
            {
                uint len = R4300.memory.ReadUInt32(AiLenReg) & 0x3FFF8;
                uint addr = R4300.memory.ReadUInt32(AiDramAddrReg) & 0x00FFFFFF;
                uint dacRate = R4300.memory.ReadUInt32(AiDacRateReg) & 0x3FFF;

                if (dacRate != 0)
                {
                    const double N64NtscClock = 48681812.0;
                    int rate = (int)Math.Round(N64NtscClock / (dacRate + 1.0));
                    if (rate < 4000) rate = 4000;
                    if (rate > 96000) rate = 96000;
                    sampleRate = (uint)rate;
                }

                if (len < 4 || addr == 0)
                    return Array.Empty<short>();

                if (addr == _lastAudioAddress && len == _lastAudioLength && dacRate == _lastAudioDacrate)
                    return Array.Empty<short>();

                _lastAudioAddress = addr;
                _lastAudioLength = len;
                _lastAudioDacrate = dacRate;

                int sampleCount = (int)(len / 2);
                short[] pcm = new short[sampleCount];

                uint readPtr = addr;
                for (int i = 0; i < sampleCount; i++)
                {
                    byte hi = R4300.memory.ReadUInt8PhysicalUncached(readPtr++);
                    byte lo = R4300.memory.ReadUInt8PhysicalUncached(readPtr++);
                    pcm[i] = (short)((hi << 8) | lo);
                }

                AudioBufferReady?.Invoke(this, new AudioBufferEventArgs(ShortToByteArray(pcm), sampleRate, channels));
                return pcm;
            }
            catch
            {
                return Array.Empty<short>();
            }
        }

        public void SetInputState(InputState input)
        {
            ushort buttons = 0;
            if (input.A) buttons |= 0x8000;
            if (input.B) buttons |= 0x4000;
            if (input.Z) buttons |= 0x2000;
            if (input.Start) buttons |= 0x1000;
            if (input.Up) buttons |= 0x0800;
            if (input.Down) buttons |= 0x0400;
            if (input.Left) buttons |= 0x0200;
            if (input.Right) buttons |= 0x0100;
            if (input.L) buttons |= 0x0020;
            if (input.R) buttons |= 0x0010;
            if (input.CUp) buttons |= 0x0008;
            if (input.CDown) buttons |= 0x0004;
            if (input.CLeft) buttons |= 0x0002;
            if (input.CRight) buttons |= 0x0001;

            R4300.memory?.SetControllerState(buttons, input.StickX, input.StickY);
        }

        public void SaveState(string path)
        {
            using FileStream stream = File.Create(path);
            using BinaryWriter writer = new BinaryWriter(stream);
            SaveState(writer);
        }

        public void LoadState(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new BinaryReader(stream);
            LoadState(reader);
        }

        public void SaveState(BinaryWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (rom == null || R4300.memory == null)
                throw new InvalidOperationException("No N64 ROM loaded.");

            bool restart = isRunning;
            if (restart)
                Stop();

            try
            {
                const int version = 1;
                writer.Write(version);
                writer.Write(_lastAudioAddress);
                writer.Write(_lastAudioLength);
                writer.Write(_lastAudioDacrate);
                writer.Write(_lastFramebufferStatus ?? string.Empty);
                writer.Write(_lastFallbackFramebufferOrigin);
                writer.Write(_lastTrackedFramebufferOrigin);
                R4300.SaveState(writer);
            }
            finally
            {
                if (restart)
                    ResumeLoadedExecution();
            }
        }

        public void LoadState(BinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (rom == null || R4300.memory == null)
                throw new InvalidOperationException("No N64 ROM loaded.");

            bool restart = isRunning;
            if (restart)
                Stop();

            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported Ryu64 savestate version: {version}.");

            _lastAudioAddress = reader.ReadUInt32();
            _lastAudioLength = reader.ReadUInt32();
            _lastAudioDacrate = reader.ReadUInt32();
            _lastFramebufferStatus = reader.ReadString();
            _lastFallbackFramebufferOrigin = reader.ReadUInt32();
            _lastTrackedFramebufferOrigin = reader.ReadUInt32();
            ClearFramebufferCandidateCache();
            ClearLastVisibleFramebuffer();
            R4300.LoadState(reader);

            if (restart)
                ResumeLoadedExecution();
            else
                _resumeLoadedState = true;
        }

        private void ResumeLoadedExecution()
        {
            R4300.ResumeR4300();
            isRunning = true;
            _resumeLoadedState = false;
            StateChanged?.Invoke(this, new EmulationStateChangedEventArgs(true));
        }

        private void ClearFramebufferCandidateCache()
        {
            _cachedFramebufferValid = false;
            _cachedFramebufferOrigin = 0;
            _cachedFramebufferRawViOrigin = 0;
            _cachedFramebufferWidth = 0;
            _cachedFramebufferHeight = 0;
            _cachedFramebufferBytesPerPixel = 0;
            _cachedFramebufferViType = 0;
            _cachedFramebufferProducerBacked = false;
            _cachedFramebufferPreferSnapshot = false;
        }

        private void ClearLastVisibleFramebuffer()
        {
            _lastVisibleFramebufferValid = false;
            _lastVisibleFramebufferOrigin = 0;
            _lastVisibleFramebufferRawViOrigin = 0;
            _lastVisibleFramebufferWidth = 0;
            _lastVisibleFramebufferHeight = 0;
            _lastVisibleFramebufferBytesPerPixel = 0;
            _lastVisibleFramebufferViType = 0;
            _lastVisibleFramebuffer = Array.Empty<byte>();
        }

        private void RememberFramebufferCandidate(
            uint origin,
            uint rawViOrigin,
            int width,
            int height,
            int bytesPerPixel,
            int viType,
            bool producerBacked,
            bool preferSnapshot)
        {
            _cachedFramebufferValid = true;
            _cachedFramebufferOrigin = origin;
            _cachedFramebufferRawViOrigin = rawViOrigin;
            _cachedFramebufferWidth = width;
            _cachedFramebufferHeight = height;
            _cachedFramebufferBytesPerPixel = bytesPerPixel;
            _cachedFramebufferViType = viType;
            _cachedFramebufferProducerBacked = producerBacked;
            _cachedFramebufferPreferSnapshot = preferSnapshot;
        }

        private void RememberLastVisibleFramebuffer(
            uint origin,
            uint rawViOrigin,
            int width,
            int height,
            int bytesPerPixel,
            int viType,
            byte[] framebuffer,
            int bufferSize)
        {
            if (bufferSize <= 0 || framebuffer.Length < bufferSize)
                return;

            if (_lastVisibleFramebuffer.Length != bufferSize)
                _lastVisibleFramebuffer = new byte[bufferSize];

            Buffer.BlockCopy(framebuffer, 0, _lastVisibleFramebuffer, 0, bufferSize);
            _lastVisibleFramebufferValid = true;
            _lastVisibleFramebufferOrigin = origin;
            _lastVisibleFramebufferRawViOrigin = rawViOrigin;
            _lastVisibleFramebufferWidth = width;
            _lastVisibleFramebufferHeight = height;
            _lastVisibleFramebufferBytesPerPixel = bytesPerPixel;
            _lastVisibleFramebufferViType = viType;
        }

        private bool TryCopyLastVisibleFramebuffer(
            int width,
            int height,
            int bytesPerPixel,
            int viType,
            out byte[] framebuffer)
        {
            framebuffer = Array.Empty<byte>();
            int bufferSize = checked(width * height * bytesPerPixel);
            if (!_lastVisibleFramebufferValid
                || _lastVisibleFramebufferWidth != width
                || _lastVisibleFramebufferHeight != height
                || _lastVisibleFramebufferBytesPerPixel != bytesPerPixel
                || _lastVisibleFramebufferViType != viType
                || _lastVisibleFramebuffer.Length < bufferSize)
            {
                return false;
            }

            byte[] framebufferScratch = GetFramebufferScratch(bufferSize);
            Buffer.BlockCopy(_lastVisibleFramebuffer, 0, framebufferScratch, 0, bufferSize);
            framebuffer = framebufferScratch;
            return true;
        }

        private bool TryCopyCachedFramebuffer(
            uint rawViOrigin,
            int width,
            int height,
            int bytesPerPixel,
            int viType,
            out byte[] framebuffer)
        {
            framebuffer = Array.Empty<byte>();
            if (!_cachedFramebufferValid
                || _cachedFramebufferWidth != width
                || _cachedFramebufferHeight != height
                || _cachedFramebufferBytesPerPixel != bytesPerPixel
                || _cachedFramebufferViType != viType)
            {
                return false;
            }

            bool suspiciousViOrigin = rawViOrigin < 0x00001000u;
            if (rawViOrigin != _cachedFramebufferRawViOrigin)
            {
                if (!suspiciousViOrigin || _cachedFramebufferRawViOrigin >= 0x00001000u)
                    return false;
            }

            if (!suspiciousViOrigin && _cachedFramebufferOrigin != rawViOrigin)
                return false;

            int bufferSize = checked(width * height * bytesPerPixel);
            if (bufferSize <= 0 || (long)_cachedFramebufferOrigin + bufferSize > RdramSizeBytes)
            {
                ClearFramebufferCandidateCache();
                return false;
            }

            byte[] framebufferScratch = GetFramebufferScratch(bufferSize);
            if ((_cachedFramebufferPreferSnapshot || _cachedFramebufferProducerBacked)
                && R4300.memory.TryCopyLastVisibleRdpFramebufferSnapshot(
                    _cachedFramebufferOrigin,
                    (uint)width,
                    (uint)height,
                    (uint)bytesPerPixel,
                    framebufferScratch,
                    out uint snapshotOrigin,
                    out uint snapshotEpoch)
                && snapshotOrigin == _cachedFramebufferOrigin)
            {
                framebuffer = framebufferScratch;
                R4300.memory.NotifyFramebufferConsumerRead(snapshotOrigin, (uint)bufferSize);
                FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                _lastFramebufferStatus =
                    $"Cached RDP snapshot used (fb=0x{snapshotOrigin:x8}, size={width}x{height} bpp={bytesPerPixel}, snapshotEpoch={snapshotEpoch})";
                RememberLastVisibleFramebuffer(
                    snapshotOrigin,
                    rawViOrigin,
                    width,
                    height,
                    bytesPerPixel,
                    viType,
                    framebuffer,
                    bufferSize);
                return true;
            }

            for (int i = 0; i < bufferSize; i++)
            {
                framebufferScratch[i] = R4300.memory.ReadUInt8PhysicalUncached(_cachedFramebufferOrigin + (uint)i);
            }

            int visiblePixels = CountVisibleFramebufferPixels(framebufferScratch, width, height, bytesPerPixel);
            if (!_cachedFramebufferProducerBacked
                && !_cachedFramebufferPreferSnapshot
                && visiblePixels < MinimumLiveFramebufferVisiblePixels)
            {
                if (TryCopyLastVisibleFramebuffer(width, height, bytesPerPixel, viType, out byte[] heldFramebuffer))
                {
                    framebuffer = heldFramebuffer;
                    FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                    _lastFramebufferStatus =
                        $"Held last visible framebuffer over blank cached VI fallback (vi=0x{rawViOrigin:x8} -> fb=0x{_lastVisibleFramebufferOrigin:x8}, blank=0x{_cachedFramebufferOrigin:x8})";
                    return true;
                }

                ClearFramebufferCandidateCache();
                return false;
            }

            framebuffer = framebufferScratch;
            R4300.memory.NotifyFramebufferConsumerRead(_cachedFramebufferOrigin, (uint)bufferSize);
            FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
            _lastFramebufferStatus =
                $"Cached framebuffer used (vi=0x{rawViOrigin:x8} -> fb=0x{_cachedFramebufferOrigin:x8}, size={width}x{height} bpp={bytesPerPixel}, producerBacked={_cachedFramebufferProducerBacked})";
            if (visiblePixels >= MinimumLiveFramebufferVisiblePixels)
            {
                RememberLastVisibleFramebuffer(
                    _cachedFramebufferOrigin,
                    rawViOrigin,
                    width,
                    height,
                    bytesPerPixel,
                    viType,
                    framebuffer,
                    bufferSize);
            }
            return true;
        }

        private static int InferVideoHeight(uint vStart)
        {
            int start = (int)((vStart >> 16) & 0x03FF);
            int end = (int)(vStart & 0x03FF);
            int delta = end - start;
            if (delta <= 0)
                delta += 0x400;

            // VI V_START is encoded in half-lines on real hardware.
            int height = delta / 2;
            if (height < 120 || height > 576)
                return 240;
            return height;
        }

        private int ScoreFramebufferCandidate(uint origin, int width, int height, int bytesPerPixel)
        {
            int bufferSize = checked(width * height * bytesPerPixel);
            if (bufferSize <= 0 || (long)origin + bufferSize > RdramSizeBytes)
                return int.MinValue;

            int sampleCols = Math.Min(64, Math.Max(1, width));
            int sampleRows = Math.Min(48, Math.Max(1, height));
            int stepX = Math.Max(1, width / sampleCols);
            int stepY = Math.Max(1, height / sampleRows);

            int nonZero = 0;
            int zeroCount = 0;
            int sameLeft = 0;
            int sameUp = 0;
            int hugeDiff = 0;
            uint firstPixel = 0;
            bool firstPixelSet = false;
            bool allSame = true;
            uint[] prevRow = new uint[sampleCols];
            bool prevRowValid = false;

            for (int sy = 0; sy < sampleRows; sy++)
            {
                int y = Math.Min(height - 1, sy * stepY);
                uint left = 0;
                bool leftValid = false;

                for (int sx = 0; sx < sampleCols; sx++)
                {
                    int x = Math.Min(width - 1, sx * stepX);
                    uint pixelOffset = (uint)((y * width + x) * bytesPerPixel);
                    uint pixel;
                    if (bytesPerPixel >= 4)
                    {
                        byte r = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset);
                        byte g = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset + 1u);
                        byte b = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset + 2u);
                        pixel = (uint)((r << 16) | (g << 8) | b);
                    }
                    else if (bytesPerPixel >= 2)
                    {
                        byte hi = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset);
                        byte lo = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset + 1u);
                        pixel = (uint)(((hi << 8) | lo) & 0xFFFE);
                    }
                    else
                    {
                        byte value = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset);
                        pixel = value;
                    }

                    if (!firstPixelSet)
                    {
                        firstPixel = pixel;
                        firstPixelSet = true;
                    }
                    else if (pixel != firstPixel)
                    {
                        allSame = false;
                    }

                    if (pixel != 0)
                        nonZero++;
                    else
                        zeroCount++;

                    if (leftValid)
                    {
                        if (pixel == left)
                            sameLeft++;
                        else if (Math.Abs((long)pixel - left) > 0x1800)
                            hugeDiff++;
                    }

                    if (prevRowValid)
                    {
                        uint up = prevRow[sx];
                        if (pixel == up)
                            sameUp++;
                        else if (Math.Abs((long)pixel - up) > 0x1800)
                            hugeDiff++;
                    }

                    prevRow[sx] = pixel;
                    left = pixel;
                    leftValid = true;
                }

                prevRowValid = true;
            }

            if (nonZero == 0 || allSame)
                return int.MinValue;

            int sampleCount = sampleCols * sampleRows;
            int coherentEdges = sameLeft + sameUp;
            if (hugeDiff > sampleCount && coherentEdges < sampleCount / 3)
                return int.MinValue;

            int sparsePenalty = zeroCount * 8;

            // Favor coherent images and penalize noisy/high-frequency regions.
            return (nonZero * 5)
                + (sameLeft * 3)
                + (sameUp * 3)
                - (hugeDiff * 2)
                - sparsePenalty
                + (sampleCount - zeroCount);
        }

        private int CountVisibleFramebufferPixels(uint origin, int width, int height, int bytesPerPixel)
        {
            int bufferSize = checked(width * height * bytesPerPixel);
            if (bufferSize <= 0 || (long)origin + bufferSize > RdramSizeBytes)
                return 0;

            int visible = 0;
            for (int y = 0; y < height; y++)
            {
                uint row = origin + (uint)(y * width * bytesPerPixel);
                for (int x = 0; x < width; x++)
                {
                    uint offset = row + (uint)(x * bytesPerPixel);
                    if (bytesPerPixel >= 4)
                    {
                        byte r = R4300.memory.ReadUInt8PhysicalUncached(offset);
                        byte g = R4300.memory.ReadUInt8PhysicalUncached(offset + 1u);
                        byte b = R4300.memory.ReadUInt8PhysicalUncached(offset + 2u);
                        if (r != 0 || g != 0 || b != 0)
                            visible++;
                    }
                    else if (bytesPerPixel >= 2)
                    {
                        byte hi = R4300.memory.ReadUInt8PhysicalUncached(offset);
                        byte lo = R4300.memory.ReadUInt8PhysicalUncached(offset + 1u);
                        if ((((hi << 8) | lo) & 0xFFFE) != 0)
                            visible++;
                    }
                    else if (R4300.memory.ReadUInt8PhysicalUncached(offset) != 0)
                    {
                        visible++;
                    }
                }
            }

            return visible;
        }

        private static int CountVisibleFramebufferPixels(byte[] framebuffer, int width, int height, int bytesPerPixel)
        {
            int bufferSize = checked(width * height * bytesPerPixel);
            if (bufferSize <= 0 || framebuffer.Length < bufferSize)
                return 0;

            int visible = 0;
            for (int offset = 0; offset < bufferSize; offset += bytesPerPixel)
            {
                if (bytesPerPixel >= 4)
                {
                    if (framebuffer[offset] != 0 || framebuffer[offset + 1] != 0 || framebuffer[offset + 2] != 0)
                        visible++;
                }
                else if (bytesPerPixel >= 2)
                {
                    int pixel = (framebuffer[offset] << 8) | framebuffer[offset + 1];
                    if ((pixel & 0xFFFE) != 0)
                        visible++;
                }
                else if (framebuffer[offset] != 0)
                {
                    visible++;
                }
            }

            return visible;
        }

        private uint FindBestFramebufferOrigin(int width, int height, int bytesPerPixel, uint viOrigin, out int bestScore, out int viScore)
        {
            int bufferSize = checked(width * height * bytesPerPixel);
            viScore = ScoreFramebufferCandidate(viOrigin, width, height, bytesPerPixel);
            bestScore = viScore;
            uint bestOrigin = viOrigin;

            // Prefer reusing previous good fallback if still plausible.
            if (_lastFallbackFramebufferOrigin != 0)
            {
                int lastScore = ScoreFramebufferCandidate(_lastFallbackFramebufferOrigin, width, height, bytesPerPixel);
                if (lastScore > bestScore)
                {
                    bestScore = lastScore;
                    bestOrigin = _lastFallbackFramebufferOrigin;
                }
            }

            for (uint candidate = HeuristicFramebufferOriginFloor; (long)candidate + bufferSize <= RdramSizeBytes; candidate += 0x2000u)
            {
                int score = ScoreFramebufferCandidate(candidate, width, height, bytesPerPixel);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOrigin = candidate;
                }
            }

            // Refine around the strongest coarse hit with smaller alignment steps.
            uint refineStart = bestOrigin >= 0x4000u ? bestOrigin - 0x4000u : HeuristicFramebufferOriginFloor;
            if (refineStart < HeuristicFramebufferOriginFloor)
                refineStart = HeuristicFramebufferOriginFloor;
            uint refineEnd = Math.Min((uint)(RdramSizeBytes - bufferSize), bestOrigin + 0x4000u);
            for (uint candidate = refineStart; candidate <= refineEnd; candidate += 0x200u)
            {
                int score = ScoreFramebufferCandidate(candidate, width, height, bytesPerPixel);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOrigin = candidate;
                }
            }

            // When the VI-provided origin is obviously bogus, accept a smaller margin.
            // Low VI origins are common during bring-up and often point into scratch/state data.
            // Require a much clearer win before replacing them with a scanned fallback.
            int requiredMargin = viOrigin < 0x00001000u ? 256 : 80;
            if (bestOrigin != viOrigin && bestScore < viScore + requiredMargin)
                return viOrigin;

            return bestOrigin;
        }

        private static bool IsRecoveredFramebufferCandidateAcceptable(uint origin, int visualScore, bool producerBacked)
        {
            if (visualScore == int.MinValue)
                return false;

            if (producerBacked)
                return visualScore >= MinimumTrackedFramebufferScore;

            if (origin < UntrackedFramebufferOriginFloor)
                return false;

            return visualScore >= MinimumUntrackedFramebufferScore;
        }

        private bool IsStrongRecentFramebufferCandidateAcceptable(
            uint origin,
            int visualScore,
            ulong recentScore,
            ulong viScore,
            int width,
            int height,
            int bytesPerPixel,
            out int visiblePixels)
        {
            visiblePixels = 0;
            if (origin < HeuristicFramebufferOriginFloor
                || origin >= RdramSizeBytes
                || recentScore <= viScore + 65536UL)
            {
                return false;
            }

            visiblePixels = CountVisibleFramebufferPixels(origin, width, height, bytesPerPixel);
            int minimumVisiblePixels = Math.Max(MinimumLiveFramebufferVisiblePixels, (width * height) / 64);
            if (visiblePixels < minimumVisiblePixels)
                return false;

            return visualScore != int.MinValue || visiblePixels >= minimumVisiblePixels * 4;
        }

        private uint RefineFramebufferOriginNearHint(uint hint, int width, int height, int bytesPerPixel, uint radius, uint step, out int bestScore)
        {
            bestScore = ScoreFramebufferCandidate(hint, width, height, bytesPerPixel);
            uint bestOrigin = hint;

            int bufferSize = checked(width * height * bytesPerPixel);
            if (bufferSize <= 0)
                return hint;

            uint maxOrigin = (uint)Math.Max(0, RdramSizeBytes - bufferSize);
            uint start = hint > radius ? hint - radius : 0u;
            uint end = Math.Min(maxOrigin, hint + radius);
            start &= ~0x1FFu;
            end &= ~0x1FFu;
            if (step == 0)
                step = 0x200u;

            for (uint candidate = start; candidate <= end; candidate += step)
            {
                int score = ScoreFramebufferCandidate(candidate, width, height, bytesPerPixel);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOrigin = candidate;
                }

                if (candidate > end - step)
                    break;
            }

            return bestOrigin;
        }

        private static byte[] ShortToByteArray(short[] samples)
        {
            byte[] bytes = new byte[samples.Length * 2];
            int bi = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                short sample = samples[i];
                bytes[bi++] = (byte)(sample & 0xFF);
                bytes[bi++] = (byte)((sample >> 8) & 0xFF);
            }

            return bytes;
        }
    }

    public class FramebufferUpdatedEventArgs : EventArgs
    {
        public byte[] Framebuffer { get; }
        public uint Width { get; }
        public uint Height { get; }
        public uint BytesPerPixel { get; }

        public FramebufferUpdatedEventArgs(byte[] framebuffer, uint width, uint height, uint bytesPerPixel)
        {
            Framebuffer = framebuffer;
            Width = width;
            Height = height;
            BytesPerPixel = bytesPerPixel;
        }
    }

    public class AudioBufferEventArgs : EventArgs
    {
        public byte[] AudioBuffer { get; }
        public uint SampleRate { get; }
        public uint Channels { get; }

        public AudioBufferEventArgs(byte[] audioBuffer, uint sampleRate, uint channels)
        {
            AudioBuffer = audioBuffer;
            SampleRate = sampleRate;
            Channels = channels;
        }
    }

    public class EmulationStateChangedEventArgs : EventArgs
    {
        public bool IsRunning { get; }

        public EmulationStateChangedEventArgs(bool isRunning)
        {
            IsRunning = isRunning;
        }
    }

    public struct InputState
    {
        public bool A;
        public bool B;
        public bool Start;
        public bool Up;
        public bool Down;
        public bool Left;
        public bool Right;
        public bool L;
        public bool R;
        public bool Z;
        public bool CUp;
        public bool CDown;
        public bool CLeft;
        public bool CRight;
        public sbyte StickX;
        public sbyte StickY;
    }
}
