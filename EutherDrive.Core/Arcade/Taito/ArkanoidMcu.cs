// SPDX-License-Identifier: BSD-3-Clause
// Hardware/opcode reference: MAME m6805 and taito68705 (Aaron Giles, David Haywood, Vas Crabb).
namespace EutherDrive.Core.Arcade.Taito;

// MC68705P5 execution and Arkanoid's two hardware mailboxes. No game-specific responses.
internal sealed class ArkanoidMcu
{
    [NonSerialized] private readonly byte[] rom;
    private readonly byte[] ram = new byte[128], ports = new byte[3], ddr = new byte[3];
    private int pc, sp, a, x, cc, pcPins = 255;
    private byte hostLatch = 255, reply = 255;
    public bool HostPending { get; private set; }
    public bool ReplyPending { get; private set; }
    public bool Enabled { get; private set; }
    public byte Paddle;
    public int Pc => pc;
    public long Instructions { get; private set; }
    public ArkanoidMcu(byte[] image) { rom = image; Reset(); }
    private int Word(int addr) => (Read(addr) << 8 | Read(addr + 1)) & 2047;
    public void Reset()
    {
        Array.Clear(ram); Array.Fill(ports, (byte)255); Array.Clear(ddr);
        pcPins = 255; sp = 127; a = x = 0; cc = 8; pc = Word(2046);
        HostPending = ReplyPending = Enabled = false; hostLatch = reply = 255; Instructions = 0;
        Paddle = 0;
    }
    public void Enable(bool value)
    {
        if (!value && Enabled) Reset();
        Enabled = value;
    }
    public void HostWrite(byte value) { hostLatch = value; HostPending = Enabled; }
    public byte HostRead() { if ((pcPins & 8) != 0) ReplyPending = false; return reply; }
    private int Pa => (ports[0] | ~ddr[0]) & ((pcPins & 4) == 0 ? hostLatch : 255) & 255;
    private int Read(int addr)
    {
        addr &= 2047;
        if (addr < 3)
        {
            int input = addr switch { 0 => (pcPins & 4) == 0 ? hostLatch : 255, 1 => Paddle,
                _ => (HostPending ? 1 : 0) | (ReplyPending ? 0 : 2) | 252 };
            return ((ports[addr] & ddr[addr]) | (input & ~ddr[addr])) & 255;
        }
        if (addr < 16) return 255; // unused peripheral registers; timer not used by these MCU ROMs
        return addr < 128 ? ram[addr] : rom[addr];
    }
    private void Write(int addr, int value)
    {
        addr &= 2047; value &= 255;
        if (addr < 3 || addr is >= 4 and <= 6)
        {
            int oldPa = Pa;
            if (addr < 3) ports[addr] = (byte)value; else ddr[addr - 4] = (byte)value;
            if (addr is 2 or 6)
            {
                int pins = (ports[2] | ~ddr[2]) & 255;
                if ((pins & 4) != 0 && (pcPins & 4) == 0) HostPending = false;
                if ((pins & 8) == 0)
                {
                    ReplyPending = Enabled;
                    if ((pcPins & 8) != 0) reply = (byte)oldPa;
                }
                pcPins = pins;
            }
        }
        else if (addr is >= 16 and < 128) ram[addr] = (byte)value;
    }
    private int Fetch() { int v = Read(pc); pc = (pc + 1) & 2047; return v; }
    private void Push(int v) { ram[sp] = (byte)v; sp = 96 | ((sp - 1) & 31); }
    private int Pop() { sp = 96 | ((sp + 1) & 31); return ram[sp]; }
    private void PushPc() { Push(pc); Push(pc >> 8); }
    private void PopPc() { pc = ((Pop() << 8) | Pop()) & 2047; }
    private int Nz(int v) { v &= 255; cc = (cc & ~6) | (v == 0 ? 2 : 0) | ((v & 128) >> 5); return v; }
    private int Arithmetic(int lhs, int rhs, bool add, int carry = 0)
    {
        int v = add ? lhs + rhs + carry : lhs - rhs - carry;
        cc = (cc & ~1) | ((v & 256) >> 8);
        if (add) cc = (cc & ~16) | ((lhs ^ rhs ^ v) & 16);
        return Nz(v);
    }
    public int Step()
    {
        if (!Enabled) return 1;
        if (HostPending && (cc & 8) == 0)
        {
            PushPc(); Push(x); Push(a); Push(cc); cc |= 8; pc = Word(2042); return 11;
        }
        int op = Fetch(), lo = op & 15, hi = op >> 4;
        Instructions++;
        if (hi == 0)
        {
            int v = Read(Fetch()), rel = (sbyte)Fetch();
            bool bit = (v & (1 << (lo >> 1))) != 0;
            cc = (cc & ~1) | (bit ? 1 : 0);
            if (bit == ((lo & 1) == 0)) pc = (pc + rel) & 2047;
            return 10;
        }
        if (hi == 1)
        {
            int addr = Fetch(), v = Read(addr), mask = 1 << (lo >> 1);
            Write(addr, (lo & 1) == 0 ? v | mask : v & ~mask); return 7;
        }
        if (hi == 2)
        {
            int rel = (sbyte)Fetch();
            bool take = (lo >> 1) switch { 0 => true, 1 => (cc & 3) == 0,
                2 => (cc & 1) == 0, 3 => (cc & 2) == 0, 4 => (cc & 16) == 0,
                5 => (cc & 4) == 0, 6 => (cc & 8) == 0, _ => HostPending };
            if (take == ((lo & 1) == 0)) pc = (pc + rel) & 2047;
            return 4;
        }
        if (hi is >= 3 and <= 7)
        {
            int addr = hi switch { 3 => Fetch(), 6 => x + Fetch(), 7 => x, _ => 0 };
            int v = hi == 4 ? a : hi == 5 ? x : Read(addr), r = v, carry = cc & 1;
            switch (lo)
            {
                case 0: r = Arithmetic(0, v, false); break;
                case 3: r = Nz(~v); cc |= 1; break;
                case 4: cc = (cc & ~1) | (v & 1); r = Nz(v >> 1); break;
                case 6: cc = (cc & ~1) | (v & 1); r = Nz((v >> 1) | carry << 7); break;
                case 7: cc = (cc & ~1) | (v & 1); r = Nz((v >> 1) | (v & 128)); break;
                case 8: cc = (cc & ~1) | (v >> 7); r = Nz(v << 1); break;
                case 9: cc = (cc & ~1) | (v >> 7); r = Nz((v << 1) | carry); break;
                case 10: r = Nz(v - 1); break;
                case 12: r = Nz(v + 1); break;
                case 13: Nz(v); break;
                case 15: r = Nz(0); break;
                default: throw new InvalidOperationException($"68705 illegal {op:X2} at {pc - 1:X3}");
            }
            if (lo != 13) { if (hi == 4) a = r; else if (hi == 5) x = r; else Write(addr, r); }
            return hi is 4 or 5 ? 4 : hi == 6 ? 7 : 6;
        }
        switch (op)
        {
            case 0x80: cc = Pop(); a = Pop(); x = Pop(); PopPc(); return 9;
            case 0x81: PopPc(); return 6;
            case 0x83: PushPc(); Push(x); Push(a); Push(cc); cc |= 8; pc = Word(2044); return 11;
            case 0x97: x = a; return 2;
            case 0x98: cc &= ~1; return 2;
            case 0x99: cc |= 1; return 2;
            case 0x9a: cc &= ~8; return 2;
            case 0x9b: cc |= 8; return 2;
            case 0x9c: sp = 127; return 2;
            case 0x9d: return 2;
            case 0x9f: a = x; return 2;
            case 0xad: int rel = (sbyte)Fetch(); PushPc(); pc = (pc + rel) & 2047; return 8;
        }
        if (hi < 10) throw new InvalidOperationException($"68705 illegal {op:X2} at {pc - 1:X3}");
        int ea = hi switch { 10 => pc, 11 => Fetch(), 12 => Fetch() << 8 | Fetch(),
            13 => (Fetch() << 8 | Fetch()) + x, 14 => Fetch() + x, _ => x };
        int value = lo is 7 or 12 or 13 or 15 ? 0 : Read(ea);
        if (hi == 10) pc = (pc + 1) & 2047;
        switch (lo)
        {
            case 0: a = Arithmetic(a, value, false); break;
            case 1: Arithmetic(a, value, false); break;
            case 2: a = Arithmetic(a, value, false, cc & 1); break;
            case 3: Arithmetic(x, value, false); break;
            case 4: a = Nz(a & value); break;
            case 5: Nz(a & value); break;
            case 6: a = Nz(value); break;
            case 7: Write(ea, Nz(a)); break;
            case 8: a = Nz(a ^ value); break;
            case 9: a = Arithmetic(a, value, true, cc & 1); break;
            case 10: a = Nz(a | value); break;
            case 11: a = Arithmetic(a, value, true); break;
            case 12: pc = ea & 2047; break;
            case 13: PushPc(); pc = ea & 2047; break;
            case 14: x = Nz(value); break;
            case 15: Write(ea, Nz(x)); break;
        }
        int cycles = hi switch { 10 => 2, 11 or 15 => 4, 12 or 14 => 5, _ => 6 };
        return cycles + (lo is 7 or 15 ? 1 : lo == 12 ? -1 : lo == 13 ? 3 : 0);
    }
}
