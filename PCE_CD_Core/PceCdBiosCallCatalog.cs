using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ePceCD
{
    internal sealed class PceCdBiosCallCatalog
    {
        internal sealed class Entry
        {
            public ushort Address { get; init; }
            public string Name { get; set; } = string.Empty;
            public string Purpose { get; set; } = string.Empty;
            public string Notes { get; set; } = string.Empty;
            public PceCdBiosCallStatus Status { get; set; } = PceCdBiosCallStatus.Unknown;
            public int HitCount { get; set; }
            public HashSet<ushort> Callers { get; } = new HashSet<ushort>();
        }

        internal readonly record struct Snapshot(
            ushort Address,
            string Name,
            string Purpose,
            string Notes,
            PceCdBiosCallStatus Status,
            int HitCount,
            ushort[] Callers);

        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();
        private readonly object _sync = new object();

        public PceCdBiosCallCatalog()
        {
            Seed(0xE05D, "bios_e05d", PceCdBiosCallStatus.Traced, "Observed in Golden Axe after direct boot.");
            Seed(0xE009, "bios_e009", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader.");
            Seed(0xE00F, "bios_e00f", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader; likely data-load related.");
            Seed(0xE012, "bios_e012", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader.");
            Seed(0xE01E, "bios_e01e", PceCdBiosCallStatus.Traced, "Observed in Golden Axe loaded code as a status poll.");
            Seed(0xE02D, "bios_e02d", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader.");
            Seed(0xE030, "bios_e030", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader.");
            Seed(0xE033, "bios_e033", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader.");
            Seed(0xE036, "bios_e036", PceCdBiosCallStatus.Traced, "Observed in Golden Axe as a tail-called BIOS path after E05A.");
            Seed(0xE03C, "bios_e03c", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader.");
            Seed(0xE05A, "bios_e05a", PceCdBiosCallStatus.Traced, "Observed in Golden Axe before the E036 tail-call path.");
            Seed(0xE069, "bios_e069", PceCdBiosCallStatus.Traced, "Observed in Golden Axe loaded code.");
            Seed(0xE06C, "bios_e06c", PceCdBiosCallStatus.Traced, "Observed in Golden Axe loaded code.");
            Seed(0xE06F, "bios_e06f", PceCdBiosCallStatus.Traced, "Observed in Golden Axe loaded code.");
            Seed(0xE07B, "bios_e07b", PceCdBiosCallStatus.Traced, "Observed in Golden Axe secondary loader.");
            Seed(0xE08A, "bios_e08a", PceCdBiosCallStatus.Traced, "Observed in Golden Axe boot loader.");
            Seed(0xE099, "bios_e099", PceCdBiosCallStatus.Traced, "Observed in Golden Axe loaded code.");
            Seed(PceCdBiosDispatcher.ResetTrapAddress, "hle_reset", PceCdBiosCallStatus.Implemented, "Synthetic HLE reset trap; not a BIOS ROM address.");
            Seed(PceCdBiosDispatcher.Irq2TrapAddress, "hle_irq2", PceCdBiosCallStatus.PartiallyImplemented, "Synthetic HLE IRQ2 trap.");
            Seed(PceCdBiosDispatcher.Irq1TrapAddress, "hle_irq1", PceCdBiosCallStatus.PartiallyImplemented, "Synthetic HLE IRQ1 trap.");
            Seed(PceCdBiosDispatcher.TimerTrapAddress, "hle_timer", PceCdBiosCallStatus.PartiallyImplemented, "Synthetic HLE timer IRQ trap.");
        }

        private void Seed(ushort address, string name, PceCdBiosCallStatus status, string notes)
        {
            _entries[address] = new Entry
            {
                Address = address,
                Name = name,
                Status = status,
                Notes = notes
            };
        }

        public Entry NoteCall(ushort address, ushort caller)
        {
            lock (_sync)
            {
                Entry entry = GetOrCreateLocked(address);
                entry.HitCount++;
                entry.Callers.Add(caller);
                if (entry.Status == PceCdBiosCallStatus.Unknown)
                    entry.Status = PceCdBiosCallStatus.Traced;
                return entry;
            }
        }

        public void MarkStatus(ushort address, PceCdBiosCallStatus status, string? purpose = null, string? notes = null)
        {
            lock (_sync)
            {
                Entry entry = GetOrCreateLocked(address);
                entry.Status = status;
                if (!string.IsNullOrWhiteSpace(purpose))
                    entry.Purpose = purpose!;
                if (!string.IsNullOrWhiteSpace(notes))
                    entry.Notes = notes!;
            }
        }

        public IReadOnlyList<Snapshot> SnapshotEntries()
        {
            lock (_sync)
            {
                return _entries.Values
                    .OrderBy(entry => entry.Address)
                    .Select(entry => new Snapshot(
                        entry.Address,
                        entry.Name,
                        entry.Purpose,
                        entry.Notes,
                        entry.Status,
                        entry.HitCount,
                        entry.Callers.OrderBy(caller => caller).ToArray()))
                    .ToArray();
            }
        }

        public string BuildMarkdown(string discName, PceCdBiosMode mode)
        {
            IReadOnlyList<Snapshot> entries = SnapshotEntries();
            var sb = new StringBuilder(4096);
            sb.AppendLine("# PCE CD BIOS Call Catalog");
            sb.AppendLine();
            sb.AppendLine($"- Disc: `{discName}`");
            sb.AppendLine($"- Mode: `{mode}`");
            sb.AppendLine($"- Generated: `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC`");
            sb.AppendLine();
            sb.AppendLine("| Entry | Name | Status | Hits | Callers | Notes |");
            sb.AppendLine("| --- | --- | --- | ---: | --- | --- |");

            foreach (Snapshot entry in entries)
            {
                string callers = entry.Callers.Length == 0
                    ? "-"
                    : string.Join(", ", entry.Callers.Select(caller => $"`0x{caller:X4}`"));
                string note = entry.Purpose;
                if (!string.IsNullOrWhiteSpace(entry.Notes))
                {
                    if (!string.IsNullOrWhiteSpace(note))
                        note += " ";
                    note += entry.Notes;
                }

                sb.Append("| `0x")
                    .Append(entry.Address.ToString("X4"))
                    .Append("` | `")
                    .Append(entry.Name)
                    .Append("` | `")
                    .Append(entry.Status)
                    .Append("` | ")
                    .Append(entry.HitCount)
                    .Append(" | ")
                    .Append(callers)
                    .Append(" | ")
                    .Append(string.IsNullOrWhiteSpace(note) ? "-" : note.Replace("|", "\\|"))
                    .AppendLine(" |");
            }

            return sb.ToString();
        }

        private Entry GetOrCreateLocked(ushort address)
        {
            if (_entries.TryGetValue(address, out Entry? existing))
                return existing;

            string name = address == PceCdBiosDispatcher.ResetTrapAddress
                ? "hle_reset"
                : $"bios_{address:X4}".ToLowerInvariant();
            var entry = new Entry
            {
                Address = address,
                Name = name
            };
            _entries[address] = entry;
            return entry;
        }
    }
}
