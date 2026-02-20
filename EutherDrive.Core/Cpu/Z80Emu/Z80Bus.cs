using System;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    public enum InterruptLine : byte
    {
        High = 0,
        Low = 1,
    }

    public interface IBusInterface
    {
        byte ReadMemory(ushort address);
        void WriteMemory(ushort address, byte value);
        byte ReadIo(ushort address);
        void WriteIo(ushort address, byte value);
        InterruptLine Nmi();
        InterruptLine Int();
        bool BusReq();
        bool Reset();
    }
}
