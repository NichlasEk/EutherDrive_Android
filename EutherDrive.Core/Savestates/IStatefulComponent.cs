using System.IO;

namespace EutherDrive.Core.Savestates;

internal interface IStatefulComponent
{
    string Id { get; }
    void Save(BinaryWriter writer);
    void Load(BinaryReader reader);
}
