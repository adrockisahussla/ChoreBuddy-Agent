using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoreBuddy.TestApp;

/**
 * Live "game time left + banked minutes" snapshot for the Game Time window.
 *
 * Written every evaluation tick by the service (ScheduleEnforcer, running as
 * LocalSystem) and read by GameTimeForm, which runs in the kid's session and
 * therefore has NO Firebase auth of its own — it relies entirely on this file
 * the service produces. Spending flows the other way, through ExtendRequest,
 * which the service consumes (it has the auth token to debit the wallet).
 */
public class GameTimeStateData
{
    /** Are games allowed right now? */
    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }

    /** Seconds left in the current allow session. -1 = no fixed limit
     *  (manual allow / no schedule). 0 = locked. */
    [JsonPropertyName("secondsRemaining")]
    public int SecondsRemaining { get; set; } = -1;

    /** Banked screen-time minutes the kid can spend (users.minutesRemaining).
     *  Banking happens in the app; the desktop only spends. */
    [JsonPropertyName("minutesBanked")]
    public int MinutesBanked { get; set; }

    [JsonPropertyName("kidName")]
    public string KidName { get; set; } = "";

    /** "window" | "cap" | "bonus" | "manual" | "off" | "none" — status line. */
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    /** Epoch ms this snapshot was written. The UI interpolates the countdown
     *  from here so it ticks smoothly between the service's 60 s writes. */
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

public static class GameTimeStatus
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChoreBuddy");
    public static readonly string FilePath = Path.Combine(Dir, "gametime-state.json");
    static readonly object _lock = new();

    public static GameTimeStateData Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new GameTimeStateData();
            string json;
            lock (_lock) { json = File.ReadAllText(FilePath); }
            return JsonSerializer.Deserialize<GameTimeStateData>(json) ?? new GameTimeStateData();
        }
        catch { return new GameTimeStateData(); }
    }

    public static void Write(GameTimeStateData data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            lock (_lock) { File.WriteAllText(FilePath, json); }
        }
        catch { }
    }
}
