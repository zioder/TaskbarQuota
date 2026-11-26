#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($PublishDirectory)
$required = @(
    'TaskbarQuota.exe',
    'MainWindow.xbf',
    'FlyoutWindow.xbf',
    'FloatingUsageWindow.xbf',
    'Views\DashboardPage.xbf',
    'Controls\WidgetSummary.xbf',
    'Controls\QuotaWidgetContent.xbf'
)
foreach ($relative in $required) {
    $path = Join-Path $directory $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Publish output is missing required artifact: $relative"
    }
}
$pri = @('TaskbarQuota.pri', 'resources.pri') | Where-Object {
    $path = Join-Path $directory $_
    (Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -gt 0
}
if (!$pri) { throw 'Publish output is missing the WinUI PRI resource file.' }
$directory
