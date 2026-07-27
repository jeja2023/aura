# Commercial release-gate wrapper for Windows and PowerShell runners.
$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$runner = Join-Path $scriptRoot "release-gate.py"

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python is required to run the commercial release gate."
}

& python $runner @args
exit $LASTEXITCODE
