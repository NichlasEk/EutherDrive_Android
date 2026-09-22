#if N64_LIVE_GPU
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryu64.MIPS
{
    public interface IRdpGpuRenderer : IDisposable
    {
        void Command(ReadOnlySpan<uint> words);
        void RdramWritten(uint address, uint length);
        void Synchronize();
        string Status { get; }
    }

    public partial class Memory
    {
        public IRdpGpuRenderer GpuRenderer { get; private set; }
        public byte[] GpuHiddenBits => _rdpHiddenBits;
        private readonly bool[] _gpuPendingPages = new bool[2048];
        private readonly List<Tuple<uint, uint>> _gpuPendingRanges = new List<Tuple<uint, uint>>();
        private bool _gpuPending, _gpuApplying;
        private int _gpuThread;
        private uint _gpuScissorHeight = 1024;
        private readonly object _gpuFrameLock = new object();
        private sealed class GpuFrame
        {
            public uint Address, Width, Bpp;
            public byte[] Pixels;
        }
        private readonly Dictionary<ulong, GpuFrame> _gpuTargets = new Dictionary<ulong, GpuFrame>();
        private readonly List<GpuFrame> _gpuCompleted = new List<GpuFrame>();
        private byte[] _gpuLastPixels = Array.Empty<byte>();
        private int _gpuLastWidth, _gpuLastHeight, _gpuLastBpp;
        public long GpuReadHazards { get; private set; }
        public long GpuFrames { get; private set; }
        private readonly long[] _gpuHazardPages = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_GPU_AUDIT") == "1" ? new long[2048] : null;
        public string GpuHazardSummary => _gpuHazardPages == null ? "gpuHazardAudit=off" :
            "gpuHazardPages=" + string.Join(",", _gpuHazardPages.Select((count, page) => new { count, page })
                .Where(p => p.count != 0).OrderByDescending(p => p.count).Take(10).Select(p => $"{p.page << 12:x6}:{p.count}"))
            + $" color={_rdpColorImageAddress:x6} depth={_rdpMaskImageAddress:x6}";

        public void AttachGpu(IRdpGpuRenderer renderer)
        {
            if (GpuRenderer != null || _rdpCommandCount != 0)
                throw new InvalidOperationException("GPU attachment requires a fresh ROM reset");
            GpuRenderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        }

        // Caller must first join the emulation thread. Old software saves may
        // then load normally; an in-flight GPU state must never be saved as v7.
        public void DetachGpu()
        {
            var renderer = GpuRenderer; GpuRenderer = null;
            renderer?.Dispose(); _gpuPending = false;
            Array.Clear(_gpuPendingPages, 0, _gpuPendingPages.Length);
            _gpuPendingRanges.Clear();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void GpuBeforeRead(uint address, uint length)
        {
            if (!_gpuPending || length == 0 || address >= RDRAM.Length) return;
            uint end = (uint)Math.Min((ulong)RDRAM.Length - 1, (ulong)address + length - 1);
            uint firstPage = address >> 12, lastPage = end >> 12;
            // Keep the overwhelmingly common code/data load small enough to
            // inline. It needs no thread lookup or range enumeration when its
            // single page has no pending GPU output.
            if (firstPage == lastPage && !_gpuPendingPages[firstPage]) return;
            GpuSynchronizeForRead(address, end, firstPage, lastPage);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void GpuSynchronizeForRead(uint address, uint end, uint firstPage, uint lastPage)
        {
            // UI/debug readers consume published snapshots and must never run
            // an emulator-side synchronization concurrently with the CPU.
            if (_gpuThread != Environment.CurrentManagedThreadId) return;
            for (uint page = firstPage; page <= lastPage; page++)
                if (_gpuPendingPages[page])
                {
                    // Depth can start at 0x400 on the same page as exception
                    // vectors at 0x180. The page test is only an early reject.
                    bool overlaps = false;
                    foreach (var range in _gpuPendingRanges)
                        if (address < range.Item2 && end >= range.Item1) { overlaps = true; break; }
                    if (!overlaps) return;
                    GpuReadHazards++;
                    if (_gpuHazardPages != null) _gpuHazardPages[page]++;
                    GpuRenderer.Synchronize(); return;
                }
        }

        public void GpuExternalWrite(uint address, uint length)
        {
            if (!_gpuApplying) GpuRenderer?.RdramWritten(address, length);
        }

        public void GpuReadbackCompleted()
        {
            _gpuApplying = true;
            try
            {
                for (uint page = 0; page < _gpuPendingPages.Length; page++)
                    if (_gpuPendingPages[page]) NoteRdramWriteRange(page << 12, 4096);
            }
            finally { _gpuApplying = false; }
            Array.Clear(_gpuPendingPages, 0, _gpuPendingPages.Length); _gpuPending = false;
            _gpuPendingRanges.Clear();
        }

        private void GpuMarkTarget(uint address, uint bytes)
        {
            uint start = address & 0x7ffffcu;
            uint end = start + bytes + 4;
            GpuMarkRange(start, Math.Min(end, 0x800000u));
            if (end > 0x800000u)
                GpuMarkRange(0, end - 0x800000u);
            _gpuPending = true;
        }

        private void GpuMarkRange(uint start, uint end)
        {
            for (int i = 0; i < _gpuPendingRanges.Count; i++)
            {
                var range = _gpuPendingRanges[i];
                if (start >= range.Item1 && end <= range.Item2) return;
                if (start <= range.Item2 && end >= range.Item1)
                {
                    start = Math.Min(start, range.Item1); end = Math.Max(end, range.Item2);
                    _gpuPendingRanges.RemoveAt(i--);
                }
            }
            _gpuPendingRanges.Add(Tuple.Create(start, end));
            for (uint p = start >> 12; p <= (end - 1) >> 12; p++) _gpuPendingPages[p] = true;
        }

        private void GpuCommand(uint address, bool xbus, int length, int op)
        {
            _gpuThread = Environment.CurrentManagedThreadId;
            Span<uint> words = stackalloc uint[length];
            for (int i = 0; i < length; i++) words[i] = ReadRdpCommandWord(address + (uint)i * 4, xbus);
#if N64_RDP_JOURNAL
            if (RdpJournal != null) throw new InvalidOperationException("Live GPU and software journal capture cannot be combined");
#endif
            GpuRenderer.Command(words);
            if (op == 0x2d) _gpuScissorHeight = ((words[1] & 0xfff) + 3) >> 2;
            if ((op >= 8 && op <= 15) || op == 0x24 || op == 0x25 || op == 0x36)
            {
                uint width = Math.Min(1024u, _rdpColorImageWidth), height = Math.Min(1024u, _gpuScissorHeight);
                GpuMarkTarget(_rdpColorImageAddress, width * height * RdpBytesPerPixel(_rdpColorImageSize));
                GpuMarkTarget(_rdpMaskImageAddress, width * height * 2);
                uint bpp = RdpBytesPerPixel(_rdpColorImageSize), target = _rdpColorImageAddress & 0x7ffffcu;
                ulong key = target | ((ulong)width << 24) | ((ulong)bpp << 40);
                if (!_gpuTargets.ContainsKey(key)) _gpuTargets[key] = new GpuFrame { Address = target, Width = width, Bpp = bpp };
                MarkRdpColorImageWritten(bpp);
            }
            // Completion precedes the existing MI DP interrupt, not DPC_END.
            if (op == 0x29) { GpuRenderer.Synchronize(); GpuPublishFramebuffer(); }
        }

        private void GpuPublishFramebuffer()
        {
            // Only FULL_SYNC publishes. SetColorImage often switches to a
            // depth clear or an unfinished back buffer; neither is scanout.
            if (_gpuTargets.Count == 0) return;
            uint height = Math.Min(576u, GetFramebufferHeightHint() + 8);
            lock (_gpuFrameLock)
            {
                foreach (var target in _gpuTargets.Values)
                {
                    if (target.Width == 0 || target.Bpp < 2) continue;
                    uint rows = Math.Min(height, ((uint)RDRAM.Length - target.Address) / (target.Width * target.Bpp));
                    if (rows == 0) continue;
                    target.Pixels = new byte[target.Width * rows * target.Bpp];
                    Buffer.BlockCopy(RDRAM, (int)target.Address, target.Pixels, 0, target.Pixels.Length);
                    _gpuCompleted.RemoveAll(f => f.Address == target.Address && f.Width == target.Width && f.Bpp == target.Bpp);
                    _gpuCompleted.Add(target);
                }
                while (_gpuCompleted.Count > 8) _gpuCompleted.RemoveAt(0);
            }
            _gpuTargets.Clear(); GpuFrames++;
        }

        public bool TryGetGpuFramebuffer(out byte[] pixels, out int width, out int height, out int bpp)
        {
            lock (_gpuFrameLock)
            {
                uint origin = ReadBigEndianWord(VI_ORIGIN_REG_RW) & 0x7fffffu;
                uint viWidth = ReadBigEndianWord(VI_WIDTH_REG_RW) & 0xfffu;
                uint type = ReadBigEndianWord(VI_STATUS_REG_RW) & 3u;
                uint viBpp = type == 3 ? 4u : type == 2 ? 2u : 0u;
                uint rows = GetFramebufferHeightHint();
                for (int i = _gpuCompleted.Count - 1; i >= 0; i--)
                {
                    var frame = _gpuCompleted[i];
                    if (frame.Width != viWidth || frame.Bpp != viBpp || origin < frame.Address) continue;
                    ulong offset = origin - frame.Address, bytes = (ulong)viWidth * rows * viBpp;
                    if (offset + bytes > (ulong)frame.Pixels.Length) continue;
                    _gpuLastPixels = new byte[(int)bytes];
                    Buffer.BlockCopy(frame.Pixels, (int)offset, _gpuLastPixels, 0, (int)bytes);
                    _gpuLastWidth = (int)viWidth; _gpuLastHeight = (int)rows; _gpuLastBpp = (int)viBpp;
                    break;
                }
                // Hold the last completed VI-selected image while a newly
                // selected buffer is still rendering. Never read live RAM here.
                pixels = (byte[])_gpuLastPixels.Clone(); width = _gpuLastWidth; height = _gpuLastHeight; bpp = _gpuLastBpp;
                return pixels.Length != 0;
            }
        }
    }
}
#endif
