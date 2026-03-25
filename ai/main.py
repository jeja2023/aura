# 文件：AI推理服务入口（main.py） | File: AI Inference Service Entry
# pyright: reportMissingImports=false
from datetime import datetime
import base64
import io
import math
import os
from pathlib import Path

import numpy as np
import onnxruntime as ort
from fastapi import FastAPI
from PIL import Image
from pydantic import BaseModel

try:
    from arango import ArangoClient
except Exception:
    ArangoClient = None

app = FastAPI(title="寓瞳AI推理服务", version="0.1.0")
COLLECTION_NAME = "aura_reid"
VECTOR_DIM = 512
_local_index = []
_arango_db = None
_arango_collection = None
_ort_session = None
_ort_input_name = ""
_model_error = ""
_arango_error = ""


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
    # 默认不再“悄悄使用测试密码”，要求必须通过环境变量/ .env 明确配置
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


_init_arango()


def _init_model():
    global _ort_session, _ort_input_name, _model_error
    root = Path(__file__).resolve().parents[1]
    model_path = Path(os.getenv("AURA_MODEL_PATH", str(root / "models" / "osnet_ibn_x1_0.onnx")))
    try:
        if not model_path.exists():
            _model_error = f"model not found: {model_path}"
            return
        providers = ["CPUExecutionProvider"]
        _ort_session = ort.InferenceSession(str(model_path), providers=providers)
        _ort_input_name = _ort_session.get_inputs()[0].name
        _model_error = ""
    except Exception as ex:
        _ort_session = None
        _model_error = f"model load failed: {ex}"


_init_model()


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


def _extract_feature_by_onnx(image_base64: str) -> list[float]:
    if _ort_session is None:
        raise RuntimeError(_model_error or "onnx session not initialized")
    img = _decode_image(image_base64)
    tensor = _preprocess(img)
    outputs = _ort_session.run(None, {_ort_input_name: tensor})
    if not outputs:
        raise RuntimeError("onnx returned empty outputs")
    feat = np.asarray(outputs[0]).reshape(-1).astype(np.float32).tolist()
    return _normalize_feature(feat)


def _extract_feature_by_file_path(image_path: str) -> list[float]:
    if _ort_session is None:
        raise RuntimeError(_model_error or "onnx session not initialized")
    p = Path(image_path)
    if not p.exists():
        raise RuntimeError(f"image path not found: {image_path}")
    with Image.open(str(p)) as img:
        rgb = img.convert("RGB")
        tensor = _preprocess(rgb)
    outputs = _ort_session.run(None, {_ort_input_name: tensor})
    if not outputs:
        raise RuntimeError("onnx returned empty outputs")
    feat = np.asarray(outputs[0]).reshape(-1).astype(np.float32).tolist()
    return _normalize_feature(feat)


def _cosine(a: list[float], b: list[float]) -> float:
    return sum(x * y for x, y in zip(a, b))


@app.post("/ai/extract")
def extract(req: ImageReq):
    try:
        feature = _extract_feature_by_onnx(req.image_base64)
        return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
    except Exception as ex:
        return {"code": 50001, "msg": f"特征提取失败: {ex}", "data": {"feature": [], "dim": 0}}


@app.post("/ai/extract-file")
def extract_file(req: ImageFileReq):
    try:
        feature = _extract_feature_by_file_path(req.image_path)
        return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
    except Exception as ex:
        return {"code": 50001, "msg": f"特征提取失败: {ex}", "data": {"feature": [], "dim": 0}}


@app.post("/ai/upsert")
def upsert(req: UpsertReq):
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

    global _local_index
    _local_index = [item for item in _local_index if item["vid"] != req.vid]
    _local_index.append({"vid": req.vid, "feature": feature})
    return {"code": 0, "msg": "写入成功", "data": {"vid": req.vid, "engine": "memory"}}


@app.post("/ai/search")
def search(req: SearchReq):
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
