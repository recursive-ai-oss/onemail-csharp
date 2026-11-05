# Opens each .msg and .oft in a directory through Outlook's object model and records what Outlook sees: message class, subject, body sizes,
# recipients, attachments (with their methods and sizes), embedded messages, and a re-save of the item to a new .msg. Message files are opened
# with OpenSharedItem (the way a double-click opens them); a template (.oft) with CreateItemFromTemplate, which is how Outlook uses one. Test-side only: the library never talks to Outlook.
#
#   Check-Msgs.ps1 -Set <dir> -Out <result dir> [-Profile OneMailOracle] [-Dir <work dir>]
param(
    [Parameter(Mandatory)][string]$Set,
    [Parameter(Mandatory)][string]$Out,
    [string]$Profile = "OneMailOracle",
    [string]$Dir = "$env:USERPROFILE\onemail"
)
$ErrorActionPreference = "Continue"
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
New-Item -ItemType Directory -Force $Out, "$Dir\work" | Out-Null
$dialogLog = "$Dir\work\dialogs.log"
$watcher = Start-DialogWatcher -Log $dialogLog
$app = Start-OutlookSession -Profile $Profile -Dir $Dir
"outlook $($app.Version) build $($app.Build)"

function Describe-Item($it) {
    $d = [ordered]@{ class = $it.MessageClass; subject = $it.Subject; bodyLength = ([string]$it.Body).Length; htmlLength = 0; unread = $null; size = $it.Size }
    try { $d.htmlLength = ([string]$it.HTMLBody).Length } catch { }
    try { $d.unread = [bool]$it.UnRead } catch { }
    $d.recipientCount = $it.Recipients.Count
    $d.recipients = @(for ($k = 1; $k -le $it.Recipients.Count; $k++) { $r = $it.Recipients.Item($k); [ordered]@{ name = $r.Name; type = $r.Type; address = $r.Address } })
    $d.attachmentCount = $it.Attachments.Count
    $d.attachments = @(for ($k = 1; $k -le $it.Attachments.Count; $k++) {
        $a = $it.Attachments.Item($k)
        $e = [ordered]@{ name = $a.DisplayName; file = $a.FileName; type = $a.Type; size = $null }
        try { $e.size = $a.Size } catch { }
        $e
    })
    $d
}

$files = Get-ChildItem $Set -Recurse -File | Where-Object { $_.Extension -in ".msg", ".oft" } | Sort-Object FullName
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Set.TrimEnd('\').Length + 1)
    $res = [ordered]@{ file = $rel; length = $f.Length }
    $work = "$Dir\work\msg-$([guid]::NewGuid().ToString('N'))$($f.Extension)"
    Copy-Item $f.FullName $work
    $mark = if (Test-Path $dialogLog) { (Get-Content $dialogLog).Count } else { 0 }
    $steps = [ordered]@{}
    $alive = $false
    try { $alive = [bool](Get-Process OUTLOOK -ErrorAction SilentlyContinue) -and ($null -ne $app.Session) } catch { }
    if (-not $alive) { $app = Start-OutlookSession -Profile $Profile -Dir $Dir }
    $it = $null
    try {
        $res.stage = "open"
        $it = if ($f.Extension -eq ".oft") { $app.CreateItemFromTemplate($work) } else { $app.Session.OpenSharedItem($work) }
        $res.opened = $true
    } catch { $res.opened = $false; $res.error = "open: " + $_.Exception.Message }
    if ($it) {
        try { $res.stage = "describe"; $res.item = Describe-Item $it } catch { $res.error = "describe: " + $_.Exception.Message }
        try {
            $res.stage = "resave"
            $back = "$work.resaved.msg"
            $it.SaveAs($back, 9)   # olMSGUnicode
            $res.resavedLength = (Get-Item $back).Length
            Remove-Item $back -Force -ErrorAction SilentlyContinue
        } catch { $res.resaveError = $_.Exception.Message }
        try { $it.Close(1) } catch { }   # olDiscard
        try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($it) } catch { }
    }
    Start-Sleep -Milliseconds 500
    $res.dialogs = @(if (Test-Path $dialogLog) { Get-Content $dialogLog | Select-Object -Skip $mark })
    $json = Join-Path $Out (($rel -replace '[\\/]', '__') + ".outlook.json")
    ($res | ConvertTo-Json -Depth 8 -Compress) | Set-Content $json -Encoding UTF8
    Remove-Item $work -Force -ErrorAction SilentlyContinue
    "{0}: opened={1} dialogs={2} stage={3} {4} {5}" -f $rel, $res.opened, $res.dialogs.Count, $res.stage, $res.error, $res.resaveError
}
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
