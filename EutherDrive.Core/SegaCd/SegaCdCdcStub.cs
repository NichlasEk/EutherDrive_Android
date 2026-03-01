using System;
using System.Diagnostics;
using System.Globalization;

namespace EutherDrive.Core.SegaCd;

public enum SegaCdDeviceDestination
{
    None0 = 0,
    None1 = 1,
    MainCpuRegister = 2,
    SubCpuRegister = 3,
    Pcm = 4,
    PrgRam = 5,
    None6 = 6,
    WordRam = 7
}

public sealed class SegaCdCdcStub
{
    private const int BufferRamLen = 16 * 1024;
    private const ushort BufferRamAddressMask = (1 << 14) - 1;
    // Match jgenesis/LC8951 behavior: decoded block pointer skips 12-byte sync
    // and starts at sector header bytes 12..15.
    private const ushort DataTrackHeaderLen = 12;
    private const int Divider75Hz = 44100 / 75;
    private const int DecoderInterruptClearCycle = Divider75Hz * 4 / 10;

    private readonly byte[] _bufferRam = new byte[BufferRamLen];
    private SegaCdDeviceDestination _destination = SegaCdDeviceDestination.None0;
    private byte _destinationBits;
    private ushort? _hostDataBuffer;
    private byte _registerAddress;
    private uint _dmaAddress;

    private bool _decoderEnabled;
    private bool _decoderWritesEnabled;
    private bool _decodedFirstWrittenBlock;
    private bool _decodedLast75HzCycle;
    private uint _cycles44100SinceDecode;
    private bool _dataOutEnabled;
    private bool _dataTransferInProgress;
    private bool _endOfDataTransfer = true;
    private bool _subheaderDataEnabled;

    private readonly byte[] _headerData = new byte[4];
    private readonly byte[] _subheaderData = new byte[4];
    private ushort _writeAddress;
    private ushort _blockPointer;
    private ushort _dataByteCounter;
    private ushort _dataAddressCounter;

    private bool _transferEndInterruptEnabled = true;
    private bool _transferEndInterruptPending;
    private bool _decoderInterruptEnabled = true;
    private bool _decoderInterruptPending;
    private bool _scdInterruptFlag;
    private bool _decoderInterruptLogged;
    private bool _destLogged;
    private bool _dbcLogged;
    private static readonly bool LogCdc = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_CDC"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceCdcTimeline = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CDC_TIMELINE"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceCdcDecode = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CDC_DECODE"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool CompatCdcForceWrrq = ReadCompatFlag("EUTHERDRIVE_SCD_CDC_COMPAT_FORCE_WRRQ", defaultValue: false);
    // Keep this enabled by default: several BIOS flows set DOUTEN before explicitly
    // writing destination bits, and otherwise stall with DEST=None0.
    private static readonly bool CompatCdcAutoDest = ReadCompatFlag("EUTHERDRIVE_SCD_CDC_COMPAT_AUTO_DEST", defaultValue: true);
    private static readonly bool CompatCdcAutoXfer = ReadCompatFlag("EUTHERDRIVE_SCD_CDC_COMPAT_AUTO_XFER", defaultValue: false);
    private static readonly long TraceStartTicks = Stopwatch.GetTimestamp();

    public bool EndOfDataTransfer => _endOfDataTransfer;
    public bool DataReady => _dataTransferInProgress;
    public SegaCdDeviceDestination DeviceDestination => _destination;
    public byte DeviceDestinationBits => _destinationBits;
    public byte RegisterAddress => _registerAddress;
    public uint DmaAddress => _dmaAddress;

    private static bool ReadCompatFlag(string key, bool defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(raw, "0", StringComparison.Ordinal)
            || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }

    public byte[] GetBufferRamSnapshot()
    {
        byte[] copy = new byte[_bufferRam.Length];
        Buffer.BlockCopy(_bufferRam, 0, copy, 0, _bufferRam.Length);
        return copy;
    }

