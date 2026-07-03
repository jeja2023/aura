# File: Database maintenance script

param(
    [Parameter(Position = 0)]
    [ValidateSet("status", "migrate", "backup", "restore", "verify-backup", "rollback", "rollback-migrate")]
    [string]$Action = "status",

    [string]$ConnectionString = "",
    [string]$BackupDir = "",
    [string]$BackupFile = "",
    [string]$BuildRoot = "",
    [string]$MigratorDll = "",

    [ValidateSet("custom", "plain")]
    [string]$Format = "custom",

    [int]$CommandTimeoutSeconds = 300,
    [int]$LockTimeoutSeconds = 60,

    [switch]$SkipBackup,
    [switch]$ConfirmRestore,
    [switch]$Clean,
    [switch]$IfExists,
    [switch]$Restore,
    [switch]$VerboseMigrator
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$migratorProject = Join-Path $repoRoot "backend\Aura.DbMigrator\Aura.DbMigrator.csproj"
$buildRootBase = if (-not [string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot
} elseif (-not [string]::IsNullOrWhiteSpace($env:AURA_DOTNET_BUILD_ROOT)) {
    $env:AURA_DOTNET_BUILD_ROOT
} else {
    Join-Path $repoRoot ".codex-tmp\msbuild"
}
$buildRoot = Join-Path $buildRootBase ("db-maintenance-" + [Guid]::NewGuid().ToString("N"))

function Resolve-ConnectionStringValue {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $ConnectionString
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ConnectionStrings__PgSql)) {
        return $env:ConnectionStrings__PgSql
    }
    throw "PostgreSQL connection string is required. Provide -ConnectionString or set ConnectionStrings__PgSql."
}

function Convert-ConnectionStringToPgConfig([string]$Value) {
    $map = @{}
    foreach ($part in ($Value -split ";")) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $idx = $part.IndexOf("=")
        if ($idx -lt 1) { continue }
        $key = $part.Substring(0, $idx).Trim().ToLowerInvariant()
        $val = $part.Substring($idx + 1).Trim()
        if ($key) { $map[$key] = $val }
    }

    $hostValue = if ($map.ContainsKey("host")) { $map["host"] } elseif ($map.ContainsKey("server")) { $map["server"] } else { "127.0.0.1" }
    $portValue = if ($map.ContainsKey("port")) { $map["port"] } else { "5432" }
    $databaseValue = if ($map.ContainsKey("database")) { $map["database"] } elseif ($map.ContainsKey("db")) { $map["db"] } else { "" }
    $userValue = if ($map.ContainsKey("username")) { $map["username"] } elseif ($map.ContainsKey("user id")) { $map["user id"] } elseif ($map.ContainsKey("user")) { $map["user"] } else { "" }
    $passwordValue = if ($map.ContainsKey("password")) { $map["password"] } else { "" }

    if ([string]::IsNullOrWhiteSpace($databaseValue)) { throw "Connection string is missing Database." }
    if ([string]::IsNullOrWhiteSpace($userValue)) { throw "Connection string is missing Username/User ID." }

    [pscustomobject]@{
        Host = $hostValue
        Port = $portValue
        Database = $databaseValue
        User = $userValue
        Password = $passwordValue
    }
}

function Invoke-WithPgPassword($PgConfig, [scriptblock]$Body) {
    $oldPassword = $env:PGPASSWORD
    try {
        if (-not [string]::IsNullOrWhiteSpace($PgConfig.Password)) {
            $env:PGPASSWORD = $PgConfig.Password
        }
        & $Body
    }
    finally {
        $env:PGPASSWORD = $oldPassword
    }
}

function Resolve-BackupDirectory {
    if (-not [string]::IsNullOrWhiteSpace($BackupDir)) {
        return $BackupDir
    }
    if (-not [string]::IsNullOrWhiteSpace($env:AURA_DB_BACKUP_DIR)) {
        return $env:AURA_DB_BACKUP_DIR
    }
    return (Join-Path $repoRoot "artifacts\db-backups")
}

