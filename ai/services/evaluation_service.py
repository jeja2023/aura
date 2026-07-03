import json
import threading
import time
from copy import deepcopy
from statistics import mean
from typing import Any

from vector_store.index_store import search_vectors, upsert_vector


def _as_list(value: Any) -> list:
    return value if isinstance(value, list) else []


def _normalize_vids(values: Any) -> list[str]:
    result = []
    seen = set()
    for value in _as_list(values):
        vid = str(value).strip()
        if vid and vid not in seen:
            seen.add(vid)
            result.append(vid)
    return result


def _normalize_metadata(value: Any) -> dict:
    if not isinstance(value, dict):
        return {}
    result = {}
    for key, item in value.items():
        k = str(key).strip()
        if not k:
            continue
        if isinstance(item, (str, int, float, bool)):
            result[k] = item
    return result


def _safe_float_list(value: Any) -> list[float]:
    if not isinstance(value, list):
        return []
    result = []
    for item in value:
        try:
            result.append(float(item))
        except Exception:
            return []
    return result


def _extract_relevant_vids(query: dict) -> list[str]:
    if "relevant_vids" in query:
        return _normalize_vids(query.get("relevant_vids"))
    if "expected_vids" in query:
        return _normalize_vids(query.get("expected_vids"))
    if "positive_vids" in query:
        return _normalize_vids(query.get("positive_vids"))
    vid = str(query.get("vid", "")).strip()
    return [vid] if vid else []


def _build_empty_result(reason: str, *, query_count: int = 0, gallery_count: int = 0) -> dict:
    return {
        "summary": {
            "query_count": query_count,
            "gallery_count": gallery_count,
            "skipped_gallery": 0,
            "success_count": 0,
            "empty_count": 0,
            "failed_count": query_count,
            "recall_at_k": 0.0,
            "precision_at_k": 0.0,
            "mrr": 0.0,
            "hit_rate_at_k": 0.0,
            "avg_latency_ms": 0.0,
            "empty_rate": 0.0,
            "failure_rate": 1.0 if query_count else 0.0,
        },
        "items": [],
        "reason": reason,
    }


