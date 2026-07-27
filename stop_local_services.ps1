# stop_local_services.ps1
# 脚本功能：停止本地占用 5432 和 8529 端口的 PostgreSQL 和 ArangoDB 服务。
# 注意：此脚本必须“以管理员身份运行”。

# 提升权限检查
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "【警告】此脚本需要管理员权限来停止 Windows 服务。"
    Write-Warning "请关闭此窗口，然后右键点击此文件选择 '以管理员身份运行'，或者在管理员 PowerShell 窗口中运行它。"
    Read-Host "按回车键退出..."
    Exit
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "      正在停止本地数据库服务（端口清理）  " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 停止 PostgreSQL 服务
$pgServices = Get-Service -Name "postgresql*" -ErrorAction SilentlyContinue
if ($pgServices) {
    foreach ($svc in $pgServices) {
        if ($svc.Status -eq 'Running') {
            Write-Host "发现运行中的服务: $($svc.DisplayName) ($($svc.Name))" -ForegroundColor Yellow
            Write-Host "正在停止..." -NoNewline
            Stop-Service -Name $svc.Name -Force
            Write-Host " [已停止]" -ForegroundColor Green
        } else {
            Write-Host "服务 $($svc.DisplayName) 已处于停止状态。" -ForegroundColor Gray
        }
    }
} else {
    Write-Host "本地未检测到 PostgreSQL 服务。" -ForegroundColor Gray
}

# 2. 停止 ArangoDB 服务
$arangoServices = Get-Service -Name "ArangoDB*" -ErrorAction SilentlyContinue
if ($arangoServices) {
    foreach ($svc in $arangoServices) {
        if ($svc.Status -eq 'Running') {
            Write-Host "发现运行中的服务: $($svc.DisplayName) ($($svc.Name))" -ForegroundColor Yellow
            Write-Host "正在停止..." -NoNewline
            Stop-Service -Name $svc.Name -Force
            Write-Host " [已停止]" -ForegroundColor Green
        } else {
            Write-Host "服务 $($svc.DisplayName) 已处于停止状态。" -ForegroundColor Gray
        }
    }
} else {
    Write-Host "本地未检测到 ArangoDB 服务。" -ForegroundColor Gray
}

# 3. 停止 Redis 服务 (可选，检查是否有本地 Redis 运行在 6379 端口)
$redisServices = Get-Service -Name "*redis*" -ErrorAction SilentlyContinue
if ($redisServices) {
    foreach ($svc in $redisServices) {
        if ($svc.Status -eq 'Running') {
            Write-Host "发现运行中的 Redis 服务: $($svc.DisplayName) ($($svc.Name))" -ForegroundColor Yellow
            Write-Host "正在停止..." -NoNewline
            Stop-Service -Name $svc.Name -Force
            Write-Host " [已停止]" -ForegroundColor Green
        } else {
            Write-Host "Redis 服务 $($svc.DisplayName) 已处于停止状态。" -ForegroundColor Gray
        }
    }
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "清理完毕！现在您可以运行 Docker 容器了。" -ForegroundColor Green
Read-Host "按回车键关闭窗口..."
