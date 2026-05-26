# File: Docker Save Images Script

$ErrorActionPreference = "Stop"

$apiRepo = if ($env:API_IMAGE_REPO) { $env:API_IMAGE_REPO } else { "aura-api" }
$aiRepo = if ($env:AI_IMAGE_REPO) { $env:AI_IMAGE_REPO } else { "aura-ai" }
$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { throw "Set IMAGE_TAG first." }
$outDir = if ($env:IMAGE_ARCHIVE_DIR) { $env:IMAGE_ARCHIVE_DIR } else { "docker\dist" }

if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$apiImage = "$apiRepo`:$tag"
$aiImage = "$aiRepo`:$tag"
$archive = Join-Path $outDir "aura-images-$tag.tar"

docker save -o "$archive" $apiImage $aiImage
if ($LASTEXITCODE -ne 0) {
    throw "Image export failed."
}

Write-Host "[RESULT] Exported: $archive"
