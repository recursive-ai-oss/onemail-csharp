# Asks Outlook how it behaves where the specifications leave room, for the settles in the composition, flags and contact notes. Test-side only.
#
#   Probe-Items.ps1 -Set <dir> -Out <dir> [-Profile OneMailOracle] [-Dir <work dir>]
#
# <Set>\mail\*.msg      received messages: each is stored in a scratch PST, then Reply, ReplyAll and Forward are made and saved as .msg
#                       (conversation index, topic and threading headers), and the item is saved again after FlagStatus, MarkAsTask and UnRead changes.
# <Set>\contacts\*.vcf  vCards opened as contact items and saved as .msg (how Outlook maps TEL, ADR and BDAY).
# Results are .msg files in <Out>, named <source>.<what>.msg, plus probe.log.
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
$ns = $app.Session
$pst = "$Dir\work\probe-$([guid]::NewGuid().ToString('N')).pst"
$ns.AddStoreEx($pst, 2)
$store = $ns.Stores | Where-Object { $_.FilePath -eq $pst } | Select-Object -First 1
$root = $store.GetRootFolder()
$inbox = $root.Folders.Item(1).Folders.Add("Received")
$contacts = $root.Folders.Item(1).Folders.Add("People")
$contacts.Items | Out-Null

function Save($it, [string]$name) {
    try { $it.SaveAs((Join-Path $Out "$name.msg"), 9); "saved $name" } catch { "FAILED $name : $($_.Exception.Message)" }
}

foreach ($f in Get-ChildItem "$Set\mail" -Filter *.msg -ErrorAction SilentlyContinue | Sort-Object Name) {
    $b = $f.BaseName
    try {
        $shared = $app.Session.OpenSharedItem($f.FullName)
        $it = $shared.Move($inbox)
        "{0}: stored, class {1}" -f $b, $it.MessageClass
    } catch { "FAILED store $b : $($_.Exception.Message)"; continue }
    try { $r = $it.Reply(); Save $r "$b.reply"; $r.Close(1) } catch { "FAILED reply $b : $($_.Exception.Message)" }
    try { $r = $it.ReplyAll(); Save $r "$b.replyall"; $r.Close(1) } catch { "FAILED replyall $b : $($_.Exception.Message)" }
    try { $r = $it.Forward(); Save $r "$b.forward"; $r.Close(1) } catch { "FAILED forward $b : $($_.Exception.Message)" }
    # Flags: what Outlook writes when an item is read, unread, flagged, completed or marked as a task.
    try {
        $it.UnRead = $true; $it.Save(); Save $it "$b.unread"
        $it.UnRead = $false; $it.Save(); Save $it "$b.read"
        $it.FlagStatus = 2; $it.Save(); Save $it "$b.flagged"
        $it.FlagStatus = 1; $it.Save(); Save $it "$b.flagcomplete"
        $it.ClearTaskFlag(); $it.Save()
        $it.MarkAsTask(1); $it.Save(); Save $it "$b.marktask"
    } catch { "FAILED flags $b : $($_.Exception.Message)" }
}

foreach ($f in Get-ChildItem "$Set\contacts" -Filter *.vcf -ErrorAction SilentlyContinue | Sort-Object Name) {
    try {
        $c = $app.Session.OpenSharedItem($f.FullName)
        "{0}: opened as {1}" -f $f.Name, $c.MessageClass
        Save $c ($f.BaseName + ".contact")
        $c.Close(1)
    } catch { "FAILED vcard $($f.Name) : $($_.Exception.Message)" }
}

Start-Sleep 2
try { $ns.RemoveStore($root) } catch { }
Start-Sleep 3
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
