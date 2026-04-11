# Let's test the modulo math vs the old math
def old_math(yScroll, scanline, rows=28):
    coarseYScroll = yScroll >> 3
    fineYScroll = yScroll & 0x07
    nameTableRow = (((scanline + fineYScroll) // 8) + coarseYScroll) % rows
    bgTileRow = (scanline + fineYScroll) % 8
    return nameTableRow * 8 + bgTileRow

def new_math(yScroll, scanline, rows=28):
    return (scanline + yScroll) % (rows * 8)

for y in range(256):
    for s in range(192):
        o = old_math(y, s)
        n = new_math(y, s)
        if o != n:
            print(f"Mismatch at yScroll={y}, scanline={s}: old={o}, new={n}")
