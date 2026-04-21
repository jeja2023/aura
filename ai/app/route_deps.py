# 文件：路由依赖聚合（route_deps.py） | File: Route dependency bundle
from dataclasses import dataclass

import numpy as np
from fastapi.responses import JSONResponse
from PIL import Image

from services.arango_service import ArangoIndexService
from services.index_runtime_service import IndexRuntimeService
from services.inference_service import InferenceService
from services.retrieval_guard_service import RetrievalGuardService
from utils.service_state import build_service_state
from utils.vector_utils import cosine, decode_image, normalize_feature, preprocess_image


@dataclass
class RouteDeps:
    logger: object
    collection_name: str
    vector_dim: int
    index_lock: object
    local_index: list
    arango: ArangoIndexService
    inference: InferenceService
    index_runtime: IndexRuntimeService
    retrieval_guard: RetrievalGuardService

    def mark_arango_failure(self, ex: Exception | str) -> None:
        self.arango.mark_failure(ex)

    def service_state(self, arango_enabled: bool | None = None) -> dict:
        enabled = (self.arango.db is not None) if arango_enabled is None else arango_enabled
        payload = build_service_state(
            arango_enabled=enabled,
            arango_error=self.arango.error,
            model_loaded=self.inference.model_loaded,
            model_error=self.inference.model_error,
        )
        guard_state = self.retrieval_guard.get_state()
        payload["retrieval_guard"] = guard_state
        payload["backfill_state"] = self.index_runtime.get_backfill_state()
        payload["熔断状态"] = guard_state.get("circuit_breaker", {})
        payload["限流状态"] = guard_state.get("rate_limiter", {})
        payload["回填状态"] = payload["backfill_state"]
        return payload

    def ensure_arango(self) -> bool:
        return self.arango.ensure()

    def normalize_feature(self, feature: list[float]) -> list[float]:
        return normalize_feature(feature, self.vector_dim)

    def decode_image(self, image_base64: str) -> Image.Image:
        return decode_image(image_base64)

    def preprocess(self, img: Image.Image) -> np.ndarray:
        return preprocess_image(img)

    async def extract_feature_batched(self, tensor) -> list[float]:
        return await self.inference.extract_feature_batched(tensor)

    def cosine(self, a: list[float], b: list[float]) -> float:
        return cosine(a, b)

    def strict_arango_unavailable(self, message: str, *, data: dict | list | None = None) -> JSONResponse:
        self.logger.critical("%s; arango_error=%s", message, self.arango.error or "unknown")
        payload = {"code": 50301, "msg": message}
        if data is not None:
            payload["data"] = data
        return JSONResponse(status_code=503, content=payload)

    def search_metrics(self) -> dict:
        return self.index_runtime.get_search_metrics()

    def search_audit_logs(self, *, limit: int = 100) -> dict:
        return self.index_runtime.get_search_audit_logs(limit=limit)
