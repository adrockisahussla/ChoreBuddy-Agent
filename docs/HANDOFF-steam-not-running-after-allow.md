# Handoff — "Steam not running" after ALLOW

**Last touched:** 2026-06-02
**Status:** ✅ RESOLVED (see resolution section below)
**Affects:** All buddies whose blocked-app list includes Steam

---

## ✅ RESOLUTION (2026-06-02)

The original "Steam isn't relaunched" theory was only the surface. Implementing Option A exposed two deeper root causes that were the *real* reason games came up offline / toggles stopped working.

### Root cause 1 — orphaned firewall rules blocked Steam's network
SHUTOFF added `ChoreBuddy_Steam*` outbound **block** rules. `FirewallManager.Block` did `delete (allowFailure) → add`, and `Unblock` did `delete (allowFailure)`. When a delete silently failed, the next SHUTOFF stacked a **duplicate** rule. Duplicate rules **corrupted the Windows firewall policy store** — they enumerated in netsh but couldn't be deleted by *any* API (netsh / `Remove-NetFirewallRule` / COM all returned `0x2 / "file not found"`). ALLOW logged "Unblocked" while the block survived, so Steam launched but had **no network → offline mode**. The orphans only lived in the in-memory ActiveStore (not the registry PersistentStore), so a reboot cleared them.

**Fix (`FirewallManager.cs`):**
- New `DeleteRuleCompletely(name)` — loops `show`/`delete` until the rule is verifiably gone.
- `Block` now `DeleteRuleCompletely` **before** add → no duplicate stacking → no corruption.
- `Unblock` now **throws if the rule survives** instead of swallowing the failure.

### Root cause 2 — Firestore free-tier read quota exhaustion (why toggles "randomly" stopped)
The agent polled Firestore **every 3 s** (~57k reads/day) — over the Spark plan's 50k/day limit. By evening, reads returned **429 RESOURCE_EXHAUSTED**; `GetSnapshotAsync` returned null and commands silently never applied. This is why SHUTOFF "worked in the morning, not at night."

