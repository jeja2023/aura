# 文件：向量索引存取（index_store.py） | File: Vector index store
from fastapi.responses import JSONResponse


def upsert_vector(
    *,
    vid: str,
    feature: list[float],
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

    if ensure_arango_func():
        try:
            arango_db = get_arango_db_func()
            arango_db.aql.execute(
                """
                UPSERT { _key: @vid }
                INSERT { _key: @vid, vid: @vid, feature: @feature }
                UPDATE { vid: @vid, feature: @feature }
                IN @@col
                """,
                bind_vars={
                    "@col": collection_name,
                    "vid": vid,
                    "feature": normalized_feature,
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
        local_index.append({"vid": vid, "feature": normalized_feature})

    return {"code": 0, "msg": "写入成功", "data": {"vid": vid, "engine": "memory"}}


def search_vectors(
    *,
    feature: list[float],
    top_k: int,
    strict_mode: bool,
    ensure_arango_func,
    get_arango_db_func,
    mark_arango_failure_func,
    get_arango_error_func,
    strict_unavailable_func,
    collection_name: str,
    normalize_feature_func,
    cosine_func,
    logger,
    index_lock,
    local_index: list[dict],
) -> dict | JSONResponse:
    normalized_feature = normalize_feature_func(feature)
    limited_top_k = max(1, min(top_k, 50))

    if ensure_arango_func():
        try:
            arango_db = get_arango_db_func()
            cursor = arango_db.aql.execute(
                """
                FOR d IN @@col
                  LET score = SUM(
                    FOR i IN 0..511
                      RETURN d.feature[i] * @feature[i]
                  )
                  SORT score DESC
                  LIMIT @k
                  RETURN { vid: d.vid, score: score }
                """,
                bind_vars={
                    "@col": collection_name,
                    "feature": normalized_feature,
                    "k": limited_top_k,
                },
            )
            hits = [{"vid": item["vid"], "score": float(item["score"])} for item in cursor]
            return {"code": 0, "msg": "检索成功", "data": hits}
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
        scores = [{"vid": item["vid"], "score": cosine_func(normalized_feature, item["feature"])} for item in local_index]

    scores.sort(key=lambda item: item["score"], reverse=True)
    return {"code": 0, "msg": "检索成功", "data": scores[:limited_top_k]}


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
                  RETURN { vid: d.vid, feature: d.feature }
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
                items.append({"vid": vid, "feature": normalize_feature_func(feature)})
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
        items = [{"vid": item["vid"], "feature": item["feature"]} for item in local_index[:limit]]
    return "memory", items
