# 文件：向量索引存取（index_store.py） | File: Vector index store
from fastapi.responses import JSONResponse


def _sanitize_metadata(metadata: dict | None) -> dict:
    if not isinstance(metadata, dict):
        return {}
    result = {}
    for key, value in metadata.items():
        k = str(key).strip()
        if not k:
            continue
        if isinstance(value, (str, int, float, bool)):
            result[k] = value
    return result


def _passes_filters(
    *,
    item: dict,
    include_set: set[str],
    exclude_set: set[str],
    metadata_filter: dict,
) -> bool:
    vid = str(item.get("vid", ""))
    if include_set and vid not in include_set:
        return False
    if exclude_set and vid in exclude_set:
        return False
    if metadata_filter:
        source = item.get("metadata") or {}
        for key, value in metadata_filter.items():
            if source.get(key) != value:
                return False
    return True


def _build_ann_bucket_key(vector: list[float], probe: int) -> tuple[int, ...]:
    p = max(1, min(probe, len(vector)))
    return tuple(1 if vector[i] >= 0 else 0 for i in range(p))


def _neighbor_keys(base_key: tuple[int, ...]) -> list[tuple[int, ...]]:
    keys = [base_key]
    for i, bit in enumerate(base_key):
        mutable = list(base_key)
        mutable[i] = 0 if bit else 1
        keys.append(tuple(mutable))
    return keys


def _bucket_key_text(vector: list[float], probe: int) -> str:
    key = _build_ann_bucket_key(vector, probe)
    return "".join("1" if x else "0" for x in key)


def _bucket_neighbor_texts(vector: list[float], probe: int) -> list[str]:
    base_key = _build_ann_bucket_key(vector, probe)
    return ["".join("1" if x else "0" for x in key) for key in _neighbor_keys(base_key)]


def _select_bucket_probe(*, candidate_pool: int, top_k: int) -> int:
    ratio = candidate_pool / max(1, top_k)
    if ratio >= 18:
        return 8
    if ratio >= 10:
        return 16
    return 24


def _fast_approx_score(query: list[float], target: list[float]) -> float:
    if not query or not target:
        return 0.0
    step = 4
    return sum(query[i] * target[i] for i in range(0, min(len(query), len(target)), step))


def _adaptive_candidate_pool(*, top_k: int, multiplier: int, candidate_pool: int, corpus_size: int) -> tuple[int, int]:
    final_multiplier = max(1, min(multiplier, 30))
    base = top_k * final_multiplier
    if corpus_size > 0:
        if corpus_size <= 1000:
            base = max(base, top_k * 10)
        elif corpus_size <= 10000:
            base = max(base, top_k * 14)
        else:
            base = max(base, top_k * 20)
    pool = candidate_pool if candidate_pool > 0 else base
    pool = max(top_k, min(pool, 5000))
    return final_multiplier, pool


