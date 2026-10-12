<#
.SYNOPSIS
    优雅停止正在运行的 ScottPlotEx 时间压力测试。

.DESCRIPTION
    创建 artifacts\soak\STOP 停止标记，压测程序会在数十秒内收尾并写出报告；
    本脚本随后最多等待 60 秒确认进程退出。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$soakDirectory = Join-Path $repositoryRoot 'artifacts\soak'
$stopFile = Join-Path $soakDirectory 'STOP'

if (-not (Test-Path $soakDirectory)) {
    New-Item -ItemType Directory -Path $soakDirectory | Out-Null
}

Set-Content -Path $stopFile -Value "stop requested at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8
Write-Host "已创建停止标记：$stopFile"
Write-Host '压测程序会在数十秒内优雅收尾，并写出本次运行的报告。'

for ($second = 5; $second -le 60; $second += 5) {
    Start-Sleep -Seconds 5

    if (-not (Get-Process -Name ScottPlotEx.SoakTest -ErrorAction SilentlyContinue)) {
        Write-Host '压测进程已退出。'
        Write-Host "报告：$(Join-Path $repositoryRoot 'artifacts\soak-report.md')"
        exit 0
    }

    Write-Host "等待收尾……（已等待 $second 秒）"
}

Write-Host '60 秒内进程仍未退出：可手动结束（Get-Process ScottPlotEx.SoakTest | Stop-Process）。'
Write-Host '手动结束时报告会标记为“异常中断”，但已采集的指标与日志仍然保留。'
