using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChoreBuddy.TestApp;

public record RemoteCommand(string? Command, long Timestamp);

public record AppConfigEntry(bool RemoteEnabled, bool KillRelated);

public record AgentSnapshot(
    string? Command,
    long Timestamp,
    Dictionary<string, AppConfigEntry> AppConfig,
    string? KidId);

public record KidSettings(bool OverlayDismissable, string Name, string Avatar);

public record BuddyInfo(string Uid, string DisplayName, string Avatar);

public record InstalledAppInfo(string Key, string Label, string Path, bool IsLauncher, bool Installed);

public class FirestoreClient
{
    const string ProjectId = Secrets.FirebaseProjectId;
    const string ApiKey = Secrets.FirebaseApiKey;

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    readonly AuthClient? _auth;
    readonly LocalConfig? _config;

    /** Auth-aware constructor. When `auth` is null all requests go out
     *  unauthenticated (legacy behavior — only useful pre-pairing). */
    public FirestoreClient(AuthClient? auth = null, LocalConfig? config = null)
    {
        _auth = auth;
        _config = config;
    }

    static string DocUrl(string path)
        => $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{path}?key={ApiKey}";

    static string DocUrlWithMask(string path, params string[] maskFields)
    {
        var maskParams = string.Join("&", Array.ConvertAll(maskFields, f => $"updateMask.fieldPaths={Uri.EscapeDataString(f)}"));
        return $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{path}?key={ApiKey}&{maskParams}";
    }

