using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ChoreBuddy.TestApp;

public class AppSettings
{
    public bool RemoteEnabled { get; set; }
    public bool KillRelated { get; set; }
}

public class BlockedRecord
{
    public string AppKey { get; set; } = "";
    public string ExePath { get; set; } = "";
}

public class LocalConfig
{
    public string MachineId { get; set; } = Environment.MachineName;
    public string? KidId { get; set; }
    public Dictionary<string, AppSettings> Apps { get; set; } = new();
    public long LastCommandTimestamp { get; set; }
    public long LastRescanAt { get; set; }
    public List<BlockedRecord> RemotelyBlocked { get; set; } = new();

    /** Firebase Realtime Database URL for the push command channel.
     *  Defaults to the project's US instance; override here if RTDB was
     *  created in another region (e.g. ...europe-west1.firebasedatabase.app). */
    public string RealtimeDbUrl { get; set; } = "https://chorebuddy-67a5f-default-rtdb.firebaseio.com";

    // --- Auth (added v2: Google sign-in by manager) ---
    /** Firebase Auth uid of the manager who set up this PC. */
    public string? ManagerUid { get; set; }
    /** displayName of the manager (for setup-wizard UI / logs). */
    public string? ManagerName { get; set; }
    /** familyId resolved from the manager's user doc — used to scope
     *  Firestore reads/writes and to filter buddies in the wizard. */
    public string? FamilyId { get; set; }
    /** Long-lived Firebase refresh token. Used to mint fresh idTokens. */
    public string? RefreshToken { get; set; }
    /** Short-lived (1h) Firebase idToken — cached so we don't refresh
     *  on every Firestore call. */
    public string? IdToken { get; set; }
    /** Epoch ms when IdToken expires. AuthClient refreshes a minute early. */
    public long IdTokenExpiresAt { get; set; }
}

public static class ConfigStore
{
    // Machine-wide location so the Windows Service (LocalSystem) and the UI (current user)
    // both read/write the same config.
    static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChoreBuddy");
    static readonly string ConfigFile = Path.Combine(ConfigDir, "agent-config.json");

    public static LocalConfig Load()
    {
        if (!File.Exists(ConfigFile))
            return new LocalConfig { MachineId = Environment.MachineName };
        try
        {
            var json = File.ReadAllText(ConfigFile);
            var cfg = JsonSerializer.Deserialize<LocalConfig>(json) ?? new LocalConfig();
            if (string.IsNullOrEmpty(cfg.MachineId)) cfg.MachineId = Environment.MachineName;
            return cfg;
        }
        catch
        {
            return new LocalConfig { MachineId = Environment.MachineName };
        }
    }

    public static void Save(LocalConfig cfg)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFile, json);
    }

    public static AppSettings GetOrInit(LocalConfig cfg, string appKey)
    {
        if (!cfg.Apps.TryGetValue(appKey, out var s))
        {
            s = new AppSettings();
            cfg.Apps[appKey] = s;
        }
        return s;
    }
}
