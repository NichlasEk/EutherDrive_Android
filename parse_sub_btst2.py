import sys

# Sub CPU reads:
w1 = 0x0838
w2 = 0x0000
w3 = 0x8001

if (w1 & 0xFFC0) == 0x0800:
    # 0838 is BTST #xx, (xxx).W  (word-sized absolute address, unlike .L which is 0839)
    # The immediate data is in the low byte of w1... wait, no.
    # Instruction format for BTST #<data>, <ea>:
    # Word 1: 0000 1000 0011 1000 = 0x0838
    # Bit 5-3 = 001 (Mode 1: (An) ??? No, 001 is usually register, but let's check M68k manual)
    # Actually: Mode 111 (7), Register 000 (0) -> Absolute Short.
    
    # 0838 is BTST #<data>, (xxx).W
    # Let's write a better decoder.
    pass

print("0838 = BTST #<data>, (xxx).W")
