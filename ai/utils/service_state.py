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


def include_diagnostics() -> bool:
    override = os.getenv("AURA_AI_HEALTH_VERBOSE", "").strip()
    if override:
        return truthy(override)
    return current_environment().lower() != "production"


def build_service_state(
    *,
    arango_enabled: bool,
    arango_error: str,
    model_loaded: bool,
    model_error: str,
    inference_ready: bool | None = None,
    inference_error: str = "",
) -> dict:
    verbose = include_diagnostics()
    ready = model_loaded if inference_ready is None else inference_ready
    return {
        "time": datetime.now().isoformat(),
        "environment": current_environment(),
        "arango_required": requires_persistent_index(),
        "arangodb_enabled": arango_enabled,
        "arango_error": arango_error if verbose else "",
        "model_loaded": model_loaded,
        "inference_ready": ready,
        "model_error": model_error if verbose else "",
        "inference_error": inference_error if verbose else "",
        "diagnostics_visible": verbose,
    }
