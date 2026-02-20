using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.Savestates;

internal sealed class MdTracerStateSerializer
{
    private const string StateMagic = "EDST";
    private const int StateVersion = 2;

    private readonly List<IStatefulComponent> _components;

    public MdTracerStateSerializer()
    {
        _components = new List<IStatefulComponent>
        {
            new MdMainStateComponent(),
            new MdM68kStateComponent(),
            new ReflectionStateComponent("md_z80", () => md_main.g_md_z80),
            new ReflectionStateComponent("md_vdp", () => md_main.g_md_vdp),
            new ReflectionStateComponent("md_bus", () => md_main.g_md_bus),
            new ReflectionStateComponent("md_music", () => md_main.g_md_music),
            // md_io innehaller byref-like/stackalloc state som inte gaar att serialisera via reflection.
            new ReflectionStateComponent("md_control", () => md_main.g_md_control)
        };
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(Encoding.ASCII.GetBytes(StateMagic));
        writer.Write(StateVersion);
        writer.Write(_components.Count);

        foreach (var component in _components)
        {
            using var payloadStream = new MemoryStream();
            using (var payloadWriter = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
            {
                try
                {
                    component.Save(payloadWriter);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Savestate component '{component.Id}' save failed: {ex.Message}", ex);
                }
            }

            byte[] payload = payloadStream.ToArray();
            writer.Write(component.Id);
            writer.Write(payload.Length);
            writer.Write(payload);
        }
    }

    public void Load(BinaryReader reader)
    {
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(StateMagic.Length));
        if (!string.Equals(magic, StateMagic, StringComparison.Ordinal))
            throw new InvalidDataException($"State payload magic mismatch: '{magic}'.");

        int version = reader.ReadInt32();
        if (version != StateVersion)
            throw new InvalidDataException($"State payload version mismatch: {version}.");

        int componentCount = reader.ReadInt32();
        var lookup = new Dictionary<string, IStatefulComponent>(StringComparer.Ordinal);
        foreach (var component in _components)
            lookup[component.Id] = component;

        for (int i = 0; i < componentCount; i++)
        {
            string id = reader.ReadString();
            int length = reader.ReadInt32();
            byte[] payload = reader.ReadBytes(length);

            if (!lookup.TryGetValue(id, out var component))
                continue;

            using var payloadStream = new MemoryStream(payload, writable: false);
            using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: false);
            try
            {
                component.Load(payloadReader);
            }
            catch (Exception ex)
            {
                if (IsLenientSavestateLoad())
                {
                    Console.WriteLine($"[Savestate] WARNING: component '{component.Id}' load failed: {ex.Message}");
                    continue;
                }
                throw new InvalidOperationException(
                    $"Savestate component '{component.Id}' load failed: {ex.Message}", ex);
            }
        }
    }

    private static bool IsLenientSavestateLoad()
    {
        string? value = Environment.GetEnvironmentVariable("EUTHERDRIVE_SAVESTATE_LENIENT");
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MdMainStateComponent : IStatefulComponent
    {
        public string Id => "md_main";

        public void Save(BinaryWriter writer) => md_main.SaveState(writer);

        public void Load(BinaryReader reader) => md_main.LoadState(reader);
    }

    private sealed class MdM68kStateComponent : IStatefulComponent
    {
        public string Id => "md_m68k";

        public void Save(BinaryWriter writer) => md_m68k.SaveState(writer);

        public void Load(BinaryReader reader) => md_m68k.LoadState(reader);
    }

    private sealed class ReflectionStateComponent : IStatefulComponent
    {
        private readonly Func<object?> _getter;
        public string Id { get; }

        public ReflectionStateComponent(string id, Func<object?> getter)
        {
            Id = id;
            _getter = getter;
        }

        public void Save(BinaryWriter writer)
        {
            object? obj = _getter();
            if (obj == null)
                throw new InvalidOperationException($"Savestate component '{Id}' is missing.");
            StateBinarySerializer.WriteObject(writer, obj);
        }

        public void Load(BinaryReader reader)
        {
            object? obj = _getter();
            if (obj == null)
                throw new InvalidOperationException($"Savestate component '{Id}' is missing.");
            bool hasObject = reader.ReadBoolean();
            if (!hasObject)
                return;
            StateBinarySerializer.ReadInto(reader, obj);
        }
    }
}
