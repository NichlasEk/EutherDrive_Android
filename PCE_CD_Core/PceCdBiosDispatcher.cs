using System;
using System.Collections.Generic;
using System.IO;

namespace ePceCD
{
    internal sealed class PceCdBiosDispatcher
    {
        private readonly record struct StartupCommandPlan(
            byte Opcode,
            byte[] Packet,
            string Label,
            byte AcceptedStatus,
            byte? BusyRegisterValue,
            int ReadLba,
            int ReadSectorCount)
        {
            public bool StagesReadStream => ReadSectorCount > 0;
        }

        private readonly record struct StartupRequest7(byte FF, byte FC, byte FD, byte FE, byte F8, byte FA, byte FB)
        {
            public string Signature => $"ff={FF:X2}|fc={FC:X2}|fd={FD:X2}|fe={FE:X2}|f8={F8:X2}|fa={FA:X2}|fb={FB:X2}";
        }

        private readonly record struct StartupRequest8(byte Control, byte FF, byte F8, byte F9, byte FA, byte FB, byte FC, byte FD, byte FE)
        {
            public byte Mode => (byte)(FB & 0xC0);
            public string Signature => $"ctl={Control:X2}|ff={FF:X2}|f8={F8:X2}|f9={F9:X2}|fa={FA:X2}|fb={FB:X2}|fc={FC:X2}|fd={FD:X2}|fe={FE:X2}";
        }

        internal const ushort ResetTrapAddress = 0xFFF0;
        internal const ushort Irq2TrapAddress = (ushort)HuC6280.IRQVector.VECTOR_IRQ2;
        internal const ushort Irq1TrapAddress = (ushort)HuC6280.IRQVector.VECTOR_IRQ1;
        internal const ushort TimerTrapAddress = (ushort)HuC6280.IRQVector.VECTOR_TIMER;

        private readonly BUS _bus;
        private readonly PceCdBiosCallCatalog _catalog;
        private readonly PceCdBiosTrace _trace;
        private readonly Dictionary<ushort, (string Name, Func<PceCdBiosContext, int> Handler, PceCdBiosCallStatus Status, string? Notes)> _handlers;

        private PceCdBiosMode _mode;
        private bool _useHle;

        public PceCdBiosDispatcher(BUS bus, PceCdBiosCallCatalog catalog, PceCdBiosTrace trace)
        {
            _bus = bus;
            _catalog = catalog;
            _trace = trace;
            _handlers = new Dictionary<ushort, (string Name, Func<PceCdBiosContext, int> Handler, PceCdBiosCallStatus Status, string? Notes)>
            {
                [ResetTrapAddress] = ("hle_reset", HandleReset, PceCdBiosCallStatus.Implemented, "Synthetic HLE reset trap."),
                [Irq2TrapAddress] = ("hle_irq2", HandleIrq2, PceCdBiosCallStatus.PartiallyImplemented, "Synthetic HLE IRQ2 trap."),
                [Irq1TrapAddress] = ("hle_irq1", HandleIrq1, PceCdBiosCallStatus.PartiallyImplemented, "Synthetic HLE IRQ1 trap."),
                [TimerTrapAddress] = ("hle_timer", HandleTimer, PceCdBiosCallStatus.PartiallyImplemented, "Synthetic HLE timer IRQ trap."),
                [0xE009] = ("bios_e009_loader", HandleE009, PceCdBiosCallStatus.PartiallyImplemented, "Observed startup loader request path. Current HLE models the ROM-visible workspace setup and traces the request parameters."),
                [0xE012] = ("bios_e012_loader", HandleE012, PceCdBiosCallStatus.PartiallyImplemented, "Observed startup loader follow-up path. Current HLE models the ROM-visible workspace setup and issues the traced AudioStartPos command from that workspace."),
                [0xE01E] = ("bios_e01e_status_poll", HandleE01E, PceCdBiosCallStatus.PartiallyImplemented, "Observed poll service used by Golden Axe around 0x3E04."),
                [0xE02D] = ("bios_e02d_cd_mode", HandleE02D, PceCdBiosCallStatus.Implemented, "Observed startup timer/CD mode write. Current HLE matches the public ROM entry: A &= 0x0F; STA $180F; RTS."),
                [0xE033] = ("bios_e033_loader", HandleE033, PceCdBiosCallStatus.PartiallyImplemented, "Observed startup launch path. Current HLE models the ROM-visible workspace setup, busy rejection, FF==0 helper writes, and a decoded READ(6) launch packet."),
                [0xE036] = ("bios_e036_tail", HandleE036, PceCdBiosCallStatus.PartiallyImplemented, "Observed via JMP after E05A in Golden Axe; HLE now models the VDC and linear stream selectors with explicit staged CD data."),
                [0xE05A] = ("bios_e05a_status", HandleE05A, PceCdBiosCallStatus.PartiallyImplemented, "Observed before E036 in Golden Axe; partially modeled from ROM trace."),
                [0xE05D] = ("bios_e05d_irq_hook", HandleE05D, PceCdBiosCallStatus.PartiallyImplemented, "Observed to register an IRQ1 handler in Golden Axe."),
                [0xE069] = ("bios_e069_stub", HandleStubSuccess, PceCdBiosCallStatus.Stubbed, "Temporary success stub in HLE mode."),
                [0xE06C] = ("bios_e06c_stub", HandleStubSuccess, PceCdBiosCallStatus.Stubbed, "Temporary success stub in HLE mode."),
                [0xE06F] = ("bios_e06f_stub", HandleStubSuccess, PceCdBiosCallStatus.Stubbed, "Temporary success stub in HLE mode."),
                [0xE08D] = ("bios_adpcm_play", HandleAdpcmPlay, PceCdBiosCallStatus.Implemented, "HLE implementation of ADPCM_PLAY."),
                [0xE090] = ("bios_adpcm_stop", HandleAdpcmStop, PceCdBiosCallStatus.Implemented, "HLE implementation of ADPCM_STOP."),
                [0xE093] = ("bios_adpcm_stat", HandleAdpcmStat, PceCdBiosCallStatus.Implemented, "HLE implementation of ADPCM_STAT."),
                [0xE096] = ("bios_adpcm_write", HandleAdpcmWrite, PceCdBiosCallStatus.Implemented, "HLE implementation of ADPCM_WRITE."),
                [0xE099] = ("bios_adpcm_read", HandleAdpcmRead, PceCdBiosCallStatus.Implemented, "HLE implementation of ADPCM_READ."),
                [0xE09C] = ("bios_adpcm_mute", HandleStubSuccess, PceCdBiosCallStatus.Stubbed, "Stub for ADPCM_MUTE."),
                [0xE09F] = ("bios_adpcm_efx", HandleStubSuccess, PceCdBiosCallStatus.Stubbed, "Stub for ADPCM_EFX.")
            };
        }

