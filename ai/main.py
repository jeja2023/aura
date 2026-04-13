import asyncio
import threading
from contextlib import asynccontextmanager
from datetime import datetime
import base64
import io
import math
import os
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

_batch_task: asyncio.Task | None = None


async def _background_init_and_batch():
    """在后台加载 Arango/ONNX，避免阻塞 Uvicorn 绑定端口；完成后启动批处理循环。"""
    global _model_error, _batch_task
    loop = asyncio.get_running_loop()
    try:
        await loop.run_in_executor(None, _init_arango)
        await loop.run_in_executor(None, _init_model)
    except Exception as ex:
        _model_error = f"后台初始化异常: {ex}"
        print(f"[启动] 后台初始化失败: {ex}")
        return
    if _ort_session is not None:
        _batch_task = asyncio.create_task(_batch_loop())


@asynccontextmanager
async def _lifespan(app: FastAPI):
    asyncio.create_task(_background_init_and_batch())
    yield


app = FastAPI(title="寓瞳AI推理服务", version="0.1.0", lifespan=_lifespan)


@app.middleware("http")
async def _aura_ai_api_key_guard(request: Request, call_next):
    """若设置环境变量 AURA_API_KEY，则除根路径健康检查与 OpenAPI 文档外须携带请求头 X-Aura-Ai-Key。"""
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
            content={"code": 40101, "msg": "未授权访问AI服务"},
        )
    return await call_next(request)


COLLECTION_NAME = "aura_reid"
VECTOR_DIM = 512
_local_index = []
_index_lock = threading.Lock()
_arango_db = None
_arango_collection = None
_ort_session = None
_ort_input_name = ""
_model_error = ""
_arango_error = ""

# Batching relevant
_batch_queue = asyncio.Queue()
_BATCH_SIZE = 16
_MAX_WAIT_SECONDS = 0.05

def _init_arango():
    """初始化 ArangoDB 向量存储；失败则保持在内存降级模式。"""
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
    if "PLEASE_" in str(arango_user).upper() or "PLEASE_" in str(arango_password).upper():
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
        _arango_db = None
        _arango_collection = None
        _arango_error = str(ex)


def _ensure_arango():
    """惰性重连：避免容器编排先后导致永久降级。"""
    global _arango_db
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
        
        # 探测可用提供者，优先 CUDA
        available = ort.get_available_providers()
        providers = []
        if "CUDAExecutionProvider" in available:
            providers.append("CUDAExecutionProvider")
        providers.append("CPUExecutionProvider")
        
        _ort_session = ort.InferenceSession(str(model_path), providers=providers)
        _ort_input_name = _ort_session.get_inputs()[0].name
        _model_error = ""
        print(f"ONNX 推理会话已初始化，使用提供者：{providers}")
    except Exception as ex:
        _ort_session = None
        _model_error = f"模型加载失败: {ex}"


class ImageReq(BaseModel):
    image_base64: str
    metadata_json: str = "{}"


class ImageFileReq(BaseModel):
    image_path: str
    metadata_json: str = "{}"


async def _batch_loop():
    """推理批处理后台循环：搜集请求并统一调用 ONNX 运行。"""
    while True:
        # 等待至少一个任务进入队列
        item = await _batch_queue.get()
        batch = [item]
        
        # 尝试在短时间内搜集更多任务（最多 _BATCH_SIZE）
        start_time = asyncio.get_event_loop().time()
        while len(batch) < _BATCH_SIZE:
            time_left = _MAX_WAIT_SECONDS - (asyncio.get_event_loop().time() - start_time)
            if time_left <= 0:
                break
            try:
                # 尝试非阻塞获取
                item = await asyncio.wait_for(_batch_queue.get(), timeout=time_left)
                batch.append(item)
            except (asyncio.TimeoutError, asyncio.QueueEmpty):
                break
        
        if not batch:
            continue

        try:
            # 准备批处理张量
            tensors = [x[0] for x in batch]
            if len(tensors) == 1:
                input_data = tensors[0]
            else:
                input_data = np.concatenate(tensors, axis=0)
            
            # 执行推理 (同步调用，考虑在线程池执行以避免阻塞 Loop)
            loop = asyncio.get_event_loop()
            outputs = await loop.run_in_executor(None, lambda: _ort_session.run(None, {_ort_input_name: input_data}))
            
            # 分发结果
            feat_batch = np.asarray(outputs[0]).astype(np.float32)
            for i, (tensor, future) in enumerate(batch):
                feat = feat_batch[i].reshape(-1).tolist()
                future.set_result(_normalize_feature(feat))
        except Exception as ex:
            for _, future in batch:
                if not future.done():
                    future.set_exception(ex)


class SearchReq(BaseModel):
    feature: list[float]
    top_k: int = 10


class UpsertReq(BaseModel):
    vid: str
    feature: list[float]


@app.get("/")
def health():
    return {
        "code": 0,
        "msg": "AI服务运行正常",
        "time": datetime.now().isoformat(),
        "arangodb_enabled": _arango_db is not None,
        "arango_error": _arango_error,
        "model_loaded": _ort_session is not None,
        "model_error": _model_error,
    }


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
    """将推理请求发送到批处理队列。"""
    if _ort_session is None:
        raise RuntimeError(_model_error or "onnx session not initialized")
    
    future = asyncio.get_event_loop().create_future()
    await _batch_queue.put((tensor, future))
    return await future


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
        p = Path(req.image_path)
        if not p.exists():
            return {"code": 40401, "msg": f"文件不存在: {req.image_path}"}
        
        with Image.open(str(p)) as img:
            rgb = img.convert("RGB")
            tensor = _preprocess(rgb)
        
        feature = await _extract_feature_batched(tensor)
        return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
    except Exception as ex:
        return {"code": 50001, "msg": f"特征提取失败: {ex}", "data": {"feature": [], "dim": 0}}


def _cosine(a: list[float], b: list[float]) -> float:
    return sum(x * y for x, y in zip(a, b))


@app.post("/ai/upsert")
async def upsert(req: UpsertReq):
    feature = _normalize_feature(req.feature)
    if _ensure_arango():
        try:
            # 以 _key=vid 作为幂等写入主键
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
        except Exception:
            pass

    with _index_lock:
        global _local_index
        _local_index = [item for item in _local_index if item["vid"] != req.vid]
        _local_index.append({"vid": req.vid, "feature": feature})
    
    return {"code": 0, "msg": "写入成功", "data": {"vid": req.vid, "engine": "memory"}}


@app.post("/ai/search")
async def search(req: SearchReq):
    feature = _normalize_feature(req.feature)
    top_k = max(1, min(req.top_k, 50))
    if _ensure_arango():
        try:
            # 特征向量已归一化：cosine 相似度 = 向量点积
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
            hits = [{"vid": x["vid"], "score": float(x["score"])} for x in cursor]
            return {"code": 0, "msg": "检索成功", "data": hits}
        except Exception:
            pass

    with _index_lock:
        scores = [{"vid": x["vid"], "score": _cosine(feature, x["feature"])} for x in _local_index]
    
    scores.sort(key=lambda x: x["score"], reverse=True)
    return {
        "code": 0,
        "msg": "检索成功",
        "data": scores[:top_k],
    }


@app.post("/ai/cluster")
def cluster():
    return {"code": 0, "msg": "聚类任务已提交", "data": {"task_id": "cluster-demo-001"}}
