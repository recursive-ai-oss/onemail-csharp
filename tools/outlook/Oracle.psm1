# Helpers shared by the Outlook oracle scripts. Test-side only: the library itself never talks to Outlook.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes

$script:OutlookExe = "C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE"
$script:ScanPstExe = "C:\Program Files\Microsoft Office\root\Office16\SCANPST.EXE"
$script:ResiliencyKey = "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency"

function Save-Shot([string]$Path) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $bmp.Save($Path)
}

# Clicks the button named $Button in the top-level window whose name matches $Window; returns whether it did.
function Invoke-DialogButton([string]$Window, [string]$Button) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $wins) {
        if ($w.Current.Name -notlike $Window) { continue }
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Button)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
        $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    }
    return $false
}

# Outlook that was ended abruptly asks about safe mode next time; clearing its resiliency record avoids the prompt.
function Clear-OutlookResiliency { Remove-Item $script:ResiliencyKey -Recurse -ErrorAction SilentlyContinue }

# Only processes this run started are ended: callers pass the ids they got from Start-Process or from before/after process lists.
function Stop-OutlookGracefully($App) {
    try { if ($App) { $App.Quit() } } catch { }
    for ($i = 0; $i -lt 40 -and (Get-Process OUTLOOK -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    Get-Process OUTLOOK -ErrorAction SilentlyContinue | Stop-Process -Force
    Clear-OutlookResiliency
}


# Replaces the profile with a fresh one from profile.prf.template. The store in the profile is only a placeholder; checks add their own, and a
# profile that kept a store from an earlier run (after a crash) makes Outlook ask for the missing file again and again.
function Initialize-OutlookProfile([string]$Profile, [string]$Dir) {
    Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Profiles\$Profile" -Recurse -ErrorAction SilentlyContinue
    Remove-Item "$Dir\stores\profile-store.pst" -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$Dir\stores" | Out-Null
    $prf = "$Dir\bin\profile.prf"
    (Get-Content "$PSScriptRoot\profile.prf.template" -Raw).Replace("%PROFILE%", $Profile).Replace("%PST%", "$Dir\stores\profile-store.pst") | Set-Content $prf -Encoding ASCII
    $p = Start-Process $script:OutlookExe -ArgumentList "/importprf", $prf -PassThru
    for ($i = 0; $i -lt 60 -and -not (Test-Path "HKCU:\Software\Microsoft\Office\16.0\Outlook\Profiles\$Profile"); $i++) { Start-Sleep 1 }
    Start-Sleep 20
    Get-Process OUTLOOK -ErrorAction SilentlyContinue | Stop-Process -Force
    Clear-OutlookResiliency
}

# Starts Outlook on the dedicated profile (ending any Outlook already running: this machine's owner does not use it) and returns its Application object.
function Start-OutlookSession([string]$Profile, [string]$Dir) {
    Get-Process OUTLOOK -ErrorAction SilentlyContinue | Stop-Process -Force
    Clear-OutlookResiliency
    Initialize-OutlookProfile $Profile $Dir
    Start-Sleep 2
    Start-Process $script:OutlookExe -ArgumentList "/profile", $Profile | Out-Null
    $app = $null
    for ($i = 0; $i -lt 90 -and -not $app; $i++) {
        Start-Sleep 2
        try {
            # Outlook enters the running-object table late; COM activation reaches the instance already running.
            $app = try { [Runtime.InteropServices.Marshal]::GetActiveObject("Outlook.Application") } catch { New-Object -ComObject Outlook.Application }
            $null = $app.Session.Stores.Count
        } catch { $app = $null; $last = $_.Exception.Message }
    }
    if (-not $app) { throw "Outlook did not come up on profile ${Profile}: $last" }
    Start-Sleep 5
    return $app
}

# Runs in its own process: every top-level dialog of Outlook is logged (title, visible text, button names) and dismissed, so a prompt never blocks a
# run and is never missed. The main windows ("... - Outlook", "Opening") are left alone. Buttons are found by name whatever their UI type, because
# message boxes expose them as panes; if none can be invoked the dialog gets the matching command message, then WM_CLOSE.
function Start-DialogWatcher([string]$Log) {
    Remove-Item $Log -ErrorAction SilentlyContinue
    Start-Job -ArgumentList $Log -ScriptBlock {
        param($Log)
        Set-Content "$(Split-Path $Log)\watcher-$PID.pid" $PID
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        Add-Type -Namespace Native -Name Win -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
        $AE = [System.Windows.Automation.AutomationElement]
        $ids = @{ 'OK' = 1; 'Cancel' = 2; 'Close' = 2; 'No' = 7; 'Yes' = 6 }
        while ($true) {
            Start-Sleep -Milliseconds 400
            $pids = @(Get-Process OUTLOOK -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
            if (-not $pids) { continue }
            foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
                $c = $w.Current
                if ($pids -notcontains $c.ProcessId) { continue }
                if ($c.Name -eq '' -or $c.Name -like '*- Outlook' -or $c.Name -like 'Opening*') { continue }
                $all = @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
                $names = @($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
                $buttons = @($names | Where-Object { $ids.ContainsKey($_) })
                $texts = @($names | Where-Object { -not $ids.ContainsKey($_) })
                Add-Content $Log "$(Get-Date -Format o)|$($c.Name)|$($texts -join ' ')|buttons: $($buttons -join ',')"
                $done = $false
                foreach ($want in 'OK', 'Close', 'No', 'Cancel') {
                    $b = $all | Where-Object { $_.Current.Name -eq $want } | Select-Object -First 1
                    if (-not $b) { continue }
                    try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $done = $true } catch { }
                    if (-not $done) { $h = [IntPtr]$c.NativeWindowHandle; $done = [Native.Win]::PostMessage($h, 0x111, [IntPtr]$ids[$want], [IntPtr]::Zero) }
                    break
                }
                if (-not $done) { [void][Native.Win]::PostMessage([IntPtr]$c.NativeWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
            }
        }
    }
}
function Stop-DialogWatcher($Job) { if ($Job) { Stop-Job $Job -ErrorAction SilentlyContinue; Remove-Job $Job -Force -ErrorAction SilentlyContinue } }

# One record per folder, depth first: path below the root, container class, Items.Count, and up to $Sample items. When the folder has more
# items than the sample, the sample is spread evenly over the folder.
function Get-OutlookFolders($Folder, [string]$Path, [int]$Sample) {
    $name = $Folder.Name
    $here = if ($Path) { "$Path/$name" } else { $name }
    $rows = @()
    $countError = $null
    $count = $null
    try { $items = $Folder.Items; $count = $items.Count } catch { $countError = $_.Exception.Message }
    if ($null -eq $count -and -not $countError) { $countError = "Items.Count returned nothing" }
    if ($count -gt 0) {
        $step = [math]::Max(1, [math]::Floor($count / $Sample))
        for ($i = 1; $i -le $count -and $rows.Count -lt $Sample; $i += $step) {
            try {
                $it = $items.Item($i)
                $rows += [ordered]@{
                    subject = [string]$it.Subject; class = [string]$it.MessageClass; recipients = $it.Recipients.Count; attachments = $it.Attachments.Count
                    unread = [bool]$it.UnRead; sender = [string]$it.SenderName; size = [int]$it.Size
                    sentUtc = if ($it.SentOn -and $it.SentOn.Year -lt 4000) { $it.SentOn.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss") } else { "" }
                }
            } catch { $rows += [ordered]@{ error = $_.Exception.Message; index = $i } }
        }
    }
    [ordered]@{ path = $here; class = "$($Folder.DefaultMessageClass)"; count = $count; countError = $countError; sampled = $rows.Count; items = $rows }
    foreach ($sub in $Folder.Folders) { Get-OutlookFolders $sub $here $Sample }
}

Export-ModuleMember -Function * -Variable OutlookExe, ScanPstExe
