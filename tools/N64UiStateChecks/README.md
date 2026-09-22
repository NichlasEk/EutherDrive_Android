# N64 UI savestate integration check

This console check invokes the actual Save/Load commands, adapter and slot
service without opening a window. It requires the Linux GPU backend and a
big-endian Castlevania or other compatible N64 ROM. It copies the ROM and writes
disposable slots only inside a new output directory.

```sh
dotnet build tools/N64UiStateChecks/N64UiStateChecks.csproj -c Release -m:1 \
  -p:N64LiveGpu=true -o .build-tmp/n64-ui-state-checks
GRANITE_VULKAN_NO_VALIDATION=0 PARALLEL_RDP_SMALL_TYPES=1 \
PARALLEL_RDP_FORCE_SYNC_SHADER=1 GRANITE_NUM_WORKER_THREADS=2 \
dotnet .build-tmp/n64-ui-state-checks/N64UiStateChecks.dll \
  /path/to/game.z64 /path/to/libeuther_n64_gpu.so .build-tmp/new-ui-slot-run
```

It checks GPU save/load and resume, frame-counter restoration, the untouched
other slot, empty-slot errors, and preserving the container on save failure.
The Vulkan validation layer must be installed. No generated ROM or slot file
belongs in version control.
