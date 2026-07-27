#!/usr/bin/env python3
"""Generate a commercial release-gate evidence bundle without inventing field evidence."""

from __future__ import annotations

import argparse
import json
import locale
import os
import platform
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


REQUIRED_CHECKS = (
    "dotnet_tests",
    "python_tests",
    "frontend_lint",
    "postgres_migrations",
    "arango_real",
    "pgvector_target_scale",
    "backlog_recovery",
    "backup_restore",
    "upgrade_rollback",
    "browser_matrix",
    "linux_scripts",
    "security_privacy",
    "oidc_idp",
    "real_device_adapter",
)
AUTOMATED_CHECKS = {
    # Release candidates are built before this gate. Reusing those binaries keeps the
    # test run independent from a locally running API that may lock Aura.Api.exe.
    "dotnet_tests": (
        ["dotnet", "test", "Aura.sln", "-c", "Release", "--no-build", "--no-restore", "-p:NodeReuse=false"],
        ".",
    ),
    "python_tests": ([sys.executable, "-m", "pytest", "-p", "no:cacheprovider"], "ai"),
    "frontend_lint": (["npm", "run", "lint"], "frontend"),
    "postgres_migrations": (
        ["dotnet", "run", "--project", "backend/Aura.DbMigrator", "--", "status", "--command-timeout", "300", "--lock-timeout", "60"],
        ".",
    ),
}
SENSITIVE_KEY = re.compile(r"password|passwd|secret|token|authorization|api.?key|private.?key|connectionstring", re.I)
SENSITIVE_TEXT = re.compile(
    r"(?i)(password|passwd|secret|token|authorization|api[_-]?key|private[_-]?key)(\s*[:=]\s*)([^\s,;]+)"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Aura commercial release gate")
    parser.add_argument("--profile", required=True, help="Approved service-profile JSON")
    parser.add_argument("--output", default="artifacts/release-gate", help="Report output directory")
    parser.add_argument("--evidence-dir", help="Directory containing <check_code>.json field evidence")
    parser.add_argument("--build-version", help="Candidate build version")
    parser.add_argument("--git-commit", help="Candidate git commit")
    parser.add_argument("--image-digest", default="", help="Candidate image digest")
    parser.add_argument("--migration-version", default="037", help="Expected database migration")
    parser.add_argument("--run-automated", action="store_true", help="Run repository checks")
    parser.add_argument("--real-dependencies", action="store_true", help="Assert evidence used real dependencies")
    parser.add_argument("--target-hardware", action="store_true", help="Assert execution used approved target hardware")
    parser.add_argument("--secret-scan-clean", action="store_true", help="Assert the candidate passed the approved secret scan")
    return parser.parse_args()


def command_output(command: list[str], cwd: Path, timeout: int = 30) -> str:
    try:
        result = subprocess.run(command, cwd=cwd, capture_output=True, timeout=timeout, check=False)
        return decode_output(result.stdout or result.stderr).strip()
    except (OSError, subprocess.SubprocessError) as exc:
        return str(exc)


def decode_output(value: bytes | str | None) -> str:
    if value is None or isinstance(value, str):
        return value or ""
    encodings = ["utf-8", locale.getpreferredencoding(False), "gbk"]
    for encoding in dict.fromkeys(encodings):
        try:
            return value.decode(encoding)
        except (LookupError, UnicodeDecodeError):
            continue
    return value.decode("utf-8", errors="replace")


def default_version(root: Path) -> str:
    content = (root / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", content)
    return match.group(1) if match else "unknown"


def sanitize(value: Any) -> Any:
    if isinstance(value, dict):
        return {key: "[REDACTED]" if SENSITIVE_KEY.search(str(key)) else sanitize(item) for key, item in value.items()}
    if isinstance(value, list):
        return [sanitize(item) for item in value]
    if isinstance(value, str):
        return SENSITIVE_TEXT.sub(r"\1\2[REDACTED]", value)
    return value


def summarize(text: str, max_lines: int = 60) -> str:
    lines = sanitize(text).splitlines()
    return "\n".join(lines[-max_lines:])[-12000:]


def validate_profile(profile: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if profile.get("status") != "approved":
        errors.append("service profile status must be approved")
    for key in ("profileCode", "version", "deliveryMode", "profile"):
        if not profile.get(key):
            errors.append(f"service profile is missing {key}")
    approval = profile.get("approval") or {}
    for key in ("reference", "approvedBy", "approvedAt"):
        if not approval.get(key):
            errors.append(f"service profile approval is missing {key}")
    body = profile.get("profile") or {}
    for key in (
        "coreApiAvailability",
        "rpoMinutes",
        "rtoMinutes",
        "graphRebuildMinutes",
        "vectorRecallAt10",
        "vectorP95Ms",
        "graphP95Ms",
        "backlogRecoveryMinutes",
    ):
        if body.get(key) is None:
            errors.append(f"service profile is missing profile.{key}")
    matrix = body.get("supportMatrix") or {}
    for key in (
        "operatingSystems",
        "browsers",
        "postgres",
        "arangodb",
        "redis",
        "objectStorage",
        "containerRuntime",
        "devices",
        "providers",
        "retention",
    ):
        if not isinstance(matrix.get(key), list) or not matrix[key]:
            errors.append(f"service profile is missing profile.supportMatrix.{key}")
    return errors


def load_field_evidence(evidence_dir: Path | None) -> dict[str, dict[str, Any]]:
    evidence: dict[str, dict[str, Any]] = {}
    if evidence_dir is None or not evidence_dir.is_dir():
        return evidence
    for code in REQUIRED_CHECKS:
        path = evidence_dir / f"{code}.json"
        if not path.is_file():
            continue
        try:
            item = json.loads(path.read_text(encoding="utf-8"))
            status = str(item.get("status", "blocked")).lower()
            if status not in {"passed", "failed", "blocked", "not_run"}:
                status = "blocked"
            if status == "passed" and (item.get("exitCode") != 0 or not item.get("artifactUri")):
                status = "blocked"
                item["logSummary"] = "Passed evidence requires exitCode=0 and artifactUri."
            item.update({"checkCode": code, "status": status, "source": str(path)})
            evidence[code] = sanitize(item)
        except (OSError, json.JSONDecodeError) as exc:
            evidence[code] = {
                "checkCode": code,
                "status": "blocked",
                "exitCode": None,
                "logSummary": f"Invalid evidence file: {exc}",
                "artifactUri": str(path),
            }
    return evidence


def run_check(code: str, command: list[str], cwd: Path, root: Path, output: Path) -> dict[str, Any]:
    started = datetime.now(timezone.utc)
    try:
        process = subprocess.run(command, cwd=cwd, capture_output=True, timeout=1800, check=False)
        exit_code = process.returncode
        raw_output = f"{decode_output(process.stdout)}\n{decode_output(process.stderr)}".strip()
    except (OSError, subprocess.SubprocessError) as exc:
        exit_code = 127
        raw_output = str(exc)
    log_path = output / f"{code}.log"
    log_path.write_text(sanitize(raw_output), encoding="utf-8")
    try:
        artifact_uri = log_path.relative_to(root).as_posix()
    except ValueError:
        artifact_uri = str(log_path)
    return {
        "checkCode": code,
        "status": "passed" if exit_code == 0 else "failed",
        "commandLine": " ".join(command),
        "exitCode": exit_code,
        "environment": {"workingDirectory": str(cwd.relative_to(root)) if cwd != root else "."},
        "metrics": {"durationSeconds": round((datetime.now(timezone.utc) - started).total_seconds(), 3)},
        "logSummary": summarize(raw_output),
        "artifactUri": artifact_uri,
        "source": "automated",
    }


def markdown_report(report: dict[str, Any]) -> str:
    lines = [
        f"# Aura {report['buildVersion']} 商业发布门禁报告",
        "",
        f"- 结论：`{report['status']}`",
        f"- Git commit：`{report['gitCommit']}`",
        f"- 迁移版本：`{report['migrationVersion']}`",
        f"- 服务画像：`{report['serviceProfile']['profileCode']}` v{report['serviceProfile']['version']}",
        f"- 生成时间：`{report['completedAt']}`",
        "",
        "## 环境断言",
        "",
        f"- 已批准服务画像：`{str(report['environmentAssertions']['approvedProfile']).lower()}`",
        f"- 真实依赖：`{str(report['environmentAssertions']['realDependencies']).lower()}`",
        f"- 目标硬件：`{str(report['environmentAssertions']['targetHardware']).lower()}`",
        f"- 秘密扫描通过：`{str(report['environmentAssertions']['secretScanClean']).lower()}`",
        "",
        "## 检查结果",
        "",
        "| 检查 | 状态 | 退出码 | 证据 |",
        "| --- | --- | ---: | --- |",
    ]
    for item in report["evidence"]:
        artifact = str(item.get("artifactUri") or "-").replace("|", "\\|")
        lines.append(f"| `{item['checkCode']}` | `{item['status']}` | {item.get('exitCode', '-')} | {artifact} |")
    if report["profileValidationErrors"]:
        lines.extend(["", "## 服务画像错误", ""])
        lines.extend(f"- {error}" for error in report["profileValidationErrors"])
    lines.extend(
        [
            "",
            "## 判定说明",
            "",
            "全部 14 项检查必须为 `passed`，且服务画像、真实依赖、目标硬件和秘密扫描断言均成立。缺失的现场证据保持为 `blocked`，不得用本机 smoke 结果替代。",
            "",
        ]
    )
    return "\n".join(lines)


def main() -> int:
    args = parse_args()
    root = Path(__file__).resolve().parents[2]
    profile_path = Path(args.profile).resolve()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    try:
        profile = json.loads(profile_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"Unable to read service profile: {exc}", file=sys.stderr)
        return 2

    profile_errors = validate_profile(profile)
    evidence_dir = Path(args.evidence_dir).resolve() if args.evidence_dir else None
    evidence = load_field_evidence(evidence_dir)
    if args.run_automated:
        for code, (command, relative_cwd) in AUTOMATED_CHECKS.items():
            executable = command[0]
            if os.name == "nt" and executable == "npm":
                command = ["npm.cmd", *command[1:]]
            evidence[code] = run_check(code, command, root / relative_cwd, root, output)

    for code in REQUIRED_CHECKS:
        evidence.setdefault(
            code,
            {
                "checkCode": code,
                "status": "blocked",
                "commandLine": None,
                "exitCode": None,
                "environment": {},
                "metrics": {},
                "logSummary": "Required evidence was not supplied.",
                "artifactUri": None,
                "source": "missing",
            },
        )

    commit = args.git_commit or command_output(["git", "rev-parse", "HEAD"], root) or "unknown"
    build_version = args.build_version or default_version(root)
    assertions = {
        "approvedProfile": not profile_errors,
        "realDependencies": args.real_dependencies,
        "targetHardware": args.target_hardware,
        "secretScanClean": args.secret_scan_clean,
    }
    passed = all(item["status"] == "passed" for item in evidence.values()) and all(assertions.values())
    completed = datetime.now(timezone.utc).isoformat()
    environment = {
        "operatingSystem": platform.platform(),
        "machine": platform.machine(),
        "python": platform.python_version(),
        "dotnet": command_output(["dotnet", "--version"], root),
        "node": command_output(["node", "--version"], root),
        "ci": os.environ.get("CI", "false"),
        "ciProvider": next((name for name in ("GITHUB_ACTIONS", "GITLAB_CI", "TF_BUILD", "JENKINS_URL") if os.environ.get(name)), None),
    }
    report = sanitize(
        {
            "schemaVersion": "1.0",
            "status": "passed" if passed else "blocked",
            "buildVersion": build_version,
            "gitCommit": commit,
            "imageDigest": args.image_digest or None,
            "migrationVersion": args.migration_version,
            "serviceProfile": {
                "profileCode": profile.get("profileCode", "unknown"),
                "version": profile.get("version", "unknown"),
                "deliveryMode": profile.get("deliveryMode", "unknown"),
                "approval": profile.get("approval", {}),
                "source": str(profile_path),
            },
            "profileValidationErrors": profile_errors,
            "environment": environment,
            "environmentAssertions": assertions,
            "requiredChecks": list(REQUIRED_CHECKS),
            "evidence": [evidence[code] for code in REQUIRED_CHECKS],
            "completedAt": completed,
        }
    )
    stem = f"release-gate-{build_version}-{commit[:12]}"
    json_path = output / f"{stem}.json"
    markdown_path = output / f"{stem}.md"
    json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    markdown_path.write_text(markdown_report(report), encoding="utf-8")
    print(f"Release gate: {report['status']}")
    print(f"JSON: {json_path}")
    print(f"Markdown: {markdown_path}")
    return 0 if passed else 2


if __name__ == "__main__":
    raise SystemExit(main())
