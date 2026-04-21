import base64
import io
from pathlib import Path
import sys

import pytest
from fastapi.testclient import TestClient
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main as ai_main


def _make_image_base64() -> str:
    image = Image.new("RGB", (4, 4), color=(32, 64, 128))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode("ascii")


@pytest.fixture
def client(monkeypatch: pytest.MonkeyPatch):
    async def _noop_background_init():
        return None

    monkeypatch.delenv("AURA_API_KEY", raising=False)
    monkeypatch.setattr(ai_main, "_background_init_and_batch", _noop_background_init)

    with TestClient(ai_main.app) as test_client:
        yield test_client


def test_health_endpoint_returns_service_state(client: TestClient):
    response = client.get("/")

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0
    assert "model_loaded" in payload
    assert "arangodb_enabled" in payload


def test_extract_returns_feature_vector(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    async def _fake_extract(_tensor):
        return [1.0] + [0.0] * (ai_main.VECTOR_DIM - 1)

    monkeypatch.setattr(ai_main, "_extract_feature_batched", _fake_extract)

    response = client.post("/ai/extract", json={"image_base64": _make_image_base64()})

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0
    assert payload["data"]["dim"] == ai_main.VECTOR_DIM
    assert payload["data"]["feature"][0] == 1.0


def test_extract_returns_degraded_error_when_model_unavailable(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    async def _raise_extract(_tensor):
        raise RuntimeError("mock model unavailable")

    monkeypatch.setattr(ai_main, "_extract_feature_batched", _raise_extract)

    response = client.post("/ai/extract", json={"image_base64": _make_image_base64()})

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 50001
    assert payload["data"]["feature"] == []
    assert payload["data"]["dim"] == 0


def test_health_bypasses_api_key_guard_for_get_requests(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("AURA_API_KEY", "secret-key")

    response = client.get("/")

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0


def test_api_key_guard_rejects_unauthorized_post_requests(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("AURA_API_KEY", "secret-key")

    response = client.post("/ai/cluster")

    assert response.status_code == 401
    payload = response.json()
    assert payload["code"] == 40101


def test_api_key_guard_allows_authorized_post_requests(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("AURA_API_KEY", "secret-key")

    response = client.post("/ai/cluster", headers={"X-Aura-Ai-Key": "secret-key"})

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0
    assert payload["data"]["task_id"] == "cluster-demo-001"


def test_extract_file_returns_feature_vector(client: TestClient, monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
    async def _fake_extract(_tensor):
        return [0.5] + [0.0] * (ai_main.VECTOR_DIM - 1)

    image_path = tmp_path / "sample.png"
    Image.new("RGB", (8, 8), color=(12, 34, 56)).save(image_path)
    monkeypatch.setattr(ai_main, "_extract_feature_batched", _fake_extract)

    response = client.post("/ai/extract-file", json={"image_path": str(image_path)})

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0
    assert payload["data"]["dim"] == ai_main.VECTOR_DIM
    assert payload["data"]["feature"][0] == 0.5


def test_extract_file_returns_not_found_for_missing_file(client: TestClient, tmp_path: Path):
    missing_path = tmp_path / "missing.png"

    response = client.post("/ai/extract-file", json={"image_path": str(missing_path)})

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 40401


def test_health_reports_arango_failure_details(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr(ai_main, "_arango_db", None)
    monkeypatch.setattr(ai_main, "_arango_collection", None)
    monkeypatch.setattr(ai_main, "_arango_error", "mock arango failure")
    monkeypatch.setattr(ai_main, "_ensure_arango", lambda: False)

    response = client.get("/")

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0
    assert payload["arangodb_enabled"] is False
    assert payload["arango_error"] == "mock arango failure"


def test_cluster_endpoint_returns_demo_task(client: TestClient):
    response = client.post("/ai/cluster")

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0
    assert payload["data"]["task_id"] == "cluster-demo-001"


def test_search_falls_back_to_memory_index(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr(ai_main, "_ensure_arango", lambda: False)
    old_index = list(ai_main._local_index)
    ai_main._local_index = [
        {"vid": "vid-1", "feature": ai_main._normalize_feature([1.0, 0.0, 0.0])},
        {"vid": "vid-2", "feature": ai_main._normalize_feature([0.2, 0.8, 0.0])},
    ]

    try:
        response = client.post("/ai/search", json={"feature": [1.0, 0.0, 0.0], "top_k": 2})
    finally:
        ai_main._local_index = old_index

    assert response.status_code == 200
    payload = response.json()
    assert payload["code"] == 0
    assert len(payload["data"]) == 2
    assert payload["data"][0]["vid"] == "vid-1"


def test_health_returns_503_when_strict_mode_requires_arango(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("AURA_AI_REQUIRE_ARANGO", "true")
    monkeypatch.setattr(ai_main, "_ensure_arango", lambda: False)

    response = client.get("/")

    assert response.status_code == 503
    payload = response.json()
    assert payload["code"] == 50301
    assert payload["arango_required"] is True


def test_upsert_rejects_memory_fallback_when_strict_mode_enabled(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("AURA_AI_REQUIRE_ARANGO", "true")
    monkeypatch.setattr(ai_main, "_ensure_arango", lambda: False)
    old_index = list(ai_main._local_index)

    try:
        response = client.post("/ai/upsert", json={"vid": "vid-strict", "feature": [1.0, 0.0, 0.0]})
    finally:
        ai_main._local_index = old_index

    assert response.status_code == 503
    payload = response.json()
    assert payload["code"] == 50301
    assert payload["data"]["engine"] == "unavailable"
    assert not any(item["vid"] == "vid-strict" for item in ai_main._local_index)


def test_search_rejects_memory_fallback_when_strict_mode_enabled(client: TestClient, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("AURA_AI_REQUIRE_ARANGO", "true")
    monkeypatch.setattr(ai_main, "_ensure_arango", lambda: False)
    old_index = list(ai_main._local_index)
    ai_main._local_index = [{"vid": "vid-1", "feature": ai_main._normalize_feature([1.0, 0.0, 0.0])}]

    try:
        response = client.post("/ai/search", json={"feature": [1.0, 0.0, 0.0], "top_k": 1})
    finally:
        ai_main._local_index = old_index

    assert response.status_code == 503
    payload = response.json()
    assert payload["code"] == 50301
    assert payload["data"] == []
