using System.Text.Json;

namespace OpticalBackup.App;

/// <summary>Per-computer preferences. The backup catalog itself lives wherever CatalogPath points.</summary>
public sealed class AppSettings
{
    public string CatalogPath { get; set; } = DefaultCatalogPath;
    public string? LastJob { get; set; }
    public string? OutputDirectory { get; set; }
    public string DiscTypeId { get; set; } = "bd25";
    public int CustomDiscMegabytes { get; set; } = 4400;
    public double RecoveryPercent { get; set; } = 5;
    public bool FolderOutput { get; set; }

    public static string DefaultCatalogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Optical Backup Assistant", "catalog.json");

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpticalBackupAssistant", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
