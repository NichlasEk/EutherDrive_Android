using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using ProjectPSX.Devices.CdRom;
using static ProjectPSX.Devices.CdRom.TrackBuilder;

namespace ProjectPSX.Devices {
    //TODO This is class is pretty much broken and the culprit ofc that many games doesn't work.
    //Need to rework timmings. An edge trigger should be implemented for interrupts
    public class CDROM {

        private Queue<byte> parameterBuffer = new Queue<byte>(16);
        private Queue<byte> responseBuffer = new Queue<byte>(16);
        private Sector currentSector = new Sector(Sector.RAW_BUFFER);
        private Sector lastReadSector = new Sector(Sector.RAW_BUFFER);
        private bool transferActive;
        private bool transferReady;
        [NonSerialized] private int bufferedSectorGeneration;
        [NonSerialized] private int loadedSectorGeneration;
        [NonSerialized] private int bufferedSectorLba = -1;
        [NonSerialized] private int latchedSubchannelLba = -1;
        [NonSerialized] private int currentSubchannelLba = -1;
        [NonSerialized] private int seekStartSubchannelLba = -1;
        [NonSerialized] private int lastReportedSubchannelLba = -1;
        [NonSerialized] private bool hasLastReportedSubchannelQ;
        [NonSerialized] private SubchannelQ lastReportedSubchannelQ;
        [NonSerialized] private bool activeLogicalSeek;
        [NonSerialized] private bool logicalSeekHoldActive;

        private bool isBusy;

        private byte IE; // InterruptEnableRegister
        private byte IF; // InterruptFlagRegister

        private byte INDEX;
        private byte STAT;
        //7  Play Playing CD-DA         ;\only ONE of these bits can be set
        //6  Seek Seeking; at a time(ie.Read/Play won't get
        //5  Read Reading data sectors  ;/set until after Seek completion)
        //4  ShellOpen Once shell open(0=Closed, 1=Is/was Open)
        //3  IdError(0=Okay, 1=GetID denied) (also set when Setmode.Bit4=1)
        //2  SeekError(0=Okay, 1=Seek error)     (followed by Error Byte)
        //1  Spindle Motor(0=Motor off, or in spin-up phase, 1=Motor on)
        //0  Error Invalid Command/parameters(followed by Error Byte)

        private int seekLoc;
        private int readLoc;

        //Mode
        //7   Speed(0 = Normal speed, 1 = Double speed)
        //6   XA - ADPCM(0 = Off, 1 = Send XA - ADPCM sectors to SPU Audio Input)
        //5   Sector Size(0 = 800h = DataOnly, 1 = 924h = WholeSectorExceptSyncBytes)
        //4   Ignore Bit(0 = Normal, 1 = Ignore Sector Size and Setloc position)
        //3   XA - Filter(0 = Off, 1 = Process only XA - ADPCM sectors that match Setfilter)
        //2   Report(0 = Off, 1 = Enable Report - Interrupts for Audio Play)
        //1   AutoPause(0 = Off, 1 = Auto Pause upon End of Track); for Audio Play
        //0   CDDA(0 = Off, 1 = Allow to Read CD - DA Sectors; ignore missing EDC)
        private bool isDoubleSpeed;
        private bool isXAADPCM;
        private enum TransferSizeMode {
            Data2048,
            Sector2328,
            Sector2340
        }
        private TransferSizeMode transferSizeMode;
        private bool isXAFilter;
        private bool isReport;
        private bool isAutoPause;
        private bool isCDDA;

        private byte filterFile;
        private byte filterChannel;

        private bool mutedAudio;
        private bool mutedXAADPCM;

        private byte pendingVolumeLtoL = 0xFF;
        private byte pendingVolumeLtoR = 0;
        private byte pendingVolumeRtoL = 0;
        private byte pendingVolumeRtoR = 0xFF;

        private byte volumeLtoL = 0xFF;
        private byte volumeLtoR = 0;
        private byte volumeRtoL = 0;
        private byte volumeRtoR = 0xFF;

        private bool cdDebug = false;
        private bool protectTrace;
        private string protectTraceFile = string.Empty;
        private bool isLidOpen = false;
        private byte lastCommand;
        private int registerReadCount;
        private int registerWriteCount;
        private int commandExecCount;
        private uint lastReadAddr;
        private uint lastWriteAddr;
        private bool fastLoadEnabled;
        private bool superFastLoadEnabled;
        private readonly bool fastLoadAggressiveReads;
        private bool fastLoadBootUnlocked;
        private bool fastLoadLicensedDiscConfirmed;
        private bool fastLoadBiosExited;
        private bool fastLoadBulkReadCompleted;
        private bool fastLoadCurrentSessionCanAccelerate;
        private bool fastLoadCurrentSessionObservedBulkRead;
        private int fastLoadSequentialReadSectors;
        private int fastLoadLastReadSector = -1;
        private int fastLoadLastSeekDistance;
        private readonly byte[] licensedDiscRegionCode = new byte[] { 0x53, 0x43, 0x45, 0x41 };

        private struct SectorHeader {
            public byte mm;
            public byte ss;
            public byte ff;
            public byte mode;
        }
        private SectorHeader sectorHeader;

        private struct SectorSubHeader {
            public byte file;
            public byte channel;
            public byte subMode;
            public byte codingInfo;

            public bool isEndOfRecord => (subMode & 0x1) != 0;
            public bool isVideo => (subMode & 0x2) != 0;
            public bool isAudio => (subMode & 0x4) != 0;
            public bool isData => (subMode & 0x8) != 0;
            public bool isTrigger => (subMode & 0x10) != 0;
            public bool isForm2 => (subMode & 0x20) != 0;
            public bool isRealTime => (subMode & 0x40) != 0;
            public bool isEndOfFile => (subMode & 0x80) != 0;
        }
        private SectorSubHeader sectorSubHeader;

        private enum Mode {
            Idle,
            Seek,
            Read,
            Play,
            TOC
        }
        private Mode mode = Mode.Idle;

        private int counter;
        private Queue<DelayedInterrupt> interruptQueue = new Queue<DelayedInterrupt>();
        public bool HasPendingWork => mode != Mode.Idle || interruptQueue.Count != 0 || (IF & IE) != 0;
        public bool RequiresFrequentSync {
            get {
                if (interruptQueue.Count != 0 || IF != 0 || edgeTrigger) {
                    return true;
                }

                return mode switch {
                    Mode.Seek => true,
                    Mode.TOC => true,
                    Mode.Read => counter + 96 >= GetReadCycles(),
                    Mode.Play => (isReport && counter + 96 >= GetPlayCycles()) || cd.isTrackChange,
                    _ => false,
                };
            }
        }

        private sealed class DelayedInterrupt {
            public int delay;
            public byte interrupt;
            public byte[]? response;
            public bool applyStateChange;
            public Mode nextMode;
            public int nextReadLoc;
            public byte nextStat;
            public bool beginFastLoadReadSession;
            public bool clearTransferState;
            public bool clearBufferedSector;
            public bool hasSeekState;
            public bool logicalSeek;
            public int seekStartLba;

            public DelayedInterrupt(int delay, byte interrupt, byte[]? response = null) {
                this.delay = delay;
                this.interrupt = interrupt;
                this.response = response;
                nextMode = Mode.Idle;
            }
        }

        //INT0 No response received(no interrupt request)
        //INT1 Received SECOND(or further) response to ReadS/ReadN(and Play+Report)
        //INT2 Received SECOND response(to various commands)
        //INT3 Received FIRST response(to any command)
        //INT4 DataEnd(when Play/Forward reaches end of disk) (maybe also for Read?)
        //INT5 Received error-code(in FIRST or SECOND response)
        //INT5 also occurs on SECOND GetID response, on unlicensed disks
        //INT5 also occurs when opening the drive door(even if no command
        //   was sent, ie.even if no read-command or other command is active)
        // INT6 N/A
        //INT7   N/A
        public class Interrupt {
            public const byte INT0_NO_RESPONSE = 0;
            public const byte INT1_SECOND_RESPONSE_READ_PLAY = 1;
            public const byte INT2_SECOND_RESPONSE = 2;
            public const byte INT3_FIRST_RESPONSE = 3;
            public const byte INT4_DATA_END = 4;
            public const byte INT5_ERROR = 5;
        }

        [NonSerialized]
        private CD cd;
        [NonSerialized]
        private SPU spu;

        public CDROM(CD cd, SPU spu) {
            this.cd = cd;
            this.spu = spu;
            byte[] resolvedRegionCode = PsxDiscBootResolver.TryResolveLicensedRegionCode(cd);
            if (resolvedRegionCode is { Length: 4 }) {
                licensedDiscRegionCode = resolvedRegionCode;
            }
            fastLoadAggressiveReads = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_FAST_LOAD_AGGRESSIVE_READS") == "1";
            RefreshRuntimeSettings();
        }

