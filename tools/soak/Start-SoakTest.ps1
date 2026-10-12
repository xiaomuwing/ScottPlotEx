<#
.SYNOPSIS
    启动 ScottPlotEx 控件的长时间时间压力测试（默认 8 小时）。

.DESCRIPTION
    以不可见控制台、近透明且鼠标穿透的窗口启动压测程序，
    运行期间不会遮挡或拦截用户的鼠标操作，适合无人值守的长时运行。

.EXAMPLE
    .\Start-SoakTest.ps1
    .\Start-SoakTest.ps1 -Hours 8 -Profile standard -CycleMinutes 20 -Tag soak8h
    .\Start-SoakTest.ps1 -Hours 0.05 -CycleMinutes 1 -Tag smoke   # 3 分钟冒烟验证
#>
[CmdletBinding()]
param(
    # 运行时长（小时）
    [double]$Hours = 8,

    # 负载档位：light（速率减半）/ standard（默认）/ heavy（速率加倍）
    [ValidateSet('light', 'standard', 'heavy')]
    [string]$Profile = 'standard',

    # 一个负载循环的分钟数（循环内包含 6 个阶段）
    [double]$CycleMinutes = 20,

    # 运行标识（用于产物目录与文件名）
    [string]$Tag = 'soak',

    # 使用正常可见窗口（默认是近透明 + 鼠标穿透的“幽灵窗口”）
    [switch]$Visible
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot 'tools\ScottPlotEx.SoakTest\ScottPlotEx.SoakTest.csproj'
$exePath = Join-Path $repositoryRoot 'tools\ScottPlotEx.SoakTest\bin\Release\net8.0-windows10.0.19041.0\ScottPlotEx.SoakTest.exe'

if (-not (Test-Path $exePath)) {
    Write-Host '未找到已构建的压测程序，正在以 Release 配置构建……'
    dotnet build $projectPath -c Release | Out-Host
}

$arguments = @(
    '--hours', $Hours,
    '--cycle-minutes', $CycleMinutes,
    '--profile', $Profile,
    '--tag', $Tag,
    '--no-console'
)

if ($Visible) {
    $arguments += '--visible'
}

$process = Start-Process -FilePath $exePath -ArgumentList $arguments -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru

Write-Host "压测已启动：PID $($process.Id)"
Write-Host "计划时长：$Hours 小时；负载循环：$CycleMinutes 分钟；档位：$Profile"
Write-Host '运行期间窗口近透明且鼠标穿透，不会影响你正常使用电脑。'
Write-Host ''
Write-Host '查看进度：tools\soak\Get-SoakStatus.ps1'
Write-Host '提前停止：tools\soak\Stop-SoakTest.ps1（等价于创建 artifacts\soak\STOP）'
Write-Host '最终报告：artifacts\soak-report.md（运行结束后生成）'
