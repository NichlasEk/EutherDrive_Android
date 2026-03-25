using System;
using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Opengl;
using Android.Views;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Java.Nio;
using Javax.Microedition.Khronos.Opengles;

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

    public bool ShouldFallbackToBitmap(out string reason) => _host.ShouldFallbackToBitmap(out reason);

    public bool TryGetDebugSummary(out string summary) => _host.TryGetDebugSummary(out summary);

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
        private int _presentCount;
        private int _renderCount;
        private int _uploadCount;
        private long _renderTicksTotal;
        private long _uploadTicksTotal;
        private long _lastRenderTicks;
        private long _lastUploadTicks;
        private bool _initAttempted;
        private bool _initSucceeded;
        private string _fallbackReason = string.Empty;
        private AndroidGlSurfaceView? _nativeView;

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
                _presentCount++;
                _lastUploadTicks = Stopwatch.GetTimestamp() - copyStart;
            }

            _nativeView?.RequestRenderSafe();
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
            }

            _nativeView?.RequestRenderSafe();
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

        public bool TryGetDebugSummary(out string summary)
        {
            lock (_frameSync)
            {
                double avgRenderMs = _renderCount > 0 ? (_renderTicksTotal * 1000.0 / Stopwatch.Frequency) / _renderCount : 0;
                double avgUploadMs = _uploadCount > 0 ? (_uploadTicksTotal * 1000.0 / Stopwatch.Frequency) / _uploadCount : 0;
                double lastRenderMs = _lastRenderTicks > 0 ? _lastRenderTicks * 1000.0 / Stopwatch.Frequency : 0;
                double lastUploadMs = _lastUploadTicks > 0 ? _lastUploadTicks * 1000.0 / Stopwatch.Frequency : 0;
                summary = $"GL Present:{_presentCount} Render:{_renderCount} Upload:{_uploadCount} Pending:{(_renderRequested ? 1 : 0)} R:{avgRenderMs:0.0}/{lastRenderMs:0.0}ms U:{avgUploadMs:0.0}/{lastUploadMs:0.0}ms";
                return true;
            }
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            try
            {
                Context context = EutherDrive.Android.MainActivity.Current ?? Application.Context
                    ?? throw new InvalidOperationException("Android context is unavailable.");
                var view = new AndroidGlSurfaceView(context, this);
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

                Context context = EutherDrive.Android.MainActivity.Current ?? Application.Context
                    ?? throw new InvalidOperationException("Android context is unavailable.");
                return new Avalonia.Android.AndroidViewControlHandle(new View(context));
            }
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (control is Avalonia.Android.AndroidViewControlHandle androidHandle)
            {
                if (androidHandle.View is AndroidGlSurfaceView view)
                {
                    if (ReferenceEquals(_nativeView, view))
                        _nativeView = null;
                    view.ReleaseSurface();
                }

                androidHandle.Destroy();
            }
        }

        private void BeginRender(out byte[] frameBytes, out int frameWidth, out int frameHeight, out bool frameDirty, out bool sharpPixelsEnabled, out bool forceOpaque)
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
            }
        }

        private void NoteInitFailure(string reason)
        {
            lock (_frameSync)
            {
                _initAttempted = true;
                _initSucceeded = false;
                if (string.IsNullOrEmpty(_fallbackReason))
                    _fallbackReason = reason;
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

        private sealed class AndroidGlSurfaceView : GLSurfaceView, GLSurfaceView.IRenderer
        {
            private const int GlArrayBuffer = 0x8892;
            private const int GlBgra = 0x80E1;
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
            private const int GlVertexShader = 0x8B31;

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
            private int _programId;
            private int _textureId;
            private int _positionLocation = -1;
            private int _uvLocation = -1;
            private int _samplerLocation = -1;
            private int _swapRedBlueLocation = -1;
            private int _forceOpaqueLocation = -1;
            private int _textureWidth;
            private int _textureHeight;
            private bool _textureUsesNearest = true;
            private volatile bool _released;

            public AndroidGlSurfaceView(Context context, AndroidNativeGlHost host) : base(context)
            {
                _host = host;
                SetEGLContextClientVersion(2);
                SetEGLConfigChooser(8, 8, 8, 8, 0, 0);
                PreserveEGLContextOnPause = true;
                SetRenderer(this);
                // Continuous rendering keeps Android's native GL thread paced by the display
                // instead of relying on dirty-invalidation timing through the UI stack.
                RenderMode = Rendermode.Continuously;
                Holder?.SetFormat(global::Android.Graphics.Format.Rgba8888);

                ByteBuffer vertexByteBuffer = ByteBuffer.AllocateDirect(s_quadVertices.Length * sizeof(float));
                vertexByteBuffer.Order(ByteOrder.NativeOrder()!);
                _vertexBuffer = vertexByteBuffer.AsFloatBuffer();
                _vertexBuffer.Put(s_quadVertices);
                _vertexBuffer.Position(0);
            }

            public void ReleaseSurface()
            {
                _released = true;
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

            public void OnDrawFrame(IGL10? gl)
            {
                long renderStart = Stopwatch.GetTimestamp();
                long uploadTicks = 0;

                GLES20.GlClearColor(0f, 0f, 0f, 1f);
                GLES20.GlClear(GlColorBufferBit);

                _host.BeginRender(out byte[] frameBytes, out int frameWidth, out int frameHeight, out bool frameDirty, out bool sharpPixelsEnabled, out bool forceOpaque);
                if (_programId == 0 || _textureId == 0 || frameWidth <= 0 || frameHeight <= 0)
                {
                    _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, 0, uploaded: false);
                    return;
                }

                GLES20.GlActiveTexture(GlTexture0);
                GLES20.GlBindTexture(GlTexture2D, _textureId);
                UpdateTextureFiltering(sharpPixelsEnabled);

                bool uploaded = false;
                if (frameDirty && frameBytes.Length >= frameWidth * frameHeight * 4)
                {
                    EnsureTextureStorage(frameWidth, frameHeight);
                    long uploadStart = Stopwatch.GetTimestamp();
                    using ByteBuffer pixels = ByteBuffer.Wrap(frameBytes);
                    pixels.Position(0);
                    GLES20.GlTexSubImage2D(GlTexture2D, 0, 0, 0, frameWidth, frameHeight, GlRgba, GlUnsignedByte, pixels);
                    uploadTicks = Stopwatch.GetTimestamp() - uploadStart;
                    uploaded = true;
                }

                GLES20.GlUseProgram(_programId);
                GLES20.GlUniform1i(_samplerLocation, 0);
                GLES20.GlUniform1f(_swapRedBlueLocation, 1.0f);
                GLES20.GlUniform1f(_forceOpaqueLocation, forceOpaque ? 1.0f : 0.0f);

                _vertexBuffer.Position(0);
                GLES20.GlEnableVertexAttribArray(_positionLocation);
                GLES20.GlVertexAttribPointer(_positionLocation, 2, GlFloat, false, 4 * sizeof(float), _vertexBuffer);
                _vertexBuffer.Position(2);
                GLES20.GlEnableVertexAttribArray(_uvLocation);
                GLES20.GlVertexAttribPointer(_uvLocation, 2, GlFloat, false, 4 * sizeof(float), _vertexBuffer);
                GLES20.GlDrawArrays(GlTriangles, 0, 6);

                _host.NoteRender(Stopwatch.GetTimestamp() - renderStart, uploadTicks, uploaded);
            }

            public void OnSurfaceChanged(IGL10? gl, int width, int height)
            {
                GLES20.GlViewport(0, 0, width, height);
            }

            public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
            {
                try
                {
                    CreateProgramAndTexture();
                    _host.NoteInitSuccess();
                }
                catch (Exception ex)
                {
                    DestroyGlResources();
                    _host.NoteInitFailure(ex.Message);
                }
            }

            private void CreateProgramAndTexture()
            {
                const string vertexShaderSource = """
                    attribute vec2 aPos;
                    attribute vec2 aUv;
                    varying vec2 vUv;
                    void main()
                    {
                        vUv = aUv;
                        gl_Position = vec4(aPos, 0.0, 1.0);
                    }
                    """;

                const string fragmentShaderSource = """
                    precision mediump float;
                    varying vec2 vUv;
                    uniform sampler2D uTex;
                    uniform float uSwapRedBlue;
                    uniform float uForceOpaque;

                    void main()
                    {
                        vec4 color = texture2D(uTex, vUv);
                        if (uSwapRedBlue > 0.5)
                            color = color.bgra;
                        if (uForceOpaque > 0.5)
                            color.a = 1.0;
                        gl_FragColor = color;
                    }
                    """;

                DestroyGlResources();

                int vertexShader = CompileShader(GlVertexShader, vertexShaderSource);
                int fragmentShader = CompileShader(GlFragmentShader, fragmentShaderSource);

                _programId = GLES20.GlCreateProgram();
                GLES20.GlAttachShader(_programId, vertexShader);
                GLES20.GlAttachShader(_programId, fragmentShader);
                GLES20.GlBindAttribLocation(_programId, 0, "aPos");
                GLES20.GlBindAttribLocation(_programId, 1, "aUv");
                GLES20.GlLinkProgram(_programId);

                int[] status = new int[1];
                GLES20.GlGetProgramiv(_programId, GLES20.GlLinkStatus, IntBuffer.Wrap(status));
                if (status[0] == 0)
                {
                    string info = GLES20.GlGetProgramInfoLog(_programId) ?? "unknown";
                    throw new InvalidOperationException($"Native Android GL link failed: {info}");
                }

                GLES20.GlDeleteShader(vertexShader);
                GLES20.GlDeleteShader(fragmentShader);

                _positionLocation = GLES20.GlGetAttribLocation(_programId, "aPos");
                _uvLocation = GLES20.GlGetAttribLocation(_programId, "aUv");
                _samplerLocation = GLES20.GlGetUniformLocation(_programId, "uTex");
                _swapRedBlueLocation = GLES20.GlGetUniformLocation(_programId, "uSwapRedBlue");
                _forceOpaqueLocation = GLES20.GlGetUniformLocation(_programId, "uForceOpaque");

                int[] textures = new int[1];
                GLES20.GlGenTextures(1, IntBuffer.Wrap(textures));
                _textureId = textures[0];
                GLES20.GlBindTexture(GlTexture2D, _textureId);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
                GLES20.GlTexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
                _textureUsesNearest = true;
                _textureWidth = 0;
                _textureHeight = 0;
            }

            private static int CompileShader(int shaderType, string source)
            {
                int shader = GLES20.GlCreateShader(shaderType);
                GLES20.GlShaderSource(shader, source);
                GLES20.GlCompileShader(shader);

                int[] status = new int[1];
                GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, IntBuffer.Wrap(status));
                if (status[0] != 0)
                    return shader;

                string info = GLES20.GlGetShaderInfoLog(shader) ?? "unknown";
                GLES20.GlDeleteShader(shader);
                throw new InvalidOperationException($"Native Android GL shader compile failed: {info}");
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
                    GLES20.GlDeleteTextures(1, IntBuffer.Wrap(textures));
                    _textureId = 0;
                }

                _positionLocation = -1;
                _uvLocation = -1;
                _samplerLocation = -1;
                _swapRedBlueLocation = -1;
                _forceOpaqueLocation = -1;
                _textureWidth = 0;
                _textureHeight = 0;
            }
        }
    }
}
