# GauntletProbe

Bringup harness for Gauntlet Dark Legacy. It can cache a warm boot checkpoint so
late boot probes do not need to replay the full startup every run.

Typical use with the local ROM directory:

```sh
env EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -- \
    /home/nichlas/roms/MAME/Midway/Vegas/gauntd 450 200000 0
```

If `EUTHERDRIVE_GAUNTDL_RAW_DISK` is not set, the probe now auto-selects
`gauntd24.raw` or `gauntdl.raw` from the ROM directory before constructing the
adapter. This keeps warm-snapshot naming aligned with the actual raw disk path
used by the IDE device.

The first run saves an auto-named checkpoint under `/tmp/eutherdrive-gauntlet-probe`.
Later runs load it directly. To use frame 450 as a reusable start point and then
run farther:

```sh
env EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
    EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=450 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -- \
    /home/nichlas/roms/MAME/Midway/Vegas/gauntd 520 200000 0
```

Two default-off texture provenance overlays can be applied after loading a
snapshot:

```sh
EUTHERDRIVE_GAUNTDL_EXPERIMENT_DISK_TEXTURE_COPY=disk_offset:texture_offset:length
EUTHERDRIVE_GAUNTDL_EXPERIMENT_TEXTURE_MEMORY_COPY=source_offset:destination_offset:length
```

All fields are hexadecimal byte offsets. The first overlay reads the configured
Gauntlet raw disk; the second copies within the probe's Voodoo texture memory.
Both reject out-of-range requests and leave the saved snapshot unchanged.
