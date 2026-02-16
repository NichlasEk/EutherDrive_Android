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
    private bool _dataReady;
    private bool _endOfData;
    private bool _interruptPending;

    public bool EndOfDataTransfer => _endOfData;
    public bool DataReady => _dataReady;
    public SegaCdDeviceDestination DeviceDestination => _destination;

    public byte RegisterAddress => _registerAddress;

    public ushort ReadHostData() => _hostData;

    public void WriteHostData()
    {
        // TODO: hook CDC DMA FIFO
        _dataReady = false;
    }

    public byte ReadRegister() => _registerData;

    public void WriteRegister(byte value)
    {
        _registerData = value;
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
        _dataReady = false;
        _endOfData = false;
        _interruptPending = false;
    }
}
