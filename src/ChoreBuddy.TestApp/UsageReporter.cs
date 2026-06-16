using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChoreBuddy.TestApp;

/// <summary>
/// UsageReporter — desktop telemetry for the manager dashboard. Watches
/// the managed game processes and reports, per buddy + machine + day:
///
///   deviceUsage/{kidId}_{machine}_{YYYYMMDD} = {
///     familyId, kidId, machine, date, playedMs, lockEvents, lastSeenAt,
///     games: { [gameLabel]: ms }
///   }
///
/// plus one `gameSessions` doc per finished play session (launch → exit)
/// that powers the dashboard's "what ran when" timeline.
///
/// Runs inside the Windows service (session 0), so it keys off PROCESS
/// PRESENCE — visible across sessions — not the foreground window, which a
/// session-0 service cannot read. A session = a managed game's process
/// being alive. Writes go in as the paired manager (same identity the
/// firewall control uses), so the deviceUsage/gameSessions rules accept them.
/// </summary>
public class UsageReporter
{
    const string ProjectId = "chorebuddy-67a5f";
    const int TickSeconds = 20;
    const long MaxTickDelta = 5 * 60_000; // ignore deltas after sleep/pause

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    readonly LocalConfig _config;
    readonly AuthClient? _auth;
    readonly Func<List<(string key, string label, string path, bool isLauncher)>> _apps;
    readonly Action<string> _log;
    CancellationTokenSource? _cts;

    // Lock events recorded by the enforcer between flushes.
    static int _pendingLocks;
    public static void NoteLock() => Interlocked.Increment(ref _pendingLocks);

    // In-memory daily accumulators (seeded from Firestore on start / rollover).
    string _day = "";
    long _playedMs;
    long _lockEvents;
    readonly Dictionary<string, long> _gameMs = new();
    // Open sessions: appKey -> (label, startedAt epoch ms).
    readonly Dictionary<string, (string label, long startedAt)> _open = new();
    bool _dirty;

