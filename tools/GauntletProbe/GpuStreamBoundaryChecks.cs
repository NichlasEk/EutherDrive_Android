using System.Reflection;
using EutherDrive.Core.Arcade.Vegas;

// ROM-free call-site checks in both diagnostic and ordinary Core builds.
internal static class GpuStreamBoundaryChecks
{
    internal static void Run(Assembly assembly, bool captureBuild)
    {
        Type type=assembly.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend",true)!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        foreach(var (method,reason,args) in new (string,string,object[])[] {
            ("ReadLfb32","cpu-lfb-read",[0u]),
            ("WriteLfb32","cpu-lfb-write",[0u,0u]),
            ("FastFill","fast-fill",[]),
            ("ExecuteSwapBuffers","swap",[0u]),
            ("MaterializePendingClear","pending-clear",[0]),
            ("TryRenderLfb","host-present-read",[new EutherFrameTarget([],0,0,0)]),
            ("CapturePresentedColorBuffer","presented-buffer-copy",[0]),
            ("FillTriangle","flat-triangle",[0f,0f,0f,0f,0f,0f,(ushort)0,"test"]),
            ("FillGradientColorTriangle","gradient-color-triangle",[0f,0f,0f,0f,0f,0f,(ushort)0]),
            ("FillGradientTexturedTriangle","gradient-textured-triangle",[0f,0f,0f,0f,0f,0f,(ushort)0]),
            ("DrawLfbLine","lfb-line",[0f,0f,0f,0f,(ushort)0])
        }) {
            var directory=Directory.CreateTempSubdirectory("gauntlet-stream-boundary-");
            try {
                object backend=Activator.CreateInstance(type,nonPublic:true)!;
                type.GetField("_gpuStreamActive",flags)!.SetValue(backend,true);
                type.GetField("_gpuStreamDirectory",flags)!.SetValue(backend,directory.FullName);
                if(method=="MaterializePendingClear")
                    ((bool[])type.GetField("_pendingClearValid",flags)!.GetValue(backend)!)[0]=true;
                type.GetMethod(method,flags)!.Invoke(backend,args);
                bool stopped=(bool)type.GetField("_gpuStreamStopped",flags)!.GetValue(backend)!;
                bool active=(bool)type.GetField("_gpuStreamActive",flags)!.GetValue(backend)!;
                string path=Path.Combine(directory.FullName,"boundary.txt");
                if(stopped!=captureBuild || active==captureBuild || File.Exists(path)!=captureBuild ||
                    (captureBuild && !File.ReadAllText(path).Contains($"reason={reason}")))
                    throw new InvalidOperationException($"GPU stream boundary failed: {method}, captureBuild={captureBuild}");
            }
            finally { directory.Delete(recursive:true); }
        }
        Console.WriteLine($"gpuStreamBoundaryChecks cases=11 captureBuild={captureBuild} PASS");
        // Exercise the real physical byte writer without creating a Vulkan context.
        Type sessionType=assembly.GetType("EutherDrive.Core.Arcade.Vegas.GpuShadowSession",true)!;
        object session=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(sessionType);
        uint[] dirty=new uint[8192];
        sessionType.GetField("_dirtyPages",flags)!.SetValue(session,dirty);
        object trackedBackend=Activator.CreateInstance(type,nonPublic:true)!;
        type.GetField("_gpuShadow",flags)!.SetValue(trackedBackend,session);
        var writeByte=type.GetMethod("WriteTextureByte",flags)!;
        foreach(uint offset in new uint[]{0,1023,1024,8388607,8388608}) {
            Array.Clear(dirty);
            writeByte.Invoke(trackedBackend,[offset,(byte)37]);
            int page=(int)((offset&8388607)>>10);
            // Wrapped address zero already contains 37: it must remain clean.
            bool changed=offset!=8388608;
            for(int i=0;i<dirty.Length;i++)
                if(dirty[i]!=(captureBuild && changed && i==page?1u:0u))
                    throw new InvalidOperationException($"Dirty page writer failed offset={offset} page={i}");
        }
        ((IDisposable)session).Dispose();
        Console.WriteLine($"gpuDirtyWriterChecks cases=5 captureBuild={captureBuild} PASS");
        string? savedLimit=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT");
        string? savedBatch=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH");
        string? savedStats=Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS");
        try {
            Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS",null);
            var read=type.GetMethod("ReadGpuRuntimeLimit",BindingFlags.Static|BindingFlags.NonPublic)!;
            foreach(var (value,batch,expected) in new (string?,string?,int)[] {
                (null,null,128),("1",null,1),("4096",null,4096),("65536",null,65536),
                ("0",null,-1),("65537",null,-1),("bad",null,-1),("4096","1",-1),("128","1",128)
            }) {
                Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT",value);
                Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH",batch);
                int actual;
                try { actual=(int)read.Invoke(null,null)!; }
                catch(TargetInvocationException e) when(e.InnerException is ArgumentException or NotSupportedException) { actual=-1; }
                if(actual!=expected) throw new InvalidOperationException($"GPU limit test failed: {value}/{batch}");
            }
            Console.WriteLine("gpuRuntimeLimitChecks cases=9 PASS");
            Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH","1");
            Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS","1");
            foreach(int limit in new[]{129,4096,65536}) {
                Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT",limit.ToString());
                if((int)read.Invoke(null,null)! != limit) throw new InvalidOperationException("Batch-statistics runtime limit failed");
            }
            Console.WriteLine("gpuBatchStatisticsLimitChecks cases=3 PASS");
            object continuingBackend=Activator.CreateInstance(type,nonPublic:true)!;
            type.GetField("_gpuBatchContinuation",flags)!.SetValue(continuingBackend,true);
            type.GetMethod("ReadLfb32",flags)!.Invoke(continuingBackend,[0u]);
            bool continuation=(bool)type.GetField("_gpuBatchContinuation",flags)!.GetValue(continuingBackend)!;
            if(continuation==captureBuild) throw new InvalidOperationException("GPU batch continuation boundary failed");
            Console.WriteLine("gpuBatchContinuationBoundary PASS");
        } finally {
            Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT",savedLimit);
            Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH",savedBatch);
            Environment.SetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS",savedStats);
        }
    }
}
