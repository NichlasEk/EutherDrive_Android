using System.Collections;
using System.Reflection;

// ROM-free checks of the lookup accelerator and both execution invalidation paths.
internal static class RuntimeBlockFastCacheChecks
{
    internal static void Run(Assembly assembly)
    {
        const string prefix = "EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_";
        string[] variables = [prefix + "BLOCK_FAST_CACHE", prefix + "SAFE_INSTRUCTION_BATCHES",
            prefix + "COMPILED_BLOCKS", "EUTHERDRIVE_GAUNTDL_RUNTIME_COMPILED_BLOCK_MIN_INSTRUCTIONS",
            "EUTHERDRIVE_GAUNTDL_PROFILE_RUNTIME_BLOCK_CACHE", "EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE"];
        string?[] previous = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (string variable in variables)
                Environment.SetEnvironmentVariable(variable, "1");
            Environment.SetEnvironmentVariable(variables[3], "4");
            Environment.SetEnvironmentVariable(variables[5], "256");
            Type memoryType = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VegasMemoryMap", true)!;
            Type type = assembly.GetType("EutherDrive.Core.Arcade.Vegas.MipsR5000Core", true)!;
            object memory = Activator.CreateInstance(memoryType)!;
            object cpu = Activator.CreateInstance(type, [memory])!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            object? Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(cpu, args);
            FieldInfo Field(string name) => type.GetField(name, flags)!;
            void Reset() => type.GetMethod("Reset")!.Invoke(cpu, null);
            void Write(ulong pc, uint op) => memoryType.GetMethod("WriteRuntimeData32")!.Invoke(memory, [pc, op]);
            object Get(ulong pc) => Call("GetRuntimeSafeInstructionBlock", pc)!;
            Array Instructions(object block) => (Array)block.GetType().GetProperty("Instructions")!.GetValue(block)!;
            int checks = 0;
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
                checks++;
            }
            Reset();
            const ulong entry = 0xffffffff80010000UL;
            const ulong collision = entry + 1024;
            const ulong alias = 0x80010000UL;
            foreach (ulong pc in new[] { entry, collision })
            {
                for (ulong offset = 0; offset < 32; offset += 4) Write(pc + offset, 0);
                Write(pc + 32, 0x03e00008); // jr ra terminates the safe block
            }
            object first = Get(entry);
            Check(Instructions(first).Length == 8, "Unexpected synthetic block length");
            Check(ReferenceEquals(first, Get(entry)), "Repeated lookup missed");
            object other = Get(collision);
            Check(!ReferenceEquals(first, other), "Index collision returned wrong block");
            Check(ReferenceEquals(first, Get(entry)), "Collision lost dictionary block");
            object aliasBlock = Get(alias);
            Check(!ReferenceEquals(first, aliasBlock), "Full-width PC tag was ignored");
            Check(ReferenceEquals(first, Get(entry)), "Alias damaged original block");
            Get(collision);
            Call("InvalidateRuntimeSafeInstructionBlock", entry);
            Check(ReferenceEquals(other, Get(collision)), "Invalidation damaged colliding block");
            first = Get(entry);
            Call("InvalidateRuntimeSafeInstructionBlock", entry);
            Check(!ReferenceEquals(first, Get(entry)), "Invalidation left a stale L1 entry");
            const ulong emptyPc = entry + 0x2000;
            Write(emptyPc, 0x03e00008);
            object empty = Get(emptyPc);
            Check(Instructions(empty).Length == 0 && ReferenceEquals(empty, Get(emptyPc)), "Negative cache failed");
            Check(Get(0UL) is not null, "Zero PC confused with empty slot");
            first = Get(entry);
            Reset();
            Check(((IDictionary)Field("_runtimeSafeInstructionBlocks").GetValue(cpu)!).Count == 0,
                "Reset retained dictionary entries");
            Check(((Array)Field("_runtimeSafeBlockFastCache").GetValue(cpu)!).Cast<object>()
                .All(slot => slot.GetType().GetField("Block")!.GetValue(slot) is null), "Reset retained L1 entries");
            Check(!ReferenceEquals(first, Get(entry)), "Reset returned an old block");

