using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoreBuddy.TestApp;

public class OverlayStateData
{
    [JsonPropertyName("blocked")]
    public bool Blocked { get; set; }

    [JsonPropertyName("dismissable")]
    public bool Dismissable { get; set; } = true;

    [JsonPropertyName("kidName")]
    public string KidName { get; set; } = "";

    [JsonPropertyName("kidAvatar")]
    public string KidAvatar { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("acknowledged")]
    public bool Acknowledged { get; set; }
}

public static class OverlayState
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChoreBuddy");
    public static readonly string FilePath = Path.Combine(Dir, "overlay-state.json");

    static readonly object _lock = new();

    public static OverlayStateData Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new OverlayStateData();
            string json;
            lock (_lock) { json = File.ReadAllText(FilePath); }
            return JsonSerializer.Deserialize<OverlayStateData>(json) ?? new OverlayStateData();
        }
        catch { return new OverlayStateData(); }
    }

    public static void Write(OverlayStateData data)
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
