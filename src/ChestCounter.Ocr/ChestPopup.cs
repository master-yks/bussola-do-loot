using System.Text;
using System.Text.RegularExpressions;

namespace ChestCounter.Ocr;

internal static partial class ChestPopup
{
    // Tolerant reward label matcher covering Portuguese and English chest XP popups, OCR accent/digit
    // substitutions (BAÚ, BAU, BÁU, BAÜ, B4U, CHE5T), all themed Fortnite Chapter 7 chest variants
    // (Raro, Vasculhado, Aberto, Soterrado, Enterrado, Dourado, Mítico, Relíquia, Facção, Tesouro),
    // and stacked XP multipliers (x2, ×2, X3, *2, K2).
    [GeneratedRegex(
        @"^[\s\p{P}]*(?:B[AÁÀÂÃ4][UÚÙÛÜ0]S?(?:\s+(?:RAROS?|ABERTOS?|VASCULHADOS?|SOTERRADOS?|ENTERRADOS?|DOURADOS?|M[IÍ]TICOS?|DE\s+\p{L}+|DO\s+\p{L}+))?|(?:ABRIU|VASCULHOU)\s+(?:UM\s+)?B[AÁÀÂÃ4][UÚÙÛÜ0]|(?:RARE\s+|SEARCHED\s+|BURIED\s+|TREASURE\s+|FACTION\s+|RELIC\s+|GOLDEN\s+)?CHE[S5]TS?)[\s\p{P}]*(?:[XK×\*]\s*([1-9]\d?))?[\s\p{P}]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    [GeneratedRegex(
        @"(?<!\w)[+]?\s*\d[\d.,\s]*\s*(?:EXP|EX\s*P|[X×]P|PE)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Experience();

    internal static bool HasExperience(string line) => Experience().IsMatch(line);

    internal static int Count(IEnumerable<string> lines)
    {
        int total = 0;
        foreach (var line in lines)
        {
            var text = Experience().Replace(line, "").Normalize(NormalizationForm.FormC);
            var match = Label().Match(text);
            if (match.Success)
                total += match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 1;
        }
        return total;
    }
}