            // Patch a cached entry, then exercise the real batch guard.
            first = Get(entry);
            Write(entry, 0x2402002a); // addiu v0, zero, 42
            Field("_remainingProbeSteps").SetValue(cpu, 8);
            Check((bool)Call("TryRunRuntimeSafeInstructionBatch", entry, 0U)!, "Batch rejected synthetic code");
            Check(((ulong[])Field("_gpr").GetValue(cpu)!)[2] == 42, "Batch executed stale entry word");
            Check(!ReferenceEquals(first, Get(entry)), "Batch guard failed to replace cache entry");

            // Separately reach the compiled runner's own guard (normally the batch
            // guard catches this first). No emitted stale instruction may execute.
            Reset();
            first = Get(entry);
            object compiled = Call("CompileRuntimeBlock", entry, Instructions(first))!;
            Check(compiled is not null, "Synthetic block did not compile");
            first.GetType().GetProperty("CompiledBlock")!.SetValue(first, compiled);
            Write(entry, 0x2402002b);
            Field("_remainingProbeSteps").SetValue(cpu, 8);
            Field("_probeStepDebt").SetValue(cpu, 0); // Normally cleared by RunProbeSteps.
            Check(!(bool)Call("TryRunRuntimeCompiledBlock", entry, 0U, first)!, "Compiled guard accepted stale code");
            Check(!ReferenceEquals(first, Get(entry)), "Compiled guard retained stale L1 entry");

            Reset();
            Get(entry);
            Get(entry);
            Get(collision);
            Get(entry);
            Call("InvalidateRuntimeSafeInstructionBlock", entry);
            Get(entry);
            Check((string)type.GetProperty("RuntimeBlockCacheProfileStatus")!.GetValue(cpu)! ==
                "runtimeBlockCache slots=256 lookups=5 hits=1 collisions=2 emptyHits=0 dictionaryHits=1 builds=3 invalidations=1",
                "Cache counters did not account for the known access sequence");
            Get(emptyPc);
            Get(emptyPc);
            Check((ulong)Field("_blockCacheEmptyHits").GetValue(cpu)! == 1, "Empty hits not counted");
            Reset();
            Check((string)type.GetProperty("RuntimeBlockCacheProfileStatus")!.GetValue(cpu)! ==
                "runtimeBlockCache slots=256 lookups=0 hits=0 collisions=0 emptyHits=0 dictionaryHits=0 builds=0 invalidations=0",
                "Reset retained counters");
            foreach ((string value, int expected) in new[] { ("64", 64), ("4096", 4096),
                ("65536", 65536), ("1000", 256), ("131072", 256), ("0", 256), ("bad", 256) })
            {
                Environment.SetEnvironmentVariable(variables[5], value);
                object sized = Activator.CreateInstance(type, [memory])!;
                Array slots = (Array)Field("_runtimeSafeBlockFastCache").GetValue(sized)!;
                Check(slots.Length == expected, $"Incorrect cache size for {value}");
                object Lookup(ulong pc) => type.GetMethod("GetRuntimeSafeInstructionBlock", flags)!.Invoke(sized, [pc])!;
                object original = Lookup(entry);
                object colliding = Lookup(entry + (ulong)expected * 4);
                Check(!ReferenceEquals(original, colliding) && ReferenceEquals(original, Lookup(entry)),
                    $"Collision handling failed for {value}");
                type.GetMethod("InvalidateRuntimeSafeInstructionBlock", flags)!.Invoke(sized, [entry]);
                Check(!ReferenceEquals(original, Lookup(entry)), $"Invalidation failed for {value}");
            }
            Environment.SetEnvironmentVariable(variables[0], "0");
            Environment.SetEnvironmentVariable(variables[4], "0");
            object disabled = Activator.CreateInstance(type, [memory])!;
            Check(Field("_runtimeSafeBlockFastCache").GetValue(disabled) is null, "Disabled cache allocated storage");
            type.GetMethod("GetRuntimeSafeInstructionBlock", flags)!.Invoke(disabled, [entry]);
            Check((ulong)Field("_blockCacheLookups").GetValue(disabled)! == 0 &&
                (ulong)Field("_blockCacheBuilds").GetValue(disabled)! == 0, "Disabled profiling counted work");
            Console.WriteLine($"runtimeBlockFastCacheChecks=passed cases:{checks}");
        }
        finally
        {
            for (int i = 0; i < variables.Length; i++)
                Environment.SetEnvironmentVariable(variables[i], previous[i]);
        }
    }
}
