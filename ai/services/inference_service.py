# 文件：推理批处理服务（inference_service.py） | File: Inference batch service
import asyncio
import json
import logging
import os
from pathlib import Path
from urllib.parse import urlparse
import urllib.request

import numpy as np
import onnxruntime as ort


class InferenceBackpressureError(RuntimeError):
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
        self._remote_predict_urls: list[str] = []
        self._remote_project_name = ""
        self._remote_model_name = ""
        self._remote_api_token = ""
        self._remote_timeout_seconds = 10.0
        self._remote_next_endpoint_index = -1
        self._queue = asyncio.Queue(maxsize=self._max_queue_size)
        self._batch_task: asyncio.Task | None = None

    @property
    def model_loaded(self) -> bool:
        return self._remote_enabled or self._session is not None

    @property
    def model_error(self) -> str:
        return self._model_error

    @property
    def backend(self) -> str:
        return "gpu-worker" if self._remote_enabled else "onnx"

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
            raise RuntimeError(self._model_error or "onnx session not initialized")
        future = asyncio.get_running_loop().create_future()
        try:
            await asyncio.wait_for(
                self._queue.put((tensor, future)),
                timeout=self._enqueue_timeout_seconds,
            )
        except asyncio.TimeoutError as ex:
            raise InferenceBackpressureError(
                f"推理队列繁忙，请稍后重试（队列上限={self._max_queue_size}）"
            ) from ex
        return await future

    @property
    def queue_max_size(self) -> int:
        return self._max_queue_size

    @property
    def queue_size(self) -> int:
        return self._queue.qsize()

    def _init_remote_model(self) -> bool:
        raw_urls = os.getenv("AURA_GPU_PREDICT_URLS", os.getenv("AURA_GPU_PREDICT_URL", "")).strip()
        if not raw_urls:
            return False

        try:
            urls = [self._normalize_predict_url(item) for item in self._split_config_values(raw_urls)]
        except ValueError as ex:
            self._remote_enabled = False
            self._model_error = str(ex)
            return True

        project_name = os.getenv("AURA_GPU_PROJECT_NAME", "").strip()
        model_name = os.getenv("AURA_GPU_MODEL_NAME", "").strip()
        if not urls or not project_name or not model_name:
            self._remote_enabled = False
            self._model_error = "GPU 推理已配置但缺少 AURA_GPU_PREDICT_URLS、AURA_GPU_PROJECT_NAME 或 AURA_GPU_MODEL_NAME"
            return True

        self._remote_predict_urls = urls
        self._remote_project_name = project_name
        self._remote_model_name = model_name
        self._remote_api_token = os.getenv("AURA_GPU_API_TOKEN", "").strip()
        self._remote_timeout_seconds = self._parse_remote_timeout()
        self._remote_enabled = True
        self._session = None
        self._input_name = ""
        self._model_error = ""
        self._logger.info(
            "GPU worker 推理已启用，endpoints=%s project=%s model=%s",
            len(self._remote_predict_urls),
            self._remote_project_name,
            self._remote_model_name,
        )
        return True

    async def _extract_feature_remote(self, tensor: np.ndarray) -> list[float]:
        if not self._remote_predict_urls:
            raise RuntimeError(self._model_error or "GPU worker endpoint is not configured")

        loop = asyncio.get_running_loop()
        start = (self._remote_next_endpoint_index + 1) % len(self._remote_predict_urls)
        self._remote_next_endpoint_index = start
        last_error: Exception | None = None

        for offset in range(len(self._remote_predict_urls)):
            url = self._remote_predict_urls[(start + offset) % len(self._remote_predict_urls)]
            try:
                feature = await loop.run_in_executor(
                    None,
                    lambda endpoint=url: self._post_remote_predict(endpoint, tensor),
                )
                return self._normalize_feature(feature)
            except Exception as ex:
                last_error = ex
                if offset + 1 < len(self._remote_predict_urls):
                    self._logger.warning("GPU worker 调用失败，切换到下一个节点。endpoint=%s error=%s", url, ex)

        raise RuntimeError(f"GPU worker 推理调用失败: {last_error}")

    def _post_remote_predict(self, url: str, tensor: np.ndarray) -> list[float]:
        payload = {
            "project_name": self._remote_project_name,
            "model_name": self._remote_model_name,
            "tensor_data": tensor.astype(np.float32, copy=False).tolist(),
        }
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
    def _normalize_predict_url(raw: str) -> str:
        value = raw.strip().rstrip("/")
        parsed = urlparse(value)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise ValueError(f"GPU worker 地址无效: {raw}")
        if parsed.path in {"", "/"}:
            value = f"{value}/predict"
        return value

    @staticmethod
    def _parse_remote_timeout() -> float:
        raw = os.getenv("AURA_GPU_TIMEOUT_SECONDS", "10").strip()
        try:
            return max(0.1, float(raw))
        except ValueError:
            return 10.0

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
                    for index, (_tensor, future) in enumerate(batch):
                        feature = feat_batch[index].reshape(-1).tolist()
                        if not future.done():
                            future.set_result(self._normalize_feature(feature))
                except Exception as ex:
                    for _tensor, future in batch:
                        if not future.done():
                            future.set_exception(ex)
        except asyncio.CancelledError:
            while not self._queue.empty():
                _tensor, future = self._queue.get_nowait()
                if not future.done():
                    future.set_exception(RuntimeError("推理服务正在关闭"))
            raise
