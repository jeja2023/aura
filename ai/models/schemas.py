# 文件：请求模型定义（schemas.py） | File: Request schemas
from pydantic import BaseModel


class ImageReq(BaseModel):
    image_base64: str
    metadata_json: str = "{}"


class ImageFileReq(BaseModel):
    image_path: str
    metadata_json: str = "{}"


class SearchReq(BaseModel):
    feature: list[float]
    top_k: int = 10


class UpsertReq(BaseModel):
    vid: str
    feature: list[float]


class ClusterReq(BaseModel):
    similarity_threshold: float = 0.82
    min_points: int = 2
    max_vectors: int = 500
