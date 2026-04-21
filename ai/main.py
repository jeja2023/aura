# 文件：AI 服务入口（main.py） | File: AI service entrypoint
import logging

from app.bootstrap import build_runtime
from app.middlewares import register_middlewares
from app.lifespan import build_lifespan
from fastapi import FastAPI
from routes.api_routes import build_api_router


logger = logging.getLogger("aura.ai")

COLLECTION_NAME = "aura_reid"
VECTOR_DIM = 512

_deps, _arango, _inference, _index_runtime = build_runtime(
    logger=logger,
    collection_name=COLLECTION_NAME,
    vector_dim=VECTOR_DIM,
)

def create_app() -> FastAPI:
    app = FastAPI(
        title="Aura AI 推理服务",
        version="0.1.0",
        lifespan=build_lifespan(
            arango=_arango,
            inference=_inference,
            logger=logger,
            deps=_deps,
            index_runtime=_index_runtime,
        ),
    )
    register_middlewares(app)
    app.include_router(build_api_router(_deps))
    return app


app = create_app()
