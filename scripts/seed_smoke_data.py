#!/usr/bin/env python3
# 文件：冒烟数据注入脚本（seed_smoke_data.py） | File: Smoke Data Seeder

import argparse
import datetime as dt
import json
import ssl
import struct
import sys
import time
import urllib.error
import urllib.request
import zlib
from http.cookiejar import CookieJar
from typing import Any


def to_json_bytes(payload: dict[str, Any]) -> bytes:
    return json.dumps(payload, ensure_ascii=False).encode("utf-8")


def build_demo_floor_png(width: int = 1280, height: int = 720, style: str = "grid") -> bytes:
    """生成可读性更好的楼层示意 PNG（大尺寸网格图）。"""
    w = max(320, int(width))
    h = max(240, int(height))

    style_key = str(style or "grid").strip().lower()

    def _in_rect(rx: float, ry: float, x1: float, y1: float, x2: float, y2: float) -> bool:
        return x1 <= rx <= x2 and y1 <= ry <= y2

    def _near_v(rx: float, ry: float, x0: float, y1: float, y2: float, t: float) -> bool:
        return y1 <= ry <= y2 and abs(rx - x0) <= t

    def _near_h(rx: float, ry: float, y0: float, x1: float, x2: float, t: float) -> bool:
        return x1 <= rx <= x2 and abs(ry - y0) <= t

    def pixel(x: int, y: int) -> tuple[int, int, int]:
        rx = x / max(1, w - 1)
        ry = y / max(1, h - 1)

        if style_key == "blueprint-plus":
            # 增强蓝图：墙体、走廊、门洞、编号块
            bg = (16, 32, 60)
            major_grid = (44, 80, 132)
            minor_grid = (30, 58, 102)
            wall = (226, 238, 255)
            corridor = (28, 58, 96)
            number_block = (244, 206, 120)

            if x % 120 == 0 or y % 120 == 0:
                return major_grid
            if x % 24 == 0 or y % 24 == 0:
                return minor_grid

            if _in_rect(rx, ry, 0.46, 0.08, 0.54, 0.92) or _in_rect(rx, ry, 0.16, 0.44, 0.84, 0.56):
                base = corridor
            else:
                base = bg

            if _in_rect(rx, ry, 0.06, 0.10, 0.42, 0.90) or _in_rect(rx, ry, 0.58, 0.10, 0.94, 0.90):
                base = (22, 44, 78)

            t = 0.0022
            walls = [
                (0.06, 0.10, 0.90, "v"), (0.42, 0.10, 0.90, "v"),
                (0.58, 0.10, 0.90, "v"), (0.94, 0.10, 0.90, "v"),
                (0.10, 0.06, 0.42, "h"), (0.90, 0.06, 0.42, "h"),
                (0.10, 0.58, 0.94, "h"), (0.90, 0.58, 0.94, "h"),
                (0.44, 0.16, 0.84, "h"), (0.56, 0.16, 0.84, "h"),
            ]
            for p1, p2, p3, kind in walls:
                if (kind == "v" and _near_v(rx, ry, p1, p2, p3, t)) or (kind == "h" and _near_h(rx, ry, p1, p2, p3, t)):
                    base = wall

            for y0 in (0.20, 0.30, 0.40, 0.60, 0.70, 0.80):
                if _near_h(rx, ry, y0, 0.06, 0.42, t) or _near_h(rx, ry, y0, 0.58, 0.94, t):
                    base = wall
            for x0 in (0.18, 0.30, 0.70, 0.82):
                if _near_v(rx, ry, x0, 0.10, 0.90, t):
                    base = wall

            for dx, d1, d2 in ((0.42, 0.18, 0.22), (0.42, 0.66, 0.70), (0.58, 0.26, 0.30), (0.58, 0.74, 0.78)):
                if abs(rx - dx) <= t * 1.6 and d1 <= ry <= d2:
                    base = corridor

            if _in_rect(rx, ry, 0.44, 0.02, 0.56, 0.08):
                base = (54, 94, 146)

            for x1, y1, x2, y2 in (
                (0.12, 0.13, 0.17, 0.17), (0.24, 0.13, 0.29, 0.17), (0.64, 0.13, 0.69, 0.17),
                (0.76, 0.13, 0.81, 0.17), (0.12, 0.83, 0.17, 0.87), (0.76, 0.83, 0.81, 0.87),
            ):
                if _in_rect(rx, ry, x1, y1, x2, y2):
                    base = number_block
            return base

        if style_key == "dorm-layout":
            # 宿舍布局草图：宿舍区/公共区/通道区
            bg = (236, 239, 244)
            wall = (78, 86, 98)
            corridor = (210, 219, 230)
            dorm_a = (188, 214, 244)
            dorm_b = (176, 206, 238)
            public_zone = (196, 230, 206)
            utility_zone = (236, 214, 184)

            base = bg
            if _in_rect(rx, ry, 0.44, 0.05, 0.56, 0.95) or _in_rect(rx, ry, 0.12, 0.46, 0.88, 0.54):
                base = corridor

            room_rows = [(0.08, 0.18), (0.20, 0.30), (0.32, 0.42), (0.58, 0.68), (0.70, 0.80), (0.82, 0.92)]
            left_rooms = [(0.06, 0.22), (0.22, 0.40)]
            right_rooms = [(0.60, 0.78), (0.78, 0.94)]
            for i, (y1, y2) in enumerate(room_rows):
                for x1, x2 in left_rooms + right_rooms:
                    if _in_rect(rx, ry, x1, y1, x2, y2):
                        base = dorm_a if i % 2 == 0 else dorm_b

            if _in_rect(rx, ry, 0.24, 0.34, 0.40, 0.42) or _in_rect(rx, ry, 0.60, 0.58, 0.76, 0.66):
                base = public_zone
            if _in_rect(rx, ry, 0.24, 0.58, 0.40, 0.66) or _in_rect(rx, ry, 0.60, 0.34, 0.76, 0.42):
                base = utility_zone

            t = 0.0023
            if _near_h(rx, ry, 0.05, 0.06, 0.94, t) or _near_h(rx, ry, 0.95, 0.06, 0.94, t):
                base = wall
            if _near_v(rx, ry, 0.06, 0.05, 0.95, t) or _near_v(rx, ry, 0.94, 0.05, 0.95, t):
                base = wall
            if _near_v(rx, ry, 0.44, 0.05, 0.95, t) or _near_v(rx, ry, 0.56, 0.05, 0.95, t):
                base = wall
            if _near_h(rx, ry, 0.46, 0.12, 0.88, t) or _near_h(rx, ry, 0.54, 0.12, 0.88, t):
                base = wall
            for y1, y2 in room_rows:
                if _near_h(rx, ry, y1, 0.06, 0.40, t) or _near_h(rx, ry, y2, 0.06, 0.40, t):
                    base = wall
                if _near_h(rx, ry, y1, 0.60, 0.94, t) or _near_h(rx, ry, y2, 0.60, 0.94, t):
                    base = wall
            for x0 in (0.22, 0.40, 0.60, 0.78):
                if _near_v(rx, ry, x0, 0.08, 0.92, t):
                    base = wall

            for dy in (0.13, 0.25, 0.37, 0.63, 0.75, 0.87):
                if abs(ry - dy) <= 0.012 and (abs(rx - 0.44) <= 0.004 or abs(rx - 0.56) <= 0.004):
                    base = corridor
            return base

        if style_key == "blueprint":
            # 蓝图风：深蓝底 + 白线墙体 + 门廊块
            base_r, base_g, base_b = 18, 34, 64
            if x % 100 == 0 or y % 100 == 0:
                return (46, 84, 138)
            if x % 25 == 0 or y % 25 == 0:
                return (32, 62, 108)
            if (120 < x < 1140 and y in (110, 300, 520)) or (x in (120, 420, 760, 1140) and 110 < y < 520):
                return (218, 234, 255)
            if 430 < x < 740 and 305 < y < 515:
                return (82, 138, 208)
            return (base_r, base_g, base_b)
        if style_key == "zoning":
            # 分区风：不同功能区用明显色块表示，便于演示区域划分
            base_r, base_g, base_b = 32, 38, 46
            if x % 120 == 0 or y % 120 == 0:
                return (70, 78, 92)
            if x < w * 0.33:
                return (84, 126, 182)   # 教学区
            if x < w * 0.66:
                return (88, 164, 128)   # 生活区
            return (188, 132, 88)       # 公共区

        # 默认 grid：蓝灰底色 + 网格 + 房间块
        base_r, base_g, base_b = 28, 40, 58
        if x % 80 == 0 or y % 80 == 0:
            return (58, 90, 128)
        if x % 20 == 0 or y % 20 == 0:
            return (42, 62, 90)
        if 140 < x < 460 and 120 < y < 360:
            return (72, 112, 164)
        if 620 < x < 1060 and 220 < y < 560:
            return (86, 136, 194)
        return (base_r, base_g, base_b)

    raw = bytearray()
    for y in range(h):
        raw.append(0)  # filter: None
        for x in range(w):
            r, g, b = pixel(x, y)
            raw.extend((r, g, b))

    compressed = zlib.compress(bytes(raw), level=9)

    def chunk(chunk_type: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + chunk_type
            + data
            + struct.pack(">I", zlib.crc32(chunk_type + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)  # RGB
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", compressed) + chunk(b"IEND", b"")
    return png


class AuraSeeder:
    def __init__(
        self,
        base_url: str,
        username: str,
        password: str,
        insecure: bool,
        trend_profile: str = "campus",
        floor_width: int = 1280,
        floor_height: int = 720,
        floor_style: str = "grid",
        camera_count: int = 12,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.username = username
        self.password = password
        self.insecure = insecure
        self.trend_profile = trend_profile
        self.floor_width = max(320, int(floor_width))
        self.floor_height = max(240, int(floor_height))
        self.floor_style = floor_style
        self.camera_count = max(1, int(camera_count))
        self.ctx = ssl._create_unverified_context() if insecure else None
        self.jar = CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.jar),
            urllib.request.HTTPSHandler(context=self.ctx),
        )
        self.created: dict[str, list[int]] = {
            "deviceIds": [],
            "campusNodeIds": [],
            "floorIds": [],
            "cameraIds": [],
            "roiIds": [],
            "captureIds": [],
            "alertIds": [],
            "roleIds": [],
            "userIds": [],
        }
        self.extra: dict[str, list[str]] = {
            "trackVids": [],
            "floorFilePaths": [],
            "timeSeriesDays": [],
        }

    def call(self, method: str, path: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
        url = f"{self.base_url}{path}"
        data = to_json_bytes(payload) if payload is not None else None
        req = urllib.request.Request(
            url,
            method=method.upper(),
            data=data,
            headers={"Content-Type": "application/json"},
        )
        try:
            with self.opener.open(req, timeout=20) as resp:
                raw = resp.read()
        except urllib.error.HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="ignore")
            raise RuntimeError(f"{method} {path} 失败，HTTP {ex.code}，响应：{detail}") from ex
        except urllib.error.URLError as ex:
            raise RuntimeError(f"{method} {path} 失败，网络异常：{ex}") from ex

        try:
            parsed = json.loads(raw.decode("utf-8", errors="ignore"))
        except json.JSONDecodeError as ex:
            raise RuntimeError(f"{method} {path} 返回非 JSON：{raw[:200]!r}") from ex

        if isinstance(parsed, dict) and parsed.get("code", 0) not in (0, None):
            raise RuntimeError(f"{method} {path} 业务失败，code={parsed.get('code')}，msg={parsed.get('msg')}")
        if not isinstance(parsed, dict):
            raise RuntimeError(f"{method} {path} 返回结构异常：{type(parsed)}")
        return parsed

    def call_multipart(
        self, method: str, path: str, fields: dict[str, str], files: dict[str, tuple[str, bytes, str]]
    ) -> dict[str, Any]:
        boundary = f"----AuraBoundary{int(time.time() * 1000)}"
        body = bytearray()
        for field_name, field_value in fields.items():
            body.extend(f"--{boundary}\r\n".encode("utf-8"))
            body.extend(
                f'Content-Disposition: form-data; name="{field_name}"\r\n\r\n'.encode("utf-8")
            )
            body.extend(str(field_value).encode("utf-8"))
            body.extend(b"\r\n")
        for field_name, (file_name, file_bytes, content_type) in files.items():
            body.extend(f"--{boundary}\r\n".encode("utf-8"))
            body.extend(
                f'Content-Disposition: form-data; name="{field_name}"; filename="{file_name}"\r\n'.encode(
                    "utf-8"
                )
            )
            body.extend(f"Content-Type: {content_type}\r\n\r\n".encode("utf-8"))
            body.extend(file_bytes)
            body.extend(b"\r\n")
        body.extend(f"--{boundary}--\r\n".encode("utf-8"))

        url = f"{self.base_url}{path}"
        req = urllib.request.Request(
            url,
            method=method.upper(),
            data=bytes(body),
            headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        )
        try:
            with self.opener.open(req, timeout=20) as resp:
                raw = resp.read()
        except urllib.error.HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="ignore")
            raise RuntimeError(f"{method} {path} 失败，HTTP {ex.code}，响应：{detail}") from ex
        except urllib.error.URLError as ex:
            raise RuntimeError(f"{method} {path} 失败，网络异常：{ex}") from ex

        try:
            parsed = json.loads(raw.decode("utf-8", errors="ignore"))
        except json.JSONDecodeError as ex:
            raise RuntimeError(f"{method} {path} 返回非 JSON：{raw[:200]!r}") from ex
        if isinstance(parsed, dict) and parsed.get("code", 0) not in (0, None):
            raise RuntimeError(f"{method} {path} 业务失败，code={parsed.get('code')}，msg={parsed.get('msg')}")
        if not isinstance(parsed, dict):
            raise RuntimeError(f"{method} {path} 返回结构异常：{type(parsed)}")
        return parsed

    @staticmethod
    def read_id(data: Any, key: str) -> int:
        if isinstance(data, dict):
            value = data.get(key)
            if isinstance(value, int):
                return value
        raise RuntimeError(f"返回中缺少 {key}，data={data}")

    def login(self) -> None:
        resp = self.call(
            "POST",
            "/api/auth/login",
            {"UserName": self.username, "Password": self.password},
        )
        if resp.get("code") != 0:
            raise RuntimeError(f"登录失败：{resp}")
        print(f"[完成] 登录成功：{self.username}")

    def ensure_health(self) -> None:
        resp = self.call("GET", "/api/health")
        print(f"[检查] 健康检查：{resp.get('msg', 'ok')}")

    def create_device(self, suffix: str) -> int:
        resp = self.call(
            "POST",
            "/api/device/register",
            {
                "Name": f"冒烟NVR-{suffix}",
                "Ip": "192.168.10.88",
                "Port": 8000,
                "Brand": "hikvision",
                "Protocol": "isapi",
            },
        )
        device_id = self.read_id(resp.get("data"), "deviceId")
        self.created["deviceIds"].append(device_id)
        return device_id

    def create_campus_node(self, parent_id: int | None, level_type: str, node_name: str) -> int:
        resp = self.call(
            "POST",
            "/api/campus/create",
            {"ParentId": parent_id, "LevelType": level_type, "NodeName": node_name},
        )
        data = resp.get("data")
        if isinstance(data, dict) and isinstance(data.get("nodeId"), int):
            node_id = int(data["nodeId"])
        elif isinstance(data, dict) and isinstance(data.get("NodeId"), int):
            node_id = int(data["NodeId"])
        else:
            raise RuntimeError(f"创建资源节点返回异常：{data}")
        self.created["campusNodeIds"].append(node_id)
        return node_id

    def create_floor(self, node_id: int, suffix: str) -> int:
        # 生成大尺寸示意图，避免测试图过小导致楼层页不可读
        floor_png = build_demo_floor_png(self.floor_width, self.floor_height, self.floor_style)
        upload_resp = self.call_multipart(
            "POST",
            "/api/floor/upload",
            fields={},
            files={"file": (f"smoke_floor_{suffix}.png", floor_png, "image/png")},
        )
        upload_data = upload_resp.get("data") or {}
        file_path = str((upload_data.get("filePath") or "")).strip()
        if not file_path:
            raise RuntimeError(f"图纸上传返回异常：{upload_resp}")
        self.extra["floorFilePaths"].append(file_path)
        resp = self.call(
            "POST",
            "/api/floor/create",
            {
                "NodeId": node_id,
                "FilePath": file_path,
                "ScaleRatio": 1.0,
            },
        )
        floor_id = self.read_id(resp.get("data"), "floorId")
        self.created["floorIds"].append(floor_id)
        return floor_id

    def create_camera(self, floor_id: int, device_id: int, channel_no: int, pos_x: float, pos_y: float) -> int:
        resp = self.call(
            "POST",
            "/api/camera/create",
            {
                "FloorId": floor_id,
                "DeviceId": device_id,
                "ChannelNo": int(channel_no),
                "PosX": float(pos_x),
                "PosY": float(pos_y),
            },
        )
        camera_id = self.read_id(resp.get("data"), "cameraId")
        self.created["cameraIds"].append(camera_id)
        return camera_id

    def seed_camera_points(self, floor_id: int, device_id: int, camera_count: int) -> list[int]:
        """为摄像头布点页面注入一批测试点位（按 900x520 画布坐标生成）。"""
        total = max(1, int(camera_count))
        # 画布尺寸与 camera.html 默认保持一致
        w, h = 900.0, 520.0
        pad_x, pad_y = 70.0, 60.0
        x1, x2 = pad_x, max(pad_x + 10.0, w - pad_x)
        y1, y2 = pad_y, max(pad_y + 10.0, h - pad_y)

        # 近似方阵网格，点位均匀分布，便于演示拖拽与保存
        cols = max(1, int(round(total ** 0.5)))
        rows = max(1, (total + cols - 1) // cols)
        dx = 0.0 if cols <= 1 else (x2 - x1) / (cols - 1)
        dy = 0.0 if rows <= 1 else (y2 - y1) / (rows - 1)

        created: list[int] = []
        channel = 1
        for i in range(total):
            c = i % cols
            r = i // cols
            x = x1 + dx * c
            y = y1 + dy * r
            camera_id = self.create_camera(floor_id, device_id, channel, x, y)
            created.append(camera_id)
            channel += 1
        return created

    def save_roi(self, camera_id: int, room_node_id: int) -> int:
        vertices = [
            {"x": 120, "y": 110},
            {"x": 280, "y": 130},
            {"x": 260, "y": 280},
            {"x": 130, "y": 250},
        ]
        resp = self.call(
            "POST",
            "/api/roi/save",
            {
                "CameraId": camera_id,
                "RoomNodeId": room_node_id,
                "VerticesJson": json.dumps(vertices, ensure_ascii=False),
            },
        )
        roi_id = self.read_id(resp.get("data"), "roiId")
        self.created["roiIds"].append(roi_id)
        return roi_id

    def create_capture_mock(self, device_id: int, scene: str, is_abnormal: bool = False) -> int:
        return self.create_capture_mock_with_metadata(
            device_id=device_id,
            metadata_payload={
                "source": "smoke-seed",
                "scene": scene,
                "tag": "异常" if is_abnormal else "正常",
                "createdAt": time.strftime("%Y-%m-%d %H:%M:%S"),
            },
        )

    def create_capture_mock_with_metadata(self, device_id: int, metadata_payload: dict[str, Any]) -> int:
        metadata_payload = {
            **metadata_payload,
        }
        resp = self.call(
            "POST",
            "/api/capture/mock",
            {
                "DeviceId": device_id,
                "ChannelNo": 1,
                "MetadataJson": json.dumps(metadata_payload, ensure_ascii=False),
            },
        )
        capture_id = self.read_id(resp.get("data"), "captureId")
        self.created["captureIds"].append(capture_id)
        return capture_id

    def create_alert(self, title: str, detail: str) -> int:
        resp = self.call(
            "POST",
            "/api/alert/create",
            {"AlertType": title, "Detail": detail},
        )
        alert_id = self.read_id(resp.get("data"), "alertId")
        self.created["alertIds"].append(alert_id)
        return alert_id

    def create_role(self, role_name: str, permissions: list[str]) -> int:
        resp = self.call(
            "POST",
            "/api/role/create",
            {"RoleName": role_name, "PermissionJson": json.dumps(permissions, ensure_ascii=False)},
        )
        role_id = self.read_id(resp.get("data"), "roleId")
        self.created["roleIds"].append(role_id)
        return role_id

    def create_user(self, user_name: str, display_name: str, role_id: int) -> int:
        resp = self.call(
            "POST",
            "/api/user/create",
            {
                "UserName": user_name,
                "DisplayName": display_name,
                "Password": "Aura@123456",
                "RoleId": role_id,
            },
        )
        user_id = self.read_id(resp.get("data"), "userId")
        self.created["userIds"].append(user_id)
        return user_id

    def seed_track_events(self, camera_id: int, vid: str) -> None:
        points = [(150.0, 160.0), (180.0, 190.0), (220.0, 210.0)]
        for x, y in points:
            self.call(
                "POST",
                "/api/space/collision/check",
                {"Vid": vid, "CameraId": camera_id, "PosX": x, "PosY": y},
            )
        self.extra["trackVids"].append(vid)

    def seed_track_events_with_time(self, camera_id: int, vid: str, event_time_iso: str) -> None:
        points = [(152.0, 162.0), (186.0, 198.0), (228.0, 216.0)]
        for x, y in points:
            self.call(
                "POST",
                "/api/space/collision/check",
                {"Vid": vid, "CameraId": camera_id, "PosX": x, "PosY": y, "EventTime": event_time_iso},
            )
        self.extra["trackVids"].append(vid)

    def seed_audit_logs(self, paths: list[str]) -> None:
        for i, page_path in enumerate(paths):
            self.call(
                "POST",
                "/api/audit/page-view",
                {
                    "PagePath": page_path,
                    "PageTitle": f"冒烟演示页面-{i + 1}",
                    "EventType": "leave",
                    "StayMs": 10000 + i * 500,
                    "SessionId": f"smoke-session-{i + 1}",
                },
            )

    def run_daily_judge(self) -> None:
        self.run_daily_judge_for_date(time.strftime("%Y-%m-%d"))

    def run_daily_judge_for_date(self, date_text: str) -> None:
        try:
            self.call(
                "POST",
                "/api/judge/run/daily",
                {"Date": date_text, "CutoffHour": 23},
            )
        except RuntimeError as ex:
            # 开发环境中每日研判存在分钟级限流；注入数据不应因此整体失败
            if "HTTP 429" in str(ex) or "42901" in str(ex):
                print("[提示] 每日研判触发限流，已跳过本次执行。")
                return
            raise

    def seed_last_7_days_distribution(self, device_id: int, camera_id: int, stamp: str) -> None:
        today = dt.date.today()
        for day_offset in range(6, -1, -1):
            day = today - dt.timedelta(days=day_offset)
            day_text = day.strftime("%Y-%m-%d")
            self.extra["timeSeriesDays"].append(day_text)

            is_weekend = day.weekday() >= 5
            # 趋势模板：
            # - campus: 工作日高、周末低（校园常态）
            # - holiday: 周末/假期高、工作日低（节假日态势）
            # - exam: 考试周夜间高压（夜间抓拍与异常更密集）
            profile = self.trend_profile.strip().lower()
            if profile == "holiday":
                track_count = 3 if is_weekend else 1
                capture_count = 5 if is_weekend else 2
                alert_count = 3 if is_weekend else 1
            elif profile == "exam":
                track_count = 4 if not is_weekend else 3
                capture_count = 7 if not is_weekend else 5
                alert_count = 4 if not is_weekend else 3
            else:
                track_count = 1 if is_weekend else 3
                capture_count = 2 if is_weekend else 5
                alert_count = 1 if is_weekend else 3

            for i in range(track_count):
                event_hour = 20 + i
                event_time = dt.datetime.combine(day, dt.time(event_hour, 20, 0)).isoformat()
                vid = f"VID-TS-{stamp}-{day.strftime('%m%d')}-{i + 1}"
                self.seed_track_events_with_time(camera_id, vid, event_time)

            for i in range(capture_count):
                if profile == "exam":
                    hour = 20 + (i % 4)
                else:
                    hour = 18 + (i % 5)
                minute = 10 * (i % 6)
                self.create_capture_mock_with_metadata(
                    device_id=device_id,
                    metadata_payload={
                        "source": "smoke-seed",
                        "scene": "近7天趋势样本",
                        "tag": (
                            "异常"
                            if (profile == "exam" and i % 3 != 0) or (profile != "exam" and (not is_weekend and i % 2 == 0))
                            else "正常"
                        ),
                        "logicalDate": day_text,
                        "createdAt": f"{day_text} {hour:02d}:{minute:02d}:00",
                        "dayType": "weekend" if is_weekend else "workday",
                    },
                )

            for i in range(alert_count):
                self.create_alert(
                    "趋势样例告警",
                    f"{day_text} {'周末' if is_weekend else '工作日'}样例告警-{i + 1}（{profile}）",
                )

        # 触发一次研判。受接口限流影响，连续多天研判会被 429 限流。
        self.run_daily_judge()

    def seed(self) -> None:
        stamp = str(int(time.time()))
        self.login()
        self.ensure_health()

        device_id = self.create_device(stamp)
        campus_id = self.create_campus_node(None, "campus", f"冒烟园区-{stamp}")
        building_id = self.create_campus_node(campus_id, "building", "S1栋")
        floor_node_id = self.create_campus_node(building_id, "floor", "1层")
        room_node_id = self.create_campus_node(floor_node_id, "room", "101室")

        floor_id = self.create_floor(floor_node_id, stamp)
        camera_ids = self.seed_camera_points(floor_id, device_id, self.camera_count)
        main_camera_id = camera_ids[0]
        self.save_roi(main_camera_id, room_node_id)
        self.seed_track_events(main_camera_id, f"VID-SMOKE-{stamp}")

        self.create_capture_mock(device_id, "常规巡查", is_abnormal=False)
        self.create_capture_mock(device_id, "异常滞留", is_abnormal=True)
        self.create_capture_mock(device_id, "夜间异常", is_abnormal=True)

        self.create_alert("冒烟告警", "用于前端冒烟回归的样例告警")
        self.create_alert("门禁异常", "宿舍楼夜间进出频次异常")

        role_id = self.create_role(
            f"smoke_role_{stamp}",
            [
                "campus",
                "floor",
                "camera",
                "roi",
                "device",
                "capture",
                "scene",
                "alert",
                "judge",
                "track",
                "search",
                "stats",
                "log",
            ],
        )
        self.create_user(f"smoke_user_{stamp}", "冒烟用户", role_id)
        self.create_user(f"smoke_guard_{stamp}", "巡查员", 2)
        self.seed_audit_logs(
            [
                "/campus/",
                "/floor/",
                "/camera/",
                "/roi/",
                "/capture/",
                "/alert/",
                "/judge/",
                "/track/",
                "/search/",
                "/stats/",
                "/log/",
                "/role/",
                "/user/",
            ]
        )
        self.seed_last_7_days_distribution(device_id, main_camera_id, stamp)

    def print_summary(self) -> None:
        print("\n[完成] 冒烟数据注入成功，新增资源如下：")
        for key, values in self.created.items():
            if values:
                print(f"  - {key}: {values}")
        for key, values in self.extra.items():
            if values:
                print(f"  - {key}: {values}")
        print("\n[建议] 现在可直接打开以下页面验证：")
        print("  - /campus/ 资源树")
        print("  - /floor/ 楼层图")
        print("  - /camera/ 摄像头布点")
        print("  - /roi/ 重点防区")
        print("  - /capture/ 抓拍记录")
        print("  - /alert/ 告警中心")
        print("  - /judge/ 归寝研判")
        print("  - /stats/ 统计驾驶舱")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="注入 Aura 冒烟测试数据")
    parser.add_argument("--base-url", default="https://localhost:5001", help="后端服务地址")
    parser.add_argument("--user", default="admin", help="登录用户名")
    parser.add_argument("--password", default="123456", help="登录密码")
    parser.add_argument("--insecure", action="store_true", help="忽略 HTTPS 证书校验（本机开发证书可用）")
    parser.add_argument(
        "--trend-profile",
        default="campus",
        choices=["campus", "holiday", "exam"],
        help="近7天趋势模板：campus=工作日高周末低，holiday=周末高工作日低，exam=考试周夜间高压",
    )
    parser.add_argument("--floor-width", type=int, default=1280, help="注入楼层图宽度，最小 320")
    parser.add_argument("--floor-height", type=int, default=720, help="注入楼层图高度，最小 240")
    parser.add_argument(
        "--floor-style",
        default="grid",
        choices=["grid", "blueprint", "zoning", "blueprint-plus", "dorm-layout"],
        help="楼层图风格：grid=网格示意，blueprint=蓝图墙线，zoning=彩色分区，blueprint-plus=增强蓝图，dorm-layout=宿舍布局草图",
    )
    parser.add_argument("--camera-count", type=int, default=12, help="注入摄像头测试点位数量（默认 12）")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    seeder = AuraSeeder(
        base_url=args.base_url,
        username=args.user,
        password=args.password,
        insecure=args.insecure,
        trend_profile=args.trend_profile,
        floor_width=args.floor_width,
        floor_height=args.floor_height,
        floor_style=args.floor_style,
        camera_count=args.camera_count,
    )
    try:
        seeder.seed()
        seeder.print_summary()
        return 0
    except Exception as ex:
        print(f"[失败] 注入冒烟数据失败：{ex}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