function New-BackupFilePath($PgConfig) {
    if (-not [string]::IsNullOrWhiteSpace($BackupFile)) {
        return $BackupFile
    }

    $dir = Resolve-BackupDirectory
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $safeDb = ($PgConfig.Database -replace "[^A-Za-z0-9_.-]", "_")
    $ext = if ($Format -eq "plain") { "sql" } else { "dump" }
    return (Join-Path $dir "aura-$safeDb-$stamp.$ext")
}

function Require-Command([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$Name was not found in PATH. Install PostgreSQL client tools or run this from a PostgreSQL-enabled ops host."
    }
}

function Resolve-MigratorDll {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($MigratorDll)) {
        $candidates += $MigratorDll
    }
    if (-not [string]::IsNullOrWhiteSpace($env:AURA_DB_MIGRATOR_DLL)) {
        $candidates += $env:AURA_DB_MIGRATOR_DLL
    }
    $candidates += @(
        (Join-Path $repoRoot "migrator\Aura.DbMigrator.dll"),
        (Join-Path $repoRoot "backend\Aura.DbMigrator\bin\Release\net10.0\publish\Aura.DbMigrator.dll"),
        (Join-Path $repoRoot "backend\Aura.DbMigrator\bin\Debug\net10.0\Aura.DbMigrator.dll")
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return ""
}

