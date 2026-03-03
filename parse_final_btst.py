import sys

# Sub CPU: PC=3D4 OP=0838
# Sub CPU PC=3DA w1=0838 w2=0000 w3=8001
# Wait, 0x03D4 is where the BTST is! The BEQ is at 0x03DA.
# PC=3D4: 0838 0000 8001 -> BTST #0, $8001.W
print("Sub CPU is checking bit 0 of 0x8001 (Sub CPU Reset / INT2 from Main CPU)")

# Main CPU: PC=132C w1=0006 w2=00A1 w3=200F
# 0839 0006 00A1 200F -> BTST #6, $A1200F.L
print("Main CPU is checking bit 6 of 0xA1200F (Sub CPU Communication Flags)")
