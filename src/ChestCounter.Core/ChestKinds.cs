using System.Text.RegularExpressions;

namespace ChestCounter;

public sealed record ChestKind(string Key, string Name, string Icon, string Color);

public static partial class ChestKinds
{
    public static readonly ChestKind Common = new("common", "Comum", "chest.png", "#FFD277");
    public static readonly ChestKind Rare = new("rare", "Raro", "Assets/rare.png", "#88CCFF");
    public static readonly ChestKind Buried = new("buried", "Enterrado", "chest.png", "#EDAC85");
    public static readonly ChestKind Relic = new("relic", "Elemental", "Assets/relic.png", "#C7AAFC");
    public static readonly ChestKind Poster = new("poster", "Do cartaz", "Assets/relic.png", "#EAD7AC");

    public static IReadOnlyList<ChestKind> All { get; } = [Common, Rare, Buried, Relic, Poster];

    // A placed actor can end in a numeric instance, a UAID, or an instance hash.
    [GeneratedRegex(@"^(?:Tiered_Chest(?:_Athena)?|AlwaysSpawn_NormalChest)(?:_C)?(?:\d+)?(?:_(?:UAID_|[a-f0-9]{16}_|\d+$).*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommonActor();

    public static ChestKind? FromActor(string label)
    {
        bool Has(string value) => label.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (!Has("chest")) return null;
        if (Has("Wanted") || Has("Poster") || (Has("Buried") && Has("Sprite"))) return Poster;
        if (Has("Buried")) return Buried;
        if (Has("Relic") || Has("Sprite")) return Relic;
        if (Has("RareChest") || Has("Chest_Rare") || Has("Rare_Chest")) return Rare;
        return CommonActor().IsMatch(label) ? Common : null;
    }
}