    public void Reset()
    {
        // Reset only IFCTRL/CTRL0/CTRL1 and interrupt flags (jgenesis behavior).
        WriteIfCtrl(0x00);
        WriteCtrl0(0x00);
        WriteCtrl1(0x00);
        _transferEndInterruptPending = false;
        _decoderInterruptPending = false;
    }

    public void SetDeviceDestination(byte destBits)
    {
        _dmaAddress = 0;
        _endOfDataTransfer = false;
        _destinationBits = (byte)(destBits & 0x07);
        _destination = _destinationBits switch
        {
            0b010 => SegaCdDeviceDestination.MainCpuRegister,
            0b011 => SegaCdDeviceDestination.SubCpuRegister,
            0b100 => SegaCdDeviceDestination.Pcm,
            0b101 => SegaCdDeviceDestination.PrgRam,
            0b111 => SegaCdDeviceDestination.WordRam,
            _ => SegaCdDeviceDestination.None0
        };
        if (LogCdc)
            Console.WriteLine($"[SCD-CDC] DEST={_destination} BITS=0x{_destinationBits:X2} EOD={_endOfDataTransfer}");
        if (!_destLogged && _destination != SegaCdDeviceDestination.None0)
        {
            _destLogged = true;
            Console.Error.WriteLine($"[SCD-CDC-DEST] DEST={_destination} BITS=0x{_destinationBits:X2}");
        }
        if (TraceCdcTimeline)
        {
            Console.Error.WriteLine(
                $"[SCD-TL CDC] t={TraceStamp()} DEST bits=0x{_destinationBits:X2} dest={_destination} EOD={_endOfDataTransfer}");
        }
    }

    public void SetRegisterAddress(byte addr)
    {
        _registerAddress = (byte)(addr & 0x1F);
        if (LogCdc)
            Console.WriteLine($"[SCD-CDC] REGADDR=0x{_registerAddress:X2}");
        if (TraceCdcTimeline)
            Console.Error.WriteLine($"[SCD-TL CDC] t={TraceStamp()} REGADDR=0x{_registerAddress:X2}");
    }

    public void SetDmaAddress(uint address)
    {
        _dmaAddress = address;
        if (LogCdc)
            Console.WriteLine($"[SCD-CDC] DMA=0x{_dmaAddress:X6}");
        if (TraceCdcTimeline)
            Console.Error.WriteLine($"[SCD-TL CDC] t={TraceStamp()} DMA=0x{_dmaAddress:X6}");
    }

    public ushort ReadHostData(ScdCpu cpu)
    {
        if (!_dataTransferInProgress || !IsHostDataForCpu(cpu))
            return _hostDataBuffer ?? 0;

        ushort hostData = _hostDataBuffer ?? 0;
        if (TraceCdcTimeline)
            Console.Error.WriteLine($"[SCD-TL CDC] t={TraceStamp()} HOSTRD cpu={cpu} data=0x{hostData:X4}");
        _hostDataBuffer = null;

        if (_endOfDataTransfer)
        {
            _dataTransferInProgress = false;
        }
        else
        {
            PopulateHostDataBuffer();
        }

        return hostData;
    }

    public void WriteHostData(ScdCpu cpu)
    {
        if (!_dataTransferInProgress || !IsHostDataForCpu(cpu))
            return;

        if (TraceCdcTimeline)
            Console.Error.WriteLine($"[SCD-TL CDC] t={TraceStamp()} HOSTWR cpu={cpu}");
        if (_endOfDataTransfer)
        {
            _dataTransferInProgress = false;
        }
        else
        {
            PopulateHostDataBuffer();
        }
    }

    private bool IsHostDataForCpu(ScdCpu cpu)
    {
        return (cpu == ScdCpu.Main && _destination == SegaCdDeviceDestination.MainCpuRegister)
            || (cpu == ScdCpu.Sub && _destination == SegaCdDeviceDestination.SubCpuRegister);
    }

