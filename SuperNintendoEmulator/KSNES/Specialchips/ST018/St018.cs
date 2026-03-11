using KSNES.SNESSystem;

namespace KSNES.Specialchips.ST018;

public sealed class St018
{
    private readonly Registers _registers;
    private readonly Arm7TdmiEmulator _arm;
    private ulong _lastSnesCycles;
    private ulong _snesCycles;

    public St018(byte[] rom, ISNESSystem snes)
    {
        _registers = new Registers();
        _arm = new Arm7TdmiEmulator(rom, _registers);
    }

    public void Reset()
    {
        _registers.Reset();
        _arm.Reset();
        _lastSnesCycles = 0;
        _snesCycles = 0;
    }

    public void RunTo(ulong snesCycles)
    {
        if (snesCycles <= _lastSnesCycles)
            return;

        ulong delta = snesCycles - _lastSnesCycles;
        _lastSnesCycles = snesCycles;
        _snesCycles += delta;

        if (_registers.ArmReset)
        {
            _registers.ArmReset = false;
            _arm.Reset();
        }

        while (_arm.BusCycles < _snesCycles)
        {
            _arm.ExecuteInstruction();
        }
    }

    public void ResyncTo(ulong snesCycles)
    {
        _lastSnesCycles = snesCycles;
        _snesCycles = _arm.BusCycles;
    }

    public byte Read(uint address)
    {
        return _registers.SnesRead(address) ?? 0;
    }

    public void Write(uint address, byte value)
    {
        _registers.SnesWrite(address, value);
    }
}

internal sealed class Registers
{
    public byte SnesToArmData { get; set; }
    public bool SnesToArmDataReady { get; set; }
    public byte ArmToSnesData { get; set; }
    public bool ArmToSnesDataReady { get; set; }
    public bool ArmToSnesFlag { get; set; }
    public bool ArmReset { get; set; }

    public void Reset()
    {
        SnesToArmData = 0;
        SnesToArmDataReady = false;
        ArmToSnesData = 0;
        ArmToSnesDataReady = false;
        ArmToSnesFlag = false;
        ArmReset = true;
    }

    public byte? ArmRead(uint address)
    {
        return (address & 0xFF) switch
        {
            0x10 => ReadSnesToArmData(),
            0x20 => ReadStatus(),
            _ => null,
        };
    }

    public void ArmWrite(uint address, byte value)
    {
        switch (address & 0xFF)
        {
            case 0x00:
                ArmToSnesData = value;
                ArmToSnesDataReady = true;
                break;
            case 0x10:
                ArmToSnesFlag = true;
                break;
            case >= 0x20 and <= 0x2F:
                // Config registers are ignored, matching jgenesis.
                break;
        }
    }

    public byte? SnesRead(uint address)
    {
        return (address & 0xFFFF) switch
        {
            0x3800 => ReadArmToSnesData(),
            0x3802 => ClearArmToSnesFlag(),
            0x3804 => ReadStatus(),
            _ => null,
        };
    }

    public void SnesWrite(uint address, byte value)
    {
        switch (address & 0xFFFF)
        {
            case 0x3802:
                SnesToArmData = value;
                SnesToArmDataReady = true;
                break;
            case 0x3804:
                ArmReset = (value & 0x01) != 0;
                break;
        }
    }

    private byte ReadSnesToArmData()
    {
        SnesToArmDataReady = false;
        return SnesToArmData;
    }

    private byte ReadArmToSnesData()
    {
        ArmToSnesDataReady = false;
        return ArmToSnesData;
    }

    private byte? ClearArmToSnesFlag()
    {
        ArmToSnesFlag = false;
        return null;
    }

    private byte ReadStatus()
    {
        return (byte)(
            (ArmToSnesDataReady ? 0x01 : 0x00) |
            (ArmToSnesFlag ? 0x04 : 0x00) |
            (SnesToArmDataReady ? 0x08 : 0x00) |
            0x40 |
            0x80);
    }
}
