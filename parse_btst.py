import sys

opcode = 0x0839
w1 = 0x0001
w2 = 0x00A1
w3 = 0x2003

# BTST #<data>, (xxx).L
bit_num = w1 & 0xFF
addr = (w2 << 16) | w3

print(f"Main CPU is waiting for bit {bit_num} at address 0x{addr:06X}")
