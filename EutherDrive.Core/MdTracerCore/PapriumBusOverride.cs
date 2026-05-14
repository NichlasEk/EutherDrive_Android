using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.MdTracerCore;

internal sealed class PapriumBusOverride : IM68kBusOverride
{
    private const int DualPortBytes = 0x2000;
    private const int SdramBytes = 0x200000;
    private const int ScaleStampBytes = 64 * 32;
    private const int NvramWords = 0x800 / 2;
    private const int MaxVramSlots = 64;
    private const int ObjectsCount = 64;

    private const int SatOffset = 0x0B00;
    private const int ObjOffset = 0x0F80;
    private const int DmaCommandsOffset = 0x1400;
    private const int NetworkDataOffset = 0x1C00;
    private const int CommandArgsOffset = 0x1E10;
    private const int DmaTotalOffset = 0x1F10;
    private const int DmaBudgetOffset = 0x1F12;
    private const int DmaRemainingOffset = 0x1F14;
    private const int DmaCommandsCountOffset = 0x1F16;
    private const int SatCountOffset = 0x1F18;
    private const int RegStatus1Offset = 0x1FE4;
    private const int RegStatus2Offset = 0x1FE6;
    private const int RegCommandOffset = 0x1FEA;

    private const ushort Status2Busy = 0x4000;
    private const ushort Status2EepromError1 = 0x0100;
    private const ushort Status2EepromError2 = 0x0200;
    private const ushort Status2MwDataIn = 0x0020;

    private readonly ushort[] _romWords;
    private readonly ushort[] _dualPort = new ushort[DualPortBytes / 2];
    private readonly ushort[] _sdram = new ushort[SdramBytes / 2];
    private readonly byte[] _scaleStamp = new byte[ScaleStampBytes];
    private readonly ushort[] _nvram = new ushort[NvramWords];
    private readonly VramSlot[] _vramSlots = new VramSlot[MaxVramSlots];
    private readonly ObjectHandle[] _objectHandles = new ObjectHandle[ObjectsCount];
    private readonly byte[] _drawList = new byte[ObjectsCount];
    private int _drawListCount;
    private int _sdramPointerWord;
    private bool _sdramWindowEnabled;
    private ushort _vramMaxSlot;
    private uint _blockUnpackAddr;
    private uint _animDataBaseAddr;
    private readonly ushort[] _animMaxIndex = new ushort[256];
    private uint _bgmTracksBaseAddr;
    private uint _bgmUnpackAddr;
    private uint _sfxBaseAddr;
    private uint _gfxBlocksBaseAddr;
    private ushort _audioBgmVolume;
    private ushort _audioSfxVolume;
    private ushort _audioConfig;
    private bool _decoded;
    private readonly string? _savePath;
    private static readonly bool TracePaprium =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PAPRIUM"), "1", StringComparison.Ordinal);

    public PapriumBusOverride(byte[] romBytes, string? sourcePath)
    {
        _romWords = ToWords(romBytes);
        _savePath = BuildSavePath(sourcePath);
        LoadNvram();
        Reset();
    }

    public void Reset()
    {
        DecodeAndPatchOnce();
        RestoreBootDualPort();
        ApplyVersionPatches();
        Array.Clear(_sdram);
        Array.Clear(_scaleStamp);
        Array.Clear(_vramSlots);
        Array.Clear(_objectHandles);
        Array.Clear(_drawList);
        _drawListCount = 0;
        _sdramPointerWord = 0;
        _sdramWindowEnabled = false;
        _vramMaxSlot = 0;
        SetWord(RegCommandOffset, 0);
        SetWord(RegStatus1Offset, 0);
        SetWord(RegStatus2Offset, 7);
    }

