# Real Windows test of the installed ClipFx Share app. Run as administrator.
param([Parameter(Mandatory)] [string] $Setup, [Parameter(Mandatory)] [string] $Cli)
$ErrorActionPreference = 'Stop'
$failures = 0
function Step($m) { Write-Host "`n=== $m" -ForegroundColor Cyan }
function Check($c, $m) { if ($c) { Write-Host "PASS $m" -ForegroundColor Green } else { Write-Host "FAIL $m" -ForegroundColor Red; $script:failures++ } }
function WaitUntil([scriptblock] $c, [int] $s, [string] $what) {
  $d = (Get-Date).AddSeconds($s)
  while ((Get-Date) -lt $d) { try { $r = & $c; if ($r) { return $r } } catch { }; Start-Sleep -Milliseconds 300 }
  throw "Timed out: $what"
}
$Ui = 'http://127.0.0.1:47650/api'
$H = @{ 'X-ClipFx' = '1' }
function State { Invoke-RestMethod "$Ui/state" -TimeoutSec 5 }
function Post($p, $b) { Invoke-RestMethod "$Ui$p" -Method Post -Headers $H -ContentType 'application/json' -Body ($b | ConvertTo-Json -Compress -Depth 5) -TimeoutSec 30 }
$work = 'C:\cfs-test'; Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force "$work\src\Trip\day1" | Out-Null
$rng = [System.Random]::new(1)
function RandFile($path, $size) { $b = New-Object byte[] $size; $rng.NextBytes($b); [IO.File]::WriteAllBytes($path, $b) }
RandFile "$work\src\movie.mp4" 200000000
RandFile "$work\src\Trip\day1\beach.jpg" 3000000
RandFile "$work\src\setup.apk" 5000000
$exe = "$env:ProgramFiles\ClipFx Share\ClipFxShare.exe"
$saveRoot = Join-Path $env:USERPROFILE 'Downloads\ClipFx Share'

try {
  Step 'Install'
  Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
  Check (Test-Path $exe) 'installed'
  $rules = Get-NetFirewallRule -DisplayName 'ClipFx Share' -ErrorAction SilentlyContinue
  Check ($rules.Count -ge 2) "firewall allows incoming transfers ($($rules.Count) rules)"
  Check (Test-Path "$env:APPDATA\Microsoft\Windows\SendTo\ClipFx Share.lnk") '"Send to" entry in Explorer'
  Check ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).'ClipFx Share') 'starts with Windows'

  Step 'App starts and shows its UI'
  $app = Start-Process $exe -PassThru
  $s = WaitUntil { State } 60 'app api'
  Check ($s.self.port -eq 47630) "listening on 47630 ($($s.self.port))"
  $s = WaitUntil { $x = State; if ($x.uiClients -ge 1) { $x } } 60 'window loaded the UI'
  Check ($s.uiClients -ge 1) 'WebView2 window loaded the UI'
  Check ((Get-Process -Id $app.Id).MainWindowTitle -eq 'ClipFx Share') 'window title'
  Post '/settings' @{ askBeforeReceive = $false } | Out-Null

  Step 'PC -> app (receive), sorted by type'
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $out = & dotnet $Cli send --data "$work\cli-send" --to 127.0.0.1:47630 "$work\src\movie.mp4" "$work\src\Trip" "$work\src\setup.apk"
  Write-Host $out
  Check ($out -match 'RESULT Completed') 'transfer completed'
  Check ((Get-FileHash "$saveRoot\Videos\movie.mp4").Hash -eq (Get-FileHash "$work\src\movie.mp4").Hash) 'video saved to Videos, identical'
  Check (Test-Path "$saveRoot\Apps\setup.apk") 'apk saved to Apps'
  Check ((Get-FileHash "$saveRoot\Folders\Trip\day1\beach.jpg").Hash -eq (Get-FileHash "$work\src\Trip\day1\beach.jpg").Hash) 'folder kept under Folders'

  Step 'App -> PC (send)'
  $recv = Start-Process dotnet -ArgumentList "`"$Cli`" receive --data $work\cli-recv --save $work\cli-save --port 47700" -PassThru -NoNewWindow -RedirectStandardOutput "$work\recv.log"
  Start-Sleep 3
  $peer = Post '/peers/add' @{ address = '127.0.0.1:47700' }
  Check ($peer.name -eq '.NET receiver') "added peer by IP ($($peer.name))"
  $r = Post '/send' @{ targets = @($peer.id); paths = @("$work\src\movie.mp4"); text = 'hello from windows' }
  WaitUntil { (Get-Content "$work\recv.log" -Raw) -match 'DONE Completed' } 120 'app send' | Out-Null
  Check ((Get-FileHash "$work\cli-save\movie.mp4").Hash -eq (Get-FileHash "$work\src\movie.mp4").Hash) 'sent file identical'
  $t = (State).transfers | Where-Object { $_.direction -eq 'send' } | Select-Object -First 1
  Check ($t.state -eq 'completed') "app shows the send as completed ($($t.state))"
  Stop-Process -Id $recv.Id -Force

  Step 'Send to (second launch hands files to the running app)'
  $second = Start-Process $exe -ArgumentList "`"$work\src\setup.apk`"" -PassThru
  Check ($second.WaitForExit(15000)) 'second launch exits'
  Check (@(Get-Process ClipFxShare -ErrorAction SilentlyContinue).Count -eq 1) 'one instance running'

  Step 'Uninstall'
  Start-Process "$env:ProgramFiles\ClipFx Share\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
  Start-Sleep 3
  Check (-not (Get-NetFirewallRule -DisplayName 'ClipFx Share' -ErrorAction SilentlyContinue)) 'firewall rules removed'
  Check (-not (Get-Process ClipFxShare -ErrorAction SilentlyContinue)) 'app stopped'
}
catch { Write-Host "FAIL unexpected: $_" -ForegroundColor Red; $failures++ }

if ($failures) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host "`nAll ClipFx Share Windows checks passed" -ForegroundColor Green
