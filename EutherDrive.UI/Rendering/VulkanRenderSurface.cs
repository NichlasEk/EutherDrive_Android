using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;

namespace EutherDrive.Rendering;

public sealed class VulkanRenderSurface : IAcceleratedRenderSurface, IDisposable
{
    private readonly VulkanNativeHost _host = new();

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

    private sealed class VulkanNativeHost : NativeControlHost
    {
        private static readonly bool TraceNativeVideo =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NATIVE_VIDEO"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VULKAN"), "1", StringComparison.Ordinal);

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
        private bool _initAttempted;
        private bool _initSucceeded;
        private string _fallbackReason = string.Empty;
        private int _presentCount;
        private int _renderCount;
        private int _uploadCount;
        private long _renderTicksTotal;
        private long _uploadTicksTotal;
        private long _lastRenderTicks;
        private long _lastUploadTicks;

        public VulkanNativeHost()
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

            CopyFrameToBuffer(stagingBytes, source, width, height, srcStride, options);

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
                summary = $"Vulkan Present:{_presentCount} Render:{_renderCount} Upload:{_uploadCount} IL:{(_interlaceBlendEnabled ? 1 : 0)}/{_interlaceBlendFieldParity} R:{avgRenderMs:0.0}/{lastRenderMs:0.0}ms U:{avgUploadMs:0.0}/{lastUploadMs:0.0}ms";
                if (!string.IsNullOrEmpty(_nativeHandleDescriptor))
                    summary = $"{summary}\nHandle:{_nativeHandleDescriptor}";
                if (!string.IsNullOrEmpty(_fallbackReason))
                    summary = $"{summary}\nVulkan Fail:{_fallbackReason}";
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
                Console.WriteLine($"[DesktopVulkanVideo] child handle descriptor='{_nativeHandleDescriptor}' handle=0x{_nativeHandle.ToInt64():X}");

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
                Name = "EutherDrive Vulkan Video"
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
            VulkanBlitPresenter? presenter = null;

            try
            {
                if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
                    throw new InvalidOperationException("Vulkan presenter is currently supported on Windows and Linux only.");

                IntPtr handle;
                string handleDescriptor;
                lock (_frameSync)
                {
                    handle = _nativeHandle;
                    handleDescriptor = _nativeHandleDescriptor;
                    _initAttempted = true;
                }

                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException("Desktop Vulkan video handle is unavailable.");

                if (!IsSupportedNativeHandleDescriptor(handleDescriptor))
                    throw new InvalidOperationException($"Unsupported desktop native handle descriptor '{handleDescriptor}'.");

                if (TraceNativeVideo)
                    Console.WriteLine($"[DesktopVulkanVideo] attaching Vulkan presenter to {handleDescriptor} handle=0x{handle.ToInt64():X}");

                presenter = new VulkanBlitPresenter(handle, handleDescriptor);

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

                    bool drawableChanged = presenter.TryUpdateDrawableSize();
                    bool filterChanged = presenter.SharpPixelsEnabled != sharpPixelsEnabled;
                    if (!frameDirty && !drawableChanged && !filterChanged && presenter.HasPresentedFrame)
                    {
                        _frameSignal.WaitOne(16);
                        continue;
                    }

                    long renderStart = Stopwatch.GetTimestamp();
                    bool uploaded = presenter.PresentFrame(frameBytes, frameWidth, frameHeight, frameStride, sharpPixelsEnabled, frameDirty);
                    long renderTicks = Stopwatch.GetTimestamp() - renderStart;

                    lock (_frameSync)
                    {
                        _presentCount++;
                        _renderCount++;
                        _renderTicksTotal += renderTicks;
                        _lastRenderTicks = renderTicks;
                        if (uploaded)
                        {
                            _uploadCount++;
                            _uploadTicksTotal += presenter.LastUploadTicks;
                            _lastUploadTicks = presenter.LastUploadTicks;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (TraceNativeVideo)
                    Console.WriteLine("[DesktopVulkanVideo] " + ex.Message);
                lock (_frameSync)
                {
                    _initSucceeded = false;
                    _fallbackReason = ex.Message;
                }
            }
            finally
            {
                presenter?.Dispose();
            }
        }

        private static bool IsSupportedNativeHandleDescriptor(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
                return false;

            return descriptor.Equals("XID", StringComparison.OrdinalIgnoreCase)
                || descriptor.Equals("HWND", StringComparison.OrdinalIgnoreCase);
        }

        private static unsafe void CopyFrameToBuffer(byte[] destination, ReadOnlySpan<byte> source, int width, int height, int srcStride, in FrameBlitOptions options)
        {
            int dstStride = width * 4;
            int requiredBytes = checked(dstStride * height);
            if (destination.Length < requiredBytes)
                return;

            fixed (byte* pDst0 = destination)
            fixed (byte* pSrc0 = source)
            {
                int rowBytes = Math.Min(dstStride, srcStride);
                if (rowBytes <= 0)
                    return;

                if (rowBytes == srcStride
                    && rowBytes == dstStride
                    && !options.ForceOpaque
                    && !options.ApplyScanlines
                    && !options.ApplyAdvancedPixelFilter)
                {
                    Buffer.MemoryCopy(pSrc0, pDst0, requiredBytes, requiredBytes);
                    return;
                }

                if (options.ApplyAdvancedPixelFilter)
                {
                    BlitAdvancedPixelFilter(
                        pSrc0,
                        pDst0,
                        height,
                        srcStride,
                        dstStride,
                        rowBytes,
                        options.ForceOpaque,
                        options.ApplyScanlines,
                        options.ScanlineDarkenFactor);
                    return;
                }

                BlitFrameRows(
                    pSrc0,
                    pDst0,
                    height,
                    srcStride,
                    dstStride,
                    rowBytes,
                    options.ForceOpaque,
                    options.ApplyScanlines,
                    options.ScanlineDarkenFactor);
            }
        }

        private static unsafe void BlitFrameRows(
            byte* pSrc0,
            byte* pDst0,
            int height,
            int srcStride,
            int dstStride,
            int copyBytesPerRow,
            bool forceOpaque,
            bool applyScanlines,
            int scanlineDarkenFactor)
        {
            for (int y = 0; y < height; y++)
            {
                byte* pSrcRow = pSrc0 + (y * srcStride);
                byte* pDstRow = pDst0 + (y * dstStride);

                bool darkenRow = applyScanlines && ((y & 1) == 1);
                if (forceOpaque || darkenRow)
                {
                    for (int x = 0; x < copyBytesPerRow; x += 4)
                    {
                        byte b = pSrcRow[x + 0];
                        byte g = pSrcRow[x + 1];
                        byte r = pSrcRow[x + 2];
                        byte a = pSrcRow[x + 3];

                        if (darkenRow)
                        {
                            b = (byte)((b * scanlineDarkenFactor) >> 8);
                            g = (byte)((g * scanlineDarkenFactor) >> 8);
                            r = (byte)((r * scanlineDarkenFactor) >> 8);
                        }

                        pDstRow[x + 0] = b;
                        pDstRow[x + 1] = g;
                        pDstRow[x + 2] = r;
                        pDstRow[x + 3] = forceOpaque ? (byte)0xFF : a;
                    }
                }
                else
                {
                    Buffer.MemoryCopy(pSrcRow, pDstRow, dstStride, copyBytesPerRow);
                }
            }
        }

        private static unsafe void BlitAdvancedPixelFilter(
            byte* pSrc0,
            byte* pDst0,
            int height,
            int srcStride,
            int dstStride,
            int copyBytesPerRow,
            bool forceOpaque,
            bool applyScanlines,
            int scanlineDarkenFactor)
        {
            const int strongEdgeThreshold = 84;
            const int mediumEdgeThreshold = 40;
            const int strongGain256 = 208;
            const int mediumGain256 = 152;
            const int baseGain256 = 96;
            const int clampSlack = 10;

            for (int y = 0; y < height; y++)
            {
                byte* pSrcRow = pSrc0 + (y * srcStride);
                byte* pSrcRowUp = pSrc0 + ((y > 0 ? (y - 1) : y) * srcStride);
                byte* pSrcRowDown = pSrc0 + ((y + 1 < height ? (y + 1) : y) * srcStride);
                byte* pDstRow = pDst0 + (y * dstStride);
                bool darkenRow = applyScanlines && ((y & 1) == 1);

                for (int x = 0; x < copyBytesPerRow; x += 4)
                {
                    int xLeft = x > 0 ? x - 4 : x;
                    int xRight = x + 4 < copyBytesPerRow ? x + 4 : x;

                    byte cb = pSrcRow[x + 0];
                    byte cg = pSrcRow[x + 1];
                    byte cr = pSrcRow[x + 2];
                    byte ca = pSrcRow[x + 3];

                    byte lb = pSrcRow[xLeft + 0];
                    byte lg = pSrcRow[xLeft + 1];
                    byte lr = pSrcRow[xLeft + 2];
                    byte rb = pSrcRow[xRight + 0];
                    byte rg = pSrcRow[xRight + 1];
                    byte rr = pSrcRow[xRight + 2];
                    byte ub = pSrcRowUp[x + 0];
                    byte ug = pSrcRowUp[x + 1];
                    byte ur = pSrcRowUp[x + 2];
                    byte db = pSrcRowDown[x + 0];
                    byte dg = pSrcRowDown[x + 1];
                    byte dr = pSrcRowDown[x + 2];

                    int edge = Math.Abs(cr - lr) + Math.Abs(cr - rr) + Math.Abs(cr - ur) + Math.Abs(cr - dr)
                        + Math.Abs(cg - lg) + Math.Abs(cg - rg) + Math.Abs(cg - ug) + Math.Abs(cg - dg)
                        + Math.Abs(cb - lb) + Math.Abs(cb - rb) + Math.Abs(cb - ub) + Math.Abs(cb - db);

                    int gain256 = baseGain256;
                    if (edge >= strongEdgeThreshold)
                        gain256 = strongGain256;
                    else if (edge >= mediumEdgeThreshold)
                        gain256 = mediumGain256;

                    int minB = Math.Min(Math.Min(lb, rb), Math.Min(ub, db)) - clampSlack;
                    int minG = Math.Min(Math.Min(lg, rg), Math.Min(ug, dg)) - clampSlack;
                    int minR = Math.Min(Math.Min(lr, rr), Math.Min(ur, dr)) - clampSlack;
                    int maxB = Math.Max(Math.Max(lb, rb), Math.Max(ub, db)) + clampSlack;
                    int maxG = Math.Max(Math.Max(lg, rg), Math.Max(ug, dg)) + clampSlack;
                    int maxR = Math.Max(Math.Max(lr, rr), Math.Max(ur, dr)) + clampSlack;

                    int blendB = (lb + rb + ub + db) >> 2;
                    int blendG = (lg + rg + ug + dg) >> 2;
                    int blendR = (lr + rr + ur + dr) >> 2;

                    int outB = cb + (((cb - blendB) * gain256) >> 8);
                    int outG = cg + (((cg - blendG) * gain256) >> 8);
                    int outR = cr + (((cr - blendR) * gain256) >> 8);

                    outB = Clamp(outB, minB, maxB);
                    outG = Clamp(outG, minG, maxG);
                    outR = Clamp(outR, minR, maxR);

                    if (darkenRow)
                    {
                        outB = (outB * scanlineDarkenFactor) >> 8;
                        outG = (outG * scanlineDarkenFactor) >> 8;
                        outR = (outR * scanlineDarkenFactor) >> 8;
                    }

                    pDstRow[x + 0] = (byte)Clamp(outB, 0, 255);
                    pDstRow[x + 1] = (byte)Clamp(outG, 0, 255);
                    pDstRow[x + 2] = (byte)Clamp(outR, 0, 255);
                    pDstRow[x + 3] = forceOpaque ? (byte)0xFF : ca;
                }
            }
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);
    }

    private abstract class NativeWindowBridge : IDisposable
    {
        public static NativeWindowBridge Create(IntPtr nativeHandle, string nativeHandleDescriptor)
        {
            if (OperatingSystem.IsLinux() && nativeHandleDescriptor.Equals("XID", StringComparison.OrdinalIgnoreCase))
                return new X11NativeWindowBridge(nativeHandle);

            if (OperatingSystem.IsWindows() && nativeHandleDescriptor.Equals("HWND", StringComparison.OrdinalIgnoreCase))
                return new Win32NativeWindowBridge(nativeHandle);

            throw new InvalidOperationException($"Unsupported native Vulkan handle descriptor '{nativeHandleDescriptor}'.");
        }

        public abstract string[] GetRequiredInstanceExtensions();
        public abstract ulong CreateVulkanSurface(VulkanApi vk, IntPtr instance);
        public abstract bool TryGetDrawableSize(out int width, out int height);
        public abstract void Dispose();

        private sealed class X11NativeWindowBridge : NativeWindowBridge
        {
            private readonly IntPtr _window;
            private readonly IntPtr _display;

            public X11NativeWindowBridge(IntPtr window)
            {
                _window = window;
                _display = X11Api.XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                    throw new InvalidOperationException("XOpenDisplay failed while creating the Vulkan presenter.");
            }

            public override string[] GetRequiredInstanceExtensions()
                => ["VK_KHR_surface", "VK_KHR_xlib_surface"];

            public override unsafe ulong CreateVulkanSurface(VulkanApi vk, IntPtr instance)
            {
                VkXlibSurfaceCreateInfoKHR createInfo = new()
                {
                    SType = VulkanApi.VK_STRUCTURE_TYPE_XLIB_SURFACE_CREATE_INFO_KHR,
                    Dpy = _display,
                    Window = _window
                };
                ulong surface = 0;
                vk.Check(vk.CreateXlibSurfaceKHR(instance, &createInfo, IntPtr.Zero, out surface), "vkCreateXlibSurfaceKHR");
                return surface;
            }

            public override bool TryGetDrawableSize(out int width, out int height)
            {
                if (_display == IntPtr.Zero || _window == IntPtr.Zero)
                {
                    width = 0;
                    height = 0;
                    return false;
                }

                if (X11Api.XGetWindowAttributes(_display, _window, out XWindowAttributes attributes) == 0)
                {
                    width = 0;
                    height = 0;
                    return false;
                }

                width = Math.Max(0, attributes.Width);
                height = Math.Max(0, attributes.Height);
                return width > 0 && height > 0;
            }

            public override void Dispose()
            {
                if (_display != IntPtr.Zero)
                    X11Api.XCloseDisplay(_display);
            }
        }

        private sealed class Win32NativeWindowBridge : NativeWindowBridge
        {
            private readonly IntPtr _hwnd;
            private readonly IntPtr _hInstance;

            public Win32NativeWindowBridge(IntPtr hwnd)
            {
                _hwnd = hwnd;
                _hInstance = Win32Api.GetModuleHandle(null);
                if (_hInstance == IntPtr.Zero)
                    throw new InvalidOperationException("GetModuleHandle failed while creating the Vulkan presenter.");
            }

            public override string[] GetRequiredInstanceExtensions()
                => ["VK_KHR_surface", "VK_KHR_win32_surface"];

            public override unsafe ulong CreateVulkanSurface(VulkanApi vk, IntPtr instance)
            {
                VkWin32SurfaceCreateInfoKHR createInfo = new()
                {
                    SType = VulkanApi.VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR,
                    Hinstance = _hInstance,
                    Hwnd = _hwnd
                };
                ulong surface = 0;
                vk.Check(vk.CreateWin32SurfaceKHR(instance, &createInfo, IntPtr.Zero, out surface), "vkCreateWin32SurfaceKHR");
                return surface;
            }

            public override bool TryGetDrawableSize(out int width, out int height)
            {
                if (_hwnd == IntPtr.Zero || !Win32Api.GetClientRect(_hwnd, out Rect rect))
                {
                    width = 0;
                    height = 0;
                    return false;
                }

                width = Math.Max(0, rect.Right - rect.Left);
                height = Math.Max(0, rect.Bottom - rect.Top);
                return width > 0 && height > 0;
            }

            public override void Dispose()
            {
            }
        }
    }

    private sealed class VulkanBlitPresenter : IDisposable
    {
        private readonly NativeWindowBridge _nativeWindow;
        private readonly VulkanApi _vk = new();

        private IntPtr _instance;
        private ulong _surface;
        private IntPtr _physicalDevice;
        private IntPtr _device;
        private IntPtr _queue;
        private uint _queueFamilyIndex;

        private ulong _swapchain;
        private ulong[] _swapchainImages = Array.Empty<ulong>();
        private bool[] _swapchainImageInitialized = Array.Empty<bool>();
        private uint _swapchainFormat;
        private uint _swapchainColorSpace;
        private VkExtent2D _swapchainExtent;
        private int _drawableWidth;
        private int _drawableHeight;

        private ulong _commandPool;
        private IntPtr _commandBuffer;
        private ulong _imageAvailableSemaphore;
        private ulong _renderFinishedSemaphore;

        private ulong _stagingBuffer;
        private ulong _stagingMemory;
        private IntPtr _stagingMapped;
        private ulong _stagingCapacity;
        private bool _stagingHostCoherent;

        private ulong _uploadImage;
        private ulong _uploadImageMemory;
        private int _uploadWidth;
        private int _uploadHeight;
        private bool _uploadImageReady;

        public VulkanBlitPresenter(IntPtr nativeHandle, string nativeHandleDescriptor)
        {
            _nativeWindow = NativeWindowBridge.Create(nativeHandle, nativeHandleDescriptor);
            CreateInstance();
            _vk.LoadInstanceFunctions(_instance);
            CreateSurface();
            SelectPhysicalDevice();
            CreateLogicalDevice();
            CreateCommandObjects();
            CreateSyncObjects();
            TryUpdateDrawableSize();
            CreateSwapchain();
        }

        public bool HasPresentedFrame { get; private set; }
        public bool SharpPixelsEnabled { get; private set; } = true;
        public long LastUploadTicks { get; private set; }

        public unsafe bool TryUpdateDrawableSize()
        {
            int drawableWidth = 0;
            int drawableHeight = 0;
            if (!_nativeWindow.TryGetDrawableSize(out drawableWidth, out drawableHeight))
                return false;
            if (drawableWidth < 1 || drawableHeight < 1)
                return false;

            if (drawableWidth == _drawableWidth && drawableHeight == _drawableHeight)
                return false;

            _drawableWidth = drawableWidth;
            _drawableHeight = drawableHeight;
            return true;
        }

        public unsafe bool PresentFrame(byte[] frameBytes, int frameWidth, int frameHeight, int frameStride, bool sharpPixelsEnabled, bool frameDirty)
        {
            if (_drawableWidth <= 0 || _drawableHeight <= 0)
            {
                if (!TryUpdateDrawableSize())
                    return false;
            }

            EnsureSwapchainMatchesDrawable();

            if (frameDirty)
                UploadFrame(frameBytes, frameWidth, frameHeight, frameStride);
            else if (!_uploadImageReady)
                return false;

            if (!frameDirty && HasPresentedFrame && SharpPixelsEnabled == sharpPixelsEnabled)
                return false;

            SharpPixelsEnabled = sharpPixelsEnabled;
            DrawCurrentFrame(frameWidth, frameHeight);
            HasPresentedFrame = true;
            return frameDirty;
        }

        public void Dispose()
        {
            if (_device != IntPtr.Zero)
                _vk.DeviceWaitIdle(_device);

            DestroyUploadResources();
            DestroySwapchain();

            if (_imageAvailableSemaphore != 0)
            {
                _vk.DestroySemaphore(_device, _imageAvailableSemaphore, IntPtr.Zero);
                _imageAvailableSemaphore = 0;
            }

            if (_renderFinishedSemaphore != 0)
            {
                _vk.DestroySemaphore(_device, _renderFinishedSemaphore, IntPtr.Zero);
                _renderFinishedSemaphore = 0;
            }

            if (_commandPool != 0)
            {
                _vk.DestroyCommandPool(_device, _commandPool, IntPtr.Zero);
                _commandPool = 0;
                _commandBuffer = IntPtr.Zero;
            }

            if (_device != IntPtr.Zero)
            {
                _vk.DestroyDevice(_device, IntPtr.Zero);
                _device = IntPtr.Zero;
            }

            if (_surface != 0)
            {
                _vk.DestroySurfaceKHR(_instance, _surface, IntPtr.Zero);
                _surface = 0;
            }

            if (_instance != IntPtr.Zero)
            {
                _vk.DestroyInstance(_instance, IntPtr.Zero);
                _instance = IntPtr.Zero;
            }

            _nativeWindow.Dispose();
            _vk.Dispose();
        }

        private unsafe void CreateInstance()
        {
            string[] extensions = _nativeWindow.GetRequiredInstanceExtensions();

            IntPtr appName = Marshal.StringToHGlobalAnsi("EutherDrive");
            IntPtr engineName = Marshal.StringToHGlobalAnsi("EutherDrive");
            IntPtr[] extensionPtrs = new IntPtr[extensions.Length];
            for (int i = 0; i < extensions.Length; i++)
                extensionPtrs[i] = Marshal.StringToHGlobalAnsi(extensions[i]);

            try
            {
                VkApplicationInfo appInfo = new()
                {
                    SType = VulkanApi.VK_STRUCTURE_TYPE_APPLICATION_INFO,
                    PApplicationName = (byte*)appName,
                    ApplicationVersion = 1,
                    PEngineName = (byte*)engineName,
                    EngineVersion = 1,
                    ApiVersion = VulkanApi.VK_API_VERSION_1_0
                };

                fixed (IntPtr* pExtensionPtrs = extensionPtrs)
                {
                    VkInstanceCreateInfo createInfo = new()
                    {
                        SType = VulkanApi.VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                        PApplicationInfo = &appInfo,
                        EnabledExtensionCount = (uint)extensionPtrs.Length,
                        PpEnabledExtensionNames = (byte**)pExtensionPtrs
                    };

                    _vk.Check(_vk.CreateInstance(&createInfo, IntPtr.Zero, out _instance), "vkCreateInstance");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(appName);
                Marshal.FreeHGlobal(engineName);
                for (int i = 0; i < extensionPtrs.Length; i++)
                {
                    if (extensionPtrs[i] != IntPtr.Zero)
                        Marshal.FreeHGlobal(extensionPtrs[i]);
                }
            }
        }

        private unsafe void CreateSurface()
        {
            _surface = _nativeWindow.CreateVulkanSurface(_vk, _instance);
            if (_surface == 0)
                throw new InvalidOperationException("Vulkan surface creation returned a null surface.");
        }

        private unsafe void SelectPhysicalDevice()
        {
            uint count = 0;
            _vk.Check(_vk.EnumeratePhysicalDevices(_instance, ref count, null), "vkEnumeratePhysicalDevices(count)");
            if (count == 0)
                throw new InvalidOperationException("No Vulkan physical devices were found.");

            IntPtr* devices = stackalloc IntPtr[(int)count];
            _vk.Check(_vk.EnumeratePhysicalDevices(_instance, ref count, devices), "vkEnumeratePhysicalDevices(list)");
            for (int deviceIndex = 0; deviceIndex < count; deviceIndex++)
            {
                IntPtr candidate = devices[deviceIndex];
                if (!TryFindQueueFamily(candidate, out uint queueFamilyIndex))
                    continue;

                _physicalDevice = candidate;
                _queueFamilyIndex = queueFamilyIndex;
                return;
            }

            throw new InvalidOperationException("No Vulkan device with graphics+present support was found.");
        }

        private unsafe void CreateLogicalDevice()
        {
            float queuePriority = 1f;
            byte[] swapchainExtensionName = Encoding.ASCII.GetBytes("VK_KHR_swapchain\0");
            fixed (byte* pSwapchainExtension = swapchainExtensionName)
            {
                float* pQueuePriority = &queuePriority;
                VkDeviceQueueCreateInfo queueCreateInfo = new()
                {
                    SType = VulkanApi.VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                    QueueFamilyIndex = _queueFamilyIndex,
                    QueueCount = 1,
                    PQueuePriorities = pQueuePriority
                };

                byte** extensionNamePtrs = stackalloc byte*[1];
                extensionNamePtrs[0] = pSwapchainExtension;
                VkDeviceCreateInfo deviceCreateInfo = new()
                {
                    SType = VulkanApi.VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueCreateInfo,
                    EnabledExtensionCount = 1,
                    PpEnabledExtensionNames = extensionNamePtrs
                };

                _vk.Check(_vk.CreateDevice(_physicalDevice, &deviceCreateInfo, IntPtr.Zero, out _device), "vkCreateDevice");
                _vk.LoadDeviceFunctions(_device);
                _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
                if (_queue == IntPtr.Zero)
                    throw new InvalidOperationException("Vulkan device queue creation returned a null queue.");
            }
        }

        private unsafe void CreateCommandObjects()
        {
            VkCommandPoolCreateInfo commandPoolCreateInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
                Flags = VulkanApi.VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
                QueueFamilyIndex = _queueFamilyIndex
            };
            _vk.Check(_vk.CreateCommandPool(_device, &commandPoolCreateInfo, IntPtr.Zero, out _commandPool), "vkCreateCommandPool");

            VkCommandBufferAllocateInfo commandBufferAllocateInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                CommandPool = _commandPool,
                Level = VulkanApi.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                CommandBufferCount = 1
            };
            _vk.Check(_vk.AllocateCommandBuffers(_device, &commandBufferAllocateInfo, out _commandBuffer), "vkAllocateCommandBuffers");
        }

        private unsafe void CreateSyncObjects()
        {
            VkSemaphoreCreateInfo semaphoreCreateInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO
            };
            _vk.Check(_vk.CreateSemaphore(_device, &semaphoreCreateInfo, IntPtr.Zero, out _imageAvailableSemaphore), "vkCreateSemaphore(imageAvailable)");
            _vk.Check(_vk.CreateSemaphore(_device, &semaphoreCreateInfo, IntPtr.Zero, out _renderFinishedSemaphore), "vkCreateSemaphore(renderFinished)");
        }

        private unsafe bool TryFindQueueFamily(IntPtr physicalDevice, out uint queueFamilyIndex)
        {
            queueFamilyIndex = 0;
            uint queueFamilyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilyCount, null);
            if (queueFamilyCount == 0)
                return false;

            VkQueueFamilyProperties* queueFamilies = stackalloc VkQueueFamilyProperties[(int)queueFamilyCount];
            _vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilyCount, queueFamilies);
            for (uint index = 0; index < queueFamilyCount; index++)
            {
                bool hasGraphics = (queueFamilies[index].QueueFlags & VulkanApi.VK_QUEUE_GRAPHICS_BIT) != 0;
                if (!hasGraphics)
                    continue;

                _vk.Check(_vk.GetPhysicalDeviceSurfaceSupportKHR(physicalDevice, index, _surface, out uint presentSupported), "vkGetPhysicalDeviceSurfaceSupportKHR");
                if (presentSupported == 0)
                    continue;

                queueFamilyIndex = index;
                return true;
            }

            return false;
        }

        private unsafe void EnsureSwapchainMatchesDrawable()
        {
            if (_drawableWidth <= 0 || _drawableHeight <= 0)
                return;

            if (_swapchain == 0
                || _swapchainExtent.X != (uint)_drawableWidth
                || _swapchainExtent.Y != (uint)_drawableHeight)
            {
                CreateSwapchain();
            }
        }

        private unsafe void CreateSwapchain()
        {
            VkSurfaceCapabilitiesKHR surfaceCaps = default;
            _vk.Check(_vk.GetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, out surfaceCaps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

            uint formatCount = 0;
            _vk.Check(_vk.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref formatCount, null), "vkGetPhysicalDeviceSurfaceFormatsKHR(count)");
            if (formatCount == 0)
                throw new InvalidOperationException("The Vulkan surface reported no supported formats.");

            VkSurfaceFormatKHR* formats = stackalloc VkSurfaceFormatKHR[(int)formatCount];
            _vk.Check(_vk.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref formatCount, formats), "vkGetPhysicalDeviceSurfaceFormatsKHR(list)");
            VkSurfaceFormatKHR selectedFormat = formats[0];
            for (int i = 0; i < formatCount; i++)
            {
                if (formats[i].Format == VulkanApi.VK_FORMAT_B8G8R8A8_UNORM)
                {
                    selectedFormat = formats[i];
                    break;
                }
            }

            uint presentModeCount = 0;
            _vk.Check(_vk.GetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, _surface, ref presentModeCount, null), "vkGetPhysicalDeviceSurfacePresentModesKHR(count)");
            uint* presentModes = stackalloc uint[(int)Math.Max(1, presentModeCount)];
            if (presentModeCount > 0)
                _vk.Check(_vk.GetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, _surface, ref presentModeCount, presentModes), "vkGetPhysicalDeviceSurfacePresentModesKHR(list)");

            uint presentMode = VulkanApi.VK_PRESENT_MODE_FIFO_KHR;
            for (int i = 0; i < presentModeCount; i++)
            {
                if (presentModes[i] == VulkanApi.VK_PRESENT_MODE_FIFO_KHR)
                {
                    presentMode = presentModes[i];
                    break;
                }
            }

            if ((surfaceCaps.SupportedUsageFlags & VulkanApi.VK_IMAGE_USAGE_TRANSFER_DST_BIT) == 0)
                throw new InvalidOperationException("The Vulkan surface does not support transfer-destination swapchain images.");

            VkExtent2D imageExtent = ChooseSwapExtent(surfaceCaps);
            uint minImageCount = surfaceCaps.MinImageCount + 1;
            if (surfaceCaps.MaxImageCount > 0 && minImageCount > surfaceCaps.MaxImageCount)
                minImageCount = surfaceCaps.MaxImageCount;

            uint compositeAlpha = ChooseCompositeAlpha(surfaceCaps.SupportedCompositeAlpha);
            ulong oldSwapchain = _swapchain;

            VkSwapchainCreateInfoKHR swapchainCreateInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
                Surface = _surface,
                MinImageCount = minImageCount,
                ImageFormat = selectedFormat.Format,
                ImageColorSpace = selectedFormat.ColorSpace,
                ImageExtent = imageExtent,
                ImageArrayLayers = 1,
                ImageUsage = VulkanApi.VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                ImageSharingMode = VulkanApi.VK_SHARING_MODE_EXCLUSIVE,
                PreTransform = surfaceCaps.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = presentMode,
                Clipped = 1,
                OldSwapchain = oldSwapchain
            };

            ulong newSwapchain;
            _vk.Check(_vk.CreateSwapchainKHR(_device, &swapchainCreateInfo, IntPtr.Zero, out newSwapchain), "vkCreateSwapchainKHR");

            uint imageCount = 0;
            _vk.Check(_vk.GetSwapchainImagesKHR(_device, newSwapchain, ref imageCount, null), "vkGetSwapchainImagesKHR(count)");
            ulong[] images = new ulong[imageCount];
            fixed (ulong* pImages = images)
                _vk.Check(_vk.GetSwapchainImagesKHR(_device, newSwapchain, ref imageCount, pImages), "vkGetSwapchainImagesKHR(list)");

            if (oldSwapchain != 0)
            {
                _vk.DeviceWaitIdle(_device);
                _vk.DestroySwapchainKHR(_device, oldSwapchain, IntPtr.Zero);
            }

            _swapchain = newSwapchain;
            _swapchainImages = images;
            _swapchainImageInitialized = new bool[images.Length];
            _swapchainFormat = selectedFormat.Format;
            _swapchainColorSpace = selectedFormat.ColorSpace;
            _swapchainExtent = imageExtent;
            HasPresentedFrame = false;
        }

        private unsafe void DestroySwapchain()
        {
            if (_swapchain != 0)
            {
                _vk.DestroySwapchainKHR(_device, _swapchain, IntPtr.Zero);
                _swapchain = 0;
            }

            _swapchainImages = Array.Empty<ulong>();
            _swapchainImageInitialized = Array.Empty<bool>();
            _swapchainExtent = default;
        }

        private unsafe void EnsureUploadResources(int width, int height, int stride)
        {
            ulong requiredBytes = (ulong)Math.Max(0, stride) * (ulong)Math.Max(0, height);
            if (_uploadImage != 0
                && _uploadWidth == width
                && _uploadHeight == height
                && _stagingCapacity >= requiredBytes)
            {
                return;
            }

            DestroyUploadResources();

            VkBufferCreateInfo stagingBufferCreateInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
                Size = requiredBytes,
                Usage = VulkanApi.VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
                ImageSharingMode = VulkanApi.VK_SHARING_MODE_EXCLUSIVE
            };
            _vk.Check(_vk.CreateBuffer(_device, &stagingBufferCreateInfo, IntPtr.Zero, out _stagingBuffer), "vkCreateBuffer");

            VkMemoryRequirements stagingRequirements;
            _vk.GetBufferMemoryRequirements(_device, _stagingBuffer, out stagingRequirements);
            uint stagingMemoryType = FindMemoryType(stagingRequirements.MemoryTypeBits, VulkanApi.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VulkanApi.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, out uint stagingMemoryFlags);
            _stagingHostCoherent = (stagingMemoryFlags & VulkanApi.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) != 0;

            VkMemoryAllocateInfo stagingAllocInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                AllocationSize = stagingRequirements.Size,
                MemoryTypeIndex = stagingMemoryType
            };
            _vk.Check(_vk.AllocateMemory(_device, &stagingAllocInfo, IntPtr.Zero, out _stagingMemory), "vkAllocateMemory(staging)");
            _vk.Check(_vk.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0), "vkBindBufferMemory");
            _vk.Check(_vk.MapMemory(_device, _stagingMemory, 0, stagingRequirements.Size, 0, out _stagingMapped), "vkMapMemory");
            _stagingCapacity = stagingRequirements.Size;

            VkImageCreateInfo uploadImageCreateInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
                ImageType = VulkanApi.VK_IMAGE_TYPE_2D,
                Format = VulkanApi.VK_FORMAT_B8G8R8A8_UNORM,
                Extent = new VkExtent3D((uint)width, (uint)height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VulkanApi.VK_SAMPLE_COUNT_1_BIT,
                Tiling = VulkanApi.VK_IMAGE_TILING_OPTIMAL,
                Usage = VulkanApi.VK_IMAGE_USAGE_TRANSFER_DST_BIT | VulkanApi.VK_IMAGE_USAGE_TRANSFER_SRC_BIT,
                SharingMode = VulkanApi.VK_SHARING_MODE_EXCLUSIVE,
                InitialLayout = VulkanApi.VK_IMAGE_LAYOUT_UNDEFINED
            };
            _vk.Check(_vk.CreateImage(_device, &uploadImageCreateInfo, IntPtr.Zero, out _uploadImage), "vkCreateImage");

            VkMemoryRequirements imageRequirements;
            _vk.GetImageMemoryRequirements(_device, _uploadImage, out imageRequirements);
            uint imageMemoryType = FindMemoryType(imageRequirements.MemoryTypeBits, VulkanApi.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, out _);
            VkMemoryAllocateInfo imageAllocInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                AllocationSize = imageRequirements.Size,
                MemoryTypeIndex = imageMemoryType
            };
            _vk.Check(_vk.AllocateMemory(_device, &imageAllocInfo, IntPtr.Zero, out _uploadImageMemory), "vkAllocateMemory(image)");
            _vk.Check(_vk.BindImageMemory(_device, _uploadImage, _uploadImageMemory, 0), "vkBindImageMemory");

            _uploadWidth = width;
            _uploadHeight = height;
            _uploadImageReady = false;
            HasPresentedFrame = false;
        }

        private unsafe void DestroyUploadResources()
        {
            if (_stagingMapped != IntPtr.Zero && _stagingMemory != 0)
            {
                _vk.UnmapMemory(_device, _stagingMemory);
                _stagingMapped = IntPtr.Zero;
            }

            if (_uploadImage != 0)
            {
                _vk.DestroyImage(_device, _uploadImage, IntPtr.Zero);
                _uploadImage = 0;
            }

            if (_uploadImageMemory != 0)
            {
                _vk.FreeMemory(_device, _uploadImageMemory, IntPtr.Zero);
                _uploadImageMemory = 0;
            }

            if (_stagingBuffer != 0)
            {
                _vk.DestroyBuffer(_device, _stagingBuffer, IntPtr.Zero);
                _stagingBuffer = 0;
            }

            if (_stagingMemory != 0)
            {
                _vk.FreeMemory(_device, _stagingMemory, IntPtr.Zero);
                _stagingMemory = 0;
            }

            _stagingCapacity = 0;
            _uploadWidth = 0;
            _uploadHeight = 0;
            _uploadImageReady = false;
        }

        private unsafe void UploadFrame(byte[] frameBytes, int frameWidth, int frameHeight, int frameStride)
        {
            EnsureUploadResources(frameWidth, frameHeight, frameStride);

            ulong totalBytes = (ulong)Math.Max(0, frameStride) * (ulong)Math.Max(0, frameHeight);
            if (totalBytes == 0 || _stagingMapped == IntPtr.Zero)
                return;

            LastUploadTicks = 0;
            long uploadStart = Stopwatch.GetTimestamp();
            fixed (byte* pFrame = frameBytes)
            {
                Buffer.MemoryCopy(pFrame, (void*)_stagingMapped, _stagingCapacity, totalBytes);
            }

            if (!_stagingHostCoherent)
            {
                VkMappedMemoryRange mappedRange = new()
                {
                    SType = VulkanApi.VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE,
                    Memory = _stagingMemory,
                    Offset = 0,
                    Size = totalBytes
                };
                _vk.Check(_vk.FlushMappedMemoryRanges(_device, 1, &mappedRange), "vkFlushMappedMemoryRanges");
            }

            LastUploadTicks = Stopwatch.GetTimestamp() - uploadStart;
        }

        private unsafe void DrawCurrentFrame(int frameWidth, int frameHeight)
        {
            if (_swapchain == 0 || _uploadImage == 0)
                return;

            uint imageIndex = 0;
            int acquireResult = _vk.AcquireNextImageKHR(_device, _swapchain, ulong.MaxValue, _imageAvailableSemaphore, 0, out imageIndex);
            if (acquireResult == VulkanApi.VK_ERROR_OUT_OF_DATE_KHR)
            {
                CreateSwapchain();
                return;
            }

            _vk.Check(acquireResult, "vkAcquireNextImageKHR", allowSuboptimal: true);

            _vk.Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");

            VkCommandBufferBeginInfo beginInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                Flags = VulkanApi.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT
            };
            _vk.Check(_vk.BeginCommandBuffer(_commandBuffer, &beginInfo), "vkBeginCommandBuffer");

            VkImageSubresourceRange colorRange = new()
            {
                AspectMask = VulkanApi.VK_IMAGE_ASPECT_COLOR_BIT,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            VkImageMemoryBarrier uploadToTransferDst = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                SrcAccessMask = _uploadImageReady ? VulkanApi.VK_ACCESS_TRANSFER_READ_BIT : 0,
                DstAccessMask = VulkanApi.VK_ACCESS_TRANSFER_WRITE_BIT,
                OldLayout = _uploadImageReady ? VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL : VulkanApi.VK_IMAGE_LAYOUT_UNDEFINED,
                NewLayout = VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                SrcQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                Image = _uploadImage,
                SubresourceRange = colorRange
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                _uploadImageReady ? VulkanApi.VK_PIPELINE_STAGE_TRANSFER_BIT : VulkanApi.VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                VulkanApi.VK_PIPELINE_STAGE_TRANSFER_BIT,
                0,
                0,
                null,
                0,
                null,
                1,
                &uploadToTransferDst);

            VkBufferImageCopy uploadRegion = new()
            {
                BufferOffset = 0,
                BufferRowLength = (uint)frameWidth,
                BufferImageHeight = (uint)frameHeight,
                ImageSubresource = new VkImageSubresourceLayers
                {
                    AspectMask = VulkanApi.VK_IMAGE_ASPECT_COLOR_BIT,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageOffset = new VkOffset3D(0, 0, 0),
                ImageExtent = new VkExtent3D((uint)frameWidth, (uint)frameHeight, 1)
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _stagingBuffer,
                _uploadImage,
                VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                1,
                &uploadRegion);

            VkImageMemoryBarrier uploadToTransferSrc = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                SrcAccessMask = VulkanApi.VK_ACCESS_TRANSFER_WRITE_BIT,
                DstAccessMask = VulkanApi.VK_ACCESS_TRANSFER_READ_BIT,
                OldLayout = VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                NewLayout = VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                SrcQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                Image = _uploadImage,
                SubresourceRange = colorRange
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                VulkanApi.VK_PIPELINE_STAGE_TRANSFER_BIT,
                VulkanApi.VK_PIPELINE_STAGE_TRANSFER_BIT,
                0,
                0,
                null,
                0,
                null,
                1,
                &uploadToTransferSrc);

            bool swapchainImageInitialized = imageIndex < _swapchainImageInitialized.Length && _swapchainImageInitialized[imageIndex];
            VkImageMemoryBarrier swapchainToTransferDst = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                SrcAccessMask = 0,
                DstAccessMask = VulkanApi.VK_ACCESS_TRANSFER_WRITE_BIT,
                OldLayout = swapchainImageInitialized ? VulkanApi.VK_IMAGE_LAYOUT_PRESENT_SRC_KHR : VulkanApi.VK_IMAGE_LAYOUT_UNDEFINED,
                NewLayout = VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                SrcQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = colorRange
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                swapchainImageInitialized ? VulkanApi.VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT : VulkanApi.VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                VulkanApi.VK_PIPELINE_STAGE_TRANSFER_BIT,
                0,
                0,
                null,
                0,
                null,
                1,
                &swapchainToTransferDst);

            VkClearColorValue clearColor = default;
            _vk.CmdClearColorImage(
                _commandBuffer,
                _swapchainImages[imageIndex],
                VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                &clearColor,
                1,
                &colorRange);

            VkImageBlit blitRegion = new()
            {
                SrcSubresource = new VkImageSubresourceLayers
                {
                    AspectMask = VulkanApi.VK_IMAGE_ASPECT_COLOR_BIT,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstSubresource = new VkImageSubresourceLayers
                {
                    AspectMask = VulkanApi.VK_IMAGE_ASPECT_COLOR_BIT,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blitRegion.SrcOffsets0 = new VkOffset3D(0, 0, 0);
            blitRegion.SrcOffsets1 = new VkOffset3D(frameWidth, frameHeight, 1);
            blitRegion.DstOffsets0 = new VkOffset3D(0, 0, 0);
            blitRegion.DstOffsets1 = new VkOffset3D((int)_swapchainExtent.X, (int)_swapchainExtent.Y, 1);
            _vk.CmdBlitImage(
                _commandBuffer,
                _uploadImage,
                VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                _swapchainImages[imageIndex],
                VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                1,
                &blitRegion,
                SharpPixelsEnabled ? VulkanApi.VK_FILTER_NEAREST : VulkanApi.VK_FILTER_LINEAR);

            VkImageMemoryBarrier swapchainToPresent = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                SrcAccessMask = VulkanApi.VK_ACCESS_TRANSFER_WRITE_BIT,
                DstAccessMask = 0,
                OldLayout = VulkanApi.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                NewLayout = VulkanApi.VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                SrcQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VulkanApi.VK_QUEUE_FAMILY_IGNORED,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = colorRange
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                VulkanApi.VK_PIPELINE_STAGE_TRANSFER_BIT,
                VulkanApi.VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                0,
                0,
                null,
                0,
                null,
                1,
                &swapchainToPresent);

            _vk.Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            ulong waitSemaphore = _imageAvailableSemaphore;
            uint waitStage = VulkanApi.VK_PIPELINE_STAGE_TRANSFER_BIT;
            ulong signalSemaphore = _renderFinishedSemaphore;
            IntPtr commandBuffer = _commandBuffer;
            VkSubmitInfo submitInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_SUBMIT_INFO,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &waitSemaphore,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &signalSemaphore
            };
            _vk.Check(_vk.QueueSubmit(_queue, 1, &submitInfo, 0), "vkQueueSubmit");

            ulong swapchain = _swapchain;
            VkPresentInfoKHR presentInfo = new()
            {
                SType = VulkanApi.VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &signalSemaphore,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex
            };
            int presentResult = _vk.QueuePresentKHR(_queue, &presentInfo);
            if (presentResult == VulkanApi.VK_ERROR_OUT_OF_DATE_KHR || presentResult == VulkanApi.VK_SUBOPTIMAL_KHR)
            {
                _vk.QueueWaitIdle(_queue);
                CreateSwapchain();
                return;
            }

            _vk.Check(presentResult, "vkQueuePresentKHR");
            _vk.Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");

            _uploadImageReady = true;
            if (imageIndex < _swapchainImageInitialized.Length)
                _swapchainImageInitialized[imageIndex] = true;
        }

        private unsafe uint FindMemoryType(uint typeBits, uint requiredFlags, out uint matchedFlags)
        {
            VkPhysicalDeviceMemoryProperties memoryProperties = default;
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out memoryProperties);

            for (uint index = 0; index < memoryProperties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) == 0)
                    continue;

                VkMemoryType memoryType = memoryProperties.GetMemoryType((int)index);
                if ((memoryType.PropertyFlags & requiredFlags) != requiredFlags)
                    continue;

                matchedFlags = memoryType.PropertyFlags;
                return index;
            }

            for (uint index = 0; index < memoryProperties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) == 0)
                    continue;

                VkMemoryType memoryType = memoryProperties.GetMemoryType((int)index);
                matchedFlags = memoryType.PropertyFlags;
                return index;
            }

            throw new InvalidOperationException("Unable to find a compatible Vulkan memory type.");
        }

        private VkExtent2D ChooseSwapExtent(VkSurfaceCapabilitiesKHR surfaceCaps)
        {
            if (surfaceCaps.CurrentExtent.X != uint.MaxValue && surfaceCaps.CurrentExtent.Y != uint.MaxValue)
                return surfaceCaps.CurrentExtent;

            uint width = (uint)Math.Clamp(_drawableWidth, (int)surfaceCaps.MinImageExtent.X, (int)surfaceCaps.MaxImageExtent.X);
            uint height = (uint)Math.Clamp(_drawableHeight, (int)surfaceCaps.MinImageExtent.Y, (int)surfaceCaps.MaxImageExtent.Y);
            return new VkExtent2D(width, height);
        }

        private static uint ChooseCompositeAlpha(uint supportedCompositeAlpha)
        {
            if ((supportedCompositeAlpha & VulkanApi.VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR) != 0)
                return VulkanApi.VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
            if ((supportedCompositeAlpha & VulkanApi.VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR) != 0)
                return VulkanApi.VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR;
            if ((supportedCompositeAlpha & VulkanApi.VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR) != 0)
                return VulkanApi.VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR;
            if ((supportedCompositeAlpha & VulkanApi.VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR) != 0)
                return VulkanApi.VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR;
            return VulkanApi.VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
        }
    }

    private sealed class VulkanApi : IDisposable
    {
        public const uint VK_API_VERSION_1_0 = 0x00400000;
        public const int VK_SUCCESS = 0;
        public const int VK_SUBOPTIMAL_KHR = 1000001003;
        public const int VK_ERROR_OUT_OF_DATE_KHR = -1000001004;

        public const uint VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
        public const uint VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
        public const uint VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO = 2;
        public const uint VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 3;
        public const uint VK_STRUCTURE_TYPE_SUBMIT_INFO = 4;
        public const uint VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO = 5;
        public const uint VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE = 6;
        public const uint VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO = 12;
        public const uint VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO = 14;
        public const uint VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO = 39;
        public const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO = 42;
        public const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO = 43;
        public const uint VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO = 44;
        public const uint VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER = 45;
        public const uint VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR = 1000001000;
        public const uint VK_STRUCTURE_TYPE_PRESENT_INFO_KHR = 1000001001;
        public const uint VK_STRUCTURE_TYPE_XLIB_SURFACE_CREATE_INFO_KHR = 1000004000;
        public const uint VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR = 1000009000;

        public const uint VK_QUEUE_GRAPHICS_BIT = 0x00000001;
        public const uint VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT = 0x00000002;
        public const uint VK_COMMAND_BUFFER_LEVEL_PRIMARY = 0;
        public const uint VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT = 0x00000001;
        public const uint VK_BUFFER_USAGE_TRANSFER_SRC_BIT = 0x00000001;
        public const uint VK_IMAGE_USAGE_TRANSFER_SRC_BIT = 0x00000001;
        public const uint VK_IMAGE_USAGE_TRANSFER_DST_BIT = 0x00000002;
        public const uint VK_SHARING_MODE_EXCLUSIVE = 0;
        public const uint VK_IMAGE_TYPE_2D = 1;
        public const uint VK_FORMAT_B8G8R8A8_UNORM = 44;
        public const uint VK_IMAGE_TILING_OPTIMAL = 0;
        public const uint VK_IMAGE_LAYOUT_UNDEFINED = 0;
        public const uint VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL = 6;
        public const uint VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL = 7;
        public const uint VK_IMAGE_LAYOUT_PRESENT_SRC_KHR = 1000001002;
        public const uint VK_ACCESS_TRANSFER_READ_BIT = 0x00000800;
        public const uint VK_ACCESS_TRANSFER_WRITE_BIT = 0x00001000;
        public const uint VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT = 0x00000001;
        public const uint VK_PIPELINE_STAGE_TRANSFER_BIT = 0x00001000;
        public const uint VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT = 0x00002000;
        public const uint VK_IMAGE_ASPECT_COLOR_BIT = 0x00000001;
        public const uint VK_SAMPLE_COUNT_1_BIT = 0x00000001;
        public const uint VK_PRESENT_MODE_FIFO_KHR = 2;
        public const uint VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR = 0x00000001;
        public const uint VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR = 0x00000002;
        public const uint VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR = 0x00000004;
        public const uint VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR = 0x00000008;
        public const uint VK_FILTER_NEAREST = 0;
        public const uint VK_FILTER_LINEAR = 1;
        public const uint VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT = 0x00000001;
        public const uint VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT = 0x00000002;
        public const uint VK_MEMORY_PROPERTY_HOST_COHERENT_BIT = 0x00000004;
        public const uint VK_QUEUE_FAMILY_IGNORED = 0xFFFFFFFF;

        private readonly IntPtr _libraryHandle;
        private readonly vkGetInstanceProcAddrDelegate _getInstanceProcAddr;
        private readonly vkGetDeviceProcAddrDelegate _getDeviceProcAddr;

        public readonly vkCreateInstanceDelegate CreateInstance;
        public vkDestroyInstanceDelegate DestroyInstance = null!;
        public vkEnumeratePhysicalDevicesDelegate EnumeratePhysicalDevices = null!;
        public vkGetPhysicalDeviceQueueFamilyPropertiesDelegate GetPhysicalDeviceQueueFamilyProperties = null!;
        public vkGetPhysicalDeviceSurfaceSupportKHRDelegate GetPhysicalDeviceSurfaceSupportKHR = null!;
        public vkGetPhysicalDeviceSurfaceCapabilitiesKHRDelegate GetPhysicalDeviceSurfaceCapabilitiesKHR = null!;
        public vkGetPhysicalDeviceSurfaceFormatsKHRDelegate GetPhysicalDeviceSurfaceFormatsKHR = null!;
        public vkGetPhysicalDeviceSurfacePresentModesKHRDelegate GetPhysicalDeviceSurfacePresentModesKHR = null!;
        public vkGetPhysicalDeviceMemoryPropertiesDelegate GetPhysicalDeviceMemoryProperties = null!;
        public vkCreateDeviceDelegate CreateDevice = null!;
        public vkCreateXlibSurfaceKHRDelegate CreateXlibSurfaceKHR = null!;
        public vkCreateWin32SurfaceKHRDelegate CreateWin32SurfaceKHR = null!;
        public vkDestroySurfaceKHRDelegate DestroySurfaceKHR = null!;
        public vkDestroyDeviceDelegate DestroyDevice = null!;
        public vkGetDeviceQueueDelegate GetDeviceQueue = null!;
        public vkCreateCommandPoolDelegate CreateCommandPool = null!;
        public vkDestroyCommandPoolDelegate DestroyCommandPool = null!;
        public vkAllocateCommandBuffersDelegate AllocateCommandBuffers = null!;
        public vkResetCommandBufferDelegate ResetCommandBuffer = null!;
        public vkBeginCommandBufferDelegate BeginCommandBuffer = null!;
        public vkEndCommandBufferDelegate EndCommandBuffer = null!;
        public vkCreateSemaphoreDelegate CreateSemaphore = null!;
        public vkDestroySemaphoreDelegate DestroySemaphore = null!;
        public vkCreateSwapchainKHRDelegate CreateSwapchainKHR = null!;
        public vkDestroySwapchainKHRDelegate DestroySwapchainKHR = null!;
        public vkGetSwapchainImagesKHRDelegate GetSwapchainImagesKHR = null!;
        public vkAcquireNextImageKHRDelegate AcquireNextImageKHR = null!;
        public vkQueuePresentKHRDelegate QueuePresentKHR = null!;
        public vkCreateBufferDelegate CreateBuffer = null!;
        public vkDestroyBufferDelegate DestroyBuffer = null!;
        public vkGetBufferMemoryRequirementsDelegate GetBufferMemoryRequirements = null!;
        public vkAllocateMemoryDelegate AllocateMemory = null!;
        public vkFreeMemoryDelegate FreeMemory = null!;
        public vkBindBufferMemoryDelegate BindBufferMemory = null!;
        public vkMapMemoryDelegate MapMemory = null!;
        public vkUnmapMemoryDelegate UnmapMemory = null!;
        public vkFlushMappedMemoryRangesDelegate FlushMappedMemoryRanges = null!;
        public vkCreateImageDelegate CreateImage = null!;
        public vkDestroyImageDelegate DestroyImage = null!;
        public vkGetImageMemoryRequirementsDelegate GetImageMemoryRequirements = null!;
        public vkBindImageMemoryDelegate BindImageMemory = null!;
        public vkCmdPipelineBarrierDelegate CmdPipelineBarrier = null!;
        public vkCmdCopyBufferToImageDelegate CmdCopyBufferToImage = null!;
        public vkCmdClearColorImageDelegate CmdClearColorImage = null!;
        public vkCmdBlitImageDelegate CmdBlitImage = null!;
        public vkQueueSubmitDelegate QueueSubmit = null!;
        public vkQueueWaitIdleDelegate QueueWaitIdle = null!;
        public vkDeviceWaitIdleDelegate DeviceWaitIdle = null!;

        public VulkanApi()
        {
            _libraryHandle = LoadVulkanLibrary();
            _getInstanceProcAddr = LoadExport<vkGetInstanceProcAddrDelegate>("vkGetInstanceProcAddr");
            _getDeviceProcAddr = LoadExport<vkGetDeviceProcAddrDelegate>("vkGetDeviceProcAddr");
            CreateInstance = LoadGlobal<vkCreateInstanceDelegate>("vkCreateInstance");
        }

        public void LoadInstanceFunctions(IntPtr instance)
        {
            DestroyInstance = LoadInstance<vkDestroyInstanceDelegate>(instance, "vkDestroyInstance");
            EnumeratePhysicalDevices = LoadInstance<vkEnumeratePhysicalDevicesDelegate>(instance, "vkEnumeratePhysicalDevices");
            GetPhysicalDeviceQueueFamilyProperties = LoadInstance<vkGetPhysicalDeviceQueueFamilyPropertiesDelegate>(instance, "vkGetPhysicalDeviceQueueFamilyProperties");
            GetPhysicalDeviceSurfaceSupportKHR = LoadInstance<vkGetPhysicalDeviceSurfaceSupportKHRDelegate>(instance, "vkGetPhysicalDeviceSurfaceSupportKHR");
            GetPhysicalDeviceSurfaceCapabilitiesKHR = LoadInstance<vkGetPhysicalDeviceSurfaceCapabilitiesKHRDelegate>(instance, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            GetPhysicalDeviceSurfaceFormatsKHR = LoadInstance<vkGetPhysicalDeviceSurfaceFormatsKHRDelegate>(instance, "vkGetPhysicalDeviceSurfaceFormatsKHR");
            GetPhysicalDeviceSurfacePresentModesKHR = LoadInstance<vkGetPhysicalDeviceSurfacePresentModesKHRDelegate>(instance, "vkGetPhysicalDeviceSurfacePresentModesKHR");
            GetPhysicalDeviceMemoryProperties = LoadInstance<vkGetPhysicalDeviceMemoryPropertiesDelegate>(instance, "vkGetPhysicalDeviceMemoryProperties");
            CreateDevice = LoadInstance<vkCreateDeviceDelegate>(instance, "vkCreateDevice");
            DestroySurfaceKHR = LoadInstance<vkDestroySurfaceKHRDelegate>(instance, "vkDestroySurfaceKHR");
            if (OperatingSystem.IsLinux())
                CreateXlibSurfaceKHR = LoadInstance<vkCreateXlibSurfaceKHRDelegate>(instance, "vkCreateXlibSurfaceKHR");
            if (OperatingSystem.IsWindows())
                CreateWin32SurfaceKHR = LoadInstance<vkCreateWin32SurfaceKHRDelegate>(instance, "vkCreateWin32SurfaceKHR");
        }

        public void LoadDeviceFunctions(IntPtr device)
        {
            DestroyDevice = LoadDevice<vkDestroyDeviceDelegate>(device, "vkDestroyDevice");
            GetDeviceQueue = LoadDevice<vkGetDeviceQueueDelegate>(device, "vkGetDeviceQueue");
            CreateCommandPool = LoadDevice<vkCreateCommandPoolDelegate>(device, "vkCreateCommandPool");
            DestroyCommandPool = LoadDevice<vkDestroyCommandPoolDelegate>(device, "vkDestroyCommandPool");
            AllocateCommandBuffers = LoadDevice<vkAllocateCommandBuffersDelegate>(device, "vkAllocateCommandBuffers");
            ResetCommandBuffer = LoadDevice<vkResetCommandBufferDelegate>(device, "vkResetCommandBuffer");
            BeginCommandBuffer = LoadDevice<vkBeginCommandBufferDelegate>(device, "vkBeginCommandBuffer");
            EndCommandBuffer = LoadDevice<vkEndCommandBufferDelegate>(device, "vkEndCommandBuffer");
            CreateSemaphore = LoadDevice<vkCreateSemaphoreDelegate>(device, "vkCreateSemaphore");
            DestroySemaphore = LoadDevice<vkDestroySemaphoreDelegate>(device, "vkDestroySemaphore");
            CreateSwapchainKHR = LoadDevice<vkCreateSwapchainKHRDelegate>(device, "vkCreateSwapchainKHR");
            DestroySwapchainKHR = LoadDevice<vkDestroySwapchainKHRDelegate>(device, "vkDestroySwapchainKHR");
            GetSwapchainImagesKHR = LoadDevice<vkGetSwapchainImagesKHRDelegate>(device, "vkGetSwapchainImagesKHR");
            AcquireNextImageKHR = LoadDevice<vkAcquireNextImageKHRDelegate>(device, "vkAcquireNextImageKHR");
            QueuePresentKHR = LoadDevice<vkQueuePresentKHRDelegate>(device, "vkQueuePresentKHR");
            CreateBuffer = LoadDevice<vkCreateBufferDelegate>(device, "vkCreateBuffer");
            DestroyBuffer = LoadDevice<vkDestroyBufferDelegate>(device, "vkDestroyBuffer");
            GetBufferMemoryRequirements = LoadDevice<vkGetBufferMemoryRequirementsDelegate>(device, "vkGetBufferMemoryRequirements");
            AllocateMemory = LoadDevice<vkAllocateMemoryDelegate>(device, "vkAllocateMemory");
            FreeMemory = LoadDevice<vkFreeMemoryDelegate>(device, "vkFreeMemory");
            BindBufferMemory = LoadDevice<vkBindBufferMemoryDelegate>(device, "vkBindBufferMemory");
            MapMemory = LoadDevice<vkMapMemoryDelegate>(device, "vkMapMemory");
            UnmapMemory = LoadDevice<vkUnmapMemoryDelegate>(device, "vkUnmapMemory");
            FlushMappedMemoryRanges = LoadDevice<vkFlushMappedMemoryRangesDelegate>(device, "vkFlushMappedMemoryRanges");
            CreateImage = LoadDevice<vkCreateImageDelegate>(device, "vkCreateImage");
            DestroyImage = LoadDevice<vkDestroyImageDelegate>(device, "vkDestroyImage");
            GetImageMemoryRequirements = LoadDevice<vkGetImageMemoryRequirementsDelegate>(device, "vkGetImageMemoryRequirements");
            BindImageMemory = LoadDevice<vkBindImageMemoryDelegate>(device, "vkBindImageMemory");
            CmdPipelineBarrier = LoadDevice<vkCmdPipelineBarrierDelegate>(device, "vkCmdPipelineBarrier");
            CmdCopyBufferToImage = LoadDevice<vkCmdCopyBufferToImageDelegate>(device, "vkCmdCopyBufferToImage");
            CmdClearColorImage = LoadDevice<vkCmdClearColorImageDelegate>(device, "vkCmdClearColorImage");
            CmdBlitImage = LoadDevice<vkCmdBlitImageDelegate>(device, "vkCmdBlitImage");
            QueueSubmit = LoadDevice<vkQueueSubmitDelegate>(device, "vkQueueSubmit");
            QueueWaitIdle = LoadDevice<vkQueueWaitIdleDelegate>(device, "vkQueueWaitIdle");
            DeviceWaitIdle = LoadDevice<vkDeviceWaitIdleDelegate>(device, "vkDeviceWaitIdle");
        }

        public void Dispose()
        {
            if (_libraryHandle != IntPtr.Zero)
                NativeLibrary.Free(_libraryHandle);
        }

        public void Check(int result, string operation, bool allowSuboptimal = false)
        {
            if (result == VK_SUCCESS)
                return;
            if (allowSuboptimal && result == VK_SUBOPTIMAL_KHR)
                return;
            throw new InvalidOperationException($"{operation} failed with Vulkan result {result}.");
        }

        private static IntPtr LoadVulkanLibrary()
        {
            string[] names = OperatingSystem.IsWindows()
                ? ["vulkan-1.dll", "vulkan-1"]
                : ["libvulkan.so.1", "libvulkan.so", "vulkan"];

            for (int i = 0; i < names.Length; i++)
            {
                if (NativeLibrary.TryLoad(names[i], out IntPtr handle))
                    return handle;
            }

            throw new InvalidOperationException("Unable to load the Vulkan loader library.");
        }

        private T LoadExport<T>(string name) where T : Delegate
        {
            IntPtr proc = NativeLibrary.GetExport(_libraryHandle, name);
            return Marshal.GetDelegateForFunctionPointer<T>(proc);
        }

        private unsafe T LoadGlobal<T>(string name) where T : Delegate
        {
            IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
            try
            {
                IntPtr proc = _getInstanceProcAddr(IntPtr.Zero, (byte*)namePtr);
                if (proc == IntPtr.Zero)
                    throw new InvalidOperationException($"Vulkan entry point '{name}' was not found.");
                return Marshal.GetDelegateForFunctionPointer<T>(proc);
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
            }
        }

        private unsafe T LoadInstance<T>(IntPtr instance, string name) where T : Delegate
        {
            IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
            try
            {
                IntPtr proc = _getInstanceProcAddr(instance, (byte*)namePtr);
                if (proc == IntPtr.Zero)
                    throw new InvalidOperationException($"Vulkan instance entry point '{name}' was not found.");
                return Marshal.GetDelegateForFunctionPointer<T>(proc);
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
            }
        }

        private unsafe T LoadDevice<T>(IntPtr device, string name) where T : Delegate
        {
            IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
            try
            {
                IntPtr proc = _getDeviceProcAddr(device, (byte*)namePtr);
                if (proc == IntPtr.Zero)
                    throw new InvalidOperationException($"Vulkan device entry point '{name}' was not found.");
                return Marshal.GetDelegateForFunctionPointer<T>(proc);
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate IntPtr vkGetInstanceProcAddrDelegate(IntPtr instance, byte* pName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate IntPtr vkGetDeviceProcAddrDelegate(IntPtr device, byte* pName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateInstanceDelegate(VkInstanceCreateInfo* pCreateInfo, IntPtr pAllocator, out IntPtr pInstance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroyInstanceDelegate(IntPtr instance, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkEnumeratePhysicalDevicesDelegate(IntPtr instance, ref uint pPhysicalDeviceCount, IntPtr* pPhysicalDevices);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void vkGetPhysicalDeviceQueueFamilyPropertiesDelegate(IntPtr physicalDevice, ref uint pQueueFamilyPropertyCount, VkQueueFamilyProperties* pQueueFamilyProperties);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkGetPhysicalDeviceSurfaceSupportKHRDelegate(IntPtr physicalDevice, uint queueFamilyIndex, ulong surface, out uint pSupported);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkGetPhysicalDeviceSurfaceCapabilitiesKHRDelegate(IntPtr physicalDevice, ulong surface, out VkSurfaceCapabilitiesKHR pSurfaceCapabilities);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkGetPhysicalDeviceSurfaceFormatsKHRDelegate(IntPtr physicalDevice, ulong surface, ref uint pSurfaceFormatCount, VkSurfaceFormatKHR* pSurfaceFormats);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkGetPhysicalDeviceSurfacePresentModesKHRDelegate(IntPtr physicalDevice, ulong surface, ref uint pPresentModeCount, uint* pPresentModes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkGetPhysicalDeviceMemoryPropertiesDelegate(IntPtr physicalDevice, out VkPhysicalDeviceMemoryProperties pMemoryProperties);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateDeviceDelegate(IntPtr physicalDevice, VkDeviceCreateInfo* pCreateInfo, IntPtr pAllocator, out IntPtr pDevice);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateXlibSurfaceKHRDelegate(IntPtr instance, VkXlibSurfaceCreateInfoKHR* pCreateInfo, IntPtr pAllocator, out ulong pSurface);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateWin32SurfaceKHRDelegate(IntPtr instance, VkWin32SurfaceCreateInfoKHR* pCreateInfo, IntPtr pAllocator, out ulong pSurface);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroyDeviceDelegate(IntPtr device, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkGetDeviceQueueDelegate(IntPtr device, uint queueFamilyIndex, uint queueIndex, out IntPtr pQueue);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateCommandPoolDelegate(IntPtr device, VkCommandPoolCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pCommandPool);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroyCommandPoolDelegate(IntPtr device, ulong commandPool, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkAllocateCommandBuffersDelegate(IntPtr device, VkCommandBufferAllocateInfo* pAllocateInfo, out IntPtr pCommandBuffers);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkResetCommandBufferDelegate(IntPtr commandBuffer, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkBeginCommandBufferDelegate(IntPtr commandBuffer, VkCommandBufferBeginInfo* pBeginInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkEndCommandBufferDelegate(IntPtr commandBuffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateSemaphoreDelegate(IntPtr device, VkSemaphoreCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pSemaphore);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroySemaphoreDelegate(IntPtr device, ulong semaphore, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateSwapchainKHRDelegate(IntPtr device, VkSwapchainCreateInfoKHR* pCreateInfo, IntPtr pAllocator, out ulong pSwapchain);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroySwapchainKHRDelegate(IntPtr device, ulong swapchain, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkGetSwapchainImagesKHRDelegate(IntPtr device, ulong swapchain, ref uint pSwapchainImageCount, ulong* pSwapchainImages);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkAcquireNextImageKHRDelegate(IntPtr device, ulong swapchain, ulong timeout, ulong semaphore, ulong fence, out uint pImageIndex);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkQueuePresentKHRDelegate(IntPtr queue, VkPresentInfoKHR* pPresentInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateBufferDelegate(IntPtr device, VkBufferCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pBuffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroyBufferDelegate(IntPtr device, ulong buffer, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkGetBufferMemoryRequirementsDelegate(IntPtr device, ulong buffer, out VkMemoryRequirements pMemoryRequirements);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkAllocateMemoryDelegate(IntPtr device, VkMemoryAllocateInfo* pAllocateInfo, IntPtr pAllocator, out ulong pMemory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkFreeMemoryDelegate(IntPtr device, ulong memory, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkBindBufferMemoryDelegate(IntPtr device, ulong buffer, ulong memory, ulong memoryOffset);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkMapMemoryDelegate(IntPtr device, ulong memory, ulong offset, ulong size, uint flags, out IntPtr ppData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkUnmapMemoryDelegate(IntPtr device, ulong memory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkFlushMappedMemoryRangesDelegate(IntPtr device, uint memoryRangeCount, VkMappedMemoryRange* pMemoryRanges);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkCreateImageDelegate(IntPtr device, VkImageCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pImage);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroyImageDelegate(IntPtr device, ulong image, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkGetImageMemoryRequirementsDelegate(IntPtr device, ulong image, out VkMemoryRequirements pMemoryRequirements);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkBindImageMemoryDelegate(IntPtr device, ulong image, ulong memory, ulong memoryOffset);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void vkCmdPipelineBarrierDelegate(IntPtr commandBuffer, uint srcStageMask, uint dstStageMask, uint dependencyFlags, uint memoryBarrierCount, void* pMemoryBarriers, uint bufferMemoryBarrierCount, void* pBufferMemoryBarriers, uint imageMemoryBarrierCount, VkImageMemoryBarrier* pImageMemoryBarriers);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void vkCmdCopyBufferToImageDelegate(IntPtr commandBuffer, ulong srcBuffer, ulong dstImage, uint dstImageLayout, uint regionCount, VkBufferImageCopy* pRegions);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void vkCmdClearColorImageDelegate(IntPtr commandBuffer, ulong image, uint imageLayout, VkClearColorValue* pColor, uint rangeCount, VkImageSubresourceRange* pRanges);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void vkCmdBlitImageDelegate(IntPtr commandBuffer, ulong srcImage, uint srcImageLayout, ulong dstImage, uint dstImageLayout, uint regionCount, VkImageBlit* pRegions, uint filter);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int vkQueueSubmitDelegate(IntPtr queue, uint submitCount, VkSubmitInfo* pSubmits, ulong fence);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkQueueWaitIdleDelegate(IntPtr queue);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int vkDeviceWaitIdleDelegate(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void vkDestroySurfaceKHRDelegate(IntPtr instance, ulong surface, IntPtr pAllocator);
    }

    private static class X11Api
    {
        [DllImport("libX11.so.6")]
        public static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport("libX11.so.6")]
        public static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        public static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes windowAttributes);
    }

    private static class Win32Api
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int BorderWidth;
        public int Depth;
        public IntPtr Visual;
        public IntPtr Root;
        public int Class;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public ulong BackingPlanes;
        public ulong BackingPixel;
        public int SaveUnder;
        public IntPtr Colormap;
        public int MapInstalled;
        public int MapState;
        public long AllEventMasks;
        public long YourEventMask;
        public long DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkApplicationInfo
    {
        public uint SType;
        public void* PNext;
        public byte* PApplicationName;
        public uint ApplicationVersion;
        public byte* PEngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkInstanceCreateInfo
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
        public VkApplicationInfo* PApplicationInfo;
        public uint EnabledLayerCount;
        public byte** PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public byte** PpEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkDeviceQueueCreateInfo
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
        public uint QueueFamilyIndex;
        public uint QueueCount;
        public float* PQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkDeviceCreateInfo
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
        public uint QueueCreateInfoCount;
        public VkDeviceQueueCreateInfo* PQueueCreateInfos;
        public uint EnabledLayerCount;
        public byte** PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public byte** PpEnabledExtensionNames;
        public void* PEnabledFeatures;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkExtent2D
    {
        public uint X;
        public uint Y;

        public VkExtent2D(uint x, uint y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkQueueFamilyProperties
    {
        public uint QueueFlags;
        public uint QueueCount;
        public uint TimestampValidBits;
        public VkExtent3D MinImageTransferGranularity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkSurfaceCapabilitiesKHR
    {
        public uint MinImageCount;
        public uint MaxImageCount;
        public VkExtent2D CurrentExtent;
        public VkExtent2D MinImageExtent;
        public VkExtent2D MaxImageExtent;
        public uint MaxImageArrayLayers;
        public uint SupportedTransforms;
        public uint CurrentTransform;
        public uint SupportedCompositeAlpha;
        public uint SupportedUsageFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkSurfaceFormatKHR
    {
        public uint Format;
        public uint ColorSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkCommandPoolCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public uint QueueFamilyIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkCommandBufferAllocateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public ulong CommandPool;
        public uint Level;
        public uint CommandBufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkCommandBufferBeginInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PInheritanceInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkSemaphoreCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkSwapchainCreateInfoKHR
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
        public ulong Surface;
        public uint MinImageCount;
        public uint ImageFormat;
        public uint ImageColorSpace;
        public VkExtent2D ImageExtent;
        public uint ImageArrayLayers;
        public uint ImageUsage;
        public uint ImageSharingMode;
        public uint QueueFamilyIndexCount;
        public uint* PQueueFamilyIndices;
        public uint PreTransform;
        public uint CompositeAlpha;
        public uint PresentMode;
        public uint Clipped;
        public ulong OldSwapchain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkXlibSurfaceCreateInfoKHR
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
        public IntPtr Dpy;
        public IntPtr Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkWin32SurfaceCreateInfoKHR
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
        public IntPtr Hinstance;
        public IntPtr Hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkBufferCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public ulong Size;
        public uint Usage;
        public uint ImageSharingMode;
        public uint QueueFamilyIndexCount;
        public IntPtr PQueueFamilyIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkMemoryRequirements
    {
        public ulong Size;
        public ulong Alignment;
        public uint MemoryTypeBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkMemoryAllocateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public ulong AllocationSize;
        public uint MemoryTypeIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkMappedMemoryRange
    {
        public uint SType;
        public IntPtr PNext;
        public ulong Memory;
        public ulong Offset;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkImageCreateInfo
    {
        public uint SType;
        public void* PNext;
        public uint Flags;
        public uint ImageType;
        public uint Format;
        public VkExtent3D Extent;
        public uint MipLevels;
        public uint ArrayLayers;
        public uint Samples;
        public uint Tiling;
        public uint Usage;
        public uint SharingMode;
        public uint QueueFamilyIndexCount;
        public uint* PQueueFamilyIndices;
        public uint InitialLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkExtent3D
    {
        public uint Width;
        public uint Height;
        public uint Depth;

        public VkExtent3D(uint width, uint height, uint depth)
        {
            Width = width;
            Height = height;
            Depth = depth;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkImageSubresourceLayers
    {
        public uint AspectMask;
        public uint MipLevel;
        public uint BaseArrayLayer;
        public uint LayerCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkOffset3D
    {
        public int X;
        public int Y;
        public int Z;

        public VkOffset3D(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkBufferImageCopy
    {
        public ulong BufferOffset;
        public uint BufferRowLength;
        public uint BufferImageHeight;
        public VkImageSubresourceLayers ImageSubresource;
        public VkOffset3D ImageOffset;
        public VkExtent3D ImageExtent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkImageSubresourceRange
    {
        public uint AspectMask;
        public uint BaseMipLevel;
        public uint LevelCount;
        public uint BaseArrayLayer;
        public uint LayerCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkImageMemoryBarrier
    {
        public uint SType;
        public IntPtr PNext;
        public uint SrcAccessMask;
        public uint DstAccessMask;
        public uint OldLayout;
        public uint NewLayout;
        public uint SrcQueueFamilyIndex;
        public uint DstQueueFamilyIndex;
        public ulong Image;
        public VkImageSubresourceRange SubresourceRange;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkSubmitInfo
    {
        public uint SType;
        public void* PNext;
        public uint WaitSemaphoreCount;
        public ulong* PWaitSemaphores;
        public uint* PWaitDstStageMask;
        public uint CommandBufferCount;
        public IntPtr* PCommandBuffers;
        public uint SignalSemaphoreCount;
        public ulong* PSignalSemaphores;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkPresentInfoKHR
    {
        public uint SType;
        public void* PNext;
        public uint WaitSemaphoreCount;
        public ulong* PWaitSemaphores;
        public uint SwapchainCount;
        public ulong* PSwapchains;
        public uint* PImageIndices;
        public IntPtr PResults;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkClearColorValue
    {
        public fixed float Float32[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkImageBlit
    {
        public VkImageSubresourceLayers SrcSubresource;
        public VkOffset3D SrcOffsets0;
        public VkOffset3D SrcOffsets1;
        public VkImageSubresourceLayers DstSubresource;
        public VkOffset3D DstOffsets0;
        public VkOffset3D DstOffsets1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkMemoryType
    {
        public uint PropertyFlags;
        public uint HeapIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkMemoryHeap
    {
        public ulong Size;
        public uint Flags;
        public uint Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkPhysicalDeviceMemoryProperties
    {
        public uint MemoryTypeCount;
        public VkMemoryType MemoryType0;
        public VkMemoryType MemoryType1;
        public VkMemoryType MemoryType2;
        public VkMemoryType MemoryType3;
        public VkMemoryType MemoryType4;
        public VkMemoryType MemoryType5;
        public VkMemoryType MemoryType6;
        public VkMemoryType MemoryType7;
        public VkMemoryType MemoryType8;
        public VkMemoryType MemoryType9;
        public VkMemoryType MemoryType10;
        public VkMemoryType MemoryType11;
        public VkMemoryType MemoryType12;
        public VkMemoryType MemoryType13;
        public VkMemoryType MemoryType14;
        public VkMemoryType MemoryType15;
        public VkMemoryType MemoryType16;
        public VkMemoryType MemoryType17;
        public VkMemoryType MemoryType18;
        public VkMemoryType MemoryType19;
        public VkMemoryType MemoryType20;
        public VkMemoryType MemoryType21;
        public VkMemoryType MemoryType22;
        public VkMemoryType MemoryType23;
        public VkMemoryType MemoryType24;
        public VkMemoryType MemoryType25;
        public VkMemoryType MemoryType26;
        public VkMemoryType MemoryType27;
        public VkMemoryType MemoryType28;
        public VkMemoryType MemoryType29;
        public VkMemoryType MemoryType30;
        public VkMemoryType MemoryType31;
        public uint MemoryHeapCount;
        public VkMemoryHeap MemoryHeap0;
        public VkMemoryHeap MemoryHeap1;
        public VkMemoryHeap MemoryHeap2;
        public VkMemoryHeap MemoryHeap3;
        public VkMemoryHeap MemoryHeap4;
        public VkMemoryHeap MemoryHeap5;
        public VkMemoryHeap MemoryHeap6;
        public VkMemoryHeap MemoryHeap7;
        public VkMemoryHeap MemoryHeap8;
        public VkMemoryHeap MemoryHeap9;
        public VkMemoryHeap MemoryHeap10;
        public VkMemoryHeap MemoryHeap11;
        public VkMemoryHeap MemoryHeap12;
        public VkMemoryHeap MemoryHeap13;
        public VkMemoryHeap MemoryHeap14;
        public VkMemoryHeap MemoryHeap15;

        public VkMemoryType GetMemoryType(int index)
        {
            fixed (VkMemoryType* pTypes = &MemoryType0)
                return pTypes[index];
        }
    }
}