def upsert_vector(
    *,
    vid: str,
    feature: list[float],
    metadata: dict | None,
    strict_mode: bool,
    ensure_arango_func,
    get_arango_db_func,
    mark_arango_failure_func,
    get_arango_error_func,
    strict_unavailable_func,
    logger,
    collection_name: str,
    normalize_feature_func,
    index_lock,
    local_index: list[dict],
) -> dict | JSONResponse:
    normalized_feature = normalize_feature_func(feature)
    safe_metadata = _sanitize_metadata(metadata)
    ann_bucket_8 = _bucket_key_text(normalized_feature, 8)
    ann_bucket_16 = _bucket_key_text(normalized_feature, 16)
    ann_bucket_24 = _bucket_key_text(normalized_feature, 24)

    if ensure_arango_func():
        try:
            arango_db = get_arango_db_func()
            arango_db.aql.execute(
                """
                UPSERT { _key: @vid }
                INSERT { _key: @vid, vid: @vid, feature: @feature, metadata: @metadata, ann_bucket_8: @ann_bucket_8, ann_bucket_16: @ann_bucket_16, ann_bucket_24: @ann_bucket_24 }
                UPDATE { vid: @vid, feature: @feature, metadata: @metadata, ann_bucket_8: @ann_bucket_8, ann_bucket_16: @ann_bucket_16, ann_bucket_24: @ann_bucket_24 }
                IN @@col
                """,
                bind_vars={
                    "@col": collection_name,
                    "vid": vid,
                    "feature": normalized_feature,
                    "metadata": safe_metadata,
                    "ann_bucket_8": ann_bucket_8,
                    "ann_bucket_16": ann_bucket_16,
                    "ann_bucket_24": ann_bucket_24,
                },
            )
            return {"code": 0, "msg": "写入成功", "data": {"vid": vid, "engine": "arangodb"}}
        except Exception as ex:
            mark_arango_failure_func(ex)
            if strict_mode:
                return strict_unavailable_func(
                    "ArangoDB 不可用，已拒绝向内存索引降级写入",
                    data={"vid": vid, "engine": "unavailable"},
                )
            logger.warning("ArangoDB 写入失败，降级到内存索引. vid=%s error=%s", vid, get_arango_error_func())
    elif strict_mode:
        return strict_unavailable_func(
            "ArangoDB 不可用，已拒绝向内存索引降级写入",
            data={"vid": vid, "engine": "unavailable"},
        )

    with index_lock:
        local_index[:] = [item for item in local_index if item["vid"] != vid]
        local_index.append(
            {
                "vid": vid,
                "feature": normalized_feature,
                "metadata": safe_metadata,
                "ann_bucket_8": ann_bucket_8,
                "ann_bucket_16": ann_bucket_16,
                "ann_bucket_24": ann_bucket_24,
            }
        )

    return {"code": 0, "msg": "写入成功", "data": {"vid": vid, "engine": "memory"}}


