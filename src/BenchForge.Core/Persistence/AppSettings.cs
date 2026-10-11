using BenchForge.Core.Model;

namespace BenchForge.Core.Persistence;

public sealed class AppSettings
{
    public TestSettings Test { get; set; } = new();
    /// <summary>Claude API key, encrypted for the current Windows user (DPAPI) — never stored in plain text.</summary>
    public string? EncryptedApiKey { get; set; }
    /// <summary>AI provider settings (API key is kept out of this object when saved; see EncryptedApiKey).</summary>
    public Ai.AiSettings Ai { get; set; } = new();
    public int BurnHours { get; set; } = 1;
    public int BurnMinutes { get; set; }
    public List<Component> BurnComponents { get; set; } = new() { Component.Cpu, Component.Gpu, Component.Memory };
    public string Theme { get; set; } = "Dark";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(DataPaths.SettingsFile))
                return Json.Deserialize<AppSettings>(File.ReadAllText(DataPaths.SettingsFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(DataPaths.SettingsFile, Json.Serialize(this, indented: true)); } catch { }
    }
}
