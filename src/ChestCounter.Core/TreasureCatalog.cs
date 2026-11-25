namespace ChestCounter;

public sealed record TreasureOdds(string Kind, string Label, int OneIn, string ShortLabel, string HexColor);

public sealed record TreasureWeek(
    int Number,
    DateTime StartUtc,
    DateTime EndUtc,
    string Title,
    IReadOnlyDictionary<string, string> Prizes);

public sealed record TreasureHour(
    DateTime StartUtc,
    DateTime EndUtc,
    IReadOnlyDictionary<string, double> Boosts);

// Treasure-hunt odds, weekly prizes, Treasure Hours and cumulative probability mathematics
// for the Fortnite Chapter 7 "Treasure Hunters" season.
public static class TreasureCatalog
{
    public const string Outfit = "outfit", Token = "token", Car = "car", Pickaxe = "pickaxe",
        Jam = "jam", Loading = "loading", Gizmo = "gizmo";

    public static readonly IReadOnlyList<TreasureOdds> Odds =
    [
        new(Token,   "Ficha da Loja Pirata",             137_500,   "Token Loja Pirata", "#F5B942"),
        new(Outfit,  "Traje Dourado da Semana",        1_562_500,   "Traje Dourado",     "#FF3D6B"),
        new(Car,     "Adesivo / Veículo Dourado",          7_813,   "Veículo Dourado",   "#A78BFA"),
        new(Pickaxe, "Picareta / Acessório de Costas",       568,   "Picareta / Costas", "#4CC9F0"),
        new(Jam,     "Música de Improviso (Jam)",            391,   "Música Jam",        "#3DDC84"),
        new(Loading, "Tela de Carregamento",                 272,   "Tela Carregamento", "#6EE7B7"),
        new(Gizmo,   "Engenhoca do Esquadrão",               127,   "Engenhoca",         "#94A3B8"),
    ];

    private static DateTime Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    public static readonly IReadOnlyList<TreasureWeek> Weeks =
    [
        new(1, Utc(11, 1, 14), Utc(11, 8, 14), "A$AP Arrives", new Dictionary<string, string>
        {
            [Outfit] = "Solid Gold A$AP Rocky",
            [Pickaxe] = "Solid Gold A$AP Rocky's Gauntlets",
            [Car] = "Gold Mercedes-AMG G 63 Decal",
            [Jam] = "No Trespassing",
            [Loading] = "Plunder in Paradise",
        }),
        new(2, Utc(11, 8, 14), Utc(11, 15, 14), "Lil Uzi Arrives", new Dictionary<string, string>
        {
            [Outfit] = "Solid Gold Lil Uzi Vert",
            [Pickaxe] = "Diamond and Gold",
            [Car] = "Gold Cadillac CT5-V Decal",
            [Jam] = "London",
            [Loading] = "So Kawaii",
        }),
        new(3, Utc(11, 15, 14), Utc(11, 22, 14), "Miku Arrives", new Dictionary<string, string>
        {
            [Outfit] = "Solid Gold Hatsune Miku",
            [Pickaxe] = "Golden Miku's Takarakabako",
            [Car] = "Gold Corvette ZR1 (Llama Crew)",
            [Jam] = "PPP",
            [Loading] = "May Fortune Find You",
        }),
        new(4, Utc(11, 22, 14), Utc(11, 29, 14), "Oscar & Teto Arrive", new Dictionary<string, string>
        {
            [Outfit] = "Solid Gold Kasane Teto",
            [Pickaxe] = "Rogue Wave & Tidewalker",
            [Car] = "Gold Lamborghini Urus SE",
            [Jam] = "Sea Shanty",
            [Loading] = "Oscar's Wraps",
        }),
        new(5, Utc(11, 29, 14), Utc(12, 5, 14), "The Final Hunt (Golden Week)", new Dictionary<string, string>
        {
            [Outfit] = "Solid Gold Captain X + Retorno Semanas 1–4",
            [Pickaxe] = "Todos os Acessórios & Picaretas Douradas",
            [Car] = "Gold G-Wagon v2 + Todos os Veículos",
            [Jam] = "Todas as Músicas do Evento",
            [Loading] = "Todas as Telas de Carregamento",
        }),
    ];

    public static readonly IReadOnlyDictionary<string, double> UsualBoosts =
        new Dictionary<string, double> { [Outfit] = 7.5, [Token] = 1.5, [Car] = 10.0 };

    public static DateTime SeasonEndUtc => Weeks[^1].EndUtc;

    public static TreasureWeek? WeekAt(DateTime utc) =>
        Weeks.FirstOrDefault(w => utc >= w.StartUtc && utc < w.EndUtc);

    // The Treasure Hour running at `utc`, or the next one. Null once the season is over.
    public static TreasureHour? CurrentOrNext(DateTime utc)
    {
        for (var day = utc.Date.AddDays(-1); day < SeasonEndUtc; day = day.AddDays(1))
        {
            int hour = day.DayOfWeek switch { DayOfWeek.Saturday => 19, DayOfWeek.Sunday => 2, _ => -1 };
            if (hour < 0) continue;
            var start = DateTime.SpecifyKind(day.AddHours(hour), DateTimeKind.Utc);
            var end = start.AddHours(2);
            if (end > utc && start < SeasonEndUtc) return new TreasureHour(start, end, UsualBoosts);
        }
        return null;
    }

    public static bool IsLive(TreasureHour hour, DateTime utc) => utc >= hour.StartUtc && utc < hour.EndUtc;

    // Exact cumulative probability P(X >= 1) = 1 - (1 - 1/oneIn)^chests using high-precision log-exp.
    public static double ChanceOfAtLeastOne(double oneIn, long chests)
    {
        if (chests <= 0 || oneIn <= 1.0) return chests > 0 && oneIn <= 1.0 ? 1.0 : 0.0;
        double logComplement = chests * Math.Log(1.0 - 1.0 / oneIn);
        if (logComplement < -40.0) return 1.0;
        // For tiny x, -expm1(x) avoids catastrophic cancellation when chests << oneIn
        return -(Math.Exp(logComplement) - 1.0);
    }

    public static double ChanceOfAtLeastOne(int oneIn, long chests) =>
        ChanceOfAtLeastOne((double)oneIn, chests);

    // Number of chests required to reach `targetProb` (e.g. 0.50 for 50% coin-flip chance).
    public static long ChestsForTargetProbability(double oneIn, double targetProb)
    {
        if (targetProb <= 0) return 0;
        if (targetProb >= 0.999999) targetProb = 0.999999;
        if (oneIn <= 1.0) return 1;
        return (long)Math.Ceiling(Math.Log(1.0 - targetProb) / Math.Log(1.0 - 1.0 / oneIn));
    }

    // Expected number of drops E[X] = chests / oneIn.
    public static double ExpectedPulls(double oneIn, long chests) =>
        chests <= 0 || oneIn <= 0 ? 0.0 : chests / oneIn;

    // Human-friendly formatted percentage with smart precision so rare odds clearly show progress.
    public static string FormatChance(double p, bool highPrecision = false)
    {
        double pct = p * 100.0;
        if (pct <= 0.0) return "0,00%";
        if (pct >= 99.95) return ">99,9%";
        if (highPrecision)
        {
            if (pct < 1.0) return $"{pct:0.000}%";
            if (pct < 10.0) return $"{pct:0.00}%";
            return $"{pct:0.00}%";
        }
        if (pct < 0.01) return $"{pct:0.000}%";
        if (pct < 1.0) return $"{pct:0.00}%";
        if (pct < 10.0) return $"{pct:0.00}%";
        return $"{pct:0.0}%";
    }
}
