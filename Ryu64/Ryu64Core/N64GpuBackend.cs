using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ryu64Core
{
    [Flags]
    public enum N64GpuFlags : uint { Validate = 1, BatchStateWrites = 2, RequireDiscrete = 4, DeferDisjointWrites = 8, DeferDisjointLoadBlocks = 16, NarrowTriangleWrites = 32, TrackTextureReads = 64 }

    [StructLayout(LayoutKind.Sequential)]
    public struct N64GpuStats
    {
        public ulong Submissions, Commands, WriteSpans, WriteBytes, WriteBarriers;
        public ulong ReadbackBytes, LastTimeline, CompletedTimeline, ValidationErrors;
    }

    // Optional headless backend boundary. Nothing loads the native library
    // until explicitly constructed; the normal emulator still uses software.
    // Every method holds a SafeHandle lease, including concurrent Dispose.
    public sealed unsafe class N64GpuBackend : SafeHandleZeroOrMinusOneIsInvalid
    {
        public const int RamSize = 8 << 20, HiddenSize = 4 << 20, TmemSize = 4096;
        private const int ErrorSize = 1024;
        private readonly Api _api;

        public N64GpuBackend(string library, byte[] ram, byte[] hidden, N64GpuFlags flags) : base(true)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The initial N64 GPU backend targets Linux");
            RequireSize(ram, RamSize); RequireSize(hidden, HiddenSize);
            _api = new Api(library);
            try
            {
                if (_api.Abi() != 1) throw new InvalidOperationException("Unsupported N64 GPU ABI");
                byte* error = stackalloc byte[ErrorSize]; ulong id = 0;
                fixed (byte* ramPtr = ram, hiddenPtr = hidden)
                    Check(_api.Create(1, (uint)flags, ramPtr, RamSize, hiddenPtr, HiddenSize, &id, error, ErrorSize), error);
                SetHandle(new IntPtr(checked((long)id)));
            }
            catch { _api.Dispose(); throw; }
        }

        public ulong Submit(ReadOnlySpan<byte> batch)
        {
            bool acquired = false;
            try
            {
                DangerousAddRef(ref acquired); ulong timeline = 0; byte* error = stackalloc byte[ErrorSize];
                fixed (byte* bytes = batch)
                    Check(_api.Submit(Id, bytes, checked((uint)batch.Length), &timeline, error, ErrorSize), error);
                return timeline;
            }
            finally { if (acquired) DangerousRelease(); }
        }

        public void Wait(ulong timeline)
        {
            bool acquired = false;
            try { DangerousAddRef(ref acquired); byte* error = stackalloc byte[ErrorSize]; Check(_api.Wait(Id, timeline, error, ErrorSize), error); }
            finally { if (acquired) DangerousRelease(); }
        }

        public void Readback(ulong timeline, byte[] ram, byte[] hidden, byte[] tmem)
            => ReadbackCore(timeline, ram, hidden, tmem, false);

        // The live core retains RAM and has already applied every staged CPU
        // write to it. Old ABI-1 libraries keep the full-readback fallback.
        public void ReadbackLive(ulong timeline, byte[] ram, byte[] hidden, byte[] tmem)
            => ReadbackCore(timeline, ram, hidden, tmem, true);

        private void ReadbackCore(ulong timeline, byte[] ram, byte[] hidden, byte[] tmem, bool live)
        {
            RequireSize(ram, RamSize); RequireSize(hidden, HiddenSize); RequireSize(tmem, TmemSize);
            bool acquired = false;
            try
            {
                DangerousAddRef(ref acquired); byte* error = stackalloc byte[ErrorSize];
                var readback = live ? _api.ReadbackLive ?? _api.Readback : _api.Readback;
                fixed (byte* ramPtr = ram, hiddenPtr = hidden, tmemPtr = tmem)
                    Check(readback(Id, timeline, ramPtr, RamSize, hiddenPtr, HiddenSize, tmemPtr, TmemSize, error, ErrorSize), error);
            }
            finally { if (acquired) DangerousRelease(); }
        }

        public byte[] SaveState()
        {
            if (_api.SaveState == null)
                throw new InvalidOperationException("This GPU library predates savestate support. Restart with scripts/run-n64-gpu-desktop.sh to rebuild it.");
            bool acquired = false;
            try
            {
                DangerousAddRef(ref acquired); byte* error = stackalloc byte[ErrorSize]; uint written = 0;
                byte[] state = new byte[16384];
                fixed (byte* bytes = state)
                    Check(_api.SaveState(Id, bytes, (uint)state.Length, &written, error, ErrorSize), error);
                if (written == 0 || written > state.Length) throw new InvalidOperationException("Invalid GPU checkpoint length");
                Array.Resize(ref state, (int)written); return state;
            }
            finally { if (acquired) DangerousRelease(); }
        }

        public static void RequireStateSupport(string library)
        {
            using var api = new Api(library);
            if (api.Abi() != 1 || api.SaveState == null || api.LoadState == null)
                throw new InvalidOperationException("This GPU library predates savestate support. Restart with scripts/run-n64-gpu-desktop.sh to rebuild it.");
        }

        public void LoadState(byte[] state)
        {
            if (_api.LoadState == null)
                throw new InvalidOperationException("This GPU library predates savestate support. Restart with scripts/run-n64-gpu-desktop.sh to rebuild it.");
            if (state == null || state.Length == 0 || state.Length > 16384) throw new ArgumentException("Invalid GPU checkpoint size");
            bool acquired = false;
            try
            {
                DangerousAddRef(ref acquired); byte* error = stackalloc byte[ErrorSize];
                fixed (byte* bytes = state)
                    Check(_api.LoadState(Id, bytes, (uint)state.Length, error, ErrorSize), error);
            }
            finally { if (acquired) DangerousRelease(); }
        }

        public N64GpuStats GetStats()
        {
            bool acquired = false;
            try
            {
                DangerousAddRef(ref acquired); byte* error = stackalloc byte[ErrorSize]; N64GpuStats stats = default;
                Check(_api.Stats(Id, &stats, (uint)sizeof(N64GpuStats), error, ErrorSize), error); return stats;
            }
            finally { if (acquired) DangerousRelease(); }
        }

        public string DeviceName
        {
            get
            {
                bool acquired = false;
                try
                {
                    DangerousAddRef(ref acquired); byte* error = stackalloc byte[ErrorSize]; byte* name = stackalloc byte[256];
                    Check(_api.DeviceName(Id, name, 256, error, ErrorSize), error); return Text(name, 256);
                }
                finally { if (acquired) DangerousRelease(); }
            }
        }

        private ulong Id => (ulong)DangerousGetHandle().ToInt64();
        private static void RequireSize(byte[] bytes, int length)
        { if (bytes == null || bytes.Length != length) throw new ArgumentException($"Expected {length} memory bytes"); }
        private static string Text(byte* bytes, int size)
        {
            var span = new ReadOnlySpan<byte>(bytes, size); int zero = span.IndexOf((byte)0);
            return Encoding.UTF8.GetString(zero < 0 ? span : span.Slice(0, zero));
        }
        private static void Check(int status, byte* error)
        { if (status != 0) throw new InvalidOperationException($"N64 GPU error {status}: {Text(error, ErrorSize)}"); }

        protected override bool ReleaseHandle()
        {
            byte* error = stackalloc byte[ErrorSize];
            int status = _api.Destroy((ulong)handle.ToInt64(), error, ErrorSize);
            _api.Dispose(); return status == 0;
        }

        // Each backend owns one loader reference, released only after its native
        // context and worker threads are destroyed. No process-global resolver.
        private sealed class Api : IDisposable
        {
            private IntPtr _library;
            internal readonly AbiFn Abi;
            internal readonly CreateFn Create;
            internal readonly SubmitFn Submit;
            internal readonly WaitFn Wait;
            internal readonly ReadbackFn Readback;
            internal readonly ReadbackFn ReadbackLive;
            internal readonly StatsFn Stats;
            internal readonly NameFn DeviceName;
            internal readonly DestroyFn Destroy;
            internal readonly SaveStateFn SaveState;
            internal readonly LoadStateFn LoadState;
            internal Api(string path)
            {
                _library = NativeLibrary.Load(System.IO.Path.GetFullPath(path));
                try
                {
                    Abi = Load<AbiFn>("abi"); Create = Load<CreateFn>("create"); Submit = Load<SubmitFn>("submit");
                    Wait = Load<WaitFn>("wait"); Readback = Load<ReadbackFn>("readback"); Stats = Load<StatsFn>("get_stats");
                    if (NativeLibrary.TryGetExport(_library, "ed_n64_gpu_readback_live", out var live))
                        ReadbackLive = Marshal.GetDelegateForFunctionPointer<ReadbackFn>(live);
                    DeviceName = Load<NameFn>("device_name"); Destroy = Load<DestroyFn>("destroy");
                    if (NativeLibrary.TryGetExport(_library, "ed_n64_gpu_save_state", out var save))
                        SaveState = Marshal.GetDelegateForFunctionPointer<SaveStateFn>(save);
                    if (NativeLibrary.TryGetExport(_library, "ed_n64_gpu_load_state", out var load))
                        LoadState = Marshal.GetDelegateForFunctionPointer<LoadStateFn>(load);
                }
                catch { Dispose(); throw; }
            }
            private T Load<T>(string suffix) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, "ed_n64_gpu_" + suffix));
            public void Dispose() { if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; } }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateFn(uint abi, uint flags, byte* ram, uint ramSize, byte* hidden, uint hiddenSize, ulong* handle, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SubmitFn(ulong handle, byte* batch, uint size, ulong* timeline, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WaitFn(ulong handle, ulong timeline, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadbackFn(ulong handle, ulong timeline, byte* ram, uint ramSize, byte* hidden, uint hiddenSize, byte* tmem, uint tmemSize, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StatsFn(ulong handle, N64GpuStats* stats, uint size, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameFn(ulong handle, byte* name, uint size, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DestroyFn(ulong handle, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SaveStateFn(ulong handle, byte* state, uint capacity, uint* written, byte* error, uint errorSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadStateFn(ulong handle, byte* state, uint size, byte* error, uint errorSize);
    }
}
