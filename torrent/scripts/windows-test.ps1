# Real Windows test of the installed ClipFx Torrent app. Run as administrator.
param([Parameter(Mandatory)] [string] $Setup, [Parameter(Mandatory)] [string] $Seed)
$ErrorActionPreference = 'Stop'
$failures = 0
$summary = New-Object System.Collections.Generic.List[string]
function Step($m) { Write-Host "`n=== $m" -ForegroundColor Cyan }
function Check($c, $m) { if ($c) { Write-Host "PASS $m" -ForegroundColor Green; $summary.Add("PASS $m") } else { Write-Host "FAIL $m" -ForegroundColor Red; $summary.Add("FAIL $m"); $script:failures++ } }
function Note($m) { Write-Host "NOTE $m" -ForegroundColor Yellow; $summary.Add("NOTE $m") }
function WaitUntil([scriptblock] $c, [int] $s, [string] $what) {
  $d = (Get-Date).AddSeconds($s)
  while ((Get-Date) -lt $d) { try { $r = & $c; if ($r) { return $r } } catch { }; Start-Sleep -Milliseconds 400 }
  throw "Timed out: $what"
}
$Ui = 'http://127.0.0.1:47670/api'
$H = @{ 'X-ClipFx' = '1' }
function State { Invoke-RestMethod "$Ui/state" -TimeoutSec 5 }
function Post($p, $b) { if ($null -eq $b) { $b = @{} }; Invoke-RestMethod "$Ui$p" -Method Post -Headers $H -ContentType 'application/json' -Body ($b | ConvertTo-Json -Compress -Depth 5) -TimeoutSec 60 }
function T($id) { (State).torrents | Where-Object { $_.id -eq $id } }
function MB($b) { [math]::Round($b / 1MB, 1) }

$work = 'C:\cft-test'; Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $work | Out-Null
$dl = "$work\Downloads"
$exe = "$env:ProgramFiles\ClipFx Torrent\ClipFxTorrent.exe"
$shots = "$work\shots"; New-Item -ItemType Directory -Force $shots | Out-Null

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function Shot($name) {
  try {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save("$shots\$name.png"); $g.Dispose(); $bmp.Dispose()
  } catch { Write-Host "screenshot failed: $_" }
}
function StartApp { $p = Start-Process $exe -PassThru; WaitUntil { State } 60 'app api' | Out-Null; return $p }

