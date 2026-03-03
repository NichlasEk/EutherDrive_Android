import sys

with open("bios/BIOS_CD_U.BIN", "rb") as f:
    bios = f.read()

pc = 0x3D0
for i in range(10):
    w1 = (bios[pc] << 8) | bios[pc+1]
    print(f"{pc:04X}: {w1:04X}")
    pc += 2
