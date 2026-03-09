using System;
using System.Collections.Generic;
using System.Globalization;
using XamariNES.Cartridge.Mappers;
using XamariNES.Cartridge.Mappers.Enums;
using XamariNES.Controller;

namespace XamariNES.CPU
{
    /// <summary>
    ///     NES CPU Memory
    ///
    ///     We use this to handle the CPU specific memory map
    ///     https://wiki.nesdev.com/w/index.php/CPU_memory_map
    /// </summary>
    public class Memory
    {
        private struct DeferredWrite
        {
            public int Offset;
            public byte Data;
        }

        [NonSerialized]
        private readonly IMapper _memoryMapper;
        [NonSerialized]
        private readonly IMapperOpenBusRead _openBusMapper;
        [NonSerialized]
        private readonly IController _controller;
        private readonly byte[] _internalRam;
        [NonSerialized]
        private IApu _apu;
        private byte _openBus;
        public bool ReadPpuStatusThisInstruction { get; private set; }
        [NonSerialized]
        private readonly bool _tracePpuRegWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_PPU_REG_WRITES"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _tracePpuRegWritesLimit = ParseLimit("EUTHERDRIVE_TRACE_NES_PPU_REG_WRITES_LIMIT", 4000);
        [NonSerialized]
        private int _tracePpuRegWritesCount;
        [NonSerialized]
        private readonly long _tracePpuRegStartCycle = ParseLong("EUTHERDRIVE_TRACE_NES_PPU_REG_START_CYCLE", long.MinValue);
        [NonSerialized]
        private readonly HashSet<int> _tracePpuRegFilter = ParseAddressFilter("EUTHERDRIVE_TRACE_NES_PPU_REGS_FILTER");
        [NonSerialized]
        private readonly bool _tracePpuRegReads =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_PPU_REG_READS"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _tracePpuRegReadsLimit = ParseLimit("EUTHERDRIVE_TRACE_NES_PPU_REG_READS_LIMIT", 4000);
        [NonSerialized]
        private int _tracePpuRegReadsCount;
        [NonSerialized]
        private readonly bool _traceCartWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_CART_WRITES"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _traceCartWritesLimit = ParseLimit("EUTHERDRIVE_TRACE_NES_CART_WRITES_LIMIT", 4000);
        [NonSerialized]
        private int _traceCartWritesCount;
        [NonSerialized]
        private readonly bool _traceRamWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_RAM_WRITES"), "1", StringComparison.Ordinal);
        [NonSerialized]
        private readonly int _traceRamWritesLimit = ParseLimit("EUTHERDRIVE_TRACE_NES_RAM_WRITES_LIMIT", 4000);
        [NonSerialized]
        private int _traceRamWritesCount;
        [NonSerialized]
        private readonly long _traceRamWriteStartCycle = ParseLong("EUTHERDRIVE_TRACE_NES_RAM_WRITE_START_CYCLE", long.MinValue);
        [NonSerialized]
        private readonly int _traceRamWriteStart = ParseAddress("EUTHERDRIVE_TRACE_NES_RAM_WRITE_START", 0x0000);
        [NonSerialized]
        private readonly int _traceRamWriteEnd = ParseAddress("EUTHERDRIVE_TRACE_NES_RAM_WRITE_END", 0x07FF);
        [NonSerialized]
        private readonly bool _deferMmioWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_NES_DEFER_MMIO_WRITES"), "1", StringComparison.Ordinal);
        private readonly List<DeferredWrite> _deferredWrites = new List<DeferredWrite>(4);

        public Memory(IMapper memoryMapper, IController controller, IApu apu = null)
        {
            _memoryMapper = memoryMapper;
            _openBusMapper = memoryMapper as IMapperOpenBusRead;
            _controller = controller;
            _apu = apu;
            _internalRam = new byte[2048];
        }

        public void AttachApu(IApu apu)
        {
            _apu = apu;
        }

        public byte[] GetInternalRam() => _internalRam;

        public void SetInternalRam(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;
            int copy = data.Length < _internalRam.Length ? data.Length : _internalRam.Length;
            Buffer.BlockCopy(data, 0, _internalRam, 0, copy);
            if (copy < _internalRam.Length)
                Array.Clear(_internalRam, copy, _internalRam.Length - copy);
        }

        /// <summary>
        ///     Reads a single byte from the specified offset in the memory address space
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public byte ReadByte(int offset)
        {
            byte value;
            //2KB internal RAM (+ mirrors)
            if (offset < 0x2000) 
                value = _internalRam[offset % 0x800];

            //NES PPU Registers (Repeats every 8 bytes)
            else if (offset <= 0x3FFF)
            {
                int ppuRegister = 0x2000 + offset % 8;
                if (ppuRegister == 0x2002)
                    ReadPpuStatusThisInstruction = true;
                value = _memoryMapper.ReadByte(ppuRegister);
                if (_tracePpuRegReads &&
                    DebugTraceContext.CpuCycles >= _tracePpuRegStartCycle &&
                    (_tracePpuRegFilter.Count == 0 || _tracePpuRegFilter.Contains(ppuRegister)) &&
                    _tracePpuRegReadsCount < _tracePpuRegReadsLimit)
                {
                    Console.WriteLine($"[NES-PPU-R] {DebugTraceContext.FormatCpu()} {DebugTraceContext.FormatPpu()} reg=0x{ppuRegister:X4} data=0x{value:X2}");
                    if (_tracePpuRegReadsCount != int.MaxValue)
                        _tracePpuRegReadsCount++;
                }
            }

            //NES APU & I/O Registers
            else if (offset <= 0x4017)
            {
                switch (offset)
                {
                    case 0x4016:
                        value = _controller.ReadController();
                        break;
                    case 0x4015:
                        value = _apu != null ? _apu.ReadStatus() : (byte)0x0;
                        break;
                    default:
                        value = 0x0;
                        break;
                }
            }

            //APU and I/O functionality that is normally disabled
            else if (offset <= 0x401F)
                value = 0x0;

            //Cartridge space: PRG ROM, PRG RAM, and mapper registers 
            else if (offset >= 0x4020) 
                value = _openBusMapper != null
                    ? _openBusMapper.ReadByte(offset, _openBus)
                    : _memoryMapper.ReadByte(offset);
            else
                throw new Exception($"Invalid CPU read at address {offset:X4}");

            _openBus = value;
            return value;
        }

