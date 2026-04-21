# 文件：向量与图像工具（vector_utils.py） | File: Vector and image helpers
import base64
import io
import math

import numpy as np
from PIL import Image


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


def decode_image(image_base64: str) -> Image.Image:
    raw = image_base64.split(",", 1)[-1]
    data = base64.b64decode(raw)
    return Image.open(io.BytesIO(data)).convert("RGB")


def preprocess_image(img: Image.Image) -> np.ndarray:
    img = img.resize((128, 256), Image.BILINEAR)
    arr = np.asarray(img).astype(np.float32) / 255.0
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    arr = (arr - mean) / std
    arr = np.transpose(arr, (2, 0, 1))
    arr = np.expand_dims(arr, axis=0).astype(np.float32)
    return arr
