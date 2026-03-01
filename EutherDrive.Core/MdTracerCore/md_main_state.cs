using System.IO;
using EutherDrive.Core.Cpu.M68000Emu;

namespace EutherDrive.Core.MdTracerCore;

internal static partial class md_main
{
    internal static void SaveState(BinaryWriter writer)
    {
        writer.Write(g_masterSystemMode);
        writer.Write(g_masterSystemRomSize);
        writer.Write(g_hard_reset_req);
        writer.Write(g_trace_nextframe);

        writer.Write(_z80ResetCycleId);
        writer.Write(_z80WaitFrames);
        writer.Write(_z80StableLowFrames);
        writer.Write(_z80WaitReleased);
        writer.Write(_z80WaitLogged);

        writer.Write(_mbxInjected);
        writer.Write(_mbxInjectAcked);
        writer.Write(_mbxInjectPendingClear);
        writer.Write(_mbxInjectCleared);
        writer.Write(_mbxInjectArmedLogged);
        writer.Write(_mbxInjectEnvLogged);
        writer.Write(_mbxInjectConfigLoaded);
        writer.Write(_injectMbxAddr);
        writer.Write(_injectMbxValue);
        writer.Write(_injectMbxFrame);

        writer.Write(_systemCycles);

        // Optional M68KEmu state (for savestates taken with new core active)
        bool hasM68kEmuState = UseM68kEmuMain && _m68kEmu != null;
        writer.Write(hasM68kEmuState);
        if (hasM68kEmuState)
        {
            var state = _m68kEmu!.GetState();
            for (int i = 0; i < state.Data.Length; i++)
                writer.Write(state.Data[i]);
            for (int i = 0; i < state.Address.Length; i++)
                writer.Write(state.Address[i]);
            writer.Write(state.Usp);
            writer.Write(state.Ssp);
            writer.Write(state.Sr);
            writer.Write(state.Pc);
            writer.Write(state.Prefetch);
        }
    }

    internal static void LoadState(BinaryReader reader)
    {
        g_masterSystemMode = reader.ReadBoolean();
        int savedRomSize = reader.ReadInt32();
        g_masterSystemRomSize = savedRomSize;
        g_hard_reset_req = reader.ReadBoolean();
        g_trace_nextframe = reader.ReadBoolean();

        _z80ResetCycleId = reader.ReadInt32();
        _z80WaitFrames = reader.ReadInt32();
        _z80StableLowFrames = reader.ReadInt32();
        _z80WaitReleased = reader.ReadBoolean();
        _z80WaitLogged = reader.ReadBoolean();

        _mbxInjected = reader.ReadBoolean();
        _mbxInjectAcked = reader.ReadBoolean();
        _mbxInjectPendingClear = reader.ReadBoolean();
        _mbxInjectCleared = reader.ReadBoolean();
        _mbxInjectArmedLogged = reader.ReadBoolean();
        _mbxInjectEnvLogged = reader.ReadBoolean();
        _mbxInjectConfigLoaded = reader.ReadBoolean();
        _injectMbxAddr = reader.ReadUInt16();
        _injectMbxValue = reader.ReadByte();
        _injectMbxFrame = reader.ReadInt64();

        _systemCycles = reader.ReadInt64();
        ResetCycleCounters();
        _loadedM68kEmuStateFromSavestate = false;

        // Optional trailing M68KEmu state; absent in old savestates.
        if (reader.BaseStream.Position >= reader.BaseStream.Length)
            return;

        bool hasM68kEmuState = reader.ReadBoolean();
        if (!hasM68kEmuState)
            return;
        if (!UseM68kEmuMain)
            return;

        uint[] data = new uint[8];
        for (int i = 0; i < data.Length; i++)
            data[i] = reader.ReadUInt32();

        uint[] address = new uint[7];
        for (int i = 0; i < address.Length; i++)
            address[i] = reader.ReadUInt32();

        uint usp = reader.ReadUInt32();
        uint ssp = reader.ReadUInt32();
        ushort sr = reader.ReadUInt16();
        uint pc = reader.ReadUInt32();
        ushort prefetch = reader.ReadUInt16();

        EnsureMainM68kBackend();
        if (_m68kEmu == null)
            return;

        var state = new M68000.M68000State(
            data: data,
            address: address,
            usp: usp,
            ssp: ssp,
            sr: sr,
            pc: pc,
            prefetch: prefetch);
        _m68kEmu.SetState(state);
        _m68kWaitCycles = 0;
        _m68kRefreshCounter = 0;
        _loadedM68kEmuStateFromSavestate = true;
    }
}
