$ErrorActionPreference = "Stop"
$ToolRoot = Split-Path -Parent $PSScriptRoot
$ConfigPath = Join-Path $ToolRoot "config\\vohive-wsl.json"
$ExampleConfigPath = Join-Path $ToolRoot "config\\vohive-wsl.example.json"

function Read-VoHiveConfig {
  if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Missing config file: $ConfigPath. Copy vohive-wsl.example.json to vohive-wsl.json and edit it first."
  }
  $cfg = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if (-not $cfg.distro) { throw "config.distro is required" }
  return $cfg
}

function Expand-VoHiveValue([string]$Value) {
  if ($null -eq $Value) { return "" }
  return [Environment]::ExpandEnvironmentVariables($Value)
}

function Assert-VoHiveAdmin {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $args = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    Start-Process -FilePath "powershell.exe" -ArgumentList $args -Verb RunAs
    exit
  }
}

function Invoke-VoHiveWSL([string]$Distro, [string]$Command) {
  & wsl.exe -d $Distro -- bash -lc $Command
  if ($LASTEXITCODE -ne 0) { throw "WSL command failed with exit code $LASTEXITCODE" }
}

function Invoke-VoHiveCapture([scriptblock]$Command) {
  $old = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try { $output = & $Command 2>&1; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
  [pscustomobject]@{ ExitCode = $code; Lines = @($output | ForEach-Object { "$_" }); Text = (@($output) -join [Environment]::NewLine) }
}

function ConvertTo-VoHiveWslPath([string]$Distro, [string]$WindowsPath) {
  $result = & wsl.exe -d $Distro -- wslpath -a $WindowsPath 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $result) { throw "Cannot convert Windows path to WSL path: $WindowsPath" }
  return ($result | Select-Object -First 1).Trim()
}

function Get-VoHiveLanIPv4 {
  $configs = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address -and $_.NetAdapter.Status -eq "Up" } | Sort-Object { $_.NetAdapter.InterfaceMetric }
  foreach ($config in $configs) {
    foreach ($addr in @($config.IPv4Address)) {
      $ip = $addr.IPAddress
      if ($ip -and $ip -notmatch "^(127|169\.254)\." -and $ip -notmatch "^172\.27\.") { return [pscustomobject]@{ InterfaceAlias=$config.InterfaceAlias; IPAddress=$ip } }
    }
  }
  throw "No usable LAN IPv4 address found."
}

function Get-VoHiveConfigValue($Object, [string]$Name, [string]$Default = "") {
  $prop = $Object.PSObject.Properties[$Name]
  if ($prop -and $null -ne $prop.Value) { return (Expand-VoHiveValue ([string]$prop.Value)) }
  return $Default
}
