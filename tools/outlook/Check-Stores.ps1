# Opens each PST in a set through Outlook's object model, in the dedicated profile, and writes what Outlook sees (folders, counts, an item sample) as
# JSON next to a record of every dialog Outlook raised. A file Outlook refuses, or opens only after a repair prompt, is recorded as such.
# Test-side only: the library never talks to Outlook.
#
#   Check-Stores.ps1 -Set <dir with .pst files> -Out <result dir> [-Filter *.pst] [-Sample 200] [-Profile OneMailOracle] [-Dir <work dir>]
param(
    [Parameter(Mandatory)][string]$Set,
    [Parameter(Mandatory)][string]$Out,
    [string]$Filter = "*.pst",
    [int]$Sample = 200,
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

$files = Get-ChildItem $Set -Recurse -Filter $Filter -File | Sort-Object FullName
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Set.TrimEnd('\').Length + 1)
    $res = [ordered]@{ file = $rel; length = $f.Length }
    $work = "$Dir\work\open-$([guid]::NewGuid().ToString('N')).pst"
    Copy-Item $f.FullName $work
    $mark = if (Test-Path $dialogLog) { (Get-Content $dialogLog).Count } else { 0 }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        # An Outlook that died on the previous file is replaced before this one.
        $alive = $false
        try { $alive = [bool](Get-Process OUTLOOK -ErrorAction SilentlyContinue) -and ($null -ne $app.Session) -and ($app.Session.Stores.Count -ge 0) } catch { }
        if (-not $alive) { $app = Start-OutlookSession -Profile $Profile -Dir $Dir }
        $ns = $app.Session
        $res.stage = "AddStoreEx"; $ns.AddStoreEx($work, 2)
        $res.stage = "open"
        $store = $ns.Stores | Where-Object { $_.FilePath -eq $work } | Select-Object -First 1
        if (-not $store) { throw "AddStoreEx returned but the store is not in the session" }
        $res.opened = $true
        $root = $store.GetRootFolder()
        $res.rootName = $root.Name
        $res.stage = "enumerate"; $res.folders = @(Get-OutlookFolders $root "" $Sample)
        $res.stage = "remove"
        $ns.RemoveStore($root)
    } catch {
        $res.opened = $false
        $res.error = $_.Exception.Message
        if ($res.error -match '0x800706BA|0x800706BE|0x80010108|0x800706BF') { $res.crashed = $true }
        try { $s2 = $app.Session.Stores | Where-Object { $_.FilePath -eq $work } | Select-Object -First 1; if ($s2) { $app.Session.RemoveStore($s2.GetRootFolder()) } } catch { }
    }
    $res.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Start-Sleep -Milliseconds 700
    $res.dialogs = @(if (Test-Path $dialogLog) { Get-Content $dialogLog | Select-Object -Skip $mark })
    $json = Join-Path $Out (($rel -replace '[\\/]', '__') + ".outlook.json")
    try { $text = $res | ConvertTo-Json -Depth 8 -Compress } catch { $res.folders = @(); $res.jsonError = $_.Exception.Message; $text = $res | ConvertTo-Json -Depth 4 -Compress }
    $text | Set-Content $json -Encoding UTF8
    Remove-Item $work -Force -ErrorAction SilentlyContinue
    "{0}: opened={1} crashed={5} dialogs={2} {3}s stage={6} {4}" -f $rel, $res.opened, $res.dialogs.Count, $res.seconds, $res.error, [bool]$res.crashed, $res.stage
}
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
