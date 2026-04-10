namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2SerialInterface
{
    private readonly byte[] _registers = new byte[6];
    private byte _transferData = 0xFF;
    private byte _transferShift = 0xFF;
    private ulong _transferClocks;
    private byte _receiveData;
    private byte _pendingReceiveData;
    private bool _pendingReceiveValid;
    private bool _txDataEmpty = true;
    private bool _rxDataFull;
    private bool _transferEnd = true;

    public bool RxInterruptPending => RxInterruptEnabled && _rxDataFull;

    private bool TxInterruptEnabled => (_registers[2] & 0x80) != 0;
    private bool RxInterruptEnabled => (_registers[2] & 0x40) != 0;
    private bool TxEnabled => (_registers[2] & 0x20) != 0;
    private bool RxEnabled => (_registers[2] & 0x10) != 0;
    private byte ClockSelect => (byte)(_registers[0] & 0x03);
    private byte BitRate => _registers[1];

    public void Reset()
    {
        Array.Clear(_registers, 0, _registers.Length);
        _transferData = 0xFF;
        _transferShift = 0xFF;
        _transferClocks = 0;
        _receiveData = 0;
        _pendingReceiveData = 0;
        _pendingReceiveValid = false;
        _txDataEmpty = true;
        _rxDataFull = false;
        _transferEnd = true;
        _registers[3] = 0xFF;
    }

    public void QueueReceive(byte value)
    {
        _pendingReceiveData = value;
        _pendingReceiveValid = true;
    }

    public void Tick(ulong sh2CyclesElapsed, Action<byte> transmitByte)
    {
        if (RxEnabled && !_rxDataFull && _pendingReceiveValid)
        {
            _receiveData = _pendingReceiveData;
            _pendingReceiveValid = false;
            _rxDataFull = true;
        }

        if (_transferClocks == 0)
        {
            if (TxEnabled && !_txDataEmpty)
            {
                _transferShift = _transferData;
                _transferClocks = EstimateTxClocks(ClockSelect, BitRate);
                _txDataEmpty = true;
                _transferEnd = false;
            }
            else
            {
                return;
            }
        }

        _transferClocks = _transferClocks > sh2CyclesElapsed
            ? _transferClocks - sh2CyclesElapsed
            : 0;

        if (_transferClocks != 0)
            return;

        transmitByte(_transferShift);
        if (!_txDataEmpty)
        {
            _transferShift = _transferData;
            _transferClocks = EstimateTxClocks(ClockSelect, BitRate);
            _txDataEmpty = true;
        }
        else
        {
            _transferEnd = true;
        }
    }

    public byte ReadRegister(uint address)
    {
        return address switch
        {
            0xFFFFFE00 => ClockSelect,
            0xFFFFFE01 => _registers[1],
            0xFFFFFE02 => (byte)((TxInterruptEnabled ? 0x80 : 0)
                | (RxInterruptEnabled ? 0x40 : 0)
                | (TxEnabled ? 0x20 : 0)
                | (RxEnabled ? 0x10 : 0)),
            0xFFFFFE03 => _transferData,
            0xFFFFFE04 => (byte)((_txDataEmpty ? 0x80 : 0)
                | (_rxDataFull ? 0x40 : 0)
                | (_transferEnd ? 0x04 : 0)),
            0xFFFFFE05 => _receiveData,
            _ => 0,
        };
    }

    public void WriteRegister(uint address, byte value)
    {
        switch (address)
        {
            case 0xFFFFFE00:
                _registers[0] = (byte)(value & 0x03);
                break;
            case 0xFFFFFE01:
                _registers[1] = value;
                break;
            case 0xFFFFFE02:
                _registers[2] = (byte)(value & 0xF0);
                break;
            case 0xFFFFFE03:
                _transferData = value;
                _registers[3] = value;
                _transferEnd = false;
                break;
            case 0xFFFFFE04:
                _txDataEmpty &= (value & 0x80) != 0;
                _rxDataFull &= (value & 0x40) != 0;
                _transferEnd &= (value & 0x80) != 0;
                break;
            case 0xFFFFFE05:
                break;
        }
    }

    private static ulong EstimateTxClocks(byte clockSelect, byte bitRate)
    {
        ulong clocksPerBit = clockSelect == 0
            ? 128UL * ((ulong)bitRate + 1)
            : 256UL * (1UL << (2 * clockSelect - 1)) * ((ulong)bitRate + 1);

        return 8 * clocksPerBit;
    }
}
