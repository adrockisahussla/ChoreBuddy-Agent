using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChoreBuddy.TestApp;

/**
 * ScheduleEnforcer — owns the per-kid play-time policy on this PC.
 *
 * Pulls `gameSchedules/{kidId}` from Firestore **only twice**: once on
 * service start (cold boot) and again whenever the phone pushes a
 * `reload-schedule` command via RTDB. The schedule is cached in
 * LocalConfig so it survives restarts.
 *
 * A purely local 60 s timer evaluates the cached schedule and decides
 * whether games should be allowed or blocked NOW. No Firestore polling
 * — wall clock + cached doc only. Firestore quota burn is bounded:
 * roughly 1 read per machine per schedule edit.
 *
 * Two rule modes per day:
 *   • window — block outside [start, end], no cap inside
 *   • cap    — anytime, but only `maxHours` of accumulated allow-time
 *              per local day (resets at midnight)
 *
 * Cap accounting: every minute the enforcer thinks state should be
 * "allow", we add 60 s to today's used counter and persist it. When
 * used >= cap, flip to shutoff for the rest of the day. The counter
 * lives in LocalConfig so it survives service restarts.
 *
 * The schedule is fetched from Firestore only on:
 *   • Start() — picks up the cached doc or pulls fresh if missing,
 *   • a `reload-schedule` RTDB command — pushed by the phone after a save.
 */
public class ScheduleEnforcer
{
    const string ProjectId = "chorebuddy-67a5f";

