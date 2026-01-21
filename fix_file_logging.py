#!/usr/bin/env python3
import os
import re
import sys


def fix_file_logging():
    # Mappa filnamn till MdLog-metoder baserat på innehåll
    file_patterns = {
        "/tmp/eutherdrive_render.log": "WriteLineVdp",
        "/tmp/eutherdrive_sram.log": "WriteLineSram",
        "/tmp/eutherdrive.log": "WriteLineTest",
        "/tmp/eutherdrive_ascii_adapter.log": "WriteLineUi",
        "rom_start.log": "WriteLineTest",  # Fallback
    }

    # Sök efter File.AppendAllText i alla C# filer
    for root, dirs, files in os.walk("."):
        for file in files:
            if file.endswith(".cs"):
                filepath = os.path.join(root, file)
                try:
                    with open(filepath, "r", encoding="utf-8") as f:
                        content = f.read()

                    # Hitta alla File.AppendAllText anrop
                    pattern = r'System\.IO\.File\.AppendAllText\s*\(\s*["\']([^"\']+)["\'][^)]+\)'
                    matches = re.findall(pattern, content)

                    if matches:
                        print(f"\n{filepath}:")
                        for logfile in matches:
                            # Bestäm vilken MdLog-metod att använda
                            mdlog_method = None
                            for pattern, method in file_patterns.items():
                                if pattern in logfile:
                                    mdlog_method = method
                                    break

                            if not mdlog_method:
                                # Fallback: kolla om det finns Console.WriteLine nära
                                lines = content.split("\n")
                                for i, line in enumerate(lines):
                                    if f'File.AppendAllText("{logfile}"' in line:
                                        # Kolla föregående rad för Console.WriteLine
                                        if (
                                            i > 0
                                            and "Console.WriteLine" in lines[i - 1]
                                        ):
                                            # Extrahera meddelandet från Console.WriteLine
                                            msg_match = re.search(
                                                r'Console\.WriteLine\s*\(\s*@?"([^"]+)"',
                                                lines[i - 1],
                                            )
                                            if msg_match:
                                                msg = msg_match.group(1)
                                                if "[VSCROLL]" in msg:
                                                    mdlog_method = "WriteLineVdp"
                                                elif "[WRAM-PAL-" in msg:
                                                    mdlog_method = "WriteLineM68k"
                                                elif "[DATA-WRITE]" in msg:
                                                    mdlog_method = "WriteLineVdp"
                                                elif "[INTERLACE-DEBUG]" in msg:
                                                    mdlog_method = "WriteLineVdp"

                            if mdlog_method:
                                print(f"  {logfile} -> MdLog.{mdlog_method}")
                                # Ersätt File.AppendAllText med MdLog.{mdlog_method}
                                # Men behåll Console.WriteLine om det finns
                                # Vi behöver faktiskt bara ta bort File.AppendAllText
                                # eftersom Console.WriteLine redan är konverterat
                                old_pattern = (
                                    r'(\s*)System\.IO\.File\.AppendAllText\s*\(\s*["\']'
                                    + re.escape(logfile)
                                    + r'["\'][^)]+\)(\s*;?\s*)'
                                )
                                new_content = re.sub(
                                    old_pattern,
                                    r"\1// File logging removed - use MdLog instead\2",
                                    content,
                                )

                                with open(filepath, "w", encoding="utf-8") as f:
                                    f.write(new_content)
                                print(f"    Fixed!")
                            else:
                                print(
                                    f"  {logfile} -> UNKNOWN (no Console.WriteLine found nearby)"
                                )
                except Exception as e:
                    print(f"Error processing {filepath}: {e}")


if __name__ == "__main__":
    fix_file_logging()