    private void PopulateHostDataBuffer()
    {
        ushort msb = _bufferRam[_dataAddressCounter];
        ushort lsb = _bufferRam[(_dataAddressCounter + 1) & BufferRamAddressMask];
        _hostDataBuffer = (ushort)((msb << 8) | lsb);
        _dataAddressCounter = (ushort)((_dataAddressCounter + 2) & BufferRamAddressMask);

        ushort newCounter = (ushort)(_dataByteCounter - 2);
        bool overflowed = _dataByteCounter < 2;
        _dataByteCounter = newCounter;
        if (overflowed)
            EndDmaTransfer();
    }

    private void EndDmaTransfer()
    {
        _endOfDataTransfer = true;
        SetTransferEndInterruptFlag();
    }

    public byte ReadRegister()
    {
        byte value;
        byte reg = _registerAddress;
        switch (reg)
        {
            case 0:
                value = 0xFF;
                break;
            case 1:
                value = (byte)(0x95
                    | (( _transferEndInterruptPending ? 0 : 1) << 6)
                    | (( _decoderInterruptPending ? 0 : 1) << 5)
                    | (( _dataTransferInProgress ? 0 : 1) << 3)
                    | (( _dataTransferInProgress ? 0 : 1) << 1));
                break;
            case 2:
                value = (byte)_dataByteCounter;
                break;
            case 3:
                byte dtei = (byte)(_transferEndInterruptPending ? 1 : 0);
                byte dbcHigh = (byte)((_dataByteCounter >> 8) & 0x0F);
                value = (byte)((dtei << 7) | (dtei << 6) | (dtei << 5) | (dtei << 4) | dbcHigh);
                break;
            case >= 4 and <= 7:
                int idx = reg - 4;
                value = _subheaderDataEnabled ? _subheaderData[idx] : _headerData[idx];
                break;
            case 8:
                value = _blockPointer.Lsb();
                break;
            case 9:
                value = _blockPointer.Msb();
                break;
            case 10:
                value = _writeAddress.Lsb();
                break;
            case 11:
                value = _writeAddress.Msb();
                break;
            case 12:
                value = 0x80;
                break;
            case 13:
                value = 0x00;
                break;
            case 14:
                value = 0x00;
                break;
            case 15:
                value = (byte)((_decoderInterruptPending ? 0 : 1) << 7);
                _decoderInterruptPending = false;
                break;
            default:
                value = 0xFF;
                break;
        }

        IncrementRegisterAddress();
        if (LogCdc)
            Console.WriteLine($"[SCD-CDC] R REG=0x{reg:X2} -> 0x{value:X2}");
        if (TraceCdcTimeline)
            Console.Error.WriteLine($"[SCD-TL CDC] t={TraceStamp()} R reg=0x{reg:X2} val=0x{value:X2}");
        return value;
    }

