using EutherDrive.Core.Cpu.Z80Emu;

namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgZ80BusAdapter : IBusInterface
{
    private readonly SmsGgBus _bus;

    public SmsGgZ80BusAdapter(SmsGgBus bus)
    {
        _bus = bus;
    }

    public byte ReadMemory(ushort address) => _bus.ReadMemory(address);

    public void WriteMemory(ushort address, byte value) => _bus.WriteMemory(address, value);

    public byte ReadIo(ushort address) => _bus.ReadIo((byte)address);

    public void WriteIo(ushort address, byte value) => _bus.WriteIo((byte)address, value);

    public InterruptLine Nmi() => _bus.NmiLine ? InterruptLine.Low : InterruptLine.High;

    public InterruptLine Int() => _bus.IntLine ? InterruptLine.Low : InterruptLine.High;

    public bool BusReq() => false;

    public bool Reset() => false;
}
