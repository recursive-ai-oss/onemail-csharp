# Ends what earlier oracle runs left behind: their scheduled tasks, Outlook, and the dialog-watcher processes they started.
# Run it over ssh (no desktop needed): powershell -NoProfile -ExecutionPolicy Bypass -File Cleanup.ps1
Get-ScheduledTask -TaskName 'OneMailOracle-*' -ErrorAction SilentlyContinue | ForEach-Object { Stop-ScheduledTask $_ -ErrorAction SilentlyContinue; Unregister-ScheduledTask $_ -Confirm:$false; "removed task $($_.TaskName)" }
Get-Process OUTLOOK, SCANPST -ErrorAction SilentlyContinue | ForEach-Object { "ending $($_.ProcessName) $($_.Id)"; Stop-Process $_ -Force }
Get-ChildItem "$env:USERPROFILE\onemail\work\watcher-*.pid" -ErrorAction SilentlyContinue | ForEach-Object { $id = [int](Get-Content $_); if (Get-Process -Id $id -ErrorAction SilentlyContinue) { "ending watcher $id"; Stop-Process -Id $id -Force }; Remove-Item $_ }
