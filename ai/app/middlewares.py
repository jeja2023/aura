# 文件：应用中间件（middlewares.py） | File: App middlewares
import os
import uuid

from fastapi import Request
from fastapi.responses import JSONResponse


class RetrievalQuotaExceeded(Exception):
    """检索保护限流命中（429）。携带原始 reason 与 request_id，由统一异常处理器输出标准响应体。"""

    def __init__(self, reason: str, request_id: str = ""):
        super().__init__(reason)
        self.reason = reason
        self.request_id = request_id


def register_middlewares(app) -> None:
    @app.exception_handler(RetrievalQuotaExceeded)
    async def _retrieval_quota_handler(_request: Request, exc: RetrievalQuotaExceeded):
        return JSONResponse(
            status_code=429,
            content={"code": 42901, "msg": exc.reason, "request_id": exc.request_id},
        )

    @app.middleware("http")
    async def aura_ai_api_key_guard(request: Request, call_next):
        request_id = request.headers.get("X-Request-Id", "").strip() or str(uuid.uuid4())
        request.state.request_id = request_id
        expected = os.getenv("AURA_API_KEY", "").strip()
        if not expected:
            response = await call_next(request)
            response.headers["X-Request-Id"] = request_id
            return response

        if request.method in ("GET", "HEAD"):
            path = request.url.path
            if (
                path == "/"
                or path == "/live"
                or path == "/ready"
                or path == "/openapi.json"
                or path.startswith("/docs")
                or path.startswith("/redoc")
                or path == "/favicon.ico"
            ):
                response = await call_next(request)
                response.headers["X-Request-Id"] = request_id
                return response

        incoming = request.headers.get("X-Aura-Ai-Key", "")
        if incoming != expected:
            return JSONResponse(
                status_code=401,
                content={"code": 40101, "msg": "未授权访问 AI 服务", "request_id": request_id},
            )
        response = await call_next(request)
        response.headers["X-Request-Id"] = request_id
        return response
