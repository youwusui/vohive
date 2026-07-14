. (Join-Path $PSScriptRoot "VoHive-Wsl.Common.ps1")
$cfg=Read-VoHiveConfig
Assert-VoHiveAdmin
$distro=[string]$cfg.distro; $usbipd=Expand-VoHiveValue ([string]$cfg.usbipd_path)
if(-not (Test-Path -LiteralPath $usbipd)){throw "usbipd not found: $usbipd"}
$list=Invoke-VoHiveCapture { & $usbipd list }
$rx=[regex]::new([string]$cfg.module_match_regex,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$line=$list.Lines | Where-Object {$rx.IsMatch([string]$_)} | Select-Object -First 1
if(-not $line){throw "DJI/Quectel module not found"}
$busid=(($line -split "\s+") | Where-Object {$_ -match "^\d+-\d+$"} | Select-Object -First 1)
if(-not $busid){throw "Cannot parse BUSID"}
& $usbipd detach --busid $busid 2>$null | Out-Null
Start-Sleep -Seconds 2
& $usbipd bind --busid $busid --force
if($LASTEXITCODE -ne 0){throw "usbipd bind failed"}
& $usbipd attach --wsl --busid $busid
if($LASTEXITCODE -ne 0){throw "usbipd attach failed"}
$ready=$false
for($i=0;$i -lt 40;$i++){ $nodes=(& wsl.exe -d $distro -- bash -lc "test -e /dev/cdc-wdm0 && ls /dev/ttyUSB* >/dev/null 2>&1 && echo ready || true") -join ''; if($nodes -match "ready"){$ready=$true;break}; Start-Sleep -Seconds 1 }
if(-not $ready){throw "Module attached but WSL device nodes did not appear"}
& wsl.exe -d $distro -- bash -lc "systemctl restart vohive && sleep 5 && systemctl is-active vohive"
if($LASTEXITCODE -ne 0){throw "VoHive restart failed"}
Write-Host "Device detection reset completed."
