# Makes a PST with Outlook itself holding a copy of a large folder of another PST, the control for folders over 65,535 rows without paying for
# one COM call per item: the source store is opened in Outlook, a new PST is added, and Folder.CopyTo copies the folder inside Outlook.
#
#   Copy-Folder.ps1 -Source <store.pst> -Folder <name of a folder directly under the store's root or its IPM subtree> -Out <new.pst> [-MaxMinutes 25]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Folder,
    [Parameter(Mandatory)][string]$Out,
    [int]$MaxMinutes = 25,
    [string]$Profile = "OneMailOracle",
    [string]$Dir = "$env:USERPROFILE\onemail"
)
$ErrorActionPreference = "Stop"
trap { Write-Output ("failed at line " + $_.InvocationInfo.ScriptLineNumber + ": " + $_.InvocationInfo.Line.Trim() + " :: " + $_.Exception.Message); exit 1 }
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
New-Item -ItemType Directory -Force (Split-Path $Out), "$Dir\work" | Out-Null
Remove-Item $Out -ErrorAction SilentlyContinue
$watcher = Start-DialogWatcher -Log "$Dir\work\dialogs.log"
$app = Start-OutlookSession -Profile $Profile -Dir $Dir
$ns = $app.Session
$ns.AddStoreEx($Source, 2)
$ns.AddStoreEx($Out, 2)
$src = $ns.Stores | Where-Object { $_.FilePath -eq $Source } | Select-Object -First 1
$dst = $ns.Stores | Where-Object { $_.FilePath -eq $Out } | Select-Object -First 1
function Find-Folder($f, $name) {
    foreach ($c in $f.Folders) { if ($c.Name -eq $name) { return $c }; $r = Find-Folder $c $name; if ($r) { return $r } }
    return $null
}
$from = Find-Folder $src.GetRootFolder() $Folder
if (-not $from) { throw "no folder $Folder in $Source" }
"source folder $Folder holds $($from.Items.Count) items"
$clock = [Diagnostics.Stopwatch]::StartNew()
$copy = $from.CopyTo($dst.GetRootFolder().Folders.Item(1))
"copied in {0:F0} s: {1} items" -f $clock.Elapsed.TotalSeconds, $copy.Items.Count
Start-Sleep 2
$ns.RemoveStore($src.GetRootFolder())
$ns.RemoveStore($dst.GetRootFolder())
Start-Sleep 3
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
"{0}: {1} bytes" -f $Out, (Get-Item $Out).Length
