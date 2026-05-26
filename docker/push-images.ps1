# File: Docker Push Images Script

$ErrorActionPreference = "Stop"

$apiRepo = if ($env:API_IMAGE_REPO) { $env:API_IMAGE_REPO } else { "aura-api" }
$aiRepo = if ($env:AI_IMAGE_REPO) { $env:AI_IMAGE_REPO } else { "aura-ai" }
$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { throw "Set IMAGE_TAG first." }

$apiImage = "$apiRepo`:$tag"
$aiImage = "$aiRepo`:$tag"

Write-Host "Pushing image: $apiImage"
docker push $apiImage
if ($LASTEXITCODE -ne 0) { throw "Push failed: $apiImage" }

Write-Host "Pushing image: $aiImage"
docker push $aiImage
if ($LASTEXITCODE -ne 0) { throw "Push failed: $aiImage" }

Write-Host "[RESULT] Images pushed."
