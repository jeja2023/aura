#!/usr/bin/env python3
"""Validate commercial documentation, contracts, manifests, versions, and migration inventory."""

from __future__ import annotations

import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]
EXPECTED_ADAPTER_CHECKS = {
    "manifest_valid", "health", "discovery", "sample", "timeout",
    "error_mapping", "credentials", "ssrf",
}
ADAPTER_MATRIX_ROWS = {
    "standard-http.json": ("通用 HTTP 媒体解析提供方", "experimental"),
    "onvif-rtsp.json": ("ONVIF", "experimental"),
    "hikvision-isapi.json": ("海康 ISAPI", "experimental"),
    "dahua.json": ("大华设备", "planned"),
    "cpp-sdk-gateway.json": ("C++ SDK 网关", "planned"),
}


def fail(errors: list[str], message: str) -> None:
    errors.append(message)


def validate_links(errors: list[str]) -> None:
    link_pattern = re.compile(r"(?<!!)\[[^\]]+\]\(([^)]+)\)")
    for document in sorted((ROOT / "docs").rglob("*.md")):
        text = document.read_text(encoding="utf-8")
        for raw in link_pattern.findall(text):
            target = raw.strip().split("#", 1)[0].strip().strip("<>")
            if not target or re.match(r"^(?:https?|mailto):", target, re.I):
                continue
            target = target.replace("%20", " ")
            path = (document.parent / target).resolve()
            if not path.exists():
                fail(errors, f"broken documentation link: {document.relative_to(ROOT)} -> {raw}")