        public void RefreshRuntimeSettings() {
            cdDebug = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_CD_TRACE") == "1";
            protectTrace = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_CD_PROTECT_TRACE") == "1";
            protectTraceFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_CD_PROTECT_TRACE_FILE") ?? string.Empty;
            SyncTransientTransferState();
        }

        private static bool IsProtectTracePc(uint pc) {
            return
                (pc >= 0x80082F00 && pc <= 0x80083080)
                || (pc >= 0x80086100 && pc <= 0x80086220)
                || (pc >= 0x80096200 && pc <= 0x80096440)
                || (pc >= 0x80185680 && pc <= 0x80185820);
        }

        private static string FormatBytes(byte[]? values) {
            if (values is null || values.Length == 0) {
                return "-";
            }

            return BitConverter.ToString(values).Replace("-", "");
        }

        private void TraceProtect(string tag, string message, bool force = false) {
            if (!protectTrace) {
                return;
            }

            uint pc = global::ProjectPSX.CPU.TraceCurrentPC;
            if (!force && !IsProtectTracePc(pc)) {
                return;
            }

            string line =
                $"[CDROM][PROTECT] {tag} pc={pc:x8} mode={mode} stat={STAT:x2} if={IF:x2} ie={IE:x2} " +
                $"read={readLoc} seek={seekLoc} latch={latchedSubchannelLba} subq={currentSubchannelLba} lastq={lastReportedSubchannelLba} hold={(logicalSeekHoldActive ? 1 : 0)} " +
                $"irqq={interruptQueue.Count} rsp={responseBuffer.Count} {message}";
            Console.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(protectTraceFile)) {
                File.AppendAllText(protectTraceFile, line + Environment.NewLine);
            }
        }

        private void SyncTransientTransferState() {
            currentSector ??= new Sector(Sector.RAW_BUFFER);
            lastReadSector ??= new Sector(Sector.RAW_BUFFER);
            bufferedSectorGeneration = lastReadSector.hasLoadedData() ? 1 : 0;
            loadedSectorGeneration = currentSector.hasLoadedData() ? bufferedSectorGeneration : 0;
            if (!currentSector.hasData()) {
                transferActive = false;
            }
        }

        private void PublishReadSector(Span<byte> data, int lba) {
            lastReadSector.fillWith(data);
            bufferedSectorGeneration++;
            bufferedSectorLba = lba;
            if (cdDebug) {
                Console.WriteLine(
                    $"[CDROM] [DATA] Buffered lba={lba} bytes={data.Length} mode={transferSizeMode} " +
                    $"ready={(transferReady ? 1 : 0)} active={(transferActive ? 1 : 0)} gen={bufferedSectorGeneration}");
            }
            TryExposeBufferedSector("sector-ready");
        }

        private void TryExposeBufferedSector(string reason) {
            if (!transferReady || transferActive || !lastReadSector.hasLoadedData()) {
                if (cdDebug) {
                    Console.WriteLine(
                        $"[CDROM] [DATA] Skip expose reason={reason} ready={(transferReady ? 1 : 0)} " +
                        $"active={(transferActive ? 1 : 0)} lastLoaded={(lastReadSector.hasLoadedData() ? 1 : 0)}");
                }
                return;
            }

            if (loadedSectorGeneration == bufferedSectorGeneration) {
                if (cdDebug) {
                    Console.WriteLine(
                        $"[CDROM] [DATA] Skip stale expose reason={reason} lba={bufferedSectorLba} " +
                        $"gen={bufferedSectorGeneration}");
                }
                return;
            }

            currentSector.fillWith(lastReadSector.read());
            loadedSectorGeneration = bufferedSectorGeneration;
            transferActive = true;
            if (cdDebug) {
                Console.WriteLine(
                    $"[CDROM] [DATA] Exposed reason={reason} lba={bufferedSectorLba} bytes={currentSector.DebugSize} " +
                    $"cur={currentSector.DebugPointer}/{currentSector.DebugSize}");
            }
        }

        private bool HasPendingTransferData() {
            if (currentSector.hasData()) {
                return true;
            }

            return transferReady
                && lastReadSector.hasLoadedData()
                && loadedSectorGeneration != bufferedSectorGeneration;
        }

        public void SetFastLoadEnabled(bool enabled) {
            fastLoadEnabled = enabled;
            fastLoadBootUnlocked = !enabled;
            fastLoadLicensedDiscConfirmed = false;
            fastLoadBiosExited = !enabled;
            fastLoadBulkReadCompleted = false;
            fastLoadCurrentSessionCanAccelerate = false;
            fastLoadCurrentSessionObservedBulkRead = false;
            fastLoadSequentialReadSectors = 0;
            fastLoadLastReadSector = -1;
            fastLoadLastSeekDistance = 0;
        }

        public void SetSuperFastLoadEnabled(bool enabled) {
            superFastLoadEnabled = enabled;
        }

        private bool IsFastLoadActive => fastLoadEnabled && fastLoadBootUnlocked;

        public void NotifyBiosExited() {
            fastLoadBiosExited = true;
            UnlockFastLoadAfterBootIfReady();
        }

        private void UnlockFastLoadAfterBootIfReady() {
            if (fastLoadEnabled && fastLoadLicensedDiscConfirmed && fastLoadBiosExited) {
                fastLoadBootUnlocked = true;
            }
        }

        private int ScaleInterruptDelay(int delay) {
            if (!IsFastLoadActive || delay <= 1) {
                return delay;
            }

            return Math.Max(2_048, delay / 8);
        }

        private int ScaleQueuedDelay(int delay, bool allowFast) {
            if (superFastLoadEnabled && !fastLoadBiosExited && delay > 1) {
                return Math.Max(1_024, delay / 64);
            }

            return allowFast ? ScaleInterruptDelay(delay) : delay;
        }

        private void QueueInterrupt(byte interrupt, int delay = 50_000, bool allowFast = false, byte[]? response = null) {
            int scaledDelay = ScaleQueuedDelay(delay, allowFast);
            interruptQueue.Enqueue(new DelayedInterrupt(scaledDelay, interrupt, response));
            TraceProtect(
                "queue",
                $"intr={interrupt:x2} delay={scaledDelay} allowFast={(allowFast ? 1 : 0)} resp={FormatBytes(response)}");
        }

        private void QueueSingleByteInterrupt(byte interrupt, byte value, int delay = 50_000, bool allowFast = false) {
            QueueInterrupt(interrupt, delay, allowFast, new[] { value });
        }

        private void QueueResponseInterrupt(byte interrupt, int delay = 50_000, bool allowFast = false, params byte[] response) {
            QueueInterrupt(interrupt, delay, allowFast, response);
        }

        private void QueueStatefulSingleByteInterrupt(
            byte interrupt,
            byte responseValue,
            byte nextStat,
            Mode nextMode,
            int nextReadLoc,
            bool beginFastLoadReadSession = false,
            int delay = 50_000,
            bool allowFast = false,
            bool clearTransferState = false,
            bool clearBufferedSector = false,
            bool hasSeekState = false,
            bool logicalSeek = false,
            int seekStartLba = -1) {
            var delayed = new DelayedInterrupt(ScaleQueuedDelay(delay, allowFast), interrupt, new[] { responseValue }) {
                applyStateChange = true,
                nextMode = nextMode,
                nextReadLoc = nextReadLoc,
                nextStat = nextStat,
                beginFastLoadReadSession = beginFastLoadReadSession,
                clearTransferState = clearTransferState,
                clearBufferedSector = clearBufferedSector,
                hasSeekState = hasSeekState,
                logicalSeek = logicalSeek,
                seekStartLba = seekStartLba,
            };
            interruptQueue.Enqueue(delayed);
            TraceProtect(
                "queue-state",
                $"intr={interrupt:x2} delay={delayed.delay} nextMode={nextMode} nextRead={nextReadLoc} " +
                $"nextStat={nextStat:x2} fast={(beginFastLoadReadSession ? 1 : 0)} clear={(clearTransferState ? 1 : 0)}/{(clearBufferedSector ? 1 : 0)} " +
                $"seek={(hasSeekState ? 1 : 0)}/{(logicalSeek ? 1 : 0)} seekStart={seekStartLba} resp={responseValue:x2}");
        }

