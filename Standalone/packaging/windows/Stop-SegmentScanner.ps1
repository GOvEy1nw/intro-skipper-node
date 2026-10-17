[CmdletBinding(SupportsShouldProcess)]
param([string] $TaskName = 'SegmentScanner Worker')

$ErrorActionPreference = 'Stop'
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($TaskName, 'Stop the SegmentScanner worker task')) {
        Stop-ScheduledTask -TaskName $TaskName
    }
}
