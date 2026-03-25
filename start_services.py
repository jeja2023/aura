#!/usr/bin/env python3
# 文件：一键启动脚本（start_services.py） | File: Service Launcher

import os
import signal
import subprocess
import sys
import time
import json
import urllib.request
import urllib.error
import ssl
import webbrowser
from pathlib import Path
from typing import Optional
import threading
import re
from http.cookiejar import CookieJar


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


def _wait_http_ok(url: str, timeout_sec: int, ignore_tls: bool = False) -> bool:
    deadline = time.time() + timeout_sec
    context = None
    if ignore_tls:
        context = ssl._create_unverified_context()
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2, context=context) as resp:
                if 200 <= resp.status < 500:
                    return True
        except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError):
            time.sleep(1)
    return False


def _start_process(cmd: list[str], cwd: Path, name: str) -> subprocess.Popen:
    print(f"[启动] {name}: {' '.join(cmd)}")
    try:
        return subprocess.Popen(cmd, cwd=str(cwd))
    except FileNotFoundError as ex:
        raise RuntimeError(f"{name} 启动失败，未找到命令：{cmd[0]}") from ex


def _load_env_file(env_path: Path) -> None:
    """轻量加载根目录 .env（不依赖 python-dotenv）。

    支持 KEY=VALUE，忽略空行与以 # 开头的注释行；VALUE 支持去掉首尾引号。
    """
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
    v = os.environ.get(name)
    if v is None:
        return default
    v = str(v).strip()
    return v if v else default


def _env_key_pgsql() -> str:
    # .NET: ConnectionStrings:PgSql -> ConnectionStrings__PgSql
    return "ConnectionStrings__PgSql"


def _env_key_redis() -> str:
    return "ConnectionStrings__Redis"


def _env_key_ai_base_url() -> str:
    # .NET: Ai:BaseUrl -> Ai__BaseUrl
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

    # 优先读取根目录 .env（用于本机直跑统一配置）
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
    # 开发环境启动时 Program.cs 会打印随机密码：
    # - 开发环境管理员已自动创建：用户名=admin, 密码={devPassword}
    # - 开发环境管理员不存在，已自动重建：用户名=admin, 密码={devPassword}
    # - 开发环境管理员密码已一次性重置：用户名=admin, 新密码={devPassword}
    m = re.search(r"新密码=([A-Za-z0-9\-_]+)", line)
    if m:
        return m.group(1)
    m = re.search(r"密码=([A-Za-z0-9\-_]+)", line)
    if m:
        return m.group(1)
    return None


def _parse_json_bytes(data: bytes) -> dict:
    try:
        return json.loads(data.decode("utf-8", errors="ignore"))
    except Exception:
        return {"raw": data[:200].decode("utf-8", errors="ignore")}


def _login_and_call_readiness(admin_user: str, admin_password: str) -> dict:
    # 用 CookieJar 自动保存 aura_token HttpOnly Cookie
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
    except urllib.error.HTTPError as e:
        body = e.read() if hasattr(e, "read") else b""
        raise RuntimeError(
            f"登录失败 HTTP={e.code}, body={_parse_json_bytes(body)}"
        ) from e

    ready_req = urllib.request.Request(API_READINESS_URL, method="GET")
    try:
        with opener.open(ready_req, timeout=10) as resp:
            body = resp.read()
            return _parse_json_bytes(body)
    except urllib.error.HTTPError as e:
        body = e.read() if hasattr(e, "read") else b""
        raise RuntimeError(
            f"readiness 调用失败 HTTP={e.code}, body={_parse_json_bytes(body)}"
        ) from e


def _kill_process_on_local_port(port: int) -> None:
    """Windows: 强制杀掉占用本机端口的进程，避免重复启动时报 'address already in use'。"""
    if os.name != "nt":
        return

    try:
        out = subprocess.check_output(["netstat", "-ano"], text=True, encoding="utf-8", errors="ignore")
    except Exception:
        return

    pids: set[int] = set()
    # netstat -ano 输出格式: Proto LocalAddress ... State PID
    # 兼容不同语言下 State 字段，直接从末尾提取 PID。
    for line in out.splitlines():
        if f":{port}" not in line:
            continue
        parts = line.split()
        if not parts:
            continue
        last = parts[-1]
        if last.isdigit():
            pids.add(int(last))

    for pid in sorted(pids):
        try:
            print(f"[清理] 端口 {port} 占用 PID={pid}，尝试强制结束...")
            subprocess.run(["taskkill", "/PID", str(pid), "/F"], check=False, capture_output=True)
        except Exception:
            pass

    if pids:
        time.sleep(1)


