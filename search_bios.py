import sys

with open("bios/BIOS_CD_U.BIN", "rb") as f:
    bios = f.read()

index = 0
while True:
    index = bios.find(b'\x67\xF8', index)
    if index == -1:
        break
    print(f"Found 67F8 at {index:04X} ({index})")
    
    # Print the preceding instructions
    if index >= 6:
        w1 = (bios[index-6] << 8) | bios[index-5]
        w2 = (bios[index-4] << 8) | bios[index-3]
        w3 = (bios[index-2] << 8) | bios[index-1]
        print(f"  Preceding 6 bytes: {w1:04X} {w2:04X} {w3:04X}")
    index += 2
