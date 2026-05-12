# GauntletProbe

Bringup harness for Gauntlet Dark Legacy. It can cache a warm boot checkpoint so
late boot probes do not need to replay the full startup every run.

Typical use:

```sh
env EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -- \
    /home/nichlas/roms/MAME/Midway/Vegas/gauntd 450 200000 0
```

The first run saves an auto-named checkpoint under `/tmp/eutherdrive-gauntlet-probe`.
Later runs load it directly. To use frame 450 as a reusable start point and then
run farther:

```sh
env EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
    EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=450 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -- \
    /home/nichlas/roms/MAME/Midway/Vegas/gauntd 520 200000 0
```
