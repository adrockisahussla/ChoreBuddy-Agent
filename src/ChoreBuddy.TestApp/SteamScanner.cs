using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ChoreBuddy.TestApp;

public record InstalledGame(string Source, string AppId, string Name, string InstallPath, List<string> Executables);

public static class SteamScanner
{
    public static string? GetSteamInstallPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                       ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
        return key?.GetValue("InstallPath") as string;
    }

    public static List<string> GetLibraryFolders(string steamPath)
    {
        var libs = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return libs;

        var content = File.ReadAllText(vdf);
        foreach (Match m in Regex.Matches(content, @"""path""\s*""([^""]+)"""))
        {
            var p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(p) && !libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                libs.Add(p);
        }
        return libs;
    }

    public static List<InstalledGame> ScanInstalledGames()
    {
        var result = new List<InstalledGame>();
        var steam = GetSteamInstallPath();
        if (steam == null) return result;

        foreach (var lib in GetLibraryFolders(steam))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var manifest in Directory.GetFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var content = File.ReadAllText(manifest);
                    var appid = Match(content, @"""appid""\s*""(\d+)""");
                    var name = Match(content, @"""name""\s*""([^""]+)""");
                    var installdir = Match(content, @"""installdir""\s*""([^""]+)""");

                    if (string.IsNullOrEmpty(installdir)) continue;

                    var gamePath = Path.Combine(steamapps, "common", installdir);
                    var exes = new List<string>();
                    if (Directory.Exists(gamePath))
                    {
                        exes = Directory.EnumerateFiles(gamePath, "*.exe", SearchOption.AllDirectories)
                            .Where(IsLikelyGameExe)
                            .ToList();
                    }

                    result.Add(new InstalledGame("Steam", appid, name, gamePath, exes));
                }
                catch { }
            }
        }
        return result.OrderBy(g => g.Name).ToList();
    }

    static bool IsLikelyGameExe(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.Contains("unins")) return false;
        if (name.Contains("crash")) return false;
        if (name.Contains("setup")) return false;
        if (name.Contains("redist")) return false;
        if (name.Contains("vcredist")) return false;
        if (name.StartsWith("d3d")) return false;
        return true;
    }

    static string Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : "";
    }
}
