#!/usr/bin/env python3
import os
import re


def fix_debug_writeline():
    # Mappa Debug.WriteLine mönster till env-vars
    patterns = {
        r'Debug\.WriteLine\(\$"\[BUS\]': "EUTHERDRIVE_TRACE_BUS",
        r'Debug\.WriteLine\(\$"\[CARTRIDGE\]': "EUTHERDRIVE_TRACE_CARTRIDGE",
        r'Debug\.WriteLine\(\$"\[VDP\]': "EUTHERDRIVE_TRACE_VDP",
        r'Debug\.WriteLine\(\$"\[md_main\]': "EUTHERDRIVE_TRACE_MAIN",
        r'Debug\.WriteLine\("\[CARTRIDGE\]': "EUTHERDRIVE_TRACE_CARTRIDGE",
        r'Debug\.WriteLine\("\[VDP\]': "EUTHERDRIVE_TRACE_VDP",
    }

    for root, dirs, files in os.walk("."):
        for file in files:
            if file.endswith(".cs"):
                filepath = os.path.join(root, file)
                try:
                    with open(filepath, "r", encoding="utf-8") as f:
                        content = f.read()

                    modified = False
                    new_content = content

                    # Hitta alla Debug.WriteLine
                    for pattern, env_var in patterns.items():
                        # Skapa regex för att hitta hela Debug.WriteLine-anropet
                        debug_pattern = rf"(\s*){pattern}[^)]+\)"
                        matches = re.findall(debug_pattern, content)

                        if matches:
                            print(f"\n{filepath}:")
                            print(
                                f"  Found {len(matches)} Debug.WriteLine with pattern: {pattern}"
                            )

                            # Ersätt med env-var check
                            replacement = (
                                rf'\1if (string.Equals(Environment.GetEnvironmentVariable("{env_var}"), "1", StringComparison.Ordinal))\n\1    '
                                + pattern[17:]
                            )  # Ta bort "Debug.WriteLine("
                            new_content = re.sub(
                                debug_pattern, replacement, new_content
                            )
                            modified = True

                    if modified:
                        with open(filepath, "w", encoding="utf-8") as f:
                            f.write(new_content)
                        print(f"  Fixed!")

                except Exception as e:
                    print(f"Error processing {filepath}: {e}")


if __name__ == "__main__":
    fix_debug_writeline()