def search_vectors(
    *,
    feature: list[float],
    top_k: int,
    min_score: float,
    candidate_multiplier: int,
    candidate_pool: int,
    ann_probe: int,
    rerank_window: int,
    include_vids: list[str] | None,
    exclude_vids: list[str] | None,
    metadata_filter: dict | None,
    explain: bool,
    strict_mode: bool,
    ensure_arango_func,
    get_arango_db_func,
    mark_arango_failure_func,
    get_arango_error_func,
    strict_unavailable_func,
    collection_name: str,
    vector_dim: int,
    normalize_feature_func,
    cosine_func,
    logger,
    index_lock,
    local_index: list[dict],
) -> dict | JSONResponse:
    normalized_feature = normalize_feature_func(feature)
    limited_top_k = max(1, min(top_k, 50))
    final_min_score = max(-1.0, min(min_score, 1.0))
    include_set = {str(v).strip() for v in (include_vids or []) if str(v).strip()}
    exclude_set = {str(v).strip() for v in (exclude_vids or []) if str(v).strip()}
    metadata_eq = _sanitize_metadata(metadata_filter)
    final_candidate_multiplier, final_candidate_pool = _adaptive_candidate_pool(
        top_k=limited_top_k,
        multiplier=candidate_multiplier,
        candidate_pool=candidate_pool,
        corpus_size=0,
    )
    final_ann_probe = max(4, min(ann_probe, 64))
    final_rerank_window = max(limited_top_k, min(rerank_window, 200))

    if ensure_arango_func():
        try:
            arango_db = get_arango_db_func()
            last_i = max(0, vector_dim - 1)
            aql = """
                FOR d IN @@col
                  FILTER HAS(d, "vid") AND HAS(d, "feature")
            """
            bind_vars = {
                "@col": collection_name,
                "feature": normalized_feature,
                "k": limited_top_k,
                "candidate_pool": final_candidate_pool,
                "last_i": last_i,
                "min_score": final_min_score,
            }

            if include_set:
                aql += "\n  FILTER d.vid IN @include_vids"
                bind_vars["include_vids"] = list(include_set)
            if exclude_set:
                aql += "\n  FILTER d.vid NOT IN @exclude_vids"
                bind_vars["exclude_vids"] = list(exclude_set)
            selected_probe = _select_bucket_probe(candidate_pool=final_candidate_pool, top_k=limited_top_k)
            bucket_field = f"ann_bucket_{selected_probe}"
            bucket_candidates = _bucket_neighbor_texts(normalized_feature, selected_probe)
            aql += f"\n  FILTER HAS(d, '{bucket_field}') AND d.{bucket_field} IN @bucket_candidates"
            bind_vars["bucket_candidates"] = bucket_candidates
            for idx, (key, value) in enumerate(metadata_eq.items()):
                key_name = f"meta_key_{idx}"
                val_name = f"meta_val_{idx}"
                aql += f"\n  FILTER HAS(d, 'metadata') AND d.metadata[@{key_name}] == @{val_name}"
                bind_vars[key_name] = key
                bind_vars[val_name] = value

            aql += """
                  LET score = SUM(
                    FOR i IN 0..@last_i
                      RETURN d.feature[i] * @feature[i]
                  )
                  FILTER score >= @min_score
                  SORT score DESC
                  LIMIT @candidate_pool
                  RETURN { vid: d.vid, score: score }
            """
            cursor = arango_db.aql.execute(
                aql,
                bind_vars=bind_vars,
            )
            hits = [{"vid": item["vid"], "score": float(item["score"])} for item in cursor][:limited_top_k]
            result = {
                "code": 0,
                "msg": "检索成功",
                "data": hits,
                "meta": {
                    "engine": "arangodb",
                    "strategy": "exact-cosine",
                    "candidate_pool": final_candidate_pool,
                    "candidate_multiplier": final_candidate_multiplier,
                    "ann_prefilter": f"bucket{selected_probe}-neighbors",
                    "filters_applied": bool(include_set or exclude_set or metadata_eq),
                },
            }
            if explain:
                result["explain"] = {
                    "phase": ["prefilter:ann_bucket_16", "score:exact-cosine", "sort:desc", "cut:top_k"],
                    "candidate_pool": final_candidate_pool,
                    "top_k": limited_top_k,
                    "bucket_neighbors": len(bucket_candidates),
                    "bucket_probe": selected_probe,
                    "returned": len(hits),
                }
            return result
        except Exception as ex:
            mark_arango_failure_func(ex)
            if strict_mode:
                return strict_unavailable_func(
                    "ArangoDB 不可用，已拒绝降级到内存索引检索",
                    data=[],
                )
            logger.warning("ArangoDB 检索失败，降级到内存索引. error=%s", get_arango_error_func())
    elif strict_mode:
        return strict_unavailable_func(
            "ArangoDB 不可用，已拒绝降级到内存索引检索",
            data=[],
        )

    with index_lock:
        snapshot = list(local_index)

    filtered = [
        item
        for item in snapshot
        if _passes_filters(
            item=item,
            include_set=include_set,
            exclude_set=exclude_set,
            metadata_filter=metadata_eq,
        )
    ]
    if not filtered:
        result = {
            "code": 0,
            "msg": "检索成功",
            "data": [],
            "meta": {"engine": "memory", "strategy": "ann-rerank", "candidates": 0, "rerank_size": 0},
        }
        if explain:
            result["explain"] = {
                "phase": ["filter", "ann-bucket-recall", "approx-score", "exact-rerank"],
                "filtered": 0,
                "candidates": 0,
                "rerank_size": 0,
                "returned": 0,
            }
        return result

    final_candidate_multiplier, final_candidate_pool = _adaptive_candidate_pool(
        top_k=limited_top_k,
        multiplier=candidate_multiplier,
        candidate_pool=candidate_pool,
        corpus_size=len(filtered),
    )

    bucket_map = {}
    for item in filtered:
        key = _build_ann_bucket_key(item["feature"], final_ann_probe)
        bucket_map.setdefault(key, []).append(item)

    query_key = _build_ann_bucket_key(normalized_feature, final_ann_probe)
    candidates = []
    for key in _neighbor_keys(query_key):
        candidates.extend(bucket_map.get(key, []))
        if len(candidates) >= final_candidate_pool:
            break

    if len(candidates) < limited_top_k:
        candidates = filtered

    approx_scores = [
        {"vid": item["vid"], "feature": item["feature"], "score_approx": _fast_approx_score(normalized_feature, item["feature"])}
        for item in candidates
    ]
    approx_scores.sort(key=lambda item: item["score_approx"], reverse=True)
    rerank_items = approx_scores[: min(final_rerank_window, len(approx_scores))]

    reranked = []
    for item in rerank_items:
        score = cosine_func(normalized_feature, item["feature"])
        if score < final_min_score:
            continue
        reranked.append({"vid": item["vid"], "score": score})
    reranked.sort(key=lambda item: item["score"], reverse=True)

    result = {
        "code": 0,
        "msg": "检索成功",
        "data": reranked[:limited_top_k],
        "meta": {
            "engine": "memory",
            "strategy": "ann-rerank",
            "candidates": len(candidates),
            "rerank_size": len(rerank_items),
            "candidate_pool": final_candidate_pool,
            "candidate_multiplier": final_candidate_multiplier,
            "filters_applied": bool(include_set or exclude_set or metadata_eq),
        },
    }
    if explain:
        result["explain"] = {
            "phase": ["filter", "ann-bucket-recall", "approx-score", "exact-rerank"],
            "filtered": len(filtered),
            "candidates": len(candidates),
            "rerank_size": len(rerank_items),
            "returned": min(limited_top_k, len(reranked)),
            "ann_probe": final_ann_probe,
        }
    return result


