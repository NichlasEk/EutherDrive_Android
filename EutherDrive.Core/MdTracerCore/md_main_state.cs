using System.IO;

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
    }
}
