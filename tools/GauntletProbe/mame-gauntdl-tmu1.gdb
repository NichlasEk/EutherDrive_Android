set pagination off
set breakpoint pending on

# The optimized MAME build keeps function symbols but omits C++ debug types.
# At voodoo_1_device::update entry, RDI is the device pointer (x86-64 SysV).
# Disassembly of device_start shows TMU1's RAM pointer and mask at +0xb5e0
# and +0xb5e8 respectively.
break voodoo_1_device::update
# Start MAME from the saved Temple gameplay state. A short delay lets the
# resumed renderer publish a stable in-level frame before both banks are read.
ignore 1 599
run
set $tmu1ram = *(unsigned char **)($rdi + 0xb5e0)
set $tmu1mask = *(unsigned int *)($rdi + 0xb5e8)
set $tmu0ram = *(unsigned char **)($rdi + 0xa960)
set $tmu0mask = *(unsigned int *)($rdi + 0xa968)
printf "[gauntdl-reference] Temple update=600 TMU1 RAM=%p mask=0x%x\n", $tmu1ram, $tmu1mask
dump binary memory /tmp/mame-gauntdl-reference/tmu0-temple-frame-00600.bin $tmu0ram ($tmu0ram + $tmu0mask + 1)
dump binary memory /tmp/mame-gauntdl-reference/tmu1-temple-frame-00600.bin $tmu1ram ($tmu1ram + $tmu1mask + 1)
quit
