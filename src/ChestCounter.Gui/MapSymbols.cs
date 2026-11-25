using System.Windows;
using System.Windows.Media;

namespace ChestCounter.Gui;

// Miniatures share a 32-unit artboard so the silhouettes align in the legend and on the island.
internal static class MapSymbols
{
    internal static DrawingImage Create(MapLayer layer)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
            var outline = new Pen(Brush("#14242E"), 1.35) { LineJoin = PenLineJoin.Round };
            void Shape(string color, string data, bool edge = true) =>
                dc.DrawGeometry(Brush(color), edge ? outline : null, Geometry.Parse(data));
            void Line(string color, double width, string data) =>
                dc.DrawGeometry(null, new Pen(Brush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(data));

            switch (layer.Id)
            {
                case "chest":
                case "rare_chest":
                    bool rare = layer.Id == "rare_chest";
                    string metal = rare ? "#B7DFFF" : "#FFD26D", shade = rare ? "#5367A9" : "#A8692D";
                    Shape(shade, "M3,15 Q3,8 8,7 L23,5 Q28,5 29,11 L29,21 23,26 4,24 Z");
                    Shape(metal, "M4,14 Q4,9 8,8 L21,7 Q25,7 25,13 L22,16 Z");
                    Shape(rare ? "#637DB7" : "#985632", "M4,16 L22,17 22,25 4,23 Z");
                    Shape(shade, "M22,16 L29,12 29,21 22,25 Z");
                    Shape(metal, "M7,8 L10,8 Q8,11 9,15 L8,23 5,23 6,14 Q5,11 7,8 Z");
                    Shape(metal, "M18,7 L21,7 Q23,10 22,15 L21,25 18,24 19,15 Q20,10 18,7 Z");
                    Line(metal, 1.5, "M4,16 L22,18 28,14");
                    Shape(rare ? "#76F4FE" : "#FFE59B", rare ? "M15,14 L19,18 15,23 11,18 Z" : "M11,15 L16,15 16,20 13,22 10,19 Z");
                    break;
                case "buried_chest":
                    Shape("#BE894D", "M3,9 L11,6 20,8 28,4 28,23 20,27 11,24 4,27 Z");
                    Shape("#FFE4A0", "M4,8 L11,5 19,8 27,4 26,21 19,25 11,22 4,25 Z");
                    Line("#C99F62", 1, "M11,6 L11,21 M19,9 L19,23");
                    Line("#A67B46", 1.6, "M7,20 Q8,16 11,17 M13,18 Q15,19 16,16");
                    Line("#A84334", 2.7, "M17,11 L23,16 M23,10 L17,17");
                    break;
                case "ammo_box":
                    Shape("#58816A", "M3,12 L22,9 29,12 28,23 21,26 4,23 Z");
                    Shape("#ABCD83", "M2,11 L9,7 26,8 30,11 22,15 3,14 Z");
                    Shape("#759553", "M4,14 L22,15 21,25 5,23 Z");
                    Shape("#F1D477", "M7,14 L10,14 10,19 7,19 Z M16,15 L19,15 19,20 16,20 Z");
                    Line("#314F49", 2, "M11,10 L12,7 19,7 20,9");
                    break;
                case "wanted_poster":
                    Shape("#AA7245", "M6,5 L27,6 25,27 6,25 Z");
                    Shape("#FFE3A7", "M4,4 Q13,6 24,4 L23,26 19,25 15,27 11,25 4,26 6,16 Z");
                    Line("#A36C46", 2, "M9,8 L20,8 M9,22 L18,22");
                    Shape("#9E513E", "M10,18 Q8,16 11,13 L11,10 14,12 17,11 19,10 19,13 Q22,19 16,20 Q12,20 10,18 Z");
                    Line("#FFE3A7", 1.5, "M12,15 L13,15 M16,15 L17,15");
                    break;
                case "fishing_rod_barrel":
                    Line("#152B3B", 3.6, "M13,17 L8,4 M16,17 L19,3 M17,19 L26,5");
                    Line("#F5CA86", 1.6, "M13,17 L8,4 M16,17 L19,3 M17,19 L26,5");
                    Shape("#AC7749", "M6,14 Q15,10 24,14 L22,26 Q15,30 8,26 Z");
                    Shape("#694C39", "M6,14 Q15,10 24,14 Q17,18 6,15 Z");
                    Line("#94BED1", 2, "M8,18 Q15,21 23,17 M9,25 Q16,28 22,24");
                    break;
                case "loot_cannon":
                    Shape("#A7754D", "M6,22 L9,16 22,16 26,25 20,26 17,21 10,25 Z");
                    Shape("#608C9A", "M4,14 L22,7 26,14 8,21 Q3,22 4,14 Z");
                    Shape("#A3E3E9", "M20,6 L25,4 29,13 24,16 Z");
                    Shape("#28434A", "M24,7 L26,6 28,12 26,13 Z");
                    Shape("#D9AD69", "M18,22 A5,5 0 1 1 8,22 A5,5 0 1 1 18,22 Z");
                    Line("#805B3D", 2.2, "M11,22 L15,22 M13,20 L13,24");
                    break;
                case "sprite_shrine":
                    Shape("#8A81A1", "M3,26 L9,21 22,21 29,26 26,28 6,28 Z");
                    Shape("#EEA8E0", "M7,21 L8,13 12,16 12,9 16,4 20,10 20,16 24,12 25,21 18,25 13,25 Z");
                    Shape("#FFF4EB", "M13,16 L16,10 19,16 16,21 Z");
                    break;
                case "vault":
                    Shape("#8095A3", "M3,8 L8,5 29,6 29,24 25,27 3,26 Z");
                    Shape("#B9C9D2", "M3,8 L25,8 25,27 3,26 Z");
                    Shape("#586F80", "M6,11 L22,11 22,23 6,23 Z");
                    Shape("#E9CF91", "M18,17 A4,4 0 1 1 10,17 A4,4 0 1 1 18,17 Z");
                    Line("#354A5B", 1.7, "M14,14 L14,20 M11,17 L17,17");
                    Line("#D4E8E8", 2, "M6,13 L6,15 M6,19 L6,21");
                    break;
                case "vending":
                    Shape("#AB648B", "M6,5 L22,3 27,6 26,27 21,29 6,27 Z");
                    Shape("#ECAAC6", "M6,5 L22,6 21,28 6,27 Z");
                    Shape("#51778A", "M9,8 L19,9 18,19 9,18 Z");
                    Shape("#90E1E6","M10,10 L15,10 12,16 10,16 Z",false);
                    Shape("#473F50", "M10,22 L17,22 17,24 10,24 Z");
                    Line("#F8D974", 1.8, "M24,12 L24,15");
                    break;
                case "campfire":
                    Line("#362F2B", 4, "M5,26 L25,22 M7,22 L27,27");
                    Line("#D8A774", 2.5, "M5,26 L25,22 M7,22 L27,27");
                    Shape("#F18C55", "M9,23 Q4,18 10,12 L12,16 Q15,11 14,4 Q22,9 20,16 L24,11 Q30,22 19,24 Z");
                    Shape("#FFE37C", "M12,22 Q10,19 14,15 L15,18 18,12 Q23,21 17,23 Z",false);
                    break;
                case "fish":
                    Shape("#7DDCEB", "M4,15 Q11,6 20,12 L27,8 25,16 28,21 20,19 Q11,25 4,17 Z");
                    Line("#326C83", 1.5, "M11,12 Q15,16 11,20");
                    Shape("#163E51", "M9,15 A1.2,1.2 0 1 1 6.6,15 A1.2,1.2 0 1 1 9,15 Z",false);
                    Line("#B5ECF6", 1.6, "M5,24 Q9,26 13,24 M17,25 Q21,27 26,24");
                    break;
                case "reboot":
                    Shape("#7EA6AE", "M3,12 L7,10 19,10 25,13 29,19 28,24 3,24 Z");
                    Shape("#C5DDE2", "M19,13 L24,14 27,18 20,18 Z");
                    Shape("#52868E", "M4,14 L17,14 17,21 4,21 Z");
                    Line("#9BFFDB", 1.7, "M7,18 Q8,14 12,16 L14,16 M14,14 L14,17 11,18");
                    Shape("#91FFE0", "M10,9 L14,3 18,9 Z");
                    Shape("#49606D", "M11,24 A3,3 0 1 1 5,24 A3,3 0 1 1 11,24 Z M26,24 A3,3 0 1 1 20,24 A3,3 0 1 1 26,24 Z");
                    break;
                case "extraction":
                    Shape("#55999B", "M3,22 L12,18 23,18 29,22 24,26 9,27 Z");
                    Shape("#AFEBD9", "M6,22 L13,19 22,20 25,22 21,24 11,24 Z");
                    Shape("#77F5E3", "M13,21 L13,12 8,12 16,4 24,12 19,12 19,20 16,22 Z");
                    break;
                case "car":
                    Shape("#C6C7DF", "M2,18 L6,15 12,9 21,9 25,15 29,17 29,23 3,23 Z");
                    Shape("#536F89", "M8,15 L13,11 16,11 16,15 Z M18,11 L21,11 24,15 18,15 Z");
                    Line("#EEE2AA", 2, "M25,18 L28,18");
                    Shape("#586278", "M12,23 A3.5,3.5 0 1 1 5,23 A3.5,3.5 0 1 1 12,23 Z M27,23 A3.5,3.5 0 1 1 20,23 A3.5,3.5 0 1 1 27,23 Z");
                    break;
                case "boat":
                    Shape("#85BCD9", "M3,16 L9,16 14,9 20,9 23,15 30,13 Q27,22 20,24 L7,23 Z");
                    Shape("#D4EDFA", "M10,16 L15,11 18,11 20,15 Z");
                    Shape("#597B9A", "M4,19 L28,16 Q26,22 20,23 L7,22 Z");
                    Line("#94DCEB", 1.6, "M5,26 Q10,28 14,26 M17,26 Q22,28 27,25");
                    break;
                default:
                    Shape(layer.Color, "M16,4 L28,16 16,28 4,16 Z");
                    break;
            }
        }
        var icon = new DrawingImage(drawing);
        icon.Freeze();
        return icon;
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
