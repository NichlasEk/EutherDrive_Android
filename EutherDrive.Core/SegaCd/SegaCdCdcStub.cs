namespace EutherDrive.Core.SegaCd;

public enum SegaCdDeviceDestination
{
    None = 0,
    PrgRam = 1,
    WordRam = 2,
    Pcm = 3
}

public sealed class SegaCdCdcStub
{
    private SegaCdDeviceDestination _destination;
    private byte _registerAddress;
    private ushort _hostData;
    private byte _registerData;
    private uint _dmaAddress;
    private bool _dataReady = true;
    private bool _endOfData = true;
    private bool _interruptPending;
    private readonly byte[] _registers = new byte[0x20];

    public bool EndOfDataTransfer => _endOfData;
    public bool DataReady => _dataReady;
    public SegaCdDeviceDestination DeviceDestination => _destination;

    public byte RegisterAddress => _registerAddress;

    public ushort ReadHostData()
    {
        _dataReady = true;
        _endOfData = true;
        _interruptPending = true;
        return _hostData;
    }

    public void WriteHostData()
    {
        // TODO: hook CDC DMA FIFO
        _dataReady = true;
        _endOfData = true;
        _interruptPending = true;
    }

    public byte ReadRegister()
    {
        byte value = _registers[_registerAddress & 0x1F];
        _dataReady = true;
        _endOfData = true;
        _interruptPending = true;
        return value;
    }

    public void WriteRegister(byte value)
    {
        _registerData = value;
        _registers[_registerAddress & 0x1F] = value;
        _dataReady = true;
        _endOfData = true;
        _interruptPending = true;
    }

    public void SetRegisterAddress(byte addr)
    {
        _registerAddress = addr;
    }

    public uint DmaAddress => _dmaAddress;

    public void SetDmaAddress(uint address)
    {
        _dmaAddress = address & 0x3FFFF;
    }

    public void SetDeviceDestination(SegaCdDeviceDestination dest)
    {
        _destination = dest;
        _dataReady = true;
        _endOfData = true;
        _interruptPending = true;
    }

    public bool InterruptPending => _interruptPending;

    public void AcknowledgeInterrupt()
    {
        _interruptPending = false;
    }

    public void Reset()
    {
        _destination = SegaCdDeviceDestination.None;
        _registerAddress = 0;
        _hostData = 0;
        _registerData = 0;
        _dmaAddress = 0;
        _dataReady = true;
        _endOfData = true;
        _interruptPending = false;
        Array.Clear(_registers, 0, _registers.Length);
    }

    public void Tick()
    {
        if (_dataReady || _endOfData)
            _interruptPending = true;
    }
}
