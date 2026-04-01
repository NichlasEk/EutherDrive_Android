using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Opengl;
using Android.Util;
using Android.Views;
using Android.Widget;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Java.Nio;

namespace EutherDrive.Rendering;

public sealed class AndroidNativeGlRenderSurface : IGameRenderSurface, IDisposable
{
    private readonly AndroidNativeGlHost _host = new();

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

    public FrameBlitMetrics PresentOwnedBuffer(byte[] source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf)
    {
        if (source.Length == 0 || width <= 0 || height <= 0 || srcStride <= 0)
            return FrameBlitMetrics.None;

        long start = measurePerf ? Stopwatch.GetTimestamp() : 0;
        _host.UpdateOwnedFrame(source, width, height, srcStride, options);
        long blitTicks = measurePerf ? Stopwatch.GetTimestamp() - start : 0;
        return new FrameBlitMetrics(0, blitTicks);
    }

    public bool ShouldFallbackToBitmap(out string reason) => _host.ShouldFallbackToBitmap(out reason);

    public bool TryGetDebugSummary(out string summary) => _host.TryGetDebugSummary(out summary);

    public void SetOverlayInputCallbacks(Action<string, bool> actionStateChanged, Action<IReadOnlyCollection<string>> directionsChanged)
        => _host.SetOverlayInputCallbacks(actionStateChanged, directionsChanged);

    public void SetScreenTapCallback(Action screenTapped)
        => _host.SetScreenTapCallback(screenTapped);

    public void SetLandscapeOverlayEnabled(bool enabled)
        => _host.SetLandscapeOverlayEnabled(enabled);

    public void SetPresentationSize(double width, double height)
        => _host.SetPresentationSize(width, height);

    public void SetInterlaceBlend(bool enabled, int fieldParity)
        => _host.SetInterlaceBlend(enabled, fieldParity);

    public void Reset() => _host.ResetFrame();

    public void Dispose() => _host.DisposeSurface();

    private sealed class AndroidNativeGlHost : NativeControlHost
    {
        private readonly object _frameSync = new();
        private byte[] _frameBytes = Array.Empty<byte>();
        private byte[] _stagingBytes = Array.Empty<byte>();
        private int _frameWidth;
        private int _frameHeight;
        private int _frameStride;
        private bool _frameDirty;
        private bool _renderRequested;
        private bool _sharpPixelsEnabled = true;
        private bool _forceOpaque;
        private bool _applyScanlines;
        private float _scanlineDarken = 1.0f;
        private bool _interlaceBlendEnabled;
        private int _interlaceBlendFieldParity = -1;
        private int _presentCount;
        private int _renderCount;
        private int _uploadCount;
        private long _renderTicksTotal;
        private long _uploadTicksTotal;
        private long _lastRenderTicks;
        private long _lastUploadTicks;
        private int _surfaceAvailableCount;
        private int _surfaceDestroyedCount;
        private int _surfaceSizeChangedCount;
        private int _surfaceZeroSizeCount;
        private int _layoutRequestCount;
        private int _rootLayoutCount;
        private int _gameViewLayoutCount;
        private int _glInitCount;
        private int _blackClearCount;
        private int _reuseTextureCount;
        private bool _initAttempted;
        private bool _initSucceeded;
        private string _fallbackReason = string.Empty;
        private string _glVendor = string.Empty;
        private string _glRenderer = string.Empty;
        private string _glVersion = string.Empty;
        private string _glShadingLanguageVersion = string.Empty;
        private string _glInitDetails = string.Empty;
        private Action<string, bool>? _actionStateChanged;
        private Action<IReadOnlyCollection<string>>? _directionsChanged;
        private Action? _screenTapped;
        private bool _landscapeOverlayEnabled;
        private AndroidGlRootView? _nativeView;
        private double _presentationWidth;
        private double _presentationHeight;

        public AndroidNativeGlHost()
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

        public void SetOverlayInputCallbacks(Action<string, bool> actionStateChanged, Action<IReadOnlyCollection<string>> directionsChanged)
        {
            _actionStateChanged = actionStateChanged;
            _directionsChanged = directionsChanged;
        }

        public void SetScreenTapCallback(Action screenTapped)
        {
            _screenTapped = screenTapped;
        }

        public void SetLandscapeOverlayEnabled(bool enabled)
        {
            _landscapeOverlayEnabled = enabled;
            NoteLayoutRequest();
            _nativeView?.RequestLayout();
        }

        public void SetPresentationSize(double width, double height)
        {
            bool changed;
            double previousWidth;
            double previousHeight;
            lock (_frameSync)
            {
                previousWidth = _presentationWidth;
                previousHeight = _presentationHeight;
                changed = Math.Abs(_presentationWidth - width) > 0.5 || Math.Abs(_presentationHeight - height) > 0.5;
                _presentationWidth = width;
                _presentationHeight = height;
            }

            bool requiresLayout = changed;
            if (changed && _nativeView != null)
                requiresLayout = _nativeView.ShouldRelayoutForPresentationSize(previousWidth, previousHeight, width, height);

            if (requiresLayout)
            {
                NoteLayoutRequest();
                _nativeView?.RequestLayout();
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
            bool resized = EnsureFrameSize(width, height);

            int dstStride = width * 4;
            int requiredBytes = checked(dstStride * height);
            long copyStart = Stopwatch.GetTimestamp();
            byte[] stagingBytes;

            lock (_frameSync)
            {
                if (_stagingBytes.Length != requiredBytes)
                    _stagingBytes = new byte[requiredBytes];
                stagingBytes = _stagingBytes;
            }

            if (srcStride == dstStride)
            {
                source[..Math.Min(source.Length, requiredBytes)].CopyTo(stagingBytes);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> srcRow = source.Slice(y * srcStride, dstStride);
                    srcRow.CopyTo(stagingBytes.AsSpan(y * dstStride, dstStride));
                }
            }

            lock (_frameSync)
            {
                (_frameBytes, _stagingBytes) = (_stagingBytes, _frameBytes);
                _frameWidth = width;
                _frameHeight = height;
                _frameStride = dstStride;
                _frameDirty = true;
                _renderRequested = true;
                _sharpPixelsEnabled = options.SharpPixels;
                _forceOpaque = options.ForceOpaque;
                _applyScanlines = options.ApplyScanlines;
                _scanlineDarken = Math.Clamp(options.ScanlineDarkenFactor / 256f, 0f, 1f);
                _presentCount++;
                _lastUploadTicks = Stopwatch.GetTimestamp() - copyStart;
            }

            _nativeView?.NotifyFrameUpdated(resized);
        }

        public void UpdateOwnedFrame(byte[] source, int width, int height, int srcStride, in FrameBlitOptions options)
        {
            bool resized = EnsureFrameSize(width, height);
            int requiredBytes = checked(width * height * 4);
            if (srcStride != width * 4 || source.Length < requiredBytes)
            {
                UpdateFrame(source.AsSpan(0, Math.Min(source.Length, requiredBytes)), width, height, srcStride, options);
                return;
            }

            lock (_frameSync)
            {
                _frameBytes = source;
                _frameWidth = width;
                _frameHeight = height;
                _frameStride = srcStride;
                _frameDirty = true;
                _renderRequested = true;
                _sharpPixelsEnabled = options.SharpPixels;
                _forceOpaque = options.ForceOpaque;
                _applyScanlines = options.ApplyScanlines;
                _scanlineDarken = Math.Clamp(options.ScanlineDarkenFactor / 256f, 0f, 1f);
                _presentCount++;
            }

            _nativeView?.NotifyFrameUpdated(resized);
        }

        public void ResetFrame()
        {
            lock (_frameSync)
            {
                _frameBytes = Array.Empty<byte>();
                _stagingBytes = Array.Empty<byte>();
                _frameWidth = 0;
                _frameHeight = 0;
                _frameStride = 0;
                _frameDirty = false;
                _renderRequested = true;
                _applyScanlines = false;
                _scanlineDarken = 1.0f;
                _interlaceBlendEnabled = false;
                _interlaceBlendFieldParity = -1;
            }

            _nativeView?.NotifyFrameUpdated(resized: true);
        }

        public void DisposeSurface()
        {
            ResetFrame();
        }

        public bool ShouldFallbackToBitmap(out string reason)
        {
            lock (_frameSync)
            {
                if (!string.IsNullOrEmpty(_fallbackReason))
                {
                    reason = _fallbackReason;
                    return true;
                }

                if (_initAttempted && !_initSucceeded && _presentCount > 3)
                {
                    reason = "Native Android GL init failed before first frame.";
                    return true;
                }

                if (_presentCount > 20 && _renderCount == 0)
                {
                    reason = "Native Android GL accepted frames but never rendered.";
                    return true;
                }
            }

            reason = string.Empty;
            return false;
        }

        public bool HasRenderableFrame()
        {
            lock (_frameSync)
            {
                return _frameWidth > 0
                    && _frameHeight > 0
                    && _frameBytes.Length >= _frameWidth * _frameHeight * 4;
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
                summary = $"GL Present:{_presentCount} Render:{_renderCount} Upload:{_uploadCount} Pending:{(_renderRequested ? 1 : 0)} IL:{(_interlaceBlendEnabled ? 1 : 0)}/{_interlaceBlendFieldParity} R:{avgRenderMs:0.0}/{lastRenderMs:0.0}ms U:{avgUploadMs:0.0}/{lastUploadMs:0.0}ms";
                summary = $"{summary}\nSurf a/d/s/z0/l:{_surfaceAvailableCount}/{_surfaceDestroyedCount}/{_surfaceSizeChangedCount}/{_surfaceZeroSizeCount}/{_layoutRequestCount} root:{_rootLayoutCount} game:{_gameViewLayoutCount} init:{_glInitCount} blk:{_blackClearCount} hold:{_reuseTextureCount}";
                if (!string.IsNullOrEmpty(_glVersion) || !string.IsNullOrEmpty(_glShadingLanguageVersion))
                    summary = $"{summary}\nGLES:{_glVersion} GLSL:{_glShadingLanguageVersion}";
                if (!string.IsNullOrEmpty(_glVendor) || !string.IsNullOrEmpty(_glRenderer))
                    summary = $"{summary}\nGPU:{_glVendor} / {_glRenderer}";
                if (!string.IsNullOrEmpty(_fallbackReason))
                    summary = $"{summary}\nGL Fail:{_fallbackReason}";
                if (!string.IsNullOrEmpty(_glInitDetails))
                    summary = $"{summary}\n{_glInitDetails}";
                return true;
            }
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            try
            {
                Context context = EutherDrive.Android.MainActivity.Current ?? global::Android.App.Application.Context
                    ?? throw new InvalidOperationException("Android context is unavailable.");
                var view = new AndroidGlRootView(context, this);
                _nativeView = view;
                return new Avalonia.Android.AndroidViewControlHandle(view);
            }
            catch (Exception ex)
            {
                lock (_frameSync)
                {
                    _initAttempted = true;
                    _initSucceeded = false;
                    _fallbackReason = ex.Message;
                }

                Context context = EutherDrive.Android.MainActivity.Current ?? global::Android.App.Application.Context
                    ?? throw new InvalidOperationException("Android context is unavailable.");
                return new Avalonia.Android.AndroidViewControlHandle(new View(context));
            }
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (control is Avalonia.Android.AndroidViewControlHandle androidHandle)
            {
                if (androidHandle.View is AndroidGlRootView view)
                {
                    if (ReferenceEquals(_nativeView, view))
                        _nativeView = null;
                    view.ReleaseSurface();
                }

                androidHandle.Destroy();
            }
        }

