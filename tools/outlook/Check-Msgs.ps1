# Opens each .msg and .oft in a directory through Outlook's object model and records what Outlook sees: message class, subject, body sizes,
# recipients, attachments (with their methods and sizes), embedded messages, and a re-save of the item to a new .msg. Message files are opened
# with OpenSharedItem (the way a double-click opens them); a template (.oft) is also instantiated with CreateItemFromTemplate, which is how
# Outlook uses one. Test-side only: the library never talks to Outlook.
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
    $d.recipients = @(foreach ($r in $it.Recipients) { [ordered]@{ name = $r.Name; type = $r.Type; address = $r.Address } })
    $d.attachments = @(foreach ($a in $it.Attachments) {
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
    try {
        $alive = $false
        try { $alive = [bool](Get-Process OUTLOOK -ErrorAction SilentlyContinue) -and ($null -ne $app.Session) } catch { }
        if (-not $alive) { $app = Start-OutlookSession -Profile $Profile -Dir $Dir }
        $res.stage = "open"
        $it = $app.Session.OpenSharedItem($work)
        $res.opened = $true
        $res.item = Describe-Item $it
        $res.stage = "resave"
        $back = "$work.resaved.msg"
        $it.SaveAs($back, 3)   # olMSGUnicode
        $res.resavedLength = (Get-Item $back).Length
        $it.Close(1)   # olDiscard
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($it)
        Remove-Item $back -Force -ErrorAction SilentlyContinue
        if ($f.Extension -eq ".oft") {
            $res.stage = "template"
            $t = $app.CreateItemFromTemplate($work)
            $res.fromTemplate = Describe-Item $t
            $t.Close(1)
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($t)
        }
    } catch {
        $res.opened = [bool]$res.opened
        $res.error = $_.Exception.Message
    }
    Start-Sleep -Milliseconds 500
    $res.dialogs = @(if (Test-Path $dialogLog) { Get-Content $dialogLog | Select-Object -Skip $mark })
    $json = Join-Path $Out (($rel -replace '[\\/]', '__') + ".outlook.json")
    ($res | ConvertTo-Json -Depth 8 -Compress) | Set-Content $json -Encoding UTF8
    Remove-Item $work -Force -ErrorAction SilentlyContinue
    "{0}: opened={1} dialogs={2} stage={3} {4}" -f $rel, $res.opened, $res.dialogs.Count, $res.stage, $res.error
}
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