        public void BeginInstruction()
        {
            ReadPpuStatusThisInstruction = false;
        }

        public void FlushDeferredWrites()
        {
            if (_deferredWrites.Count == 0)
                return;

            for (int i = 0; i < _deferredWrites.Count; i++)
                ApplyDeferredWrite(_deferredWrites[i].Offset, _deferredWrites[i].Data);

            _deferredWrites.Clear();
        }

        /// <summary>
        ///     Write the specified byte to the offset in the memory address space
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="data"></param>
        public void WriteByte(int offset, byte data)
        {
            _openBus = data;
            //2KB internal RAM (+ mirrors)
            if (offset < 0x2000)
            {
                int ramOffset = offset % 0x800;
                _internalRam[ramOffset] = data;
                if (_traceRamWrites &&
                    DebugTraceContext.CpuCycles >= _traceRamWriteStartCycle &&
                    ramOffset >= _traceRamWriteStart &&
                    ramOffset <= _traceRamWriteEnd &&
                    _traceRamWritesCount < _traceRamWritesLimit)
                {
                    Console.WriteLine($"[NES-RAM-W] {DebugTraceContext.FormatCpu()} {DebugTraceContext.FormatPpu()} addr=0x{ramOffset:X4} data=0x{data:X2}");
                    if (_traceRamWritesCount != int.MaxValue)
                        _traceRamWritesCount++;
                }
                return;
            }

            //NES PPU Registers (repeats every 8 bytes and OAM register)
            if (offset <= 0x3FFF)
            {
                int ppuReg = 0x2000 + (offset % 8);
                if (_tracePpuRegWrites &&
                    DebugTraceContext.CpuCycles >= _tracePpuRegStartCycle &&
                    (_tracePpuRegFilter.Count == 0 || _tracePpuRegFilter.Contains(ppuReg)) &&
                    _tracePpuRegWritesCount < _tracePpuRegWritesLimit)
                {
                    Console.WriteLine($"[NES-PPU-W] {DebugTraceContext.FormatCpu()} {DebugTraceContext.FormatPpu()} reg=0x{ppuReg:X4} data=0x{data:X2}");
                    if (_tracePpuRegWritesCount != int.MaxValue)
                        _tracePpuRegWritesCount++;
                }
                if (_deferMmioWrites)
                {
                    _deferredWrites.Add(new DeferredWrite { Offset = ppuReg, Data = data });
                }
                else
                {
                    _memoryMapper.WriteByte(ppuReg, data);
                }
                return;
            }

            //OAM DMA
            if (offset == 0x4014)
            {
                _memoryMapper.WriteByte(offset, data);
                return;
            }

            //NES APU & I/O Registers
            if (offset <= 0x4017) 
            {
                switch (offset)
                {
                    case 0x4016:
                        _controller.SignalController(data);
                        break;
                    default:
                        if (_apu != null && offset <= 0x4017)
                            _apu.WriteRegister(offset, data);
                        break;
                }
                return;
            }

            //APU and I/O functionality that is normally disabled
            if (offset <= 0x401F)
                return;

            //Cartridge space: PRG ROM, PRG RAM, and mapper registers
            if (offset >= 0x4020)
            {
                if (_traceCartWrites && _traceCartWritesCount < _traceCartWritesLimit)
                {
                    Console.WriteLine($"[NES-CART-W] {DebugTraceContext.FormatCpu()} {DebugTraceContext.FormatPpu()} addr=0x{offset:X4} data=0x{data:X2}");
                    if (_traceCartWritesCount != int.MaxValue)
                        _traceCartWritesCount++;
                }
                if (_deferMmioWrites && offset >= 0x8000)
                {
                    _deferredWrites.Add(new DeferredWrite { Offset = offset, Data = data });
                }
                else
                {
                    _memoryMapper.WriteByte(offset, data);
                }
                return;
            }
            
            throw new Exception($"Invalid CPU write to address {offset:X4}");
        }

        private static int ParseLimit(string envName, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            return int.TryParse(raw.Trim(), out int value)
                ? (value <= 0 ? int.MaxValue : value)
                : fallback;
        }

        private static int ParseAddress(string envName, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            string token = raw.Trim();
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(2);

            return int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)
                ? hex
                : fallback;
        }

        private static HashSet<int> ParseAddressFilter(string envName)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            var output = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(raw))
                return output;

            foreach (string part in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = part.Trim();
                string normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? token.Substring(2)
                    : token;
                if (int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
                    output.Add(value & 0xFFFF);
            }

            return output;
        }

        private static long ParseLong(string envName, long fallback)
        {
            string raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            return long.TryParse(raw.Trim(), out long value) ? value : fallback;
        }

        private void ApplyDeferredWrite(int offset, byte data)
        {
            _memoryMapper.WriteByte(offset, data);
        }
    }
}
