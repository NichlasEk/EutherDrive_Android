using System;

namespace ePceCD
{
    [Serializable]
    public sealed class ArcadeCard
    {
        private const int RamMask = 0x1FFFFF;

        [Serializable]
        public sealed class Port
        {
            public int Base;
            public ushort Offset;
            public ushort Increment;
            public byte Control;
            public bool AddOffset;
            public bool AutoIncrement;
            public bool SignedOffset;
            public bool IncrementBase;
            public byte OffsetTrigger;
        }

        public readonly byte[] Ram = new byte[0x200000];
        public readonly Port[] Ports = { new Port(), new Port(), new Port(), new Port() };

        private uint _shiftRegister;
        private byte _shiftAmount;
        private byte _rotateAmount;

        public void Reset()
        {
            _shiftRegister = 0;
            _shiftAmount = 0;
            _rotateAmount = 0;

            for (int i = 0; i < Ports.Length; i++)
            {
                Ports[i].Base = 0;
                Ports[i].Offset = 0;
                Ports[i].Increment = 0;
                Ports[i].Control = 0;
                Ports[i].AddOffset = false;
                Ports[i].AutoIncrement = false;
                Ports[i].SignedOffset = false;
                Ports[i].IncrementBase = false;
                Ports[i].OffsetTrigger = 0;
            }
        }

        public byte ReadHardware(int address)
        {
            if (address < 0x1A40)
            {
                int port = (address >> 4) & 0x03;
                int reg = address & 0x0F;
                return ReadPortRegister(port, reg);
            }

            if (address < 0x1B00)
                return ReadRegister(address & 0xFF);

            return 0xFF;
        }

        public void WriteHardware(int address, byte value)
        {
            if (address < 0x1A40)
            {
                int port = (address >> 4) & 0x03;
                int reg = address & 0x0F;
                WritePortRegister(port, reg, value);
                return;
            }

            if (address < 0x1B00)
                WriteRegister(address & 0xFF, value);
        }

        public byte ReadPortData(int port)
        {
            int address = EffectiveAddress(port);
            Increment(port);
            return Ram[address];
        }

        public void WritePortData(int port, byte value)
        {
            int address = EffectiveAddress(port);
            Increment(port);
            Ram[address] = value;
        }

        private byte ReadPortRegister(int port, int reg)
        {
            Port p = Ports[port];
            return reg switch
            {
                0x00 or 0x01 => ReadPortData(port),
                0x02 => (byte)(p.Base & 0xFF),
                0x03 => (byte)((p.Base >> 8) & 0xFF),
                0x04 => (byte)((p.Base >> 16) & 0xFF),
                0x05 => (byte)(p.Offset & 0xFF),
                0x06 => (byte)((p.Offset >> 8) & 0xFF),
                0x07 => (byte)(p.Increment & 0xFF),
                0x08 => (byte)((p.Increment >> 8) & 0xFF),
                0x09 => p.Control,
                0x0A => 0x00,
                _ => 0xFF
            };
        }

        private void WritePortRegister(int port, int reg, byte value)
        {
            Port p = Ports[port];
            switch (reg)
            {
                case 0x00:
                case 0x01:
                    WritePortData(port, value);
                    break;
                case 0x02:
                    p.Base = (p.Base & 0xFFFF00) | value;
                    break;
                case 0x03:
                    p.Base = (p.Base & 0xFF00FF) | (value << 8);
                    break;
                case 0x04:
                    p.Base = (p.Base & 0x00FFFF) | (value << 16);
                    break;
                case 0x05:
                    p.Offset = (ushort)((p.Offset & 0xFF00) | value);
                    if (p.OffsetTrigger == 1)
                        AddOffset(port);
                    break;
                case 0x06:
                    p.Offset = (ushort)((p.Offset & 0x00FF) | (value << 8));
                    if (p.OffsetTrigger == 2)
                        AddOffset(port);
                    break;
                case 0x07:
                    p.Increment = (ushort)((p.Increment & 0xFF00) | value);
                    break;
                case 0x08:
                    p.Increment = (ushort)((p.Increment & 0x00FF) | (value << 8));
                    break;
                case 0x09:
                    WriteControlRegister(port, value);
                    break;
                case 0x0A:
                    if (p.OffsetTrigger == 3)
                        AddOffset(port);
                    break;
            }
        }

