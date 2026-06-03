# ChoreBuddy Firewall Agent — Handoff

Built: a Windows desktop firewall agent + remote control via Firestore + a debug UI in the existing ChoreBuddy manager app. Parent triggers SHUTOFF/ALLOW from their phone; agent on kid's PC applies it within ~3 seconds.

---

## What works today

### Firewall agent (C# / .NET 8 WinForms)
**Location:** `C:\Users\NWI - E02\Desktop\ChoreBuddy-Agent\`
**Launch:** double-click `run.cmd` (self-elevates via UAC)

- Per-app block/unblock with three enforcement layers:
  1. **Kill running processes** — entire process tree
  2. **IFEO registry block** — `HKLM\...\Image File Execution Options\<exe>\Debugger = systray.exe` — Windows refuses to launch the .exe at all
  3. **Windows Firewall outbound rule** — backup in case IFEO is bypassed
- Pre-configured app list: Steam, Epic Games Launcher, Discord (auto-resolves versioned path), Chrome, Edge, Firefox
- **Steam game scanner** — parses `libraryfolders.vdf` + `appmanifest_*.acf`, finds all installed games and their .exes (filters out uninstallers/redist/crashpads)
- **Epic game scanner** — parses `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` JSON
- Custom app picker (file dialog → any .exe)

### Remote control via Firestore
**Project:** `chorebuddy-67a5f` (same Firestore project as ChoreBuddy)
**Collection:** `firewallControl/{machineId}` (machineId = Windows hostname, e.g. `DESKTOP-EQJAT9S`)

Document schema:
```json
{
  "command": "shutoff" | "allow",
  "timestamp": <unix ms>,
  "setBy": "manager",
  "machineName": "DESKTOP-EQJAT9S",
  "lastSeenAt": <unix ms>
}
```

- Agent **polls every 3 seconds** for new commands
- Agent **heartbeats every 60 seconds** (writes `lastSeenAt`) so the manager UI can list active machines
- On SHUTOFF: blocks every app where local "Remote shutoff" checkbox is checked. For Steam/Epic with "Kill related games" also checked, scans and blocks every installed game's .exes.
- On ALLOW: unblocks everything that was remotely blocked (tracked in local config so unrelated manual blocks stay put)
- Local config: `%LOCALAPPDATA%\ChoreBuddy\agent-config.json`
- Crash log: `%LOCALAPPDATA%\ChoreBuddy\crash.log` (any startup exception is captured)

### ChoreBuddy manager-app additions
**File:** `C:\Users\NWI - E02\Desktop\ChoreBuddy\manager-app.html`

- New menu item **🚫 Firewall Debug** (in the side menu, after Reminders)
- New `FirewallDebug` React component:
  - Subscribes to the `firewallControl` collection
  - Lists every machine that's heartbeated, with "last seen: Xs ago"
  - For each machine: 🚫 **SHUTOFF** and ✅ **ALLOW** buttons
  - Writes the command to Firestore with current timestamp

---

## File map

```
ChoreBuddy-Agent/
├── run.cmd                          self-elevating launcher
├── HANDOFF.md                       this doc
├── README.md
└── src/ChoreBuddy.TestApp/
    ├── ChoreBuddy.TestApp.csproj    net8.0-windows, WinForms
    ├── app.manifest                 requireAdministrator
    ├── Program.cs                   entry + crash logging
    ├── MainForm.cs                  main window UI
    ├── GameListForm.cs              Steam/Epic game list dialog
    ├── FirewallManager.cs           netsh + IFEO + process kill
    ├── SteamScanner.cs              .vdf / .acf parser
    ├── EpicScanner.cs               .item JSON parser
    ├── LocalConfig.cs               JSON config persistence
    ├── FirestoreClient.cs           REST API client (no auth, uses public web API key)
    └── RemoteSync.cs                polling + command application

