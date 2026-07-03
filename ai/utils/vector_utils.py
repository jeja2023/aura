# File: Vector and image helpers
import base64
import binascii
import io
import math
import os
from pathlib import Path

import numpy as np
from PIL import Image, UnidentifiedImageError

DEFAULT_MAX_IMAGE_BASE64_CHARS = 20_000_000
DEFAULT_MAX_IMAGE_PIXELS = 16_000_000
DEFAULT_ALLOWED_IMAGE_FORMATS = frozenset({"JPEG", "PNG", "BMP", "WEBP"})


class ImageValidationError(ValueError):
    pass


class ImageTooLargeError(ImageValidationError):
    pass


def _read_int_env(name: str, default: int, *, min_value: int = 1, max_value: int | None = None) -> int:
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
    except ValueError:
        return default
    value = max(min_value, value)
    if max_value is not None:
        value = min(value, max_value)
    return value


def max_image_base64_chars() -> int:
    return _read_int_env(
        "AURA_AI_MAX_IMAGE_BASE64_CHARS",
        DEFAULT_MAX_IMAGE_BASE64_CHARS,
        min_value=1024,
        max_value=100_000_000,
    )


def max_image_pixels() -> int:
    return _read_int_env(
        "AURA_AI_MAX_IMAGE_PIXELS",
        DEFAULT_MAX_IMAGE_PIXELS,
        min_value=1,
        max_value=100_000_000,
    )


def allowed_image_formats() -> set[str]:
    raw = os.getenv("AURA_AI_ALLOWED_IMAGE_FORMATS", "").strip()
    if not raw:
        return set(DEFAULT_ALLOWED_IMAGE_FORMATS)
    values = {item.strip().upper() for item in raw.replace(";", ",").split(",") if item.strip()}
    return values or set(DEFAULT_ALLOWED_IMAGE_FORMATS)


def validate_image_base64_length(image_base64: str) -> str:
    value = str(image_base64 or "").strip()
    if not value:
        raise ImageValidationError("image_base64 is required")
    limit = max_image_base64_chars()
    if len(value) > limit:
        raise ImageTooLargeError(f"image_base64 exceeds limit: {limit} chars")
    return value


def _strip_data_url(image_base64: str) -> str:
    value = validate_image_base64_length(image_base64)
    raw = value.split(",", 1)[-1].strip()
    if not raw:
        raise ImageValidationError("image_base64 payload is empty")
    return raw


def _validate_open_image(img: Image.Image) -> None:
    fmt = str(img.format or "").upper()
    allowed = allowed_image_formats()
    if fmt not in allowed:
        raise ImageValidationError(f"unsupported image format: {fmt or 'unknown'}")

    width, height = img.size
    if width <= 0 or height <= 0:
        raise ImageValidationError("image dimensions are invalid")

    limit = max_image_pixels()
    pixels = width * height
    if pixels > limit:
        raise ImageTooLargeError(f"image pixel count exceeds limit: {pixels}>{limit}")


def _configure_pillow_limits() -> None:
    Image.MAX_IMAGE_PIXELS = max_image_pixels()


def decode_image(image_base64: str) -> Image.Image:
    raw = _strip_data_url(image_base64)
    try:
        data = base64.b64decode(raw, validate=True)
    except (binascii.Error, ValueError) as ex:
        raise ImageValidationError("image_base64 is not valid base64") from ex

    buffer = io.BytesIO(data)
    try:
        _configure_pillow_limits()
        with Image.open(buffer) as probe:
            _validate_open_image(probe)
            probe.verify()

        buffer.seek(0)
        _configure_pillow_limits()
        with Image.open(buffer) as img:
            _validate_open_image(img)
            return img.convert("RGB")
    except ImageTooLargeError:
        raise
    except ImageValidationError:
        raise
    except (Image.DecompressionBombError, UnidentifiedImageError, OSError, ValueError) as ex:
        raise ImageValidationError("image_base64 does not contain a valid image") from ex


def decode_image_file(image_path: str | Path) -> Image.Image:
    path = Path(image_path)
    try:
        _configure_pillow_limits()
        with Image.open(path) as probe:
            _validate_open_image(probe)
            probe.verify()

        _configure_pillow_limits()
        with Image.open(path) as img:
            _validate_open_image(img)
            return img.convert("RGB")
    except ImageTooLargeError:
        raise
    except ImageValidationError:
        raise
    except (Image.DecompressionBombError, UnidentifiedImageError, OSError, ValueError) as ex:
        raise ImageValidationError("image_path does not point to a valid image") from ex


def normalize_feature(feature: list[float], vector_dim: int) -> list[float]:
    if not feature:
        return [0.0] * vector_dim

    if len(feature) >= vector_dim:
        data = feature[:vector_dim]
    else:
        data = feature + [0.0] * (vector_dim - len(feature))

    norm = math.sqrt(sum(x * x for x in data))
    if norm == 0:
        return data

    return [x / norm for x in data]


def cosine(a: list[float], b: list[float]) -> float:
    return sum(x * y for x, y in zip(a, b))


def preprocess_image(img: Image.Image) -> np.ndarray:
    img = img.resize((128, 256), Image.BILINEAR)
    arr = np.asarray(img).astype(np.float32) / 255.0
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    arr = (arr - mean) / std
    arr = np.transpose(arr, (2, 0, 1))
    arr = np.expand_dims(arr, axis=0).astype(np.float32)
    return arr