**Fix — replaced polling with a push channel (Firebase Realtime Database):**
- New `RealtimeDbClient.cs` — one persistent SSE stream (`text/event-stream`) on `firewallControl/<machineId>/control`; auto-reconnects on drop / token expiry. Idle = ~0 reads.
- `RemoteSync` starts the listener; commands apply instantly via `OnControlEvent`. The Firestore loop dropped from 3 s → **5 min** and now only refreshes config/pairing (~900 reads/day).
- RTDB security rule locks read/write to the manager uid only (`auth.uid === '<manager>'`).
- Mobile `FirewallScreen.tsx` now writes the command to RTDB via an authenticated REST `PUT` (manager's `getIdToken()`), alongside the existing Firestore write that keeps the on-screen badge live. No RTDB native module needed.

### Option A (Steam auto-launch) — also shipped
- `ProcessLauncher.LaunchInActiveSession` — WTS token + `CreateProcessAsUser` to launch into the kid's session from LocalSystem.
- `SteamRestarter.CleanRestart` — stop Steam Client Service, kill steam procs, relaunch fresh on ALLOW.

### Verified
SHUTOFF→ALLOW over the RTDB push channel: Steam killed on shutoff, restarted on allow, **0 ChoreBuddy firewall rules left** after allow, Steam comes up online. End-to-end from the test sender confirmed; phone-triggered path pending new APK install.

### New/changed files
- Agent: `RealtimeDbClient.cs` (new), `ProcessLauncher.cs` (new), `SteamRestarter.cs` (new), `FirewallManager.cs` (delete-completely), `RemoteSync.cs` (RTDB listener + slow poll), `LocalConfig.cs` (`RealtimeDbUrl`).
- Mobile: `src/screens/manager/FirewallScreen.tsx` (RTDB push write).
- Firebase: Realtime Database enabled (us-central1), rules locked to manager uid.

### Follow-ups (not blocking)
- RTDB rule currently single-uid; extend to managers+buddies via an allowlist node if buddies should control PCs.
- Consider moving the UI's current-state read off Firestore onto RTDB so Firestore can be dropped from the command path entirely.

---

## ⬇️ Original investigation (superseded — kept for history)


## The symptom

1. Manager toggles the kid's PC **OFF** (SHUTOFF) on the mobile Firewall screen.
2. Agent does its thing — Steam.exe + every Steam-library game .exe are IFEO-blocked. Steam process is killed. Game .exe is also killed via the related-games scan. Browsers blocked.
3. Manager toggles **ON** (ALLOW). Agent removes every IFEO entry it owns and the netsh firewall rules. Verified clean via the registry query — `ChoreBuddyManaged` flag is gone from every IFEO key.
4. Kid double-clicks a game (e.g. `7DaysToDie.exe`). Game launches successfully — proof the IFEO entries are gone.
5. Game shows its own error screen: *"Could not fully initialize Steam: Not logged into Steam or running in Offline mode."*
6. Manager assumes ChoreBuddy is broken.

It is not. The game is right — Steam genuinely isn't running. ALLOW unblocked Steam.exe, but didn't *start* it. Pre-SHUTOFF Steam was running; SHUTOFF killed it via `Process.Kill(entireProcessTree:true)`; ALLOW lifts blocks but does not re-launch.

## Why this happens

The agent's contract is "allow / block at the launcher level." It treats Steam as an .exe that the kid can launch when allowed. It does not own Steam's lifecycle. Three ways out:

| Option | Tradeoff |
|---|---|
| **A. Auto-launch Steam after ALLOW** | Cleanest UX. Agent runs as LocalSystem in session 0; launching Steam into the kid's interactive session needs `CreateProcessAsUser` via WTS (`WTSGetActiveConsoleSessionId` → token impersonation). Non-trivial Win32 P/Invoke. ~80 lines of C#. |
| **B. Tell the kid "start Steam first"** | Zero code. Add a one-liner to the `BannedPopupForm` body or the overlay UI when transitioning ALLOW → "Steam is unblocked. Open Steam, then your game." Cheapest. |
| **C. Don't kill Steam on SHUTOFF** | Just block its outbound network so it can't authenticate / launch games, but leave the process alive. Kid keeps Steam open; on ALLOW everything just works. Requires distinguishing "kill" vs "network-block" intent in the agent config. Already-half-built (`KillRelated` toggle) but Steam itself is treated as kill-on-shutoff today. |

## What we tried in this session (already done, don't redo)

- **Manifest:** flipped `requireAdministrator` → `asInvoker` so the IFEO-redirected `--banned` popup can run as the kid (non-admin). Confirmed working.
- **Full-screen popup:** `BannedPopupForm` now covers the whole screen with a centered card + Dismiss button + ESC dismiss + 6 s auto-dismiss. Confirmed via IFEO trigger from Steam.exe.
- **TRACE logging:** popup writes breadcrumbs to `%TEMP%\ChoreBuddyAgent-crash.log` (writable by non-admin kids). Confirmed two TRACE lines per fire: entry + clean return.
- **Toggle confirmation:** mobile `FirewallScreen.tsx` now requires a confirm modal each direction; Allow side is `confirmDestructive` so it stands out. Ships in v1.35.
- **Manifest rebuild trap:** watchdog scheduled task `ChoreBuddyAgentWatchdog` auto-restarts the service every minute, re-locking the .exe. Must `Unregister-ScheduledTask -TaskName ChoreBuddyAgentWatchdog -Confirm:$false` before any rebuild or the new binary never overwrites the running one. Currently unregistered on the dev machine.

## What I recommend next

**Ship Option A — agent auto-launches Steam after ALLOW.**

Why: kids don't read instructions, and option B leaves them staring at a Steam-offline screen indistinguishable from a real bug. Option C is a bigger refactor of the agent config model.

Implementation sketch:
1. New helper `ProcessLauncher.LaunchInActiveSession(string exePath)` in the agent. Uses:
   - `WTSGetActiveConsoleSessionId` (wtsapi32.dll)
   - `WTSQueryUserToken` to get a token for the kid's session
   - `DuplicateTokenEx` → primary token
   - `CreateProcessAsUser` with `CREATE_NEW_CONSOLE` flag and the kid's token
2. In `RemoteSync.ApplyCommand("allow")`, after the existing unblock loop:
   - Iterate the AppConfig entries
   - For any app where the config still says `RemoteEnabled` (i.e. it WAS blocked) and the exe path resolves to Steam/Epic/Chrome/etc., `LaunchInActiveSession(exePath)`
3. Skip games (only relaunch launchers — kids re-open Steam, they launch the game themselves).

## Open questions

- Do we want to auto-launch *only* Steam, or every previously-blocked launcher (Epic too)? Probably only Steam since that's where the friction is.
- Should the kid see a "Welcome back" overlay when ALLOW lands, listing what's available? Out of scope but a nice touch.

## Build / deploy trap — READ BEFORE TOUCHING THE AGENT

The agent installs a Windows Service (`ChoreBuddyAgent`, LocalSystem) **plus a watchdog scheduled task** (`ChoreBuddyAgentWatchdog`) that re-launches the service every minute if it's not running. Together they make naive rebuilds silently fail — the build copies the new `.exe` into `bin\Debug\net8.0-windows\`, but the service has the OLD binary mapped in memory and locks the file. `dotnet build` exits with "file in use" warnings (or worse, "succeeds" while the on-disk binary is still old). You'll then waste 30 minutes wondering why your code changes didn't take effect.

**Always do this exact sequence before rebuilding:**

```powershell
# 1. Stop the watchdog so it doesn't re-launch the service mid-build
Unregister-ScheduledTask -TaskName ChoreBuddyAgentWatchdog -Confirm:$false

# 2. Stop the service
Stop-Service ChoreBuddyAgent

# 3. Kill any lingering interactive instance (overlay, wizard, mgmt UI)
Stop-Process -Name ChoreBuddy.TestApp -Force -ErrorAction SilentlyContinue

# 4. Now rebuild
cd "C:\Users\NWI - E02\Desktop\ChoreBuddy-Agent\src\ChoreBuddy.TestApp"
dotnet build

# 5. Restart the service
Start-Service ChoreBuddyAgent
```

**To verify your build actually deployed**, check the file timestamp:

```powershell
(Get-Item ".\bin\Debug\net8.0-windows\ChoreBuddy.TestApp.exe").LastWriteTime
```

If it's not within the last minute, the build was a no-op and you're still running the old binary.

**Re-registering the watchdog** when you're done with dev work: re-run the setup wizard (`ChoreBuddy.TestApp.exe --setup` from an elevated shell). The wizard's install step writes the scheduled task again. Or leave it off on dev machines indefinitely — the service is stable enough.

**Why this matters for this Steam handoff specifically:** when implementing Option A (auto-launch Steam in the kid's session), expect to iterate 5–10 times to get `CreateProcessAsUser` working. Without the watchdog dance, every iteration silently loads stale code and you'll chase ghosts.

## Where everything lives — distribution + update channels

This project is two codebases. They release on different cadences via different channels.

### Mobile app (Android APK)
- **Repo:** `https://github.com/adrockisahussla/ChoreBuddy` (public)
- **Branch:** `claude/full-port`
- **Releases:** `https://github.com/adrockisahussla/ChoreBuddy/releases`
- **Always-latest download URL:** `https://github.com/adrockisahussla/ChoreBuddy/releases/latest/download/ChoreBuddy.apk`
- **Update path on a paired phone:**
  - In-app: **Settings → App version → Check for update → Download & install**. Calls the GitHub API for `releases/latest`, downloads the APK from `releases/latest/download/ChoreBuddy.apk`, hands it to the system installer via FileProvider. Native module is `ApkUpdaterModule.kt` in the mobile repo.
  - Manual: open `https://github.com/adrockisahussla/ChoreBuddy/releases/latest` in mobile Chrome → tap the APK asset → Install. Use this if the in-app updater fails (historically buggy on poor cellular — hardened in v1.17 but keep the fallback in mind).
- **Cutting a new release:** build via `cd android && ./gradlew assembleRelease`, copy the APK to a temp file (gh's `#rename` is unreliable on Windows bash — see the mobile `HANDOFF.md` for the exact cheat-sheet), then `gh release create vX.Y --repo adrockisahussla/ChoreBuddy …`. Bump `versionCode` + `versionName` in `android/app/build.gradle` first.

### Windows agent
- **No public distribution yet.** Source lives on the dev machine at `C:\Users\NWI - E02\Desktop\ChoreBuddy-Agent\`. Each PC gets the agent by running `setup.cmd` from that folder while logged in as an admin on the kid's machine. There is no `releases/` for the agent.
- **Implication:** every fix to the agent is currently a hand-deploy. If we end up with more than a couple of paired PCs we need to either (a) publish a built agent to its own GitHub release or (b) have the mobile app trigger an agent self-update via Firestore (write a `agentVersion` to `firewallControl/{machineId}` and have the agent pull a new build from a URL when it sees a newer version).
- **Where the running binary actually is on a paired PC:** wherever `setup.cmd` was run from. The wizard's install step does `sc create ChoreBuddyAgent binPath= "<this exe>" --service`, so the service is bound to that specific path. Move the folder and the service breaks until you re-run setup. Worth fixing eventually.

### Firestore rules + landing page
- Rules file: `firestore.rules` in the mobile repo. Deploy with `firebase deploy --only firestore:rules` from the mobile repo root.
- Landing page (`https://chorebuddy-67a5f.web.app/invite`): `public/invite.html` in the mobile repo. Deploy with `firebase deploy --only hosting`.
- Both are in the mobile repo by convenience, not because they belong there architecturally.

## Files involved

- Agent — `src/ChoreBuddy.TestApp/RemoteSync.cs` (ApplyCommand, lines ~178–244 per audit).
- Agent — `src/ChoreBuddy.TestApp/FirewallManager.cs` (currently kills processes; option C would gate this).
- Mobile — `src/screens/manager/FirewallScreen.tsx` (no changes needed for A/B; if C ships, add per-app "kill on shutoff" toggle).
- Docs — this file. Move into the agent's main `HANDOFF.md` once decided.
