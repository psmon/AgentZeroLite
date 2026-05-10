"""Gemini image generation provider."""

import json
import os
import pathlib
from google import genai
from google.genai import types

SCRIPT_PATH = pathlib.Path(__file__).resolve()
REPO_ROOT = SCRIPT_PATH.parents[5]
SECRET_PATH = pathlib.Path(
    os.environ.get("GEMINI_SECRET_PATH") or str(REPO_ROOT / ".secret/gemini.json")
).expanduser()


def _load_gemini_config() -> dict:
    cfg = json.loads(SECRET_PATH.read_text(encoding="utf-8"))
    if not cfg.get("api_key"):
        raise RuntimeError(f"api_key not found in {SECRET_PATH}")
    return cfg


class GeminiProvider:
    def __init__(self):
        cfg = _load_gemini_config()
        self.client = genai.Client(api_key=cfg["api_key"])
        self.model = cfg.get("image_model", "gemini-3.1-flash-image-preview")

    def generate(self, prompt: str, output_path: pathlib.Path,
                 aspect_ratio: str = "16:9", size: str = "2K") -> pathlib.Path:
        """Generate an image from a text prompt."""
        response = self.client.models.generate_content(
            model=self.model,
            contents=prompt,
            config=types.GenerateContentConfig(
                response_modalities=["TEXT", "IMAGE"],
                image_config=types.ImageConfig(
                    aspect_ratio=aspect_ratio,
                    image_size=size,
                ),
            ),
        )
        for part in response.candidates[0].content.parts:
            if hasattr(part, "inline_data") and part.inline_data:
                image = part.inline_data
                output_path.write_bytes(image.data)
                return output_path
            if hasattr(part, "as_image"):
                img = part.as_image()
                img.save(str(output_path))
                return output_path
        raise RuntimeError("No image returned from Gemini API")

    def edit(self, prompt: str, input_image_path: pathlib.Path,
             output_path: pathlib.Path) -> pathlib.Path:
        """Edit an existing image with a text prompt."""
        from PIL import Image
        img = Image.open(input_image_path)
        response = self.client.models.generate_content(
            model=self.model,
            contents=[prompt, img],
            config=types.GenerateContentConfig(
                response_modalities=["TEXT", "IMAGE"],
            ),
        )
        for part in response.candidates[0].content.parts:
            if hasattr(part, "inline_data") and part.inline_data:
                output_path.write_bytes(part.inline_data.data)
                return output_path
            if hasattr(part, "as_image"):
                result = part.as_image()
                result.save(str(output_path))
                return output_path
        raise RuntimeError("No image returned from Gemini API")
