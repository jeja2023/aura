from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient

from routes.api_routes import build_api_router
from services.evaluation_service import evaluate_retrieval_dataset
from utils.vector_utils import cosine, normalize_feature


def _sample_dataset() -> dict:
    return {
        "gallery": [
            {"vid": "person-a-1", "feature": [1.0, 0.0, 0.0, 0.0], "metadata": {"camera": "east"}},
            {"vid": "person-a-2", "feature": [0.98, 0.02, 0.0, 0.0], "metadata": {"camera": "north"}},
            {"vid": "person-b-1", "feature": [0.0, 1.0, 0.0, 0.0], "metadata": {"camera": "east"}},
        ],
        "queries": [
            {"query_id": "qa", "feature": [1.0, 0.0, 0.0, 0.0], "relevant_vids": ["person-a-1", "person-a-2"]},
            {
                "query_id": "qb",
                "feature": [0.0, 1.0, 0.0, 0.0],
                "relevant_vids": ["person-b-1"],
                "metadata_filter": {"camera": "east"},
            },
        ],
    }


class _FakeGuard:
    def allow_request(self):
        return True, ""

    def record_result(self, **_kwargs):
        return None


class _EvalDeps:
    def __init__(self):
        self.logger = SimpleNamespace(
            warning=lambda *args, **kwargs: None,
            exception=lambda *args, **kwargs: None,
            critical=lambda *args, **kwargs: None,
        )
        self.vector_dim = 4
        self.retrieval_guard = _FakeGuard()

    def ensure_arango(self):
        return True

    def service_state(self, *, arango_enabled):
        return {
            "arango_required": False,
            "arango_enabled": arango_enabled,
            "model_loaded": True,
            "inference_ready": True,
            "inference_error": "",
        }

    def normalize_feature(self, feature):
        return normalize_feature(feature, self.vector_dim)

    def cosine(self, left, right):
        return cosine(left, right)


def test_evaluate_retrieval_dataset_reports_quality_metrics():
    result = evaluate_retrieval_dataset(
        _sample_dataset(),
        top_k=2,
        min_score=-1.0,
        candidate_multiplier=8,
        candidate_pool=0,
        ann_probe=16,
        rerank_window=30,
        normalize_feature_func=lambda feature: normalize_feature(feature, 4),
        cosine_func=cosine,
        vector_dim=4,
        logger=SimpleNamespace(warning=lambda *args, **kwargs: None),
    )

    summary = result["summary"]
    assert summary["query_count"] == 2
    assert summary["gallery_count"] == 3
    assert summary["recall_at_k"] == 1.0
    assert summary["mrr"] == 1.0
    assert summary["hit_rate_at_k"] == 1.0
    assert summary["empty_rate"] == 0.0
    assert result["items"][1]["matched_vids"] == ["person-b-1"]


def test_evaluate_search_route_accepts_inline_dataset():
    app = FastAPI()
    app.include_router(build_api_router(_EvalDeps()))
    client = TestClient(app)

    response = client.post(
        "/ai/evaluate-search",
        json={"dataset": _sample_dataset(), "top_k": 2, "candidate_multiplier": 64, "ann_probe": 128},
    )

    assert response.status_code == 200
    body = response.json()
    assert body["code"] == 0
    summary = body["data"]["summary"]
    assert summary["recall_at_k"] == 1.0
    assert summary["resolved_params"]["candidate_multiplier"] == 30
    assert summary["resolved_params"]["ann_probe"] == 64
    assert summary["warnings"]
