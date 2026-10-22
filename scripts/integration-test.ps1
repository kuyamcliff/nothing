# End-to-end test of ClipFx Manager on real Windows. Must run as administrator.
# Installs the service, generates real traffic and checks counting, blocking,
# limits, restarts and uninstall against Windows Defender Firewall.
param([Parameter(Mandatory)] [string] $Exe)

$ErrorActionPreference = 'Stop'
$Api = 'http://127.0.0.1:47613/api'
$H = @{ 'X-ClipFx' = '1' }
$Url = 'https://speed.cloudflare.com/__down?bytes='
$probeDir = 'C:\clipfx-probe'
$Probe = Join-Path $probeDir 'probe.exe'
$Probe2 = Join-Path $probeDir 'probe2.exe'
$failures = 0

function Step($m) { Write-Host "`n=== $m" -ForegroundColor Cyan }
function Pass($m) { Write-Host "PASS $m" -ForegroundColor Green }
function Fail($m) { Write-Host "FAIL $m" -ForegroundColor Red; $script:failures++ }
function Check($cond, $m) { if ($cond) { Pass $m } else { Fail $m } }

function Snap { Invoke-RestMethod "$Api/snapshot" -TimeoutSec 5 }
function Post($path, $body) {
  $json = if ($null -eq $body) { '{}' } else { $body | ConvertTo-Json -Compress }
  Invoke-RestMethod "$Api$path" -Method Post -Headers $H -ContentType 'application/json' -Body $json -TimeoutSec 10 | Out-Null
}
function ProbeApp($path = $Probe) { (Snap).apps | Where-Object { $_.path -and ($_.path -ieq $path) } | Select-Object -First 1 }
function WaitUntil([scriptblock] $cond, [int] $seconds, [string] $what) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    try { $r = & $cond; if ($r) { return $r } } catch { }
    Start-Sleep -Milliseconds 300
  }
  throw "Timed out waiting for: $what"
}
function Download([string] $exe, [long] $bytes, [string] $rate = '', [int] $maxTime = 60) {
  $curlArgs = @('-s', '-o', 'NUL', '--max-time', "$maxTime", '-4')
  if ($rate) { $curlArgs += @('--limit-rate', $rate) }
  $curlArgs += "$Url$bytes"
  & $exe @curlArgs
  return $LASTEXITCODE
}

New-Item -ItemType Directory -Force $probeDir | Out-Null
Copy-Item "$env:SystemRoot\System32\curl.exe" $Probe -Force
Copy-Item "$env:SystemRoot\System32\curl.exe" $Probe2 -Force
Set-NetFirewallProfile -All -Enabled True

