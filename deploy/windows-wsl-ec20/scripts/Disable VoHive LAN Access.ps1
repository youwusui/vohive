. (Join-Path $PSScriptRoot "VoHive-Wsl.Common.ps1")
$cfg=Read-VoHiveConfig
Assert-VoHiveAdmin
$port=[int]$cfg.listen_port; $rule="VoHive LAN " + $port
$rows=netsh interface portproxy show v4tov4
foreach($row in @($rows)){if($row -match ("^\s*(\d+\.\d+\.\d+\.\d+)\s+" + $port + "\s+")){if($matches[1] -ne "127.0.0.1"){netsh interface portproxy delete v4tov4 listenaddress=$($matches[1]) listenport=$port | Out-Null}}}
Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host ("LAN access disabled. Local http://localhost:" + $port + " remains available.")
