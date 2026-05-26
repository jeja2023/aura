# File: Docker Registry Login Script

$ErrorActionPreference = "Stop"

$registry = if ($env:REGISTRY_HOST) { $env:REGISTRY_HOST } else { throw "Set REGISTRY_HOST first." }
$user = if ($env:REGISTRY_USER) { $env:REGISTRY_USER } else { throw "Set REGISTRY_USER first." }
$pass = if ($env:REGISTRY_PASSWORD) { $env:REGISTRY_PASSWORD } else { throw "Set REGISTRY_PASSWORD first." }

$pass | docker login $registry -u $user --password-stdin
if ($LASTEXITCODE -ne 0) {
    throw "Registry login failed: $registry"
}

Write-Host "[RESULT] Logged in: $registry"