    static readonly HttpClient http;
    static ScheduleEnforcer()
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChoreBuddyAgent-Schedule", "1.0"));
    }

    readonly LocalConfig _config;
    readonly AuthClient? _auth;
    readonly Func<List<(string key, string label, string path, bool isLauncher)>> _apps;
    readonly Action<string> _log;

    CancellationTokenSource? _cts;
    GameSchedule? _schedule;
    string? _currentState; // "allow" / "shutoff" — what we last enforced

    // 5-minutes-before-cutoff warning. Armed = eligible to fire. We disarm
    // after firing and re-arm whenever there's more than the lead time left
    // (or play is shut off), so the toast shows once per play session.
    const int WarnLeadSeconds = 300;
    bool _warnArmed = true;
    // Cached banked minutes (users/{kidId}.minutesRemaining) shown on the
    // warning toast and spent when the kid taps "Use extra time".
    int _availableMinutes;

    public ScheduleEnforcer(
        LocalConfig config,
        AuthClient? auth,
        Func<List<(string key, string label, string path, bool isLauncher)>> apps,
        Action<string> log)
    {
        _config = config;
        _auth = auth;
        _apps = apps;
        _log = log;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => TickLoop(_cts.Token));
        _ = Task.Run(() => ExtendLoop(_cts.Token));
        _log("Schedule enforcer started");
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    /** Force an immediate re-pull of the schedule doc. Called from the
     *  RTDB `reload-schedule` command path. */
    public void RequestReload() => _reloadRequested = true;
    volatile bool _reloadRequested;

    async Task TickLoop(CancellationToken ct)
    {
        // One Firestore read on cold start, then never again until a
        // phone-push fires `reload-schedule`. In-memory only.
        await TryReload(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_reloadRequested) { _reloadRequested = false; await TryReload(ct); }
                Tick();
            }
            catch (Exception ex) { _log($"Enforcer tick error: {ex.Message}"); }

            // Local 60 s evaluation — wall clock only, no Firestore.
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                if (_reloadRequested) break;
                try { await Task.Delay(1000, ct); } catch { return; }
            }
        }
    }

    async Task TryReload(CancellationToken ct)
    {
        try
        {
            var kid = _config.KidId;
            if (string.IsNullOrEmpty(kid)) { _log("Enforcer: no KidId — schedule disabled"); _schedule = null; return; }

            var url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/gameSchedules/{Uri.EscapeDataString(kid)}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (_auth != null && _auth.IsSignedIn)
            {
                var token = await _auth.GetValidIdTokenAsync(ct);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _log("Enforcer: no schedule doc yet");
                _schedule = null;
                return;
            }
            if (!resp.IsSuccessStatusCode) { _log($"Enforcer: schedule fetch {(int)resp.StatusCode}"); return; }

            var body = await resp.Content.ReadAsStringAsync(ct);
            _schedule = ParseSchedule(body);
            _availableMinutes = await GetMinutesAsync(ct);
            _log($"Enforcer: schedule loaded ({_schedule?.Days.Count ?? 0} day rules), {_availableMinutes} min banked");
        }
        catch (Exception ex) { _log($"Enforcer: reload failed — {ex.Message}"); }
    }

    void Tick()
    {
        if (_config.SchedulePaused) return; // manual override in effect — schedule is paused
        if (_schedule == null) return;       // no policy → leave manual state alone

        var now = DateTime.Now; // local time
        var dayKey = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "mon", DayOfWeek.Tuesday => "tue", DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu", DayOfWeek.Friday => "fri", DayOfWeek.Saturday => "sat",
            _ => "sun",
        };
        if (!_schedule.Days.TryGetValue(dayKey, out var day) || !day.Enabled)
        {
            // Day disabled → enforce shutoff.
            ApplyState("shutoff", "day disabled");
            _warnArmed = true;
            return;
        }

        var mode = string.IsNullOrEmpty(day.Mode) ? "window" : day.Mode;
        var localDayKey = now.ToString("yyyy-MM-dd");

        // Reset cap counter at midnight.
        if (_config.ScheduleCapDay != localDayKey)
        {
            _config.ScheduleCapDay = localDayKey;
            _config.ScheduleCapUsedSeconds = 0;
            ConfigStore.Save(_config);
        }

        // Kid-purchased bonus time overrides the normal schedule until it
        // runs out. Warn 5 min before the bonus itself ends (so they can
        // top up again if they have more banked).
        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (_config.BonusUntilUnix > nowMs)
        {
            ApplyState("allow", "bonus time");
            MaybeWarn((int)((_config.BonusUntilUnix - nowMs) / 1000));
            return;
        }
        if (_config.BonusUntilUnix != 0)
        {
            _config.BonusUntilUnix = 0;
            ConfigStore.Save(_config);
        }

        if (mode == "window")
        {
            var nowMin = now.Hour * 60 + now.Minute;
            var startMin = ToMin(day.Start);
            var endMin = ToMin(day.End);
            var inWindow = nowMin >= startMin && nowMin < endMin;
            ApplyState(inWindow ? "allow" : "shutoff", inWindow ? "in window" : "outside window");
            if (inWindow) MaybeWarn((endMin - nowMin) * 60 - now.Second);
            else _warnArmed = true;
        }
        else // cap
        {
            var capSeconds = (int)((day.MaxHours ?? 0) * 3600);
            if (capSeconds <= 0) { ApplyState("shutoff", "cap=0"); _warnArmed = true; return; }
            if (_config.ScheduleCapUsedSeconds >= capSeconds)
            {
                ApplyState("shutoff", "cap reached");
                _warnArmed = true;
                return;
            }
            ApplyState("allow", $"cap {_config.ScheduleCapUsedSeconds}/{capSeconds}s used");
            MaybeWarn(capSeconds - _config.ScheduleCapUsedSeconds);
            // Burn 60 s for this tick (the tick interval).
            _config.ScheduleCapUsedSeconds = Math.Min(capSeconds, _config.ScheduleCapUsedSeconds + 60);
            ConfigStore.Save(_config);
        }
    }

    /** Fire the corner toast once when we cross into the final
     *  WarnLeadSeconds of a play session. Re-arms once there's more than
     *  the lead time left again (or play stops). */
    void MaybeWarn(int remainingSec)
    {
        if (remainingSec > 0 && remainingSec <= WarnLeadSeconds)
        {
            if (_warnArmed)
            {
                _warnArmed = false;
                WriteWarn(remainingSec);
            }
        }
        else
        {
            _warnArmed = true;
        }
    }

    void WriteWarn(int remainingSec)
    {
        try
        {
            WarnState.Write(new WarnStateData
            {
                WarnSeconds = remainingSec,
                WarnId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                KidName = OverlayState.Read().KidName,
                AvailableMinutes = _availableMinutes,
            });
            _log($"Enforcer: warning written ({remainingSec}s left, {_availableMinutes} min banked)");
        }
        catch (Exception ex) { _log($"Enforcer: warn write failed — {ex.Message}"); }
    }

    // ---- Kid "use extra time" → extend session + spend banked minutes ----

    async Task ExtendLoop(CancellationToken ct)
    {
        int sinceRefresh = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessExtendRequestAsync(ct);
                // Keep the banked balance reasonably fresh for the toast.
                if (++sinceRefresh >= 15) { sinceRefresh = 0; _availableMinutes = await GetMinutesAsync(ct); }
            }
            catch (Exception ex) { _log($"Enforcer: extend loop error — {ex.Message}"); }
            try { await Task.Delay(2000, ct); } catch { return; }
        }
    }

    async Task ProcessExtendRequestAsync(CancellationToken ct)
    {
        var req = ExtendRequest.Read();
        if (req == null || req.Minutes <= 0) return;
        ExtendRequest.Clear();

        var avail = await GetMinutesAsync(ct);
        var mins = Math.Min(req.Minutes, avail);
        if (mins <= 0) { _availableMinutes = avail; return; }

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var basis = Math.Max(now, _config.BonusUntilUnix); // stack onto any active bonus
        _config.BonusUntilUnix = basis + (long)mins * 60000L;
        ConfigStore.Save(_config);
        _warnArmed = true; // so it warns again near the new end

        await AddMinutesAsync(-mins, ct);
        ApplyState("allow", $"kid used {mins} min extra");
        _log($"Enforcer: +{mins} min bonus (until {_config.BonusUntilUnix}), {_availableMinutes} min left");
    }

    async Task AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_auth != null && _auth.IsSignedIn)
        {
            try
            {
                var token = await _auth.GetValidIdTokenAsync(ct);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch { /* server will 401; caller tolerates */ }
        }
    }

    async Task<int> GetMinutesAsync(CancellationToken ct)
    {
        var kid = _config.KidId;
        if (string.IsNullOrEmpty(kid)) return 0;
        try
        {
            var url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/users/{Uri.EscapeDataString(kid)}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            await AuthorizeAsync(req, ct);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return _availableMinutes;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("fields", out var f)) return 0;
            if (!f.TryGetProperty("minutesRemaining", out var m)) return 0;
            if (m.TryGetProperty("integerValue", out var iv) && long.TryParse(iv.GetString(), out var n)) return (int)Math.Max(0, n);
            if (m.TryGetProperty("doubleValue", out var dv)) return (int)Math.Max(0, dv.GetDouble());
            return 0;
        }
        catch { return _availableMinutes; }
    }

    async Task AddMinutesAsync(int delta, CancellationToken ct)
    {
        var kid = _config.KidId;
        if (string.IsNullOrEmpty(kid)) return;
        try
        {
            var current = await GetMinutesAsync(ct);
            var next = Math.Max(0, current + delta);
            var url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/users/{Uri.EscapeDataString(kid)}?updateMask.fieldPaths=minutesRemaining";
            var body = $"{{\"fields\":{{\"minutesRemaining\":{{\"integerValue\":\"{next}\"}}}}}}";
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            await AuthorizeAsync(req, ct);
            await http.SendAsync(req, ct);
            _availableMinutes = next;
        }
        catch (Exception ex) { _log($"Enforcer: minutes write failed — {ex.Message}"); }
    }

    void ApplyState(string target, string reason)
    {
        if (_currentState == target) return;
        _currentState = target;
        _log($"Enforcer: → {target} ({reason})");
        try
        {
            if (target == "shutoff") FirewallHelpers.BlockAllConfigured(_config, _apps(), _log);
            else FirewallHelpers.UnblockAll(_config, _log);
        }
        catch (Exception ex) { _log($"Enforcer apply failed: {ex.Message}"); }
    }

    static int ToMin(string hhmm)
    {
        if (string.IsNullOrEmpty(hhmm)) return 0;
        var p = hhmm.Split(':');
        return int.Parse(p[0]) * 60 + int.Parse(p[1]);
    }

    // ---- Firestore field parsing ----

    GameSchedule? ParseSchedule(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("fields", out var f)) return null;
        var sched = new GameSchedule();
        if (f.TryGetProperty("days", out var daysEl) && daysEl.TryGetProperty("mapValue", out var dmv) &&
            dmv.TryGetProperty("fields", out var dFields))
        {
            foreach (var prop in dFields.EnumerateObject())
            {
                if (!prop.Value.TryGetProperty("mapValue", out var mv)) continue;
                if (!mv.TryGetProperty("fields", out var dayFields)) continue;
                var d = new DayRule
                {
                    Enabled = GetBool(dayFields, "enabled"),
                    Mode = GetStr(dayFields, "mode"),
                    Start = GetStr(dayFields, "start") ?? "00:00",
                    End = GetStr(dayFields, "end") ?? "00:00",
                    MaxHours = GetDouble(dayFields, "maxHours"),
                };
                sched.Days[prop.Name] = d;
            }
        }
        return sched;
    }

    static bool GetBool(JsonElement f, string k)
        => f.TryGetProperty(k, out var v) && v.TryGetProperty("booleanValue", out var b) && b.GetBoolean();
    static string? GetStr(JsonElement f, string k)
        => f.TryGetProperty(k, out var v) && v.TryGetProperty("stringValue", out var s) ? s.GetString() : null;
    static double? GetDouble(JsonElement f, string k)
    {
        if (!f.TryGetProperty(k, out var v)) return null;
        if (v.TryGetProperty("doubleValue", out var d)) return d.GetDouble();
        if (v.TryGetProperty("integerValue", out var i) && long.TryParse(i.GetString(), out var n)) return n;
        return null;
    }
}