def evaluate_retrieval_dataset(
    dataset: dict,
    *,
    top_k: int,
    min_score: float,
    candidate_multiplier: int,
    candidate_pool: int,
    ann_probe: int,
    rerank_window: int,
    normalize_feature_func,
    cosine_func,
    vector_dim: int,
    logger,
) -> dict:
    gallery_rows = _as_list(dataset.get("gallery"))
    query_rows = _as_list(dataset.get("queries"))
    safe_top_k = max(1, min(int(top_k or 10), 50))
    local_index: list[dict] = []
    index_lock = threading.RLock()
    skipped_gallery = 0

    for row in gallery_rows:
        if not isinstance(row, dict):
            skipped_gallery += 1
            continue
        vid = str(row.get("vid", "")).strip()
        feature = _safe_float_list(row.get("feature"))
        if not vid or not feature:
            skipped_gallery += 1
            continue
        upsert_vector(
            vid=vid,
            feature=feature,
            metadata=_normalize_metadata(row.get("metadata")),
            strict_mode=False,
            ensure_arango_func=lambda: False,
            get_arango_db_func=lambda: None,
            mark_arango_failure_func=lambda _ex: None,
            get_arango_error_func=lambda: "",
            strict_unavailable_func=lambda message, data=None: (_ for _ in ()).throw(RuntimeError(message)),
            logger=logger,
            collection_name="offline_eval",
            normalize_feature_func=normalize_feature_func,
            index_lock=index_lock,
            local_index=local_index,
        )

    if not query_rows:
        result = _build_empty_result("dataset_missing_queries", gallery_count=len(local_index))
        result["summary"]["skipped_gallery"] = skipped_gallery
        return result
    if not local_index:
        result = _build_empty_result("dataset_missing_valid_gallery", query_count=len(query_rows), gallery_count=0)
        result["summary"]["skipped_gallery"] = skipped_gallery
        return result

    items = []
    success_count = 0
    empty_count = 0
    failed_count = 0
    recalls = []
    precisions = []
    reciprocal_ranks = []
    latencies = []

    for query_index, row in enumerate(query_rows):
        if not isinstance(row, dict):
            failed_count += 1
            items.append({"query_index": query_index, "status": "failed", "reason": "query_not_object"})
            continue

        feature = _safe_float_list(row.get("feature"))
        relevant_vids = _extract_relevant_vids(row)
        if not feature or not relevant_vids:
            failed_count += 1
            items.append(
                {
                    "query_id": row.get("query_id", query_index),
                    "query_index": query_index,
                    "status": "failed",
                    "reason": "missing_feature_or_relevant_vids",
                }
            )
            continue

        begin = time.perf_counter()
        result = search_vectors(
            feature=feature,
            top_k=safe_top_k,
            min_score=min_score,
            candidate_multiplier=candidate_multiplier,
            candidate_pool=candidate_pool,
            ann_probe=ann_probe,
            rerank_window=rerank_window,
            include_vids=None,
            exclude_vids=None,
            metadata_filter=_normalize_metadata(row.get("metadata_filter")),
            explain=False,
            strict_mode=False,
            ensure_arango_func=lambda: False,
            get_arango_db_func=lambda: None,
            mark_arango_failure_func=lambda _ex: None,
            get_arango_error_func=lambda: "",
            strict_unavailable_func=lambda message, data=None: (_ for _ in ()).throw(RuntimeError(message)),
            collection_name="offline_eval",
            vector_dim=vector_dim,
            normalize_feature_func=normalize_feature_func,
            cosine_func=cosine_func,
            logger=logger,
            index_lock=index_lock,
            local_index=local_index,
        )
        latency_ms = (time.perf_counter() - begin) * 1000.0
        latencies.append(latency_ms)

        hits = result.get("data", []) if isinstance(result, dict) else []
        hit_vids = [str(item.get("vid", "")).strip() for item in hits if isinstance(item, dict)]
        relevant_set = set(relevant_vids)
        matched = [vid for vid in hit_vids if vid in relevant_set]
        unique_matched = set(matched)
        recall = len(unique_matched) / len(relevant_set) if relevant_set else 0.0
        precision = len(matched) / safe_top_k
        rank = next((idx + 1 for idx, vid in enumerate(hit_vids) if vid in relevant_set), 0)
        reciprocal_rank = (1.0 / rank) if rank else 0.0

        success_count += 1
        if not hit_vids:
            empty_count += 1
        recalls.append(recall)
        precisions.append(precision)
        reciprocal_ranks.append(reciprocal_rank)
        items.append(
            {
                "query_id": row.get("query_id", query_index),
                "query_index": query_index,
                "status": "empty" if not hit_vids else "success",
                "relevant_vids": relevant_vids,
                "hit_vids": hit_vids,
                "matched_vids": matched,
                "recall_at_k": round(recall, 6),
                "precision_at_k": round(precision, 6),
                "reciprocal_rank": round(reciprocal_rank, 6),
                "latency_ms": round(latency_ms, 3),
                "engine": result.get("meta", {}).get("engine", "unknown") if isinstance(result, dict) else "unknown",
                "strategy": result.get("meta", {}).get("strategy", "unknown") if isinstance(result, dict) else "unknown",
            }
        )

    query_count = len(query_rows)
    return {
        "summary": {
            "query_count": query_count,
            "gallery_count": len(local_index),
            "skipped_gallery": skipped_gallery,
            "success_count": success_count,
            "empty_count": empty_count,
            "failed_count": failed_count,
            "recall_at_k": round(mean(recalls), 6) if recalls else 0.0,
            "precision_at_k": round(mean(precisions), 6) if precisions else 0.0,
            "mrr": round(mean(reciprocal_ranks), 6) if reciprocal_ranks else 0.0,
            "hit_rate_at_k": round(sum(1 for rank in reciprocal_ranks if rank > 0) / success_count, 6)
            if success_count
            else 0.0,
            "avg_latency_ms": round(mean(latencies), 3) if latencies else 0.0,
            "empty_rate": round(empty_count / query_count, 6) if query_count else 0.0,
            "failure_rate": round(failed_count / query_count, 6) if query_count else 0.0,
            "top_k": safe_top_k,
        },
        "items": items,
    }


def load_eval_dataset(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        payload = json.load(f)
    if not isinstance(payload, dict):
        raise ValueError("evaluation dataset must be a JSON object")
    return deepcopy(payload)
