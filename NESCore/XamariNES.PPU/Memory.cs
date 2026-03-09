using System;
using System.Runtime.CompilerServices;
using XamariNES.Cartridge.Mappers;
using XamariNES.Cartridge.Mappers.Enums;

namespace XamariNES.PPU
{
    /// <summary>
    ///     PPU Internal Memory
    ///
    ///     In addition to accessing the CHR ROM via the mapper, the PPU itself has 2K RAM
    ///     and 32 bytes of Palette RAM
    /// </summary>
    public class Memory
    {
        [NonSerialized]
        private readonly IMapper _memoryMapper;
        private readonly byte[] _ppuVram;
        private readonly byte[] _paletteMemory;
        [NonSerialized]
        private readonly bool _tracePatternReads =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_PATTERN_READS"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _tracePatternReadsLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_NES_PATTERN_READS_LIMIT", 2048);
        [NonSerialized]
        private readonly long _tracePatternReadsStartCycle = ParseTraceStartCycle("EUTHERDRIVE_TRACE_NES_PATTERN_READS_START_CYCLE");
        [NonSerialized]
        private int _tracePatternReadsCount;

        public Memory(IMapper memoryMapper)
        {
            _memoryMapper = memoryMapper;
            _ppuVram = new byte[2048];
            _paletteMemory = new byte[32];
        }

        /// <summary>
        ///     Resets the memory back to startup state
        /// </summary>
        public void Reset()
        {
            Array.Clear(_ppuVram, 0, _ppuVram.Length);
            Array.Clear(_paletteMemory, 0, _paletteMemory.Length);
        }

        public byte ReadByte(int offset)
        {
            return ReadByteInternal(offset, -1, false);
        }

        public byte ReadByteWithA12(int offset, long ppuCycle)
        {
            return ReadByteInternal(offset, ppuCycle, true);
        }

        public byte ReadByteRender(int offset, bool sprite)
        {
            return ReadByteRender(offset, sprite, -1);
        }

        public byte ReadByteRender(int offset, bool sprite, long ppuCycle)
        {
            offset &= 0x3FFF;

            if (_memoryMapper is IPpuA12Observer observer && ppuCycle >= 0)
                observer.NotifyPpuA12(offset, ppuCycle);

            if (_memoryMapper is IPpuMemoryMapperEx ppuMapper)
                return ppuMapper.ReadPpuRender(offset, _ppuVram, sprite);
            return ReadByteInternal(offset, ppuCycle, false);
        }

        public void ClockMapperScanline()
        {
            if (_memoryMapper is IMapperScanlineCounter counter)
                counter.ClockScanline();
        }

        private byte ReadByteInternal(int offset, long ppuCycle, bool notifyA12)
        {
            // PPU address bus is 14-bit; $4000+ mirrors back into $0000-$3FFF.
            offset &= 0x3FFF;

            if (notifyA12 && _memoryMapper is IPpuA12Observer observer && ppuCycle >= 0)
                observer.NotifyPpuA12(offset, ppuCycle);

            if (_tracePatternReads &&
                offset < 0x2000 &&
                ppuCycle >= _tracePatternReadsStartCycle &&
                _tracePatternReadsCount < _tracePatternReadsLimit)
            {
                Console.WriteLine($"[NES-PATTERN-READ] ppu=0x{offset:X4} cycle={ppuCycle}");
                if (_tracePatternReadsCount != int.MaxValue)
                    _tracePatternReadsCount++;
            }

            if (offset >= 0x3F00 && offset <= 0x3FFF) // Palette RAM
                return _paletteMemory[GetPaletteRamOffsetIndex(offset)];

            if (_memoryMapper is IPpuMemoryMapper ppuMapper)
                return ppuMapper.ReadPpu(offset, _ppuVram);

            if (offset < 0x2000) // CHR (ROM or RAM) pattern tables
            {
                return _memoryMapper.ReadByte(offset);
            }

            if (offset <= 0x3EFF) // Internal _vRam
                return _ppuVram[VramOffsetToOffsetIndex(offset)];

            throw new Exception($"Invalid PPU Memory Read at address: {offset:X4}");
        }

        /// <summary>
        ///     Write a byte of memory to the specified address.
        /// </summary>
        /// <param name="offset">the address to write to</param>
        /// <param name="data">the byte to write to the specified address</param>
        public void WriteByte(int offset, byte data)
        {
            // PPU address bus is 14-bit; $4000+ mirrors back into $0000-$3FFF.
            offset &= 0x3FFF;

            if (offset < 0x2000)
            {
                if (_memoryMapper is IPpuMemoryMapper ppuMapper)
                {
                    ppuMapper.WritePpu(offset, data, _ppuVram);
                }
                else
                {
                    _memoryMapper.WriteByte(offset, data);
                }
                return;
            }

            if (offset >= 0x2000 && offset <= 0x3EFF) // Internal VRAM
            {
                if (_memoryMapper is IPpuMemoryMapper ppuMapper)
                    ppuMapper.WritePpu(offset, data, _ppuVram);
                else
                    _ppuVram[VramOffsetToOffsetIndex(offset)] = data;
                return;
            }

            if (offset >= 0x3F00 && offset <= 0x3FFF) // Palette RAM addresses
            {
                _paletteMemory[GetPaletteRamOffsetIndex(offset)] = data;
                return;
            }

            throw new Exception($"Invalid PPU Memory Write at address: {offset:X4}");
        }

        /// <summary>
        ///     Given a palette RAM address ($3F00-$3FFF), return the index in
        ///     palette RAM that it corresponds to.
        /// </summary>
        /// <returns>Offset in our 32-byte non-mirrored Palette RAM</returns>
        /// <param name="offset">Offset in Palette RAM</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetPaletteRamOffsetIndex(int offset)
        {
            var index = (offset - 0x3F00) % 32;

            // Mirror $3F10, $3F14, $3F18, $3F1C to $3F00, $3F14, $3F08 $3F0C
            if (index >= 16 && (index - 16) % 4 == 0) return index - 16;

            return index;
        }

        /// <summary>
        ///     Given a address $2000-$3EFFF, returns the index in the VRAM array
        ///     that the address points to depending on the current VRAM mirroring
        ///     mode.
        ///
        ///     We get the mirroring mode from the memory mapper, which loaded it
        ///     from the cartridge/ROM
        /// </summary>
        /// <returns>Offset of the Index</returns>
        /// <param name="offset">Index Offset</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int VramOffsetToOffsetIndex(int offset)
        {
            // Address in VRAM indexed with 0 at 0x2000
            var index = (offset - 0x2000) % 0x1000;
            switch (_memoryMapper.NametableMirroring)
            {
                case enumNametableMirroring.Vertical:
                    // If in one of the mirrored regions, subtract 0x800 to get index
                    if (index >= 0x800) index -= 0x800;
                    break;
                case enumNametableMirroring.Horizontal:
                    if (index >= 0x800) index = (index - 0x800) % 0x400 + 0x400; // In the 2 B regions
                    else index %= 0x400; // In one of the 2 A regions
                    break;
                case enumNametableMirroring.SingleLower:
                    index %= 0x400;
                    break;
                case enumNametableMirroring.SingleUpper:
                    index = index % 0x400 + 0x400;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return index;
        }

        private static int ParseTraceLimit(string envName, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            return int.TryParse(raw.Trim(), out int value)
                ? (value <= 0 ? int.MaxValue : value)
                : fallback;
        }

        private static long ParseTraceStartCycle(string envName)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
                return long.MinValue;

            return long.TryParse(raw.Trim(), out long value) ? value : long.MinValue;
        }
    }
}
