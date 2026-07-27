$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
python (Join-Path $scriptRoot "adapter-certify.py") @args
exit $LASTEXITCODE
