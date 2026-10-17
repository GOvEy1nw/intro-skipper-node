[CmdletBinding(SupportsShouldProcess)]
param([string] $TaskName = 'SegmentScanner Worker')

$ErrorActionPreference = 'Stop'
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister the SegmentScanner worker task')) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
}