    public bool TryRead8(uint address, out byte value)
    {
        address &= 0x00FF_FFFF;
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        uint offset = address & 0x003F_FFFF;
        if (offset < DualPortBytes)
        {
            value = RawReadByte(_dualPort, (int)(offset ^ 1));
            return true;
        }

        if (offset >= 0xC000 && offset < 0x10000 && _sdramWindowEnabled)
        {
            ushort word = ReadSdramWindowWord(sideEffects: false);
            value = (offset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
            return true;
        }

        if (offset < 0x400000)
        {
            value = CpuReadByte(_romWords, (int)offset);
            return true;
        }

        value = 0xFF;
        return true;
    }

    public bool TryRead16(uint address, out ushort value)
    {
        address &= 0x00FF_FFFF;
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        uint offset = address & 0x003F_FFFE;
        if (offset < DualPortBytes)
        {
            value = ReadPapriumRegisterWord((int)offset);
            return true;
        }

        if (offset >= 0xC000 && offset < 0x10000 && _sdramWindowEnabled)
        {
            value = ReadSdramWindowWord(sideEffects: true);
            return true;
        }

        if (offset < 0x400000)
        {
            value = ReadWord(_romWords, (int)offset);
            return true;
        }

        value = 0xFFFF;
        return true;
    }

    public bool TryRead32(uint address, out uint value)
    {
        if (!TryRead16(address, out ushort hi))
        {
            value = 0;
            return false;
        }

        TryRead16(address + 2, out ushort lo);
        value = ((uint)hi << 16) | lo;
        return true;
    }

    public bool TryWrite8(uint address, byte value)
    {
        address &= 0x00FF_FFFF;
        if (!Handles(address))
            return false;

        uint offset = address & 0x003F_FFFF;
        if (offset < DualPortBytes)
        {
            RawWriteByte(_dualPort, (int)(offset ^ 1), value);
            if ((offset & 0xFFFE) == RegCommandOffset)
                ProcessCommand();
        }

        return true;
    }

    public bool TryWrite16(uint address, ushort value)
    {
        address &= 0x00FF_FFFF;
        if (!Handles(address))
            return false;

        uint offset = address & 0x003F_FFFE;
        if (offset < DualPortBytes)
        {
            SetWord((int)offset, value);
            if (offset == RegCommandOffset)
                ProcessCommand();
        }

        return true;
    }

    public bool TryWrite32(uint address, uint value)
    {
        if (!Handles(address & 0x00FF_FFFF))
            return false;

        TryWrite16(address, (ushort)(value >> 16));
        TryWrite16(address + 2, (ushort)value);
        return true;
    }

    private static bool Handles(uint address)
    {
        address &= 0x00FF_FFFF;
        return address <= 0x003F_FFFF;
    }

    private ushort ReadPapriumRegisterWord(int offset)
    {
        switch (offset)
        {
            case RegStatus1Offset:
                return 0xFFBB;
            case RegStatus2Offset:
                return 0xFFFF & unchecked((ushort)~(1 << 14)) & unchecked((ushort)~(1 << 8)) & unchecked((ushort)~(1 << 9));
            case RegCommandOffset:
                return 0x7FFF;
            default:
                return _dualPort[offset >> 1];
        }
    }

    private ushort ReadSdramWindowWord(bool sideEffects)
    {
        int index = Math.Clamp(_sdramPointerWord, 0, _sdram.Length - 1);
        ushort value = _sdram[index];
        if (sideEffects && _sdramPointerWord < _sdram.Length - 1)
            _sdramPointerWord++;
        return value;
    }

    private void ProcessCommand()
    {
        ushort command = GetWord(RegCommandOffset);
        int id = command >> 8;
        int arg = command & 0xFF;

        switch (id)
        {
            case 0x00:
                if (arg == 0xAA)
                {
                    SetWord(RegCommandOffset, 0x00FF);
                    return;
                }
                if (arg == 0x55)
                {
                    SetWord(RegCommandOffset, 0x0000);
                    return;
                }
                break;
            case 0x81:
                _sdramWindowEnabled = true;
                break;
            case 0x83:
            case 0x95:
            case 0x96:
            case 0xB1:
            case 0xB6:
                break;
            case 0x84:
                _sdramWindowEnabled = false;
                break;
            case 0x88:
                _audioConfig = (ushort)arg;
                break;
            case 0x8C:
                if (_bgmTracksBaseAddr != 0)
                    Unpack(BgmAddr(arg & 0x7F), _bgmUnpackAddr);
                break;
            case 0xA4:
                break;
            case 0xAD:
                ObjAdd((byte)arg);
                break;
            case 0xAE:
                ObjFrameStart();
                break;
            case 0xAF:
                ObjFrameEnd();
                break;
            case 0xB0:
                ObjReset();
                break;
            case 0xC6:
                SetupData(
                    SwapShorts(GetCommandArgLong(0)),
                    SwapShorts(GetCommandArgLong(1)),
                    SwapShorts(GetCommandArgLong(2)),
                    SwapShorts(GetCommandArgLong(3)),
                    SwapShorts(GetCommandArgLong(4)),
                    SwapShorts(GetCommandArgLong(5)),
                    SwapShorts(GetCommandArgLong(6)));
                break;
            case 0xC9:
                _audioBgmVolume = (ushort)arg;
                break;
            case 0xCA:
                _audioSfxVolume = (ushort)arg;
                break;
            case 0xD1:
            case 0xD2:
            case 0xD6:
                break;
            case 0xDA:
            {
                uint source = ((uint)GetCommandArg(1) << 16) | GetCommandArg(2);
                ushort dest = GetCommandArg(0);
                Unpack(source, dest);
                _sdramPointerWord = dest >> 1;
                SetWord(RegStatus1Offset, (ushort)(GetWord(RegStatus1Offset) & ~0x0004));
                SetWord(RegStatus2Offset, (ushort)(GetWord(RegStatus2Offset) & ~Status2Busy));
                break;
            }
            case 0xDB:
                _sdramPointerWord = (int)(SwapShorts(GetCommandArgLong(0)) >> 1);
                break;
            case 0xDF:
                LoadEepromBlock(arg);
                break;
            case 0xE0:
                SaveEepromBlock(arg);
                break;
            case 0xE7:
                SetWord(RegStatus2Offset, (ushort)(GetWord(RegStatus2Offset) | Status2MwDataIn));
                SetWord(NetworkDataOffset + 0x10, (ushort)(GetCommandArg(0) + 16));
                break;
            case 0xEC:
                VramSetBudget(GetCommandArg(1));
                break;
            case 0xF2:
            {
                ushort block = GetCommandArg(0);
                Unpack(BlockAddr(block), 0x9000);
                Unpack(BlockAddr(block), 0x9200);
                _sdramPointerWord = 0x9000 >> 1;
                break;
            }
            case 0xF4:
                Unpack(SwapShorts(GetCommandArgLong(0)), 0, isScaleStamp: true);
                break;
            case 0xF5:
                StampRescale(GetCommandArg(0), GetCommandArg(1), GetCommandArg(2), GetCommandArg(3));
                break;
            default:
                if (TracePaprium)
                    Console.WriteLine($"[PAPRIUM] unhandled command 0x{command:X4}");
                break;
        }

        SetWord(RegCommandOffset, 0);
    }

    private void SetupData(uint bgmFile, uint unk1File, uint smpFile, uint unk2File, uint sfxFile, uint anmFile, uint blkFile)
    {
        uint unpackAddr = 0x10000;
        _bgmTracksBaseAddr = bgmFile;
        _ = smpFile;

        unpackAddr += Unpack(unk1File, unpackAddr);
        unpackAddr = (unpackAddr + 1) & 0xFFFF_FFFEu;

        unpackAddr += Unpack(unk2File, unpackAddr);
        unpackAddr = (unpackAddr + 1) & 0xFFFF_FFFEu;

        _sfxBaseAddr = sfxFile;

        _animDataBaseAddr = unpackAddr;
        unpackAddr += Unpack(anmFile, unpackAddr);
        unpackAddr = (unpackAddr + 1) & 0xFFFF_FFFEu;

        uint objectCount = ReadSdramU32(_animDataBaseAddr);
        objectCount = Math.Min(objectCount, 255);
        Array.Clear(_animMaxIndex);
        for (uint obj = 1; obj <= objectCount; obj++)
        {
            uint animOffset = ReadAnimU32(obj);
            ushort animCount = 0;
            while (animCount < 0x400 && ReadAnimU32((animOffset >> 2) + animCount) != 0xFFFF_FFFFu)
                animCount++;
            _animMaxIndex[obj - 1] = animCount == 0 ? (ushort)0 : (ushort)(animCount - 1);
        }

        _gfxBlocksBaseAddr = blkFile;
        _bgmUnpackAddr = unpackAddr;

        if (TracePaprium)
            Console.WriteLine($"[PAPRIUM] setup bgm=0x{bgmFile:X6} anm=0x{anmFile:X6} blk=0x{blkFile:X6} unpackEnd=0x{unpackAddr:X6}");
    }

    private uint Unpack(uint sourceAddr, uint destAddr, bool isScaleStamp = false)
    {
        uint initialDest = destAddr;
        byte first = RomPackedByte(sourceAddr++);
        if (first == 0x80)
        {
            byte code;
            while ((code = RomPackedByte(sourceAddr++)) != 0)
            {
                int count = code & 0x3F;
                switch (code >> 6)
                {
                    case 0:
                        while (count-- > 0)
                            PackedWriteByte(destAddr++, RomPackedByte(sourceAddr++), isScaleStamp);
                        break;
                    case 1:
                    {
                        byte data = RomPackedByte(sourceAddr++);
                        while (count-- > 0)
                            PackedWriteByte(destAddr++, data, isScaleStamp);
                        break;
                    }
                    case 2:
                    {
                        uint copyAddr = destAddr - RomPackedByte(sourceAddr++);
                        while (count-- > 0)
                            PackedWriteByte(destAddr++, PackedReadByte(copyAddr++, isScaleStamp), isScaleStamp);
                        break;
                    }
                    case 3:
                        while (count-- > 0)
                            PackedWriteByte(destAddr++, 0, isScaleStamp);
                        break;
                }
            }
        }
        else if (first == 0x81)
        {
            byte code;
            while ((code = RomPackedByte(sourceAddr++)) != 0x11)
            {
                uint copyAddr = 0;
                int copySize;
                int literalSize;
                switch (code >> 4)
                {
                    case 0:
                        copySize = 0;
                        literalSize = code != 0 ? 3 + (code & 0x1F) : 0x12 + RomPackedByte(sourceAddr++);
                        break;
                    case 1:
                        copySize = 2 + (code & 0x7);
                        if (copySize == 2)
                            copySize = 9 + RomPackedByte(sourceAddr++);
                        literalSize = RomPackedByte(sourceAddr) & 0x3;
                        copyAddr = destAddr - 0x4000 - ((((uint)RomPackedByte(sourceAddr + 1) << 8) + RomPackedByte(sourceAddr)) >> 2);
                        sourceAddr += 2;
                        break;
                    case 2:
                    case 3:
                        copySize = code & 0x1F;
                        if (copySize != 0)
                        {
                            copySize += 2;
                        }
                        else
                        {
                            copySize = 0x21;
                            while (RomPackedByte(sourceAddr++) == 0)
                                copySize += 0xFF;
                            copySize += RomPackedByte(sourceAddr - 1);
                        }
                        literalSize = RomPackedByte(sourceAddr) & 0x3;
                        copyAddr = destAddr - 1 - ((((uint)RomPackedByte(sourceAddr + 1) << 8) + RomPackedByte(sourceAddr)) >> 2);
                        sourceAddr += 2;
                        break;
                    default:
                        copySize = (code >> 5) + 1;
                        literalSize = code & 0x3;
                        copyAddr = destAddr - 1 - (((uint)((code >> 2) & 0x7)) + ((uint)RomPackedByte(sourceAddr) << 3));
                        sourceAddr++;
                        break;
                }

                while (copySize-- > 0)
                    PackedWriteByte(destAddr++, PackedReadByte(copyAddr++, isScaleStamp), isScaleStamp);
                while (literalSize-- > 0)
                    PackedWriteByte(destAddr++, RomPackedByte(sourceAddr++), isScaleStamp);
            }
        }
        else if (TracePaprium)
        {
            Console.WriteLine($"[PAPRIUM] unknown packer 0x{first:X2} at 0x{sourceAddr - 1:X6}");
        }

        return destAddr - initialDest;
    }

    private void VramSetBudget(ushort blocks)
    {
        if (blocks > 0x35)
            blocks = 0x35;
        _vramMaxSlot = blocks;
        VramResetBlocks(blocks);
    }

    private void VramResetBlocks(int first)
    {
        first = Math.Clamp(first, 0, MaxVramSlots);
        for (int i = first; i < MaxVramSlots; i++)
            _vramSlots[i] = default;
    }

    private ushort VramFindBlock(ushort num)
    {
        for (ushort x = 0; x < _vramMaxSlot; x++)
        {
            if (_vramSlots[x].BlockNum == num)
                return (ushort)((x + (x <= 0x30 ? 1 : 0x4B)) << 4);
        }
        return 0;
    }

    private ushort VramLoadBlock(ushort num)
    {
        if (num == 0)
            return 0;

        for (ushort x = 0; x < _vramMaxSlot; x++)
        {
            if (_vramSlots[x].BlockNum == num)
            {
                _vramSlots[x].Usage++;
                _vramSlots[x].Age = 0;
                return (ushort)((x + (x <= 0x30 ? 1 : 0x4B)) << 4);
            }
        }

        if (GetWord(DmaRemainingOffset) < 0x110)
            return 0;

        uint maxAge = 0;
        int blockIndex = -1;
        for (ushort x = 0; x < _vramMaxSlot; x++)
        {
            if (_vramSlots[x].Usage == 0 && _vramSlots[x].Age > maxAge)
            {
                maxAge = _vramSlots[x].Age;
                blockIndex = x;
            }
        }

        if (blockIndex < 0)
            return 0;

        _vramSlots[blockIndex].BlockNum = num;
        _vramSlots[blockIndex].Usage++;
        _vramSlots[blockIndex].Age = 0;

        Unpack(BlockAddr(num), _blockUnpackAddr);
        _blockUnpackAddr += 0x200;

        int dma = DmaEntryOffset(IncDmaCommandsCount());
        SetWord(dma + 0x00, 0x8F02);
        SetWord(dma + 0x02, 0x9401);
        SetWord(dma + 0x04, 0x9300);
        SetWord(dma + 0x06, 0x9700);
        SetWord(dma + 0x08, 0x9660);
        SetWord(dma + 0x0A, 0x9500);
        SetWord(DmaRemainingOffset, (ushort)(GetWord(DmaRemainingOffset) - 0x110));

        uint translated = (uint)(blockIndex + (blockIndex <= 0x30 ? 1 : 0x4B));
        uint command = (((translated << 25) | (translated >> 5)) & 0x3FFF_0003u) | 0x4000_0080u;
        SetWord(dma + 0x0C, (ushort)(command >> 16));
        SetWord(dma + 0x0E, (ushort)command);

        return (ushort)(translated << 4);
    }

    private void ObjReset()
    {
        VramResetBlocks(0);
        Array.Clear(_dualPort, ObjOffset / 2, 0x400 / 2);
        _drawListCount = 0;
    }

    private void ObjAdd(byte num)
    {
        if (_drawListCount < _drawList.Length)
            _drawList[_drawListCount++] = num;
    }

    private void ObjFrameStart()
    {
        _drawListCount = 0;
        for (int i = 0; i < MaxVramSlots; i++)
            _vramSlots[i].Usage = 0;
    }

    private void ObjFrameEnd()
    {
        _blockUnpackAddr = 0x9000;
        SetWord(DmaRemainingOffset, (ushort)(GetWord(DmaBudgetOffset) - GetWord(DmaTotalOffset)));

        for (int i = 0; i < _drawListCount; i++)
            ObjRender(_drawList[i]);

        for (int i = 0; i < MaxVramSlots; i++)
        {
            if (_vramSlots[i].Usage == 0)
                _vramSlots[i].Age++;
        }

        CloseSpriteTable();
        _sdramPointerWord = 0x9000 >> 1;
    }

    private void CloseSpriteTable()
    {
        ushort satCount = GetWord(SatCountOffset);
        if (satCount == 0)
        {
            int sat = SatEntryOffset(0);
            SetWord(sat + 0x00, 0x0010);
            SetWord(sat + 0x02, 0x0000);
            SetWord(sat + 0x04, 0x0000);
            SetWord(sat + 0x06, 0x0010);
            SetWord(SatCountOffset, 1);
            satCount = 1;
        }
        else
        {
            int prev = SatEntryOffset(satCount - 1);
            SetWord(prev + 0x02, (ushort)(GetWord(prev + 0x02) & 0xFF00));
        }

        int dma = DmaEntryOffset(IncDmaCommandsCount());
        SetWord(dma + 0x00, 0x8F02);
        ushort wordSize = (ushort)(GetWord(SatCountOffset) * 4);
        SetWord(dma + 0x02, (ushort)(0x9400 + (wordSize >> 8)));
        SetWord(dma + 0x04, (ushort)(0x9300 + (wordSize & 0xFF)));
        uint satAddr = SatOffset / 2u;
        SetWord(dma + 0x06, (ushort)(0x9700 + ((satAddr >> 16) & 0xFF)));
        SetWord(dma + 0x08, (ushort)(0x9600 + ((satAddr >> 8) & 0xFF)));
        SetWord(dma + 0x0A, (ushort)(0x9500 + (satAddr & 0xFF)));
        SetWord(dma + 0x0C, 0x7000);
        SetWord(dma + 0x0E, 0x0083);
    }

    private void ObjRender(byte objSlot)
    {
        if (objSlot >= ObjectsCount || _animDataBaseAddr == 0)
            return;

        int obj = ObjEntryOffset(objSlot);
        ushort objId = GetWord(obj + 0x04);
        ushort anim = GetWord(obj + 0x00);
        if ((anim & 0xFF) > _animMaxIndex[objId & 0xFF])
            return;

        ref ObjectHandle handle = ref _objectHandles[objSlot];
        uint previousOffset = handle.AnimOffset;
        ushort previousCounter = handle.Counter;
        uint offset;
        uint dataOffset;
        ushort animCounter = GetWord(obj + 0x0A);

        if ((objId & 0x8000) != 0 || anim != handle.CurrentAnim || animCounter != handle.Counter)
        {
            if ((objId & 0x8000) != 0)
            {
                previousOffset = 0;
                previousCounter = 1;
            }

            offset = ReadAnimU32((uint)((objId & 0xFF) + 1));
            offset = ReadAnimU32((offset >> 2) + (uint)(anim & 0xFF));
            dataOffset = ReadAnimU32(offset >> 2) & 0x00FF_FFFFu;
            handle.AnimOffset = offset;
            handle.CurrentAnim = anim;
            handle.Counter = animCounter;
        }
        else
        {
            offset = handle.AnimOffset;
            if (offset == 0)
                return;

            dataOffset = ReadAnimU32(offset >> 2);
            if ((dataOffset & 0x8000_0000u) != 0)
            {
                offset += 4;
            }
            else
            {
                ushort nextAnim = GetWord(obj + 0x02);
                if (nextAnim != 0xFFFF)
                {
                    SetWord(obj + 0x00, nextAnim);
                    SetWord(obj + 0x02, 0xFFFF);
                    ObjRender(objSlot);
                    return;
                }
                offset = ReadAnimU32((offset + 4) >> 2) & 0x00FF_FFFFu;
            }

            if (offset == 0)
                return;
            handle.AnimOffset = offset;
            dataOffset = ReadAnimU32(offset >> 2) & 0x00FF_FFFFu;
            SetWord(obj + 0x0A, (ushort)(animCounter + 1));
            handle.Counter++;
        }

        uint spriteBase = _animDataBaseAddr + dataOffset;
        int count = RawSdramByte(spriteBase + 1);
        short posX = unchecked((short)GetWord(obj + 0x0C));
        short posY = unchecked((short)GetWord(obj + 0x0E));
        ushort attrsObj = GetWord(obj + 0x08);
        bool blocksAvailable = true;

        for (int i = 0; i < count; i++)
        {
            uint spr = spriteBase + 2u + (uint)(i * 8);
            ushort blockNum = ReadSdramWordAtRawStruct(spr + 4);
            if (blockNum == 0)
                continue;
            if (VramLoadBlock(blockNum) == 0)
                blocksAvailable = false;
        }

        if (!blocksAvailable)
        {
            if (previousOffset == 0)
                return;
            handle.AnimOffset = previousOffset;
            handle.Counter = previousCounter;
            SetWord(obj + 0x0A, previousCounter);
            dataOffset = ReadAnimU32(previousOffset >> 2) & 0x00FF_FFFFu;
            spriteBase = _animDataBaseAddr + dataOffset;
            count = RawSdramByte(spriteBase + 1);
        }

        for (int i = 0; i < count; i++)
        {
            uint spr = spriteBase + 2u + (uint)(i * 8);
            sbyte relY = unchecked((sbyte)RawSdramByte(spr + 0));
            sbyte relX = unchecked((sbyte)RawSdramByte(spr + 1));
            sbyte flipRelX = unchecked((sbyte)RawSdramByte(spr + 2));
            byte size = RawSdramByte(spr + 3);
            ushort blockNum = ReadSdramWordAtRawStruct(spr + 4);
            byte offsetTile = RawSdramByte(spr + 6);
            byte attrs = RawSdramByte(spr + 7);

            posX += (short)(((attrsObj & 0x0800) != 0) ? flipRelX : relX);
            posY += relY;
            if (blockNum == 0)
                continue;

            int width = (((size >> 2) & 0x3) + 1) * 8;
            int height = ((size & 0x3) + 1) * 8;
            if (posX >= 448 || posY >= 368 || posX < 128 - width || posY < 128 - height)
                continue;

            ushort satCount = GetWord(SatCountOffset);
            if (satCount >= 144)
                break;

            ushort nextCount = (ushort)(satCount + 1);
            SetWord(SatCountOffset, nextCount);
            int sat = SatEntryOffset(satCount);
            SetWord(sat + 0x06, (ushort)(posX & 0x01FF));
            SetWord(sat + 0x00, (ushort)(posY & 0x03FF));
            SetWord(sat + 0x02, (ushort)(((size & 0x0F) << 8) | (nextCount & 0xFF)));
            SetWord(sat + 0x04, (ushort)(((attrs & 0xF8) << 8) ^ attrsObj ^ (VramFindBlock(blockNum) + offsetTile)));
        }

        SetWord(obj + 0x04, (ushort)(objId & 0x7FFF));
    }

    private void StampRescale(ushort windowStart, ushort windowEnd, ushort factor, ushort stampOffset)
    {
        byte[] scaled = new byte[128 * 32];
        float offset = stampOffset;
        float adder = factor / 64.0f;
        for (int y = windowStart; y < windowEnd && y < 128; y++, offset += adder)
        {
            int srcY = Math.Clamp((int)offset, 0, 63);
            Buffer.BlockCopy(_scaleStamp, srcY * 32, scaled, y * 32, 32);
        }

        for (int s = 0; s < 32; s++)
        {
            int column = ((s & 0xFE) << 4) + ((s & 1) << 9);
            for (int y = 0; y < 32; y++)
            {
                ushort word = (ushort)(
                    ((((scaled[((s << 2) + 0) * 32 + (y ^ 1)] & 0xF0) |
                       (scaled[((s << 2) + 1) * 32 + (y ^ 1)] & 0x0F)) << 8) |
                     (((scaled[((s << 2) + 2) * 32 + (y ^ 1)] & 0xF0) |
                       (scaled[((s << 2) + 3) * 32 + (y ^ 1)] & 0x0F)))));
                SetWord(0x200 + (column + y) * 2, word);
            }
        }
    }

    private void LoadEepromBlock(int block)
    {
        ushort dest = GetCommandArg(0);
        switch (block)
        {
            case 1:
            case 2:
            case 3:
                CopyWords(_nvram, (0x200 + block * 0x200) / 2, _dualPort, dest / 2, 0x100 / 2);
                break;
            case 4:
                CopyWords(_nvram, 0, _dualPort, dest / 2, 0x200 / 2);
                break;
        }
    }

    private void SaveEepromBlock(int block)
    {
        ushort src = GetCommandArg(1);
        switch (block)
        {
            case 1:
            case 2:
            case 3:
                CopyWords(_dualPort, src / 2, _nvram, (0x200 + block * 0x200) / 2, 0x100 / 2);
                break;
            case 4:
                CopyWords(_dualPort, src / 2, _nvram, 0, 0x200 / 2);
                break;
        }
        SetWord(RegStatus2Offset, (ushort)(GetWord(RegStatus2Offset) & ~Status2EepromError1 & ~Status2EepromError2));
        SaveNvram();
    }

    private uint BlockAddr(ushort num)
    {
        return _gfxBlocksBaseAddr + SwapShorts(ReadRomU32AtRawStruct(_gfxBlocksBaseAddr + (uint)num * 4));
    }

    private uint BgmAddr(int num)
    {
        return _bgmTracksBaseAddr + SwapShorts(ReadRomU32AtRawStruct(_bgmTracksBaseAddr + (uint)num * 4));
    }

    private uint ReadAnimU32(uint index)
    {
        return ReadSdramU32(_animDataBaseAddr + index * 4);
    }

    private uint ReadSdramU32(uint byteAddr)
    {
        return ((uint)ReadSdramWord(byteAddr + 2) << 16) | ReadSdramWord(byteAddr);
    }

    private ushort ReadSdramWord(uint byteAddr)
    {
        int index = (int)(byteAddr >> 1);
        if ((uint)index >= _sdram.Length)
            return 0;
        return _sdram[index];
    }

    private ushort ReadSdramWordAtRawStruct(uint byteAddr)
    {
        byte loRaw = RawSdramByte(byteAddr);
        byte hiRaw = RawSdramByte(byteAddr + 1);
        return (ushort)(loRaw | (hiRaw << 8));
    }

    private uint ReadRomU32AtRawStruct(uint byteAddr)
    {
        uint b0 = RomRawByte(byteAddr);
        uint b1 = RomRawByte(byteAddr + 1);
        uint b2 = RomRawByte(byteAddr + 2);
        uint b3 = RomRawByte(byteAddr + 3);
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    private ushort GetCommandArg(int index) => GetWord(CommandArgsOffset + index * 2);

    private uint GetCommandArgLong(int index)
    {
        int offset = CommandArgsOffset + index * 4;
        uint lo = GetWord(offset);
        uint hi = GetWord(offset + 2);
        return lo | (hi << 16);
    }

    private ushort IncDmaCommandsCount()
    {
        ushort count = GetWord(DmaCommandsCountOffset);
        SetWord(DmaCommandsCountOffset, (ushort)(count + 1));
        return count;
    }

    private static int DmaEntryOffset(int index) => DmaCommandsOffset + index * 16;
    private static int SatEntryOffset(int index) => SatOffset + index * 8;
    private static int ObjEntryOffset(int index) => ObjOffset + index * 16;

    private ushort GetWord(int byteOffset) => _dualPort[byteOffset >> 1];

    private void SetWord(int byteOffset, ushort value)
    {
        if ((uint)(byteOffset >> 1) < _dualPort.Length)
            _dualPort[byteOffset >> 1] = value;
    }

    private void DecodeAndPatchOnce()
    {
        if (_decoded || _romWords.Length < 0x800000 / 2)
            return;

        if (_romWords[0x8000 / 2] != 0)
        {
            ushort key1 = _romWords[0x8000 / 2];
            ushort key2 = _romWords[0xBD000 / 2];
            for (uint addr = 0x2000 / 2; addr < 0x10000 / 2; addr++)
                _romWords[addr] ^= (ushort)(key1 | BitswapPaprium(addr & 0xFF));
            for (uint addr = 0x10000 / 2; addr < 0x800000 / 2; addr++)
                _romWords[addr] ^= (ushort)(key2 | BitswapPaprium(addr & 0xFF));
        }

        if (_romWords.Length > 0x1000A / 2 && _romWords[0x1000A / 2] != 0x2E7F && TracePaprium)
        {
            Console.WriteLine($"[PAPRIUM] unknown version 0x{_romWords[0x1000A / 2]:X4}");
        }

        _decoded = true;
    }

    private void RestoreBootDualPort()
    {
        Array.Copy(_romWords, _dualPort, Math.Min(_dualPort.Length, _romWords.Length));
    }

    private void ApplyVersionPatches()
    {
        if (_romWords.Length <= 0x81104 / 2 || _romWords[0x1000A / 2] != 0x2E7F)
            return;

        SetWord(0x1D1C, 0x0004);
        SetWord(0x1D2C, (ushort)(GetWord(0x1D2C) | 0x0100));
        SetWord(0x1560, 0x4EF9);
        SetWord(0x1562, 0x0001);
        SetWord(0x1564, 0x0100);
        _romWords[0x81104 / 2] = 0x4E71;
    }

    private static ushort BitswapPaprium(uint value)
    {
        int[] bits = { 15, 1, 14, 6, 13, 2, 12, 0, 11, 3, 10, 4, 9, 7, 8, 5 };
        uint result = 0;
        for (int i = 0; i < bits.Length; i++)
            result |= ((value >> bits[i]) & 1u) << (15 - i);
        return (ushort)result;
    }

    private byte RomPackedByte(uint logicalByteAddr) => RomRawByte(logicalByteAddr ^ 1);

    private byte RomRawByte(uint rawByteAddr)
    {
        int word = (int)(rawByteAddr >> 1);
        if ((uint)word >= _romWords.Length)
            return 0xFF;
        ushort value = _romWords[word];
        return (rawByteAddr & 1) == 0 ? (byte)value : (byte)(value >> 8);
    }

    private byte PackedReadByte(uint logicalByteAddr, bool scaleStamp)
    {
        uint raw = logicalByteAddr ^ 1;
        if (scaleStamp)
            return raw < _scaleStamp.Length ? _scaleStamp[raw] : (byte)0;
        return RawReadByte(_sdram, (int)raw);
    }

    private void PackedWriteByte(uint logicalByteAddr, byte value, bool scaleStamp)
    {
        uint raw = logicalByteAddr ^ 1;
        if (scaleStamp)
        {
            if (raw < _scaleStamp.Length)
                _scaleStamp[raw] = value;
            return;
        }
        RawWriteByte(_sdram, (int)raw, value);
    }

    private byte RawSdramByte(uint rawByteAddr) => RawReadByte(_sdram, (int)rawByteAddr);

    private static byte CpuReadByte(ushort[] words, int byteAddress)
    {
        return RawReadByte(words, byteAddress ^ 1);
    }

    private static byte RawReadByte(ushort[] words, int rawByteAddress)
    {
        int index = rawByteAddress >> 1;
        if ((uint)index >= words.Length)
            return 0xFF;
        ushort value = words[index];
        return (rawByteAddress & 1) == 0 ? (byte)value : (byte)(value >> 8);
    }

    private static void RawWriteByte(ushort[] words, int rawByteAddress, byte value)
    {
        int index = rawByteAddress >> 1;
        if ((uint)index >= words.Length)
            return;
        ushort old = words[index];
        words[index] = (rawByteAddress & 1) == 0
            ? (ushort)((old & 0xFF00) | value)
            : (ushort)((old & 0x00FF) | (value << 8));
    }

    private static ushort ReadWord(ushort[] words, int byteOffset)
    {
        int index = byteOffset >> 1;
        return (uint)index < words.Length ? words[index] : (ushort)0xFFFF;
    }

    private static ushort[] ToWords(byte[] romBytes)
    {
        int wordCount = Math.Max(0x800000 / 2, (romBytes.Length + 1) / 2);
        ushort[] words = new ushort[wordCount];
        for (int i = 0; i < words.Length; i++)
        {
            int b = i * 2;
            byte hi = b < romBytes.Length ? romBytes[b] : (byte)0xFF;
            byte lo = b + 1 < romBytes.Length ? romBytes[b + 1] : (byte)0xFF;
            words[i] = (ushort)((hi << 8) | lo);
        }
        return words;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SwapShorts(uint value) => ((value & 0xFFFF0000u) >> 16) | ((value & 0x0000FFFFu) << 16);

    private static void CopyWords(ushort[] source, int sourceIndex, ushort[] dest, int destIndex, int count)
    {
        if (sourceIndex < 0 || destIndex < 0 || count <= 0)
            return;
        int sourceAvailable = source.Length - sourceIndex;
        int destAvailable = dest.Length - destIndex;
        int copy = Math.Min(count, Math.Min(sourceAvailable, destAvailable));
        if (copy > 0)
            Array.Copy(source, sourceIndex, dest, destIndex, copy);
    }

    private void LoadNvram()
    {
        if (string.IsNullOrWhiteSpace(_savePath) || !File.Exists(_savePath))
            return;
        try
        {
            byte[] data = File.ReadAllBytes(_savePath);
            int words = Math.Min(_nvram.Length, data.Length / 2);
            for (int i = 0; i < words; i++)
                _nvram[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
        }
        catch { }
    }

    private void SaveNvram()
    {
        if (string.IsNullOrWhiteSpace(_savePath))
            return;
        try
        {
            byte[] data = new byte[_nvram.Length * 2];
            for (int i = 0; i < _nvram.Length; i++)
            {
                data[i * 2] = (byte)(_nvram[i] >> 8);
                data[i * 2 + 1] = (byte)_nvram[i];
            }
            File.WriteAllBytes(_savePath, data);
        }
        catch { }
    }

    private static string? BuildSavePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;
        try
        {
            return Path.ChangeExtension(sourcePath, ".paprium.srm");
        }
        catch
        {
            return null;
        }
    }

    private struct VramSlot
    {
        public ushort BlockNum;
        public ushort Usage;
        public uint Age;
    }

    private struct ObjectHandle
    {
        public uint AnimOffset;
        public ushort CurrentAnim;
        public ushort Counter;
    }
}
