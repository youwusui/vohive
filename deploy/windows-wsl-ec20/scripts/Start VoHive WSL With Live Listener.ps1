param([int]$LiveTcpPort = 0)
. (Join-Path $PSScriptRoot "VoHive-Wsl.Common.ps1")
$ErrorActionPreference = "Stop"
$cfg = Read-VoHiveConfig
Assert-VoHiveAdmin
if ($LiveTcpPort -le 0) { $LiveTcpPort = [int]$cfg.live_tcp_port }
$toolRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $toolRoot "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir "start-vohive-combined.log"
Set-Content -LiteralPath $log -Value "VoHive combined startup log" -Encoding UTF8
function Log([string]$Message) { $line="[" + (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + "] " + $Message; Write-Host $line; Add-Content -LiteralPath $log -Value $line -Encoding UTF8 }
$mutex = New-Object System.Threading.Mutex($false,"Global\VoHiveCombinedStartup")
try { $owns = $mutex.WaitOne(0,$false) } catch [System.Threading.AbandonedMutexException] { $owns = $true }
if(-not $owns){Write-Host "VoHive combined startup is already running."; exit 0}
try {
  $helper = Join-Path $PSScriptRoot "Start VoHive Local Helper.ps1"
  if(Test-Path -LiteralPath $helper){
    try { $status=Invoke-RestMethod -TimeoutSec 2 "http://127.0.0.1:17575/status" } catch { $status=$null }
    if(-not $status){
      $helperArgs = '-NoProfile -ExecutionPolicy Bypass -File "' + $helper + '"'
      Start-Process powershell.exe -ArgumentList $helperArgs -WindowStyle Hidden | Out-Null
    }
  }
  Log "Starting WSL, USB module, Mihomo and VoHive"
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Start VoHive WSL.ps1") -NoPause
  if($LASTEXITCODE -ne 0){throw "VoHive WSL startup failed with exit code $LASTEXITCODE"}
  $running = (& wsl.exe -d ([string]$cfg.distro) -- bash -lc "pgrep -f '[v]ohive_call_asr_sidecar.py' >/dev/null && echo running || true") -join ''
  if($running -match "running"){
    Log "Call listener is already running"
  }else{
    Log "Starting call listener with Windows live playback"
    $listener=Join-Path $PSScriptRoot "Start VoHive Call Listener.ps1"
    $listenerArgs = '-NoProfile -ExecutionPolicy Bypass -File "' + $listener + '" -PlayLive -LiveTcpPort ' + $LiveTcpPort
    $proc=Start-Process powershell.exe -ArgumentList $listenerArgs -PassThru
    Start-Sleep -Seconds 4
    if($proc.HasExited){throw "Call listener exited with code $($proc.ExitCode)"}
    Log "Call listener window PID $($proc.Id)"
  }
  $states=& wsl.exe -d ([string]$cfg.distro) -- bash -lc "systemctl is-active vohive mihomo"
  if(@($states | Where-Object {$_ -eq "active"}).Count -ne 2){throw "VoHive or Mihomo is not active"}
  Log "Ready: WSL, VoHive, Mihomo, VoWiFi and call listener are running"
} finally { if($owns){try{$mutex.ReleaseMutex()}catch{}}; $mutex.Dispose() }
