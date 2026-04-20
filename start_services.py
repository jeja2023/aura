#!/usr/bin/env python3
# 文件：一键启动脚本（start_services.py） | File: Service Launcher
#
# 适用范围：本机全栈联调（AI + .NET + PostgreSQL + Redis）。
# 默认不会强制清理占用端口的进程；如确认可清理，请附加 --kill-conflicts。
import json
import os
import re
import signal
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import webbrowser
from http.cookiejar import CookieJar
from pathlib import Path
from typing import Any, Callable, Optional


ROOT = Path(__file__).resolve().parent
AI_DIR = ROOT / "ai"
API_DIR = ROOT / "backend" / "Aura.Api"
FRONTEND_URL = "https://localhost:5001/"
AI_HEALTH_URL = "http://127.0.0.1:8000/"
API_HEALTH_URL = "https://localhost:5001/api/health"
DEV_CONFIG = API_DIR / "appsettings.Development.json"
API_LOGIN_URL = "https://localhost:5001/api/auth/login"
API_READINESS_URL = "https://localhost:5001/api/ops/readiness"


def _pick_ai_python() -> str:
    venv_python = AI_DIR / ".venv" / "Scripts" / "python.exe"
    if venv_python.exists():
        return str(venv_python)
    return sys.executable


def _wait_http_json_probe(
    url: str,
    timeout_sec: int,
    *,
    ignore_tls: bool = False,
    predicate: Callable[[dict[str, Any]], bool],
    progress_label: Optional[str] = None,
    progress_interval_sec: int = 15,
) -> bool:
    start_ts = time.time()
    deadline = start_ts + timeout_sec
    next_progress_ts = start_ts + progress_interval_sec
    context = ssl._create_unverified_context() if ignore_tls else None
    while time.time() < deadline:
        now = time.time()
        if progress_label and now >= next_progress_ts:
            print(
                f"[检查] {progress_label} 仍在等待，已耗时约 {int(now - start_ts)} 秒（上限 {timeout_sec} 秒）..."
            )
            next_progress_ts = now + progress_interval_sec
        try:
            with urllib.request.urlopen(url, timeout=2, context=context) as resp:
                if not (200 <= resp.status <= 299):
                    time.sleep(1)
                    continue
                raw = resp.read()
                try:
                    data = json.loads(raw.decode("utf-8", errors="replace"))
                except json.JSONDecodeError:
                    time.sleep(1)
                    continue
                if isinstance(data, dict) and predicate(data):
                    return True
        except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError, OSError):
            time.sleep(1)
    return False


def _start_process(cmd: list[str], cwd: Path, name: str) -> subprocess.Popen:
    print(f"[启动] {name}: {' '.join(cmd)}")
    try:
        return subprocess.Popen(cmd, cwd=str(cwd))
    except FileNotFoundError as ex:
        raise RuntimeError(f"{name} 启动失败，未找到命令：{cmd[0]}") from ex


def _load_env_file(env_path: Path) -> None:
    if not env_path.exists():
        return

    for raw_line in env_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if not key:
            continue
        value = value.strip()
        if (value.startswith('"') and value.endswith('"')) or (value.startswith("'") and value.endswith("'")):
            value = value[1:-1]
        os.environ[key] = value


def _get_or_default_env(name: str, default: Optional[str]) -> Optional[str]:
    value = os.environ.get(name)
    if value is None:
        return default
    value = str(value).strip()
    return value if value else default


def _env_key_pgsql() -> str:
    return "ConnectionStrings__PgSql"


def _env_key_redis() -> str:
    return "ConnectionStrings__Redis"


def _env_key_ai_base_url() -> str:
    return "Ai__BaseUrl"


def _preflight_check() -> None:
    if not DEV_CONFIG.exists():
        raise RuntimeError(f"缺少开发配置文件：{DEV_CONFIG}")
    try:
        cfg = json.loads(DEV_CONFIG.read_text(encoding="utf-8"))
    except Exception as ex:
        raise RuntimeError(f"读取开发配置失败：{ex}") from ex

    jwt_key = str(((cfg.get("Jwt") or {}).get("Key") or "")).strip()
    pgsql_conn_from_cfg = str(((cfg.get("ConnectionStrings") or {}).get("PgSql") or "")).strip()
    redis_conn_from_cfg = str(((cfg.get("ConnectionStrings") or {}).get("Redis") or "")).strip()
    ai_base_url_from_cfg = str(((cfg.get("Ai") or {}).get("BaseUrl") or "")).strip()

    jwt_key = (_get_or_default_env("Jwt__Key", jwt_key) or "").strip()
    pgsql_conn = _get_or_default_env(_env_key_pgsql(), pgsql_conn_from_cfg) or ""
    redis_conn = _get_or_default_env(_env_key_redis(), redis_conn_from_cfg) or ""
    ai_base_url = _get_or_default_env(_env_key_ai_base_url(), ai_base_url_from_cfg) or ""

    if not jwt_key:
        raise RuntimeError("开发配置缺少 Jwt:Key")
    if "PLEASE_" in pgsql_conn.upper() or not pgsql_conn:
        raise RuntimeError("开发配置中的 PgSql 连接串仍为占位值，请先配置后再启动。")
    if "PLEASE_" in redis_conn.upper() or not redis_conn:
        raise RuntimeError("开发配置中的 Redis 连接串仍为占位值，请先配置后再启动。")
    if not ai_base_url:
        raise RuntimeError("开发配置缺少 Ai:BaseUrl")
    print("[预检] 开发配置检查通过。")


