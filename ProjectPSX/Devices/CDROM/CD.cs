using System.IO;
using System;
using System.Collections.Generic;
using ProjectPSX.IO;
using static ProjectPSX.Devices.CdRom.TrackBuilder;

namespace ProjectPSX.Devices.CdRom {
    public class CD {

        private const int BYTES_PER_SECTOR_RAW = 2352;
        private const int BYTES_PER_SUBCHANNEL_FRAME = 96;
        private const int DISC_FIRST_LBA = 150;
        private static readonly bool Verbose = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE") == "1";

        private byte[] rawSectorBuffer = new byte[BYTES_PER_SECTOR_RAW];

        private Stream[] streams;
        private readonly Dictionary<string, Stream> subStreams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> subSectorCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private Stream? discSubStream;
        private long discSubSectors;
        private readonly Dictionary<int, SubchannelQ> discSubchannelOverrides = new Dictionary<int, SubchannelQ>();

        public List<Track> tracks;

        public bool isTrackChange;

        public CD(string diskFilename, string? subchannelOverridePath = null) {
            string ext = Path.GetExtension(diskFilename);

            if (ext == ".bin") {
                tracks = TrackBuilder.fromBin(diskFilename);
            } else if (ext == ".cue") {
                tracks = TrackBuilder.fromCue(diskFilename);
            } else if (ext == ".exe") {
                // TODO: THERES NOT ONLY NO CD BUT ANY ACCESS TO THE CDROM WILL THROW.
                // EXES THAT ACCES THE CDROM WILL CURRENTLY CRASH.
                return;
            }

            streams = new Stream[tracks.Count];

            for (int i = 0; i < tracks.Count; i++) {
                streams[i] = VirtualFileSystem.OpenRead(tracks[i].file);
                if (Verbose)
                    Console.WriteLine($"Track {i} size: {tracks[i].size} lbaStart: {tracks[i].lbaStart} lbaEnd: {tracks[i].lbaEnd}");
            }

            TryLoadSubchannels(diskFilename, subchannelOverridePath);
        }

        public byte[] Read(int loc) {

            Track currentTrack = getTrackFromLoc(loc);
            if (!TryMapTrackSectorToFileIndex(currentTrack, loc, out int position)) {
                Array.Clear(rawSectorBuffer, 0, rawSectorBuffer.Length);
                return rawSectorBuffer;
            }

            Stream currentStream = streams[currentTrack.number - 1];
            currentStream.Seek(position * BYTES_PER_SECTOR_RAW, SeekOrigin.Begin);
            currentStream.Read(rawSectorBuffer, 0, rawSectorBuffer.Length);
            return rawSectorBuffer;
        }

        public SubchannelQ GetSubchannelQ(int loc) {
            Track currentTrack = getTrackFromLoc(loc);
            if (TryReadSubchannelQ(loc, currentTrack, out SubchannelQ subQ)) {
                return subQ;
            }

            return SynthesizeSubchannelQ(currentTrack, loc);
        }

        public Track getTrackFromLoc(int loc) {
            foreach (Track track in tracks) {
                isTrackChange = loc == track.lbaEnd;
                //Console.WriteLine(loc + " " + track.number + " " + track.lbaEnd + " " + isTrackChange);
                if (track.lbaEnd >= loc) return track;
            }
            if (Verbose)
                Console.WriteLine("[CD] WARNING: LBA beyond tracks!");
            return tracks[0]; //and explode ¯\_(ツ)_/¯ 
        }

        public int getLBA() {
            int lba = 150;

            foreach (Track track in tracks) {
                lba += track.lba;
            }
            if (Verbose)
                Console.WriteLine($"[CD] LBA: {lba:x8}");
            return lba;
        }

        public bool isAudioCD() {
            return tracks[0].isAudio;
        }

        private bool TryReadSubchannelQ(int loc, Track currentTrack, out SubchannelQ subQ) {
            if (TryReadDiscOverrideSubchannelQ(loc, out subQ)) {
                return true;
            }

            if (TryReadDiscSubchannelQ(loc, out subQ)) {
                return true;
            }

            if (subStreams.TryGetValue(currentTrack.file, out Stream? trackSubStream)
                && subSectorCounts.TryGetValue(currentTrack.file, out long trackSubSectors)
                && TryMapTrackSectorToFileIndex(currentTrack, loc, out int sectorIndex)
                && sectorIndex >= 0
                && sectorIndex < trackSubSectors) {
                return TryReadSubchannelQFrame(trackSubStream, sectorIndex, out subQ);
            }

            subQ = default;
            return false;
        }

        private bool TryReadDiscOverrideSubchannelQ(int loc, out SubchannelQ subQ) {
            return discSubchannelOverrides.TryGetValue(loc, out subQ);
        }

