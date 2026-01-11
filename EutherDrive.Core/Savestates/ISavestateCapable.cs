using System.IO;

namespace EutherDrive.Core.Savestates;

public interface ISavestateCapable
{
    RomIdentity? RomIdentity { get; }
    long? FrameCounter { get; }
    void SaveState(BinaryWriter writer);
    void LoadState(BinaryReader reader);
}
