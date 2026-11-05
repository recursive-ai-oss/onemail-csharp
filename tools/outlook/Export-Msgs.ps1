# Saves every item of a PST as an .msg (Unicode) with Outlook itself, and one mail item also as an .oft: the files Outlook writes, to be read by our
# reader and to be compared with the ones we write. Test-side only.
#
#   Export-Msgs.ps1 -Pst <file.pst> -Out <dir> [-Profile OneMailOracle] [-Dir <work dir>]
param(
    [Parameter(Mandatory)][string]$Pst,
    [Parameter(Mandatory)][string]$Out,
    [string]$Profile = "OneMailOracle",
    [string]$Dir = "$env:USERPROFILE\onemail"
)
$ErrorActionPreference = "Stop"
trap { Write-Output ("failed at line " + $_.InvocationInfo.ScriptLineNumber + ": " + $_.InvocationInfo.Line.Trim() + " :: " + $_.Exception.Message); exit 1 }
Import-Module "$PSScriptRoot\Oracle.psm1" -Force
New-Item -ItemType Directory -Force $Out, "$Dir\work" | Out-Null
$watcher = Start-DialogWatcher -Log "$Dir\work\dialogs.log"
$app = Start-OutlookSession -Profile $Profile -Dir $Dir
$ns = $app.Session
$work = "$Dir\work\export-$([guid]::NewGuid().ToString('N')).pst"
Copy-Item $Pst $work
$ns.AddStoreEx($work, 2)
$store = $ns.Stores | Where-Object { $_.FilePath -eq $work } | Select-Object -First 1
$n = 0; $oft = $false
function Export-Folder($folder, [string]$prefix) {
    for ($i = 1; $i -le $folder.Items.Count; $i++) {
        $it = $folder.Items.Item($i)
        $name = "{0}-{1:D3}.msg" -f $prefix, $i
        $it.SaveAs((Join-Path $Out $name), 9)   # olMSGUnicode
        $script:n++
        if (-not $script:oft -and $it.Class -eq 43) {
            $it.SaveAs((Join-Path $Out ("{0}-{1:D3}.oft" -f $prefix, $i)), 2)   # olTemplate
            $script:oft = $true
        }
    }
    $k = 0
    foreach ($sub in $folder.Folders) { $k++; Export-Folder $sub ("{0}_{1}" -f $prefix, $k) }
}
Export-Folder $store.GetRootFolder() "f"
"exported $n items"
Start-Sleep 2
$ns.RemoveStore($store.GetRootFolder())
Stop-DialogWatcher $watcher
Stop-OutlookGracefully $app
Remove-Item $work -Force -ErrorAction SilentlyContinue
