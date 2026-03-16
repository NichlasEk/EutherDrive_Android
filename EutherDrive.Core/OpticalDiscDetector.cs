using System;
using System.IO;
using System.Text;
using EutherDrive.Core.SegaCd;
using ProjectPSX.IO;

namespace EutherDrive.Core;

public enum OpticalDiscKind
{
    Unknown = 0,
    Psx = 1,
    SegaCd = 2,
    PceCd = 3
}

public static class OpticalDiscDetector
{
    private const int PsxProbeBytes = 0x20000;

    public static OpticalDiscKind Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !VirtualFileSystem.Exists(path))
            return OpticalDiscKind.Unknown;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".pbp")
            return OpticalDiscKind.Psx;

        if (ext == ".cue")
            return DetectCue(path);

        if (ext is ".bin" or ".img" or ".iso" or ".chd")
        {
            if (LooksLikeSegaCdImage(path))
                return OpticalDiscKind.SegaCd;
            if (LooksLikePsxImage(path))
                return OpticalDiscKind.Psx;
        }

        return OpticalDiscKind.Unknown;
    }

    private static OpticalDiscKind DetectCue(string cuePath)
    {
        CueSheetResolver.CueTrackReference? dataTrack = CueSheetResolver.ResolveFirstDataTrack(cuePath);
        if (dataTrack == null || !VirtualFileSystem.Exists(dataTrack.FilePath))
            return OpticalDiscKind.Unknown;

        if (LooksLikeSegaCdImage(dataTrack))
            return OpticalDiscKind.SegaCd;
        if (LooksLikePceCdImage(dataTrack))
            return OpticalDiscKind.PceCd;
        if (LooksLikePsxImage(dataTrack))
            return OpticalDiscKind.Psx;

        return OpticalDiscKind.Unknown;
    }

    private static bool LooksLikeSegaCdImage(string path)
    {
        try
        {
            return SegaCdDiscInfo.IsSegaCdDisc(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeSegaCdImage(CueSheetResolver.CueTrackReference track)
    {
        try
        {
            byte[] header = ReadData(track, 0, 0x800);
            if (header.Length < 0x210)
                return false;

            string headerAt0x10 = ReadAscii(header, 0x10, 0x100);
            if (LooksLikeSegaCdHeader(headerAt0x10))
                return true;

            string headerAt0x00 = ReadAscii(header, 0x00, 0x100);
            return LooksLikeSegaCdHeader(headerAt0x00);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikePceCdImage(CueSheetResolver.CueTrackReference track)
    {
        try
        {
            byte[] probe = ReadData(track, 0, Math.Max(track.SectorSize * 4, 0x1000));
            if (probe.Length == 0)
                return false;

            string text = Encoding.ASCII.GetString(probe);
            return text.Contains("PC ENGINE CD-ROM SYSTEM", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikePsxImage(string path)
    {
        try
        {
            if (!VirtualFileSystem.Exists(path))
                return false;

            using Stream fs = VirtualFileSystem.OpenRead(path);
            int readLen = (int)Math.Min(PsxProbeBytes, fs.Length);
            byte[] buf = new byte[readLen];
            int n = fs.Read(buf, 0, readLen);
            if (n <= 0)
                return false;

            return BufferLooksLikePsx(buf, n);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikePsxImage(CueSheetResolver.CueTrackReference track)
    {
        try
        {
            byte[] buf = ReadData(track, 0, PsxProbeBytes);
            return BufferLooksLikePsx(buf, buf.Length);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadData(CueSheetResolver.CueTrackReference track, long dataOffsetBytes, int byteCount)
    {
        if (byteCount <= 0)
            return Array.Empty<byte>();

        using var stream = VirtualFileSystem.OpenRead(track.FilePath);
        long absoluteOffset = track.FileOffsetBytes + track.DataOffset + dataOffsetBytes;
        if (absoluteOffset < 0 || absoluteOffset >= stream.Length)
            return Array.Empty<byte>();

        stream.Seek(absoluteOffset, SeekOrigin.Begin);
        int readLen = (int)Math.Min(byteCount, stream.Length - absoluteOffset);
        byte[] buffer = new byte[readLen];
        int totalRead = stream.Read(buffer, 0, readLen);
        if (totalRead == readLen)
            return buffer;

        Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    private static bool BufferLooksLikePsx(byte[] buffer, int length)
    {
        if (length <= 0)
            return false;

        string text = Encoding.ASCII.GetString(buffer, 0, length);
        return text.Contains("PLAYSTATION", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PS-X EXE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Sony Computer Entertainment", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset + length > data.Length)
            return string.Empty;

        return Encoding.ASCII.GetString(data, offset, length).TrimEnd('\0', ' ');
    }

    private static bool LooksLikeSegaCdHeader(string header)
    {
        string upper = header.ToUpperInvariant();
        return upper.Contains("SEGADISCSYSTEM", StringComparison.Ordinal)
            || upper.Contains("SEGA MEGA CD", StringComparison.Ordinal)
            || upper.Contains("SEGA CD", StringComparison.Ordinal);
    }
}