        private void BeginRender(out byte[] frameBytes, out int frameWidth, out int frameHeight, out bool frameDirty, out bool sharpPixelsEnabled, out bool forceOpaque, out bool applyScanlines, out float scanlineDarken, out bool interlaceBlendEnabled, out int interlaceBlendFieldParity)
        {
            lock (_frameSync)
            {
                _renderRequested = false;
                frameBytes = _frameBytes;
                frameWidth = _frameWidth;
                frameHeight = _frameHeight;
                frameDirty = _frameDirty;
                sharpPixelsEnabled = _sharpPixelsEnabled;
                forceOpaque = _forceOpaque;
                applyScanlines = _applyScanlines;
                scanlineDarken = _scanlineDarken;
                interlaceBlendEnabled = _interlaceBlendEnabled;
                interlaceBlendFieldParity = _interlaceBlendFieldParity;
                _frameDirty = false;
            }
        }

        private void NoteInitSuccess()
        {
            lock (_frameSync)
            {
                _initAttempted = true;
                _initSucceeded = true;
                _fallbackReason = string.Empty;
                _glInitDetails = string.Empty;
            }
        }

        private void NoteInitFailure(string reason, string? details = null)
        {
            lock (_frameSync)
            {
                _initAttempted = true;
                _initSucceeded = false;
                if (string.IsNullOrEmpty(_fallbackReason))
                    _fallbackReason = reason;
                if (!string.IsNullOrEmpty(details))
                    _glInitDetails = details;
            }
        }

        private void NoteGlInfo(string vendor, string renderer, string version, string shadingLanguageVersion)
        {
            lock (_frameSync)
            {
                _glVendor = vendor;
                _glRenderer = renderer;
                _glVersion = version;
                _glShadingLanguageVersion = shadingLanguageVersion;
            }
        }

        private void NoteRender(long renderTicks, long uploadTicks, bool uploaded)
        {
            lock (_frameSync)
            {
                _renderCount++;
                _renderTicksTotal += renderTicks;
                _lastRenderTicks = renderTicks;
                if (uploaded)
                {
                    _uploadCount++;
                    _uploadTicksTotal += uploadTicks;
                    _lastUploadTicks = uploadTicks;
                }
                else
                {
                    _lastUploadTicks = 0;
                }
            }
        }

        private void NoteLayoutRequest()
        {
            lock (_frameSync)
                _layoutRequestCount++;
        }

        private void NoteRootLayout()
        {
            lock (_frameSync)
                _rootLayoutCount++;
        }

        private void NoteGameViewLayout()
        {
            lock (_frameSync)
                _gameViewLayoutCount++;
        }

        private void NoteSurfaceAvailable()
        {
            lock (_frameSync)
                _surfaceAvailableCount++;
        }

        private void NoteSurfaceDestroyed()
        {
            lock (_frameSync)
                _surfaceDestroyedCount++;
        }

        private void NoteSurfaceSizeChanged(int width, int height)
        {
            lock (_frameSync)
            {
                _surfaceSizeChangedCount++;
                if (width <= 0 || height <= 0)
                    _surfaceZeroSizeCount++;
            }
        }

        private void NoteGlInit()
        {
            lock (_frameSync)
                _glInitCount++;
        }

        private void NoteBlackClear()
        {
            lock (_frameSync)
                _blackClearCount++;
        }

        private void NoteTextureReuse()
        {
            lock (_frameSync)
                _reuseTextureCount++;
        }

        private void GetLatestFrameSize(out int frameWidth, out int frameHeight)
        {
            lock (_frameSync)
            {
                frameWidth = _frameWidth;
                frameHeight = _frameHeight;
            }
        }

        private void GetLatestPresentationSize(out double presentationWidth, out double presentationHeight)
        {
            lock (_frameSync)
            {
                presentationWidth = _presentationWidth;
                presentationHeight = _presentationHeight;
            }
        }

        private void NotifyActionStateChanged(string tag, bool pressed)
            => _actionStateChanged?.Invoke(tag, pressed);

        private void NotifyDirectionsChanged(IReadOnlyCollection<string> directions)
            => _directionsChanged?.Invoke(directions);

        private void NotifyScreenTapped()
            => _screenTapped?.Invoke();

        private bool IsLandscapeOverlayEnabled() => _landscapeOverlayEnabled;

        private sealed class AndroidGlRootView : FrameLayout
        {
            private const float LandscapeIntegerSnapThreshold = 0.08f;
            private const int PresentationLayoutJitterThresholdPx = 3;
            private const int OverlayDpadSizeDp = 192;
            private const int OverlayFaceButtonSizeDp = 74;
            private const int OverlayFaceAreaSizeDp = 212;
            private readonly AndroidNativeGlHost _host;
            private readonly AndroidGlSurfaceView _gameView;
            private readonly NativeDPadView _dPadView;
            private readonly NativeActionButtonView _buttonL2;
            private readonly NativeActionButtonView _buttonSelect;
            private readonly NativeActionButtonView _buttonL1;
            private readonly NativeActionButtonView _buttonStart;
            private readonly NativeActionButtonView _buttonMenu;
            private readonly NativeActionButtonView _buttonR2;
            private readonly NativeActionButtonView _buttonR1;
            private readonly NativeActionButtonView _buttonX;
            private readonly NativeActionButtonView _buttonY;
            private readonly NativeActionButtonView _buttonB;
            private readonly NativeActionButtonView _buttonA;
            private Rect _lastGameRect;
            private bool _hasLastGameRect;

