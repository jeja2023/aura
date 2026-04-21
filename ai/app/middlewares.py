# 文件：应用中间件（middlewares.py） | File: App middlewares
import os

from fastapi import Request
from fastapi.responses import JSONResponse


def register_middlewares(app) -> None:
    @app.middleware("http")
    async def aura_ai_api_key_guard(request: Request, call_next):
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
