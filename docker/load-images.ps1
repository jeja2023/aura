# 文件：Docker 镜像离线导入脚本（load-images.ps1） | File: Docker Load Images Script

$ErrorActionPreference = "Stop"

$archive = if ($env:IMAGE_ARCHIVE_FILE) { $env:IMAGE_ARCHIVE_FILE } else { throw "请先设置 IMAGE_ARCHIVE_FILE（tar 包路径）。" }

if (-not (Test-Path $archive)) {
    throw "未找到镜像包：$archive"
}

docker load -i "$archive"
if ($LASTEXITCODE -ne 0) {
    throw "镜像导入失败：$archive"
}

Write-Host "[RESULT] 导入完成：$archive"
