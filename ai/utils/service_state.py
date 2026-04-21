# 文件：服务状态工具（service_state.py） | File: Service state helpers
import os
from datetime import datetime


def truthy(value: str | None) -> bool:
    if value is None:
        return False
    return value.strip().lower() in {"1", "true", "yes", "on"}


def current_environment() -> str:
    for key in ("AURA_ENV", "ASPNETCORE_ENVIRONMENT", "ENVIRONMENT", "FASTAPI_ENV"):
        value = os.getenv(key, "").strip()
        if value:
            return value
    return "Development"


def requires_persistent_index() -> bool:
    override = os.getenv("AURA_AI_REQUIRE_ARANGO", "").strip()
    if override:
        return truthy(override)
    return current_environment().lower() == "production"


def build_service_state(
    *,
    arango_enabled: bool,
    arango_error: str,
    model_loaded: bool,
    model_error: str,
) -> dict:
    return {
        "time": datetime.now().isoformat(),
        "environment": current_environment(),
        "arango_required": requires_persistent_index(),
        "arangodb_enabled": arango_enabled,
        "arango_error": arango_error,
        "model_loaded": model_loaded,
        "model_error": model_error,
    }
