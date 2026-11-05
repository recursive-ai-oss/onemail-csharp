# Runs the Inbox Repair Tool (SCANPST.EXE) over copies of PST files and records what it reports. Test-side only.
#
#   Run-ScanPst.ps1 <Set dir> <Out dir> [Filter=*.pst] [Repair=0] [PerFileSeconds=240] [Trace=0]
#
# The tool is a plain dialog: for every file it is started, the path is put into its edit box, Start is pressed, and the text of every control
# and of every message box it raises is recorded to <Out dir>\<name>.scanpst.json together with the log file it writes beside the copy. With
# Repair=1 the tool's Repair is pressed afterwards (used only to compare its repair with ours, never on the files under test).
# Trace=1 writes the window's controls every few seconds to <Out dir>\trace.txt (to learn what a new tool version shows).
param([string]$Set, [string]$Out, [string]$Filter = '*.pst', [int]$Repair = 0, [int]$PerFileSeconds = 240, [int]$Trace = 0)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
Add-Type -TypeDefinition @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class W32 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static List<IntPtr> Dialogs(uint pid) { var r = new List<IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); var c = new StringBuilder(64); GetClassName(h, c, 64); if (p == pid && c.ToString() == "#32770" && IsWindowVisible(h)) r.Add(h); return true; }, IntPtr.Zero); return r; }
  public static IntPtr Child(IntPtr top, int id) { IntPtr f = IntPtr.Zero; EnumChildWindows(top, (c, l) => { if (GetDlgCtrlID(c) == id) { f = c; return false; } return true; }, IntPtr.Zero); return f; }
  public static string Text(IntPtr h) { var t = new StringBuilder(8192); GetWindowText(h, t, 8192); return t.ToString(); }
  public static bool Enabled(IntPtr h) { return IsWindowEnabled(h); }
  public static void SetText(IntPtr h, string s) { SendMessage(h, 0x0C, IntPtr.Zero, s); }
  public static void Click(IntPtr h) { PostMessage(h, 0xF5, IntPtr.Zero, IntPtr.Zero); }
  public static void Command(IntPtr dlg, int id) { PostMessage(dlg, 0x111, (IntPtr)id, IntPtr.Zero); }
  static string Info(IntPtr h) { var c = new StringBuilder(64); GetClassName(h, c, 64); return GetDlgCtrlID(h) + " [" + c + "]" + (IsWindowVisible(h) ? "" : " hidden") + (IsWindowEnabled(h) ? "" : " disabled") + " '" + Text(h) + "'"; }
  public static string Describe(IntPtr top) { var r = new List<string>(); r.Add("DIALOG '" + Text(top) + "'"); EnumChildWindows(top, (c, l) => { r.Add("  " + Info(c)); return true; }, IntPtr.Zero); return string.Join("\n", r); }
  // Visible static and button texts of a dialog, for the record.
  public static string Texts(IntPtr top) { var r = new List<string>(); EnumChildWindows(top, (c, l) => { if (IsWindowVisible(c)) { var t = Text(c); if (t.Length > 0) r.Add(t); } return true; }, IntPtr.Zero); return string.Join(" / ", r); }
}
'@
$ScanPst = $script:ScanPstExe
New-Item -ItemType Directory -Force $Out, "$Out\work" | Out-Null
$ID_EDIT = 1004; $ID_START = 1012; $ID_CLOSE = 1013

function Wait-Main([uint32]$ProcId) {
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        foreach ($d in [W32]::Dialogs($ProcId)) { if ([W32]::Text($d) -like '*Inbox Repair Tool*') { return $d } }
    }
    $null
}
# The tool shows its outcome in a second dialog (text in control 1043, Repair 1048, Cancel 1049). Returns that dialog once it is up.
function Find-Outcome([uint32]$ProcId, $Main) {
    foreach ($d in [W32]::Dialogs($ProcId)) { if ($d -ne $Main -and [W32]::Child($d, 1043) -ne [IntPtr]::Zero) { return $d } }
    $null
}
# Any other message box (an error, a prompt): record and dismiss with OK.
function Clear-Boxes([uint32]$ProcId, $Main, $Result) {
    foreach ($d in [W32]::Dialogs($ProcId)) {
        if ($d -eq $Main -or [W32]::Child($d, 1043) -ne [IntPtr]::Zero) { continue }
        $Result.texts += ('box [{0}]: {1}' -f [W32]::Text($d), [W32]::Texts($d))
        if ($Trace) { Add-Content "$Out\trace.txt" ([W32]::Describe($d)) }
        [W32]::Command($d, 1); Start-Sleep -Milliseconds 400
    }
}

