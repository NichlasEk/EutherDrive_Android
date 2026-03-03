import sys

# Sub CPU reads:
w1 = 0x0838
w2 = 0x0000
w3 = 0x8001

if (w1 & 0xFFC0) == 0x0800:
    bit_num = w1 & 0xFF
    addr = (w2 << 16) | w3
    print(f"Sub CPU is waiting for bit {bit_num} at address 0x{addr:06X}")
