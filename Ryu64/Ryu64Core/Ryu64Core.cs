using Ryu64.Formats;
using Ryu64.MIPS;
using Ryu64.Common;
using System;

namespace Ryu64Core
{
    public class Ryu64Core
    {
        private const uint ViStatusReg = 0xA4400000;
        private const uint ViOriginReg = 0xA4400004;
        private const uint ViWidthReg = 0xA4400008;
        private const uint ViVStartReg = 0xA4400028;
        private const int RdramSizeBytes = 8 * 1024 * 1024;
        private static readonly bool EnableFramebufferOriginScanFallback =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FB_SCAN_FALLBACK"), "0", StringComparison.Ordinal);

        private const uint AiDramAddrReg = 0xA4500000;
        private const uint AiLenReg = 0xA4500004;
        private const uint AiDacRateReg = 0xA4500010;
        private const uint MiIntrReg = 0xA4300008;
        private const uint MiIntrMaskReg = 0xA430000C;
        private const uint PiStatusReg = 0xA4600010;
        private const uint SiDramAddrReg = 0xA4800000;
        private const uint SiPifAddrRd64bReg = 0xA4800004;
        private const uint SiPifAddrWr64bReg = 0xA4800010;
        private const uint SiStatusReg = 0xA4800018;
        private const uint PifRamStatusByte = 0xBFC007FF;

        private Z64 rom;
        private bool isRunning = false;

        private uint _lastAudioAddress;
        private uint _lastAudioLength;
        private uint _lastAudioDacrate;
        private string _lastFramebufferStatus = "Not started";
        private uint _lastFallbackFramebufferOrigin;

        public event EventHandler<FramebufferUpdatedEventArgs> FramebufferUpdated;
        public event EventHandler<AudioBufferEventArgs> AudioBufferReady;
        public event EventHandler<EmulationStateChangedEventArgs> StateChanged;