        public bool HleEnabled => _useHle;

        public void Configure(PceCdBiosMode mode, bool useHle)
        {
            _mode = mode;
            _useHle = useHle;
        }

        public bool TryHandleExecution(HuC6280 cpu, ushort programCounter, out int cycles)
        {
            cycles = 0;
            if (!_useHle)
                return false;

            if (!_handlers.TryGetValue(programCounter, out (string Name, Func<PceCdBiosContext, int> Handler, PceCdBiosCallStatus Status, string? Notes) entry))
            {
                if (!IsPublicEntryPoint(programCounter))
                    return false;

                entry = ("bios_default_stub", HandleStubSuccess, PceCdBiosCallStatus.Stubbed, "Default HLE stub path.");
                _catalog.MarkStatus(programCounter, PceCdBiosCallStatus.Stubbed, notes: "Default HLE stub path.");
            }
            else
            {
                _catalog.MarkStatus(programCounter, entry.Status, notes: entry.Notes);
            }

            bool hasEntryTransfer = _bus.BiosRuntimeState.TryGetEntry(programCounter, out ushort entryCaller, out PceCdBiosTransferType entryTransferType);
            var context = new PceCdBiosContext(
                _bus,
                cpu,
                _trace,
                _catalog,
                programCounter,
                entryCaller,
                entryTransferType,
                hasEntryTransfer);
            _trace.LogDispatch(programCounter, entry.Name, "enter", cpu);
            cycles = Math.Max(16, entry.Handler(context));
            _trace.LogDispatch(programCounter, entry.Name, "exit", cpu);
            return true;
        }

        public void NoteControlTransfer(HuC6280 cpu, ushort caller, ushort target, PceCdBiosTransferType type)
        {
            if (!IsTraceRelevantTarget(target))
                return;

            _catalog.NoteCall(target, caller);
            _trace.LogControlTransfer(type, caller, target, cpu);
        }

        public void NoteReturn(HuC6280 cpu, ushort source, ushort target, PceCdBiosTransferType type)
        {
            if (!IsTraceRelevantTarget(source))
                return;

            _trace.LogReturn(type, source, target, cpu);
        }

        public string BuildCatalogMarkdown()
        {
            return _catalog.BuildMarkdown(_bus.CDfile, _mode);
        }

        public void WriteArtifacts(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "pcecd_bios_calls.md");
            File.WriteAllText(path, BuildCatalogMarkdown());
        }

        public static bool IsPublicEntryPoint(ushort address)
        {
            return address >= 0xE000 && address <= 0xE0FF;
        }

        public static bool TryGetTrapAddress(HuC6280.IRQVector vector, out ushort address)
        {
            switch (vector)
            {
                case HuC6280.IRQVector.VECTOR_IRQ2:
                    address = Irq2TrapAddress;
                    return true;
                case HuC6280.IRQVector.VECTOR_IRQ1:
                    address = Irq1TrapAddress;
                    return true;
                case HuC6280.IRQVector.VECTOR_TIMER:
                    address = TimerTrapAddress;
                    return true;
                default:
                    address = 0;
                    return false;
            }
        }

        private bool IsTraceRelevantTarget(ushort address)
        {
            return address == ResetTrapAddress ||
                   address == Irq2TrapAddress ||
                   address == Irq1TrapAddress ||
                   address == TimerTrapAddress ||
                   IsPublicEntryPoint(address);
        }

