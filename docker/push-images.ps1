# File: Docker Push Images Script

$ErrorActionPreference = "Stop"

$apiRepo = if ($env:API_IMAGE_REPO) { $env:API_IMAGE_REPO } else { "aura-api" }
$aiRepo = if ($env:AI_IMAGE_REPO) { $env:AI_IMAGE_REPO } else { "aura-ai" }
$simulatorRepo = if ($env:MEDIA_PROVIDER_SIMULATOR_IMAGE_REPO) { $env:MEDIA_PROVIDER_SIMULATOR_IMAGE_REPO } else { "aura-media-provider-simulator" }
$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { throw "Set IMAGE_TAG first." }

$apiImage = "$apiRepo`:$tag"
$aiImage = "$aiRepo`:$tag"
$simulatorImage = "$simulatorRepo`:$tag"

Write-Host "Pushing image: $apiImage"
docker push $apiImage
if ($LASTEXITCODE -ne 0) { throw "Push failed: $apiImage" }

Write-Host "Pushing image: $aiImage"
docker push $aiImage
if ($LASTEXITCODE -ne 0) { throw "Push failed: $aiImage" }

Write-Host "Pushing image: $simulatorImage"
docker push $simulatorImage
if ($LASTEXITCODE -ne 0) { throw "Push failed: $simulatorImage" }

Write-Host "[RESULT] Images pushed."
