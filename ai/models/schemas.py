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
    min_score: float = -1.0
    candidate_multiplier: int = 8
    candidate_pool: int = 0
    ann_probe: int = 16
    rerank_window: int = 30
    include_vids: list[str] | None = None
    exclude_vids: list[str] | None = None
    metadata_filter: dict[str, str | int | float | bool] | None = None
    explain: bool = False


class UpsertReq(BaseModel):
    vid: str
    feature: list[float]
    metadata: dict[str, str | int | float | bool] | None = None


class ClusterReq(BaseModel):
    similarity_threshold: float = 0.82
    min_points: int = 2
    max_vectors: int = 500
