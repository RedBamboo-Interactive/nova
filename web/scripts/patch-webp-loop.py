"""Patch the loop count in animated WebP files.

Usage:
  python patch-webp-loop.py <file.webp> [loop_count]

loop_count defaults to 1 (play once). Use 0 for infinite loop.
"""
import struct
import sys

def patch(path, loops=1):
    with open(path, "rb") as f:
        data = bytearray(f.read())

    pos = 12
    while pos < len(data) - 8:
        fourcc = data[pos:pos + 4]
        size = struct.unpack_from("<I", data, pos + 4)[0]
        if fourcc == b"ANIM":
            loop_offset = pos + 8 + 4
            old = struct.unpack_from("<H", data, loop_offset)[0]
            struct.pack_into("<H", data, loop_offset, loops)
            with open(path, "wb") as f:
                f.write(data)
            print(f"{path}: {old} -> {loops}")
            return
        pos += 8 + size + (size % 2)

    print(f"{path}: ANIM chunk not found", file=sys.stderr)
    sys.exit(1)

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    loops = int(sys.argv[2]) if len(sys.argv) > 2 else 1
    patch(sys.argv[1], loops)
