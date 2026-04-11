def decode(nibble, step_index, predictor):
    index_shift = [-1, -1, -1, -1, 2, 4, 6, 8]
    step_table = [16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552]
    
    sign = -1 if (nibble & 8) else 1
    value = nibble & 7
    
    step = step_table[step_index]
    delta = step >> 3
    if value & 1: delta += step >> 2
    if value & 2: delta += step >> 1
    if value & 4: delta += step
    
    delta *= sign
    predictor += delta
    predictor = max(0, min(4095, predictor))
    
    step_index += index_shift[value]
    step_index = max(0, min(48, step_index))
    
    return predictor, step_index

p, idx = 2048, 0
for n in [8, 0, 8, 11, 12, 3, 0, 3, 11, 8, 11, 8, 0, 8, 0, 3, 0, 8, 11, 8, 0, 8, 4, 8, 11, 3, 0, 8, 4, 8, 0, 8, 0, 8, 0, 3, 4, 15, 14, 9, 0, 3, 2, 3, 8, 14, 12, 3, 6, 1]:
    p, idx = decode(n, idx, p)
    print(f"nibble={n:x} pred={p} idx={idx}")
