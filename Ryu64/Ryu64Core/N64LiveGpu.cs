#if N64_LIVE_GPU
using System;
using System.IO;
using System.Numerics;
using System.Diagnostics;
using Ryu64.MIPS;

namespace Ryu64Core
{
    // One emulation thread stages immutable command/input batches. The GPU is
    // lazy-created at the first RDP command, after boot-time RAM initialization.
    public sealed class N64LiveGpu : IRdpGpuRenderer
    {
        private readonly Memory _memory;
        private readonly string _library;
        private readonly N64GpuFlags _flags;
        private N64GpuBackend _gpu;
        private readonly MemoryStream _batch = new MemoryStream();
        private readonly BinaryWriter _writer;
        private readonly ulong[] _dirty = new ulong[N64GpuBackend.RamSize / 64];
        private readonly ulong[] _pages = new ulong[N64GpuBackend.RamSize / (4096 * 64)];
        private uint _pageGroups;
        private readonly byte[] _tmem = new byte[4096];
        private byte[] _auditShadow;
        private ulong[] _auditWrites;
        private long _syncs, _ticks;
        private string _status = "gpu=waiting-for-first-command";
        public string Status => _status;

        public N64LiveGpu(Memory memory, string library, bool validate)
        {
            if (!File.Exists(library)) throw new FileNotFoundException("N64 GPU library not found", library);
            _memory = memory; _library = library;
            _flags = N64GpuFlags.RequireDiscrete | N64GpuFlags.DeferDisjointLoadBlocks | (validate ? N64GpuFlags.Validate : 0);
            _writer = new BinaryWriter(_batch);
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_GPU_AUDIT") == "1")
                _auditWrites = new ulong[_dirty.Length];
        }

        public void RdramWritten(uint address, uint length)
        {
            if (_gpu == null || length == 0 || address >= N64GpuBackend.RamSize) return;
            uint end = (uint)Math.Min((ulong)N64GpuBackend.RamSize, (ulong)address + length);
            for (uint p = address >> 12; p <= (end - 1) >> 12; p++)
            {
                _pages[p >> 6] |= 1UL << (int)(p & 63);
                _pageGroups |= 1u << (int)(p >> 6);
            }
            while (address < end)
            {
                int bit = (int)(address & 63), count = (int)Math.Min(end - address, (uint)(64 - bit));
                ulong mask = (ulong.MaxValue >> (64 - count)) << bit;
                _dirty[address >> 6] |= mask;
                if (_auditWrites != null) _auditWrites[address >> 6] |= mask;
                address += (uint)count;
            }
        }

        private void Writes()
        {
            // Enumerate only dirty pages, in the same address order as the
            // old full scan. CPU-byte ownership and command order are unchanged.
            uint groups = _pageGroups;
            _pageGroups = 0;
            while (groups != 0)
            {
                int group = BitOperations.TrailingZeroCount(groups);
                groups &= groups - 1;
                ulong pages = _pages[group];
                _pages[group] = 0;
                while (pages != 0)
                {
                    int page = group * 64 + BitOperations.TrailingZeroCount(pages);
                    pages &= pages - 1;
                    for (int word = page * 64; word < (page + 1) * 64; word++)
                    {
                        ulong bits = _dirty[word]; _dirty[word] = 0;
                        while (bits != 0)
                        {
                            int begin = BitOperations.TrailingZeroCount(bits);
                            int count = BitOperations.TrailingZeroCount(~(bits >> begin));
                            int offset = word * 64 + begin;
                            _writer.Write(1u); _writer.Write((uint)count + 4); _writer.Write((uint)offset);
                            _writer.Write(_memory.RDRAM, offset, count);
                            bits &= ~((ulong.MaxValue >> (64 - count)) << begin);
                        }
                    }
                }
            }
        }

        public void Command(ReadOnlySpan<uint> words)
        {
            if (_gpu == null)
            {
                try { _gpu = new N64GpuBackend(_library, _memory.RDRAM, _memory.GpuHiddenBits, _flags); }
                catch (Exception ex) { _status = "gpu=FAILED " + ex.Message; throw; }
                if (_auditWrites != null) _auditShadow = (byte[])_memory.RDRAM.Clone();
                _status = "gpu=" + _gpu.DeviceName;
                Console.WriteLine("[N64 GPU] live backend: " + _gpu.DeviceName);
            }
            // A bounded queue also handles command streams without FULL_SYNC.
            // A worst-case alternating-byte patch adds 52 MiB of records;
            // leave enough space below the native 64-MiB envelope limit.
            if (_batch.Length > 8 * 1024 * 1024) Synchronize();
            Writes(); _writer.Write(2u); _writer.Write((uint)words.Length * 4);
            foreach (uint word in words) _writer.Write(word);
        }

        public void Synchronize()
        {
            if (_gpu == null) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                // Audit live CPU writes independently of staging. Check once
                // per synchronization, including stores made between commands.
                if (_auditShadow != null)
                    for (int word = 0; word < _auditWrites.Length; word++)
                    {
                        int offset = word * 64;
                        if (_auditShadow.AsSpan(offset, 64).SequenceEqual(_memory.RDRAM.AsSpan(offset, 64))) continue;
                        for (int bit = 0; bit < 64; bit++)
                            if (_auditShadow[offset + bit] != _memory.RDRAM[offset + bit] && (_auditWrites[word] & (1UL << bit)) == 0)
                                throw new InvalidOperationException($"Untracked CPU RDRAM write at {offset + bit:x6}");
                    }
                Writes();
                if (_batch.Length == 0) return;
                ulong token = _gpu.Submit(_batch.GetBuffer().AsSpan(0, checked((int)_batch.Length)));
                _gpu.Readback(token, _memory.RDRAM, _memory.GpuHiddenBits, _tmem);
                _batch.SetLength(0); _batch.Position = 0;
                _memory.GpuReadbackCompleted();
                if (_auditShadow != null)
                {
                    _memory.RDRAM.CopyTo(_auditShadow, 0);
                    Array.Clear(_auditWrites, 0, _auditWrites.Length);
                }
                _syncs++; _ticks += Stopwatch.GetTimestamp() - start;
                var stats = _gpu.GetStats();
                _status = $"gpu=Vulkan cmds={stats.Commands} syncs={_syncs} readHazards={_memory.GpuReadHazards} frames={_memory.GpuFrames} writeBarriers={stats.WriteBarriers} writeBytes={stats.WriteBytes} waitCopyRenderMs={_ticks * 1000.0 / Stopwatch.Frequency:F1} errors={stats.ValidationErrors}";
            }
            catch (Exception ex) { _status = "gpu=FAILED " + ex.Message; throw; }
        }

        public void Dispose() { _gpu?.Dispose(); _gpu = null; _writer.Dispose(); _batch.Dispose(); }
    }
}
#endif