def load_vectors_for_cluster(
    *,
    max_vectors: int,
    strict_mode: bool,
    ensure_arango_func,
    get_arango_db_func,
    mark_arango_failure_func,
    get_arango_error_func,
    strict_unavailable_func,
    logger,
    collection_name: str,
    normalize_feature_func,
    index_lock,
    local_index: list[dict],
) -> tuple[str, list[dict]] | JSONResponse:
    limit = max(1, min(max_vectors, 2000))
    if ensure_arango_func():
        try:
            arango_db = get_arango_db_func()
            cursor = arango_db.aql.execute(
                """
                FOR d IN @@col
                  FILTER HAS(d, "vid") AND HAS(d, "feature")
                  LIMIT @limit
                  RETURN { vid: d.vid, feature: d.feature, metadata: d.metadata, ann_bucket_8: d.ann_bucket_8, ann_bucket_16: d.ann_bucket_16, ann_bucket_24: d.ann_bucket_24 }
                """,
                bind_vars={
                    "@col": collection_name,
                    "limit": limit,
                },
            )
            items = []
            for item in cursor:
                vid = str(item.get("vid", "")).strip()
                feature = item.get("feature")
                if not vid or not isinstance(feature, list):
                    continue
                items.append(
                    {
                        "vid": vid,
                        "feature": normalize_feature_func(feature),
                        "metadata": _sanitize_metadata(item.get("metadata")),
                        "ann_bucket_8": str(item.get("ann_bucket_8", "")).strip(),
                        "ann_bucket_16": str(item.get("ann_bucket_16", "")).strip(),
                        "ann_bucket_24": str(item.get("ann_bucket_24", "")).strip(),
                    }
                )
            return "arangodb", items
        except Exception as ex:
            mark_arango_failure_func(ex)
            if strict_mode:
                return strict_unavailable_func(
                    "ArangoDB 不可用，已拒绝降级到内存索引聚类",
                    data=[],
                )
            logger.warning("ArangoDB 聚类数据加载失败，降级到内存索引. error=%s", get_arango_error_func())
    elif strict_mode:
        return strict_unavailable_func(
            "ArangoDB 不可用，已拒绝降级到内存索引聚类",
            data=[],
        )

    with index_lock:
        items = [
            {
                "vid": item["vid"],
                "feature": item["feature"],
                "metadata": _sanitize_metadata(item.get("metadata")),
                "ann_bucket_8": str(item.get("ann_bucket_8", "")).strip(),
                "ann_bucket_16": str(item.get("ann_bucket_16", "")).strip(),
                "ann_bucket_24": str(item.get("ann_bucket_24", "")).strip(),
            }
            for item in local_index[:limit]
        ]
    return "memory", items
