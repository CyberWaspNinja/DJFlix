# Installs DJFlix silently, checks the service, restart and firewall rule, then uninstalls.
param([string]$Setup)
$failed = $false
function Fail($m) { Write-Host "INSTALLER FAIL: $m"; $script:failed = $true }
function Healthy { try { (Invoke-RestMethod http://127.0.0.1:8674/api/v1/health -TimeoutSec 3).status -eq 'ok' } catch { $false } }
function WaitHealthy($secs) { foreach ($i in 1..($secs * 2)) { if (Healthy) { return $true }; Start-Sleep -Milliseconds 500 }; $false }

Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LOG=install.log' -Wait
$svc = Get-Service DJFlix -ErrorAction SilentlyContinue
if (-not $svc) { Fail 'service was not registered' } else { Write-Host "service: $($svc.Status), start type $($svc.StartType)" }
if (-not (WaitHealthy 30)) { Fail 'service is not answering after install' }
if (-not (Test-Path "$env:ProgramFiles\DJFlix\DJFlix.exe")) { Fail 'desktop app was not installed' }
$rule = netsh advfirewall firewall show rule name=DJFlix | Out-String
if ($rule -notmatch 'LocalSubnet' -or $rule -notmatch '8674') { Fail "firewall rule missing or not LAN-only: $rule" } else { Write-Host 'firewall rule: TCP 8674, local subnet only' }

# Restart through the API, as the dashboard does: the service must come back by itself.
$admin = (Get-Content "$env:ProgramData\DJFlix\admin.token").Trim()
Invoke-RestMethod -Method POST -Uri http://127.0.0.1:8674/api/v1/admin/restart -Headers @{ Authorization = "Bearer $admin" } -ContentType 'application/json' -Body '{}' | Out-Null
Start-Sleep 3
if (-not (WaitHealthy 40)) { Fail 'service did not come back after an API restart' } else { Write-Host 'service restarted itself after API restart' }

# Service keeps running with no desktop window open.
if (Get-Process DJFlix -ErrorAction SilentlyContinue) { Fail 'desktop app should not be running' }
if (-not (Healthy)) { Fail 'hub must run without the desktop UI' } else { Write-Host 'hub answers with the desktop UI closed' }

$un = Get-ChildItem "$env:ProgramFiles\DJFlix\unins*.exe" | Select-Object -First 1
Start-Process $un.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
Start-Sleep 3
if (Get-Service DJFlix -ErrorAction SilentlyContinue) { Fail 'service still registered after uninstall' } else { Write-Host 'uninstall removed the service' }
if ((netsh advfirewall firewall show rule name=DJFlix | Out-String) -match 'LocalSubnet') { Fail 'firewall rule left behind' }
if ($failed) { Get-Content install.log -Tail 40 -ErrorAction SilentlyContinue; exit 1 } else { Write-Host 'INSTALLER OK'; exit 0 }
