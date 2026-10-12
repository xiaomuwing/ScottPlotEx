<#
.SYNOPSIS
    查看 ScottPlotEx 时间压力测试的进度。

.DESCRIPTION
    读取 artifacts\soak\current.json（每次运行都会刷新的状态文件），
    输出进度、当前阶段与资源指标，并给出日志与报告路径。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$statusPath = Join-Path $repositoryRoot 'artifacts\soak\current.json'

if (-not (Test-Path $statusPath)) {
    Write-Host "还没有压测记录（未找到 $statusPath）。"
    Write-Host '可以先用 tools\soak\Start-SoakTest.ps1 启动一次压测。'
    exit 0
}

$status = Get-Content $statusPath -Raw | ConvertFrom-Json
$process = Get-Process -Id $status.processId -ErrorAction SilentlyContinue

function Format-Duration([double]$seconds) {
    $span = [TimeSpan]::FromSeconds($seconds)

    if ($span.TotalHours -ge 1) {
        return '{0} 小时 {1} 分 {2} 秒' -f [int]$span.TotalHours, $span.Minutes, $span.Seconds
    }

    return '{0} 分 {1} 秒' -f $span.Minutes, $span.Seconds
}

$stateText = switch ($status.state) {
    'running' { if ($process) { '运行中' } else { '运行中（但进程已不存在，可能被中断）' } }
    'completed' { '已完成' }
    'stopped' { '被手动停止' }
    'failed' { '失败' }
    default { $status.state }
}

Write-Host 'ScottPlotEx 时间压力测试进度'
Write-Host '--------------------------------'
Write-Host ('状态        ：{0}' -f $stateText)
Write-Host ('运行标识    ：{0}' -f $status.runId)
Write-Host ('开始时间    ：{0}' -f $status.startedLocal)
Write-Host ('已运行      ：{0} / 计划 {1}（{2}%）' -f (Format-Duration $status.elapsedSeconds), (Format-Duration $status.plannedSeconds), $status.percentComplete)
Write-Host ('当前阶段    ：{0}（第 {1} 轮）' -f $status.phase, $status.cycle)
Write-Host ('滚动模式    ：{0}' -f $status.mode)
Write-Host ('当前数据点  ：{0}（累计写入 {1}）' -f $status.dataCount, $status.pointsTotal)
Write-Host ('坐标轴有效  ：{0}' -f $status.axisValid)
Write-Host ('工作集      ：{0} MB' -f $status.workingSetMb)
Write-Host ('托管堆      ：{0} MB' -f $status.managedHeapMb)
Write-Host ('句柄 / GDI / 线程：{0} / {1} / {2}' -f $status.handleCount, $status.gdiObjects, $status.threadCount)
Write-Host ('错误 / 告警 ：{0} / {1}' -f $status.errors, $status.warnings)
Write-Host ('最近更新    ：{0}' -f $status.updatedLocal)
Write-Host ''
Write-Host ('运行日志    ：{0}' -f $status.logPath)
Write-Host ('指标明细    ：{0}' -f $status.csvPath)
Write-Host ('本次报告    ：{0}' -f $status.reportPath)
Write-Host ('最新报告副本：{0}' -f (Join-Path $repositoryRoot 'artifacts\soak-report.md'))
