using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace ProjectPSX.Devices.CdRom {
    internal static class PsxDiscBootResolver {
        private const int SectorDataOffset = 24;
        private const int SectorDataLength = 2048;
        private const int PrimaryVolumeDescriptorSector = 16;
        private const int PrimaryVolumeDescriptorRootRecordOffset = 156;

        internal sealed class ResolvedExecutable {
            public string BootPath { get; }
            public byte[] ExecutableBytes { get; }
            public uint EntryPoint { get; }

            public ResolvedExecutable(string bootPath, byte[] executableBytes, uint entryPoint) {
                BootPath = bootPath;
                ExecutableBytes = executableBytes;
                EntryPoint = entryPoint;
            }
        }

        private sealed class DirectoryRecord {
            public int ExtentSector { get; }
            public int DataLength { get; }
            public bool IsDirectory { get; }
            public string Identifier { get; }

            public DirectoryRecord(int extentSector, int dataLength, bool isDirectory, string identifier) {
                ExtentSector = extentSector;
                DataLength = dataLength;
                IsDirectory = isDirectory;
                Identifier = identifier;
            }
        }

        public static ResolvedExecutable TryResolveExecutable(CD cd) {
            try {
                byte[] systemCnf = TryReadFile(cd, "SYSTEM.CNF;1");
                if (systemCnf == null) {
                    Console.WriteLine("[PSX-SUPERFAST] SYSTEM.CNF not found; falling back to turbo boot only.");
                    return null;
                }

                string bootPath = TryExtractBootPath(systemCnf);
                if (string.IsNullOrWhiteSpace(bootPath)) {
                    Console.WriteLine("[PSX-SUPERFAST] BOOT entry missing in SYSTEM.CNF; falling back to turbo boot only.");
                    return null;
                }

                byte[] executableBytes = TryReadFile(cd, bootPath);
                if (executableBytes == null) {
                    Console.WriteLine($"[PSX-SUPERFAST] Boot executable '{bootPath}' not found; falling back to turbo boot only.");
                    return null;
                }

                if (!IsPsxExecutable(executableBytes)) {
                    Console.WriteLine($"[PSX-SUPERFAST] Boot executable '{bootPath}' is not a PS-X EXE; falling back to turbo boot only.");
                    return null;
                }

                uint entryPoint = BinaryPrimitives.ReadUInt32LittleEndian(executableBytes.AsSpan(0x10, 4));
                return new ResolvedExecutable(bootPath, executableBytes, entryPoint);
            } catch (Exception ex) {
                Console.WriteLine($"[PSX-SUPERFAST] Disc boot resolve failed: {ex.Message}");
                return null;
            }
        }

        private static byte[] TryReadFile(CD cd, string path) {
            DirectoryRecord record = TryResolvePath(cd, NormalizeDiscPath(path));
            if (record == null || record.IsDirectory || record.DataLength < 0) {
                return null;
            }

            return ReadExtent(cd, record.ExtentSector, record.DataLength);
        }

        private static DirectoryRecord TryResolvePath(CD cd, string normalizedPath) {
            if (string.IsNullOrWhiteSpace(normalizedPath)) {
                return null;
            }

            string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) {
                return null;
            }

            DirectoryRecord current = GetRootDirectoryRecord(cd);
            if (current == null) {
                return null;
            }

            for (int i = 0; i < segments.Length; i++) {
                bool lastSegment = i == segments.Length - 1;
                DirectoryRecord next = TryFindChild(cd, current, segments[i], lastSegment);
                if (next == null) {
                    return null;
                }

                current = next;
            }

            return current;
        }

        private static DirectoryRecord GetRootDirectoryRecord(CD cd) {
            byte[] pvd = ReadDataSector(cd, PrimaryVolumeDescriptorSector);
            if (pvd.Length < PrimaryVolumeDescriptorRootRecordOffset + 34) {
                return null;
            }

            if (pvd[0] != 0x01 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") {
                return null;
            }

            return ParseDirectoryRecord(pvd.AsSpan(PrimaryVolumeDescriptorRootRecordOffset));
        }

        private static DirectoryRecord TryFindChild(CD cd, DirectoryRecord parent, string targetSegment, bool allowFiles) {
            if (!parent.IsDirectory) {
                return null;
            }

            byte[] directoryData = ReadExtent(cd, parent.ExtentSector, parent.DataLength);
            int offset = 0;
            while (offset < directoryData.Length) {
                byte recordLength = directoryData[offset];
                if (recordLength == 0) {
                    int nextSector = ((offset / SectorDataLength) + 1) * SectorDataLength;
                    offset = nextSector;
                    continue;
                }

                if (offset + recordLength > directoryData.Length) {
                    break;
                }

                DirectoryRecord entry = ParseDirectoryRecord(directoryData.AsSpan(offset, recordLength));
                if (entry != null && IdentifiersMatch(entry.Identifier, targetSegment)) {
                    if (entry.IsDirectory || allowFiles) {
                        return entry;
                    }
                }

                offset += recordLength;
            }

            return null;
        }

        private static DirectoryRecord ParseDirectoryRecord(ReadOnlySpan<byte> recordBytes) {
            if (recordBytes.Length < 34) {
                return null;
            }

            int recordLength = recordBytes[0];
            if (recordLength <= 0 || recordLength > recordBytes.Length) {
                return null;
            }

            int extentSector = BinaryPrimitives.ReadInt32LittleEndian(recordBytes.Slice(2, 4));
            int dataLength = BinaryPrimitives.ReadInt32LittleEndian(recordBytes.Slice(10, 4));
            bool isDirectory = (recordBytes[25] & 0x02) != 0;
            int identifierLength = recordBytes[32];
            if (identifierLength <= 0 || 33 + identifierLength > recordLength) {
                return null;
            }

            ReadOnlySpan<byte> identifierBytes = recordBytes.Slice(33, identifierLength);
            string identifier = identifierLength == 1 && identifierBytes[0] == 0
                ? "."
                : identifierLength == 1 && identifierBytes[0] == 1
                    ? ".."
                    : Encoding.ASCII.GetString(identifierBytes);

            return new DirectoryRecord(extentSector, dataLength, isDirectory, identifier);
        }

        private static byte[] ReadExtent(CD cd, int extentSector, int dataLength) {
            if (dataLength <= 0) {
                return Array.Empty<byte>();
            }

            int sectorCount = (dataLength + SectorDataLength - 1) / SectorDataLength;
            byte[] result = new byte[dataLength];
            int writeOffset = 0;
            for (int i = 0; i < sectorCount; i++) {
                byte[] sector = ReadDataSector(cd, extentSector + i);
                int copyLength = Math.Min(SectorDataLength, dataLength - writeOffset);
                Buffer.BlockCopy(sector, 0, result, writeOffset, copyLength);
                writeOffset += copyLength;
            }

            return result;
        }

        private static byte[] ReadDataSector(CD cd, int logicalSector) {
            int volumeStartLba = GetVolumeStartLba(cd);
            byte[] rawSector = cd.Read(volumeStartLba + logicalSector);
            byte[] result = new byte[SectorDataLength];
            Buffer.BlockCopy(rawSector, SectorDataOffset, result, 0, SectorDataLength);
            return result;
        }

        private static int GetVolumeStartLba(CD cd) {
            foreach (TrackBuilder.Track track in cd.tracks) {
                if (!track.isAudio) {
                    return track.lbaStart;
                }
            }

            return 150;
        }

        private static string NormalizeDiscPath(string path) {
            string normalized = path.Trim();
            if (normalized.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase)) {
                normalized = normalized.Substring("cdrom:".Length);
            }

            normalized = normalized.Replace('\\', '/');
            while (normalized.StartsWith("/", StringComparison.Ordinal)) {
                normalized = normalized.Substring(1);
            }

            return normalized.Trim();
        }

        private static bool IdentifiersMatch(string isoIdentifier, string targetSegment) {
            string normalizedIso = NormalizeIdentifier(isoIdentifier);
            string normalizedTarget = NormalizeIdentifier(targetSegment);
            if (normalizedIso.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            int isoVersionIndex = normalizedIso.IndexOf(';');
            if (isoVersionIndex >= 0 &&
                normalizedIso.Substring(0, isoVersionIndex).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            int targetVersionIndex = normalizedTarget.IndexOf(';');
            if (targetVersionIndex >= 0 &&
                normalizedTarget.Substring(0, targetVersionIndex).Equals(normalizedIso, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            return false;
        }

        private static string NormalizeIdentifier(string identifier) {
            return identifier.Trim().TrimEnd('.');
        }

        private static string TryExtractBootPath(byte[] systemCnfBytes) {
            string text = Encoding.ASCII.GetString(systemCnfBytes).Replace("\0", string.Empty);
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines) {
                string line = rawLine.Trim();
                int eqIndex = line.IndexOf('=');
                if (eqIndex <= 0) {
                    continue;
                }

                string key = line.Substring(0, eqIndex).Trim();
                if (!key.StartsWith("BOOT", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                string value = line.Substring(eqIndex + 1).Trim();
                if (!string.IsNullOrWhiteSpace(value)) {
                    return NormalizeDiscPath(value);
                }
            }

            return string.Empty;
        }

        private static bool IsPsxExecutable(byte[] executableBytes) {
            if (executableBytes.Length < 0x800) {
                return false;
            }

            return executableBytes[0] == (byte)'P'
                && executableBytes[1] == (byte)'S'
                && executableBytes[2] == (byte)'-'
                && executableBytes[3] == (byte)'X'
                && executableBytes[4] == (byte)' '
                && executableBytes[5] == (byte)'E'
                && executableBytes[6] == (byte)'X'
                && executableBytes[7] == (byte)'E';
        }
    }
}
