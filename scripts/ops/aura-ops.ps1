# File: Aura Ops Entry Script

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$validTasks = @("readiness", "ai-check", "ai-eval", "capture-regression", "full-check", "db-status", "db-migrate", "db-backup", "db-restore", "db-rollback", "db-rollback-migrate", "db-verify-backup")
$Task = if ($args.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$args[0])) { [string]$args[0] } else { "readiness" }
$RemainingArgs = if ($args.Count -gt 1) { @($args[1..($args.Count - 1)]) } else { @() }

if ($validTasks -notcontains $Task) {
    throw "Unknown ops task '$Task'. Valid tasks: $($validTasks -join ', ')"
}

$scriptName = switch ($Task) {
    "readiness" { "readiness-check.ps1" }
    "ai-check" { "ai-check.ps1" }
    "ai-eval" { "ai-eval.ps1" }
    "capture-regression" { "capture-regression.ps1" }
    "full-check" { "full-check.ps1" }
    "db-status" { "db-maintenance.ps1" }
    "db-migrate" { "db-maintenance.ps1" }
    "db-backup" { "db-maintenance.ps1" }
    "db-restore" { "db-maintenance.ps1" }
    "db-rollback" { "db-maintenance.ps1" }
    "db-rollback-migrate" { "db-maintenance.ps1" }
    "db-verify-backup" { "db-maintenance.ps1" }
}

$scriptPath = Join-Path $scriptRoot $scriptName
if (-not (Test-Path $scriptPath)) {
    throw "Ops script not found: $scriptPath"
}

$actionArgs = @(switch ($Task) {
    "db-status" { @("status") }
    "db-migrate" { @("migrate") }
    "db-backup" { @("backup") }
    "db-restore" { @("restore") }
    "db-rollback" { @("rollback") }
    "db-rollback-migrate" { @("rollback-migrate") }
    "db-verify-backup" { @("verify-backup") }
    default { @() }
})
$invokeArgs = @($actionArgs) + @($RemainingArgs)
& $scriptPath @invokeArgs
