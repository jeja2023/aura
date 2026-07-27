# 文件：AI 服务入口（main.py）
import logging

from app.bootstrap import build_runtime
from app.middlewares import register_middlewares
from app.security import is_production, validate_production_security
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
    validate_production_security()
    production = is_production()

    app = FastAPI(
        title="Aura AI 推理服务",
        version="0.4.0",
        docs_url=None if production else "/docs",
        redoc_url=None if production else "/redoc",
        openapi_url=None if production else "/openapi.json",
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
