namespace ChestCounter;

// Treasure-hunt prize ids as they appear in replays. Unknown ids still show up with a clean label.
public static class Treasures
{
    public const string PirateShopToken = "9f73f94d-2e61-4523-97d7-d61235d3365e";

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [PirateShopToken] = "Ficha de Acesso à Loja Pirata",
        // Week 1 — A$AP Arrives
        ["DelilahPrize:dpid_item1"] = "Solid Gold A$AP Rocky (Traje)",
        ["DelilahPrize:dpid_item2"] = "Solid Gold A$AP Rocky's Gauntlets",
        ["DelilahPrize:dpid_item3"] = "No Trespassing (Música Jam)",
        ["DelilahPrize:dpid_item4"] = "Plunder in Paradise (Tela de Carregamento)",
        ["DelilahPrize:dpid_item5"] = "Gold Mercedes-AMG G 63 Decal",
        // Week 2 — Lil Uzi Arrives
        ["DelilahPrize:dpid_item14"] = "Solid Gold Lil Uzi Vert (Traje)",
        ["DelilahPrize:dpid_item15"] = "Diamond and Gold (Acessório de Costas)",
        ["DelilahPrize:dpid_item16"] = "London (Música Jam)",
        ["DelilahPrize:dpid_item17"] = "So Kawaii (Tela de Carregamento)",
        ["DelilahPrize:dpid_item18"] = "Gold Cadillac CT5-V Decal",
        // Week 3 & 4 — Miku, Oscar & Teto
        ["DelilahPrize:dpid_item19"] = "Solid Gold Hatsune Miku (Traje)",
        ["DelilahPrize:dpid_item20"] = "Golden Miku's Takarakabako",
        ["DelilahPrize:dpid_item21"] = "PPP (Música Jam)",
        ["DelilahPrize:dpid_item22"] = "May Fortune Find You (Tela de Carregamento)",
        ["DelilahPrize:dpid_item23"] = "Gold Corvette ZR1 Decal",
        ["DelilahPrize:dpid_item24"] = "Solid Gold Kasane Teto (Traje)",
        ["DelilahPrize:dpid_item25"] = "Rogue Wave & Tidewalker",
        ["DelilahPrize:dpid_item26"] = "Sea Shanty (Música Jam)",
        ["DelilahPrize:dpid_item27"] = "Oscar's Wraps (Tela de Carregamento)",
        ["DelilahPrize:dpid_item28"] = "Gold Lamborghini Urus SE Decal",
        // Week 5 — The Final Hunt
        ["DelilahPrize:dpid_item29"] = "Solid Gold Captain X (Traje)",
        ["DelilahPrize:dpid_item30"] = "Golden Captain X Acessório",
        ["DelilahPrize:dpid_item31"] = "Música Jam (Semana Final)",
        ["DelilahPrize:dpid_item32"] = "Tela de Carregamento (Semana Final)",
        ["DelilahPrize:dpid_item33"] = "Gold G-Wagon v2 Decal",
    };

    private static readonly Dictionary<string, string> GizmoFriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FlameCeleb"] = "Sinalizador de Fogo Dourado",
        ["RelicFinder"] = "Localizador de Relíquias",
        ["LuckyLocator"] = "Localizador da Sorte",
        ["SmugglerExtractionKey"] = "Chave de Extração Pirata",
        ["RelicExtractor"] = "Extrator de Relíquias",
    };

    // Gizmos carry no prize id in the replay; we tag them "gizmo:<item>" (item may be empty).
    public const string GizmoPrefix = "gizmo:";

    public static bool IsGizmo(string prizeId) => prizeId.StartsWith(GizmoPrefix, StringComparison.Ordinal);

    public static bool IsToken(string prizeId) =>
        string.Equals(prizeId, PirateShopToken, StringComparison.OrdinalIgnoreCase);

    public static string Name(string prizeId)
    {
        if (IsGizmo(prizeId))
        {
            var item = prizeId[GizmoPrefix.Length..];
            if (item.Length == 0) return "Engenhoca do Esquadrão";
            foreach (var kv in GizmoFriendlyNames)
            {
                if (item.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return "Engenhoca: " + kv.Value;
            }
            item = item.Replace("WID_", "").Replace("B_Weap_", "").Replace("B_", "")
                       .Replace("_Gizmo", "").Replace("_Athena", "").Replace("_Consumable_S43", "").Replace("_C", "");
            item = System.Text.RegularExpressions.Regex.Replace(item, "(?<=[a-z])(?=[A-Z])", " ").Trim('_', ' ');
            return item.Length == 0 ? "Engenhoca do Esquadrão" : "Engenhoca: " + item;
        }
        if (Names.TryGetValue(prizeId, out var name)) return name;
        if (prizeId.StartsWith("DelilahPrize:", StringComparison.OrdinalIgnoreCase))
            return $"Tesouro Semanal ({prizeId["DelilahPrize:".Length..]})";
        return $"Tesouro ({prizeId})";
    }
}
