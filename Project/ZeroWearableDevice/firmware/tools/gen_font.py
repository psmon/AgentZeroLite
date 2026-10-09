"""Generate the Korean+Latin LVGL font used by the Claude HUD app.

  font: NanumGothic-Regular.ttf (OFL) - download once:
        curl -L -o tools/NanumGothic-Regular.ttf https://github.com/google/fonts/raw/main/ofl/nanumgothic/NanumGothic-Regular.ttf
  glyphs: ASCII + Latin-1 punctuation + the 2350 KS X 1001 Hangul syllables (covers everyday Korean)
  usage:  python tools/gen_font.py            -> components/brookesia_app_claude_hud/assets/font_nanum_18.c
Requires node (npx lv_font_conv).
"""
import os, subprocess, sys
here = os.path.dirname(os.path.abspath(__file__))
ttf = os.path.join(here, "NanumGothic-Regular.ttf")
out = os.path.join(here, "..", "components", "brookesia_app_claude_hud", "assets", "font_nanum_18.c")
size = int(sys.argv[1]) if len(sys.argv) > 1 else 18

# KS X 1001 Hangul syllables = EUC-KR rows 0xB0..0xC8, cols 0xA1..0xFE
syl = []
for hi in range(0xB0, 0xC9):
    for lo in range(0xA1, 0xFF):
        try:
            syl.append(bytes([hi, lo]).decode("cp949"))
        except UnicodeDecodeError:
            pass
symbols = "".join(syl) + "·…‘’“”→←↑↓°×※─│■□●○★☆♪"
print(f"{len(syl)} syllables + extras; size {size}px")
# --no-compress is REQUIRED, not an optimisation: LVGL's compressed-glyph decoder keeps its RLE
# reader in one global (LV_GLOBAL_DEFAULT()->font_fmt_rle), and this project renders with
# CONFIG_LV_DRAW_SW_DRAW_UNIT_CNT=2. Two draw units decompressing at once clobber that state and
# most glyphs come out as garbage on screen. Uncompressed bitmaps have no shared state.
cmd = ["npx", "--yes", "lv_font_conv@1.5.3", "--font", ttf, "-r", "0x20-0x7E", "-r", "0xA0-0xFF",
       "--symbols", symbols, "--size", str(size), "--bpp", "2", "--format", "lvgl", "--no-compress",
       "--lv-include", "lvgl.h", "--lv-font-name", f"font_nanum_{size}", "--force-fast-kern-format", "-o", out]
subprocess.check_call(cmd, shell=(os.name == "nt"))
print("wrote", os.path.abspath(out), os.path.getsize(out) // 1024, "KB")