def validate_json(errors: list[str]) -> None:
    locations = [ROOT / "adapters", ROOT / "docs" / "contracts", ROOT / "docs" / "commercial"]
    for location in locations:
        for path in sorted(location.rglob("*.json")):
            try:
                json.loads(path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as exc:
                fail(errors, f"invalid JSON: {path.relative_to(ROOT)}: {exc}")


def validate_version(errors: list[str]) -> str:
    project = ET.parse(ROOT / "Directory.Build.props")
    version = project.findtext(".//Version")
    if not version:
        fail(errors, "Directory.Build.props has no Version")
        return "unknown"
    checks = {
        ROOT / "ai" / "main.py": f'version="{version}"',
        ROOT / "docs" / "commercial" / "能力与支持矩阵.md": f"# Aura {version} 能力与支持矩阵",
    }
    for path, marker in checks.items():
        if not path.exists() or marker not in path.read_text(encoding="utf-8"):
            fail(errors, f"version {version} is not synchronized in {path.relative_to(ROOT)}")
    release_records = (ROOT / "docs").glob(f"????-??-??-{version}商业产品化实施与验收记录.md")
    if not any(f"# Aura {version}" in path.read_text(encoding="utf-8") for path in release_records):
        fail(errors, f"version {version} has no synchronized commercial acceptance record")
    return version


def validate_migrations(errors: list[str]) -> None:
    numbers: list[int] = []
    for path in sorted((ROOT / "database" / "migrations").glob("[0-9][0-9][0-9]_*.sql")):
        numbers.append(int(path.name[:3]))
    if not numbers:
        fail(errors, "no numbered migrations found")
        return
    expected = list(range(min(numbers), max(numbers) + 1))
    if numbers != expected:
        fail(errors, f"migration inventory is not contiguous: found {numbers[0]}..{numbers[-1]}")
    readme = (ROOT / "database" / "migrations" / "README.txt").read_text(encoding="utf-8")
    if f"{max(numbers):03d}" not in readme:
        fail(errors, f"migration README does not mention latest migration {max(numbers):03d}")


def validate_adapters(errors: list[str], version: str) -> None:
    matrix = (ROOT / "docs" / "commercial" / "能力与支持矩阵.md").read_text(encoding="utf-8")
    manifests = ROOT / "adapters" / "manifests"
    for name, (label, expected_status) in ADAPTER_MATRIX_ROWS.items():
        path = manifests / name
        if not path.exists():
            fail(errors, f"missing adapter manifest: {path.relative_to(ROOT)}")
            continue
        manifest = json.loads(path.read_text(encoding="utf-8"))
        required = {
            "schemaVersion", "code", "name", "version", "protocol", "lifecycle",
            "implementationStatus", "auraVersions", "capabilities", "configuration",
            "contract", "security", "support",
        }
        missing = sorted(required - set(manifest))
        if missing:
            fail(errors, f"{name} is missing fields: {', '.join(missing)}")
        if manifest.get("implementationStatus") != expected_status and not (
            name in {"standard-http.json", "hikvision-isapi.json"}
            and manifest.get("implementationStatus") == "supported"
            and expected_status == "experimental"
        ):
            fail(errors, f"{name} implementation status disagrees with the capability matrix")
        row_pattern = re.compile(rf"^\|\s*{re.escape(label)}\s*\|\s*{re.escape(expected_status)}\s*\|", re.M)
        if not row_pattern.search(matrix):
            fail(errors, f"capability matrix has no synchronized row for {label}={expected_status}")
        if manifest.get("implementationStatus") not in {"planned", "deprecated"}:
            supported = manifest.get("auraVersions", [])
            if f"{version.rsplit('.', 1)[0]}.x" not in supported:
                fail(errors, f"{name} does not declare compatibility with Aura {version}")

    product_source = (ROOT / "backend" / "Aura.Api" / "Product" / "ProductInsightsService.cs").read_text(encoding="utf-8")
    certify_source = (ROOT / "scripts" / "ops" / "adapter-certify.py").read_text(encoding="utf-8")
    for code in EXPECTED_ADAPTER_CHECKS:
        if f'"{code}"' not in product_source:
            fail(errors, f"backend adapter certification is missing required check {code}")
        if f'"{code}"' not in certify_source:
            fail(errors, f"adapter certification tool is missing required check {code}")


def validate_contracts(errors: list[str]) -> None:
    endpoint_source = (ROOT / "backend" / "Aura.Api" / "Extensions" / "AuraEndpointsProduct.cs").read_text(encoding="utf-8")
    for route in ("/events", "/cases", "/investigations", "/controlled-queries", "/data-lifecycle/jobs", "/mobile", "/analytics"):
        if route not in endpoint_source:
            fail(errors, f"commercial API route is missing: /api/v1{route}")
    for route in ("/{queryPlanId:long}/plan", "/push-config", "/push-subscriptions", "/deep-links", "/cases/{caseId:long}/photos"):
        if route not in endpoint_source:
            fail(errors, f"commercial completion route is missing: {route}")

    workbench_html = (ROOT / "frontend" / "workbench" / "workbench.html").read_text(encoding="utf-8")
    workbench_script = (ROOT / "frontend" / "workbench" / "workbench.js").read_text(encoding="utf-8")
    service_worker = (ROOT / "frontend" / "workbench" / "sw.js").read_text(encoding="utf-8")
    for marker in ('id="loadMyTasks"', 'id="controlledPlan"', 'id="enablePush"', 'id="deepLinkFile"'):
        if marker not in workbench_html:
            fail(errors, f"workbench mobile/controlled-query marker is missing: {marker}")
    for marker in ("/controlled-queries/${queryPlanId}/plan", "/mobile/cases/${caseId}/photos", "pushManager.subscribe"):
        if marker not in workbench_script:
            fail(errors, f"workbench completion flow is missing: {marker}")
    for marker in ('url.pathname.startsWith("/api/")', 'url.pathname.startsWith("/storage/")', 'self.addEventListener("push"', 'self.addEventListener("notificationclick"'):
        if marker not in service_worker:
            fail(errors, f"workbench service-worker invariant is missing: {marker}")

    notification_source = (ROOT / "backend" / "Aura.Api" / "Product" / "NotificationChannelAdapters.cs").read_text(encoding="utf-8")
    if '"web_push"' not in notification_source or "mobile_push_subscription" not in notification_source:
        fail(errors, "Web Push delivery is not connected to active mobile subscriptions")
    start_script = (ROOT / "scripts" / "ops" / "start-local-api.ps1").read_text(encoding="utf-8")
    if '"--configuration", "Release"' not in start_script or '"--no-build"' not in start_script:
        fail(errors, "local commercial API startup must run the prebuilt Release output")
    gate_source = (ROOT / "scripts" / "ops" / "release-gate.py").read_text(encoding="utf-8")
    if 'default="036"' not in gate_source or "decode_output" not in gate_source:
        fail(errors, "release gate must default to migration 036 and safely decode cross-platform command output")
    openapi = ROOT / "docs" / "contracts" / "media-analysis-provider-v1.openapi.yaml"
    if "openapi: 3." not in openapi.read_text(encoding="utf-8"):
        fail(errors, "media provider OpenAPI contract has no OpenAPI 3 version")
    profile = json.loads((ROOT / "docs" / "commercial" / "service-profile.template.json").read_text(encoding="utf-8"))
    for section in ("environment", "load", "profile"):
        if section not in profile:
            fail(errors, f"service profile template is missing {section}")


def main() -> int:
    errors: list[str] = []
    validate_links(errors)
    validate_json(errors)
    version = validate_version(errors)
    validate_migrations(errors)
    validate_adapters(errors, version)
    validate_contracts(errors)
    if errors:
        print("Commercial artifact validation failed:", file=sys.stderr)
        for item in errors:
            print(f"- {item}", file=sys.stderr)
        return 1
    print(f"Commercial artifact validation passed for Aura {version}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
