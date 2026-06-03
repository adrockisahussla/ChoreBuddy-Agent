using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChoreBuddy.TestApp;

public record SignInResult(string ManagerUid, string ManagerName, string Email, string? FamilyId);

/**
 * AuthClient — Google OAuth (Desktop) + Firebase Auth REST sign-in for
 * the agent's setup wizard. After successful sign-in the manager's
 * Firebase refreshToken + uid + familyId are persisted to LocalConfig
 * so the long-running Windows Service can mint fresh idTokens on demand
 * via GetValidIdTokenAsync().
 *
 * Token mint flow (one-time, in the wizard):
 *   1. Pick a free loopback port + start HttpListener.
 *   2. Open OS browser to accounts.google.com/o/oauth2/v2/auth.
 *   3. Catch the redirect at http://localhost:PORT?code=...
 *   4. POST oauth2.googleapis.com/token → Google id_token + refresh_token.
 *   5. POST identitytoolkit.../accounts:signInWithIdp → Firebase
 *      idToken + refreshToken + localId.
 *   6. GET users/{localId} via the new idToken to learn familyId.
 *   7. Persist to LocalConfig.
 *
 * Refresh flow (any time GetValidIdTokenAsync is called when the cached
 * idToken is within 60s of expiry):
 *   POST securetoken.googleapis.com/v1/token → new idToken + refresh.
 *
 * The Google client_id + client_secret are baked into the binary by
 * design — for Desktop OAuth clients the "secret" isn't really secret,
 * PKCE is the real security layer (we include a state nonce + the
 * exact loopback port to defend against CSRF; PKCE itself could be
 * added if Google's desktop client policy requires it later).
 */
public class AuthClient
{
    // Real values live in Secrets.cs (git-ignored). See Secrets.example.cs.
    const string OAuthClientId = Secrets.OAuthClientId;
    const string OAuthClientSecret = Secrets.OAuthClientSecret;
    const string FirebaseApiKey = Secrets.FirebaseApiKey;
    const string FirebaseProjectId = Secrets.FirebaseProjectId;

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    readonly LocalConfig _config;

    public AuthClient(LocalConfig config) { _config = config; }

    public bool IsSignedIn => !string.IsNullOrEmpty(_config.RefreshToken)
                              && !string.IsNullOrEmpty(_config.ManagerUid);

    public string? ManagerName => _config.ManagerName;
    public string? ManagerUid => _config.ManagerUid;
    public string? FamilyId => _config.FamilyId;

    /**
     * Run the full OAuth → Firebase sign-in dance. Opens a browser
     * window; blocks until the manager completes sign-in (or the
     * cancellation token fires). Returns the resulting manager identity.
     */
    public async Task<SignInResult> SignInWithGoogleAsync(CancellationToken ct = default)
    {
        // 1. Bind a loopback HttpListener to a free port.
        var listener = new HttpListener();
        int port = 0;
        Exception? lastBindErr = null;
        for (int candidate = 49152; candidate < 65535; candidate++)
        {
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                listener.Start();
                port = candidate;
                break;
            }
            catch (Exception ex) { lastBindErr = ex; }
        }
        if (port == 0)
            throw new Exception("Could not bind a loopback port for OAuth: " + lastBindErr?.Message);

        try
        {
            var redirectUri = $"http://127.0.0.1:{port}";
            var nonce = Guid.NewGuid().ToString("N");

            // 2. Launch browser to Google's auth endpoint.
            var authUrl =
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(OAuthClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                "&scope=" + Uri.EscapeDataString("openid email profile") +
                "&access_type=offline" +
                "&prompt=consent" +
                $"&state={nonce}";
            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });

            // 3. Wait for redirect. GetContextAsync doesn't accept a CT,
            //    so we race it against the cancellation.
            var getCtx = listener.GetContextAsync();
            var done = await Task.WhenAny(getCtx, Task.Delay(Timeout.Infinite, ct));
            if (done != getCtx) throw new OperationCanceledException(ct);

            var context = await getCtx;
            var code = context.Request.QueryString["code"];
            var returnedState = context.Request.QueryString["state"];
            var error = context.Request.QueryString["error"];

            // 4. Send a friendly close-this-tab page back to the browser.
            string responseHtml;
            if (!string.IsNullOrEmpty(error))
            {
                responseHtml = $"<html><body style='font-family:sans-serif;background:#0f1117;color:#fff;text-align:center;padding-top:80px'>" +
                               $"<h2>✗ Sign-in cancelled</h2><p>{WebUtility.HtmlEncode(error)}</p>" +
                               "<p>You can close this tab and return to the ChoreBuddy Agent setup.</p>" +
                               "</body></html>";
            }
            else
            {
                responseHtml = "<html><body style='font-family:sans-serif;background:#0f1117;color:#fff;text-align:center;padding-top:80px'>" +
                               "<h2>✓ Signed in!</h2><p>You can close this tab and return to the ChoreBuddy Agent setup.</p>" +
                               "</body></html>";
            }
            var bytes = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
            context.Response.Close();

            if (!string.IsNullOrEmpty(error))
                throw new Exception($"OAuth error: {error}");
            if (string.IsNullOrEmpty(code))
                throw new Exception("OAuth redirect missing code parameter");
            if (returnedState != nonce)
                throw new Exception("OAuth state mismatch (possible CSRF) — please retry");

