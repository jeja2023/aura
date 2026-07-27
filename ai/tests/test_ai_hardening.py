import os
from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient

import routes.api_routes as api_routes_module
from app.bootstrap import _env_float, _env_int
from app.middlewares import register_middlewares
from app.security import validate_production_security
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
    assert deps.index_runtime.records[-1]["reason"] == "internal_exception"


def test_search_success_returns_resolved_params_and_records_warnings():
    deps = _SearchDeps()
    app = FastAPI()
    app.include_router(build_api_router(deps))
    client = TestClient(app)

    original = api_routes_module.search_vectors

    def _fake_search_vectors(**kwargs):
        assert kwargs["candidate_multiplier"] == 30
        assert kwargs["candidate_pool"] == 5000
        assert kwargs["ann_probe"] == 64
        assert kwargs["rerank_window"] == 200
        return {
            "code": 0,
            "msg": "检索成功",
            "data": [],
            "meta": {"engine": "memory", "strategy": "stub", "filters_applied": False},
        }

    api_routes_module.search_vectors = _fake_search_vectors
    try:
        response = client.post(
            "/ai/search",
            json={
                "feature": [0.1] * 512,
                "top_k": 999,
                "candidate_multiplier": 64,
                "candidate_pool": 10000,
                "ann_probe": 128,
                "rerank_window": 10000,
            },
        )
    finally:
        api_routes_module.search_vectors = original

    assert response.status_code == 200
    body = response.json()
    assert body["code"] == 0
    assert body["meta"]["resolved_params"] == {
        "top_k": 50,
        "min_score": -1.0,
        "candidate_multiplier": 30,
        "candidate_pool": 5000,
        "ann_probe": 64,
        "rerank_window": 200,
    }
    assert body["meta"]["warnings"]
    assert deps.index_runtime.records[-1]["status"] == "empty"
    assert deps.index_runtime.records[-1]["warnings"] == body["meta"]["warnings"]

import base64
import io

import pytest
from PIL import Image

from utils.vector_utils import ImageTooLargeError, ImageValidationError, decode_image


class _ExtractDeps:
    def __init__(self, *, decode_error: Exception | None = None):
        self.decode_error = decode_error
        self.logger = SimpleNamespace(
            warning=lambda *args, **kwargs: None,
            info=lambda *args, **kwargs: None,
            exception=lambda *args, **kwargs: None,
        )
        self.retrieval_guard = _FakeGuard()

    def accepts_raw_image_inference(self):
        return False

    def decode_image(self, image_base64):
        if self.decode_error is not None:
            raise self.decode_error
        return object()

    def decode_image_file(self, image_path):
        if self.decode_error is not None:
            raise self.decode_error
        return object()

    def preprocess(self, img):
        return img

    async def extract_feature_batched(self, tensor):
        return [0.25, 0.5]


def _png_base64(width=1, height=1):
    buffer = io.BytesIO()
    Image.new("RGB", (width, height), color=(255, 0, 0)).save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode("ascii")


def test_decode_image_accepts_data_url_png():
    image = decode_image(f"data:image/png;base64,{_png_base64()}")

    assert image.mode == "RGB"
    assert image.size == (1, 1)


def test_decode_image_rejects_invalid_base64():
    with pytest.raises(ImageValidationError):
        decode_image("not-base64")


def test_decode_image_respects_base64_limit(monkeypatch):
    monkeypatch.setenv("AURA_AI_MAX_IMAGE_BASE64_CHARS", "1024")

    with pytest.raises(ImageTooLargeError):
        decode_image("A" * 1025)


def test_decode_image_respects_pixel_limit(monkeypatch):
    monkeypatch.setenv("AURA_AI_MAX_IMAGE_PIXELS", "3")

    with pytest.raises(ImageTooLargeError):
        decode_image(_png_base64(width=2, height=2))


def test_extract_invalid_image_returns_400():
    deps = _ExtractDeps(decode_error=ImageValidationError("bad image"))
    app = FastAPI()
    app.include_router(build_api_router(deps))
    client = TestClient(app)

    response = client.post("/ai/extract", json={"image_base64": "not-base64", "metadata_json": "{}"})

    assert response.status_code == 400
    assert response.json()["code"] == 40002


def test_extract_too_large_image_returns_413(monkeypatch):
    monkeypatch.setenv("AURA_AI_MAX_IMAGE_BASE64_CHARS", "1024")
    deps = _ExtractDeps()
    app = FastAPI()
    app.include_router(build_api_router(deps))
    client = TestClient(app)

    response = client.post("/ai/extract", json={"image_base64": "A" * 1025, "metadata_json": "{}"})

    assert response.status_code == 413
    assert response.json()["code"] == 41301


def test_production_requires_non_placeholder_api_key(monkeypatch):
    monkeypatch.setenv("AURA_ENV", "Production")
    monkeypatch.delenv("AURA_API_KEY", raising=False)

    with pytest.raises(RuntimeError, match="AURA_API_KEY"):
        validate_production_security()


def test_production_disables_unscoped_extract_file(monkeypatch):
    monkeypatch.setenv("AURA_ENV", "Production")
    monkeypatch.delenv("AURA_AI_EXTRACT_FILE_ROOTS", raising=False)
    deps = _ExtractDeps()
    app = FastAPI()
    app.include_router(build_api_router(deps))
    client = TestClient(app)

    response = client.post("/ai/extract-file", json={"image_path": "/tmp/private.jpg", "metadata_json": "{}"})

    assert response.status_code == 403


def test_api_key_middleware_rejects_wrong_key(monkeypatch):
    monkeypatch.setenv("AURA_API_KEY", "test-ai-key-that-is-long-enough")
    app = FastAPI()
    register_middlewares(app)

    @app.post("/private")
    async def _private():
        return {"ok": True}

    response = TestClient(app).post("/private", headers={"X-Aura-Ai-Key": "wrong"})

    assert response.status_code == 401