        private void DeliverInterrupt(DelayedInterrupt delayedInterrupt) {
            bool wasInterruptVisible = (IF & IE) != 0;
            TraceProtect(
                "deliver-pre",
                $"intr={delayedInterrupt.interrupt:x2} delay={delayedInterrupt.delay} state={(delayedInterrupt.applyStateChange ? 1 : 0)} " +
                $"nextMode={delayedInterrupt.nextMode} nextRead={delayedInterrupt.nextReadLoc} nextStat={delayedInterrupt.nextStat:x2} " +
                $"clear={(delayedInterrupt.clearTransferState ? 1 : 0)}/{(delayedInterrupt.clearBufferedSector ? 1 : 0)} " +
                $"resp={FormatBytes(delayedInterrupt.response)}",
                force: true);
            if (delayedInterrupt.applyStateChange) {
                if (delayedInterrupt.beginFastLoadReadSession) {
                    BeginFastLoadReadSession();
                }
                readLoc = delayedInterrupt.nextReadLoc;
                latchedSubchannelLba = -1;
                if (delayedInterrupt.hasSeekState) {
                    activeLogicalSeek = delayedInterrupt.logicalSeek;
                    seekStartSubchannelLba = delayedInterrupt.seekStartLba;
                } else if (delayedInterrupt.nextMode != Mode.Seek) {
                    activeLogicalSeek = false;
                    seekStartSubchannelLba = -1;
                }
                STAT = delayedInterrupt.nextStat;
                mode = delayedInterrupt.nextMode;
            }

            if (delayedInterrupt.clearTransferState) {
                ResetTransferState(delayedInterrupt.clearBufferedSector);
            }

            if (delayedInterrupt.response is not null && delayedInterrupt.response.Length > 0) {
                responseBuffer.EnqueueRange(delayedInterrupt.response);
            }

            IF |= delayedInterrupt.interrupt;
            if (!wasInterruptVisible && (IF & IE) != 0) {
                edgeTrigger = true;
            }
            TraceProtect(
                "deliver-post",
                $"intr={delayedInterrupt.interrupt:x2} resp={FormatBytes(delayedInterrupt.response)}",
                force: true);
        }

        private void ResetTransferState(bool clearBufferedSector) {
            currentSector.clear();
            transferActive = false;
            transferReady = false;
            loadedSectorGeneration = 0;
            latchedSubchannelLba = -1;

            if (!clearBufferedSector) {
                return;
            }

            lastReadSector.clear();
            bufferedSectorGeneration = 0;
            bufferedSectorLba = -1;
        }

        private int GetSeekCycles() {
            if (activeLogicalSeek && CanFastCompleteLogicalSeek(readLoc)) {
                return Math.Min(33868800 / 300, 50_000);
            }

            if (!IsFastLoadActive || !fastLoadBulkReadCompleted) {
                return 33868800 / 3;
            }

            return 33868800 / 30;
        }

        private int GetReadCycles() {
            if (!fastLoadAggressiveReads || !IsFastLoadActive || !fastLoadCurrentSessionCanAccelerate || fastLoadSequentialReadSectors < 64) {
                return 33868800 / (isDoubleSpeed ? 150 : 75);
            }

            return 33868800 / (isDoubleSpeed ? 600 : 300);
        }

        private int GetPlayCycles() {
            return 33868800 / (isDoubleSpeed ? 150 : 75);
        }

        private int GetPauseCompletionDelay() => mode switch {
            Mode.Read => GetReadCycles(),
            Mode.Play => GetPlayCycles(),
            _ => 50_000,
        };

        private int GetTocCycles() {
            if (!IsFastLoadActive || !fastLoadBulkReadCompleted) {
                return 33868800 / (isDoubleSpeed ? 150 : 75);
            }

            return 33868800 / 1200;
        }

        private void CompleteFastLoadReadSession() {
            if (fastLoadCurrentSessionObservedBulkRead) {
                fastLoadBulkReadCompleted = true;
            }

            fastLoadCurrentSessionCanAccelerate = false;
            fastLoadCurrentSessionObservedBulkRead = false;
            fastLoadSequentialReadSectors = 0;
            fastLoadLastReadSector = -1;
        }

        private void BeginFastLoadReadSession() {
            CompleteFastLoadReadSession();
            if (IsFastLoadActive) {
                fastLoadCurrentSessionCanAccelerate = fastLoadBulkReadCompleted && fastLoadLastSeekDistance >= 64;
            }
        }

        private bool edgeTrigger;