def _extract_dev_admin_password_from_log_line(line: str) -> Optional[str]:
    match = re.search(r"(?:临时密码|新密码|密码)\s*[=:：]\s*([A-Za-z0-9!@#$%^&*\-_=+]+)", line)
    if match:
        return match.group(1)
    return None


def _parse_json_bytes(data: bytes) -> dict[str, Any]:
    try:
        return json.loads(data.decode("utf-8", errors="ignore"))
    except Exception:
        return {"raw": data[:200].decode("utf-8", errors="ignore")}


def _login_and_call_readiness(admin_user: str, admin_password: str) -> dict[str, Any]:
    ctx = ssl._create_unverified_context()
    jar = CookieJar()
    opener = urllib.request.build_opener(
        urllib.request.HTTPCookieProcessor(jar),
        urllib.request.HTTPSHandler(context=ctx),
    )

    payload = {"UserName": admin_user, "Password": admin_password}
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        API_LOGIN_URL,
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with opener.open(req, timeout=10) as resp:
            _ = resp.read()
    except urllib.error.HTTPError as ex:
        body = ex.read() if hasattr(ex, "read") else b""
        raise RuntimeError(f"登录失败 HTTP={ex.code}, body={_parse_json_bytes(body)}") from ex

    ready_req = urllib.request.Request(API_READINESS_URL, method="GET")
    try:
        with opener.open(ready_req, timeout=10) as resp:
            return _parse_json_bytes(resp.read())
    except urllib.error.HTTPError as ex:
        body = ex.read() if hasattr(ex, "read") else b""
        raise RuntimeError(f"readiness 调用失败 HTTP={ex.code}, body={_parse_json_bytes(body)}") from ex


def _find_process_ids_on_local_port(port: int) -> list[int]:
    if os.name != "nt":
        return []

    try:
        out = subprocess.check_output(["netstat", "-ano"], text=True, encoding="utf-8", errors="ignore")
    except Exception:
        return []

    pids: set[int] = set()
    for line in out.splitlines():
        raw = line.strip()
        if not raw:
            continue
        # netstat 示例：
        # TCP    127.0.0.1:8000     0.0.0.0:0      LISTENING       1234
        # TCP    127.0.0.1:55199    127.0.0.1:8000 TIME_WAIT       0
        # 仅将 LISTENING 视为“端口被占用”。TIME_WAIT / ESTABLISHED 等不应阻断启动。
        if "LISTENING" not in raw.upper():
            continue

        parts = raw.split()
        if not parts:
            continue

        # 兼容 IPv4/IPv6：本地地址通常位于第 2 列（如 127.0.0.1:8000 或 [::]:8000）
        if len(parts) < 2:
            continue
        local = parts[1]
        if f":{port}" not in local:
            continue

        last = parts[-1]
        if last.isdigit():
            pids.add(int(last))

    return sorted(pids)


def _kill_process_on_local_port(port: int) -> None:
    pids = _find_process_ids_on_local_port(port)
    for pid in pids:
        try:
            print(f"[清理] 端口 {port} 被 PID={pid} 占用，尝试强制结束...")
            subprocess.run(["taskkill", "/PID", str(pid), "/F"], check=False, capture_output=True)
        except Exception:
            pass
    if pids:
        time.sleep(1)


def _ensure_local_port_available(port: int) -> None:
    pids = _find_process_ids_on_local_port(port)
    if not pids:
        return
    raise RuntimeError(
        f"端口 {port} 已被进程占用（PID={','.join(str(pid) for pid in pids)}）。"
        "如确认可清理，请重新运行并附加 --kill-conflicts。"
    )


