using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace EutherDrive.Rendering;

public sealed class OpenGlRenderSurface : IAcceleratedRenderSurface, IDisposable
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
        _control.UpdateFrame(source, width, height, srcStride, options);
        long blitTicks = measurePerf ? Stopwatch.GetTimestamp() - start : 0;
        return new FrameBlitMetrics(0, blitTicks);
    }

    public FrameBlitMetrics PresentOwnedBuffer(byte[] source, int width, int height, int srcStride, in FrameBlitOptions options, bool measurePerf)
    {
        if (source.Length == 0 || width <= 0 || height <= 0 || srcStride <= 0)
            return FrameBlitMetrics.None;

        long start = measurePerf ? Stopwatch.GetTimestamp() : 0;
        _control.UpdateOwnedFrame(source, width, height, srcStride, options);
        long blitTicks = measurePerf ? Stopwatch.GetTimestamp() - start : 0;
        return new FrameBlitMetrics(0, blitTicks);
    }

    public bool ShouldFallbackToBitmap(out string reason) => _control.ShouldFallbackToBitmap(out reason);

    public bool TryGetDebugSummary(out string summary) => _control.TryGetDebugSummary(out summary);

    public void SetInterlaceBlend(bool enabled, int fieldParity)
        => _control.SetInterlaceBlend(enabled, fieldParity);

    public void Reset() => _control.ResetFrame();

    public void Dispose() => _control.DisposeSurface();

    private sealed class OpenGlFrameControl : OpenGlControlBase
    {
        private const int GlArrayBuffer = 0x8892;
        private const int GlBgra = 0x80E1;
        private const int GlColorBufferBit = 0x00004000;
        private const int GlClampToEdge = 0x812F;
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
        private const int GlTextureWrapS = 0x2802;
        private const int GlTextureWrapT = 0x2803;
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
        private int _samplerLocation = -1;
        private int _textureSizeLocation = -1;
        private int _forceOpaqueLocation = -1;
        private int _advancedFilterLocation = -1;
        private int _advancedFilterProfileLocation = -1;
        private int _scanlinesLocation = -1;
        private int _scanlineDarkenLocation = -1;
        private int _swapRedBlueLocation = -1;
        private int _interlaceBlendLocation = -1;
        private int _interlaceFieldParityLocation = -1;
        private int _presentCount;
        private int _renderCount;
        private int _uploadCount;
        private long _renderTicksTotal;
        private long _uploadTicksTotal;
        private long _lastRenderTicks;
        private long _lastUploadTicks;
        private readonly bool _traceEnabled = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_GL"), "1", StringComparison.Ordinal);
        private readonly bool _enablePixelUnpackBuffers = GetPixelUnpackBufferDefault();
        private readonly bool _useSafeRgbaUpload = GetSafeRgbaUploadDefault();
        private bool _initAttempted;
        private bool _initSucceeded;
        private bool _renderRequested;
        private bool _sharpPixelsEnabled = true;
        private bool _forceOpaque;
        private bool _applyScanlines;
        private bool _applyAdvancedPixelFilter;
        private AdvancedPixelFilterProfile _advancedFilterProfile;
        private bool _interlaceBlendEnabled;
        private int _interlaceBlendFieldParity = -1;
        private float _scanlineDarken = 1.0f;
        private bool _textureUsesNearest = true;
        private TexSubImage2DInvoker? _texSubImage2D;
        private GetUniformLocationInvoker? _getUniformLocation;
        private Uniform1iInvoker? _uniform1i;
        private Uniform1fInvoker? _uniform1f;
        private Uniform2fInvoker? _uniform2f;

        public OpenGlFrameControl()
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }

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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetUniformLocationCdecl(int program, IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetUniformLocationStdCall(int program, IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Uniform1iCdecl(int location, int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Uniform1iStdCall(int location, int value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Uniform1fCdecl(int location, float value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Uniform1fStdCall(int location, float value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Uniform2fCdecl(int location, float value0, float value1);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Uniform2fStdCall(int location, float value0, float value1);

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
                if (_frameBytes.Length != requiredBytes)
                    _frameBytes = new byte[requiredBytes];
                if (_uploadBytes.Length != requiredBytes)
                    _uploadBytes = new byte[requiredBytes];
                return recreated;
            }
        }

        public void UpdateFrame(ReadOnlySpan<byte> source, int width, int height, int srcStride, in FrameBlitOptions options)
        {
            EnsureFrameSize(width, height);
            int dstStride = width * 4;
            int requiredBytes = dstStride * height;
            byte[] stagingBytes;
            lock (_frameSync)
            {
                if (_uploadBytes.Length != requiredBytes)
                    _uploadBytes = new byte[requiredBytes];
                stagingBytes = _uploadBytes;
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
                (_frameBytes, _uploadBytes) = (_uploadBytes, _frameBytes);
                _frameWidth = width;
                _frameHeight = height;
                _frameStride = dstStride;
                _frameDirty = true;
                ApplyOptionsLocked(options);
            }

            _presentCount++;
            QueueRenderRequest();
        }

        public void UpdateOwnedFrame(byte[] source, int width, int height, int srcStride, in FrameBlitOptions options)
        {
            EnsureFrameSize(width, height);
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
                ApplyOptionsLocked(options);
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

        public bool TryGetDebugSummary(out string summary)
        {
            lock (_frameSync)
            {
                double avgRenderMs = _renderCount > 0 ? (_renderTicksTotal * 1000.0 / Stopwatch.Frequency) / _renderCount : 0;
                double avgUploadMs = _uploadCount > 0 ? (_uploadTicksTotal * 1000.0 / Stopwatch.Frequency) / _uploadCount : 0;
                double lastRenderMs = _lastRenderTicks > 0 ? _lastRenderTicks * 1000.0 / Stopwatch.Frequency : 0;
                double lastUploadMs = _lastUploadTicks > 0 ? _lastUploadTicks * 1000.0 / Stopwatch.Frequency : 0;
                summary = $"GL Present:{_presentCount} Render:{_renderCount} Upload:{_uploadCount} Pending:{(_renderRequested ? 1 : 0)} IL:{(_interlaceBlendEnabled ? 1 : 0)}/{_interlaceBlendFieldParity} R:{avgRenderMs:0.0}/{lastRenderMs:0.0}ms U:{avgUploadMs:0.0}/{lastUploadMs:0.0}ms";
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
                _frameWidth = 0;
                _frameHeight = 0;
                _frameStride = 0;
                _frameBytes = Array.Empty<byte>();
                _uploadBytes = Array.Empty<byte>();
                _frameDirty = false;
                _interlaceBlendEnabled = false;
                _interlaceBlendFieldParity = -1;
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
                uniform vec2 uTextureSize;
                uniform float uForceOpaque;
                uniform float uApplyAdvancedFilter;
                uniform float uAdvancedFilterProfile;
                uniform float uApplyScanlines;
                uniform float uScanlineDarken;
                uniform float uSwapRedBlue;
                uniform float uInterlaceBlend;
                uniform float uInterlaceFieldParity;

                float luma(vec3 color)
                {
                    return dot(color, vec3(0.299, 0.587, 0.114));
                }

                vec2 clampTexel(vec2 texelPos)
                {
                    return clamp(texelPos, vec2(0.0, 0.0), uTextureSize - vec2(1.0, 1.0));
                }

                vec4 sampleTexel(vec2 texelPos)
                {
                    vec2 uv = (clampTexel(texelPos) + vec2(0.5, 0.5)) / uTextureSize;
                    vec4 color = texture2D(uTex, uv);
                    return uSwapRedBlue > 0.5 ? color.bgra : color;
                }

                vec3 sharpen(vec2 texelPos)
                {
                    vec4 c4 = sampleTexel(texelPos);
                    vec3 center = c4.rgb;
                    vec3 left = sampleTexel(texelPos + vec2(-1.0, 0.0)).rgb;
                    vec3 right = sampleTexel(texelPos + vec2(1.0, 0.0)).rgb;
                    vec3 up = sampleTexel(texelPos + vec2(0.0, -1.0)).rgb;
                    vec3 down = sampleTexel(texelPos + vec2(0.0, 1.0)).rgb;

                    float cY = luma(center);
                    float edge = abs(cY - luma(left))
                        + abs(cY - luma(right))
                        + abs(cY - luma(up))
                        + abs(cY - luma(down));
                    float gain = edge > (84.0 / 255.0)
                        ? (208.0 / 256.0)
                        : edge > (40.0 / 255.0)
                            ? (152.0 / 256.0)
                            : (96.0 / 256.0);

                    vec3 blur = ((center * 2.0) + left + right + up + down) / 6.0;
                    vec3 detail = center - blur;
                    vec3 sharpened = center + (detail * gain);

                    vec3 minN = min(center, min(min(left, right), min(up, down)));
                    vec3 maxN = max(center, max(max(left, right), max(up, down)));
                    vec3 low = max(vec3(0.0), minN - vec3(10.0 / 255.0));
                    vec3 high = min(vec3(1.0), maxN + vec3(10.0 / 255.0));
                    return clamp(sharpened, low, high);
                }

                float classifyTextEdge(float cY, float lY, float rY, float uY, float dY)
                {
                    float minY = min(cY, min(min(lY, rY), min(uY, dY)));
                    float maxY = max(cY, max(max(lY, rY), max(uY, dY)));
                    if ((maxY - minY) < (72.0 / 255.0))
                        return 0.0;

                    bool nearBrightExtreme = (maxY - cY) <= (28.0 / 255.0);
                    bool nearDarkExtreme = (cY - minY) <= (28.0 / 255.0);
                    if (!nearBrightExtreme && !nearDarkExtreme)
                        return 0.0;

                    float supportCount = 0.0;
                    float oppositeCount = 0.0;
                    if (nearBrightExtreme)
                    {
                        supportCount += (maxY - lY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        supportCount += (maxY - rY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        supportCount += (maxY - uY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        supportCount += (maxY - dY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (lY - minY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (rY - minY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (uY - minY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (dY - minY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                    }
                    else
                    {
                        supportCount += (lY - minY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        supportCount += (rY - minY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        supportCount += (uY - minY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        supportCount += (dY - minY) <= (28.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (maxY - lY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (maxY - rY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (maxY - uY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                        oppositeCount += (maxY - dY) <= (40.0 / 255.0) ? 1.0 : 0.0;
                    }

                    if (supportCount < 1.0 || oppositeCount < 1.0)
                        return 0.0;

                    return nearBrightExtreme ? 1.0 : -1.0;
                }

                vec3 sharpenInterlacedTextSafe(vec2 texelPos)
                {
                    vec4 c4 = sampleTexel(texelPos);
                    vec3 center = c4.rgb;
                    vec3 left = sampleTexel(texelPos + vec2(-1.0, 0.0)).rgb;
                    vec3 right = sampleTexel(texelPos + vec2(1.0, 0.0)).rgb;
                    vec3 up = sampleTexel(texelPos + vec2(0.0, -1.0)).rgb;
                    vec3 down = sampleTexel(texelPos + vec2(0.0, 1.0)).rgb;

                    float cY = luma(center);
                    float lY = luma(left);
                    float rY = luma(right);
                    float uY = luma(up);
                    float dY = luma(down);
                    vec3 minN = min(center, min(min(left, right), min(up, down)));
                    vec3 maxN = max(center, max(max(left, right), max(up, down)));
                    float textEdgePolarity = classifyTextEdge(cY, lY, rY, uY, dY);
                    if (abs(textEdgePolarity) > 0.5)
                    {
                        vec3 target = textEdgePolarity > 0.0 ? maxN + vec3(4.0 / 255.0) : max(vec3(0.0), minN - vec3(4.0 / 255.0));
                        return center + ((target - center) * (176.0 / 256.0));
                    }

                    float edge = abs(cY - lY)
                        + abs(cY - rY)
                        + abs(cY - uY)
                        + abs(cY - dY);
                    float gain = edge > (84.0 / 255.0)
                        ? (160.0 / 256.0)
                        : edge > (40.0 / 255.0)
                            ? (120.0 / 256.0)
                            : (64.0 / 256.0);

                    vec3 blur = ((center * 3.0) + ((left + right) * 2.0) + up + down) / 9.0;
                    vec3 detail = center - blur;
                    vec3 sharpened = center + (detail * gain);

                    vec3 low = max(vec3(0.0), minN - vec3(4.0 / 255.0));
                    vec3 high = min(vec3(1.0), maxN + vec3(4.0 / 255.0));
                    return clamp(sharpened, low, high);
                }

                vec4 sampleCurrentField(vec2 texelPos)
                {
                    float fieldParity = uInterlaceFieldParity;
                    float maxFieldIndex = floor((uTextureSize.y - 1.0 - fieldParity) * 0.5);
                    if (maxFieldIndex <= 0.0)
                        return sampleTexel(vec2(texelPos.x, fieldParity));

                    float fieldPos = clamp((texelPos.y - fieldParity) * 0.5, 0.0, maxFieldIndex);
                    float fieldIndex0 = floor(fieldPos);
                    float fieldIndex1 = min(fieldIndex0 + 1.0, maxFieldIndex);
                    float y0 = fieldParity + (fieldIndex0 * 2.0);
                    float y1 = fieldParity + (fieldIndex1 * 2.0);
                    float t = fieldPos - fieldIndex0;
                    return mix(
                        sampleTexel(vec2(texelPos.x, y0)),
                        sampleTexel(vec2(texelPos.x, y1)),
                        t);
                }

                vec4 softenInterlace(vec2 texelPos, vec4 color)
                {
                    if (uInterlaceBlend <= 0.5 || uTextureSize.y < 2.0)
                        return color;

                    vec4 bob = sampleCurrentField(texelPos);
                    return vec4(bob.rgb, color.a);
                }

                void main()
                {
                    vec2 texelPos = floor(vUv * uTextureSize);
                    vec4 color = sampleTexel(texelPos);

                    if (uApplyAdvancedFilter > 0.5)
                    {
                        vec3 filtered = uAdvancedFilterProfile > 0.5
                            ? sharpenInterlacedTextSafe(texelPos)
                            : sharpen(texelPos);
                        color = vec4(filtered, sampleTexel(texelPos).a);
                    }

                    color = softenInterlace(texelPos, color);

                    if (uApplyScanlines > 0.5 && mod(texelPos.y, 2.0) > 0.5)
                        color.rgb *= uScanlineDarken;

                    if (uForceOpaque > 0.5)
                        color.a = 1.0;

                    gl_FragColor = color;
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
            _getUniformLocation = GetUniformLocationInvoker.Create(gl.GetProcAddress("glGetUniformLocation"));
            _uniform1i = Uniform1iInvoker.Create(gl.GetProcAddress("glUniform1i"));
            _uniform1f = Uniform1fInvoker.Create(gl.GetProcAddress("glUniform1f"));
            _uniform2f = Uniform2fInvoker.Create(gl.GetProcAddress("glUniform2f"));
            _samplerLocation = GetUniformLocation("uTex");
            _textureSizeLocation = GetUniformLocation("uTextureSize");
            _forceOpaqueLocation = GetUniformLocation("uForceOpaque");
            _advancedFilterLocation = GetUniformLocation("uApplyAdvancedFilter");
            _advancedFilterProfileLocation = GetUniformLocation("uAdvancedFilterProfile");
            _scanlinesLocation = GetUniformLocation("uApplyScanlines");
            _scanlineDarkenLocation = GetUniformLocation("uScanlineDarken");
            _swapRedBlueLocation = GetUniformLocation("uSwapRedBlue");
            _interlaceBlendLocation = GetUniformLocation("uInterlaceBlend");
            _interlaceFieldParityLocation = GetUniformLocation("uInterlaceFieldParity");

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
            gl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
            gl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
            _textureWidth = 0;
            _textureHeight = 0;
            _texSubImage2D = TexSubImage2DInvoker.Create(gl.GetProcAddress("glTexSubImage2D"));
            _usePixelUnpackBuffers = _enablePixelUnpackBuffers && _texSubImage2D != null && SupportsPixelUnpackBuffers(gl.Version);
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
            _getUniformLocation = null;
            _uniform1i = null;
            _uniform1f = null;
            _uniform2f = null;

            if (_programId != 0)
            {
                gl.DeleteProgram(_programId);
                _programId = 0;
            }

            _positionLocation = -1;
            _uvLocation = -1;
            _samplerLocation = -1;
            _textureSizeLocation = -1;
            _forceOpaqueLocation = -1;
            _advancedFilterLocation = -1;
            _advancedFilterProfileLocation = -1;
            _scanlinesLocation = -1;
            _scanlineDarkenLocation = -1;
            _swapRedBlueLocation = -1;
            _interlaceBlendLocation = -1;
            _interlaceFieldParityLocation = -1;
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
            _samplerLocation = -1;
            _textureSizeLocation = -1;
            _forceOpaqueLocation = -1;
            _advancedFilterLocation = -1;
            _advancedFilterProfileLocation = -1;
            _scanlinesLocation = -1;
            _scanlineDarkenLocation = -1;
            _swapRedBlueLocation = -1;
            _interlaceBlendLocation = -1;
            _interlaceFieldParityLocation = -1;
            _initSucceeded = false;
            _texSubImage2D = null;
            _getUniformLocation = null;
            _uniform1i = null;
            _uniform1f = null;
            _uniform2f = null;

            if (_traceEnabled)
                Console.WriteLine("[OpenGL] Context lost");
        }

        protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
        {
            long renderStart = Stopwatch.GetTimestamp();
            long uploadTicks = 0;
            _renderCount++;
            int viewportWidth = Math.Max(1, (int)Math.Round(Bounds.Width));
            int viewportHeight = Math.Max(1, (int)Math.Round(Bounds.Height));
            if (VisualRoot is TopLevel topLevel)
            {
                double scale = topLevel.RenderScaling;
                if (scale > 0)
                {
                    viewportWidth = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
                    viewportHeight = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
                }
            }
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
            bool frameDirty;
            bool sharpPixelsEnabled;
            bool forceOpaque;
            bool applyScanlines;
            bool applyAdvancedPixelFilter;
            AdvancedPixelFilterProfile advancedFilterProfile;
            bool interlaceBlendEnabled;
            int interlaceBlendFieldParity;
            float scanlineDarken;
            lock (_frameSync)
            {
                frameBytes = _frameBytes;
                frameWidth = _frameWidth;
                frameHeight = _frameHeight;
                frameDirty = _frameDirty;
                sharpPixelsEnabled = _sharpPixelsEnabled;
                forceOpaque = _forceOpaque;
                applyScanlines = _applyScanlines;
                applyAdvancedPixelFilter = _applyAdvancedPixelFilter;
                advancedFilterProfile = _advancedFilterProfile;
                interlaceBlendEnabled = _interlaceBlendEnabled;
                interlaceBlendFieldParity = _interlaceBlendFieldParity;
                scanlineDarken = _scanlineDarken;
                _frameDirty = false;
            }

            if (frameWidth <= 0 || frameHeight <= 0 || _textureId == 0 || _programId == 0 || _vertexBufferId == 0)
                return;

            gl.ActiveTexture(GlTexture0);
            gl.BindTexture(GlTexture2D, _textureId);
            if (_vertexArrayId != 0)
                gl.BindVertexArray(_vertexArrayId);
            UpdateTextureFiltering(gl, sharpPixelsEnabled);

            if (frameDirty && frameBytes.Length > 0)
            {
                EnsureTextureStorage(gl, frameWidth, frameHeight);
                long uploadStart = Stopwatch.GetTimestamp();
                fixed (byte* pFrame = frameBytes)
                {
                    UploadTexturePixels(gl, frameWidth, frameHeight, (IntPtr)pFrame, _useSafeRgbaUpload ? GlRgba : GlBgra);
                }
                uploadTicks = Stopwatch.GetTimestamp() - uploadStart;

                lock (_frameSync)
                {
                    _uploadCount++;
                    _uploadTicksTotal += uploadTicks;
                    _lastUploadTicks = uploadTicks;
                }
            }

            gl.UseProgram(_programId);
            if (_samplerLocation >= 0 && _uniform1i != null)
                _uniform1i.Invoke(_samplerLocation, 0);
            if (_textureSizeLocation >= 0 && _uniform2f != null)
                _uniform2f.Invoke(_textureSizeLocation, frameWidth, frameHeight);
            if (_forceOpaqueLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_forceOpaqueLocation, forceOpaque ? 1.0f : 0.0f);
            if (_advancedFilterLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_advancedFilterLocation, applyAdvancedPixelFilter ? 1.0f : 0.0f);
            if (_advancedFilterProfileLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_advancedFilterProfileLocation, (float)advancedFilterProfile);
            if (_scanlinesLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_scanlinesLocation, applyScanlines ? 1.0f : 0.0f);
            if (_scanlineDarkenLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_scanlineDarkenLocation, scanlineDarken);
            if (_swapRedBlueLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_swapRedBlueLocation, _useSafeRgbaUpload ? 1.0f : 0.0f);
            if (_interlaceBlendLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_interlaceBlendLocation, interlaceBlendEnabled ? 1.0f : 0.0f);
            if (_interlaceFieldParityLocation >= 0 && _uniform1f != null)
                _uniform1f.Invoke(_interlaceFieldParityLocation, interlaceBlendFieldParity == 1 ? 1.0f : 0.0f);
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

            long renderTicks = Stopwatch.GetTimestamp() - renderStart;
            lock (_frameSync)
            {
                _renderTicksTotal += renderTicks;
                _lastRenderTicks = renderTicks;
                if (!frameDirty)
                    _lastUploadTicks = 0;
            }

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
            {
                void requestOnUi()
                {
                    if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
                    {
                        topLevel.RequestAnimationFrame(_ => RequestNextFrameRendering());
                    }
                    else
                    {
                        RequestNextFrameRendering();
                    }
                }

                if (Dispatcher.UIThread.CheckAccess())
                    requestOnUi();
                else
                    Dispatcher.UIThread.Post(requestOnUi, DispatcherPriority.Render);
            }
        }

        private void ApplyOptionsLocked(in FrameBlitOptions options)
        {
            _sharpPixelsEnabled = options.SharpPixels;
            _forceOpaque = options.ForceOpaque;
            _applyScanlines = options.ApplyScanlines;
            _applyAdvancedPixelFilter = options.ApplyAdvancedPixelFilter;
            _advancedFilterProfile = options.AdvancedFilterProfile;
            _scanlineDarken = Math.Clamp(options.ScanlineDarkenFactor / 256f, 0f, 1f);
        }

        private void UpdateTextureFiltering(GlInterface gl, bool sharpPixelsEnabled)
        {
            bool useNearest = sharpPixelsEnabled;
            if (_textureUsesNearest == useNearest)
                return;

            int filter = useNearest ? GlNearest : GlLinear;
            gl.TexParameteri(GlTexture2D, GlTextureMinFilter, filter);
            gl.TexParameteri(GlTexture2D, GlTextureMagFilter, filter);
            _textureUsesNearest = useNearest;
        }

        private int GetUniformLocation(string name)
        {
            if (_getUniformLocation == null || _programId == 0)
                return -1;

            return _getUniformLocation.Invoke(_programId, name);
        }

        private static bool GetSafeRgbaUploadDefault()
        {
            string? env = Environment.GetEnvironmentVariable("EUTHERDRIVE_GL_SAFE_UPLOAD");
            if (string.Equals(env, "1", StringComparison.Ordinal))
                return true;
            if (string.Equals(env, "0", StringComparison.Ordinal))
                return false;

            // GLES drivers on Android are much less consistent about BGRA texture uploads
            // than desktop GL. Default to the explicit BGRA->RGBA conversion there so we
            // prefer a visible frame over the slightly faster desktop upload path.
            return OperatingSystem.IsAndroid();
        }

        private static bool GetPixelUnpackBufferDefault()
        {
            string? env = Environment.GetEnvironmentVariable("EUTHERDRIVE_GL_ENABLE_PBO");
            if (string.Equals(env, "1", StringComparison.Ordinal))
                return true;
            if (string.Equals(env, "0", StringComparison.Ordinal))
                return false;

            // Android benefits the most from asynchronous texture upload when GLES supports it.
            return OperatingSystem.IsAndroid();
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

        private sealed class GetUniformLocationInvoker
        {
            private readonly GetUniformLocationCdecl? _cdecl;
            private readonly GetUniformLocationStdCall? _stdcall;

            private GetUniformLocationInvoker(GetUniformLocationCdecl callback) => _cdecl = callback;
            private GetUniformLocationInvoker(GetUniformLocationStdCall callback) => _stdcall = callback;

            public static GetUniformLocationInvoker? Create(IntPtr proc)
            {
                if (proc == IntPtr.Zero)
                    return null;

                if (OperatingSystem.IsWindows())
                    return new GetUniformLocationInvoker(Marshal.GetDelegateForFunctionPointer<GetUniformLocationStdCall>(proc));

                return new GetUniformLocationInvoker(Marshal.GetDelegateForFunctionPointer<GetUniformLocationCdecl>(proc));
            }

            public int Invoke(int program, string name)
            {
                IntPtr utf8 = Marshal.StringToHGlobalAnsi(name);
                try
                {
                    if (_stdcall != null)
                        return _stdcall(program, utf8);

                    return _cdecl!(program, utf8);
                }
                finally
                {
                    Marshal.FreeHGlobal(utf8);
                }
            }
        }

        private sealed class Uniform1iInvoker
        {
            private readonly Uniform1iCdecl? _cdecl;
            private readonly Uniform1iStdCall? _stdcall;

            private Uniform1iInvoker(Uniform1iCdecl callback) => _cdecl = callback;
            private Uniform1iInvoker(Uniform1iStdCall callback) => _stdcall = callback;

            public static Uniform1iInvoker? Create(IntPtr proc)
            {
                if (proc == IntPtr.Zero)
                    return null;

                if (OperatingSystem.IsWindows())
                    return new Uniform1iInvoker(Marshal.GetDelegateForFunctionPointer<Uniform1iStdCall>(proc));

                return new Uniform1iInvoker(Marshal.GetDelegateForFunctionPointer<Uniform1iCdecl>(proc));
            }

            public void Invoke(int location, int value)
            {
                if (_stdcall != null)
                {
                    _stdcall(location, value);
                    return;
                }

                _cdecl!(location, value);
            }
        }

        private sealed class Uniform1fInvoker
        {
            private readonly Uniform1fCdecl? _cdecl;
            private readonly Uniform1fStdCall? _stdcall;

            private Uniform1fInvoker(Uniform1fCdecl callback) => _cdecl = callback;
            private Uniform1fInvoker(Uniform1fStdCall callback) => _stdcall = callback;

            public static Uniform1fInvoker? Create(IntPtr proc)
            {
                if (proc == IntPtr.Zero)
                    return null;

                if (OperatingSystem.IsWindows())
                    return new Uniform1fInvoker(Marshal.GetDelegateForFunctionPointer<Uniform1fStdCall>(proc));

                return new Uniform1fInvoker(Marshal.GetDelegateForFunctionPointer<Uniform1fCdecl>(proc));
            }

            public void Invoke(int location, float value)
            {
                if (_stdcall != null)
                {
                    _stdcall(location, value);
                    return;
                }

                _cdecl!(location, value);
            }
        }

        private sealed class Uniform2fInvoker
        {
            private readonly Uniform2fCdecl? _cdecl;
            private readonly Uniform2fStdCall? _stdcall;

            private Uniform2fInvoker(Uniform2fCdecl callback) => _cdecl = callback;
            private Uniform2fInvoker(Uniform2fStdCall callback) => _stdcall = callback;

            public static Uniform2fInvoker? Create(IntPtr proc)
            {
                if (proc == IntPtr.Zero)
                    return null;

                if (OperatingSystem.IsWindows())
                    return new Uniform2fInvoker(Marshal.GetDelegateForFunctionPointer<Uniform2fStdCall>(proc));

                return new Uniform2fInvoker(Marshal.GetDelegateForFunctionPointer<Uniform2fCdecl>(proc));
            }

            public void Invoke(int location, float value0, float value1)
            {
                if (_stdcall != null)
                {
                    _stdcall(location, value0, value1);
                    return;
                }

                _cdecl!(location, value0, value1);
            }
        }
    }
}
