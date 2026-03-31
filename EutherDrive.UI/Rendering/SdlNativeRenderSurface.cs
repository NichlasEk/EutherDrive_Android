using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Silk.NET.Maths;
using Silk.NET.SDL;
using SdlApi = Silk.NET.SDL.Sdl;

namespace EutherDrive.Rendering;

public sealed class SdlNativeRenderSurface : IAcceleratedRenderSurface, IDisposable
{
    private readonly SdlNativeHost _host = new();

    public Control View => _host;
    public int PixelWidth => _host.PixelWidth;
    public int PixelHeight => _host.PixelHeight;

    public bool EnsureSize(int width, int height) => _host.EnsureFrameSize(width, height);

    public FrameBlitMetrics Present(ReadOnlySpan<byte> source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf)
    {
        if (source.IsEmpty || width <= 0 || height <= 0 || srcStride <= 0)
            return FrameBlitMetrics.None;

        long start = measurePerf ? Stopwatch.GetTimestamp() : 0;
        _host.UpdateFrame(source, width, height, srcStride, options);
        long blitTicks = measurePerf ? Stopwatch.GetTimestamp() - start : 0;
        return new FrameBlitMetrics(0, blitTicks);
    }

    public bool ShouldFallbackToBitmap(out string reason) => _host.ShouldFallbackToBitmap(out reason);

    public bool TryGetDebugSummary(out string summary) => _host.TryGetDebugSummary(out summary);

    public void SetInterlaceBlend(bool enabled, int fieldParity) => _host.SetInterlaceBlend(enabled, fieldParity);

    public void Reset() => _host.ResetFrame();

    public void Dispose() => _host.DisposeSurface();

