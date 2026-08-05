using System.Text.Json;

namespace FetchPkg.Win;

internal sealed class AppSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");

    public int MaxConnections { get; set; } = 8;
    public int SegmentSizeMiB { get; set; } = 16;
    public bool UseSparseFiles { get; set; } = true;
    public bool VerifyHashes { get; set; } = true;
    public bool IgnoreSslErrors { get; set; }
    public string ProxyUrl { get; set; } = string.Empty;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Settings persistence must never interrupt downloads or application shutdown.
        }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FetchPkg.Win",
        "settings.json");
}
