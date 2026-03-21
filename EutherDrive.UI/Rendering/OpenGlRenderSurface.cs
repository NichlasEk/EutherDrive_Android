using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;

namespace EutherDrive.Rendering;

public sealed class OpenGlRenderSurface : IGameRenderSurface, IDisposable
{
    private readonly OpenGlFrameControl _control = new();

    public Control View => _control;
    public int PixelWidth => _control.PixelWidth;
    public int PixelHeight => _control.PixelHeight;

    public bool EnsureSize(int width, int height) => _control.EnsureFrameSize(width, height);

    public FrameBlitMetrics Present(ReadOnlySpan<byte> source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf)
    {
        if (source.IsEmpty || width <= 0 || height <= 0 || srcStride <= 0)
            return FrameBlitMetrics.None;

        long start = measurePerf ? Stopwatch.GetTimestamp() : 0;
        _control.UpdateFrame(source, width, height, srcStride);
        long blitTicks = measurePerf ? Stopwatch.GetTimestamp() - start : 0;
        return new FrameBlitMetrics(0, blitTicks);
    }

    public FrameBlitMetrics PresentOwnedBuffer(byte[] source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf)
    {
        if (source.Length == 0 || width <= 0 || height <= 0 || srcStride <= 0)
            return FrameBlitMetrics.None;

        long start = measurePerf ? Stopwatch.GetTimestamp() : 0;
        _control.UpdateOwnedFrame(source, width, height, srcStride);
        long blitTicks = measurePerf ? Stopwatch.GetTimestamp() - start : 0;
        return new FrameBlitMetrics(0, blitTicks);
    }

    public bool ShouldFallbackToBitmap(out string reason) => _control.ShouldFallbackToBitmap(out reason);

    public void Reset() => _control.ResetFrame();

    public void Dispose() => _control.DisposeSurface();

    private sealed class OpenGlFrameControl : OpenGlControlBase
    {
        private const int GlArrayBuffer = 0x8892;
        private const int GlBgra = 0x80E1;
        private const int GlColorBufferBit = 0x00004000;
        private const int GlCullFace = 0x0B44;
        private const int GlFramebuffer = 0x8D40;
        private const int GlFloat = 0x1406;
        private const int GlFragmentShader = 0x8B30;
        private const int GlLinear = 0x2601;
        private const int GlNearest = 0x2600;
        private const int GlPixelUnpackBuffer = 0x88EC;
        private const int GlRgba = 0x1908;
        private const int GlScissorTest = 0x0C11;
        private const int GlStaticDraw = 0x88E4;
        private const int GlStreamDraw = 0x88E0;
        private const int GlTexture0 = 0x84C0;
        private const int GlTexture2D = 0x0DE1;
        private const int GlTextureMagFilter = 0x2800;
        private const int GlTextureMinFilter = 0x2801;
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

        private readonly object _frameSync = new();
        private byte[] _frameBytes = Array.Empty<byte>();
        private byte[] _uploadBytes = Array.Empty<byte>();
        private int _frameWidth;
        private int _frameHeight;
        private int _frameStride;
        private bool _frameDirty;
        private int _textureId;
        private int _textureWidth;
        private int _textureHeight;
        private readonly int[] _pixelUnpackBufferIds = new int[2];
        private int _pixelUnpackBufferIndex;
        private bool _usePixelUnpackBuffers;
        private int _programId;
        private int _vertexBufferId;
        private int _vertexArrayId;
        private int _positionLocation = -1;
        private int _uvLocation = -1;
        private int _presentCount;
        private int _renderCount;
        private readonly bool _traceEnabled = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_GL"), "1", StringComparison.Ordinal);
        private readonly bool _enablePixelUnpackBuffers = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_GL_ENABLE_PBO"), "1", StringComparison.Ordinal);
        private readonly bool _useSafeRgbaUpload = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_GL_SAFE_UPLOAD"), "1", StringComparison.Ordinal);
        private bool _initAttempted;
        private bool _initSucceeded;
        private bool _renderRequested;
        private TexSubImage2DInvoker? _texSubImage2D;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate void TexSubImage2DCdecl(
            int target,
            int level,
            int xoffset,
            int yoffset,
            int width,
            int height,
            int format,
            int type,
            IntPtr pixels);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate void TexSubImage2DStdCall(
            int target,
            int level,
            int xoffset,
            int yoffset,
            int width,
            int height,
            int format,
            int type,
            IntPtr pixels);

