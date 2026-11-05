# Makes a PST with Outlook itself holding one folder of many small items: the control for folders over 65,535 rows, which the repair tool
# counts in 16 bits. Stops early after -MaxMinutes and says how many items it made.
#
#   Make-BigFolder.ps1 -Out <file.pst> [-Items 66000] [-MaxMinutes 20]
param(
    [Parameter(Mandatory)][string]$Out,
    [int]$Items = 66000,
    [int]$MaxMinutes = 20,
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
$ns.AddStoreEx($Out, 2)
$store = $ns.Stores | Where-Object { $_.FilePath -eq $Out } | Select-Object -First 1
$root = $store.GetRootFolder()
$big = $root.Folders.Add("Big")
$clock = [Diagnostics.Stopwatch]::StartNew()
$made = 0
for ($i = 1; $i -le $Items; $i++) {
    # A post item is created in the folder it is added to, so there is no move to pay for (a mail item would land in Drafts first).
    $p = $big.Items.Add(6)   # olPostItem
    $p.Subject = "item $i"
    $p.Body = "body $i"
    $p.Save()
    $made++
    if ($made % 5000 -eq 0) { "{0} items after {1:F0} s" -f $made, $clock.Elapsed.TotalSeconds }
    if ($clock.Elapsed.TotalMinutes -ge $MaxMinutes) { break }
}
"made $made items in {0:F0} s" -f $clock.Elapsed.TotalSeconds
Start-Sleep 2
$ns.RemoveStore($root)
Start-Sleep 3
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
"{0}: {1} bytes" -f $Out, (Get-Item $Out).Length
