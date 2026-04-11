import struct

with open('/home/nichlas/roms/PCE/Valis II (USA)/Valis II (USA) (Track 05).bin', 'rb') as f:
    data = f.read(44100 * 2 * 2 * 3) # Read 3 seconds

max_val_le = 0
max_val_be = 0
for i in range(0, len(data), 2):
    val_le = struct.unpack('<h', data[i:i+2])[0]
    val_be = struct.unpack('>h', data[i:i+2])[0]
    max_val_le = max(max_val_le, abs(val_le))
    max_val_be = max(max_val_be, abs(val_be))

print(f"Max LE amplitude: {max_val_le}")
print(f"Max BE amplitude: {max_val_be}")