    public void WriteRegister(byte value)
    {
        byte reg = _registerAddress;
        if (LogCdc)
            Console.WriteLine($"[SCD-CDC] W REG=0x{reg:X2} = 0x{value:X2}");
        if (TraceCdcTimeline)
            Console.Error.WriteLine($"[SCD-TL CDC] t={TraceStamp()} W reg=0x{reg:X2} val=0x{value:X2}");
        switch (reg)
        {
            case 0:
                break;
            case 1:
                WriteIfCtrl(value);
                break;
            case 2:
                SegaCdBitUtils.SetLsb(ref _dataByteCounter, value);
                if (!_dbcLogged && _dataByteCounter != 0)
                {
                    _dbcLogged = true;
                    Console.Error.WriteLine($"[SCD-CDC-DBC] DBC=0x{_dataByteCounter:X4}");
                }
                break;
            case 3:
                SegaCdBitUtils.SetMsb(ref _dataByteCounter, (byte)(value & 0x0F));
                if (!_dbcLogged && _dataByteCounter != 0)
                {
                    _dbcLogged = true;
                    Console.Error.WriteLine($"[SCD-CDC-DBC] DBC=0x{_dataByteCounter:X4}");
                }
                break;
            case 4:
                SegaCdBitUtils.SetLsb(ref _dataAddressCounter, value);
                break;
            case 5:
                SegaCdBitUtils.SetMsb(ref _dataAddressCounter, value);
                _dataAddressCounter &= BufferRamAddressMask;
                break;
            case 6:
                if (CompatCdcAutoDest && _destination == SegaCdDeviceDestination.None0)
                {
                    _destination = SegaCdDeviceDestination.SubCpuRegister;
                    _destinationBits = 0b011;
                }
                _dataTransferInProgress = _dataOutEnabled;
                _endOfDataTransfer = !_dataTransferInProgress;
                if (_dataTransferInProgress && IsHostData())
                    PopulateHostDataBuffer();
                if (LogCdc)
                    Console.WriteLine($"[SCD-CDC] DTTRG transfer={_dataTransferInProgress} DOUTEN={_dataOutEnabled}");
                Console.Error.WriteLine($"[SCD-CDC-DTTRG] DOUTEN={(_dataOutEnabled ? 1 : 0)} DEST={_destination} DBC=0x{_dataByteCounter:X4} DAC=0x{_dataAddressCounter:X4}");
                break;
            case 7:
                _transferEndInterruptPending = false;
                break;
            case 8:
                SegaCdBitUtils.SetLsb(ref _writeAddress, value);
                break;
            case 9:
                SegaCdBitUtils.SetMsb(ref _writeAddress, value);
                _writeAddress &= BufferRamAddressMask;
                break;
            case 10:
                WriteCtrl0(value);
                break;
            case 11:
                WriteCtrl1(value);
                break;
            case 12:
                SegaCdBitUtils.SetLsb(ref _blockPointer, value);
                break;
            case 13:
                SegaCdBitUtils.SetMsb(ref _blockPointer, value);
                _blockPointer &= BufferRamAddressMask;
                break;
            case 15:
                Reset();
                break;
            default:
                break;
        }

        IncrementRegisterAddress();
    }

    private void WriteIfCtrl(byte value)
    {
        bool prevDtei = _transferEndInterruptEnabled;
        bool prevDeci = _decoderInterruptEnabled;

        _transferEndInterruptEnabled = value.Bit(6);
        _decoderInterruptEnabled = value.Bit(5);

        if ((!prevDtei && _transferEndInterruptEnabled && _transferEndInterruptPending)
            || (!prevDeci && _decoderInterruptEnabled && _decoderInterruptPending))
        {
            _scdInterruptFlag = true;
        }

        _dataOutEnabled = value.Bit(1);
        if (!_dataOutEnabled)
        {
            _dataTransferInProgress = false;
            _endOfDataTransfer = true;
        }
        else if (CompatCdcAutoDest && _destination == SegaCdDeviceDestination.None0)
        {
            // Some BIOS/game flows enable DOUTEN before destination bits are written.
            _destination = SegaCdDeviceDestination.SubCpuRegister;
            _destinationBits = 0b011;
        }
    }

    private void WriteCtrl0(byte value)
    {
        _decoderEnabled = value.Bit(7);
        _decoderWritesEnabled = value.Bit(2);
        if (CompatCdcForceWrrq && _decoderEnabled && !_decoderWritesEnabled && _dataOutEnabled)
            _decoderWritesEnabled = true;
        if (!_decoderEnabled)
            _decoderInterruptPending = false;
        if (!_decoderEnabled || !_decoderWritesEnabled)
            _decodedFirstWrittenBlock = false;
    }

    private void WriteCtrl1(byte value)
    {
        _subheaderDataEnabled = value.Bit(0);
    }

    private void IncrementRegisterAddress()
    {
        if (_registerAddress != 0)
            _registerAddress = (byte)((_registerAddress + 1) & 0x1F);
    }

