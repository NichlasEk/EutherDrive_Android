#!/usr/bin/env python3
"""C ABI bounds, ordering, byte lanes and lifetime tests on a real GPU."""
import ctypes as C
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import struct
import sys

RAM, HIDDEN, TMEM = 8 << 20, 4 << 20, 4096
U32, U64, PTR = C.c_uint32, C.c_uint64, C.c_void_p


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: check_abi.py LIBEUTHER_N64_GPU NEW_OUTPUT")
    output = Path(sys.argv[2]); output.mkdir(parents=True, exist_ok=False)
    lib = C.CDLL(str(Path(sys.argv[1]).resolve()))
    signatures = {
        "create": [U32, U32, PTR, U32, PTR, U32, C.POINTER(U64)],
        "submit": [U64, PTR, U32, C.POINTER(U64)], "wait": [U64, U64],
        "readback": [U64, U64, PTR, U32, PTR, U32, PTR, U32],
        "get_stats": [U64, PTR, U32], "device_name": [U64, PTR, U32], "destroy": [U64]
    }
    api = {}
    for name, args in signatures.items():
        fn = getattr(lib, "ed_n64_gpu_" + name); fn.argtypes = args + [PTR, U32]; fn.restype = C.c_int; api[name] = fn
    lib.ed_n64_gpu_abi.restype = U32
    assert lib.ed_n64_gpu_abi() == 1
    checks = []

    def call(name, *args, expected=0):
        error = C.create_string_buffer(1024)
        status = api[name](*args, error, len(error))
        allowed = expected if isinstance(expected, tuple) else (expected,)
        assert status in allowed, (name, status, error.value)
        if status: assert error.value, "Missing native error text"
        checks.append({"operation": name, "status": status})
        return status

    initial = bytes(range(256)) * (RAM // 256)
    ram = C.create_string_buffer(initial, RAM); hidden = C.create_string_buffer(bytes([3]) * HIDDEN, HIDDEN)
    read_ram = C.create_string_buffer(RAM); read_hidden = C.create_string_buffer(HIDDEN); read_tmem = C.create_string_buffer(TMEM)
    handle = U64()
    for abi, flags, size, pointer in [(0, 7, RAM, ram), (1, 16, RAM, ram), (1, 7, RAM - 1, ram), (1, 7, RAM, None)]:
        call("create", abi, flags, pointer, size, hidden, HIDDEN, C.byref(handle), expected=1)
        assert handle.value == 0
    call("create", 1, 7, ram, RAM, hidden, HIDDEN, C.byref(handle))
    first = handle.value
    try:
        call("wait", first, 0, expected=1); call("wait", first, 2**64 - 1, expected=1)
        def record(kind, payload): return struct.pack("<II", kind, len(payload)) + payload
        def command(*words): return record(2, struct.pack("<" + "I" * len(words), *words))
        def write(offset, payload): return record(1, struct.pack("<I", offset) + payload)
        sync = command(0xe9000000, 0)
        def submit(data):
            buf = C.create_string_buffer(data, len(data)); timeline = U64()
            call("submit", first, buf, len(data), C.byref(timeline)); return timeline.value
        def snapshot(timeline):
            call("readback", first, timeline, read_ram, RAM, read_hidden, HIDDEN, read_tmem, TMEM)
        def stats():
            result = (U64 * 9)(); call("get_stats", first, result, C.sizeof(result)); return list(result)
        before = stats()
        prefix = write(12, b"bad!")
        malformed = [b"", b"\0" * 7, record(99, b""), record(1, b"\0" * 4), write(RAM, b"x"),
                     write(0xffffffff, b"x"), write(RAM - 1, b"xy"), record(2, b"\0" * 4),
                     command(0xcf000000, 0), record(3, bytes(55)), struct.pack("<II", 1, 0xffffffff)]
        for data in malformed:
            data = prefix + data if data else b""
            buf = C.create_string_buffer(data); timeline = U64(123)
            call("submit", first, buf, len(data), C.byref(timeline), expected=1)
            assert timeline.value == 0 and stats() == before, "Malformed batch partially executed"
        timeline = U64(123)
        call("submit", first, None, 10, C.byref(timeline), expected=1)
        call("submit", first, ram, 0xffffffff, C.byref(timeline), expected=1)
        timeline = submit(sync); snapshot(timeline)
        assert read_ram.raw == initial and read_hidden.raw == bytes([3]) * HIDDEN and read_tmem.raw == bytes(TMEM)
        expected = bytearray(initial)
        patches = [(0, b"\0"), (1, b"ab"), (3, bytes(range(13))), (63, bytes(range(10))), (RAM - 3, b"x\0y")]
        events = b""
        for offset, data in patches:
            events += write(offset, data) + command(0xf7000000, 0x12345678)
            expected[offset:offset + len(data)] = data
        events += record(3, bytes(56)) + sync
        latest = submit(events)
        call("readback", first, timeline, read_ram, RAM, read_hidden, HIDDEN, read_tmem, TMEM, expected=1)
        call("readback", first, latest, read_ram, RAM - 1, read_hidden, HIDDEN, read_tmem, TMEM, expected=1)
        snapshot(latest)
        assert read_ram.raw == expected and read_hidden.raw == bytes([3]) * HIDDEN
        current = stats(); assert current[4] == 1 and current[8] == 0, current
        # A second context has independent RAM. Calls and destruction from a
        # different thread must bind that context's Granite thread-local state.
        call("create", 1, 5, ram, RAM, hidden, HIDDEN, C.byref(handle))
        second = handle.value
        try:
            with ThreadPoolExecutor(max_workers=1) as pool:
                pool.submit(call, "wait", first, latest).result()
                pool.submit(call, "destroy", first).result()
            call("destroy", first, expected=1)
            first = 0
            buf = C.create_string_buffer(sync); token = U64()
            call("submit", second, buf, len(sync), C.byref(token))
            call("readback", second, token.value, read_ram, RAM, read_hidden, HIDDEN, read_tmem, TMEM)
            assert read_ram.raw == initial, "Contexts shared RDRAM or teardown damaged another context"
        finally: call("destroy", second)
        call("get_stats", second, None, 0, expected=1)
        call("create", 1, 13, ram, RAM, hidden, HIDDEN, C.byref(handle))
        first = handle.value
        setup = command(0xef300000, 0) + command(0xed000000, 0x00100004) + command(0xff10003f, 0x300000) + command(0xfe000000, 0x500000)
        red = command(0xf7000000, 0xff01ff01) + command(0xf60fc000, 0)
        blue = command(0xf7000000, 0x003f003f) + command(0xf60fc000, 0)
        # Independent data can pass two draws and still reaches RAM at submit.
        latest = submit(setup + write(0x100003, b'a') + red + write(0x100009, b'b') + blue + sync)
        snapshot(latest)
        assert stats()[4] == 1 and read_ram.raw[0x300000:0x300080] == b'\x00\x3f' * 64
        assert read_ram.raw[0x100003] == ord('a') and read_ram.raw[0x100009] == ord('b')
        before = stats()[4]
        latest = submit(write(0x300001, b'\0') + red + write(0x100010, b'c') + blue + sync)
        snapshot(latest)
        assert stats()[4] - before == 2 and read_ram.raw[0x300000:0x300080] == b'\x00\x3f' * 64, 'Write-after-draw corruption'
        # The conservative 1024-row range wraps, although this one-row draw
        # itself stays in RAM. A low-memory write must flush before that draw.
        before = stats()[4]
        latest = submit(command(0xff10003f, RAM - 256) + write(0x10, b'q') + red + write(0x600000, b'z') + sync)
        snapshot(latest)
        assert stats()[4] - before == 2 and read_ram.raw[0x10] == ord('q'), 'Wrapped color range missed'
        before = stats()[4]
        latest = submit(command(0xff10003f, 0x300000) + command(0xfe000000, RAM - 256) + write(0x20, b'r') + red + write(0x600000, b'y') + sync)
        snapshot(latest)
        assert stats()[4] - before == 2 and read_ram.raw[0x20] == ord('r'), 'Wrapped depth range missed'
        # A hardware-invalid I4 fill reports a backend failure, without crossing
        # the C boundary with an exception; a poisoned context rejects more work.
        bad = command(0xff00003f, 0x300000) + red + sync
        buf = C.create_string_buffer(bad); token = U64()
        status = call('submit', first, buf, len(bad), C.byref(token), expected=(0, 2))
        if status == 0: call('wait', first, token.value, expected=2)
        assert stats()[8] > 0, 'Missing RDP validation failure'
        buf = C.create_string_buffer(sync)
        call('submit', first, buf, len(sync), C.byref(token), expected=2)
    finally:
        if first: call("destroy", first)
    (output / "results.json").write_text(json.dumps(checks, indent=2) + "\n")
    print(f"nativeGpuAbiChecks={len(checks)} byteLanes=exact rejectedBatches=atomic crossThreadDestroy=passed contexts=isolated")


if __name__ == "__main__":
    main()
