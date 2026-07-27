$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$pidFile = Join-Path $repositoryRoot "storage\runtime\aura-api.pid"
if (-not (Test-Path -LiteralPath $pidFile)) {
    Write-Output "Aura API PID file is not present."
    return
}

$processId = [int](Get-Content -LiteralPath $pidFile -Raw)
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$processId"
if ($null -eq $process) {
    Remove-Item -LiteralPath $pidFile -Force
    Write-Output "Aura API was already stopped."
    return
}
if ($process.Name -notin @("Aura.Api.exe", "dotnet.exe") -or $process.CommandLine -notmatch "Aura.Api") {
    throw "PID $processId does not belong to the Aura API; refusing to stop it."
}

Stop-Process -Id $processId -Force
Remove-Item -LiteralPath $pidFile -Force
Write-Output "Aura API stopped: PID $processId"