    private bool IsHostData()
    {
        return _destination == SegaCdDeviceDestination.MainCpuRegister
            || _destination == SegaCdDeviceDestination.SubCpuRegister;
    }

    private bool IsDma()
    {
        return _destination == SegaCdDeviceDestination.Pcm
            || _destination == SegaCdDeviceDestination.PrgRam
            || _destination == SegaCdDeviceDestination.WordRam;
    }

    public void DecodeBlock(byte[] sectorBuffer)
    {
        if (!_decoderEnabled)
            return;

        _decodedLast75HzCycle = true;
        Array.Copy(sectorBuffer, 12, _headerData, 0, 4);
        Array.Copy(sectorBuffer, 16, _subheaderData, 0, 4);
        SetDecoderInterruptFlag();
        if (TraceCdcDecode)
        {
            Console.Error.WriteLine(
                $"[SCD-TL CDC] t={TraceStamp()} DECODE hdr={_headerData[0]:X2} {_headerData[1]:X2} {_headerData[2]:X2} {_headerData[3]:X2} " +
                $"sub={_subheaderData[0]:X2} {_subheaderData[1]:X2} {_subheaderData[2]:X2} {_subheaderData[3]:X2} " +
                $"decEn={_decoderEnabled} wrEn={_decoderWritesEnabled}");
        }

        if (_decoderWritesEnabled)
        {
            for (int i = 0; i < sectorBuffer.Length; i++)
            {
                _bufferRam[_writeAddress] = sectorBuffer[i];
                _writeAddress = (ushort)((_writeAddress + 1) & BufferRamAddressMask);
            }

            if (_decodedFirstWrittenBlock)
            {
                _blockPointer = (ushort)((_blockPointer + CdRom.BytesPerSector) & BufferRamAddressMask);
            }
            else
            {
                _blockPointer = (ushort)((_blockPointer + DataTrackHeaderLen) & BufferRamAddressMask);
                _decodedFirstWrittenBlock = true;
            }
        }

        if (CompatCdcAutoDest && _dataOutEnabled && _destination == SegaCdDeviceDestination.None0)
        {
            _destination = SegaCdDeviceDestination.SubCpuRegister;
            _destinationBits = 0b011;
        }

        if (CompatCdcAutoXfer && _dataOutEnabled && !_dataTransferInProgress)
        {
            if (CompatCdcAutoDest && _destination == SegaCdDeviceDestination.None0)
            {
                _destination = SegaCdDeviceDestination.SubCpuRegister;
                _destinationBits = 0b011;
            }
            if (!IsHostData())
                return;
            if (_dataByteCounter == 0)
                _dataByteCounter = 0xFFFF;
            _dataAddressCounter = _blockPointer;
            _dataTransferInProgress = true;
            _endOfDataTransfer = false;
            PopulateHostDataBuffer();
        }
    }

    private void SetDecoderInterruptFlag()
    {
        _decoderInterruptPending = true;
        if (_decoderInterruptEnabled && (!_transferEndInterruptEnabled || !_transferEndInterruptPending))
            _scdInterruptFlag = true;
        if (!_decoderInterruptLogged)
        {
            _decoderInterruptLogged = true;
            Console.Error.WriteLine(
                $"[SCD-CDC-DECI] decEn={(_decoderInterruptEnabled ? 1 : 0)} scdInt={(_scdInterruptFlag ? 1 : 0)} " +
                $"DOUTEN={(_dataOutEnabled ? 1 : 0)} DEST={_destination} DBC=0x{_dataByteCounter:X4} DAC=0x{_dataAddressCounter:X4}");
        }
    }

    private void SetTransferEndInterruptFlag()
    {
        if (_transferEndInterruptEnabled && !_transferEndInterruptPending
            && (!_decoderInterruptEnabled || !_decoderInterruptPending))
        {
            _scdInterruptFlag = true;
        }
        _transferEndInterruptPending = true;
    }