    /** Attach Authorization: Bearer <idToken> to every outbound request
     *  when AuthClient is present. Safe to call even when not signed in
     *  (skips auth silently — server returns permission-denied which
     *  callers already handle via try/catch). */
    async Task<HttpResponseMessage> SendAuthedAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_auth != null && _auth.IsSignedIn)
        {
            try
            {
                var token = await _auth.GetValidIdTokenAsync(ct);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            catch { /* fall through unauth'd; server will 401 */ }
        }
        var resp = await http.SendAsync(req, ct);
        // One-shot retry on 401: refresh token + retry.
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && _auth != null && _auth.IsSignedIn)
        {
            try
            {
                await _auth.RefreshAsync(ct);
                var retry = await CloneAsync(req);
                var token = await _auth.GetValidIdTokenAsync(ct);
                retry.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                resp.Dispose();
                resp = await http.SendAsync(retry, ct);
            }
            catch { /* return the original 401 */ }
        }
        return resp;
    }

    static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage src)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri);
        if (src.Content != null)
        {
            var ms = new MemoryStream();
            await src.Content.CopyToAsync(ms);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            foreach (var h in src.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        foreach (var h in src.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }

    static JsonObject V(bool b) => new() { ["booleanValue"] = b };
    static JsonObject V(string s) => new() { ["stringValue"] = s };
    static JsonObject V(long n) => new() { ["integerValue"] = n.ToString() };
    static JsonObject MapOf(Action<JsonObject> build)
    {
        var fields = new JsonObject();
        build(fields);
        return new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = fields } };
    }

    public async Task<AgentSnapshot?> GetSnapshotAsync(string machineId, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, DocUrl($"firewallControl/{Uri.EscapeDataString(machineId)}"));
            var resp = await SendAuthedAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new AgentSnapshot(null, 0, new(), null);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("fields", out var fields))
                return new AgentSnapshot(null, 0, new(), null);

            string? command = null;
            long timestamp = 0;
            string? kidId = null;
            var appConfig = new Dictionary<string, AppConfigEntry>();

            if (fields.TryGetProperty("kidId", out var kidEl) &&
                kidEl.TryGetProperty("stringValue", out var kidVal))
                kidId = kidVal.GetString();

            if (fields.TryGetProperty("command", out var cmdEl) &&
                cmdEl.TryGetProperty("stringValue", out var cmdVal))
                command = cmdVal.GetString();

            if (fields.TryGetProperty("timestamp", out var tsEl))
            {
                if (tsEl.TryGetProperty("integerValue", out var tsInt))
                    long.TryParse(tsInt.GetString(), out timestamp);
                else if (tsEl.TryGetProperty("doubleValue", out var tsDbl))
                    timestamp = (long)tsDbl.GetDouble();
            }

            if (fields.TryGetProperty("appConfig", out var cfgEl) &&
                cfgEl.TryGetProperty("mapValue", out var cfgMap) &&
                cfgMap.TryGetProperty("fields", out var cfgFields))
            {
                foreach (var prop in cfgFields.EnumerateObject())
                {
                    if (!prop.Value.TryGetProperty("mapValue", out var entryMap)) continue;
                    if (!entryMap.TryGetProperty("fields", out var entryFields)) continue;

                    bool remoteEnabled = false, killRelated = false;
                    if (entryFields.TryGetProperty("remoteEnabled", out var re) && re.TryGetProperty("booleanValue", out var reVal))
                        remoteEnabled = reVal.GetBoolean();
                    if (entryFields.TryGetProperty("killRelated", out var kr) && kr.TryGetProperty("booleanValue", out var krVal))
                        killRelated = krVal.GetBoolean();
                    appConfig[prop.Name] = new AppConfigEntry(remoteEnabled, killRelated);
                }
            }

            return new AgentSnapshot(command, timestamp, appConfig, kidId);
        }
        catch { return null; }
    }

    public async Task<List<BuddyInfo>> GetBuddiesAsync(CancellationToken ct)
    {
        var result = new List<BuddyInfo>();
        try
        {
            // Scoped via Firestore runQuery so we only fetch the manager's
            // own family. Falls back to /users collection list when no
            // familyId is set (e.g. pre-sign-in legacy path).
            string? familyId = _auth?.FamilyId ?? _config?.FamilyId;

            HttpRequestMessage req;
            if (!string.IsNullOrEmpty(familyId))
            {
                var queryBody = JsonSerializer.Serialize(new
                {
                    structuredQuery = new
                    {
                        from = new[] { new { collectionId = "users" } },
                        where = new
                        {
                            fieldFilter = new
                            {
                                field = new { fieldPath = "familyId" },
                                op = "EQUAL",
                                value = new { stringValue = familyId },
                            }
                        }
                    }
                });
                req = new HttpRequestMessage(HttpMethod.Post,
                    $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents:runQuery?key={ApiKey}")
                {
                    Content = new StringContent(queryBody, Encoding.UTF8, "application/json"),
                };
            }
            else
            {
                req = new HttpRequestMessage(HttpMethod.Get, DocUrl("users"));
            }

            var resp = await SendAuthedAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return result;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // runQuery returns an array of { document: { name, fields, ... } } envelopes.
            // The list-collection endpoint returns { documents: [...] }.
            IEnumerable<JsonElement> rows;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                rows = doc.RootElement.EnumerateArray()
                    .Where(e => e.TryGetProperty("document", out _))
                    .Select(e => e.GetProperty("document"));
            else if (doc.RootElement.TryGetProperty("documents", out var docs))
                rows = docs.EnumerateArray();
            else
                return result;

            foreach (var d in rows)
            {
                if (!d.TryGetProperty("fields", out var fields)) continue;
                var role = GetStr(fields, "role");
                if (!string.Equals(role, "buddy", StringComparison.OrdinalIgnoreCase)) continue;

                var uid = "";
                if (d.TryGetProperty("name", out var nameEl))
                {
                    var fullPath = nameEl.GetString() ?? "";
                    var idx = fullPath.LastIndexOf('/');
                    if (idx >= 0) uid = fullPath[(idx + 1)..];
                }
                var displayName = GetStr(fields, "displayName");
                var avatar = GetStr(fields, "avatar");
                if (string.IsNullOrEmpty(displayName)) displayName = "Buddy";
                if (string.IsNullOrEmpty(avatar)) avatar = "👤";

                result.Add(new BuddyInfo(uid, displayName, avatar));
            }
        }
        catch { }
        return result;
    }

    static string GetStr(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var el)) return "";
        if (!el.TryGetProperty("stringValue", out var v)) return "";
        return v.GetString() ?? "";
    }

    public async Task<KidSettings?> GetKidSettingsAsync(string kidId, CancellationToken ct)
    {
        bool dismissable = true;
        string name = kidId;
        string avatar = "👤";

        // 1. Read kids/{kidId} for dismissable + (optional) name override
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, DocUrl($"kids/{Uri.EscapeDataString(kidId)}"));
            var resp = await SendAuthedAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("fields", out var fields))
                {
                    if (fields.TryGetProperty("overlayDismissable", out var de) && de.TryGetProperty("booleanValue", out var dv))
                        dismissable = dv.GetBoolean();
                    if (fields.TryGetProperty("name", out var ne) && ne.TryGetProperty("stringValue", out var nv))
                        name = nv.GetString() ?? kidId;
                    if (fields.TryGetProperty("avatar", out var ae) && ae.TryGetProperty("stringValue", out var av))
                        avatar = av.GetString() ?? "👤";
                }
            }
        }
        catch { }

        // 2. If name still looks like a Firebase Auth uid, look up users/{kidId} for displayName
        if (name == kidId && IsLikelyUid(kidId))
        {
            try
            {
                var userReq = new HttpRequestMessage(HttpMethod.Get, DocUrl($"users/{Uri.EscapeDataString(kidId)}"));
                var userResp = await SendAuthedAsync(userReq, ct);
                if (userResp.IsSuccessStatusCode)
                {
                    var json = await userResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("fields", out var fields))
                    {
                        var dn = GetStr(fields, "displayName");
                        if (!string.IsNullOrEmpty(dn)) name = dn;
                        var av = GetStr(fields, "avatar");
                        if (!string.IsNullOrEmpty(av)) avatar = av;
                    }
                }
            }
            catch { }
        }

        return new KidSettings(dismissable, name, avatar);
    }

    static bool IsLikelyUid(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Length < 16) return false;
        if (s.StartsWith("Kid", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public async Task RegisterInstalledAppsAsync(string machineId, List<InstalledAppInfo> apps, CancellationToken ct)
    {
        try
        {
            var fields = new JsonObject();
            fields["machineName"] = V(machineId);
            fields["lastSeenAt"] = V(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            fields["installedApps"] = MapOf(map =>
            {
                foreach (var a in apps)
                {
                    map[a.Key] = MapOf(entry =>
                    {
                        entry["label"] = V(a.Label);
                        entry["path"] = V(a.Path);
                        entry["isLauncher"] = V(a.IsLauncher);
                        entry["installed"] = V(a.Installed);
                    });
                }
            });

            var payload = new JsonObject { ["fields"] = fields };
            var url = DocUrlWithMask($"firewallControl/{Uri.EscapeDataString(machineId)}",
                "machineName", "lastSeenAt", "installedApps");
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            await SendAuthedAsync(req, ct);
        }
        catch { }
    }

    public async Task HeartbeatAsync(string machineId, CancellationToken ct)
    {
        try
        {
            var fields = new JsonObject();
            fields["machineName"] = V(machineId);
            fields["lastSeenAt"] = V(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var payload = new JsonObject { ["fields"] = fields };
            var url = DocUrlWithMask($"firewallControl/{Uri.EscapeDataString(machineId)}",
                "machineName", "lastSeenAt");
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            await SendAuthedAsync(req, ct);
        }
        catch { }
    }

    public async Task PushAppConfigAsync(string machineId, string appKey, AppConfigEntry settings, CancellationToken ct)
    {
        try
        {
            var payload = new JsonObject
            {
                ["fields"] = new JsonObject
                {
                    ["appConfig"] = MapOf(appCfg =>
                    {
                        appCfg[appKey] = MapOf(entry =>
                        {
                            entry["remoteEnabled"] = V(settings.RemoteEnabled);
                            entry["killRelated"] = V(settings.KillRelated);
                        });
                    })
                }
            };

            var url = DocUrlWithMask($"firewallControl/{Uri.EscapeDataString(machineId)}",
                $"appConfig.{appKey}");
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            var resp = await SendAuthedAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new Exception($"Firestore PATCH returned {(int)resp.StatusCode}: {body}");
            }
        }
        catch { }
    }
}
