$log = "C:\Users\feaz\Desktop\_Projeler\Mouse Firmware\tools\lgs_install.log"
function L($m) { "$((Get-Date).ToString('HH:mm:ss')) $m" | Add-Content -Path $log }
"---" | Set-Content -Path $log
L "launching elevated installer (UAC prompt should appear)"
$exe = "C:\Users\feaz\Desktop\_Projeler\Mouse Firmware\vendor\LGS_9.04.49_x64.exe"
try {
    $p = Start-Process -FilePath $exe -ArgumentList "/S" -Verb RunAs -PassThru -ErrorAction Stop
} catch {
    L "start failed: $_"
    exit 1
}
L "started pid $($p.Id)"
$target = "C:\Program Files\Logitech Gaming Software\LCore.exe"
$deadline = (Get-Date).AddMinutes(20)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (Test-Path $target) { L "LCore.exe present -> install looks done"; break }
    if ($p.HasExited) { L "installer exited, code $($p.ExitCode)"; break }
}
Get-ChildItem "C:\Program Files\Logitech Gaming Software" -ErrorAction SilentlyContinue |
    Select-Object -First 5 Name | ForEach-Object { L ("  " + $_.Name) }
L "end"
