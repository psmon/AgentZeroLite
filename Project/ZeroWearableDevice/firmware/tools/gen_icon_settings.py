"""Generate the 112x112 ARGB8888 LVGL9 launcher icon for the Settings app: a gear (no PIL).

  python tools/gen_icon_settings.py components/brookesia_app_settings/assets/esp_brookesia_app_icon_launcher_settings_112_112.c
"""
import math, sys

W = H = 112
px = [[(0, 0, 0, 0) for _ in range(W)] for _ in range(H)]


def blend(x, y, c, a):
    if a <= 0 or not (0 <= x < W and 0 <= y < H):
        return
    a = max(0, min(255, int(a)))
    r0, g0, b0, a0 = px[y][x]
    f = a / 255.0
    px[y][x] = (int(c[0] * f + r0 * (1 - f)), int(c[1] * f + g0 * (1 - f)),
                int(c[2] * f + b0 * (1 - f)), max(a0, a))


CX = CY = W / 2.0
BG = (0x1E, 0x22, 0x30)
GEAR = (0x8A, 0x8F, 0x98)
HOLE = (0x1E, 0x22, 0x30)
DOT = (0x4C, 0x8D, 0xFF)

# rounded-square background, matching the other launcher icons
R = 26
for y in range(H):
    for x in range(W):
        dx = max(abs(x + 0.5 - CX) - (W / 2 - R), 0)
        dy = max(abs(y + 0.5 - CY) - (H / 2 - R), 0)
        d = math.hypot(dx, dy)
        if d <= R:
            blend(x, y, BG, 255 if d < R - 1 else 255 * (R - d))

# gear: a ring whose outer radius steps between two values eight times around
TEETH = 8
R_BASE, R_TIP, R_IN = 30.0, 38.0, 19.0
for y in range(H):
    for x in range(W):
        fx, fy = x + 0.5 - CX, y + 0.5 - CY
        d = math.hypot(fx, fy)
        if d > R_TIP + 1 or d < R_IN - 1:
            continue
        ang = math.atan2(fy, fx)
        # tooth profile: smooth-ish square wave, flat top over ~55% of each period
        phase = (ang * TEETH / (2 * math.pi)) % 1.0
        tooth = 1.0 if 0.225 < phase < 0.775 else 0.0
        r_out = R_BASE + (R_TIP - R_BASE) * tooth
        edge = min(r_out - d, d - R_IN)
        if edge > -1:
            blend(x, y, GEAR, 255 if edge >= 1 else 255 * (edge + 1) / 2)

# hub hole, then a blue dot in the middle so the icon is not pure grey
for y in range(H):
    for x in range(W):
        d = math.hypot(x + 0.5 - CX, y + 0.5 - CY)
        if d <= R_IN:
            blend(x, y, HOLE, 255 if d < R_IN - 1 else 255 * (R_IN - d))
        if d <= 9:
            blend(x, y, DOT, 255 if d < 8 else 255 * (9 - d))

name = "esp_brookesia_app_icon_launcher_settings_112_112"
out = sys.argv[1] if len(sys.argv) > 1 else name + ".c"
with open(out, "w", newline="\n") as f:
    f.write('#ifdef __has_include\n#if __has_include("lvgl.h")\n#ifndef LV_LVGL_H_INCLUDE_SIMPLE\n'
            '#define LV_LVGL_H_INCLUDE_SIMPLE\n#endif\n#endif\n#endif\n\n')
    f.write('#if defined(LV_LVGL_H_INCLUDE_SIMPLE)\n#include "lvgl.h"\n#else\n#include "lvgl/lvgl.h"\n#endif\n\n')
    f.write('#ifndef LV_ATTRIBUTE_MEM_ALIGN\n#define LV_ATTRIBUTE_MEM_ALIGN\n#endif\n\n')
    f.write(f'const LV_ATTRIBUTE_MEM_ALIGN LV_ATTRIBUTE_LARGE_CONST uint8_t {name}_map[] = {{\n')
    for y in range(H):
        row = []
        for x in range(W):
            r, g, b, a = px[y][x]
            row += [b, g, r, a]          # ARGB8888 in memory = B,G,R,A (little endian)
        f.write("    " + ",".join(f"0x{v:02x}" for v in row) + ",\n")
    f.write("};\n\n")
    f.write(f'const lv_image_dsc_t {name} = {{\n')
    f.write('    .header.cf = LV_COLOR_FORMAT_ARGB8888,\n    .header.magic = LV_IMAGE_HEADER_MAGIC,\n')
    f.write(f'    .header.w = {W},\n    .header.h = {H},\n    .header.stride = {W * 4},\n')
    f.write(f'    .data_size = {W * H} * 4,\n    .data = {name}_map,\n}};\n')
print("wrote", out)
