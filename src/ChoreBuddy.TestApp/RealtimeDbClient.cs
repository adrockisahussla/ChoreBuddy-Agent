using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ChoreBuddy.TestApp;

/**
 * RealtimeDbClient — holds ONE persistent HTTP connection open to Firebase
 * Realtime Database and receives pushed changes over Server-Sent Events
 * (the REST streaming protocol: GET …/path.json with Accept:text/event-stream).
 *
 * This replaces the old every-3s Firestore polling for commands. While idle
 * the connection just sits there costing nothing; the instant the phone
 * writes a new command, RTDB pushes it down the wire and onEvent fires.
 *
 * The connection is auto-reconnected (with backoff) on any drop — including
 * the expected drop when the Firebase idToken expires (~1h), at which point
 * we mint a fresh token via AuthClient and reconnect.
 */
public class RealtimeDbClient
{
    readonly string _dbUrl;          // e.g. https://chorebuddy-67a5f-default-rtdb.firebaseio.com
    readonly AuthClient? _auth;

    // Dedicated client with no overall timeout — the stream is meant to stay open.
    static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public RealtimeDbClient(string dbUrl, AuthClient? auth)
    {
        _dbUrl = (dbUrl ?? "").TrimEnd('/');
        _auth = auth;
    }

    /**
     * Open the stream on `path` (e.g. "firewallControl/DESKTOP-X/control")
     * and call onEvent(eventName, dataPayload) for every "put"/"patch"
     * event. dataPayload is the raw JSON envelope, e.g.
     *   {"path":"/","data":{"command":"shutoff","timestamp":123}}
     * Blocks until ct is cancelled; reconnects internally on errors.
     */
    public async Task ListenAsync(string path, Action<string, string> onEvent, Action<string> log, CancellationToken ct)
    {
        var backoffSec = 2.0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string url = $"{_dbUrl}/{path}.json";
                if (_auth != null && _auth.IsSignedIn)
                {
                    var token = await _auth.GetValidIdTokenAsync(ct);
                    url += $"?auth={Uri.EscapeDataString(token)}";
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                log("RTDB stream connected");
                backoffSec = 2.0; // reset backoff on a healthy connection

                string? evName = null;
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break; // server closed the stream → reconnect

                    if (line.StartsWith("event:", StringComparison.Ordinal))
                    {
                        evName = line.Substring(6).Trim();
                        // token expired / access revoked → break to reconnect with a fresh one
                        if (evName is "auth_revoked" or "cancel")
                        {
                            log($"RTDB stream {evName} — reconnecting");
                            break;
                        }
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var data = line.Substring(5).Trim();
                        if ((evName == "put" || evName == "patch") && data != "null")
                            onEvent(evName, data);
                    }
                    // blank line = event terminator; keep-alive events carry no data
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log($"RTDB stream error: {ex.Message}"); }

            try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct); }
            catch { break; }
            backoffSec = Math.Min(30, backoffSec * 2);
        }
    }
}