try {
  Step 'Install service'
  & $Exe --install | Out-Host
  Check ($LASTEXITCODE -eq 0) "installer exit code ($LASTEXITCODE)"
  $svc = Get-Service ClipFxManager
  Check ($svc.Status -eq 'Running') "service running ($($svc.Status))"
  Check ($svc.StartType -eq 'Automatic') "service starts automatically ($($svc.StartType))"
  $s = WaitUntil { $x = Snap; if ($x.status.monitoring) { $x } } 30 'monitoring'
  Check $s.status.monitoring 'monitoring active'
  Check $s.status.firewallEnabled 'firewall reported on'
  Check (-not $s.status.simulated) 'real platform'

  Step 'Counts real traffic per app'
  $code = Download $Probe 5000000
  Check ($code -eq 0) "download works before blocking (curl $code)"
  $app = WaitUntil { $a = ProbeApp; if ($a -and $a.received -ge 4500000) { $a } } 20 'probe usage'
  Pass "probe counted: $($app.received) bytes received, $($app.sent) sent"
  Check ($app.received -lt 6500000) "count is close to the real size ($($app.received))"
  Check ($app.kind -eq 'Program') "kind is Program ($($app.kind))"
  Check (-not $app.isSystem) 'probe is not a system item'
  $icon = Invoke-WebRequest "$Api/apps/$($app.id)/icon" -UseBasicParsing
  Check ($icon.StatusCode -eq 200 -and $icon.Headers['Content-Type'] -eq 'image/png') 'icon served'
  $sys = (Snap).apps | Where-Object { $_.isSystem }
  Write-Host "system items seen: $(($sys | ForEach-Object name) -join ', ')"

  Step 'Switch off blocks the app'
  Post "/apps/$($app.id)/blocked" @{ blocked = $true }
  WaitUntil { $a = ProbeApp; if ($a.blocked -and $a.applied) { $a } } 15 'block applied' | Out-Null
  $rules = Get-NetFirewallRule -Group 'ClipFx Manager' -ErrorAction SilentlyContinue
  Check ($rules.Count -ge 2) "firewall rules created ($($rules.Count))"
  Check ((($rules | Where-Object Action -eq 'Block').Count) -eq $rules.Count) 'all rules are block rules'
  $before = (ProbeApp).total
  $code = Download $Probe 2000000 '' 15
  Check ($code -ne 0) "blocked app can't connect (curl $code)"
  Start-Sleep 2
  $after = (ProbeApp).total
  Check (($after - $before) -lt 20000) "no data used while blocked (+$($after - $before) bytes)"
  $code = Download $Probe2 1000 '' 20
  Check ($code -eq 0) "other apps still work (curl $code)"

  Step 'Blocks survive the service being stopped'
  Stop-Service ClipFxManager
  $code = Download $Probe 1000 '' 15
  Check ($code -ne 0) "still blocked with service stopped (curl $code)"
  Start-Service ClipFxManager
  WaitUntil { (Snap).status.monitoring } 30 'service back' | Out-Null

  Step 'Switch on restores access'
  Post "/apps/$($app.id)/blocked" @{ blocked = $false }
  WaitUntil { $a = ProbeApp; if (-not $a.blocked -and $a.applied) { $a } } 15 'unblock applied' | Out-Null
  $code = Download $Probe 100000
  Check ($code -eq 0) "download works again (curl $code)"

  Step 'App limit cuts off an active download'
  $start = (ProbeApp).total
  $limit = $start + 3MB
  Post "/apps/$($app.id)/limit" @{ bytes = $limit }
  $code = Download $Probe 100000000 '2M' 90
  Check ($code -ne 0) "download cut off at the limit (curl $code)"
  $a = WaitUntil { $a = ProbeApp; if ($a.limitReached -and $a.applied) { $a } } 15 'limit block'
  $over = $a.total - $limit
  Write-Host "used $($a.total - $start) bytes against a 3 MB allowance (overshoot $over)"
  Check ($over -lt 3MB) "stopped near the limit (overshoot $over bytes)"
  $code = Download $Probe 1000 '' 15
  Check ($code -ne 0) "stays blocked after the limit (curl $code)"

  Step 'State and blocks survive a service restart'
  Restart-Service ClipFxManager
  $a2 = WaitUntil { $x = ProbeApp; if ($x -and (Snap).status.monitoring) { $x } } 30 'service restart'
  Check ($a2.total -ge $a.total) "usage kept ($($a2.total))"
  Check ($a2.limit -eq $limit) 'limit kept'
  Check ($a2.limitReached -and $a2.effectiveBlocked) 'still at limit'
  $code = Download $Probe 1000 '' 15
  Check ($code -ne 0) "still blocked after restart (curl $code)"
  Post "/apps/$($app.id)/limit" @{ bytes = $null }
  WaitUntil { $x = ProbeApp; if (-not $x.effectiveBlocked -and $x.applied) { $x } } 15 'limit removed' | Out-Null
  $code = Download $Probe 1000
  Check ($code -eq 0) "works after removing the limit (curl $code)"

  Step 'Overall limit blocks every app'
  $total = (Snap).overall.total
  Post '/overall/limit' @{ bytes = $total + 2MB }
  $code = Download $Probe 100000000 '2M' 60
  Check ($code -ne 0) "download cut off at the overall limit (curl $code)"
  $s = WaitUntil { $x = Snap; if ($x.overall.limitReached -and $x.overall.applied) { $x } } 15 'global block'
  $code = Download $Probe2 1000 '' 10
  Check ($code -ne 0) "other apps blocked too (curl $code)"
  Post '/usage/reset' $null
  WaitUntil { $x = Snap; if (-not $x.overall.limitReached -and $x.overall.applied) { $x } } 15 'global unblock' | Out-Null
  Post '/overall/limit' @{ bytes = $null }
  $code = Download $Probe2 1000
  Check ($code -eq 0) "internet back after reset (curl $code)"

  Step 'Desktop window loads the UI'
  $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9333'
  $gui = Start-Process $Exe -PassThru
  try {
    $page = WaitUntil { (Invoke-RestMethod 'http://127.0.0.1:9333/json' -TimeoutSec 3) | Where-Object { $_.type -eq 'page' -and $_.url -like 'http://127.0.0.1:47613/*' } } 60 'webview page'
    Check ($page.title -eq 'ClipFx Manager') "window shows the UI ($($page.title))"
    Check (-not $gui.HasExited) 'window process running'
  } finally {
    Stop-Process -Id $gui.Id -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
  }

  Step 'Uninstall cleans up'
  Post "/apps/$($app.id)/blocked" @{ blocked = $true }
  WaitUntil { $a = ProbeApp; if ($a.blocked -and $a.applied) { $a } } 15 'block before uninstall' | Out-Null
  & $Exe --uninstall | Out-Host
  Check ($LASTEXITCODE -eq 0) 'uninstall exit code'
  Check (-not (Get-Service ClipFxManager -ErrorAction SilentlyContinue)) 'service removed'
  $left = Get-NetFirewallRule -Group 'ClipFx Manager' -ErrorAction SilentlyContinue
  Check (-not $left) 'firewall rules removed'
  $code = Download $Probe 1000
  Check ($code -eq 0) "app works after uninstall (curl $code)"
}
catch {
  Fail "unexpected error: $_"
  Get-WinEvent -LogName Application -MaxEvents 30 -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match 'ClipFx|\.NET Runtime|Application Error' } |
    ForEach-Object { Write-Host "$($_.TimeCreated) $($_.ProviderName): $($_.Message)" }
}
finally {
  & $Exe --uninstall 2>$null | Out-Null
}

if ($failures -gt 0) { Write-Host "`n$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "`nAll Windows checks passed" -ForegroundColor Green
