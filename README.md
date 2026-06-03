# ChoreBuddy Agent

Windows-side companion to the ChoreBuddy web app. Controls Windows Firewall to block/unblock apps on a kid's PC.

## Current state: test app

`src/ChoreBuddy.TestApp/` — minimal WinForms tool with on/off toggles per app. No Firestore yet, no service, no install — just a window with switches so you can verify the firewall logic works on your own PC.

## Run it

```
run.cmd
```

(or `dotnet run` from `src/ChoreBuddy.TestApp/`)

A UAC prompt appears — required because changing firewall rules needs admin. Toggle an app; flipping to **BLOCKED** kills its internet, **Allowed** restores it. Use "+ Add custom app..." to pick any .exe.

Verify it worked: with Steam blocked, open Steam — it should fail to connect to the network.

## What it does under the hood

- Each toggle creates or removes a Windows Firewall outbound block rule named `ChoreBuddy_<AppKey>`
- Uses `netsh advfirewall` (simple, reliable)
- State is read live from the firewall, so manually editing rules in `wf.msc` is reflected on next launch

## Next steps (not built yet)

1. Convert agent core into a Windows Service (LocalSystem)
2. Firestore listener subscribes to `kids/{id}/appAccess`
3. Watchdog scheduled task
4. Inno Setup installer with Google OAuth gate
5. Uninstaller with matching OAuth check