        private byte ReadRegister(int reg)
        {
            switch (reg)
            {
                case 0xE0:
                case 0xE1:
                case 0xE2:
                case 0xE3:
                    return (byte)((_shiftRegister >> ((reg & 0x03) << 3)) & 0xFF);
                case 0xE4:
                    return _shiftAmount;
                case 0xE5:
                    return _rotateAmount;
                case 0xEC:
                case 0xED:
                case 0xFC:
                case 0xFD:
                    return 0x00;
                case 0xFE:
                    return 0x10;
                case 0xFF:
                    return 0x51;
                default:
                    return 0xFF;
            }
        }

        private void WriteRegister(int reg, byte value)
        {
            switch (reg)
            {
                case 0xE0:
                case 0xE1:
                case 0xE2:
                case 0xE3:
                {
                    int shift = (reg & 0x03) << 3;
                    _shiftRegister = (_shiftRegister & ~((uint)0xFF << shift)) | ((uint)value << shift);
                    break;
                }
                case 0xE4:
                    _shiftAmount = value;
                    ApplyShift(value);
                    break;
                case 0xE5:
                    _rotateAmount = value;
                    ApplyRotate(value);
                    break;
            }
        }

        private void ApplyShift(byte value)
        {
            if (value == 0)
                return;

            sbyte signedAmount = (sbyte)(value << 4);
            signedAmount >>= 4;
            if (signedAmount > 0)
                _shiftRegister <<= signedAmount;
            else if (signedAmount < 0)
                _shiftRegister >>= -signedAmount;
        }

        private void ApplyRotate(byte value)
        {
            if (value == 0)
                return;

            sbyte signedAmount = (sbyte)(value << 4);
            signedAmount >>= 4;
            int count = Math.Abs(signedAmount) & 31;
            if (count == 0)
                return;

            if (signedAmount > 0)
                _shiftRegister = (_shiftRegister << count) | (_shiftRegister >> (32 - count));
            else
                _shiftRegister = (_shiftRegister >> count) | (_shiftRegister << (32 - count));
        }

        private void WriteControlRegister(int port, byte value)
        {
            Port p = Ports[port];
            p.Control = (byte)(value & 0x7F);
            p.AutoIncrement = (value & 0x01) != 0;
            p.AddOffset = (value & 0x02) != 0;
            p.SignedOffset = (value & 0x08) != 0;
            p.IncrementBase = (value & 0x10) != 0;
            p.OffsetTrigger = (byte)((value >> 5) & 0x03);
        }

        private void Increment(int port)
        {
            Port p = Ports[port];
            if (!p.AutoIncrement)
                return;

            if (p.IncrementBase)
                p.Base = (p.Base + p.Increment) & 0xFFFFFF;
            else
                p.Offset = (ushort)(p.Offset + p.Increment);
        }

        private void AddOffset(int port)
        {
            Port p = Ports[port];
            int realOffset = p.SignedOffset ? (short)p.Offset : p.Offset;
            p.Base = (p.Base + realOffset) & 0xFFFFFF;
        }

        private int EffectiveAddress(int port)
        {
            Port p = Ports[port];
            int address = p.Base;
            if (p.AddOffset)
            {
                int realOffset = p.SignedOffset ? (short)p.Offset : p.Offset;
                address += realOffset;
            }
            return address & RamMask;
        }
    }

    [Serializable]
    public sealed class ArcadeCardDataBank : MemoryBank
    {
        [NonSerialized]
        private ArcadeCard? _arcadeCard;
        private readonly int _port;

        public ArcadeCardDataBank(ArcadeCard arcadeCard, int port)
        {
            _arcadeCard = arcadeCard;
            _port = port;
        }

        public void Rebind(ArcadeCard arcadeCard)
        {
            _arcadeCard = arcadeCard;
        }

        public override byte ReadAt(int address)
        {
            return _arcadeCard?.ReadPortData(_port) ?? (byte)0xFF;
        }

        public override void WriteAt(int address, byte data)
        {
            _arcadeCard?.WritePortData(_port, data);
        }
    }
}
