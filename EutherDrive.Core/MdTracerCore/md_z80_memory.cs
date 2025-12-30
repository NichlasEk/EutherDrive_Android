using System;
using System.Diagnostics;
using System.Text;
using EutherDrive.Core.MdTracerCore;
using static EutherDrive.Core.MdTracerCore.md_m68k;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_z80
    {
        private byte[] g_ram = Array.Empty<byte>();
        private uint g_bank_register; // Z80 bankregister (9-bit index)
        private const int SmsLogLimit = 48;
        private int _smsLogCount;
        private byte _smsBankSelect;
        private const int SmsPortLogLimit = 16;
        private static int _smsPortBeReadLog;
        private static int _smsPortBfReadLog;
        private static int _smsPortBeWriteLog;
        private static int _smsPortBfWriteLog;
        private static int _smsPort7EWriteLog;
        private static int _smsPort7FWriteLog;
        private static bool _smsFirstBeWriteLogged;
        private static bool _smsFirstBfWriteLogged;
        private static long _smsStatusPollFrame = -1;
        private static bool _forceStatus7Logged;
        private int _ymWriteLogRemaining = 64;
        private int _z80BankRegLogRemaining = 16;
        private int _z80BankReadLogRemaining = 32;
        private int _z80MailboxReadLogRemaining = 256;
        private readonly byte[] _z80MailboxSnapshot = new byte[0x10];
        private readonly byte[] _mbxShadow = new byte[0x10];
        private bool _mbxShadowValid;
        private long _z80BankStatSecond = -1;
        private int _z80BankStatReadCount;
        private int _z80BankStatReadFfCount;
        private ushort _z80BankStatLastPc;
        private ushort _z80BankStatLastAddr;
        private static readonly bool ForceSmsStatus7 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_SMS_STATUS7"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYm =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Win =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN"), "1", StringComparison.Ordinal);
        private static bool TraceZ80Bank => TraceZ80Win || MbxSyncTrace.IsEnabled;
        private static bool TraceZ80Ym => MdTracerCore.MdLog.TraceZ80Ym;
        private static readonly bool TraceMbxSync =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MBXSYNC"), "1", StringComparison.Ordinal);
        private static readonly bool MirrorZ80Mailbox = ReadEnvDefaultOn("EUTHERDRIVE_MBX_MIRROR");
        private static readonly bool TraceZ80Vdp =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80VDP"), "1", StringComparison.Ordinal);

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        //----------------------------------------------------------------
        // read
        //----------------------------------------------------------------
        private uint GetBankBase()
        {
            return (g_bank_register & 0x1FFu) * 0x8000u;
        }

        public byte read8(uint in_address)
        {
            byte w_out = 0;
            ushort a = (ushort)(in_address & 0xFFFF);

            if (md_main.g_masterSystemMode)
            {
                byte result = ReadSms(a);
                LogSmsAccess("read", a, result);
                return result;
            }

            if (a < 0x4000)
            {
                // 8 KB Z80 RAM (0x0000..0x1FFF) speglad över 0x0000..0x3FFF
                w_out = g_ram[(ushort)(a & 0x1FFF)];
                if (a >= 0x1B80 && a <= 0x1B8F)
                {
                    if (MirrorZ80Mailbox)
                        w_out = MaybeMirrorMailboxRead(a, w_out);
                    w_out = MaybeApplyMailboxShadow(a, w_out);
                    if (MbxSyncTrace.IsEnabled)
                        TrackMailboxRead(a, w_out);
                }
                MbxSyncTrace.MaybeSyncOnZ80Read(a, w_out, DebugPc, g_ram);
            }
            else if (a <= 0x5FFF)
            {
                // YM2612
                if (a == 0x4000 || a == 0x4002)
                    w_out = md_main.g_md_music.g_md_ym2612.ReadStatus(false);
                else
                    w_out = md_main.g_md_music.g_md_ym2612.read8(a);
                if (TraceZ80Ym && (a == 0x4000 || a == 0x4002))
                    Console.WriteLine($"[Z80YM] read addr=0x{a:X4} -> 0x{w_out:X2}");
            }
            else if (a >= 0x6000 && a < 0x7F00)
            {
                // I/O/UB – returnera “öppet bussvärde”
                w_out = 0xFF;
            }
            else if (a >= 0x7F00 && a <= 0x7FFF)
            {
                // VDP via 68k-bussen (0xC00000 + låga 5 bitar)
                uint vdpAddr = 0xC00000u + (uint)(a & 0x1F);
                if (md_main.g_md_bus != null)
                    w_out = md_main.g_md_bus.read8(vdpAddr);
                else if (md_main.g_md_vdp != null)
                    w_out = md_main.g_md_vdp.read8(vdpAddr);
                else
                    w_out = 0xFF;
                if (TraceZ80Vdp)
                    Console.WriteLine($"[Z80VDP] read addr=0x{a:X4} -> 0x{w_out:X2} vdp=0x{vdpAddr:X6}");
            }
            else if (a >= 0x8000)
            {
                // 68k bankfönster, 0x8000..0xFFFF => 32 KB
                // Maskera alltid till 32KB offset och OR:a med bankbasen.
                uint bankBase = GetBankBase();
                uint m68kAddr = bankBase | (uint)(a & 0x7FFF);
                if (md_main.g_md_bus != null)
                    w_out = md_main.g_md_bus.read8(m68kAddr);
                else
                    w_out = md_m68k.read8(m68kAddr);
                if (TraceZ80Bank)
                    UpdateBankStat(a, w_out);
                if (TraceZ80Bank && _z80BankReadLogRemaining > 0)
                {
                    _z80BankReadLogRemaining--;
                    Console.WriteLine($"[Z80BANKRD] pc=0x{DebugPc:X4} z80addr=0x{a:X4} val=0x{w_out:X2} bank=0x{bankBase:X6}");
                }
            }
            else
            {
                MessageBox.Show("md_z80_memory.read8", "error");
            }

            return w_out;
        }

        public ushort read16(uint in_address)
        {
            // Läs via read8 så MMIO-sidoeffekter och wrapping blir korrekt
            ushort a = (ushort)(in_address & 0xFFFF);
            byte hi = read8(a);
            byte lo = read8((ushort)(a + 1));
            return (ushort)((hi << 8) | lo);
        }

        public uint read32(uint in_address)
        {
            ushort a = (ushort)(in_address & 0xFFFF);
            byte b3 = read8(a);
            byte b2 = read8((ushort)(a + 1));
            byte b1 = read8((ushort)(a + 2));
            byte b0 = read8((ushort)(a + 3));
            return (uint)((b3 << 24) | (b2 << 16) | (b1 << 8) | b0);
        }

        //----------------------------------------------------------------
        // write
        //----------------------------------------------------------------
        public void write8(uint in_address, byte in_data)
        {
            ushort a = (ushort)(in_address & 0xFFFF);

            if (md_main.g_masterSystemMode)
            {
                LogSmsAccess("write", a, in_data);
                if (HandleSmsPortWrite(a, in_data))
                    return;
                if (a >= 0xC000)
                {
                    g_ram[(ushort)(a & 0x1FFF)] = in_data;
                    return;
                }
                if (a < 0x4000)
                    return;
            }
            if (a < 0x4000)
            {
                // 8 KB Z80 RAM (0x0000..0x1FFF) speglad över 0x0000..0x3FFF
                g_ram[(ushort)(a & 0x1FFF)] = in_data;
                if (a >= 0x1B80 && a <= 0x1B8F)
                {
                    if (MirrorZ80Mailbox)
                        MaybeMirrorMailboxWriteZ80(a, in_data);
                    if (MbxSyncTrace.IsEnabled)
                    {
                        string dump = BuildMailboxDump();
                        Console.WriteLine($"[Z80MBXWR] pc={DebugPc:X4} addr={a:X4} val={in_data:X2} dump={dump}");
                    }
                }
                return;
            }
            else if (a >= 0x4000 && a <= 0x5FFF)
            {
                // YM2612
                md_main.g_md_music.g_md_ym2612.write8(a, in_data);
                if (TraceYm && _ymWriteLogRemaining > 0)
                {
                    _ymWriteLogRemaining--;
                    Console.WriteLine($"[YMTRACE] Z80 pc=0x{DebugPc:X4} addr=0x{a:X4} val=0x{in_data:X2}");
                }
                if (TraceZ80Ym)
                    Console.WriteLine($"[Z80YM] write addr=0x{a:X4} val=0x{in_data:X2}");
            }
            else if (a >= 0x6000 && a <= 0x60FF)
            {
                // Z80 bank register till 68k-bussen (9-bit skiftregister).
                // Värde bit0 skiftas in i bit8 varje skrivning.
                uint newBank = (g_bank_register >> 1) & 0x1FFu;
                if ((in_data & 0x01) != 0)
                    newBank |= 0x100u;
                if (TraceZ80Bank && _z80BankRegLogRemaining > 0)
                {
                    _z80BankRegLogRemaining--;
                    uint bankBase = (newBank & 0x1FFu) * 0x8000u;
                    Console.WriteLine($"[Z80BANKREG] pc=0x{DebugPc:X4} addr=0x{a:X4} val=0x{in_data:X2} bank=0x{bankBase:X6}");
                }
                g_bank_register = newBank;
            }
            else if (a >= 0x6100 && a <= 0x7EFF)
            {
                // “nothing”
            }
            else if (a == 0x7F11)
            {
                // SN76489 PSG
                md_psg_trace.TraceWrite("Z80", a, in_data, DebugPc);
                md_main.g_md_music.g_md_sn76489.write8(in_data);
                if (TraceZ80Ym)
                    Console.WriteLine($"[Z80YM] write addr=0x{a:X4} val=0x{in_data:X2}");
            }
            else if (a >= 0x7F00 && a <= 0x7FFF)
            {
                // VDP via 68k-bussen (0xC00000 + låga 5 bitar)
                uint vdpAddr = 0xC00000u + (uint)(a & 0x1F);
                if (md_main.g_md_bus != null)
                    md_main.g_md_bus.write8(vdpAddr, in_data);
                else
                    md_main.g_md_vdp?.write8(vdpAddr, in_data);
                if (TraceZ80Vdp)
                    Console.WriteLine($"[Z80VDP] write addr=0x{a:X4} val=0x{in_data:X2} vdp=0x{vdpAddr:X6}");
            }
            else if (a >= 0x8000)
            {
                // 68k bankfönster (32KB)
                uint bankBase = GetBankBase();
                uint m68kAddr = bankBase | (uint)(a & 0x7FFF);
                if (md_main.g_md_bus != null)
                    md_main.g_md_bus.write8(m68kAddr, in_data);
                else
                    md_m68k.write8(m68kAddr, in_data);
            }
            else
            {
                MessageBox.Show("md_z80_memory.write8", "error");
            }
        }

        private byte ReadSms(ushort a)
        {
            if (TryReadSmsPort(a, out byte portValue))
                return portValue;

            if (a >= 0xC000)
            {
                return g_ram[(ushort)(a & 0x1FFF)];
            }

            if (a < 0x4000)
            {
                if (md_main.g_masterSystemRomSize > (int)a)
                    return md_main.g_masterSystemRom[a];
                return 0xFF;
            }

            if (a >= 0x4000 && a <= 0xBFFF && md_main.g_masterSystemRomSize > 0)
            {
                uint romIdx = (uint)(a & 0x3FFF);
                int bankCount = Math.Max(1, (md_main.g_masterSystemRomSize + 0x3FFF) / 0x4000);
                uint bank = (uint)(_smsBankSelect % bankCount);
                uint bankOffset = bank * 0x4000u;
                uint idx = (bankOffset + romIdx) % (uint)md_main.g_masterSystemRomSize;
                return md_main.g_masterSystemRom[idx];
            }

            return 0xFF;
        }

        private void ResetTraceBudgets()
        {
            if (TraceZ80Bank)
            {
                _z80BankRegLogRemaining = 32;
                _z80BankReadLogRemaining = 32;
                _z80BankStatSecond = -1;
                _z80BankStatReadCount = 0;
                _z80BankStatLastPc = 0;
                _z80BankStatLastAddr = 0;
            }
            if (MbxSyncTrace.IsEnabled)
            {
                _z80MailboxReadLogRemaining = 256;
                for (int i = 0; i < _z80MailboxSnapshot.Length; i++)
                    _z80MailboxSnapshot[i] = 0xFF;
            }
            ResetMailboxShadow();
        }

        private void TrackMailboxRead(ushort addr, byte value)
        {
            if (_z80MailboxReadLogRemaining <= 0)
                return;
            ushort pc = DebugPc;
            int baseIndex = 0x1B80 & 0x1FFF;
            bool changed = false;
            for (int i = 8; i < 0x10; i++)
            {
                byte current = g_ram[(baseIndex + i) & 0x1FFF];
                if (current != _z80MailboxSnapshot[i])
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
                return;
            _z80MailboxReadLogRemaining--;
            string dump = BuildMailboxDump();
            Console.WriteLine($"[Z80MBXRD] pc={pc:X4} addr={addr:X4} val={value:X2} dump={dump}");
            for (int i = 0; i < 0x10; i++)
                _z80MailboxSnapshot[i] = g_ram[(baseIndex + i) & 0x1FFF];
        }

        private byte MaybeMirrorMailboxRead(ushort addr, byte value)
        {
            if (addr < 0x1B80 || addr > 0x1B8F)
                return value;
            int baseIndex = 0x1B80 & 0x1FFF;
            int offset = addr - 0x1B80;
            int mirrorOffset = offset < 8 ? offset + 8 : offset - 8;
            byte mirror = g_ram[(baseIndex + mirrorOffset) & 0x1FFF];
            if (offset < 8)
            {
                if (mirror != 0x00 && mirror != value)
                    return mirror;
                if (MbxSyncTrace.TryGetLast68k(out uint lastAddr, out byte lastVal))
                {
                    if (lastAddr >= 0xA01B88 && lastAddr <= 0xA01B8F)
                    {
                        int lastOffset = (int)(lastAddr - 0xA01B88);
                        if (lastOffset == offset)
                            return lastVal;
                    }
                }
                return value;
            }
            if (value == 0x00 && mirror != 0x00)
                return mirror;
            return value;
        }

        private void MaybeMirrorMailboxWriteZ80(ushort addr, byte value)
        {
            if (addr < 0x1B80 || addr > 0x1B8F)
                return;
            int baseIndex = 0x1B80 & 0x1FFF;
            int offset = addr - 0x1B80;
            if (offset < 8)
                return;
            int mirrorOffset = offset - 8;
            g_ram[(baseIndex + mirrorOffset) & 0x1FFF] = value;
        }

        private byte MaybeApplyMailboxShadow(ushort addr, byte value)
        {
            if (!_mbxShadowValid || value != 0x00)
                return value;
            if (addr < 0x1B80 || addr > 0x1B8F)
                return value;
            int offset = addr - 0x1B80;
            byte shadow = _mbxShadow[offset];
            return shadow == 0x00 ? value : shadow;
        }

        private void ResetMailboxShadow()
        {
            Array.Clear(_mbxShadow, 0, _mbxShadow.Length);
            _mbxShadowValid = false;
        }

        internal void RecordMailboxWriteFrom68k(uint addr, byte value)
        {
            if (addr < 0xA01B80 || addr > 0xA01B8F)
                return;
            int offset = (int)(addr - 0xA01B80);
            _mbxShadow[offset] = value;
            _mbxShadowValid = true;
        }

        private string BuildMailboxDump()
        {
            int baseIndex = 0x1B80 & 0x1FFF;
            StringBuilder sb = new StringBuilder(16 * 3 - 1);
            for (int i = 0; i < 0x10; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(g_ram[(baseIndex + i) & 0x1FFF].ToString("X2"));
            }
            return sb.ToString();
        }

        internal string GetMailboxDump()
        {
            return BuildMailboxDump();
        }

        internal byte PeekMailboxByte(int index)
        {
            int baseIndex = 0x1B80 & 0x1FFF;
            return g_ram[(baseIndex + index) & 0x1FFF];
        }

        private void UpdateBankStat(ushort addr, byte val)
        {
            long nowSec = Environment.TickCount64 / 1000;
            if (_z80BankStatSecond == -1)
                _z80BankStatSecond = nowSec;
            if (nowSec != _z80BankStatSecond)
            {
                if (_z80BankStatReadCount > 0)
                {
                    Console.WriteLine(
                        $"[Z80BANKSTAT] sec={_z80BankStatSecond} rd={_z80BankStatReadCount} " +
                        $"ff={_z80BankStatReadFfCount} lastPc={_z80BankStatLastPc:X4} " +
                        $"lastAddr={_z80BankStatLastAddr:X4} bank={GetBankBase():X6}");
                }
                _z80BankStatReadCount = 0;
                _z80BankStatReadFfCount = 0;
                _z80BankStatSecond = nowSec;
            }
            _z80BankStatReadCount++;
            if (val == 0xFF)
                _z80BankStatReadFfCount++;
            _z80BankStatLastPc = DebugPc;
            _z80BankStatLastAddr = addr;
        }

        private bool TryReadSmsPort(ushort addr, out byte value)
        {
            value = 0;
            if (!md_main.g_masterSystemMode)
                return false;

            ushort port = (ushort)(addr & 0xFF);
            switch (port)
            {
                case 0xBE:
                    if (md_main.g_md_vdp != null)
                    {
                        value = md_main.g_md_vdp.read8(0xC00000);
                        SmsPortLog(port, "read", value);
                        return true;
                    }
                    break;
                case 0xBF:
                    if (md_main.g_md_vdp != null)
                    {
                        bool irqPending = md_m68k.g_interrupt_V_req;
                        byte raw = md_main.g_md_vdp.read8(0xC00004);
                        value = raw;
                        if (ForceSmsStatus7)
                        {
                            value = (byte)(raw | 0x80);
                            if (!_forceStatus7Logged)
                            {
                                _forceStatus7Logged = true;
                                Console.WriteLine("[SMS VDP] forcing status bit7 in IN 0xBF return");
                            }
                        }
                        LogSmsStatusPoll(raw, value, irqPending);
                        SmsPortLog(port, "read", value);
                        return true;
                    }
                    break;
            }

            return false;
        }

        private bool HandleSmsPortWrite(ushort addr, byte data)
        {
            if (!md_main.g_masterSystemMode)
                return false;

            ushort port = (ushort)(addr & 0xFF);
            switch (port)
            {
                case 0xBE:
                    if (!_smsFirstBeWriteLogged)
                    {
                        _smsFirstBeWriteLogged = true;
                        ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
                        Console.WriteLine($"[SMS IO] first BE write val=0x{data:X2} PC=0x{pc:X4}");
                    }
                    md_main.g_md_vdp?.RecordSmsBeWrite();
                    md_main.g_md_vdp?.write8(0xC00000, data);
                    SmsPortLog(port, "write", data);
                    return true;
                case 0xBF:
                    if (!_smsFirstBfWriteLogged)
                    {
                        _smsFirstBfWriteLogged = true;
                        ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
                        Console.WriteLine($"[SMS IO] first BF write val=0x{data:X2} PC=0x{pc:X4}");
                    }
                    md_main.g_md_vdp?.RecordSmsBfWrite();
                    md_main.g_md_vdp?.write8(0xC00004, data);
                    SmsPortLog(port, "write", data);
                    return true;
                case 0x7E:
                    SetSmsBank(data);
                    g_bank_register = _smsBankSelect;
                    SmsPortLog(port, "write", data);
                    return true;
                case 0x7F:
                    md_psg_trace.TraceWrite("Z80-SMS", port, data, md_main.g_md_z80?.DebugPc ?? 0);
                    md_main.g_md_music?.g_md_sn76489.write8(data);
                    SmsPortLog(port, "write", data);
                    return true;
            }

            return false;
        }

        private void SetSmsBank(byte value)
        {
            if (md_main.g_masterSystemRomSize == 0)
            {
                _smsBankSelect = 0;
                return;
            }

            int bankCount = Math.Max(1, (md_main.g_masterSystemRomSize + 0x3FFF) / 0x4000);
            _smsBankSelect = (byte)(value % bankCount);
        }

        private static void SmsPortLog(ushort port, string action, ushort value)
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;

            switch ((port, action))
            {
                case (0xBE, "read"):
                    if (_smsPortBeReadLog >= SmsPortLogLimit) return;
                    _smsPortBeReadLog++;
                    break;
                case (0xBF, "read"):
                    if (_smsPortBfReadLog >= SmsPortLogLimit) return;
                    _smsPortBfReadLog++;
                    break;
                case (0xBE, "write"):
                    if (_smsPortBeWriteLog >= SmsPortLogLimit) return;
                    _smsPortBeWriteLog++;
                    break;
                case (0xBF, "write"):
                    if (_smsPortBfWriteLog >= SmsPortLogLimit) return;
                    _smsPortBfWriteLog++;
                    break;
                case (0x7E, "write"):
                    if (_smsPort7EWriteLog >= SmsPortLogLimit) return;
                    _smsPort7EWriteLog++;
                    break;
                case (0x7F, "write"):
                    if (_smsPort7FWriteLog >= SmsPortLogLimit) return;
                    _smsPort7FWriteLog++;
                    break;
                default:
                    return;
            }

            MdTracerCore.MdLog.WriteLine($"[md_z80 SMS port {action}] port=0x{port:X2} value=0x{value:X4}");
        }

        private static void LogSmsStatusPoll(byte rawStatus, byte finalStatus, bool irqPending)
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;

            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            if (pc < 0x0570 || pc > 0x0580)
                return;

            md_vdp? vdp = md_main.g_md_vdp;
            if (vdp == null)
                return;

            long frame = vdp.FrameCounter;
            if (frame == _smsStatusPollFrame)
                return;

            _smsStatusPollFrame = frame;

            int bit7 = (finalStatus & 0x80) != 0 ? 1 : 0;
            int irq = irqPending ? 1 : 0;
            int line = vdp.g_scanline;

            MdTracerCore.MdLog.WriteLine(
                $"[SMS WAIT] PC=0x{pc:X4} raw=0x{rawStatus:X2} final=0x{finalStatus:X2} bit7={bit7} irq={irq} line={line} frame={frame}");
        }

        private void LogSmsAccess(string op, ushort addr, byte value)
        {
            if (!md_main.g_masterSystemMode)
                return;

            if (op == "read" && !MdTracerCore.MdLog.TraceZ80InstructionLogging)
                return;

            if (_smsLogCount >= SmsLogLimit)
                return;

            _smsLogCount++;
            MdTracerCore.MdLog.WriteLine($"[md_z80 SMS {op}] addr=0x{addr:X4} val=0x{value:X2} bank=0x{g_bank_register:X6}");
        }

        public void write16(uint in_address, ushort in_data)
        {
            // Skriv via write8 så MMIO hanteras korrekt och wrapping funkar
            ushort a = (ushort)(in_address & 0xFFFF);
            write8(a,     (byte)((in_data >> 8) & 0xFF));
            write8((ushort)(a + 1), (byte)(in_data & 0xFF));
        }

        public void write32(uint in_address, uint in_data)
        {
            ushort a = (ushort)(in_address & 0xFFFF);
            write8(a,                     (byte)((in_data >> 24) & 0xFF));
            write8((ushort)(a + 1),       (byte)((in_data >> 16) & 0xFF));
            write8((ushort)(a + 2),       (byte)((in_data >> 8)  & 0xFF));
            write8((ushort)(a + 3),       (byte)( in_data        & 0xFF));
        }
    }
}
