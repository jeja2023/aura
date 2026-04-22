from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient

from routes.api_routes import build_api_router
from services.index_runtime_service import IndexRuntimeService
from vector_store.index_store import search_vectors


class _FakeDeps:
    def decode_image(self, image_base64):
        return object()

    def preprocess(self, img):
        return img

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
    assert "boom" in body["msg"]


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
