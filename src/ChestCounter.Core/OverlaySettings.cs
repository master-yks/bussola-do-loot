using System.Text.Json;

namespace ChestCounter;

public sealed record OverlayPresetInfo(
    string Id,
    string Name,
    string Subtitle,
    string SwatchHex,
    string SecondaryHex,
    Func<OverlaySettings> Create);

// Customizable in-game & OBS HUD configuration with Fortnite-aesthetic presets and granular controls.
public sealed class OverlaySettings
{
    public const int CurrentSchemaVersion = 2;
    public const string DefaultAccent = "#F5B942";
    public const string DefaultPresetId = "fortnite_tactical";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string PresetId { get; set; } = DefaultPresetId;

    // Identity & Colors
    public string Title { get; set; } = "Bússola do Loot";
    public string Accent { get; set; } = DefaultAccent;
    public string SecondaryAccent { get; set; } = "#4CC9F0";
    public string BackgroundHex { get; set; } = "#0A0F1A";
    public string TextHex { get; set; } = "#F8FAFC";

    // Geometry & Typography
    // Layout: "tactical" | "pill" | "micro" | "vertical" | "card"
    public string Layout { get; set; } = "tactical";
    // Chrome: "angled" | "capsule" | "glass" | "sharp" | "none"
    public string Chrome { get; set; } = "angled";
    // FontStyle: "fortnite" | "modern" | "mono"
    public string FontStyle { get; set; } = "fortnite";

    // Transparency, Visibility & Sizing
    public double BgOpacity { get; set; } = 0.34;
    public double HudOpacity { get; set; } = 0.98;
    public double Scale { get; set; } = 1.00;
    public double TextStroke { get; set; } = 1.65;
    public double BorderStrength { get; set; } = 0.36;
    public bool TextShadow { get; set; } = true;
    public bool AccentBar { get; set; } = true;

    // Visible Metrics & Behavior
    public bool ShowIcon { get; set; } = true;
    public bool ShowLive { get; set; } = true;
    public bool ShowSeason { get; set; } = true;
    public bool ShowToken { get; set; } = true;
    public bool ShowAvg { get; set; } = false;
    public bool ShowLast { get; set; } = false;
    public bool ShowBest { get; set; } = false;
    public bool ShowTreasureHour { get; set; } = false;
    public bool ShowHeader { get; set; } = false;
    public bool ShowLabels { get; set; } = true;

    public bool PulseOnChest { get; set; } = true;
    public bool DimWhenIdle { get; set; } = true;
    public bool HideWhenIdle { get; set; } = false;
    public bool SyncObsStyle { get; set; } = true;

    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();

    public void Normalize()
    {
        BgOpacity = Math.Clamp(double.IsFinite(BgOpacity) ? BgOpacity : 0.34, 0.0, 1.0);
        HudOpacity = Math.Clamp(double.IsFinite(HudOpacity) ? HudOpacity : 0.98, 0.25, 1.0);
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1.0, 0.65, 1.65);
        TextStroke = Math.Clamp(double.IsFinite(TextStroke) ? TextStroke : 1.65, 0.0, 3.2);
        BorderStrength = Math.Clamp(double.IsFinite(BorderStrength) ? BorderStrength : 0.36, 0.0, 1.0);

        if (Layout is not ("tactical" or "pill" or "micro" or "vertical" or "card"))
            Layout = "tactical";
        if (Chrome is not ("angled" or "capsule" or "glass" or "sharp" or "none"))
            Chrome = "angled";
        if (FontStyle is not ("fortnite" or "modern" or "mono"))
            FontStyle = "fortnite";

        if (string.IsNullOrWhiteSpace(Accent)) Accent = DefaultAccent;
        if (string.IsNullOrWhiteSpace(SecondaryAccent)) SecondaryAccent = "#4CC9F0";
        if (string.IsNullOrWhiteSpace(BackgroundHex)) BackgroundHex = "#0A0F1A";
        if (string.IsNullOrWhiteSpace(TextHex)) TextHex = "#F8FAFC";
        if (string.IsNullOrWhiteSpace(Title)) Title = "Bússola do Loot";

        // Guarantee at least one primary piece of information stays visible so the HUD never turns completely empty.
        if (!ShowLive && !ShowSeason && !ShowToken && !ShowAvg && !ShowLast && !ShowBest && !ShowTreasureHour)
            ShowLive = true;
    }
}

