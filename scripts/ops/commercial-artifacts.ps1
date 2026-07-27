$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
python (Join-Path $scriptRoot "validate-commercial-artifacts.py") @args
exit $LASTEXITCODE
