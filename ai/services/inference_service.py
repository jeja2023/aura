# 文件：推理批处理服务（inference_service.py） | File: Inference batch service
import asyncio
import json
import logging
import os
import threading
import time
from pathlib import Path
from urllib.parse import urlparse
import urllib.request

import numpy as np
import onnxruntime as ort


class InferenceBackpressureError(RuntimeError):
    pass


class InferenceUnavailableError(RuntimeError):
    pass


class InferenceService:
    def __init__(
        self,
        *,
        normalize_feature_func,
        logger: logging.Logger,
        batch_size: int = 16,
        max_wait_seconds: float = 0.05,
        max_queue_size: int = 256,
        enqueue_timeout_seconds: float = 0.2,
    ):
        self._normalize_feature = normalize_feature_func
        self._logger = logger
        self._batch_size = batch_size
        self._max_wait_seconds = max_wait_seconds
        self._max_queue_size = max(1, max_queue_size)
        self._enqueue_timeout_seconds = max(0.01, enqueue_timeout_seconds)
        self._session = None
        self._input_name = ""
        self._model_error = ""
        self._remote_enabled = False
        self._remote_mode = ""
        self._remote_predict_pool: list[dict[str, object]] = []
        self._remote_extract_pool: list[dict[str, object]] = []
        self._remote_project_name = ""
        self._remote_model_name = ""
        self._remote_api_token = ""
        self._remote_timeout_seconds = 10.0
        self._remote_breaker_fail_threshold = 8
        self._remote_breaker_open_seconds = 20
        self._remote_predict_cursor = -1
        self._remote_extract_cursor = -1
        self._queue = asyncio.Queue(maxsize=self._max_queue_size)
        self._batch_task: asyncio.Task | None = None
        self._metrics_lock = threading.Lock()
        self._enqueue_total = 0
        self._backpressure_total = 0
        self._batch_total = 0
        self._batch_item_total = 0
        self._batch_error_total = 0
        self._last_batch_size = 0
        self._last_batch_latency_ms = 0.0
        self._last_backpressure_at = 0.0

    @property
    def model_loaded(self) -> bool:
        return self._remote_enabled or self._session is not None

    @property
    def is_ready(self) -> bool:
        if self._remote_enabled:
            return self._has_healthy_remote_endpoint()
        return self._session is not None

    @property
    def readiness_error(self) -> str:
        if self._remote_enabled:
            if self._has_healthy_remote_endpoint():
                return ""
            if self._remote_mode == "image-extract":
                return self._model_error or "外部图片特征服务节点均处于熔断状态"
            if self._remote_mode == "gpu-worker":
                return self._model_error or "GPU worker 节点均处于熔断状态"
            return self._model_error or "远程推理节点不可用"
        if self._session is None:
            return self._model_error or "onnx session not initialized"
        return ""

    @property
    def model_error(self) -> str:
        return self._model_error

    @property
    def backend(self) -> str:
        if self._remote_mode == "image-extract":
            return "external-image"
        if self._remote_mode == "gpu-worker":
            return "gpu-worker"
        return "onnx"

    @property
    def accepts_raw_image(self) -> bool:
        return self._remote_mode == "image-extract"

    def init_model(self) -> None:
        if self._init_remote_model():
            return

        root = Path(__file__).resolve().parents[2]
        model_path = Path(os.getenv("AURA_MODEL_PATH", str(root / "models" / "osnet_ibn_x1_0.onnx")))
        try:
            if not model_path.exists():
                self._model_error = f"未找到模型文件: {model_path}"
                return

            available = ort.get_available_providers()
            providers = []
            if "CUDAExecutionProvider" in available:
                providers.append("CUDAExecutionProvider")
            providers.append("CPUExecutionProvider")

            self._session = ort.InferenceSession(str(model_path), providers=providers)
            self._input_name = self._session.get_inputs()[0].name
            self._model_error = ""
            self._logger.info("ONNX 推理会话已初始化，providers=%s", providers)
        except Exception as ex:
            self._session = None
            self._model_error = f"模型加载失败: {ex}"

    async def start_batch_loop(self) -> None:
        if self._remote_enabled:
            return
        if self._session is not None and self._batch_task is None:
            self._batch_task = asyncio.create_task(self._batch_loop())

    async def stop_batch_loop(self) -> None:
        if self._batch_task is None:
            return
        task = self._batch_task
        self._batch_task = None
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass

    async def extract_feature_batched(self, tensor: np.ndarray) -> list[float]:
        if self._remote_enabled:
            return await self._extract_feature_remote(tensor)

        if self._session is None:
            raise InferenceUnavailableError(self._model_error or "onnx session not initialized")
        future = asyncio.get_running_loop().create_future()
        try:
            await asyncio.wait_for(
                self._queue.put((tensor, future)),
                timeout=self._enqueue_timeout_seconds,
            )
        except asyncio.TimeoutError as ex:
            self._record_backpressure()
            raise InferenceBackpressureError(
                f"推理队列繁忙，请稍后重试（队列上限={self._max_queue_size}）"
            ) from ex
        self._record_enqueue()
        return await future

    async def extract_feature_from_base64(self, image_base64: str, metadata_json: str = "{}") -> list[float]:
        if not self.accepts_raw_image:
            raise RuntimeError("external image extract endpoint is not configured")
        return await self._extract_feature_remote_image(
            {
                "image_base64": image_base64,
                "metadata_json": metadata_json,
            }
        )

    async def extract_feature_from_file(self, image_path: str, metadata_json: str = "{}") -> list[float]:
        if not self.accepts_raw_image:
            raise RuntimeError("external image extract endpoint is not configured")
        return await self._extract_feature_remote_image(
            {
                "image_path": image_path,
                "metadata_json": metadata_json,
            }
        )

    @property
    def queue_max_size(self) -> int:
        return self._max_queue_size

    @property
    def queue_size(self) -> int:
        return self._queue.qsize()

    def inference_metrics(self) -> dict:
        current_size = self.queue_size
        with self._metrics_lock:
            enqueue_total = self._enqueue_total
            backpressure_total = self._backpressure_total
            batch_total = self._batch_total
            batch_item_total = self._batch_item_total
            batch_error_total = self._batch_error_total
            last_batch_size = self._last_batch_size
            last_batch_latency_ms = self._last_batch_latency_ms
            last_backpressure_at = self._last_backpressure_at

        return {
            "backend": self.backend,
            "queue": {
                "max_size": self._max_queue_size,
                "current_size": current_size,
                "remaining": max(0, self._max_queue_size - current_size),
                "enqueue_total": enqueue_total,
                "backpressure_total": backpressure_total,
                "last_backpressure_at": last_backpressure_at,
            },
            "batch": {
                "batch_size": self._batch_size,
                "max_wait_seconds": self._max_wait_seconds,
                "processed_batches_total": batch_total,
                "processed_items_total": batch_item_total,
                "failed_batches_total": batch_error_total,
                "last_batch_size": last_batch_size,
                "last_batch_latency_ms": round(last_batch_latency_ms, 3),
                "avg_batch_size": round(batch_item_total / batch_total, 3) if batch_total else 0.0,
            },
        }

    def _record_enqueue(self) -> None:
        with self._metrics_lock:
            self._enqueue_total += 1

    def _record_backpressure(self) -> None:
        with self._metrics_lock:
            self._backpressure_total += 1
            self._last_backpressure_at = time.time()

    def _record_batch_result(self, *, batch_size: int, latency_ms: float, success: bool) -> None:
        with self._metrics_lock:
            if success:
                self._batch_total += 1
                self._batch_item_total += max(0, batch_size)
                self._last_batch_size = max(0, batch_size)
                self._last_batch_latency_ms = max(0.0, latency_ms)
            else:
                self._batch_error_total += 1

    def _init_remote_model(self) -> bool:
        if self._init_remote_extract_model():
            return True

        raw_urls = os.getenv("AURA_GPU_PREDICT_URLS", os.getenv("AURA_GPU_PREDICT_URL", "")).strip()
        if not raw_urls:
            return False

        try:
            pool = [self._build_remote_endpoint(item, self._normalize_predict_url) for item in self._split_config_values(raw_urls)]
        except ValueError as ex:
            self._remote_enabled = False
            self._model_error = str(ex)
            return True

        project_name = os.getenv("AURA_GPU_PROJECT_NAME", "").strip()
        model_name = os.getenv("AURA_GPU_MODEL_NAME", "").strip()
        if not pool or not project_name or not model_name:
            self._remote_enabled = False
            self._model_error = "GPU 推理已配置但缺少 AURA_GPU_PREDICT_URLS、AURA_GPU_PROJECT_NAME 或 AURA_GPU_MODEL_NAME"
            return True

        self._remote_predict_pool = pool
        self._remote_project_name = project_name
        self._remote_model_name = model_name
        self._remote_api_token = os.getenv("AURA_GPU_API_TOKEN", "").strip()
        self._remote_timeout_seconds = self._parse_remote_timeout()
        self._remote_breaker_fail_threshold = self._read_int_env(
            "AURA_AI_BREAKER_FAIL_THRESHOLD",
            default=8,
            min_value=1,
            max_value=100,
        )
        self._remote_breaker_open_seconds = self._read_int_env(
            "AURA_AI_BREAKER_OPEN_SECONDS",
            default=20,
            min_value=1,
            max_value=300,
        )
        self._remote_enabled = True
        self._remote_mode = "gpu-worker"
        self._session = None
        self._input_name = ""
        self._model_error = ""
        self._logger.info(
            "GPU worker 推理已启用，endpoints=%s weight=%s project=%s model=%s",
            len(self._remote_predict_pool),
            self._remote_total_weight(self._remote_predict_pool),
            self._remote_project_name,
            self._remote_model_name,
        )
        return True

    def _init_remote_extract_model(self) -> bool:
        raw_urls = os.getenv("AURA_EXTERNAL_EXTRACT_URLS", os.getenv("AURA_EXTERNAL_EXTRACT_URL", "")).strip()
        if not raw_urls:
            return False

        try:
            pool = [self._build_remote_endpoint(item, self._normalize_extract_url) for item in self._split_config_values(raw_urls)]
        except ValueError as ex:
            self._remote_enabled = False
            self._remote_mode = "image-extract"
            self._model_error = str(ex)
            return True

        if not pool:
            self._remote_enabled = False
            self._remote_mode = "image-extract"
            self._model_error = "外部图片特征服务已配置但缺少 AURA_EXTERNAL_EXTRACT_URLS"
            return True

        self._remote_extract_pool = pool
        self._remote_project_name = os.getenv("AURA_EXTERNAL_PROJECT_NAME", os.getenv("AURA_GPU_PROJECT_NAME", "")).strip()
        self._remote_model_name = os.getenv("AURA_EXTERNAL_MODEL_NAME", os.getenv("AURA_GPU_MODEL_NAME", "")).strip()
        self._remote_api_token = os.getenv("AURA_EXTERNAL_API_TOKEN", os.getenv("AURA_GPU_API_TOKEN", "")).strip()
        self._remote_timeout_seconds = self._parse_remote_timeout("AURA_EXTERNAL_TIMEOUT_SECONDS")
        self._remote_breaker_fail_threshold = self._read_int_env(
            "AURA_AI_BREAKER_FAIL_THRESHOLD",
            default=8,
            min_value=1,
            max_value=100,
        )
        self._remote_breaker_open_seconds = self._read_int_env(
            "AURA_AI_BREAKER_OPEN_SECONDS",
            default=20,
            min_value=1,
            max_value=300,
        )
        self._remote_enabled = True
        self._remote_mode = "image-extract"
        self._session = None
        self._input_name = ""
        self._model_error = ""
        self._logger.info(
            "外部图片特征服务已启用，endpoints=%s weight=%s project=%s model=%s",
            len(self._remote_extract_pool),
            self._remote_total_weight(self._remote_extract_pool),
            self._remote_project_name or "-",
            self._remote_model_name or "-",
        )
        return True

    async def _extract_feature_remote(self, tensor: np.ndarray) -> list[float]:
        return await self._call_remote_pool(
            pool=self._remote_predict_pool,
            cursor_attr="_remote_predict_cursor",
            label="GPU worker",
            payload_factory=lambda _endpoint: {
                "project_name": self._remote_project_name,
                "model_name": self._remote_model_name,
                "tensor_data": tensor.astype(np.float32, copy=False).tolist(),
            },
        )

    async def _extract_feature_remote_image(self, payload: dict) -> list[float]:
        return await self._call_remote_pool(
            pool=self._remote_extract_pool,
            cursor_attr="_remote_extract_cursor",
            label="外部图片特征服务",
            payload_factory=lambda _endpoint: self._build_remote_extract_payload(payload),
        )

    def _post_remote_extract(self, url: str, payload: dict) -> list[float]:
        return self._post_json_for_feature(url, self._build_remote_extract_payload(payload))

    def _build_remote_extract_payload(self, payload: dict) -> dict:
        body = dict(payload)
        if self._remote_project_name:
            body.setdefault("project_name", self._remote_project_name)
        if self._remote_model_name:
            body.setdefault("model_name", self._remote_model_name)
        return body

    def _post_remote_predict(self, url: str, tensor: np.ndarray) -> list[float]:
        payload = {
            "project_name": self._remote_project_name,
            "model_name": self._remote_model_name,
            "tensor_data": tensor.astype(np.float32, copy=False).tolist(),
        }
        return self._post_json_for_feature(url, payload)

    async def _call_remote_pool(
        self,
        *,
        pool: list[dict[str, object]],
        cursor_attr: str,
        label: str,
        payload_factory,
    ) -> list[float]:
        if not pool:
            raise InferenceUnavailableError(self.readiness_error or f"{label} endpoint is not configured")

        attempted: set[str] = set()
        last_error: Exception | None = None
        loop = asyncio.get_running_loop()

        while len(attempted) < len(pool):
            endpoint = self._pick_remote_endpoint(pool, cursor_attr, attempted)
            if endpoint is None:
                break

            url = str(endpoint.get("url", "")).strip()
            payload = payload_factory(url)
            try:
                feature = await loop.run_in_executor(
                    None,
                    lambda endpoint_url=url, body=payload: self._post_json_for_feature(endpoint_url, body),
                )
                self._record_remote_success(endpoint)
                return self._normalize_feature(feature)
            except Exception as ex:
                last_error = ex
                attempted.add(url)
                self._record_remote_failure(endpoint)
                if len(attempted) < len(pool):
                    self._logger.warning("%s 调用失败，切换到下一个节点。endpoint=%s error=%s", label, url, ex)

        if last_error is None:
            raise InferenceUnavailableError(self.readiness_error or f"{label} 节点不可用")
        raise InferenceUnavailableError(f"{label} 调用失败: {last_error}") from last_error

    def _pick_remote_endpoint(
        self,
        pool: list[dict[str, object]],
        cursor_attr: str,
        attempted: set[str],
    ) -> dict[str, object] | None:
        now = time.time()
        healthy: list[dict[str, object]] = []
        for endpoint in pool:
            url = str(endpoint.get("url", "")).strip()
            if not url or url in attempted:
                continue
            open_until = float(endpoint.get("open_until", 0.0) or 0.0)
            if open_until > now:
                continue
            healthy.append(endpoint)

        if not healthy:
            return None

        weighted: list[dict[str, object]] = []
        for endpoint in healthy:
            weight = max(1, int(endpoint.get("weight", 1) or 1))
            weighted.extend([endpoint] * weight)

        if not weighted:
            return None

        cursor = getattr(self, cursor_attr)
        cursor = (cursor + 1) % len(weighted)
        setattr(self, cursor_attr, cursor)
        return weighted[cursor]

    def _record_remote_success(self, endpoint: dict[str, object]) -> None:
        endpoint["failures"] = 0
        endpoint["open_until"] = 0.0

    def _record_remote_failure(self, endpoint: dict[str, object]) -> None:
        failures = int(endpoint.get("failures", 0) or 0) + 1
        endpoint["failures"] = failures
        if failures >= self._remote_breaker_fail_threshold:
            endpoint["failures"] = 0
            endpoint["open_until"] = time.time() + self._remote_breaker_open_seconds

    def _has_healthy_remote_endpoint(self) -> bool:
        pool = self._remote_active_pool()
        if not pool:
            return False
        now = time.time()
        return any(float(endpoint.get("open_until", 0.0) or 0.0) <= now for endpoint in pool)

    def _remote_active_pool(self) -> list[dict[str, object]]:
        if self._remote_mode == "image-extract":
            return self._remote_extract_pool
        if self._remote_mode == "gpu-worker":
            return self._remote_predict_pool
        return []

    @staticmethod
    def _remote_total_weight(pool: list[dict[str, object]]) -> int:
        total = 0
        for endpoint in pool:
            try:
                total += max(1, int(endpoint.get("weight", 1) or 1))
            except Exception:
                total += 1
        return total

    def remote_status(self) -> dict:
        pool = self._remote_active_pool()
        now = time.time()
        nodes = []
        healthy_nodes = 0
        open_nodes = 0
        for endpoint in pool:
            url = str(endpoint.get("url", "")).strip()
            weight = max(1, int(endpoint.get("weight", 1) or 1))
            failures = max(0, int(endpoint.get("failures", 0) or 0))
            open_until = float(endpoint.get("open_until", 0.0) or 0.0)
            is_open = open_until > now
            if is_open:
                open_nodes += 1
            else:
                healthy_nodes += 1
            nodes.append(
                {
                    "url": url,
                    "weight": weight,
                    "failures": failures,
                    "is_open": is_open,
                    "open_until": open_until if is_open else 0.0,
                }
            )
        return {
            "mode": self._remote_mode or "onnx",
            "configured": bool(pool),
            "healthy_nodes": healthy_nodes,
            "open_nodes": open_nodes,
            "total_weight": self._remote_total_weight(pool),
            "fail_threshold": self._remote_breaker_fail_threshold,
            "open_seconds": self._remote_breaker_open_seconds,
            "supports_raw_image": self.accepts_raw_image,
            "ready": self.is_ready,
            "nodes": nodes,
        }

    def _post_json_for_feature(self, url: str, payload: dict) -> list[float]:
        headers = {"Content-Type": "application/json"}
        if self._remote_api_token:
            headers["Authorization"] = f"Bearer {self._remote_api_token}"

        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(url, data=body, headers=headers, method="POST")
        with urllib.request.urlopen(request, timeout=self._remote_timeout_seconds) as response:
            response_body = response.read().decode("utf-8")

        if not response_body:
            raise RuntimeError("GPU worker 返回空响应")

        try:
            decoded = json.loads(response_body)
        except json.JSONDecodeError as ex:
            raise RuntimeError("GPU worker 返回的响应不是合法 JSON") from ex

        return self._extract_feature_from_payload(decoded)

    @classmethod
    def _extract_feature_from_payload(cls, payload) -> list[float]:
        if isinstance(payload, dict):
            code = payload.get("code")
            if code not in (None, 0, "0"):
                message = payload.get("msg") or payload.get("message") or payload.get("error") or "unknown error"
                raise RuntimeError(f"GPU worker 返回失败 code={code}: {message}")

            for key in ("feature", "features", "embedding", "embeddings", "output", "outputs", "result", "prediction", "predictions"):
                if key in payload:
                    try:
                        feature = cls._coerce_feature_vector(payload[key])
                    except RuntimeError:
                        feature = []
                    if feature:
                        return feature

            if "data" in payload:
                try:
                    feature = cls._extract_feature_from_payload(payload["data"])
                except RuntimeError:
                    feature = []
                if feature:
                    return feature

        if not isinstance(payload, dict):
            feature = cls._coerce_feature_vector(payload)
            if feature:
                return feature

        raise RuntimeError("GPU worker 响应中未找到特征向量")

    @classmethod
    def _coerce_feature_vector(cls, value) -> list[float]:
        if isinstance(value, dict):
            return cls._extract_feature_from_payload(value)

        if not isinstance(value, list) or not value:
            return []

        if all(isinstance(item, (int, float)) and not isinstance(item, bool) for item in value):
            return [float(item) for item in value]

        for item in value:
            try:
                feature = cls._coerce_feature_vector(item)
            except RuntimeError:
                feature = []
            if feature:
                return feature

        return []

    @staticmethod
    def _split_config_values(value: str) -> list[str]:
        return [item.strip() for item in value.replace("\r", "\n").replace(",", ";").replace("\n", ";").split(";") if item.strip()]

    @staticmethod
    def _build_remote_endpoint(raw: str, normalizer) -> dict[str, object]:
        endpoint_value, weight = InferenceService._split_endpoint_weight(raw)
        url = normalizer(endpoint_value)
        return {
            "url": url,
            "weight": weight,
            "failures": 0,
            "open_until": 0.0,
        }

    @staticmethod
    def _split_endpoint_weight(raw: str) -> tuple[str, int]:
        value = raw.strip()
        weight = 1
        if "|" in value:
            endpoint_value, weight_value = value.rsplit("|", 1)
            endpoint_value = endpoint_value.strip()
            weight_value = weight_value.strip()
            if not endpoint_value:
                raise ValueError("remote endpoint is empty")
            if weight_value:
                try:
                    weight = int(weight_value)
                except ValueError as ex:
                    raise ValueError(f"节点权重无效: {raw}") from ex
            value = endpoint_value
        if weight <= 0:
            raise ValueError(f"节点权重必须大于 0: {raw}")
        return value, min(weight, 100)

    @staticmethod
    def _normalize_predict_url(raw: str) -> str:
        value = raw.strip().rstrip("/")
        parsed = urlparse(value)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise ValueError(f"GPU worker 地址无效: {raw}")
        if parsed.path in {"", "/"}:
            value = f"{value}/predict"
        return value

    @staticmethod
    def _normalize_extract_url(raw: str) -> str:
        value = raw.strip().rstrip("/")
        parsed = urlparse(value)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise ValueError(f"外部图片特征服务地址无效: {raw}")
        if parsed.path in {"", "/"}:
            value = f"{value}/ai/extract"
        return value

    @staticmethod
    def _parse_remote_timeout(name: str = "AURA_GPU_TIMEOUT_SECONDS") -> float:
        raw = os.getenv(name, os.getenv("AURA_GPU_TIMEOUT_SECONDS", "10")).strip()
        try:
            return max(0.1, float(raw))
        except ValueError:
            return 10.0

    @staticmethod
    def _read_int_env(name: str, *, default: int, min_value: int, max_value: int) -> int:
        raw = os.getenv(name, "").strip()
        if not raw:
            return default
        try:
            value = int(raw)
        except ValueError:
            return default
        return max(min_value, min(value, max_value))

    async def _batch_loop(self) -> None:
        try:
            while True:
                item = await self._queue.get()
                batch = [item]
                start_time = asyncio.get_running_loop().time()

                try:
                    while len(batch) < self._batch_size:
                        time_left = self._max_wait_seconds - (asyncio.get_running_loop().time() - start_time)
                        if time_left <= 0:
                            break
                        try:
                            item = await asyncio.wait_for(self._queue.get(), timeout=time_left)
                            batch.append(item)
                        except (asyncio.TimeoutError, asyncio.QueueEmpty):
                            break

                    tensors = [tensor for tensor, _future in batch]
                    input_data = tensors[0] if len(tensors) == 1 else np.concatenate(tensors, axis=0)
                    loop = asyncio.get_running_loop()
                    outputs = await loop.run_in_executor(
                        None,
                        lambda: self._session.run(None, {self._input_name: input_data}),
                    )

                    feat_batch = np.asarray(outputs[0]).astype(np.float32)
                    latency_ms = (asyncio.get_running_loop().time() - start_time) * 1000.0
                    self._record_batch_result(batch_size=len(batch), latency_ms=latency_ms, success=True)
                    for index, (_tensor, future) in enumerate(batch):
                        feature = feat_batch[index].reshape(-1).tolist()
                        if not future.done():
                            future.set_result(self._normalize_feature(feature))
                except Exception as ex:
                    latency_ms = (asyncio.get_running_loop().time() - start_time) * 1000.0
                    self._record_batch_result(batch_size=len(batch), latency_ms=latency_ms, success=False)
                    for _tensor, future in batch:
                        if not future.done():
                            future.set_exception(ex)
        except asyncio.CancelledError:
            while not self._queue.empty():
                _tensor, future = self._queue.get_nowait()
                if not future.done():
                    future.set_exception(RuntimeError("推理服务正在关闭"))
            raise