public static class OverlayPresets
{
    public static readonly IReadOnlyList<OverlayPresetInfo> All =
    [
        new(
            "fortnite_tactical",
            "⚡ Fortnite Tático",
            "Inclinado 34% vidro · encaixe nativo no HUD",
            "#F5B942",
            "#4CC9F0",
            () => new OverlaySettings
            {
                PresetId = "fortnite_tactical",
                Layout = "tactical",
                Chrome = "angled",
                FontStyle = "fortnite",
                Accent = "#F5B942",
                SecondaryAccent = "#4CC9F0",
                BackgroundHex = "#0A0F1A",
                TextHex = "#F8FAFC",
                BgOpacity = 0.34,
                HudOpacity = 0.98,
                Scale = 1.00,
                TextStroke = 1.65,
                BorderStrength = 0.36,
                TextShadow = true,
                AccentBar = true,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowLabels = true,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "ghost_clean",
            "👻 Fantasma Puro",
            "Zero fundo · só números com contorno nítido",
            "#FFD15C",
            "#7DD3FC",
            () => new OverlaySettings
            {
                PresetId = "ghost_clean",
                Layout = "pill",
                Chrome = "none",
                FontStyle = "fortnite",
                Accent = "#FFD15C",
                SecondaryAccent = "#7DD3FC",
                BackgroundHex = "#000000",
                TextHex = "#FFFFFF",
                BgOpacity = 0.00,
                HudOpacity = 1.00,
                Scale = 1.04,
                TextStroke = 2.25,
                BorderStrength = 0.00,
                TextShadow = true,
                AccentBar = false,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowLabels = false,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "minimal_pill",
            "💊 Pílula Minimalista",
            "Cápsula fina em 1 linha · super discreta",
            "#F4F2ED",
            "#F5B942",
            () => new OverlaySettings
            {
                PresetId = "minimal_pill",
                Layout = "pill",
                Chrome = "capsule",
                FontStyle = "modern",
                Accent = "#F5B942",
                SecondaryAccent = "#38BDF8",
                BackgroundHex = "#090C14",
                TextHex = "#F4F2ED",
                BgOpacity = 0.36,
                HudOpacity = 0.96,
                Scale = 0.96,
                TextStroke = 1.40,
                BorderStrength = 0.28,
                TextShadow = true,
                AccentBar = false,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowLabels = true,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "slurp_hud",
            "💧 Slurp Neon",
            "Vidro azul-ciano estilo Escudo do Fortnite",
            "#00F5D4",
            "#38BDF8",
            () => new OverlaySettings
            {
                PresetId = "slurp_hud",
                Layout = "tactical",
                Chrome = "angled",
                FontStyle = "fortnite",
                Accent = "#00F5D4",
                SecondaryAccent = "#38BDF8",
                BackgroundHex = "#07182B",
                TextHex = "#F0F9FF",
                BgOpacity = 0.36,
                HudOpacity = 0.98,
                Scale = 1.00,
                TextStroke = 1.65,
                BorderStrength = 0.48,
                TextShadow = true,
                AccentBar = true,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowLabels = true,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "esports_micro",
            "🎯 Competitivo Micro",
            "Micro-contador cirúrgico estilo FNCS / FPS",
            "#3DDC84",
            "#CBD5E1",
            () => new OverlaySettings
            {
                PresetId = "esports_micro",
                Layout = "micro",
                Chrome = "sharp",
                FontStyle = "mono",
                Accent = "#3DDC84",
                SecondaryAccent = "#CBD5E1",
                BackgroundHex = "#06080D",
                TextHex = "#F1F5F9",
                BgOpacity = 0.28,
                HudOpacity = 0.95,
                Scale = 0.92,
                TextStroke = 1.60,
                BorderStrength = 0.26,
                TextShadow = true,
                AccentBar = true,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = false,
                ShowLabels = false,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "pirate_gold",
            "🏴‍☠️ Tesouro Pirata",
            "Vidro âmbar + Token & Hora do Tesouro",
            "#F5B942",
            "#FF3D6B",
            () => new OverlaySettings
            {
                PresetId = "pirate_gold",
                Layout = "tactical",
                Chrome = "glass",
                FontStyle = "fortnite",
                Accent = "#F5B942",
                SecondaryAccent = "#FF3D6B",
                BackgroundHex = "#161008",
                TextHex = "#FFFBEB",
                BgOpacity = 0.42,
                HudOpacity = 0.98,
                Scale = 1.00,
                TextStroke = 1.55,
                BorderStrength = 0.48,
                TextShadow = true,
                AccentBar = true,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowTreasureHour = true,
                ShowLabels = true,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "mythic_storm",
            "🔮 Ponto Zero",
            "Cápsula violeta mítica com brilho da Tempestade",
            "#E879F9",
            "#4CC9F0",
            () => new OverlaySettings
            {
                PresetId = "mythic_storm",
                Layout = "pill",
                Chrome = "capsule",
                FontStyle = "fortnite",
                Accent = "#E879F9",
                SecondaryAccent = "#4CC9F0",
                BackgroundHex = "#150926",
                TextHex = "#FAF5FF",
                BgOpacity = 0.38,
                HudOpacity = 0.98,
                Scale = 1.00,
                TextStroke = 1.60,
                BorderStrength = 0.46,
                TextShadow = true,
                AccentBar = false,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowAvg = false,
                ShowLabels = true,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "side_stack",
            "📐 Coluna Lateral",
            "Bloco vertical estreito p/ bordas ou minimapa",
            "#38BDF8",
            "#F5B942",
            () => new OverlaySettings
            {
                PresetId = "side_stack",
                Layout = "vertical",
                Chrome = "glass",
                FontStyle = "fortnite",
                Accent = "#38BDF8",
                SecondaryAccent = "#F5B942",
                BackgroundHex = "#090E18",
                TextHex = "#F8FAFC",
                BgOpacity = 0.34,
                HudOpacity = 0.98,
                Scale = 0.98,
                TextStroke = 1.55,
                BorderStrength = 0.32,
                TextShadow = true,
                AccentBar = true,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowAvg = true,
                ShowLabels = true,
                ShowHeader = false,
                DimWhenIdle = true,
            }),

        new(
            "streamer_card",
            "📊 Cartão Streamer",
            "Painel completo em vidro com todas as métricas",
            "#F5B942",
            "#3DDC84",
            () => new OverlaySettings
            {
                PresetId = "streamer_card",
                Layout = "card",
                Chrome = "glass",
                FontStyle = "fortnite",
                Accent = "#F5B942",
                SecondaryAccent = "#3DDC84",
                BackgroundHex = "#0B0E17",
                TextHex = "#F4F2ED",
                BgOpacity = 0.56,
                HudOpacity = 0.98,
                Scale = 1.00,
                TextStroke = 1.25,
                BorderStrength = 0.36,
                TextShadow = true,
                AccentBar = true,
                ShowIcon = true,
                ShowLive = true,
                ShowSeason = true,
                ShowToken = true,
                ShowAvg = true,
                ShowLast = true,
                ShowBest = true,
                ShowTreasureHour = true,
                ShowLabels = true,
                ShowHeader = true,
                DimWhenIdle = false,
            }),
    ];

    public static OverlayPresetInfo GetOrDefault(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static OverlayPresetInfo NextAfter(string? currentId)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Id, currentId, StringComparison.OrdinalIgnoreCase))
                return All[(i + 1) % All.Count];
        }
        return All[0];
    }
}

public static class OverlayConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounter", "overlay.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path));
                bool isLegacyV1 = !doc.RootElement.TryGetProperty("SchemaVersion", out var sv) || sv.GetInt32() < 2;
                var s = JsonSerializer.Deserialize<OverlaySettings>(doc.RootElement.GetRawText()) ?? new();

                if (isLegacyV1)
                {
                    s.SchemaVersion = OverlaySettings.CurrentSchemaVersion;
                    if (s.Accent is "#F5C147" or "#E7B23C" or "#F3F1EC")
                        s.Accent = OverlaySettings.DefaultAccent;
                }

                if (s.Title is "Chest Counter" or "Chest Tracker" or "Contador de Baús" or "Contador de baús")
                    s.Title = "Bússola do Loot";
                s.Normalize();
                return s;
            }
        }
        catch { }

        var fresh = OverlayPresets.All[0].Create();
        fresh.Normalize();
        return fresh;
    }

    public static void Save(OverlaySettings s)
    {
        s.SchemaVersion = OverlaySettings.CurrentSchemaVersion;
        s.Normalize();
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        File.Move(tmp, Path, overwrite: true);
    }

    // Full configuration as JSON for the OBS Browser Source /config.json endpoint (read fresh each request).
    public static string Json()
    {
        var s = Load();
        return JsonSerializer.Serialize(new
        {
            presetId = s.PresetId,
            title = string.IsNullOrWhiteSpace(s.Title) ? "Bússola do Loot" : s.Title,
            accent = s.Accent,
            secondaryAccent = s.SecondaryAccent,
            backgroundHex = s.BackgroundHex,
            textHex = s.TextHex,
            layout = s.Layout,
            chrome = s.Chrome,
            fontStyle = s.FontStyle,
            bgOpacity = s.BgOpacity,
            hudOpacity = s.HudOpacity,
            scale = s.Scale,
            textStroke = s.TextStroke,
            borderStrength = s.BorderStrength,
            textShadow = s.TextShadow,
            accentBar = s.AccentBar,
            showIcon = s.ShowIcon,
            showLive = s.ShowLive,
            showSeason = s.ShowSeason,
            showToken = s.ShowToken,
            showAvg = s.ShowAvg,
            showLast = s.ShowLast,
            showBest = s.ShowBest,
            showTreasureHour = s.ShowTreasureHour,
            showHeader = s.ShowHeader,
            showLabels = s.ShowLabels,
            pulseOnChest = s.PulseOnChest,
            syncObsStyle = s.SyncObsStyle,
            domain = ChestCounterService.Domain,
        });
    }
}
