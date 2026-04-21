import asyncio
import base64
import io
import logging
import math
import os
import threading
from contextlib import asynccontextmanager
from datetime import datetime
from pathlib import Path

import numpy as np
import onnxruntime as ort
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from PIL import Image
from pydantic import BaseModel

try:
    from arango import ArangoClient
except Exception:
    ArangoClient = None


logger = logging.getLogger("aura.ai")

COLLECTION_NAME = "aura_reid"
VECTOR_DIM = 512

_batch_task: asyncio.Task | None = None
_batch_queue = asyncio.Queue()
_BATCH_SIZE = 16
_MAX_WAIT_SECONDS = 0.05

_local_index = []
_index_lock = threading.Lock()
_arango_db = None
_arango_collection = None
_ort_session = None
_ort_input_name = ""
_model_error = ""
_arango_error = ""


def _truthy(value: str | None) -> bool:
    if value is None:
        return False
    return value.strip().lower() in {"1", "true", "yes", "on"}


def _current_environment() -> str:
    for key in ("AURA_ENV", "ASPNETCORE_ENVIRONMENT", "ENVIRONMENT", "FASTAPI_ENV"):
        value = os.getenv(key, "").strip()
        if value:
            return value
    return "Development"


def _requires_persistent_index() -> bool:
    override = os.getenv("AURA_AI_REQUIRE_ARANGO", "").strip()
    if override:
        return _truthy(override)
    return _current_environment().lower() == "production"


def _mark_arango_failure(ex: Exception | str) -> None:
    global _arango_db, _arango_collection, _arango_error
    _arango_db = None
    _arango_collection = None
    _arango_error = str(ex)


def _service_state(arango_enabled: bool | None = None) -> dict:
    return {
        "time": datetime.now().isoformat(),
        "environment": _current_environment(),
        "arango_required": _requires_persistent_index(),
        "arangodb_enabled": (_arango_db is not None) if arango_enabled is None else arango_enabled,
        "arango_error": _arango_error,
        "model_loaded": _ort_session is not None,
        "model_error": _model_error,
    }


def _service_unavailable_response(code: int, message: str, *, data: dict | list | None = None) -> JSONResponse:
    payload = {"code": code, "msg": message}
    if data is not None:
        payload["data"] = data
    return JSONResponse(status_code=503, content=payload)


async def _background_init_and_batch():
    global _model_error, _batch_task
    loop = asyncio.get_running_loop()
    try:
        await loop.run_in_executor(None, _init_arango)
        await loop.run_in_executor(None, _init_model)
    except Exception as ex:
        _model_error = f"后台初始化异常: {ex}"
        logger.exception("后台初始化失败")
        return

    if _ort_session is not None:
        _batch_task = asyncio.create_task(_batch_loop())


@asynccontextmanager
async def _lifespan(app: FastAPI):
    asyncio.create_task(_background_init_and_batch())
    yield


app = FastAPI(title="Aura AI 推理服务", version="0.1.0", lifespan=_lifespan)


@app.middleware("http")
async def _aura_ai_api_key_guard(request: Request, call_next):
    expected = os.getenv("AURA_API_KEY", "").strip()
    if not expected:
        return await call_next(request)

    if request.method in ("GET", "HEAD"):
        path = request.url.path
        if (
            path == "/"
            or path == "/openapi.json"
            or path.startswith("/docs")
            or path.startswith("/redoc")
            or path == "/favicon.ico"
        ):
            return await call_next(request)

    incoming = request.headers.get("X-Aura-Ai-Key", "")
    if incoming != expected:
        return JSONResponse(
            status_code=401,
            content={"code": 40101, "msg": "未授权访问 AI 服务"},
        )
    return await call_next(request)


def _init_arango():
    global _arango_db, _arango_collection, _arango_error
    if ArangoClient is None:
        _arango_db = None
        _arango_collection = None
        _arango_error = "python-arango 未安装或无法导入"
        return

    arango_uri = os.getenv("ARANGO_URI", "http://127.0.0.1:8529")
    arango_db_name = os.getenv("ARANGO_DB", "aura")
    arango_user = os.getenv("ARANGO_USER", "")
    arango_password = os.getenv("ARANGO_PASSWORD", "")
    _arango_error = ""

    if not arango_user or not arango_password:
        _arango_db = None
        _arango_collection = None
        _arango_error = "ARANGO_USER/ARANGO_PASSWORD 未配置"
        return

    if "PLEASE_" in arango_user.upper() or "PLEASE_" in arango_password.upper():
        _arango_db = None
        _arango_collection = None
        _arango_error = "ARANGO_USER/ARANGO_PASSWORD 仍为占位值"
        return

    try:
        client = ArangoClient(hosts=arango_uri)
        _arango_db = client.db(
            name=arango_db_name,
            username=arango_user,
            password=arango_password,
        )
        if not _arango_db.has_collection(COLLECTION_NAME):
            _arango_db.create_collection(COLLECTION_NAME)
        _arango_collection = _arango_db.collection(COLLECTION_NAME)
    except Exception as ex:
        _mark_arango_failure(ex)


