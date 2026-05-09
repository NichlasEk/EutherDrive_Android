using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade;

public sealed class McsArcadeAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private const string SavestateMagic = "MCSARC";
    private const int SavestateVersion = 2;
    private const int PlaceholderWidth = 256;
    private const int PlaceholderHeight = 224;
    private const int PlaceholderStride = PlaceholderWidth * 4;
    private const int AudioOutputDivisor = 8;
    private static readonly int OutputSampleRate = ParseOutputSampleRate();
    private const int OutputChannels = 2;
    private static readonly int MaxQueuedAudioSamples = OutputSampleRate * OutputChannels * 2;
    private static readonly bool TraceMcsProfile =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_MCS_PROFILE") == "1";
    private static readonly bool TraceMcsInput =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_MCS_INPUT_TRACE") == "1";
    private static readonly object McsInitLock = new();
    private static readonly McsHostCore HostCore = new();
    private static readonly McsHostFileSystem HostFileSystem = new();
    private static readonly McsHostDirectorySystem HostDirectorySystem = new();
    private static readonly McsHostLibrary HostLibrary = new();
    private static readonly string McsDataRoot = CreateMcsDataRoot();
    private static bool _mcsInitialized;

    private static readonly HashSet<string> ConsoleArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".32x", ".md", ".gen", ".smd",
        ".sms", ".sg", ".gg",
        ".iso", ".cue", ".chd",
        ".z64", ".n64", ".v64",
        ".gba", ".agb",
        ".gb", ".gbc",
        ".smc", ".sfc",
        ".nes",
        ".pce"
    };

    private readonly object _sync = new();

    private byte[] _frameBuffer = new byte[PlaceholderHeight * PlaceholderStride];
    private short[] _audioBuffer = Array.Empty<short>();
    private readonly List<short> _audioQueue = new();
    private int _frameWidth = PlaceholderWidth;
    private int _frameHeight = PlaceholderHeight;
    private int _frameStride = PlaceholderStride;

    private string? _driverName;
    private string? _romPath;
    private string? _romDirectory;
    private McsRuntime? _runtime;
    private ArcadeInputState _inputState;
    private int _masterVolumePercent = 50;

    internal static void EnsureMcsInitialized()
    {
        lock (McsInitLock)
        {
            mame.osdcore_global.set_osdcore(HostCore);
            mame.osdfile_global.set_osdfile(HostFileSystem);
            mame.osdfile_global.set_osddirectory(HostDirectorySystem);
            mame.osdlib_global.set_osdlib(HostLibrary);

            if (_mcsInitialized)
                return;

            mame.drivlist_global_generated.init();
            mame.object_finder_operations_global.init();
            mame.samples_device_enumerator_helper_samples.init();
            mame.cassette_device_enumerator_helper_cassette.init();
            mame.nld_sound_in_helper_global.init();
            _mcsInitialized = true;
        }
    }

    public static bool IsLikelyArcadeArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string driverName = GetDriverNameFromPath(path);
        if (McsDriverCatalog.Contains(driverName))
            return true;

        try
        {
            using IArchive archive = ArchiveFactory.Open(path);
            int fileCount = 0;
            foreach (IArchiveEntry entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    continue;

                fileCount++;
                string ext = Path.GetExtension(entry.Key);
                if (ConsoleArchiveExtensions.Contains(ext))
                    return false;
            }

            return fileCount > 1;
        }
        catch
        {
            return false;
        }
    }

    public static string GetArchiveDriverName(string path) => GetDriverNameFromPath(path);

    public static bool IsDriverAvailableForArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        return McsDriverCatalog.Contains(GetDriverNameFromPath(path));
    }

    private static int ParseOutputSampleRate()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_OUTPUT_HZ");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value >= 22_050
            && value <= 192_000)
        {
            return value;
        }

        return 44_100;
    }

    private static string CreateMcsDataRoot()
    {
        string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = Path.Combine(Path.GetTempPath(), "EutherDrive");

        string root = Path.Combine(baseDirectory, "mcs");
        Directory.CreateDirectory(root);
        return root;
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Arcade ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Arcade ROM archive not found.", path);

        StopRuntime();

        _driverName = GetDriverNameFromPath(path);
        _romPath = Path.GetFullPath(path);
        DrawPlaceholderFrame();

        if (!McsDriverCatalog.TryFind(_driverName, out McsDriverInfo? driver))
        {
            string examples = string.Join(", ", McsDriverCatalog.DriverNames.Take(12));
            throw new NotSupportedException(
                $"MCS arcade core is installed, but this MCS snapshot does not contain driver '{_driverName}'. " +
                "This arcade set needs its MAME driver ported or included in MCS before it can boot. " +
                $"Available MCS examples: {examples}.");
        }

        McsDriverInfo presentDriver = driver!;
        string romDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
        _romDirectory = romDirectory;
        _runtime = new McsRuntime(this, presentDriver.Name, romDirectory);
        _runtime.Start();
    }

    public void Reset()
    {
        string? driverName = _driverName;
        string? romDirectory = _romDirectory;
        if (string.IsNullOrWhiteSpace(driverName) || string.IsNullOrWhiteSpace(romDirectory))
            return;

        StopRuntime();
        DrawPlaceholderFrame();
        _runtime = new McsRuntime(this, driverName, romDirectory);
        _runtime.Start();
    }

    public RomIdentity? RomIdentity
    {
        get
        {
            string? romPath = _romPath;
            string? driverName = _driverName;
            if (string.IsNullOrWhiteSpace(romPath) || string.IsNullOrWhiteSpace(driverName) || !File.Exists(romPath))
                return null;

            using FileStream stream = File.OpenRead(romPath);
            return new RomIdentity(
                driverName,
                RomIdentity.ComputeSha256(stream),
                PersistentStoragePath.ResolveSavestateDirectory(romPath, "mcs"));
        }
    }

    public long? FrameCounter => _runtime?.PublishedFrames;

    public void SaveState(BinaryWriter writer)
    {
        McsRuntime runtime = _runtime ?? throw new InvalidOperationException("MCS runtime is not running.");
        writer.Write(SavestateMagic);
        writer.Write(SavestateVersion);
        writer.Write(_driverName ?? "");

        using var payloadStream = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, leaveOpen: true))
            runtime.SaveState(payloadWriter);
        byte[] mcsPayload = payloadStream.ToArray();
        writer.Write(mcsPayload.Length);
        writer.Write(mcsPayload);

        lock (_sync)
        {
            int frameLength = Math.Min(_frameBuffer.Length, _frameHeight * _frameStride);
            writer.Write(_frameWidth);
            writer.Write(_frameHeight);
            writer.Write(_frameStride);
            writer.Write(frameLength);
            writer.Write(_frameBuffer, 0, frameLength);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        string magic = reader.ReadString();
        if (!string.Equals(magic, SavestateMagic, StringComparison.Ordinal))
            throw new InvalidDataException("MCS savestate magic mismatch.");

        int version = reader.ReadInt32();
        if (version != 1 && version != SavestateVersion)
            throw new InvalidDataException($"MCS savestate version mismatch: {version}.");

        string driverName = reader.ReadString();
        if (!string.Equals(driverName, _driverName, StringComparison.Ordinal))
            throw new InvalidDataException($"MCS savestate is for '{driverName}', current driver is '{_driverName}'.");

        string? romDirectory = _romDirectory;
        if (string.IsNullOrWhiteSpace(romDirectory))
            throw new InvalidOperationException("MCS ROM is not loaded.");

        byte[] payload;
        byte[]? frameBuffer = null;
        int frameWidth = 0;
        int frameHeight = 0;
        int frameStride = 0;

        if (version == 1)
        {
            using var payloadStream = new MemoryStream();
            reader.BaseStream.CopyTo(payloadStream);
            payload = payloadStream.ToArray();
        }
        else
        {
            int payloadLength = reader.ReadInt32();
            if (payloadLength < 0)
                throw new InvalidDataException("MCS savestate payload length is invalid.");
            payload = reader.ReadBytes(payloadLength);
            if (payload.Length != payloadLength)
                throw new EndOfStreamException("MCS savestate payload is truncated.");

            frameWidth = reader.ReadInt32();
            frameHeight = reader.ReadInt32();
            frameStride = reader.ReadInt32();
            int frameLength = reader.ReadInt32();
            if (frameWidth <= 0 || frameHeight <= 0 || frameStride <= 0 || frameLength < 0 || frameLength > frameHeight * frameStride)
                throw new InvalidDataException("MCS savestate framebuffer metadata is invalid.");
            frameBuffer = reader.ReadBytes(frameLength);
            if (frameBuffer.Length != frameLength)
                throw new EndOfStreamException("MCS savestate framebuffer is truncated.");
        }

        McsRuntime runtime = _runtime ?? throw new InvalidOperationException("MCS runtime is not running.");
        using var stateStream = new MemoryStream(payload, writable: false);
        using var stateReader = new BinaryReader(stateStream, System.Text.Encoding.UTF8, leaveOpen: false);
        runtime.LoadState(stateReader);

        if (frameBuffer != null)
        {
            lock (_sync)
            {
                _frameWidth = frameWidth;
                _frameHeight = frameHeight;
                _frameStride = frameStride;
                if (_frameBuffer.Length != frameBuffer.Length)
                    _frameBuffer = new byte[frameBuffer.Length];
                Buffer.BlockCopy(frameBuffer, 0, _frameBuffer, 0, frameBuffer.Length);
            }
        }
    }

    public void RunFrame()
    {
        McsRuntime? runtime = _runtime;
        if (runtime == null)
            return;

        runtime.WaitForNextFrame(TimeSpan.FromMilliseconds(250));
        runtime.ThrowIfFaulted();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        lock (_sync)
        {
            width = _frameWidth;
            height = _frameHeight;
            stride = _frameStride;
            return _frameBuffer.AsSpan(0, Math.Min(_frameBuffer.Length, _frameHeight * _frameStride));
        }
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = OutputChannels;
        lock (_sync)
        {
            if (_audioQueue.Count == 0)
                return ReadOnlySpan<short>.Empty;

            if (_audioBuffer.Length != _audioQueue.Count)
                _audioBuffer = new short[_audioQueue.Count];
            _audioQueue.CopyTo(_audioBuffer);
            _audioQueue.Clear();
            return _audioBuffer;
        }
    }

    public void SetMasterVolumePercent(int percent)
    {
        lock (_sync)
            _masterVolumePercent = Math.Clamp(percent, 0, 100);
    }

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        lock (_sync)
        {
            _inputState = new ArcadeInputState(
                up,
                down,
                left,
                right,
                a,
                b,
                c,
                start,
                x,
                y,
                z,
                mode);
        }
    }

    public void Dispose()
    {
        StopRuntime();
    }

    private static string GetDriverNameFromPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Trim().ToLowerInvariant();
    }

    private void DrawPlaceholderFrame()
    {
        lock (_sync)
        {
            _frameWidth = PlaceholderWidth;
            _frameHeight = PlaceholderHeight;
            _frameStride = PlaceholderStride;
            if (_frameBuffer.Length != PlaceholderHeight * PlaceholderStride)
                _frameBuffer = new byte[PlaceholderHeight * PlaceholderStride];

            for (int y = 0; y < PlaceholderHeight; y++)
            {
                for (int x = 0; x < PlaceholderWidth; x++)
                {
                    int index = y * PlaceholderStride + x * 4;
                    byte shade = (byte)(16 + ((x ^ y) & 0x0F));
                    _frameBuffer[index + 0] = shade;
                    _frameBuffer[index + 1] = shade;
                    _frameBuffer[index + 2] = shade;
                    _frameBuffer[index + 3] = 0xFF;
                }
            }
        }
    }

    private void StopRuntime()
    {
        McsRuntime? runtime = _runtime;
        _runtime = null;
        runtime?.Dispose();
        ClearAudio();
    }

    private void ClearAudio()
    {
        lock (_sync)
        {
            _audioQueue.Clear();
            _audioBuffer = Array.Empty<short>();
        }
    }

    private ArcadeInputState SnapshotInput()
    {
        lock (_sync)
            return _inputState;
    }

    private void PublishFrame(mame.PointerU32 pixels, int width, int height, int rowPixels)
    {
        if (width <= 0 || height <= 0 || rowPixels < width)
            return;

        int stride = width * 4;
        int required = checked(height * stride);

        lock (_sync)
        {
            _frameWidth = width;
            _frameHeight = height;
            _frameStride = stride;
            if (_frameBuffer.Length != required)
                _frameBuffer = new byte[required];

            byte[] source = pixels.Buffer.data_raw;
            int sourceBase = pixels.Offset;
            int sourceRowBytes = rowPixels * 4;
            int copyBytes = width * 4;

            for (int y = 0; y < height; y++)
            {
                int src = sourceBase + y * sourceRowBytes;
                int dst = y * stride;
                Buffer.BlockCopy(source, src, _frameBuffer, dst, copyBytes);
            }
        }
    }

    private void PublishAudio(mame.Pointer<short> samples, int sampleFrames)
    {
        int sampleCount = sampleFrames * OutputChannels;
        if (sampleCount <= 0)
            return;

        lock (_sync)
        {
            int overflow = _audioQueue.Count + sampleCount - MaxQueuedAudioSamples;
            if (overflow > 0)
                _audioQueue.RemoveRange(0, Math.Min(overflow, _audioQueue.Count));

            if (_audioQueue.Capacity < _audioQueue.Count + sampleCount)
                _audioQueue.Capacity = _audioQueue.Count + sampleCount;

            int volume = _masterVolumePercent;
            for (int i = 0; i < sampleCount; i++)
            {
                int scaled = samples[i] * volume / (AudioOutputDivisor * 100);
                _audioQueue.Add((short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
            }
        }
    }

    private readonly record struct ArcadeInputState(
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool Button1,
        bool Button2,
        bool Button3,
        bool Start,
        bool Button4,
        bool Button5,
        bool Button6,
        bool Coin);

    private sealed class McsRuntime : IDisposable
    {
        private readonly McsArcadeAdapter _owner;
        private readonly string _driverName;
        private readonly string _romDirectory;
        private readonly ManualResetEventSlim _firstFrame = new(false);
        private readonly AutoResetEvent _frameReady = new(false);
        private readonly object _frameSync = new();
        private readonly ManualResetEventSlim _stopped = new(false);
        private readonly object _stateRequestSync = new();
        private readonly object _frameGateSync = new();
        private readonly ManualResetEventSlim _frameGateChanged = new(false);
        private readonly McsOsd _osd;
        private readonly Thread _thread;
        private StateRequest? _pendingStateRequest;
        private int _frameAdvancePermits;
        private bool _shutdownFrameGate;
        private volatile Exception? _fault;
        private long _profileLastTicks = Stopwatch.GetTimestamp();
        private long _profileFrames;
        private long _profileWaitTicks;
        private long _profileUpdateTicks;
        private long _profileDrawTicks;
        private long _profilePublishTicks;
        private long _profileAudioTicks;
        private long _profileInputTicks;
        private long _profileAudioCallbacks;

        public McsRuntime(McsArcadeAdapter owner, string driverName, string romDirectory)
        {
            _owner = owner;
            _driverName = driverName;
            _romDirectory = romDirectory;
            _osd = new McsOsd(this, owner);
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"EutherDrive MCS {_driverName}"
            };
        }

        public void Start()
        {
            _thread.Start();

            TimeSpan timeout = TimeSpan.FromSeconds(10);
            if (!_firstFrame.Wait(timeout))
            {
                if (_stopped.IsSet)
                    ThrowIfFaulted();

                Dispose();
                throw new TimeoutException($"MCS driver '{_driverName}' did not produce a frame within {timeout.TotalSeconds:0} seconds.");
            }

            ThrowIfFaulted();
        }

        public void Dispose()
        {
            _osd.ScheduleExit();
            ReleaseFrameGateForShutdown();
            TimeSpan timeout = TimeSpan.FromSeconds(10);
            bool stopped = _stopped.Wait(timeout);
            if (!stopped)
            {
                var timeoutException = new TimeoutException($"MCS driver '{_driverName}' did not stop within {timeout.TotalSeconds:0} seconds.");
                _fault ??= timeoutException;
                Console.Error.WriteLine($"[MCS] {timeoutException.Message}");
                return;
            }

            _firstFrame.Dispose();
            _frameReady.Dispose();
            _stopped.Dispose();
            _frameGateChanged.Dispose();
        }

        public void MarkFrameReady()
        {
            lock (_frameSync)
                PublishedFrames++;
        }

        public long PublishedFrames { get; private set; }

        public void WaitForNextFrame(TimeSpan timeout)
        {
            long waitStart = TraceMcsProfile ? Stopwatch.GetTimestamp() : 0;
            long start;
            lock (_frameSync)
                start = PublishedFrames;

            RequestFrameAdvance();

            if (start == 0 || _stopped.IsSet)
            {
                AddProfileWait(waitStart);
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (!_stopped.IsSet)
            {
                lock (_frameSync)
                {
                    if (PublishedFrames != start)
                    {
                        AddProfileWait(waitStart);
                        return;
                    }
                }

                TimeSpan remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    AddProfileWait(waitStart);
                    return;
                }

                _frameReady.WaitOne(remaining);
            }

            AddProfileWait(waitStart);
        }

        public void ThrowIfFaulted()
        {
            Exception? fault = _fault;
            if (fault != null)
                throw new InvalidOperationException($"MCS driver '{_driverName}' failed.", fault);
        }

        public void ScheduleReset() => _osd.ScheduleReset();

        public void SaveState(BinaryWriter writer)
        {
            byte[]? payload = null;
            EnqueueStateRequest(new StateRequest(machine =>
            {
                using var stream = new MemoryStream();
                using (var stateWriter = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                    machine.save().write_stream(stateWriter);
                payload = stream.ToArray();
            }));

            writer.Write(payload ?? Array.Empty<byte>());
        }

        public void LoadState(BinaryReader reader)
        {
            using var payloadStream = new MemoryStream();
            reader.BaseStream.CopyTo(payloadStream);
            byte[] payload = payloadStream.ToArray();

            EnqueueStateRequest(new StateRequest(machine =>
            {
                using var stream = new MemoryStream(payload, writable: false);
                using var stateReader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
                machine.save().read_stream(stateReader);
            }));
        }

        private void EnqueueStateRequest(StateRequest request)
        {
            lock (_stateRequestSync)
            {
                if (_pendingStateRequest != null)
                    throw new InvalidOperationException("A MCS savestate operation is already pending.");

                _pendingStateRequest = request;
            }

            _frameGateChanged.Set();

            while (!request.Done.Wait(TimeSpan.FromMilliseconds(50)))
            {
                if (_stopped.IsSet)
                {
                    ThrowIfFaulted();
                    throw new InvalidOperationException("MCS runtime stopped before savestate operation completed.");
                }
            }

            if (request.Error != null)
                throw new InvalidOperationException("MCS savestate operation failed.", request.Error);
        }

        public void ProcessMachineUpdate(mame.running_machine machine)
        {
            StateRequest? request = ProcessOneStateRequest(machine);
            if (request != null)
            {
                ClearFrameAdvancePermits();
                request.Done.Set();
                WaitForFrameAdvance(machine, processStateRequests: true);
            }
        }

        public void ProcessFrameBoundaryStateRequest(mame.running_machine machine)
        {
            WaitForFrameAdvance(machine, processStateRequests: false);
        }

        private StateRequest? ProcessOneStateRequest(mame.running_machine machine)
        {
            if (!machine.scheduler().can_save())
                return null;

            StateRequest? request;
            lock (_stateRequestSync)
            {
                request = _pendingStateRequest;
                _pendingStateRequest = null;
            }

            if (request == null)
                return null;

            try
            {
                request.Execute(machine);
            }
            catch (Exception ex)
            {
                request.Error = ex;
                request.Done.Set();
            }

            return request;
        }

        private bool HasPendingStateRequest()
        {
            lock (_stateRequestSync)
                return _pendingStateRequest != null;
        }

        private void RequestFrameAdvance()
        {
            lock (_frameGateSync)
            {
                _frameAdvancePermits++;
                _frameGateChanged.Set();
            }
        }

        private void ReleaseFrameGateForShutdown()
        {
            lock (_frameGateSync)
            {
                _shutdownFrameGate = true;
                _frameGateChanged.Set();
            }
        }

        private void ClearFrameAdvancePermits()
        {
            lock (_frameGateSync)
            {
                _frameAdvancePermits = 0;
                _frameGateChanged.Reset();
            }
        }

        private void WaitForFrameAdvance(mame.running_machine machine, bool processStateRequests)
        {
            while (!_stopped.IsSet)
            {
                if (processStateRequests)
                {
                    StateRequest? request = ProcessOneStateRequest(machine);
                    if (request != null)
                    {
                        ClearFrameAdvancePermits();
                        request.Done.Set();
                        continue;
                    }
                }

                bool hasPendingStateRequest = HasPendingStateRequest();

                lock (_frameGateSync)
                {
                    if (_shutdownFrameGate)
                        return;

                    if (!processStateRequests && hasPendingStateRequest)
                        return;

                    if (_frameAdvancePermits > 0)
                    {
                        _frameAdvancePermits--;
                        if (_frameAdvancePermits == 0)
                            _frameGateChanged.Reset();
                        return;
                    }
                }

                SignalFrameParked();
                _frameGateChanged.Wait(TimeSpan.FromMilliseconds(10));
            }
        }

        private void SignalFrameParked()
        {
            _firstFrame.Set();
            _frameReady.Set();
        }

        private static mame.running_machine CurrentMachine()
        {
            mame.mame_machine_manager manager = mame.mame_machine_manager.instance()
                ?? throw new InvalidOperationException("MCS machine manager is not running.");
            return manager.machine()
                ?? throw new InvalidOperationException("MCS running machine is not available.");
        }

        private void AddProfileWait(long startTicks)
        {
            if (TraceMcsProfile && startTicks != 0)
                _profileWaitTicks += Stopwatch.GetTimestamp() - startTicks;
        }

        public void AddProfileFrame(long updateTicks, long drawTicks, long publishTicks)
        {
            if (!TraceMcsProfile)
                return;

            _profileFrames++;
            _profileUpdateTicks += updateTicks;
            _profileDrawTicks += drawTicks;
            _profilePublishTicks += publishTicks;
            MaybeReportProfile();
        }

        public void AddProfileAudio(long ticks)
        {
            if (!TraceMcsProfile)
                return;

            _profileAudioTicks += ticks;
            _profileAudioCallbacks++;
        }

        public void AddProfileInput(long ticks)
        {
            if (TraceMcsProfile)
                _profileInputTicks += ticks;
        }

        private void MaybeReportProfile()
        {
            long now = Stopwatch.GetTimestamp();
            long elapsedTicks = now - _profileLastTicks;
            if (elapsedTicks < Stopwatch.Frequency)
                return;

            double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
            double scale = 1000.0 / Stopwatch.Frequency;
            long frames = _profileFrames;
            Console.WriteLine(
                $"[MCS-PROFILE] driver={_driverName} fps={frames / elapsedSeconds:0.0} " +
                $"wait_ms={_profileWaitTicks * scale:0.0} update_ms={_profileUpdateTicks * scale:0.0} " +
                $"draw_ms={_profileDrawTicks * scale:0.0} publish_ms={_profilePublishTicks * scale:0.0} " +
                $"audio_ms={_profileAudioTicks * scale:0.0}/{_profileAudioCallbacks} input_ms={_profileInputTicks * scale:0.0}");

            _profileLastTicks = now;
            _profileFrames = 0;
            _profileWaitTicks = 0;
            _profileUpdateTicks = 0;
            _profileDrawTicks = 0;
            _profilePublishTicks = 0;
            _profileAudioTicks = 0;
            _profileAudioCallbacks = 0;
            _profileInputTicks = 0;
        }

        private void Run()
        {
            try
            {
                EnsureMcsInitialized();
                mame.mame_machine_manager.close_instance();

                var options = new mame.osd_options();
                string cfgDirectory = EnsureMcsDirectory("cfg");
                string nvramDirectory = EnsureMcsDirectory("nvram");
                string inputDirectory = EnsureMcsDirectory("inp");
                string stateDirectory = EnsureMcsDirectory("sta");
                string snapshotDirectory = EnsureMcsDirectory("snap");
                string diffDirectory = EnsureMcsDirectory("diff");
                string commentDirectory = EnsureMcsDirectory("comments");
                string shareDirectory = EnsureMcsDirectory("share");
                var args = new mame.std.vector<string>(new[]
                {
                    "eutherdrive-mcs",
                    _driverName,
                    "-rompath",
                    _romDirectory,
                    "-homepath",
                    McsDataRoot,
                    "-cfg_directory",
                    cfgDirectory,
                    "-nvram_directory",
                    nvramDirectory,
                    "-input_directory",
                    inputDirectory,
                    "-state_directory",
                    stateDirectory,
                    "-snapshot_directory",
                    snapshotDirectory,
                    "-diff_directory",
                    diffDirectory,
                    "-comment_directory",
                    commentDirectory,
                    "-share_directory",
                    shareDirectory,
                    "-noreadconfig",
                    "-nowriteconfig",
                    "-samplerate",
                    OutputSampleRate.ToString(CultureInfo.InvariantCulture),
                    "-skip_gameinfo",
                    "-ui",
                    "simple"
                });

                int result = mame.emulator_info.start_frontend(options, _osd, args);
                if (result != mame.main_global.EMU_ERR_NONE)
                    _fault = new InvalidOperationException($"MCS exited with code {result} ({DescribeMcsExitCode(result)}) while running '{_driverName}'.");
            }
            catch (Exception ex)
            {
                _fault = ex;
            }
            finally
            {
                _firstFrame.Set();
                _stopped.Set();
                mame.mame_machine_manager.close_instance();
            }
        }

        private sealed class StateRequest
        {
            public StateRequest(Action<mame.running_machine> execute)
            {
                Execute = execute;
            }

            public Action<mame.running_machine> Execute { get; }
            public ManualResetEventSlim Done { get; } = new(false);
            public Exception? Error { get; set; }
        }

        private static string EnsureMcsDirectory(string name)
        {
            string path = Path.Combine(McsDataRoot, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string DescribeMcsExitCode(int result)
        {
            return result switch
            {
                mame.main_global.EMU_ERR_FAILED_VALIDITY => "failed validity checks",
                mame.main_global.EMU_ERR_MISSING_FILES => "missing ROM or sample files",
                mame.main_global.EMU_ERR_FATALERROR => "fatal emulator error",
                mame.main_global.EMU_ERR_DEVICE => "device initialization error",
                mame.main_global.EMU_ERR_NO_SUCH_SYSTEM => "unknown MAME system",
                mame.main_global.EMU_ERR_INVALID_CONFIG => "invalid MAME configuration",
                _ => "unknown error"
            };
        }
    }

    private sealed class McsHostCore : mame.osdcore_interface
    {
        public override ulong osd_ticks() => (ulong)Stopwatch.GetTimestamp();

        public override ulong osd_ticks_per_second() => (ulong)Stopwatch.Frequency;

        public override void osd_sleep(ulong duration)
        {
            ulong ticksPerSecond = osd_ticks_per_second();
            if (ticksPerSecond == 0)
                return;

            ulong milliseconds = duration * 1000 / ticksPerSecond;
            if (milliseconds >= 2)
                Thread.Sleep((int)Math.Min(milliseconds - 1, int.MaxValue));
        }

        public override mame.osd_work_queue osd_work_queue_alloc(int flags)
            => mame.osdsync_global.osd_work_queue_alloc(flags);

        public override bool osd_work_queue_wait(mame.osd_work_queue queue, ulong timeout)
            => mame.osdsync_global.osd_work_queue_wait(queue, timeout);

        public override void osd_work_queue_free(mame.osd_work_queue queue)
            => mame.osdsync_global.osd_work_queue_free(queue);

        public override mame.osd_work_item osd_work_item_queue_multiple(
            mame.osd_work_queue queue,
            mame.osd_work_callback callback,
            int numitems,
            List<object> parambase,
            uint flags)
            => mame.osdsync_global.osd_work_item_queue_multiple(queue, callback, numitems, parambase, flags);

        public override void osd_break_into_debugger(string message)
        {
            Console.Error.WriteLine(message);
            if (Debugger.IsAttached)
                Debugger.Break();
        }

        public override string osd_subst_env(string src)
            => Environment.ExpandEnvironmentVariables(src ?? string.Empty);
    }

    private sealed class McsHostLibrary : mame.osdlib_interface
    {
        public override string osd_get_clipboard_text() => string.Empty;
    }

    private sealed class McsHostFileSystem : mame.osd_file
    {
        private readonly FileStream? _stream;

        public McsHostFileSystem()
        {
        }

        private McsHostFileSystem(FileStream stream)
        {
            _stream = stream;
        }

        public override mame.std.error_condition open(string path, uint openflags, out mame.osd_file file, out ulong filesize)
        {
            file = null!;
            filesize = 0;

            if (string.IsNullOrWhiteSpace(path))
                return mame.std.errc.no_such_file_or_directory;

            try
            {
                bool wantsRead = (openflags & mame.osdfile_global.OPEN_FLAG_READ) != 0;
                bool wantsWrite = (openflags & mame.osdfile_global.OPEN_FLAG_WRITE) != 0;
                if ((openflags & mame.osdfile_global.OPEN_FLAG_CREATE_PATHS) != 0)
                {
                    string? directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                }

                FileMode mode = (openflags & mame.osdfile_global.OPEN_FLAG_CREATE) != 0
                    ? FileMode.Create
                    : wantsWrite
                        ? FileMode.OpenOrCreate
                        : FileMode.Open;
                FileAccess access = wantsWrite
                    ? FileAccess.ReadWrite
                    : FileAccess.Read;

                var stream = new FileStream(path, mode, access, FileShare.ReadWrite);
                file = new McsHostFileSystem(stream);
                filesize = (ulong)stream.Length;
                return new mame.std.error_condition();
            }
            catch (UnauthorizedAccessException)
            {
                return mame.std.errc.permission_denied;
            }
            catch (DirectoryNotFoundException)
            {
                return mame.std.errc.no_such_file_or_directory;
            }
            catch (FileNotFoundException)
            {
                return mame.std.errc.no_such_file_or_directory;
            }
            catch (IOException)
            {
                return mame.std.errc.io_error;
            }
            catch
            {
                return mame.std.errc.no_such_file_or_directory;
            }
        }

        public override void close()
        {
            _stream?.Dispose();
        }

        protected override mame.std.error_condition openpty(out mame.osd_file file, out string name)
        {
            file = null!;
            name = string.Empty;
            return mame.std.errc.not_supported;
        }

        public override mame.std.error_condition read(mame.Pointer<byte> buffer, ulong offset, uint length, out uint actual)
        {
            actual = 0;
            if (_stream == null || buffer == null)
                return mame.std.errc.bad_file_descriptor;

            try
            {
                _stream.Position = Math.Min((long)offset, _stream.Length);
                int requested = checked((int)Math.Min(length, int.MaxValue));
                byte[] temp = new byte[requested];
                int read = _stream.Read(temp, 0, temp.Length);
                for (int i = 0; i < read; i++)
                    buffer[i] = temp[i];
                actual = (uint)read;
                return new mame.std.error_condition();
            }
            catch (IOException)
            {
                return mame.std.errc.io_error;
            }
        }

        public override mame.std.error_condition write(mame.Pointer<byte> buffer, ulong offset, uint length, out uint actual)
        {
            actual = 0;
            if (_stream == null || buffer == null)
                return mame.std.errc.bad_file_descriptor;

            try
            {
                _stream.Position = (long)offset;
                int requested = checked((int)Math.Min(length, int.MaxValue));
                byte[] temp = new byte[requested];
                for (int i = 0; i < temp.Length; i++)
                    temp[i] = buffer[i];
                _stream.Write(temp, 0, temp.Length);
                actual = (uint)temp.Length;
                return new mame.std.error_condition();
            }
            catch (IOException)
            {
                return mame.std.errc.io_error;
            }
        }

        public override mame.std.error_condition truncate(ulong offset)
        {
            if (_stream == null)
                return mame.std.errc.bad_file_descriptor;

            try
            {
                _stream.SetLength((long)offset);
                return new mame.std.error_condition();
            }
            catch (IOException)
            {
                return mame.std.errc.io_error;
            }
        }

        public override mame.std.error_condition flush()
        {
            if (_stream == null)
                return mame.std.errc.bad_file_descriptor;

            try
            {
                _stream.Flush();
                return new mame.std.error_condition();
            }
            catch (IOException)
            {
                return mame.std.errc.io_error;
            }
        }

        public override mame.std.error_condition remove(string filename)
        {
            try
            {
                if (File.Exists(filename))
                    File.Delete(filename);
                return new mame.std.error_condition();
            }
            catch (UnauthorizedAccessException)
            {
                return mame.std.errc.permission_denied;
            }
            catch (IOException)
            {
                return mame.std.errc.io_error;
            }
        }

        public override Stream stream => _stream ?? Stream.Null;
    }

    private sealed class McsHostDirectorySystem : mame.osd.directory_static
    {
        public override mame.osd.directory open(string dirname)
        {
            try
            {
                return Directory.Exists(dirname) ? new McsHostDirectory(dirname) : null!;
            }
            catch
            {
                return null!;
            }
        }
    }

    private sealed class McsHostDirectory : mame.osd.directory
    {
        private readonly FileSystemInfo[] _entries;
        private int _index;

        public McsHostDirectory(string dirname)
        {
            _entries = new DirectoryInfo(dirname).GetFileSystemInfos();
        }

        public override entry read()
        {
            if (_index >= _entries.Length)
                return null!;

            FileSystemInfo info = _entries[_index++];
            return new entry
            {
                name = info.Name,
                type = info switch
                {
                    DirectoryInfo => entry.entry_type.DIR,
                    FileInfo => entry.entry_type.FILE,
                    _ => entry.entry_type.OTHER
                }
            };
        }
    }

    private sealed class McsOsd : mame.osd_interface
    {
        private readonly McsRuntime _runtime;
        private readonly McsArcadeAdapter _owner;
        private readonly mame.bitmap_argb32 _bitmap = new();
        private mame.running_machine? _machine;
        private mame.render_target? _target;
        private int _captureFailuresRemaining = 3;
        private long _lastInputTraceTicks;

        public McsOsd(McsRuntime runtime, McsArcadeAdapter owner)
        {
            _runtime = runtime;
            _owner = owner;
        }

        public void init(mame.running_machine machine)
        {
            _machine = machine;
            _target = machine.render().target_alloc(null, mame.render_global.RENDER_CREATE_NO_ART);
            _target.set_view(_target.configured_view("auto", 0, 1));
            _target.set_screen_overlay_enabled(false);
        }

        public void machine_update(mame.running_machine machine)
        {
            ApplyInput(machine);
            _runtime.ProcessMachineUpdate(machine);
        }

        public void update(bool skipRedraw)
        {
            if (_machine == null)
                return;

            if (skipRedraw || _target == null)
            {
                CompleteFrameBoundary(_machine);
                return;
            }

            long updateStart = TraceMcsProfile ? Stopwatch.GetTimestamp() : 0;
            long drawTicks = 0;
            long publishTicks = 0;
            try
            {
                _target.compute_minimum_size(out int width, out int height);
                if (width <= 0 || height <= 0)
                {
                    CompleteFrameBoundary(_machine);
                    return;
                }

                _target.set_bounds(width, height);
                if (_bitmap.width() != width || _bitmap.height() != height)
                    _bitmap.resize(width, height);

                mame.render_primitive_list primitives = _target.get_primitives();
                primitives.acquire_lock();
                long drawStart = TraceMcsProfile ? Stopwatch.GetTimestamp() : 0;
                if (!TryDrawDirectPalette16(primitives, width, height))
                {
                    mame.software_renderer<uint, mame.int_const_0, mame.int_const_0, mame.int_const_0, mame.int_const_16, mame.int_const_8, mame.int_const_0, mame.bool_const_false, mame.bool_const_false>.draw_primitives(
                        primitives,
                        _bitmap.pix(0),
                        (uint)width,
                        (uint)height,
                        (uint)_bitmap.rowpixels());
                }
                if (TraceMcsProfile)
                    drawTicks = Stopwatch.GetTimestamp() - drawStart;
                primitives.release_lock();

                long publishStart = TraceMcsProfile ? Stopwatch.GetTimestamp() : 0;
                _owner.PublishFrame(_bitmap.pix(0), width, height, _bitmap.rowpixels());
                if (TraceMcsProfile)
                    publishTicks = Stopwatch.GetTimestamp() - publishStart;
                _runtime.MarkFrameReady();
                _runtime.ProcessFrameBoundaryStateRequest(_machine);
            }
            catch (Exception ex)
            {
                if (_captureFailuresRemaining-- > 0)
                    Console.Error.WriteLine($"[MCS] Frame capture failed: {ex.Message}");
            }
            finally
            {
                if (TraceMcsProfile)
                    _runtime.AddProfileFrame(Stopwatch.GetTimestamp() - updateStart, drawTicks, publishTicks);
            }
        }

        private void CompleteFrameBoundary(mame.running_machine machine)
        {
            _runtime.MarkFrameReady();
            _runtime.ProcessFrameBoundaryStateRequest(machine);
        }

        private bool TryDrawDirectPalette16(mame.render_primitive_list primitives, int width, int height)
        {
            mame.render_primitive? screenQuad = null;
            for (mame.render_primitive? prim = primitives.first(); prim != null; prim = prim.next())
            {
                if (prim.type != mame.render_primitive.primitive_type.QUAD || prim.texture.base_ == null)
                    continue;

                if (screenQuad != null)
                    return false;

                screenQuad = prim;
            }

            if (screenQuad == null ||
                screenQuad.texture.palette == null ||
                screenQuad.texture.width != width ||
                screenQuad.texture.height != height ||
                screenQuad.texture.rowpixels < screenQuad.texture.width)
                return false;

            uint expectedFlags =
                mame.render_global.PRIMFLAG_TEXFORMAT((uint)mame.texture_format.TEXFORMAT_PALETTE16) |
                mame.render_global.PRIMFLAG_BLENDMODE(mame.rendertypes_global.BLENDMODE_NONE);
            uint relevantFlags = screenQuad.flags & (mame.render_global.PRIMFLAG_TEXFORMAT_MASK | mame.render_global.PRIMFLAG_BLENDMODE_MASK);
            if (relevantFlags != expectedFlags ||
                mame.render_global.PRIMFLAG_GET_TEXWRAP(screenQuad.flags) ||
                Math.Abs(screenQuad.bounds.x0) > 0.001f ||
                Math.Abs(screenQuad.bounds.y0) > 0.001f ||
                Math.Abs(screenQuad.bounds.x1 - width) > 0.001f ||
                Math.Abs(screenQuad.bounds.y1 - height) > 0.001f ||
                Math.Abs(screenQuad.texcoords.tl.u) > 0.001f ||
                Math.Abs(screenQuad.texcoords.tl.v) > 0.001f ||
                Math.Abs(screenQuad.texcoords.tr.u - 1.0f) > 0.001f ||
                Math.Abs(screenQuad.texcoords.tr.v) > 0.001f ||
                Math.Abs(screenQuad.texcoords.bl.u) > 0.001f ||
                Math.Abs(screenQuad.texcoords.bl.v - 1.0f) > 0.001f)
                return false;

            CopyPalette16ToBgra(
                screenQuad.texture.base_,
                screenQuad.texture.palette,
                _bitmap.pix(0),
                width,
                height,
                (int)screenQuad.texture.rowpixels,
                _bitmap.rowpixels());

            return true;
        }

        private static unsafe void CopyPalette16ToBgra(
            mame.Pointer<byte> source,
            mame.Pointer<mame.rgb_t> palette,
            mame.PointerU32 destination,
            int width,
            int height,
            int sourceRowPixels,
            int destinationRowPixels)
        {
            byte[] sourceData = source.Buffer.data_raw;
            byte[] destinationData = destination.Buffer.data_raw;
            mame.rgb_t[] paletteData = palette.Buffer.data_raw;
            int sourceOffset = source.Offset;
            int destinationOffset = destination.Offset;
            int paletteOffset = palette.Offset;

            fixed (byte* sourceBase = sourceData)
            fixed (byte* destinationBase = destinationData)
            {
                ushort* source16 = (ushort*)(sourceBase + sourceOffset);
                uint* destination32 = (uint*)(destinationBase + destinationOffset);

                for (int y = 0; y < height; y++)
                {
                    int sourceRow = y * sourceRowPixels;
                    int destinationRow = y * destinationRowPixels;
                    for (int x = 0; x < width; x++)
                    {
                        ushort pen = source16[sourceRow + x];
                        destination32[destinationRow + x] = paletteData[paletteOffset + pen];
                    }
                }
            }
        }

        public void input_update()
        {
            long inputStart = TraceMcsProfile ? Stopwatch.GetTimestamp() : 0;
            mame.running_machine? machine = _machine;
            if (machine == null)
            {
                if (TraceMcsProfile)
                    _runtime.AddProfileInput(Stopwatch.GetTimestamp() - inputStart);
                return;
            }

            ApplyInput(machine);

            if (TraceMcsProfile)
                _runtime.AddProfileInput(Stopwatch.GetTimestamp() - inputStart);
        }

        private void ApplyInput(mame.running_machine machine)
        {
            ArcadeInputState input = _owner.SnapshotInput();
            machine.set_ui_active(false);
            bool anyInput =
                input.Up || input.Down || input.Left || input.Right ||
                input.Button1 || input.Button2 || input.Button3 || input.Button4 ||
                input.Button5 || input.Button6 || input.Start || input.Coin;
            foreach (KeyValuePair<string, mame.ioport_port> port in machine.ioport().ports())
            {
                uint digital = port.Value.live().digital;
                foreach (mame.ioport_field field in port.Value.fields())
                {
                    bool? pressed = field.type() switch
                    {
                        mame.ioport_type.IPT_JOYSTICK_UP => input.Up && field.player() == 0,
                        mame.ioport_type.IPT_JOYSTICK_DOWN => input.Down && field.player() == 0,
                        mame.ioport_type.IPT_JOYSTICK_LEFT => input.Left && field.player() == 0,
                        mame.ioport_type.IPT_JOYSTICK_RIGHT => input.Right && field.player() == 0,
                        mame.ioport_type.IPT_BUTTON1 => input.Button1 && field.player() == 0,
                        mame.ioport_type.IPT_BUTTON2 => input.Button2 && field.player() == 0,
                        mame.ioport_type.IPT_BUTTON3 => input.Button3 && field.player() == 0,
                        mame.ioport_type.IPT_BUTTON4 => input.Button4 && field.player() == 0,
                        mame.ioport_type.IPT_BUTTON5 => input.Button5 && field.player() == 0,
                        mame.ioport_type.IPT_BUTTON6 => input.Button6 && field.player() == 0,
                        mame.ioport_type.IPT_START1 => input.Start,
                        mame.ioport_type.IPT_COIN1 => input.Coin,
                        _ => null
                    };

                    if (pressed.HasValue)
                    {
                        field.set_value(pressed.Value ? 1u : 0u);
                        digital &= ~field.mask();
                        if (pressed.Value)
                            digital |= field.mask();
                    }
                }

                port.Value.live().digital = digital;
            }

            if (TraceMcsInput && anyInput)
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _lastInputTraceTicks > Stopwatch.Frequency / 8)
                {
                    _lastInputTraceTicks = now;
                    Console.Error.WriteLine(
                        $"[MCS-INPUT] up={input.Up} down={input.Down} left={input.Left} right={input.Right} " +
                        $"b1={input.Button1} b2={input.Button2} b3={input.Button3} b4={input.Button4} " +
                        $"start={input.Start} coin={input.Coin}");
                    foreach (KeyValuePair<string, mame.ioport_port> port in machine.ioport().ports())
                    {
                        string tag = port.Key;
                        if (!tag.EndsWith(":P1", StringComparison.Ordinal)
                            && !tag.EndsWith(":SYSTEM", StringComparison.Ordinal)
                            && !tag.EndsWith(":AUDIO_COIN", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        Console.Error.WriteLine(
                            $"[MCS-INPUT-PORT] {tag} digital=0x{port.Value.live().digital:x4} " +
                            $"def=0x{port.Value.live().defvalue:x4} read=0x{port.Value.read():x4}");
                    }
                }
            }
        }

        public void update_audio_stream(mame.Pointer<short> buffer, int samplesThisFrame)
        {
            long audioStart = TraceMcsProfile ? Stopwatch.GetTimestamp() : 0;
            _owner.PublishAudio(buffer, samplesThisFrame);
            if (TraceMcsProfile)
                _runtime.AddProfileAudio(Stopwatch.GetTimestamp() - audioStart);
        }

        public void set_mastervolume(int attenuation)
        {
        }

        public bool no_sound() => false;

        public void customize_input_type_list(mame.std.vector<mame.input_type_entry> typelist)
        {
        }

        public void add_audio_to_recording(mame.Pointer<short> buffer, int samplesThisFrame)
        {
        }

        public mame.std.vector<mame.ui.menu_item> get_slider_list() => new();

        public mame.osd_font font_alloc() => new NullMcsFont();

        public bool execute_command(string command) => false;

        public void set_verbose(bool printVerbose)
        {
        }

        public void ScheduleExit()
        {
            try
            {
                _machine?.schedule_exit();
            }
            catch
            {
            }
        }

        public void ScheduleReset()
        {
            try
            {
                _machine?.schedule_hard_reset();
            }
            catch
            {
            }
        }
    }

    private sealed class NullMcsFont : mame.osd_font
    {
        public bool open(string fontPath, string name, out int height)
        {
            height = 0;
            return false;
        }
    }
}

public sealed record McsDriverInfo(string Name, string Year, string Manufacturer);

public static class McsDriverCatalog
{
    private static readonly object InitLock = new();
    private static IReadOnlyDictionary<string, McsDriverInfo>? _drivers;

        public static IEnumerable<string> DriverNames => Drivers.Keys;

    public static bool Contains(string driverName) => Drivers.ContainsKey(driverName);

    public static bool TryFind(string driverName, out McsDriverInfo? driver)
        => Drivers.TryGetValue(driverName, out driver);

    internal static void Invalidate()
    {
        lock (InitLock)
            _drivers = null;
    }

    private static IReadOnlyDictionary<string, McsDriverInfo> Drivers
    {
        get
        {
            if (_drivers != null)
                return _drivers;

            lock (InitLock)
            {
                if (_drivers != null)
                    return _drivers;

                McsArcadeAdapter.EnsureMcsInitialized();
                var drivers = new SortedDictionary<string, McsDriverInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (mame.game_driver driver in mame.drivlist_global.s_drivers_sorted ?? Array.Empty<mame.game_driver>())
                {
                    if (string.IsNullOrWhiteSpace(driver.name) || driver.name.StartsWith("___", StringComparison.Ordinal))
                        continue;

                    drivers[driver.name] = new McsDriverInfo(
                        driver.name,
                        driver.year ?? "?",
                        driver.manufacturer ?? "?");
                }

                _drivers = drivers;
                return _drivers;
            }
        }
    }
}
