# 文件：应用启动装配（bootstrap.py） | File: App bootstrap
import logging
import threading

from app.route_deps import RouteDeps
from services.arango_service import ArangoIndexService
from services.inference_service import InferenceService
from utils.vector_utils import normalize_feature

try:
    from arango import ArangoClient
except Exception:
    ArangoClient = None


def build_runtime(*, logger, collection_name: str, vector_dim: int):
    local_index = []
    index_lock = threading.Lock()
    arango = ArangoIndexService(arango_client_cls=ArangoClient, collection_name=collection_name)
    inference = InferenceService(normalize_feature_func=lambda f: normalize_feature(f, vector_dim), logger=logger)
    deps = RouteDeps(
        logger=logger,
        collection_name=collection_name,
        vector_dim=vector_dim,
        index_lock=index_lock,
        local_index=local_index,
        arango=arango,
        inference=inference,
    )
    return deps, arango, inference
