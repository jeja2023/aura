import asyncio
import os
from pathlib import Path
from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient
import numpy as np

from routes.api_routes import build_api_router
from services.inference_service import InferenceBackpressureError
from services.inference_service import InferenceUnavailableError
from services.inference_service import InferenceService
from services.index_runtime_service import IndexRuntimeService
from vector_store.index_store import search_vectors


class _FakeDeps:
    logger = SimpleNamespace(
        exception=lambda *args, **kwargs: None,
        critical=lambda *args, **kwargs: None,
        warning=lambda *args, **kwargs: None,
    )
    retrieval_guard = SimpleNamespace(
        allow_request=lambda: (True, ""),
        record_result=lambda **kwargs: None,
    )

    def ensure_arango(self):
        return True

    def service_state(self, *, arango_enabled):
        return {
            "arango_required": False,
            "arango_enabled": arango_enabled,
            "model_loaded": True,
            "inference_ready": True,
            "inference_error": "",
            "inference_metrics": {
                "queue": {"max_size": 256, "current_size": 0, "remaining": 256},
                "batch": {"processed_batches_total": 0, "failed_batches_total": 0},
            },
        }

    def decode_image(self, image_base64):
        return object()

    def preprocess(self, img):
        return img

    def accepts_raw_image_inference(self):
        return False

    async def extract_feature_batched(self, tensor):
        raise RuntimeError("boom")


def test_extract_failure_returns_500_status():
    app = FastAPI()
    app.include_router(build_api_router(_FakeDeps()))
    client = TestClient(app)

    response = client.post("/ai/extract", json={"image_base64": "ignored", "metadata_json": "{}"})

    assert response.status_code == 500
    body = response.json()
    assert body["code"] == 50001
    assert body["msg"] == "特征提取失败，请稍后重试"


def test_live_probe_does_not_require_model_readiness():
    app = FastAPI()
    app.include_router(build_api_router(_FakeDeps()))
    client = TestClient(app)

    response = client.get("/live")

    assert response.status_code == 200
    assert response.json()["status"] == "alive"


def test_ready_probe_returns_structured_health_payload():
    app = FastAPI()
    app.include_router(build_api_router(_FakeDeps()))
    client = TestClient(app)

    response = client.get("/ready")

    assert response.status_code == 200
    body = response.json()
    assert body["code"] == 0
    assert body["model_loaded"] is True
    assert body["inference_ready"] is True
    assert body["inference_metrics"]["queue"]["remaining"] == 256


def test_ready_probe_returns_503_when_inference_is_not_ready():
    class _UnavailableReadyDeps(_FakeDeps):
        def service_state(self, *, arango_enabled):
            payload = super().service_state(arango_enabled=arango_enabled)
            payload["inference_ready"] = False
            payload["inference_error"] = "all remote nodes are open"
            return payload

    app = FastAPI()
    app.include_router(build_api_router(_UnavailableReadyDeps()))
    client = TestClient(app)

    response = client.get("/ready")

    assert response.status_code == 503
    body = response.json()
    assert body["code"] == 50302
    assert body["inference_ready"] is False


def test_extract_file_missing_returns_404_status():
    app = FastAPI()
    app.include_router(build_api_router(_FakeDeps()))
    client = TestClient(app)

    response = client.post("/ai/extract-file", json={"image_path": "Z:/missing/image.bin", "metadata_json": "{}"})

    assert response.status_code == 404
    assert response.json()["code"] == 40401


def test_record_search_failed_request_is_not_counted_as_empty():
    service = IndexRuntimeService(snapshot_path=":memory:")

    service.record_search(success=False, hit_count=0, latency_ms=12.5, engine="unavailable", strategy="none")
    service.record_search(success=True, hit_count=0, latency_ms=8.0, engine="memory", strategy="ann-rerank")

    data = service.get_search_metrics()
    assert data["search_failed"] == 1
    assert data["search_success"] == 1
    assert data["search_empty"] == 1


def test_search_vectors_explain_uses_requested_bucket_probe():
    class _FakeAql:
        def execute(self, _aql, bind_vars):
            assert bind_vars["bucket_candidates"]
            return [{"vid": "C_1", "score": 0.91}]

    fake_db = SimpleNamespace(aql=_FakeAql())

    result = search_vectors(
        feature=[0.2] * 512,
        top_k=5,
        min_score=-1.0,
        candidate_multiplier=8,
        candidate_pool=50,
        ann_probe=24,
        rerank_window=30,
        include_vids=None,
        exclude_vids=None,
        metadata_filter=None,
        explain=True,
        strict_mode=False,
        ensure_arango_func=lambda: True,
        get_arango_db_func=lambda: fake_db,
        mark_arango_failure_func=lambda ex: None,
        get_arango_error_func=lambda: "",
        strict_unavailable_func=lambda message, data=None: (_ for _ in ()).throw(AssertionError(message)),
        collection_name="aura_reid",
        vector_dim=512,
        normalize_feature_func=lambda feature: feature,
        cosine_func=lambda left, right: 1.0,
        logger=SimpleNamespace(warning=lambda *args, **kwargs: None),
        index_lock=None,
        local_index=[],
    )

    assert result["meta"]["ann_probe"] == 24
    assert result["meta"]["strategy"] == "bucket-prefilter-exact-cosine"
    assert result["explain"]["phase"][0] == "prefilter:ann_bucket_24"


