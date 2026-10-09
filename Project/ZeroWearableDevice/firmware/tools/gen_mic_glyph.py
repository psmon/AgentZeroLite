"""Generate the 48x48 ARGB8888 microphone glyph drawn on the Chat app's talk button (no PIL).

LVGL's built-in symbol font has no microphone - LV_SYMBOL_AUDIO is a music note, which reads as
"play something", not "hold to talk". This draws a plain white mic so the button says what it does.

  python tools/gen_mic_glyph.py components/brookesia_app_chat/assets/chat_icon_mic_48.c
"""
import math, sys

W = H = 48
px = [[(0, 0, 0, 0) for _ in range(W)] for _ in range(H)]


def blend(x, y, c, a):
    if a <= 0 or not (0 <= x < W and 0 <= y < H):
        return
    a = max(0, min(255, int(a)))
    r0, g0, b0, a0 = px[y][x]
    f = a / 255.0
    px[y][x] = (int(c[0] * f + r0 * (1 - f)), int(c[1] * f + g0 * (1 - f)),
                int(c[2] * f + b0 * (1 - f)), max(a0, a))


WHITE = (0xFF, 0xFF, 0xFF)
CX = 24.0

# capsule: the mic body. width 16, from y=6 to y=28, fully rounded ends.
BODY_HW, BODY_TOP, BODY_BOT = 8.0, 6.0, 28.0
for y in range(H):
    for x in range(W):
        fx, fy = x + 0.5, y + 0.5
        cy = min(max(fy, BODY_TOP + BODY_HW), BODY_BOT - BODY_HW)   # nearest point on the spine
        d = math.hypot(fx - CX, fy - cy) - BODY_HW
        if d < 1:
            blend(x, y, WHITE, 255 if d <= 0 else 255 * (1 - d))

# holder: open arc under the body, radius 14, thickness 3.5, lower half only
R_ARC, T_ARC, ARC_CY = 14.0, 1.75, 24.0
for y in range(H):
    for x in range(W):
        fx, fy = x + 0.5, y + 0.5
        if fy < ARC_CY:
            continue
        d = abs(math.hypot(fx - CX, fy - ARC_CY) - R_ARC) - T_ARC
        if d < 1:
            blend(x, y, WHITE, 255 if d <= 0 else 255 * (1 - d))

# stem + base
for y in range(H):
    for x in range(W):
        fx, fy = x + 0.5, y + 0.5
        d_stem = max(abs(fx - CX) - 1.75, 38.0 - fy, fy - 43.0)
        d_base = max(abs(fx - CX) - 9.0, 41.5 - fy, fy - 44.5)
        d = min(d_stem, d_base)
        if d < 1:
            blend(x, y, WHITE, 255 if d <= 0 else 255 * (1 - d))

name = "chat_icon_mic_48"
out = sys.argv[1] if len(sys.argv) > 1 else "chat_icon_mic_48.c"
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