        private bool TryReadDiscSubchannelQ(int loc, out SubchannelQ subQ) {
            int sectorIndex = loc - DISC_FIRST_LBA;
            if (discSubStream is not null && sectorIndex >= 0 && sectorIndex < discSubSectors) {
                return TryReadSubchannelQFrame(discSubStream, sectorIndex, out subQ);
            }

            subQ = default;
            return false;
        }

        private static bool TryMapTrackSectorToFileIndex(Track currentTrack, int loc, out int sectorIndex) {
            sectorIndex = -1;
            if (loc > currentTrack.lbaEnd) {
                return false;
            }

            if (currentTrack.hasEmbeddedPregap) {
                int trackDiscStart = currentTrack.lbaPregapStart;
                if (loc >= trackDiscStart) {
                    sectorIndex = currentTrack.filePregapStartSector + (loc - trackDiscStart);
                    return sectorIndex >= 0;
                }
            }

            if (loc < currentTrack.lbaStart) {
                return false;
            }

            sectorIndex = currentTrack.fileStartSector + (loc - currentTrack.lbaStart);
            return sectorIndex >= 0;
        }

        private bool TryReadSubchannelQFrame(Stream stream, int sectorIndex, out SubchannelQ subQ) {
            Span<byte> subFrame = stackalloc byte[BYTES_PER_SUBCHANNEL_FRAME];
            stream.Seek((long)sectorIndex * BYTES_PER_SUBCHANNEL_FRAME, SeekOrigin.Begin);
            int read = stream.Read(subFrame);
            if (read != subFrame.Length) {
                subQ = default;
                return false;
            }

            subQ = SubchannelQ.FromCloneCdSub(subFrame);
            return true;
        }

        private void TryLoadSubchannels(string diskFilename, string? subchannelOverridePath) {
            TryLoadDiscWideSubchannel(diskFilename);
            if (!TryLoadDiscWideSubchannelOverrideFile(subchannelOverridePath))
                TryLoadDiscWideSubchannelOverrides(diskFilename);

            foreach (Track track in tracks) {
                if (subStreams.ContainsKey(track.file)) {
                    continue;
                }

                string subPath = Path.ChangeExtension(track.file, ".sub");
                if (!TryOpenSubchannelFile(subPath, out Stream? stream, out long sectors)) {
                    continue;
                }

                subStreams[track.file] = stream;
                subSectorCounts[track.file] = sectors;
                if (Verbose) {
                    Console.WriteLine($"[CD] Loaded track subchannel sidecar: {subPath} sectors={sectors}");
                }
            }
        }

        private void TryLoadDiscWideSubchannelOverrides(string diskFilename) {
            if (TryLoadDiscWideSubchannelOverrideFile(Path.ChangeExtension(diskFilename, ".sbi")))
                return;
            TryLoadDiscWideSubchannelOverrideFile(Path.ChangeExtension(diskFilename, ".lsd"));
        }

        private void TryLoadDiscWideSubchannel(string diskFilename) {
            string subPath = Path.ChangeExtension(diskFilename, ".sub");
            if (!TryOpenSubchannelFile(subPath, out Stream? stream, out long sectors)) {
                return;
            }

            discSubStream = stream;
            discSubSectors = sectors;
            if (Verbose) {
                Console.WriteLine($"[CD] Loaded disc subchannel sidecar: {subPath} sectors={sectors}");
            }
        }

        private bool TryLoadDiscWideSubchannelOverrideFile(string? path) {
            if (string.IsNullOrWhiteSpace(path) || !VirtualFileSystem.Exists(path)) {
                return false;
            }

            bool isSbi = Path.GetExtension(path).Equals(".sbi", StringComparison.OrdinalIgnoreCase);
            bool isLsd = Path.GetExtension(path).Equals(".lsd", StringComparison.OrdinalIgnoreCase);
            if (!isSbi && !isLsd) {
                return false;
            }
            try {
                byte[] data = VirtualFileSystem.ReadAllBytes(path);
                int countBefore = discSubchannelOverrides.Count;
                bool loaded = isSbi
                    ? TryParseSbiSubchannelOverrides(data, discSubchannelOverrides)
                    : TryParseLsdSubchannelOverrides(data, discSubchannelOverrides);
                if (loaded && Verbose) {
                    Console.WriteLine(
                        $"[CD] Loaded disc subchannel {(isSbi ? "SBI" : "LSD")} sidecar: {path} entries={discSubchannelOverrides.Count - countBefore}");
                }
                return loaded;
            } catch (Exception ex) {
                if (Verbose) {
                    Console.WriteLine($"[CD] Failed loading {(isSbi ? "SBI" : "LSD")} sidecar '{path}': {ex.Message}");
                }
                return false;
            }
        }

