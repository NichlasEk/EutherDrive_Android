def old_x_math(xScroll, dot):
    coarseXScroll = (xScroll >> 3) & 0x1F
    fineXScroll = xScroll & 0x07
    
    # Old code skipped the first `fineXScroll` pixels (drawing backdrop).
    if dot < fineXScroll:
        return -1 # Backdrop
    
    column = (dot - fineXScroll) // 8
    bgTileCol = (dot - fineXScroll) % 8
    
    nameTableCol = (column + (32 - coarseXScroll)) % 32
    return nameTableCol * 8 + bgTileCol

def new_x_math(xScroll, dot):
    return (dot - xScroll) & 0xFF

for x in range(256):
    for d in range(256):
        o = old_x_math(x, d)
        n = new_x_math(x, d)
        if o != -1 and o != n:
            print(f"Mismatch at xScroll={x}, dot={d}: old={o}, new={n}")
            exit()
