# ClipFx Manager

See and control exactly which apps use your internet data on Windows.

- Live download/upload per app, including Windows services and system processes
- One switch per app to cut its internet completely (Windows Defender Firewall block rules, inbound and outbound, open connections dropped)
- Per-app data limits and an overall limit: when a limit is reached the app (or everything) is cut off at once
- Usage resets daily, weekly, monthly or never
- Light, dark and system theme
- Runs as a Windows service: counting and blocking continue after closing the window and survive restarts

## Install

Download `ClipFxManagerSetup-x.y.z.exe` from the latest successful **Build and test** workflow run (Actions → run → Artifacts) and run it. Requires Windows 10 1809 or later (x64).

## How it works

| Part | What it does |
| --- | --- |
| `ClipFxManager.exe --service` | Background service (LocalSystem). Reads per-process traffic from the `Microsoft-Windows-Kernel-Network` ETW provider, keeps totals in `%ProgramData%\ClipFx Manager\state.json`, and applies firewall rules in the group "ClipFx Manager". Serves the UI on `127.0.0.1:47613`. |
| `ClipFxManager.exe` | The window (WebView2). Needs no admin rights. |

Only internet traffic is counted: loopback, LAN, link-local and multicast addresses are ignored. Counts are payload bytes, so a carrier may report slightly more (protocol headers).

## Develop

```
dotnet test tests/ClipFx.Tests                                   # unit + API tests
dotnet run --project tools/ClipFx.DevHost -- --ui src/ClipFx.Server/wwwroot   # simulated traffic on any OS
cd tests/e2e && npm ci && npx playwright test                    # UI tests
```

CI (`.github/workflows/build.yml`) also runs `scripts/integration-test.ps1` on a real Windows machine: it installs the service, downloads real data, and checks counting, blocking, limits, restarts and uninstall, then builds the installer with Inno Setup.
