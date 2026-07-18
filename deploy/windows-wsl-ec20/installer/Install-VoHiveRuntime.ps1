param(
  [Parameter(Mandatory = $true)][string]$InstallRoot,
  [string]$DistroName = "VoHive",
  [string]$DistroDataRoot = "$env:ProgramData\VOHIVE for Windows\WSL",
  [switch]$ContinueAfterRestart
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$stateRoot = Join-Path $env:ProgramData "VOHIVE for Windows"
$logDir = Join-Path $stateRoot "Logs"
$logPath = Join-Path $logDir "install.log"
$taskName = "VOHIVE for Windows - Continue Setup"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Write-InstallLog([string]$Message) {
  $line = "[" + (Get-Date -Format "yyyy-MM-dd HH:mm:ss") + "] " + $Message
  Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Get-DistroNames {
  $output = & wsl.exe --list --quiet 2>$null
  if ($LASTEXITCODE -ne 0) { return @() }
  return @($output | ForEach-Object { ([string]$_).Trim([char]0).Trim() } | Where-Object { $_ })
}

function Register-Continuation {
  $script = Join-Path $InstallRoot "Installer\Install-VoHiveRuntime.ps1"
  $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $script + '" -InstallRoot "' + $InstallRoot + '" -DistroName "' + $DistroName + '" -DistroDataRoot "' + $DistroDataRoot + '" -ContinueAfterRestart'
  $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments
  $trigger = New-ScheduledTaskTrigger -AtLogOn
  $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
  Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $InstallRoot "install-pending-restart.marker") -Value "restart required" -Encoding ASCII
  Write-InstallLog "Windows restart is required; registered continuation task."
}

function Enable-RequiredFeature([string]$Name) {
  $feature = Get-WindowsOptionalFeature -Online -FeatureName $Name
  if ($feature.State -eq "Enabled") { return $false }
  Write-InstallLog "Enabling Windows feature $Name"
  $result = Enable-WindowsOptionalFeature -Online -FeatureName $Name -All -NoRestart
  return [bool]$result.RestartNeeded -or $result.State -ne "Enabled"
}

function Ensure-HostsAlias {
  $hostsPath = Join-Path $env:WINDIR "System32\drivers\etc\hosts"
  $content = Get-Content -LiteralPath $hostsPath -Raw -ErrorAction SilentlyContinue
  if ($content -notmatch "(?im)^\s*127\.0\.0\.1\s+vohive-wsl(?:\s|$)") {
    $entry = [Environment]::NewLine + "# VOHIVE for Windows" + [Environment]::NewLine + "127.0.0.1 vohive-wsl"
    Add-Content -LiteralPath $hostsPath -Value $entry -Encoding ASCII
    Write-InstallLog "Added vohive-wsl hosts alias."
  }
}

try {
  Write-InstallLog "Starting VOHIVE for Windows runtime installation."
  $restartNeeded = $false
  $restartNeeded = (Enable-RequiredFeature "Microsoft-Windows-Subsystem-Linux") -or $restartNeeded
  $restartNeeded = (Enable-RequiredFeature "VirtualMachinePlatform") -or $restartNeeded

  if ($restartNeeded -and -not $ContinueAfterRestart) {
    Register-Continuation
    exit 0
  }

  $rootfs = Join-Path $InstallRoot "Payload\vohive-rootfs.tar.gz"
  if (-not (Test-Path -LiteralPath $rootfs)) { throw "Missing WSL rootfs: $rootfs" }
  $configDir = Join-Path $InstallRoot "Tools\config"
  $configPath = Join-Path $configDir "vohive-wsl.json"
  if (-not (Test-Path -LiteralPath $configPath)) {
    Copy-Item -LiteralPath (Join-Path $configDir "vohive-wsl.example.json") -Destination $configPath
  }

  $distros = Get-DistroNames
  if ($distros -notcontains $DistroName) {
    New-Item -ItemType Directory -Force -Path $DistroDataRoot | Out-Null
    Write-InstallLog "Importing WSL distribution $DistroName into $DistroDataRoot"
    & wsl.exe --import $DistroName $DistroDataRoot $rootfs --version 2
    if ($LASTEXITCODE -ne 0) { throw "wsl --import failed with exit code $LASTEXITCODE" }
  } else {
    Write-InstallLog "WSL distribution $DistroName already exists; leaving it intact."
  }

  & wsl.exe -d $DistroName --user root -- bash -lc "systemctl enable mihomo.service vohive.service >/dev/null 2>&1 || true"
  if ($LASTEXITCODE -ne 0) { throw "Could not initialize $DistroName" }
  & wsl.exe --terminate $DistroName 2>$null
  Ensure-HostsAlias
  Set-Service -Name iphlpsvc -StartupType Automatic
  Start-Service -Name iphlpsvc -ErrorAction SilentlyContinue

  $marker = Join-Path $InstallRoot "install-pending-restart.marker"
  if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
  Set-Content -LiteralPath (Join-Path $stateRoot "installed.marker") -Value (Get-Date -Format "o") -Encoding ASCII
  Write-InstallLog "VOHIVE for Windows runtime installation completed."
  exit 0
} catch {
  Write-InstallLog ("ERROR: " + $_.Exception.Message)
  throw
}
