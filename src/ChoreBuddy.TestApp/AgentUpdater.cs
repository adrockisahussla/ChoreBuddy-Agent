using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChoreBuddy.TestApp;

/// <summary>
/// Self-update for the Windows agent. The running service periodically checks
/// the agent's PUBLIC GitHub releases for a newer version. When one exists it
/// downloads the self-contained build, then hands off to a TEMP COPY of itself
/// (`--apply-update`) — a separate short-lived process that stops the service,
/// swaps the files in the install dir (which is unlocked once the service is
/// stopped), and restarts it. The temp copy is used so the updater isn't the
/// file being overwritten.
/// </summary>
public static class AgentUpdater
{
    // Bump this with every release; the GitHub release tag must match (vX.Y.Z).
    public const string CurrentVersionString = "1.0.13";

    const string Owner = "adrockisahussla";
    const string Repo = "ChoreBuddy-Agent";
    const string AssetName = "ChoreBuddyAgent.zip";
    const string ServiceName = "ChoreBuddyAgent";

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static Version Current => Version.Parse(CurrentVersionString);

    static string UpdateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChoreBuddy", "update");

    /// <summary>
    /// Check GitHub for a newer release; if found, download + stage it and
    /// launch the detached updater. Returns true if an update was started
    /// (the caller should expect the service to be stopped shortly after).
    /// </summary>
    public static async Task<bool> CheckAndUpdateAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            var (latest, assetUrl) = await GetLatestAsync(ct);
            if (latest == null) return false;
            if (latest <= Current) return false;
            if (string.IsNullOrEmpty(assetUrl))
            {
                log($"Update {latest} available but asset '{AssetName}' missing from release");
                return false;
            }

            log($"Update available: {Current} -> {latest}. Downloading…");
            Directory.CreateDirectory(UpdateRoot);
            var zipPath = Path.Combine(UpdateRoot, AssetName);
            var staging = Path.Combine(UpdateRoot, "staging");

            using (var resp = await http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            var newExe = Path.Combine(staging, "ChoreBuddy.TestApp.exe");
            if (!File.Exists(newExe)) { log("Update: staged exe missing, aborting"); return false; }

            var installDir = AppContext.BaseDirectory.TrimEnd('\\');

            // Run the updater straight from the STAGING folder. It has the full
            // build (exe + DLLs + runtimeconfig + deps) so the apphost can
            // actually start — copying just the .exe to a temp dir left it
            // without its managed DLLs, so the runner never launched (the real
            // reason self-update never applied). Running from staging is safe:
            // ApplyUpdate copies staging -> install, so the running runner (in
            // staging) is never the file being overwritten.
            var psi = new ProcessStartInfo
            {
                FileName = newExe,
                WorkingDirectory = staging,
                // false: a session-0 service can't ShellExecute a child.
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(installDir);
            psi.ArgumentList.Add(staging);
            Process.Start(psi);
            log($"Updater launched from staging → service will restart on {latest}.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Update check failed: {ex.Message}");
            return false;
        }
    }

    static async Task<(Version? version, string? assetUrl)> GetLatestAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        req.Headers.UserAgent.ParseAdd("ChoreBuddyAgent");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return (null, null);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(tag)) return (null, null);
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return (null, null);

        string? assetUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase) &&
                    a.TryGetProperty("browser_download_url", out var u))
                {
                    assetUrl = u.GetString();
                    break;
                }
            }
        }
        return (version, assetUrl);
    }

    /// <summary>
    /// Runs inside the temp-copy process (`--apply-update install staging`):
    /// stop the service, swap files, restart. Never call from the service.
    /// </summary>
    public static void ApplyUpdate(string installDir, string stagingDir, Action<string> log)
    {
        log($"--apply-update: install='{installDir}' staging='{stagingDir}'");

        // Pause the watchdog (a 1-min SYSTEM scheduled task that restarts the
        // service whenever it's not Running). If we don't, it relaunches the
        // service mid-swap and re-locks the files, so the copy fails and the
        // version never changes. Also clear SCM restart-on-failure as a belt.
        RunProc("schtasks.exe", "/Change /TN ChoreBuddyAgentWatchdog /DISABLE", log);
        RunSc($"failure {ServiceName} reset= 0 actions= \"\"", log);

        RunSc("stop " + ServiceName, log);
        for (var i = 0; i < 30; i++)
        {
            if (IsStopped()) { log("service stopped"); break; }
            Thread.Sleep(1000);
        }

        foreach (var p in Process.GetProcessesByName("ChoreBuddy.TestApp"))
        {
            try { if (p.Id != Environment.ProcessId) p.Kill(true); } catch { }
        }
        Thread.Sleep(1000);

        CopyDir(stagingDir, installDir, log);

        // Leave a one-shot notice so the overlay shows "updated" once the
        // service restarts and relaunches it. CurrentVersionString here is
        // the NEW build's (this runner is a copy of the freshly-staged exe).
        try
        {
            NoticeState.Write(new NoticeStateData
            {
                NoticeId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Message = $"ChoreBuddy Agent updated to v{CurrentVersionString}",
            });
            log($"update notice queued (v{CurrentVersionString})");
        }
        catch { }

        RunSc("start " + ServiceName, log);

        // Resume the watchdog + restore SCM recovery now the swap is done.
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000", log);
        RunProc("schtasks.exe", "/Change /TN ChoreBuddyAgentWatchdog /ENABLE", log);
        log("--apply-update: done");
    }

    static void CopyDir(string src, string dst, Action<string> log)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { File.Copy(file, target, true); break; }
                catch (Exception ex)
                {
                    if (attempt == 9) log($"copy failed {rel}: {ex.Message}");
                    else Thread.Sleep(500);
                }
            }
        }
    }

    static void RunSc(string args, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
        }
        catch (Exception ex) { log($"sc {args} failed: {ex.Message}"); }
    }

    static void RunProc(string file, string args, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
        }
        catch (Exception ex) { log($"{file} {args} failed: {ex.Message}"); }
    }

    static bool IsStopped()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = "query " + ServiceName,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return outp.Contains("STOPPED");
        }
        catch { return false; }
    }
}
