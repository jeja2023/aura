# 文件：AI 路由定义（api_routes.py） | File: AI route definitions
from pathlib import Path

from fastapi import APIRouter
from fastapi.responses import JSONResponse
from PIL import Image

from app.route_deps import RouteDeps
from models.schemas import ClusterReq, ImageFileReq, ImageReq, SearchReq, UpsertReq
from services.cluster_service import cluster_vectors, compute_cluster_cohesion
from vector_store.index_store import load_vectors_for_cluster, search_vectors, upsert_vector
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
    async def search(req: SearchReq):
        strict_mode = requires_persistent_index()
        return search_vectors(
            feature=req.feature,
            top_k=req.top_k,
            strict_mode=strict_mode,
            ensure_arango_func=deps.ensure_arango,
            get_arango_db_func=lambda: deps.arango.db,
            mark_arango_failure_func=deps.mark_arango_failure,
            get_arango_error_func=lambda: deps.arango.error,
            strict_unavailable_func=deps.strict_arango_unavailable,
            collection_name=deps.collection_name,
            normalize_feature_func=deps.normalize_feature,
            cosine_func=deps.cosine,
            logger=deps.logger,
            index_lock=deps.index_lock,
            local_index=deps.local_index,
        )

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
            cosine_func=cosine_func,
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
