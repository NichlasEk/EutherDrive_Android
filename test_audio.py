import struct

with open('/home/nichlas/roms/PCE/Valis II (USA)/Valis II (USA) (Track 05).bin', 'rb') as f:
    data = f.read()

max_val = 0
for i in range(0, len(data), 2):
    val = struct.unpack('<h', data[i:i+2])[0]
    max_val = max(max_val, abs(val))
    if max_val > 2000:
        print(f"Loud sound starts at byte offset {i} (sector {i//2352})")
        break
print(f"Max amplitude in file: {max_val}")