    public void Clock44100Hz(WordRam wordRam, byte[] prgRam, bool prgRamAccessible, SegaCdPcmStub pcm)
    {
        if (_dataTransferInProgress && IsDma())
            ProgressDma(wordRam, prgRam, prgRamAccessible, pcm);

        _cycles44100SinceDecode++;
        if (_cycles44100SinceDecode == DecoderInterruptClearCycle)
            _decoderInterruptPending = false;
    }

    public void Clock75Hz()
    {
        if (!_decodedLast75HzCycle && _decoderEnabled)
            SetDecoderInterruptFlag();
        _decodedLast75HzCycle = false;
        _cycles44100SinceDecode = 0;
    }

    private static string TraceStamp()
    {
        long ticks = Stopwatch.GetTimestamp() - TraceStartTicks;
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        return ms.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private void ProgressDma(WordRam wordRam, byte[] prgRam, bool prgRamAccessible, SegaCdPcmStub pcm)
    {
        if (_destination == SegaCdDeviceDestination.PrgRam && !prgRamAccessible)
            return;
        if (_destination == SegaCdDeviceDestination.WordRam && wordRam.IsSubAccessBlocked())
            return;

        uint dmaAddressMask = _destination switch
        {
            SegaCdDeviceDestination.PrgRam => (1u << 19) - 1,
            SegaCdDeviceDestination.WordRam => wordRam.Mode == WordRamMode.TwoM ? (1u << 18) - 1 : (1u << 17) - 1,
            SegaCdDeviceDestination.Pcm => (1u << 12) - 1,
            _ => 0
        };

        if (_destination == SegaCdDeviceDestination.Pcm)
        {
            uint dmaAddress = (_dmaAddress >> 1) & dmaAddressMask;
            for (int i = 0; i < 128; i++)
            {
                byte value = _bufferRam[_dataAddressCounter];
                pcm.DmaWrite(dmaAddress, value);
                _dataAddressCounter = (ushort)((_dataAddressCounter + 1) & BufferRamAddressMask);
                dmaAddress = (dmaAddress + 1) & dmaAddressMask;
                if (_dataByteCounter == 0)
                {
                    _dataByteCounter = 0xFFFF;
                    _dataTransferInProgress = false;
                    EndDmaTransfer();
                    break;
                }
                _dataByteCounter--;
            }
            _dmaAddress = dmaAddress << 1;
            return;
        }

        uint addr = _dmaAddress & dmaAddressMask;
        for (int i = 0; i < 64; i++)
        {
            if (_dataByteCounter == 0)
            {
                _dataByteCounter = 0xFFFF;
                _dataTransferInProgress = false;
                EndDmaTransfer();
                break;
            }

            byte msb = _bufferRam[_dataAddressCounter];
            byte lsb = _bufferRam[(_dataAddressCounter + 1) & BufferRamAddressMask];

            if (_destination == SegaCdDeviceDestination.PrgRam)
            {
                prgRam[addr] = msb;
                prgRam[(addr + 1) & dmaAddressMask] = lsb;
            }
            else if (_destination == SegaCdDeviceDestination.WordRam)
            {
                wordRam.DmaWrite(addr, msb);
                wordRam.DmaWrite((addr + 1) & dmaAddressMask, lsb);
            }

            _dataAddressCounter = (ushort)((_dataAddressCounter + 2) & BufferRamAddressMask);
            addr = (addr + 2) & dmaAddressMask;

            ushort newCounter = (ushort)(_dataByteCounter - 2);
            bool overflowed = _dataByteCounter < 2;
            _dataByteCounter = newCounter;
            if (overflowed)
            {
                _dataTransferInProgress = false;
                EndDmaTransfer();
                break;
            }
        }

        _dmaAddress = addr;
    }

    public bool InterruptPending => _scdInterruptFlag;

    public void AcknowledgeInterrupt()
    {
        _scdInterruptFlag = false;
    }
}
