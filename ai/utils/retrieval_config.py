# 文件：检索配置工具（retrieval_config.py） | File: Retrieval config helpers
import os


def _read_int(name: str, default: int, *, min_value: int, max_value: int) -> int:
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
    except Exception:
        return default
    return max(min_value, min(value, max_value))


def _read_float(name: str, default: float, *, min_value: float, max_value: float) -> float:
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        value = float(raw)
    except Exception:
        return default
    return max(min_value, min(value, max_value))


def build_retrieval_defaults() -> dict:
    return {
        "min_score": _read_float("AURA_AI_MIN_SCORE_DEFAULT", -1.0, min_value=-1.0, max_value=1.0),
        "candidate_multiplier": _read_int("AURA_AI_CANDIDATE_MULTIPLIER_DEFAULT", 8, min_value=1, max_value=30),
        "candidate_pool": _read_int("AURA_AI_CANDIDATE_POOL_DEFAULT", 0, min_value=0, max_value=5000),
        "ann_probe": _read_int("AURA_AI_ANN_PROBE_DEFAULT", 16, min_value=4, max_value=64),
        "rerank_window": _read_int("AURA_AI_RERANK_WINDOW_DEFAULT", 30, min_value=1, max_value=200),
    }


def resolve_search_params(req, defaults: dict) -> tuple[dict, list[str]]:
    warnings = []

    if req.min_score < -1.0 or req.min_score > 1.0:
        warnings.append("min_score 超出范围[-1,1]，已自动裁剪")
    min_score = max(-1.0, min(req.min_score, 1.0))
    if req.min_score == -1.0:
        min_score = defaults["min_score"]

    if req.candidate_multiplier <= 0:
        warnings.append("candidate_multiplier 非正数，已使用默认配置")
        candidate_multiplier = defaults["candidate_multiplier"]
    else:
        candidate_multiplier = max(1, min(req.candidate_multiplier, 30))
        if candidate_multiplier != req.candidate_multiplier:
            warnings.append("candidate_multiplier 超出范围[1,30]，已自动裁剪")

    if req.candidate_pool < 0:
        warnings.append("candidate_pool 不可为负数，已使用默认配置")
        candidate_pool = defaults["candidate_pool"]
    elif req.candidate_pool == 0:
        candidate_pool = defaults["candidate_pool"]
    else:
        candidate_pool = max(0, min(req.candidate_pool, 5000))
        if candidate_pool != req.candidate_pool:
            warnings.append("candidate_pool 超出范围[0,5000]，已自动裁剪")

    if req.ann_probe <= 0:
        warnings.append("ann_probe 非正数，已使用默认配置")
        ann_probe = defaults["ann_probe"]
    else:
        ann_probe = max(4, min(req.ann_probe, 64))
        if ann_probe != req.ann_probe:
            warnings.append("ann_probe 超出范围[4,64]，已自动裁剪")

    if req.rerank_window <= 0:
        warnings.append("rerank_window 非正数，已使用默认配置")
        rerank_window = defaults["rerank_window"]
    else:
        rerank_window = max(1, min(req.rerank_window, 200))
        if rerank_window != req.rerank_window:
            warnings.append("rerank_window 超出范围[1,200]，已自动裁剪")

    return (
        {
            "min_score": min_score,
            "candidate_multiplier": candidate_multiplier,
            "candidate_pool": candidate_pool,
            "ann_probe": ann_probe,
            "rerank_window": rerank_window,
        },
        warnings,
    )
