using BenchForge.App.Platform;
using BenchForge.Core.Ai;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;

namespace BenchForge.App;

/// <summary>App-wide singletons: settings, sensors, results, event log, AI.</summary>
public sealed class Services : IDisposable
{
    public static Services Instance { get; } = new();

    public AppSettings Settings { get; } = AppSettings.Load();
    public ResultStore Store { get; } = new();
    public WindowsEventSource Events { get; } = new();
    public AiService Ai { get; } = new();
    public SensorHub? Sensors { get; private set; }
    public HardwareInventory? Inventory { get; private set; }
    public LhmSensorProvider? Lhm { get; private set; }

    public event Action? SensorsReady;
    public event Action? InventoryReady;

    public void Start()
    {
        Task.Run(() =>
        {
            try
            {
                Lhm = new LhmSensorProvider();
                Sensors = new SensorHub(Lhm);
            }
            catch (Exception ex) { DataPaths.AppLogWrite("Sensors failed: " + ex); }
            SensorsReady?.Invoke();
        });
        Task.Run(() =>
        {
            try { Inventory = WmiInventory.Collect(); }
            catch (Exception ex) { DataPaths.AppLogWrite("Inventory failed: " + ex); Inventory = new HardwareInventory(); }
            InventoryReady?.Invoke();
        });
    }

    /// <summary>AI settings with the decrypted API key filled in.</summary>
    public AiSettings AiSettings()
    {
        var s = Settings.Ai;
        s.ClaudeApiKey = Secrets.Unprotect(Settings.EncryptedApiKey);
        return s;
    }

    public void SaveSettings() => Settings.Save();

    public void Dispose()
    {
        Ai.Dispose();
        Sensors?.Dispose();
    }
}
