"""生成 破烂优化 的 exe 图标（app.ico）。

纯 Python 画像素，不依赖 Pillow / ImageMagick——项目里没有这两个依赖，
而图标又必须进构建（csproj 的 ApplicationIcon），所以用一个可复现的脚本比手放一个二进制文件好：
改配色或形状重跑一次即可。

形状：粉色圆角方块 + 白色闪电。16px 下也要认得出，所以闪电画得比常规更粗、留白更少。

用法：python tools/make-ico.py [输出路径]
"""

import struct
import sys

SIZES = [16, 24, 32, 48, 64, 128, 256]
SUPERSAMPLE = 4

TOP = (232, 96, 142)      # #E8608E
BOTTOM = (201, 58, 109)   # #C93A6D
BOLT = (255, 255, 255)

# 闪电多边形（归一化坐标，y 向下）
BOLT_POINTS = [
    (0.600, 0.070),
    (0.335, 0.535),
    (0.480, 0.535),
    (0.410, 0.945),
    (0.680, 0.450),
    (0.525, 0.450),
]

CORNER = 0.22
INSET = 0.015


def in_round_rect(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def in_polygon(x, y, points):
    inside = False
    n = len(points)
    for i in range(n):
        x1, y1 = points[i]
        x2, y2 = points[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            span = y2 - y1
            if span == 0:
                continue
            xi = x1 + (y - y1) * (x2 - x1) / span
            if x < xi:
                inside = not inside
    return inside


def dist_to_polygon(x, y, points):
    """点到多边形边界的最短距离，用来给闪电加粗。"""
    best = 1e9
    n = len(points)
    for i in range(n):
        x1, y1 = points[i]
        x2, y2 = points[(i + 1) % n]
        dx, dy = x2 - x1, y2 - y1
        seg = dx * dx + dy * dy
        t = 0.0 if seg == 0 else max(0.0, min(1.0, ((x - x1) * dx + (y - y1) * dy) / seg))
        px, py = x1 + t * dx, y1 + t * dy
        d = ((x - px) ** 2 + (y - py) ** 2) ** 0.5
        if d < best:
            best = d
    return best


def render(size):
    """返回 size×size 的 RGBA 像素（list of rows，每行 list of (r,g,b,a)）。"""
    # 16/24 这类小图标里闪电原本会细到快看不见，按归一化距离往外膨胀一点。
    bolt_pad = 0.030 if size <= 24 else 0.0
    ss = SUPERSAMPLE
    big = size * ss
    acc = [[[0.0, 0.0, 0.0, 0.0] for _ in range(size)] for _ in range(size)]

    for by in range(big):
        ny = (by + 0.5) / big
        for bx in range(big):
            nx = (bx + 0.5) / big

            if not in_round_rect(nx, ny, INSET, INSET, 1 - INSET, 1 - INSET, CORNER):
                continue

            is_bolt = in_polygon(nx, ny, BOLT_POINTS) or (
                bolt_pad > 0 and dist_to_polygon(nx, ny, BOLT_POINTS) <= bolt_pad)

            if is_bolt:
                color = BOLT
            else:
                # 竖向渐变：顶部略亮
                t = (ny - INSET) / max(1e-6, 1 - 2 * INSET)
                color = (
                    int(TOP[0] + (BOTTOM[0] - TOP[0]) * t),
                    int(TOP[1] + (BOTTOM[1] - TOP[1]) * t),
                    int(TOP[2] + (BOTTOM[2] - TOP[2]) * t),
                )

            cell = acc[by // ss][bx // ss]
            cell[0] += color[0]
            cell[1] += color[1]
            cell[2] += color[2]
            cell[3] += 1.0

    total = ss * ss
    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            r, g, b, cover = acc[y][x]
            if cover <= 0:
                row.append((0, 0, 0, 0))
            else:
                row.append((
                    int(round(r / cover)),
                    int(round(g / cover)),
                    int(round(b / cover)),
                    int(round(255 * cover / total)),
                ))
        rows.append(row)
    return rows


def to_dib(rows):
    """32bpp BMP（DIB）编码：ICO 里 BGRA + 自下而上 + AND 掩码全 0。"""
    size = len(rows)
    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        size,        # biWidth
        size * 2,    # biHeight（ICO 里是 XOR + AND 两层）
        1,           # biPlanes
        32,          # biBitCount
        0,           # biCompression = BI_RGB
        0,           # biSizeImage
        0, 0, 0, 0,  # 分辨率与调色板
    )

    body = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            r, g, b, a = rows[y][x]
            body += bytes((b, g, r, a))

    # AND 掩码：1bpp，每行按 4 字节对齐。32bpp 下 alpha 已表达透明，这里全 0。
    mask_stride = ((size + 31) // 32) * 4
    body += bytes(mask_stride * size)

    return header + bytes(body)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "src/SysDiag/app.ico"

    images = []
    for size in SIZES:
        images.append((size, to_dib(render(size))))
        print(f"  {size}x{size}  {len(images[-1][1]):,} 字节")

    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)

    offset = 6 + 16 * count
    entries = bytearray()
    for size, data in images:
        entries += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0, 0, 1, 32,
            len(data),
            offset,
        )
        offset += len(data)

    with open(out, "wb") as f:
        f.write(header)
        f.write(entries)
        for _, data in images:
            f.write(data)

    print(f"写出 {out}（{offset:,} 字节，{count} 个尺寸）")


if __name__ == "__main__":
    main()
