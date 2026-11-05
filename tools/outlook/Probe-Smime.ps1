# Opens S/MIME messages in Outlook with the matching private keys imported into the current user's certificate store, records what Outlook shows
# (item class, subject, body start, attachments) and saves each item as .msg for the library to read. The certificates are removed again at the end,
# whatever happens. Test-side only; the certificates are throw-away test keys.
#
#   Probe-Smime.ps1 -Set <dir> -Out <dir> [-Password pw] [-Profile OneMailOracle] [-Dir <work dir>]
# <Set> holds the .msg files to open (S/MIME items) and, in sub-directories, key.p12 files to import. Saved copies are named <name>.saved.msg.
param(
    [Parameter(Mandatory)][string]$Set,
    [Parameter(Mandatory)][string]$Out,
    [string]$Password = "pw",
    [string]$Profile = "OneMailOracle",
    [string]$Dir = "$env:USERPROFILE\onemail"
)
$ErrorActionPreference = "Continue"
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
New-Item -ItemType Directory -Force $Out, "$Dir\work" | Out-Null
$secure = ConvertTo-SecureString $Password -AsPlainText -Force
$imported = @()
$watcher = $null
$app = $null
try {
    foreach ($id in Get-ChildItem $Set -Directory -Recurse) {
        $p12 = Join-Path $id.FullName "key.p12"
        if (Test-Path $p12) {
            $c = Import-PfxCertificate -FilePath $p12 -CertStoreLocation Cert:\CurrentUser\My -Password $secure
            $imported += $c.Thumbprint
            "imported $($id.Name): $($c.Subject) $($c.Thumbprint) privateKey=$($c.HasPrivateKey) usage=$(($c.Extensions | Where-Object { $_.Oid.FriendlyName -in 'Key Usage','Enhanced Key Usage' } | ForEach-Object { $_.Format($false) }) -join ' | ')"
            $keyInfo = certutil -user -store My $c.Thumbprint 2>&1 | Select-String -Pattern "Provider|Private key|Key Container|Cannot find|Encryption test" | ForEach-Object { $_.Line.Trim() }
            "  certutil: " + ($keyInfo -join " ; ")
        }
    }
    $watcher = Start-DialogWatcher -Log "$Dir\work\dialogs.log"
    $app = Start-OutlookSession -Profile $Profile -Dir $Dir
    foreach ($f in Get-ChildItem $Set -Recurse -Filter *.msg | Sort-Object FullName) {
        $rel = $f.FullName.Substring($Set.TrimEnd('\').Length + 1)
        $name = ($rel -replace '[\\/]', '__') -replace '\.msg$', ''
        $work = "$Dir\work\sm-$([guid]::NewGuid().ToString('N')).msg"
        Copy-Item $f.FullName $work
        $mark = if (Test-Path "$Dir\work\dialogs.log") { (Get-Content "$Dir\work\dialogs.log").Count } else { 0 }
        try {
            $it = $app.Session.OpenSharedItem($work)
            $atts = @(for ($k = 1; $k -le $it.Attachments.Count; $k++) { $it.Attachments.Item($k).FileName })
            $body = ([string]$it.Body); if ($body.Length -gt 60) { $body = $body.Substring(0, 60) }
            "{0}: class={1} subject={2} body=[{3}] attachments=[{4}]" -f $name, $it.MessageClass, $it.Subject, ($body -replace "[\r\n]+", " "), ($atts -join ",")
            try { $it.SaveAs((Join-Path $Out "$name.saved.msg"), 9) } catch { "  save failed: " + $_.Exception.Message }
            $it.Close(1)
        } catch { "{0}: FAILED {1}" -f $name, $_.Exception.Message }
        $dl = @(if (Test-Path "$Dir\work\dialogs.log") { Get-Content "$Dir\work\dialogs.log" | Select-Object -Skip $mark })
        if ($dl.Count -gt 0) { "  dialogs: " + ($dl -join " | ") }
        Remove-Item $work -Force -ErrorAction SilentlyContinue
    }
} finally {
    if ($watcher) { Stop-DialogWatcher $watcher }
    if ($app) { Stop-OutlookGracefully $app }
    foreach ($t in $imported) { Remove-Item "Cert:\CurrentUser\My\$t" -DeleteKey -ErrorAction SilentlyContinue; "removed $t" }
}
