using System.IO;
using System;
using System.Collections.Generic;
using ProjectPSX.IO;
using static ProjectPSX.Devices.CdRom.TrackBuilder;

namespace ProjectPSX.Devices.CdRom {
    public class CD {

        private const int BYTES_PER_SECTOR_RAW = 2352;
        private static readonly bool Verbose = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE") == "1";

        private byte[] rawSectorBuffer = new byte[BYTES_PER_SECTOR_RAW];

        private Stream[] streams;

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
        }

        public byte[] Read(int loc) {

            Track currentTrack = getTrackFromLoc(loc);

            if (loc < currentTrack.lbaStart) {
                Array.Clear(rawSectorBuffer, 0, rawSectorBuffer.Length);
                return rawSectorBuffer;
            }

            //Console.WriteLine("Loc: " + loc + " TrackLbaStart: " + currentTrack.lbaStart);
            //Console.WriteLine("readPos = " + (loc - currentTrack.lbaStart));

            int position = currentTrack.fileStartSector + (loc - currentTrack.lbaStart);
            if (position < 0) position = 0;

            Stream currentStream = streams[currentTrack.number - 1];
            currentStream.Seek(position * BYTES_PER_SECTOR_RAW, SeekOrigin.Begin);
            currentStream.Read(rawSectorBuffer, 0, rawSectorBuffer.Length);
            return rawSectorBuffer;
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

    }
}
