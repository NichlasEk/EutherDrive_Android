#!/usr/bin/env python3
"""
Säkert script för att fixa gating utan att förstöra filer.
"""

import os
import re
from pathlib import Path

# Mapping av log prefixes till MdLog metoder
LOG_PREFIX_MAPPING = {
    # Z80 related
    r"\[Z80-?WIN\]": "WriteLineZ80Win",
    r"\[Z80SAFE\]": "WriteLineZ80",
    r"\[Z80SIG\]": "WriteLineZ80Sig",
    r"\[Z80RESET\]": "WriteLineZ80",
    r"\[Z80YM\]": "WriteLineZ80Ym",
    r"\[Z80IO\]": "WriteLineZ80Io",
    r"\[Z80BUSREQ\]": "WriteLineZ80",
    r"\[Z80RAMRD\]": "WriteLineZ80Memory",
    r"\[Z80RAMWR\]": "WriteLineZ80Memory",
    r"\[Z80RD\]": "WriteLineZ80Memory",
    r"\[Z80WR65\]": "WriteLineZ80Memory",
    r"\[Z80MBXRD\]": "WriteLineZ80Mbx",
    r"\[Z80MBXWR\]": "WriteLineZ80Mbx",
    r"\[Z80-INT-IM1\]": "WriteLineZ80Int",
    r"\[Z80-INT-IM2\]": "WriteLineZ80Int",
    r"\[Z80-IRQ-ACCEPT\]": "WriteLineZ80Irq",
    r"\[Z80-IRQ-DROP\]": "WriteLineZ80Irq",
    r"\[Z80-INTERRUPT-ACCEPT\]": "WriteLineZ80Irq",
    r"\[Z80BANKRD\]": "WriteLineZ80Bank",
    r"\[Z80BANKREG\]": "WriteLineZ80Bank",
    r"\[Z80BANKREG68K\]": "WriteLineZ80Bank",
    r"\[Z80-BANK\]": "WriteLineZ80Bank",
    r"\[Z80-BOOT-CODE\]": "WriteLineZ80Boot",
    r"\[Z80-BOOT-DUMP\]": "WriteLineZ80Boot",
    r"\[Z80BOOTIO\]": "WriteLineZ80Boot",
    r"\[Z80-DRIVER-ENTRY\]": "WriteLineZ80Boot",
    r"\[Z80STEP\]": "WriteLineZ80Step",
    r"\[Z80-FIRST-100\]": "WriteLineZ80First100",
    r"\[Z80-EI-DI-FETCH\]": "WriteLineZ80EiDi",
    r"\[Z80-RET\]": "WriteLineZ80Ret",
    r"\[Z80-EI\]": "WriteLineZ80EiDi",
    r"\[Z80-DI\]": "WriteLineZ80EiDi",
    r"\[Z80-INT-VECTOR\]": "WriteLineZ80IntVector",
    r"\[Z80MBX-POLL\]": "WriteLineZ80Mbx",
    r"\[Z80MBX-POLL-EDGE\]": "WriteLineZ80Mbx",
    r"\[Z80MBX-DATA\]": "WriteLineZ80Mbx",
    r"\[Z80PC-HIST\]": "WriteLineZ80",
    r"\[Z80WIN-HIST\]": "WriteLineZ80Win",
    r"\[Z80SCHED\]": "WriteLineZ80",
    r"\[Z80INT-STATS\]": "WriteLineZ80Int",
    # VDP related
    r"\[VDP\]": "WriteLineVdp",
    r"\[VDP-DMAREG\]": "WriteLineVdp",
    r"\[VDP-HMODE\]": "WriteLineVdp",
    r"\[VDP-AUTO-FIX\]": "WriteLineVdp",
    r"\[VDP-REG1\]": "WriteLineVdp",
    r"\[VDP-REG2\]": "WriteLineVdp",
    r"\[VDP-REG4\]": "WriteLineVdp",
    r"\[VDP-REG7-BD\]": "WriteLineVdp",
    r"\[VDP-REG12\]": "WriteLineVdp",
    r"\[VDP-REG12-SH\]": "WriteLineVdp",
    r"\[VDP-DISPLAY\]": "WriteLineVdp",
    r"\[VDP-SMS-DISPLAY\]": "WriteLineVdp",
    r"\[VDP-SMS\]": "WriteLineVdp",
    r"\[VDP-ADDR-SET\]": "WriteLineVdp",
    r"\[VDP-CTRL-WRITE\]": "WriteLineVdp",
    r"\[VDP-DATA-CODE\]": "WriteLineVdp",
    # VRAM related
    r"\[VRAM-PAGE-STATS\]": "WriteLineVram",
    r"\[VRAM-VS-CACHE\]": "WriteLineVram",
    r"\[VRAM-WRITE-DETAIL\]": "WriteLineVram",
    r"\[VRAM-WRITE-CPU\]": "WriteLineVram",
    r"\[VRAM-WATCH\]": "WriteLineVram",
    r"\[VRAM-PAGE-HIST\]": "WriteLineVram",
    r"\[VRAM-NAME\]": "WriteLineVram",
    r"\[VRAM-CLEAR\]": "WriteLineVram",
    r"\[VRAM\]": "WriteLineVram",
    # M68K related
    r"\[m68k-reset\]": "WriteLineM68k",
    r"\[m68k boot\]": "WriteLineM68k",
    r"\[m68k\]": "WriteLineM68k",
    # YM2612 related
    r"\[YMTRACE\]": "WriteLineYm",
    r"\[YMLVL\]": "WriteLineYm",
    r"\[YM-BUSY-COUNTER\]": "WriteLineYm",
    r"\[YM-STATUS\]": "WriteLineYm",
    r"\[YMREG\]": "WriteLineYm",
    r"\[YMIRQ\]": "WriteLineYm",
    r"\[YMDAC\]": "WriteLineYm",
    r"\[YM-BUSY\]": "WriteLineYm",
    # PSG related
    r"\[PSGLVL\]": "WriteLinePsg",
    r"\[PSG\]": "WriteLinePsg",
    # SMS related
    r"\[SMS-VDP\]": "WriteLineSms",
    r"\[SMS-MAPPER\]": "WriteLineSms",
    r"\[SMS-IO-READ\]": "WriteLineSms",
    r"\[SMS DELAY\]": "WriteLineSms",
    r"\[SMS-VDP-READ\]": "WriteLineSms",
    r"\[SMS-VDP-FIRST\]": "WriteLineSms",
    r"\[SMS-VDP-REG-DETAIL\]": "WriteLineSms",
    r"\[SMS-VDP-NAME\]": "WriteLineSms",
    r"\[SMS-VDP-DATA\]": "WriteLineSms",
    r"\[SMS-VDP-CMD\]": "WriteLineSms",
    r"\[SMS-Z80\]": "WriteLineSms",
    r"\[SMS-Z80-RUN\]": "WriteLineSms",
    r"\[SMS-Z80-BLOCK\]": "WriteLineSms",
    r"\[SMS-RENDER\]": "WriteLineSms",
    r"\[SMS-NO-RENDER\]": "WriteLineSms",
    r"\[SMS-FORCE-RENDER\]": "WriteLineSms",
    r"\[SMS-FIRST-LINE\]": "WriteLineSms",
    r"\[SMS-DISPLAY-LOCK\]": "WriteLineSms",
    r"\[SMS-VRAM-READ\]": "WriteLineSms",
    r"\[SMS-STATUS\]": "WriteLineSms",
    r"\[SMS-STATUS-READ\]": "WriteLineSms",
    r"\[SMS-RUNFRAME\]": "WriteLineSms",
    r"\[SMS-ROM\]": "WriteLineSms",
    r"\[SMS-RESET\]": "WriteLineSms",
    r"\[SMS-WARN\]": "WriteLineSms",
    r"\[SMS-AUTOFIX\]": "WriteLineSmsAutofix",
    # Audio related
    r"\[AUDLVL\]": "WriteLineAudio",
    r"\[AudioEngine\]": "WriteLineAudio",
    r"\[Audio\]": "WriteLineAudio",
    r"\[PwCatAudioSink\]": "WriteLineAudio",
    r"\[OpenAlAudioOutput\]": "WriteLineAudio",
    # Memory/Storage related
    r"\[SRAM\]": "WriteLineSram",
    r"\[SRAM-READ\]": "WriteLineSram",
    r"\[SRAM-WRITE\]": "WriteLineSram",
    r"\[Savestate\]": "WriteLineSram",
    # Test/Debug related
    r"\[TEST\]": "WriteLineTest",
    r"\[TEST-FAIL\]": "WriteLineTest",
    r"\[TEST-PASS\]": "WriteLineTest",
    r"\[CACHE-COPY\]": "WriteLineTest",
    r"\[STALL\]": "WriteLineTest",
    r"\[WARN\]": "WriteLineTest",
    # UI/Application related
    r"\[MdTracerAdapter\]": "WriteLineUi",
    r"\[HEADLESS\]": "WriteLineUi",
    r"\[UI\]": "WriteLineUi",
    r"\[MainWindow\]": "WriteLineUi",
    r"\[AsciiViewer\]": "WriteLineUi",
    # Additional prefixes found in rom_start.log
    r"\[AUDIOCORE\]": "WriteLineAudio",
    r"\[INTERLACE-DEBUG\]": "WriteLineVdp",
    r"\[MBXINJ-ENV\]": "WriteLineTest",
    r"\[PATTERN-WRITE\]": "WriteLineVram",
    r"\[ROMMODE\]": "WriteLineUi",
    r"\[VDP-REG12-DBG\]": "WriteLineVdp",
    r"\[VINT\]": "WriteLineVdp",
    r"\[VINT-SKIP\]": "WriteLineVdp",
    r"\[VINT-TAKEN\]": "WriteLineM68k",
    r"\[Z80-EI-DELAY\]": "WriteLineZ80",
    r"\[Z80-IRQ-SIGNAL\]": "WriteLineZ80",
    r"\[Z80SAFE-UPLOAD\]": "WriteLineZ80",
    # DBRA
    r"\[DBRA\]": "WriteLineDbra",
}


