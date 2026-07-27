import os

from utils.service_state import current_environment


def is_production() -> bool:
    return current_environment().lower() == "production"


def validate_production_security() -> None:
    if not is_production():
        return

    api_key = os.getenv("AURA_API_KEY", "").strip()
    if (
        not api_key
        or "replace" in api_key.lower()
        or "please" in api_key.lower()
        or len(api_key) < 24
    ):
        raise RuntimeError("AURA_API_KEY must be a non-placeholder secret of at least 24 characters in production")
