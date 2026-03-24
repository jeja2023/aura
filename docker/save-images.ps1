# 文件：Docker 镜像离线导出脚本（save-images.ps1） | File: Docker Save Images Script

$ErrorActionPreference = "Stop"

$apiRepo = if ($env:API_IMAGE_REPO) { $env:API_IMAGE_REPO } else { "aura-api" }
$aiRepo = if ($env:AI_IMAGE_REPO) { $env:AI_IMAGE_REPO } else { "aura-ai" }
$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { throw "请先设置 IMAGE_TAG。" }
$outDir = if ($env:IMAGE_ARCHIVE_DIR) { $env:IMAGE_ARCHIVE_DIR } else { "docker\dist" }

if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$apiImage = "$apiRepo`:$tag"
$aiImage = "$aiRepo`:$tag"
$archive = Join-Path $outDir "aura-images-$tag.tar"

docker save -o "$archive" $apiImage $aiImage
if ($LASTEXITCODE -ne 0) {
    throw "镜像导出失败。"
}

Write-Host "[RESULT] 导出完成：$archive"