def safe_process_file(filepath):
    """Process a single C# file safely without breaking syntax."""
    with open(filepath, "r") as f:
        content = f.read()

    lines = content.split("\n")
    modified = False

    for i, line in enumerate(lines):
        if "Console.WriteLine(" in line and "MdLog." not in line:
            # Kolla om raden innehåller något av våra kända prefix
            for pattern, method in LOG_PREFIX_MAPPING.items():
                if re.search(pattern, line):
                    # Ersätt Console.WriteLine med MdLog.{method}
                    new_line = line.replace("Console.WriteLine(", f"MdLog.{method}(")
                    lines[i] = new_line
                    modified = True
                    print(f"  {filepath}:{i + 1}: {pattern} -> {method}")
                    break

    if not modified:
        return False

    # Bygg nytt innehåll
    new_content = "\n".join(lines)

    # Lägg till using statement om det behövs OCH om filen inte redan har det
    if "using EutherDrive.Core.MdTracerCore;" not in new_content:
        # Kolla om filen är i namespace EutherDrive.Core.MdTracerCore
        # Om den är det, behövs inte using statement
        if "namespace EutherDrive.Core.MdTracerCore" in new_content:
            print(f"  {filepath}: Redan i rätt namespace, behöver inte using")
        else:
            # Lägg till using statement på rätt ställe
            new_lines = new_content.split("\n")
            final_lines = []
            using_added = False

            for line in new_lines:
                final_lines.append(line)
                # Lägg till using statement efter sista using statement
                if line.strip().startswith("using ") and not using_added:
                    # Kolla om nästa rad inte är en using statement
                    idx = new_lines.index(line)
                    if idx + 1 >= len(new_lines) or not new_lines[
                        idx + 1
                    ].strip().startswith("using "):
                        final_lines.append("using EutherDrive.Core.MdTracerCore;")
                        using_added = True

            # Om vi inte hittade någon using statement, lägg till efter namespace
            if not using_added:
                final_lines = []
                for line in new_lines:
                    final_lines.append(line)
                    if line.strip().startswith("namespace ") and not using_added:
                        # Lägg till using statement inne i namespace
                        final_lines.append("    using EutherDrive.Core.MdTracerCore;")
                        using_added = True

            new_content = "\n".join(final_lines)

    # Skriv tillbaka filen
    with open(filepath, "w") as f:
        f.write(new_content)

    return True


def main():
    """Main function."""
    project_dir = Path("/home/nichlas/EutherDrive")

    # Hitta alla C# filer som innehåller Console.WriteLine
    cs_files = list(project_dir.rglob("*.cs"))

    print(f"Hittade {len(cs_files)} C# filer")

    modified_count = 0
    for cs_file in cs_files:
        # Hoppa över testfiler och genererade filer
        if any(
            skip in str(cs_file)
            for skip in ["/obj/", "/bin/", "/Test", "/test", ".claude"]
        ):
            continue

        # Kolla om filen innehåller Console.WriteLine
        with open(cs_file, "r") as f:
            content = f.read()

        if "Console.WriteLine(" in content:
            print(f"Bearbetar {cs_file.relative_to(project_dir)}...")
            if safe_process_file(cs_file):
                modified_count += 1

    print(f"\nÄndrade {modified_count} filer")

    # Kör dotnet build för att testa
    print("\nKör dotnet build för att testa...")
    os.system("dotnet build 2>&1 | tail -20")


if __name__ == "__main__":
    main()
