using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChestCounter.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        var folder = Directory.CreateTempSubdirectory("bussola-map-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "legacy.json"),
                """{"Name":"Antiga","Radius":20,"Stops":[{"X":0.2993,"Y":0.5521}]}""");
            var planner = new IslandPlanner(true, folder.FullName);
            var map = planner.Map;
            var landing = new Point(.2993, .5521);
            foreach (var (limit, range) in new[] { (4, 100), (8, 180), (12, 300) })
            {
                int added = map.SuggestStops(landing, limit, range);
                Check(added > 1 && added <= limit + 1 && map.Route[0] == landing, $"pouso e limite de destinos ({range} m)");
                Check(map.LengthMeters <= 2 * range + 1e-6 && map.Nearby.Count > 0, $"orçamento e cobertura ({range} m)");
                Check(map.Route.All(p => (p - landing).Length * map.Data.WidthMeters <= range + 1e-6), "raio de busca");
                var first = map.Route.ToArray();
                int previous = map.Nearby.Count;
                double length = map.LengthMeters;
                int extra = map.SuggestStops(first[^1], limit, range);
                Check(map.Route.Take(first.Length).SequenceEqual(first), "extensão preserva as paradas anteriores");
                Check(extra == 0 || map.Nearby.Count > previous && map.LengthMeters - length <= 2 * range + 1e-6,
                    "extensão cobre novos baús no orçamento");
                if (extra > 0) map.Undo();
                Check(map.Route.SequenceEqual(first), "desfazer extensão");
                map.Undo();
                Check(map.Route.Count == 0 && !map.CanUndo, "desfazer sugestão inteira");
            }

            map.VisibleLayers.Clear();
            Check(map.SuggestStops(landing, 4) == 0 && !map.CanUndo, "sem camadas, sem edição");
            map.VisibleLayers.Add("chest");
            map.SuggestStops(landing, 4);
            var saved = map.Route.ToArray();
            planner.Itinerary.IsExpanded = true;
            var firstRow = (DockPanel)planner.StopList.Children[0];
            ((Button)firstRow.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(map.Route[0] == saved[1] && map.Route[1] == saved[0], "reordenar pelo itinerário");
            map.Undo();
            Check(map.Route.SequenceEqual(saved), "desfazer reordenação");
            planner.RouteName.Text = "Só comuns";
            planner.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            planner.NewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            planner.Saved.SelectedIndex = 0;
            Check(map.Route.Count == 1 && map.Route[0] == landing && map.VisibleLayers.Contains("rare_chest"), "arquivo antigo");
            planner.Saved.SelectedIndex = 1;
            Check(map.Route.SequenceEqual(saved) && map.VisibleLayers.SetEquals(["chest"]), "reabrir paradas e camadas");
            Check(map.Nearby.All(s => s.Layer.Id == "chest"), "resumo segue camadas ativas");
        }
        finally { folder.Delete(recursive: true); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine($"OK {label}");
    }
}
