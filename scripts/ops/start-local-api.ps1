param(
    [int]$Port = 5099,
    [string]$EnvironmentFile = ".env.docker"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$environmentPath = if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) {
    $EnvironmentFile
} else {
    Join-Path $repositoryRoot $EnvironmentFile
}
if (-not (Test-Path -LiteralPath $environmentPath)) {
    throw "Environment file not found: $environmentPath"
}
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $Port is already in use."
}

$values = @{}
Get-Content -LiteralPath $environmentPath -Encoding utf8 | ForEach-Object {
    if ($_ -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $values[$matches[1]] = $matches[2].Trim().Trim('"').Trim("'")
    }
}
if (Get-Command docker -ErrorAction SilentlyContinue) {
    $containerEnvironmentJson = & docker inspect aura-pgsql --format '{{json .Config.Env}}' 2>$null
    if ($LASTEXITCODE -eq 0 -and $containerEnvironmentJson) {
        $containerEnvironment = $containerEnvironmentJson | ConvertFrom-Json
        foreach ($entry in $containerEnvironment) {
            if ($entry -match '^(POSTGRES_DB|POSTGRES_USER|POSTGRES_PASSWORD)=(.*)$') {
                $values[$matches[1]] = $matches[2]
            }
        }
    }
    $postgresPort = & docker port aura-pgsql 5432/tcp 2>$null
    if ($LASTEXITCODE -eq 0 -and $postgresPort -match ':(\d+)$') {
        $values["POSTGRES_PORT"] = $matches[1]
    }
    $redisPort = & docker port aura-redis 6379/tcp 2>$null
    if ($LASTEXITCODE -eq 0 -and $redisPort -match ':(\d+)$') {
        $values["REDIS_PORT"] = $matches[1]
    }
}
foreach ($name in @("POSTGRES_PORT", "POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD", "REDIS_PORT", "AURA_ADMIN_PASSWORD", "JWT__KEY", "SECURITY__HMACSECRET")) {
    if ([string]::IsNullOrWhiteSpace([string]$values[$name])) {
        throw "Required setting $name is missing from $environmentPath"
    }
}

$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
$env:AllowedHosts = "127.0.0.1;localhost"
$env:ConnectionStrings__PgSql = "Host=127.0.0.1;Port=$($values['POSTGRES_PORT']);Database=$($values['POSTGRES_DB']);Username=$($values['POSTGRES_USER']);Password=$($values['POSTGRES_PASSWORD']);Pooling=true;Maximum Pool Size=50"
$env:ConnectionStrings__Redis = "127.0.0.1:$($values['REDIS_PORT']),abortConnect=false"
$env:AURA_ADMIN_USER = if ($values["AURA_ADMIN_USER"]) { $values["AURA_ADMIN_USER"] } else { "admin" }
$env:AURA_ADMIN_PASSWORD = $values["AURA_ADMIN_PASSWORD"]
$env:Jwt__Key = $values["JWT__KEY"]
$env:Security__HmacSecret = $values["SECURITY__HMACSECRET"]
$env:Security__Cookies__ForceSecure = "false"
$env:Security__ForwardedHeaders__Enabled = "false"
$env:Ops__Alert__WebhookUrl = " "
$env:Ops__Alert__HealthFailIfRecentFailureMinutes = "0"
$env:Ai__BaseUrl = "http://127.0.0.1:8000"
$env:Paths__FrontendRoot = (Resolve-Path (Join-Path $repositoryRoot "frontend")).Path
$env:CommercialProduct__Rules__WorkerEnabled = "false"

$runtimeDirectory = Join-Path $repositoryRoot "storage\runtime"
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
$stdout = Join-Path $runtimeDirectory "aura-api.stdout.log"
$stderr = Join-Path $runtimeDirectory "aura-api.stderr.log"
$pidFile = Join-Path $runtimeDirectory "aura-api.pid"
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$process = Start-Process -FilePath $dotnet `
    -ArgumentList @("run", "--project", "backend/Aura.Api", "--configuration", "Release", "--no-build", "--no-launch-profile") `
    -WorkingDirectory $repositoryRoot `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -WindowStyle Hidden `
    -PassThru
Set-Content -LiteralPath $pidFile -Value $process.Id -Encoding ascii

Write-Output "Aura API started with PID $($process.Id)."
Write-Output "URL: http://127.0.0.1:$Port/"
Write-Output "Logs: $stdout"
