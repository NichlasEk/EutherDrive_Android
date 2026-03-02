import sys

def decode_btst(opcode):
    if (opcode & 0xFFC0) == 0x0800:
        # BTST #<data>, (xxx).L
        return "BTST #??, (ext).L"
    elif (opcode & 0xF1C0) == 0x0100:
        # BTST Dn, (xxx).L
        return f"BTST D{(opcode >> 9) & 7}, (ext).L"
    return "UNKNOWN"

print(f"Main OP=0839 is {decode_btst(0x0839)}")