        private Dictionary<uint, string> commands = new Dictionary<uint, string> {
            [0x00] = "Cmd_00_Invalid",
            [0x01] = "Cmd_01_GetStat",
            [0x02] = "Cmd_02_SetLoc",
            [0x03] = "Cmd_03_Play",
            [0x04] = "Cmd_04_Forward",
            [0x05] = "Cmd_05_Backward",
            [0x06] = "Cmd_06_ReadN",
            [0x07] = "Cmd_07_MotorOn",
            [0x08] = "Cmd_08_Stop",
            [0x09] = "Cmd_09_Pause",
            [0x0A] = "Cmd_0A_Init",
            [0x0B] = "Cmd_0B_Mute",
            [0x0C] = "Cmd_0C_Demute",
            [0x0D] = "Cmd_0D_SetFilter",
            [0x0E] = "Cmd_0E_SetMode",
            [0x0F] = "Cmd_0F_GetParam",
            [0x10] = "Cmd_10_GetLocL",
            [0x11] = "Cmd_11_GetLocP",
            [0x12] = "Cmd_12_SetSession",
            [0x13] = "Cmd_13_GetTN",
            [0x14] = "Cmd_14_GetTD",
            [0x15] = "Cmd_15_SeekL",
            [0x16] = "Cmd_16_SeekP",
            [0x17] = "--- [Unimplemented]",
            [0x18] = "--- [Unimplemented]",
            [0x19] = "Cmd_19_Test",
            [0x1A] = "Cmd_1A_GetID",
            [0x1B] = "Cmd_1B_ReadS",
            [0x1C] = "Cmd_1C_Reset [Unimplemented]",
            [0x1D] = "Cmd_1D_GetQ",
            [0x1E] = "Cmd_1E_ReadTOC",
            [0x1F] = "Cmd_1F_VideoCD",
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool tick(int cycles) {
            bool triggerInterrupt = false;
            counter += cycles;

            if (interruptQueue.Count != 0) {
                var delayedInterrupt = interruptQueue.Peek();
                delayedInterrupt.delay -= cycles;
            }

            if (interruptQueue.Count != 0 && IF == 0 && interruptQueue.Peek().delay <= 0) {
                if (cdDebug) Console.WriteLine($"[CDROM] Interrupt Queue is size: {interruptQueue.Count} dequeue to IF next Interrupt: {interruptQueue.Peek()}");
                DeliverInterrupt(interruptQueue.Dequeue());
            }

            if (edgeTrigger && (IF & IE) != 0) {
                if (cdDebug) Console.WriteLine($"[CD INT] Triggering {IF:x8}");
                edgeTrigger = false;
                isBusy = false;
                triggerInterrupt = true;
            }

            switch (mode) {
                case Mode.Idle:
                    counter = 0;
                    return triggerInterrupt;

                case Mode.Seek: //Hardcoded seek time...
                    if (counter < GetSeekCycles() || interruptQueue.Count != 0) {
                        return triggerInterrupt;
                    }
                    counter = 0;
                    currentSubchannelLba = activeLogicalSeek
                        ? Math.Max(0, readLoc - 2)
                        : readLoc;
                    logicalSeekHoldActive = activeLogicalSeek;
                    activeLogicalSeek = false;
                    seekStartSubchannelLba = -1;
                    latchedSubchannelLba = -1;
                    TryLatchReportedSubchannelQ(currentSubchannelLba);
                    mode = Mode.Idle;
                    STAT = (byte)(STAT & (~0x40));

                    QueueSingleByteInterrupt(Interrupt.INT2_SECOND_RESPONSE, STAT, allowFast: true);
                    break;

                case Mode.Read:
                    int readCycles = GetReadCycles();
                    if (counter < readCycles || interruptQueue.Count != 0 || IF != 0) {
                        return triggerInterrupt;
                    }
                    counter -= readCycles;

                    int deliveredSector = readLoc;
                    Track currentTrack = cd.getTrackFromLoc(deliveredSector);
                    if (currentTrack.isAudio && !isCDDA) {
                        if (cdDebug) {
                            Console.WriteLine($"[CDROM] Read denied on audio sector lba={deliveredSector} track={currentTrack.number}");
                        }

                        TraceProtect("read-audio-denied", $"lba={deliveredSector} track={currentTrack.number}");
                        logicalSeekHoldActive = false;
                        CompleteFastLoadReadSession();
                        STAT = 0x06;
                        mode = Mode.Idle;
                        QueueInterrupt(Interrupt.INT5_ERROR, response: new byte[] { STAT, 0x04 });
                        return triggerInterrupt;
                    }

                    byte[] readSector = cd.Read(readLoc++);
                    latchedSubchannelLba = deliveredSector;
                    currentSubchannelLba = deliveredSector;
                    logicalSeekHoldActive = false;
                    bool sectorSubQValid = TryLatchReportedSubchannelQ(deliveredSector);
                    TraceProtect("sector", $"lba={deliveredSector} bytes={readSector.Length} subqValid={(sectorSubQValid ? 1 : 0)}");

                    if (cdDebug) {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine($"Reading readLoc: {readLoc - 1} seekLoc: {seekLoc} size: {readSector.Length}");
                        Console.ResetColor();
                    }

                    //first 12 are the sync header
                    sectorHeader.mm = readSector[12];
                    sectorHeader.ss = readSector[13];
                    sectorHeader.ff = readSector[14];
                    sectorHeader.mode = readSector[15];

                    sectorSubHeader.file = readSector[16];
                    sectorSubHeader.channel = readSector[17];
                    sectorSubHeader.subMode = readSector[18];
                    sectorSubHeader.codingInfo = readSector[19];

                    if (isXAADPCM && sectorSubHeader.isForm2) {
                        if (sectorSubHeader.isEndOfFile) {
                            if (cdDebug) Console.WriteLine("[CDROM] XA ON: End of File!");
                        }

                        if (sectorSubHeader.isRealTime && sectorSubHeader.isAudio) {
                            if (isXAFilter && (filterFile != sectorSubHeader.file || filterChannel != sectorSubHeader.channel)) {
                                if (cdDebug) Console.WriteLine("[CDROM] XA Filter: file || channel");
                                return triggerInterrupt;
                            }

                            if (cdDebug) Console.WriteLine("[CDROM] XA ON: Realtime + Audio");

                            if (!mutedAudio && !mutedXAADPCM) {
                                byte[] decodedXaAdpcm = XaAdpcm.Decode(readSector, sectorSubHeader.codingInfo);
                                applyVolume(decodedXaAdpcm);
                                spu.pushCdBufferSamples(decodedXaAdpcm);
                            }

                            return triggerInterrupt;
                        }
                    }

                    switch (transferSizeMode) {
                        case TransferSizeMode.Sector2340:
                            PublishReadSector(readSector.AsSpan().Slice(12, 2340), deliveredSector);
                            break;
                        case TransferSizeMode.Sector2328:
                            PublishReadSector(readSector.AsSpan().Slice(12, 2328), deliveredSector);
                            break;
                        default:
                            PublishReadSector(readSector.AsSpan().Slice(24, 0x800), deliveredSector);
                            break;
                    }
                    if (fastLoadLastReadSector + 1 == deliveredSector) {
                        fastLoadSequentialReadSectors++;
                    } else {
                        fastLoadSequentialReadSectors = 1;
                    }
                    fastLoadLastReadSector = deliveredSector;
                    if (fastLoadSequentialReadSectors >= 128) {
                        fastLoadCurrentSessionObservedBulkRead = true;
                    }

                    QueueInterrupt(Interrupt.INT1_SECOND_RESPONSE_READ_PLAY, response: new[] { STAT });
                    break;

                case Mode.Play:
                    int playCycles = GetPlayCycles();
                    while (counter >= playCycles && mode == Mode.Play) {
                        counter -= playCycles;

                        int playSector = readLoc;
                        byte[] playRawSector = cd.Read(readLoc++);
                        latchedSubchannelLba = playSector;
                        currentSubchannelLba = playSector;
                        logicalSeekHoldActive = false;
                        bool playSubQValid = TryLatchReportedSubchannelQ(playSector);
                        TraceProtect("sector", $"lba={playSector} bytes={playRawSector.Length} subqValid={(playSubQValid ? 1 : 0)}");

                        if (cdDebug) {
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.WriteLine($"Reading readLoc: {readLoc - 1} seekLoc: {seekLoc} size: {playRawSector.Length}");
                            Console.ResetColor();
                        }

                        if (!mutedAudio) {
                            applyVolume(playRawSector);
                            spu.pushCdBufferSamples(playRawSector);
                        }

                        if (isAutoPause && cd.isTrackChange) {
                            QueueSingleByteInterrupt(Interrupt.INT4_DATA_END, STAT, 1);

                            STAT = 0x2;
                            mode = Mode.Idle;
                            break;
                        }

                        if (isReport && IF == 0 && interruptQueue.Count == 0) {
                            SubchannelQ subQ = GetReportedSubchannelQ(GetSubchannelQueryLba());
                            byte[] reportResponse = new byte[8];
                            reportResponse[0] = STAT;
                            reportResponse[1] = subQ.Track;
                            reportResponse[2] = subQ.Index;

                            if ((subQ.AbsoluteFrame & 0x10) != 0) {
                                reportResponse[3] = subQ.Minute;
                                reportResponse[4] = (byte)(subQ.Second | 0x80);
                                reportResponse[5] = subQ.Frame;
                            } else {
                                reportResponse[3] = subQ.AbsoluteMinute;
                                reportResponse[4] = subQ.AbsoluteSecond;
                                reportResponse[5] = subQ.AbsoluteFrame;
                            }

                            reportResponse[6] = 0x80; // peekLo
                            reportResponse[7] = 0x80; // peekHi

                            QueueInterrupt(Interrupt.INT1_SECOND_RESPONSE_READ_PLAY, 1, response: reportResponse);
                        }
                    }
                    break;

                case Mode.TOC:
                    if (counter < GetTocCycles() || interruptQueue.Count != 0) {
                        return triggerInterrupt;
                    }
                    mode = Mode.Idle;
                    QueueSingleByteInterrupt(Interrupt.INT2_SECOND_RESPONSE, STAT, allowFast: true);
                    counter = 0;
                    break;
            }
            return triggerInterrupt;

        }

        public uint load(uint addr) {
            registerReadCount++;
            lastReadAddr = addr;
            switch (addr) {
                case 0x1F801800:
                    if (cdDebug) Console.WriteLine($"[CDROM] [L00] STATUS: {STATUS():x2}");
                    return STATUS();

                case 0x1F801801:
                    //Console.ReadLine();
                    //if (w == Width.HALF || w == Width.WORD) Console.WriteLine("WARNING RESPONSE BUFFER LOAD " + w);

                    if (responseBuffer.Count > 0) {
                        if (cdDebug) Console.WriteLine("[CDROM] [L01] RESPONSE " + responseBuffer.Peek().ToString("x8"));
                        byte response = responseBuffer.Dequeue();
                        TraceProtect("resp-pop", $"value={response:x2}", force: true);
                        return response;
                    }

                    if (cdDebug) Console.WriteLine("[CDROM] [L01] RESPONSE 0xFF");

                    return 0xFF;

                case 0x1F801802:
                    if (cdDebug) Console.WriteLine("[CDROM] [L02] DATA");
                    if (!transferActive && transferReady) {
                        TryExposeBufferedSector("data-port");
                    }
                    if (!currentSector.hasData()) {
                        return 0;
                    }
                    byte value8 = currentSector.readByte();
                    if (!currentSector.hasData()) {
                        currentSector.clear();
                        transferActive = false;
                        loadedSectorGeneration = 0;
                    }
                    return value8;

                case 0x1F801803:
                    switch (INDEX) {
                        case 0:
                        case 2:
                            if (cdDebug) Console.WriteLine("[CDROM] [L03.0] IE: {0}", ((uint)(0xe0 | IE)).ToString("x8"));
                            return (uint)(0xe0 | IE);
                        case 1:
                        case 3:
                            if (cdDebug) Console.WriteLine("[CDROM] [L03.1] IF: {0}", ((uint)(0xe0 | IF)).ToString("x8"));
                            return (uint)(0xe0 | IF);
                        default:
                            if (cdDebug) Console.WriteLine("[CDROM] [L03.x] Unimplemented");
                            return 0;
                    }

                default: return 0;
            }
        }

        public void write(uint addr, uint value) {
            registerWriteCount++;
            lastWriteAddr = addr;
            switch (addr) {
                case 0x1F801800:
                    if (cdDebug) Console.WriteLine($"[CDROM] [W00] I: {value:x8}");
                    INDEX = (byte)(value & 0x3);
                    break;
                case 0x1F801801:
                    if (INDEX == 0) {
                        if (cdDebug) {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"[CDROM] [W01.0] [COMMAND] {value:x2} {commands.GetValueOrDefault(value, "---")}");
                            Console.ResetColor();
                        }
                        ExecuteCommand(value);
                    } else if (INDEX == 3) {
                        if (cdDebug) Console.WriteLine($"[CDROM] [W01.3] pendingVolumeRtoR: {value:x8}");
                        pendingVolumeRtoR = (byte)value;
                    } else {
                        if (cdDebug) Console.WriteLine($"[CDROM] [Unhandled Write] Index: {INDEX:x8} Access: {addr:x8} Value: {value:x8}");
                    }
                    break;
                case 0x1F801802:
                    switch (INDEX) {
                        case 0:
                            if (cdDebug) Console.WriteLine($"[CDROM] [W02.0] Parameter: {value:x8}");
                            parameterBuffer.Enqueue((byte)value);
                            break;
                        case 1:
                            if (cdDebug) Console.WriteLine($"[CDROM] [W02.1] Set IE: {value:x8}");
                            bool wasInterruptVisible = (IF & IE) != 0;
                            IE = (byte)(value & 0x1F);
                            if (!wasInterruptVisible && (IF & IE) != 0) {
                                edgeTrigger = true;
                            }
                            break;

                        case 2:
                            if (cdDebug) Console.WriteLine($"[CDROM] [W02.2] pendingVolumeLtoL: {value:x8}");
                            pendingVolumeLtoL = (byte)value;
                            break;

                        case 3:
                            if (cdDebug) Console.WriteLine($"[CDROM] [W02.3] pendingVolumeRtoL: {value:x8}");
                            pendingVolumeRtoL = (byte)value;
                            break;

                        default:
                            if (cdDebug) Console.WriteLine($"[CDROM] [Unhandled Write] Access: {addr:x8} Value: {value:x8}");
                            break;
                    }
                    break;
                case 0x1F801803:
                    switch (INDEX) {
                        case 0:
                            // 1F801803h.Index0 - Request Register(W)
                            //0 - 4 0    Not used(should be zero)
                            //5   SMEN Want Command Start Interrupt on Next Command(0 = No change, 1 = Yes)
                            //6   BFWR...
                            //7   BFRD Want Data(0 = No / Reset Data Fifo, 1 = Yes / Load Data Fifo)
                            if ((value & 0x80) != 0) {
                                transferReady = true;
                                if (cdDebug) {
                                    Console.WriteLine(
                                        $"[CDROM] [W03.0] Data Request cur={currentSector.DebugPointer}/{currentSector.DebugSize} " +
                                        $"last={lastReadSector.DebugPointer}/{lastReadSector.DebugSize} " +
                                        $"loadedGen={loadedSectorGeneration} bufferedGen={bufferedSectorGeneration}");
                                }
                                TryExposeBufferedSector("request");
                            } else {
                                if (cdDebug) {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine(
                                        $"[CDROM] [W03.0] Data Clear cur={currentSector.DebugPointer}/{currentSector.DebugSize} " +
                                        $"last={lastReadSector.DebugPointer}/{lastReadSector.DebugSize}");
                                    Console.ResetColor();
                                }
                                currentSector.clear();
                                transferActive = false;
                                transferReady = false;
                                loadedSectorGeneration = 0;
                            }
                            break;
                        case 1:
                            IF &= (byte)~(value & 0x1F);
                            if (cdDebug) Console.WriteLine($"[CDROM] [W03.1] Set IF: {value:x8} -> IF = {IF:x8}");
                            TraceProtect("if-ack", $"value={value:x2} newIf={IF:x2}", force: true);
                            if (interruptQueue.Count > 0 && interruptQueue.Peek().delay <= 0) {
                                DeliverInterrupt(interruptQueue.Dequeue());
                            }

                            if ((value & 0x40) == 0x40) {
                                if (cdDebug) Console.WriteLine($"[CDROM] [W03.1 Parameter Buffer Clear] value {value:x8}");
                                parameterBuffer.Clear();
                            }
                            break;

                        case 2:
                            if (cdDebug) Console.WriteLine($"[CDROM] [W03.2] pendingVolumeLtoR: {value:x8}");
                            pendingVolumeLtoR = (byte)value;
                            break;

                        case 3:
                            if (cdDebug) Console.WriteLine($"[CDROM] [W03.3] ApplyVolumes: {value:x8}");
                            mutedXAADPCM = (value & 0x1) != 0;
                            bool applyVolume = (value & 0x20) != 0;
                            if (applyVolume) {
                                volumeLtoL = pendingVolumeLtoL;
                                volumeLtoR = pendingVolumeLtoR;
                                volumeRtoL = pendingVolumeRtoL;
                                volumeRtoR = pendingVolumeRtoR;
                            }
                            break;

                        default:
                            if (cdDebug) Console.WriteLine($"[CDROM] [Unhandled Write] Access: {addr:x8} Value: {value:x8}");
                            break;
                    }
                    break;
                default:
                    if (cdDebug) Console.WriteLine($"[CDROM] [Unhandled Write] Access: {addr:x8} Value: {value:x8}");
                    break;
            }
        }

