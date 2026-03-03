import sys

# Sub CPU: PC=3D4: 0838 0000 8001
# 0838 = BTST #x, (xxx).W
# w2 = immediate data (0000) -> wait, no. Word 2 is the data. 
# So it's BTST #0, $8001.W
print("Sub CPU is polling BTST #0, ($8001).W")

# Main CPU: PC=132C: 0839 0006 00A1 200F
# 0839 = BTST #x, (xxx).L
# w2 = 0006 -> Bit 6
# w3, w4 = 00A1, 200F -> Address $A1200F
print("Main CPU is polling BTST #6, ($A1200F).L")
