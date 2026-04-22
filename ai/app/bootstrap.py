# 文件：应用启动装配（bootstrap.py） | File: App bootstrap
import logging
import os
import threading

from app.route_deps import RouteDeps
from services.arango_service import ArangoIndexService
from services.index_runtime_service import IndexRuntimeService
from services.inference_service import InferenceService
from services.retrieval_guard_service import RetrievalGuardService
from utils.vector_utils import normalize_feature

try:
    from arango import ArangoClient
except Exception:
    ArangoClient = None


def _env_int(name: str, default: int, *, logger) -> int:
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        return int(raw)
    except ValueError:
        logger.warning("环境变量 %s 非法值=%s，已回退默认值=%s", name, raw, default)
        return default


def _env_float(name: str, default: float, *, logger) -> float:
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        return float(raw)
    except ValueError:
        logger.warning("环境变量 %s 非法值=%s，已回退默认值=%s", name, raw, default)
        return default


def build_runtime(*, logger, collection_name: str, vector_dim: int):
    local_index = []
    index_lock = threading.Lock()
    arango = ArangoIndexService(arango_client_cls=ArangoClient, collection_name=collection_name)
    snapshot_path = os.getenv("AURA_AI_INDEX_SNAPSHOT_PATH", "data/ai/local_index_snapshot.json")
    index_runtime = IndexRuntimeService(snapshot_path=snapshot_path)
    retrieval_guard = RetrievalGuardService(
        rate_limit_per_minute=_env_int("AURA_AI_SEARCH_RATE_LIMIT_PER_MINUTE", 600, logger=logger),
        breaker_fail_threshold=_env_int("AURA_AI_BREAKER_FAIL_THRESHOLD", 8, logger=logger),
        breaker_open_seconds=_env_int("AURA_AI_BREAKER_OPEN_SECONDS", 20, logger=logger),
    )
    inference = InferenceService(
        normalize_feature_func=lambda f: normalize_feature(f, vector_dim),
        logger=logger,
        batch_size=_env_int("AURA_AI_INFER_BATCH_SIZE", 16, logger=logger),
        max_wait_seconds=_env_float("AURA_AI_INFER_MAX_WAIT_SECONDS", 0.05, logger=logger),
        max_queue_size=_env_int("AURA_AI_INFER_QUEUE_MAX_SIZE", 256, logger=logger),
        enqueue_timeout_seconds=_env_float("AURA_AI_INFER_ENQUEUE_TIMEOUT_SECONDS", 0.2, logger=logger),
    )
    deps = RouteDeps(
        logger=logger,
        collection_name=collection_name,
        vector_dim=vector_dim,
        index_lock=index_lock,
        local_index=local_index,
        arango=arango,
        inference=inference,
        index_runtime=index_runtime,
        retrieval_guard=retrieval_guard,
    )
    return deps, arango, inference, index_runtime