function Invoke-Migrator([string]$Command, [string[]]$ExtraArgs = @()) {
    $connection = Resolve-ConnectionStringValue
    $migratorArgs = @(
        $Command,
        "--connection", $connection,
        "--command-timeout", "$CommandTimeoutSeconds"
    )
    if ($Command -eq "migrate") {
        $migratorArgs += @("--lock-timeout", "$LockTimeoutSeconds")
        if ($VerboseMigrator) { $migratorArgs += "--verbose" }
    }
    $migratorArgs += $ExtraArgs

    $resolvedDll = Resolve-MigratorDll
    if (-not [string]::IsNullOrWhiteSpace($resolvedDll)) {
        & dotnet $resolvedDll @migratorArgs
    } else {
        if (-not (Test-Path -LiteralPath $migratorProject)) {
            throw "Migrator project not found: $migratorProject"
        }

        $objRoot = Join-Path $buildRoot "Aura.DbMigrator\obj\"
        $binRoot = Join-Path $buildRoot "Aura.DbMigrator\bin\"
        New-Item -ItemType Directory -Path $objRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $binRoot -Force | Out-Null

        $dotnetArgs = @(
            "run",
            "--no-restore",
            "--project", $migratorProject,
            "-p:AuraIntermediateRoot=$objRoot",
            "-p:BaseOutputPath=$binRoot",
            "--"
        )
        if ($Restore) {
            $dotnetArgs = $dotnetArgs | Where-Object { $_ -ne "--no-restore" }
        }
        $dotnetArgs += $migratorArgs
        & dotnet @dotnetArgs
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Migrator command '$Command' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Backup {
    Require-Command "pg_dump"
    $connection = Resolve-ConnectionStringValue
    $pg = Convert-ConnectionStringToPgConfig $connection
    $file = New-BackupFilePath $pg
    $parent = Split-Path -Parent $file
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }

    $dumpFormat = if ($Format -eq "plain") { "plain" } else { "custom" }
    Write-Host "Creating PostgreSQL backup: $file"
    Invoke-WithPgPassword $pg {
        & pg_dump `
            --host $pg.Host `
            --port $pg.Port `
            --username $pg.User `
            --dbname $pg.Database `
            --format $dumpFormat `
            --file $file `
            --no-owner `
            --no-acl
        if ($LASTEXITCODE -ne 0) {
            throw "pg_dump failed with exit code $LASTEXITCODE."
        }
    }

    $info = Get-Item -LiteralPath $file
    if ($info.Length -le 0) {
        throw "Backup file is empty: $file"
    }

    $manifest = [pscustomobject]@{
        createdAt = (Get-Date).ToString("o")
        database = $pg.Database
        host = $pg.Host
        port = $pg.Port
        format = $Format
        backupFile = $file
        sizeBytes = $info.Length
    }
    $manifestPath = "$file.json"
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    if ($Format -eq "custom") {
        Invoke-VerifyBackup -File $file
    }

    Write-Host "Backup OK: $file"
    return $file
}

function Invoke-VerifyBackup([string]$File = "") {
    $target = if (-not [string]::IsNullOrWhiteSpace($File)) { $File } else { $BackupFile }
    if ([string]::IsNullOrWhiteSpace($target)) {
        throw "Provide -BackupFile for verify-backup."
    }
    if (-not (Test-Path -LiteralPath $target)) {
        throw "Backup file not found: $target"
    }

    $info = Get-Item -LiteralPath $target
    if ($info.Length -le 0) {
        throw "Backup file is empty: $target"
    }

    if ($target.EndsWith(".sql", [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Plain SQL backup exists and is non-empty: $target"
        return
    }

    Require-Command "pg_restore"
    & pg_restore --list $target | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore --list failed with exit code $LASTEXITCODE."
    }
    Write-Host "Custom backup is readable: $target"
}

function Invoke-Restore {
    if (-not $ConfirmRestore) {
        throw "Restore is destructive. Re-run with -ConfirmRestore after verifying the target database and backup file."
    }
    if ([string]::IsNullOrWhiteSpace($BackupFile)) {
        throw "Provide -BackupFile for restore."
    }
    if (-not (Test-Path -LiteralPath $BackupFile)) {
        throw "Backup file not found: $BackupFile"
    }

    $connection = Resolve-ConnectionStringValue
    $pg = Convert-ConnectionStringToPgConfig $connection
    $isPlainSql = $BackupFile.EndsWith(".sql", [System.StringComparison]::OrdinalIgnoreCase)

    if ($isPlainSql) {
        Require-Command "psql"
        Write-Host "Restoring plain SQL backup into database '$($pg.Database)': $BackupFile"
        Invoke-WithPgPassword $pg {
            & psql `
                --host $pg.Host `
                --port $pg.Port `
                --username $pg.User `
                --dbname $pg.Database `
                --set "ON_ERROR_STOP=1" `
                --file $BackupFile
            if ($LASTEXITCODE -ne 0) {
                throw "psql restore failed with exit code $LASTEXITCODE."
            }
        }
        return
    }

    Require-Command "pg_restore"
    $restoreArgs = @(
        "--host", $pg.Host,
        "--port", $pg.Port,
        "--username", $pg.User,
        "--dbname", $pg.Database,
        "--no-owner",
        "--no-acl"
    )
    if ($Clean) { $restoreArgs += "--clean" }
    if ($IfExists) { $restoreArgs += "--if-exists" }
    $restoreArgs += $BackupFile

    Write-Host "Restoring custom backup into database '$($pg.Database)': $BackupFile"
    Invoke-WithPgPassword $pg {
        & pg_restore @restoreArgs
        if ($LASTEXITCODE -ne 0) {
            throw "pg_restore failed with exit code $LASTEXITCODE."
        }
    }
}

switch ($Action) {
    "status" {
        Invoke-Migrator "status" @("--fail-on-drift")
    }
    "migrate" {
        Invoke-Migrator "status" @("--fail-on-drift")
        if (-not $SkipBackup) {
            Invoke-Backup | Out-Null
        } else {
            Write-Host "Skipping pre-migration backup because -SkipBackup was supplied." -ForegroundColor Yellow
        }
        Invoke-Migrator "migrate"
        Invoke-Migrator "status" @("--fail-on-pending", "--fail-on-drift")
    }
    "backup" {
        Invoke-Backup | Out-Null
    }
    "restore" {
        Invoke-Restore
        Invoke-Migrator "status" @("--fail-on-drift")
    }
    "rollback" {
        Invoke-VerifyBackup
        Invoke-Restore
        Invoke-Migrator "status" @("--fail-on-drift")
    }
    "rollback-migrate" {
        Invoke-VerifyBackup
        Invoke-Restore
        Invoke-Migrator "status" @("--fail-on-drift")
        Invoke-Migrator "migrate"
        Invoke-Migrator "status" @("--fail-on-pending", "--fail-on-drift")
    }
    "verify-backup" {
        Invoke-VerifyBackup
    }
}