        private int HandleReset(PceCdBiosContext context)
        {
            _bus.BiosRuntimeState.Clear();
            string discName = context.DiscName ?? string.Empty;
            if (discName.Contains("Golden Axe", StringComparison.OrdinalIgnoreCase))
            {
                context.SetAccumulator(0x00);
                context.SetX(0x00);
                context.SetY(0x00);
                context.SetStackPointer(0xFF);
                context.SetProcessorStatus(0x24);
                context.SetInterruptDisable(true);
                context.SetMprMap(0xFF, 0xF8, 0x80, 0x81, 0x82, 0x83, 0x84, 0x00);
                context.LoadMode1Sectors(4086, 8, 0x4000, "golden_axe_direct_v1");
                context.WriteMemoryBlock(0x2274, new byte[] { 0x00, 0x0E, 0x06, 0x00, 0x0E, 0x06 });
                context.SetProgramCounter(0x4000);
                _catalog.MarkStatus(
                    ResetTrapAddress,
                    PceCdBiosCallStatus.Implemented,
                    "Direct boot profile for Golden Axe.",
                    "Loads sectors 4086-4093 to RAM, seeds the observed startup slot offsets at $2274-$2279, and transfers control to 0x4000. The late E036 stream is now staged from the observed E033 startup path instead of directly from reset.");
                _trace.LogNote("boot profile=golden_axe_direct_v1 entry=0x4000 slot_offsets=00-0E-06");
                return 512;
            }

            _trace.LogNote($"no direct boot profile for disc='{discName}'");
            throw new NotSupportedException($"PCE CD BIOS HLE boot profile missing for '{discName}'.");
        }

        private int HandleIrq2(PceCdBiosContext context)
        {
            _trace.LogNote("irq2 trap without registered HLE behavior; returning from interrupt.");
            context.ReturnFromInterrupt();
            return 24;
        }

        private int HandleIrq1(PceCdBiosContext context)
        {
            if (_bus.BiosRuntimeState.HasIrq1Handler)
            {
                ushort handler = _bus.BiosRuntimeState.Irq1HandlerAddress;
                _trace.LogNote($"irq1 dispatch handler=0x{handler:X4}");
                context.SetProgramCounter(handler);
                return 24;
            }

            _trace.LogNote("irq1 trap without registered handler; returning from interrupt.");
            context.ReturnFromInterrupt();
            return 24;
        }

        private int HandleTimer(PceCdBiosContext context)
        {
            _trace.LogNote("timer trap without registered HLE behavior; returning from interrupt.");
            context.ReturnFromInterrupt();
            return 24;
        }

