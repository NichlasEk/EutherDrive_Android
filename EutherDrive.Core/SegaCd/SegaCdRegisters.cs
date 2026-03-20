using System;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdRegisters
{
    // $A12000: Reset / BUSREQ
    public bool MainToSubInterruptLatched { get; set; }
    public bool MainSoftwareInterruptPending { get; set; }
    public bool SubSoftwareInterruptPending { get; set; }
    public bool SoftwareInterruptEnabled { get; set; }
    public bool SubCpuBusReq { get; set; } = true;
    public bool SubCpuReset { get; set; } = true;
    public bool LedGreen { get; set; } = true;
    public bool LedRed { get; set; }

    // $A12002: Memory mode / PRG RAM bank select
    public byte PrgRamWriteProtect { get; set; }
    public byte PrgRamBank { get; set; }

    // $A12006: HINT vector
    public ushort HInterruptVector { get; set; } = 0xFFFF;

    // $A1200C: Stopwatch (12-bit)
    public ushort StopwatchCounter { get; set; }

    // $A1200E: Communication flags
    public byte SubCpuCommunicationFlags { get; set; }
    public byte MainCpuCommunicationFlags { get; set; }

    // $A12010-$A1201E: Communication commands
    public ushort[] CommunicationCommands { get; } = new ushort[8];

    // $A12020-$A1202E: Communication statuses
    public ushort[] CommunicationStatuses { get; } = new ushort[8];

    // $A12030: Timer
    public byte TimerCounter { get; set; }
    public byte TimerInterval { get; set; }
    public bool TimerInterruptPending { get; set; }

    // $A12032: Interrupt mask control
    public bool SubcodeInterruptEnabled { get; set; }
    public bool CdcInterruptEnabled { get; set; }
    public bool CddInterruptEnabled { get; set; }
    public bool TimerInterruptEnabled { get; set; }
    public bool GraphicsInterruptEnabled { get; set; }

    // $A12036: CDD control
    public bool CddHostClockOn { get; set; }

    // $A12042-$A1204B: CDD command buffer
    public byte[] CddCommand { get; } = new byte[10];

    public uint PrgRamAddr(uint address)
    {
        return ((uint)PrgRamBank << 17) | (address & 0x1FFFF);
    }

    public void Reset()
    {
        MainToSubInterruptLatched = false;
        MainSoftwareInterruptPending = false;
        SubSoftwareInterruptPending = false;
        SubCpuBusReq = true;
        SubCpuReset = true;
        LedGreen = true;
        LedRed = false;
        PrgRamWriteProtect = 0;
        PrgRamBank = 0;
        HInterruptVector = 0xFFFF;
        StopwatchCounter = 0;
        SubCpuCommunicationFlags = 0;
        MainCpuCommunicationFlags = 0;
        Array.Clear(CommunicationCommands, 0, CommunicationCommands.Length);
        Array.Clear(CommunicationStatuses, 0, CommunicationStatuses.Length);
        TimerCounter = 0;
        TimerInterval = 0;
        TimerInterruptPending = false;
        SubcodeInterruptEnabled = false;
        CdcInterruptEnabled = false;
        CddInterruptEnabled = false;
        TimerInterruptEnabled = false;
        GraphicsInterruptEnabled = false;
        CddHostClockOn = false;
        Array.Clear(CddCommand, 0, CddCommand.Length);
    }
}
