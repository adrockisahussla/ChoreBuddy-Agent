using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChoreBuddy.TestApp;

public class RemoteSync
{
    readonly FirestoreClient _firestore;
    readonly LocalConfig _config;
    readonly AuthClient? _auth;
    readonly Func<List<(string key, string label, string path, bool isLauncher)>> _appProvider;
    readonly Action<string> _log;
    readonly Action _onConfigChanged;
    readonly Action _onCommandApplied;
    readonly Action<string?> _onKidIdChanged;

    CancellationTokenSource? _cts;
    Task? _pollTask;
    Task? _listenTask;
    RealtimeDbClient? _rtdb;
    readonly object _applyLock = new();
    DateTimeOffset _lastScanAt = DateTimeOffset.MinValue;
    static readonly TimeSpan RescanInterval = TimeSpan.FromHours(1);
    // Commands now arrive via RTDB push, so the Firestore loop only needs to
    // refresh slow-moving config/pairing — a few hundred reads/day, well under quota.
    static readonly TimeSpan ConfigPollInterval = TimeSpan.FromMinutes(5);

    public RemoteSync(
        LocalConfig config,
        AuthClient? auth,
        Func<List<(string key, string label, string path, bool isLauncher)>> appProvider,
        Action<string> log,
        Action onConfigChanged,
        Action onCommandApplied,
        Action<string?> onKidIdChanged)
    {
        _config = config;
        _auth = auth;
        _firestore = new FirestoreClient(auth, config);
        _appProvider = appProvider;
        _log = log;
        _onConfigChanged = onConfigChanged;
        _onCommandApplied = onCommandApplied;
        _onKidIdChanged = onKidIdChanged;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _rtdb = new RealtimeDbClient(_config.RealtimeDbUrl, _auth);
        _pollTask = Task.Run(() => PollLoop(_cts.Token));
        // Persistent push channel for commands — instant, near-zero cost.
        _listenTask = Task.Run(() => _rtdb.ListenAsync(
            $"firewallControl/{_config.MachineId}/control",
            OnControlEvent, _log, _cts!.Token));
        _log($"Remote sync started (machine: {_config.MachineId})");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public async Task PushAppConfigAsync(string appKey, AppConfigEntry settings)
    {
        if (_cts == null) return;
        await _firestore.PushAppConfigAsync(_config.MachineId, appKey, settings, _cts.Token);
    }

    async Task RescanAndRegister(CancellationToken ct)
    {
        var apps = _appProvider();
        var info = apps.Select(a => new InstalledAppInfo(
            a.key, a.label, a.path, a.isLauncher, !string.IsNullOrEmpty(a.path) && System.IO.File.Exists(a.path)
        )).ToList();
        await _firestore.RegisterInstalledAppsAsync(_config.MachineId, info, ct);
        _lastScanAt = DateTimeOffset.UtcNow;
        _config.LastRescanAt = _lastScanAt.ToUnixTimeMilliseconds();
        ConfigStore.Save(_config);
    }

    async Task PollLoop(CancellationToken ct)
    {
        try
        {
            await RescanAndRegister(ct);
            _log("Registered installed apps (initial scan)");
        }
        catch (Exception ex) { _log($"Register failed: {ex.Message}"); }

        var heartbeatEvery = TimeSpan.FromSeconds(45);
        var lastHeartbeat = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - _lastScanAt > RescanInterval)
                {
                    await RescanAndRegister(ct);
                    _log("Re-scanned installed apps");
                }

                if (DateTimeOffset.UtcNow - lastHeartbeat > heartbeatEvery)
                {
                    await _firestore.HeartbeatAsync(_config.MachineId, ct);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }

                var snap = await _firestore.GetSnapshotAsync(_config.MachineId, ct);
                if (snap != null)
                {
                    if (snap.KidId != _config.KidId)
                    {
                        _log($"Paired to: {snap.KidId ?? "(none)"}");
                        _config.KidId = snap.KidId;
                        ConfigStore.Save(_config);
                        _onKidIdChanged(snap.KidId);
                    }

                    if (!string.IsNullOrEmpty(snap.KidId))
                    {
                        var ks = await _firestore.GetKidSettingsAsync(snap.KidId, ct);
                        if (ks != null) _kidSettings = ks;
                    }

                    if (ApplyCloudConfig(snap.AppConfig))
                    {
                        ConfigStore.Save(_config);
                        _onConfigChanged();
                    }

                    // Commands are handled by the RTDB push listener (OnControlEvent),
                    // not here — this loop only refreshes config/pairing.
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log($"Poll error: {ex.Message}");
            }

            try { await Task.Delay(ConfigPollInterval, ct); }
            catch { break; }
        }
    }

    /**
     * Called by the RTDB stream whenever the machine's control node changes.
     * dataPayload is the SSE envelope, e.g.
     *   {"path":"/","data":{"command":"shutoff","timestamp":1780457981384}}
     * The phone writes the whole control object at once, so a real command
     * arrives as path "/" with data = { command, timestamp }.
     */
    void OnControlEvent(string evName, string dataPayload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(dataPayload);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return;
            if (dataEl.ValueKind != System.Text.Json.JsonValueKind.Object) return; // null / partial write

            string? command = null;
            long timestamp = 0;
            if (dataEl.TryGetProperty("command", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                command = c.GetString();
            if (dataEl.TryGetProperty("timestamp", out var t))
            {
                if (t.ValueKind == System.Text.Json.JsonValueKind.Number) timestamp = t.GetInt64();
                else if (t.ValueKind == System.Text.Json.JsonValueKind.String) long.TryParse(t.GetString(), out timestamp);
            }

            if (string.IsNullOrEmpty(command)) return;

            lock (_applyLock)
            {
                // On (re)connect RTDB replays the current value — skip anything
                // we've already applied so a reconnect can't re-fire an old command.
                if (timestamp <= _config.LastCommandTimestamp) return;

                _log($"Push command: {command} (ts={timestamp})");
                ApplyCommand(command);
                _config.LastCommandTimestamp = timestamp;
                ConfigStore.Save(_config);
            }
            _onCommandApplied();
        }
        catch (Exception ex) { _log($"Push handler error: {ex.Message}"); }
    }

    bool ApplyCloudConfig(Dictionary<string, AppConfigEntry> cloudConfig)
    {
        if (cloudConfig.Count == 0) return false;
        bool changed = false;
        foreach (var (key, entry) in cloudConfig)
        {
            var local = ConfigStore.GetOrInit(_config, key);
            if (local.RemoteEnabled != entry.RemoteEnabled || local.KillRelated != entry.KillRelated)
            {
                local.RemoteEnabled = entry.RemoteEnabled;
                local.KillRelated = entry.KillRelated;
                changed = true;
            }
        }
        return changed;
    }

    KidSettings _kidSettings = new(true, "", "👤");

    public void UpdateKidSettings(KidSettings settings) => _kidSettings = settings;

    void WriteOverlayState(bool blocked)
    {
        OverlayState.Write(new OverlayStateData
        {
            Blocked = blocked,
            Dismissable = _kidSettings.OverlayDismissable,
            KidName = _kidSettings.Name,
            KidAvatar = _kidSettings.Avatar,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Acknowledged = false
        });
    }

    void ApplyCommand(string command)
    {
        var apps = _appProvider();

        if (command.Equals("shutoff", StringComparison.OrdinalIgnoreCase))
        {
            WriteOverlayState(true);
            var newlyBlocked = new List<BlockedRecord>();
            foreach (var (key, label, path, isLauncher) in apps)
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                var s = ConfigStore.GetOrInit(_config, key);
                if (!s.RemoteEnabled) continue;

                try
                {
                    FirewallManager.Block(key, path);
                    newlyBlocked.Add(new BlockedRecord { AppKey = key, ExePath = path });
                    _log($"Blocked: {label}");

                    if (isLauncher && s.KillRelated)
                    {
                        var games = key.StartsWith("Steam", StringComparison.OrdinalIgnoreCase)
                            ? SteamScanner.ScanInstalledGames()
                            : key.StartsWith("Epic", StringComparison.OrdinalIgnoreCase)
                                ? EpicScanner.ScanInstalledGames()
                                : new List<InstalledGame>();
                        foreach (var g in games)
                        {
                            foreach (var exe in g.Executables)
                            {
                                var gkey = $"{g.Source}_{g.AppId}_{System.IO.Path.GetFileNameWithoutExtension(exe)}";
                                try
                                {
                                    FirewallManager.Block(gkey, exe);
                                    newlyBlocked.Add(new BlockedRecord { AppKey = gkey, ExePath = exe });
                                }
                                catch { }
                            }
                        }
                        _log($"Killed related games for {label}: {games.Count}");
                    }
                }
                catch (Exception ex) { _log($"Failed to block {label}: {ex.Message}"); }
            }

            foreach (var b in newlyBlocked)
            {
                if (!_config.RemotelyBlocked.Any(r => r.AppKey == b.AppKey))
                    _config.RemotelyBlocked.Add(b);
            }
        }
        else if (command.Equals("allow", StringComparison.OrdinalIgnoreCase))
        {
            WriteOverlayState(false);
            var wasBlocked = _config.RemotelyBlocked.ToList();
            foreach (var b in wasBlocked)
            {
                try
                {
                    FirewallManager.Unblock(b.AppKey, b.ExePath);
                    _log($"Unblocked: {b.AppKey}");
                }
                catch (Exception ex) { _log($"Failed to unblock {b.AppKey}: {ex.Message}"); }
            }
            _config.RemotelyBlocked.Clear();

            // Belt-and-suspenders: disable EVERY ChoreBuddy rule still enabled,
            // even ones the tracked list missed (out-of-sync after a restart, or
            // created by a different shutoff). Non-destructive — just flips
            // enable=no — so it can never strand an orphan block (which is what
            // kept Steam offline). Returns the rules it touched.
            var disabled = new List<string>();
            try
            {
                disabled = FirewallManager.DisableAllRules(_log);
                if (disabled.Count > 0) _log($"Disabled {disabled.Count} ChoreBuddy rule(s) on allow");
            }
            catch (Exception ex) { _log($"DisableAllRules failed: {ex.Message}"); }

            // SHUTOFF hard-kills Steam; unblocking only lifts the launch block, it
            // doesn't restart Steam. And a bare relaunch into the leftover Steam state
            // (surviving Steam Client Service + orphaned helpers) comes up Offline, so
            // games still fail with "Could not initialize Steam." Tear Steam fully down
            // and boot it fresh so it signs in online. Launchers only — the kid opens
            // their own games.
            var steamApp = apps.FirstOrDefault(a =>
                a.isLauncher && a.key.StartsWith("Steam", StringComparison.OrdinalIgnoreCase));
            var steamWasBlocked = wasBlocked.Any(b =>
                b.AppKey.Equals(steamApp.key, StringComparison.OrdinalIgnoreCase))
                || disabled.Any(n => n.StartsWith("ChoreBuddy_Steam", StringComparison.OrdinalIgnoreCase));
            if (steamWasBlocked && !string.IsNullOrEmpty(steamApp.path) && System.IO.File.Exists(steamApp.path))
            {
                try { SteamRestarter.CleanRestart(steamApp.path, _log); }
                catch (Exception ex) { _log($"Failed to restart Steam: {ex.Message}"); }
            }
        }
        else if (command.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            // Manager-triggered immediate update check. Bypasses the hourly
            // poll; AgentUpdater spawns a temp-copy that stops the service,
            // swaps files, restarts.
            _log("Push command: update — checking GitHub for newer release");
            _ = Task.Run(async () =>
            {
                try
                {
                    var started = await AgentUpdater.CheckAndUpdateAsync(_log, CancellationToken.None);
                    if (!started) _log("Updater: already up to date or no release");
                }
                catch (Exception ex) { _log($"Updater error: {ex.Message}"); }
            });
        }
    }
}