def main() -> int:
    run_until_ready = ("--run-until-ready" in sys.argv) or ("--check-only" in sys.argv)
    # 本机直跑：优先从根目录 .env 注入数据库/AI 等配置到环境变量，供 AI 与 .NET 读取
    _load_env_file(ROOT / ".env")
    _preflight_check()
    # 一键启动前清理常见占用端口，避免重复启动导致绑定失败/进程锁文件
    _kill_process_on_local_port(8000)  # AI 服务
    _kill_process_on_local_port(5001)  # .NET 后端（launchSettings.json https://localhost:5001）
    ai_python = _pick_ai_python()
    ai_cmd = [ai_python, "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", "8000"]
    api_cmd = ["dotnet", "run"]

    ai_proc = _start_process(ai_cmd, AI_DIR, "AI 服务")
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

    # 异步读取 .NET 控制台输出：尝试提取开发环境管理员随机密码
    dev_admin_password_from_log: Optional[str] = None

    def _read_api_stdout() -> None:
        nonlocal dev_admin_password_from_log
        assert api_proc.stdout is not None
        for line in api_proc.stdout:
            # 保持可观测性：把输出回显到控制台
            print(line, end="")
            if dev_admin_password_from_log is None:
                pwd = _extract_dev_admin_password_from_log_line(line)
                if pwd:
                    dev_admin_password_from_log = pwd

    t = threading.Thread(target=_read_api_stdout, daemon=True)
    t.start()

    try:
        print("[检查] 等待 AI 服务就绪...")
        if not _wait_http_ok(AI_HEALTH_URL, timeout_sec=60):
            raise RuntimeError("AI 服务 60 秒内未就绪。")

        print("[检查] 等待 .NET 服务就绪...")
        if not _wait_http_ok(API_HEALTH_URL, timeout_sec=90, ignore_tls=True):
            raise RuntimeError(".NET 服务 90 秒内未就绪。")

        # 自动登录并调用 readiness（需要“超级管理员”权限）
        admin_user = str((os.environ.get("AURA_ADMIN_USER") or "admin")).strip()
        admin_password = str((os.environ.get("AURA_ADMIN_PASSWORD") or "")).strip()
        readiness_payload: Optional[dict] = None
        if admin_password:
            try:
                readiness_payload = _login_and_call_readiness(admin_user, admin_password)
            except Exception as ex:
                print(f"[readiness] 使用 .env 提供管理员密码登录失败：{ex}")

        if readiness_payload is None and dev_admin_password_from_log:
            readiness_payload = _login_and_call_readiness(admin_user, dev_admin_password_from_log)

        if readiness_payload is None:
            raise RuntimeError(
                "自动化 readiness 跑通失败：未能获得可用管理员密码。"
                "请检查 .env 的 AURA_ADMIN_PASSWORD 是否与数据库一致，或重启后观察控制台打印随机密码。"
            )

        data = readiness_payload.get("data") or {}
        ready = bool(data.get("ready"))
        checks = data.get("checks") or {}
        print(f"[readiness] ready={ready}, checks={checks}")
        if not ready:
            raise RuntimeError(f"readiness 检查未通过：{readiness_payload}")

        if run_until_ready:
            print("[readiness] 运行完成（run-until-ready 模式），即将退出并停止子进程。")
            return 0

        print(f"[打开] 前端页面：{FRONTEND_URL}")
        webbrowser.open(FRONTEND_URL)
        print("[完成] 服务已启动。按 Ctrl+C 停止两个服务。")

        while True:
            if ai_proc.poll() is not None:
                raise RuntimeError(f"AI 服务已退出，退出码：{ai_proc.returncode}")
            if api_proc.poll() is not None:
                raise RuntimeError(f".NET 服务已退出，退出码：{api_proc.returncode}")
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n[停止] 收到 Ctrl+C，正在停止服务...")
    except Exception as ex:
        print(f"[错误] {ex}")
        return_code = 1
    else:
        return_code = 0
    finally:
        for proc, name in ((ai_proc, "AI 服务"), (api_proc, ".NET 服务")):
            if proc.poll() is None:
                print(f"[停止] {name}")
                if os.name == "nt":
                    proc.send_signal(signal.CTRL_BREAK_EVENT)
                    time.sleep(1)
                proc.terminate()
                try:
                    proc.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    proc.kill()

    return return_code


if __name__ == "__main__":
    raise SystemExit(main())
