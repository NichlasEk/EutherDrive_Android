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

Warmup and final snapshot paths ending in `.gz` are compressed transparently.
This preserves the same snapshot format after decompression while avoiding the
large sparse-memory footprint of raw `.warm` files:

```sh
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/gaunt-f35.warm.gz
EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=/tmp/gaunt-f60.warm.gz
```

Two default-off texture provenance overlays can be applied after loading a
snapshot:

```sh
EUTHERDRIVE_GAUNTDL_EXPERIMENT_DISK_TEXTURE_COPY=disk_offset:texture_offset:length
EUTHERDRIVE_GAUNTDL_EXPERIMENT_TEXTURE_MEMORY_COPY=source_offset:destination_offset:length
EUTHERDRIVE_GAUNTDL_EXPERIMENT_TEXTURE_MEMORY_COPY_ZERO_DESTINATION_ONLY=1
```

All fields are hexadecimal byte offsets. The first overlay reads the configured
Gauntlet raw disk; the second copies within the probe's Voodoo texture memory.
The optional third flag makes the texture-memory overlay preserve every
already-populated destination byte. Both overlays reject out-of-range requests
and leave the saved snapshot unchanged.

A third default-off diagnostic can copy an exact file range into guest main
RAM immediately after a warm snapshot is loaded:

```sh
EUTHERDRIVE_GAUNTDL_EXPERIMENT_GUEST_MEMORY_FILE_PATCH=path:file_offset:guest_address:length
```

The three numeric fields are hexadecimal byte values. The probe validates both
the source-file and main-RAM ranges before copying. This is intended for narrow
causal replay against an owned disk or RAM oracle; it is not part of the
runtime baseline.

The Gauntlet bringup baseline also keeps the Temple weapons object source
distinct from the resource builder's writable output:

```sh
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_TEMPLE_WEAPONS_DISTINCT_RESOURCE_SOURCE=1
```

The builder uses the free high-RAM range immediately after the transient
weapons texture companion as its work arena, restores the source-table slot
after returning, and publishes the resource-table pointer into the work arena.
When an older warm snapshot still has an aliased resource pointer, the repair
first preserves its built records there and then rehydrates the immutable
`weapons/objects.rom` source from the configured raw disk.