function Invoke-ScanOne([string]$File) {
    $name = [IO.Path]::GetFileName($File)
    $copy = "$Out\work\$name"; Copy-Item $File $copy -Force
    $stem = [IO.Path]::GetFileNameWithoutExtension($name)
    Get-ChildItem "$Out\work" -Filter "$stem*.log" | Remove-Item -ErrorAction SilentlyContinue
    $p = Start-Process $ScanPst -PassThru
    $result = [ordered]@{ file = $name; bytes = (Get-Item $copy).Length; texts = @(); log = ''; status = 'started' }
    try {
        $main = Wait-Main $p.Id
        if (-not $main) { $result.status = 'no window'; return $result }
        [W32]::SetText([W32]::Child($main, $ID_EDIT), $copy); Start-Sleep -Milliseconds 500
        [W32]::Click([W32]::Child($main, $ID_START))
        $t0 = Get-Date; $outcome = $null; $last = ''
        while (((Get-Date) - $t0).TotalSeconds -lt $PerFileSeconds -and -not $outcome) {
            Start-Sleep -Seconds 2
            if ($p.HasExited) { $result.status = 'tool exited'; return $result }
            Clear-Boxes $p.Id $main $result
            $outcome = Find-Outcome $p.Id $main
            $snap = [W32]::Describe($main)
            if ($Trace -and $snap -ne $last) { Add-Content "$Out\trace.txt" ("--- {0}`n{1}" -f $name, $snap); $last = $snap }
        }
        if (-not $outcome) { $result.status = 'timeout' }
        else {
            $result.status = 'scanned'
            $result.texts += ('outcome: ' + [W32]::Text([W32]::Child($outcome, 1043)))
            if ($Repair) {
                [W32]::Command($outcome, 1048); Start-Sleep -Seconds 3
                $t1 = Get-Date; $fin = $false
                while (((Get-Date) - $t1).TotalSeconds -lt $PerFileSeconds -and -not $fin) {
                    Start-Sleep -Seconds 2
                    foreach ($d in [W32]::Dialogs($p.Id)) {
                        if ($d -eq $main) { continue }
                        $t = [W32]::Texts($d); if ($t -match 'complete|Repair') { $result.texts += ('repair: ' + $t); [W32]::Command($d, 1); $fin = $true }
                    }
                }
                $result.status = if ($fin) { 'repaired' } else { 'repair timeout' }
            } else { [W32]::Command($outcome, 1049); Start-Sleep -Milliseconds 500 }
        }
        $logf = Get-ChildItem "$Out\work" -Filter "$stem*.log" | Select-Object -First 1
        if ($logf) { $result.log = Get-Content $logf.FullName -Raw; Copy-Item $logf.FullName "$Out\$name.scanpst.log" -Force }
    } finally {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        if ($Repair) { Copy-Item $copy "$Out\$name.repaired" -Force }
        Remove-Item $copy -ErrorAction SilentlyContinue
    }
    $result
}

foreach ($f in (Get-ChildItem $Set -Recurse -Filter $Filter | Sort-Object FullName)) {
    $rel = $f.FullName.Substring($Set.TrimEnd('\').Length + 1).Replace('\', '__')
    $r = Invoke-ScanOne $f.FullName
    $r.file = $rel
    $r | ConvertTo-Json -Depth 4 | Set-Content "$Out\$rel.scanpst.json" -Encoding UTF8
    "{0}: {1}" -f $rel, $r.status
}
