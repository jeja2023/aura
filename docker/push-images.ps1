# 文件：Docker 镜像推送脚本（push-images.ps1） | File: Docker Push Images Script

$ErrorActionPreference = "Stop"

$apiRepo = if ($env:API_IMAGE_REPO) { $env:API_IMAGE_REPO } else { "aura-api" }
$aiRepo = if ($env:AI_IMAGE_REPO) { $env:AI_IMAGE_REPO } else { "aura-ai" }
$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { throw "请先设置 IMAGE_TAG。" }

$apiImage = "$apiRepo`:$tag"
$aiImage = "$aiRepo`:$tag"

Write-Host "推送镜像：$apiImage"
docker push $apiImage
if ($LASTEXITCODE -ne 0) { throw "推送失败：$apiImage" }

Write-Host "推送镜像：$aiImage"
docker push $aiImage
if ($LASTEXITCODE -ne 0) { throw "推送失败：$aiImage" }

Write-Host "[RESULT] 镜像推送完成。"