        public bool IsRunning => isRunning;
        public string GameName => rom?.Name?.Trim() ?? "No ROM loaded";
        public string LastFramebufferStatus => _lastFramebufferStatus;
        public string LastExecutionStatus
        {
            get
            {
                if (!isRunning)
                    return "Core not running";
                if (R4300.memory == null)
                    return "Memory not initialized";

                try
                {
                    static uint SafeReadWord(uint addr)
                    {
                        try { return R4300.memory.ReadUInt32(addr); }
                        catch { return 0; }
                    }

                    uint pc = R4300.GetCurrentPc();
                    ulong cycles = R4300.GetCycleCounter();
                    long unknown = R4300.GetUnknownOpcodeCount();
                    uint viStatus = R4300.memory.ReadUInt32(ViStatusReg);
                    uint viOrigin = R4300.memory.ReadUInt32(ViOriginReg) & 0x00FFFFFF;
                    uint viWidth = R4300.memory.ReadUInt32(ViWidthReg) & 0x0FFF;
                    uint aiLen = R4300.memory.ReadUInt32(AiLenReg) & 0x3FFF8;
                    uint miIntr = R4300.memory.ReadUInt32(MiIntrReg);
                    uint miMask = R4300.memory.ReadUInt32(MiIntrMaskReg);
                    uint piStatus = R4300.memory.ReadUInt32(PiStatusReg);
                    uint siStatus = R4300.memory.ReadUInt32(SiStatusReg);
                    uint siDram = R4300.memory.ReadUInt32(SiDramAddrReg);
                    uint siRd64 = R4300.memory.ReadUInt32(SiPifAddrRd64bReg);
                    uint siWr64 = R4300.memory.ReadUInt32(SiPifAddrWr64bReg);
                    uint pifCtrl = R4300.memory.ReadUInt8(PifRamStatusByte);
                    uint piDram = R4300.memory.ReadUInt32(0xA4600000);
                    uint piCart = R4300.memory.ReadUInt32(0xA4600004);
                    uint piRdLen = R4300.memory.ReadUInt32(0xA4600008);
                    uint piWrLen = R4300.memory.ReadUInt32(0xA460000C);
                    uint op = 0;
                    try { op = R4300.memory.ReadUInt32(pc); } catch { }
                    ulong cop0Status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
                    ulong cop0Cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
                    ulong cop0BadVaddr = Registers.COP0.Reg[Registers.COP0.BADVADDR_REG];
                    ulong cop0Epc = Registers.COP0.Reg[Registers.COP0.EPC_REG];
                    uint epc = (uint)cop0Epc;
                    uint epcOp = 0;
                    uint epcPrevOp = 0;
                    uint epcNextOp = 0;
                    try { epcOp = R4300.memory.ReadUInt32(epc); } catch { }
                    try { epcPrevOp = R4300.memory.ReadUInt32(epc - 4); } catch { }
                    try { epcNextOp = R4300.memory.ReadUInt32(epc + 4); } catch { }
                    ulong t6 = Registers.R4300.Reg[14];
                    ulong t7 = Registers.R4300.Reg[15];
                    ulong t8 = Registers.R4300.Reg[24];
                    ulong t9 = Registers.R4300.Reg[25];
                    ulong t0 = Registers.R4300.Reg[8];
                    ulong t1 = Registers.R4300.Reg[9];
                    ulong v0 = Registers.R4300.Reg[2];
                    ulong v1 = Registers.R4300.Reg[3];
                    ulong a0 = Registers.R4300.Reg[4];
                    ulong a1 = Registers.R4300.Reg[5];
                    ulong ra = Registers.R4300.Reg[31];
                    uint a0w = SafeReadWord((uint)a0);
                    uint a0w4 = SafeReadWord((uint)a0 + 4u);
                    uint v0w = SafeReadWord((uint)v0);
                    uint v0w4 = SafeReadWord((uint)v0 + 4u);
                    return $"pc=0x{pc:x8} op=0x{op:x8} epc=0x{epc:x8} epcPrev=0x{epcPrevOp:x8} epcOp=0x{epcOp:x8} epcNext=0x{epcNextOp:x8} cycles={cycles} unk={unknown} viStatus=0x{viStatus:x8} viOrigin=0x{viOrigin:x8} viWidth={viWidth} aiLen=0x{aiLen:x} miIntr=0x{miIntr:x8} miMask=0x{miMask:x8} piStatus=0x{piStatus:x8} piDram=0x{piDram:x8} piCart=0x{piCart:x8} piRdLen=0x{piRdLen:x8} piWrLen=0x{piWrLen:x8} siStatus=0x{siStatus:x8} siDram=0x{siDram:x8} siRd=0x{siRd64:x8} siWr=0x{siWr64:x8} pifCtl=0x{pifCtrl:x2} cop0Status=0x{cop0Status:x8} cop0Cause=0x{cop0Cause:x8} badv=0x{cop0BadVaddr:x8} v0=0x{v0:x16} v1=0x{v1:x16} a0=0x{a0:x16} a1=0x{a1:x16} [a0]=0x{a0w:x8} [a0+4]=0x{a0w4:x8} [v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} t0=0x{t0:x16} t1=0x{t1:x16} t6=0x{t6:x16} t7=0x{t7:x16} t8=0x{t8:x16} t9=0x{t9:x16} ra=0x{ra:x16}";
                }
                catch (Exception ex)
                {
                    return $"Execution snapshot failed: {ex.Message}";
                }
            }
        }

        public void LoadROM(string romPath)
        {
            if (isRunning)
            {
                Stop();
            }

            rom = new Z64(romPath);
            rom.Parse();

            if (!rom.HasBeenParsed)
            {
                throw new InvalidOperationException("Can't open ROM, it's either a bad ROM or it is in Little Endian (byte swapping not implemented yet).");
            }

            if (!System.IO.Directory.Exists(Variables.AppdataFolder))
            {
                System.IO.Directory.CreateDirectory(Variables.AppdataFolder);
                System.IO.Directory.CreateDirectory($"{Variables.AppdataFolder}/saves");
            }

            Settings.Parse($"{AppDomain.CurrentDomain.BaseDirectory}/Settings.ini");
            R4300.memory = new Memory(rom.AllData);
            _lastAudioAddress = 0;
            _lastAudioLength = 0;
            _lastAudioDacrate = 0;
            _lastFramebufferStatus = "ROM loaded";
        }

        public void Start()
        {
            if (rom == null)
            {
                throw new InvalidOperationException("No ROM loaded. Call LoadROM first.");
            }

            if (isRunning)
            {
                return;
            }

            R4300.PowerOnR4300();
            isRunning = true;
            StateChanged?.Invoke(this, new EmulationStateChangedEventArgs(true));
        }