def _ensure_arango():
    if _arango_db is not None:
        return True

    _init_arango()
    return _arango_db is not None


def _init_model():
    global _ort_session, _ort_input_name, _model_error
    root = Path(__file__).resolve().parents[1]
    model_path = Path(os.getenv("AURA_MODEL_PATH", str(root / "models" / "osnet_ibn_x1_0.onnx")))
    try:
        if not model_path.exists():
            _model_error = f"未找到模型文件: {model_path}"
            return

        available = ort.get_available_providers()
        providers = []
        if "CUDAExecutionProvider" in available:
            providers.append("CUDAExecutionProvider")
        providers.append("CPUExecutionProvider")

        _ort_session = ort.InferenceSession(str(model_path), providers=providers)
        _ort_input_name = _ort_session.get_inputs()[0].name
        _model_error = ""
        logger.info("ONNX 推理会话已初始化，providers=%s", providers)
    except Exception as ex:
        _ort_session = None
        _model_error = f"模型加载失败: {ex}"


class ImageReq(BaseModel):
    image_base64: str
    metadata_json: str = "{}"


class ImageFileReq(BaseModel):
    image_path: str
    metadata_json: str = "{}"


class SearchReq(BaseModel):
    feature: list[float]
    top_k: int = 10


class UpsertReq(BaseModel):
    vid: str
    feature: list[float]


async def _batch_loop():
    while True:
        item = await _batch_queue.get()
        batch = [item]
        start_time = asyncio.get_running_loop().time()

        while len(batch) < _BATCH_SIZE:
            time_left = _MAX_WAIT_SECONDS - (asyncio.get_running_loop().time() - start_time)
            if time_left <= 0:
                break
            try:
                item = await asyncio.wait_for(_batch_queue.get(), timeout=time_left)
                batch.append(item)
            except (asyncio.TimeoutError, asyncio.QueueEmpty):
                break

        try:
            tensors = [tensor for tensor, _future in batch]
            input_data = tensors[0] if len(tensors) == 1 else np.concatenate(tensors, axis=0)
            loop = asyncio.get_running_loop()
            outputs = await loop.run_in_executor(
                None,
                lambda: _ort_session.run(None, {_ort_input_name: input_data}),
            )

            feat_batch = np.asarray(outputs[0]).astype(np.float32)
            for index, (_tensor, future) in enumerate(batch):
                feature = feat_batch[index].reshape(-1).tolist()
                future.set_result(_normalize_feature(feature))
        except Exception as ex:
            for _tensor, future in batch:
                if not future.done():
                    future.set_exception(ex)


def _normalize_feature(feature: list[float]) -> list[float]:
    if not feature:
        return [0.0] * VECTOR_DIM

    if len(feature) >= VECTOR_DIM:
        data = feature[:VECTOR_DIM]
    else:
        data = feature + [0.0] * (VECTOR_DIM - len(feature))

    norm = math.sqrt(sum(x * x for x in data))
    if norm == 0:
        return data

    return [x / norm for x in data]


def _decode_image(image_base64: str) -> Image.Image:
    raw = image_base64.split(",", 1)[-1]
    data = base64.b64decode(raw)
    return Image.open(io.BytesIO(data)).convert("RGB")


def _preprocess(img: Image.Image) -> np.ndarray:
    img = img.resize((128, 256), Image.BILINEAR)
    arr = np.asarray(img).astype(np.float32) / 255.0
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    arr = (arr - mean) / std
    arr = np.transpose(arr, (2, 0, 1))
    arr = np.expand_dims(arr, axis=0).astype(np.float32)
    return arr


async def _extract_feature_batched(tensor: np.ndarray) -> list[float]:
    if _ort_session is None:
        raise RuntimeError(_model_error or "onnx session not initialized")

    future = asyncio.get_running_loop().create_future()
    await _batch_queue.put((tensor, future))
    return await future


def _cosine(a: list[float], b: list[float]) -> float:
    return sum(x * y for x, y in zip(a, b))


def _strict_arango_unavailable(message: str, *, data: dict | list | None = None) -> JSONResponse:
    logger.critical("%s; arango_error=%s", message, _arango_error or "unknown")
    return _service_unavailable_response(50301, message, data=data)


