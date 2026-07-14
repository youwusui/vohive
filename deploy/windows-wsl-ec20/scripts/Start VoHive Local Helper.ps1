. (Join-Path $PSScriptRoot "VoHive-Wsl.Common.ps1")
$cfg = Read-VoHiveConfig
$VoHivePort = [int]$cfg.listen_port
$ErrorActionPreference = "Stop"

$Port = 17575
$Prefix = "http://127.0.0.1:$Port/"
$Log = Join-Path $PSScriptRoot "vohive-local-helper.log"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $Utf8NoBom
$OutputEncoding = $Utf8NoBom

function Write-HelperLog($Message) {
  $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
  Write-Host $line
  Add-Content -LiteralPath $Log -Value $line -Encoding UTF8
}

function ConvertTo-JsonBytes($Object) {
  return [System.Text.Encoding]::UTF8.GetBytes(($Object | ConvertTo-Json -Depth 8 -Compress))
}

function Get-RequestBody($Request) {
  if (-not $Request.HasEntityBody) { return "" }
  $reader = New-Object System.IO.StreamReader($Request.InputStream, $Request.ContentEncoding)
  try { return $reader.ReadToEnd() } finally { $reader.Close() }
}

function Is-AllowedOrigin($Origin) {
  if ([string]::IsNullOrWhiteSpace($Origin)) { return $true }
  try {
    $uri = [Uri]$Origin
    if ($uri.Scheme -ne "http" -and $uri.Scheme -ne "https") { return $false }
    if ($uri.Port -ne $VoHivePort -and $uri.Port -ne 5173) { return $false }
    $host = $uri.Host.ToLowerInvariant()
    if ($host -eq "localhost" -or $host -eq "127.0.0.1" -or $host -eq "::1") { return $true }
    if ($host -match "^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[0-1])\.)") { return $true }
  } catch {
    return $false
  }
  return $false
}

function Add-CorsHeaders($Context) {
  $origin = $Context.Request.Headers["Origin"]
  if (Is-AllowedOrigin $origin) {
    if (-not [string]::IsNullOrWhiteSpace($origin)) {
      $Context.Response.Headers["Access-Control-Allow-Origin"] = $origin
      $Context.Response.Headers["Vary"] = "Origin"
    }
    $Context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
    $Context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-VoHive-Local-Helper"
    $Context.Response.Headers["Access-Control-Max-Age"] = "600"
  }
}

function Send-Json($Context, [int]$StatusCode, $Object) {
  Add-CorsHeaders $Context
  $bytes = ConvertTo-JsonBytes $Object
  $Context.Response.StatusCode = $StatusCode
  $Context.Response.ContentType = "application/json; charset=utf-8"
  $Context.Response.ContentLength64 = $bytes.Length
  $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  $Context.Response.OutputStream.Close()
}

function Send-Empty($Context, [int]$StatusCode) {
  Add-CorsHeaders $Context
  $Context.Response.StatusCode = $StatusCode
  $Context.Response.ContentLength64 = 0
  $Context.Response.OutputStream.Close()
}

function Assert-LocalHelperHeader($Context) {
  $value = $Context.Request.Headers["X-VoHive-Local-Helper"]
  return $value -eq "1"
}

function Test-Admin {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-LanState {
  $ListenPort = $VoHivePort
  $rows = netsh interface portproxy show v4tov4 2>$null
  $addresses = @()
  foreach ($row in @($rows)) {
    if ($row -match "^\s*(\d+\.\d+\.\d+\.\d+)\s+($ListenPort)\s+") {
      $addr = $matches[1]
      if ($addr -ne "127.0.0.1") { $addresses += $addr }
    }
  }
  [pscustomobject]@{
    enabled = ($addresses.Count -gt 0)
    addresses = $addresses
  }
}

function Start-ToolScript($ScriptName) {
  $scriptPath = Join-Path $PSScriptRoot $ScriptName
  if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "Missing script: $scriptPath"
  }
  $escapedScriptPath = $scriptPath.Replace('"', '\"')
  $argLine = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $escapedScriptPath
  $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argLine -WindowStyle Hidden -PassThru
  Write-HelperLog "Started $ScriptName pid=$($proc.Id)"
  return $proc.Id
}

Set-Content -LiteralPath $Log -Value "VoHive local helper log" -Encoding UTF8

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($Prefix)

try {
  $listener.Start()
  Write-HelperLog "Listening on $Prefix"

  while ($listener.IsListening) {
    $context = $listener.GetContext()
    $request = $context.Request
    $path = $request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant()
    if ($path -eq "") { $path = "/" }

    try {
      if (-not (Is-AllowedOrigin $request.Headers["Origin"])) {
        Send-Json $context 403 @{ ok = $false; message = "origin not allowed" }
        continue
      }

      if ($request.HttpMethod -eq "OPTIONS") {
        Send-Empty $context 204
        continue
      }

      if ($request.HttpMethod -eq "GET" -and $path -eq "/status") {
        $lan = Get-LanState
        Send-Json $context 200 @{
          ok = $true
          helper = "vohive-local-helper"
          port = $Port
          pid = $PID
          is_admin = (Test-Admin)
          lan = $lan
        }
        continue
      }

      if ($request.HttpMethod -ne "POST") {
        Send-Json $context 405 @{ ok = $false; message = "method not allowed" }
        continue
      }

      if (-not (Assert-LocalHelperHeader $context)) {
        Send-Json $context 403 @{ ok = $false; message = "missing local helper header" }
        continue
      }

      [void](Get-RequestBody $request)
      switch ($path) {
        "/actions/reset-device" {
          $processId = Start-ToolScript "Reset VoHive Device Detect.ps1"
          Send-Json $context 202 @{ ok = $true; message = "device reset started"; pid = $processId }
          continue
        }
        "/actions/lan-enable" {
          $processId = Start-ToolScript "Enable VoHive LAN Access.ps1"
          Send-Json $context 202 @{ ok = $true; message = "LAN enable started"; pid = $processId }
          continue
        }
        "/actions/lan-disable" {
          $processId = Start-ToolScript "Disable VoHive LAN Access.ps1"
          Send-Json $context 202 @{ ok = $true; message = "LAN disable started"; pid = $processId }
          continue
        }
        default {
          Send-Json $context 404 @{ ok = $false; message = "not found" }
          continue
        }
      }
    } catch {
      Write-HelperLog "Request failed: $($_.Exception.Message)"
      try {
        Send-Json $context 500 @{ ok = $false; message = $_.Exception.Message }
      } catch {
      }
    }
  }
} finally {
  if ($listener.IsListening) { $listener.Stop() }
  $listener.Close()
}
