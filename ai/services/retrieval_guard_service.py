# 文件：检索保护服务（retrieval_guard_service.py） | File: Retrieval guard service
import threading
import time
from collections import deque


class RetrievalGuardService:
    def __init__(self, *, rate_limit_per_minute: int = 600, breaker_fail_threshold: int = 8, breaker_open_seconds: int = 20):
        self._rate_limit = max(30, min(rate_limit_per_minute, 10000))
        self._breaker_fail_threshold = max(1, min(breaker_fail_threshold, 100))
        self._breaker_open_seconds = max(1, min(breaker_open_seconds, 300))
        self._lock = threading.Lock()
        self._request_timestamps = deque()
        self._continuous_failures = 0
        self._breaker_open_until = 0.0

    def allow_request(self) -> tuple[bool, str]:
        now = time.time()
        with self._lock:
            if self._breaker_open_until > now:
                return False, "检索熔断中，请稍后重试"
            minute_ago = now - 60.0
            while self._request_timestamps and self._request_timestamps[0] < minute_ago:
                self._request_timestamps.popleft()
            if len(self._request_timestamps) >= self._rate_limit:
                return False, "检索请求过于频繁，请稍后重试"
            self._request_timestamps.append(now)
            return True, ""

    def record_result(self, *, success: bool) -> None:
        now = time.time()
        with self._lock:
            if success:
                self._continuous_failures = 0
                return
            self._continuous_failures += 1
            if self._continuous_failures >= self._breaker_fail_threshold:
                self._breaker_open_until = now + self._breaker_open_seconds
                self._continuous_failures = 0

    def get_state(self) -> dict:
        now = time.time()
        with self._lock:
            minute_ago = now - 60.0
            while self._request_timestamps and self._request_timestamps[0] < minute_ago:
                self._request_timestamps.popleft()
            breaker_open = self._breaker_open_until > now
            return {
                "circuit_breaker": {
                    "is_open": breaker_open,
                    "open_until": self._breaker_open_until if breaker_open else 0.0,
                    "fail_threshold": self._breaker_fail_threshold,
                    "open_seconds": self._breaker_open_seconds,
                },
                "rate_limiter": {
                    "limit_per_minute": self._rate_limit,
                    "current_requests": len(self._request_timestamps),
                    "remaining": max(0, self._rate_limit - len(self._request_timestamps)),
                },
            }
