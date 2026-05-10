"""ComfyUI Z-Image-Turbo image generation provider (local, no API key)."""

import json
import pathlib
import random
import time
import urllib.request
import urllib.error


COMFYUI_BASE = "http://192.168.0.64:8188"

WORKFLOW_TEMPLATE = {
    "1": {
        "class_type": "UNETLoader",
        "inputs": {
            "unet_name": r"split_files\diffusion_models\z_image_turbo_bf16.safetensors",
            "weight_dtype": "default"
        }
    },
    "2": {
        "class_type": "CLIPLoader",
        "inputs": {
            "clip_name": r"split_files\text_encoders\qwen_3_4b.safetensors",
            "type": "qwen_image"
        }
    },
    "3": {
        "class_type": "VAELoader",
        "inputs": {
            "vae_name": r"split_files\vae\ae.safetensors"
        }
    },
    "4": {
        "class_type": "CLIPTextEncode",
        "inputs": {
            "text": "",
            "clip": ["2", 0]
        }
    },
    "5": {
        "class_type": "EmptyLatentImage",
        "inputs": {
            "width": 1024,
            "height": 1024,
            "batch_size": 1
        }
    },
    "6": {
        "class_type": "KSampler",
        "inputs": {
            "model": ["1", 0],
            "positive": ["4", 0],
            "negative": ["7", 0],
            "latent_image": ["5", 0],
            "seed": 42,
            "steps": 4,
            "cfg": 1.0,
            "sampler_name": "euler",
            "scheduler": "normal",
            "denoise": 1.0
        }
    },
    "7": {
        "class_type": "CLIPTextEncode",
        "inputs": {
            "text": "",
            "clip": ["2", 0]
        }
    },
    "8": {
        "class_type": "VAEDecode",
        "inputs": {
            "samples": ["6", 0],
            "vae": ["3", 0]
        }
    },
    "9": {
        "class_type": "SaveImage",
        "inputs": {
            "images": ["8", 0],
            "filename_prefix": "api_output"
        }
    }
}

# Aspect ratio to pixel size mapping (max 1024 on longest side)
ASPECT_SIZES = {
    "1:1": (1024, 1024),
    "16:9": (1024, 576),
    "9:16": (576, 1024),
    "4:3": (1024, 768),
    "3:4": (768, 1024),
}


class ComfyUIProvider:
    def __init__(self):
        self.base_url = COMFYUI_BASE

    def _check_server(self):
        """Check if ComfyUI server is reachable."""
        try:
            req = urllib.request.Request(f"{self.base_url}/system_stats")
            with urllib.request.urlopen(req, timeout=5) as resp:
                return resp.status == 200
        except (urllib.error.URLError, OSError):
            raise RuntimeError(
                f"ComfyUI server not reachable at {self.base_url}. "
                "Start it with: cd C:\\Users\\psmon\\ComfyUI && .\\venv\\Scripts\\Activate.ps1 && python main.py --listen 0.0.0.0 --port 8188"
            )

    def _submit_prompt(self, prompt_text: str, width: int, height: int, seed: int) -> str:
        """Submit workflow to ComfyUI and return prompt_id."""
        import copy
        workflow = copy.deepcopy(WORKFLOW_TEMPLATE)
        workflow["4"]["inputs"]["text"] = prompt_text
        workflow["5"]["inputs"]["width"] = width
        workflow["5"]["inputs"]["height"] = height
        workflow["6"]["inputs"]["seed"] = seed

        payload = json.dumps({
            "prompt": workflow,
            "client_id": f"image-gen-{seed}"
        }).encode("utf-8")

        req = urllib.request.Request(
            f"{self.base_url}/prompt",
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST"
        )
        with urllib.request.urlopen(req, timeout=30) as resp:
            result = json.loads(resp.read().decode("utf-8"))
            return result["prompt_id"]

    def _wait_for_result(self, prompt_id: str, timeout: int = 120) -> str:
        """Poll history until image is ready, return filename."""
        start = time.time()
        while time.time() - start < timeout:
            try:
                req = urllib.request.Request(f"{self.base_url}/history/{prompt_id}")
                with urllib.request.urlopen(req, timeout=10) as resp:
                    history = json.loads(resp.read().decode("utf-8"))
                    if prompt_id in history:
                        outputs = history[prompt_id].get("outputs", {})
                        for node_id, node_output in outputs.items():
                            images = node_output.get("images", [])
                            if images:
                                return images[0]["filename"]
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(1)
        raise RuntimeError(f"Timeout waiting for ComfyUI result (>{timeout}s)")

    def _download_image(self, filename: str, output_path: pathlib.Path):
        """Download generated image from ComfyUI."""
        url = f"{self.base_url}/view?filename={filename}&type=output"
        req = urllib.request.Request(url)
        with urllib.request.urlopen(req, timeout=30) as resp:
            output_path.write_bytes(resp.read())

    def generate(self, prompt: str, output_path: pathlib.Path,
                 aspect_ratio: str = "1:1", **kwargs) -> pathlib.Path:
        """Generate an image using ComfyUI Z-Image-Turbo."""
        self._check_server()

        width, height = ASPECT_SIZES.get(aspect_ratio, (1024, 1024))
        seed = random.randint(1, 2**31)

        prompt_id = self._submit_prompt(prompt, width, height, seed)
        filename = self._wait_for_result(prompt_id)
        self._download_image(filename, output_path)

        return output_path

    def edit(self, prompt: str, input_image_path: pathlib.Path,
             output_path: pathlib.Path) -> pathlib.Path:
        """Edit not supported by ComfyUI Z-Image-Turbo (text-to-image only)."""
        raise NotImplementedError(
            "ComfyUI Z-Image-Turbo is text-to-image only. "
            "Use 'gemini' provider for image editing."
        )
