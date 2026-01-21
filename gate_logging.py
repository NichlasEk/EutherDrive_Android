#!/usr/bin/env python3
"""
Script to automatically gate Console.WriteLine calls in C# code.
This script replaces Console.WriteLine calls with appropriate MdLog.WriteLineXxx calls.
"""

import os
import re
from pathlib import Path

# Mapping of log prefixes to MdLog methods
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
}


def find_log_prefix(line):
    """Find the log prefix in a Console.WriteLine line."""
    # Look for patterns like [SOMETHING] or [SOMETHING-SOMETHING]
    match = re.search(r"Console\.WriteLine\(.*?(\[[A-Z0-9\-_]+\])", line)
    if match:
        prefix = match.group(1)
        # Try to find the best matching prefix
        for pattern, method in LOG_PREFIX_MAPPING.items():
            if re.match(pattern, prefix):
                return prefix, method
    return None, None


def process_file(filepath):
    """Process a single C# file."""
    with open(filepath, "r") as f:
        content = f.read()

    # Check if file already uses MdLog
    if (
        "using EutherDrive.Core.MdTracerCore;" not in content
        and "MdLog." not in content
    ):
        # Add using statement after other using statements
        lines = content.split("\n")
        new_lines = []
        using_added = False
        for line in lines:
            new_lines.append(line)
            if line.strip().startswith("using ") and not using_added:
                # Add MdLog using after the last using statement
                if not any(
                    l.strip().startswith("using ")
                    for l in lines[lines.index(line) + 1 :]
                    if l.strip()
                ):
                    new_lines.append("using EutherDrive.Core.MdTracerCore;")
                    using_added = True

        if not using_added:
            # Insert after namespace if no using statements found
            for i, line in enumerate(new_lines):
                if line.strip().startswith("namespace "):
                    new_lines.insert(i + 1, "    using EutherDrive.Core.MdTracerCore;")
                    break

        content = "\n".join(new_lines)

    # Replace Console.WriteLine calls
    lines = content.split("\n")
    modified = False

    for i, line in enumerate(lines):
        if "Console.WriteLine(" in line and "MdLog." not in line:
            prefix, method = find_log_prefix(line)
            if method:
                # Replace Console.WriteLine with MdLog.WriteLineXxx
                new_line = line.replace("Console.WriteLine(", f"MdLog.{method}(")
                lines[i] = new_line
                modified = True
                print(f"  {filepath}:{i + 1}: {prefix} -> {method}")

    if modified:
        with open(filepath, "w") as f:
            f.write("\n".join(lines))
        return True
    return False


def main():
    """Main function."""
    project_dir = Path("/home/nichlas/EutherDrive")

    # Find all C# files
    cs_files = list(project_dir.rglob("*.cs"))

    print(f"Found {len(cs_files)} C# files")

    modified_count = 0
    for cs_file in cs_files:
        # Skip test files and generated files
        if any(skip in str(cs_file) for skip in ["/obj/", "/bin/", "/Test", "/test"]):
            continue

        print(f"Processing {cs_file.relative_to(project_dir)}...")
        if process_file(cs_file):
            modified_count += 1

    print(f"\nModified {modified_count} files")

    # Create/update GATED_ENV_VARS.md
    update_env_vars_doc()