def test_extract_busy_returns_429_status():
    class _BusyDeps(_FakeDeps):
        async def extract_feature_batched(self, tensor):
            raise InferenceBackpressureError("busy")

    app = FastAPI()
    app.include_router(build_api_router(_BusyDeps()))
    client = TestClient(app)

    response = client.post("/ai/extract", json={"image_base64": "ignored", "metadata_json": "{}"})

    assert response.status_code == 429
    body = response.json()
    assert body["code"] == 42902
    assert body["msg"] == "推理服务繁忙，请稍后重试"


def test_inference_metrics_track_backpressure_and_batch_results():
    service = InferenceService(
        normalize_feature_func=lambda feature: feature,
        logger=SimpleNamespace(info=lambda *args, **kwargs: None, warning=lambda *args, **kwargs: None),
        batch_size=4,
        max_wait_seconds=0.03,
        max_queue_size=2,
        enqueue_timeout_seconds=0.01,
    )

    service._record_enqueue()
    service._record_backpressure()
    service._record_batch_result(batch_size=2, latency_ms=12.3456, success=True)
    service._record_batch_result(batch_size=1, latency_ms=2.0, success=False)

    metrics = service.inference_metrics()

    assert metrics["backend"] == "onnx"
    assert metrics["queue"]["max_size"] == 2
    assert metrics["queue"]["enqueue_total"] == 1
    assert metrics["queue"]["backpressure_total"] == 1
    assert metrics["queue"]["last_backpressure_at"] > 0
    assert metrics["batch"]["batch_size"] == 4
    assert metrics["batch"]["processed_batches_total"] == 1
    assert metrics["batch"]["processed_items_total"] == 2
    assert metrics["batch"]["failed_batches_total"] == 1
    assert metrics["batch"]["last_batch_size"] == 2
    assert metrics["batch"]["last_batch_latency_ms"] == 12.346
    assert metrics["batch"]["avg_batch_size"] == 2.0


def test_extract_unavailable_returns_503_status():
    class _UnavailableDeps(_FakeDeps):
        async def extract_feature_batched(self, tensor):
            raise InferenceUnavailableError("all remote nodes are open")

    app = FastAPI()
    app.include_router(build_api_router(_UnavailableDeps()))
    client = TestClient(app)

    response = client.post("/ai/extract", json={"image_base64": "ignored", "metadata_json": "{}"})

    assert response.status_code == 503
    body = response.json()
    assert body["code"] == 50302
    assert body["msg"] == "AI 推理服务当前不可用，请稍后重试"


def test_extract_file_rejects_path_outside_allowed_roots(tmp_path):
    app = FastAPI()
    app.include_router(build_api_router(_FakeDeps()))
    client = TestClient(app)
    base = tmp_path / "ai-route-path-guard"
    allowed_root = base / "allowed"
    outside_file = base / "outside.txt"
    allowed_root.mkdir(parents=True, exist_ok=True)
    base.mkdir(parents=True, exist_ok=True)
    outside_file.write_text("x", encoding="utf-8")

    os.environ["AURA_AI_EXTRACT_FILE_ROOTS"] = str(allowed_root.resolve())
    try:
        response = client.post("/ai/extract-file", json={"image_path": str(outside_file), "metadata_json": "{}"})
    finally:
        os.environ.pop("AURA_AI_EXTRACT_FILE_ROOTS", None)

    assert response.status_code == 403
    assert response.json()["code"] == 40301


def test_gpu_predict_response_parser_accepts_nested_feature_payloads():
    payload = {
        "code": 0,
        "data": {
            "model": "osnet_x1_0_v1.onnx",
            "outputs": [[[0.1, 0.2, 0.3]]],
        },
    }

    assert InferenceService._extract_feature_from_payload(payload) == [0.1, 0.2, 0.3]


def test_gpu_predict_url_defaults_to_predict_path():
    assert InferenceService._normalize_predict_url("http://gpu-worker-0:8000") == "http://gpu-worker-0:8000/predict"
    assert InferenceService._normalize_predict_url("http://gpu-worker-0:8000/predict") == "http://gpu-worker-0:8000/predict"


def test_external_extract_url_defaults_to_ai_extract_path():
    assert InferenceService._normalize_extract_url("http://external-ai:8000") == "http://external-ai:8000/ai/extract"
    assert InferenceService._normalize_extract_url("http://external-ai:8000/custom/extract") == "http://external-ai:8000/custom/extract"


def test_remote_endpoint_weight_parser():
    endpoint = InferenceService._build_remote_endpoint("http://gpu-worker-0:8000|3", InferenceService._normalize_predict_url)

    assert endpoint["url"] == "http://gpu-worker-0:8000/predict"
    assert endpoint["weight"] == 3


