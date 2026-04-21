# 文件：推理批处理服务（inference_service.py） | File: Inference batch service
import asyncio
import logging
import os
from pathlib import Path

import numpy as np
import onnxruntime as ort


class InferenceService:
    def __init__(
        self,
        *,
        normalize_feature_func,
        logger: logging.Logger,
        batch_size: int = 16,
        max_wait_seconds: float = 0.05,
    ):
        self._normalize_feature = normalize_feature_func
        self._logger = logger
        self._batch_size = batch_size
        self._max_wait_seconds = max_wait_seconds
        self._session = None
        self._input_name = ""
        self._model_error = ""
        self._queue = asyncio.Queue()
        self._batch_task: asyncio.Task | None = None

    @property
    def model_loaded(self) -> bool:
        return self._session is not None

    @property
    def model_error(self) -> str:
        return self._model_error

    def init_model(self) -> None:
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
        if self._session is not None and self._batch_task is None:
            self._batch_task = asyncio.create_task(self._batch_loop())

    async def extract_feature_batched(self, tensor: np.ndarray) -> list[float]:
        if self._session is None:
            raise RuntimeError(self._model_error or "onnx session not initialized")
        future = asyncio.get_running_loop().create_future()
        await self._queue.put((tensor, future))
        return await future

    async def _batch_loop(self) -> None:
        while True:
            item = await self._queue.get()
            batch = [item]
            start_time = asyncio.get_running_loop().time()

            while len(batch) < self._batch_size:
                time_left = self._max_wait_seconds - (asyncio.get_running_loop().time() - start_time)
                if time_left <= 0:
                    break
                try:
                    item = await asyncio.wait_for(self._queue.get(), timeout=time_left)
                    batch.append(item)
                except (asyncio.TimeoutError, asyncio.QueueEmpty):
                    break

            try:
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
                    future.set_result(self._normalize_feature(feature))
            except Exception as ex:
                for _tensor, future in batch:
                    if not future.done():
                        future.set_exception(ex)
