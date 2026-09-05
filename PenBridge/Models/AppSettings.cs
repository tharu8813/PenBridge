using System.Text.Json;

namespace PenBridge.Models;

public enum MappingMode { Stretch, PreserveAspectRatio }

/// <summary>Persisted user choices — everything except the pairing token, which is
/// Web assets and access credentials are intentionally not stored here.</summary>
public sealed class AppSettings
{
    public string? NetworkAddress { get; set; }
    public int Port { get; set; } = 8080;
    public string? MonitorDeviceName { get; set; }
    public MappingMode MappingMode { get; set; } = MappingMode.PreserveAspectRatio;
    public bool AllowNonPenInput { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenBridge", "settings.json");

    /// <summary>A missing or corrupt settings file must never block startup — falls back to defaults.</summary>
    public static AppSettings Load()
    {
        try
        {
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence — losing a settings write is not worth crashing over.
        }
    }
}
