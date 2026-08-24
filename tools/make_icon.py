#!/usr/bin/env python3
"""生成 mod 图标:黑底 + macOS 忙碌彩虹球 + 大红叉。4x 超采样抗锯齿。"""
import math, struct, zlib, sys

SIZE = 256          # 输出尺寸
SS = 4              # 超采样倍数
N = SIZE * SS       # 渲染尺寸

BG = (10, 10, 12)
SECTORS = [
    (232, 62, 52),   # red
    (246, 148, 38),  # orange
    (250, 214, 58),  # yellow
    (88, 198, 82),   # green
    (58, 128, 236),  # blue
    (152, 88, 222),  # violet
]
RED = (235, 45, 40)
RED_EDGE = (150, 24, 20)
BALL_R = 0.345
BAR_HW = 0.072      # 红叉半宽(比例)
EDGE = 0.012

def render(px, x, y):
    cx = cy = 0.5
    dx, dy = x - cx, y - cy
    d = math.hypot(dx, dy)
    r, g, b = BG

    # 彩虹球
    if d < BALL_R:
        ang = math.atan2(dy, dx) + math.pi      # 0..2pi
        sector = int(ang / (2 * math.pi / 6)) % 6
        r, g, b = SECTORS[sector]
        # 球体光照:法线 · 左上光
        nz = math.sqrt(max(0.0, 1.0 - (d / BALL_R) ** 2))
        nx, ny = dx / BALL_R, dy / BALL_R
        lx, ly, lz = -0.45, -0.6, 0.66
        diff = max(0.0, nx * lx + ny * ly + nz * lz)
        shade = 0.38 + 0.72 * diff
        r, g, b = min(255, int(r * shade)), min(255, int(g * shade)), min(255, int(b * shade))
        # 高光点(左上)
        hd = math.hypot(x - (cx - 0.13), y - (cy - 0.16))
        if hd < 0.10:
            k = 1.0 - hd / 0.10
            add = int(170 * k * k)
            r, g, b = min(255, r + add), min(255, g + add), min(255, b + add)

    # 大红叉(贯穿整图,含暗边)
    s = (dx + dy) * math.sqrt(0.5)        # 到 +45° 对角线的有符号距离
    t = (dx - dy) * math.sqrt(0.5)        # 到 -45° 对角线的有符号距离
    for dist in (abs(s), abs(t)):
        if dist < BAR_HW + EDGE:
            r, g, b = (RED if dist < BAR_HW else RED_EDGE)
    return r, g, b

def main():
    out = bytearray()
    for yy in range(N):
        out.append(0)
        y = (yy + 0.5) / N
        for xx in range(N):
            x = (xx + 0.5) / N
            r, g, b = render(None, x, y)
            out += bytes((r, g, b))

    # 降采样:SS×SS 块平均
    img = bytearray()
    for yy in range(SIZE):
        img.append(0)
        for xx in range(SIZE):
            rs = gs = bs = 0
            for sy in range(SS):
                base = ((yy * SS + sy) * N + xx * SS) * 3 + 1
                for sx in range(SS):
                    o = base + sx * 3
                    rs += out[o]; gs += out[o + 1]; bs += out[o + 2]
            k = SS * SS
            img += bytes((rs // k, gs // k, bs // k))

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xffffffff)
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(bytes(img), 9))
           + chunk(b"IEND", b""))
    path = sys.argv[1] if len(sys.argv) > 1 else "mod_image.png"
    open(path, "wb").write(png)
    print(f"written {path} ({SIZE}x{SIZE}, {len(png)//1024}KB)")

if __name__ == "__main__":
    main()
