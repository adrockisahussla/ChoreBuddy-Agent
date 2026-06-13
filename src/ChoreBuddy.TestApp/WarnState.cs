using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoreBuddy.TestApp;

/**
 * One-shot "game time is about to end" warning, written by the service
 * (ScheduleEnforcer) and read by the overlay UI process. Kept in its own
 * file so it never races with / clobbers the blocked-overlay state.
 *
 * The UI shows a toast once per WarnId. The enforcer bumps WarnId only on
 * the rising edge of "≤ 5 min remaining", so the toast fires once per
 * play session, not every 60 s tick.
 */
public class WarnStateData
{
    /** Seconds remaining until cutoff at the moment the warning fired. */
    [JsonPropertyName("warnSeconds")]
    public int WarnSeconds { get; set; }

    /** Unique id per warning episode. 0 = no warning yet. */
    [JsonPropertyName("warnId")]
    public long WarnId { get; set; }

    [JsonPropertyName("kidName")]
    public string KidName { get; set; } = "";
}

public static class WarnState
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChoreBuddy");
    public static readonly string FilePath = Path.Combine(Dir, "warn-state.json");

    static readonly object _lock = new();

    public static WarnStateData Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new WarnStateData();
            string json;
            lock (_lock) { json = File.ReadAllText(FilePath); }
            return JsonSerializer.Deserialize<WarnStateData>(json) ?? new WarnStateData();
        }
        catch { return new WarnStateData(); }
    }

    public static void Write(WarnStateData data)
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