            public AndroidGlRootView(Context context, AndroidNativeGlHost host) : base(context)
            {
                _host = host;
                SetClipChildren(false);
                SetClipToPadding(false);

                _gameView = new AndroidGlSurfaceView(context, host);
                AddView(_gameView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

                _buttonL2 = CreateActionButton(context, "L2", "L2", 12f);
                _buttonSelect = CreateActionButton(context, "Select", "Select", 11f);
                _buttonL1 = CreateActionButton(context, "L1", "L1", 12f);
                _buttonStart = CreateActionButton(context, "Start", "Start", 11f);
                _buttonMenu = CreateActionButton(context, "Menu", "Menu", 11f);
                _buttonR2 = CreateActionButton(context, "R2", "R2", 12f);
                _buttonR1 = CreateActionButton(context, "R1", "R1", 12f);
                _buttonY = CreateActionButton(context, "Y", "Y", 20f);
                _buttonB = CreateActionButton(context, "B", "B", 20f);
                _buttonX = CreateActionButton(context, "X", "X", 20f);
                _buttonA = CreateActionButton(context, "A", "A", 20f);

                _dPadView = new NativeDPadView(context, directions => _host.NotifyDirectionsChanged(directions));

                AddView(_dPadView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                AddView(_buttonL2);
                AddView(_buttonSelect);
                AddView(_buttonL1);
                AddView(_buttonStart);
                AddView(_buttonMenu);
                AddView(_buttonR2);
                AddView(_buttonR1);
                AddView(_buttonY);
                AddView(_buttonB);
                AddView(_buttonX);
                AddView(_buttonA);
            }

            public void NotifyFrameUpdated(bool resized)
            {
                _gameView.RequestRenderSafe();
            }

            public void ReleaseSurface()
            {
                ReleaseOverlayInputs();
                _gameView.ReleaseSurface();
            }

            public bool ShouldRelayoutForPresentationSize(double previousWidth, double previousHeight, double nextWidth, double nextHeight)
            {
                int width = Width;
                int height = Height;
                if (width <= 0 || height <= 0)
                    return true;

                _host.GetLatestFrameSize(out int frameWidth, out int frameHeight);
                Rect previousRect = ComputeGameRect(width, height, frameWidth, frameHeight, previousWidth, previousHeight);
                Rect nextRect = ComputeGameRect(width, height, frameWidth, frameHeight, nextWidth, nextHeight);
                return Math.Abs(previousRect.Left - nextRect.Left) > PresentationLayoutJitterThresholdPx
                    || Math.Abs(previousRect.Top - nextRect.Top) > PresentationLayoutJitterThresholdPx
                    || Math.Abs(previousRect.Right - nextRect.Right) > PresentationLayoutJitterThresholdPx
                    || Math.Abs(previousRect.Bottom - nextRect.Bottom) > PresentationLayoutJitterThresholdPx;
            }

            protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
            {
                _host.NoteRootLayout();
                int width = right - left;
                int height = bottom - top;
                if (width <= 0 || height <= 0)
                {
                    base.OnLayout(changed, left, top, right, bottom);
                    return;
                }

                _host.GetLatestFrameSize(out int frameWidth, out int frameHeight);
                _host.GetLatestPresentationSize(out double presentationWidth, out double presentationHeight);
                Rect gameRect = ComputeGameRect(width, height, frameWidth, frameHeight, presentationWidth, presentationHeight);
                if (!_hasLastGameRect || !RectsEqual(_lastGameRect, gameRect))
                {
                    _gameView.Layout(gameRect.Left, gameRect.Top, gameRect.Right, gameRect.Bottom);
                    _lastGameRect = gameRect;
                    _hasLastGameRect = true;
                    _host.NoteGameViewLayout();
                }

                bool showLandscapeOverlay = _host.IsLandscapeOverlayEnabled();
                SetLandscapeOverlayVisibility(showLandscapeOverlay);
                if (!showLandscapeOverlay)
                {
                    ReleaseOverlayInputs();
                    return;
                }

                int marginTop = Dp(14);
                int marginSideLeft = Dp(22);
                int marginSideRight = Dp(22);
                int bottomMargin = Dp(18);
                int buttonGap = Dp(8);

                int topSmallWidth = Dp(46);
                int topWideWidth = Dp(62);
                int topButtonHeight = Dp(40);
                int topRowOffset = Dp(16);

                LayoutView(_buttonL2, marginSideLeft, marginTop + topRowOffset, topSmallWidth, topButtonHeight);
                LayoutView(_buttonSelect, marginSideLeft + topSmallWidth + buttonGap, marginTop + topRowOffset, topWideWidth, topButtonHeight);
                LayoutView(_buttonL1, marginSideLeft, marginTop + topRowOffset + topButtonHeight + buttonGap, topSmallWidth, topButtonHeight);
                LayoutView(_buttonStart, marginSideLeft + topSmallWidth + buttonGap, marginTop + topRowOffset + topButtonHeight + buttonGap, topWideWidth, topButtonHeight);

                LayoutView(_buttonMenu, width - marginSideRight - topWideWidth - topSmallWidth - buttonGap, marginTop + topRowOffset, topWideWidth, topButtonHeight);
                LayoutView(_buttonR2, width - marginSideRight - topSmallWidth, marginTop + topRowOffset, topSmallWidth, topButtonHeight);
                LayoutView(_buttonR1, width - marginSideRight - topSmallWidth, marginTop + topRowOffset + topButtonHeight + buttonGap, topSmallWidth, topButtonHeight);

                int dpadSize = Dp(OverlayDpadSizeDp);
                LayoutView(_dPadView, Dp(26), height - bottomMargin - dpadSize, dpadSize, dpadSize);

                int faceButtonSize = Dp(OverlayFaceButtonSizeDp);
                int faceAreaSize = Dp(OverlayFaceAreaSizeDp);
                int faceOriginX = width - Dp(22) - faceAreaSize;
                int faceOriginY = height - bottomMargin - faceAreaSize;
                LayoutView(_buttonX, faceOriginX + ((faceAreaSize - faceButtonSize) / 2), faceOriginY, faceButtonSize, faceButtonSize);
                LayoutView(_buttonY, faceOriginX, faceOriginY + ((faceAreaSize - faceButtonSize) / 2), faceButtonSize, faceButtonSize);
                LayoutView(_buttonA, faceOriginX + faceAreaSize - faceButtonSize, faceOriginY + ((faceAreaSize - faceButtonSize) / 2), faceButtonSize, faceButtonSize);
                LayoutView(_buttonB, faceOriginX + ((faceAreaSize - faceButtonSize) / 2), faceOriginY + faceAreaSize - faceButtonSize, faceButtonSize, faceButtonSize);
            }

            private void SetLandscapeOverlayVisibility(bool visible)
            {
                var state = visible ? ViewStates.Visible : ViewStates.Gone;
                _dPadView.Visibility = state;
                _buttonL2.Visibility = state;
                _buttonSelect.Visibility = state;
                _buttonL1.Visibility = state;
                _buttonStart.Visibility = state;
                _buttonMenu.Visibility = state;
                _buttonR2.Visibility = state;
                _buttonR1.Visibility = state;
                _buttonY.Visibility = state;
                _buttonB.Visibility = state;
                _buttonX.Visibility = state;
                _buttonA.Visibility = state;
            }

            private void ReleaseOverlayInputs()
            {
                _dPadView.ResetInput();
                _buttonL2.ResetInput();
                _buttonSelect.ResetInput();
                _buttonL1.ResetInput();
                _buttonStart.ResetInput();
                _buttonMenu.ResetInput();
                _buttonR2.ResetInput();
                _buttonR1.ResetInput();
                _buttonY.ResetInput();
                _buttonB.ResetInput();
                _buttonX.ResetInput();
                _buttonA.ResetInput();
            }

            private NativeActionButtonView CreateActionButton(Context context, string tag, string label, float textSizeSp)
                => new(context, tag, label, textSizeSp, _host.NotifyActionStateChanged);

            private static void LayoutView(View view, int x, int y, int width, int height)
            {
                int widthSpec = global::Android.Views.View.MeasureSpec.MakeMeasureSpec(width, MeasureSpecMode.Exactly);
                int heightSpec = global::Android.Views.View.MeasureSpec.MakeMeasureSpec(height, MeasureSpecMode.Exactly);
                view.Measure(widthSpec, heightSpec);
                view.Layout(x, y, x + width, y + height);
            }

            private Rect ComputeGameRect(int width, int height, int frameWidth, int frameHeight, double presentationWidth, double presentationHeight)
            {
                double sourceWidth = presentationWidth > 0 ? presentationWidth : frameWidth;
                double sourceHeight = presentationHeight > 0 ? presentationHeight : frameHeight;
                if (sourceWidth <= 0 || sourceHeight <= 0)
                    return new Rect(0, 0, width, height);

                float fitScale = Math.Min((float)width / (float)sourceWidth, (float)height / (float)sourceHeight);
                int integerScale = (int)MathF.Floor(fitScale);
                float scale = fitScale;
                if (width > height && integerScale >= 1 && fitScale - integerScale <= LandscapeIntegerSnapThreshold)
                    scale = integerScale;
                else if (integerScale >= 1 && width <= height)
                    scale = integerScale;

                int scaledWidth = Math.Max(1, (int)MathF.Round((float)sourceWidth * scale));
                int scaledHeight = Math.Max(1, (int)MathF.Round((float)sourceHeight * scale));
                int x = (width - scaledWidth) / 2;
                int y = (height - scaledHeight) / 2;
                return new Rect(x, y, x + scaledWidth, y + scaledHeight);
            }

            private static bool RectsEqual(Rect a, Rect b)
            {
                return a.Left == b.Left
                    && a.Top == b.Top
                    && a.Right == b.Right
                    && a.Bottom == b.Bottom;
            }

            private int Dp(int value)
            {
                float density = Resources?.DisplayMetrics?.Density ?? 1f;
                return (int)MathF.Round(value * density);
            }
        }

        private sealed class NativeActionButtonView : TextView
        {
            private readonly string _tag;
            private readonly Action<string, bool> _onPressedChanged;
            private bool _pressed;

            public NativeActionButtonView(Context context, string tag, string label, float textSizeSp, Action<string, bool> onPressedChanged) : base(context)
            {
                _tag = tag;
                _onPressedChanged = onPressedChanged;
                Text = label;
                Gravity = GravityFlags.Center;
                TextAlignment = global::Android.Views.TextAlignment.Center;
                SetTextColor(Color.Rgb(0xEE, 0xF6, 0xFF));
                SetTextSize(ComplexUnitType.Sp, textSizeSp);
                Typeface = Typeface.Create(Typeface.Default, TypefaceStyle.Bold);
                SetSingleLine();
                SetIncludeFontPadding(false);
                Clickable = true;
                Focusable = false;
                FocusableInTouchMode = false;
                SetPadding(0, 0, 0, 0);
                UpdateBackground();
            }

            public override bool OnTouchEvent(MotionEvent? e)
            {
                if (e == null)
                    return false;

                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                        SetPressedState(true);
                        return true;
                    case MotionEventActions.Move:
                        SetPressedState(IsPointInside(e.GetX(), e.GetY()));
                        return true;
                    case MotionEventActions.Up:
                        SetPressedState(false);
                        return true;
                    case MotionEventActions.Cancel:
                        SetPressedState(false);
                        return true;
                }

                return base.OnTouchEvent(e);
            }

            public void ResetInput() => SetPressedState(false);

            private bool IsPointInside(float x, float y)
                => x >= 0 && y >= 0 && x <= Width && y <= Height;

            private void SetPressedState(bool pressed)
            {
                if (_pressed == pressed)
                    return;

                _pressed = pressed;
                UpdateBackground();
                _onPressedChanged(_tag, pressed);
            }

            private void UpdateBackground()
            {
                var drawable = new GradientDrawable();
                drawable.SetShape(ShapeType.Rectangle);
                drawable.SetCornerRadius(MathF.Min(Width, Height) > 0 ? MathF.Min(Width, Height) / 4f : 22f);
                drawable.SetColor(_pressed ? Color.Argb(0xB0, 0x34, 0xCF, 0xC1) : Color.Argb(0x70, 0x17, 0x2A, 0x3B));
                drawable.SetStroke(_pressed ? 4 : 3, Color.Argb(0xCC, 0x7D, 0xD3, 0xFC));
                Background = drawable;
                Alpha = _pressed ? 1.0f : 0.92f;
            }
        }

        private sealed class NativeDPadView : View
        {
            private const float DeadZoneRatio = 0.20f;
            private const float AxisEngageRatio = 0.28f;
            private const float DiagonalRatio = 0.56f;
            private readonly Action<IReadOnlyCollection<string>> _onDirectionsChanged;
            private readonly Paint _basePaint = new(PaintFlags.AntiAlias);
            private readonly Paint _trackPaint = new(PaintFlags.AntiAlias);
            private readonly Paint _thumbPaint = new(PaintFlags.AntiAlias);
            private readonly Paint _hintPaint = new(PaintFlags.AntiAlias);
            private HashSet<string> _activeDirections = new(StringComparer.OrdinalIgnoreCase);
            private float _thumbOffsetX;
            private float _thumbOffsetY;

            public NativeDPadView(Context context, Action<IReadOnlyCollection<string>> onDirectionsChanged) : base(context)
            {
                _onDirectionsChanged = onDirectionsChanged;
                Clickable = true;
                Focusable = false;
                _basePaint.Color = Color.Argb(0x55, 0x41, 0x79, 0xB8);
                _basePaint.SetStyle(Paint.Style.Stroke);
                _basePaint.StrokeWidth = 4f;
                _trackPaint.Color = Color.Argb(0x33, 0x73, 0xC0, 0xFF);
                _trackPaint.SetStyle(Paint.Style.Stroke);
                _trackPaint.StrokeWidth = 3f;
                _thumbPaint.Color = Color.Argb(0x88, 0x73, 0xC0, 0xFF);
                _thumbPaint.SetStyle(Paint.Style.FillAndStroke);
                _hintPaint.Color = Color.Argb(0x88, 0xD9, 0xEC, 0xFF);
                _hintPaint.SetStyle(Paint.Style.FillAndStroke);
            }

            public override bool OnTouchEvent(MotionEvent? e)
            {
                if (e == null)
                    return false;

                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                    case MotionEventActions.Move:
                        UpdateFromPoint(e.GetX(), e.GetY());
                        return true;
                    case MotionEventActions.Up:
                    case MotionEventActions.Cancel:
                        ResetInput();
                        return true;
                }

                return base.OnTouchEvent(e);
            }

            protected override void OnDraw(global::Android.Graphics.Canvas canvas)
            {
                base.OnDraw(canvas);
                float width = Width;
                float height = Height;
                if (width <= 0 || height <= 0)
                    return;

                float centerX = width * 0.5f;
                float centerY = height * 0.5f;
                float baseRadius = Math.Min(width, height) * 0.46f;
                float trackRadius = Math.Min(width, height) * 0.34f;
                float thumbRadius = Math.Min(width, height) * 0.19f;
                float hintRadius = Math.Min(width, height) * 0.13f;

                canvas.DrawCircle(centerX, centerY, baseRadius, _basePaint);
                canvas.DrawCircle(centerX, centerY, trackRadius, _trackPaint);
                canvas.DrawCircle(centerX, centerY, hintRadius, _hintPaint);
                canvas.DrawCircle(centerX, centerY - trackRadius, hintRadius, _trackPaint);
                canvas.DrawCircle(centerX, centerY + trackRadius, hintRadius, _trackPaint);
                canvas.DrawCircle(centerX - trackRadius, centerY, hintRadius, _trackPaint);
                canvas.DrawCircle(centerX + trackRadius, centerY, hintRadius, _trackPaint);
                canvas.DrawCircle(centerX + _thumbOffsetX, centerY + _thumbOffsetY, thumbRadius, _thumbPaint);
            }

            public void ResetInput()
            {
                _thumbOffsetX = 0;
                _thumbOffsetY = 0;
                if (_activeDirections.Count != 0)
                {
                    _activeDirections.Clear();
                    _onDirectionsChanged(Array.Empty<string>());
                }

                Invalidate();
            }

