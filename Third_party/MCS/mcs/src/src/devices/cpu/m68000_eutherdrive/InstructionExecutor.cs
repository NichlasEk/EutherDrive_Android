using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace mame.eutherdrive_m68000;

internal sealed partial class InstructionExecutor
{
    private readonly Registers _registers;
    private readonly IBusInterface _bus;
    private readonly bool _allowTasWrites;
    private readonly string _name;

    private ushort _opcode;
    private Instruction? _instruction;
    private uint _tracePc;
    private bool _traceThisInstruction;
    private bool _pgmDemonFrontZeroTableSignatureChecked;
    private bool _pgmDemonFrontZeroTableSignatureValid;
    private bool _pgmDemonFrontByteSlotSignatureChecked;
    private bool _pgmDemonFrontByteSlotSignatureValid;
    private bool _pgmDemonFrontByteCounterSignatureChecked;
    private bool _pgmDemonFrontByteCounterSignatureValid;
    private bool _pgmObjectMaskSignatureChecked;
    private bool _pgmObjectMaskSignatureValid;

    private static readonly bool TraceExceptions =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_EX"), "1", StringComparison.Ordinal);
    private static readonly string? TraceExceptionsFile =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_EX_FILE");
    private static readonly uint? TracePcMin = ReadHexEnv("EUTHERDRIVE_M68K_TRACE_PC_MIN");
    private static readonly uint? TracePcMax = ReadHexEnv("EUTHERDRIVE_M68K_TRACE_PC_MAX");
    private static readonly string? TracePcFile =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_PC_FILE");
    private static readonly int TracePcLimit = ParseTraceLimit("EUTHERDRIVE_M68K_TRACE_PC_LIMIT", 1024);
    private static int _tracePcRemaining = TracePcLimit;
    private static readonly uint? TraceWriteAddress = ReadHexEnv("EUTHERDRIVE_M68K_TRACE_WRITE_ADDR");
    private static readonly string? TraceWriteFile =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_WRITE_FILE");
    private static readonly bool TracePcIndexed =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC_INDEXED"), "1", StringComparison.Ordinal);
    private static readonly int TracePcIndexedLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_PC_INDEXED_LIMIT", 64);
    private static int _tracePcIndexedRemaining = TracePcIndexed ? TracePcIndexedLimit : 0;
    private static readonly bool TraceOddIndexed =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_ODD_INDEXED"), "1", StringComparison.Ordinal);
    private static readonly string? TraceOddIndexedFile =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_ODD_INDEXED_FILE");
    private static readonly int TraceOddIndexedLimit = ParseTraceLimit("EUTHERDRIVE_M68K_TRACE_ODD_INDEXED_LIMIT", 32);
    private static int _traceOddIndexedRemaining = TraceOddIndexed ? TraceOddIndexedLimit : 0;
    private static readonly bool TraceInterrupts =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_IRQ"), "1", StringComparison.Ordinal);
    private static readonly string? TraceInterruptsFile =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_IRQ_FILE");
    private static readonly string? TraceInterruptCpu =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_IRQ_CPU");
    private static readonly int TraceInterruptLimit = ParseTraceLimit("EUTHERDRIVE_M68K_TRACE_IRQ_LIMIT", 256);
    private static int _traceInterruptRemaining = TraceInterrupts ? TraceInterruptLimit : 0;
    private static readonly bool ProfileOpcodes =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_OPCODE_PROFILE"), "1", StringComparison.Ordinal);
    private static readonly long[] ProfileOpcodeCounts = new long[ushort.MaxValue + 1];
    private static readonly long[] ProfileKindCounts = new long[Enum.GetValues(typeof(InstructionKind)).Length];
    private static readonly Dictionary<ulong, long> ProfilePcOpcodeCounts = new();
    private static long _profileTotalInstructions;
    private static long _profileWindowStartTicks = Stopwatch.GetTimestamp();

    private const uint AddressErrorVector = 3;
    private const uint IllegalOpcodeVector = 4;
    private const uint DivideByZeroVector = 5;
    private const uint CheckRegisterVector = 6;
    private const uint PrivilegeViolationVector = 8;
    private const uint Line1010Vector = 10;
    private const uint Line1111Vector = 11;
    private const uint AutoVectoredInterruptBase = 0x60;

    public InstructionExecutor(Registers registers, IBusInterface bus, bool allowTasWrites, string name)
    {
        _registers = registers;
        _bus = bus;
        _allowTasWrites = allowTasWrites;
        _name = name;
    }

    public uint Execute()
    {
        _registers.AddressError = false;
        _registers.LastInstructionWasMulDiv = false;

        if (_registers.PendingInterruptLevel.HasValue)
        {
            byte level = _registers.PendingInterruptLevel.Value;
            ushort srBefore = _registers.StatusRegister();
            _registers.PendingInterruptLevel = null;
            _bus.AcknowledgeInterrupt(level);
            _registers.Stopped = false;
            MaybeTraceInterrupt("take", level, srBefore, _registers.InterruptPriorityMask, pending: level);
            var interrupt = HandleAutoVectoredInterrupt(level);
            return interrupt.IsOk ? interrupt.Value : HandleException(interrupt.Error!.Value);
        }

        byte interruptLevel = (byte)(_bus.InterruptLevel() & 0x07);
        byte mask = (byte)(_registers.InterruptPriorityMask & 0x07);
        if (interruptLevel > mask)
        {
            _registers.PendingInterruptLevel = interruptLevel;
            MaybeTraceInterrupt("latch", interruptLevel, _registers.StatusRegister(), mask, pending: interruptLevel);
            return 10;
        }
        if (interruptLevel != 0)
            MaybeTraceInterrupt("masked", interruptLevel, _registers.StatusRegister(), mask, pending: null);

        if (_registers.Stopped)
            return 4;

        var result = DoExecute();
        return result.IsOk ? result.Value : HandleException(result.Error!.Value);
    }

    public bool TryConsumeIdleLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (cycleBudget < 4 || _registers.Frozen || _registers.Stopped || _registers.PendingInterruptLevel.HasValue)
            return false;

        byte interruptLevel = (byte)(_bus.InterruptLevel() & 0x07);
        if (interruptLevel > (_registers.InterruptPriorityMask & 0x07))
            return false;

        uint pc = _registers.Pc;
        switch (pc)
        {
            case 0x00106868:
            case 0x00106884:
                return TryConsumeKovWaitLoop(cycleBudget, out cycles);

            case 0x000011A8:
                return TryConsumePgmArmResultWaitLoop(cycleBudget, out cycles);

            case 0x00101B04:
                return TryConsumePgmSpriteFlagScanLoop(cycleBudget, out cycles);

            case 0x0010E2C6:
                return TryConsumePgmSvgLatchDelayLoop(cycleBudget, out cycles);

            case 0x00125308:
                return TryConsumePgmObjectMaskScanLoop(cycleBudget, out cycles);

            case 0x00125AB0:
            case 0x00125D2C:
                return TryConsumePgmObjectBitScanLoop(cycleBudget, out cycles);

            case 0x00125B44:
            case 0x00125C04:
                return TryConsumePgmDemonFrontObjectAccumulatorLoop(cycleBudget, out cycles);

            case 0x0010775E:
                return TryConsumePgmDemonFrontCopy10TailLoop(cycleBudget, out cycles);

            case 0x00101E64:
            case 0x00101E6E:
                return TryConsumePgmDemonFrontTileWordGatherLoop(cycleBudget, out cycles);

            case 0x001028B4:
                return TryConsumePgmDemonFrontWordFillDbfLoop(cycleBudget, out cycles);

            case 0x00102784:
                return TryConsumePgmDemonFrontWordCopyDbfLoop(cycleBudget, out cycles);

            case 0x00005E1E:
            case 0x00005E20:
                return TryConsumePgmDemonFrontReadyPollLoop(cycleBudget, out cycles);
        }

        switch (_registers.Prefetch)
        {
            case 0x20C0:
                return TryConsumeMoveLongFillDbfLoop(cycleBudget, out cycles);

            case 0x4A39:
                return TryConsumeTstBneIdleLoop(cycleBudget, out cycles);

            case 0x5279:
                return TryConsumeMetalSlugFrameWaitLoop(cycleBudget, out cycles);

            case 0x13C0:
                if (TryConsumeNeoGeoInputPollLoop(cycleBudget, out cycles))
                    return true;
                if (TryConsumeNeoGeoBiosChecksumLoop(cycleBudget, out cycles))
                    return true;
                return TryConsumeNeoGeoWatchdogVramPortFillLoop(cycleBudget, out cycles);

            case 0x1480:
                return TryConsumeNeoGeoRamFillLoop(cycleBudget, out cycles);

            case 0x3080:
                return TryConsumeNeoGeoVramPortFillLoop(cycleBudget, out cycles);

            case 0x389A:
                return TryConsumeNeoGeoVramPortCopyLoop(cycleBudget, out cycles);
        }