public class GameSchedule
{
    public Dictionary<string, DayRule> Days { get; } = new();
}

public class DayRule
{
    public bool Enabled { get; set; }
    public string? Mode { get; set; }
    public string Start { get; set; } = "00:00";
    public string End { get; set; } = "00:00";
    public double? MaxHours { get; set; }
}

/** Tiny façade so the enforcer doesn't reach into RemoteSync internals. */
public static class FirewallHelpers
{
    public static void BlockAllConfigured(LocalConfig cfg, List<(string key, string label, string path, bool isLauncher)> apps, Action<string> log)
    {
        foreach (var (key, label, path, _) in apps)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            var s = ConfigStore.GetOrInit(cfg, key);
            if (!s.RemoteEnabled) continue;
            try { FirewallManager.Block(key, path); }
            catch (Exception ex) { log($"Enforcer block {label}: {ex.Message}"); }
        }
    }
    public static void UnblockAll(LocalConfig cfg, Action<string> log)
    {
        foreach (var b in cfg.RemotelyBlocked.ToArray())
        {
            try { FirewallManager.Unblock(b.AppKey, b.ExePath); }
            catch (Exception ex) { log($"Enforcer unblock {b.AppKey}: {ex.Message}"); }
        }
        try { FirewallManager.DisableAllRules(log); } catch { }
    }
}
