# File: Docker Load Images Script

$ErrorActionPreference = "Stop"

$archive = if ($env:IMAGE_ARCHIVE_FILE) { $env:IMAGE_ARCHIVE_FILE } else { throw "Set IMAGE_ARCHIVE_FILE first." }

if (-not (Test-Path $archive)) {
    throw "Image archive not found: $archive"
}

docker load -i "$archive"
if ($LASTEXITCODE -ne 0) {
    throw "Image import failed: $archive"
}

Write-Host "[RESULT] Imported: $archive"
