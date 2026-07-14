. (Join-Path $PSScriptRoot "VoHive-Wsl.Common.ps1")
$cfg=Read-VoHiveConfig
Assert-VoHiveAdmin
$distro=[string]$cfg.distro; $port=[int]$cfg.listen_port; $rule="VoHive LAN " + $port
$wslIp=(& wsl.exe -d $distro -- bash -lc "hostname -I | cut -d' ' -f1") -join ''; $wslIp=$wslIp.Trim(); if(-not $wslIp){throw "Cannot determine WSL IP"}
$ping=(& wsl.exe -d $distro -- bash -lc ("curl -sS --max-time 8 http://127.0.0.1:" + $port + "/ping")) -join ''; if($ping -notmatch "pong"){throw "VoHive is not responding inside WSL"}
$lan=Get-VoHiveLanIPv4
netsh interface portproxy delete v4tov4 listenaddress=$($lan.IPAddress) listenport=$port 2>$null | Out-Null
netsh interface portproxy add v4tov4 listenaddress=$($lan.IPAddress) listenport=$port connectaddress=$wslIp connectport=$port | Out-Null
Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $rule -Direction Inbound -Action Allow -Protocol TCP -LocalAddress $lan.IPAddress -LocalPort $port -RemoteAddress LocalSubnet -Profile Any | Out-Null
Write-Host ("LAN access enabled: http://" + $lan.IPAddress + ":" + $port)