@app.get("/")
def health():
    arango_enabled = _ensure_arango()
    payload = _service_state(arango_enabled=arango_enabled)
    if payload["arango_required"] and not arango_enabled:
        payload["code"] = 50301
        payload["msg"] = "ArangoDB 不可用，AI 服务处于受限状态"
        logger.critical("健康检查发现 ArangoDB 不可用且当前环境要求持久化索引")
        return JSONResponse(status_code=503, content=payload)

    payload["code"] = 0
    payload["msg"] = "AI 服务运行正常"
    return payload


@app.post("/ai/extract")
async def extract(req: ImageReq):
    try:
        img = _decode_image(req.image_base64)
        tensor = _preprocess(img)
        feature = await _extract_feature_batched(tensor)
        return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
    except Exception as ex:
        return {"code": 50001, "msg": f"特征提取失败: {ex}", "data": {"feature": [], "dim": 0}}


@app.post("/ai/extract-file")
async def extract_file(req: ImageFileReq):
    try:
        path = Path(req.image_path)
        if not path.exists():
            return {"code": 40401, "msg": f"文件不存在: {req.image_path}"}

        with Image.open(str(path)) as img:
            rgb = img.convert("RGB")
            tensor = _preprocess(rgb)

        feature = await _extract_feature_batched(tensor)
        return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
    except Exception as ex:
        return {"code": 50001, "msg": f"特征提取失败: {ex}", "data": {"feature": [], "dim": 0}}


@app.post("/ai/upsert")
async def upsert(req: UpsertReq):
    feature = _normalize_feature(req.feature)
    strict_mode = _requires_persistent_index()

    if _ensure_arango():
        try:
            _arango_db.aql.execute(
                """
                UPSERT { _key: @vid }
                INSERT { _key: @vid, vid: @vid, feature: @feature }
                UPDATE { vid: @vid, feature: @feature }
                IN @@col
                """,
                bind_vars={
                    "@col": COLLECTION_NAME,
                    "vid": req.vid,
                    "feature": feature,
                },
            )
            return {"code": 0, "msg": "写入成功", "data": {"vid": req.vid, "engine": "arangodb"}}
        except Exception as ex:
            _mark_arango_failure(ex)
            if strict_mode:
                return _strict_arango_unavailable(
                    "ArangoDB 不可用，已拒绝向内存索引降级写入",
                    data={"vid": req.vid, "engine": "unavailable"},
                )
            logger.warning("ArangoDB 写入失败，降级到内存索引. vid=%s error=%s", req.vid, _arango_error)
    elif strict_mode:
        return _strict_arango_unavailable(
            "ArangoDB 不可用，已拒绝向内存索引降级写入",
            data={"vid": req.vid, "engine": "unavailable"},
        )

    with _index_lock:
        global _local_index
        _local_index = [item for item in _local_index if item["vid"] != req.vid]
        _local_index.append({"vid": req.vid, "feature": feature})

    return {"code": 0, "msg": "写入成功", "data": {"vid": req.vid, "engine": "memory"}}


@app.post("/ai/search")
async def search(req: SearchReq):
    feature = _normalize_feature(req.feature)
    top_k = max(1, min(req.top_k, 50))
    strict_mode = _requires_persistent_index()

    if _ensure_arango():
        try:
            cursor = _arango_db.aql.execute(
                """
                FOR d IN @@col
                  LET score = SUM(
                    FOR i IN 0..511
                      RETURN d.feature[i] * @feature[i]
                  )
                  SORT score DESC
                  LIMIT @k
                  RETURN { vid: d.vid, score: score }
                """,
                bind_vars={
                    "@col": COLLECTION_NAME,
                    "feature": feature,
                    "k": top_k,
                },
            )
            hits = [{"vid": item["vid"], "score": float(item["score"])} for item in cursor]
            return {"code": 0, "msg": "检索成功", "data": hits}
        except Exception as ex:
            _mark_arango_failure(ex)
            if strict_mode:
                return _strict_arango_unavailable(
                    "ArangoDB 不可用，已拒绝降级到内存索引检索",
                    data=[],
                )
            logger.warning("ArangoDB 检索失败，降级到内存索引. error=%s", _arango_error)
    elif strict_mode:
        return _strict_arango_unavailable(
            "ArangoDB 不可用，已拒绝降级到内存索引检索",
            data=[],
        )

    with _index_lock:
        scores = [{"vid": item["vid"], "score": _cosine(feature, item["feature"])} for item in _local_index]

    scores.sort(key=lambda item: item["score"], reverse=True)
    return {"code": 0, "msg": "检索成功", "data": scores[:top_k]}


@app.post("/ai/cluster")
def cluster():
    return {"code": 0, "msg": "聚类任务已提交", "data": {"task_id": "cluster-demo-001"}}
