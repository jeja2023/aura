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
        except Exception as ex:
            self.mark_failure(ex)

    def ensure(self) -> bool:
        if self._db is not None:
            return True
        self.init()
        return self._db is not None