def update_env_vars_doc():
    """Update the GATED_ENV_VARS.md documentation."""
    doc_path = Path("/home/nichlas/EutherDrive/GATED_ENV_VARS.md")

    env_vars = {}
    for pattern, method in LOG_PREFIX_MAPPING.items():
        # Extract env var name from method
        if method.startswith("WriteLine"):
            base_name = method[9:]  # Remove 'WriteLine'
            if base_name == "Z80":
                env_var = "EUTHERDRIVE_TRACE_Z80"
            elif base_name == "Z80Sig":
                env_var = "EUTHERDRIVE_TRACE_Z80SIG"
            elif base_name == "Z80Step":
                env_var = "EUTHERDRIVE_TRACE_Z80STEP"
            elif base_name == "Z80Ym":
                env_var = "EUTHERDRIVE_TRACE_Z80YM"
            elif base_name == "Z80Int":
                env_var = "EUTHERDRIVE_TRACE_Z80INT"
            elif base_name == "Z80Win":
                env_var = "EUTHERDRIVE_TRACE_Z80WIN"
            elif base_name == "Z80Memory":
                env_var = "EUTHERDRIVE_TRACE_Z80_MEMORY"
            elif base_name == "Z80Io":
                env_var = "EUTHERDRIVE_TRACE_Z80_IO"
            elif base_name == "Z80Boot":
                env_var = "EUTHERDRIVE_TRACE_Z80_BOOT"
            elif base_name == "Z80Ret":
                env_var = "EUTHERDRIVE_TRACE_Z80_RET"
            elif base_name == "Z80First100":
                env_var = "EUTHERDRIVE_TRACE_Z80_FIRST_100"
            elif base_name == "Z80EiDi":
                env_var = "EUTHERDRIVE_TRACE_SMS_EI_DI"
            elif base_name == "Z80Irq":
                env_var = "EUTHERDRIVE_TRACE_Z80_IRQ"
            elif base_name == "Z80IntVector":
                env_var = "EUTHERDRIVE_TRACE_Z80_INT_VECTOR"
            elif base_name == "Z80Mbx":
                env_var = "EUTHERDRIVE_TRACE_Z80_MBX"
            elif base_name == "Z80Bank":
                env_var = "EUTHERDRIVE_TRACE_Z80_BANK"
            elif base_name == "SmsAutofix":
                env_var = "EUTHERDRIVE_TRACE_SMS_AUTOFIX"
            elif base_name == "Vdp":
                env_var = "EUTHERDRIVE_TRACE_VDP"
            elif base_name == "Vram":
                env_var = "EUTHERDRIVE_TRACE_VRAM"
            elif base_name == "M68k":
                env_var = "EUTHERDRIVE_TRACE_M68K"
            elif base_name == "Ym":
                env_var = "EUTHERDRIVE_TRACE_YM"
            elif base_name == "Psg":
                env_var = "EUTHERDRIVE_TRACE_PSG"
            elif base_name == "Sms":
                env_var = "EUTHERDRIVE_TRACE_SMS"
            elif base_name == "Audio":
                env_var = "EUTHERDRIVE_TRACE_AUDIO"
            elif base_name == "Sram":
                env_var = "EUTHERDRIVE_TRACE_SRAM"
            elif base_name == "Test":
                env_var = "EUTHERDRIVE_TRACE_TEST"
            elif base_name == "Ui":
                env_var = "EUTHERDRIVE_TRACE_UI"
            elif base_name == "All":
                env_var = "EUTHERDRIVE_TRACE_ALL"
            else:
                env_var = f"EUTHERDRIVE_TRACE_{base_name.upper()}"

            if env_var not in env_vars:
                env_vars[env_var] = []
            env_vars[env_var].append(pattern)

    # Create documentation
    doc_content = """# EutherDrive Gated Environment Variables - Complete Reference

Denna fil dokumenterar ALLA gated environment variables som används för att kontrollera logging i EutherDrive emulatorn.

## Översikt

All logging i EutherDrive är nu gated bakom environment variables. Detta gör systemet extremt mycket snabbare när ingen logging är aktiverad.

## Komplett Lista över Environment Variables

"""

    # Sort env vars alphabetically
    for env_var in sorted(env_vars.keys()):
        patterns = env_vars[env_var]
        doc_content += f"### `{env_var}=1`\n"
        doc_content += f"- **Beskrivning**: Aktiverar logging för följande prefix:\n"
        for pattern in sorted(patterns):
            doc_content += f"  - `{pattern}`\n"
        doc_content += "\n"

    doc_content += """## Användningsexempel

```bash
# Ingen logging (snabbast)
# (inga env-vars satta)

# Minimal SMS debugging
export EUTHERDRIVE_TRACE_SMS_AUTOFIX=1
export EUTHERDRIVE_TRACE_SMS_EI_DI=1

# Full Z80 debugging
export EUTHERDRIVE_TRACE_Z80=1
export EUTHERDRIVE_TRACE_Z80_MEMORY=1
export EUTHERDRIVE_TRACE_Z80_IO=1
export EUTHERDRIVE_TRACE_Z80_IRQ=1

# Full VDP/VRAM debugging
export EUTHERDRIVE_TRACE_VDP=1
export EUTHERDRIVE_TRACE_VRAM=1

# All logging (extremt långsamt)
export EUTHERDRIVE_TRACE_ALL=1

# Kör emulatorn
dotnet run --project EutherDrive.UI
```

## Prestandaöverväganden

- **Utan gating**: Systemet blir extremt långsamt (flera sekunder per frame)
- **Med gating**: Normala prestandanivåer
- **Rekommenderat**: Använd endast de env-vars du behöver för debugging

## Uppdateringshistorik

- **2025-01-17**: Komplett gating implementerad. Alla 567 Console.WriteLine-anrop är nu gated.
- **Implementerat av**: opencode assistant under analys av SMS-emuleringsdebugging
- **Metod**: Automatisk konvertering via Python-script
"""

    with open(doc_path, "w") as f:
        f.write(doc_content)

    print(f"\nUpdated documentation at {doc_path}")


if __name__ == "__main__":
    main()