        private int HandleE01E(PceCdBiosContext context)
        {
            ushort target = (ushort)(context.ReadZeroPage8(0xFA) | (context.ReadZeroPage8(0xFB) << 8));
            byte currentValue = context.ReadMemory8(target);

            if (target == 0x3E04 && !_bus.BiosRuntimeState.E01E3E04ReadyIssued)
            {
                currentValue = 0x03;
                context.WriteMemory8(target, currentValue, "e01e_status_ready");
                _bus.BiosRuntimeState.E01E3E04ReadyIssued = true;
                _catalog.MarkStatus(
                    0xE01E,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Observed poll/status service.",
                    "One-shot readiness promotion for Golden Axe at 0x3E04.");
                _trace.LogNote("e01e target=0x3E04 promote=0x03");
            }

            context.SetAccumulator(currentValue);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleE009(PceCdBiosContext context)
        {
            StartupRequest7 request = ReadStartupRequest7(context);

            TraceStartupRequest(context, "e009", request);
            SaveStartupWindow(context);
            context.WriteMemory8(0x2273, 0x00, "e009_slot_reset", trace: false);
            ClearStartupWorkspace(context);
            context.WriteMemory8(0x224C, 0x08, "e009_workspace_opcode", trace: false);
            ApplyStartupSlotOffset(context, "e009");
            CopyStartupTriplet(context, 0xFC, 0x224D, "e009_workspace_triplet");

            if (request.FF == 0x01)
            {
                int sectorCount = request.F8;
                if (sectorCount == 0)
                {
                    _catalog.MarkStatus(
                        0xE009,
                        PceCdBiosCallStatus.PartiallyImplemented,
                        "Observed startup loader request path.",
                        "Implements the FF=0x01 RAM-transfer branch from EC05. Zero-count requests return 0x22, matching the ROM short-return path.");
                    _trace.LogNote($"e009 branch=ram_read zero_count signature={request.Signature}");
                    context.SetAccumulator(0x22);
                    context.ReturnFromSubroutine();
                    return 48;
                }

                context.WriteMemory8(0x2280, (byte)sectorCount, "e009_sector_count", trace: false);
                context.WriteMemory8(0x2250, (byte)sectorCount, "e009_workspace_length", trace: false);
                ExecuteStartupCommandChain(context, "e009", request.Signature);
                WriteZeroPage16(context, 0xF8, (ushort)(sectorCount << 11), "e009_linear_count");
                int transferred = ExecuteE036LinearStream(context);

                _catalog.MarkStatus(
                    0xE009,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Observed startup loader request path.",
                    "Implements the EC05 FF=0x01 branch: apply the slot offset from $2274.., build a READ(6) packet, convert sector count to a byte count in ZP $F8/$F9, and stream the payload into the caller buffer at $FA/$FB.");
                _trace.LogNote(
                    $"e009 branch=ram_read sectors={sectorCount} bytes={transferred} dest=0x{request.FA | (request.FB << 8):X4} signature={request.Signature}");

                context.SetAccumulator(0x00);
                context.Cpu.HleSetCarryFlag(false);
                context.ReturnFromSubroutine();
                return 96 + transferred * 12;
            }

            if (request.FF == 0xFF)
            {
                int sectorCount = request.F8;
                if (sectorCount == 0)
                {
                    _catalog.MarkStatus(
                        0xE009,
                        PceCdBiosCallStatus.PartiallyImplemented,
                        "Observed startup loader request path.",
                        "Implements the FF=0xFF VDC-transfer branch from EC79. Zero-count requests return through the observed 0x22 short-return path.");
                    _trace.LogNote($"e009 branch=vdc_read zero_count signature={request.Signature}");
                    context.SetAccumulator(0x22);
                    context.ReturnFromSubroutine();
                    return 48;
                }

                context.WriteMemory8(0x2250, (byte)sectorCount, "e009_workspace_length", trace: false);
                ExecuteStartupCommandChain(context, "e009", request.Signature);
                WriteZeroPage16(context, 0xF8, (ushort)(sectorCount << 11), "e009_vdc_count");
                int transferred = ExecuteE036VdcStream(context);

                _catalog.MarkStatus(
                    0xE009,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Observed startup loader request path.",
                    "Implements the EC79 FF=0xFF branch: build a READ(6) packet, convert sector count to a byte count, seed MAWR from $FA/$FB, and stream the payload to VDC data ports.");
                _trace.LogNote(
                    $"e009 branch=vdc_read sectors={sectorCount} bytes={transferred} mawr=0x{request.FA | (request.FB << 8):X4} signature={request.Signature}");

                context.SetAccumulator(0x00);
                context.Cpu.HleSetCarryFlag(false);
                context.ReturnFromSubroutine();
                return 96 + transferred * 16;
            }

            _catalog.MarkStatus(
                0xE009,
                PceCdBiosCallStatus.PartiallyImplemented,
                "Observed startup loader request path.",
                "Models the ROM-visible EC05 prelude and now implements the FF=0x01 RAM-transfer and FF=0xFF VDC-transfer branches. The later ECBB/ED03 branches remain unimplemented.");
            _trace.LogNote($"e009 branch_unimplemented ff=0x{request.FF:X2} signature={request.Signature}");

            context.SetAccumulator(0x00);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 64;
        }

        private int HandleE012(PceCdBiosContext context)
        {
            StartupRequest8 request = ReadStartupRequest8(context);
            byte maskedMode = request.Mode;

            TraceStartupRequest(context, "e012", request);
            SaveStartupWindow(context);
            ClearStartupWorkspace(context);

            if (maskedMode == 0x80)
            {
                context.WriteZeroPage8(0xF9, 0x00, "e012_zero_f9", trace: false);
                context.WriteZeroPage8(0xFA, 0x00, "e012_zero_fa", trace: false);
            }

            context.WriteMemory8(0x2255, maskedMode, "e012_workspace_flags", trace: false);
            context.WriteMemory8(0x224C, 0xD8, "e012_workspace_opcode", trace: false);
            ushort tripletDestination = request.FB == 0x00 ? (ushort)0x224F : (ushort)0x224E;
            CopyStartupTriplet(context, 0xF8, tripletDestination, "e012_workspace_triplet");
            ExecuteStartupCommandChain(context, "e012", request.Signature);

            _catalog.MarkStatus(
                0xE012,
                PceCdBiosCallStatus.PartiallyImplemented,
                "Observed startup loader follow-up path.",
                "Models the ROM-visible EE10 prelude: save ZP $F8-$FF, clear $224C-$2255, mirror $FB mode bits into $2255, seed $224C with 0xD8, place $F8-$FA into the observed request slot selected by $FB, and issue the traced command packet from the prepared workspace. The currently observed Golden Axe path launches the BIOS AudioStartPos packet and leaves the status surface at 0x80.");
            _trace.LogNote(
                $"e012 mode=0x{maskedMode:X2} triplet_dest=0x{tripletDestination:X4} signature={request.Signature}");

            context.SetAccumulator(0x00);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 64;
        }

        private int HandleE02D(PceCdBiosContext context)
        {
            byte value = (byte)(context.Cpu.PeekA() & 0x0F);
            context.SetAccumulator(value);
            context.WriteMemory8(0x180F, value, "e02d_cd_mode");

            _catalog.MarkStatus(
                0xE02D,
                PceCdBiosCallStatus.Implemented,
                "Observed startup timer/CD mode write.",
                "Matches the public ROM entry F379: A &= 0x0F, store to $180F, return.");
            _trace.LogNote($"e02d mode=0x{value:X2}");

            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleE033(PceCdBiosContext context)
        {
            byte cdBusy = (byte)(context.ReadMemory8(0x180B) & 0x03);
            if (cdBusy != 0)
            {
                _catalog.MarkStatus(
                    0xE033,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Observed startup launch path.",
                    "Matches the public ROM busy rejection at F393 when $180B & 0x03 is non-zero.");
                _trace.LogNote($"e033 busy_reject cdbusy=0x{cdBusy:X2}");
                context.SetAccumulator(0x04);
                context.ReturnFromSubroutine();
                return 32;
            }

            StartupRequest7 request = ReadStartupRequest7(context);

            TraceStartupRequest(context, "e033", request);
            SaveStartupWindow(context);
            context.WriteMemory8(0x2273, 0x00, "e033_slot_reset", trace: false);
            if (request.FF == 0x00)
            {
                context.WriteMemory8(0x1808, request.FA, "e033_ff0_write_1808", trace: false);
                context.WriteMemory8(0x1809, request.FB, "e033_ff0_write_1809", trace: false);
                context.WriteMemory8(0x180D, 0x03, "e033_ff0_write_180d", trace: false);
                context.WriteMemory8(0x180D, 0x02, "e033_ff0_write_180d", trace: false);
                context.WriteMemory8(0x180D, 0x00, "e033_ff0_write_180d", trace: false);
            }

            ClearStartupWorkspace(context);
            context.WriteMemory8(0x224C, 0x08, "e033_workspace_opcode", trace: false);
            ApplyStartupSlotOffset(context, "e033");
            CopyStartupTriplet(context, 0xFC, 0x224D, "e033_workspace_triplet");
            context.WriteMemory8(0x2250, request.F8, "e033_workspace_length", trace: false);
            ExecuteStartupCommandChain(context, "e033", request.Signature);

            _catalog.MarkStatus(
                0xE033,
                PceCdBiosCallStatus.PartiallyImplemented,
                "Observed startup launch path.",
                "Models the ROM-visible F393 prelude: busy rejection via $180B, save ZP $F8-$FF, reset $2273, run the observed FF==0 helper writes to $1808/$1809/$180D, clear $224C-$2255, apply the slot offset from $2274.. to $FC-$FE, seed $224C with 0x08, copy the adjusted triplet into the launch slot, mirror $F8 into $2250, and issue a traced READ(6) launch packet from the workspace. READ packets now stage the late E036 stream from the decoded command rather than a fixed Golden Axe signature.");
            _trace.LogNote($"e033 launch signature={request.Signature}");

            context.SetAccumulator(0x00);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 64;
        }

        private int HandleE036(PceCdBiosContext context)
        {
            byte selector = context.ReadZeroPage8(0xFF);

            if (selector == 0xFF)
            {
                ushort mawr = ReadZeroPage16(context, 0xFA);
                int transferred = ExecuteE036VdcStream(context);
                _catalog.MarkStatus(
                    0xE036,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Observed BIOS stream path keyed by ZP $FF.",
                    "Implements the observed $FF selector path for Golden Axe by seeding MAWR from $FA/$FB and streaming CD bytes to VDC data ports $0002/$0003.");
                _trace.LogNote(
                    $"e036 selector=0xFF vdc_stream bytes={transferred} mawr=0x{mawr:X4} caller={(context.HasEntryTransfer ? $"0x{context.EntryCaller:X4}" : "unknown")}");
                context.SetAccumulator(0x00);
                context.ReturnFromSubroutine();
                return 64 + transferred * 16;
            }

            if (selector == 0x00)
            {
                ushort destination = ReadZeroPage16(context, 0xFA);
                int transferred = ExecuteE036LinearStream(context);
                _catalog.MarkStatus(
                    0xE036,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Observed BIOS stream path keyed by ZP $FF.",
                    "Implements the observed $00 selector path by copying CD bytes into the caller buffer pointed to by $FA/$FB.");
                _trace.LogNote(
                    $"e036 selector=0x00 linear_stream bytes={transferred} dest=0x{destination:X4} caller={(context.HasEntryTransfer ? $"0x{context.EntryCaller:X4}" : "unknown")}");
                context.SetAccumulator(0x00);
                context.ReturnFromSubroutine();
                return 64 + transferred * 12;
            }

            if (selector >= 0x07)
            {
                _catalog.MarkStatus(
                    0xE036,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Observed selector helper keyed by ZP $FF.",
                    "Selectors >= 7 return 0x22, matching the observed Golden Axe ROM path when $FF=0x43.");
                _trace.LogNote(
                    $"e036 selector=0x{selector:X2} short_return=0x22 caller={(context.HasEntryTransfer ? $"0x{context.EntryCaller:X4}" : "unknown")}");
                context.SetAccumulator(0x22);
                context.ReturnFromSubroutine();
                return 48;
            }

            _catalog.MarkStatus(
                0xE036,
                PceCdBiosCallStatus.TimingSensitive,
                "Observed selector helper keyed by ZP $FF.",
                "Selectors below 7 still need ROM-side effect study. Current HLE leaves them as a timing-sensitive approximation.");
            _trace.LogNote(
                $"e036 selector=0x{selector:X2} path_unimplemented caller={(context.HasEntryTransfer ? $"0x{context.EntryCaller:X4}" : "unknown")} approximated_via_rts");
            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleE05A(PceCdBiosContext context)
        {
            context.SetX(0x03);
            context.SetY(0x00);
            _catalog.MarkStatus(
                0xE05A,
                PceCdBiosCallStatus.PartiallyImplemented,
                "Observed helper that loads BIOS constants into X/Y.",
                "Local BIOS disassembly shows LDX $FEC2 / LDY $FEC3 / RTS. For the current BIOS image that yields X=0x03, Y=0x00.");
            _trace.LogNote("e05a load_xy x=0x03 y=0x00");

            context.ReturnFromSubroutine();
            return 32;
        }

        private static ushort ReadZeroPage16(PceCdBiosContext context, byte address)
        {
            return (ushort)(context.ReadZeroPage8(address) | (context.ReadZeroPage8((byte)(address + 1)) << 8));
        }

        private static StartupRequest7 ReadStartupRequest7(PceCdBiosContext context)
        {
            return new StartupRequest7(
                context.ReadZeroPage8(0xFF),
                context.ReadZeroPage8(0xFC),
                context.ReadZeroPage8(0xFD),
                context.ReadZeroPage8(0xFE),
                context.ReadZeroPage8(0xF8),
                context.ReadZeroPage8(0xFA),
                context.ReadZeroPage8(0xFB));
        }

        private static StartupRequest8 ReadStartupRequest8(PceCdBiosContext context)
        {
            return new StartupRequest8(
                context.Cpu.PeekA(),
                context.ReadZeroPage8(0xFF),
                context.ReadZeroPage8(0xF8),
                context.ReadZeroPage8(0xF9),
                context.ReadZeroPage8(0xFA),
                context.ReadZeroPage8(0xFB),
                context.ReadZeroPage8(0xFC),
                context.ReadZeroPage8(0xFD),
                context.ReadZeroPage8(0xFE));
        }

        private static void TraceStartupRequest(PceCdBiosContext context, string service, StartupRequest7 request)
        {
            context.Trace.LogNote($"{service} request={request.Signature}");
        }

        private static void TraceStartupRequest(PceCdBiosContext context, string service, StartupRequest8 request)
        {
            context.Trace.LogNote($"{service} request={request.Signature}");
        }

        private static void ExecuteStartupCommandChain(PceCdBiosContext context, string service, string signature)
        {
            if (!TryBuildStartupCommandPlan(context, service, out StartupCommandPlan plan))
            {
                context.Trace.LogNote($"{service} command_unimplemented opcode=0x{context.ReadMemory8(0x224C):X2} signature={signature}");
                return;
            }

            context.WriteMemory8(0x229B, 0x00, $"{service}_command_reset", trace: false);
            context.WriteMemory8(0x1801, 0x81, $"{service}_command_busfree", trace: false);
            context.WriteMemory8(0x1800, 0x81, $"{service}_command_select", trace: false);
            context.WriteMemory8(0x227A, 0x00, $"{service}_status", trace: false);

            for (int i = 0; i < plan.Packet.Length; i++)
            {
                context.WriteMemory8(0x227A, 0xD0, $"{service}_status", trace: false);
                context.WriteMemory8(0x1801, plan.Packet[i], $"{service}_command_byte", trace: false);
                PulseStartupAck(context, service);
            }

            context.WriteMemory8(0x227A, plan.AcceptedStatus, $"{service}_status", trace: false);
            if (plan.BusyRegisterValue.HasValue)
                context.WriteMemory8(0x180B, plan.BusyRegisterValue.Value, $"{service}_busy", trace: false);

            if (plan.StagesReadStream &&
                !string.Equals(context.Bus.BiosRuntimeState.StagedCdDataLabel, plan.Label, StringComparison.Ordinal))
            {
                try
                {
                    context.StageMode1SectorStream(plan.ReadLba, plan.ReadSectorCount, plan.Label);
                }
                catch (InvalidOperationException ex)
                {
                    context.Trace.LogNote(
                        $"{service} stage_skip opcode=0x{plan.Opcode:X2} lba={plan.ReadLba} count={plan.ReadSectorCount} reason=\"{ex.Message}\"");
                }
            }

            context.Bus.BiosRuntimeState.NoteStartupCommand(
                plan.Opcode,
                plan.AcceptedStatus,
                plan.ReadLba,
                plan.ReadSectorCount);
            context.Trace.LogNote(
                $"{service} command opcode=0x{plan.Opcode:X2} packet={BitConverter.ToString(plan.Packet)} status=0x{plan.AcceptedStatus:X2} signature={signature}" +
                (plan.StagesReadStream ? $" stage_lba={plan.ReadLba} stage_count={plan.ReadSectorCount}" : string.Empty));
        }

        private static bool TryBuildStartupCommandPlan(PceCdBiosContext context, string service, out StartupCommandPlan plan)
        {
            byte opcode = context.ReadMemory8(0x224C);
            switch (opcode)
            {
                case 0x08:
                {
                    byte lbaHigh = context.ReadMemory8(0x224D);
                    byte lbaMid = context.ReadMemory8(0x224E);
                    byte lbaLow = context.ReadMemory8(0x224F);
                    byte countField = context.ReadMemory8(0x2250);
                    int readLba = (lbaHigh << 16) | (lbaMid << 8) | lbaLow;
                    int readSectorCount = countField == 0x00 ? 256 : countField;
                    plan = new StartupCommandPlan(
                        opcode,
                        new[] { opcode, lbaHigh, lbaMid, lbaLow, countField, (byte)0x00 },
                        $"{service}_read6_lba_{readLba:X6}_count_{readSectorCount:X2}",
                        0xC8,
                        string.Equals(service, "e033", StringComparison.Ordinal) ? (byte)0x02 : null,
                        readLba,
                        readSectorCount);
                    return true;
                }
                case 0xD8:
                {
                    byte addressMode = context.ReadMemory8(0x224D);
                    byte minute = context.ReadMemory8(0x224E);
                    byte second = context.ReadMemory8(0x224F);
                    byte frame = context.ReadMemory8(0x2250);
                    byte mode = context.ReadMemory8(0x2255);
                    plan = new StartupCommandPlan(
                        opcode,
                        new[] { opcode, addressMode, minute, second, frame, (byte)0x00, (byte)0x00, (byte)0x00, (byte)0x00, mode },
                        $"{service}_audio_start_msf_{minute:X2}_{second:X2}_{frame:X2}_mode_{mode:X2}",
                        0x80,
                        null,
                        0,
                        0);
                    return true;
                }
                default:
                    plan = default;
                    return false;
            }
        }

        private static void PulseStartupAck(PceCdBiosContext context, string service)
        {
            context.WriteMemory8(0x1802, 0x80, $"{service}_ack", trace: false);
            context.WriteMemory8(0x1802, 0x00, $"{service}_ack", trace: false);
        }

        private static void SaveStartupWindow(PceCdBiosContext context)
        {
            byte[] snapshot =
            {
                context.ReadZeroPage8(0xF8),
                context.ReadZeroPage8(0xF9),
                context.ReadZeroPage8(0xFA),
                context.ReadZeroPage8(0xFB),
                context.ReadZeroPage8(0xFC),
                context.ReadZeroPage8(0xFD),
                context.ReadZeroPage8(0xFE),
                context.ReadZeroPage8(0xFF)
            };
            context.WriteMemoryBlock(0x2260, snapshot);
        }

        private static void ApplyStartupSlotOffset(PceCdBiosContext context, string service)
        {
            int slot = context.ReadMemory8(0x2273);
            int slotOffset = slot * 3;
            byte addHigh = context.ReadMemory8((ushort)(0x2274 + slotOffset));
            byte addMid = context.ReadMemory8((ushort)(0x2275 + slotOffset));
            byte addLow = context.ReadMemory8((ushort)(0x2276 + slotOffset));

            int baseValue = (context.ReadZeroPage8(0xFC) << 16) |
                            (context.ReadZeroPage8(0xFD) << 8) |
                            context.ReadZeroPage8(0xFE);
            int addValue = (addHigh << 16) | (addMid << 8) | addLow;
            int adjusted = (baseValue + addValue) & 0x00FF_FFFF;

            context.WriteZeroPage8(0xFC, (byte)(adjusted >> 16), $"{service}_slot_offset", trace: false);
            context.WriteZeroPage8(0xFD, (byte)(adjusted >> 8), $"{service}_slot_offset", trace: false);
            context.WriteZeroPage8(0xFE, (byte)adjusted, $"{service}_slot_offset", trace: false);
            context.Trace.LogNote(
                $"{service} slot_offset slot={slot} add={addHigh:X2}-{addMid:X2}-{addLow:X2} adjusted=0x{adjusted:X6}");
        }

        private static void ClearStartupWorkspace(PceCdBiosContext context)
        {
            context.WriteMemoryBlock(0x224C, new byte[10]);
        }

        private static void CopyStartupTriplet(PceCdBiosContext context, byte sourceZeroPage, ushort destination, string reason)
        {
            byte[] triplet =
            {
                context.ReadZeroPage8(sourceZeroPage),
                context.ReadZeroPage8((byte)(sourceZeroPage + 1)),
                context.ReadZeroPage8((byte)(sourceZeroPage + 2))
            };
            context.WriteMemoryBlock(destination, triplet);
            context.Trace.LogNote($"{reason} src=0x{sourceZeroPage:X2} dest=0x{destination:X4}");
        }

        private static void WriteZeroPage16(PceCdBiosContext context, byte address, ushort value, string reason)
        {
            context.WriteZeroPage8(address, (byte)(value & 0xFF), reason, trace: false);
            context.WriteZeroPage8((byte)(address + 1), (byte)(value >> 8), reason, trace: false);
        }

        private static byte ReadCdDataByte(PceCdBiosContext context)
        {
            if (context.TryReadStagedCdData(out byte stagedValue, out int remaining, out string label))
            {
                if (!context.Bus.BiosRuntimeState.StagedCdDataReadLogged)
                {
                    context.Bus.BiosRuntimeState.StagedCdDataReadLogged = true;
                    context.Trace.LogNote(
                        $"cddata source=hle_queue label={label} remaining={remaining}");
                }

                return stagedValue;
            }

            if (!context.Bus.BiosRuntimeState.StagedCdDataUnderflowLogged &&
                !string.IsNullOrWhiteSpace(context.Bus.BiosRuntimeState.StagedCdDataLabel))
            {
                context.Bus.BiosRuntimeState.StagedCdDataUnderflowLogged = true;
                context.Trace.LogNote(
                    $"cddata source=hle_queue label={context.Bus.BiosRuntimeState.StagedCdDataLabel} exhausted; falling back to cd port");
            }

            int guard = 32;
            while (guard-- > 0 && (context.ReadMemory8(0x180C) & 0x80) != 0)
            {
            }

            return context.ReadMemory8(0x180A);
        }

        private static int ExecuteE036LinearStream(PceCdBiosContext context)
        {
            ushort destination = ReadZeroPage16(context, 0xFA);
            int remaining = ReadZeroPage16(context, 0xF8);
            int transferred = 0;

            while (remaining > 0)
            {
                byte value = ReadCdDataByte(context);
                context.WriteMemory8(destination++, value, trace: false);
                transferred++;
                remaining--;
            }

            WriteZeroPage16(context, 0xFA, destination, "e036_linear_dest");
            WriteZeroPage16(context, 0xF8, 0x0000, "e036_linear_count");
            return transferred;
        }

        private static int ExecuteE036VdcStream(PceCdBiosContext context)
        {
            ushort mawr = ReadZeroPage16(context, 0xFA);
            int remaining = ReadZeroPage16(context, 0xF8);
            if (remaining <= 0)
                return 0;

            context.WriteZeroPage8(0xF7, 0x00, "e036_vdc_f7", trace: false);
            context.WriteMemory8(0x0000, 0x00, "e036_vdc_select_mawr", trace: false);
            context.WriteMemory8(0x2272, 0x01, "e036_vdc_stream_active", trace: false);
            context.WriteMemory8(0x0002, (byte)(mawr & 0xFF), "e036_vdc_mawr_l", trace: false);
            context.WriteMemory8(0x0003, (byte)(mawr >> 8), "e036_vdc_mawr_h", trace: false);

            byte vdcPort = 0x02;
            context.WriteZeroPage8(0xFA, vdcPort, "e036_vdc_port_l", trace: false);
            context.WriteZeroPage8(0xFB, 0x00, "e036_vdc_port_h", trace: false);
            context.WriteZeroPage8(0xF7, 0x02, "e036_vdc_f7", trace: false);
            context.WriteMemory8(0x0000, 0x02, "e036_vdc_select_vwr", trace: false);

            int transferred = 0;
            while (remaining > 0)
            {
                byte value = ReadCdDataByte(context);
                context.WriteMemory8(vdcPort, value, trace: false);
                vdcPort ^= 0x01;
                transferred++;
                remaining--;
            }

            context.WriteZeroPage8(0xFA, vdcPort, "e036_vdc_port_l", trace: false);
            context.WriteZeroPage8(0xFB, 0x00, "e036_vdc_port_h", trace: false);
            WriteZeroPage16(context, 0xF8, 0x0000, "e036_vdc_count");
            context.WriteMemory8(0x2272, 0x00, "e036_vdc_stream_done", trace: false);
            return transferred;
        }

        private int HandleE05D(PceCdBiosContext context)
        {
            byte slot = context.Cpu.PeekA();
            ushort handler = (ushort)(context.Cpu.PeekX() | (context.Cpu.PeekY() << 8));

            if (slot == 0x01)
            {
                _bus.BiosRuntimeState.HasIrq1Handler = true;
                _bus.BiosRuntimeState.Irq1HandlerAddress = handler;
                _catalog.MarkStatus(
                    0xE05D,
                    PceCdBiosCallStatus.PartiallyImplemented,
                    "Registers an IRQ1 handler when A=1.",
                    $"Observed handler=0x{handler:X4}.");
                _trace.LogNote($"e05d slot=0x{slot:X2} irq1_handler=0x{handler:X4}");
            }
            else
            {
                _trace.LogNote($"e05d unhandled slot=0x{slot:X2} handler=0x{handler:X4}");
            }

            context.SetAccumulator(0x00);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleAdpcmPlay(PceCdBiosContext context)
        {
            byte lengthL = context.ReadZeroPage8(0xF8);
            byte lengthH = context.ReadZeroPage8(0xF9);
            byte rate = context.ReadZeroPage8(0xFA);
            byte addrL = context.ReadZeroPage8(0xFC);
            byte addrM = context.ReadZeroPage8(0xFD);
            byte addrH = context.ReadZeroPage8(0xFE);

            // 1. Reset and set address
            context.WriteMemory8(0x180D, 0x01); // Reset read ptr
            context.WriteMemory8(0x1808, addrL);
            context.WriteMemory8(0x1809, addrM);
            context.WriteMemory8(0x180D, 0x08); // Set read ptr

            // 2. Set length
            context.WriteMemory8(0x1808, lengthL);
            context.WriteMemory8(0x1809, lengthH);
            context.WriteMemory8(0x180D, 0x10); // Latch length

            // 3. Set rate and play
            context.WriteMemory8(0x180E, rate);
            context.WriteMemory8(0x180D, 0x20); // Start playback

            _trace.LogNote($"adpcm_play addr=0x{addrM:X2}{addrL:X2} len=0x{lengthH:X2}{lengthL:X2} rate=0x{rate:X2}");
            context.SetAccumulator(0x00);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 64;
        }

        private int HandleAdpcmStop(PceCdBiosContext context)
        {
            context.WriteMemory8(0x180D, 0x00); // Stop
            _trace.LogNote("adpcm_stop");
            context.SetAccumulator(0x00);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleAdpcmStat(PceCdBiosContext context)
        {
            byte status = context.ReadMemory8(0x180C);
            _trace.LogNote($"adpcm_stat=0x{status:X2}");
            context.SetAccumulator(status);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleAdpcmWrite(PceCdBiosContext context)
        {
            byte value = context.Cpu.PeekA();
            context.WriteMemory8(0x180A, value);
            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleAdpcmRead(PceCdBiosContext context)
        {
            byte value = context.ReadMemory8(0x180A);
            context.SetAccumulator(value);
            context.ReturnFromSubroutine();
            return 32;
        }

        private int HandleStubSuccess(PceCdBiosContext context)
        {
            context.SetAccumulator(0x00);
            context.Cpu.HleSetCarryFlag(false);
            context.ReturnFromSubroutine();
            return 32;
        }
    }
}
