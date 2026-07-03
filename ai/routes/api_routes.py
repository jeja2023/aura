# 文件：AI 路由定义（api_routes.py）
from pathlib import Path
import os
import time

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse, PlainTextResponse

from app.middlewares import RetrievalQuotaExceeded
from app.route_deps import RouteDeps
from models.schemas import ClusterReq, EvalReq, ImageFileReq, ImageReq, SearchReq, UpsertReq
from services.cluster_service import cluster_vectors, compute_cluster_cohesion
from services.evaluation_service import evaluate_retrieval_dataset, load_eval_dataset
from services.inference_service import InferenceBackpressureError, InferenceUnavailableError
from vector_store.index_store import load_vectors_for_cluster, search_vectors, upsert_vector
from utils.retrieval_config import build_retrieval_defaults, resolve_search_params
from utils.service_state import requires_persistent_index
from utils.vector_utils import ImageTooLargeError, ImageValidationError, validate_image_base64_length


def build_api_router(deps: RouteDeps) -> APIRouter:
    router = APIRouter()

    def _build_health_payload() -> tuple[int, dict]:
        arango_enabled = deps.ensure_arango()
        payload = deps.service_state(arango_enabled=arango_enabled)
        if payload["arango_required"] and not arango_enabled:
            payload["code"] = 50301
            payload["msg"] = "ArangoDB 不可用，AI 服务处于受限状态"
            deps.logger.critical("健康检查发现 ArangoDB 不可用且当前环境要求持久化索引")
            return 503, payload

        if not payload.get("inference_ready", True):
            payload["code"] = 50302
            payload["msg"] = "AI 推理服务当前不可用"
            deps.logger.warning(
                "健康检查发现推理链路不可用。backend=%s error=%s",
                payload.get("inference_backend", "unknown"),
                payload.get("inference_error", ""),
            )
            return 503, payload

        payload["code"] = 0
        payload["msg"] = "AI 服务运行正常"
        return 200, payload

    def _resolve_extract_file_path(raw_path: str) -> tuple[bool, str, str]:
        target = Path(raw_path).expanduser()
        try:
            target_resolved = target.resolve(strict=False)
        except Exception:
            return False, "", "file path resolve failed"

        allowed_roots_raw = os.getenv("AURA_AI_EXTRACT_FILE_ROOTS", "").strip()
        if not allowed_roots_raw:
            return True, str(target_resolved), ""

        allowed_roots = []
        for item in allowed_roots_raw.replace("\n", ";").split(";"):
            value = item.strip()
            if not value:
                continue
            try:
                allowed_roots.append(Path(value).expanduser().resolve(strict=False))
            except Exception:
                deps.logger.warning("extract-file allowed root resolve failed: %s", value)

        for root in allowed_roots:
            try:
                target_resolved.relative_to(root)
                return True, str(target_resolved), ""
            except Exception:
                continue
        return False, "", "file path is outside allowed roots"

    def _resolve_eval_dataset_path(raw_path: str) -> tuple[bool, str, str]:
        target = Path(raw_path).expanduser()
        allowed_roots_raw = os.getenv("AURA_AI_EVAL_DATASET_ROOTS", "").strip()
        if not allowed_roots_raw:
            return True, str(target), ""

        try:
            target_resolved = target.resolve()
        except Exception:
            return False, "", "dataset_path_resolve_failed"

        allowed_roots = [
            Path(item.strip()).expanduser().resolve()
            for item in allowed_roots_raw.replace("\n", ";").split(";")
            if item.strip()
        ]
        for root in allowed_roots:
            try:
                target_resolved.relative_to(root)
                return True, str(target_resolved), ""
            except Exception:
                continue
        return False, "", "dataset_path_outside_allowed_roots"

    def require_retrieval_quota(request: Request) -> None:
        allowed, reason = deps.retrieval_guard.allow_request()
        if allowed:
            return
        request_id = getattr(request.state, "request_id", "")
        raise RetrievalQuotaExceeded(reason=reason, request_id=request_id)

    @router.get("/")
    def health():
        status_code, payload = _build_health_payload()
        if status_code != 200:
            return JSONResponse(status_code=status_code, content=payload)
        return payload

    @router.get("/live")
    def live():
        return {"code": 0, "msg": "AI 服务进程存活", "status": "alive"}

    @router.get("/ready")
    def ready():
        status_code, payload = _build_health_payload()
        if status_code != 200:
            return JSONResponse(status_code=status_code, content=payload)
        return payload

    @router.post("/ai/extract")
    async def extract(req: ImageReq, _quota: None = Depends(require_retrieval_quota)):
        try:
            validate_image_base64_length(req.image_base64)
            if deps.accepts_raw_image_inference():
                feature = await deps.extract_feature_from_base64(req.image_base64, req.metadata_json)
            else:
                img = deps.decode_image(req.image_base64)
                tensor = deps.preprocess(img)
                feature = await deps.extract_feature_batched(tensor)
            return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
        except InferenceBackpressureError:
            return JSONResponse(
                status_code=429,
                content={"code": 42902, "msg": "推理服务繁忙，请稍后重试", "data": {"feature": [], "dim": 0}},
            )
        except InferenceUnavailableError:
            return JSONResponse(
                status_code=503,
                content={"code": 50302, "msg": "AI 推理服务当前不可用，请稍后重试", "data": {"feature": [], "dim": 0}},
            )
        except ImageTooLargeError as ex:
            return JSONResponse(
                status_code=413,
                content={"code": 41301, "msg": str(ex), "data": {"feature": [], "dim": 0}},
            )
        except ImageValidationError as ex:
            return JSONResponse(
                status_code=400,
                content={"code": 40002, "msg": str(ex), "data": {"feature": [], "dim": 0}},
            )
        except Exception:
            deps.logger.exception("特征提取失败（/ai/extract）")
            return JSONResponse(
                status_code=500,
                content={"code": 50001, "msg": "特征提取失败，请稍后重试", "data": {"feature": [], "dim": 0}},
            )

    @router.post("/ai/extract-file")
    async def extract_file(req: ImageFileReq, _quota: None = Depends(require_retrieval_quota)):
        try:
            allowed, resolved_path, reason = _resolve_extract_file_path(req.image_path)
            if not allowed:
                return JSONResponse(status_code=403, content={"code": 40301, "msg": reason})

            path = Path(resolved_path)
            use_external_image_service = deps.accepts_raw_image_inference()
            if not path.exists() and not use_external_image_service:
                return JSONResponse(status_code=404, content={"code": 40401, "msg": f"file not found: {req.image_path}"})

            if use_external_image_service:
                feature = await deps.extract_feature_from_file(req.image_path, req.metadata_json)
            else:
                rgb = deps.decode_image_file(str(path))
                tensor = deps.preprocess(rgb)
                feature = await deps.extract_feature_batched(tensor)
            return {"code": 0, "msg": "特征提取成功", "data": {"feature": feature, "dim": len(feature)}}
        except InferenceBackpressureError:
            return JSONResponse(
                status_code=429,
                content={"code": 42902, "msg": "推理服务繁忙，请稍后重试", "data": {"feature": [], "dim": 0}},
            )
        except InferenceUnavailableError:
            return JSONResponse(
                status_code=503,
                content={"code": 50302, "msg": "AI 推理服务当前不可用，请稍后重试", "data": {"feature": [], "dim": 0}},
            )
        except ImageTooLargeError as ex:
            return JSONResponse(
                status_code=413,
                content={"code": 41301, "msg": str(ex), "data": {"feature": [], "dim": 0}},
            )
        except ImageValidationError as ex:
            return JSONResponse(
                status_code=400,
                content={"code": 40002, "msg": str(ex), "data": {"feature": [], "dim": 0}},
            )
        except Exception:
            deps.logger.exception("特征提取失败（/ai/extract-file）")
            return JSONResponse(
                status_code=500,
                content={"code": 50001, "msg": "特征提取失败，请稍后重试", "data": {"feature": [], "dim": 0}},
            )

    @router.post("/ai/upsert")
    async def upsert(req: UpsertReq, _quota: None = Depends(require_retrieval_quota)):
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
    async def search(req: SearchReq, request: Request, _quota: None = Depends(require_retrieval_quota)):
        request_id = getattr(request.state, "request_id", "")
        strict_mode = requires_persistent_index()
        defaults = build_retrieval_defaults()
        resolved, warnings = resolve_search_params(req, defaults)
        begin = time.perf_counter()
        try:
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
        except Exception:
            deps.retrieval_guard.record_result(success=False)
            elapsed_ms = (time.perf_counter() - begin) * 1000.0
            deps.index_runtime.record_search(
                success=False,
                hit_count=0,
                latency_ms=elapsed_ms,
                engine="unavailable",
                strategy="exception",
                filters_applied=bool(req.include_vids or req.exclude_vids or req.metadata_filter),
                request_id=request_id,
                status="failed",
                reason="internal_exception",
                warnings=warnings,
            )
            deps.logger.exception("检索内部异常 request_id=%s", request_id)
            return JSONResponse(status_code=500, content={"code": 50002, "msg": "检索失败，请稍后重试", "request_id": request_id})

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
                reason="index_unavailable",
                warnings=warnings,
            )
            deps.logger.warning("检索失败 request_id=%s", request_id)
            return result

        hits = result.get("data", [])
        meta = result.setdefault("meta", {})
        meta["request_id"] = request_id
        meta["resolved_params"] = {
            "top_k": max(1, min(req.top_k, 50)),
            "min_score": resolved["min_score"],
            "candidate_multiplier": resolved["candidate_multiplier"],
            "candidate_pool": resolved["candidate_pool"],
            "ann_probe": resolved["ann_probe"],
            "rerank_window": resolved["rerank_window"],
        }
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

    @router.post("/ai/evaluate-search")
    async def evaluate_search(req: EvalReq, request: Request, _quota: None = Depends(require_retrieval_quota)):
        request_id = getattr(request.state, "request_id", "")
        defaults = build_retrieval_defaults()
        resolved, warnings = resolve_search_params(req, defaults)
        try:
            if req.dataset is not None:
                dataset = req.dataset
            elif req.dataset_path:
                allowed, resolved_path, reason = _resolve_eval_dataset_path(req.dataset_path)
                if not allowed:
                    return JSONResponse(status_code=403, content={"code": 40301, "msg": reason, "request_id": request_id})
                dataset = load_eval_dataset(resolved_path)
            else:
                return JSONResponse(
                    status_code=400,
                    content={"code": 40001, "msg": "dataset or dataset_path is required", "request_id": request_id},
                )

            result = evaluate_retrieval_dataset(
                dataset,
                top_k=req.top_k,
                min_score=resolved["min_score"],
                candidate_multiplier=resolved["candidate_multiplier"],
                candidate_pool=resolved["candidate_pool"],
                ann_probe=resolved["ann_probe"],
                rerank_window=resolved["rerank_window"],
                normalize_feature_func=deps.normalize_feature,
                cosine_func=deps.cosine,
                vector_dim=deps.vector_dim,
                logger=deps.logger,
            )
        except FileNotFoundError:
            return JSONResponse(status_code=404, content={"code": 40401, "msg": "dataset_path not found", "request_id": request_id})
        except ValueError as ex:
            return JSONResponse(status_code=400, content={"code": 40002, "msg": str(ex), "request_id": request_id})
        except Exception:
            deps.logger.exception("search evaluation failed request_id=%s", request_id)
            return JSONResponse(
                status_code=500,
                content={"code": 50003, "msg": "search evaluation failed", "request_id": request_id},
            )

        summary = result.setdefault("summary", {})
        summary["resolved_params"] = {
            "top_k": max(1, min(req.top_k, 50)),
            "min_score": resolved["min_score"],
            "candidate_multiplier": resolved["candidate_multiplier"],
            "candidate_pool": resolved["candidate_pool"],
            "ann_probe": resolved["ann_probe"],
            "rerank_window": resolved["rerank_window"],
        }
        if warnings:
            summary["warnings"] = warnings
        summary["request_id"] = request_id
        return {"code": 0, "msg": "search evaluation completed", "data": result}

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
    async def cluster(req: ClusterReq, _quota: None = Depends(require_retrieval_quota)):
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