        public int PixelWidth => _frameWidth;
        public int PixelHeight => _frameHeight;

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
                if (_uploadBytes.Length != requiredBytes)
                    _uploadBytes = new byte[requiredBytes];
                return recreated;
            }
        }

        public void UpdateFrame(ReadOnlySpan<byte> source, int width, int height, int srcStride)
        {
            EnsureFrameSize(width, height);
            int dstStride = width * 4;
            int requiredBytes = dstStride * height;
            byte[] frameBytes;
            lock (_frameSync)
            {
                if (_frameBytes.Length != requiredBytes)
                    _frameBytes = new byte[requiredBytes];
                frameBytes = _frameBytes;
            }

            if (dstStride <= 0 || frameBytes.Length < requiredBytes)
                return;

            if (srcStride == dstStride)
            {
                source[..Math.Min(source.Length, requiredBytes)].CopyTo(frameBytes);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> srcRow = source.Slice(y * srcStride, dstStride);
                    Span<byte> dstRow = frameBytes.AsSpan(y * dstStride, dstStride);
                    srcRow.CopyTo(dstRow);
                }
            }

            lock (_frameSync)
            {
                _frameWidth = width;
                _frameHeight = height;
                _frameStride = dstStride;
                _frameDirty = true;
            }

            _presentCount++;
            QueueRenderRequest();
        }

        public void UpdateOwnedFrame(byte[] source, int width, int height, int srcStride)
        {
            EnsureFrameSize(width, height);
            int requiredBytes = checked(width * height * 4);
            if (srcStride != width * 4 || source.Length < requiredBytes)
            {
                UpdateFrame(source.AsSpan(0, Math.Min(source.Length, requiredBytes)), width, height, srcStride);
                return;
            }

            lock (_frameSync)
            {
                _frameBytes = source;
                _frameWidth = width;
                _frameHeight = height;
                _frameStride = srcStride;
                _frameDirty = true;
            }

            _presentCount++;
            QueueRenderRequest();
        }

        public bool ShouldFallbackToBitmap(out string reason)
        {
            if (_initAttempted && !_initSucceeded && _presentCount > 3)
            {
                reason = "OpenGL init failed before first frame.";
                return true;
            }

            if (_presentCount > 20 && _renderCount == 0)
            {
                reason = "OpenGL surface accepted frames but never rendered.";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public void ResetFrame()
        {
            lock (_frameSync)
            {
                _frameWidth = 0;
                _frameHeight = 0;
                _frameStride = 0;
                _frameBytes = Array.Empty<byte>();
                _uploadBytes = Array.Empty<byte>();
                _frameDirty = false;
            }
            QueueRenderRequest();
        }

        public void DisposeSurface()
        {
            ResetFrame();
        }

        protected override unsafe void OnOpenGlInit(GlInterface gl)
        {
            _initAttempted = true;
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
                #ifdef GL_ES
                precision mediump float;
                #endif
                varying vec2 vUv;
                uniform sampler2D uTex;
                void main()
                {
                    gl_FragColor = texture2D(uTex, vUv);
                }
                """;

            int vertexShader = gl.CreateShader(GlVertexShader);
            string? vertexError = gl.CompileShaderAndGetError(vertexShader, vertexShaderSource);
            if (!string.IsNullOrWhiteSpace(vertexError))
                throw new InvalidOperationException($"OpenGL vertex shader failed: {vertexError}");

            int fragmentShader = gl.CreateShader(GlFragmentShader);
            string? fragmentError = gl.CompileShaderAndGetError(fragmentShader, fragmentShaderSource);
            if (!string.IsNullOrWhiteSpace(fragmentError))
                throw new InvalidOperationException($"OpenGL fragment shader failed: {fragmentError}");

            _programId = gl.CreateProgram();
            gl.AttachShader(_programId, vertexShader);
            gl.AttachShader(_programId, fragmentShader);
            gl.BindAttribLocationString(_programId, 0, "aPos");
            gl.BindAttribLocationString(_programId, 1, "aUv");
            string? linkError = gl.LinkProgramAndGetError(_programId);
            gl.DeleteShader(vertexShader);
            gl.DeleteShader(fragmentShader);
            if (!string.IsNullOrWhiteSpace(linkError))
                throw new InvalidOperationException($"OpenGL program link failed: {linkError}");

            _positionLocation = gl.GetAttribLocationString(_programId, "aPos");
            _uvLocation = gl.GetAttribLocationString(_programId, "aUv");

            _vertexBufferId = gl.GenBuffer();
            gl.BindBuffer(GlArrayBuffer, _vertexBufferId);
            fixed (float* pVertices = s_quadVertices)
            {
                gl.BufferData(
                    GlArrayBuffer,
                    (IntPtr)(s_quadVertices.Length * sizeof(float)),
                    (IntPtr)pVertices,
                    GlStaticDraw);
            }

            if (gl.IsGenVertexArraysAvailable)
            {
                _vertexArrayId = gl.GenVertexArray();
                gl.BindVertexArray(_vertexArrayId);
            }

            _textureId = gl.GenTexture();
            gl.ActiveTexture(GlTexture0);
            gl.BindTexture(GlTexture2D, _textureId);
            gl.TexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
            gl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);
            _textureWidth = 0;
            _textureHeight = 0;
            _texSubImage2D = TexSubImage2DInvoker.Create(gl.GetProcAddress("glTexSubImage2D"));
            _usePixelUnpackBuffers = _enablePixelUnpackBuffers && !_useSafeRgbaUpload && _texSubImage2D != null && SupportsPixelUnpackBuffers(gl.Version);
            if (_usePixelUnpackBuffers)
            {
                _pixelUnpackBufferIds[0] = gl.GenBuffer();
                _pixelUnpackBufferIds[1] = gl.GenBuffer();
                _pixelUnpackBufferIndex = 0;
            }
            _initSucceeded = true;

            if (_traceEnabled)
                Console.WriteLine($"[OpenGL] Init ok renderer={gl.Renderer} version={gl.Version} safeUpload={_useSafeRgbaUpload} texSub={(_texSubImage2D != null)} pbo={_usePixelUnpackBuffers}");
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            if (_vertexArrayId != 0)
            {
                gl.DeleteVertexArray(_vertexArrayId);
                _vertexArrayId = 0;
            }

            if (_vertexBufferId != 0)
            {
                gl.DeleteBuffer(_vertexBufferId);
                _vertexBufferId = 0;
            }

            if (_textureId != 0)
            {
                gl.DeleteTexture(_textureId);
                _textureId = 0;
            }
            DeletePixelUnpackBuffers(gl);
            _textureWidth = 0;
            _textureHeight = 0;
            _texSubImage2D = null;
            _usePixelUnpackBuffers = false;

            if (_programId != 0)
            {
                gl.DeleteProgram(_programId);
                _programId = 0;
            }

            _positionLocation = -1;
            _uvLocation = -1;
            _initSucceeded = false;
        }

        protected override void OnOpenGlLost()
        {
            _vertexArrayId = 0;
            _vertexBufferId = 0;
            _textureId = 0;
            _textureWidth = 0;
            _textureHeight = 0;
            _pixelUnpackBufferIds[0] = 0;
            _pixelUnpackBufferIds[1] = 0;
            _pixelUnpackBufferIndex = 0;
            _usePixelUnpackBuffers = false;
            _programId = 0;
            _positionLocation = -1;
            _uvLocation = -1;
            _initSucceeded = false;
            _texSubImage2D = null;

            if (_traceEnabled)
                Console.WriteLine("[OpenGL] Context lost");
        }

        protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
        {
            _renderCount++;
            int viewportWidth = Math.Max(1, (int)Math.Round(Bounds.Width));
            int viewportHeight = Math.Max(1, (int)Math.Round(Bounds.Height));
            lock (_frameSync)
                _renderRequested = false;
            gl.BindFramebuffer(GlFramebuffer, fb);
            gl.Viewport(0, 0, viewportWidth, viewportHeight);
            gl.Disable(GlScissorTest);
            gl.Disable(GlCullFace);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(GlColorBufferBit);

            byte[] frameBytes;
            int frameWidth;
            int frameHeight;
            byte[] uploadBytes;
            bool frameDirty;
            lock (_frameSync)
            {
                frameBytes = _frameBytes;
                frameWidth = _frameWidth;
                frameHeight = _frameHeight;
                uploadBytes = _uploadBytes;
                frameDirty = _frameDirty;
                _frameDirty = false;
            }

            if (frameWidth <= 0 || frameHeight <= 0 || _textureId == 0 || _programId == 0 || _vertexBufferId == 0)
                return;

            gl.ActiveTexture(GlTexture0);
            gl.BindTexture(GlTexture2D, _textureId);
            if (_vertexArrayId != 0)
                gl.BindVertexArray(_vertexArrayId);

            if (frameDirty && frameBytes.Length > 0)
            {
                EnsureTextureStorage(gl, frameWidth, frameHeight);
                if (_useSafeRgbaUpload)
                {
                    ConvertBgraToRgba(frameBytes, uploadBytes);
                    fixed (byte* pUpload = uploadBytes)
                    {
                        UploadTexturePixels(gl, frameWidth, frameHeight, (IntPtr)pUpload, GlRgba);
                    }
                }
                else
                {
                    fixed (byte* pFrame = frameBytes)
                    {
                        UploadTexturePixels(gl, frameWidth, frameHeight, (IntPtr)pFrame, GlBgra);
                    }
                }
            }

            gl.UseProgram(_programId);
            gl.BindBuffer(GlArrayBuffer, _vertexBufferId);

            if (_positionLocation >= 0)
            {
                gl.EnableVertexAttribArray(_positionLocation);
                gl.VertexAttribPointer(_positionLocation, 2, GlFloat, 0, 4 * sizeof(float), IntPtr.Zero);
            }

            if (_uvLocation >= 0)
            {
                gl.EnableVertexAttribArray(_uvLocation);
                gl.VertexAttribPointer(_uvLocation, 2, GlFloat, 0, 4 * sizeof(float), (IntPtr)(2 * sizeof(float)));
            }

            gl.DrawArrays(GlTriangles, 0, (IntPtr)6);

            if (_traceEnabled && (_renderCount <= 5 || (_renderCount % 60) == 0))
                Console.WriteLine($"[OpenGL] Render frame#{_renderCount} fb={fb} tex={_textureId} size={frameWidth}x{frameHeight} viewport={viewportWidth}x{viewportHeight} dirty={frameDirty}");
        }

        private void QueueRenderRequest()
        {
            bool shouldRequest = false;
            lock (_frameSync)
            {
                if (!_renderRequested)
                {
                    _renderRequested = true;
                    shouldRequest = true;
                }
            }

            if (shouldRequest)
                RequestNextFrameRendering();
        }

        private static void ConvertBgraToRgba(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            int length = Math.Min(source.Length, destination.Length);
            for (int i = 0; i + 3 < length; i += 4)
            {
                destination[i + 0] = source[i + 2];
                destination[i + 1] = source[i + 1];
                destination[i + 2] = source[i + 0];
                destination[i + 3] = source[i + 3];
            }
        }

        private void EnsureTextureStorage(GlInterface gl, int frameWidth, int frameHeight)
        {
            if (_textureWidth == frameWidth && _textureHeight == frameHeight)
                return;

            gl.TexImage2D(
                GlTexture2D,
                0,
                GlRgba,
                frameWidth,
                frameHeight,
                0,
                _useSafeRgbaUpload ? GlRgba : GlBgra,
                GlUnsignedByte,
                IntPtr.Zero);

            _textureWidth = frameWidth;
            _textureHeight = frameHeight;

            if (_traceEnabled)
                Console.WriteLine($"[OpenGL] Alloc texture {_textureWidth}x{_textureHeight}");
        }

        private void UploadTexturePixels(GlInterface gl, int frameWidth, int frameHeight, IntPtr pixels, int format)
        {
            int byteCount = checked(frameWidth * frameHeight * 4);
            if (_usePixelUnpackBuffers && _pixelUnpackBufferIds[0] != 0 && byteCount > 0 && _texSubImage2D != null)
            {
                int pboId = _pixelUnpackBufferIds[_pixelUnpackBufferIndex];
                _pixelUnpackBufferIndex = (_pixelUnpackBufferIndex + 1) % _pixelUnpackBufferIds.Length;
                gl.BindBuffer(GlPixelUnpackBuffer, pboId);
                gl.BufferData(GlPixelUnpackBuffer, (IntPtr)byteCount, pixels, GlStreamDraw);
                _texSubImage2D.Invoke(GlTexture2D, 0, 0, 0, frameWidth, frameHeight, format, GlUnsignedByte, IntPtr.Zero);
                gl.BindBuffer(GlPixelUnpackBuffer, 0);
                return;
            }

            if (_texSubImage2D != null)
            {
                _texSubImage2D.Invoke(GlTexture2D, 0, 0, 0, frameWidth, frameHeight, format, GlUnsignedByte, pixels);
                return;
            }

            gl.TexImage2D(
                GlTexture2D,
                0,
                GlRgba,
                frameWidth,
                frameHeight,
                0,
                format,
                GlUnsignedByte,
                pixels);
        }

        private void DeletePixelUnpackBuffers(GlInterface gl)
        {
            for (int i = 0; i < _pixelUnpackBufferIds.Length; i++)
            {
                if (_pixelUnpackBufferIds[i] != 0)
                {
                    gl.DeleteBuffer(_pixelUnpackBufferIds[i]);
                    _pixelUnpackBufferIds[i] = 0;
                }
            }
            _pixelUnpackBufferIndex = 0;
        }

        private static bool SupportsPixelUnpackBuffers(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return false;

            if (version.Contains("OpenGL ES 2", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private sealed class TexSubImage2DInvoker
        {
            private readonly TexSubImage2DCdecl? _cdecl;
            private readonly TexSubImage2DStdCall? _stdcall;

            private TexSubImage2DInvoker(TexSubImage2DCdecl callback)
            {
                _cdecl = callback;
            }

            private TexSubImage2DInvoker(TexSubImage2DStdCall callback)
            {
                _stdcall = callback;
            }

            public static TexSubImage2DInvoker? Create(IntPtr proc)
            {
                if (proc == IntPtr.Zero)
                    return null;

                if (OperatingSystem.IsWindows())
                {
                    return new TexSubImage2DInvoker(Marshal.GetDelegateForFunctionPointer<TexSubImage2DStdCall>(proc));
                }

                return new TexSubImage2DInvoker(Marshal.GetDelegateForFunctionPointer<TexSubImage2DCdecl>(proc));
            }

            public void Invoke(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, IntPtr pixels)
            {
                if (_stdcall != null)
                {
                    _stdcall(target, level, xoffset, yoffset, width, height, format, type, pixels);
                    return;
                }

                _cdecl!(target, level, xoffset, yoffset, width, height, format, type, pixels);
            }
        }
    }
}
