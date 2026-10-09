"""Generate the 112x112 ARGB8888 LVGL9 launcher icon for the Chat app: a microphone on a rounded square (no PIL)."""
import math, sys
W = H = 112
px = [[(0, 0, 0, 0) for _ in range(W)] for _ in range(H)]

def put(x, y, c, a=255):
    if 0 <= x < W and 0 <= y < H:
        r, g, b = c
        px[y][x] = (r, g, b, a)

def blend(x, y, c, a):
    if a <= 0: return
    r0, g0, b0, a0 = px[y][x]
    a = min(255, a); af = a / 255
    px[y][x] = (int(c[0] * af + r0 * (1 - af)), int(c[1] * af + g0 * (1 - af)), int(c[2] * af + b0 * (1 - af)), max(a0, a))

cx, cy = W / 2, H / 2
R = 26
for y in range(H):
    for x in range(W):
        dx = max(abs(x + 0.5 - cx) - (W / 2 - R), 0)
        dy = max(abs(y + 0.5 - cy) - (H / 2 - R), 0)
        d = math.hypot(dx, dy)
        if d <= R:
            put(x, y, (0x1E, 0x22, 0x30), 255 if d < R - 1 else int(255 * (R - d)))

BLUE = (0x4C, 0x8D, 0xFF); WHITE = (0xF2, 0xF4, 0xF8)
# mic capsule: rounded rect 26 wide x 46 tall centred at (56, 46)
def capsule(x, y, w, h, cxr, cyr):
    hx, hy = w / 2, h / 2 - w / 2
    ddx = max(abs(x + 0.5 - cxr) - 0, 0)
    ddy = max(abs(y + 0.5 - cyr) - hy, 0)
    return math.hypot(ddx, ddy) - hx   # <0 inside
for y in range(H):
    for x in range(W):
        d = capsule(x, y, 26, 48, 56, 44)
        if d < 1: blend(x, y, BLUE, 255 if d < 0 else int(255 * (1 - d)))
# holder arc (U shape) radius 22 around (56, 52), thickness 5, lower half only
for y in range(H):
    for x in range(W):
        d = math.hypot(x + 0.5 - 56, y + 0.5 - 52)
        if 19.5 <= d <= 24.5 and (y + 0.5) > 52:
            edge = min(d - 19.5, 24.5 - d)
            blend(x, y, WHITE, 255 if edge >= 1 else int(255 * edge))
# stem + base
for y in range(74, 84):
    for x in range(54, 59): blend(x, y, WHITE, 255)
for y in range(83, 88):
    for x in range(42, 71): blend(x, y, WHITE, 255)
# sound waves (two arcs on the right)
for y in range(H):
    for x in range(W):
        d = math.hypot(x + 0.5 - 56, y + 0.5 - 44)
        if (x + 0.5) > 56 + 24 and abs(y + 0.5 - 44) < d * 0.55:
            for r0 in (31, 38):
                if r0 - 1.5 <= d <= r0 + 1.5:
                    edge = 1.5 - abs(d - r0)
                    blend(x, y, (0x3D, 0xD6, 0x8C), 255 if edge >= 1 else int(255 * edge))

name = "esp_brookesia_app_icon_launcher_chat_112_112"
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
