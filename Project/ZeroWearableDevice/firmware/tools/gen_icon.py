"""Generate the 112x112 ARGB8888 LVGL9 launcher icon for the Claude HUD app (no PIL needed)."""
import math, sys
W = H = 112
px = [[(0, 0, 0, 0) for _ in range(W)] for _ in range(H)]

def put(x, y, c, a=255):
    if 0 <= x < W and 0 <= y < H:
        r, g, b = c
        px[y][x] = (r, g, b, a)

cx, cy = W / 2, H / 2
# rounded-square background (#1E2230) with radius 26
R = 26
for y in range(H):
    for x in range(W):
        dx = max(abs(x + 0.5 - cx) - (W / 2 - R), 0)
        dy = max(abs(y + 0.5 - cy) - (H / 2 - R), 0)
        d = math.hypot(dx, dy)
        if d <= R:
            a = 255 if d < R - 1 else int(255 * (R - d))
            put(x, y, (0x1E, 0x22, 0x30), a)
# progress ring: gray track + green arc (from -90deg, 270deg sweep) + amber end dot
r_out, r_in = 40, 30
for y in range(H):
    for x in range(W):
        d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
        if r_in <= d <= r_out:
            ang = (math.degrees(math.atan2(y + 0.5 - cy, x + 0.5 - cx)) + 90) % 360
            edge = min(d - r_in, r_out - d)
            a = 255 if edge >= 1 else int(255 * edge)
            col = (0x3D, 0xD6, 0x8C) if ang <= 270 else (0x3A, 0x40, 0x52)
            put(x, y, col, a)
# center "C"-like dot: small amber disc
for y in range(H):
    for x in range(W):
        d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
        if d <= 12:
            a = 255 if d < 11 else int(255 * (12 - d))
            put(x, y, (0xF5, 0xA5, 0x24), a)

name = "esp_brookesia_app_icon_launcher_claude_hud_112_112"
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
