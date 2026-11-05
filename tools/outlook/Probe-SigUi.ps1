# Opens signed S/MIME items in Outlook's reading window and reads the signature state from the window itself with UI Automation: every element's
# name and value, the "Signed By" line, and the Digital Signature dialog that line opens (valid / invalid / untrusted wording). Test-side only.
#   Probe-SigUi.ps1 -Set <dir with .msg> -Out <dir> [-Profile OneMailOracle] [-Dir <work dir>]
#
# With -TrustRoot the set's root.crt (made for the run, valid for a day) is trusted in the current user's Root store for the duration of the run, by writing
# the store's registry entry directly (no prompt), and removed at the end; the signer's PKCS #12 in <Set>\keys\*\key.p12 (password pw) is imported
# into the user's store and removed too. Without it the signature is judged against an untrusted chain.
param([Parameter(Mandatory)][string]$Set, [Parameter(Mandatory)][string]$Out, [switch]$TrustRoot, [string]$Profile = "OneMailOracle", [string]$Dir = "$env:USERPROFILE\onemail")
$ErrorActionPreference = "Continue"
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
New-Item -ItemType Directory -Force $Out, "$Dir\work" | Out-Null
$AE = [System.Windows.Automation.AutomationElement]
function Dump($w, [string]$tag) {
    foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $c = $e.Current; $v = ""
        try { $p = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $v = $p.Current.Value } catch {}
        if ($c.Name -or $v) { "{0}|{1}|{2}|{3}|{4}" -f $tag, $c.ControlType.ProgrammaticName.Replace("ControlType.", ""), $c.AutomationId, $c.Name, ($v -replace "\s+", " ") }
    }
}
$app = $null
$rootKey = $null
$imported = @()
try {
    if ($TrustRoot) {
        $rc = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList "$Set\root.crt"
        $rootKey = "HKCU:\Software\Microsoft\SystemCertificates\Root\Certificates\$($rc.Thumbprint)"
        $raw = $rc.RawData
        $blob = [byte[]]([BitConverter]::GetBytes([uint32]32) + [BitConverter]::GetBytes([uint32]1) + [BitConverter]::GetBytes([uint32]$raw.Length) + $raw)
        New-Item -Path $rootKey -Force | Out-Null
        New-ItemProperty -Path $rootKey -Name Blob -PropertyType Binary -Value $blob -Force | Out-Null
        "trusted test root $($rc.Thumbprint)"
        foreach ($k in Get-ChildItem "$Set\keys" -Directory -ErrorAction SilentlyContinue) {
            $c = Import-PfxCertificate -FilePath "$($k.FullName)\key.p12" -CertStoreLocation Cert:\CurrentUser\My -Password (ConvertTo-SecureString "pw" -AsPlainText -Force)
            $imported += $c.Thumbprint
        }
    }
    $app = Start-OutlookSession -Profile $Profile -Dir $Dir
    $ns = $app.Session
    foreach ($f in Get-ChildItem $Set -Filter *.msg | Sort-Object Name) {
        $work = "$Dir\work\su-$([guid]::NewGuid().ToString('N')).msg"
        Copy-Item $f.FullName $work
        $it = $ns.OpenSharedItem($work)
        $it.Display(); Start-Sleep 5
        $win = $null
        foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($w.Current.Name -like "*Message*" -and $w.Current.ProcessId -in (Get-Process OUTLOOK).Id) { $win = $w }
        }
        if (-not $win) { "== $($f.BaseName): no window"; continue }
        "== $($f.BaseName): window '$($win.Current.Name)'"
        Dump $win "inspector" | Where-Object { $_ -match "Sign|Digital|Encrypt|Valid|Trust|Certificate|warning|Security" }
        # The signature button sits beside the "Signed By" line; clicking it opens the Digital Signature dialog, whose text says valid, changed or untrusted.
        $sb = $null
        foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($e.Current.Name -eq "Signed By" -and $e.Current.ControlType.ProgrammaticName -like "*Pane" -and -not $sb) { $sb = $e } }
        if ($sb) {
            $r = $sb.Current.BoundingRectangle
            "  Signed By pane at $($r.Left),$($r.Top) $($r.Width)x$($r.Height)"
            foreach ($e in $sb.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { "  child {0} '{1}' {2}" -f $e.Current.ControlType.ProgrammaticName, $e.Current.Name, $e.Current.BoundingRectangle }
            foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                $br = $e.Current.BoundingRectangle
                if ($br.Top -gt $r.Top - 40 -and $br.Top -lt $r.Bottom + 40 -and $br.Width -gt 0) { "  near {0} id={1} '{2}' {3}" -f $e.Current.ControlType.ProgrammaticName, $e.Current.AutomationId, $e.Current.Name, $br }
            }
            # The signature icon is the small toolbar pane at the right of the "Signed By" line (no invoke pattern), so it is clicked with the mouse.
            Add-Type -Namespace Native -Name Mouse -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, System.IntPtr e);' -ErrorAction SilentlyContinue
            $tb = $null
            foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($e.Current.AutomationId -eq "4342") { $tb = $e } }
            $tr = if ($tb) { $tb.Current.BoundingRectangle } else { $r }
            $x = [int]($tr.Left + $tr.Width / 2); $y = [int]($tr.Top + $tr.Height / 2)
            try { $win.SetFocus() } catch { "  focus failed" }
            Start-Sleep -Milliseconds 800
            "  window rect " + $win.Current.BoundingRectangle
            [Native.Mouse]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 300
            [Native.Mouse]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); [Native.Mouse]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
            "  clicked $x,$y (toolbar found: $([bool]$tb)); patterns: " + (($tb.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ",")
            if ($tb) { foreach ($e in $tb.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { "  tb child {0} '{1}' {2}" -f $e.Current.ControlType.ProgrammaticName, $e.Current.Name, (($e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName.Replace("Identifiers.Pattern","") }) -join ",") } }
            Start-Sleep 3
            foreach ($t in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($t.Current.Name) { "  top '{0}' pid={1}" -f $t.Current.Name, $t.Current.ProcessId } }
            foreach ($d in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($d.Current.ProcessId -in (Get-Process OUTLOOK).Id -and $d.Current.Name -and $d.Current.Name -ne $win.Current.Name -and $d.Current.Name -notlike "*- Outlook") {
                    "  dialog '$($d.Current.Name)'"
                    Dump $d "dialog" | Select-Object -First 80
                    Invoke-DialogButton $d.Current.Name "Close" | Out-Null; Invoke-DialogButton $d.Current.Name "OK" | Out-Null
                }
            }
        } else { "  no Signed By pane" }
        try { $it.Close(1) } catch {}
        Remove-Item $work -Force -ErrorAction SilentlyContinue
    }
} finally {
    if ($app) { Stop-OutlookGracefully $app }
    if ($rootKey) { Remove-Item -Path $rootKey -Recurse -ErrorAction SilentlyContinue; "removed test root: " + (-not (Test-Path $rootKey)) }
    foreach ($t in $imported) { Remove-Item "Cert:\CurrentUser\My\$t" -DeleteKey -ErrorAction SilentlyContinue }
}