            private void UpdateFromPoint(float x, float y)
            {
                float width = Width;
                float height = Height;
                if (width <= 0 || height <= 0)
                    return;

                float centerX = width * 0.5f;
                float centerY = height * 0.5f;
                float dx = x - centerX;
                float dy = y - centerY;
                float travelRadius = Math.Min(width, height) * 0.32f;
                float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                if (distance > travelRadius && distance > 0)
                {
                    float scale = travelRadius / distance;
                    dx *= scale;
                    dy *= scale;
                    distance = travelRadius;
                }

                _thumbOffsetX = dx;
                _thumbOffsetY = dy;
                float normalizedDistance = distance / travelRadius;
                HashSet<string> directions = normalizedDistance < DeadZoneRatio
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : DetermineDirections(dx / travelRadius, dy / travelRadius);

                if (!_activeDirections.SetEquals(directions))
                {
                    _activeDirections = directions;
                    _onDirectionsChanged(_activeDirections.ToArray());
                }

                Invalidate();
            }

            private static HashSet<string> DetermineDirections(float normalizedX, float normalizedY)
            {
                float absX = MathF.Abs(normalizedX);
                float absY = MathF.Abs(normalizedY);
                float major = MathF.Max(absX, absY);
                float minor = MathF.Min(absX, absY);

                var directions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (major < AxisEngageRatio)
                    return directions;

                bool horizontalDominant = absX >= absY;
                directions.Add(horizontalDominant
                    ? (normalizedX >= 0 ? "Right" : "Left")
                    : (normalizedY >= 0 ? "Down" : "Up"));

                bool diagonalIntent = minor >= AxisEngageRatio && (minor / major) >= DiagonalRatio;
                if (!diagonalIntent)
                    return directions;

                directions.Add(horizontalDominant
                    ? (normalizedY >= 0 ? "Down" : "Up")
                    : (normalizedX >= 0 ? "Right" : "Left"));
                return directions;
            }
        }

        private sealed class AndroidGlSurfaceView : GLSurfaceView, GLSurfaceView.IRenderer, ISurfaceHolderCallback
        {
            private const int GlColorBufferBit = 0x00004000;
            private const int GlFloat = 0x1406;
            private const int GlFragmentShader = 0x8B30;
            private const int GlLinear = 0x2601;
            private const int GlNearest = 0x2600;
            private const int GlRgba = 0x1908;
            private const int GlTexture0 = 0x84C0;
            private const int GlTexture2D = 0x0DE1;
            private const int GlTextureMagFilter = 0x2800;
            private const int GlTextureMinFilter = 0x2801;
            private const int GlTextureWrapS = 0x2802;
            private const int GlTextureWrapT = 0x2803;
            private const int GlClampToEdge = 0x812F;
            private const int GlTriangles = 0x0004;
            private const int GlUnsignedByte = 0x1401;
            private const int GlVendor = 0x1F00;
            private const int GlRenderer = 0x1F01;
            private const int GlVersion = 0x1F02;
            private const int GlVertexShader = 0x8B31;
            private const int GlShadingLanguageVersion = 0x8B8C;
            private const string VertexShaderSourceEs100 = """
                #version 100
                attribute vec2 aPos;
                attribute vec2 aUv;
                varying vec2 vUv;
                void main()
                {
                    vUv = aUv;
                    gl_Position = vec4(aPos, 0.0, 1.0);
                }
                """;
            private const string FragmentShaderSourceEs100 = """
                #version 100
                precision mediump float;
                varying vec2 vUv;
                uniform sampler2D uTex;
                uniform float uForceOpaque;
                uniform float uApplyScanlines;
                uniform float uScanlineDarken;
                uniform vec2 uTextureSize;
                uniform float uInterlaceBlend;
                uniform float uInterlaceFieldParity;

                vec4 softenInterlace(vec4 color)
                {
                    if (uInterlaceBlend <= 0.5 || uTextureSize.y < 2.0)
                        return color;

                    float row = floor(vUv.y * uTextureSize.y);
                    float rowParity = mod(row, 2.0);
                    if (abs(rowParity - uInterlaceFieldParity) <= 0.5)
                        return color;

                    vec2 texel = vec2(1.0 / uTextureSize.x, 1.0 / uTextureSize.y);
                    vec2 uvUp = vec2(vUv.x, max(0.0, vUv.y - texel.y));
                    vec2 uvDown = vec2(vUv.x, min(1.0, vUv.y + texel.y));
                    vec4 blended = (texture2D(uTex, uvUp) + texture2D(uTex, uvDown)) * 0.5;
                    return mix(color, blended, 0.65);
                }

                void main()
                {
                    vec4 color = texture2D(uTex, vUv);
                    color = softenInterlace(color);
                    if (uApplyScanlines > 0.5 && mod(floor(vUv.y * uTextureSize.y), 2.0) > 0.5)
                        color.rgb *= uScanlineDarken;
                    float alpha = uForceOpaque > 0.5 ? 1.0 : color.a;
                    gl_FragColor = vec4(color.b, color.g, color.r, alpha);
                }
                """;
            private const string VertexShaderSourceEs300 = """
                #version 300 es
                in vec2 aPos;
                in vec2 aUv;
                out vec2 vUv;
                void main()
                {
                    vUv = aUv;
                    gl_Position = vec4(aPos, 0.0, 1.0);
                }
                """;
            private const string FragmentShaderSourceEs300 = """
                #version 300 es
                precision mediump float;
                in vec2 vUv;
                uniform sampler2D uTex;
                uniform float uForceOpaque;
                uniform float uApplyScanlines;
                uniform float uScanlineDarken;
                uniform vec2 uTextureSize;
                uniform float uInterlaceBlend;
                uniform float uInterlaceFieldParity;
                out vec4 fragColor;

                vec4 softenInterlace(vec4 color)
                {
                    if (uInterlaceBlend <= 0.5 || uTextureSize.y < 2.0)
                        return color;

                    float row = floor(vUv.y * uTextureSize.y);
                    float rowParity = mod(row, 2.0);
                    if (abs(rowParity - uInterlaceFieldParity) <= 0.5)
                        return color;

                    vec2 texel = vec2(1.0 / uTextureSize.x, 1.0 / uTextureSize.y);
                    vec2 uvUp = vec2(vUv.x, max(0.0, vUv.y - texel.y));
                    vec2 uvDown = vec2(vUv.x, min(1.0, vUv.y + texel.y));
                    vec4 blended = (texture(uTex, uvUp) + texture(uTex, uvDown)) * 0.5;
                    return mix(color, blended, 0.65);
                }

                void main()
                {
                    vec4 color = texture(uTex, vUv);
                    color = softenInterlace(color);
                    if (uApplyScanlines > 0.5 && mod(floor(vUv.y * uTextureSize.y), 2.0) > 0.5)
                        color.rgb *= uScanlineDarken;
                    float alpha = uForceOpaque > 0.5 ? 1.0 : color.a;
                    fragColor = vec4(color.b, color.g, color.r, alpha);
                }
                """;

            private static readonly float[] s_quadVertices =
            {
                -1f, -1f, 0f, 1f,
                 1f, -1f, 1f, 1f,
                -1f,  1f, 0f, 0f,
                -1f,  1f, 0f, 0f,
                 1f, -1f, 1f, 1f,
                 1f,  1f, 1f, 0f
            };

            private readonly AndroidNativeGlHost _host;
            private readonly FloatBuffer _vertexBuffer;
            private readonly object _surfaceSync = new();
            private float _touchDownX;
            private float _touchDownY;
            private bool _trackingTap;
            private int _programId;
            private int _textureId;
            private int _positionLocation = -1;
            private int _uvLocation = -1;
            private int _samplerLocation = -1;
            private int _forceOpaqueLocation = -1;
            private int _scanlinesLocation = -1;
            private int _scanlineDarkenLocation = -1;
            private int _textureSizeLocation = -1;
            private int _interlaceBlendLocation = -1;
            private int _interlaceFieldParityLocation = -1;
            private int _textureWidth;
            private int _textureHeight;
            private bool _textureUsesNearest = true;
            private bool _forceTextureUpload = true;
            private int _surfaceWidth;
            private int _surfaceHeight;
            private bool _surfaceReady;
            private bool _released;
            private bool _renderFailed;
            private ByteBuffer? _directUploadBuffer;
            private int _directUploadCapacity;

            private readonly record struct ShaderSources(string Label, string VertexSource, string FragmentSource);

            public AndroidGlSurfaceView(Context context, AndroidNativeGlHost host) : base(context)
            {
                _host = host;
                Holder?.AddCallback(this);
                SetZOrderOnTop(false);
                SetZOrderMediaOverlay(false);
                SetSurfaceLifecycle(SurfaceViewLifecycle.FollowsAttachment);
                SetEGLContextClientVersion(2);
                PreserveEGLContextOnPause = true;
                SetRenderer(this);
                Clickable = true;
                Focusable = false;
                FocusableInTouchMode = false;

                ByteBuffer vertexByteBuffer = ByteBuffer.AllocateDirect(s_quadVertices.Length * sizeof(float));
                vertexByteBuffer.Order(ByteOrder.NativeOrder()!);
                _vertexBuffer = vertexByteBuffer.AsFloatBuffer();
                _vertexBuffer.Put(s_quadVertices);
                _vertexBuffer.Position(0);
            }

            public override bool OnTouchEvent(MotionEvent? e)
            {
                if (e == null)
                    return false;

                if (_host.IsLandscapeOverlayEnabled())
                    return false;

                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                        _touchDownX = e.GetX();
                        _touchDownY = e.GetY();
                        _trackingTap = true;
                        return true;
                    case MotionEventActions.Move:
                        if (_trackingTap && (Math.Abs(e.GetX() - _touchDownX) > 18f || Math.Abs(e.GetY() - _touchDownY) > 18f))
                            _trackingTap = false;
                        return true;
                    case MotionEventActions.Up:
                        bool shouldToggle = _trackingTap;
                        _trackingTap = false;
                        if (shouldToggle)
                            _host.NotifyScreenTapped();
                        return true;
                    case MotionEventActions.Cancel:
                        _trackingTap = false;
                        return true;
                }

                return base.OnTouchEvent(e);
            }

            public void ReleaseSurface()
            {
                _released = true;
                lock (_surfaceSync)
                {
                    _surfaceReady = false;
                    _surfaceWidth = 0;
                    _surfaceHeight = 0;
                }

                _host.NoteSurfaceDestroyed();
                try
                {
                    QueueEvent(DestroyGlResources);
                }
                catch
                {
                }

                try
                {
                    OnPause();
                }
                catch
                {
                }
            }

            public void RequestRenderSafe()
            {
                if (_released)
                    return;

                try
                {
                    RequestRender();
                }
                catch
                {
                }
            }

            public void SurfaceCreated(ISurfaceHolder holder)
            {
                lock (_surfaceSync)
                {
                    _released = false;
                    _surfaceReady = true;
                }

                _host.NoteSurfaceAvailable();
                RequestRenderSafe();
            }

            public void SurfaceDestroyed(ISurfaceHolder holder)
            {
                lock (_surfaceSync)
                {
                    _surfaceReady = false;
                    _surfaceWidth = 0;
                    _surfaceHeight = 0;
                }

                _host.NoteSurfaceDestroyed();
                try
                {
                    QueueEvent(DestroyGlResources);
                }
                catch
                {
                }
            }