def main() -> int:
    run_until_ready = ("--run-until-ready" in sys.argv) or ("--check-only" in sys.argv)
    kill_conflicts = "--kill-conflicts" in sys.argv

    _load_env_file(ROOT / ".env")
    _preflight_check()

    # 经验：AI 服务（8000）更容易因上次调试/异常退出残留监听而阻塞一键启动。
    # 因此默认“只对 8000 做一次安全清理”，避免误判或手动介入。
    # 5001（.NET）仍保持谨慎策略：仅在显式 --kill-conflicts 时清理。
    _kill_process_on_local_port(8000)
    if kill_conflicts:
        _kill_process_on_local_port(5001)
    else:
        _ensure_local_port_available(5001)

    ai_python = _pick_ai_python()
    ai_cmd = [ai_python, "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", "8000"]
    api_cmd = ["dotnet", "run"]

    ai_proc = _start_process(ai_cmd, AI_DIR, "AI 服务")
    api_proc: Optional[subprocess.Popen] = None
    return_code = 0

    try:
        print("[检查] 等待 AI 服务就绪（HTTP 2xx + code=0 + model_loaded=true）...")
        if not _wait_http_json_probe(
            AI_HEALTH_URL,
            timeout_sec=120,
            predicate=lambda d: d.get("code") == 0 and d.get("model_loaded") is True,
            progress_label="AI 服务（含 ONNX 加载）",
        ):
            raise RuntimeError(
                "AI 服务 120 秒内未就绪。请检查：1) 8000 端口；2) ai/.venv 与依赖；3) 模型路径 AURA_MODEL_PATH；4) uvicorn 控制台报错。"
            )

        api_proc = subprocess.Popen(
            api_cmd,
            cwd=str(API_DIR),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )

        dev_admin_password_from_log: Optional[str] = None

        def _read_api_stdout() -> None:
            nonlocal dev_admin_password_from_log
            assert api_proc is not None and api_proc.stdout is not None
            for line in api_proc.stdout:
                print(line, end="")
                if dev_admin_password_from_log is None:
                    pwd = _extract_dev_admin_password_from_log_line(line)
                    if pwd:
                        dev_admin_password_from_log = pwd

        threading.Thread(target=_read_api_stdout, daemon=True).start()

        print("[检查] 等待 .NET 服务就绪（HTTP 2xx + /api/health 返回 code=0）...")
        if not _wait_http_json_probe(
            API_HEALTH_URL,
            timeout_sec=180,
            ignore_tls=True,
            predicate=lambda d: d.get("code") == 0,
            progress_label=".NET API（首次启动可能包含编译）",
        ):
            raise RuntimeError(
                ".NET 服务 180 秒内未就绪。请检查 PostgreSQL/Redis 可达性、HTTPS 开发证书与控制台日志。"
            )

        admin_user = str((os.environ.get("AURA_ADMIN_USER") or "admin")).strip()
        admin_password = str((os.environ.get("AURA_ADMIN_PASSWORD") or "")).strip()
        readiness_payload: Optional[dict[str, Any]] = None
        if admin_password:
            try:
                readiness_payload = _login_and_call_readiness(admin_user, admin_password)
            except Exception as ex:
                print(f"[readiness] 使用 .env 中的管理员密码登录失败：{ex}")

        if readiness_payload is None and dev_admin_password_from_log:
            try:
                readiness_payload = _login_and_call_readiness(admin_user, dev_admin_password_from_log)
            except Exception as ex:
                print(f"[readiness] 使用启动日志中的临时密码登录失败：{ex}")

        if readiness_payload is None:
            print("[readiness] 未拿到管理员密码，跳过需要登录态的 readiness 深度检查。")
            print("[readiness] 如需执行完整检查，请在 .env 中设置 AURA_ADMIN_PASSWORD，或使用启动日志中显示的临时密码。")
        else:
            data = readiness_payload.get("data") or {}
            ready = bool(data.get("ready"))
            checks = data.get("checks") or {}
            print(f"[就绪检查] 整体状态：{'全部就绪' if ready else '存在异常'}")
            desc_map = {
                "jwt": "JWT 密钥",
                "hmac": "抓拍校验密钥",
                "pgsql": "PostgreSQL 数据库",
                "redis": "Redis 缓存",
                "ai_service": "AI 推理服务",
                "ai_model": "AI 模型加载",
                "alertNotify": "告警通知通道",
            }
            for key, value in checks.items():
                desc = desc_map.get(key, key)
                status = "已就绪" if value else "异常"
                print(f"  - {desc}: {status}")
            if not ready:
                raise RuntimeError(f"就绪检查未通过：{readiness_payload}")

        if run_until_ready:
            print("[readiness] 运行完成（run-until-ready 模式），即将退出并停止子进程。")
            return 0

        print(f"[打开] 前端页面：{FRONTEND_URL}")
        webbrowser.open(FRONTEND_URL)
        print("[完成] 服务已启动。按 Ctrl+C 停止两个服务。")

        while True:
            if ai_proc.poll() is not None:
                raise RuntimeError(f"AI 服务已退出，退出码：{ai_proc.returncode}")
            if api_proc is not None and api_proc.poll() is not None:
                raise RuntimeError(f".NET 服务已退出，退出码：{api_proc.returncode}")
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n[停止] 收到 Ctrl+C，正在停止服务...")
        return_code = 0
    except Exception as ex:
        print(f"[错误] {ex}")
        return_code = 1
    finally:
        for proc, name in ((ai_proc, "AI 服务"), (api_proc, ".NET 服务")):
            if proc is None:
                continue
            if proc.poll() is None:
                print(f"[停止] {name}")
                if os.name == "nt":
                    try:
                        proc.send_signal(signal.CTRL_BREAK_EVENT)
                        time.sleep(1)
                    except Exception:
                        pass
                proc.terminate()
                try:
                    proc.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    proc.kill()

    return return_code


if __name__ == "__main__":
    raise SystemExit(main())
