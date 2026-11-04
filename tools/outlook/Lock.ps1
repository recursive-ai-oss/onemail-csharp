# Acquires or releases the machine-wide oracle lock shared with other projects that drive Office on this machine.
#   Lock.ps1 acquire <lock path> <instance id> [maxWaitMinutes = 120]   waits for the lock; a lock older than 2 hours is stale and is removed (reported)
#   Lock.ps1 release <lock path>
# The lock is a file created atomically (CreateNew) holding: project, instance, start time.
param([Parameter(Mandatory)][string]$Action, [Parameter(Mandatory)][string]$Path, [string]$Instance = "unknown", [int]$MaxWaitMinutes = 120)
if ($Action -eq "release") { Remove-Item $Path -ErrorAction SilentlyContinue; "released"; exit 0 }
$deadline = (Get-Date).AddMinutes($MaxWaitMinutes)
while ($true) {
    try {
        $fs = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $bytes = [Text.Encoding]::UTF8.GetBytes("project: onemail`ninstance: $Instance`nstarted: $((Get-Date).ToString('o'))`n")
        $fs.Write($bytes, 0, $bytes.Length); $fs.Dispose()
        "acquired"; exit 0
    } catch [IO.IOException] {
        $age = (Get-Date) - (Get-Item $Path -ErrorAction SilentlyContinue).LastWriteTime
        if ($age -gt [TimeSpan]::FromHours(2)) { "STALE lock ($([int]$age.TotalMinutes) min old) removed: $((Get-Content $Path -ErrorAction SilentlyContinue) -join ' | ')"; Remove-Item $Path -ErrorAction SilentlyContinue; continue }
        if ((Get-Date) -gt $deadline) { "lock busy: $((Get-Content $Path -ErrorAction SilentlyContinue) -join ' | ')"; exit 75 }
        Start-Sleep 20
    }
}
