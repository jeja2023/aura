# 文件：Arango 索引服务（arango_service.py） | File: Arango index service
import os


class ArangoIndexService:
    def __init__(self, *, arango_client_cls, collection_name: str):
        self._arango_client_cls = arango_client_cls
        self._collection_name = collection_name
        self._db = None
        self._collection = None
        self._error = ""

    @property
    def db(self):
        return self._db

    @property
    def error(self) -> str:
        return self._error

    def mark_failure(self, ex: Exception | str) -> None:
        self._db = None
        self._collection = None
        self._error = str(ex)

    def init(self) -> None:
        if self._arango_client_cls is None:
            self._db = None
            self._collection = None
            self._error = "python-arango 未安装或无法导入"
            return

        arango_uri = os.getenv("ARANGO_URI", "http://127.0.0.1:8529")
        arango_db_name = os.getenv("ARANGO_DB", "aura")
        arango_user = os.getenv("ARANGO_USER", "")
        arango_password = os.getenv("ARANGO_PASSWORD", "")
        self._error = ""

        if not arango_user or not arango_password:
            self._db = None
            self._collection = None
            self._error = "ARANGO_USER/ARANGO_PASSWORD 未配置"
            return

        if "PLEASE_" in arango_user.upper() or "PLEASE_" in arango_password.upper():
            self._db = None
            self._collection = None
            self._error = "ARANGO_USER/ARANGO_PASSWORD 仍为占位值"
            return

        try:
            client = self._arango_client_cls(hosts=arango_uri)
            self._db = client.db(
                name=arango_db_name,
                username=arango_user,
                password=arango_password,
            )
            if not self._db.has_collection(self._collection_name):
                self._db.create_collection(self._collection_name)
            self._collection = self._db.collection(self._collection_name)
            self._ensure_query_indexes()
        except Exception as ex:
            self.mark_failure(ex)

    def _ensure_query_indexes(self) -> None:
        if self._collection is None:
            return
        try:
            existed = self._collection.indexes()
            existed_fields = {
                tuple(item.get("fields", []))
                for item in existed
                if isinstance(item, dict) and item.get("fields")
            }
            required_fields = [
                ("vid",),
                ("ann_bucket_8",),
                ("ann_bucket_16",),
                ("ann_bucket_24",),
                ("metadata",),
            ]
            for fields in required_fields:
                if fields in existed_fields:
                    continue
                self._collection.add_hash_index(fields=list(fields), unique=False, sparse=False)
        except Exception as ex:
            # 索引创建失败不阻断服务，只记录错误供健康检查观察
            self._error = f"索引保障失败: {ex}"

    def ensure(self) -> bool:
        if self._db is not None:
            return True
        self.init()
        return self._db is not None

    def backfill_ann_buckets(self, *, batch_size: int = 1000) -> int:
        if self._db is None:
            return 0
        try:
            batch = max(100, min(batch_size, 5000))
            cursor = self._db.aql.execute(
                """
                FOR d IN @@col
                  FILTER HAS(d, "feature")
                    AND (!HAS(d, "ann_bucket_8") OR !HAS(d, "ann_bucket_16") OR !HAS(d, "ann_bucket_24"))
                  LIMIT @batch
                  RETURN { _key: d._key, feature: d.feature }
                """,
                bind_vars={"@col": self._collection_name, "batch": batch},
            )
            rows = list(cursor)
            if not rows:
                return 0

            def _bucket(feature, probe: int) -> str:
                length = max(1, min(probe, len(feature)))
                return "".join("1" if feature[i] >= 0 else "0" for i in range(length))

            for row in rows:
                feature = row.get("feature")
                if not isinstance(feature, list) or not feature:
                    continue
                self._db.aql.execute(
                    """
                    UPDATE { _key: @key, ann_bucket_8: @b8, ann_bucket_16: @b16, ann_bucket_24: @b24 } IN @@col
                    """,
                    bind_vars={
                        "@col": self._collection_name,
                        "key": row["_key"],
                        "b8": _bucket(feature, 8),
                        "b16": _bucket(feature, 16),
                        "b24": _bucket(feature, 24),
                    },
                )
            return len(rows)
        except Exception as ex:
            self._error = f"桶字段回填失败: {ex}"
            return 0
