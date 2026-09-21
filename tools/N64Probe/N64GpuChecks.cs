using System.Runtime.CompilerServices;
using Ryu64Core;

internal static class N64GpuChecks
{
    internal static void Run(string library)
    {
        byte[] ram = new byte[N64GpuBackend.RamSize], hidden = new byte[N64GpuBackend.HiddenSize];
        byte[] sync = { 2,0,0,0, 8,0,0,0, 0,0,0,0xe9, 0,0,0,0 };
        var flags = N64GpuFlags.Validate | N64GpuFlags.RequireDiscrete | N64GpuFlags.DeferDisjointWrites;
        int checks = 0;
        void Reject<T>(Action call) where T : Exception
        {
            try { call(); } catch (T) { checks++; return; }
            throw new Exception($"Expected {typeof(T).Name}");
        }
        Reject<ArgumentException>(() => new N64GpuBackend(library, new byte[1], hidden, flags));
        Reject<InvalidOperationException>(() => new N64GpuBackend(library, ram, hidden, (N64GpuFlags)16));
        using (var gpu = new N64GpuBackend(library, ram, hidden, flags))
        {
            Reject<InvalidOperationException>(() => gpu.Submit(ReadOnlySpan<byte>.Empty));
            ulong token = gpu.Submit(sync);
            Task.Run(() => gpu.Wait(token)).GetAwaiter().GetResult();
            byte[] result = new byte[ram.Length], resultHidden = new byte[hidden.Length], tmem = new byte[4096];
            gpu.Readback(token, result, resultHidden, tmem);
            if (!result.AsSpan().SequenceEqual(ram) || !resultHidden.AsSpan().SequenceEqual(hidden)
                || gpu.GetStats().ValidationErrors != 0) throw new Exception("Managed memory roundtrip failed");
            checks++;
            Reject<ArgumentException>(() => gpu.Readback(token, result, resultHidden, new byte[1]));
            // Simultaneous native calls and Dispose must either hold a lifetime
            // lease or throw ObjectDisposedException, never use unloaded code.
            using var start = new ManualResetEventSlim();
            var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => {
                start.Wait();
                for (int i = 0; i < 256; i++)
                    try { gpu.Wait(token); gpu.GetStats(); }
                    catch (ObjectDisposedException) { return; }
            })).ToArray();
            start.Set();
            Task.Run(gpu.Dispose).GetAwaiter().GetResult();
            Task.WaitAll(workers); checks++;
            gpu.Dispose();
            Reject<ObjectDisposedException>(() => gpu.Submit(sync));
            Reject<ObjectDisposedException>(() => gpu.Wait(token));
            Reject<ObjectDisposedException>(() => gpu.GetStats());
            Reject<ObjectDisposedException>(() => _ = gpu.DeviceName);
            Reject<ObjectDisposedException>(() => gpu.Readback(token, result, resultHidden, tmem));
        }
        // Exercise the finalizer thread, then load/create again after unload.
        // NoInlining prevents the local strong reference surviving the call.
        var weak = Abandon(library, ram, hidden, flags, sync);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        if (weak.IsAlive) throw new Exception("Backend survived finalization");
        using (var recreated = new N64GpuBackend(library, ram, hidden, flags))
        {
            recreated.Wait(recreated.Submit(sync));
            if (recreated.GetStats().ValidationErrors != 0) throw new Exception("Recreated backend invalid");
        }
        checks += 2;
        Console.WriteLine($"managedGpuChecks={checks} disposeRace=passed finalizer=passed recreate=passed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(string library, byte[] ram, byte[] hidden, N64GpuFlags flags, byte[] sync)
    {
        var gpu = new N64GpuBackend(library, ram, hidden, flags);
        gpu.Submit(sync);
        return new WeakReference(gpu);
    }
}
