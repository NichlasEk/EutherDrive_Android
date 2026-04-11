import wave
import struct

with open('/home/nichlas/roms/PCE/Valis II (USA)/Valis II (USA) (Track 05).bin', 'rb') as f:
    data = f.read(44100 * 2 * 2 * 3) # Read 3 seconds

with wave.open('track5_dump.wav', 'wb') as wav:
    wav.setnchannels(2)
    wav.setsampwidth(2)
    wav.setframerate(44100)
    wav.writeframes(data)
