"""从 shot.py 输出的 32 位 BMP 里裁一块并放大成 PNG（纯标准库，不依赖 Pillow）。

用法：python tools/zoom.py in.bmp out.png x y w h [zoom]

用来在验收 UI 细节时把某块区域放大看清楚——shot.py 默认缩到 62%，
看图标/对齐这种像素级细节会糊，所以需要单独裁切放大。
"""
import struct
import sys
import zlib


def read_bmp(path):
    data = open(path, "rb").read()
    if data[:2] != b"BM":
        raise SystemExit("不是 BMP 文件")
    offset = struct.unpack_from("<I", data, 10)[0]
    width = struct.unpack_from("<i", data, 18)[0]
    height = struct.unpack_from("<i", data, 22)[0]
    bits = struct.unpack_from("<H", data, 28)[0]
    if bits != 32:
        raise SystemExit(f"只支持 32 位 BMP，当前 {bits}")

    top_down = height < 0
    height = abs(height)
    stride = width * 4
    return data, offset, width, height, stride, top_down


def write_png(path, width, height, rows):
    raw = b"".join(b"\x00" + row for row in rows)

    def chunk(tag, payload):
        body = tag + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", header))
        f.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        f.write(chunk(b"IEND", b""))


def main():
    src, dst = sys.argv[1], sys.argv[2]
    x, y, w, h = (int(v) for v in sys.argv[3:7])
    zoom = float(sys.argv[7]) if len(sys.argv) > 7 else 2.0

    data, offset, width, height, stride, top_down = read_bmp(src)

    x = max(0, min(x, width - 1))
    y = max(0, min(y, height - 1))
    w = max(1, min(w, width - x))
    h = max(1, min(h, height - y))

    out_w = int(w * zoom)
    out_h = int(h * zoom)
    rows = []

    for oy in range(out_h):
        sy = y + int(oy / zoom)
        row_index = sy if top_down else (height - 1 - sy)
        line = data[offset + row_index * stride: offset + row_index * stride + stride]
        out = bytearray()
        for ox in range(out_w):
            sx = x + int(ox / zoom)
            p = sx * 4
            out += bytes((line[p + 2], line[p + 1], line[p]))   # BGRA -> RGB
        rows.append(bytes(out))

    write_png(dst, out_w, out_h, rows)
    print(f"{dst}  {out_w}x{out_h}  (源区域 {x},{y} {w}x{h} 放大 {zoom}x)")


if __name__ == "__main__":
    main()
