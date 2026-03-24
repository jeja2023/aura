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


ROOT = Path(__file__).resolve().parent
AI_DIR = ROOT / "ai"
API_DIR = ROOT / "backend" / "Aura.Api"
FRONTEND_URL = "https://localhost:5001/"
AI_HEALTH_URL = "http://127.0.0.1:8000/"
API_HEALTH_URL = "https://localhost:5001/api/health"
DEV_CONFIG = API_DIR / "appsettings.Development.json"


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


def _preflight_check() -> None:
    if not DEV_CONFIG.exists():
        raise RuntimeError(f"缺少开发配置文件：{DEV_CONFIG}")
    try:
        cfg = json.loads(DEV_CONFIG.read_text(encoding="utf-8"))
    except Exception as ex:
        raise RuntimeError(f"读取开发配置失败：{ex}") from ex

    jwt_key = str(((cfg.get("Jwt") or {}).get("Key") or "")).strip()
    mysql_conn = str(((cfg.get("ConnectionStrings") or {}).get("MySql") or "")).strip()
    redis_conn = str(((cfg.get("ConnectionStrings") or {}).get("Redis") or "")).strip()
    ai_base_url = str(((cfg.get("Ai") or {}).get("BaseUrl") or "")).strip()
    if not jwt_key:
        raise RuntimeError("开发配置缺少 Jwt:Key")
    if "PLEASE_" in mysql_conn.upper() or not mysql_conn:
        raise RuntimeError("开发配置中的 MySql 连接串仍为占位值，请先配置后再启动。")
    if "PLEASE_" in redis_conn.upper() or not redis_conn:
        raise RuntimeError("开发配置中的 Redis 连接串仍为占位值，请先配置后再启动。")
    if not ai_base_url:
        raise RuntimeError("开发配置缺少 Ai:BaseUrl")
    print("[预检] 开发配置检查通过。")


def main() -> int:
    _preflight_check()
    ai_python = _pick_ai_python()
    ai_cmd = [ai_python, "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", "8000"]
    api_cmd = ["dotnet", "run"]

    ai_proc = _start_process(ai_cmd, AI_DIR, "AI 服务")
    api_proc = _start_process(api_cmd, API_DIR, ".NET 服务")

    try:
        print("[检查] 等待 AI 服务就绪...")
        if not _wait_http_ok(AI_HEALTH_URL, timeout_sec=60):
            raise RuntimeError("AI 服务 60 秒内未就绪。")

        print("[检查] 等待 .NET 服务就绪...")
        if not _wait_http_ok(API_HEALTH_URL, timeout_sec=90, ignore_tls=True):
            raise RuntimeError(".NET 服务 90 秒内未就绪。")

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
