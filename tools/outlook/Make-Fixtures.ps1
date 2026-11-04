# Makes a PST with Outlook itself, through the object model: the fixtures the oracle compares our writers with.
#
#   Make-Fixtures.ps1 -Kind big|mixed -Out <file.pst> [-Items 600]
#
#   big    one folder of -Items text messages, one of -Items/4 with recipients and attachments, and a deleted folder
#   mixed  folders with every common item kind (mail, appointment, contact, task, note, journal), attachments, an embedded message, categories,
#          flags, a draft, a read/unread mix, and a few items deleted
param(
    [Parameter(Mandatory)][ValidateSet("big", "mixed")][string]$Kind,
    [Parameter(Mandatory)][string]$Out,
    [int]$Items = 600,
    [string]$Profile = "OneMailOracle",
    [string]$Dir = "$env:USERPROFILE\onemail"
)
$ErrorActionPreference = "Stop"
trap { Write-Output ("failed at line " + $_.InvocationInfo.ScriptLineNumber + ": " + $_.InvocationInfo.Line.Trim() + " :: " + $_.Exception.Message); exit 1 }
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
$dialogLog = "$Dir\work\dialogs.log"
New-Item -ItemType Directory -Force (Split-Path $Out), "$Dir\work" | Out-Null
Remove-Item $Out -ErrorAction SilentlyContinue
$watcher = Start-DialogWatcher -Log $dialogLog
$app = Start-OutlookSession -Profile $Profile -Dir $Dir
$ns = $app.Session
$ns.AddStoreEx($Out, 2)
$store = $ns.Stores | Where-Object { $_.FilePath -eq $Out } | Select-Object -First 1
$root = $store.GetRootFolder()
$tmp = "$Dir\work\fixture-files"; New-Item -ItemType Directory -Force $tmp | Out-Null

function New-Attachment([string]$name, [int]$bytes) {
    $p = Join-Path $tmp $name
    $b = New-Object byte[] $bytes; (New-Object Random 7).NextBytes($b); [IO.File]::WriteAllBytes($p, $b); $p
}

function Add-Mail($folder, [string]$subject, [string]$body, [string[]]$to = @(), [string[]]$files = @(), [bool]$read = $true, [bool]$html = $false) {
    $m = $folder.Items.Add(0)   # olMailItem
    $m.Subject = $subject
    if ($html) { $m.HTMLBody = "<html><body><p>$body</p></body></html>" } else { $m.Body = $body }
    foreach ($t in $to) { $null = $m.Recipients.Add($t) }
    if ($to.Count) { $null = $m.Recipients.ResolveAll() }
    foreach ($f in $files) { $null = $m.Attachments.Add($f) }
    $m.UnRead = -not $read
    $m.Save()
    # Items.Add on a mail folder saves into the default store's Drafts; the move puts the item where it belongs.
    $m.Move($folder)
}

if ($Kind -eq "big") {
    $a = $root.Folders.Add("Big")
    for ($i = 1; $i -le $Items; $i++) { $null = Add-Mail $a "message $i" ("body of message $i " + ("x" * ($i % 50))) @() @() ($i % 3 -ne 0) }
    $b = $root.Folders.Add("Recipients")
    $att = New-Attachment "a.bin" 3000
    for ($i = 1; $i -le [math]::Max(8, $Items / 4); $i++) { $null = Add-Mail $b "with attachment $i" "text" @("p$i@example.com", "q$i@example.com", "r$i@example.com") @($att) }
    $c = $root.Folders.Add("To delete")
    for ($i = 1; $i -le 20; $i++) { $null = Add-Mail $c "doomed $i" "bye" }
    $c.Delete()
}
else {
    $inbox = $root.Folders.Add("Mail")
    $null = Add-Mail $inbox "plain" "plain text body" @("one@example.com")
    $null = Add-Mail $inbox "html" "html body" @("one@example.com", "two@example.com") @() $false $true
    $null = Add-Mail $inbox "with files" "see attached" @("one@example.com") @((New-Attachment "small.bin" 500), (New-Attachment "big.bin" 70000))
    $null = Add-Mail $inbox "unread" "not read yet" @() @() $false
    $d = Add-Mail $inbox "categorised" "has categories" @(); $d.Categories = "Red category, Blue category"; $d.FlagStatus = 2; $d.Importance = 2; $d.Save()
    # an embedded message: attach a saved MSG
    $inner = Add-Mail $inbox "inner message" "i am inside" @("in@example.com")
    $msgPath = Join-Path $tmp "inner.msg"; $inner.SaveAs($msgPath, 3)
    $outer = Add-Mail $inbox "outer message" "has an embedded message" @()
    $null = $outer.Attachments.Add($msgPath, 1); $outer.Save()
    $cal = $root.Folders.Add("Calendar items", 9)   # olFolderAppointment-type container
    $ap = $cal.Items.Add(1); $ap.Subject = "meeting"; $ap.Start = [datetime]"2026-11-10 10:00"; $ap.End = [datetime]"2026-11-10 11:00"; $ap.Location = "room 1"; $ap.Body = "agenda"; $ap.Save()
    $rec = $cal.Items.Add(1); $rec.Subject = "weekly"; $rec.Start = [datetime]"2026-11-02 09:00"; $rec.End = [datetime]"2026-11-02 09:30"
    $pat = $rec.GetRecurrencePattern(); $pat.RecurrenceType = 1; $pat.Interval = 1; $pat.PatternStartDate = [datetime]"2026-11-02"; $pat.Occurrences = 8; $rec.Save()
    $con = $root.Folders.Add("People", 10)
    $c1 = $con.Items.Add(2); $c1.FullName = "Ada Lovelace"; $c1.Email1Address = "ada@example.com"; $c1.CompanyName = "Analytical"; $c1.BusinessTelephoneNumber = "+1 555 0100"; $c1.Save()
    $tasks = $root.Folders.Add("Todo", 13)
    $t1 = $tasks.Items.Add(3); $t1.Subject = "task"; $t1.DueDate = [datetime]"2026-12-01"; $t1.Save()
    $notes = $root.Folders.Add("Notes here", 12)
    $n1 = $notes.Items.Add(5); $n1.Body = "a note"; $n1.Save()
    $del = $root.Folders.Add("Deleted from")
    $x1 = Add-Mail $del "to delete one" "x"; $x2 = Add-Mail $del "to delete two" "y"; $x3 = Add-Mail $del "kept" "z"
    $x1.Delete(); $x2.Delete()
}
Start-Sleep 2
$ns.RemoveStore($root)
Start-Sleep 3
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
"{0}: {1} bytes" -f $Out, (Get-Item $Out).Length