            public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
            {
                lock (_surfaceSync)
                {
                    _surfaceWidth = width;
                    _surfaceHeight = height;
                    _surfaceReady = width > 0 && height > 0;
                }

                _host.NoteSurfaceSizeChanged(width, height);
                RequestRenderSafe();
            }

            public void OnSurfaceCreated(Javax.Microedition.Khronos.Opengles.IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
            {
                try
                {
                    _renderFailed = false;
                    _host.NoteGlInfo(
                        SafeGetGlString(GlVendor),
                        SafeGetGlString(GlRenderer),
                        SafeGetGlString(GlVersion),
                        SafeGetGlString(GlShadingLanguageVersion));
                    CreateProgramAndTexture();
                    _host.NoteInitSuccess();
                    _host.NoteGlInit();
                }
                catch (Exception ex)
                {
                    _renderFailed = true;
                    _host.NoteInitFailure(ex.Message, BuildInitDetails(ex));
                }
            }

            public void OnSurfaceChanged(Javax.Microedition.Khronos.Opengles.IGL10? gl, int width, int height)
            {
                lock (_surfaceSync)
                {
                    _surfaceWidth = width;
                    _surfaceHeight = height;
                    _surfaceReady = width > 0 && height > 0;
                }

                GLES20.GlViewport(0, 0, width, height);
            }

            public void OnDrawFrame(Javax.Microedition.Khronos.Opengles.IGL10? gl)
            {
                if (_released || _renderFailed)
                    return;

                int surfaceWidth;
                int surfaceHeight;
                lock (_surfaceSync)
                {
                    if (!_surfaceReady || _surfaceWidth <= 0 || _surfaceHeight <= 0)
                        return;

                    surfaceWidth = _surfaceWidth;
                    surfaceHeight = _surfaceHeight;
                }

                try
                {
                    DrawFrame(surfaceWidth, surfaceHeight);
                }
                catch (Exception ex)
                {
                    _renderFailed = true;
                    _host.NoteInitFailure(ex.Message, BuildInitDetails(ex));
                }
            }

            private void DrawFrame(int surfaceWidth, int surfaceHeight)
            {
                long renderStart = Stopwatch.GetTimestamp();
                long uploadTicks = 0;

                GLES20.GlViewport(0, 0, surfaceWidth, surfaceHeight);
                _host.BeginRender(
                    out byte[] frameBytes,
                    out int frameWidth,
                    out int frameHeight,
                    out bool frameDirty,
                    out bool sharpPixelsEnabled,
                    out bool forceOpaque,
                    out bool applyScanlines,
                    out float scanlineDarken,
                    out bool interlaceBlendEnabled,
                    out int interlaceBlendFieldParity);
                if (_programId == 0 || _textureId == 0)
                {
                    GLES20.GlClearColor(0f, 0f, 0f, 1f);
                    GLES20.GlClear(GlColorBufferBit);
                    _host.NoteBlackClear();
                    _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, 0, uploaded: false);
                    return;
                }

                GLES20.GlActiveTexture(GlTexture0);
                GLES20.GlBindTexture(GlTexture2D, _textureId);
                UpdateTextureFiltering(sharpPixelsEnabled);

                bool haveUploadedTexture = _textureWidth > 0 && _textureHeight > 0;
                if (frameWidth <= 0 || frameHeight <= 0)
                {
                    if (!haveUploadedTexture)
                    {
                        GLES20.GlClearColor(0f, 0f, 0f, 1f);
                        GLES20.GlClear(GlColorBufferBit);
                        _host.NoteBlackClear();
                        _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, 0, uploaded: false);
                        return;
                    }

                    frameWidth = _textureWidth;
                    frameHeight = _textureHeight;
                    _host.NoteTextureReuse();
                }

                bool uploaded = false;
                bool haveFrameBytes = frameBytes.Length >= frameWidth * frameHeight * 4;
                bool mustUploadTexture = _forceTextureUpload || frameDirty;
                if (mustUploadTexture && haveFrameBytes)
                {
                    EnsureTextureStorage(frameWidth, frameHeight);
                    long uploadStart = Stopwatch.GetTimestamp();
                    ByteBuffer pixels = GetOrCreateUploadBuffer(frameBytes, frameWidth * frameHeight * 4);
                    GLES20.GlTexSubImage2D(GlTexture2D, 0, 0, 0, frameWidth, frameHeight, GlRgba, GlUnsignedByte, pixels);
                    uploadTicks = Stopwatch.GetTimestamp() - uploadStart;
                    uploaded = true;
                    _forceTextureUpload = false;
                }
                else if (!haveFrameBytes && haveUploadedTexture)
                {
                    _host.NoteTextureReuse();
                }
                else if (!haveFrameBytes)
                {
                    GLES20.GlClearColor(0f, 0f, 0f, 1f);
                    GLES20.GlClear(GlColorBufferBit);
                    _host.NoteBlackClear();
                    _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, 0, uploaded: false);
                    return;
                }

                GLES20.GlUseProgram(_programId);
                GLES20.GlUniform1i(_samplerLocation, 0);
                GLES20.GlUniform1f(_forceOpaqueLocation, forceOpaque ? 1.0f : 0.0f);
                if (_scanlinesLocation >= 0)
                    GLES20.GlUniform1f(_scanlinesLocation, applyScanlines ? 1.0f : 0.0f);
                if (_scanlineDarkenLocation >= 0)
                    GLES20.GlUniform1f(_scanlineDarkenLocation, scanlineDarken);
                if (_textureSizeLocation >= 0)
                    GLES20.GlUniform2f(_textureSizeLocation, frameWidth, frameHeight);
                if (_interlaceBlendLocation >= 0)
                    GLES20.GlUniform1f(_interlaceBlendLocation, interlaceBlendEnabled ? 1.0f : 0.0f);
                if (_interlaceFieldParityLocation >= 0)
                    GLES20.GlUniform1f(_interlaceFieldParityLocation, interlaceBlendFieldParity == 1 ? 1.0f : 0.0f);

                _vertexBuffer.Position(0);
                GLES20.GlEnableVertexAttribArray(_positionLocation);
                GLES20.GlVertexAttribPointer(_positionLocation, 2, GlFloat, false, 4 * sizeof(float), _vertexBuffer);
                _vertexBuffer.Position(2);
                GLES20.GlEnableVertexAttribArray(_uvLocation);
                GLES20.GlVertexAttribPointer(_uvLocation, 2, GlFloat, false, 4 * sizeof(float), _vertexBuffer);
                GLES20.GlDrawArrays(GlTriangles, 0, 6);

