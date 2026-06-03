using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ChoreBuddy.TestApp;

public record KnownApp(string Key, string Label, string Path, bool IsLauncher)
{
    public bool Installed => !string.IsNullOrEmpty(Path) && File.Exists(Path);
}

public static class KnownApps
{
    public static List<KnownApp> All() => new()
    {
        new("Steam",     "Steam",                @"C:\Program Files (x86)\Steam\steam.exe", true),
        new("Epic",      "Epic Games Launcher",  @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", true),
        new("Discord",   "Discord",              FindDiscord() ?? "", false),
        new("Roblox",    "Roblox",               FindRoblox() ?? "", false),
        new("Minecraft", "Minecraft",            FindMinecraft() ?? "", false),
        new("Chrome",    "Google Chrome",        @"C:\Program Files\Google\Chrome\Application\chrome.exe", false),
        new("Edge",      "Microsoft Edge",       @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", false),
        new("Firefox",   "Firefox",              @"C:\Program Files\Mozilla Firefox\firefox.exe", false),
    };

    public static string? FindDiscord()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return FindDiscordIn(local) ?? FindDiscordAcrossUsers();
        }
        catch { return null; }
    }

    static string? FindDiscordIn(string localAppData)
    {
        var dir = Path.Combine(localAppData, "Discord");
        if (!Directory.Exists(dir)) return null;
        var appDir = Directory.GetDirectories(dir, "app-*").OrderByDescending(d => d).FirstOrDefault();
        if (appDir == null) return null;
        var exe = Path.Combine(appDir, "Discord.exe");
        return File.Exists(exe) ? exe : null;
    }

    static string? FindDiscordAcrossUsers()
    {
        try
        {
            var usersRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".."));
            if (!Directory.Exists(usersRoot)) return null;
            foreach (var u in Directory.EnumerateDirectories(usersRoot))
            {
                var p = FindDiscordIn(Path.Combine(u, "AppData", "Local"));
                if (p != null) return p;
            }
        }
        catch { }
        return null;
    }

    public static string? FindRoblox()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return FindRobloxIn(local) ?? FindRobloxAcrossUsers();
        }
        catch { return null; }
    }

    static string? FindRobloxIn(string localAppData)
    {
        var dir = Path.Combine(localAppData, "Roblox", "Versions");
        if (!Directory.Exists(dir)) return null;
        foreach (var v in Directory.EnumerateDirectories(dir).OrderByDescending(d => d))
        {
            foreach (var n in new[] { "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe" })
            {
                var exe = Path.Combine(v, n);
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    static string? FindRobloxAcrossUsers()
    {
        try
        {
            var usersRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".."));
            if (!Directory.Exists(usersRoot)) return null;
            foreach (var u in Directory.EnumerateDirectories(usersRoot))
            {
                var p = FindRobloxIn(Path.Combine(u, "AppData", "Local"));
                if (p != null) return p;
            }
        }
        catch { }
        return null;
    }

    public static string? FindMinecraft()
    {
        try
        {
            var candidates = new List<string>
            {
                @"C:\Program Files (x86)\Minecraft Launcher\MinecraftLauncher.exe",
                @"C:\Program Files\Minecraft Launcher\MinecraftLauncher.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Minecraft Launcher", "MinecraftLauncher.exe"),
            };
            return candidates.FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }
}