try {
  Step 'Install'
  Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=assoc' -Wait
  Check (Test-Path $exe) 'installed'
  $rules = Get-NetFirewallRule -DisplayName 'ClipFx Torrent' -ErrorAction SilentlyContinue
  Check ($rules.Count -ge 2) "firewall allows incoming peers ($($rules.Count) rules)"
  $cmd = (Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\magnet\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
  Check ($cmd -like '*ClipFxTorrent.exe*') "magnet links open ClipFx Torrent ($cmd)"
  $ext = (Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\.torrent' -ErrorAction SilentlyContinue).'(default)'
  Check ($ext -eq 'ClipFxTorrent.torrent') ".torrent files open ClipFx Torrent ($ext)"

  Step 'App starts and shows its UI'
  $app = StartApp
  $s = WaitUntil { $x = State; if ($x.uiClients -ge 1) { $x } } 60 'window loaded the UI'
  Check ($s.uiClients -ge 1) 'WebView2 window loaded the UI'
  Check ((Get-Process -Id $app.Id).MainWindowTitle -like '*ClipFx Torrent*') 'window title'
  Check ($s.global.port -gt 0) "listening on port $($s.global.port)"
  Post '/settings' @{ showAddDialog = $false; saveDir = $dl } | Out-Null

  Step 'Local swarm: open a .torrent file from Explorer'
  $seedLog = "$work\seed.log"
  $seed = Start-Process dotnet -ArgumentList "`"$Seed`" $work\swarm 1500 4" -PassThru -NoNewWindow -RedirectStandardOutput $seedLog
  WaitUntil { (Get-Content $seedLog -Raw -ErrorAction SilentlyContinue) -match 'READY' } 300 'seeders' | Out-Null
  $seedOut = Get-Content $seedLog
  $tpath = ($seedOut | Where-Object { $_ -like 'TORRENT *' }).Substring(8)
  $magnet = ($seedOut | Where-Object { $_ -like 'MAGNET *' }).Substring(7)
  $hash = ($seedOut | Where-Object { $_ -like 'HASH *' }).Substring(5)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Start-Process $tpath   # same as double-clicking it
  $t = $null
  try { $t = WaitUntil { T $hash } 30 'torrent added via file association' } catch { }
  Check ($null -ne $t) 'double-clicking a .torrent adds it'
  if (-not $t) { Post '/add' @{ source = $tpath } | Out-Null }
  $t = WaitUntil { $x = T $hash; if ($x.status -in 'seeding', 'finished') { $x } } 300 'local download'
  $secs = $sw.Elapsed.TotalSeconds
  $avg = $t.size / $secs / 1MB
  Shot 'local-done'
  Note ("Local swarm: {0} MB in {1:N1}s = {2:N0} MB/s average (incl. startup)" -f (MB $t.size), $secs, $avg)
  Check ($avg -gt 40) ('local download speed {0:N0} MB/s' -f $avg)
  $orig = Get-FileHash "$work\swarm\seed\Test Pack\movie.mkv"
  Check ((Get-FileHash "$dl\Test Pack\movie.mkv").Hash -eq $orig.Hash) 'downloaded file is identical'
  Check ((Get-FileHash "$dl\Test Pack\extras\cover.jpg").Hash -eq (Get-FileHash "$work\swarm\seed\Test Pack\extras\cover.jpg").Hash) 'folder structure kept'

  Step 'Clicking a magnet link while the app runs'
  Post "/torrents/$hash/remove" @{ deleteFiles = $true } | Out-Null
  Check (-not (Test-Path "$dl\Test Pack\movie.mkv")) 'remove + delete files'
  Start-Process $exe -ArgumentList "`"$magnet`""
  $t = WaitUntil { T $hash } 30 'magnet handed to running app'
  Check ($null -ne $t) 'magnet link reached the running app'
  $t = WaitUntil { $x = T $hash; if ($x.status -in 'seeding', 'finished') { $x } } 300 'magnet download'
  Check ((Get-FileHash "$dl\Test Pack\movie.mkv").Hash -eq $orig.Hash) 'magnet download identical'
  Post "/torrents/$hash/remove" @{ deleteFiles = $true } | Out-Null
  Stop-Process -Id $seed.Id -Force -ErrorAction SilentlyContinue

  Step 'Internet: real torrent (Ubuntu desktop ISO)'
  $page = (Invoke-WebRequest 'https://releases.ubuntu.com/noble/' -UseBasicParsing).Content
  $name = ([regex]::Matches($page, 'ubuntu-[\d.]+-desktop-amd64\.iso\.torrent') | Select-Object -First 1).Value
  $url = "https://releases.ubuntu.com/noble/$name"
  Write-Host "Using $url"
  $r = Post '/add' @{ source = $url }
  $id = $r.id
  $samples = New-Object System.Collections.Generic.List[double]
  $end = (Get-Date).AddSeconds(150); $started = $null
  while ((Get-Date) -lt $end) {
    $x = T $id
    if ($x.status -eq 'downloading' -and -not $started) { $started = Get-Date }
    if ($started) { $samples.Add($x.down) }
    if ($x.progress -ge 1) { break }
    Start-Sleep 1
  }
  Shot 'internet'
  $x = T $id
  $steady = $samples | Select-Object -Skip 20
  $peak = ($samples | Measure-Object -Maximum).Maximum
  $mean = if ($steady) { ($steady | Measure-Object -Average).Average } else { 0 }
  Note ("Internet: peak {0} MB/s, steady average {1} MB/s over {2}s, peers {3}, done {4}" -f (MB $peak), (MB $mean), $steady.Count, $x.peers, [math]::Round($x.progress * 100, 1))
  Check ($peak -gt 5MB) ('real swarm download peaked at {0} MB/s' -f (MB $peak))

  Step 'Pause, resume'
  Post "/torrents/$id/pause" | Out-Null
  Start-Sleep 3
  $p1 = T $id; Start-Sleep 3; $p2 = T $id
  Check ($p2.status -eq 'paused' -and $p2.down -eq 0 -and ($p2.done - $p1.done) -lt 2MB) 'paused: nothing downloads'
  Post "/torrents/$id/resume" | Out-Null
  $x = WaitUntil { $y = T $id; if ($y.down -gt 1MB -or $y.progress -ge 1) { $y } } 90 'resume'
  Check ($x.down -gt 1MB -or $x.progress -ge 1) ('resumed at {0} MB/s' -f (MB $x.down))

  Step 'Crash and restart keeps progress'
  Start-Sleep 5
  $before = T $id
  Stop-Process -Id $app.Id -Force
  Start-Sleep 2
  $app = StartApp
  $after = WaitUntil { $y = T $id; if ($y -and $y.hasMetadata -and $y.status -ne 'checking' -and $y.progress -gt 0) { $y } } 600 'restart'
  Note ("before crash {0:P1}, after restart {1:P1}" -f $before.progress, $after.progress)
  Check ($after.progress -ge $before.progress - 0.05) 'progress kept after a crash'
  $x = WaitUntil { $y = T $id; if ($y.down -gt 1MB -or $y.progress -ge 1) { $y } } 120 'download continues'
  Check ($true) ('download continued at {0} MB/s' -f (MB $x.down))
  Shot 'after-restart'
  $detail = Invoke-RestMethod "$Ui/torrents/$id"
  Note ("peers connected {0}, trackers working {1}" -f $detail.peers.Count, (@($detail.trackers | Where-Object { $_.status -eq 'Working' })).Count)
  Post "/torrents/$id/remove" @{ deleteFiles = $true } | Out-Null

  Step 'Bare magnet link (no trackers): DHT + public trackers find peers'
  $m = Post '/add' @{ source = "magnet:?xt=urn:btih:$($detail.hash)" }
  $x = $null
  try { $x = WaitUntil { $y = T $m.id; if ($y.hasMetadata) { $y } } 120 'metadata' } catch { }
  Check ($null -ne $x) 'file list fetched from a bare magnet link'
  if ($x) {
    $x = WaitUntil { $y = T $m.id; if ($y.down -gt 512KB) { $y } } 90 'magnet speed'
    Note ('bare magnet downloading at {0} MB/s' -f (MB $x.down))
  }
  $st = State
  Note ("DHT nodes {0}, port {1}" -f $st.global.dhtNodes, $st.global.portStatus)
  Check ($st.global.dhtNodes -gt 20) "DHT running ($($st.global.dhtNodes) nodes)"
  Post "/torrents/$($m.id)/remove" @{ deleteFiles = $true } | Out-Null

  Step 'Single instance'
  Start-Process $exe | Out-Null
  Start-Sleep 3
  Check (@(Get-Process ClipFxTorrent -ErrorAction SilentlyContinue).Count -eq 1) 'second launch reuses the window'
  Shot 'final'
}
catch { Write-Host "ERROR $_" -ForegroundColor Red; $summary.Add("ERROR $_"); $failures++ }
finally {
  Step 'Uninstall'
  Get-Process ClipFxTorrent -ErrorAction SilentlyContinue | Stop-Process -Force
  $un = "$env:ProgramFiles\ClipFx Torrent\unins000.exe"
  if (Test-Path $un) { Start-Process $un -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait }
  Start-Sleep 2
  Check (-not (Test-Path $exe)) 'uninstalled'
  Check (-not (Get-NetFirewallRule -DisplayName 'ClipFx Torrent' -ErrorAction SilentlyContinue)) 'firewall rule removed'
  Check (-not ((Get-ItemProperty 'Registry::HKEY_CURRENT_USER\Software\Classes\magnet\shell\open\command' -ErrorAction SilentlyContinue).'(default)' -like '*ClipFxTorrent*')) 'magnet handler removed'
  Copy-Item "$work\shots" "$PSScriptRoot\..\test-shots" -Recurse -Force -ErrorAction SilentlyContinue
  if ($env:GITHUB_STEP_SUMMARY) { "### ClipFx Torrent on Windows`n" + (($summary | ForEach-Object { "- $_" }) -join "`n") | Out-File $env:GITHUB_STEP_SUMMARY -Append }
  Write-Host "`n$($summary -join "`n")"
  Write-Host "`nFailures: $failures"
}
exit $failures
