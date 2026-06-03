using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ChoreBuddy.TestApp;

public static class EpicScanner
{
    static readonly string ManifestsDir =
        @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

    public static List<InstalledGame> ScanInstalledGames()
    {
        var result = new List<InstalledGame>();
        if (!Directory.Exists(ManifestsDir)) return result;

        foreach (var file in Directory.GetFiles(ManifestsDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                var name = GetString(root, "DisplayName");
                var appName = GetString(root, "AppName");
                var installLocation = GetString(root, "InstallLocation")
                    ?.Replace('/', Path.DirectorySeparatorChar);
                var launchExe = GetString(root, "LaunchExecutable")
                    ?.Replace('/', Path.DirectorySeparatorChar);

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installLocation)) continue;
                if (!Directory.Exists(installLocation)) continue;

                var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(launchExe))
                {
                    var fullLaunch = Path.Combine(installLocation, launchExe);
                    if (File.Exists(fullLaunch)) exes.Add(fullLaunch);
                }

                if (root.TryGetProperty("ProcessNames", out var procs) && procs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in procs.EnumerateArray())
                    {
                        var procName = p.GetString();
                        if (string.IsNullOrEmpty(procName)) continue;
                        var hits = Directory.EnumerateFiles(installLocation, procName, SearchOption.AllDirectories);
                        foreach (var h in hits) exes.Add(h);
                    }
                }

                try
                {
                    foreach (var exe in Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.AllDirectories))
                    {
                        if (IsLikelyGameExe(exe)) exes.Add(exe);
                    }
                }
                catch { }

                result.Add(new InstalledGame("Epic", appName ?? name, name, installLocation, exes.ToList()));
            }
            catch { }
        }

        return result.OrderBy(g => g.Name).ToList();
    }

    static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool IsLikelyGameExe(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.Contains("unins")) return false;
        if (name.Contains("crash")) return false;
        if (name.Contains("setup")) return false;
        if (name.Contains("redist")) return false;
        if (name.Contains("vcredist")) return false;
        if (name.StartsWith("d3d")) return false;
        if (name.Contains("eacrunner")) return false;
        if (name.Contains("easyanticheat")) return false;
        return true;
    }
}
