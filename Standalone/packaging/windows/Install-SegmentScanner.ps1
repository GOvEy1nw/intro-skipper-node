[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $InstallDirectory,
    [Parameter(Mandatory)] [string] $StateDirectory,
    [string] $TaskName = 'SegmentScanner Worker',
    [string] $ConfigPath = '',
    [switch] $StartNow
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $package 'segmentscanner.exe') -PathType Leaf)) {
    throw "PackageDirectory must contain segmentscanner.exe: $package"
}
if (-not $PSCmdlet.ShouldProcess($InstallDirectory, "Install package and register '$TaskName' worker task")) { return }
foreach ($directory in @($InstallDirectory, $StateDirectory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
Get-ChildItem -LiteralPath $package -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallDirectory -Recurse -Force
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $StateDirectory 'worker.json' }
if (-not (Test-Path -LiteralPath $ConfigPath)) {
    Copy-Item -LiteralPath (Join-Path $package 'samples\config.windows.json') -Destination $ConfigPath
}
$exe = Join-Path $InstallDirectory 'segmentscanner.exe'
$action = New-ScheduledTaskAction -Execute $exe -Argument ('work --config "{0}"' -f $ConfigPath)
$task = New-ScheduledTask -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero))
Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null
if ($StartNow) { Start-ScheduledTask -TaskName $TaskName }
