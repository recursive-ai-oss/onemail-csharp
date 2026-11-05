# Opens each .msg in -Set and saves it as a template (.oft) the way Outlook does, to see what Outlook writes for each kind of item (class ID, flags).
#
#   Save-Templates.ps1 -Set <dir> -Out <dir> [-Profile OneMailOracle] [-Dir <work dir>]
param(
    [Parameter(Mandatory)][string]$Set,
    [Parameter(Mandatory)][string]$Out,
    [string]$Profile = "OneMailOracle",
    [string]$Dir = "$env:USERPROFILE\onemail"
)
$ErrorActionPreference = "Continue"
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
New-Item -ItemType Directory -Force $Out, "$Dir\work" | Out-Null
$watcher = Start-DialogWatcher -Log "$Dir\work\dialogs.log"
$app = Start-OutlookSession -Profile $Profile -Dir $Dir
"outlook $($app.Version)"
foreach ($f in Get-ChildItem $Set -Recurse -File -Filter *.msg | Sort-Object FullName) {
    $work = "$Dir\work\tpl-$([guid]::NewGuid().ToString('N')).msg"
    Copy-Item $f.FullName $work
    try {
        $it = $app.Session.OpenSharedItem($work)
        $dest = Join-Path $Out ($f.BaseName + ".oft")
        $it.SaveAs($dest, 2)   # olTemplate
        "{0}: {1} -> {2} bytes" -f $f.Name, $it.MessageClass, (Get-Item $dest).Length
        $it.Close(1)
    } catch { "{0}: failed: {1}" -f $f.Name, $_.Exception.Message }
    Remove-Item $work -Force -ErrorAction SilentlyContinue
}
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
