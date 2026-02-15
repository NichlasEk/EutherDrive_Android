using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EutherDrive.UI.Zuul;

internal sealed class JoxFile
{
    public string Title { get; init; } = string.Empty;
    public uint Flags { get; init; }
    public uint TicksPerSecond { get; init; }
    public uint EntryChunkIndex { get; init; }
    public List<Chunk> Chunks { get; } = new();

    internal sealed class Chunk
    {
        public string Tag { get; init; } = string.Empty;
        public uint Offset { get; init; }
        public uint Length { get; init; }
        public uint Flags { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    public static JoxFile Load(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (magic != "JOX1")
            throw new InvalidDataException("Not a JOX1 file.");

        ushort headerSize = br.ReadUInt16();
        ushort version = br.ReadUInt16();
        if (headerSize != 64)
            throw new InvalidDataException("Unsupported header size.");
        if (version != 1)
            throw new InvalidDataException("Unsupported JOX version.");

        uint fileSize = br.ReadUInt32();
        uint chunkCount = br.ReadUInt32();
        uint flags = br.ReadUInt32();
        uint tps = br.ReadUInt32();
        uint entryIndex = br.ReadUInt32();
        uint crc32 = br.ReadUInt32();
        _ = fileSize;
        _ = crc32;

        byte[] titleBytes = br.ReadBytes(32);
        string title = Encoding.UTF8.GetString(titleBytes).TrimEnd('\0');

        var table = new List<(string tag, uint off, uint len, uint cflg)>();
        for (int i = 0; i < chunkCount; i++)
        {
            string tag = Encoding.ASCII.GetString(br.ReadBytes(4));
            uint off = br.ReadUInt32();
            uint len = br.ReadUInt32();
            uint cflg = br.ReadUInt32();
            table.Add((tag, off, len, cflg));
        }

        var file = new JoxFile
        {
            Title = title,
            Flags = flags,
            TicksPerSecond = tps == 0 ? 60u : tps,
            EntryChunkIndex = entryIndex
        };

        foreach (var c in table)
        {
            stream.Position = c.off;
            byte[] data = br.ReadBytes(checked((int)c.len));
            file.Chunks.Add(new Chunk
            {
                Tag = c.tag,
                Offset = c.off,
                Length = c.len,
                Flags = c.cflg,
                Data = data
            });
        }

        return file;
    }
}
