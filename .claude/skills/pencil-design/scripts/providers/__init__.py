"""Provider registry for image generation."""


def get_provider(name: str):
    if name == "gemini":
        from .gemini_provider import GeminiProvider
        return GeminiProvider()
    if name in ("comfyui", "z-image", "z-image-turbo"):
        from .comfyui_provider import ComfyUIProvider
        return ComfyUIProvider()
    raise ValueError(f"Unknown provider: {name}. Available: gemini, comfyui (z-image)")