        private static bool TryOpenSubchannelFile(string subPath, out Stream? stream, out long sectors) {
            stream = null;
            sectors = 0;
            if (!VirtualFileSystem.Exists(subPath)) {
                return false;
            }

            try {
                stream = VirtualFileSystem.OpenRead(subPath);
                if (stream.Length % BYTES_PER_SUBCHANNEL_FRAME != 0) {
                    stream.Dispose();
                    stream = null;
                    return false;
                }

                sectors = stream.Length / BYTES_PER_SUBCHANNEL_FRAME;
                return true;
            } catch {
                stream?.Dispose();
                stream = null;
                sectors = 0;
                return false;
            }
        }

        private static bool TryParseSbiSubchannelOverrides(byte[] data, Dictionary<int, SubchannelQ> output) {
            if (data.Length < 4 || data[0] != (byte)'S' || data[1] != (byte)'B' || data[2] != (byte)'I' || data[3] != 0) {
                return false;
            }

            const int recordSize = 14;
            int payloadBytes = data.Length - 4;
            if ((payloadBytes % recordSize) != 0) {
                return false;
            }

            for (int offset = 4; offset < data.Length; offset += recordSize) {
                TryAddSubchannelOverride(data.AsSpan(offset, recordSize), hasCrc: false, output);
            }

            return true;
        }

        private static bool TryParseLsdSubchannelOverrides(byte[] data, Dictionary<int, SubchannelQ> output) {
            const int recordSize = 15;
            if (data.Length == 0 || (data.Length % recordSize) != 0) {
                return false;
            }

            for (int offset = 0; offset < data.Length; offset += recordSize) {
                TryAddSubchannelOverride(data.AsSpan(offset, recordSize), hasCrc: true, output);
            }

            return true;
        }

        private static void TryAddSubchannelOverride(ReadOnlySpan<byte> entry, bool hasCrc, Dictionary<int, SubchannelQ> output) {
            int expectedLength = hasCrc ? 15 : 14;
            if (entry.Length != expectedLength) {
                return;
            }

            int mm = BcdToDec(entry[0]);
            int ss = BcdToDec(entry[1]);
            int ff = BcdToDec(entry[2]);
            if (ss >= 60 || ff >= 75) {
                return;
            }

            int qOffset = hasCrc ? 3 : 4;
            int loc = (mm * 60 * 75) + (ss * 75) + ff;
            ReadOnlySpan<byte> qData = entry.Slice(qOffset, 10);
            bool hasValidCrc = false;
            if (hasCrc) {
                ushort actualCrc = (ushort)(entry[qOffset + 10] | (entry[qOffset + 11] << 8));
                hasValidCrc = actualCrc == SubchannelQ.ComputeCrc(qData);
            }

            output[loc] = new SubchannelQ(
                entry[qOffset + 0],
                entry[qOffset + 1],
                entry[qOffset + 2],
                entry[qOffset + 3],
                entry[qOffset + 4],
                entry[qOffset + 5],
                entry[qOffset + 6],
                entry[qOffset + 7],
                entry[qOffset + 8],
                entry[qOffset + 9],
                hasValidCrc);
        }

        private static SubchannelQ SynthesizeSubchannelQ(Track track, int loc) {
            bool inPregap = loc < track.lbaStart;
            int relativeLba = inPregap
                ? Math.Abs(track.lbaStart - loc)
                : loc - track.lbaStart;

            (byte mm, byte ss, byte ff) = GetMsfFromLba(relativeLba);
            (byte amm, byte ass, byte aff) = GetMsfFromLba(Math.Max(0, loc));
            byte controlAdr = track.isAudio ? (byte)0x01 : (byte)0x41;

            return new SubchannelQ(
                controlAdr,
                DecToBcd((byte)track.number),
                DecToBcd((byte)(inPregap ? 0 : 1)),
                DecToBcd(mm),
                DecToBcd(ss),
                DecToBcd(ff),
                0,
                DecToBcd(amm),
                DecToBcd(ass),
                DecToBcd(aff));
        }

        private static byte DecToBcd(byte value) {
            return (byte)(value + 6 * (value / 10));
        }

        private static int BcdToDec(byte value) {
            return ((value >> 4) * 10) + (value & 0x0F);
        }

        private static (byte mm, byte ss, byte ff) GetMsfFromLba(int lba) {
            int ff = lba % 75;
            lba /= 75;

            int ss = lba % 60;
            lba /= 60;

            int mm = lba;
            return ((byte)mm, (byte)ss, (byte)ff);
        }

    }
}
