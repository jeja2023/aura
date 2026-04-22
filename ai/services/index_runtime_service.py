# 文件：索引运行时服务（index_runtime_service.py） | File: Index runtime service
import json
import os
import threading
from collections import deque
from datetime import datetime


class IndexRuntimeService:
    def __init__(self, *, snapshot_path: str):
        self._snapshot_path = snapshot_path
        self._metric_lock = threading.Lock()
        self._search_total = 0
        self._search_success = 0
        self._search_empty = 0
        self._search_failed = 0
        self._search_latency_ms_total = 0.0
        self._last_search_time = ""
        self._bucket_stats = {}
        self._search_events = deque(maxlen=10000)
        self._search_audits = deque(maxlen=2000)
        self._backfill_rounds = 0
        self._backfill_rows = 0
        self._backfill_failures = 0

    def load_snapshot(self, *, local_index: list, normalize_feature_func, logger) -> int:
        if not os.path.exists(self._snapshot_path):
            return 0
        try:
            with open(self._snapshot_path, "r", encoding="utf-8") as f:
                payload = json.load(f)
            rows = payload if isinstance(payload, list) else payload.get("items", [])
            loaded = 0
            for row in rows:
                vid = str(row.get("vid", "")).strip()
                feature = row.get("feature")
                metadata = row.get("metadata", {})
                ann_bucket_16 = str(row.get("ann_bucket_16", "")).strip()
                ann_bucket_8 = str(row.get("ann_bucket_8", "")).strip()
                ann_bucket_24 = str(row.get("ann_bucket_24", "")).strip()
                if not vid or not isinstance(feature, list):
                    continue
                local_index.append(
                    {
                        "vid": vid,
                        "feature": normalize_feature_func(feature),
                        "metadata": metadata if isinstance(metadata, dict) else {},
                        "ann_bucket_8": ann_bucket_8,
                        "ann_bucket_16": ann_bucket_16,
                        "ann_bucket_24": ann_bucket_24,
                    }
                )
                loaded += 1
            logger.info("本地索引快照加载完成，条数=%s path=%s", loaded, self._snapshot_path)
            return loaded
        except Exception as ex:
            logger.warning("本地索引快照加载失败，已忽略. path=%s error=%s", self._snapshot_path, ex)
            return 0

    def save_snapshot(self, *, local_index: list, logger) -> int:
        try:
            folder = os.path.dirname(self._snapshot_path)
            if folder:
                os.makedirs(folder, exist_ok=True)
            payload = {
                "version": 2,
                "updated_at": datetime.now().isoformat(),
                "items": local_index,
            }
            temp_path = f"{self._snapshot_path}.tmp"
            with open(temp_path, "w", encoding="utf-8") as f:
                json.dump(payload, f, ensure_ascii=False)
            os.replace(temp_path, self._snapshot_path)
            logger.info("本地索引快照写入完成，条数=%s path=%s", len(local_index), self._snapshot_path)
            return len(local_index)
        except Exception as ex:
            logger.warning("本地索引快照写入失败. path=%s error=%s", self._snapshot_path, ex)
            return 0

    def record_search(
        self,
        *,
        success: bool,
        hit_count: int,
        latency_ms: float,
        engine: str = "unknown",
        strategy: str = "unknown",
        filters_applied: bool = False,
        request_id: str = "",
        status: str = "",
        reason: str = "",
        warnings: list[str] | None = None,
    ) -> None:
        with self._metric_lock:
            self._search_total += 1
            if success:
                self._search_success += 1
            else:
                self._search_failed += 1
            is_empty = bool(success and hit_count == 0)
            if is_empty:
                self._search_empty += 1
            self._search_latency_ms_total += max(0.0, latency_ms)
            self._last_search_time = datetime.now().isoformat()
            bucket_key = f"{engine}|{strategy}|{'filter' if filters_applied else 'plain'}"
            bucket = self._bucket_stats.setdefault(
                bucket_key,
                {"count": 0, "success": 0, "empty": 0, "latency_ms_total": 0.0},
            )
            bucket["count"] += 1
            if success:
                bucket["success"] += 1
            if is_empty:
                bucket["empty"] += 1
            bucket["latency_ms_total"] += max(0.0, latency_ms)
            self._search_events.append(
                {
                    "time": datetime.now().timestamp(),
                    "success": 1 if success else 0,
                    "empty": 1 if is_empty else 0,
                    "latency_ms": max(0.0, latency_ms),
                }
            )
            self._search_audits.append(
                {
                    "time": datetime.now().isoformat(),
                    "request_id": request_id,
                    "success": bool(success),
                    "status": status or ("success" if success else "failed"),
                    "reason": reason,
                    "hit_count": max(0, hit_count),
                    "latency_ms": round(max(0.0, latency_ms), 3),
                    "engine": engine,
                    "strategy": strategy,
                    "filters_applied": bool(filters_applied),
                    "warnings": warnings or [],
                }
            )

    def get_search_metrics(self) -> dict:
        with self._metric_lock:
            avg_latency = 0.0
            if self._search_total > 0:
                avg_latency = self._search_latency_ms_total / self._search_total
            buckets = {}
            for key, value in self._bucket_stats.items():
                bucket_avg = 0.0
                if value["count"] > 0:
                    bucket_avg = value["latency_ms_total"] / value["count"]
                buckets[key] = {
                    "count": value["count"],
                    "success": value["success"],
                    "empty": value["empty"],
                    "avg_latency_ms": round(bucket_avg, 3),
                }
            return {
                "search_total": self._search_total,
                "search_success": self._search_success,
                "search_failed": self._search_failed,
                "search_empty": self._search_empty,
                "search_avg_latency_ms": round(avg_latency, 3),
                "last_search_time": self._last_search_time,
                "buckets": buckets,
            }

    def get_search_metrics_window(self, *, window_minutes: int) -> dict:
        minutes = max(1, min(window_minutes, 120))
        now_ts = datetime.now().timestamp()
        start_ts = now_ts - minutes * 60
        total = 0
        success = 0
        empty = 0
        latency_total = 0.0
        with self._metric_lock:
            for event in self._search_events:
                if event["time"] < start_ts:
                    continue
                total += 1
                success += event["success"]
                empty += event["empty"]
                latency_total += event["latency_ms"]
        avg_latency = (latency_total / total) if total > 0 else 0.0
        return {
            "window_minutes": minutes,
            "search_total": total,
            "search_success": success,
            "search_failed": total - success,
            "search_empty": empty,
            "search_avg_latency_ms": round(avg_latency, 3),
        }

    def build_prometheus_metrics(self) -> str:
        data = self.get_search_metrics()
        win5 = self.get_search_metrics_window(window_minutes=5)
        win15 = self.get_search_metrics_window(window_minutes=15)
        lines = [
            "# HELP aura_ai_search_total 累计检索请求总数",
            "# TYPE aura_ai_search_total counter",
            f"aura_ai_search_total {data['search_total']}",
            "# HELP aura_ai_search_success 累计检索成功次数",
            "# TYPE aura_ai_search_success counter",
            f"aura_ai_search_success {data['search_success']}",
            "# HELP aura_ai_search_failed 累计检索失败次数",
            "# TYPE aura_ai_search_failed counter",
            f"aura_ai_search_failed {data['search_failed']}",
            "# HELP aura_ai_search_empty 累计检索空结果次数",
            "# TYPE aura_ai_search_empty counter",
            f"aura_ai_search_empty {data['search_empty']}",
            "# HELP aura_ai_search_avg_latency_ms 检索平均耗时毫秒",
            "# TYPE aura_ai_search_avg_latency_ms gauge",
            f"aura_ai_search_avg_latency_ms {data['search_avg_latency_ms']}",
            "# HELP aura_ai_search_window_total 指定窗口检索请求总数",
            "# TYPE aura_ai_search_window_total gauge",
            f"aura_ai_search_window_total{{window=\"5m\"}} {win5['search_total']}",
            f"aura_ai_search_window_total{{window=\"15m\"}} {win15['search_total']}",
            "# HELP aura_ai_search_window_avg_latency_ms 指定窗口检索平均耗时毫秒",
            "# TYPE aura_ai_search_window_avg_latency_ms gauge",
            f"aura_ai_search_window_avg_latency_ms{{window=\"5m\"}} {win5['search_avg_latency_ms']}",
            f"aura_ai_search_window_avg_latency_ms{{window=\"15m\"}} {win15['search_avg_latency_ms']}",
            "# HELP aura_ai_backfill_rounds 历史桶字段回填轮次",
            "# TYPE aura_ai_backfill_rounds counter",
            f"aura_ai_backfill_rounds {self._backfill_rounds}",
            "# HELP aura_ai_backfill_rows 历史桶字段回填条数",
            "# TYPE aura_ai_backfill_rows counter",
            f"aura_ai_backfill_rows {self._backfill_rows}",
            "# HELP aura_ai_backfill_failures 历史桶字段回填失败次数",
            "# TYPE aura_ai_backfill_failures counter",
            f"aura_ai_backfill_failures {self._backfill_failures}",
        ]
        for key, value in data.get("buckets", {}).items():
            parts = key.split("|")
            engine = parts[0] if len(parts) > 0 else "unknown"
            strategy = parts[1] if len(parts) > 1 else "unknown"
            filter_mode = parts[2] if len(parts) > 2 else "plain"
            labels = f'engine="{engine}",strategy="{strategy}",filter_mode="{filter_mode}"'
            lines.append(f"aura_ai_search_bucket_count{{{labels}}} {value.get('count', 0)}")
            lines.append(f"aura_ai_search_bucket_success{{{labels}}} {value.get('success', 0)}")
            lines.append(f"aura_ai_search_bucket_empty{{{labels}}} {value.get('empty', 0)}")
            lines.append(f"aura_ai_search_bucket_avg_latency_ms{{{labels}}} {value.get('avg_latency_ms', 0.0)}")
        return "\n".join(lines) + "\n"

    def get_backfill_state(self) -> dict:
        with self._metric_lock:
            return {
                "rounds": self._backfill_rounds,
                "rows": self._backfill_rows,
                "failures": self._backfill_failures,
                "status": "异常" if self._backfill_failures > 0 else "正常",
            }

    def get_search_audit_logs(self, *, limit: int = 100) -> dict:
        safe_limit = max(1, min(limit, 500))
        with self._metric_lock:
            items = list(self._search_audits)[-safe_limit:]
            return {
                "total_cached": len(self._search_audits),
                "returned": len(items),
                "items": items,
            }

    def record_backfill(self, *, rows: int, failed: bool) -> None:
        with self._metric_lock:
            if failed:
                self._backfill_failures += 1
                return
            self._backfill_rounds += 1
            self._backfill_rows += max(0, rows)