        return false;
    }

    private bool TryConsumePgmDemonFrontZeroTableLookup(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (_registers.Pc != 0x00101D7E)
            return false;

        if (!_pgmDemonFrontZeroTableSignatureChecked)
        {
            _pgmDemonFrontZeroTableSignatureValid =
                _bus.ReadWord(0x00101D7E) == 0x202F
                && _bus.ReadWord(0x00101D82) == 0x2200
                && _bus.ReadWord(0x00101D84) == 0xD080
                && _bus.ReadWord(0x00101D86) == 0xD081
                && _bus.ReadWord(0x00101D88) == 0xD080
                && _bus.ReadWord(0x00101D8A) == 0x207C
                && _bus.ReadWord(0x00101D90) == 0xD1C0
                && _bus.ReadWord(0x00101D92) == 0x2248
                && _bus.ReadWord(0x00101D94) == 0x4A69
                && _bus.ReadWord(0x00101D98) == 0x671A
                && _bus.ReadWord(0x00101DB4) == 0x7000
                && _bus.ReadWord(0x00101DB6) == 0x4E75;
            _pgmDemonFrontZeroTableSignatureChecked = true;
        }

        if (!_pgmDemonFrontZeroTableSignatureValid)
        {
            return false;
        }

        uint stackPointer = _registers.StackPointer();
        uint index = _bus.ReadLong(stackPointer + unchecked((uint)(short)_bus.ReadWord(0x00101D80)));
        uint baseAddress = ((uint)_bus.ReadWord(0x00101D8C) << 16) | _bus.ReadWord(0x00101D8E);
        uint entryAddress = (baseAddress + index * 6u) & 0x00ff_ffff;
        uint testAddress = (entryAddress + unchecked((uint)(short)_bus.ReadWord(0x00101D96))) & 0x00ff_ffff;
        if (_bus.ReadWord(testAddress) != 0)
            return false;

        uint returnAddress = _bus.ReadLong(stackPointer);
        _registers.SetStackPointer(stackPointer + 4u);
        _registers.Data[0] = 0;
        _registers.Data[1] = index;
        _registers.Address[0] = entryAddress;
        _registers.Address[1] = entryAddress;
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = true;
        _registers.Ccr.Negative = false;
        _registers.Pc = returnAddress & 0x00ff_ffff;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = (uint)Math.Max(80, cycleBudget);
        return true;
    }

    private bool TryConsumePgmDemonFrontByteSlotScanLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (_registers.Pc != 0x00105C8E)
            return false;

        if (!_pgmDemonFrontByteSlotSignatureChecked)
        {
            _pgmDemonFrontByteSlotSignatureValid =
                _bus.ReadWord(0x00105C8E) == 0x0C12
                && _bus.ReadWord(0x00105C92) == 0x6638
                && _bus.ReadWord(0x00105CCC) == 0x588A
                && _bus.ReadWord(0x00105CCE) == 0x2002
                && _bus.ReadWord(0x00105CD0) == 0x5342
                && _bus.ReadWord(0x00105CD2) == 0x4A40
                && _bus.ReadWord(0x00105CD4) == 0x66B8
                && _bus.ReadWord(0x00105CD6) == 0x4CDF;
            _pgmDemonFrontByteSlotSignatureChecked = true;
        }

        if (!_pgmDemonFrontByteSlotSignatureValid)
        {
            return false;
        }

        byte compareValue = (byte)_bus.ReadWord(0x00105C90);
        uint a2 = _registers.Address[2];
        ushort d2 = (ushort)_registers.Data[2];
        uint maxIterations = Math.Min((uint)d2 + 1u, Math.Max(1u, (uint)cycleBudget / 58u));
        uint iterations = 0;
        ushort lastD0 = d2;

        while (iterations < maxIterations)
        {
            if (_bus.ReadByte(a2) == compareValue)
                break;

            a2 = (a2 + 4u) & 0x00ff_ffff;
            lastD0 = d2;
            d2--;
            iterations++;

            if (lastD0 == 0)
                break;
        }

        if (iterations == 0)
            return false;

        _registers.Address[2] = a2;
        _registers.Data[0] = lastD0;
        _registers.Data[2] = (_registers.Data[2] & 0xffff_0000u) | d2;
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = lastD0 == 0;
        _registers.Ccr.Negative = lastD0.SignBit();
        _registers.Pc = lastD0 == 0 ? 0x00105CD6u : 0x00105C8Eu;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = Math.Max(58u, iterations * 58u);
        return true;
    }

    private bool TryConsumePgmDemonFrontByteCounterTail(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (_registers.Pc != 0x00107A0C)
            return false;

        if (!_pgmDemonFrontByteCounterSignatureChecked)
        {
            _pgmDemonFrontByteCounterSignatureValid =
                _bus.ReadWord(0x00107A0C) == 0x2002
                && _bus.ReadWord(0x00107A0E) == 0x5302
                && _bus.ReadWord(0x00107A10) == 0x4A00
                && _bus.ReadWord(0x00107A12) == 0x6600
                && _bus.ReadWord(0x00107A16) == 0x4CDF
                && _bus.ReadWord(0x00107A1A) == 0x4E75;
            _pgmDemonFrontByteCounterSignatureChecked = true;
        }

        if (!_pgmDemonFrontByteCounterSignatureValid)
        {
            return false;
        }

        uint d2 = _registers.Data[2];
        uint d0 = d2;
        byte newD2 = unchecked((byte)((byte)d2 - 1));
        _registers.Data[0] = d0;
        _registers.Data[2] = (d2 & 0xffff_ff00u) | newD2;
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = (byte)d0 == 0;
        _registers.Ccr.Negative = ((byte)d0).SignBit();
        _registers.Pc = (byte)d0 == 0 ? 0x00107A16u : 0x0010795Eu;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = (uint)Math.Max(34, cycleBudget);
        return true;
    }

    private bool TryConsumePgmDemonFrontCopy10TailLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (_registers.Pc != 0x0010775E)
            return false;

        if (_bus.ReadWord(0x00107758) != 0x26DC
            || _bus.ReadWord(0x0010775A) != 0x26DC
            || _bus.ReadWord(0x0010775C) != 0x36DC
            || _bus.ReadWord(0x0010775E) != 0x2002
            || _bus.ReadWord(0x00107760) != 0x5342
            || _bus.ReadWord(0x00107762) != 0x4A40
            || _bus.ReadWord(0x00107764) != 0x66F2
            || _bus.ReadWord(0x00107766) != 0x246A)
        {
            return false;
        }

        uint d2Full = _registers.Data[2];
        ushort remaining = (ushort)d2Full;
        ushort resultLow;
        uint d0;
        uint iterations = 0;

        if (remaining != 0)
        {
            uint maxIterations = Math.Min((uint)remaining, Math.Max(1u, (uint)cycleBudget / 62u));
            uint a3 = _registers.Address[3];
            uint a4 = _registers.Address[4];

            for (iterations = 0; iterations < maxIterations; iterations++)
            {
                _bus.WriteLong(a3, _bus.ReadLong(a4));
                a3 = (a3 + 4u) & 0x00ff_ffff;
                a4 = (a4 + 4u) & 0x00ff_ffff;

                _bus.WriteLong(a3, _bus.ReadLong(a4));
                a3 = (a3 + 4u) & 0x00ff_ffff;
                a4 = (a4 + 4u) & 0x00ff_ffff;

                _bus.WriteWord(a3, _bus.ReadWord(a4));
                a3 = (a3 + 2u) & 0x00ff_ffff;
                a4 = (a4 + 2u) & 0x00ff_ffff;
            }

            _registers.Address[3] = a3;
            _registers.Address[4] = a4;
        }

        if (iterations >= remaining)
        {
            d0 = d2Full & 0xffff_0000u;
            resultLow = 0xffff;
            _registers.Pc = 0x00107766;
        }
        else
        {
            ushort d0Low = (ushort)(remaining - iterations);
            d0 = (d2Full & 0xffff_0000u) | d0Low;
            resultLow = (ushort)(d0Low - 1);
            _registers.Pc = 0x00107758;
        }

        _registers.Data[0] = d0;
        _registers.Data[2] = (d2Full & 0xffff_0000u) | resultLow;
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = (ushort)d0 == 0;
        _registers.Ccr.Negative = ((ushort)d0).SignBit();
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = Math.Max(34u, 18u + iterations * 62u);
        return true;
    }

    private bool TryConsumePgmDemonFrontWordFillDbfLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if (pc != 0x001028B4
            || _registers.Prefetch != 0x30C0
            || _bus.ReadWord(pc + 2) != 0x51C9
            || _bus.ReadWord(pc + 4) != 0xFFFC)
        {
            return false;
        }

        ushort d1 = (ushort)_registers.Data[1];
        uint remainingIterations = (uint)d1 + 1u;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 18u);
        uint iterations = Math.Min(remainingIterations, maxIterations);
        if (iterations == 0)
            return false;

        uint a0 = _registers.Address[0];
        ushort value = (ushort)_registers.Data[0];
        for (uint i = 0; i < iterations; i++)
        {
            _bus.WriteWord(a0, value);
            a0 = (a0 + 2u) & 0x00ff_ffff;
        }

        _registers.Address[0] = a0;
        _registers.Data[1] = (_registers.Data[1] & 0xffff_0000u) | (ushort)(d1 - iterations);
        SetMoveWordFlags(value);

        cycles = Math.Max(18u, iterations * 18u);
        if (iterations == remainingIterations)
        {
            cycles += 4;
            _registers.Pc = (pc + 6u) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        }

        return true;
    }

    private bool TryConsumePgmDemonFrontWordCopyDbfLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if (pc != 0x00102784
            || _registers.Prefetch != 0x32D8
            || _bus.ReadWord(pc + 2) != 0x51C8
            || _bus.ReadWord(pc + 4) != 0xFFFC)
        {
            return false;
        }

        ushort d0 = (ushort)_registers.Data[0];
        uint remainingIterations = (uint)d0 + 1u;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 26u);
        uint iterations = Math.Min(remainingIterations, maxIterations);
        if (iterations == 0)
            return false;

        uint a0 = _registers.Address[0];
        uint a1 = _registers.Address[1];
        ushort value = 0;
        for (uint i = 0; i < iterations; i++)
        {
            value = _bus.ReadWord(a0);
            _bus.WriteWord(a1, value);
            a0 = (a0 + 2u) & 0x00ff_ffff;
            a1 = (a1 + 2u) & 0x00ff_ffff;
        }

        _registers.Address[0] = a0;
        _registers.Address[1] = a1;
        _registers.Data[0] = (_registers.Data[0] & 0xffff_0000u) | (ushort)(d0 - iterations);
        SetMoveWordFlags(value);

        cycles = Math.Max(26u, iterations * 26u);
        if (iterations == remainingIterations)
        {
            cycles += 4;
            _registers.Pc = (pc + 6u) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        }

        return true;
    }

    private bool TryConsumeMoveLongFillDbfLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if (_registers.Prefetch != 0x20C0
            || _bus.ReadWord(pc + 2) != 0x51C9
            || _bus.ReadWord(pc + 4) != 0xFFFC)
        {
            return false;
        }

        ushort d1 = (ushort)_registers.Data[1];
        uint remainingIterations = (uint)d1 + 1u;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 30u);
        uint iterations = Math.Min(remainingIterations, maxIterations);
        if (iterations == 0)
            return false;

        uint a0 = _registers.Address[0];
        uint value = _registers.Data[0];
        for (uint i = 0; i < iterations; i++)
        {
            _bus.WriteLong(a0, value);
            a0 = (a0 + 4u) & 0x00ff_ffff;
        }

        _registers.Address[0] = a0;
        _registers.Data[1] = (_registers.Data[1] & 0xffff_0000u) | (ushort)(d1 - iterations);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        cycles = Math.Max(30u, iterations * 30u);
        if (iterations == remainingIterations)
        {
            cycles += 4;
            _registers.Pc = (pc + 6u) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        }

        return true;
    }

    private bool TryConsumePgmDemonFrontTileWordGatherLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if ((pc != 0x00101E64 && pc != 0x00101E6E)
            || _bus.ReadWord(0x00101E64) != 0x32A8
            || _bus.ReadWord(0x00101E68) != 0x5281
            || _bus.ReadWord(0x00101E6A) != 0x5C88
            || _bus.ReadWord(0x00101E6C) != 0x5489
            || _bus.ReadWord(0x00101E6E) != 0x7030
            || _bus.ReadWord(0x00101E70) != 0xB081
            || _bus.ReadWord(0x00101E72) != 0x6EF0)
        {
            return false;
        }

        uint d1 = _registers.Data[1];
        if (pc == 0x00101E6E && d1 >= 0x30)
        {
            _registers.Data[0] = 0x30;
            CompareLongWords(d1, 0x30, ref _registers.Ccr);
            _registers.Pc = 0x00101E74;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            cycles = 18;
            return true;
        }

        if (d1 >= 0x30)
            return false;

        uint remainingIterations = 0x30u - d1;
        const uint cyclesPerIteration = 54;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / cyclesPerIteration);
        uint iterations = Math.Min(remainingIterations, maxIterations);
        short sourceOffset = (short)_bus.ReadWord(0x00101E66);
        uint a0 = _registers.Address[0];
        uint a1 = _registers.Address[1];

        for (uint i = 0; i < iterations; i++)
        {
            ushort value = _bus.ReadWord((a0 + unchecked((uint)sourceOffset)) & 0x00ff_ffff);
            _bus.WriteWord(a1, value);
            d1 = (d1 + 1u) & 0xffff_ffffu;
            a0 = (a0 + 6u) & 0x00ff_ffff;
            a1 = (a1 + 2u) & 0x00ff_ffff;
        }

        _registers.Address[0] = a0;
        _registers.Address[1] = a1;
        _registers.Data[0] = 0x30;
        _registers.Data[1] = d1;
        CompareLongWords(d1, 0x30, ref _registers.Ccr);
        _registers.Pc = d1 < 0x30 ? 0x00101E64u : 0x00101E74u;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = Math.Max(cyclesPerIteration, iterations * cyclesPerIteration);
        return true;
    }

    private bool TryConsumePgmDemonFrontReadyPollLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if ((pc != 0x00005E1E && pc != 0x00005E20)
            || _bus.ReadWord(0x00005E1E) != 0x5282
            || _bus.ReadWord(0x00005E20) != 0x4A39
            || _bus.ReadWord(0x00005E26) != 0x67F6)
        {
            return false;
        }

        uint pollAddress = ((uint)_bus.ReadWord(0x00005E22) << 16) | _bus.ReadWord(0x00005E24);
        byte value = _bus.ReadByte(pollAddress);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        if (pc == 0x00005E20)
        {
            _registers.Pc = value == 0 ? 0x00005E1Eu : 0x00005E28u;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            cycles = value == 0 ? 26u : 24u;
            return true;
        }

        if (value == 0)
        {
            const uint cyclesPerIteration = 34;
            uint iterations = Math.Max(1u, (uint)cycleBudget / cyclesPerIteration);
            _registers.Data[2] += iterations;
            _registers.Pc = 0x00005E1E;
            _registers.Prefetch = 0x5282;
            cycles = iterations * cyclesPerIteration;
            return true;
        }

        _registers.Data[2]++;
        _registers.Pc = 0x00005E28;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = 32;
        return true;
    }

    private bool TryConsumePgmDemonFrontObjectAccumulatorLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint start = _registers.Pc;
        if (start != 0x00125B44 && start != 0x00125C04)
            return false;

        bool usesThreshold;
        uint fallthroughOffset;
        uint cyclesPerIteration;
        short firstDeltaOffset;
        short secondCountOffset;
        short secondDeltaOffset;
        short thirdCountOffset;
        short thirdDeltaOffset;

        if (_bus.ReadWord(start + 0x00) != 0x3413
            || _bus.ReadWord(start + 0x02) != 0x0282
            || _bus.ReadWord(start + 0x08) != 0x700A
            || _bus.ReadWord(start + 0x0A) != 0xE0A2
            || _bus.ReadWord(start + 0x0C) != 0x3813
            || _bus.ReadWord(start + 0x0E) != 0x0284
            || _bus.ReadWord(start + 0x14) != 0xEA84
            || _bus.ReadWord(start + 0x16) != 0x3A13
            || _bus.ReadWord(start + 0x18) != 0x0245
            || _bus.ReadWord(start + 0x1C) != 0x4A12)
        {
            return false;
        }

        if (_bus.ReadWord(start + 0x1E) == 0x670E)
        {
            if (_bus.ReadWord(start + 0x20) != 0xBC12
                || _bus.ReadWord(start + 0x22) != 0x620A
                || _bus.ReadWord(start + 0x24) != 0x102A
                || _bus.ReadWord(start + 0x28) != 0x4880
                || _bus.ReadWord(start + 0x2A) != 0xD440
                || _bus.ReadWord(start + 0x2C) != 0x5312
                || _bus.ReadWord(start + 0x2E) != 0x4A2A
                || _bus.ReadWord(start + 0x32) != 0x6712
                || _bus.ReadWord(start + 0x34) != 0xBC2A
                || _bus.ReadWord(start + 0x38) != 0x620C
                || _bus.ReadWord(start + 0x3A) != 0x102A
                || _bus.ReadWord(start + 0x3E) != 0x4880
                || _bus.ReadWord(start + 0x40) != 0xD840
                || _bus.ReadWord(start + 0x42) != 0x532A
                || _bus.ReadWord(start + 0x46) != 0x4A2A
                || _bus.ReadWord(start + 0x4A) != 0x6712
                || _bus.ReadWord(start + 0x4C) != 0xBC2A
                || _bus.ReadWord(start + 0x50) != 0x620C
                || _bus.ReadWord(start + 0x52) != 0x102A
                || _bus.ReadWord(start + 0x56) != 0x4880
                || _bus.ReadWord(start + 0x58) != 0xDA40
                || _bus.ReadWord(start + 0x5A) != 0x532A
                || _bus.ReadWord(start + 0x5E) != 0x3002
                || _bus.ReadWord(start + 0x60) != 0x720A
                || _bus.ReadWord(start + 0x62) != 0xE368
                || _bus.ReadWord(start + 0x64) != 0x3204
                || _bus.ReadWord(start + 0x66) != 0xEB49
                || _bus.ReadWord(start + 0x68) != 0xD041
                || _bus.ReadWord(start + 0x6A) != 0xD045
                || _bus.ReadWord(start + 0x6C) != 0x36C0
                || _bus.ReadWord(start + 0x6E) != 0x5C8A
                || _bus.ReadWord(start + 0x70) != 0x5343
                || _bus.ReadWord(start + 0x72) != 0x668C)
            {
                return false;
            }

            firstDeltaOffset = (short)_bus.ReadWord(start + 0x26);
            secondCountOffset = (short)_bus.ReadWord(start + 0x30);
            short secondCompareOffset = (short)_bus.ReadWord(start + 0x36);
            secondDeltaOffset = (short)_bus.ReadWord(start + 0x3C);
            short secondDecrementOffset = (short)_bus.ReadWord(start + 0x44);
            thirdCountOffset = (short)_bus.ReadWord(start + 0x48);
            short thirdCompareOffset = (short)_bus.ReadWord(start + 0x4E);
            thirdDeltaOffset = (short)_bus.ReadWord(start + 0x54);
            short thirdDecrementOffset = (short)_bus.ReadWord(start + 0x5C);
            if (secondCountOffset != secondCompareOffset
                || secondCountOffset != secondDecrementOffset
                || thirdCountOffset != thirdCompareOffset
                || thirdCountOffset != thirdDecrementOffset)
            {
                return false;
            }

            usesThreshold = true;
            fallthroughOffset = 0x74u;
            cyclesPerIteration = 238u;
        }
        else if (_bus.ReadWord(start + 0x1E) == 0x670A)
        {
            if (_bus.ReadWord(start + 0x20) != 0x102A
                || _bus.ReadWord(start + 0x24) != 0x4880
                || _bus.ReadWord(start + 0x26) != 0xD440
                || _bus.ReadWord(start + 0x28) != 0x5312
                || _bus.ReadWord(start + 0x2A) != 0x4A2A
                || _bus.ReadWord(start + 0x2E) != 0x670C
                || _bus.ReadWord(start + 0x30) != 0x102A
                || _bus.ReadWord(start + 0x34) != 0x4880
                || _bus.ReadWord(start + 0x36) != 0xD840
                || _bus.ReadWord(start + 0x38) != 0x532A
                || _bus.ReadWord(start + 0x3C) != 0x4A2A
                || _bus.ReadWord(start + 0x40) != 0x670C
                || _bus.ReadWord(start + 0x42) != 0x102A
                || _bus.ReadWord(start + 0x46) != 0x4880
                || _bus.ReadWord(start + 0x48) != 0xDA40
                || _bus.ReadWord(start + 0x4A) != 0x532A
                || _bus.ReadWord(start + 0x4E) != 0x3002
                || _bus.ReadWord(start + 0x50) != 0x720A
                || _bus.ReadWord(start + 0x52) != 0xE368
                || _bus.ReadWord(start + 0x54) != 0x3204
                || _bus.ReadWord(start + 0x56) != 0xEB49
                || _bus.ReadWord(start + 0x58) != 0xD041
                || _bus.ReadWord(start + 0x5A) != 0xD045
                || _bus.ReadWord(start + 0x5C) != 0x36C0
                || _bus.ReadWord(start + 0x5E) != 0x5C8A
                || _bus.ReadWord(start + 0x60) != 0x5343
                || _bus.ReadWord(start + 0x62) != 0x669C)
            {
                return false;
            }

            firstDeltaOffset = (short)_bus.ReadWord(start + 0x22);
            secondCountOffset = (short)_bus.ReadWord(start + 0x2C);
            secondDeltaOffset = (short)_bus.ReadWord(start + 0x32);
            short secondDecrementOffset = (short)_bus.ReadWord(start + 0x3A);
            thirdCountOffset = (short)_bus.ReadWord(start + 0x3E);
            thirdDeltaOffset = (short)_bus.ReadWord(start + 0x44);
            short thirdDecrementOffset = (short)_bus.ReadWord(start + 0x4C);
            if (secondCountOffset != secondDecrementOffset || thirdCountOffset != thirdDecrementOffset)
            {
                return false;
            }

            usesThreshold = false;
            fallthroughOffset = 0x64u;
            cyclesPerIteration = 186u;
        }
        else
        {
            return false;
        }

        ushort d3 = (ushort)_registers.Data[3];
        if (d3 == 0)
            return false;

        uint maxIterations = Math.Min((uint)d3, Math.Max(1u, (uint)cycleBudget / cyclesPerIteration));
        uint a2 = _registers.Address[2];
        uint a3 = _registers.Address[3];
        uint d0 = _registers.Data[0];
        uint d1 = _registers.Data[1];
        uint d2 = _registers.Data[2];
        uint d4 = _registers.Data[4];
        uint d5High = _registers.Data[5] & 0xffff_0000u;
        uint d5 = _registers.Data[5];
        byte threshold = (byte)_registers.Data[6];
        uint firstMask = _bus.ReadLong(start + 0x04);
        uint secondMask = _bus.ReadLong(start + 0x10);
        ushort thirdMask = _bus.ReadWord(start + 0x1A);
        uint iterations = 0;

        while (iterations < maxIterations)
        {
            ushort packed = _bus.ReadWord(a3);
            d2 = (packed & firstMask) >> 10;
            d4 = (packed & secondMask) >> 5;
            d5 = d5High | (uint)(packed & thirdMask);

            ApplyDemonFrontObjectChannel(a2, 0, firstDeltaOffset, threshold, usesThreshold, ref d0, ref d2);
            ApplyDemonFrontObjectChannel(a2, secondCountOffset, secondDeltaOffset, threshold, usesThreshold, ref d0, ref d4);
            ApplyDemonFrontObjectChannel(a2, thirdCountOffset, thirdDeltaOffset, threshold, usesThreshold, ref d0, ref d5);

            d0 = (d0 & 0xffff_0000u) | (((d2 & 0x3fu) << 10) & 0xffffu);
            d1 = (d1 & 0xffff_0000u) | (((d4 & 0x1fu) << 5) & 0xffffu);
            d0 = (d0 & 0xffff_0000u) | ((d0 + d1 + (d5 & 0x1fu)) & 0xffffu);
            _bus.WriteWord(a3, (ushort)d0);

            a2 = (a2 + 6u) & 0x00ff_ffff;
            a3 = (a3 + 2u) & 0x00ff_ffff;
            d3--;
            iterations++;
        }

        _registers.Address[2] = a2;
        _registers.Address[3] = a3;
        _registers.Data[0] = d0;
        _registers.Data[1] = d1;
        _registers.Data[2] = d2;
        _registers.Data[3] = (_registers.Data[3] & 0xffff_0000u) | d3;
        _registers.Data[4] = d4;
        _registers.Data[5] = d5;
        _registers.Pc = d3 == 0 ? start + fallthroughOffset : start;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = d3 == 0;
        _registers.Ccr.Negative = d3.SignBit();
        _registers.Ccr.Extend = false;
        cycles = Math.Max(cyclesPerIteration, iterations * cyclesPerIteration);
        return true;
    }

    private void ApplyDemonFrontObjectChannel(uint baseAddress, short countOffset, short deltaOffset, byte threshold, bool usesThreshold, ref uint d0, ref uint accumulator)
    {
        uint countAddress = (baseAddress + unchecked((uint)countOffset)) & 0x00ff_ffff;
        byte count = _bus.ReadByte(countAddress);
        if (count == 0 || (usesThreshold && count < threshold))
            return;

        uint deltaAddress = (baseAddress + unchecked((uint)deltaOffset)) & 0x00ff_ffff;
        byte deltaByte = _bus.ReadByte(deltaAddress);
        d0 = (d0 & 0xffff_0000u) | (ushort)(short)(sbyte)deltaByte;
        accumulator = (accumulator & 0xffff_0000u) | (ushort)((ushort)accumulator + (short)(sbyte)deltaByte);
        _bus.WriteByte(countAddress, (byte)(count - 1));
    }

    private bool TryConsumePgmArmResultWaitLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (_registers.Pc != 0x0011A8 || _registers.Address[2] != 0)
            return false;

        if (_bus.ReadWord(0x0011A8) != 0x200A
            || _bus.ReadWord(0x0011AA) != 0x670A
            || _bus.ReadWord(0x0011B6) != 0x2F02
            || _bus.ReadWord(0x0011B8) != 0x6100
            || _bus.ReadWord(0x0011BC) != 0x588F
            || _bus.ReadWord(0x0011BE) != 0x4A80
            || _bus.ReadWord(0x0011C0) != 0x66E6
            || _bus.ReadWord(0x001012) != 0x2F02
            || _bus.ReadWord(0x001014) != 0x242F
            || _bus.ReadWord(0x001018) != 0x4A82
            || _bus.ReadWord(0x00103C) != 0x3002
            || _bus.ReadWord(0x00103E) != 0xE548
            || _bus.ReadWord(0x001040) != 0x207C
            || _bus.ReadWord(0x001042) != 0x0080
            || _bus.ReadWord(0x001044) != 0x1550
            || _bus.ReadWord(0x00104A) != 0x241F
            || _bus.ReadWord(0x00104C) != 0x4E75)
        {
            return false;
        }

        uint index = _registers.Data[2] & 0xffff;
        if (index >= 4)
            return false;

        uint result = _bus.ReadLong(0x00801550 + index * 4);
        if (result == 0)
            return false;

        cycles = (uint)Math.Max(118, cycleBudget);
        return true;
    }

    private bool TryConsumePgmSvgLatchDelayLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if (pc == 0x0010E2C6)
        {
            return TryConsumePgmSvgLatchDelayLoopVariant(
                cycleBudget,
                out cycles,
                start: 0x0010E2C6,
                firstBsrDisplacement: 0xFF70,
                secondBsrDisplacement: 0xFF66,
                mismatchBranch: 0x6614,
                equalBranch: 0x6768,
                addOffset: 0x2C,
                compareOffset: 0x2E,
                limitOffset: 0x30,
                loopBranchOffset: 0x34,
                loopBranch: 0x65CA,
                fallthroughOffset: 0x36,
                cyclesPerIteration: 188,
                iterationBudgetCycles: 188,
                consumeToLimit: false);
        }

        return false;
    }

    private bool TryConsumePgmSvgLatchDelayLoopVariant(
        int cycleBudget,
        out uint cycles,
        uint start,
        ushort firstBsrDisplacement,
        ushort secondBsrDisplacement,
        ushort mismatchBranch,
        ushort equalBranch,
        uint addOffset,
        uint compareOffset,
        uint limitOffset,
        uint loopBranchOffset,
        ushort loopBranch,
        uint fallthroughOffset,
        uint cyclesPerIteration,
        uint iterationBudgetCycles,
        bool consumeToLimit)
    {
        cycles = 0;
        ushort firstBsr = _bus.ReadWord(start + 0x02);
        ushort secondBsr = _bus.ReadWord(start + 0x0C);
        if (_bus.ReadWord(start) != 0x6100
            || (firstBsr != firstBsrDisplacement && firstBsr != unchecked((ushort)(firstBsrDisplacement + 2)))
            || _bus.ReadWord(start + 0x04) != 0x0240
            || _bus.ReadWord(start + 0x08) != 0x3600
            || _bus.ReadWord(start + 0x0A) != 0x6100
            || (secondBsr != secondBsrDisplacement && secondBsr != unchecked((ushort)(secondBsrDisplacement + 2)))
            || _bus.ReadWord(start + 0x0E) != 0x0240
            || _bus.ReadWord(start + 0x12) != 0x3800
            || _bus.ReadWord(start + 0x14) != 0xB644
            || _bus.ReadWord(start + 0x16) != mismatchBranch
            || _bus.ReadWord(start + 0x18) != 0x3003
            || _bus.ReadWord(start + 0x1A) != 0x0280
            || _bus.ReadWord(start + 0x20) != 0x7200
            || _bus.ReadWord(start + 0x22) != 0x1239
            || _bus.ReadWord(start + 0x28) != 0xB081
            || _bus.ReadWord(start + 0x2A) != equalBranch
            || _bus.ReadWord(start + addOffset) != 0x5282
            || _bus.ReadWord(start + compareOffset) != 0x0C82
            || _bus.ReadWord(start + loopBranchOffset) != loopBranch)
        {
            return false;
        }

        uint d2 = _registers.Data[2];
        uint limit = _bus.ReadLong(start + limitOffset);
        if (d2 >= limit)
            return false;

        uint mask = _bus.ReadWord(start + 0x06);
        ushort latch = _bus.ReadWord(0x005C0300);
        uint masked = latch & mask;
        uint narrowed = masked & _bus.ReadLong(start + 0x1C);
        uint targetAddress = ((uint)_bus.ReadWord(start + 0x24) << 16) | _bus.ReadWord(start + 0x26);
        uint target = _bus.ReadByte(targetAddress);
        if (narrowed == target)
            return false;

        uint maxIterations = consumeToLimit
            ? limit - d2
            : Math.Max(1u, (uint)cycleBudget / iterationBudgetCycles);
        uint iterations = Math.Min(limit - d2, maxIterations);
        if (iterations == 0)
            return false;

        d2 += iterations;
        _registers.Data[0] = narrowed;
        _registers.Data[1] = target;
        _registers.Data[2] = d2;
        _registers.Data[3] = (_registers.Data[3] & 0xffff_0000u) | masked;
        _registers.Data[4] = (_registers.Data[4] & 0xffff_0000u) | masked;
        _registers.Pc = d2 >= limit ? start + fallthroughOffset : start;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = Math.Max(cyclesPerIteration, iterations * cyclesPerIteration);
        return true;
    }

    private bool TryConsumePgmSpriteFlagScanLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (_registers.Pc != 0x00101B04)
            return false;

        if (_bus.ReadWord(0x00101B04) != 0x7000
            || _bus.ReadWord(0x00101B06) != 0x1012
            || _bus.ReadWord(0x00101B08) != 0x7203
            || _bus.ReadWord(0x00101B0A) != 0xB081
            || _bus.ReadWord(0x00101B0C) != 0x6264
            || _bus.ReadWord(0x00101B20) != 0x2004
            || _bus.ReadWord(0x00101B22) != 0xC082
            || _bus.ReadWord(0x00101B24) != 0x6704
            || _bus.ReadWord(0x00101B2A) != 0x2004
            || _bus.ReadWord(0x00101B2C) != 0xC082
            || _bus.ReadWord(0x00101B2E) != 0x670C
            || _bus.ReadWord(0x00101B3C) != 0x4212
            || _bus.ReadWord(0x00101B3E) != 0x6032
            || _bus.ReadWord(0x00101B72) != 0x5C8A
            || _bus.ReadWord(0x00101B74) != 0xD482
            || _bus.ReadWord(0x00101B76) != 0x51CB
            || (_bus.ReadWord(0x00101B78) != 0xFF8A && _bus.ReadWord(0x00101B78) != 0xFF8C)
            || _bus.ReadWord(0x00101B7A) != 0x4CDF
            || _bus.ReadWord(0x00101B7E) != 0x4E75)
        {
            return false;
        }

        uint a2 = _registers.Address[2];
        uint d2 = _registers.Data[2];
        uint d3Full = _registers.Data[3];
        ushort d3 = (ushort)d3Full;
        uint d4 = _registers.Data[4];
        uint maxIterations = Math.Min((uint)d3 + 1u, Math.Max(1u, (uint)cycleBudget / 82u));
        uint iterations = 0;

        while (iterations < maxIterations)
        {
            if (_bus.ReadByte(a2) != 0 || (d4 & d2) != 0)
                break;

            a2 = (a2 + 6u) & 0x00ff_ffff;
            d2 = (d2 << 1) & 0xffff_ffffu;
            iterations++;

            if (d3 == 0)
            {
                d3 = 0xffff;
                break;
            }

            d3--;
        }

        if (iterations == 0)
            return false;

        _registers.Address[2] = a2;
        _registers.Data[0] = 0;
        _registers.Data[1] = 3;
        _registers.Data[2] = d2;
        _registers.Data[3] = (d3Full & 0xffff_0000u) | d3;
        _registers.Pc = d3 == 0xffff ? 0x00101B7Au : 0x00101B04u;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = Math.Max(82u, iterations * 82u);
        return true;
    }

    private bool TryConsumePgmObjectMaskScanLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        if (_registers.Pc != 0x00125308)
            return false;

        if (!_pgmObjectMaskSignatureChecked)
        {
            _pgmObjectMaskSignatureValid =
                _bus.ReadWord(0x00125308) == 0x1A14
                && _bus.ReadWord(0x0012530A) == 0x0205
                && _bus.ReadWord(0x0012530E) == 0x4A05
                && _bus.ReadWord(0x00125310) == 0x6700
                && _bus.ReadWord(0x00125312) == 0x009C
                && _bus.ReadWord(0x00125314) == 0x7A00
                && _bus.ReadWord(0x00125316) == 0x1A14
                && _bus.ReadWord(0x00125318) == 0xE88D
                && _bus.ReadWord(0x0012531A) == 0x0205
                && _bus.ReadWord(0x0012531E) == 0x4A05
                && _bus.ReadWord(0x00125320) == 0x6700
                && _bus.ReadWord(0x00125322) == 0x008C
                && _bus.ReadWord(0x001253AE) == 0x7054
                && _bus.ReadWord(0x001253B0) == 0x99C0
                && _bus.ReadWord(0x001253B2) == 0x51CC;
            _pgmObjectMaskSignatureChecked = true;
        }

        if (!_pgmObjectMaskSignatureValid)
        {
            return false;
        }

        byte mask = (byte)_bus.ReadWord(0x0012530C);
        byte shiftedMask = (byte)_bus.ReadWord(0x0012531C);
        uint a4 = _registers.Address[4];
        ushort d4 = (ushort)_registers.Data[4];
        uint maxIterations = Math.Min((uint)d4 + 1u, Math.Max(1u, (uint)cycleBudget / 52u));
        uint iterations = 0;

        while (iterations < maxIterations)
        {
            byte value = _bus.ReadByte(a4);
            byte masked = (byte)(value & mask);
            if (masked != 0 && (((uint)value >> 4) & shiftedMask) != 0)
                break;

            iterations++;
            a4 = (a4 - 0x54u) & 0x00ff_ffff;
            if (d4 == 0)
            {
                d4 = 0xffff;
                break;
            }
            d4--;
        }

        if (iterations == 0)
            return false;

        _registers.Address[4] = a4;
        _registers.Data[0] = 0x54;
        _registers.Data[4] = (_registers.Data[4] & 0xffff_0000u) | d4;
        _registers.Data[5] &= 0xffff_ff00u;
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = true;
        _registers.Ccr.Negative = false;

        _registers.Pc = d4 == 0xffff ? 0x001253B6u : 0x00125308u;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = Math.Max(52u, iterations * 52u);
        return true;
    }

    private bool TryConsumePgmObjectBitScanLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if (pc != 0x00125D2C && pc != 0x00125AB0)
            return false;

        if (_bus.ReadWord(0x00125AB0) != 0x082C
            || _bus.ReadWord(0x00125AB6) != 0x6700
            || _bus.ReadWord(0x00125ABA) != 0x082C
            || _bus.ReadWord(0x00125AC0) != 0x6600
            || _bus.ReadWord(0x00125D26) != 0x5287
            || _bus.ReadWord(0x00125D28) != 0x49EC
            || _bus.ReadWord(0x00125D2C) != 0x703C
            || _bus.ReadWord(0x00125D2E) != 0xB087
            || _bus.ReadWord(0x00125D30) != 0x6E00)
        {
            return false;
        }

        uint d7 = _registers.Data[7];
        uint a4 = _registers.Address[4];
        byte bit1 = (byte)(_bus.ReadWord(0x00125AB2) & 7);
        int disp1 = (short)_bus.ReadWord(0x00125AB4);
        byte bit2 = (byte)(_bus.ReadWord(0x00125ABC) & 7);
        int disp2 = (short)_bus.ReadWord(0x00125ABE);
        int step = (short)_bus.ReadWord(0x00125D2A);
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 58u);
        uint iterations = 0;

        while (d7 < 0x3c && iterations < maxIterations)
        {
            bool firstBitSet = ((_bus.ReadByte((a4 + unchecked((uint)disp1)) & 0x00ff_ffff) >> bit1) & 1) != 0;
            bool skip = !firstBitSet;
            if (!skip)
            {
                bool secondBitSet = ((_bus.ReadByte((a4 + unchecked((uint)disp2)) & 0x00ff_ffff) >> bit2) & 1) != 0;
                skip = secondBitSet;
            }

            if (!skip)
                break;

            d7 = (d7 + 1) & 0xffff_ffffu;
            a4 = (a4 + unchecked((uint)step)) & 0x00ff_ffff;
            iterations++;
        }

        if (iterations == 0)
            return false;

        _registers.Data[0] = 0x3c;
        _registers.Data[7] = d7;
        _registers.Address[4] = a4;
        _registers.Pc = 0x00125D2C;
        _registers.Prefetch = _bus.ReadWord(_registers.Pc);
        cycles = Math.Max(58u, iterations * 58u);
        return true;
    }

    private bool TryConsumeKovWaitLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if ((pc != 0x00106868 && pc != 0x00106884)
            || _bus.ReadWord(0x00106868) != 0x4AB9
            || _bus.ReadWord(0x0010686A) != 0x0080
            || _bus.ReadWord(0x0010686C) != 0xB78C
            || _bus.ReadWord(0x0010686E) != 0x6714
            || _bus.ReadWord(0x00106884) != 0x4A39
            || _bus.ReadWord(0x00106886) != 0x0080
            || _bus.ReadWord(0x00106888) != 0xB79C
            || _bus.ReadWord(0x0010688A) != 0x67DC)
        {
            return false;
        }

        if (_bus.ReadLong(0x0080B78C) != 0 || _bus.ReadByte(0x0080B79C) != 0)
            return false;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = true;
        _registers.Ccr.Negative = false;

        cycles = (uint)Math.Max(58, cycleBudget);
        return true;
    }

    private bool TryConsumeMetalSlugFrameWaitLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;

        // Metal Slug frame wait:
        // addq.w #1,$106ee0; clr.b $106edd; cmpi.b #imm,$106ede;
        // beq $2004; tst.b $106ed8; beq loop.
        if (_registers.Prefetch != 0x5279
            || _bus.ReadWord(pc + 2) != 0x0010
            || _bus.ReadWord(pc + 4) != 0x6EE0
            || _bus.ReadWord(pc + 6) != 0x4239
            || _bus.ReadWord(pc + 8) != 0x0010
            || _bus.ReadWord(pc + 10) != 0x6EDD
            || _bus.ReadWord(pc + 12) != 0x0C39
            || _bus.ReadWord(pc + 16) != 0x0010
            || _bus.ReadWord(pc + 18) != 0x6EDE
            || _bus.ReadWord(pc + 20) != 0x6700
            || _bus.ReadWord(pc + 22) != 0x000C
            || _bus.ReadWord(pc + 34) != 0x4A39
            || _bus.ReadWord(pc + 36) != 0x0010
            || _bus.ReadWord(pc + 38) != 0x6ED8
            || _bus.ReadWord(pc + 40) != 0x67D6)
        {
            return false;
        }

        byte compareValue = (byte)_bus.ReadWord(pc + 14);
        if (_bus.ReadByte(0x00106EDE) != compareValue || _bus.ReadByte(0x00106ED8) != 0)
            return false;

        const uint cyclesPerIteration = 92;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / cyclesPerIteration);
        ushort counter = _bus.ReadWord(0x00106EE0);
        counter = (ushort)(counter + maxIterations);
        _bus.WriteWord(0x00106EE0, counter);
        _bus.WriteByte(0x00106EDD, 0);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = true;
        _registers.Ccr.Negative = false;

        cycles = maxIterations * cyclesPerIteration;
        return true;
    }

    private bool TryConsumeTstBneIdleLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;
        if (_registers.Prefetch != 0x4A39 || _bus.ReadWord(pc + 6) != 0x66F8)
            return false;

        uint address = ((uint)_bus.ReadWord(pc + 2) << 16) | _bus.ReadWord(pc + 4);
        byte value = _bus.ReadByte(address);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        if (value == 0)
            return false;

        cycles = (uint)Math.Max(22, cycleBudget);
        return true;
    }

    private bool TryConsumeNeoGeoInputPollLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;

        // NeoGeo BIOS busy-waits on the coin/audio input edge:
        // move.b D0,$300001; move.b D0,D2; move.b $320001,D0; move.b D0,D1;
        // eor.b D1,D2; and.b D1,D2; andi.b #mask,D2; beq loop.
        if (_registers.Prefetch != 0x13C0
            || _bus.ReadWord(pc + 2) != 0x0030
            || _bus.ReadWord(pc + 4) != 0x0001
            || _bus.ReadWord(pc + 6) != 0x1400
            || _bus.ReadWord(pc + 8) != 0x1039
            || _bus.ReadWord(pc + 10) != 0x0032
            || _bus.ReadWord(pc + 12) != 0x0001
            || _bus.ReadWord(pc + 14) != 0x1200
            || _bus.ReadWord(pc + 16) != 0xB302
            || _bus.ReadWord(pc + 18) != 0xC401
            || _bus.ReadWord(pc + 20) != 0x0202
            || _bus.ReadWord(pc + 24) != 0x67E6)
        {
            return false;
        }

        byte previous = (byte)_registers.Data[0];
        _bus.WriteByte(0x00300001, previous);

        byte current = _bus.ReadByte(0x00320001);
        byte mask = (byte)_bus.ReadWord(pc + 22);
        byte edge = (byte)(((previous ^ current) & current) & mask);

        _registers.Data[0] = (_registers.Data[0] & 0xffff_ff00) | current;
        _registers.Data[1] = (_registers.Data[1] & 0xffff_ff00) | current;
        _registers.Data[2] = (_registers.Data[2] & 0xffff_ff00) | edge;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = edge == 0;
        _registers.Ccr.Negative = edge.SignBit();

        if (edge != 0)
        {
            _registers.Pc = (pc + 26) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            cycles = 64;
            return true;
        }

        cycles = (uint)Math.Max(66, cycleBudget);
        return true;
    }

    private bool TryConsumeNeoGeoBiosChecksumLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;

        // NeoGeo BIOS checksum loop:
        // move.b D0,$300001; add.b (A0)+,D0; subq.l #1,D7; bne loop.
        if (_registers.Prefetch != 0x13C0
            || _bus.ReadWord(pc + 2) != 0x0030
            || _bus.ReadWord(pc + 4) != 0x0001
            || _bus.ReadWord(pc + 6) != 0xD018
            || _bus.ReadWord(pc + 8) != 0x5387
            || _bus.ReadWord(pc + 10) != 0x66F4)
        {
            return false;
        }

        uint remaining = _registers.Data[7];
        if (remaining == 0)
            return false;

        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 42u);
        uint iterations = Math.Min(remaining, maxIterations);
        byte d0 = (byte)_registers.Data[0];
        uint a0 = _registers.Address[0];

        _bus.WriteByte(0x00300001, d0);

        for (uint i = 0; i < iterations; i++)
        {
            d0 = unchecked((byte)(d0 + _bus.ReadByte(a0)));
            a0++;
        }

        uint d7BeforeSub = remaining - iterations + 1;
        uint d7 = remaining - iterations;
        var (_, carry, overflow) = SubLongWords(d7BeforeSub, 1, false);

        _registers.Data[0] = (_registers.Data[0] & 0xffff_ff00) | d0;
        _registers.Address[0] = a0;
        _registers.Data[7] = d7;

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = d7 == 0;
        _registers.Ccr.Negative = d7.SignBit();
        _registers.Ccr.Extend = carry;

        cycles = iterations * 42u;
        if (d7 == 0)
        {
            _registers.Pc = (pc + 12) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            return true;
        }

        cycles = Math.Max(42u, cycles);
        return true;
    }

    private bool TryConsumeNeoGeoRamFillLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;

        // NeoGeo BIOS main-RAM fill:
        // move.b D0,(A2); move.w D0,(A0)+; dbf D7,loop.
        if (_registers.Prefetch != 0x1480
            || _bus.ReadWord(pc + 2) != 0x30C0
            || _bus.ReadWord(pc + 4) != 0x51CF
            || _bus.ReadWord(pc + 6) != 0xFFFA
            || _registers.Address[2] != 0x00300001)
        {
            return false;
        }

        uint a0 = _registers.Address[0];
        if ((a0 & 1) != 0 || a0 < 0x00100000 || a0 >= 0x00110000)
            return false;

        uint wordsUntilRamEnd = (0x00110000 - a0) / 2;
        if (wordsUntilRamEnd == 0)
            return false;

        ushort d7 = (ushort)_registers.Data[7];
        uint remainingIterations = (uint)d7 + 1u;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 26u);
        uint iterations = Math.Min(Math.Min(remainingIterations, maxIterations), wordsUntilRamEnd);
        if (iterations == 0)
            return false;

        byte watchdogValue = (byte)_registers.Data[0];
        ushort value = (ushort)_registers.Data[0];
        _bus.WriteByte(0x00300001, watchdogValue);

        for (uint i = 0; i < iterations; i++)
        {
            _bus.WriteWord(a0, value);
            a0 += 2;
        }

        _registers.Address[0] = a0;
        ushort newD7 = (ushort)(d7 - iterations);
        _registers.Data[7] = (_registers.Data[7] & 0xffff_0000) | newD7;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        cycles = iterations * 26u;
        if (iterations == remainingIterations)
        {
            _registers.Pc = (pc + 8) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            return true;
        }

        cycles = Math.Max(26u, cycles);
        return true;
    }

    private bool TryConsumeNeoGeoVramPortCopyLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;

        // Metal Slug streams sprite/VRAM command words through the NeoGeo VRAM data port:
        // move.w (A2)+,(A4); subq.w #1,D7; bne loop.
        // Keep every bus read/write so the port side effects remain intact; this only
        // removes the repeated interpreter/decode overhead around the tight loop.
        if (_registers.Prefetch != 0x389A
            || _bus.ReadWord(pc + 2) != 0x5347
            || _bus.ReadWord(pc + 4) != 0x66FA
            || _registers.Address[4] != 0x003C0002)
        {
            return false;
        }

        uint a2 = _registers.Address[2];
        if ((a2 & 1) != 0 || a2 < 0x00100000 || a2 >= 0x00110000)
            return false;

        ushort d7 = (ushort)_registers.Data[7];
        uint remainingIterations = d7 == 0 ? 0x1_0000u : d7;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 26u);
        uint wordsUntilRamEnd = (0x00110000 - a2) / 2;
        uint iterations = Math.Min(Math.Min(remainingIterations, maxIterations), wordsUntilRamEnd);
        if (iterations == 0)
            return false;

        const uint destination = 0x003C0002;
        for (uint i = 0; i < iterations; i++)
        {
            ushort value = _bus.ReadWord(a2);
            _bus.WriteWord(destination, value);
            a2 += 2;
        }

        ushort d7BeforeLastSub = (ushort)(d7 - iterations + 1u);
        ushort newD7 = (ushort)(d7 - iterations);
        var (_, carry, overflow) = SubWords(d7BeforeLastSub, 1, false);

        _registers.Address[2] = a2;
        _registers.Data[7] = (_registers.Data[7] & 0xffff_0000) | newD7;

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = newD7 == 0;
        _registers.Ccr.Negative = newD7.SignBit();
        _registers.Ccr.Extend = carry;

        cycles = iterations * 26u;
        if (iterations == remainingIterations)
        {
            cycles -= 2;
            _registers.Pc = (pc + 6) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            return true;
        }

        cycles = Math.Max(26u, cycles);
        return true;
    }

    private bool TryConsumeNeoGeoVramPortFillLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;

        // BIOS/game helper delay-fills the NeoGeo VRAM data port:
        // move.w D0,(A0); dbf D1,loop.
        if (_registers.Prefetch != 0x3080
            || _bus.ReadWord(pc + 2) != 0x51C9
            || _bus.ReadWord(pc + 4) != 0xFFFC
            || _registers.Address[0] != 0x003C0002)
        {
            return false;
        }

        ushort d1 = (ushort)_registers.Data[1];
        uint remainingIterations = (uint)d1 + 1u;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 18u);
        uint iterations = Math.Min(remainingIterations, maxIterations);
        if (iterations == 0)
            return false;

        ushort value = (ushort)_registers.Data[0];
        for (uint i = 0; i < iterations; i++)
            _bus.WriteWord(0x003C0002, value);

        ushort newD1 = (ushort)(d1 - iterations);
        _registers.Data[1] = (_registers.Data[1] & 0xffff_0000) | newD1;
        SetMoveWordFlags(value);

        cycles = iterations * 18u;
        if (iterations == remainingIterations)
        {
            cycles += 4;
            _registers.Pc = (pc + 6) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            return true;
        }

        cycles = Math.Max(18u, cycles);
        return true;
    }

    private bool TryConsumeNeoGeoWatchdogVramPortFillLoop(int cycleBudget, out uint cycles)
    {
        cycles = 0;
        uint pc = _registers.Pc;

        // Same VRAM port fill, with the BIOS watchdog poke in the loop body:
        // move.b D0,$300001; move.w D0,(A1); dbf D7,loop.
        if (_registers.Prefetch != 0x13C0
            || _bus.ReadWord(pc + 2) != 0x0030
            || _bus.ReadWord(pc + 4) != 0x0001
            || _bus.ReadWord(pc + 6) != 0x3280
            || _bus.ReadWord(pc + 8) != 0x51CF
            || _bus.ReadWord(pc + 10) != 0xFFF6
            || _registers.Address[1] != 0x003C0002)
        {
            return false;
        }

        ushort d7 = (ushort)_registers.Data[7];
        uint remainingIterations = (uint)d7 + 1u;
        uint maxIterations = Math.Max(1u, (uint)cycleBudget / 38u);
        uint iterations = Math.Min(remainingIterations, maxIterations);
        if (iterations == 0)
            return false;

        byte watchdogValue = (byte)_registers.Data[0];
        ushort value = (ushort)_registers.Data[0];
        for (uint i = 0; i < iterations; i++)
        {
            _bus.WriteByte(0x00300001, watchdogValue);
            _bus.WriteWord(0x003C0002, value);
        }

        ushort newD7 = (ushort)(d7 - iterations);
        _registers.Data[7] = (_registers.Data[7] & 0xffff_0000) | newD7;
        SetMoveWordFlags(value);

        cycles = iterations * 38u;
        if (iterations == remainingIterations)
        {
            cycles += 4;
            _registers.Pc = (pc + 12) & 0x00ff_ffff;
            _registers.Prefetch = _bus.ReadWord(_registers.Pc);
            return true;
        }

        cycles = Math.Max(38u, cycles);
        return true;
    }

    private void SetMoveWordFlags(ushort value)
    {
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
    }

    private ExecuteResult<uint> DoExecute()
    {
        uint pcBefore = _registers.Pc;
        _tracePc = pcBefore;
        _opcode = _registers.Prefetch;
        _instruction = InstructionTable.Decode(_opcode);
        _traceThisInstruction = TracePcInRange(pcBefore) && _tracePcRemaining > 0;

        if (_instruction.Value.Kind == InstructionKind.Illegal)
            return ExecuteResult<uint>.Err(M68kException.IllegalInstruction(_opcode));

        if (ProfileOpcodes)
            AddOpcodeProfile(pcBefore, _opcode, _instruction.Value);

        if (_traceThisInstruction)
        {
            _tracePcRemaining--;
            Instruction traceInst = _instruction.Value;
            EmitPcTraceLine(
                $"[M68K-PC] cpu={_name} pc=0x{pcBefore:X8} op=0x{_opcode:X4} inst={traceInst.Kind} size={traceInst.Size} " +
                $"src={FormatMode(traceInst.Source)} dst={FormatMode(traceInst.Dest)} " +
                $"A0=0x{_registers.Address[0]:X8} A1=0x{_registers.Address[1]:X8} A2=0x{_registers.Address[2]:X8} A4=0x{_registers.Address[4]:X8} A5=0x{_registers.Address[5]:X8} " +
                $"D0=0x{_registers.Data[0]:X8} D1=0x{_registers.Data[1]:X8} D2=0x{_registers.Data[2]:X8} D3=0x{_registers.Data[3]:X8} D4=0x{_registers.Data[4]:X8} D5=0x{_registers.Data[5]:X8} D6=0x{_registers.Data[6]:X8} D7=0x{_registers.Data[7]:X8}");
        }

        var next = ReadBusWord(_registers.Pc + 2);
        if (!next.IsOk)
            return ExecuteResult<uint>.Err(next.Error!.Value);
        _registers.Prefetch = next.Value;
        _registers.Pc = (_registers.Pc + 2) & 0x00FF_FFFF;

        Instruction inst = _instruction.Value;
        return inst.Kind switch
        {
            InstructionKind.Add => inst.Size switch
            {
                OpSize.Byte => AddByte(inst.Source, inst.Dest, inst.WithExtend),
                OpSize.Word => AddWord(inst.Source, inst.Dest, inst.WithExtend),
                _ => AddLongWord(inst.Source, inst.Dest, inst.WithExtend),
            },
            InstructionKind.AddDecimal => Abcd(inst.Source, inst.Dest),
            InstructionKind.And => inst.Size switch
            {
                OpSize.Byte => AndByte(inst.Source, inst.Dest),
                OpSize.Word => AndWord(inst.Source, inst.Dest),
                _ => AndLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.AndToCcr => AndiToCcr(),
            InstructionKind.AndToSr => AndiToSr(),
            InstructionKind.ArithmeticShiftMemory => AsdMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.ArithmeticShiftRegister => AsdRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.BitTest => Btst(inst.Source, inst.Dest),
            InstructionKind.BitTestAndChange => Bchg(inst.Source, inst.Dest),
            InstructionKind.BitTestAndClear => Bclr(inst.Source, inst.Dest),
            InstructionKind.BitTestAndSet => Bset(inst.Source, inst.Dest),
            InstructionKind.Branch => Branch(inst.BranchCondition, inst.Displacement8),
            InstructionKind.BranchDecrement => Dbcc(inst.BranchCondition, inst.DataReg),
            InstructionKind.BranchToSubroutine => Bsr(inst.Displacement8),
            InstructionKind.CheckRegister => Chk(inst.DataReg, inst.Source),
            InstructionKind.Clear => inst.Size switch
            {
                OpSize.Byte => ClrByte(inst.Dest),
                OpSize.Word => ClrWord(inst.Dest),
                _ => ClrLongWord(inst.Dest),
            },
            InstructionKind.Compare => inst.Size switch
            {
                OpSize.Byte => CmpByte(inst.Source, inst.Dest),
                OpSize.Word => CmpWord(inst.Source, inst.Dest),
                _ => CmpLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.DivideSigned => Divs(inst.DataReg, inst.Source),
            InstructionKind.DivideUnsigned => Divu(inst.DataReg, inst.Source),
            InstructionKind.ExchangeAddress => ExecuteResult<uint>.Ok(ExgAddress(inst.AddrReg, inst.Dest.AddrReg)),
            InstructionKind.ExchangeData => ExecuteResult<uint>.Ok(ExgData(inst.DataReg, inst.Dest.DataReg)),
            InstructionKind.ExchangeDataAddress => ExecuteResult<uint>.Ok(ExgDataAddress(inst.DataReg, inst.AddrReg)),
            InstructionKind.ExclusiveOr => inst.Size switch
            {
                OpSize.Byte => EorByte(inst.Source, inst.Dest),
                OpSize.Word => EorWord(inst.Source, inst.Dest),
                _ => EorLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.ExclusiveOrToCcr => EoriToCcr(),
            InstructionKind.ExclusiveOrToSr => EoriToSr(),
            InstructionKind.Extend => ExecuteResult<uint>.Ok(Ext(inst.Size, inst.DataReg)),
            InstructionKind.Jump => Jmp(inst.Dest),
            InstructionKind.JumpToSubroutine => Jsr(inst.Dest),
            InstructionKind.Link => Link(inst.AddrReg),
            InstructionKind.LoadEffectiveAddress => Lea(inst.Source, inst.AddrReg),
            InstructionKind.LogicalShiftMemory => LsdMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.LogicalShiftRegister => LsdRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.Move => inst.Size switch
            {
                OpSize.Byte => MoveByte(inst.Source, inst.Dest),
                OpSize.Word => MoveWord(inst.Source, inst.Dest),
                _ => MoveLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.MoveFromSr => MoveFromSr(inst.Dest),
            InstructionKind.MoveMultiple => Movem(inst.Size, inst.Dest, inst.Direction),
            InstructionKind.MovePeripheral => Movep(inst.Size, inst.DataReg, inst.AddrReg, inst.Direction),
            InstructionKind.MoveQuick => ExecuteResult<uint>.Ok(Moveq(unchecked((sbyte)inst.QuickValue), inst.DataReg)),
            InstructionKind.MoveToCcr => MoveToCcr(inst.Source),
            InstructionKind.MoveToSr => MoveToSr(inst.Source),
            InstructionKind.MoveUsp => MoveUsp(inst.UspDirection, inst.AddrReg),
            InstructionKind.MultiplySigned => Muls(inst.DataReg, inst.Source),
            InstructionKind.MultiplyUnsigned => Mulu(inst.DataReg, inst.Source),
            InstructionKind.Negate => inst.Size switch
            {
                OpSize.Byte => NegByte(inst.Dest, inst.WithExtend),
                OpSize.Word => NegWord(inst.Dest, inst.WithExtend),
                _ => NegLongWord(inst.Dest, inst.WithExtend),
            },
            InstructionKind.NegateDecimal => Nbcd(inst.Dest),
            InstructionKind.NoOp => ExecuteResult<uint>.Ok(Nop()),
            InstructionKind.Not => inst.Size switch
            {
                OpSize.Byte => NotByte(inst.Dest),
                OpSize.Word => NotWord(inst.Dest),
                _ => NotLongWord(inst.Dest),
            },
            InstructionKind.Or => inst.Size switch
            {
                OpSize.Byte => OrByte(inst.Source, inst.Dest),
                OpSize.Word => OrWord(inst.Source, inst.Dest),
                _ => OrLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.OrToCcr => OriToCcr(),
            InstructionKind.OrToSr => OriToSr(),
            InstructionKind.PushEffectiveAddress => Pea(inst.Source),
            InstructionKind.Reset => ExecuteResult<uint>.Ok(ResetInstruction()),
            InstructionKind.Return => Ret(inst.RestoreCcr),
            InstructionKind.ReturnFromException => Rte(),
            InstructionKind.RotateMemory => RodMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.RotateRegister => RodRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.RotateThruExtendMemory => RoxdMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.RotateThruExtendRegister => RoxdRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.Set => Scc(inst.BranchCondition, inst.Dest),
            InstructionKind.Subtract => inst.Size switch
            {
                OpSize.Byte => SubByte(inst.Source, inst.Dest, inst.WithExtend),
                OpSize.Word => SubWord(inst.Source, inst.Dest, inst.WithExtend),
                _ => SubLongWord(inst.Source, inst.Dest, inst.WithExtend),
            },
            InstructionKind.SubtractDecimal => Sbcd(inst.Source, inst.Dest),
            InstructionKind.Swap => ExecuteResult<uint>.Ok(Swap(inst.DataReg)),
            InstructionKind.Stop => Stop(),
            InstructionKind.Test => inst.Size switch
            {
                OpSize.Byte => TstByte(inst.Source),
                OpSize.Word => TstWord(inst.Source),
                _ => TstLongWord(inst.Source),
            },
            InstructionKind.TestAndSet => Tas(inst.Dest),
            InstructionKind.Trap => Trap(inst.TrapVector),
            InstructionKind.TrapOnOverflow => Trapv(),
            InstructionKind.Unlink => Unlk(inst.AddrReg),
            _ => ExecuteResult<uint>.Err(M68kException.IllegalInstruction(_opcode)),
        };
    }

    private static void AddOpcodeProfile(uint pc, ushort opcode, Instruction instruction)
    {
        ProfileOpcodeCounts[opcode]++;
        ProfileKindCounts[(int)instruction.Kind]++;
        ulong pcOpcode = ((ulong)(pc & 0x00ff_ffff) << 16) | opcode;
        ProfilePcOpcodeCounts.TryGetValue(pcOpcode, out long pcOpcodeCount);
        ProfilePcOpcodeCounts[pcOpcode] = pcOpcodeCount + 1;
        long total = ++_profileTotalInstructions;
        if ((total & 0xffff) != 0)
            return;

        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _profileWindowStartTicks;
        if (elapsed < Stopwatch.Frequency)
            return;

        double seconds = elapsed / (double)Stopwatch.Frequency;
        Console.WriteLine($"[M68K-OP] ips={total / seconds:0} kinds={FormatTopKinds()} opcodes={FormatTopOpcodes()} pcs={FormatTopPcOpcodes()}");
        Array.Clear(ProfileOpcodeCounts, 0, ProfileOpcodeCounts.Length);
        Array.Clear(ProfileKindCounts, 0, ProfileKindCounts.Length);
        ProfilePcOpcodeCounts.Clear();
        _profileTotalInstructions = 0;
        _profileWindowStartTicks = now;
    }

    private static string FormatTopKinds()
    {
        Span<int> top = stackalloc int[8];
        Span<long> counts = stackalloc long[8];
        for (int kind = 0; kind < ProfileKindCounts.Length; kind++)
            InsertTop(top, counts, kind, ProfileKindCounts[kind]);

        return FormatTop(top, counts, kind => ((InstructionKind)kind).ToString());
    }

    private static string FormatTopOpcodes()
    {
        Span<int> top = stackalloc int[12];
        Span<long> counts = stackalloc long[12];
        for (int opcode = 0; opcode < ProfileOpcodeCounts.Length; opcode++)
            InsertTop(top, counts, opcode, ProfileOpcodeCounts[opcode]);

        return FormatTop(top, counts, opcode =>
        {
            Instruction instruction = InstructionTable.Decode((ushort)opcode);
            return $"0x{opcode:X4}/{instruction.Kind}";
        });
    }

    private static string FormatTopPcOpcodes()
    {
        Span<long> counts = stackalloc long[12];
        ulong[] keys = new ulong[12];

        foreach (var pair in ProfilePcOpcodeCounts)
        {
            long count = pair.Value;
            if (count <= 0 || count <= counts[^1])
                continue;

            int index = counts.Length - 1;
            while (index > 0 && count > counts[index - 1])
            {
                counts[index] = counts[index - 1];
                keys[index] = keys[index - 1];
                index--;
            }

            counts[index] = count;
            keys[index] = pair.Key;
        }

        string[] parts = new string[counts.Length];
        int partCount = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] <= 0)
                break;

            uint pc = (uint)(keys[i] >> 16);
            ushort opcode = (ushort)keys[i];
            Instruction instruction = InstructionTable.Decode(opcode);
            parts[partCount++] = $"0x{pc:X6}:0x{opcode:X4}/{instruction.Kind}:{counts[i]}";
        }

        return partCount == 0 ? "-" : string.Join(",", parts, 0, partCount);
    }

    private static void InsertTop(Span<int> top, Span<long> counts, int key, long count)
    {
        if (count <= 0 || count <= counts[^1])
            return;

        int index = counts.Length - 1;
        while (index > 0 && count > counts[index - 1])
        {
            counts[index] = counts[index - 1];
            top[index] = top[index - 1];
            index--;
        }

        counts[index] = count;
        top[index] = key;
    }

    private static string FormatTop(Span<int> top, Span<long> counts, Func<int, string> nameForKey)
    {
        string[] parts = new string[counts.Length];
        int partCount = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] <= 0)
                break;
            parts[partCount++] = $"{nameForKey(top[i])}:{counts[i]}";
        }

        return partCount == 0 ? "-" : string.Join(",", parts, 0, partCount);
    }

    private ExecuteResult<ushort> ReadBusWord(uint address)
    {
        if ((address & 1) != 0)
            return ExecuteResult<ushort>.Err(M68kException.AddressError(address, BusOpType.Read));
        return ExecuteResult<ushort>.Ok(_bus.ReadWord(address));
    }

    private ExecuteResult<uint> ReadBusLong(uint address)
    {
        if ((address & 1) != 0)
            return ExecuteResult<uint>.Err(M68kException.AddressError(address, BusOpType.Read));
        return ExecuteResult<uint>.Ok(_bus.ReadLong(address));
    }

    private ExecuteResult<object> WriteBusWord(uint address, ushort value)
    {
        if ((address & 1) != 0)
            return ExecuteResult<object>.Err(M68kException.AddressError(address, BusOpType.Write));
        _bus.WriteWord(address, value);
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<object> WriteBusLong(uint address, uint value)
    {
        if ((address & 1) != 0)
            return ExecuteResult<object>.Err(M68kException.AddressError(address, BusOpType.Write));
        _bus.WriteLong(address, value);
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<ushort> FetchOperand()
    {
        ushort operand = _registers.Prefetch;
        var next = ReadBusWord(_registers.Pc + 2);
        if (!next.IsOk) return ExecuteResult<ushort>.Err(next.Error!.Value);
        _registers.Prefetch = next.Value;
        _registers.Pc = (_registers.Pc + 2) & 0x00FF_FFFF;
        return ExecuteResult<ushort>.Ok(operand);
    }

    private ExecuteResult<ResolvedAddress> ResolveAddress(AddressingMode mode, OpSize size)
    {
        ResolvedAddress resolved;
        switch (mode.Kind)
        {
            case AddressingModeKind.DataDirect:
                resolved = ResolvedAddress.DataRegister(mode.DataReg);
                break;
            case AddressingModeKind.AddressDirect:
                resolved = ResolvedAddress.AddressRegister(mode.AddrReg);
                break;
            case AddressingModeKind.AddressIndirect:
                resolved = ResolvedAddress.Memory(mode.AddrReg.Read(_registers));
                break;
            case AddressingModeKind.AddressIndirectPredecrement:
                {
                    uint inc = size.IncrementStepFor(mode.AddrReg);
                    uint addr = mode.AddrReg.Read(_registers) - inc;
                    mode.AddrReg.WriteLong(_registers, addr);
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AddressIndirectPostincrement:
                {
                    uint inc = size.IncrementStepFor(mode.AddrReg);
                    uint addr = mode.AddrReg.Read(_registers);
                    resolved = ResolvedAddress.MemoryPostincrement(addr, mode.AddrReg, inc);
                    break;
                }
            case AddressingModeKind.AddressIndirectDisplacement:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    short disp = (short)ext.Value;
                    uint addr = mode.AddrReg.Read(_registers) + (uint)disp;
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AddressIndirectIndexed:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    var (idxReg, idxSize) = Indexing.ParseIndex(ext.Value);
                    uint baseAddress = mode.AddrReg.Read(_registers);
                    uint index = idxReg.Read(_registers, idxSize);
                    sbyte disp = (sbyte)ext.Value;
                    uint addr = baseAddress + index + (uint)disp;
                    if (size != OpSize.Byte && (addr & 1) != 0)
                        MaybeTraceOddIndexed(mode, ext.Value, idxReg, idxSize, baseAddress, index, disp, addr, size);
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.PcRelativeDisplacement:
                {
                    uint pcBefore = _registers.Pc;
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    short disp = (short)ext.Value;
                    // PC-relative bases on the extension word address (PC before FetchOperand)
                    uint addr = pcBefore + (uint)disp;
                    if (TracePcIndexed && _tracePcIndexedRemaining > 0)
                    {
                        _tracePcIndexedRemaining--;
                        Console.WriteLine(
                            $"[M68K-PCREL] pcBefore=0x{pcBefore:X8} pcAfter=0x{_registers.Pc:X8} ext=0x{ext.Value:X4} disp=0x{disp:X4} addr=0x{addr:X8}");
                    }
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.PcRelativeIndexed:
                {
                    uint pcBefore = _registers.Pc;
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    var (idxReg, idxSize) = Indexing.ParseIndex(ext.Value);
                    uint index = idxReg.Read(_registers, idxSize);
                    sbyte disp = (sbyte)ext.Value;
                    // PC-relative bases on the extension word address (PC before FetchOperand)
                    uint addr = pcBefore + index + (uint)disp;
                    if (TracePcIndexed && _tracePcIndexedRemaining > 0)
                    {
                        _tracePcIndexedRemaining--;
                        string idxKind = idxReg.IsAddress ? "A" : "D";
                        int idxNum = idxReg.IsAddress ? idxReg.AddrReg.Index : idxReg.DataReg.Index;
                        Console.WriteLine(
                            $"[M68K-PCIDX] pcBefore=0x{pcBefore:X8} pcAfter=0x{_registers.Pc:X8} ext=0x{ext.Value:X4} " +
                            $"idx={idxKind}{idxNum} size={(idxSize == IndexSize.LongWord ? "L" : "W")} " +
                            $"index=0x{index:X8} disp=0x{(byte)disp:X2} addr=0x{addr:X8}");
                    }
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AbsoluteShort:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    // Absolute short is sign-extended (e.g. 0xFFB8 -> 0xFFFF_FFB8).
                    uint addr = unchecked((uint)(short)ext.Value);
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AbsoluteLong:
                {
                    var hi = FetchOperand();
                    if (!hi.IsOk) return ExecuteResult<ResolvedAddress>.Err(hi.Error!.Value);
                    var lo = FetchOperand();
                    if (!lo.IsOk) return ExecuteResult<ResolvedAddress>.Err(lo.Error!.Value);
                    uint addr = ((uint)hi.Value << 16) | lo.Value;
                    if (ShouldTracePc(_tracePc))
                        EmitPcTraceLine($"[M68K-ABS] pc=0x{_tracePc:X8} hi=0x{hi.Value:X4} lo=0x{lo.Value:X4} addr=0x{addr:X8}");
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.Immediate:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    if (size == OpSize.Byte)
                        resolved = ResolvedAddress.Immediate((byte)ext.Value);
                    else if (size == OpSize.Word)
                        resolved = ResolvedAddress.Immediate(ext.Value);
                    else
                    {
                        var lo = FetchOperand();
                        if (!lo.IsOk) return ExecuteResult<ResolvedAddress>.Err(lo.Error!.Value);
                        uint value = ((uint)ext.Value << 16) | lo.Value;
                        resolved = ResolvedAddress.Immediate(value);
                    }
                    break;
                }
            case AddressingModeKind.Quick:
                resolved = ResolvedAddress.Immediate(mode.QuickValue);
                break;
            default:
                resolved = ResolvedAddress.Immediate(0);
                break;
        }

        return ExecuteResult<ResolvedAddress>.Ok(resolved);
    }

    private ExecuteResult<ResolvedAddress> ResolveAddressWithPost(AddressingMode mode, OpSize size)
    {
        var resolved = ResolveAddress(mode, size);
        if (!resolved.IsOk) return resolved;
        resolved.Value.ApplyPost(_registers);
        return resolved;
    }

    private ExecuteResult<ushort> ReadWordResolved(ResolvedAddress resolved)
    {
        return resolved.Kind switch
        {
            ResolvedAddressKind.DataRegister => ExecuteResult<ushort>.Ok((ushort)resolved.DataReg.Read(_registers)),
            ResolvedAddressKind.AddressRegister => ExecuteResult<ushort>.Ok((ushort)resolved.AddrReg.Read(_registers)),
            ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement => ReadBusWord(resolved.Address),
            ResolvedAddressKind.Immediate => ExecuteResult<ushort>.Ok((ushort)resolved.ImmediateValue),
            _ => ExecuteResult<ushort>.Ok(0)
        };
    }

    private ExecuteResult<uint> ReadLongResolved(ResolvedAddress resolved)
    {
        var result = resolved.Kind switch
        {
            ResolvedAddressKind.DataRegister => ExecuteResult<uint>.Ok(resolved.DataReg.Read(_registers)),
            ResolvedAddressKind.AddressRegister => ExecuteResult<uint>.Ok(resolved.AddrReg.Read(_registers)),
            ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement => ReadBusLong(resolved.Address),
            ResolvedAddressKind.Immediate => ExecuteResult<uint>.Ok(resolved.ImmediateValue),
            _ => ExecuteResult<uint>.Ok(0)
        };
        if (ShouldTracePc(_tracePc) && result.IsOk && resolved.Kind is ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement)
            EmitPcTraceLine($"[M68K-RL] pc=0x{_tracePc:X8} addr=0x{resolved.Address:X8} value=0x{result.Value:X8}");
        return result;
    }

    private ExecuteResult<object> WriteWordResolved(ResolvedAddress resolved, ushort value)
    {
        switch (resolved.Kind)
        {
            case ResolvedAddressKind.DataRegister:
                resolved.DataReg.WriteWord(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.AddressRegister:
                resolved.AddrReg.WriteWord(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.Memory:
            case ResolvedAddressKind.MemoryPostincrement:
                if (TraceWriteAddress.HasValue
                    && (resolved.Address == TraceWriteAddress.Value
                        || (resolved.Address + 1) == TraceWriteAddress.Value))
                {
                    string instKind = _instruction?.Kind.ToString() ?? "?";
                    string line =
                        $"[M68K-WW] cpu={_name} tracePc=0x{_tracePc:X8} curPc=0x{_registers.Pc:X8} op=0x{_opcode:X4} inst={instKind} " +
                        $"addr=0x{resolved.Address:X8} val=0x{value:X4}";
                    Console.WriteLine(line);
                    AppendTraceLine(TraceWriteFile, line);
                }
                return WriteBusWord(resolved.Address, value);
            default:
                throw new InvalidOperationException("Cannot write to immediate addressing mode.");
        }
    }

    private ExecuteResult<object> WriteLongResolved(ResolvedAddress resolved, uint value)
    {
        switch (resolved.Kind)
        {
            case ResolvedAddressKind.DataRegister:
                resolved.DataReg.WriteLong(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.AddressRegister:
                resolved.AddrReg.WriteLong(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.Memory:
            case ResolvedAddressKind.MemoryPostincrement:
                return WriteBusLong(resolved.Address, value);
            default:
                throw new InvalidOperationException("Cannot write to immediate addressing mode.");
        }
    }

    private ExecuteResult<object> PushStackU16(ushort value)
    {
        uint sp = _registers.StackPointer() - 2;
        _registers.SetStackPointer(sp);
        return WriteBusWord(sp, value);
    }

    private ExecuteResult<object> PushStackU32(uint value)
    {
        ushort hi = (ushort)(value >> 16);
        ushort lo = (ushort)(value & 0xFFFF);
        uint sp = _registers.StackPointer() - 4;
        _registers.SetStackPointer(sp);
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_STACK") == "1")
        {
            if ((value & 0x00FF_FFFF) < 0x000100 || (value & 0x00FF_FFFF) >= 0x00FF0000)
            {
                Console.WriteLine($"[M68K-STK] cpu={_name} push value=0x{value:X8} sp=0x{sp:X8}");
            }
        }
        var r0 = WriteBusWord(sp, hi);
        if (!r0.IsOk) return r0;
        return WriteBusWord(sp + 2, lo);
    }

    private ExecuteResult<ushort> PopStackU16()
    {
        uint sp = _registers.StackPointer();
        var value = ReadBusWord(sp);
        if (!value.IsOk) return ExecuteResult<ushort>.Err(value.Error!.Value);
        _registers.SetStackPointer(sp + 2);
        return ExecuteResult<ushort>.Ok(value.Value);
    }

    private ExecuteResult<uint> PopStackU32()
    {
        uint sp = _registers.StackPointer();
        var value = ReadBusLong(sp);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        _registers.SetStackPointer(sp + 4);
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_STACK") == "1")
        {
            if ((value.Value & 0x00FF_FFFF) < 0x000100 || (value.Value & 0x00FF_FFFF) >= 0x00FF0000)
            {
                Console.WriteLine($"[M68K-STK] cpu={_name} pop value=0x{value.Value:X8} sp=0x{sp:X8}");
            }
        }
        return ExecuteResult<uint>.Ok(value.Value);
    }

    private ExecuteResult<object> JumpToAddress(uint address)
    {
        uint masked = address & 0x00FF_FFFF;
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_JUMP") == "1")
        {
            if (masked < 0x000100 || masked >= 0x00FF0000 || (address & 0xFF00_0000) != 0)
            {
                Console.WriteLine($"[M68K-JUMP] cpu={_name} from=0x{_registers.Pc:X8} addr=0x{address:X8} masked=0x{masked:X8}");
            }
        }
        _registers.Pc = (masked - 2) & 0x00FF_FFFF;
        if ((address & 1) != 0)
            return ExecuteResult<object>.Err(M68kException.AddressError(address, BusOpType.Jump));
        var _ = FetchOperand();
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<uint> HandleAutoVectoredInterrupt(byte interruptLevel)
    {
        ushort sr = _registers.StatusRegister();
        _registers.TraceEnabled = false;
        _registers.SupervisorMode = true;
        _registers.InterruptPriorityMask = interruptLevel;

        var r0 = PushStackU32(_registers.Pc);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
        var r1 = PushStackU16(sr);
        if (!r1.IsOk) return ExecuteResult<uint>.Err(r1.Error!.Value);

        uint vectorAddr = AutoVectoredInterruptBase + 4u * interruptLevel;
        uint newPc = _bus.ReadLong(vectorAddr);
        if (TraceInterrupts && _traceInterruptRemaining > 0)
        {
            string line =
                $"[M68K-IRQVEC] cpu={_name} level={interruptLevel} vector=0x{vectorAddr:X4} target=0x{newPc:X8}";
            Console.WriteLine(line);
            AppendTraceLine(TraceInterruptsFile, line);
        }
        var r2 = JumpToAddress(newPc);
        if (!r2.IsOk) return ExecuteResult<uint>.Err(r2.Error!.Value);

        return ExecuteResult<uint>.Ok(44);
    }

    private uint HandleException(M68kException ex)
    {
        if (TraceExceptions)
        {
            string detail = ex.Kind == M68kExceptionKind.AddressError
                ? $" addr=0x{ex.Address:X8} op={ex.BusOp} A0=0x{_registers.Address[0]:X8} SP=0x{_registers.StackPointer():X8}"
                : string.Empty;
            string instKind = _instruction.HasValue ? _instruction.Value.Kind.ToString() : "unknown";
            string modeInfo = string.Empty;
            if (_instruction.HasValue)
            {
                Instruction inst = _instruction.Value;
                modeInfo = $" size={inst.Size} src={FormatMode(inst.Source)} dst={FormatMode(inst.Dest)}";
            }
            string line = $"[M68K-EX] cpu={_name} kind={ex.Kind} pc=0x{_registers.Pc:X8} op=0x{_opcode:X4} inst={instKind}{modeInfo}{detail}";
            Console.WriteLine(line);
            AppendTraceLine(TraceExceptionsFile, line);
        }
        switch (ex.Kind)
        {
            case M68kExceptionKind.AddressError:
                _registers.AddressError = true;
                if (!HandleAddressError(ex.Address, ex.BusOp).IsOk)
                {
                    _registers.Frozen = true;
                }
                return 50;
            case M68kExceptionKind.PrivilegeViolation:
                HandleTrap(PrivilegeViolationVector, _registers.Pc - 2);
                return 34;
            case M68kExceptionKind.IllegalInstruction:
                uint vector = (_opcode >> 12) switch
                {
                    0b1010 => Line1010Vector,
                    0b1111 => Line1111Vector,
                    _ => IllegalOpcodeVector
                };
                HandleTrap(vector, _registers.Pc - 2);
                return 34;
            case M68kExceptionKind.DivisionByZero:
                HandleTrap(DivideByZeroVector, _registers.Pc);
                return 38 + ex.Cycles;
            case M68kExceptionKind.Trap:
                HandleTrap(ex.Vector, _registers.Pc);
                return 34;
            case M68kExceptionKind.CheckRegister:
                HandleTrap(CheckRegisterVector, _registers.Pc);
                return 30 + ex.Cycles;
            default:
                return 50;
        }
    }

    private static string FormatMode(AddressingMode mode)
    {
        return mode.Kind switch
        {
            AddressingModeKind.DataDirect => $"D{mode.DataReg.Index}",
            AddressingModeKind.AddressDirect => $"A{mode.AddrReg.Index}",
            AddressingModeKind.AddressIndirect => $"(A{mode.AddrReg.Index})",
            AddressingModeKind.AddressIndirectPostincrement => $"(A{mode.AddrReg.Index})+",
            AddressingModeKind.AddressIndirectPredecrement => $"- (A{mode.AddrReg.Index})",
            AddressingModeKind.AddressIndirectDisplacement => $"(d16,A{mode.AddrReg.Index})",
            AddressingModeKind.AddressIndirectIndexed => $"(d8,An,Xn)A{mode.AddrReg.Index}",
            AddressingModeKind.PcRelativeDisplacement => "(d16,PC)",
            AddressingModeKind.PcRelativeIndexed => "(d8,PC,Xn)",
            AddressingModeKind.AbsoluteShort => "(abs.w)",
            AddressingModeKind.AbsoluteLong => "(abs.l)",
            AddressingModeKind.Immediate => "#imm",
            AddressingModeKind.Quick => "#q",
            _ => mode.Kind.ToString()
        };
    }

    private static void AppendTraceLine(string? path, string line)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Ignore trace write failures to avoid altering emulation behavior.
        }
    }

    private void EmitPcTraceLine(string line)
    {
        if (string.IsNullOrWhiteSpace(TracePcFile))
            Console.WriteLine(line);
        AppendTraceLine(TracePcFile, line);
    }

    private bool ShouldTracePc(uint pc)
    {
        return _traceThisInstruction && pc == _tracePc;
    }

    private static bool TracePcInRange(uint pc)
    {
        if (!TracePcMin.HasValue || !TracePcMax.HasValue)
            return false;
        return pc >= TracePcMin.Value && pc <= TracePcMax.Value;
    }

    private static uint? ReadHexEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];
        if (uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value))
            return value;
        return null;
    }

    private static int ParseTraceLimit(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        raw = raw.Trim();
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
            return fallback;
        if (value <= 0)
            return int.MaxValue;
        return value;
    }

    private void MaybeTraceOddIndexed(
        AddressingMode mode,
        ushort extension,
        IndexRegister idxReg,
        IndexSize idxSize,
        uint baseAddress,
        uint index,
        sbyte displacement,
        uint address,
        OpSize size)
    {
        if (!TraceOddIndexed || _traceOddIndexedRemaining <= 0)
            return;

        _traceOddIndexedRemaining--;
        string idxKind = idxReg.IsAddress ? "A" : "D";
        int idxNum = idxReg.IsAddress ? idxReg.AddrReg.Index : idxReg.DataReg.Index;
        uint rawIndex = idxReg.IsAddress ? idxReg.AddrReg.Read(_registers) : idxReg.DataReg.Read(_registers);
        string instKind = _instruction.HasValue ? _instruction.Value.Kind.ToString() : "?";
        string line =
            $"[M68K-ODDIDX] cpu={_name} pc=0x{_tracePc:X8} curPc=0x{_registers.Pc:X8} op=0x{_opcode:X4} inst={instKind} size={size} " +
            $"base=A{mode.AddrReg.Index}=0x{baseAddress:X8} ext=0x{extension:X4} " +
            $"idx={idxKind}{idxNum}.{(idxSize == IndexSize.LongWord ? "L" : "W")} raw=0x{rawIndex:X8} value=0x{index:X8} " +
            $"disp=0x{(byte)displacement:X2} addr=0x{address:X8} " +
            $"A0=0x{_registers.Address[0]:X8} A1=0x{_registers.Address[1]:X8} A2=0x{_registers.Address[2]:X8} A3=0x{_registers.Address[3]:X8} " +
            $"D0=0x{_registers.Data[0]:X8} D1=0x{_registers.Data[1]:X8} D2=0x{_registers.Data[2]:X8} D3=0x{_registers.Data[3]:X8}";
        Console.WriteLine(line);
        AppendTraceLine(TraceOddIndexedFile, line);
    }

    private void MaybeTraceInterrupt(string phase, byte busLevel, ushort sr, byte mask, byte? pending)
    {
        if (!string.IsNullOrWhiteSpace(TraceInterruptCpu)
            && !string.Equals(_name, TraceInterruptCpu, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TraceInterrupts || _traceInterruptRemaining <= 0)
            return;
        _traceInterruptRemaining--;
        string pendingText = pending.HasValue ? pending.Value.ToString() : "-";
        string line =
            $"[M68K-IRQ] cpu={_name} phase={phase} pc=0x{_registers.Pc:X8} sr=0x{sr:X4} mask={mask} bus={busLevel} pending={pendingText}";
        Console.WriteLine(line);
        AppendTraceLine(TraceInterruptsFile, line);
    }

    private ExecuteResult<object> HandleTrap(uint vector, uint pc)
    {
        ushort sr = _registers.StatusRegister();
        _registers.TraceEnabled = false;
        _registers.SupervisorMode = true;
        var r0 = PushStackU32(pc);
        if (!r0.IsOk) return r0;
        var r1 = PushStackU16(sr);
        if (!r1.IsOk) return r1;
        uint newPc = _bus.ReadLong(vector * 4);
        return JumpToAddress(newPc);
    }

    private ExecuteResult<object> HandleAddressError(uint address, BusOpType opType)
    {
        ushort sr = _registers.StatusRegister();
        bool supervisorMode = _registers.SupervisorMode;

        _registers.TraceEnabled = false;
        _registers.SupervisorMode = true;

        AddressingMode? dest = _instruction.HasValue ? _instruction.Value.DestAddressingMode() : null;
        AddressingMode? source = _instruction.HasValue ? _instruction.Value.SourceAddressingMode() : null;

        uint pc;
        if (opType == BusOpType.Write
            && dest.HasValue
            && dest.Value.Kind == AddressingModeKind.AddressIndirectPredecrement)
        {
            pc = _registers.Pc;
        }
        else if (opType == BusOpType.Write
                 && dest.HasValue
                 && dest.Value.Kind == AddressingModeKind.AbsoluteLong
                 && source.HasValue)
        {
            var srcKind = source.Value.Kind;
            if (srcKind == AddressingModeKind.AddressIndirect
                || srcKind == AddressingModeKind.AddressIndirectPostincrement
                || srcKind == AddressingModeKind.AddressIndirectPredecrement
                || srcKind == AddressingModeKind.AddressIndirectDisplacement
                || srcKind == AddressingModeKind.AddressIndirectIndexed
                || srcKind == AddressingModeKind.PcRelativeDisplacement
                || srcKind == AddressingModeKind.PcRelativeIndexed
                || srcKind == AddressingModeKind.AbsoluteShort
                || srcKind == AddressingModeKind.AbsoluteLong)
            {
                pc = _registers.Pc - 4;
            }
            else
            {
                pc = _registers.Pc - 2;
            }
        }
        else
        {
            pc = _registers.Pc - 2;
        }

        var r0 = PushStackU32(pc);
        if (!r0.IsOk) return r0;
        var r1 = PushStackU16(sr);
        if (!r1.IsOk) return r1;
        var r2 = PushStackU16(_opcode);
        if (!r2.IsOk) return r2;
        var r3 = PushStackU32(address);
        if (!r3.IsOk) return r3;

        bool rwBit = (opType == BusOpType.Read || opType == BusOpType.Jump)
            ^ (_instruction.HasValue && _instruction.Value.Kind == InstructionKind.MoveFromSr);
        ushort statusCode = opType == BusOpType.Jump ? (ushort)(supervisorMode ? 0x0E : 0x0A) : (ushort)0x05;
        ushort statusWord = (ushort)((_opcode & 0xFFE0) | ((rwBit ? 1 : 0) << 4) | statusCode);
        var r4 = PushStackU16(statusWord);
        if (!r4.IsOk) return r4;

        uint vector = _bus.ReadLong(AddressErrorVector * 4);
        return JumpToAddress(vector);
    }
}

internal readonly struct ResolvedAddress
{
    public readonly ResolvedAddressKind Kind;
    public readonly DataRegister DataReg;
    public readonly AddressRegister AddrReg;
    public readonly uint Address;
    public readonly uint ImmediateValue;
    public readonly uint PostIncrement;

    private ResolvedAddress(
        ResolvedAddressKind kind,
        DataRegister dataReg,
        AddressRegister addrReg,
        uint address,
        uint immediateValue,
        uint postIncrement)
    {
        Kind = kind;
        DataReg = dataReg;
        AddrReg = addrReg;
        Address = address;
        ImmediateValue = immediateValue;
        PostIncrement = postIncrement;
    }

    public static ResolvedAddress DataRegister(DataRegister reg) =>
        new(ResolvedAddressKind.DataRegister, reg, default, 0, 0, 0);

    public static ResolvedAddress AddressRegister(AddressRegister reg) =>
        new(ResolvedAddressKind.AddressRegister, default, reg, 0, 0, 0);

    public static ResolvedAddress Memory(uint address) =>
        new(ResolvedAddressKind.Memory, default, default, address, 0, 0);

    public static ResolvedAddress MemoryPostincrement(uint address, AddressRegister reg, uint increment) =>
        new(ResolvedAddressKind.MemoryPostincrement, default, reg, address, 0, increment);

    public static ResolvedAddress Immediate(uint value) =>
        new(ResolvedAddressKind.Immediate, default, default, 0, value, 0);

    public void ApplyPost(Registers regs)
    {
        if (Kind == ResolvedAddressKind.MemoryPostincrement)
        {
            AddrReg.WriteLong(regs, Address + PostIncrement);
        }
    }
}

internal enum ResolvedAddressKind
{
    DataRegister,
    AddressRegister,
    Memory,
    MemoryPostincrement,
    Immediate,
}
