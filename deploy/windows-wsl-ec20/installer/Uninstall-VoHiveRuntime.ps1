param(
  [string]$DistroName = "VoHive",
  [string]$DistroDataRoot = "$env:ProgramData\VOHIVE for Windows\WSL"
)

$ErrorActionPreference = "Continue"
$taskName = "VOHIVE for Windows - Continue Setup"
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
$distros = @(& wsl.exe --list --quiet 2>$null | ForEach-Object { ([string]$_).Trim([char]0).Trim() })
if ($distros -contains $DistroName) {
  & wsl.exe --terminate $DistroName 2>$null
  & wsl.exe --unregister $DistroName
}
netsh interface portproxy delete v4tov4 listenaddress=127.0.0.1 listenport=7575 2>$null | Out-Null
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=7575 2>$null | Out-Null
$hostsPath = Join-Path $env:WINDIR "System32\drivers\etc\hosts"
if (Test-Path -LiteralPath $hostsPath) {
  $lines = Get-Content -LiteralPath $hostsPath | Where-Object { $_ -notmatch "vohive-wsl" -and $_ -notmatch "^# VOHIVE for Windows$" }
  Set-Content -LiteralPath $hostsPath -Value $lines -Encoding ASCII
}