        private void ExecuteCommand(uint value) {
            commandExecCount++;
            lastCommand = (byte)value;
            if (cdDebug) Console.WriteLine($"[CDROM] Command {value:x4}");
            //Console.WriteLine($"PRE STAT {STAT:x2}");
            List<DelayedInterrupt>? preservedAsyncReadInterrupts = null;
            bool preserveAsyncReadInterrupts =
                (mode == Mode.Read || mode == Mode.Play)
                && (value == 0x01 || value == 0x10 || value == 0x11 || value == 0x1D);
            if (!preserveAsyncReadInterrupts) {
                int clearedInterrupts = 0;
                if (mode == Mode.Read || mode == Mode.Play) {
                    preservedAsyncReadInterrupts = new List<DelayedInterrupt>();
                    while (interruptQueue.Count > 0) {
                        DelayedInterrupt delayedInterrupt = interruptQueue.Dequeue();
                        if (IsPreservableAsyncReadInterrupt(delayedInterrupt)) {
                            preservedAsyncReadInterrupts.Add(delayedInterrupt);
                        } else {
                            clearedInterrupts++;
                        }
                    }
                } else {
                    clearedInterrupts = interruptQueue.Count;
                    interruptQueue.Clear();
                }

                if (clearedInterrupts > 0) {
                    TraceProtect(
                        "queue-clear",
                        $"cmd={value:x2} cleared={clearedInterrupts} preserve=0",
                        force: true);
                }
                if (preservedAsyncReadInterrupts is { Count: > 0 }) {
                    TraceProtect(
                        "queue-keep",
                        $"cmd={value:x2} kept={preservedAsyncReadInterrupts.Count} intr=01",
                        force: true);
                }
            }
            if (responseBuffer.Count > 0) {
                TraceProtect(
                    "rsp-clear",
                    $"cmd={value:x2} cleared={responseBuffer.Count} preserve=0",
                    force: true);
            }
            responseBuffer.Clear();
            isBusy = true;
            TraceProtect(
                "cmd",
                $"value={value:x2} preserve={(preserveAsyncReadInterrupts ? 1 : 0)}");
            switch (value) {
                case 0x00: Cmd_00_Invalid(); break;
                case 0x01: Cmd_01_GetStat(); break;
                case 0x02: Cmd_02_SetLoc(); break;
                case 0x03: Cmd_03_Play(); break;
                //case 0x04: Cmd_04_Forward(); break; //todo
                //case 0x05: Cmd_05_Backward(); break; //todo
                case 0x06: Cmd_06_ReadN(); break;
                case 0x07: Cmd_07_MotorOn(); break;
                case 0x08: Cmd_08_Stop(); break;
                case 0x09: Cmd_09_Pause(); break;
                case 0x0A: Cmd_0A_Init(); break;
                case 0x0B: Cmd_0B_Mute(); break;
                case 0x0C: Cmd_0C_Demute(); break;
                case 0x0D: Cmd_0D_SetFilter(); break;
                case 0x0E: Cmd_0E_SetMode(); break;
                //case 0x0F: Cmd_0F_GetParam(); break; //todo
                case 0x10: Cmd_10_GetLocL(); break;
                case 0x11: Cmd_11_GetLocP(); break;
                case 0x12: Cmd_12_SetSession(); break;
                case 0x13: Cmd_13_GetTN(); break;
                case 0x14: Cmd_14_GetTD(); break;
                case 0x15: Cmd_15_SeekL(); break;
                case 0x16: Cmd_16_SeekP(); break;
                case 0x19: Cmd_19_Test(); break;
                case 0x1A: Cmd_1A_GetID(); break;
                case 0x1B: Cmd_1B_ReadS(); break;
                case 0x1D: Cmd_1D_GetQ(); break;
                case 0x1E: Cmd_1E_ReadTOC(); break;
                case 0x1F: Cmd_1F_VideoCD(); break;
                case uint _ when value >= 0x50 && value <= 0x57: Cmd_5x_lockUnlock(); break;
                default: UnimplementedCDCommand(value); break;
            }
            if (preservedAsyncReadInterrupts is { Count: > 0 }) {
                foreach (DelayedInterrupt delayedInterrupt in preservedAsyncReadInterrupts) {
                    interruptQueue.Enqueue(delayedInterrupt);
                }
                TraceProtect(
                    "queue-restore",
                    $"cmd={value:x2} restored={preservedAsyncReadInterrupts.Count} intr=01",
                    force: true);
            }
            //Console.WriteLine($"POST STAT {STAT:x2}");
        }

