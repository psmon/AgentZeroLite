"""Generate the 112x112 ARGB8888 LVGL9 launcher icon for the AskBot app (no PIL).

Glyph: three linked nodes - an actor system, with the device as the small node
reaching the big one. Same rounded-square plate as the other app icons.
"""
import math, sys

W = H = 112
px = [[(0, 0, 0, 0) for _ in range(W)] for _ in range(H)]


def put(x, y, c, a=255):
    if 0 <= x < W and 0 <= y < H:
        r, g, b = c
        px[y][x] = (r, g, b, a)


def blend(x, y, c, a):
    if a <= 0:
        return
    r0, g0, b0, a0 = px[y][x]
    a = min(255, a)
    af = a / 255
    px[y][x] = (int(c[0] * af + r0 * (1 - af)),
                int(c[1] * af + g0 * (1 - af)),
                int(c[2] * af + b0 * (1 - af)),
                max(a0, a))


# plate
cx, cy = W / 2, H / 2
R = 26
for y in range(H):
    for x in range(W):
        dx = max(abs(x + 0.5 - cx) - (W / 2 - R), 0)
        dy = max(abs(y + 0.5 - cy) - (H / 2 - R), 0)
        d = math.hypot(dx, dy)
        if d <= R:
            put(x, y, (0x1E, 0x22, 0x30), 255 if d < R - 1 else int(255 * (R - d)))

PURPLE = (0xA7, 0x8B, 0xFA)
CYAN = (0x5C, 0xE1, 0xE6)
GREEN = (0x3D, 0xD6, 0x8C)
GRAY = (0x4A, 0x50, 0x60)

NODES = [
    (56, 40, 15, PURPLE),   # the host's actor
    (36, 74, 10, CYAN),     # the device's client actor
    (76, 72, 8, GREEN),     # another peer
]


def line(x0, y0, x1, y1, width, color):
    length = math.hypot(x1 - x0, y1 - y0)
    if length == 0:
        return
    for y in range(H):
        for x in range(W):
            # distance from the point to the segment
            t = ((x + 0.5 - x0) * (x1 - x0) + (y + 0.5 - y0) * (y1 - y0)) / (length * length)
            t = max(0.0, min(1.0, t))
            d = math.hypot(x + 0.5 - (x0 + t * (x1 - x0)), y + 0.5 - (y0 + t * (y1 - y0)))
            if d <= width:
                blend(x, y, color, 255 if d < width - 1 else int(255 * (width - d)))


line(56, 40, 36, 74, 2.5, GRAY)
line(56, 40, 76, 72, 2.5, GRAY)

for (nx, ny, nr, color) in NODES:
    for y in range(H):
        for x in range(W):
            d = math.hypot(x + 0.5 - nx, y + 0.5 - ny)
            if d <= nr:
                blend(x, y, color, 255 if d < nr - 1 else int(255 * (nr - d)))

# a question mark cut into the big node, so it reads as "ask"
WHITE = (0x12, 0x14, 0x1A)
for y in range(H):
    for x in range(W):
        dx, dy = x + 0.5 - 56, y + 0.5 - 38
        d = math.hypot(dx, dy)
        if 4.0 <= d <= 6.5 and dy < 2 and not (dx < 0 and dy > 0):
            blend(x, y, WHITE, 255)
        if abs(dx - 0.5) < 1.4 and 2.0 < dy < 5.0:
            blend(x, y, WHITE, 255)
        if math.hypot(dx - 0.5, dy - 7.5) <= 1.5:
            blend(x, y, WHITE, 255)

name = "esp_brookesia_app_icon_launcher_askbot_112_112"
out = sys.argv[1]
with open(out, "w", newline="\n") as f:
    f.write('#ifdef __has_include\n#if __has_include("lvgl.h")\n#ifndef LV_LVGL_H_INCLUDE_SIMPLE\n#define LV_LVGL_H_INCLUDE_SIMPLE\n#endif\n#endif\n#endif\n\n')
    f.write('#if defined(LV_LVGL_H_INCLUDE_SIMPLE)\n#include "lvgl.h"\n#else\n#include "lvgl/lvgl.h"\n#endif\n\n')
    f.write('#ifndef LV_ATTRIBUTE_MEM_ALIGN\n#define LV_ATTRIBUTE_MEM_ALIGN\n#endif\n\n')
    f.write(f'const LV_ATTRIBUTE_MEM_ALIGN LV_ATTRIBUTE_LARGE_CONST uint8_t {name}_map[] = {{\n')
    for y in range(H):
        row = []
        for x in range(W):
            r, g, b, a = px[y][x]
            row += [b, g, r, a]  # ARGB8888 in memory = B,G,R,A (little endian)
        f.write("    " + ",".join(f"0x{v:02x}" for v in row) + ",\n")
    f.write("};\n\n")
    f.write(f'const lv_image_dsc_t {name} = {{\n')
    f.write('    .header.cf = LV_COLOR_FORMAT_ARGB8888,\n    .header.magic = LV_IMAGE_HEADER_MAGIC,\n')
    f.write(f'    .header.w = {W},\n    .header.h = {H},\n    .header.stride = {W * 4},\n')
    f.write(f'    .data_size = {W * H} * 4,\n    .data = {name}_map,\n}};\n')
print("wrote", out)
