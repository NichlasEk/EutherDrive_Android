using System.Runtime.CompilerServices;

namespace KSNES.Specialchips.SuperFX;

internal sealed class CodeCache
{
    private const int CodeCacheRamLen = 512;
    private readonly byte[] _ram = new byte[CodeCacheRamLen];
    private ushort _cbr;
    private uint _cachedLines;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PcIsCacheable(ushort address)
        => (ushort)(address - _cbr) < CodeCacheRamLen;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte? Get(ushort address)
        => TryGetCacheable(address, out byte value) ? value : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetCacheable(ushort address, out byte value)
    {
        int cacheAddr = (ushort)(address - _cbr);
        if ((uint)cacheAddr >= CodeCacheRamLen)
        {
            value = 0;
            return false;
        }

        int cacheLineBit = cacheAddr >> 4;
        if (((_cachedLines >> cacheLineBit) & 1) == 0)
        {
            value = 0;
            return false;
        }

        value = _ram[cacheAddr];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(ushort address, out byte value)
    {
        ushort cacheAddr = (ushort)(address - _cbr);
        int cacheLineBit = cacheAddr >> 4;
        if (((_cachedLines >> cacheLineBit) & 1) == 0)
        {
            value = 0;
            return false;
        }

        value = _ram[cacheAddr & 0x1FF];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ushort address, byte value)
    {
        ushort cacheAddr = (ushort)(address - _cbr);
        _ram[cacheAddr & 0x1FF] = value;

        if ((address & 0xF) == 0xF)
            SetCacheLine(cacheAddr);
    }

    private void SetCacheLine(ushort cacheAddr)
    {
        uint cacheLine = 1u << (cacheAddr >> 4);
        _cachedLines |= cacheLine;
    }

    public byte ReadRam(ushort address) => _ram[address & 0x1FF];

    public void WriteRam(ushort address, byte value)
    {
        ushort cacheAddr = (ushort)(address & 0x1FF);
        _ram[cacheAddr] = value;

        if ((address & 0xF) == 0xF)
            SetCacheLine(cacheAddr);
    }

    public void UpdateCbr(ushort cbr)
    {
        _cbr = cbr;
        _cachedLines = 0;
    }

    public void FullClear()
    {
        _cbr = 0;
        _cachedLines = 0;
    }

    public ushort Cbr => _cbr;

    public uint CachedLines => _cachedLines;
}
