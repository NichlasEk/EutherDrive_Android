using System.Diagnostics;
namespace EutherDrive.Core.Arcade.Vegas;

internal partial class VoodooBringupBackend
{
    private sealed record GpuDrawCapture(string Path, uint[] Meta, uint[] Initial, uint[] Texture,
        uint[] Ncc, long Started, int Frame, int Buffer, long[]? Stats=null);
    private GpuDrawCapture? _gpuDraw;
    private int _gpuDrawCount, _gpuDrawEligible, _gpuExtendedColorDrawCount;
    private bool _gpuStreamActive, _gpuStreamStopped;
    private string? _gpuStreamDirectory;
    private int _gpuStreamBuffer;
    private GpuShadowSession? _gpuShadow;
    private int _gpuShadowSegments,_gpuShadowSegmentDraws;
    [Conditional("GAUNTLET_GPU_CAPTURE")]
    private void MarkGpuTextureWrite(int wordOffset,bool changed)
    {
        if(changed) _gpuShadow?.MarkTextureWrite(wordOffset);
    }
    private int? _gpuRuntimeLimit;
    private int GpuRuntimeLimit => _gpuRuntimeLimit ??= ReadGpuRuntimeLimit();
    private static int ReadGpuRuntimeLimit()
    {
        string? value=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT");
        if(string.IsNullOrEmpty(value)) return 128;
        if(!int.TryParse(value,out int limit) || limit<1 || limit>65536)
            throw new ArgumentException("GPU draw limit must be between 1 and 65536");
        if(limit>128 && Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH")=="1")
            throw new NotSupportedException("Batched shadow remains limited to 128 draws");
        return limit;
    }

    [Conditional("GAUNTLET_GPU_CAPTURE")]
    internal void CloseGpuShadow()
    {
        if(_gpuShadow is not null && _gpuStreamActive) GpuStreamBoundary("backend-reset");
        _gpuShadow?.Dispose();_gpuShadow=null;
        _gpuStreamActive=false;_gpuStreamStopped=true;_gpuDraw=null;
    }

    [Conditional("GAUNTLET_GPU_CAPTURE")]
    private void GpuStreamBoundary(string reason)
    {
        if (!_gpuStreamActive) return;
        if(_gpuShadow is not null) {
            if(_gpuShadow.Resident) {
                try { _gpuShadow.SynchronizePixels(_colorBuffers[_gpuStreamBuffer],_auxBuffer); }
                catch { _gpuShadow.Dispose();_gpuShadow=null;_gpuStreamActive=false;_gpuStreamStopped=true;throw; }
            }
            if(_gpuShadow.Batched) {
                try { _gpuShadow.FlushAndCompare(_colorBuffers[_gpuStreamBuffer],_auxBuffer); }
                catch { _gpuShadow.Dispose();_gpuShadow=null;_gpuStreamActive=false;_gpuStreamStopped=true;throw; }
            }
            _gpuStreamActive=false;_gpuStreamStopped=_gpuDrawCount>=GpuRuntimeLimit;
            string result=_gpuShadow.Replace?"mode=replace cpuRasterSkipped=true rasterCounters=PASS":"mode=shadow colorDepthMismatch=0";
            Console.WriteLine($"gpuShadowBoundary segment={_gpuShadowSegments} draws={_gpuShadowSegmentDraws} totalDraws={_gpuDrawCount} extendedDraws={_gpuExtendedColorDrawCount} reason={reason} {result}");
            if(_gpuStreamStopped) { _gpuShadow.Dispose();_gpuShadow=null; }
            return;
        }
        _gpuStreamActive=false; _gpuStreamStopped=true;
        string message=$"gpuStreamBoundary draws={_gpuDrawCount} reason={reason}";
        Console.WriteLine(message);
        using var writer=new StreamWriter(new FileStream(Path.Combine(_gpuStreamDirectory!,"boundary.txt"),FileMode.CreateNew));
        writer.WriteLine(message);
    }

    private static bool IsGpuExtendedColorState(uint fbz, uint tm0, uint tm1, int level)
    {
        if(level is not (1 or 2)) return false;
        if(fbz==0x000b4779U && ((tm0==0x8c24110fU && tm1==0x8c241acfU) ||
            (tm0==0x80000009U && tm1==0x8c24110fU))) return true;
        return level==2 &&
            ((fbz==0x000b4779U && tm0==0x8c24110fU && tm1==0x8c24110fU) ||
             ((fbz is 0x000b4779U or 0x000b4379U) && tm0==0x8c24190fU && tm1==0x8c241acfU));
    }

    [Conditional("GAUNTLET_GPU_CAPTURE")]
    private void BeginGpuDrawCapture(bool common, int minX, int minY, int maxX, int maxY,
        int setupAx, int setupAy, bool positive, SetupVertex a, SetupVertex b, SetupVertex c,
        int buffer, ushort za, ushort fog, FbzColorPathState colors, bool rgbMask, bool auxMask,
        bool depthTest, ushort fallback, bool has0, MameTextureTriangleState state0,
        bool has1, MameTextureTriangleState state1, int lodBase0, int lodBase1, int lodOverride,
        long[] gradients, int startAlpha, int alphaDx, int alphaDy)
    {
        bool extendedColor = colors.Mode == 0x0c602c19U;
        if(extendedColor) {
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_EXTENDED_COLOR_PATH"),out int level);
            common &= IsGpuExtendedColorState(_registers[RegFbzMode],state0.Mode,state1.Mode,level);
        }
        string? directory = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DRAW_DIR");
        string? streamDirectory = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_STREAM_DIR");
        bool shadow=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW")=="1" ||
            Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_REPLACE")=="1";
        if(shadow && (!string.IsNullOrEmpty(directory) || !string.IsNullOrEmpty(streamDirectory)))
            throw new NotSupportedException("Shadow and file capture are mutually exclusive");
        bool stream = shadow || !string.IsNullOrEmpty(streamDirectory);
        if(stream) {
            if(!string.IsNullOrEmpty(directory)) throw new NotSupportedException("Choose draw OR stream capture");
            directory=streamDirectory;
            if(!common) {
                if(_gpuStreamActive && shadow && Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_PROFILE")=="1")
                    Console.WriteLine($"gpuUnsupportedState fbz={_registers[RegFbzMode]:x8} cp={_registers[RegFbzColorPath]:x8} alpha={_registers[RegAlphaMode]:x8} fog={_registers[RegFogMode]:x8} tm0={state0.Mode:x8} tm1={state1.Mode:x8}");
                GpuStreamBoundary("unsupported-textured-state");
            }
            if(_gpuStreamActive && buffer!=_gpuStreamBuffer) GpuStreamBoundary("draw-buffer-change");
            if(_gpuStreamStopped) return;
            // Clamped Y would alias multiple shader invocations to one pixel.
            if(GetRasterYOrigin()>=0 && maxY>GetRasterYOrigin()+1) {
                GpuStreamBoundary("aliased-y-origin");return;
            }
        }
        if ((!shadow && string.IsNullOrEmpty(directory)) || !common || _gpuDrawCount >= (shadow?GpuRuntimeLimit:stream?32:8)) return;
        if (!_gpuStreamActive && (maxX - minX) * (maxY - minY) < 8192) return;
        int skip = int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DRAW_SKIP"), out int n) ? n : 0;
        if (!_gpuStreamActive && _gpuDrawEligible++ < skip) return;
        if (ShouldTrackTextureSampleDiagnostics() || _gpuSamples is not null ||
            !_experimentTextureMameSetupGradients || !_experimentTextureMameFixedFetch ||
            !_experimentTextureMamePixelLod)
            throw new NotSupportedException("Draw capture requires fixed/pixel-LOD path and no sample capture/diagnostics");
        uint[] m = new uint[256];
        m[0]=(uint)minX; m[1]=(uint)minY; m[2]=(uint)(maxX-minX); m[3]=(uint)(maxY-minY);
        m[4]=(uint)setupAx; m[5]=(uint)setupAy; m[6]=positive?1u:0u; m[7]=za;
        float[] vertices = [a.X,a.Y,b.X,b.Y,c.X,c.Y];
        for (int i=0;i<6;i++) m[8+i]=BitConverter.SingleToUInt32Bits(vertices[i]);
        m[14]=colors.Color0; m[15]=colors.Color1; m[16]=(uint)colors.Color0Alpha; m[17]=(uint)colors.Color1Alpha;
        m[18]=fog; m[19]=_registers[RegFbzMode]; m[20]=rgbMask?1u:0u; m[21]=auxMask?1u:0u; m[22]=depthTest?1u:0u;
        m[24]=_visualizeZeroTextureFallback?1u:0u; m[25]=_treatZeroTextureTexelAsTransparent?1u:0u;
        m[26]=_experimentTmu1ZeroAsNeutralWhite?1u:0u; m[27]=fallback;
        m[28]=extendedColor?1u:0u;
        m[116]=(uint)startAlpha;m[117]=(uint)alphaDx;m[118]=(uint)alphaDy;
        m[23]=stream?1u:0u;m[30]=(uint)GetRasterYOrigin();
        for (int i=0;i<gradients.Length;i++) { m[32+2*i]=(uint)gradients[i];m[33+2*i]=(uint)((ulong)gradients[i]>>32); }
        m[80]=(uint)lodBase0;m[81]=(uint)lodBase1;m[82]=(uint)lodOverride;
        for (int i=0;i<32;i++) m[84+i]=_registers[RegFogTable+i];
        uint[] ncc = new uint[512];
        void State(int tmu, bool has, MameTextureTriangleState s)
        {
            int p=128+tmu*64; m[p+13]=has?1u:0u;
            if (!has) return;
            if (s.Format is not (0 or 1 or 2 or 3 or 4 or 8 or 9 or 10 or 11 or 12 or 13) ||
                (s.Format is 1 or 9) && s.NccRgbaLut is null) throw new NotSupportedException("Unsupported draw texture format");
            m[p]=s.Mode;m[p+1]=s.Lod;m[p+2]=s.Base;m[p+3]=(uint)s.Format;
            m[p+4]=(s.Perspective?1u:0u)|(s.ClampNegativeW?2u:0u)|(s.Filtered?4u:0u)|
                (!_experimentTextureCoordinateWrap&&(s.Mode&64)!=0?8u:0u)|(!_experimentTextureCoordinateWrap&&(s.Mode&128)!=0?16u:0u)|
                (_fixTextureTOriginFlip?32u:0u)|(s.Swap16BitBytes?64u:0u)|
                (_experimentReverse16BitTextureSampleLanes?128u:0u)|(_experimentReverse8BitTextureSampleLanes?256u:0u);
            int memTmu=_experimentTmu1SampleTmu0Memory&&tmu==1?0:tmu;
            m[p+5]=_experimentSeparateTmuTextureMemory?(uint)(memTmu*TextureBankBytes):0;
            m[p+6]=(uint)(_experimentSeparateTmuTextureMemory?TextureBankBytes-1:TextureBytes-1);m[p+7]=(uint)(tmu*256);
            m[p+8]=(uint)s.LodMin8p8;m[p+9]=(uint)s.LodMax8p8;m[p+10]=(uint)s.LodBias8p8;
            m[p+11]=s.LodMask;m[p+12]=s.LodDither?1u:0u;
            m[p+14]=s.CombineLocalOnly?1u:0u;m[p+15]=s.CombineModulateOtherRgbLocalAlpha?1u:0u;
            m[p+16]=(uint)GetTextureTargetLod(s.Lod,s.Base);
            for(int i=0;i<9;i++) { m[p+24+3*i]=(uint)s.Layouts[i].Width;m[p+25+3*i]=(uint)s.Layouts[i].Height;m[p+26+3*i]=s.Layouts[i].BaseAddress; }
            if(s.NccRgbaLut is not null) for(int i=0;i<256;i++) ncc[tmu*256+i]=PackGpuRgba(s.NccRgbaLut[i]);
        }
        State(0,has0,state0);State(1,has1,state1);
        if(shadow) {
            _gpuShadow ??= new GpuShadowSession(
                Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_LIBRARY") ?? ".build-tmp/gauntlet-gpu-probe/libgauntlet_shadow.so",
                Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_SHADER") ?? ".build-tmp/gauntlet-gpu-probe/draw.spv");
            bool reset=!_gpuStreamActive;
            m[31]=_gpuShadow.Batched?0u:1u;
            if(reset) { _gpuShadowSegments++;_gpuShadowSegmentDraws=0; }
            try { _gpuShadow.Render(_textureMemory,ncc,m,_colorBuffers[buffer],_auxBuffer,reset); }
            catch { _gpuShadow.Dispose();_gpuShadow=null;_gpuStreamActive=false;_gpuStreamStopped=true;throw; }
            _gpuStreamActive=true;_gpuStreamBuffer=buffer;
            _gpuDrawCount++;_gpuShadowSegmentDraws++;
            if(extendedColor) _gpuExtendedColorDrawCount++;
            _gpuDraw=new("",m,[],[],[],Stopwatch.GetTimestamp(),_renderFrame,buffer,
                _gpuShadow.Batched?null:SnapshotGpuCounters(buffer));
            return;
        }
        Directory.CreateDirectory(directory);
        if(stream) { _gpuStreamActive=true;_gpuStreamDirectory=directory;_gpuStreamBuffer=buffer; }
        uint[] initial=ReadGpuDrawRectangle(m,buffer);
        uint[] texture=(uint[])_textureMemory.Clone();
        _gpuDraw=new(Path.Combine(directory,$"draw-{_gpuDrawCount++:D2}.gdr"),m,initial,texture,ncc,
            Stopwatch.GetTimestamp(),_renderFrame,buffer);
    }

    private uint[] ReadGpuDrawRectangle(uint[] m,int buffer)
    {
        if(m[23]!=0) {
            uint[] full=new uint[LfbPixels];
            for(int i=0;i<full.Length;i++) full[i]=_colorBuffers[buffer][i]|(uint)_auxBuffer[i]<<16;
            return full;
        }
        int width=(int)m[2],height=(int)m[3];uint[] pixels=new uint[width*height];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
            int at=(GetRasterBufferY((int)m[1]+y)*LfbRowPixels+(int)m[0]+x)&(LfbPixels-1);
            pixels[y*width+x]=_colorBuffers[buffer][at]|(uint)_auxBuffer[at]<<16;
        }
        return pixels;
    }

    private long[] SnapshotGpuCounters(int buffer)
    {
        long[] counters=new long[17];
        counters[0]=_texturedPixelCount;counters[1]=_texturedZeroPixelCount;
        counters[2]=_texturedFallbackPixelCount;counters[3]=_texturedRasterPixelCount;
        counters[4]=_profiledCommonRasterPixelCount;
        for(int i=0;i<9;i++) counters[6+i]=_experimentTextureMamePixelLodCounts[i];
        counters[15]=_lfbWriteCount;counters[16]=_rasterBufferPixelCounts[buffer];
        return counters;
    }

    private bool TryApplyGpuReplacement(ref bool coveredAny,ref int coveredPixels,ref int zeroPixels)
    {
        if(_gpuShadow is not { Replace:true } || _gpuDraw is not {} capture) return false;
        uint[] s=_gpuShadow.Statistics();
        if(!_gpuShadow.Resident) _gpuShadow.Apply(_colorBuffers[capture.Buffer],_auxBuffer);
        _texturedPixelCount+=s[0];coveredPixels+=(int)s[0];coveredAny=s[0]!=0;
        _texturedZeroPixelCount+=s[1];zeroPixels+=(int)s[1];_texturedFallbackPixelCount+=s[2];
        _texturedRasterPixelCount+=s[3];_lfbWriteCount+=s[3];_rasterBufferPixelCounts[capture.Buffer]+=s[3];
        _profiledCommonRasterPixelCount+=s[4];
        for(int i=0;i<9;i++) _experimentTextureMamePixelLodCounts[i]+=s[6+i];
        return true;
    }

    [Conditional("GAUNTLET_GPU_CAPTURE")]
    private void EndGpuDrawCapture(bool coveredAny,int coveredPixels,int zeroPixels)
    {
        if(_gpuDraw is not {} capture) return;
        double cpuMs=Stopwatch.GetElapsedTime(capture.Started).TotalMilliseconds;_gpuDraw=null;
        if(_gpuShadow is not null) {
            try {
                if(!_gpuShadow.Batched) {
                    if(!_gpuShadow.Replace) _gpuShadow.Compare(_colorBuffers[capture.Buffer],_auxBuffer);
                    uint[] gpu=_gpuShadow.Statistics();long[] after=SnapshotGpuCounters(capture.Buffer);
                    if(coveredAny!=(gpu[0]!=0) || coveredPixels!=gpu[0] || zeroPixels!=gpu[1])
                        throw new InvalidOperationException("GPU raster coverage/return mismatch");
                    for(int i=0;i<17;i++) {
                        long cpuDelta=after[i]-capture.Stats![i];
                        if(i==0 && Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_COUNTER_CORRUPT_ORACLE")=="1") cpuDelta^=1;
                        uint actual=i>=15?gpu[3]:gpu[i];
                        if(cpuDelta!=actual) throw new InvalidOperationException($"GPU raster counter mismatch index={i} cpu={cpuDelta} gpu={actual}");
                    }
                }
            }
            catch { _gpuShadow.Dispose();_gpuShadow=null;_gpuStreamActive=false;_gpuStreamStopped=true;throw; }
            if(_gpuDrawCount==GpuRuntimeLimit) GpuStreamBoundary("shadow-limit");
            return;
        }
        uint[] expected=ReadGpuDrawRectangle(capture.Meta,capture.Buffer);
        int colorChanges=0,depthChanges=0;
        for(int i=0;i<expected.Length;i++) { if((ushort)expected[i]!=(ushort)capture.Initial[i]) colorChanges++;if(expected[i]>>16!=capture.Initial[i]>>16) depthChanges++; }
        using var writer=new BinaryWriter(new FileStream(capture.Path,FileMode.CreateNew));
        bool stream=capture.Meta[23]!=0;
        foreach(uint word in new uint[]{stream?0x32524447u:0x31524447u,stream?2u:1u,(uint)capture.Texture.Length,512,256,(uint)expected.Length,(uint)capture.Frame,(uint)capture.Buffer}) writer.Write(word);
        foreach(uint[] block in new[]{capture.Texture,capture.Ncc,capture.Meta,capture.Initial,expected}) foreach(uint word in block) writer.Write(word);
        Console.WriteLine($"gpuDrawCapture path={capture.Path} frame={capture.Frame} pixels={expected.Length} colorChanges={colorChanges} depthChanges={depthChanges} cpuRasterMs={cpuMs:F4}");
        if(stream && _gpuDrawCount==32) GpuStreamBoundary("capture-limit");
    }
}
