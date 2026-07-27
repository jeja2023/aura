#!/usr/bin/env python3
"""Run the Aura eight-check adapter contract suite and emit auditable JSON evidence."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import pathlib
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

CHECK_CODES = (
    "manifest_valid", "health", "discovery", "sample", "timeout",
    "error_mapping", "credentials", "ssrf",
)
REQUIRED_MANIFEST_FIELDS = (
    "schemaVersion", "code", "name", "version", "protocol", "lifecycle",
    "implementationStatus", "auraVersions", "capabilities", "configuration",
    "contract", "security", "support",
)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001
        raise urllib.error.HTTPError(req.full_url, code, "redirect blocked", headers, fp)


def result(code: str, status: str, detail: str, **metrics: object) -> dict[str, object]:
    return {"code": code, "status": status, "detail": detail, "metrics": metrics}


def load_manifest(path: pathlib.Path) -> tuple[dict[str, object], list[str]]:
    document = json.loads(path.read_text(encoding="utf-8"))
    errors = [f"missing field: {field}" for field in REQUIRED_MANIFEST_FIELDS if field not in document]
    if document.get("schemaVersion") != "1.0":
        errors.append("schemaVersion must be 1.0")
    if document.get("lifecycle") not in {"experimental", "certified", "deprecated", "unsupported"}:
        errors.append("invalid lifecycle")
    if document.get("implementationStatus") not in {"supported", "experimental", "planned", "deprecated"}:
        errors.append("invalid implementationStatus")
    contract = document.get("contract")
    if not isinstance(contract, dict):
        errors.append("contract must be an object")
    else:
        for field in ("healthPath", "discoveryPath", "samplePath", "timeoutSeconds", "errorCodes"):
            if field not in contract:
                errors.append(f"missing contract field: {field}")
    return document, errors


def validate_base_url(value: str, allow_http: bool) -> str:
    parsed = urllib.parse.urlsplit(value)
    allowed_schemes = {"https"} | ({"http"} if allow_http else set())
    if parsed.scheme not in allowed_schemes or not parsed.hostname:
        raise ValueError("base URL must have an approved HTTP(S) scheme and host")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("base URL cannot contain credentials, query, or fragment")
    return value.rstrip("/")


def probe(
    opener: urllib.request.OpenerDirector,
    base_url: str,
    path: str | None,
    timeout: float,
    authorization: str | None,
    max_bytes: int,
) -> tuple[int, int, float, str]:
    if not path:
        raise ValueError("manifest has no endpoint path for this check")
    url = base_url + "/" + str(path).lstrip("/")
    parsed = urllib.parse.urlsplit(url)
    if parsed.netloc != urllib.parse.urlsplit(base_url).netloc:
        raise ValueError("contract path escaped the approved adapter origin")
    headers = {"Accept": "application/json, application/xml, image/*;q=0.8, */*;q=0.1"}
    if authorization:
        headers["Authorization"] = authorization
    request = urllib.request.Request(url, headers=headers, method="GET")
    started = time.monotonic()
    with opener.open(request, timeout=timeout) as response:
        content = response.read(max_bytes + 1)
        if len(content) > max_bytes:
            raise ValueError(f"response exceeded {max_bytes} bytes")
        elapsed = time.monotonic() - started
        digest = hashlib.sha256(content).hexdigest()
        return response.status, len(content), elapsed, digest


def submit_run(args: argparse.Namespace, report: dict[str, object], report_path: pathlib.Path) -> dict[str, object] | None:
    if not args.aura_api:
        return None
    if not args.adapter_id or not args.aura_token:
        raise ValueError("--adapter-id and --aura-token are required with --aura-api")
    payload = {
        "adapterId": args.adapter_id,
        "deviceModel": args.device_model,
        "firmwareVersion": args.firmware,
        "environment": report["environment"],
        "checks": [{"code": item["code"], "status": item["status"], "detail": item["detail"]} for item in report["checks"]],
        "reportUri": report_path.resolve().as_uri(),
    }
    request = urllib.request.Request(
        args.aura_api.rstrip("/") + f"/api/v1/integrations/adapters/{args.adapter_id}/contract-runs",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Authorization": f"Bearer {args.aura_token}", "Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read(1024 * 1024).decode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    parser.add_argument("--base-url")
    parser.add_argument("--device-model", required=True)
    parser.add_argument("--firmware", required=True)
    parser.add_argument("--report", required=True, type=pathlib.Path)
    parser.add_argument("--real-device", action="store_true")
    parser.add_argument("--allow-http", action="store_true")
    parser.add_argument("--authorization", help="Authorization header used for probes; never written to evidence")
    parser.add_argument("--ca-file")
    parser.add_argument("--max-response-bytes", type=int, default=4 * 1024 * 1024)
    parser.add_argument("--aura-api")
    parser.add_argument("--adapter-id", type=int)
    parser.add_argument("--aura-token")
    args = parser.parse_args()

    checks: list[dict[str, object]] = []
    try:
        manifest, errors = load_manifest(args.manifest)
    except (OSError, json.JSONDecodeError) as exc:
        manifest, errors = {}, [str(exc)]
    checks.append(result("manifest_valid", "passed" if not errors else "failed", "; ".join(errors) if errors else "Manifest structure is valid"))
    contract = manifest.get("contract") if isinstance(manifest.get("contract"), dict) else {}
    security = manifest.get("security") if isinstance(manifest.get("security"), dict) else {}
    timeout = float(contract.get("timeoutSeconds", 15)) if contract else 15.0
    base_url_error = None
    base_url = None
    if args.base_url:
        try:
            base_url = validate_base_url(args.base_url, args.allow_http)
        except ValueError as exc:
            base_url_error = str(exc)
    else:
        base_url_error = "--base-url is required for runtime checks"

    context = ssl.create_default_context(cafile=args.ca_file)
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=context))
    for code, path_field in (("health", "healthPath"), ("discovery", "discoveryPath"), ("sample", "samplePath")):
        if base_url_error:
            checks.append(result(code, "blocked", base_url_error))
            continue
        try:
            status, size, elapsed, digest = probe(
                opener, base_url, contract.get(path_field), timeout, args.authorization,
                max(1024, min(args.max_response_bytes, 64 * 1024 * 1024)),
            )
            checks.append(result(code, "passed" if 200 <= status < 300 else "failed", f"HTTP {status}", responseBytes=size, elapsedMs=round(elapsed * 1000, 2), sha256=digest))
        except urllib.error.HTTPError as exc:
            checks.append(result(code, "failed", f"HTTP {exc.code}: {exc.reason}"))
        except (urllib.error.URLError, TimeoutError, ValueError, OSError) as exc:
            checks.append(result(code, "failed", str(exc)))

    timeout_ok = 1 <= timeout <= 120
    checks.append(result("timeout", "passed" if timeout_ok else "failed", f"Bounded client timeout is {timeout:g} seconds", timeoutSeconds=timeout))
    error_codes = contract.get("errorCodes") if isinstance(contract, dict) else None
    required_error_concepts = ("timeout", "authentication")
    error_text = json.dumps(error_codes or {}, ensure_ascii=False).lower()
    mapping_ok = isinstance(error_codes, dict) and bool(error_codes) and all(concept in error_text for concept in required_error_concepts)
    checks.append(result("error_mapping", "passed" if mapping_ok else "failed", "Stable authentication and timeout mappings are declared" if mapping_ok else "Error mapping must include authentication and timeout concepts"))
    credentials_ok = bool(security.get("secretReferencesOnly")) and bool(security.get("credentialMode"))
    checks.append(result("credentials", "passed" if credentials_ok else "failed", "Credentials use references and no credential value is written to evidence" if credentials_ok else "Secret-reference credential policy is incomplete"))
    ssrf_ok = bool(security.get("ssrfPolicy")) and base_url_error is None
    checks.append(result("ssrf", "passed" if ssrf_ok else "failed", "Origin is fixed, redirects are blocked, and the manifest declares an SSRF policy" if ssrf_ok else (base_url_error or "SSRF policy is missing")))

    if manifest.get("implementationStatus") == "planned":
        overall = "blocked"
    elif not args.real_device:
        overall = "blocked"
    elif any(item["status"] != "passed" for item in checks):
        overall = "failed"
    else:
        overall = "passed"
    report = {
        "schemaVersion": "1.0",
        "adapter": {"code": manifest.get("code"), "version": manifest.get("version"), "manifest": str(args.manifest)},
        "device": {"model": args.device_model, "firmware": args.firmware},
        "environment": {
            "realDevice": args.real_device,
            "baseUrlHost": urllib.parse.urlsplit(base_url).hostname if base_url else None,
            "transport": urllib.parse.urlsplit(base_url).scheme if base_url else None,
            "allowHttp": args.allow_http,
            "credentialSupplied": bool(args.authorization),
        },
        "checks": checks,
        "status": overall,
        "executedAt": datetime.now(timezone.utc).isoformat(),
        "requiredChecks": list(CHECK_CODES),
        "certificationRule": "All checks must pass with realDevice=true and a persisted report URI",
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    try:
        submission = submit_run(args, report, args.report)
    except (ValueError, urllib.error.URLError, urllib.error.HTTPError, json.JSONDecodeError) as exc:
        print(f"Adapter evidence written, but Aura submission failed: {exc}", file=sys.stderr)
        return 2
    if submission is not None:
        print(json.dumps(submission, ensure_ascii=False))
    print(f"Adapter contract status: {overall}; report: {args.report}")
    return 0 if overall == "passed" else 3


if __name__ == "__main__":
    raise SystemExit(main())
