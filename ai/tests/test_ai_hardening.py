import os
from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient

import routes.api_routes as api_routes_module
from app.bootstrap import _env_float, _env_int
from routes.api_routes import build_api_router
from utils.service_state import build_service_state


class _FakeGuard:
    def allow_request(self):
        return True, ""

    def record_result(self, *, success: bool):
        return None


class _FakeIndexRuntime:
    def __init__(self):
        self.records = []

    def record_search(self, **kwargs):
        self.records.append(kwargs)


class _SearchDeps:
    def __init__(self):
        self.logger = SimpleNamespace(
            warning=lambda *args, **kwargs: None,
            info=lambda *args, **kwargs: None,
            exception=lambda *args, **kwargs: None,
        )
        self.retrieval_guard = _FakeGuard()
        self.index_runtime = _FakeIndexRuntime()
        self.arango = SimpleNamespace(db=None, error="")
        self.collection_name = "aura_reid"
        self.vector_dim = 512
        self.index_lock = None
        self.local_index = []

    def ensure_arango(self):
        return True

    def mark_arango_failure(self, ex):
        return None

    def strict_arango_unavailable(self, message: str, *, data=None):
        raise RuntimeError(message)

    def normalize_feature(self, feature):
        return feature

    def cosine(self, left, right):
        return 1.0

    def search_metrics(self):
        return {}

    def search_audit_logs(self, *, limit: int = 100):
        return {"total_cached": 0, "returned": 0, "items": []}


def test_env_parser_invalid_value_fallback():
    logger = SimpleNamespace(warning=lambda *args, **kwargs: None)
    os.environ["AURA_TEST_INT"] = "not-int"
    os.environ["AURA_TEST_FLOAT"] = "not-float"
    try:
        assert _env_int("AURA_TEST_INT", 7, logger=logger) == 7
        assert _env_float("AURA_TEST_FLOAT", 0.5, logger=logger) == 0.5
    finally:
        os.environ.pop("AURA_TEST_INT", None)
        os.environ.pop("AURA_TEST_FLOAT", None)


def test_service_state_masks_errors_in_production():
    os.environ["ASPNETCORE_ENVIRONMENT"] = "Production"
    os.environ["AURA_AI_HEALTH_VERBOSE"] = "false"
    try:
        payload = build_service_state(
            arango_enabled=False,
            arango_error="db password invalid",
            model_loaded=False,
            model_error="model missing at x:/secret/path",
        )
    finally:
        os.environ.pop("ASPNETCORE_ENVIRONMENT", None)
        os.environ.pop("AURA_AI_HEALTH_VERBOSE", None)
    assert payload["diagnostics_visible"] is False
    assert payload["arango_error"] == ""
    assert payload["model_error"] == ""


def test_search_unexpected_exception_returns_500_and_records_failed():
    deps = _SearchDeps()
    app = FastAPI()
    app.include_router(build_api_router(deps))
    client = TestClient(app)

    original = api_routes_module.search_vectors
    api_routes_module.search_vectors = lambda **kwargs: (_ for _ in ()).throw(RuntimeError("boom"))
    try:
        response = client.post("/ai/search", json={"feature": [0.1] * 512, "top_k": 5})
    finally:
        api_routes_module.search_vectors = original

    assert response.status_code == 500
    body = response.json()
    assert body["code"] == 50002
    assert deps.index_runtime.records
    assert deps.index_runtime.records[-1]["status"] == "failed"
    assert deps.index_runtime.records[-1]["reason"] == "检索内部异常"