ChoreBuddy/
└── manager-app.html                 added FirewallDebug component + menu entry + route
```

---

## How to test end-to-end

1. Launch firewall agent: `run.cmd` → UAC Yes
2. In the agent window, check the blue **"Remote shutoff"** boxes for apps you want remote-controllable
3. For Steam/Epic, also check yellow **"Kill related games"** if you want all games blocked
4. Open manager-app.html (any device with internet) → ☰ menu → 🚫 Firewall Debug
5. Wait up to 60s for your machine to register (or relaunch agent for immediate heartbeat)
6. Tap **SHUTOFF** → within 3s, agent kills + IFEO-blocks + firewall-blocks every checked app
7. Tap **ALLOW** → within 3s, all unblocked

---

## Known limits / things not yet done

1. **Anyone can write to `firewallControl`** — Firestore rules are permissive (matching the rest of ChoreBuddy). Production needs auth-gated writes. Currently any client with the public API key can SHUTOFF any registered machine.
2. **No time-based unlocks** — "Roblox for 30 min" auto-reblock isn't built. Manual ALLOW only.
3. **No tamper resistance** — kid can close the firewall window (which stops the polling loop). Next step is wrapping the engine as a Windows Service with a watchdog so it can't be killed from a Standard user account.
4. **No install/uninstall flow** — runs from `bin\Debug\...\TestApp.exe`. Needs Inno Setup installer with Google OAuth gate (parent's Google account = uninstall key) per earlier design discussion.
5. **Steam offline mode** — if Steam is already open and in offline mode, blocking won't pull it down until next launch. The IFEO + process kill on shutoff handles this.
6. **Edge webview kill** — `IsLaunchBlocked("msedge")` and the kill logic match `msedge*` which also catches `msedgewebview2.exe`. Office/Outlook/Teams may briefly break when Edge is blocked. Refine in production.
7. **Multi-kid** — agent only knows itself, not which kid. For ChoreBuddy integration we'd map machineId → kidId in Firestore.
8. **Firefox not installed** on this PC — row shows grayed out. Fine, just informational.

---

## Architecture summary

```
[Parent phone/browser]                    [Firestore: chorebuddy-67a5f]              [Kid's PC]
  manager-app.html      ───── write ───>  firewallControl/{machineId}      <───── poll/3s ───── ChoreBuddy.TestApp.exe
   ↑ FirewallDebug                          {command, timestamp,                                  ↑ RemoteSync
   tap SHUTOFF/ALLOW                         lastSeenAt}                                          calls FirewallManager
                                                                                                  → kills processes
                        <──── heartbeat ───                              ────── 60s ──────         → IFEO registry
                                                                                                  → netsh firewall
```

- **No direct connection** between phone and PC. Firestore is the broker.
- **Both sides need internet**, but they don't need to be on the same network.
- **API key in source is public** — that's Firestore's standard design. Security is enforced by rules, not by hiding keys.

---

## Next session — recommended priorities

In order of value:

1. **Tighten Firestore security rules** so only the parent's authenticated Google account can write to `firewallControl/*`. Currently anyone can — that's fine for testing, not for production.
2. **Time-based unlocks** — add "Allow for X minutes" buttons. Agent auto-reblocks when timer expires. This is what makes the reward loop real ("complete chore → earn 30 min Roblox").
3. **Windows Service + watchdog** — wrap the engine so closing the window doesn't stop enforcement. Watchdog scheduled task restarts the service if killed.
4. **Inno Setup installer with Google OAuth gate** — parent's Google account = install/uninstall key. Standard user kid can't remove.
5. **Per-kid machine mapping** — Firestore `kids/{kidId}.deviceMachineId` so the manager UI says "Block Anna's PC" instead of "Block DESKTOP-EQJAT9S".
6. **Polish edges** — better Edge handling (don't kill webview2), better Discord version pinning, faster heartbeat on agent start so manager UI shows the machine immediately.

---

## Quick commands

```powershell
# Build
dotnet build "C:\Users\NWI - E02\Desktop\ChoreBuddy-Agent\src\ChoreBuddy.TestApp\ChoreBuddy.TestApp.csproj"

# Clear all ChoreBuddy firewall rules manually (if needed)
Get-NetFirewallRule -DisplayName "ChoreBuddy_*" | Remove-NetFirewallRule

# Clear all IFEO blocks manually (if needed)
Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options" |
  Where-Object { (Get-ItemProperty $_.PSPath -Name "ChoreBuddyManaged" -ErrorAction SilentlyContinue) } |
  ForEach-Object { Remove-Item $_.PSPath -Recurse -Force }

# View crash log
Get-Content "$env:LOCALAPPDATA\ChoreBuddy\crash.log"

# View agent config
Get-Content "$env:LOCALAPPDATA\ChoreBuddy\agent-config.json"
```
