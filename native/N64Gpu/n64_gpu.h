#ifndef EUTHER_N64_GPU_H
#define EUTHER_N64_GPU_H
#include <stdint.h>
#if defined(__GNUC__)
#define ED_N64_GPU_API __attribute__((visibility("default")))
#else
#define ED_N64_GPU_API
#endif
#ifdef __cplusplus
extern "C" {
#endif

enum { ED_N64_GPU_ABI = 1, ED_N64_GPU_RAM_SIZE = 8 << 20, ED_N64_GPU_HIDDEN_SIZE = 4 << 20, ED_N64_GPU_TMEM_SIZE = 4096 };
enum { ED_N64_GPU_VALIDATE = 1, ED_N64_GPU_BATCH_STATE_WRITES = 2, ED_N64_GPU_REQUIRE_DISCRETE = 4,
       ED_N64_GPU_DEFER_DISJOINT_WRITES = 8, // Includes state-write batching.
       ED_N64_GPU_DEFER_DISJOINT_LOAD_BLOCKS = 16 }; // Includes both previous batching modes.
enum { ED_N64_GPU_OK = 0, ED_N64_GPU_ARGUMENT = 1, ED_N64_GPU_BACKEND = 2 };
// All fields and pointers belong to the caller. Functions retain no caller
// buffers after returning. Handles are IDs, never addresses. Calls serialize.
// Buffers must remain valid and unmodified for the duration of a call.
// Error/name buffers receive terminated UTF-8. Discard a context after BACKEND
// failure; no partial-submit rollback or device-loss recovery is promised.
typedef struct ed_n64_gpu_stats {
    uint64_t submissions, commands, write_spans, write_bytes, write_barriers;
    uint64_t readback_bytes, last_timeline, completed_timeline, validation_errors;
} ed_n64_gpu_stats;

ED_N64_GPU_API uint32_t ed_n64_gpu_abi(void);
ED_N64_GPU_API int ed_n64_gpu_create(uint32_t abi, uint32_t flags,
    const uint8_t *ram_be, uint32_t ram_size, const uint8_t *hidden_logical, uint32_t hidden_size,
    uint64_t *handle, char *error, uint32_t error_capacity);
// Batch: repeated u32 kind, u32 length, payload, all integer fields LE.
// 1 = u32 offset + exact big-endian RDRAM bytes; 2 = raw u32 command words;
// 3 = 14 big-endian VI register values. The whole batch is checked before
// execution. Commands must be complete; maximum batch size is 64 MiB.
// All commands/writes precede the returned timeline; submission isn't completion.
ED_N64_GPU_API int ed_n64_gpu_submit(uint64_t handle, const uint8_t *batch, uint32_t size,
    uint64_t *timeline, char *error, uint32_t error_capacity);
ED_N64_GPU_API int ed_n64_gpu_wait(uint64_t handle, uint64_t timeline, char *error, uint32_t error_capacity);
// Waits for the latest submitted timeline and copies CPU-visible memory.
// RAM is big-endian bytes, hidden is logical halfword order; TMEM is opaque
// backend bytes for validation, not a portable savestate representation.
ED_N64_GPU_API int ed_n64_gpu_readback(uint64_t handle, uint64_t timeline,
    uint8_t *ram_be, uint32_t ram_size, uint8_t *hidden_logical, uint32_t hidden_size,
    uint8_t *tmem, uint32_t tmem_size, char *error, uint32_t error_capacity);
ED_N64_GPU_API int ed_n64_gpu_get_stats(uint64_t handle, ed_n64_gpu_stats *stats, uint32_t size,
    char *error, uint32_t error_capacity);
ED_N64_GPU_API int ed_n64_gpu_device_name(uint64_t handle, char *name, uint32_t capacity,
    char *error, uint32_t error_capacity);
// Optional ABI-1 extension. A checkpoint drains pending work and serializes
// persistent RDP registers, tiles, TMEM and noise position. RDRAM/hidden bytes
// are supplied separately to create(). No scanout/upscaling state is exposed.
// Save requires a 16-KiB buffer and returns the actual byte count. Load is
// permitted only on a newly created context; discard it if import fails.
ED_N64_GPU_API int ed_n64_gpu_save_state(uint64_t handle, uint8_t *state, uint32_t capacity,
    uint32_t *written, char *error, uint32_t error_capacity);
ED_N64_GPU_API int ed_n64_gpu_load_state(uint64_t handle, const uint8_t *state, uint32_t size,
    char *error, uint32_t error_capacity);
ED_N64_GPU_API int ed_n64_gpu_destroy(uint64_t handle, char *error, uint32_t error_capacity);

#ifdef __cplusplus
}
#endif
#endif
