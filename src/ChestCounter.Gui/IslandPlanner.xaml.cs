using System.Globalization;
using System.IO;
using System.Windows.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChestCounter.Gui;

public partial class IslandPlanner : UserControl
{
    private sealed record Stop(double X, double Y);
    private sealed record SavedRoute(string Name, double Radius, Stop[] Stops);
    private readonly string _folder;

    public IslandPlanner() : this(true) { }

    internal IslandPlanner(bool loadSavedRoutes)
    {
        InitializeComponent();
        _folder = Path.Combine(Path.GetDirectoryName(AppConfig.Path)!, "rotas", "crossbones-" + Map.Data.Version);
        Places.ItemsSource = Map.Data.Places.OrderBy(p => p.Name);
        BuildLayers();
        Map.SpotInspected += (spot, detail) =>
        {
            InfoIcon.Source = Map.Symbols[spot.Layer.Id];
            InfoName.Text = spot.Layer.Name;
            InfoDetail.Text = detail;
            InfoCard.Visibility = Visibility.Visible;
        };
        Map.BackgroundClicked += () => InfoCard.Visibility = Visibility.Collapsed;
        Map.OriginPicked += Generate;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && Map.PickingOrigin) { CancelPicking(); e.Handled = true; }
        };
        SizeChanged += (_, _) => ResizeSidebar();
        UpdateMode();
        Map.RouteChanged += UpdateSummary;
        Itinerary.Expanded += (_, _) => UpdateItinerary();
        UpdateSummary();
        if (loadSavedRoutes) LoadSavedList();
        else Saved.IsEnabled = false;
    }
    private bool _collapsed;
    private readonly Dictionary<string, CheckBox> _toggles = new();
    private readonly Dictionary<string, List<MapLayer>> _groups = new();
    private readonly Dictionary<string, TextBlock> _headers = new();
    private static string Category(MapLayer layer) => layer.Id switch
    {
        _ when layer.Chest => "Baús",
        "ammo_box" or "loot_cannon" or "fishing_rod_barrel" or "fish" => "Suprimentos",
        "boat" or "car" or "reboot" => "Veículos",
        _ => "Serviços e objetivos"
    };

    private void BuildLayers()
    {
        foreach (var group in Map.Data.Layers.GroupBy(Category))
        {
            _groups[group.Key] = group.ToList();
            var title = new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Gold"), Margin = new Thickness(1, 10, 0, 3) };
            _headers[group.Key] = title;
            LayerList.Children.Add(title);
            foreach (var layer in group)
            {
                var row = new DockPanel();
                var count = new TextBlock { Text = layer.Points.Length.ToString("N0"), Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(count, Dock.Right);
                row.Children.Add(count);
                row.Children.Add(new Image { Source = Map.Symbols[layer.Id], Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 0) });
                row.Children.Add(new TextBlock { Text = layer.Name, Foreground = (Brush)FindResource("Text"), FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var box = new CheckBox { Content = row, Style = (Style)FindResource("LayerToggle"), IsChecked = Map.VisibleLayers.Contains(layer.Id) };
                AutomationProperties.SetName(box, layer.Name);
                box.Click += (_, _) =>
                {
                    if (box.IsChecked == true) Map.VisibleLayers.Add(layer.Id);
                    else Map.VisibleLayers.Remove(layer.Id);
                    RefreshLayers();
                };
                _toggles[layer.Id] = box;
                LayerList.Children.Add(box);
            }
        }
        RefreshLayers();
    }
    private void RefreshLayers()
    {
        int count = Map.Data.Layers.Where(l => Map.VisibleLayers.Contains(l.Id)).Sum(l => l.Points.Length);
        LayerSummary.Text = $"{Map.VisibleLayers.Count} de {Map.Data.Layers.Length} camadas  ·  {count:N0} pontos";
        InfoCard.Visibility = Visibility.Collapsed;
        Map.InvalidateVisual();
    }
    private void Preset(object sender, RoutedEventArgs e)
    {
        string preset = (string)((Button)sender).Tag;
        Map.VisibleLayers.Clear();
        foreach (var layer in Map.Data.Layers)
        {
            bool visible = preset == "all" || preset == "chests" && layer.Chest;
            if (visible) Map.VisibleLayers.Add(layer.Id);
            _toggles[layer.Id].IsChecked = visible;
        }
        RefreshLayers();
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        string query = LayerQuery.Text.Trim();
        QueryHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool any = false;
        foreach (var (name, layers) in _groups)
        {
            bool groupVisible = false;
            foreach (var layer in layers)
            {
                bool show = CultureInfo.CurrentCulture.CompareInfo.IndexOf(name + " " + layer.Name, query, CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase) >= 0;
                _toggles[layer.Id].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                groupVisible |= show;
            }
            _headers[name].Visibility = groupVisible ? Visibility.Visible : Visibility.Collapsed;
            any |= groupVisible;
        }
        NoMatches.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }
    private void TogglePanel(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ResizeSidebar();
    }
    private void ResizeSidebar()
    {
        Sidebar.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        SideWidth.Width = new GridLength(_collapsed ? 0 : ActualWidth < 920 ? 258 : 308);
        PanelGlyph.RenderTransformOrigin = new Point(.5, .5);
        PanelGlyph.RenderTransform = new ScaleTransform(_collapsed ? -1 : 1, 1);
        PanelToggle.ToolTip = _collapsed ? "Abrir painel" : "Recolher painel";
        Places.Width = ActualWidth < 920 && !_collapsed ? 137 : 157;
    }
    private void UpdateMode()
    {
        bool route = RouteTab.IsChecked == true;
        Map.Editing = route && Edit.IsChecked == true;
        MapMode.Text = Map.PickingOrigin ? "Pouso" : Map.Editing ? "Editar rota" : "Explorar";
        MapHint.Text = Map.PickingOrigin ? "Clique no mapa para escolher o ponto de partida." : Map.Editing
            ? "Clique para marcar uma parada · Ctrl + Z para desfazer"
            : "Clique em um grupo para ampliar ou em um objeto para identificá-lo.";
        SuggestButton.Content = Map.PickingOrigin ? "Cancelar escolha do pouso" : Map.Route.Count > 0 ? "Continuar a partir do fim" : "Sugerir percurso de baús";
    }
    private void Suggest(object sender, RoutedEventArgs e)
    {
        if (Map.PickingOrigin) { CancelPicking(); return; }
        if (!Map.Data.Layers.Any(l => l.Chest && Map.VisibleLayers.Contains(l.Id)))
        {
            RouteStatus.Text = "Ative uma camada de baús em Explorar para sugerir paradas.";
            return;
        }
        if (Map.Route.Count > 0) { Generate(Map.Route[^1]); return; }
        Map.PickingOrigin = true;
        RouteStatus.Text = "Clique no local de pouso para gerar o percurso.";
        UpdateMode();
    }
    private void CancelPicking()
    {
        Map.PickingOrigin = false;
        RouteStatus.Text = "";
        UpdateMode();
    }
    private void Generate(Point start)
    {
        CancelPicking();
        int count = Map.SuggestStops(start, 8);
        RouteStatus.Text = count == 0 ? "Nenhum novo baú ativo em até 180 m. Escolha outro local ou mude as camadas."
            : count == 1 ? "1 parada adicionada." : $"{count} paradas adicionadas.";
    }
    private void CloseInfo(object sender, RoutedEventArgs e) => InfoCard.Visibility = Visibility.Collapsed;
    private void FitStops(object sender, RoutedEventArgs e) => Map.FitRoute();

    private void LoadSavedList()
    {
        var routes = new List<SavedRoute>();
        try
        {
            if (Directory.Exists(_folder))
                foreach (string file in Directory.EnumerateFiles(_folder, "*.json"))
                {
                    try
                    {
                        var route = JsonSerializer.Deserialize<SavedRoute>(File.ReadAllText(file));
                        if (route is { Stops.Length: > 0 } && !string.IsNullOrWhiteSpace(route.Name)
                            && route.Radius is >= 5 and <= 100
                            && route.Stops.All(p => p is { X: >= 0 and <= 1, Y: >= 0 and <= 1 }))
                            routes.Add(route);
                    }
                    catch (JsonException) { Notice.Text = "Um arquivo de rota está ilegível."; }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice.Text = "Falha ao ler as rotas salvas.";
        }
        Saved.ItemsSource = routes.OrderBy(r => r.Name).ToArray();
        Saved.IsEnabled = routes.Count > 0;
        Saved.Tag = routes.Count > 0 ? "Escolher rota" : "Nenhuma rota salva";
    }
    private void UpdateSummary()
    {
        int common = Map.Nearby.Count(s => s.Layer.Id == "chest");
        int rare = Map.Nearby.Count(s => s.Layer.Id == "rare_chest");
        int hidden = Map.Nearby.Count - common - rare;
        ChestTotal.Text = Map.Nearby.Count == 1 ? "1 baú possível" : $"{Map.Nearby.Count:N0} baús possíveis";
        Breakdown.Text = $"{common} comuns  ·  {rare} raros  ·  {hidden} enterrados";
        string stops = Map.Route.Count == 1 ? "1 parada" : $"{Map.Route.Count} paradas";
        Distance.Text = $"{stops}   ·   {Map.LengthMeters:N0} m";
        RadiusLabel.Text = $"Alcance de coleta  ·  {Radius.Value:0} m";
        UpdateMode();
        FitButton.IsEnabled = Map.Route.Count > 0;
        Itinerary.Visibility = Map.Route.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Itinerary.IsExpanded) UpdateItinerary();
        UndoButton.IsEnabled = Map.CanUndo;
        NewButton.IsEnabled = Map.Route.Count > 0;
        SaveButton.IsEnabled = Map.Route.Count > 0 && !string.IsNullOrWhiteSpace(RouteName.Text);
    }
    private void UpdateItinerary()
    {
        StopList.Children.Clear();
        for (int i = 0; i < Map.Route.Count; i++)
        {
            var p = Map.Route[i];
            double leg = i == 0 ? 0 : (p - Map.Route[i - 1]).Length * Map.Data.WidthMeters;
            string text = i == 0 ? "1   ·   Partida" : $"{i + 1}   ·   {leg:N0} m desde a anterior";
            var button = new Button { Content = text, Style = (Style)FindResource("Quiet"),
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1),
                ToolTip = $"Centralizar na parada {i + 1}" };
            button.Click += (_, _) => Map.FocusStop(p);
            StopList.Children.Add(button);
        }
    }
    private void Export(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Imagem PNG (*.png)|*.png", FileName = "Crossbones Isle", DefaultExt = ".png"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string temp = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                ExportView(stream);
            File.Move(temp, dialog.FileName, true);
            Notice.Text = "Imagem da vista atual exportada.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice.Text = "Falha ao gravar a imagem na pasta escolhida.";
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal void ExportView(Stream output)
    {
        double width = Math.Max(1, Map.ActualWidth), height = Math.Max(1, Map.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, width, height + 86));
            void Caption(string value, double size, string brush, double y)
            {
                var type = new Typeface((FontFamily)FindResource("Body"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, type, size,
                    (Brush)FindResource(brush), 1) { MaxTextWidth = Math.Max(1, width - 24), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new Point(12, y));
            }
            string name = RouteName.Text.Trim();
            Caption(name.Length > 0 ? name + "  ·  Crossbones Isle" : "Crossbones Isle", 19, "Text", 8);
            dc.DrawRectangle(new VisualBrush(Map), null, new Rect(0, 41, width, height));
            Caption($"{Distance.Text}  ·  {ChestTotal.Text}  ·  coleta até {Radius.Value:0} m", 12, "Gold", height + 44);
            Caption($"Bússola do Loot  ·  BR {Map.Data.Version}  ·  Epic Games / Fortnite-API / Fortiq", 10, "Muted", height + 65);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 2), (int)Math.Ceiling((height + 86) * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        string name = RouteName.Text.Trim();
        var route = new SavedRoute(name, Radius.Value, Map.Route.Select(p => new Stop(p.X, p.Y)).ToArray());
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())));
        string temp = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(temp, JsonSerializer.Serialize(route));
            File.Move(temp, Path.Combine(_folder, key + ".json"), true);
            Notice.Text = $"Rota salva · {name}";
            LoadSavedList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice.Text = "Falha ao gravar a rota. O traçado continua aberto.";
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private void SavedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Saved.SelectedItem is not SavedRoute route) return;
        CancelPicking();
        RouteName.Text = route.Name;
        Radius.Value = route.Radius;
        Map.ReplaceRoute(route.Stops.Select(p => new Point(p.X, p.Y)));
        Map.FitRoute();
        Notice.Text = $"Rota aberta · {route.Name}";
        Saved.SelectedIndex = -1;
    }
    private void NewRoute(object sender, RoutedEventArgs e)
    {
        CancelPicking();
        Map.ReplaceRoute([]);
        RouteName.Clear();
        Notice.Text = "";
        Edit.IsChecked = true;
    }
    private void PlaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Places.SelectedItem is not MapPlace place) return;
        InfoCard.Visibility = Visibility.Collapsed;
        Map.FocusPlace(place);
        Places.SelectedIndex = -1;
    }
    private void SwitchPane(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || RoutePane == null) return;
        InfoCard.Visibility = Visibility.Collapsed;
        CancelPicking();
        PaneScroll.ScrollToTop();
        bool layers = LayerTab.IsChecked == true;
        LayerPane.Visibility = layers ? Visibility.Visible : Visibility.Collapsed;
        RoutePane.Visibility = layers ? Visibility.Collapsed : Visibility.Visible;
    }
    private void NameChanged(object sender, TextChangedEventArgs e) { if (IsInitialized) UpdateSummary(); }
    private void EditChanged(object sender, RoutedEventArgs e) { if (IsInitialized) UpdateMode(); }
    private void LabelsChanged(object sender, RoutedEventArgs e) { Map.ShowLabels = ((CheckBox)sender).IsChecked == true; Map.InvalidateVisual(); }
    private void RadiusChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Map != null) Map.SetRadius(e.NewValue); }
    private void Undo(object sender, RoutedEventArgs e) { CancelPicking(); Map.Undo(); }
    private void Fit(object sender, RoutedEventArgs e) { InfoCard.Visibility = Visibility.Collapsed; Map.ResetView(); Places.SelectedIndex = -1; }
    private void ZoomIn(object sender, RoutedEventArgs e) => Map.Zoom(1.5);
    private void ZoomOut(object sender, RoutedEventArgs e) => Map.Zoom(1 / 1.5);
}