    private sealed class SdlNativeHost : NativeControlHost
    {
        private static readonly bool TraceNativeVideo =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NATIVE_VIDEO"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE"), "1", StringComparison.Ordinal);
        private readonly object _frameSync = new();
        private readonly AutoResetEvent _frameSignal = new(false);
        private byte[] _frameBytes = Array.Empty<byte>();
        private byte[] _stagingBytes = Array.Empty<byte>();
        private int _frameWidth;
        private int _frameHeight;
        private int _frameStride;
        private bool _frameDirty;
        private bool _sharpPixelsEnabled = true;
        private bool _interlaceBlendEnabled;
        private int _interlaceBlendFieldParity = -1;
        private System.Threading.Thread? _renderThread;
        private volatile bool _renderThreadStopRequested;
        private IntPtr _nativeHandle;
        private string _nativeHandleDescriptor = string.Empty;
        private bool _videoSubsystemInitialized;
        private bool _initAttempted;
        private bool _initSucceeded;
        private string _fallbackReason = string.Empty;
        private string _rendererBackend = string.Empty;
        private int _presentCount;
        private int _renderCount;
        private int _uploadCount;
        private long _renderTicksTotal;
        private long _uploadTicksTotal;
        private long _lastRenderTicks;
        private long _lastUploadTicks;

        public SdlNativeHost()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }

        public int PixelWidth
        {
            get
            {
                lock (_frameSync)
                    return _frameWidth;
            }
        }

        public int PixelHeight
        {
            get
            {
                lock (_frameSync)
                    return _frameHeight;
            }
        }

        public bool EnsureFrameSize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return false;

            lock (_frameSync)
            {
                bool recreated = width != _frameWidth || height != _frameHeight;
                _frameWidth = width;
                _frameHeight = height;
                _frameStride = width * 4;
                int requiredBytes = checked(width * height * 4);
                if (_frameBytes.Length != requiredBytes)
                    _frameBytes = new byte[requiredBytes];
                if (_stagingBytes.Length != requiredBytes)
                    _stagingBytes = new byte[requiredBytes];
                return recreated;
            }
        }

        public void UpdateFrame(ReadOnlySpan<byte> source, int width, int height, int srcStride, in FrameBlitOptions options)
        {
            EnsureFrameSize(width, height);

            int dstStride = width * 4;
            int requiredBytes = checked(dstStride * height);
            byte[] stagingBytes;

            lock (_frameSync)
            {
                if (_stagingBytes.Length != requiredBytes)
                    _stagingBytes = new byte[requiredBytes];
                stagingBytes = _stagingBytes;
            }

            if (dstStride <= 0 || stagingBytes.Length < requiredBytes)
                return;

            if (srcStride == dstStride)
            {
                source[..Math.Min(source.Length, requiredBytes)].CopyTo(stagingBytes);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> srcRow = source.Slice(y * srcStride, dstStride);
                    Span<byte> dstRow = stagingBytes.AsSpan(y * dstStride, dstStride);
                    srcRow.CopyTo(dstRow);
                }
            }

            lock (_frameSync)
            {
                (_frameBytes, _stagingBytes) = (_stagingBytes, _frameBytes);
                _frameWidth = width;
                _frameHeight = height;
                _frameStride = dstStride;
                _frameDirty = true;
                _sharpPixelsEnabled = options.SharpPixels;
            }

            _frameSignal.Set();
        }

        public bool ShouldFallbackToBitmap(out string reason)
        {
            lock (_frameSync)
            {
                reason = _fallbackReason;
                return !string.IsNullOrEmpty(reason);
            }
        }

        public bool TryGetDebugSummary(out string summary)
        {
            lock (_frameSync)
            {
                double avgRenderMs = _renderCount > 0 ? (_renderTicksTotal * 1000.0 / Stopwatch.Frequency) / _renderCount : 0;
                double avgUploadMs = _uploadCount > 0 ? (_uploadTicksTotal * 1000.0 / Stopwatch.Frequency) / _uploadCount : 0;
                double lastRenderMs = _lastRenderTicks > 0 ? _lastRenderTicks * 1000.0 / Stopwatch.Frequency : 0;
                double lastUploadMs = _lastUploadTicks > 0 ? _lastUploadTicks * 1000.0 / Stopwatch.Frequency : 0;
                summary = $"SDL Present:{_presentCount} Render:{_renderCount} Upload:{_uploadCount} IL:{(_interlaceBlendEnabled ? 1 : 0)}/{_interlaceBlendFieldParity} R:{avgRenderMs:0.0}/{lastRenderMs:0.0}ms U:{avgUploadMs:0.0}/{lastUploadMs:0.0}ms";
                if (!string.IsNullOrEmpty(_rendererBackend))
                    summary = $"{summary}\nSDL Renderer:{_rendererBackend}";
                if (!string.IsNullOrEmpty(_nativeHandleDescriptor))
                    summary = $"{summary}\nHandle:{_nativeHandleDescriptor}";
                if (!string.IsNullOrEmpty(_fallbackReason))
                    summary = $"{summary}\nSDL Fail:{_fallbackReason}";
                return true;
            }
        }

        public void SetInterlaceBlend(bool enabled, int fieldParity)
        {
            lock (_frameSync)
            {
                _interlaceBlendEnabled = enabled && (fieldParity == 0 || fieldParity == 1);
                _interlaceBlendFieldParity = _interlaceBlendEnabled ? (fieldParity & 1) : -1;
            }
        }

        public void ResetFrame()
        {
            lock (_frameSync)
            {
                _frameDirty = false;
                _frameWidth = 0;
                _frameHeight = 0;
                _frameStride = 0;
                _frameBytes = Array.Empty<byte>();
                _stagingBytes = Array.Empty<byte>();
            }
        }

        public void DisposeSurface()
        {
            StopRendererThread();
            ResetFrame();
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            IPlatformHandle handle = base.CreateNativeControlCore(parent);
            lock (_frameSync)
            {
                _nativeHandle = handle.Handle;
                _nativeHandleDescriptor = handle.HandleDescriptor ?? string.Empty;
                _fallbackReason = string.Empty;
                _initAttempted = false;
                _initSucceeded = false;
            }

            if (TraceNativeVideo)
                Console.WriteLine($"[DesktopNativeVideo] child handle descriptor='{_nativeHandleDescriptor}' handle=0x{_nativeHandle.ToInt64():X}");

            StartRendererThread();
            return handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            StopRendererThread();

            lock (_frameSync)
            {
                _nativeHandle = IntPtr.Zero;
                _nativeHandleDescriptor = string.Empty;
                _initAttempted = false;
                _initSucceeded = false;
                _rendererBackend = string.Empty;
            }

            base.DestroyNativeControlCore(control);
        }

        private void StartRendererThread()
        {
            StopRendererThread();

            lock (_frameSync)
            {
                if (_nativeHandle == IntPtr.Zero)
                    return;
            }

            _renderThreadStopRequested = false;
            _renderThread = new System.Threading.Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "EutherDrive SDL Video"
            };
            _renderThread.Start();
        }

        private void StopRendererThread()
        {
            System.Threading.Thread? thread = _renderThread;
            if (thread == null)
                return;

            _renderThreadStopRequested = true;
            _frameSignal.Set();
            thread.Join(500);
            _renderThread = null;
            _renderThreadStopRequested = false;
        }

        private unsafe void RenderLoop()
        {
            Silk.NET.SDL.Window* window = null;
            Renderer* renderer = null;
            Texture* texture = null;
            int textureWidth = 0;
            int textureHeight = 0;
            bool textureSharpPixels = true;
            SdlApi sdl = SdlApi.GetApi();

            try
            {
                if (sdl.InitSubSystem(SdlApi.InitVideo) != 0)
                    throw new InvalidOperationException($"SDL video init failed: {sdl.GetErrorS()}");

                _videoSubsystemInitialized = true;
                sdl.SetHint(SdlApi.HintRenderDriver, "opengl");
                sdl.SetHint(SdlApi.HintRenderVsync, "1");

                IntPtr handle;
                string handleDescriptor;
                lock (_frameSync)
                {
                    handle = _nativeHandle;
                    handleDescriptor = _nativeHandleDescriptor;
                    _initAttempted = true;
                }

                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException("Desktop native video handle is unavailable.");

                if (!IsSupportedNativeHandleDescriptor(handleDescriptor))
                    throw new InvalidOperationException($"Unsupported desktop native handle descriptor '{handleDescriptor}'.");

                if (TraceNativeVideo)
                    Console.WriteLine($"[DesktopNativeVideo] attaching SDL to {handleDescriptor} handle=0x{handle.ToInt64():X}");

                window = sdl.CreateWindowFrom((void*)handle);
                if (window == null)
                    throw new InvalidOperationException($"SDL window attach failed: {sdl.GetErrorS()}");

                renderer = sdl.CreateRenderer(window, -1, (uint)(RendererFlags.Accelerated | RendererFlags.Presentvsync));
                if (renderer == null)
                    renderer = sdl.CreateRenderer(window, -1, (uint)RendererFlags.Accelerated);
                if (renderer == null)
                    renderer = sdl.CreateRenderer(window, -1, (uint)RendererFlags.Software);
                if (renderer == null)
                    throw new InvalidOperationException($"SDL renderer init failed: {sdl.GetErrorS()}");

                if (TraceNativeVideo)
                    Console.WriteLine("[DesktopNativeVideo] SDL renderer attached");

                _ = sdl.RenderSetVSync(renderer, 1);

                RendererInfo rendererInfo;
                if (sdl.GetRendererInfo(renderer, &rendererInfo) == 0 && rendererInfo.Name != null)
                    _rendererBackend = Marshal.PtrToStringAnsi((IntPtr)rendererInfo.Name) ?? string.Empty;

                lock (_frameSync)
                {
                    _initSucceeded = true;
                    _fallbackReason = string.Empty;
                }

                while (!_renderThreadStopRequested)
                {
                    byte[] frameBytes;
                    int frameWidth;
                    int frameHeight;
                    int frameStride;
                    bool frameDirty;
                    bool sharpPixelsEnabled;

                    lock (_frameSync)
                    {
                        frameBytes = _frameBytes;
                        frameWidth = _frameWidth;
                        frameHeight = _frameHeight;
                        frameStride = _frameStride;
                        frameDirty = _frameDirty;
                        sharpPixelsEnabled = _sharpPixelsEnabled;
                        _frameDirty = false;
                    }

                    if (frameWidth <= 0 || frameHeight <= 0 || frameStride <= 0 || frameBytes.Length < frameStride * frameHeight)
                    {
                        _frameSignal.WaitOne(16);
                        continue;
                    }

                    if (texture == null || textureWidth != frameWidth || textureHeight != frameHeight)
                    {
                        if (texture != null)
                        {
                            sdl.DestroyTexture(texture);
                            texture = null;
                        }

                        texture = sdl.CreateTexture(
                            renderer,
                            (uint)PixelFormatEnum.Argb8888,
                            (int)TextureAccess.Streaming,
                            frameWidth,
                            frameHeight);
                        if (texture == null)
                            throw new InvalidOperationException($"SDL texture init failed: {sdl.GetErrorS()}");

                        textureWidth = frameWidth;
                        textureHeight = frameHeight;
                        textureSharpPixels = !sharpPixelsEnabled;
                        frameDirty = true;
                    }

                    if (textureSharpPixels != sharpPixelsEnabled)
                    {
                        _ = sdl.SetTextureScaleMode(texture, sharpPixelsEnabled ? ScaleMode.Nearest : ScaleMode.Linear);
                        textureSharpPixels = sharpPixelsEnabled;
                    }

                    if (frameDirty)
                    {
                        fixed (byte* pFrame = frameBytes)
                        {
                            long uploadStart = Stopwatch.GetTimestamp();
                            if (sdl.UpdateTexture(texture, (Rectangle<int>*)null, pFrame, frameStride) != 0)
                                throw new InvalidOperationException($"SDL texture upload failed: {sdl.GetErrorS()}");

                            long uploadTicks = Stopwatch.GetTimestamp() - uploadStart;
                            lock (_frameSync)
                            {
                                _uploadCount++;
                                _uploadTicksTotal += uploadTicks;
                                _lastUploadTicks = uploadTicks;
                            }
                        }
                    }

                    long renderStart = Stopwatch.GetTimestamp();
                    _ = sdl.SetRenderDrawColor(renderer, 0, 0, 0, 255);
                    _ = sdl.RenderClear(renderer);
                    if (sdl.RenderCopy(renderer, texture, (Rectangle<int>*)null, (Rectangle<int>*)null) != 0)
                        throw new InvalidOperationException($"SDL render copy failed: {sdl.GetErrorS()}");
                    sdl.RenderPresent(renderer);
                    long renderTicks = Stopwatch.GetTimestamp() - renderStart;

                    lock (_frameSync)
                    {
                        _presentCount++;
                        _renderCount++;
                        _renderTicksTotal += renderTicks;
                        _lastRenderTicks = renderTicks;
                    }
                }
            }
            catch (Exception ex)
            {
                if (TraceNativeVideo)
                    Console.WriteLine("[DesktopNativeVideo] " + ex.Message);
                lock (_frameSync)
                {
                    _initSucceeded = false;
                    _fallbackReason = ex.Message;
                }
            }
            finally
            {
                if (texture != null)
                    sdl.DestroyTexture(texture);
                if (renderer != null)
                    sdl.DestroyRenderer(renderer);
                if (window != null)
                    sdl.DestroyWindow(window);
                if (_videoSubsystemInitialized)
                {
                    sdl.QuitSubSystem(SdlApi.InitVideo);
                    _videoSubsystemInitialized = false;
                }
            }
        }

        private static bool IsSupportedNativeHandleDescriptor(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return false;

            return descriptor.Equals("XID", StringComparison.OrdinalIgnoreCase)
                || descriptor.Equals("HWND", StringComparison.OrdinalIgnoreCase);
        }
    }
}