        public string DebugSummary() {
            string modeText = mode.ToString();
            string commandText = commands.TryGetValue(lastCommand, out string? name) ? name : $"0x{lastCommand:X2}";
            return
                $"cmd={commandText} mode={modeText} stat={STAT:x2} ie={IE:x2} if={IF:x2} busy={(isBusy ? 1 : 0)} " +
                $"seek={seekLoc} read={readLoc} ctr={counter} irqq={interruptQueue.Count} rsp={responseBuffer.Count} " +
                $"buf={currentSector.DebugPointer}/{currentSector.DebugSize} last={lastReadSector.DebugPointer}/{lastReadSector.DebugSize} " +
                $"io=r{registerReadCount}/w{registerWriteCount}/c{commandExecCount} idx={INDEX} last=({lastReadAddr:x8}/{lastWriteAddr:x8}) " +
                $"fast={(IsFastLoadActive ? 1 : 0)} bulk={(fastLoadBulkReadCompleted ? 1 : 0)} seq={fastLoadSequentialReadSectors} seekdist={fastLoadLastSeekDistance}";
        }

        private void Cmd_00_Invalid() {
            QueueInvalidCommandError();
        }

        private void Cmd_01_GetStat() {
            if (!isLidOpen) {
                STAT = (byte)(STAT & (~0x18));
                STAT |= 0x2;
            }

            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
        }

        private void Cmd_02_SetLoc() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            byte mm = parameterBuffer.Dequeue();
            byte ss = parameterBuffer.Dequeue();
            byte ff = parameterBuffer.Dequeue();

            //Console.WriteLine($"[CDROM] setLoc BCD {mm:x2}:{ss:x2}:{ff:x2}");


            int minute = BcdToDec(mm);
            int second = BcdToDec(ss);
            int sector = BcdToDec(ff);

            //There are 75 sectors on a second
            seekLoc = sector + (second * 75) + (minute * 60 * 75);

            if (seekLoc < 0) {
                Console.WriteLine($"[CDROM] WARNING NEGATIVE setLOC {seekLoc:x8}");
                seekLoc = 0;
            }

