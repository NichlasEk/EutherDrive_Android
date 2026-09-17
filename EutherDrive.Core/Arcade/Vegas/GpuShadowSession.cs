using System.Runtime.InteropServices;
using System.Diagnostics;

namespace EutherDrive.Core.Arcade.Vegas;

// Diagnostic-only caller. No native library is loaded by an ordinary build.
internal sealed unsafe class GpuShadowSession : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Create([MarshalAs(UnmanagedType.LPUTF8Str)] string shader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Error();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Flush(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Draw(nint context,uint* texture,uint* ncc,uint* meta,ushort* color,ushort* depth,int reset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint* Output(nint context);
    private nint _library,_context;
    private readonly Destroy _destroy;
    private readonly Error _error;
    private readonly Draw _draw;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DirtyDraw(nint context,uint* texture,uint* ncc,uint* meta,ushort* color,ushort* depth,int reset,uint* dirty);
    private readonly DirtyDraw? _dirtyDraw;
    private readonly uint[]? _dirtyPages;
    public void MarkTextureWrite(int wordOffset) { if(_dirtyPages is not null) _dirtyPages[wordOffset>>8]=1; }
    private readonly Output _output;
    private readonly Flush _flush;
    private readonly Flush? _flushKeep;
    private readonly Flush _readPixels;
    public bool Batched { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH")=="1";
    public bool BatchStatistics { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS")=="1";
    public bool BatchResident { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_BATCH_RESIDENT")=="1";
    public bool BatchPixelsPending { get; private set; }
    private readonly List<long[]> _batchExpected=[];
    private int _batchDraws;
    public bool Replace { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_REPLACE")=="1";
    public bool Resident { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_RESIDENT")=="1";
    private readonly bool _profiling=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_PROFILE")=="1";
    private double _applyMs,_renderMs,_flushMs,_statisticsMs;
    private long _renderCalls,_flushCalls;

    public GpuShadowSession(string library,string shader)
    {
        _library=NativeLibrary.Load(Path.GetFullPath(library));
        try {
            if(Replace && Batched && (!BatchStatistics || Resident))
                throw new NotSupportedException("Batch replacement requires batch statistics and non-resident mode");
            if(BatchStatistics && !Batched) throw new NotSupportedException("Batch statistics requires batched shadow mode");
            if(BatchResident && (!Batched || !BatchStatistics || Resident))
                throw new NotSupportedException("Resident batches require batch statistics and no per-draw resident flag");
            if(Resident && !Replace) throw new NotSupportedException("Resident mode requires GPU replacement");
            if(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DIRTY_TEXTURE")=="1") {
                if((Batched?!BatchStatistics:!Resident || !Replace) ||
                    Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL")!="1" || Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT")!="1")
                    throw new NotSupportedException("Dirty tracking requires incremental sparse resident replacement or statistics batches");
                _dirtyPages=new uint[8192];
            }
            T Bind<T>(string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library,name));
            if(Bind<Version>("gauntlet_shadow_abi_version")()!=4) throw new NotSupportedException("Unsupported GPU shadow ABI");
            if(BatchStatistics && Bind<Version>("gauntlet_shadow_batch_stats_version")()!=1)
                throw new NotSupportedException("Unsupported GPU batch statistics capability");
            _destroy=Bind<Destroy>("gauntlet_shadow_destroy");_error=Bind<Error>("gauntlet_shadow_error");
            _draw=Bind<Draw>(Batched?"gauntlet_shadow_enqueue":Resident?"gauntlet_shadow_resident_draw":"gauntlet_shadow_draw");_output=Bind<Output>("gauntlet_shadow_output");
            _flush=Bind<Flush>("gauntlet_shadow_flush");
            if(BatchResident) {
                if(Bind<Version>("gauntlet_shadow_batch_resident_version")()!=1)
                    throw new NotSupportedException("Unsupported resident batch capability");
                _flushKeep=Bind<Flush>("gauntlet_shadow_flush_keep");
            }
            _readPixels=Bind<Flush>("gauntlet_shadow_read_pixels");
            if(_dirtyPages is not null) _dirtyDraw=Bind<DirtyDraw>(Batched?"gauntlet_shadow_dirty_enqueue":"gauntlet_shadow_dirty_draw");
            _context=Bind<Create>("gauntlet_shadow_create")(Path.GetFullPath(shader));
            if(_context==0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(_error()));
        } catch { NativeLibrary.Free(_library);_library=0;throw; }
    }
    public void Render(uint[] texture,uint[] ncc,uint[] meta,ushort[] color,ushort[] depth,bool reset)
    {
        long started=_profiling?Stopwatch.GetTimestamp():0;
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(texture.Length!=2097152 || ncc.Length!=512 || meta.Length!=256 || color.Length!=2097152 || depth.Length!=2097152)
            throw new ArgumentException("Invalid GPU shadow buffers");
        if(BatchStatistics && (reset != (_batchDraws==0) || _batchDraws>=128))
            throw new InvalidOperationException("Invalid managed batch statistics order");
        int resetMode=reset?(BatchPixelsPending?2:1):0;
        fixed(uint* t=texture,n=ncc,m=meta,dirty=_dirtyPages) fixed(ushort* c=color,d=depth)
            if((_dirtyDraw is not null?_dirtyDraw(_context,t,n,m,c,d,resetMode,dirty):_draw(_context,t,n,m,c,d,resetMode))!=0)
                throw new InvalidOperationException(Marshal.PtrToStringUTF8(_error()));
        if(_dirtyPages is not null) Array.Clear(_dirtyPages);
        if(BatchStatistics) _batchDraws++;
        if(_profiling) { _renderMs+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;_renderCalls++; }
        GC.KeepAlive(this);
    }
    public void Compare(ushort[] color,ushort[] depth)
    {
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(color.Length!=2097152 || depth.Length!=2097152) throw new ArgumentException("Invalid GPU shadow comparison buffers");
        uint* result=_output(_context);
        int mismatches=0,first=-1;
        bool corrupt=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_CORRUPT_ORACLE")=="1";
        for(int i=0;i<color.Length;i++) {
            uint expected=color[i]|(uint)depth[i]<<16;
            if(corrupt && i==0) expected^=1;
            if(result[i]!=expected) { if(first<0) first=i;mismatches++; }
        }
        if(mismatches!=0) throw new InvalidOperationException($"GPU shadow color/depth mismatches={mismatches} first={first}");
        GC.KeepAlive(this);
    }
    public void FlushAndCompare(ushort[] color,ushort[] depth,bool keepResident=false)
    {
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(BatchStatistics && (_batchDraws==0 || _batchExpected.Count!=_batchDraws))
            throw new InvalidOperationException("Missing CPU batch oracle");
        FlushBatch(keepResident);
        if(!keepResident) Compare(color,depth);
        if(BatchStatistics) {
            uint* result=_output(_context);
            for(int draw=0;draw<_batchDraws;draw++) for(int i=0;i<17;i++) {
                uint actual=result[2097152+draw*16+(i>=15?3:i)];
                long expected=_batchExpected[draw][i];
                if(actual!=expected) throw new InvalidOperationException($"GPU batch counter mismatch draw={draw} index={i} cpu={expected} gpu={actual}");
            }
            Console.WriteLine($"gpuBatchStatistics draws={_batchDraws} perDrawCounters=PASS");
            _batchDraws=0;_batchExpected.Clear();
        }
        GC.KeepAlive(this);
    }
    public void AddBatchOracle(long[] before,long[] after,bool covered,int coveredPixels,int zeroPixels)
    {
        if(!BatchStatistics || _batchExpected.Count+1!=_batchDraws || before.Length!=17 || after.Length!=17)
            throw new InvalidOperationException("Invalid CPU batch oracle order");
        long[] delta=new long[17];
        for(int i=0;i<17;i++) delta[i]=after[i]-before[i];
        if(covered!=(delta[0]!=0) || coveredPixels!=delta[0] || zeroPixels!=delta[1])
            throw new InvalidOperationException("CPU batch coverage/return mismatch");
        if(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_COUNTER_CORRUPT_ORACLE")=="1") delta[0]^=1;
        _batchExpected.Add(delta);
    }
    private void FlushBatch(bool keepResident)
    {
        long started=_profiling?Stopwatch.GetTimestamp():0;
        if(keepResident && !BatchResident) throw new InvalidOperationException("Resident batch mode is disabled");
        if((keepResident?_flushKeep!:_flush)(_context)!=0)
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(_error()));
        BatchPixelsPending=keepResident;
        if(_profiling) { _flushMs+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;_flushCalls++; }
    }
    public uint[][] FlushAndApplyBatch(ushort[] color,ushort[] depth,bool keepResident=false)
    {
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(!Replace || !Batched || !BatchStatistics || _batchDraws==0 || _batchExpected.Count!=0)
            throw new InvalidOperationException("Invalid replacement batch order");
        FlushBatch(keepResident);
        long statisticsStarted=_profiling?Stopwatch.GetTimestamp():0;
        uint* result=_output(_context);
        uint[][] statistics=new uint[_batchDraws][];
        for(int draw=0;draw<_batchDraws;draw++) {
            statistics[draw]=new uint[16];
            for(int i=0;i<16;i++) statistics[draw][i]=result[2097152+draw*16+i];
        }
        if(_profiling) _statisticsMs+=Stopwatch.GetElapsedTime(statisticsStarted).TotalMilliseconds;
        if(!keepResident) Apply(color,depth);
        _batchDraws=0;
        GC.KeepAlive(this);return statistics;
    }
    public uint[] Statistics()
    {
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        uint* result=_output(_context);uint[] stats=new uint[16];
        for(int i=0;i<16;i++) stats[i]=result[2097152+i];
        GC.KeepAlive(this);return stats;
    }
    public void Apply(ushort[] color,ushort[] depth)
    {
        long started=_profiling?Stopwatch.GetTimestamp():0;
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(color.Length!=2097152 || depth.Length!=2097152) throw new ArgumentException("Invalid replacement buffers");
        uint* result=_output(_context);
        for(int i=0;i<color.Length;i++) { color[i]=(ushort)result[i];depth[i]=(ushort)(result[i]>>16); }
        if(_profiling) _applyMs+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        GC.KeepAlive(this);
    }
    public void SynchronizePixels(ushort[] color,ushort[] depth)
    {
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(_readPixels(_context)!=0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(_error()));
        if(BatchResident && !Replace) Compare(color,depth);else Apply(color,depth);
        BatchPixelsPending=false;GC.KeepAlive(this);
    }
    public void Dispose()
    {
        if(_context!=0) {
            // Render/flush include native time; do not add them to native totals.
            if(_profiling) Console.WriteLine(FormattableString.Invariant($"gpuProfileManaged applyPixelsMs={_applyMs:F3} renderInclusiveMs={_renderMs:F3} flushInclusiveMs={_flushMs:F3} batchStatisticsMs={_statisticsMs:F3} renderCalls={_renderCalls} flushCalls={_flushCalls}"));
            _destroy(_context);_context=0;
        }
        if(_library!=0) { NativeLibrary.Free(_library);_library=0; }
        GC.SuppressFinalize(this);
    }
    ~GpuShadowSession() { Dispose(); }
}
