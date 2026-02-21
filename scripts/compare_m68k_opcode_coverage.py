#!/usr/bin/env python3
"""
Static first-pass comparison between EutherDrive M68K opcode table and jgenesis instruction set.

This compares:
1) Registered opcode mnemonics in EutherDrive's opcode_add table.
2) Instruction variants referenced in jgenesis table population.

It does not prove semantic equivalence. It is used to quickly rule out missing-opcode coverage.
"""

from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
EUTH_OPCODE_TABLE = ROOT / "EutherDrive.Core" / "MdTracerCore" / "md_m68k_initialize2.cs"
JGEN_TABLE = Path("/home/nichlas/jgenesis/cpu/m68000-emu/src/core/instructions/table.rs")


def parse_eutherdrive() -> tuple[set[str], set[int]]:
    text = EUTH_OPCODE_TABLE.read_text(encoding="utf-8", errors="ignore")
    mnemonics = set(
        re.findall(
            r'opcode_add\s*\(\s*0x([0-9a-fA-F]+)\s*,\s*[^,]+,\s*"([A-Z0-9]+)"',
            text,
        )
    )
    opcode_values: set[int] = set()
    mnem_set: set[str] = set()
    for opcode_hex, mnemonic in mnemonics:
        opcode_values.add(int(opcode_hex, 16) & 0xFFFF)
        mnem_set.add(mnemonic)
    return mnem_set, opcode_values


def parse_jgenesis_variants() -> set[str]:
    text = JGEN_TABLE.read_text(encoding="utf-8", errors="ignore")
    return set(re.findall(r"Instruction::([A-Za-z0-9_]+)", text))


def build_variant_mapping() -> dict[str, list[str]]:
    # Approximate mapping for first-pass coverage. Semantic equivalence must still be verified separately.
    return {
        "Add": ["ADD", "ADDA", "ADDI", "ADDQ", "ADDX"],
        "AddDecimal": ["ABCD"],
        "And": ["AND", "ANDI", "ANDITOCCR", "ANDITOSR"],
        "Branch": ["BRA", "BCC"],
        "BranchDecrement": ["DBCC"],
        "BranchToSubroutine": ["BSR"],
        "BitChange": ["BCHG"],
        "BitClear": ["BCLR"],
        "BitSet": ["BSET"],
        "BitTest": ["BTST"],
        "CheckRegister": ["CHK"],
        "Clear": ["CLR"],
        "Compare": ["CMP", "CMPA", "CMPI", "CMPM"],
        "DivideSigned": ["DIVS"],
        "DivideUnsigned": ["DIVU"],
        "ExclusiveOr": ["EOR", "EORI", "EORITOCCR", "EORITOSR"],
        "Exchange": ["EXG"],
        "Extend": ["EXT"],
        "Jump": ["JMP"],
        "JumpToSubroutine": ["JSR"],
        "Link": ["LINK"],
        "LoadEffectiveAddress": ["LEA"],
        "Move": ["MOVE", "MOVEA"],
        "MoveFromSr": ["MOVEFROMSR"],
        "MoveMultiple": ["MOVEM"],
        "MovePeripheral": ["MOVEP"],
        "MoveQuick": ["MOVEQ"],
        "MoveToCcr": ["MOVETOCCR"],
        "MoveToSr": ["MOVETOSR"],
        "MoveUsp": ["MOVEUSP"],
        "MultiplySigned": ["MULS"],
        "MultiplyUnsigned": ["MULU"],
        "Negate": ["NEG", "NEGX"],
        "NegateDecimal": ["NBCD"],
        "NoOp": ["NOP"],
        "Not": ["NOT"],
        "Or": ["OR", "ORI", "ORITOCCR", "ORITOSR"],
        "PushEffectiveAddress": ["PEA"],
        "Reset": ["RESET"],
        "ReturnAndRestore": ["RTR"],
        "ReturnFromException": ["RTE"],
        "ReturnFromSubroutine": ["RTS"],
        "Rotate": ["RO", "ROX", "LS", "AS"],
        "Set": ["SCC"],
        "Stop": ["STOP"],
        "Subtract": ["SUB", "SUBA", "SUBI", "SUBQ", "SUBX"],
        "SubtractDecimal": ["SBCD"],
        "Swap": ["SWAP"],
        "Test": ["TST"],
        "TestAndSet": ["TAS"],
        "Trap": ["TRAP"],
        "TrapOnOverflow": ["TRAPV"],
        "Unlink": ["UNLK"],
    }


def main() -> int:
    e_mnemonics, e_opcodes = parse_eutherdrive()
    j_variants = parse_jgenesis_variants()
    mapping = build_variant_mapping()

    mapped_variants = sorted(v for v in j_variants if v in mapping)
    missing_variants = []
    for variant in mapped_variants:
        if not any(m in e_mnemonics for m in mapping[variant]):
            missing_variants.append(variant)

    print("M68K static coverage comparison")
    print(f"EutherDrive unique registered opcodes: {len(e_opcodes)}")
    print(f"EutherDrive unique mnemonics: {len(e_mnemonics)}")
    print(f"jgenesis instruction variants seen in table.rs: {len(j_variants)}")
    print(f"Mapped variants checked: {len(mapped_variants)}")
    print(f"Mapped variants with no EutherDrive mnemonic hit: {len(missing_variants)}")

    if missing_variants:
        print("Missing mapped variants:")
        for variant in missing_variants:
            print(f"  - {variant} (expected one of {mapping[variant]})")
    else:
        print("No first-pass mnemonic coverage gaps in mapped variants.")

    print("\nNote: semantic mismatches can still exist even with full mnemonic coverage.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
