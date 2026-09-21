#if N64_RDP_JOURNAL
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ryu64Core;

internal static class N64GpuJournalReplay
{
    internal static void Run(string library, string journalDirectory, string referenceDirectory, string output, string mode, bool benchmark)
    {
        if (Directory.Exists(output)) throw new IOException("Use a new GPU replay output directory");
        using var reader = new RdpJournalReader(Path.Combine(journalDirectory, "journal.bin"));
        if (!reader.FromReset) throw new InvalidDataException("Native GPU replay requires a reset capture; warm state import is not implemented");
        if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(journalDirectory, "start.bin"))).AsSpan().SequenceEqual(reader.StateHash))
            throw new InvalidDataException("Wrong journal start state");
        var frames = new List<(byte[] Batch, byte[][] Hashes)>();
        using var packed = new MemoryStream();
        using var writer = new BinaryWriter(packed);
        while (!reader.Ended)
        {
            var (kind, payload) = reader.Next();
            if (kind is RdpJournal.Write or RdpJournal.Command or RdpJournal.Vi)
            { writer.Write((uint)kind); writer.Write((uint)payload.Length); writer.Write(payload); }
            else if (kind == RdpJournal.Checkpoint)
            {
                byte[] expected = File.ReadAllBytes(Path.Combine(referenceDirectory, $"frame-{frames.Count + 1:D4}.bin"));
                if (expected.Length != N64GpuBackend.RamSize + N64GpuBackend.HiddenSize + N64GpuBackend.TmemSize)
                    throw new InvalidDataException("Invalid reference memory size");
                // The independent oracle files use native word-swapped RDRAM
                // and halfword-swapped hidden bytes; ABI output is CPU order.
                for (int i = 0; i < N64GpuBackend.RamSize; i += 4)
                    BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(i, 4), BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(i, 4)));
                for (int i = N64GpuBackend.RamSize; i < N64GpuBackend.RamSize + N64GpuBackend.HiddenSize; i += 2)
                    (expected[i], expected[i + 1]) = (expected[i + 1], expected[i]);
                byte[][] hashes = {
                    SHA256.HashData(expected.AsSpan(0, N64GpuBackend.RamSize)),
                    SHA256.HashData(expected.AsSpan(N64GpuBackend.RamSize, N64GpuBackend.HiddenSize)),
                    SHA256.HashData(expected.AsSpan(N64GpuBackend.RamSize + N64GpuBackend.HiddenSize))
                };
                frames.Add((packed.ToArray(), hashes)); packed.SetLength(0); packed.Position = 0;
            }
        }
        Directory.CreateDirectory(output);
        var flags = N64GpuFlags.RequireDiscrete | (benchmark ? 0 : N64GpuFlags.Validate) | (mode switch {
            "strict" => 0, "batched" => N64GpuFlags.BatchStateWrites, "ranges" => N64GpuFlags.DeferDisjointWrites,
            _ => throw new ArgumentException("Invalid GPU replay mode")
        });
        var initialization = Stopwatch.StartNew();
        using var gpu = new N64GpuBackend(library, reader.Ram, reader.Hidden, flags);
        double initializationMs = initialization.Elapsed.TotalMilliseconds;
        Console.WriteLine($"gpuDevice={gpu.DeviceName} mode={mode} initializationMs={initializationMs:F3}");
        byte[] ram = new byte[N64GpuBackend.RamSize], hidden = new byte[N64GpuBackend.HiddenSize], tmem = new byte[N64GpuBackend.TmemSize];
        var measured = new List<double>(); var results = new List<object>();
        ulong lastBarriers = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var timer = Stopwatch.StartNew();
            ulong timeline = gpu.Submit(frames[i].Batch);
            gpu.Readback(timeline, ram, hidden, tmem);
            double elapsedMs = timer.Elapsed.TotalMilliseconds;
            // Hash and file IO are outside the transfer/render/readback timer.
            byte[][] actual = { SHA256.HashData(ram), SHA256.HashData(hidden), SHA256.HashData(tmem) };
            for (int component = 0; component < 3; component++)
                if (!actual[component].AsSpan().SequenceEqual(frames[i].Hashes[component]))
                    throw new InvalidDataException($"GPU/reference mismatch: frame {i + 1}, component {component}");
            N64GpuStats stats = gpu.GetStats();
            if (stats.ValidationErrors != 0 || stats.CompletedTimeline < timeline) throw new Exception("Unfinished or invalid GPU result");
            ulong barriers = stats.WriteBarriers - lastBarriers; lastBarriers = stats.WriteBarriers;
            measured.Add(elapsedMs);
            results.Add(new { frame = i + 1, elapsedMs, barriers, timeline, rdram = Convert.ToHexString(actual[0]), hidden = Convert.ToHexString(actual[1]), tmem = Convert.ToHexString(actual[2]) });
            Console.WriteLine($"managedGpuFrame={i + 1} exact=true totalMs={elapsedMs:F3} writeBarriers={barriers}");
        }
        N64GpuStats final = gpu.GetStats();
        var steady = measured.Skip(5).Order().ToArray();
        double? median = steady.Length == 0 ? null : (steady[(steady.Length - 1) / 2] + steady[steady.Length / 2]) * .5;
        var report = new {
            mode, benchmark, frames = frames.Count, initializationMs,
            submitTransferWaitReadbackTotalMs = measured.Sum(), afterFirstFiveMedianMs = median,
            final.Commands, final.WriteSpans, final.WriteBytes, final.WriteBarriers, final.ReadbackBytes, final.ValidationErrors,
            timing = "Includes managed/native submission, validation of batch, input copy/byte swap, GPU waits, all output copies/byte swaps and frame context advance; excludes parsing, reference loading, hashes, file IO and initialization",
            results
        };
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"managedGpuReplay=passed frames={frames.Count} writeBarriers={final.WriteBarriers} vulkanValidationErrors={final.ValidationErrors} afterFirstFiveMedianMs={median:F3}");
    }
}
#endif