        public void Stop()
        {
            if (!isRunning)
            {
                return;
            }

            R4300.StopR4300();
            isRunning = false;
            StateChanged?.Invoke(this, new EmulationStateChangedEventArgs(false));
        }

        public ulong GetCycleCounter()
        {
            return R4300.GetCycleCounter();
        }

        public void Pause()
        {
            // Note: Ryu64 doesn't have built-in pause functionality
            // You would need to implement this by controlling the CPU thread
        }

        public void Resume()
        {
            // Resume emulation if paused
        }

        public byte[] GetFramebuffer()
        {
            if (TryGetFramebuffer(out byte[] framebuffer, out int width, out int height, out int bytesPerPixel))
            {
                FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                return framebuffer;
            }

            return null;
        }

        public bool TryGetFramebuffer(out byte[] framebuffer, out int width, out int height, out int bytesPerPixel)
        {
            framebuffer = Array.Empty<byte>();
            width = 0;
            height = 0;
            bytesPerPixel = 0;

            if (!isRunning || R4300.memory == null)
            {
                _lastFramebufferStatus = "Core not running or memory not ready";
                return false;
            }

            try
            {
                uint status = R4300.memory.ReadUInt32(ViStatusReg);
                uint origin = R4300.memory.ReadUInt32(ViOriginReg) & 0x00FFFFFF;

                width = (int)(R4300.memory.ReadUInt32(ViWidthReg) & 0x0FFF);
                if (width <= 0)
                    width = 320;

                uint vStart = R4300.memory.ReadUInt32(ViVStartReg);
                height = InferVideoHeight(vStart);
                if (height <= 0)
                    height = 240;

                int viType = (int)(status & 0x3);
                if (viType == 2)
                    bytesPerPixel = 2;
                else if (viType == 3)
                    bytesPerPixel = 4;
                else
                {
                    _lastFramebufferStatus = $"VI mode not active (status=0x{status:x8}, viType={viType})";
                    return false;
                }

                if (width > 640) width = 640;
                if (height > 480) height = 480;
                if (origin >= RdramSizeBytes)
                {
                    _lastFramebufferStatus = $"VI origin out of RDRAM (origin=0x{origin:x8})";
                    return false;
                }

                // Bring-up fallback: some paths still produce obviously invalid VI origins
                // (for example 0x0000027f). Scan RDRAM for a stronger framebuffer candidate.
                if (EnableFramebufferOriginScanFallback
                    && origin < 0x00001000u
                    && bytesPerPixel > 0)
                {
                    uint best = FindBestFramebufferOrigin(width, height, bytesPerPixel, origin, out int bestScore, out int viScore);
                    if (best != origin)
                    {
                        _lastFallbackFramebufferOrigin = best;
                        origin = best;
                        _lastFramebufferStatus =
                            $"Fallback VI origin used (vi=0x{(R4300.memory.ReadUInt32(ViOriginReg) & 0x00FFFFFF):x8} -> fb=0x{origin:x8}, score={bestScore}, viScore={viScore})";
                    }
                }

                int bufferSize = checked(width * height * bytesPerPixel);
                if (bufferSize <= 0)
                {
                    _lastFramebufferStatus = "Computed framebuffer size <= 0";
                    return false;
                }
                if ((long)origin + bufferSize > RdramSizeBytes)
                {
                    _lastFramebufferStatus = $"Framebuffer range out of RDRAM (origin=0x{origin:x8}, size=0x{bufferSize:x})";
                    return false;
                }

                framebuffer = new byte[bufferSize];
                for (int i = 0; i < bufferSize; i++)
                {
                    framebuffer[i] = R4300.memory.ReadUInt8PhysicalUncached(origin + (uint)i);
                }

                FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(framebuffer, (uint)width, (uint)height, (uint)bytesPerPixel));
                if (string.IsNullOrEmpty(_lastFramebufferStatus) || !_lastFramebufferStatus.StartsWith("Fallback VI origin used", StringComparison.Ordinal))
                    _lastFramebufferStatus = $"OK viType={viType} origin=0x{origin:x8} size={width}x{height} bpp={bytesPerPixel}";
                return true;
            }
            catch (Exception ex)
            {
                framebuffer = Array.Empty<byte>();
                width = 0;
                height = 0;
                bytesPerPixel = 0;
                _lastFramebufferStatus = $"Exception while reading framebuffer: {ex.Message}";
                return false;
            }
        }

