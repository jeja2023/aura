# 文件：请求模型定义（schemas.py） | File: Request schemas
from pydantic import BaseModel, Field


class ImageReq(BaseModel):
    image_base64: str = Field(min_length=1, max_length=20_000_000)
    metadata_json: str = "{}"


class ImageFileReq(BaseModel):
    image_path: str = Field(min_length=1, max_length=1024)
    metadata_json: str = "{}"


class SearchReq(BaseModel):
    feature: list[float] = Field(min_length=1, max_length=4096)
    top_k: int = Field(default=10, ge=1, le=1000)
    min_score: float = Field(default=-1.0, ge=-1.0, le=1.0)
    candidate_multiplier: int = Field(default=8, ge=1, le=64)
    candidate_pool: int = Field(default=0, ge=0, le=10000)
    ann_probe: int = Field(default=16, ge=1, le=128)
    rerank_window: int = Field(default=30, ge=1, le=10000)
    include_vids: list[str] | None = None
    exclude_vids: list[str] | None = None
    metadata_filter: dict[str, str | int | float | bool] | None = None
    explain: bool = False


class UpsertReq(BaseModel):
    vid: str = Field(min_length=1, max_length=256)
    feature: list[float] = Field(min_length=1, max_length=4096)
    metadata: dict[str, str | int | float | bool] | None = None


class ClusterReq(BaseModel):
    similarity_threshold: float = Field(default=0.82, ge=0.5, le=0.99)
    min_points: int = Field(default=2, ge=1, le=1000)
    max_vectors: int = Field(default=500, ge=1, le=50000)
