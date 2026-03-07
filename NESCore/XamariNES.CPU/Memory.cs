using System;
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
                _internalRam[offset % 0x800] = data;
                return;
            }

            //NES PPU Registers (repeats every 8 bytes and OAM register)
            if (offset <= 0x3FFF)
            {
                _memoryMapper.WriteByte(0x2000 + (offset % 8), data);
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
                _memoryMapper.WriteByte(offset, data);
                return;
            }
            
            throw new Exception($"Invalid CPU write to address {offset:X4}");
        }
    }
}