        public short[] GetAudioSamples(out uint sampleRate, out uint channels)
        {
            sampleRate = 44100;
            channels = 2;

            if (!isRunning || R4300.memory == null)
                return Array.Empty<short>();

            try
            {
                uint len = R4300.memory.ReadUInt32(AiLenReg) & 0x3FFF8;
                uint addr = R4300.memory.ReadUInt32(AiDramAddrReg) & 0x00FFFFFF;
                uint dacRate = R4300.memory.ReadUInt32(AiDacRateReg) & 0x3FFF;

                if (dacRate != 0)
                {
                    const double N64NtscClock = 48681812.0;
                    int rate = (int)Math.Round(N64NtscClock / (dacRate + 1.0));
                    if (rate < 4000) rate = 4000;
                    if (rate > 96000) rate = 96000;
                    sampleRate = (uint)rate;
                }

                if (len < 4 || addr == 0)
                    return Array.Empty<short>();

                if (addr == _lastAudioAddress && len == _lastAudioLength && dacRate == _lastAudioDacrate)
                    return Array.Empty<short>();

                _lastAudioAddress = addr;
                _lastAudioLength = len;
                _lastAudioDacrate = dacRate;

                int sampleCount = (int)(len / 2);
                short[] pcm = new short[sampleCount];

                uint readPtr = addr;
                for (int i = 0; i < sampleCount; i++)
                {
                    byte hi = R4300.memory.ReadUInt8PhysicalUncached(readPtr++);
                    byte lo = R4300.memory.ReadUInt8PhysicalUncached(readPtr++);
                    pcm[i] = (short)((hi << 8) | lo);
                }

                AudioBufferReady?.Invoke(this, new AudioBufferEventArgs(ShortToByteArray(pcm), sampleRate, channels));
                return pcm;
            }
            catch
            {
                return Array.Empty<short>();
            }
        }

        public void SetInputState(InputState input)
        {
            // TODO: Map to PIF RAM command protocol for real controller support.
            _ = input;
        }

        public void SaveState(string path)
        {
            // Save emulator state to file
        }

        public void LoadState(string path)
        {
            // Load emulator state from file
        }

        private static int InferVideoHeight(uint vStart)
        {
            int start = (int)((vStart >> 16) & 0x03FF);
            int end = (int)(vStart & 0x03FF);
            int delta = end - start;
            if (delta <= 0)
                delta += 0x400;

            // VI V_START is encoded in half-lines on real hardware.
            int height = delta / 2;
            if (height < 120 || height > 576)
                return 240;
            return height;
        }

        private int ScoreFramebufferCandidate(uint origin, int width, int height, int bytesPerPixel)
        {
            int bufferSize = checked(width * height * bytesPerPixel);
            if (bufferSize <= 0 || (long)origin + bufferSize > RdramSizeBytes)
                return int.MinValue;

            int sampleCols = Math.Min(64, Math.Max(1, width));
            int sampleRows = Math.Min(48, Math.Max(1, height));
            int stepX = Math.Max(1, width / sampleCols);
            int stepY = Math.Max(1, height / sampleRows);

            int nonZero = 0;
            int sameLeft = 0;
            int sameUp = 0;
            int hugeDiff = 0;
            ushort[] prevRow = new ushort[sampleCols];
            bool prevRowValid = false;

            for (int sy = 0; sy < sampleRows; sy++)
            {
                int y = Math.Min(height - 1, sy * stepY);
                ushort left = 0;
                bool leftValid = false;

                for (int sx = 0; sx < sampleCols; sx++)
                {
                    int x = Math.Min(width - 1, sx * stepX);
                    uint pixelOffset = (uint)((y * width + x) * bytesPerPixel);
                    ushort pixel;
                    if (bytesPerPixel >= 2)
                    {
                        byte hi = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset);
                        byte lo = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset + 1u);
                        pixel = (ushort)((hi << 8) | lo);
                    }
                    else
                    {
                        byte value = R4300.memory.ReadUInt8PhysicalUncached(origin + pixelOffset);
                        pixel = (ushort)(value << 8);
                    }

                    if (pixel != 0)
                        nonZero++;

                    if (leftValid)
                    {
                        if (pixel == left)
                            sameLeft++;
                        else if (Math.Abs(pixel - left) > 0x1800)
                            hugeDiff++;
                    }

                    if (prevRowValid)
                    {
                        ushort up = prevRow[sx];
                        if (pixel == up)
                            sameUp++;
                        else if (Math.Abs(pixel - up) > 0x1800)
                            hugeDiff++;
                    }

                    prevRow[sx] = pixel;
                    left = pixel;
                    leftValid = true;
                }

