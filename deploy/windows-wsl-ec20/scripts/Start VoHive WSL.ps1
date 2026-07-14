param([switch]$NoPause)
. (Join-Path $PSScriptRoot "VoHive-Wsl.Common.ps1")
$ErrorActionPreference = "Stop"
$cfg = Read-VoHiveConfig
Assert-VoHiveAdmin
$toolRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $toolRoot "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir "start-vohive-wsl.log"
Set-Content -LiteralPath $log -Value "VoHive Windows/WSL startup log" -Encoding UTF8
function Log([string]$Message) { $line = "[" + (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + "] " + $Message; Write-Host $line; Add-Content -LiteralPath $log -Value $line -Encoding UTF8 }
function AddLog($Lines) { @($Lines) | ForEach-Object { Write-Host $_; Add-Content -LiteralPath $log -Value "$_" -Encoding UTF8 } }
$distro = [string]$cfg.distro
$port = [int]$cfg.listen_port
$usbipd = Expand-VoHiveValue ([string]$cfg.usbipd_path)
if (-not (Test-Path -LiteralPath $usbipd)) { throw "usbipd not found: $usbipd" }
Log "Starting WSL distro $distro"
& wsl.exe -d $distro -- true
if ($LASTEXITCODE -ne 0) { throw "Cannot start WSL distro $distro" }
$systemd = (& wsl.exe -d $distro -- bash -lc "systemctl is-system-running 2>/dev/null || true") -join [Environment]::NewLine
if ($systemd -notmatch "running|degraded|starting") { throw "WSL systemd unavailable: $systemd" }
$list = Invoke-VoHiveCapture { & $usbipd list }
Log "usbipd list:"; AddLog $list.Lines
$rx = [regex]::new([string]$cfg.module_match_regex, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$deviceLine = $list.Lines | Where-Object { $rx.IsMatch([string]$_) } | Select-Object -First 1
if (-not $deviceLine) { throw "DJI/Quectel module not found. Replug it or release it from another VM." }
$busid = (($deviceLine -split "\s+") | Where-Object { $_ -match "^\d+-\d+$" } | Select-Object -First 1)
if (-not $busid) { throw "Could not parse BUSID from: $deviceLine" }
Log "Detected BUSID $busid"
if ($deviceLine -match "Not shared") { $r = Invoke-VoHiveCapture { & $usbipd bind --busid $busid --force }; AddLog $r.Lines; if ($r.ExitCode -ne 0) { throw "usbipd bind failed" }; Start-Sleep -Seconds 2 }
if ($deviceLine -notmatch "Attached") { $r = Invoke-VoHiveCapture { & $usbipd attach --wsl --busid $busid }; AddLog $r.Lines; if ($r.ExitCode -ne 0) { $after = Invoke-VoHiveCapture { & $usbipd list }; if ($after.Text -notmatch [regex]::Escape($busid) -or $after.Text -notmatch "Attached") { throw "usbipd attach failed" } } } else { Log "Module is already attached" }
$ready = $false
for ($i = 0; $i -lt 40; $i++) { $nodes = (& wsl.exe -d $distro -- bash -lc "test -e /dev/cdc-wdm0 && ls /dev/ttyUSB* >/dev/null 2>&1 && echo ready || true") -join [Environment]::NewLine; if ($nodes -match "ready") { $ready = $true; break }; Start-Sleep -Seconds 1 }
if (-not $ready) { throw "WSL cannot see /dev/cdc-wdm0 and /dev/ttyUSB*" }
$epdg = Get-VoHiveConfigValue $cfg "epdg_ip" ""
if ($epdg) { $hostCmd = "sed -i '/# VOHIVE_REAL_EPDG_START/,/# VOHIVE_REAL_EPDG_END/d' /etc/hosts; printf '\n# VOHIVE_REAL_EPDG_START\n$epdg epdg.epc.mnc033.mcc234.pub.3gppnetwork.org\n# VOHIVE_REAL_EPDG_END\n' >> /etc/hosts"; Invoke-VoHiveWSL $distro $hostCmd | Out-Null }
Log "Ensuring Mihomo and VoHive are active"
Invoke-VoHiveWSL $distro "systemctl is-active --quiet mihomo || systemctl start mihomo"
Invoke-VoHiveWSL $distro "systemctl is-active --quiet vohive || systemctl start vohive"
$proxy = $cfg.proxy
$proxyAddr = Get-VoHiveConfigValue $proxy "socks5" ""
$expectedCountry = Get-VoHiveConfigValue $proxy "expected_country_code" ""
if ($proxyAddr -and $expectedCountry) { $check = (& wsl.exe -d $distro -- bash -lc "curl -sS --max-time 25 --socks5-hostname '$proxyAddr' 'http://ip-api.com/json/?fields=query,country,countryCode'") -join ''; Log "Proxy check: $check"; if ($check -notmatch ('countryCode[^A-Za-z]+"' + [regex]::Escape($expectedCountry) + '"')) { throw "Proxy country check failed" } }
$wslIp = (& wsl.exe -d $distro -- bash -lc "hostname -I | cut -d' ' -f1") -join ''
$wslIp = $wslIp.Trim()
if (-not $wslIp) { throw "Could not determine WSL IP" }
netsh interface portproxy delete v4tov4 listenaddress=127.0.0.1 listenport=$port 2>$null | Out-Null
netsh interface portproxy add v4tov4 listenaddress=127.0.0.1 listenport=$port connectaddress=$wslIp connectport=$port | Out-Null
$apiPassword = Get-VoHiveConfigValue $cfg.vohive "api_password" ""
if ($apiPassword) {
  $loginBody = @{ username = (Get-VoHiveConfigValue $cfg.vohive "api_username" "admin"); password = $apiPassword } | ConvertTo-Json
  $token = $null
  for ($i = 0; $i -lt 30 -and -not $token; $i++) { try { $login = Invoke-RestMethod -Method Post -Uri ("http://127.0.0.1:" + $port + "/api/auth/login") -ContentType "application/json" -Body $loginBody -TimeoutSec 5; $token = $login.token } catch { Start-Sleep -Seconds 2 } }
  if (-not $token) { throw "VoHive API login timed out" }
  $headers = @{ Authorization = "Bearer " + $token }; $dev = $null; $rt = $null
  for ($i = 0; $i -lt 36; $i++) { $overview = Invoke-RestMethod -Uri ("http://127.0.0.1:" + $port + "/api/devices/eSIM/overview") -Headers $headers -TimeoutSec 15; $dev = @($overview.devices)[0]; $rt = $dev.vowifi_runtime; Log ("active=" + $dev.vowifi_active + " ims=" + $rt.ims_ready + " sms=" + $rt.sms_ready + " phase=" + $rt.phase); if ($dev.vowifi_active -and $rt.ims_ready -and $rt.sms_ready) { break }; Start-Sleep -Seconds 5 }
  if (-not ($dev.vowifi_active -and $rt.ims_ready -and $rt.sms_ready)) { throw "VoWiFi did not reach IMS/SMS ready" }
} else { Log "api_password is empty; skipped VoWiFi readiness API check" }
$ping = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 ("http://127.0.0.1:" + $port + "/ping")
Log ("VoHive ping: " + $ping.StatusCode + " " + $ping.Content)
Log "Ready: WSL, VoHive and Mihomo are running"
if (-not $NoPause) { Read-Host "Press Enter to close" }
