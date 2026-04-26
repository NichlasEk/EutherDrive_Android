using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade;

public sealed class McsArcadeAdapter : IEmulatorCore, IDisposable
{
    private const int PlaceholderWidth = 256;
    private const int PlaceholderHeight = 224;
    private const int PlaceholderStride = PlaceholderWidth * 4;
    private const int OutputSampleRate = 48_000;
    private const int OutputChannels = 2;
    private static readonly object McsInitLock = new();
    private static readonly McsHostCore HostCore = new();
    private static readonly McsHostFileSystem HostFileSystem = new();
    private static readonly McsHostDirectorySystem HostDirectorySystem = new();
    private static readonly McsHostLibrary HostLibrary = new();
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
    private int _frameWidth = PlaceholderWidth;
    private int _frameHeight = PlaceholderHeight;
    private int _frameStride = PlaceholderStride;

    private string? _driverName;
    private McsRuntime? _runtime;
    private ArcadeInputState _inputState;

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

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Arcade ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Arcade ROM archive not found.", path);

        StopRuntime();

        _driverName = GetDriverNameFromPath(path);
        DrawPlaceholderFrame();

        if (!McsDriverCatalog.TryFind(_driverName, out McsDriverInfo? driver))
        {
            string examples = string.Join(", ", McsDriverCatalog.DriverNames.Take(12));
            throw new NotSupportedException(
                $"MCS arcade core is installed, but this MCS snapshot does not contain driver '{_driverName}'. " +
                "Rampage arcade needs the MAME MCR3/Rampage driver ported or added before it can boot. " +
                $"Available MCS examples: {examples}.");
        }

        McsDriverInfo presentDriver = driver!;
        string romDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
        _runtime = new McsRuntime(this, presentDriver.Name, romDirectory);
        _runtime.Start();
    }

    public void Reset()
    {
        _runtime?.ScheduleReset();
    }

    public void RunFrame()
    {
        _runtime?.ThrowIfFaulted();
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
            return _audioBuffer;
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

            for (int y = 0; y < height; y++)
            {
                int srcRow = y * rowPixels;
                int dst = y * stride;
                for (int x = 0; x < width; x++)
                {
                    uint argb = pixels[srcRow + x];
                    _frameBuffer[dst + 0] = (byte)(argb & 0xFF);
                    _frameBuffer[dst + 1] = (byte)((argb >> 8) & 0xFF);
                    _frameBuffer[dst + 2] = (byte)((argb >> 16) & 0xFF);
                    _frameBuffer[dst + 3] = 0xFF;
                    dst += 4;
                }
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
            if (_audioBuffer.Length != sampleCount)
                _audioBuffer = new short[sampleCount];

            for (int i = 0; i < sampleCount; i++)
                _audioBuffer[i] = samples[i];
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
        private readonly ManualResetEventSlim _stopped = new(false);
        private readonly McsOsd _osd;
        private readonly Thread _thread;
        private volatile Exception? _fault;

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
            if (!_stopped.Wait(TimeSpan.FromSeconds(2)))
                Console.Error.WriteLine($"[MCS] Driver '{_driverName}' did not stop within timeout.");

            _firstFrame.Dispose();
            _stopped.Dispose();
        }

        public void MarkFrameReady() => _firstFrame.Set();

        public void ThrowIfFaulted()
        {
            Exception? fault = _fault;
            if (fault != null)
                throw new InvalidOperationException($"MCS driver '{_driverName}' failed.", fault);
        }

        public void ScheduleReset() => _osd.ScheduleReset();

        private void Run()
        {
            try
            {
                EnsureMcsInitialized();
                mame.mame_machine_manager.close_instance();

                var options = new mame.osd_options();
                var args = new mame.std.vector<string>(new[]
                {
                    "eutherdrive-mcs",
                    _driverName,
                    "-rompath",
                    _romDirectory,
                    "-noreadconfig",
                    "-nowriteconfig",
                    "-skip_gameinfo",
                    "-ui",
                    "simple"
                });

                int result = mame.emulator_info.start_frontend(options, _osd, args);
                if (result != mame.main_global.EMU_ERR_NONE)
                    _fault = new InvalidOperationException($"MCS exited with code {result} while running '{_driverName}'.");
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

        public void update(bool skipRedraw)
        {
            if (skipRedraw || _target == null)
                return;

            try
            {
                _target.compute_minimum_size(out int width, out int height);
                if (width <= 0 || height <= 0)
                    return;

                _target.set_bounds(width, height);
                if (_bitmap.width() != width || _bitmap.height() != height)
                    _bitmap.resize(width, height);

                mame.render_primitive_list primitives = _target.get_primitives();
                primitives.acquire_lock();
                mame.software_renderer<uint, mame.int_const_0, mame.int_const_0, mame.int_const_0, mame.int_const_16, mame.int_const_8, mame.int_const_0, mame.bool_const_false, mame.bool_const_false>.draw_primitives(
                    primitives,
                    _bitmap.pix(0),
                    (uint)width,
                    (uint)height,
                    (uint)_bitmap.rowpixels());
                primitives.release_lock();

                _owner.PublishFrame(_bitmap.pix(0), width, height, _bitmap.rowpixels());
                _runtime.MarkFrameReady();
            }
            catch (Exception ex)
            {
                if (_captureFailuresRemaining-- > 0)
                    Console.Error.WriteLine($"[MCS] Frame capture failed: {ex.Message}");
            }
        }

        public void input_update()
        {
            mame.running_machine? machine = _machine;
            if (machine == null)
                return;

            ArcadeInputState input = _owner.SnapshotInput();
            foreach (KeyValuePair<string, mame.ioport_port> port in machine.ioport().ports())
            {
                foreach (mame.ioport_field field in port.Value.fields())
                {
                    bool pressed = field.type() switch
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
                        _ => false
                    };

                    field.set_value(pressed ? 1u : 0u);
                }
            }
        }

        public void update_audio_stream(mame.Pointer<short> buffer, int samplesThisFrame)
        {
            _owner.PublishAudio(buffer, samplesThisFrame);
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