                prevRowValid = true;
            }

            // Favor coherent images and penalize noisy/high-frequency regions.
            return (nonZero * 5) + (sameLeft * 3) + (sameUp * 3) - (hugeDiff * 2);
        }

        private uint FindBestFramebufferOrigin(int width, int height, int bytesPerPixel, uint viOrigin, out int bestScore, out int viScore)
        {
            int bufferSize = checked(width * height * bytesPerPixel);
            viScore = ScoreFramebufferCandidate(viOrigin, width, height, bytesPerPixel);
            bestScore = viScore;
            uint bestOrigin = viOrigin;

            // Prefer reusing previous good fallback if still plausible.
            if (_lastFallbackFramebufferOrigin != 0)
            {
                int lastScore = ScoreFramebufferCandidate(_lastFallbackFramebufferOrigin, width, height, bytesPerPixel);
                if (lastScore > bestScore)
                {
                    bestScore = lastScore;
                    bestOrigin = _lastFallbackFramebufferOrigin;
                }
            }

            for (uint candidate = 0x001000u; (long)candidate + bufferSize <= RdramSizeBytes; candidate += 0x2000u)
            {
                int score = ScoreFramebufferCandidate(candidate, width, height, bytesPerPixel);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOrigin = candidate;
                }
            }

            // Refine around the strongest coarse hit with smaller alignment steps.
            uint refineStart = bestOrigin >= 0x4000u ? bestOrigin - 0x4000u : 0x001000u;
            uint refineEnd = Math.Min((uint)(RdramSizeBytes - bufferSize), bestOrigin + 0x4000u);
            for (uint candidate = refineStart; candidate <= refineEnd; candidate += 0x200u)
            {
                int score = ScoreFramebufferCandidate(candidate, width, height, bytesPerPixel);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOrigin = candidate;
                }
            }

            // When the VI-provided origin is obviously bogus, accept a smaller margin.
            int requiredMargin = viOrigin < 0x00001000u ? 24 : 80;
            if (bestOrigin != viOrigin && bestScore < viScore + requiredMargin)
                return viOrigin;

            return bestOrigin;
        }

        private static byte[] ShortToByteArray(short[] samples)
        {
            byte[] bytes = new byte[samples.Length * 2];
            int bi = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                short sample = samples[i];
                bytes[bi++] = (byte)(sample & 0xFF);
                bytes[bi++] = (byte)((sample >> 8) & 0xFF);
            }

            return bytes;
        }
    }

    public class FramebufferUpdatedEventArgs : EventArgs
    {
        public byte[] Framebuffer { get; }
        public uint Width { get; }
        public uint Height { get; }
        public uint BytesPerPixel { get; }

        public FramebufferUpdatedEventArgs(byte[] framebuffer, uint width, uint height, uint bytesPerPixel)
        {
            Framebuffer = framebuffer;
            Width = width;
            Height = height;
            BytesPerPixel = bytesPerPixel;
        }
    }

    public class AudioBufferEventArgs : EventArgs
    {
        public byte[] AudioBuffer { get; }
        public uint SampleRate { get; }
        public uint Channels { get; }

        public AudioBufferEventArgs(byte[] audioBuffer, uint sampleRate, uint channels)
        {
            AudioBuffer = audioBuffer;
            SampleRate = sampleRate;
            Channels = channels;
        }
    }

    public class EmulationStateChangedEventArgs : EventArgs
    {
        public bool IsRunning { get; }

        public EmulationStateChangedEventArgs(bool isRunning)
        {
            IsRunning = isRunning;
        }
    }

    public struct InputState
    {
        public bool A;
        public bool B;
        public bool Start;
        public bool Up;
        public bool Down;
        public bool Left;
        public bool Right;
        public bool L;
        public bool R;
        public bool Z;
        public bool CUp;
        public bool CDown;
        public bool CLeft;
        public bool CRight;
        public sbyte StickX;
        public sbyte StickY;
    }
}