            if (cdDebug) {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"[CDROM] setLoc {mm:x2}:{ss:x2}:{ff:x2} Loc: {seekLoc:x8}");
                Console.ResetColor();
            }

            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
        }

        private void Cmd_03_Play() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }
            //If theres a trackN param it seeks and plays from the start location of it
            CompleteFastLoadReadSession();
            int track = 0;
            int targetReadLoc;
            if (parameterBuffer.Count > 0 && parameterBuffer.Peek() != 0) {
                track = BcdToDec(parameterBuffer.Dequeue());
                if (track >= 1 && track <= cd.tracks.Count) {
                    targetReadLoc = cd.tracks[track - 1].lbaStart;
                } else {
                    Track currentTrack = cd.getTrackFromLoc(readLoc);
                    track = currentTrack.number;
                    targetReadLoc = currentTrack.lbaStart;
                }
                //else it plays from the previously seekLoc and seeks if not done (actually not checking if already seeked)
            } else {
                targetReadLoc = seekLoc;
            }

            Console.WriteLine($"[CDROM] CDDA Play Triggered Track: {track} readLoc: {targetReadLoc}");
            seekLoc = targetReadLoc;
            QueueStatefulSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, 0x82, 0x82, Mode.Play, targetReadLoc);
        }

        private void Cmd_06_ReadN() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            UnlockFastLoadAfterBootIfReady();
            byte readStat = 0x22;
            QueueStatefulSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, readStat, readStat, Mode.Read, seekLoc, beginFastLoadReadSession: true);
        }

        private void Cmd_07_MotorOn() {
            CompleteFastLoadReadSession();
            STAT = 0x2;

            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
            QueueSingleByteInterrupt(Interrupt.INT2_SECOND_RESPONSE, STAT);
        }

        private void Cmd_08_Stop() {
            CompleteFastLoadReadSession();
            byte firstStat = 0x2;
            byte secondStat = 0;
            STAT = firstStat;
            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, firstStat);
            QueueStatefulSingleByteInterrupt(
                Interrupt.INT2_SECOND_RESPONSE,
                secondStat,
                secondStat,
                Mode.Idle,
                readLoc,
                delay: GetPauseCompletionDelay(),
                allowFast: true,
                clearTransferState: true,
                clearBufferedSector: true);
        }

        private void Cmd_09_Pause() {
            CompleteFastLoadReadSession();
            byte firstStat = STAT;
            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, firstStat);
            QueueStatefulSingleByteInterrupt(
                Interrupt.INT2_SECOND_RESPONSE,
                0x2,
                0x2,
                Mode.Idle,
                readLoc,
                delay: GetPauseCompletionDelay(),
                allowFast: true);
        }

        private void Cmd_0A_Init() {
            CompleteFastLoadReadSession();
            STAT = 0x2;

            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
            QueueSingleByteInterrupt(Interrupt.INT2_SECOND_RESPONSE, STAT);
        }

        private void Cmd_0B_Mute() {
            mutedAudio = true;
            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
        }

        private void Cmd_0C_Demute() {
            mutedAudio = false;
            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
        }

        private void Cmd_0D_SetFilter() {
            filterFile = parameterBuffer.Dequeue();
            filterChannel = parameterBuffer.Dequeue();
            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
        }

        private void Cmd_0E_SetMode() {
            //7   Speed(0 = Normal speed, 1 = Double speed)
            //6   XA - ADPCM(0 = Off, 1 = Send XA - ADPCM sectors to SPU Audio Input)
            //5-4 Transfer size:
            //    00 = 2048 bytes data only
            //    01 = 2328 bytes starting after sync
            //    10 = 2340 bytes starting after sync
            //3   XA - Filter(0 = Off, 1 = Process only XA - ADPCM sectors that match Setfilter)
            //2   Report(0 = Off, 1 = Enable Report - Interrupts for Audio Play)
            //1   AutoPause(0 = Off, 1 = Auto Pause upon End of Track); for Audio Play
            //0   CDDA(0 = Off, 1 = Allow to Read CD - DA Sectors; ignore missing EDC)
            uint mode = parameterBuffer.Dequeue();

            //Console.WriteLine($"[CDROM] SetMode: {mode:x8}");

            isDoubleSpeed = ((mode >> 7) & 0x1) == 1;
            isXAADPCM = ((mode >> 6) & 0x1) == 1;
            transferSizeMode = (mode & 0x30) switch {
                0x20 => TransferSizeMode.Sector2340,
                0x10 => TransferSizeMode.Sector2328,
                _ => TransferSizeMode.Data2048,
            };
            isXAFilter = ((mode >> 3) & 0x1) == 1;
            isReport = ((mode >> 2) & 0x1) == 1;
            isAutoPause = ((mode >> 1) & 0x1) == 1;
            isCDDA = (mode & 0x1) == 1;

            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
        }

        private void Cmd_10_GetLocL() {
            if (cdDebug) {
                Console.WriteLine($"mm: {sectorHeader.mm} ss: {sectorHeader.ss} ff: {sectorHeader.ff} mode: {sectorHeader.mode}" +
                    $" file: {sectorSubHeader.file} channel: {sectorSubHeader.channel} subMode: {sectorSubHeader.subMode} codingInfo: {sectorSubHeader.codingInfo}");
            }

            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            Span<byte> response = stackalloc byte[] {
                sectorHeader.mm,
                sectorHeader.ss,
                sectorHeader.ff,
                sectorHeader.mode,
                sectorSubHeader.file,
                sectorSubHeader.channel,
                sectorSubHeader.subMode,
                sectorSubHeader.codingInfo
            };

            TraceProtect("getlocl", $"resp={FormatBytes(response.ToArray())}");
            QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, delay: 1, response: response.ToArray());
        }

        private void Cmd_11_GetLocP() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            int queryLba = GetSubchannelQueryLba();
            SubchannelQ subQ = GetReportedSubchannelQ(queryLba);

            if (cdDebug) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(
                    $"track: {BcdToDec(subQ.Track)} index: {BcdToDec(subQ.Index)} mm: {BcdToDec(subQ.Minute)} ss: {BcdToDec(subQ.Second)}" +
                    $" ff: {BcdToDec(subQ.Frame)} amm: {BcdToDec(subQ.AbsoluteMinute)} ass: {BcdToDec(subQ.AbsoluteSecond)} aff: {BcdToDec(subQ.AbsoluteFrame)}");
                Console.WriteLine(
                    $"ctrl: {subQ.ControlAdr:X2} track: {subQ.Track:X2} index: {subQ.Index:X2} mm: {subQ.Minute:X2} ss: {subQ.Second:X2}" +
                    $" ff: {subQ.Frame:X2} zero: {subQ.AbsoluteZero:X2} amm: {subQ.AbsoluteMinute:X2} ass: {subQ.AbsoluteSecond:X2} aff: {subQ.AbsoluteFrame:X2}");
                Console.ResetColor();
            }

            Span<byte> response = stackalloc byte[] {
                subQ.Track,
                subQ.Index,
                subQ.Minute,
                subQ.Second,
                subQ.Frame,
                subQ.AbsoluteMinute,
                subQ.AbsoluteSecond,
                subQ.AbsoluteFrame
            };
            TraceProtect("getlocp", $"qLba={queryLba} srcLba={lastReportedSubchannelLba} valid={(subQ.HasValidCrc ? 1 : 0)} resp={FormatBytes(response.ToArray())}", force: true);
            QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, delay: 1, response: response.ToArray());
        }

        private void Cmd_1D_GetQ() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            int queryLba = GetSubchannelQueryLba();
            SubchannelQ subQ = GetReportedSubchannelQ(queryLba);
            Span<byte> response = stackalloc byte[] {
                subQ.ControlAdr,
                subQ.Track,
                subQ.Index,
                subQ.Minute,
                subQ.Second,
                subQ.Frame,
                subQ.AbsoluteZero,
                subQ.AbsoluteMinute,
                subQ.AbsoluteSecond,
                subQ.AbsoluteFrame
            };
            TraceProtect("getq", $"qLba={queryLba} srcLba={lastReportedSubchannelLba} valid={(subQ.HasValidCrc ? 1 : 0)} resp={FormatBytes(response.ToArray())}", force: true);
            QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, delay: 1, response: response.ToArray());
        }

        private void Cmd_12_SetSession() { //broken
            parameterBuffer.Clear();

            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            STAT = 0x42;

            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
            QueueSingleByteInterrupt(Interrupt.INT2_SECOND_RESPONSE, STAT);
        }

        private void Cmd_13_GetTN() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }
            //if (cdDebug)
            Console.WriteLine($"[CDROM] getTN First Track: 1 (Hardcoded) - Last Track: {cd.tracks.Count}");
            //Console.ReadLine();
            QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, response: new byte[] { STAT, 1, DecToBcd((byte)cd.tracks.Count) });
        }

        private void Cmd_14_GetTD() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            int track = BcdToDec(parameterBuffer.Dequeue());

            if (track == 0) { //returns CD LBA / End of last track
                (byte mm, byte ss, byte ff) = getMMSSFFfromLBA(cd.getLBA());
                QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, response: new byte[] { STAT, DecToBcd(mm), DecToBcd(ss) });
                //if (cdDebug)
                Console.WriteLine($"[CDROM] getTD Track: {track} STAT: {STAT:x2} {mm}:{ss}");
            } else { //returns Track Start
                (byte mm, byte ss, byte ff) = getMMSSFFfromLBA(cd.tracks[track - 1].lbaStart);
                QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, response: new byte[] { STAT, DecToBcd(mm), DecToBcd(ss) });
                //if (cdDebug)
                Console.WriteLine($"[CDROM] getTD Track: {track} STAT: {STAT:x2} {mm}:{ss}");
            }
        }

        private void Cmd_15_SeekL() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            UnlockFastLoadAfterBootIfReady();
            CompleteFastLoadReadSession();
            fastLoadLastSeekDistance = Math.Abs(seekLoc - readLoc);
            QueueStatefulSingleByteInterrupt(
                Interrupt.INT3_FIRST_RESPONSE,
                0x42,
                0x42,
                Mode.Seek,
                seekLoc,
                hasSeekState: true,
                logicalSeek: true,
                seekStartLba: GetSeekOriginSubchannelLba());
        }

        private void Cmd_16_SeekP() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            UnlockFastLoadAfterBootIfReady();
            CompleteFastLoadReadSession();
            fastLoadLastSeekDistance = Math.Abs(seekLoc - readLoc);
            QueueStatefulSingleByteInterrupt(
                Interrupt.INT3_FIRST_RESPONSE,
                0x42,
                0x42,
                Mode.Seek,
                seekLoc,
                hasSeekState: true,
                logicalSeek: false,
                seekStartLba: GetSeekOriginSubchannelLba());
        }

        private void Cmd_19_Test() {
            uint command = parameterBuffer.Dequeue();
            responseBuffer.Clear(); //we need to clear the delay on response to get the actual 0 0 to bypass antimodchip protection
            switch (command) {
                case 0x04://Com 19h,04h   ;ResetSCExInfo (reset GetSCExInfo response to 0,0) Used for antimodchip games like Dino Crisis
                    Console.WriteLine("[CDROM] Command 19 04 ResetSCExInfo Anti Mod Chip Meassures");
                    STAT = 0x2;
                    QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
                    break;
                case 0x05:// 05h      -   INT3(total,success);Stop SCEx reading and get counters
                    Console.WriteLine("[CDROM] Command 19 05 GetSCExInfo Hack 0 0 Bypass Response");
                    QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, response: new byte[] { 0, 0 });
                    break;
                case 0x20: //INT3(yy,mm,dd,ver) ;Get cdrom BIOS date/version (yy,mm,dd,ver) http://www.psxdev.net/forum/viewtopic.php?f=70&t=557
                    QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, response: new byte[] { 0x94, 0x09, 0x19, 0xC0 });
                    break;
                case 0x22: //INT3("for US/AEP") --> Region-free debug version --> accepts unlicensed CDRs
                    QueueResponseInterrupt(Interrupt.INT3_FIRST_RESPONSE, response: new byte[] { 0x66, 0x6F, 0x72, 0x20, 0x55, 0x53, 0x2F, 0x41, 0x45, 0x50 });
                    break;
                case 0x60://  60h      lo,hi     INT3(databyte)   ;HC05 SUB-CPU read RAM and I/O ports
                    QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, 0);
                    break;
                default:
                    Console.WriteLine($"[CDROM] Unimplemented 0x19 Test Command {command:x8}");
                    break;
            }
        }

        private void Cmd_1A_GetID() {
            //Door Open              INT5(11h,80h)  N/A
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            //No Disk                INT3(stat)     INT5(08h, 40h, 00h, 00h, 00h, 00h, 00h, 00h)
            //STAT = 0x2; //0x40 seek
            //responseBuffer.Enqueue(STAT);
            //interruptQueue.EnqueueDelayedInterrupt(Interrupt.INT3_RECEIVED_FIRST_RESPONSE);
            //
            //responseBuffer.EnqueueRange(stackalloc byte[] { 0x08, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
            //interruptQueue.EnqueueDelayedInterrupt(Interrupt.INT5_ERROR);

            //Licensed: Mode2 INT3(stat)     INT2(02h, 00h, 20h, 00h, 53h, 43h, 45h, 4xh)
            STAT = 0x40; //0x40 seek
            STAT |= 0x2;
            QueueInterrupt(Interrupt.INT3_FIRST_RESPONSE, response: new byte[] { STAT });

            // Audio Disk INT3(stat) INT5(0Ah,90h, 00h,00h, 00h,00h,00h,00h)
            if (cd.isAudioCD()) {
                QueueInterrupt(
                    Interrupt.INT5_ERROR,
                    response: new byte[] { 0x0A, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
                return;
            }

            // Licensed: Mode2 INT3(stat)     INT2(02h, 00h, 20h, 00h, 53h, 43h, 45h, 4xh)
            if (fastLoadEnabled || superFastLoadEnabled) {
                fastLoadLicensedDiscConfirmed = true;
            }
            QueueInterrupt(
                Interrupt.INT2_SECOND_RESPONSE,
                response: new byte[] {
                    0x02, 0x00, 0x20, 0x00,
                    licensedDiscRegionCode[0],
                    licensedDiscRegionCode[1],
                    licensedDiscRegionCode[2],
                    licensedDiscRegionCode[3]
                });
        }

        private void Cmd_1B_ReadS() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            UnlockFastLoadAfterBootIfReady();
            byte readStat = 0x22;
            QueueStatefulSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, readStat, readStat, Mode.Read, seekLoc, beginFastLoadReadSession: true);
        }

        private void Cmd_1E_ReadTOC() {
            if (isLidOpen) {
                QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x80 });
                return;
            }

            CompleteFastLoadReadSession();
            mode = Mode.TOC;
            QueueSingleByteInterrupt(Interrupt.INT3_FIRST_RESPONSE, STAT);
        }

        private void Cmd_1F_VideoCD() { //INT5(11h,40h)  ;-Unused/invalid
            QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { 0x11, 0x40 });
        }

        private int GetSubchannelQueryLba() {
            if (latchedSubchannelLba >= 0) {
                return latchedSubchannelLba;
            }

            if (mode == Mode.Seek) {
                return GetInterpolatedSeekSubchannelLba();
            }

            return currentSubchannelLba >= 0 ? currentSubchannelLba : readLoc;
        }

        private bool TryLatchReportedSubchannelQ(int lba) {
            if (lba < 0) {
                return false;
            }

            SubchannelQ subQ = cd.GetSubchannelQ(lba);
            if (!subQ.HasValidCrc) {
                return false;
            }

            lastReportedSubchannelQ = subQ;
            hasLastReportedSubchannelQ = true;
            lastReportedSubchannelLba = lba;
            return true;
        }

        private SubchannelQ GetReportedSubchannelQ(int lba) {
            SubchannelQ subQ = cd.GetSubchannelQ(lba);
            if (subQ.HasValidCrc) {
                lastReportedSubchannelQ = subQ;
                hasLastReportedSubchannelQ = true;
                lastReportedSubchannelLba = lba;
                return subQ;
            }

            if (hasLastReportedSubchannelQ) {
                return lastReportedSubchannelQ;
            }

            lastReportedSubchannelQ = subQ;
            lastReportedSubchannelLba = lba;
            return subQ;
        }

        private int GetSeekOriginSubchannelLba() {
            if (latchedSubchannelLba >= 0) {
                return latchedSubchannelLba;
            }

            if (currentSubchannelLba >= 0) {
                return currentSubchannelLba;
            }

            return readLoc;
        }

        private int GetInterpolatedSeekSubchannelLba() {
            int start = seekStartSubchannelLba >= 0 ? seekStartSubchannelLba : readLoc;
            int target = readLoc;
            if (start == target) {
                return target;
            }

            int totalCycles = Math.Max(GetSeekCycles(), 1);
            int progressedCycles = Math.Clamp(counter, 0, totalCycles);
            long delta = target - start;
            long progressedDistance = (Math.Abs(delta) * progressedCycles) / totalCycles;
            if (progressedDistance == 0) {
                progressedDistance = 1;
            }

            int current = delta < 0
                ? start - (int)progressedDistance
                : start + (int)progressedDistance;
            return delta < 0
                ? Math.Max(current, target)
                : Math.Min(current, target);
        }

        private bool CanFastCompleteLogicalSeek(int targetLba) {
            if (!logicalSeekHoldActive || currentSubchannelLba < 0) {
                return false;
            }

            int expectedHoldLba = Math.Max(0, targetLba - 2);
            return currentSubchannelLba == expectedHoldLba;
        }

        private static bool IsPreservableAsyncReadInterrupt(DelayedInterrupt delayedInterrupt) {
            return delayedInterrupt.interrupt == Interrupt.INT1_SECOND_RESPONSE_READ_PLAY
                && !delayedInterrupt.applyStateChange;
        }

        private void Cmd_5x_lockUnlock() {
            QueueInterrupt(Interrupt.INT5_ERROR);
        }

        private void QueueInvalidCommandError() {
            byte errorStat = (byte)(STAT | 0x01);
            QueueResponseInterrupt(Interrupt.INT5_ERROR, response: new byte[] { errorStat, 0x40 });
        }

        private void UnimplementedCDCommand(uint value) {
            Console.WriteLine($"[CDROM] Unimplemented CD Command 0x{value:X2}");
            QueueInvalidCommandError();
        }

        private byte STATUS() {
            //1F801800h - Index/Status Register (Bit0-1 R/W) (Bit2-7 Read Only)
            //0 - 1 Index Port 1F801801h - 1F801803h index(0..3 = Index0..Index3)   (R / W)
            //2   ADPBUSY XA-ADPCM fifo empty(0 = Empty) ; set when playing XA-ADPCM sound
            //3   PRMEMPT Parameter fifo empty(1 = Empty) ; triggered before writing 1st byte
            //4   PRMWRDY Parameter fifo full(0 = Full)  ; triggered after writing 16 bytes
            //5   RSLRRDY Response fifo empty(0 = Empty) ; triggered after reading LAST byte
            //6   DRQSTS Data fifo empty(0 = Empty) ; triggered after reading LAST byte
            //7   BUSYSTS Command/ parameter transmission busy(1 = Busy)

            int stat = 0;
            stat |= (isBusy ? 1 : 0) << 7;
            stat |= transferBuffer_hasData() << 6;
            stat |= responseBuffer_hasData() << 5;
            stat |= parametterBuffer_hasSpace() << 4;
            stat |= parametterBuffer_isEmpty() << 3;
            stat |= isXAADPCM ? (1 << 2) : 0;
            stat |= INDEX;
            return (byte)stat;
        }

        public int GetDmaWordCount() => GetTransferBytesRemaining() >> 2;

        private int GetTransferBytesRemaining() {
            int remaining = currentSector.DebugSize - currentSector.DebugPointer;
            if (remaining > 0) {
                return remaining;
            }

            if (HasPendingTransferData()) {
                return lastReadSector.DebugSize;
            }

            return 0;
        }

        private int transferBuffer_hasData() {
            return HasPendingTransferData() ? 1 : 0;
        }

        private int parametterBuffer_isEmpty() {
            return (parameterBuffer.Count == 0) ? 1 : 0;
        }

        private int parametterBuffer_hasSpace() {
            return (parameterBuffer.Count < 16) ? 1 : 0;
        }

        private int responseBuffer_hasData() {
            return (responseBuffer.Count > 0) ? 1 : 0;
        }

        private static byte DecToBcd(byte value) {
            return (byte)(value + 6 * (value / 10));
        }

        private static int BcdToDec(byte value) {
            return value - 6 * (value >> 4);
        }

        private static (byte mm, byte ss, byte ff) getMMSSFFfromLBA(int lba) {
            int ff = lba % 75;
            lba /= 75;

            int ss = lba % 60;
            lba /= 60;

            int mm = lba;

            return ((byte)mm, (byte)ss, (byte)ff);
        }

        private void applyVolume(byte[] rawSector) {
            var samples = MemoryMarshal.Cast<byte, short>(rawSector);

            for (int i = 0; i < samples.Length; i += 2) {
                short l = samples[i];
                short r = samples[i + 1];

                int volumeL = ((l * volumeLtoL) >> 7) + ((r * volumeRtoL) >> 7);
                int volumeR = ((l * volumeLtoR) >> 7) + ((r * volumeRtoR) >> 7);

                samples[i] =     (short)Math.Clamp(volumeL, -0x8000, 0x7FFF);
                samples[i + 1] = (short)Math.Clamp(volumeR, -0x8000, 0x7FFF);
            }

        }

        public Span<uint> processDmaLoad(int size) {
            if (!transferActive && transferReady) {
                TryExposeBufferedSector("dma");
            }

            if (!currentSector.hasData()) {
                return Span<uint>.Empty;
            }

            int bytesRemaining = GetTransferBytesRemaining();
            int wordsRequested = size == 0 ? (bytesRemaining >> 2) : size;
            int wordsAvailable = bytesRemaining >> 2;
            if (wordsRequested > wordsAvailable) {
                wordsRequested = wordsAvailable;
            }

            if (cdDebug) {
                Console.WriteLine(
                    $"[CDROM] [DMA] reqWords={size} xferWords={wordsRequested} bytesRemaining={bytesRemaining} " +
                    $"cur={currentSector.DebugPointer}/{currentSector.DebugSize} " +
                    $"last={lastReadSector.DebugPointer}/{lastReadSector.DebugSize} ready={(transferReady ? 1 : 0)}");
            }

            var dma = currentSector.read(wordsRequested);
            if (!currentSector.hasData()) {
                currentSector.clear();
                transferActive = false;
                loadedSectorGeneration = 0;
            }
            return dma;
        }

        internal void toggleLid() {
            isLidOpen = !isLidOpen;
            if (isLidOpen) {
                STAT = 0x18;
                mode = Mode.Idle;
                interruptQueue.Clear();
                responseBuffer.Clear();
                currentSector.clear();
                lastReadSector.clear();
                transferActive = false;
                transferReady = false;
            } else {
                //todo handle the Cd load and not this hardcoded test:
                //cd = new CD(@"cd_change_path");
            }
            Console.WriteLine($"[CDROM] Shell is Open: {isLidOpen} STAT: {STAT}");
        }

    }
}
