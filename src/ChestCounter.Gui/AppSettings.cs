using System.IO;
using System.Text.Json;

namespace ChestCounter.Gui;

// Local GUI preferences (window geometry, active view, tray behaviour & in-game HUD), separate from overlay branding.
public sealed class AppSettings
{
    public bool InGameOverlay { get; set; }
    public bool OverlayLocked { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public string OverlayAnchor { get; set; } = "free";
    public int PreviewBackdropIndex { get; set; } = 0; // 0 = Céu Dia, 1 = Neve, 2 = Combate, 3 = Noite

    // Persisted main window bounds & state so user remodeling is remembered across sessions
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public int SelectedPeriodIndex { get; set; } = 2; // Default: Temporada
}

public static class AppConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounter", "app.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
            File.Move(tmp, Path, overwrite: true);
        }
        catch { }
    }
}