    public UsageReporter(
        LocalConfig config, AuthClient? auth,
        Func<List<(string key, string label, string path, bool isLauncher)>> apps,
        Action<string> log)
    { _config = config; _auth = auth; _apps = apps; _log = log; }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
        _log("Usage reporter started");
    }
    public void Stop() { _cts?.Cancel(); _cts = null; }

    static string DayKey(DateTime d) => d.ToString("yyyyMMdd");
    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    string DocId() => $"{_config.KidId}_{_config.MachineId}_{_day}".Replace('/', '_');

    async Task Loop(CancellationToken ct)
    {
        _day = DayKey(DateTime.Now);
        await SeedAsync(ct);
        long lastTick = NowMs();
        long lastFlush = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = NowMs();
                var delta = now - lastTick; lastTick = now;

                bool ready = !string.IsNullOrEmpty(_config.KidId)
                    && !string.IsNullOrEmpty(_config.FamilyId)
                    && _auth != null && _auth.IsSignedIn;

                if (ready)
                {
                    var today = DayKey(DateTime.Now);
                    if (today != _day) await RolloverAsync(today, now, ct);

                    var finished = Sample(delta, now);
                    foreach (var s in finished) await PostSessionAsync(s.label, s.startedAt, s.endedAt, ct);

                    var locks = Interlocked.Exchange(ref _pendingLocks, 0);
                    if (locks > 0) { _lockEvents += locks; _dirty = true; }

                    if (_dirty && now - lastFlush >= 55_000) { await FlushAsync(ct); lastFlush = now; }
                }
            }
            catch (Exception ex) { _log($"Usage tick error: {ex.Message}"); }

            try { await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct); } catch { break; }
        }

        // graceful shutdown: close + flush
        try
        {
            var now = NowMs();
            var finished = CloseAll(now);
            foreach (var s in finished) await PostSessionAsync(s.label, s.startedAt, s.endedAt, CancellationToken.None);
            if (_dirty) await FlushAsync(CancellationToken.None);
        }
        catch { }
    }

    // Detect which managed games are running; accrue time; open/close sessions.
    List<(string label, long startedAt, long endedAt)> Sample(long delta, long now)
    {
        var finished = new List<(string, long, long)>();
        List<(string key, string label, string path, bool isLauncher)> apps;
        try { apps = _apps(); } catch { return finished; }
        var labelByKey = new Dictionary<string, string>();
        var running = new HashSet<string>();
        foreach (var a in apps)
        {
            labelByKey[a.key] = a.label;
            try
            {
                var name = Path.GetFileNameWithoutExtension(a.path);
                if (!string.IsNullOrEmpty(name) && Process.GetProcessesByName(name).Length > 0)
                    running.Add(a.key);
            }
            catch { }
        }

        bool counted = delta > 0 && delta < MaxTickDelta;
        foreach (var key in running)
        {
            var label = labelByKey.TryGetValue(key, out var l) ? l : key;
            if (!_open.ContainsKey(key)) { _open[key] = (label, now); _log($"Game start: {label}"); }
            if (counted) { _gameMs[label] = (_gameMs.TryGetValue(label, out var v) ? v : 0) + delta; _dirty = true; }
        }
        foreach (var key in _open.Keys.ToList())
        {
            if (!running.Contains(key))
            {
                var s = _open[key]; _open.Remove(key);
                finished.Add((s.label, s.startedAt, now));
                _log($"Game stop: {s.label} ({(now - s.startedAt) / 1000}s)");
            }
        }
        if (running.Count > 0 && counted) { _playedMs += delta; _dirty = true; }
        return finished;
    }

    List<(string label, long startedAt, long endedAt)> CloseAll(long now)
    {
        var f = new List<(string, long, long)>();
        foreach (var kv in _open) f.Add((kv.Value.label, kv.Value.startedAt, now));
        _open.Clear();
        return f;
    }

    async Task RolloverAsync(string newDay, long now, CancellationToken ct)
    {
        var finished = CloseAll(now);
        foreach (var s in finished) await PostSessionAsync(s.label, s.startedAt, now, ct);
        if (_dirty) await FlushAsync(ct);
        _day = newDay; _playedMs = 0; _lockEvents = 0; _gameMs.Clear(); _dirty = false;
        await SeedAsync(ct);
        // re-open any still-running games under the new day
        Sample(0, now);
    }

    async Task AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_auth != null && _auth.IsSignedIn)
        {
            var token = await _auth.GetValidIdTokenAsync(ct);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    async Task SeedAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/deviceUsage/{Uri.EscapeDataString(DocId())}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            await AuthorizeAsync(req, ct);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return; // 404 = fresh day
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("fields", out var f)) return;
            _playedMs = ReadInt(f, "playedMs");
            _lockEvents = ReadInt(f, "lockEvents");
            if (f.TryGetProperty("games", out var g) && g.TryGetProperty("mapValue", out var mv)
                && mv.TryGetProperty("fields", out var gf))
            {
                foreach (var p in gf.EnumerateObject())
                    if (p.Value.TryGetProperty("integerValue", out var iv) && long.TryParse(iv.GetString(), out var n))
                        _gameMs[p.Name] = n;
            }
            _log($"Usage seeded: {_playedMs / 60000}m played, {_lockEvents} locks");
        }
        catch (Exception ex) { _log($"Usage seed failed: {ex.Message}"); }
    }

    static long ReadInt(JsonElement fields, string name)
        => fields.TryGetProperty(name, out var el) && el.TryGetProperty("integerValue", out var iv)
           && long.TryParse(iv.GetString(), out var n) ? n : 0;

    async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            var games = new JsonObject();
            foreach (var kv in _gameMs) games[kv.Key] = new JsonObject { ["integerValue"] = kv.Value.ToString() };
            var fields = new JsonObject
            {
                ["familyId"] = new JsonObject { ["stringValue"] = _config.FamilyId },
                ["kidId"] = new JsonObject { ["stringValue"] = _config.KidId },
                ["machine"] = new JsonObject { ["stringValue"] = _config.MachineId },
                ["date"] = new JsonObject { ["stringValue"] = _day },
                ["playedMs"] = new JsonObject { ["integerValue"] = _playedMs.ToString() },
                ["lockEvents"] = new JsonObject { ["integerValue"] = _lockEvents.ToString() },
                ["lastSeenAt"] = new JsonObject { ["integerValue"] = NowMs().ToString() },
                ["games"] = new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = games } },
            };
            var bodyJson = new JsonObject { ["fields"] = fields }.ToJsonString();

            var mask = string.Join("&", new[] { "familyId", "kidId", "machine", "date", "playedMs", "lockEvents", "lastSeenAt", "games" }
                .Select(p => $"updateMask.fieldPaths={p}"));
            var url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/deviceUsage/{Uri.EscapeDataString(DocId())}?{mask}";
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
            await AuthorizeAsync(req, ct);
            var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) _dirty = false;
            else _log($"Usage flush {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        }
        catch (Exception ex) { _log($"Usage flush failed: {ex.Message}"); }
    }

    async Task PostSessionAsync(string label, long startedAt, long endedAt, CancellationToken ct)
    {
        try
        {
            if (endedAt - startedAt < 5_000) return; // ignore blips < 5s
            var fields = new JsonObject
            {
                ["familyId"] = new JsonObject { ["stringValue"] = _config.FamilyId },
                ["kidId"] = new JsonObject { ["stringValue"] = _config.KidId },
                ["machine"] = new JsonObject { ["stringValue"] = _config.MachineId },
                ["game"] = new JsonObject { ["stringValue"] = label },
                ["startedAt"] = new JsonObject { ["integerValue"] = startedAt.ToString() },
                ["endedAt"] = new JsonObject { ["integerValue"] = endedAt.ToString() },
                ["durationMs"] = new JsonObject { ["integerValue"] = (endedAt - startedAt).ToString() },
                ["date"] = new JsonObject { ["stringValue"] = _day },
            };
            var bodyJson = new JsonObject { ["fields"] = fields }.ToJsonString();
            var url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/gameSessions";
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
            await AuthorizeAsync(req, ct);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) _log($"Session post {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        }
        catch (Exception ex) { _log($"Session post failed: {ex.Message}"); }
    }
}
