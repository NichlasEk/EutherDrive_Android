namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgBus
{
    private readonly SmsGgVdpVersion _version;
    private readonly SmsGgMemory _memory;
    private readonly SmsGgInputPorts _input;
    private readonly SmsGgVdp _vdp;
    private readonly Action<byte>? _psgWrite;
    private readonly Action<byte>? _stereoWrite;

    public SmsGgBus(
        SmsGgVdpVersion version,
        SmsGgMemory memory,
        SmsGgInputPorts input,
        SmsGgVdp vdp,
        Action<byte>? psgWrite = null,
        Action<byte>? stereoWrite = null)
    {
        _version = version;
        _memory = memory;
        _input = input;
        _vdp = vdp;
        _psgWrite = psgWrite;
        _stereoWrite = stereoWrite;
    }

    public byte ReadMemory(ushort address) => _memory.Read(address);

    public void WriteMemory(ushort address, byte value) => _memory.Write(address, value);

    public byte ReadIo(byte address)
    {
        if (_version == SmsGgVdpVersion.GameGear && address <= 0x06)
        {
            return address switch
            {
                0x00 => (byte)(((_input.PausePressed ? 0 : 1) << 7) | ((_input.Region == SmsGgRegion.International ? 1 : 0) << 6)),
                0x01 => _memory.GameGearRegisters.ExtPort,
                0x02 => _memory.GameGearRegisters.ParallelPort,
                0x04 or 0x06 => 0xFF,
                0x03 or 0x05 => 0x00,
                _ => 0xFF
            };
        }

        if (address == 0xF2)
            return _memory.ReadAudioControl();

        return (((address >> 7) & 1), ((address >> 6) & 1), (address & 1)) switch
        {
            (0, 0, _) => 0xFF,
            (0, 1, 0) => _vdp.VCounter(),
            (0, 1, 1) => _vdp.HCounter(),
            (1, 0, 0) => _vdp.ReadData(),
            (1, 0, 1) => _vdp.ReadControl(),
            (1, 1, 0) => _input.PortDc(),
            (1, 1, 1) => _input.PortDd(),
            _ => 0xFF
        };
    }

    public void WriteIo(byte address, byte value)
    {
        if (_version == SmsGgVdpVersion.GameGear && address <= 0x06)
        {
            switch (address)
            {
                case 0x01:
                    _memory.GameGearRegisters.ExtPort = (byte)(value & 0x7F);
                    break;
                case 0x02:
                    _memory.GameGearRegisters.ParallelPort = value;
                    break;
                case 0x06:
                    _stereoWrite?.Invoke(value);
                    break;
            }
            return;
        }

        if (address == 0xF2)
        {
            _memory.WriteAudioControl(value);
            return;
        }

        switch ((((address >> 7) & 1), ((address >> 6) & 1), (address & 1)))
        {
            case (0, 0, 0):
                _memory.MemoryControl.CartridgeEnabled = ((value & 0x40) == 0) || _version == SmsGgVdpVersion.GameGear;
                _memory.MemoryControl.BiosEnabled = (value & 0x08) == 0;
                break;
            case (0, 0, 1):
                _input.WriteControl(value, _vdp);
                break;
            case (0, 1, 0):
            case (0, 1, 1):
                _psgWrite?.Invoke(value);
                break;
            case (1, 0, 0):
                _vdp.WriteData(value);
                break;
            case (1, 0, 1):
                _vdp.WriteControl(value);
                break;
        }
    }

    public bool NmiLine => _version.IsMasterSystem() && _input.PausePressed;
    public bool IntLine => _vdp.InterruptPending;
}
