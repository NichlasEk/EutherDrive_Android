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

        public List<Track> tracks;

        public bool isTrackChange;

        public CD(string diskFilename) {
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

            TryLoadSubchannels(diskFilename);
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

        private void TryLoadSubchannels(string diskFilename) {
            TryLoadDiscWideSubchannel(diskFilename);

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
