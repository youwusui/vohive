param([switch]$PlayLive, [int]$LiveTcpPort = 0)
. (Join-Path $PSScriptRoot "VoHive-Wsl.Common.ps1")
$ErrorActionPreference = "Stop"
$cfg = Read-VoHiveConfig
$distro = [string]$cfg.distro
if ($LiveTcpPort -le 0) { $LiveTcpPort = [int]$cfg.live_tcp_port }
$toolRoot = Split-Path -Parent $PSScriptRoot
$sidecarWin = Join-Path $PSScriptRoot "vohive_call_asr_sidecar.py"
if (-not (Test-Path -LiteralPath $sidecarWin)) { throw "Missing sidecar: $sidecarWin" }
& wsl.exe -d $distro -- true
if ($LASTEXITCODE -ne 0) { throw "Cannot start WSL distro $distro" }
$sidecarWsl = ConvertTo-VoHiveWslPath $distro $sidecarWin
$probeDir = Get-VoHiveConfigValue $cfg.vohive "rtp_dir" "/opt/vohive/rtp-probe"
$python = Get-VoHiveConfigValue $cfg.vohive "python" "/opt/vohive/asr-venv/bin/python"
$vohiveRoot = Get-VoHiveConfigValue $cfg.vohive "root" "/opt/vohive"
$apiUser = Get-VoHiveConfigValue $cfg.vohive "api_username" "admin"
$apiPassword = Get-VoHiveConfigValue $cfg.vohive "api_password" ""
$defaultDuration = [int]$cfg.listener.default_duration
$durationPadding = [int]$cfg.listener.duration_padding
$recordRoot = Expand-VoHiveValue ([string]$cfg.listener.record_root)
if (-not [IO.Path]::IsPathRooted($recordRoot)) { $recordRoot = Join-Path $toolRoot $recordRoot }
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$sessionDir = Join-Path $recordRoot ("call-" + $stamp)
New-Item -ItemType Directory -Force -Path $sessionDir | Out-Null
$mutex = New-Object System.Threading.Mutex($false, "Global\VoHiveCallListener")
try { $owns = $mutex.WaitOne(0, $false) } catch [System.Threading.AbandonedMutexException] { $owns = $true }
if (-not $owns) { Write-Host "VoHive call listener is already running."; exit 0 }
function Wsl([string]$Command) { & wsl.exe -d $distro -- bash -lc $Command }
function BashQuote([string]$Value) {
  $quote = [string][char]39
  $escapedQuote = $quote + [char]92 + $quote + $quote
  return $quote + $Value.Replace($quote, $escapedQuote) + $quote
}
Wsl "pkill -f '[v]ohive_call_asr_sidecar.py' 2>/dev/null || true"
Start-Sleep -Milliseconds 500
$known = @{}
foreach ($name in @(Wsl ("mkdir -p " + (BashQuote $probeDir) + "; find " + (BashQuote $probeDir) + " -maxdepth 1 -type f -printf '%f\n' 2>/dev/null"))) { if ($name) { $known[$name] = $true } }
$summary = Join-Path $sessionDir "session.txt"
Set-Content -LiteralPath $summary -Encoding UTF8 -Value @("started_at=" + $stamp, "play_live=" + $PlayLive, "live_tcp_port=" + $LiveTcpPort, "status=running")
$wslIp = ((Wsl "hostname -I | cut -d' ' -f1") -join '').Trim()
if (-not $wslIp) { throw "Cannot determine WSL IP" }
$liveUrl = "tcp://" + $wslIp + ":" + $LiveTcpPort
function Find-Ffplay {
  $bundled = Join-Path $toolRoot "ffmpeg\ffplay.exe"
  if (Test-Path -LiteralPath $bundled) { return $bundled }
  $cmd = Get-Command ffplay.exe -ErrorAction SilentlyContinue
  if ($cmd -and $cmd.Source) { return $cmd.Source }
  foreach ($candidate in @($cfg.listener.ffplay_candidates)) { $p = Expand-VoHiveValue ([string]$candidate); if ($p -and (Test-Path -LiteralPath $p)) { return $p } }
  $winget = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages") -Filter ffplay.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($winget) { return $winget.FullName }
  return ""
}
Write-Host "VoHive call listener started."
Write-Host "Record folder: $sessionDir"
Write-Host "QQ Bot dial: /vocall eSIM 888 35"
Write-Host "DTMF after connected: 2 or /dtmf 2"
$syncJob = Start-Job -ScriptBlock {
  param($Distro,$ProbeDir,$SessionDir,$KnownNames)
  $seen=@{}; foreach($n in $KnownNames){if($n){$seen[$n]=$true}}
  while($true){ try { $files=wsl.exe -d $Distro -- bash -lc ("find '" + $ProbeDir + "' -maxdepth 1 -type f -printf '%f\n' 2>/dev/null"); foreach($name in @($files)){if(-not $name -or $seen.ContainsKey($name)){continue}; $src="\\wsl.localhost\" + $Distro + ($ProbeDir.Replace('/','\')) + "\" + $name; if(Test-Path -LiteralPath $src){Copy-Item -LiteralPath $src -Destination (Join-Path $SessionDir $name) -Force; $seen[$name]=$true}} } catch {}; Start-Sleep -Seconds 4 }
} -ArgumentList $distro,$probeDir,$sessionDir,@($known.Keys)
$ffplayJob = $null
if ($PlayLive) { $ffplay = Find-Ffplay; if ($ffplay) { Write-Host "Windows live playback: $liveUrl"; $ffplayJob = Start-Job -ScriptBlock { param($Exe,$Url); while($true){ & $Exe -hide_banner -loglevel warning -nodisp -autoexit -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 -f amr ($Url + "?timeout=300000&tcp_nodelay=1"); Start-Sleep -Milliseconds 150 } } -ArgumentList $ffplay,$liveUrl } else { Write-Host "ffplay.exe not found; recording remains available." } }
try {
  $envParts = @("VOHIVE_RTP_OUT_DIR=" + (BashQuote $probeDir), "VOHIVE_CONFIG=" + (BashQuote ($vohiveRoot + "/config/config.yaml")), "VOHIVE_API=http://127.0.0.1:" + [int]$cfg.listen_port, "VOHIVE_API_USERNAME=" + (BashQuote $apiUser))
  if ($apiPassword) { $envParts += "VOHIVE_API_PASSWORD=" + (BashQuote $apiPassword) }
  $liveArg = if ($PlayLive) { " --live-tcp-port " + $LiveTcpPort } else { "" }
  $command = ($envParts -join " ") + " " + (BashQuote $python) + " " + (BashQuote $sidecarWsl) + " --duration " + $defaultDuration + " --duration-from-vocall --duration-padding " + $durationPadding + " --qq-dtmf" + $liveArg
  while ($true) { Wsl $command; $code=$LASTEXITCODE; if($code -eq 2){break}; Write-Host "Sidecar exited with code $code; restarting in 2 seconds."; Start-Sleep -Seconds 2 }
} finally {
  if($syncJob){Stop-Job $syncJob -ErrorAction SilentlyContinue; Remove-Job $syncJob -Force -ErrorAction SilentlyContinue}
  if($ffplayJob){Stop-Job $ffplayJob -ErrorAction SilentlyContinue; Remove-Job $ffplayJob -Force -ErrorAction SilentlyContinue}
  Add-Content -LiteralPath $summary -Encoding UTF8 -Value @("finished_at=" + (Get-Date -Format "yyyyMMdd-HHmmss"),"status=stopped")
  if($owns){try{$mutex.ReleaseMutex()}catch{}}
  $mutex.Dispose()
}
