using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ryu64.MIPS
{
    public class TLB
    {
        private static readonly bool DisableTlb =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_DISABLE_TLB"), "1", StringComparison.Ordinal);

        public struct TLBEntry
        {
            public bool Written;
            public uint PFN0;
            public byte PageCoherency0;
            public byte Dirty0;
            public byte Valid0;
            public byte Global0;
            public uint PFN1;
            public byte PageCoherency1;
            public byte Dirty1;
            public byte Valid1;
            public byte Global1;
            public uint VPN2;
            public byte ASID;
            public ushort PageMask;
        }

        private const int TlbEntryCount = 32;
        private readonly static TLBEntry[] TLBEntries = new TLBEntry[TlbEntryCount];
        // Derived lookup state only; every entry mutation invalidates it. Cache
        // 4 KiB subpages so large/overlapping mappings keep the scan's priority.
        private struct Translation
        {
            public bool Valid;
            public uint Tag;
            public uint PhysicalPage;
        }
        // UI diagnostics can translate addresses too. Never let those readers
        // publish into the CPU thread's cache or retain a mapping after a write.
        [ThreadStatic] private static Translation[] Translations;
        [ThreadStatic] private static int CachedTranslationVersion;
        private static int TranslationVersion;

        private static void InvalidateTranslations() => Interlocked.Increment(ref TranslationVersion);

        public static void Reset()
        {
            for (int i = 0; i < TLBEntries.Length; i++)
                TLBEntries[i] = default;
            InvalidateTranslations();
        }

        public static void SaveState(BinaryWriter writer)
        {
            writer.Write(TlbEntryCount);
            for (int i = 0; i < TLBEntries.Length; i++)
            {
                TLBEntry entry = TLBEntries[i];
                writer.Write(entry.Written);
                writer.Write(entry.PFN0);
                writer.Write(entry.PageCoherency0);
                writer.Write(entry.Dirty0);
                writer.Write(entry.Valid0);
                writer.Write(entry.Global0);
                writer.Write(entry.PFN1);
                writer.Write(entry.PageCoherency1);
                writer.Write(entry.Dirty1);
                writer.Write(entry.Valid1);
                writer.Write(entry.Global1);
                writer.Write(entry.VPN2);
                writer.Write(entry.ASID);
                writer.Write(entry.PageMask);
            }
        }

        public static void LoadState(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count != TlbEntryCount)
                throw new InvalidDataException($"Unsupported N64 TLB entry count: {count}.");

            try
            {
                for (int i = 0; i < TLBEntries.Length; i++)
                {
                    TLBEntries[i] = new TLBEntry
                    {
                        Written = reader.ReadBoolean(),
                        PFN0 = reader.ReadUInt32(),
                        PageCoherency0 = reader.ReadByte(),
                        Dirty0 = reader.ReadByte(),
                        Valid0 = reader.ReadByte(),
                        Global0 = reader.ReadByte(),
                        PFN1 = reader.ReadUInt32(),
                        PageCoherency1 = reader.ReadByte(),
                        Dirty1 = reader.ReadByte(),
                        Valid1 = reader.ReadByte(),
                        Global1 = reader.ReadByte(),
                        VPN2 = reader.ReadUInt32(),
                        ASID = reader.ReadByte(),
                        PageMask = reader.ReadUInt16()
                    };
                }
            }
            finally { InvalidateTranslations(); }
        }

        public static uint TranslateAddress(uint Address)
        {
            return TranslateAddress(Address, false);
        }

        public static uint TranslateAddress(uint Address, bool throwOnMiss, bool isStore = false)
        {
            if (DisableTlb)
                return Address;

            uint currentAsid = (uint)Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] & 0xFF;
            uint page = Address >> 12;
            uint tag = (page << 8) | currentAsid;
            int version = Volatile.Read(ref TranslationVersion);
            Translation[] translations = Translations;
            if (translations == null || CachedTranslationVersion != version)
            {
                if (translations == null) Translations = translations = new Translation[256];
                else Array.Clear(translations, 0, translations.Length);
                CachedTranslationVersion = version;
            }
            ref Translation cached = ref translations[page & 255u];
            if (cached.Valid && cached.Tag == tag)
                return cached.PhysicalPage | (Address & 0xfffu);

            foreach (TLBEntry Entry in TLBEntries)
            {
                if (!Entry.Written)
                    continue;

                // Pair mask (both even+odd pages): low bits set up to pair size - 1.
                // For 4K pages this becomes 0x1FFF (8K pair), while each page offset mask is 0x0FFF.
                uint pairMask = (uint)((Entry.PageMask << 13) | 0x1FFFu);
                uint vpn2Mask = ~pairMask;
                uint entryVpn2 = (Entry.VPN2 << 13) & vpn2Mask;
                uint addrVpn2 = Address & vpn2Mask;

                if (addrVpn2 != entryVpn2)
                    continue;

                bool global = (Entry.Global0 & Entry.Global1) != 0;
                if (!global && Entry.ASID != currentAsid)
                    continue;

                uint pageOffsetMask = pairMask >> 1;
                uint oddPageBit = pageOffsetMask + 1;
                bool oddPage = (Address & oddPageBit) != 0;
                uint valid = oddPage ? Entry.Valid1 : Entry.Valid0;

                if (valid == 0)
                    continue;

                uint pfn = oddPage ? Entry.PFN1 : Entry.PFN0;
                uint physical = (pfn << 12) | (Address & pageOffsetMask);
                cached = new Translation { Valid = true, Tag = tag, PhysicalPage = physical & ~0xfffu };
                return physical;
            }

            if (throwOnMiss)
                throw new Common.Exceptions.TLBMissException(Address, isStore);

            return Address;
        }

        public static void WriteTLBEntryIndexed()
        {
            WriteTLBEntry((uint)Registers.COP0.Reg[Registers.COP0.INDEX_REG] & 0x1F);
        }

        public static void WriteTLBEntryRandom()
        {
            WriteTLBEntry((uint)Registers.COP0.Reg[Registers.COP0.RANDOM_REG] & 0x1F);
        }

        public static void ReadTLBEntry()
        {
            TLBEntry Entry = TLBEntries[(uint)Registers.COP0.Reg[Registers.COP0.INDEX_REG] & 0x1F];
            Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] = (Entry.PFN0 << 6)
                                                               | (byte)(Entry.Global0 & 0x1)
                                                               | (byte)((Entry.Valid0 & 0x1) << 1)
                                                               | (byte)((Entry.Dirty0 & 0x1) << 2)
                                                               | (byte)((Entry.PageCoherency0 & 0b111) << 3);
            Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] = (Entry.PFN1 << 6)
                                                               | (byte)(Entry.Global1 & 0x1)
                                                               | (byte)((Entry.Valid1 & 0x1) << 1)
                                                               | (byte)((Entry.Dirty1 & 0x1) << 2)
                                                               | (byte)((Entry.PageCoherency1 & 0b111) << 3);

            Registers.COP0.Reg[Registers.COP0.PAGEMASK_REG] = (uint)(Entry.PageMask << 13);
            Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] = (Entry.VPN2 << 13) | Entry.ASID;
        }

        private static void WriteTLBEntry(uint Index)
        {
            TLBEntries[Index & 0x1F] = new TLBEntry()
            {
                Written        = true,
                PFN0           = (uint)((Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] & 0x3FFFFFC0) >> 6),
                Valid0         = (byte)((Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] & 0b000010)   >> 1),
                Dirty0         = (byte)((Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] & 0b000100)   >> 2),
                PageCoherency0 = (byte)((Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] & 0b111000)   >> 3),
                PFN1           = (uint)((Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] & 0x3FFFFFC0) >> 6),
                Valid1         = (byte)((Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] & 0b000010) >> 1),
                Dirty1         = (byte)((Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] & 0b000100) >> 2),
                PageCoherency1 = (byte)((Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] & 0b111000) >> 3),
                VPN2          = (uint)((Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] & 0xFFFFE000)  >> 13),
                PageMask      = (ushort)((Registers.COP0.Reg[Registers.COP0.PAGEMASK_REG] & 0x01FFE000) >> 13),
                Global0       = (byte)(((byte)Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] & 0x1)
                                     & ((byte)Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] & 0x1)),
                Global1       = (byte)(((byte)Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] & 0x1)
                                     & ((byte)Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] & 0x1)),
                ASID = (byte)(Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] & 0xFF)
            };
            InvalidateTranslations();
        }

        public static void ProbeTLB()
        {
            uint probeEntryHi = (uint)Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG];
            uint probeAsid = probeEntryHi & 0xFF;
            bool FoundEntry = false;
            for (uint i = 0; i < TLBEntries.Length; ++i)
            {
                TLBEntry Entry = TLBEntries[i];
                if (!Entry.Written)
                    continue;

                uint vpn2Mask = ~((uint)(Entry.PageMask << 13) | 0x1FFFu);
                uint entryVpn2 = (Entry.VPN2 << 13) & vpn2Mask;
                uint probeVpn2 = probeEntryHi & vpn2Mask;
                if (entryVpn2 != probeVpn2)
                    continue;

                bool global = (Entry.Global0 & Entry.Global1) != 0;
                if (!global && Entry.ASID != probeAsid)
                    continue;

                FoundEntry = true;
                Registers.COP0.Reg[Registers.COP0.INDEX_REG] = i & 0x1F;
                break;
            }

            if (!FoundEntry)
            {
                // Real hardware sets P bit in INDEX on miss instead of throwing.
                Registers.COP0.Reg[Registers.COP0.INDEX_REG] = 0x80000000;
            }
        }
    }
}
