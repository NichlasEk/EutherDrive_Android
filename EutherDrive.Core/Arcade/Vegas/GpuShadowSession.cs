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
    private readonly Flush _readPixels;
    public bool Batched { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH")=="1";
    public bool Replace { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_REPLACE")=="1";
    public bool Resident { get; }=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_RESIDENT")=="1";
    private readonly bool _profiling=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_PROFILE")=="1";
    private double _applyMs;

    public GpuShadowSession(string library,string shader)
    {
        _library=NativeLibrary.Load(Path.GetFullPath(library));
        try {
            if(Replace && Batched) throw new NotSupportedException("GPU replacement currently requires per-draw synchronization");
            if(Resident && !Replace) throw new NotSupportedException("Resident mode requires GPU replacement");
            if(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DIRTY_TEXTURE")=="1") {
                if(!Resident || !Replace || Batched || Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL")!="1" || Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT")!="1")
                    throw new NotSupportedException("Dirty tracking requires resident incremental sparse replacement");
                _dirtyPages=new uint[8192];
            }
            T Bind<T>(string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library,name));
            if(Bind<Version>("gauntlet_shadow_abi_version")()!=4) throw new NotSupportedException("Unsupported GPU shadow ABI");
            _destroy=Bind<Destroy>("gauntlet_shadow_destroy");_error=Bind<Error>("gauntlet_shadow_error");
            _draw=Bind<Draw>(Batched?"gauntlet_shadow_enqueue":Resident?"gauntlet_shadow_resident_draw":"gauntlet_shadow_draw");_output=Bind<Output>("gauntlet_shadow_output");
            _flush=Bind<Flush>("gauntlet_shadow_flush");
            _readPixels=Bind<Flush>("gauntlet_shadow_read_pixels");
            if(_dirtyPages is not null) _dirtyDraw=Bind<DirtyDraw>("gauntlet_shadow_dirty_draw");
            _context=Bind<Create>("gauntlet_shadow_create")(Path.GetFullPath(shader));
            if(_context==0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(_error()));
        } catch { NativeLibrary.Free(_library);_library=0;throw; }
    }
    public void Render(uint[] texture,uint[] ncc,uint[] meta,ushort[] color,ushort[] depth,bool reset)
    {
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(texture.Length!=2097152 || ncc.Length!=512 || meta.Length!=256 || color.Length!=2097152 || depth.Length!=2097152)
            throw new ArgumentException("Invalid GPU shadow buffers");
        fixed(uint* t=texture,n=ncc,m=meta,dirty=_dirtyPages) fixed(ushort* c=color,d=depth)
            if((_dirtyDraw is not null?_dirtyDraw(_context,t,n,m,c,d,reset?1:0,dirty):_draw(_context,t,n,m,c,d,reset?1:0))!=0)
                throw new InvalidOperationException(Marshal.PtrToStringUTF8(_error()));
        if(_dirtyPages is not null) Array.Clear(_dirtyPages);
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
    public void FlushAndCompare(ushort[] color,ushort[] depth)
    {
        if(_context==0) throw new ObjectDisposedException(nameof(GpuShadowSession));
        if(_flush(_context)!=0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(_error()));
        Compare(color,depth);
        GC.KeepAlive(this);
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
        Apply(color,depth);GC.KeepAlive(this);
    }
    public void Dispose()
    {
        if(_context!=0) {
            if(_profiling) Console.WriteLine($"gpuProfileManaged applyPixelsMs={_applyMs:F3}");
            _destroy(_context);_context=0;
        }
        if(_library!=0) { NativeLibrary.Free(_library);_library=0; }
        GC.SuppressFinalize(this);
    }
    ~GpuShadowSession() { Dispose(); }
}