            // 5. Exchange code → Google tokens.
            var tokenResp = await http.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code!,
                    ["client_id"] = OAuthClientId,
                    ["client_secret"] = OAuthClientSecret,
                    ["redirect_uri"] = redirectUri,
                    ["grant_type"] = "authorization_code",
                }), ct);
            var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
            if (!tokenResp.IsSuccessStatusCode)
                throw new Exception($"Google token exchange failed ({(int)tokenResp.StatusCode}): {tokenBody}");
            var tokenJson = JsonNode.Parse(tokenBody)!;
            var googleIdToken = tokenJson["id_token"]?.ToString()
                ?? throw new Exception("Google response missing id_token");

            // 6. Exchange Google id_token → Firebase auth (signInWithIdp).
            var fbBody = JsonSerializer.Serialize(new
            {
                postBody = $"id_token={googleIdToken}&providerId=google.com",
                requestUri = "http://localhost",
                returnSecureToken = true,
            });
            var fbResp = await http.PostAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={FirebaseApiKey}",
                new StringContent(fbBody, Encoding.UTF8, "application/json"), ct);
            var fbText = await fbResp.Content.ReadAsStringAsync(ct);
            if (!fbResp.IsSuccessStatusCode)
                throw new Exception($"Firebase signInWithIdp failed ({(int)fbResp.StatusCode}): {fbText}");
            var fbJson = JsonNode.Parse(fbText)!;
            var fbIdToken = fbJson["idToken"]?.ToString() ?? throw new Exception("Firebase response missing idToken");
            var fbRefresh = fbJson["refreshToken"]?.ToString() ?? throw new Exception("Firebase response missing refreshToken");
            var fbUid = fbJson["localId"]?.ToString() ?? throw new Exception("Firebase response missing localId");
            var fbEmail = fbJson["email"]?.ToString() ?? "";
            var displayName = fbJson["displayName"]?.ToString();
            if (string.IsNullOrWhiteSpace(displayName)) displayName = fbEmail;
            var expiresIn = int.TryParse(fbJson["expiresIn"]?.ToString(), out var ei) ? ei : 3600;

            // 7. Persist before we even read the user doc — we want
            //    GetValidIdTokenAsync to be functional immediately.
            _config.ManagerUid = fbUid;
            _config.ManagerName = displayName;
            _config.RefreshToken = fbRefresh;
            _config.IdToken = fbIdToken;
            _config.IdTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToUnixTimeMilliseconds();
            ConfigStore.Save(_config);

            // 8. Look up the manager's user doc to learn familyId.
            var familyId = await FetchFamilyIdAsync(fbUid, fbIdToken, ct);
            if (!string.IsNullOrEmpty(familyId))
            {
                _config.FamilyId = familyId;
                ConfigStore.Save(_config);
            }

            return new SignInResult(fbUid, displayName!, fbEmail, familyId);
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }

    /**
     * Returns a non-expired Firebase idToken. If the cached one is
     * within the refresh window (60s of expiry), refresh first.
     * Throws InvalidOperationException if not signed in.
     */
    public async Task<string> GetValidIdTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.RefreshToken))
            throw new InvalidOperationException("Agent not signed in — run setup wizard");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrEmpty(_config.IdToken) && now < _config.IdTokenExpiresAt)
            return _config.IdToken!;

        await RefreshAsync(ct);
        return _config.IdToken!;
    }

    /**
     * Force a refresh via securetoken.googleapis.com. Persists new
     * idToken + (rotated) refreshToken + expiry.
     */
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.RefreshToken))
            throw new InvalidOperationException("No refresh token to refresh with");

        var resp = await http.PostAsync(
            $"https://securetoken.googleapis.com/v1/token?key={FirebaseApiKey}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _config.RefreshToken!,
            }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token refresh failed ({(int)resp.StatusCode}): {body}");

        var json = JsonNode.Parse(body)!;
        _config.IdToken = json["id_token"]?.ToString()
            ?? throw new Exception("Refresh response missing id_token");
        var newRefresh = json["refresh_token"]?.ToString();
        if (!string.IsNullOrEmpty(newRefresh)) _config.RefreshToken = newRefresh;
        var expiresIn = int.TryParse(json["expires_in"]?.ToString(), out var ei) ? ei : 3600;
        _config.IdTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToUnixTimeMilliseconds();
        ConfigStore.Save(_config);
    }

    async Task<string?> FetchFamilyIdAsync(string uid, string idToken, CancellationToken ct)
    {
        try
        {
            var url = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/users/{Uri.EscapeDataString(uid)}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("fields", out var fields)) return null;
            if (!fields.TryGetProperty("familyId", out var fEl)) return null;
            if (!fEl.TryGetProperty("stringValue", out var sv)) return null;
            return sv.GetString();
        }
        catch { return null; }
    }

    /** Wipe the auth section of LocalConfig — used by an "unpair" action. */
    public void SignOut()
    {
        _config.ManagerUid = null;
        _config.ManagerName = null;
        _config.FamilyId = null;
        _config.RefreshToken = null;
        _config.IdToken = null;
        _config.IdTokenExpiresAt = 0;
        ConfigStore.Save(_config);
    }
}