                _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, uploadTicks, uploaded);
            }

            private ByteBuffer GetOrCreateUploadBuffer(byte[] frameBytes, int requiredBytes)
            {
                if (_directUploadBuffer == null || _directUploadCapacity < requiredBytes)
                {
                    _directUploadBuffer?.Dispose();
                    _directUploadBuffer = ByteBuffer.AllocateDirect(requiredBytes);
                    _directUploadCapacity = requiredBytes;
                }

                ByteBuffer uploadBuffer = _directUploadBuffer;
                uploadBuffer.Position(0);
                uploadBuffer.Put(frameBytes, 0, requiredBytes);
                uploadBuffer.Position(0);
                return uploadBuffer;
            }

            private void CreateProgramAndTexture()
            {
                DestroyGlResources();

                ShaderSources preferredShaders = SelectShaderSources(
                    SafeGetGlString(GlVersion),
                    SafeGetGlString(GlShadingLanguageVersion));
                ShaderSources fallbackShaders = string.Equals(preferredShaders.Label, "ES300", StringComparison.Ordinal)
                    ? new ShaderSources("ES100", VertexShaderSourceEs100, FragmentShaderSourceEs100)
                    : new ShaderSources("ES300", VertexShaderSourceEs300, FragmentShaderSourceEs300);

                string? firstFailure = null;
                try
                {
                    CreateProgramAndTexture(preferredShaders);
                    return;
                }
                catch (Exception ex)
                {
                    firstFailure = BuildShaderAttemptDetails(preferredShaders, ex);
                }

                CreateProgramAndTexture(fallbackShaders);
            }

            private void CreateProgramAndTexture(ShaderSources shaderSources)
            {
                int vertexShader = CompileShader(GlVertexShader, shaderSources.VertexSource);
                int fragmentShader = CompileShader(GlFragmentShader, shaderSources.FragmentSource);

                _programId = GLES20.GlCreateProgram();
                if (_programId == 0)
                    throw new InvalidOperationException($"Native Android GL program creation failed: err=0x{GLES20.GlGetError():X}");
                GLES20.GlAttachShader(_programId, vertexShader);
                GLES20.GlAttachShader(_programId, fragmentShader);
                GLES20.GlBindAttribLocation(_programId, 0, "aPos");
                GLES20.GlBindAttribLocation(_programId, 1, "aUv");
                GLES20.GlLinkProgram(_programId);

                int[] status = new int[1];
                GLES20.GlGetProgramiv(_programId, GLES20.GlLinkStatus, status, 0);
                if (status[0] == 0)
                {
                    string info = GLES20.GlGetProgramInfoLog(_programId) ?? "unknown";
                    throw new InvalidOperationException($"Native Android GL link failed: {info} err=0x{GLES20.GlGetError():X}");
                }

                GLES20.GlDeleteShader(vertexShader);
                GLES20.GlDeleteShader(fragmentShader);

                _positionLocation = GLES20.GlGetAttribLocation(_programId, "aPos");
                _uvLocation = GLES20.GlGetAttribLocation(_programId, "aUv");
                _samplerLocation = GLES20.GlGetUniformLocation(_programId, "uTex");
                _forceOpaqueLocation = GLES20.GlGetUniformLocation(_programId, "uForceOpaque");
                _scanlinesLocation = GLES20.GlGetUniformLocation(_programId, "uApplyScanlines");
                _scanlineDarkenLocation = GLES20.GlGetUniformLocation(_programId, "uScanlineDarken");
                _textureSizeLocation = GLES20.GlGetUniformLocation(_programId, "uTextureSize");
                _interlaceBlendLocation = GLES20.GlGetUniformLocation(_programId, "uInterlaceBlend");
                _interlaceFieldParityLocation = GLES20.GlGetUniformLocation(_programId, "uInterlaceFieldParity");

                int[] textures = new int[1];
                GLES20.GlGenTextures(1, textures, 0);
                _textureId = textures[0];
                if (_textureId == 0)
                    throw new InvalidOperationException($"Native Android GL texture creation failed: err=0x{GLES20.GlGetError():X}");
                GLES20.GlBindTexture(GlTexture2D, _textureId);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
                _textureUsesNearest = true;
                _textureWidth = 0;
                _textureHeight = 0;
                _forceTextureUpload = true;
            }

            private static int CompileShader(int shaderType, string source)
            {
                int shader = GLES20.GlCreateShader(shaderType);
                if (shader == 0)
                    throw new InvalidOperationException($"Native Android GL shader creation failed: type=0x{shaderType:X} err=0x{GLES20.GlGetError():X}");
                GLES20.GlShaderSource(shader, source);
                GLES20.GlCompileShader(shader);

                int[] status = new int[1];
                GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, status, 0);
                if (status[0] != 0)
                    return shader;

                string info = GLES20.GlGetShaderInfoLog(shader) ?? "unknown";
                GLES20.GlDeleteShader(shader);
                string shaderName = shaderType == GlVertexShader ? "vertex" : shaderType == GlFragmentShader ? "fragment" : $"0x{shaderType:X}";
                throw new InvalidOperationException($"Native Android GL {shaderName} shader compile failed: {info} err=0x{GLES20.GlGetError():X}");
            }

            private static string SafeGetGlString(int name)
            {
                try
                {
                    return GLES20.GlGetString(name) ?? "unknown";
                }
                catch (Exception ex)
                {
                    return $"error:{ex.GetType().Name}";
                }
            }

            private static ShaderSources SelectShaderSources(string glVersion, string shadingLanguageVersion)
            {
                int shadingMajor = ParseLeadingVersionComponent(shadingLanguageVersion);
                int glMajor = ParseLeadingVersionComponent(glVersion);
                if (shadingMajor >= 3 || glMajor >= 3)
                    return new ShaderSources("ES300", VertexShaderSourceEs300, FragmentShaderSourceEs300);

                return new ShaderSources("ES100", VertexShaderSourceEs100, FragmentShaderSourceEs100);
            }

            private static int ParseLeadingVersionComponent(string value)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    if (!char.IsDigit(value[i]))
                        continue;

                    int start = i;
                    while (i < value.Length && char.IsDigit(value[i]))
                        i++;

                    if (int.TryParse(value[start..i], out int parsed))
                        return parsed;

                    break;
                }

                return 0;
            }

            private static string BuildShaderAttemptDetails(ShaderSources shaderSources, Exception ex)
            {
                return
                    $"Mode:{shaderSources.Label}\n" +
                    $"Error:{ex.Message}\n" +
                    $"Vertex Shader:\n{shaderSources.VertexSource}\n" +
                    $"Fragment Shader:\n{shaderSources.FragmentSource}";
            }

            private static string BuildInitDetails(Exception ex)
            {
                return $"GL Init Detail:{ex.Message}";
            }

            private void EnsureTextureStorage(int frameWidth, int frameHeight)
            {
                if (_textureWidth == frameWidth && _textureHeight == frameHeight)
                    return;

                GLES20.GlTexImage2D(GlTexture2D, 0, GlRgba, frameWidth, frameHeight, 0, GlRgba, GlUnsignedByte, null);
                _textureWidth = frameWidth;
                _textureHeight = frameHeight;
            }

            private void UpdateTextureFiltering(bool sharpPixelsEnabled)
            {
                bool useNearest = sharpPixelsEnabled;
                if (_textureUsesNearest == useNearest)
                    return;

                int filter = useNearest ? GlNearest : GlLinear;
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMinFilter, filter);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMagFilter, filter);
                _textureUsesNearest = useNearest;
            }

            private void DestroyGlResources()
            {
                if (_programId != 0)
                {
                    GLES20.GlDeleteProgram(_programId);
                    _programId = 0;
                }

                if (_textureId != 0)
                {
                    int[] textures = { _textureId };
                    GLES20.GlDeleteTextures(1, textures, 0);
                    _textureId = 0;
                }

                _positionLocation = -1;
                _uvLocation = -1;
                _samplerLocation = -1;
                _forceOpaqueLocation = -1;
                _scanlinesLocation = -1;
                _scanlineDarkenLocation = -1;
                _textureSizeLocation = -1;
                _interlaceBlendLocation = -1;
                _interlaceFieldParityLocation = -1;
                _textureWidth = 0;
                _textureHeight = 0;
                _forceTextureUpload = true;
                _directUploadBuffer?.Dispose();
                _directUploadBuffer = null;
                _directUploadCapacity = 0;
            }
        }

        private sealed class AndroidGlTextureView : SurfaceView, ISurfaceHolderCallback
        {
            private const int FrameRepeatIntervalMs = 16;
            private const int GlColorBufferBit = 0x00004000;
            private const int GlFloat = 0x1406;
            private const int GlFragmentShader = 0x8B30;
            private const int GlLinear = 0x2601;
            private const int GlNearest = 0x2600;
            private const int GlRgba = 0x1908;
            private const int GlTexture0 = 0x84C0;
            private const int GlTexture2D = 0x0DE1;
            private const int GlTextureMagFilter = 0x2800;
            private const int GlTextureMinFilter = 0x2801;
            private const int GlTextureWrapS = 0x2802;
            private const int GlTextureWrapT = 0x2803;
            private const int GlClampToEdge = 0x812F;
            private const int GlTriangles = 0x0004;
            private const int GlUnsignedByte = 0x1401;
            private const int GlVendor = 0x1F00;
            private const int GlRenderer = 0x1F01;
            private const int GlVersion = 0x1F02;
            private const int GlVertexShader = 0x8B31;
            private const int GlShadingLanguageVersion = 0x8B8C;
            private const string VertexShaderSourceEs100 = """
                #version 100
                attribute vec2 aPos;
                attribute vec2 aUv;
                varying vec2 vUv;
                void main()
                {
                    vUv = aUv;
                    gl_Position = vec4(aPos, 0.0, 1.0);
                }
                """;
            private const string FragmentShaderSourceEs100 = """
                #version 100
                precision mediump float;
                varying vec2 vUv;
                uniform sampler2D uTex;
                uniform float uForceOpaque;
                uniform float uApplyScanlines;
                uniform float uScanlineDarken;
                uniform vec2 uTextureSize;
                uniform float uInterlaceBlend;
                uniform float uInterlaceFieldParity;

                vec4 softenInterlace(vec4 color)
                {
                    if (uInterlaceBlend <= 0.5 || uTextureSize.y < 2.0)
                        return color;

                    float row = floor(vUv.y * uTextureSize.y);
                    float rowParity = mod(row, 2.0);
                    if (abs(rowParity - uInterlaceFieldParity) <= 0.5)
                        return color;

                    vec2 texel = vec2(1.0 / uTextureSize.x, 1.0 / uTextureSize.y);
                    vec2 uvUp = vec2(vUv.x, max(0.0, vUv.y - texel.y));
                    vec2 uvDown = vec2(vUv.x, min(1.0, vUv.y + texel.y));
                    vec4 blended = (texture2D(uTex, uvUp) + texture2D(uTex, uvDown)) * 0.5;
                    return mix(color, blended, 0.65);
                }

                void main()
                {
                    vec4 color = texture2D(uTex, vUv);
                    color = softenInterlace(color);
                    if (uApplyScanlines > 0.5 && mod(floor(vUv.y * uTextureSize.y), 2.0) > 0.5)
                        color.rgb *= uScanlineDarken;
                    float alpha = uForceOpaque > 0.5 ? 1.0 : color.a;
                    gl_FragColor = vec4(color.b, color.g, color.r, alpha);
                }
                """;
            private const string VertexShaderSourceEs300 = """
                #version 300 es
                in vec2 aPos;
                in vec2 aUv;
                out vec2 vUv;
                void main()
                {
                    vUv = aUv;
                    gl_Position = vec4(aPos, 0.0, 1.0);
                }
                """;
            private const string FragmentShaderSourceEs300 = """
                #version 300 es
                precision mediump float;
                in vec2 vUv;
                uniform sampler2D uTex;
                uniform float uForceOpaque;
                uniform float uApplyScanlines;
                uniform float uScanlineDarken;
                uniform vec2 uTextureSize;
                uniform float uInterlaceBlend;
                uniform float uInterlaceFieldParity;
                out vec4 fragColor;

                vec4 softenInterlace(vec4 color)
                {
                    if (uInterlaceBlend <= 0.5 || uTextureSize.y < 2.0)
                        return color;

                    float row = floor(vUv.y * uTextureSize.y);
                    float rowParity = mod(row, 2.0);
                    if (abs(rowParity - uInterlaceFieldParity) <= 0.5)
                        return color;

                    vec2 texel = vec2(1.0 / uTextureSize.x, 1.0 / uTextureSize.y);
                    vec2 uvUp = vec2(vUv.x, max(0.0, vUv.y - texel.y));
                    vec2 uvDown = vec2(vUv.x, min(1.0, vUv.y + texel.y));
                    vec4 blended = (texture(uTex, uvUp) + texture(uTex, uvDown)) * 0.5;
                    return mix(color, blended, 0.65);
                }

                void main()
                {
                    vec4 color = texture(uTex, vUv);
                    color = softenInterlace(color);
                    if (uApplyScanlines > 0.5 && mod(floor(vUv.y * uTextureSize.y), 2.0) > 0.5)
                        color.rgb *= uScanlineDarken;
                    float alpha = uForceOpaque > 0.5 ? 1.0 : color.a;
                    fragColor = vec4(color.b, color.g, color.r, alpha);
                }
                """;

            private static readonly float[] s_quadVertices =
            {
                -1f, -1f, 0f, 1f,
                 1f, -1f, 1f, 1f,
                -1f,  1f, 0f, 0f,
                -1f,  1f, 0f, 0f,
                 1f, -1f, 1f, 1f,
                 1f,  1f, 1f, 0f
            };

            private readonly AndroidNativeGlHost _host;
            private readonly FloatBuffer _vertexBuffer;
            private readonly AutoResetEvent _renderSignal = new(initialState: false);
            private readonly object _surfaceSync = new();
            private float _touchDownX;
            private float _touchDownY;
            private bool _trackingTap;
            private int _programId;
            private int _textureId;
            private int _positionLocation = -1;
            private int _uvLocation = -1;
            private int _samplerLocation = -1;
            private int _forceOpaqueLocation = -1;
            private int _scanlinesLocation = -1;
            private int _scanlineDarkenLocation = -1;
            private int _textureSizeLocation = -1;
            private int _interlaceBlendLocation = -1;
            private int _interlaceFieldParityLocation = -1;
            private int _textureWidth;
            private int _textureHeight;
            private bool _textureUsesNearest = true;
            private bool _forceTextureUpload = true;
            private Thread? _renderThread;
            private ISurfaceHolder? _surfaceHolder;
            private EGLDisplay? _eglDisplay;
            private EGLContext? _eglContext;
            private EGLSurface? _eglSurface;
            private EGLConfig? _eglConfig;
            private int _surfaceWidth;
            private int _surfaceHeight;
            private bool _surfaceReady;
            private volatile bool _released;
            private volatile bool _renderThreadRunning;
            private ByteBuffer? _directUploadBuffer;
            private int _directUploadCapacity;

            private readonly record struct ShaderSources(string Label, string VertexSource, string FragmentSource);

            public AndroidGlTextureView(Context context, AndroidNativeGlHost host) : base(context)
            {
                _host = host;
                // SurfaceView gives Android a more stable dedicated producer surface than
                // TextureView, which has shown periodic black flashes on some devices.
                Holder?.AddCallback(this);
                SetZOrderOnTop(false);
                SetZOrderMediaOverlay(false);
                SetSurfaceLifecycle(SurfaceViewLifecycle.FollowsAttachment);
                Clickable = true;
                Focusable = false;
                FocusableInTouchMode = false;

                ByteBuffer vertexByteBuffer = ByteBuffer.AllocateDirect(s_quadVertices.Length * sizeof(float));
                vertexByteBuffer.Order(ByteOrder.NativeOrder()!);
                _vertexBuffer = vertexByteBuffer.AsFloatBuffer();
                _vertexBuffer.Put(s_quadVertices);
                _vertexBuffer.Position(0);
            }

            public override bool OnTouchEvent(MotionEvent? e)
            {
                if (e == null)
                    return false;

                if (_host.IsLandscapeOverlayEnabled())
                    return false;

                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                        _touchDownX = e.GetX();
                        _touchDownY = e.GetY();
                        _trackingTap = true;
                        return true;
                    case MotionEventActions.Move:
                        if (_trackingTap && (Math.Abs(e.GetX() - _touchDownX) > 18f || Math.Abs(e.GetY() - _touchDownY) > 18f))
                            _trackingTap = false;
                        return true;
                    case MotionEventActions.Up:
                        bool shouldToggle = _trackingTap;
                        _trackingTap = false;
                        if (shouldToggle)
                            _host.NotifyScreenTapped();
                        return true;
                    case MotionEventActions.Cancel:
                        _trackingTap = false;
                        return true;
                }

                return base.OnTouchEvent(e);
            }

            public void ReleaseSurface()
            {
                _released = true;
                lock (_surfaceSync)
                {
                    _surfaceReady = false;
                    _surfaceHolder = null;
                    _surfaceWidth = 0;
                    _surfaceHeight = 0;
                }

                _renderSignal.Set();
                JoinRenderThread();
                CleanupGl();
            }

            public void RequestRenderSafe()
            {
                if (_released)
                    return;

                _renderSignal.Set();
            }

            public void SurfaceCreated(ISurfaceHolder holder)
            {
                lock (_surfaceSync)
                {
                    _surfaceHolder = holder;
                    _surfaceReady = true;
                }

                _host.NoteSurfaceAvailable();
                StartRenderThreadIfNeeded();
                _renderSignal.Set();
            }

            public void SurfaceDestroyed(ISurfaceHolder holder)
            {
                lock (_surfaceSync)
                {
                    _surfaceReady = false;
                    if (ReferenceEquals(_surfaceHolder, holder))
                        _surfaceHolder = null;
                    _surfaceWidth = 0;
                    _surfaceHeight = 0;
                }

                _host.NoteSurfaceDestroyed();
                _renderSignal.Set();
                JoinRenderThread();
                CleanupGl();
            }

            public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
            {
                lock (_surfaceSync)
                {
                    _surfaceHolder = holder;
                    _surfaceWidth = width;
                    _surfaceHeight = height;
                    _surfaceReady = true;
                }

                _host.NoteSurfaceSizeChanged(width, height);
                if (width > 0 && height > 0)
                    StartRenderThreadIfNeeded();
                _renderSignal.Set();
            }

            private void StartRenderThreadIfNeeded()
            {
                if (_renderThreadRunning)
                    return;

                _released = false;
                _renderThreadRunning = true;
                _renderThread = new Thread(RenderLoop)
                {
                    IsBackground = true,
                    Name = "EutherDriveAndroidTextureGL"
                };
                _renderThread.Start();
            }

            private void JoinRenderThread()
            {
                try
                {
                    if (_renderThread is { IsAlive: true } thread && !ReferenceEquals(Thread.CurrentThread, thread))
                        thread.Join(500);
                }
                catch
                {
                }
                finally
                {
                    _renderThread = null;
                    _renderThreadRunning = false;
                }
            }

            private void RenderLoop()
            {
                try
                {
                    while (!_released)
                    {
                        bool signaled = _renderSignal.WaitOne(FrameRepeatIntervalMs);
                        if (_released)
                            break;

                        if (!signaled && !_host.HasRenderableFrame())
                            continue;

                        ISurfaceHolder? surfaceHolder;
                        int surfaceWidth;
                        int surfaceHeight;
                        lock (_surfaceSync)
                        {
                            if (!_surfaceReady || _surfaceHolder == null)
                                break;

                            if (_surfaceWidth <= 0 || _surfaceHeight <= 0)
                                continue;

                            surfaceHolder = _surfaceHolder;
                            surfaceWidth = _surfaceWidth;
                            surfaceHeight = _surfaceHeight;
                        }

                        EnsureGlReady(surfaceHolder, surfaceWidth, surfaceHeight);
                        DrawFrame(surfaceWidth, surfaceHeight);

                        if (_eglDisplay != null && _eglSurface != null && !EGL14.EglSwapBuffers(_eglDisplay, _eglSurface))
                            throw new InvalidOperationException($"Native Android GL swap failed: err=0x{EGL14.EglGetError():X}");
                    }
                }
                catch (Exception ex)
                {
                    _host.NoteInitFailure(ex.Message, BuildInitDetails(ex));
                }
                finally
                {
                    CleanupGl();
                    _renderThreadRunning = false;
                }
            }

            private void EnsureGlReady(ISurfaceHolder surfaceHolder, int surfaceWidth, int surfaceHeight)
            {
                if (_eglDisplay != null && _eglContext != null && _eglSurface != null)
                {
                    GLES20.GlViewport(0, 0, surfaceWidth, surfaceHeight);
                    return;
                }

                EGLDisplay display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
                if (IsNoDisplay(display))
                    throw new InvalidOperationException($"Native Android GL display init failed: err=0x{EGL14.EglGetError():X}");

                int[] versions = new int[2];
                if (!EGL14.EglInitialize(display, versions, 0, versions, 1))
                    throw new InvalidOperationException($"Native Android GL EGL init failed: err=0x{EGL14.EglGetError():X}");

                int[] configAttributes =
                {
                    EGL14.EglRedSize, 8,
                    EGL14.EglGreenSize, 8,
                    EGL14.EglBlueSize, 8,
                    EGL14.EglAlphaSize, 0,
                    EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
                    EGL14.EglNone
                };
                EGLConfig[] configs = new EGLConfig[1];
                int[] numConfigs = new int[1];
                if (!EGL14.EglChooseConfig(display, configAttributes, 0, configs, 0, 1, numConfigs, 0) || numConfigs[0] == 0)
                    throw new InvalidOperationException($"Native Android GL config selection failed: err=0x{EGL14.EglGetError():X}");

                int[] contextAttributes =
                {
                    EGL14.EglContextClientVersion, 2,
                    EGL14.EglNone
                };
                EGLContext context = EGL14.EglCreateContext(display, configs[0], EGL14.EglNoContext, contextAttributes, 0);
                if (IsNoContext(context))
                    throw new InvalidOperationException($"Native Android GL context creation failed: err=0x{EGL14.EglGetError():X}");

                Surface? windowSurface = surfaceHolder.Surface;
                if (windowSurface == null || !windowSurface.IsValid)
                    throw new InvalidOperationException("Native Android GL surface holder is not valid.");
                int[] surfaceAttributes = { EGL14.EglNone };
                EGLSurface? eglSurface = EGL14.EglCreateWindowSurface(display, configs[0], windowSurface, surfaceAttributes, 0);
                if (IsNoSurface(eglSurface))
                {
                    throw new InvalidOperationException($"Native Android GL window surface creation failed: err=0x{EGL14.EglGetError():X}");
                }

                if (!EGL14.EglMakeCurrent(display, eglSurface, eglSurface, context))
                {
                    throw new InvalidOperationException($"Native Android GL make current failed: err=0x{EGL14.EglGetError():X}");
                }

                _eglDisplay = display;
                _eglConfig = configs[0];
                _eglContext = context;
                _eglSurface = eglSurface;
                EGL14.EglSwapInterval(display, 1);

                _host.NoteGlInfo(
                    SafeGetGlString(GlVendor),
                    SafeGetGlString(GlRenderer),
                    SafeGetGlString(GlVersion),
                    SafeGetGlString(GlShadingLanguageVersion));
                CreateProgramAndTexture();
                _host.NoteInitSuccess();
                _host.NoteGlInit();
                GLES20.GlViewport(0, 0, surfaceWidth, surfaceHeight);
            }

            private void DrawFrame(int surfaceWidth, int surfaceHeight)
            {
                long renderStart = Stopwatch.GetTimestamp();
                long uploadTicks = 0;

                GLES20.GlViewport(0, 0, surfaceWidth, surfaceHeight);
                _host.BeginRender(
                    out byte[] frameBytes,
                    out int frameWidth,
                    out int frameHeight,
                    out bool frameDirty,
                    out bool sharpPixelsEnabled,
                    out bool forceOpaque,
                    out bool applyScanlines,
                    out float scanlineDarken,
                    out bool interlaceBlendEnabled,
                    out int interlaceBlendFieldParity);
                if (_programId == 0 || _textureId == 0)
                {
                    GLES20.GlClearColor(0f, 0f, 0f, 1f);
                    GLES20.GlClear(GlColorBufferBit);
                    _host.NoteBlackClear();
                    _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, 0, uploaded: false);
                    return;
                }

                GLES20.GlActiveTexture(GlTexture0);
                GLES20.GlBindTexture(GlTexture2D, _textureId);
                UpdateTextureFiltering(sharpPixelsEnabled);

                bool haveUploadedTexture = _textureWidth > 0 && _textureHeight > 0;
                if (frameWidth <= 0 || frameHeight <= 0)
                {
                    if (!haveUploadedTexture)
                    {
                        GLES20.GlClearColor(0f, 0f, 0f, 1f);
                        GLES20.GlClear(GlColorBufferBit);
                        _host.NoteBlackClear();
                        _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, 0, uploaded: false);
                        return;
                    }

                    frameWidth = _textureWidth;
                    frameHeight = _textureHeight;
                    _host.NoteTextureReuse();
                }

                bool uploaded = false;
                bool haveFrameBytes = frameBytes.Length >= frameWidth * frameHeight * 4;
                bool mustUploadTexture = _forceTextureUpload || frameDirty;
                if (mustUploadTexture && haveFrameBytes)
                {
                    EnsureTextureStorage(frameWidth, frameHeight);
                    long uploadStart = Stopwatch.GetTimestamp();
                    ByteBuffer pixels = GetOrCreateUploadBuffer(frameBytes, frameWidth * frameHeight * 4);
                    GLES20.GlTexSubImage2D(GlTexture2D, 0, 0, 0, frameWidth, frameHeight, GlRgba, GlUnsignedByte, pixels);
                    uploadTicks = Stopwatch.GetTimestamp() - uploadStart;
                    uploaded = true;
                    _forceTextureUpload = false;
                }
                else if (!haveFrameBytes && haveUploadedTexture)
                {
                    _host.NoteTextureReuse();
                }
                else if (!haveFrameBytes)
                {
                    GLES20.GlClearColor(0f, 0f, 0f, 1f);
                    GLES20.GlClear(GlColorBufferBit);
                    _host.NoteBlackClear();
                    _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, 0, uploaded: false);
                    return;
                }

                GLES20.GlUseProgram(_programId);
                GLES20.GlUniform1i(_samplerLocation, 0);
                GLES20.GlUniform1f(_forceOpaqueLocation, forceOpaque ? 1.0f : 0.0f);
                if (_scanlinesLocation >= 0)
                    GLES20.GlUniform1f(_scanlinesLocation, applyScanlines ? 1.0f : 0.0f);
                if (_scanlineDarkenLocation >= 0)
                    GLES20.GlUniform1f(_scanlineDarkenLocation, scanlineDarken);
                if (_textureSizeLocation >= 0)
                    GLES20.GlUniform2f(_textureSizeLocation, frameWidth, frameHeight);
                if (_interlaceBlendLocation >= 0)
                    GLES20.GlUniform1f(_interlaceBlendLocation, interlaceBlendEnabled ? 1.0f : 0.0f);
                if (_interlaceFieldParityLocation >= 0)
                    GLES20.GlUniform1f(_interlaceFieldParityLocation, interlaceBlendFieldParity == 1 ? 1.0f : 0.0f);

                _vertexBuffer.Position(0);
                GLES20.GlEnableVertexAttribArray(_positionLocation);
                GLES20.GlVertexAttribPointer(_positionLocation, 2, GlFloat, false, 4 * sizeof(float), _vertexBuffer);
                _vertexBuffer.Position(2);
                GLES20.GlEnableVertexAttribArray(_uvLocation);
                GLES20.GlVertexAttribPointer(_uvLocation, 2, GlFloat, false, 4 * sizeof(float), _vertexBuffer);
                GLES20.GlDrawArrays(GlTriangles, 0, 6);

                _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, uploadTicks, uploaded);
            }

            private ByteBuffer GetOrCreateUploadBuffer(byte[] frameBytes, int requiredBytes)
            {
                if (_directUploadBuffer == null || _directUploadCapacity < requiredBytes)
                {
                    _directUploadBuffer?.Dispose();
                    _directUploadBuffer = ByteBuffer.AllocateDirect(requiredBytes);
                    _directUploadCapacity = requiredBytes;
                }

                ByteBuffer uploadBuffer = _directUploadBuffer;
                uploadBuffer.Position(0);
                uploadBuffer.Put(frameBytes, 0, requiredBytes);
                uploadBuffer.Position(0);
                return uploadBuffer;
            }

            private void CreateProgramAndTexture()
            {
                DestroyGlResources();

                ShaderSources preferredShaders = SelectShaderSources(
                    SafeGetGlString(GlVersion),
                    SafeGetGlString(GlShadingLanguageVersion));
                ShaderSources fallbackShaders = string.Equals(preferredShaders.Label, "ES300", StringComparison.Ordinal)
                    ? new ShaderSources("ES100", VertexShaderSourceEs100, FragmentShaderSourceEs100)
                    : new ShaderSources("ES300", VertexShaderSourceEs300, FragmentShaderSourceEs300);

                string? firstFailure = null;
                try
                {
                    CreateProgramAndTexture(preferredShaders);
                    return;
                }
                catch (Exception ex)
                {
                    firstFailure = BuildShaderAttemptDetails(preferredShaders, ex);
                }

                try
                {
                    CreateProgramAndTexture(fallbackShaders);
                    return;
                }
                catch (Exception ex)
                {
                    string secondFailure = BuildShaderAttemptDetails(fallbackShaders, ex);
                    throw new InvalidOperationException($"{firstFailure}\nFallback Attempt:\n{secondFailure}");
                }
            }

            private void CreateProgramAndTexture(ShaderSources shaderSources)
            {
                int vertexShader = CompileShader(GlVertexShader, shaderSources.VertexSource);
                int fragmentShader = CompileShader(GlFragmentShader, shaderSources.FragmentSource);

                _programId = GLES20.GlCreateProgram();
                if (_programId == 0)
                    throw new InvalidOperationException($"Native Android GL program creation failed: err=0x{GLES20.GlGetError():X}");
                GLES20.GlAttachShader(_programId, vertexShader);
                GLES20.GlAttachShader(_programId, fragmentShader);
                GLES20.GlBindAttribLocation(_programId, 0, "aPos");
                GLES20.GlBindAttribLocation(_programId, 1, "aUv");
                GLES20.GlLinkProgram(_programId);

                int[] status = new int[1];
                GLES20.GlGetProgramiv(_programId, GLES20.GlLinkStatus, status, 0);
                if (status[0] == 0)
                {
                    string info = GLES20.GlGetProgramInfoLog(_programId) ?? "unknown";
                    throw new InvalidOperationException($"Native Android GL link failed: {info} err=0x{GLES20.GlGetError():X}");
                }

                GLES20.GlDeleteShader(vertexShader);
                GLES20.GlDeleteShader(fragmentShader);

                _positionLocation = GLES20.GlGetAttribLocation(_programId, "aPos");
                _uvLocation = GLES20.GlGetAttribLocation(_programId, "aUv");
                _samplerLocation = GLES20.GlGetUniformLocation(_programId, "uTex");
                _forceOpaqueLocation = GLES20.GlGetUniformLocation(_programId, "uForceOpaque");
                _scanlinesLocation = GLES20.GlGetUniformLocation(_programId, "uApplyScanlines");
                _scanlineDarkenLocation = GLES20.GlGetUniformLocation(_programId, "uScanlineDarken");
                _textureSizeLocation = GLES20.GlGetUniformLocation(_programId, "uTextureSize");
                _interlaceBlendLocation = GLES20.GlGetUniformLocation(_programId, "uInterlaceBlend");
                _interlaceFieldParityLocation = GLES20.GlGetUniformLocation(_programId, "uInterlaceFieldParity");

                int[] textures = new int[1];
                GLES20.GlGenTextures(1, textures, 0);
                _textureId = textures[0];
                if (_textureId == 0)
                    throw new InvalidOperationException($"Native Android GL texture creation failed: err=0x{GLES20.GlGetError():X}");
                GLES20.GlBindTexture(GlTexture2D, _textureId);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
                _textureUsesNearest = true;
                _textureWidth = 0;
                _textureHeight = 0;
                _forceTextureUpload = true;
            }

            private static int CompileShader(int shaderType, string source)
            {
                int shader = GLES20.GlCreateShader(shaderType);
                if (shader == 0)
                    throw new InvalidOperationException($"Native Android GL shader creation failed: type=0x{shaderType:X} err=0x{GLES20.GlGetError():X}");
                GLES20.GlShaderSource(shader, source);
                GLES20.GlCompileShader(shader);

                int[] status = new int[1];
                GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, status, 0);
                if (status[0] != 0)
                    return shader;

                string info = GLES20.GlGetShaderInfoLog(shader) ?? "unknown";
                GLES20.GlDeleteShader(shader);
                string shaderName = shaderType == GlVertexShader ? "vertex" : shaderType == GlFragmentShader ? "fragment" : $"0x{shaderType:X}";
                throw new InvalidOperationException($"Native Android GL {shaderName} shader compile failed: {info} err=0x{GLES20.GlGetError():X}");
            }

            private static string SafeGetGlString(int name)
            {
                try
                {
                    return GLES20.GlGetString(name) ?? "unknown";
                }
                catch (Exception ex)
                {
                    return $"error:{ex.GetType().Name}";
                }
            }

            private static ShaderSources SelectShaderSources(string glVersion, string shadingLanguageVersion)
            {
                int shadingMajor = ParseLeadingVersionComponent(shadingLanguageVersion);
                int glMajor = ParseLeadingVersionComponent(glVersion);
                if (shadingMajor >= 3 || glMajor >= 3)
                    return new ShaderSources("ES300", VertexShaderSourceEs300, FragmentShaderSourceEs300);

                return new ShaderSources("ES100", VertexShaderSourceEs100, FragmentShaderSourceEs100);
            }

            private static int ParseLeadingVersionComponent(string value)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    if (!char.IsDigit(value[i]))
                        continue;

                    int start = i;
                    while (i < value.Length && char.IsDigit(value[i]))
                        i++;

                    if (int.TryParse(value[start..i], out int parsed))
                        return parsed;

                    break;
                }

                return 0;
            }

            private static string BuildShaderAttemptDetails(ShaderSources shaderSources, Exception ex)
            {
                return
                    $"Mode:{shaderSources.Label}\n" +
                    $"Error:{ex.Message}\n" +
                    $"Vertex Shader:\n{shaderSources.VertexSource}\n" +
                    $"Fragment Shader:\n{shaderSources.FragmentSource}";
            }

            private static string BuildInitDetails(Exception ex)
            {
                return $"GL Init Detail:{ex.Message}";
            }

            private void EnsureTextureStorage(int frameWidth, int frameHeight)
            {
                if (_textureWidth == frameWidth && _textureHeight == frameHeight)
                    return;

                GLES20.GlTexImage2D(GlTexture2D, 0, GlRgba, frameWidth, frameHeight, 0, GlRgba, GlUnsignedByte, null);
                _textureWidth = frameWidth;
                _textureHeight = frameHeight;
            }

            private void UpdateTextureFiltering(bool sharpPixelsEnabled)
            {
                bool useNearest = sharpPixelsEnabled;
                if (_textureUsesNearest == useNearest)
                    return;

                int filter = useNearest ? GlNearest : GlLinear;
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMinFilter, filter);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMagFilter, filter);
                _textureUsesNearest = useNearest;
            }

            private void DestroyGlResources()
            {
                if (_programId != 0)
                {
                    GLES20.GlDeleteProgram(_programId);
                    _programId = 0;
                }

                if (_textureId != 0)
                {
                    int[] textures = { _textureId };
                    GLES20.GlDeleteTextures(1, textures, 0);
                    _textureId = 0;
                }

                _positionLocation = -1;
                _uvLocation = -1;
                _samplerLocation = -1;
                _forceOpaqueLocation = -1;
                _scanlinesLocation = -1;
                _scanlineDarkenLocation = -1;
                _textureSizeLocation = -1;
                _interlaceBlendLocation = -1;
                _interlaceFieldParityLocation = -1;
                _textureWidth = 0;
                _textureHeight = 0;
                _forceTextureUpload = true;
                _directUploadBuffer?.Dispose();
                _directUploadBuffer = null;
                _directUploadCapacity = 0;
            }

            private void CleanupGl()
            {
                DestroyGlResources();

                if (_eglDisplay != null)
                {
                    EGL14.EglMakeCurrent(_eglDisplay, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                    if (_eglSurface != null)
                        EGL14.EglDestroySurface(_eglDisplay, _eglSurface);
                    if (_eglContext != null)
                        EGL14.EglDestroyContext(_eglDisplay, _eglContext);
                    EGL14.EglTerminate(_eglDisplay);
                    EGL14.EglReleaseThread();
                }

                _eglSurface = null;
                _eglContext = null;
                _eglConfig = null;
                _eglDisplay = null;
            }

            private static bool IsNoDisplay(EGLDisplay? display)
            {
                return display == null
                    || ReferenceEquals(display, EGL14.EglNoDisplay)
                    || display.Equals(EGL14.EglNoDisplay);
            }

            private static bool IsNoContext(EGLContext? context)
            {
                return context == null
                    || ReferenceEquals(context, EGL14.EglNoContext)
                    || context.Equals(EGL14.EglNoContext);
            }

            private static bool IsNoSurface(EGLSurface? surface)
            {
                return surface == null
                    || ReferenceEquals(surface, EGL14.EglNoSurface)
                    || surface.Equals(EGL14.EglNoSurface);
            }
        }
    }
}
