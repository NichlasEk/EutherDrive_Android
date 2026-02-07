#!/usr/bin/env python3
import struct


def disasm_68000(opcode):
    """Enkel 68000 disassembler för vårt problem"""
    op = (opcode >> 12) & 0xF

    # ROL/L/ROR/L etc
    if (opcode & 0xF000) == 0xE000:
        # 1110 xxx1 xmm0 rrrr
        dir_bit = (opcode >> 3) & 0x1  # bit 3: 0=ROR/ROXR, 1=ROL/ROXL
        size_bits = (opcode >> 6) & 0x3  # bits 6-7: 00=byte, 01=word, 10=long
        count_bits = (opcode >> 9) & 0x7  # bits 9-11: 000=8, 001=1, 010=2, etc
        reg = opcode & 0x7  # bits 0-2: register

        count = 8 if count_bits == 0 else count_bits
        size = ["byte", "word", "long"][size_bits]

        # Kolla om det är ROX (rotate with extend)
        if (opcode & 0x00C0) == 0x00C0:  # bits 6-7 = 11
            instr = "ROXL" if dir_bit else "ROXR"
        else:
            instr = "ROL" if dir_bit else "ROR"

        return f"{instr}.{size} #{count}, D{reg} (opcode: 0x{opcode:04X})"

    # ANDI - exakta matchningar
    if (opcode & 0xFFF8) == 0x0200:  # ANDI.B #imm, Dn
        reg = opcode & 0x7
        return f"ANDI.B #imm, D{reg} (needs immediate byte)"
    elif (opcode & 0xFFF8) == 0x0240:  # ANDI.W #imm, Dn
        reg = opcode & 0x7
        return f"ANDI.W #imm, D{reg} (needs immediate word)"
    elif (opcode & 0xFFF8) == 0x0280:  # ANDI.L #imm, Dn
        reg = opcode & 0x7
        return f"ANDI.L #imm, D{reg} (needs immediate long)"

    # ASL/ASR/LSL/LSR
    elif (opcode & 0xF100) == 0xE100:  # ASL/LSL etc
        # Komplex format...
        return f"Shift/Rotate opcode: 0x{opcode:04X}"

    return f"Unknown: 0x{opcode:04X}"


# Testa med vårt kända opcode
print("Test disassembler:")
print("0xE198 =", disasm_68000(0xE198))
print("0x0240 =", disasm_68000(0x0240))  # ANDI.W #imm, D0
print("0x0200 =", disasm_68000(0x0200))  # ANDI.B #imm, D0
print("0x0280 =", disasm_68000(0x0280))  # ANDI.L #imm, D0

# Testa andra möjliga opcodes
print("\nAndra möjliga opcodes för rotation:")
print("0xE118 =", disasm_68000(0xE118))  # ROL.B #8, D0?
print("0xE158 =", disasm_68000(0xE158))  # ROL.W #8, D0?
print("0xE198 =", disasm_68000(0xE198))  # ROL.L #8, D0
print("0xE018 =", disasm_68000(0xE018))  # ROR.B #8, D0?
print("0xE058 =", disasm_68000(0xE058))  # ROR.W #8, D0?
print("0xE098 =", disasm_68000(0xE098))  # ROR.L #8, D0

print("\nVad behöver spelet?")
print("Start: D0 = 0x00070000")
print("Efter instruktion: behöver D0 = 0x00000007 för ANDI.W att fungera")
print("\nMöjligheter:")
print("1. ROL.L #8 på 0x07000000 ger 0x00000007")
print("2. ROR.L #8 på 0x00000700 ger 0x00000007")
print("3. ROR.L #24 på 0x00070000 ger 0x00000007")
print("4. LSR.L #20 på 0x07000000 ger 0x00000007")
print("\nMen ROM har 0xE198 vid 0x013A4E, vilket är ROL.L #8, D0")
