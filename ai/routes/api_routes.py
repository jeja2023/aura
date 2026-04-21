# 文件：AI 路由定义（api_routes.py） | File: AI route definitions
from pathlib import Path
import time

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, PlainTextResponse
from PIL import Image

from app.route_deps import RouteDeps
from models.schemas import ClusterReq, ImageFileReq, ImageReq, SearchReq, UpsertReq
from services.cluster_service import cluster_vectors, compute_cluster_cohesion
from vector_store.index_store import load_vectors_for_cluster, search_vectors, upsert_vector
from utils.retrieval_config import build_retrieval_defaults, resolve_search_params
from utils.service_state import requires_persistent_index


def build_api_router(deps: RouteDeps) -> APIRouter:
    router = APIRouter()

    @router.get("/")
    def health():
        arango_enabled = deps.ensure_arango()
        payload = deps.service_state(arango_enabled=arango_enabled)
        if payload["arango_required"] and not arango_enabled:
            payload["code"] = 50301
            payload["msg"] = "ArangoDB 不可用，AI 服务处于受限状态"
            deps.logger.critical("健康检查发现 ArangoDB 不可用且当前环境要求持久化索引")
            return JSONResponse(status_code=503, content=payload)

        payload["code"] = 0
        payload["msg"] = "AI 服务运行正常"
        return payload

    @router.post("/ai/extract")
    async def extract(req: ImageReq):
        try:
            img = deps.decode_image(req.image_base64)
            tensor = deps.preprocess(img)
            feature = await deps.extract_feature_batched(tensor)
            return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
        except Exception as ex:
            return {"code": 50001, "msg": f"特征提取失败: {ex}", "data": {"feature": [], "dim": 0}}

    @router.post("/ai/extract-file")
    async def extract_file(req: ImageFileReq):
        try:
            path = Path(req.image_path)
            if not path.exists():
                return {"code": 40401, "msg": f"文件不存在: {req.image_path}"}

            with Image.open(str(path)) as img:
                rgb = img.convert("RGB")
                tensor = deps.preprocess(rgb)

            feature = await deps.extract_feature_batched(tensor)
            return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
        except Exception as ex:
            return {"code": 50001, "msg": f"特征提取失败: {ex}", "data": {"feature": [], "dim": 0}}

    @router.post("/ai/upsert")
    async def upsert(req: UpsertReq):
        strict_mode = requires_persistent_index()
        return upsert_vector(
            vid=req.vid,
            feature=req.feature,
            metadata=req.metadata,
            strict_mode=strict_mode,
            ensure_arango_func=deps.ensure_arango,
            get_arango_db_func=lambda: deps.arango.db,
            mark_arango_failure_func=deps.mark_arango_failure,
            get_arango_error_func=lambda: deps.arango.error,
            strict_unavailable_func=deps.strict_arango_unavailable,
            logger=deps.logger,
            collection_name=deps.collection_name,
            normalize_feature_func=deps.normalize_feature,
            index_lock=deps.index_lock,
            local_index=deps.local_index,
        )

    @router.post("/ai/search")
    async def search(req: SearchReq, request: Request):
        request_id = getattr(request.state, "request_id", "")
        allowed, reason = deps.retrieval_guard.allow_request()
        if not allowed:
            return JSONResponse(status_code=429, content={"code": 42901, "msg": reason, "request_id": request_id})
        strict_mode = requires_persistent_index()
        defaults = build_retrieval_defaults()
        resolved, warnings = resolve_search_params(req, defaults)
        begin = time.perf_counter()
        result = search_vectors(
            feature=req.feature,
            top_k=req.top_k,
            min_score=resolved["min_score"],
            candidate_multiplier=resolved["candidate_multiplier"],
            candidate_pool=resolved["candidate_pool"],
            ann_probe=resolved["ann_probe"],
            rerank_window=resolved["rerank_window"],
            include_vids=req.include_vids,
            exclude_vids=req.exclude_vids,
            metadata_filter=req.metadata_filter,
            explain=req.explain,
            strict_mode=strict_mode,
            ensure_arango_func=deps.ensure_arango,
            get_arango_db_func=lambda: deps.arango.db,
            mark_arango_failure_func=deps.mark_arango_failure,
            get_arango_error_func=lambda: deps.arango.error,
            strict_unavailable_func=deps.strict_arango_unavailable,
            collection_name=deps.collection_name,
            vector_dim=deps.vector_dim,
            normalize_feature_func=deps.normalize_feature,
            cosine_func=deps.cosine,
            logger=deps.logger,
            index_lock=deps.index_lock,
            local_index=deps.local_index,
        )
        elapsed_ms = (time.perf_counter() - begin) * 1000.0
        if isinstance(result, JSONResponse):
            deps.retrieval_guard.record_result(success=False)
            if warnings:
                deps.logger.warning("检索参数已自动纠正: %s", "; ".join(warnings))
            deps.index_runtime.record_search(
                success=False,
                hit_count=0,
                latency_ms=elapsed_ms,
                engine="unavailable",
                strategy="none",
                filters_applied=bool(req.include_vids or req.exclude_vids or req.metadata_filter),
                request_id=request_id,
                status="failed",
                reason="向量索引不可用或检索失败",
                warnings=warnings,
            )
            deps.logger.warning("检索失败 request_id=%s", request_id)
            return result
        hits = result.get("data", [])
        meta = result.setdefault("meta", {})
        meta["request_id"] = request_id
        deps.retrieval_guard.record_result(success=True)
        deps.index_runtime.record_search(
            success=True,
            hit_count=len(hits),
            latency_ms=elapsed_ms,
            engine=str(meta.get("engine", "unknown")),
            strategy=str(meta.get("strategy", "unknown")),
            filters_applied=bool(meta.get("filters_applied", False)),
            request_id=request_id,
            status="success" if len(hits) > 0 else "empty",
            reason="",
            warnings=warnings,
        )
        meta["latency_ms"] = round(elapsed_ms, 3)
        if warnings:
            meta["warnings"] = warnings
            deps.logger.warning("检索参数已自动纠正: %s", "; ".join(warnings))
        deps.logger.info(
            "检索完成 request_id=%s engine=%s strategy=%s hits=%s latency_ms=%.3f",
            request_id,
            meta.get("engine", "unknown"),
            meta.get("strategy", "unknown"),
            len(hits),
            elapsed_ms,
        )
        return result

    @router.get("/ai/search-stats")
    async def search_stats(window_minutes: int = 0):
        data = deps.search_metrics()
        if window_minutes > 0:
            data["window"] = deps.index_runtime.get_search_metrics_window(window_minutes=window_minutes)
        return {"code": 0, "msg": "检索指标查询成功", "data": data}

    @router.get("/ai/search-metrics")
    async def search_metrics():
        metrics_text = deps.index_runtime.build_prometheus_metrics()
        return PlainTextResponse(content=metrics_text, media_type="text/plain; version=0.0.4")

    @router.get("/ai/search-audit-logs")
    async def search_audit_logs(limit: int = 100):
        data = deps.search_audit_logs(limit=limit)
        return {"code": 0, "msg": "检索审计日志查询成功", "data": data}

    @router.post("/ai/cluster")
    async def cluster(req: ClusterReq):
        strict_mode = requires_persistent_index()
        loaded = load_vectors_for_cluster(
            max_vectors=req.max_vectors,
            strict_mode=strict_mode,
            ensure_arango_func=deps.ensure_arango,
            get_arango_db_func=lambda: deps.arango.db,
            mark_arango_failure_func=deps.mark_arango_failure,
            get_arango_error_func=lambda: deps.arango.error,
            strict_unavailable_func=deps.strict_arango_unavailable,
            logger=deps.logger,
            collection_name=deps.collection_name,
            normalize_feature_func=deps.normalize_feature,
            index_lock=deps.index_lock,
            local_index=deps.local_index,
        )
        if isinstance(loaded, JSONResponse):
            return loaded

        engine, items = loaded
        if not items:
            return {
                "code": 0,
                "msg": "聚类完成",
                "data": {
                    "engine": engine,
                    "algorithm": "feature-dbscan",
                    "candidates": 0,
                    "clusters": 0,
                    "noise": 0,
                    "groups": [],
                },
            }

        clusters, noise_indexes = cluster_vectors(
            items,
            similarity_threshold=req.similarity_threshold,
            min_points=req.min_points,
            cosine_func=deps.cosine,
        )

        groups = []
        for idx, members in enumerate(clusters, start=1):
            vids = [items[m]["vid"] for m in members]
            groups.append(
                {
                    "cluster_index": idx,
                    "size": len(members),
                    "cohesion_score": compute_cluster_cohesion(
                        members,
                        items,
                        vector_dim=deps.vector_dim,
                        normalize_func=deps.normalize_feature,
                        cosine_func=deps.cosine,
                    ),
                    "members": vids,
                }
            )

        return {
            "code": 0,
            "msg": "聚类完成",
            "data": {
                "engine": engine,
                "algorithm": "feature-dbscan",
                "candidates": len(items),
                "clusters": len(groups),
                "noise": len(noise_indexes),
                "similarity_threshold": max(0.5, min(req.similarity_threshold, 0.99)),
                "min_points": max(1, req.min_points),
                "groups": groups,
            },
        }

    return router
