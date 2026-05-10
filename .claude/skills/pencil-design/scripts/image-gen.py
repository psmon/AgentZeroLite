#!/usr/bin/env python3
"""
image-gen CLI — 이미지 생성/편집 도구

사용법:
  python image-gen.py generate --prompt "설명" --topic 주제명 [--provider gemini] [--aspect-ratio 16:9]
  python image-gen.py edit --prompt "수정 설명" --input-image 경로 --topic 주제명 [--provider gemini]

출력: JSON {"status": "ok", "path": "...", "provider": "..."} 또는 {"status": "error", "message": "..."}
"""

import argparse
import json
import os
import pathlib
import sys
from datetime import date

SCRIPT_PATH = pathlib.Path(__file__).resolve()
REPO_ROOT = SCRIPT_PATH.parents[4]
IMAGE_ROOT = pathlib.Path(os.environ.get("IMAGE_GEN_ROOT", str(REPO_ROOT / "image"))).expanduser()


def main():
    parser = argparse.ArgumentParser(description="Image generation CLI")
    sub = parser.add_subparsers(dest="command", required=True)

    # generate
    gen = sub.add_parser("generate", help="Generate image from text prompt")
    gen.add_argument("--prompt", required=True, help="Image generation prompt")
    gen.add_argument("--topic", required=True, help="Topic keyword for filename")
    gen.add_argument("--provider", default="gemini", help="Provider name (default: gemini)")
    gen.add_argument("--aspect-ratio", default="16:9", help="Aspect ratio (default: 16:9)")
    gen.add_argument("--size", default="2K", help="Image size (default: 2K)")

    # edit
    ed = sub.add_parser("edit", help="Edit existing image with prompt")
    ed.add_argument("--prompt", required=True, help="Edit instruction prompt")
    ed.add_argument("--input-image", required=True, help="Path to source image")
    ed.add_argument("--topic", required=True, help="Topic keyword for filename")
    ed.add_argument("--provider", default="gemini", help="Provider name (default: gemini)")

    args = parser.parse_args()

    try:
        # Add parent dir to path so providers package is importable
        sys.path.insert(0, str(pathlib.Path(__file__).parent))
        from providers import get_provider

        provider = get_provider(args.provider)

        # Build output path: image/{provider}/{date}-{topic}.png
        out_dir = IMAGE_ROOT / args.provider
        out_dir.mkdir(parents=True, exist_ok=True)
        filename = f"{date.today().isoformat()}-{args.topic}.png"
        output_path = out_dir / filename

        if args.command == "generate":
            result = provider.generate(
                prompt=args.prompt,
                output_path=output_path,
                aspect_ratio=args.aspect_ratio,
                size=getattr(args, "size", "2K"),
            )
        elif args.command == "edit":
            input_img = pathlib.Path(args.input_image)
            if not input_img.exists():
                raise FileNotFoundError(f"Input image not found: {input_img}")
            result = provider.edit(
                prompt=args.prompt,
                input_image_path=input_img,
                output_path=output_path,
            )

        try:
            display_path = result.resolve().relative_to(REPO_ROOT.resolve())
        except ValueError:
            display_path = result.resolve()

        print(json.dumps({
            "status": "ok",
            "path": str(display_path).replace("\\", "/"),
            "provider": args.provider,
        }, ensure_ascii=False))

    except Exception as e:
        print(json.dumps({
            "status": "error",
            "message": str(e),
        }, ensure_ascii=False))
        sys.exit(1)


if __name__ == "__main__":
    main()
