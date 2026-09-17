using System.Diagnostics;

namespace EutherDrive.Core.Arcade.Vegas;

// Deliberately a diagnostic build, not a GPU backend. Conditional calls disappear
// from normal builds, including the per-sample hook in the software renderer.
internal partial class VoodooBringupBackend
{
    private sealed record GpuSample(int Tmu, long S, long T, long W, int Lod,
        MameTextureTriangleState State, double Reciprocal, TextureRgba Expected);
    private List<GpuSample>? _gpuSamples;
    private int _gpuCaptureCount;
    private int _gpuCaptureEligible;

    [Conditional("GAUNTLET_GPU_CAPTURE")]
    private void BeginGpuSampleCapture(ref bool parallel, long boundingPixels)
    {
        string? directory = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory) || _gpuCaptureCount >= 8 || boundingPixels < 8192)
            return;
        int skip = int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_CAPTURE_SKIP"), out int n) ? n : 0;
        if (_gpuCaptureEligible++ < skip)
            return;
        if (ShouldTrackTextureSampleDiagnostics())
            throw new InvalidOperationException("GPU sampling capture requires sample diagnostics disabled");
        // One triangle has immutable texture RAM and TMU state. Record all sample
        // calls in deterministic order, without racing raster worker threads.
        parallel = false;
        _gpuSamples = new List<GpuSample>();
    }

    [Conditional("GAUNTLET_GPU_CAPTURE")]
    private void RecordGpuSample(int tmu, long s, long t, long w, int lod,
        in MameTextureTriangleState state, double reciprocal, TextureRgba expected)
        => _gpuSamples?.Add(new(tmu, s, t, w, lod, state, reciprocal, expected));

    private static uint PackGpuRgba(TextureRgba c)
        => (uint)c.R | (uint)c.G << 8 | (uint)c.B << 16 | (uint)c.A << 24;

    [Conditional("GAUNTLET_GPU_CAPTURE")]
    private void EndGpuSampleCapture()
    {
        if (_gpuSamples is not { } samples)
            return;
        _gpuSamples = null;
        if (samples.Count == 0)
            return;
        // Reject unsupported formats explicitly; never label a partial result PASS.
        if (samples.Any(s => s.State.Format is not (0 or 1 or 2 or 3 or 4 or 8 or 9 or 10 or 11 or 12 or 13) ||
            (s.State.Format is 1 or 9) && s.State.NccRgbaLut is null))
            throw new NotSupportedException("GPU sample capture encountered an unsupported texture format/NCC state");
        string directory = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_GPU_CAPTURE_DIR")!;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"triangle-{_gpuCaptureCount++:D2}.gsc");
        // GSC1: eight uint header words, raw texture words, 2x256 RGBA NCC LUT,
        // then 20 uint words/request. All numbers little-endian. Expected RGBA
        // is oracle-only; the shader must not read request word 15.
        using (var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew)))
        {
            foreach (uint word in new uint[] { 0x31435347, 1, (uint)_textureMemory.Length,
                512, 20, (uint)samples.Count, (uint)_renderFrame, 0 }) writer.Write(word);
            foreach (uint word in _textureMemory) writer.Write(word);
            for (int tmu = 0; tmu < 2; tmu++)
            {
                TextureRgba[]? lut = samples.FirstOrDefault(s => s.Tmu == tmu)?.State.NccRgbaLut;
                for (int i = 0; i < 256; i++) writer.Write(lut is null ? 0u : PackGpuRgba(lut[i]));
            }
            foreach (GpuSample sample in samples)
            {
                var state = sample.State;
                var layout = state.Layouts[sample.Lod];
                uint flags = (state.Perspective ? 1u : 0u) | (state.ClampNegativeW ? 2u : 0u) |
                    (state.Filtered ? 4u : 0u) |
                    (!_experimentTextureCoordinateWrap && (state.Mode & 0x40) != 0 ? 8u : 0u) |
                    (!_experimentTextureCoordinateWrap && (state.Mode & 0x80) != 0 ? 16u : 0u) |
                    (_fixTextureTOriginFlip ? 32u : 0u) | (state.Swap16BitBytes ? 64u : 0u) |
                    (_experimentReverse16BitTextureSampleLanes ? 128u : 0u) |
                    (_experimentReverse8BitTextureSampleLanes ? 256u : 0u);
                int memoryTmu = _experimentTmu1SampleTmu0Memory && sample.Tmu == 1 ? 0 : sample.Tmu;
                writer.Write(sample.S); writer.Write(sample.T); writer.Write(sample.W); writer.Write(sample.Reciprocal);
                writer.Write(flags); writer.Write(sample.Lod); writer.Write(layout.Width); writer.Write(layout.Height);
                writer.Write(layout.BaseAddress); writer.Write(state.Format); writer.Write(sample.Tmu * 256);
                writer.Write(PackGpuRgba(sample.Expected));
                writer.Write(_experimentSeparateTmuTextureMemory ? (uint)(memoryTmu * TextureBankBytes) : 0u);
                writer.Write(_experimentSeparateTmuTextureMemory ? TextureBankBytes - 1 : TextureBytes - 1);
                writer.Write(state.Mode); writer.Write(0);
            }
        }
        // Replay ONLY captured sampler calls, no MIPS execution. Compare the actual
        // software sampler (not a translated C++ facsimile) against the capture.
        uint[] output = new uint[samples.Count];
        void Range(int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                GpuSample s = samples[i];
                output[i] = PackGpuRgba(SampleTextureMameFixedForTmu(s.Tmu, s.S, s.T, s.W,
                    s.Lod, s.State, s.Reciprocal));
            }
        }
        double Measure(bool parallel)
        {
            var times = new List<double>();
            for (int repeat = 0; repeat < 12; repeat++)
            {
                var clock = Stopwatch.StartNew();
                if (parallel)
                    DynamicRasterWorkerPool.For(0, (samples.Count + 511) / 512, 8,
                        block => Range(block * 512, Math.Min(samples.Count, (block + 1) * 512)));
                else Range(0, samples.Count);
                clock.Stop();
                for (int i = 0; i < output.Length; i++)
                    if (output[i] != PackGpuRgba(samples[i].Expected))
                        throw new InvalidOperationException($"CPU sampling replay mismatch at {i}");
                if (repeat >= 3) times.Add(clock.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return times[times.Count / 2];
        }
        Console.WriteLine($"gpuSampleCapture path={path} frame={_renderFrame} samples={samples.Count} " +
            $"formats={string.Join(',', samples.Select(s => s.State.Format).Distinct())} " +
            $"cpuSerialMedianMs={Measure(false):F4} cpu8MedianMs={Measure(true):F4} oracle=PASS");
    }
}