def test_remote_failure_opens_single_node_breaker():
    service = InferenceService(
        normalize_feature_func=lambda feature: feature,
        logger=SimpleNamespace(info=lambda *args, **kwargs: None, warning=lambda *args, **kwargs: None),
    )
    endpoint = InferenceService._build_remote_endpoint("http://gpu-worker-0:8000", InferenceService._normalize_predict_url)
    service._remote_enabled = True
    service._remote_mode = "gpu-worker"
    service._remote_predict_pool = [endpoint]
    service._remote_breaker_fail_threshold = 2
    service._remote_breaker_open_seconds = 30

    service._record_remote_failure(endpoint)
    assert service.is_ready is True
    service._record_remote_failure(endpoint)

    status = service.remote_status()
    assert service.is_ready is False
    assert status["open_nodes"] == 1
    assert status["healthy_nodes"] == 0


def test_remote_pool_fails_over_to_next_healthy_node():
    service = InferenceService(
        normalize_feature_func=lambda feature: feature,
        logger=SimpleNamespace(info=lambda *args, **kwargs: None, warning=lambda *args, **kwargs: None),
    )
    service._remote_enabled = True
    service._remote_mode = "gpu-worker"
    service._remote_project_name = "person_reid"
    service._remote_model_name = "osnet.onnx"
    service._remote_breaker_fail_threshold = 1
    service._remote_breaker_open_seconds = 30
    service._remote_predict_pool = [
        InferenceService._build_remote_endpoint("http://gpu-worker-0:8000", InferenceService._normalize_predict_url),
        InferenceService._build_remote_endpoint("http://gpu-worker-1:8000", InferenceService._normalize_predict_url),
    ]
    calls = []

    def fake_post(url, payload):
        calls.append(url)
        if "gpu-worker-0" in url:
            raise RuntimeError("node down")
        assert payload["project_name"] == "person_reid"
        assert payload["model_name"] == "osnet.onnx"
        return [0.4, 0.6]

    service._post_json_for_feature = fake_post

    feature = asyncio.run(service._extract_feature_remote(np.zeros((1, 3), dtype=np.float32)))

    assert feature == [0.4, 0.6]
    assert calls == ["http://gpu-worker-0:8000/predict", "http://gpu-worker-1:8000/predict"]
    assert service.remote_status()["open_nodes"] == 1


def test_external_image_extract_mode_has_priority_over_gpu_predict(monkeypatch):
    monkeypatch.setenv("AURA_EXTERNAL_EXTRACT_URLS", "http://external-ai:8000")
    monkeypatch.setenv("AURA_GPU_PREDICT_URLS", "http://gpu-worker-0:8000/predict")
    monkeypatch.setenv("AURA_GPU_PROJECT_NAME", "person_reid")
    monkeypatch.setenv("AURA_GPU_MODEL_NAME", "osnet.onnx")
    service = InferenceService(
        normalize_feature_func=lambda feature: feature,
        logger=SimpleNamespace(info=lambda *args, **kwargs: None, warning=lambda *args, **kwargs: None),
    )

    assert service.init_model() is None
    assert service.backend == "external-image"
    assert service.model_loaded is True
    assert service.accepts_raw_image is True


def test_extract_route_uses_external_image_service_without_local_decode():
    class _ExternalImageDeps(_FakeDeps):
        def accepts_raw_image_inference(self):
            return True

        def decode_image(self, image_base64):
            raise AssertionError("local decode should not run")

        async def extract_feature_from_base64(self, image_base64, metadata_json="{}"):
            assert image_base64 == "base64-data"
            assert metadata_json == '{"source":"test"}'
            return [0.25, 0.5]

    app = FastAPI()
    app.include_router(build_api_router(_ExternalImageDeps()))
    client = TestClient(app)

    response = client.post("/ai/extract", json={"image_base64": "base64-data", "metadata_json": '{"source":"test"}'})

    assert response.status_code == 200
    body = response.json()
    assert body["code"] == 0
    assert body["data"]["feature"] == [0.25, 0.5]
    assert body["data"]["dim"] == 2


def test_extract_file_route_can_delegate_missing_path_to_external_image_service():
    class _ExternalImageDeps(_FakeDeps):
        def accepts_raw_image_inference(self):
            return True

        async def extract_feature_from_file(self, image_path, metadata_json="{}"):
            assert image_path == "/shared/captures/person.jpg"
            assert metadata_json == '{"source":"external"}'
            return [0.75]

    app = FastAPI()
    app.include_router(build_api_router(_ExternalImageDeps()))
    client = TestClient(app)

    response = client.post(
        "/ai/extract-file",
        json={"image_path": "/shared/captures/person.jpg", "metadata_json": '{"source":"external"}'},
    )

    assert response.status_code == 200
    body = response.json()
    assert body["code"] == 0
    assert body["data"]["feature"] == [0.75]
