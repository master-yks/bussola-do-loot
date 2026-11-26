using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using ChestCounter;
using ChestCounter.Ocr;
using Forms = System.Windows.Forms;

namespace ChestCounter.Gui;

public partial class MainWindow : Window
{
    private sealed record Row(string Name, string Value, string Tooltip);

    private sealed record OddsQuickRow(
        string Label,
        string Item,
        string Odds,
        Brush OddsBrush,
        string Luck,
        Brush BarBrush,
        GridLength FillStar,
        GridLength EmptyStar,
        string Tooltip);

    private sealed record OddsMatrixRow(
        string Label,
        string ItemName,
        string BaseRate,
        string SoloChance,
        string SquadChance,
        string Projections,
        string ExpectedText,
        Brush AccentBrush,
        GridLength FillStar,
        GridLength EmptyStar,
        string Tooltip);

    private sealed record MatchHistoryRow(
        string Number,
        string When,
        string Mode,
        string You,
        string Squad,
        string Details,
        Brush DetailsBrush,
        string Tooltip,
        string SearchKey,
        string KindSummary);

    private readonly bool _isVerifyMode;
    private LifetimeStats _stats = new();
    private OverlaySettings _overlay = new();
    private AppSettings _app = new();
    private OverlayServer? _server;
    private LiveChestCounter? _live;
    private InGameOverlay? _inGame;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _loadingSettings;
    private bool _scanning;
    private bool _windowReadyForBoundsSave;
    private int? _liveCount;
    private string _selectedProbKind = TreasureCatalog.Token;
    private string _oddsCacheKey = "";
    private List<MatchHistoryRow> _allHistoryRows = new();
    private readonly Dictionary<string, RadioButton> _presetRadios = new(StringComparer.OrdinalIgnoreCase);
    private bool _presetCardsBuilt;
    private bool _previewSimLive = true;
    private int _previewSimCount = 14;
    private int _previewBackdropIndex;
    private (LifetimeStats? Stats, int? Live, string ThText, bool ThLive, OverlayStatsSnapshot Snap) _overlaySnapCache;

    // Cached chart data & non-thrashing hover overlay elements (O(1) updates, zero Children.Clear on MouseMove!)
    private List<MatchRecord> _chartBars = new();
    private readonly List<(Border Rect, TextBlock? Label, bool IsTop)> _barVisuals = new();
    private int _hoveredBarIndex = -1;

    private double _probChartMaxChests = 10_000;
    private double _probChartMaxY = 0.1;
    private Line? _probCrosshairLine;
    private Ellipse? _probCrosshairDot;

    private List<(int MatchNum, DateTime TimeUtc, int MatchYou, long CumChests, double Prob)> _seasonProgPoints = new();
    private Line? _seasonCrosshairLine;
    private Ellipse? _seasonCrosshairDot;

    public MainWindow() : this(false) { }

    public MainWindow(bool isVerifyMode)
    {
        _isVerifyMode = isVerifyMode;
        _loadingSettings = true;
        InitializeComponent();
        OverlayUrlText.Text = AppChannel.OverlayUrl;
        if (AppChannel.IsTest)
        {
            Title = TitleBarProduct.Text = "Bússola do Loot · Testes";
        }
        if (isVerifyMode)
            _stats = CreatePreviewHistory();
        else
        {
            _stats = ChestCounterService.LoadStats();
            _overlay = OverlayConfig.Load();
            _app = AppConfig.Load();
        }

        ApplySmartInitialWindowBounds();

        PreviewHudView.IsPreview = true;
        _previewBackdropIndex = Math.Clamp(_app.PreviewBackdropIndex, 0, 3);
        RestorePreviewBackdropRadio(_previewBackdropIndex);
        SyncOverlayControlsFromModel();

        InGameOverlayCheck.IsChecked = _app.InGameOverlay;
        OverlayLockCheck.IsChecked = _app.OverlayLocked;
        TrayCheck.IsChecked = _app.MinimizeToTray;
        RestoreSelectedPeriod(_app.SelectedPeriodIndex);
        UpdateTitleHudButton();
        UpdateOverlayLockUi();
        UpdateOverlayCoordsUi();
        _loadingSettings = false;

        FooterText.Text = ChestCounterService.Credits;

        if (!_isVerifyMode)
        {
            StartOverlay();
            StartLive();
            SetupTray();
            if (_app.InGameOverlay) ShowInGame();
        }
        else
        {
            OverlayStatusText.Text = "Modo de verificação visual ativo.";
        }

        Render();
        RenderTreasureHour();
        UpdateDiagnosticsText();

        // Keep stats synced if external CLI or background process updates stats.json
        var syncTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        var lastWrite = isVerifyMode ? DateTime.MinValue : SafeStatsLastWrite();
        syncTimer.Tick += (_, _) =>
        {
            var curWrite = SafeStatsLastWrite();
            if (_scanning || curWrite == lastWrite) return;
            lastWrite = curWrite;
            _stats = ChestCounterService.LoadStats();
            Render();
            UpdateDiagnosticsText();
        };
        if (!_isVerifyMode) syncTimer.Start();

        // 1-second live countdown for Treasure Hour
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) =>
        {
            RenderTreasureHour();
            if (_overlay.ShowTreasureHour)
                PushInGameStats();
        };
        if (!_isVerifyMode) clock.Start();

        StateChanged += (_, _) =>
        {
            UpdateMaximizeIconAndFrame();
            if (WindowState == WindowState.Minimized && _app.MinimizeToTray && !_isVerifyMode)
            {
                Hide();
                return;
            }
            SaveWindowBounds();
        };

        Loaded += async (_, _) =>
        {
            _windowReadyForBoundsSave = true;
            UpdateMaximizeIconAndFrame();
            ApplyResponsiveLayout();
            if (!_isVerifyMode)
                await RunScanAsync(isAutoStartup: true);
        };
    }

    private static LifetimeStats CreatePreviewHistory()
    {
        var stats = new LifetimeStats();
        int[] counts = [9, 14, 8, 21, 16, 12, 19, 24];
        for (int i = 0; i < counts.Length; i++)
        {
            int count = counts[i];
            stats.AddMatch(new MatchChestCounts
            {
                MatchId = $"demo-{i + 1}",
                Time = ChestCounterService.SeasonStartUtc.AddDays(1).AddHours(i),
                Playlist = "Playlist_NoBuildBR_Duo",
                You = count,
                YourChestKinds = new() { ["common"] = count - 3, ["rare"] = 2, ["buried"] = 1 },
                Mates = [new() { Name = "Navegante Demo", Chests = count / 2 }],
                Unattributed = 1,
            });
        }
        return stats;
    }

    // ==================== SMART WINDOW GEOMETRY & RESPONSIVE LAYOUT ====================

    private void ApplySmartInitialWindowBounds()
    {
        var wa = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, wa.Width);
        MinHeight = Math.Min(MinHeight, wa.Height);
        double idealW = Math.Clamp(Math.Round(wa.Width * 0.86), MinWidth, Math.Max(MinWidth, Math.Min(1340, wa.Width - 24)));
        double idealH = Math.Clamp(Math.Round(wa.Height * 0.90), MinHeight, Math.Max(MinHeight, Math.Min(820, wa.Height - 20)));

        if (!_isVerifyMode && _app.WindowWidth is { } sw && _app.WindowHeight is { } sh && sw >= MinWidth && sh >= MinHeight)
        {
            Width = Math.Clamp(sw, MinWidth, wa.Width);
            Height = Math.Clamp(sh, MinHeight, wa.Height);

            double left = _app.WindowLeft ?? (wa.Left + Math.Round((wa.Width - Width) / 2));
            double top = _app.WindowTop ?? (wa.Top + Math.Round((wa.Height - Height) / 2));

            // Guarantee entire window titlebar and body start inside visible monitor WorkArea
            Left = Math.Clamp(left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
            Top = Math.Clamp(top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));

            if (_app.WindowMaximized)
                WindowState = WindowState.Maximized;
        }
        else
        {
            Width = idealW;
            Height = idealH;
            Left = wa.Left + Math.Round((wa.Width - Width) / 2);
            Top = wa.Top + Math.Round((wa.Height - Height) / 2);
        }
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e) => SaveWindowBounds();

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
        SaveWindowBounds();
    }

    private void SaveWindowBounds()
    {
        if (!_windowReadyForBoundsSave || _isVerifyMode || _loadingSettings) return;
        if (WindowState == WindowState.Minimized) return;

        _app.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            if (Left > -10000 && Top > -10000)
            {
                _app.WindowLeft = Left;
                _app.WindowTop = Top;
            }
            _app.WindowWidth = Width;
            _app.WindowHeight = Height;
        }
        else if (RestoreBounds is { Width: > 0, Height: > 0 } rb)
        {
            _app.WindowLeft = rb.Left;
            _app.WindowTop = rb.Top;
            _app.WindowWidth = rb.Width;
            _app.WindowHeight = rb.Height;
        }
        AppConfig.Save(_app);
    }

    private void UpdateMaximizeIconAndFrame()
    {
        bool max = WindowState == WindowState.Maximized;
        if (MaxRestoreIcon != null)
        {
            MaxRestoreIcon.Data = Geometry.Parse(max
                ? "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z"
                : "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");
            MaxRestoreButton.ToolTip = max ? "Restaurar tamanho" : "Maximizar";
        }

        if (WindowRootBorder != null)
        {
            WindowRootBorder.Padding = max ? GetMaximizedFramePadding() : new Thickness(0);
        }
    }

    private Thickness GetMaximizedFramePadding()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref mi) && GetWindowRect(hwnd, out var wr))
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                    double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                    double l = Math.Max(0, (mi.rcWork.Left - wr.Left) / sx);
                    double t = Math.Max(0, (mi.rcWork.Top - wr.Top) / sy);
                    double r = Math.Max(0, (wr.Right - mi.rcWork.Right) / sx);
                    double b = Math.Max(0, (wr.Bottom - mi.rcWork.Bottom) / sy);
                    return new Thickness(l, t, r, b);
                }
            }
        }
        catch { }
        return new Thickness(7);
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsInitialized || ToolbarGrid == null) return;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;

        // Outer content padding tightens slightly in compact windows
        MainContentGrid.Margin = w < 960 ? new Thickness(12, 9, 12, 8) : new Thickness(20, 11, 20, 9);
        TitleBarAuthorText.Visibility = w < 860 ? Visibility.Collapsed : Visibility.Visible;

        // 1) Navigation & Action Toolbar reflow
        bool stackedToolbar = w < 1230;
        if (stackedToolbar)
        {
            Grid.SetColumn(NavTabsBorder, 0);
            Grid.SetColumnSpan(NavTabsBorder, 3);
            Grid.SetRow(NavTabsBorder, 0);

            Grid.SetColumn(ControlsRightPanel, 0);
            Grid.SetColumnSpan(ControlsRightPanel, 3);
            Grid.SetRow(ControlsRightPanel, 1);
            ControlsRightPanel.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            Grid.SetColumn(NavTabsBorder, 0);
            Grid.SetColumnSpan(NavTabsBorder, 1);
            Grid.SetRow(NavTabsBorder, 0);

            Grid.SetColumn(ControlsRightPanel, 2);
            Grid.SetColumnSpan(ControlsRightPanel, 1);
            Grid.SetRow(ControlsRightPanel, 0);
            ControlsRightPanel.Margin = new Thickness(0);
        }

        // 2) Tab 1 (Visão Geral) responsive reflow
        bool compactOverview = w < 1060;
        double rightColW = w >= 1260 ? 382 : 344;

        if (compactOverview)
        {
            OverviewRightCol.Width = new GridLength(0);
            Grid.SetColumn(OverviewRightScroll, 0);
            Grid.SetRow(OverviewRightScroll, 1);
            OverviewRightScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            OverviewRightScroll.Margin = new Thickness(0, 12, 0, 0);
            OverviewLeftPanel.Margin = new Thickness(0);
            OverviewGrid.MinHeight = 0;
            OverviewGrid.Height = double.NaN;
        }
        else
        {
            OverviewRightCol.Width = new GridLength(rightColW);
            Grid.SetColumn(OverviewRightScroll, 1);
            Grid.SetRow(OverviewRightScroll, 0);
            OverviewRightScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            OverviewRightScroll.Margin = new Thickness(0);
            OverviewLeftPanel.Margin = new Thickness(0, 0, 14, 0);

            // Stretch cleanly to fill viewport when tall enough; become scrollable smoothly if window is shrunk vertically!
            double availH = OverviewScroll.ActualHeight;
            OverviewGrid.MinHeight = Math.Max(0, availH);
            OverviewGrid.Height = double.NaN;
        }

        // Hero Dual Panel & 4-KPI Strip adaptation
        bool ultraCompact = w < 880;
        bool twoColKpi = w < 940;
        HeroStatsStrip.Columns = twoColKpi ? 2 : 4;
        KpiAvgCard.BorderBrush = ResBrush("Stroke");
        KpiAvgCard.BorderThickness = twoColKpi ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        KpiPeriodCard.BorderThickness = twoColKpi ? new Thickness(1, 0, 0, 1) : new Thickness(1, 0, 0, 0);
        KpiBestCard.BorderThickness = twoColKpi ? new Thickness(0) : new Thickness(1, 0, 0, 0);
        if (KpiTokenCard != null) KpiTokenCard.BorderThickness = new Thickness(1, 0, 0, 0);
        SeasonHeroTotalText.FontSize = ultraCompact ? 52 : 68;
        ThisGameText.FontSize = ultraCompact ? 52 : 68;
        HeroChestImage.Width = HeroChestImage.Height = ultraCompact ? 84 : 112;
        HeroLiveStack.Margin = ultraCompact ? new Thickness(16, 12, 98, 10) : new Thickness(22, 14, 134, 12);
        ChartSubtitleHint.Visibility = w < 980 ? Visibility.Collapsed : Visibility.Visible;

        if (ultraCompact)
        {
            SquadCol0.Width = new GridLength(1, GridUnitType.Star);
            SquadCol1.Width = new GridLength(0);
            Grid.SetColumn(MatesCardBorder, 0);
            Grid.SetRow(MatesCardBorder, 1);
            SquadCardBorder.Margin = new Thickness(0, 0, 0, 8);
            MatesCardBorder.Margin = new Thickness(0);
        }
        else
        {
            SquadCol0.Width = new GridLength(1, GridUnitType.Star);
            SquadCol1.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(MatesCardBorder, 1);
            Grid.SetRow(MatesCardBorder, 0);
            SquadCardBorder.Margin = new Thickness(0, 0, 6, 0);
            MatesCardBorder.Margin = new Thickness(6, 0, 0, 0);
        }

        // 3) Tab 2 (Gráficos de Probabilidade) responsive reflow
        ProbKpiUniformGrid.Columns = w < 1020 ? 2 : 4;
        if (w < 1040)
        {
            ProbChartCol0.Width = new GridLength(1, GridUnitType.Star);
            ProbChartCol1.Width = new GridLength(0);
            Grid.SetColumn(SeasonProbCardBorder, 0);
            Grid.SetRow(SeasonProbCardBorder, 1);
            ProbCurveCardBorder.Margin = new Thickness(0, 0, 0, 10);
            SeasonProbCardBorder.Margin = new Thickness(0);
        }
        else
        {
            ProbChartCol0.Width = new GridLength(1.18, GridUnitType.Star);
            ProbChartCol1.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(SeasonProbCardBorder, 1);
            Grid.SetRow(SeasonProbCardBorder, 0);
            ProbCurveCardBorder.Margin = new Thickness(0, 0, 6, 0);
            SeasonProbCardBorder.Margin = new Thickness(6, 0, 0, 0);
        }

        // 4) Tab 3 (Estúdio de Personalização do Overlay) responsive reflow
        if (OverlayStudioGrid != null)
        {
            PresetCardsGrid.Columns = w < 860 ? 2 : 3;
            OverlayChecksUniformGrid.Columns = w < 800 ? 1 : 2;
            if (w < 1060)
            {
                OverlayStudioLeftCol.Width = new GridLength(1, GridUnitType.Star);
                OverlayStudioRightCol.Width = new GridLength(0);
                Grid.SetColumn(OverlayStudioRightPanel, 0);
                Grid.SetRow(OverlayStudioRightPanel, 1);
                OverlayStudioLeftPanel.Margin = new Thickness(0, 0, 0, 12);
            }
            else
            {
                OverlayStudioLeftCol.Width = new GridLength(w >= 1260 ? 436 : 386);
                OverlayStudioRightCol.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(OverlayStudioRightPanel, 1);
                Grid.SetRow(OverlayStudioRightPanel, 0);
                OverlayStudioLeftPanel.Margin = new Thickness(0, 0, 14, 0);
            }
        }

        // 5) Tab 4 (Partidas & Sistema) responsive reflow
        if (w < 1060)
        {
            HistoryRightCol.Width = new GridLength(0);
            Grid.SetColumn(HistoryRightScroll, 0);
            Grid.SetRow(HistoryRightScroll, 1);
            HistoryRightScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HistoryRightScroll.Margin = new Thickness(0, 12, 0, 0);
            HistoryLeftBorder.Margin = new Thickness(0);
            HistoryLeftBorder.MaxHeight = 460;
            HistoryGrid.Height = double.NaN;
        }
        else
        {
            HistoryRightCol.Width = new GridLength(rightColW);
            Grid.SetColumn(HistoryRightScroll, 1);
            Grid.SetRow(HistoryRightScroll, 0);
            HistoryRightScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HistoryRightScroll.Margin = new Thickness(0);
            HistoryLeftBorder.Margin = new Thickness(0, 0, 14, 0);
            HistoryLeftBorder.MaxHeight = double.PositiveInfinity;

            double availHistH = HistoryScroll.ActualHeight;
            HistoryGrid.Height = availHistH >= 360 ? availHistH : double.NaN;
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleHudQuickButton_Click(object sender, RoutedEventArgs e) =>
        SetInGameOverlay(!_app.InGameOverlay);

    private void TitleHudStyleButton_Click(object sender, RoutedEventArgs e) =>
        TabOverlay.IsChecked = true;

    private void TitleHudLockButton_Click(object sender, RoutedEventArgs e) =>
        SetOverlayLocked(!_app.OverlayLocked);

    private void UpdateTitleHudButton()
    {
        if (TitleHudQuickButton == null) return;
        bool on = _app.InGameOverlay;
        TitleHudQuickButton.Content = on ? "● HUD no Jogo: ON" : "HUD no Jogo: OFF";
        TitleHudQuickButton.Foreground = ResBrush(on ? "Gold" : "Steel");
        TitleHudQuickButton.BorderBrush = ResBrush(on ? "GoldBorder" : "Stroke");
    }

    private void UpdateOverlayLockUi()
    {
        bool locked = _app.OverlayLocked;
        if (TitleHudLockButton != null)
        {
            TitleHudLockButton.Content = locked ? "🔒 HUD Fixo" : "🔓 HUD Móvel";
            TitleHudLockButton.Foreground = ResBrush(locked ? "Emerald" : "Steel");
            TitleHudLockButton.BorderBrush = ResBrush(locked ? "Emerald" : "Stroke");
            TitleHudLockButton.ToolTip = locked
                ? "Overlay fixado: cliques atravessam direto para o Fortnite. Clique para destravar e mover (Ctrl+Shift+L)."
                : "Overlay destravado: pode ser arrastado. Clique para fixar e evitar cliques acidentais no jogo (Ctrl+Shift+L).";
        }
        if (OverlayLockCheck != null)
        {
            bool prev = _loadingSettings;
            _loadingSettings = true;
            OverlayLockCheck.IsChecked = locked;
            _loadingSettings = prev;
        }
        if (OverlayLockButton != null)
        {
            OverlayLockButton.Content = locked ? "🔓 Destravar Overlay" : "🔒 Fixar Overlay";
            OverlayLockButton.Foreground = ResBrush(locked ? "Emerald" : "Gold");
            OverlayLockButton.BorderBrush = ResBrush(locked ? "Emerald" : "GoldBorder");
        }
    }

    public void ExportWorkspaceSnapshots(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        void Capture(string fileName)
        {
            ApplyResponsiveLayout();
            UpdateLayout();
            int w = Math.Max(1, (int)ActualWidth);
            int h = Math.Max(1, (int)ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(outputDir, fileName));
            enc.Save(fs);
        }

        var savedOverlay = _overlay.Clone();

        // 1) Standard Desktop Fit (1300x734 -> fits 1920x1080@125% = 1536x816 WorkArea!)
        Width = 1300;
        Height = 734;
        TabOverview.IsChecked = true;
        UpdateLayout();
        ApplyResponsiveLayout();
        UpdateLayout();
        RebuildMatchBarChart();
        Capture("tab1_overview.png");

        // Simulated hover over a match bar in Tab 1
        if (_chartBars.Count > 0)
        {
            int simIdx = Math.Max(0, _chartBars.Count - 3);
            ShowMatchBarHover(simIdx, new Point((simIdx + 0.5) * (MatchChartCanvas.ActualWidth / _chartBars.Count), 60));
            Capture("tab1_overview_hover.png");
            ClearMatchBarHover();
        }

        TabProbabilities.IsChecked = true;
        UpdateLayout();
        ApplyResponsiveLayout();
        RenderProbabilityWorkspace();
        UpdateLayout();
        RebuildProbabilityCurveChart();
        RebuildSeasonProgressionChart();
        UpdateProbCurveHover(ProbCurveCanvas.ActualWidth * 0.55);
        UpdateSeasonProbHover(SeasonProbCanvas.ActualWidth * 0.65);
        Capture("tab2_probabilities_hover.png");

        // Overlay Studio Tab (Sky + Default Fortnite Tactical)
        TabOverlay.IsChecked = true;
        EnsurePresetCardsBuilt();
        ApplyOverlayPresetInternal(OverlayPresets.All[0], persist: false);
        SceneSkyRadio.IsChecked = true;
        UpdateLayout();
        ApplyResponsiveLayout();
        UpdateLayout();
        RebuildPreviewSceneBackdrop();
        Capture("tab_overlay_studio_sky.png");

        // Overlay Studio Tab (Snow + Ghost 0% Backdrop legibility test!)
        ApplyOverlayPresetInternal(OverlayPresets.All[1], persist: false);
        SceneSnowRadio.IsChecked = true;
        UpdateLayout();
        RebuildPreviewSceneBackdrop();
        Capture("tab_overlay_studio_snow.png");

        // Restore user's overlay settings
        _overlay = savedOverlay;
        SyncOverlayControlsFromModel();
        RefreshPreviewHud();
        SceneSkyRadio.IsChecked = true;

        TabHistory.IsChecked = true;
        UpdateLayout();
        ApplyResponsiveLayout();
        UpdateLayout();
        UpdateDiagnosticsText();
        Capture("tab3_history.png");

        TabMap.IsChecked = true;
        UpdateLayout();
        Capture("tab_map.png");
        var planner = (IslandPlanner)MapHost.Child;
        planner.RouteTab.IsChecked = true;
        planner.Map.SuggestStops(new(.2993, .5521), 8);
        UpdateLayout();
        Capture("tab_map_route.png");
        using (var png = File.Create(Path.Combine(outputDir, "mapa-rota.png"))) planner.ExportView(png);

        // 9-Preset Visual Showcase Sheet (Sky & Dark split backdrop)
        ExportPresetShowcaseSheet(Path.Combine(outputDir, "overlay_9_presets_showcase.png"));

        // 2) Compact Remodeled Window Test (800x560 -> half-screen / small window remodeling test!)
        Width = 800;
        Height = 560;
        TabOverlay.IsChecked = true;
        UpdateLayout();
        ApplyResponsiveLayout();
        UpdateLayout();
        RebuildPreviewSceneBackdrop();
        Capture("compact_800x560_overlay_studio.png");

        TabMap.IsChecked = true;
        UpdateLayout();
        Capture("compact_800x560_map.png");

        TabOverview.IsChecked = true;
    }

    private void ExportPresetShowcaseSheet(string filePath)
    {
        var snap = BuildOverlayStatsSnapshot();
        if (string.IsNullOrEmpty(snap.TreasureHourText))
            snap = snap with { TreasureHourText = "★ 01:14:22", TreasureHourLive = true };

        var sheet = new Border
        {
            Width = 1140,
            Background = HexBrush("#090C14", Brushes.Black),
            Padding = new Thickness(20),
        };
        var grid = new UniformGrid { Columns = 3, Rows = 3 };
        for (int i = 0; i < OverlayPresets.All.Count; i++)
        {
            var p = OverlayPresets.All[i];
            var cellBg = (i % 2 == 0)
                ? new LinearGradientBrush(Color.FromRgb(0x63, 0xB8, 0xFA), Color.FromRgb(0xDB, 0xF0, 0xFF), new Point(0, 0), new Point(1, 1))
                : new LinearGradientBrush(Color.FromRgb(0x16, 0x24, 0x36), Color.FromRgb(0x0B, 0x10, 0x1B), new Point(0, 0), new Point(1, 1));

            var card = new Border
            {
                Margin = new Thickness(7),
                CornerRadius = new CornerRadius(11),
                BorderBrush = HexBrush("#38FFFFFF", Brushes.Gray),
                BorderThickness = new Thickness(1),
                Background = cellBg,
                Padding = new Thickness(12, 10, 12, 12),
                MinHeight = 148,
            };
            var sp = new StackPanel();
            var lbl = new Border
            {
                Background = HexBrush("#B807090E", Brushes.Black),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = $"{p.Name}  ·  {(i % 2 == 0 ? "Fundo Claro" : "Fundo Escuro")}",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                }
            };
            var hud = new OverlayHudView { IsPreview = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hud.ApplySettings(p.Create());
            hud.SetStats(snap);
            hud.SetLive(18, allowPulse: false);

            sp.Children.Add(lbl);
            sp.Children.Add(hud);
            card.Child = sp;
            grid.Children.Add(card);
        }
        sheet.Child = grid;
        sheet.Measure(new Size(1140, 1200));
        sheet.Arrange(new Rect(0, 0, sheet.DesiredSize.Width, sheet.DesiredSize.Height));
        sheet.UpdateLayout();

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(sheet.DesiredSize.Width),
            (int)Math.Ceiling(sheet.DesiredSize.Height),
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(sheet);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        enc.Save(fs);
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private static DateTime SafeStatsLastWrite()
    {
        try { return File.Exists(ChestCounterService.StatsPath) ? File.GetLastWriteTimeUtc(ChestCounterService.StatsPath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private Brush ResBrush(string key) => (Brush)FindResource(key);

    private static Brush HexBrush(string hex, Brush fallback)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch { return fallback; }
    }

    // ==================== OVERLAY SERVER & LIVE OCR ====================

    private void StartOverlay()
    {
        _server = new OverlayServer()
        {
            ConfigProvider = OverlayConfig.Json,
            StatsBuilder = live => ChestCounterService.OverlayJson(_stats, live),
        };
        try
        {
            _server.Start();
            OverlayStatusText.Text = $"✓ Servidor OBS ativo em {AppChannel.OverlayUrl} (sincronizado ao vivo com seus estilos).";
        }
        catch
        {
            _server = null;
            OverlayStatusText.Text = $"Porta {AppChannel.OverlayPort} já em uso por outra instância local.";
        }
    }

    private void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_live == null) StartLive(); else StopLive();
    }

    private void StartLive()
    {
        if (_live != null) return;
        var live = new LiveChestCounter();

        live.LiveChanged += n => Dispatcher.BeginInvoke(() =>
        {
            if (n == null)
                _stats = ChestCounterService.LoadStats();

            _liveCount = n;
            if (_server != null) _server.LiveCount = n;
            _inGame?.SetLive(n);
            RefreshPreviewHud(allowPulse: false);

            ThisGameText.Text = n?.ToString() ?? "—";
            ThisGameSubText.Text = n == null
                ? (live.IsInPlayAgainQueue
                    ? "Partida salva · buscando próxima (Jogar novamente)…"
                    : "Aguardando partida no Fortnite…")
                : "baús abertos ao vivo (já somados à temporada)";
            LiveDotEllipse.Fill = ResBrush(n == null ? "Gem" : "Emerald");

            if (!_scanning)
            {
                StatusText.Text = n == null
                    ? (live.IsInPlayAgainQueue
                        ? "✓ Partida finalizada no Jogar novamente · buscando próxima partida…"
                        : "Monitoramento ativo · aguardando nova partida…")
                    : $"● Em partida ao vivo · {n} {(n == 1 ? "baú aberto" : "baús abertos")}";
            }

            _oddsCacheKey = "";
            Render();
        });

        live.StatsUpdated += (run, updatedStats) => Dispatcher.BeginInvoke(() =>
        {
            _stats = updatedStats;
            _liveCount = live.CurrentLiveCount;
            if (_server != null) _server.LiveCount = _liveCount;
            _inGame?.SetLive(_liveCount, allowPulse: false);
            _oddsCacheKey = "";
            Render();
            UpdateDiagnosticsText();
            var season = _stats.Over(ChestCounterService.SeasonStartUtc);
            if (run.NewMatches > 0 || run.RescuedViaOcr > 0)
            {
                StatusText.Text = live.IsInPlayAgainQueue
                    ? $"✓ Partida finalizada no Jogar novamente! Temporada: {season.YourChests:N0} baús ({season.Matches} partidas)."
                    : $"✓ Partida finalizada e somada! Total na temporada: {season.YourChests:N0} baús ({season.Matches} partidas).";
            }
            else if (run.UpgradedFromLive > 0)
            {
                StatusText.Text = $"✓ Replay consolidado com precisão! Total na temporada: {season.YourChests:N0} baús ({season.Matches} partidas).";
            }
            else if (run.SkippedMode > 0)
            {
                StatusText.Text = "Partida finalizada fora dos modos válidos da Caça ao Tesouro (ex: Ranqueada/Reload/Criativo).";
            }
        });

        live.PopupRead += text => Dispatcher.BeginInvoke(() =>
        {
            if (!_scanning)
                StatusText.Text = $"Último aviso de EXP lido: “{text}”";
        });

        live.Start();
        _live = live;

        if (live.Available)
        {
            LiveButton.Content = "● Ao vivo ligado";
            LiveButton.Foreground = ResBrush("Emerald");
            StatusText.Text = $"OCR ({live.LanguageTag}) + Monitor de Replays ativos.";
        }
        else
        {
            LiveButton.Content = "● Replays automáticos";
            LiveButton.Foreground = ResBrush("Gold");
            StatusText.Text = "Monitor de Replays ativo (instale OCR pt-BR no Windows para leitura de tela).";
        }
    }

    private void StopLive()
    {
        _live?.Stop();
        _live = null;
        _liveCount = null;
        if (_server != null) _server.LiveCount = null;
        _inGame?.SetLive(null);
        RefreshPreviewHud(allowPulse: false);
        LiveButton.Content = "Ao vivo pausado";
        LiveButton.Foreground = ResBrush("Muted");
        LiveDotEllipse.Fill = ResBrush("Muted");
        ThisGameText.Text = "—";
        ThisGameSubText.Text = "Monitoramento em pausa";
        _oddsCacheKey = "";
        Render();
    }

    // ==================== IN-GAME OVERLAY, STUDIO CUSTOMIZER & DARK SYSTEM TRAY ====================

    private void InGameOverlay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        SetInGameOverlay(InGameOverlayCheck.IsChecked == true);
    }

    private void SetInGameOverlay(bool on)
    {
        _app.InGameOverlay = on;
        AppConfig.Save(_app);
        _loadingSettings = true;
        InGameOverlayCheck.IsChecked = on;
        _loadingSettings = false;
        UpdateTitleHudButton();
        if (on) ShowInGame(); else HideInGame();
    }

    private void ShowInGame()
    {
        _inGame ??= CreateInGame();
        _inGame.ApplySettings(_overlay, _app.OverlayAnchor);
        _inGame.EnsureOnScreen();
        _inGame.Show();
        _inGame.SetLocked(_app.OverlayLocked, showToast: false);
        _inGame.SetLive(_liveCount, allowPulse: false);
        PushInGameStats();
        UpdateOverlayCoordsUi();
    }

    private InGameOverlay CreateInGame()
    {
        var o = new InGameOverlay();
        o.ApplySettings(_overlay, _app.OverlayAnchor);
        if (_app.OverlayAnchor == "free" && _app.OverlayLeft is { } l && _app.OverlayTop is { } t)
        {
            o.Left = l;
            o.Top = t;
            o.EnsureOnScreen();
        }
        else if (_app.OverlayAnchor != "free")
        {
            o.SnapToAnchor(_app.OverlayAnchor);
        }
        o.SetLocked(_app.OverlayLocked, showToast: false);
        o.Moved += () =>
        {
            _app.OverlayAnchor = "free";
            _app.OverlayLeft = o.Left;
            _app.OverlayTop = o.Top;
            AppConfig.Save(_app);
            UpdateOverlayCoordsUi();
        };
        return o;
    }

    private void HideInGame() => _inGame?.Hide();

    private void OverlayAnchor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string anchor }) return;
        _app.OverlayAnchor = anchor;
        _inGame ??= CreateInGame();
        _inGame.SnapToAnchor(anchor);
        _app.OverlayLeft = _inGame.Left;
        _app.OverlayTop = _inGame.Top;
        if (!_app.InGameOverlay) SetInGameOverlay(true);
        else AppConfig.Save(_app);
        UpdateOverlayCoordsUi();
        StatusText.Text = $"✓ Overlay posicionado ({((Button)sender).Content}).";
    }

    private void UpdateOverlayCoordsUi()
    {
        if (OverlayCoordsText == null) return;
        string anchorName = _app.OverlayAnchor switch
        {
            "top_left" => "Topo Esquerdo",
            "top_center" => "Topo Central",
            "beside_minimap" => "Ao Lado do Minimapa",
            "below_minimap" => "Abaixo do Minimapa",
            "bottom_left" => "Base Esquerda",
            "bottom_right" => "Base Direita",
            _ => _app.OverlayLeft is { } lx && _app.OverlayTop is { } ty
                ? $"Livre (X: {lx:0}, Y: {ty:0})"
                : "Posição Livre",
        };
        OverlayCoordsText.Text = anchorName;

        Button[] anchorBtns = [AnchorTopLeftBtn, AnchorTopCenterBtn, AnchorBesideMapBtn, AnchorBelowMapBtn, AnchorBottomLeftBtn, AnchorBottomRightBtn];
        foreach (var btn in anchorBtns)
        {
            if (btn == null) continue;
            bool sel = string.Equals(btn.Tag as string, _app.OverlayAnchor, StringComparison.Ordinal);
            btn.BorderBrush = ResBrush(sel ? "Gold" : "Stroke");
            btn.Foreground = ResBrush(sel ? "Gold" : "Text");
        }
    }

    private void OverlayLock_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        SetOverlayLocked(OverlayLockCheck.IsChecked == true);
    }

    private void OverlayLockButton_Click(object sender, RoutedEventArgs e) =>
        SetOverlayLocked(!_app.OverlayLocked);

    private OverlayStatsSnapshot BuildOverlayStatsSnapshot()
    {
        var now = DateTime.UtcNow;
        var hour = TreasureCatalog.CurrentOrNext(now);
        bool thLive = hour != null && TreasureCatalog.IsLive(hour, now);
        string thText = "";
        if (hour != null)
        {
            var left = (thLive ? hour.EndUtc : hour.StartUtc) - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string clock = left.TotalHours >= 24
                ? $"{(int)left.TotalDays}d {left.Hours:00}h"
                : $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
            thText = thLive ? $"★ {clock}" : clock;
        }

        if (ReferenceEquals(_overlaySnapCache.Stats, _stats)
            && _overlaySnapCache.Live == _liveCount
            && _overlaySnapCache.ThLive == thLive
            && _overlaySnapCache.ThText == thText)
        {
            return _overlaySnapCache.Snap;
        }

        var season = _stats.Over(ChestCounterService.SeasonStartUtc);
        long seasonLive = season.YourChests + Math.Max(0, _liveCount ?? 0);
        double tokenProb = TreasureCatalog.ChanceOfAtLeastOne(137_500, seasonLive);
        string tokenStr = TreasureCatalog.FormatChance(tokenProb, highPrecision: true);

        int? lastMatchYou = _stats.Records.Count > 0 ? _stats.Records[^1].You : null;
        int best = 0;
        foreach (var r in _stats.Records)
            if (r.You > best) best = r.You;
        double avg = season.Matches > 0 ? season.AvgYou : _stats.AvgYou;

        var snap = new OverlayStatsSnapshot(avg, lastMatchYou, best, seasonLive, tokenStr, thText, thLive);
        _overlaySnapCache = (_stats, _liveCount, thText, thLive, snap);
        return snap;
    }

    private void PushInGameStats()
    {
        bool needInGame = _inGame != null && _inGame.IsVisible;
        bool needPreview = PreviewHudView != null && TabOverlay?.IsChecked == true;
        if (!needInGame && !needPreview) return;
        var snap = BuildOverlayStatsSnapshot();
        if (needInGame) _inGame!.SetStats(snap);
        if (needPreview) PreviewHudView!.SetStats(snap);
    }

    private void ToggleInGameFromHotkey() => SetInGameOverlay(!_app.InGameOverlay);

    private void ToggleInGameLock() => SetOverlayLocked(!(_inGame?.Locked ?? _app.OverlayLocked));

    private void SetOverlayLocked(bool locked)
    {
        _app.OverlayLocked = locked;
        AppConfig.Save(_app);
        _inGame?.SetLocked(locked, showToast: true);
        UpdateOverlayLockUi();
        StatusText.Text = locked
            ? "🔒 Overlay fixado (cliques atravessam até o Fortnite · Ctrl+Shift+L para destravar)."
            : "🔓 Overlay destravado (arraste pelo canto para reposicionar · Ctrl+Shift+L para fixar).";
    }

    private sealed class DarkTrayColorTable : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(18, 21, 31);
        public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(18, 21, 31);
        public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(18, 21, 31);
        public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(18, 21, 31);
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(56, 65, 90);
        public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(88, 245, 185, 66);
        public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(35, 41, 58);
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(38, 44, 62);
        public override System.Drawing.Color SeparatorLight => System.Drawing.Color.FromArgb(38, 44, 62);
    }

    private void SetupTray()
    {
        try
        {
            RefreshTrayIcon();
            DpiChanged += (_, _) => RefreshTrayIcon();
            _tray = new Forms.NotifyIcon
            {
                Text = "Bússola do Loot · master-yks",
                Visible = true,
                Icon = _trayIcon,
            };
            var menu = new Forms.ContextMenuStrip
            {
                Renderer = new Forms.ToolStripProfessionalRenderer(new DarkTrayColorTable()),
                ShowImageMargin = false,
                ForeColor = System.Drawing.Color.FromArgb(244, 242, 237),
                BackColor = System.Drawing.Color.FromArgb(18, 21, 31),
            };
            menu.Items.Add("Abrir Bússola do Loot", null, (_, _) => Dispatcher.BeginInvoke(BringToFront));
            menu.Items.Add("Alternar HUD no Jogo (Ctrl+Shift+O)", null, (_, _) => Dispatcher.BeginInvoke(ToggleInGameFromHotkey));
            menu.Items.Add("Travar/Destravar HUD (Ctrl+Shift+L)", null, (_, _) => Dispatcher.BeginInvoke(ToggleInGameLock));
            menu.Items.Add("Trocar Estilo do HUD (Ctrl+Shift+M)", null, (_, _) => Dispatcher.BeginInvoke(CycleOverlayPreset));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Sair", null, (_, _) => Dispatcher.BeginInvoke(Close));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(BringToFront);
        }
        catch { _tray = null; }
    }

    private void RefreshTrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("Assets/token.ico", UriKind.Relative)).Stream;
        int size = Math.Max(16, (int)Math.Round(16 * VisualTreeHelper.GetDpi(this).DpiScaleX));
        var icon = new System.Drawing.Icon(stream, size, size);
        if (_tray != null) _tray.Icon = icon;
        _trayIcon?.Dispose();
        _trayIcon = icon;
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _app.MinimizeToTray = TrayCheck.IsChecked == true;
        AppConfig.Save(_app);
    }

    // ==================== OVERLAY STUDIO CONTROLS & LIVE PREVIEW ====================

    private void EnsurePresetCardsBuilt()
    {
        if (_presetCardsBuilt) return;
        BuildPresetCardsUi();
        _presetCardsBuilt = true;
        bool prev = _loadingSettings;
        _loadingSettings = true;
        foreach (var kv in _presetRadios)
            kv.Value.IsChecked = string.Equals(kv.Key, _overlay.PresetId, StringComparison.OrdinalIgnoreCase);
        _loadingSettings = prev;
    }

    private void BuildPresetCardsUi()
    {
        PresetCardsGrid.Children.Clear();
        _presetRadios.Clear();

        var snapshot = BuildOverlayStatsSnapshot();
        foreach (var p in OverlayPresets.All)
        {
            var stack = new StackPanel();
            var sample = new OverlayHudView { IsPreview = true, IsHitTestVisible = false };
            sample.ApplySettings(p.Create());
            sample.SetStats(snapshot);
            sample.SetLive(18, allowPulse: false);
            var tile = new Border
            {
                Height = 52, Padding = new Thickness(6), CornerRadius = new CornerRadius(5),
                Background = new LinearGradientBrush(Color.FromRgb(61, 83, 102), Color.FromRgb(21, 33, 46), 25),
                Child = new Viewbox { Child = sample, StretchDirection = StretchDirection.DownOnly }
            };
            stack.Children.Add(tile);
            stack.Children.Add(new TextBlock
            {
                Text = p.Name, Foreground = ResBrush("Text"), FontWeight = FontWeights.SemiBold,
                FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });

            var rb = new RadioButton
            {
                Style = (Style)FindResource("PresetCard"),
                GroupName = "hudpreset",
                Tag = p.Id,
                Content = stack,
                ToolTip = $"{p.Name}\n{p.Subtitle}",
            };
            System.Windows.Automation.AutomationProperties.SetName(rb, p.Name);
            rb.Click += (_, _) => ApplyOverlayPresetInternal(p, persist: true);

            _presetRadios[p.Id] = rb;
            PresetCardsGrid.Children.Add(rb);
        }
    }

    private void SyncOverlayControlsFromModel()
    {
        bool prev = _loadingSettings;
        _loadingSettings = true;

        TitleBox.Text = _overlay.Title;
        AccentBox.Text = _overlay.Accent;
        SecondaryBox.Text = _overlay.SecondaryAccent;
        BgHexBox.Text = _overlay.BackgroundHex;

        switch (_overlay.Layout)
        {
            case "pill": LayoutPillRadio.IsChecked = true; break;
            case "micro": LayoutMicroRadio.IsChecked = true; break;
            case "vertical": LayoutVerticalRadio.IsChecked = true; break;
            case "card": LayoutCardRadio.IsChecked = true; break;
            default: LayoutTacticalRadio.IsChecked = true; break;
        }

        switch (_overlay.Chrome)
        {
            case "capsule": ChromeCapsuleRadio.IsChecked = true; break;
            case "glass": ChromeGlassRadio.IsChecked = true; break;
            case "sharp": ChromeSharpRadio.IsChecked = true; break;
            case "none": ChromeNoneRadio.IsChecked = true; break;
            default: ChromeAngledRadio.IsChecked = true; break;
        }

        switch (_overlay.FontStyle)
        {
            case "modern": FontModernRadio.IsChecked = true; break;
            case "mono": FontMonoRadio.IsChecked = true; break;
            default: FontFortniteRadio.IsChecked = true; break;
        }

        BgOpacitySlider.Value = Math.Round(_overlay.BgOpacity * 100);
        TextStrokeSlider.Value = Math.Round(_overlay.TextStroke * 10, 1);
        ScaleSlider.Value = Math.Round(_overlay.Scale * 100);
        BorderStrengthSlider.Value = Math.Round(_overlay.BorderStrength * 100);
        HudOpacitySlider.Value = Math.Round(_overlay.HudOpacity * 100);

        HudShowIconCheck.IsChecked = _overlay.ShowIcon;
        HudShowLiveCheck.IsChecked = _overlay.ShowLive;
        HudShowSeasonCheck.IsChecked = _overlay.ShowSeason;
        HudShowTokenCheck.IsChecked = _overlay.ShowToken;
        HudShowAvgCheck.IsChecked = _overlay.ShowAvg;
        HudShowLastCheck.IsChecked = _overlay.ShowLast;
        HudShowBestCheck.IsChecked = _overlay.ShowBest;
        HudShowHourCheck.IsChecked = _overlay.ShowTreasureHour;
        HudShowLabelsCheck.IsChecked = _overlay.ShowLabels;
        HudShowHeaderCheck.IsChecked = _overlay.ShowHeader;
        HudAccentBarCheck.IsChecked = _overlay.AccentBar;
        HudTextShadowCheck.IsChecked = _overlay.TextShadow;
        HudPulseCheck.IsChecked = _overlay.PulseOnChest;
        HudDimIdleCheck.IsChecked = _overlay.DimWhenIdle;
        HudHideIdleCheck.IsChecked = _overlay.HideWhenIdle;
        SyncObsCheck.IsChecked = _overlay.SyncObsStyle;

        foreach (var kv in _presetRadios)
            kv.Value.IsChecked = string.Equals(kv.Key, _overlay.PresetId, StringComparison.OrdinalIgnoreCase);

        UpdateOverlaySliderLabelsAndSwatches();
        _loadingSettings = prev;
    }

    private void UpdateOverlaySliderLabelsAndSwatches()
    {
        BgOpacityValueText.Text = _overlay.Chrome == "none" ? "0% (Puro)" : $"{Math.Round(_overlay.BgOpacity * 100):0}%";
        TextStrokeValueText.Text = $"{_overlay.TextStroke:0.0} px";
        ScaleValueText.Text = $"{Math.Round(_overlay.Scale * 100):0}%";
        BorderStrengthValueText.Text = $"{Math.Round(_overlay.BorderStrength * 100):0}%";
        HudOpacityValueText.Text = $"{Math.Round(_overlay.HudOpacity * 100):0}%";

        AccentSwatch.Background = HexBrush(_overlay.Accent, ResBrush("Gold"));
        SecondarySwatch.Background = HexBrush(_overlay.SecondaryAccent, ResBrush("Cyan"));
        BgColorSwatch.Background = HexBrush(_overlay.BackgroundHex, ResBrush("Card"));

        var matchPreset = OverlayPresets.All.FirstOrDefault(p => string.Equals(p.Id, _overlay.PresetId, StringComparison.OrdinalIgnoreCase));
        OverlayActivePresetBadgeText.Text = matchPreset != null ? matchPreset.Name : "✨ Estilo Personalizado";
        if (TitleHudStyleButton != null)
            TitleHudStyleButton.Content = matchPreset != null ? $"🎨 {matchPreset.Name.Split(' ')[0]} Estilo" : "🎨 Custom";
    }

    private void ApplyOverlayPresetInternal(OverlayPresetInfo preset, bool persist)
    {
        string customTitle = _overlay.Title;
        bool hideIdle = _overlay.HideWhenIdle;
        bool syncObs = _overlay.SyncObsStyle;

        var next = preset.Create();
        next.Title = string.IsNullOrWhiteSpace(customTitle) ? "Bússola do Loot" : customTitle;
        next.HideWhenIdle = hideIdle;
        next.SyncObsStyle = syncObs;
        next.Normalize();

        _overlay = next;
        SyncOverlayControlsFromModel();
        ApplyLiveOverlayChanges(persist);
        if (persist)
            StatusText.Text = $"✓ Estilo aplicado: {preset.Name} — {preset.Subtitle}";
    }

    private void CycleOverlayPreset()
    {
        var next = OverlayPresets.NextAfter(_overlay.PresetId);
        ApplyOverlayPresetInternal(next, persist: true);
    }

    private void CyclePresetButton_Click(object sender, RoutedEventArgs e) => CycleOverlayPreset();

    private void ResetDefaultOverlayStyle_Click(object sender, RoutedEventArgs e)
    {
        _overlay = OverlayPresets.All[0].Create();
        SyncOverlayControlsFromModel();
        ApplyLiveOverlayChanges(persist: true);
        StatusText.Text = "✓ Overlay restaurado para o padrão recomendado (⚡ Fortnite Tático).";
    }

    private void MarkCustomAndCommit()
    {
        _overlay.PresetId = "custom";
        _overlay.Normalize();
        bool prev = _loadingSettings;
        _loadingSettings = true;
        foreach (var kv in _presetRadios) kv.Value.IsChecked = false;
        // Re-sync checkboxes if Normalize() enforced at least ShowLive = true
        HudShowLiveCheck.IsChecked = _overlay.ShowLive;
        _loadingSettings = prev;

        UpdateOverlaySliderLabelsAndSwatches();
        ApplyLiveOverlayChanges(persist: true);
    }

    private void ApplyLiveOverlayChanges(bool persist)
    {
        RefreshPreviewHud(allowPulse: false);
        _inGame?.ApplySettings(_overlay, _app.OverlayAnchor);
        PushInGameStats();
        if (persist && !_isVerifyMode)
            SaveOverlay();
    }

    private void RefreshPreviewHud(bool allowPulse = false)
    {
        if (PreviewHudView == null) return;
        PreviewHudView.ApplySettings(_overlay);
        PreviewHudView.SetStats(BuildOverlayStatsSnapshot());
        int? simLive = _previewSimLive ? _previewSimCount : null;
        PreviewHudView.SetLive(simLive, allowPulse);

        if (PreviewToggleStateButton != null)
        {
            bool isLiveShown = simLive != null;
            PreviewToggleStateButton.Content = isLiveShown ? "Mudar para lobby" : "Mudar para partida";
            PreviewToggleStateButton.Foreground = ResBrush(isLiveShown ? "Emerald" : "Steel");
        }
        if (PreviewHintText != null)
        {
            PreviewHintText.Text = (_overlay.HideWhenIdle && simLive == null)
                ? "Lobby · contador oculto no jogo"
                : simLive == null ? "Lobby · em espera" : "Em partida";
        }
    }

    private void PreviewSimPulseButton_Click(object sender, RoutedEventArgs e)
    {
        _previewSimLive = true;
        _previewSimCount++;
        RefreshPreviewHud(allowPulse: false);
        PreviewHudView.TriggerSimulatedPulse(1);
    }

    private void PreviewToggleStateButton_Click(object sender, RoutedEventArgs e)
    {
        _previewSimLive = !_previewSimLive;
        RefreshPreviewHud(allowPulse: false);
    }

    private void RestorePreviewBackdropRadio(int index)
    {
        switch (index)
        {
            case 1: SceneSnowRadio.IsChecked = true; break;
            case 2: SceneIslandRadio.IsChecked = true; break;
            case 3: SceneNightRadio.IsChecked = true; break;
            default: SceneSkyRadio.IsChecked = true; break;
        }
    }

    private void PreviewBackdrop_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int idx)) return;
        _previewBackdropIndex = idx;
        if (!_loadingSettings && !_isVerifyMode)
        {
            _app.PreviewBackdropIndex = idx;
            AppConfig.Save(_app);
        }
        RebuildPreviewSceneBackdrop();
    }

    private void RebuildPreviewSceneBackdrop()
    {
        if (PreviewViewportBorder == null) return;
        if (TabOverlay?.IsChecked != true && !_isVerifyMode) return;
        string scene = _previewBackdropIndex switch { 1 => "snow", 2 => "island", 3 => "night", _ => "sky" };
        if (Equals(PreviewViewportBorder.Tag, scene)) return;
        var uri = new Uri($"pack://application:,,,/Assets/Studio/{scene}.jpg");
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = uri;
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        var background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        background.Freeze();
        PreviewViewportBorder.Background = background;
        PreviewViewportBorder.Tag = scene;
    }

    private void HudLayout_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || sender is not RadioButton { Tag: string layout }) return;
        _overlay.Layout = layout;
        MarkCustomAndCommit();
    }

    private void HudChrome_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || sender is not RadioButton { Tag: string chrome }) return;
        _overlay.Chrome = chrome;
        if (chrome != "none" && _overlay.BgOpacity < 0.05)
        {
            _loadingSettings = true;
            _overlay.BgOpacity = 0.34;
            BgOpacitySlider.Value = 34;
            _loadingSettings = false;
        }
        MarkCustomAndCommit();
    }

    private void HudFont_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || sender is not RadioButton { Tag: string font }) return;
        _overlay.FontStyle = font;
        MarkCustomAndCommit();
    }

    private void OverlaySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings || !IsInitialized) return;
        _overlay.BgOpacity = BgOpacitySlider.Value / 100.0;
        _overlay.TextStroke = TextStrokeSlider.Value / 10.0;
        _overlay.Scale = ScaleSlider.Value / 100.0;
        _overlay.BorderStrength = BorderStrengthSlider.Value / 100.0;
        _overlay.HudOpacity = HudOpacitySlider.Value / 100.0;

        if (ReferenceEquals(sender, BgOpacitySlider) && _overlay.BgOpacity > 0.02 && _overlay.Chrome == "none")
        {
            _loadingSettings = true;
            _overlay.Chrome = "glass";
            ChromeGlassRadio.IsChecked = true;
            _loadingSettings = false;
        }
        MarkCustomAndCommit();
    }

    private void Accent_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _overlay.Accent = AccentBox.Text.Trim();
        MarkCustomAndCommit();
    }

    private void SecondaryAccent_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _overlay.SecondaryAccent = SecondaryBox.Text.Trim();
        MarkCustomAndCommit();
    }

    private void BgHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _overlay.BackgroundHex = BgHexBox.Text.Trim();
        MarkCustomAndCommit();
    }

    private void Title_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _overlay.Title = TitleBox.Text;
        UpdateOverlaySliderLabelsAndSwatches();
        ApplyLiveOverlayChanges(persist: true);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) AccentBox.Text = hex;
    }

    private void SecondaryPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) SecondaryBox.Text = hex;
    }

    private void BgPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) BgHexBox.Text = hex;
    }

    private void OverlayToggleOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _overlay.ShowIcon = HudShowIconCheck.IsChecked == true;
        _overlay.ShowLive = HudShowLiveCheck.IsChecked == true;
        _overlay.ShowSeason = HudShowSeasonCheck.IsChecked == true;
        _overlay.ShowToken = HudShowTokenCheck.IsChecked == true;
        _overlay.ShowAvg = HudShowAvgCheck.IsChecked == true;
        _overlay.ShowLast = HudShowLastCheck.IsChecked == true;
        _overlay.ShowBest = HudShowBestCheck.IsChecked == true;
        _overlay.ShowTreasureHour = HudShowHourCheck.IsChecked == true;
        _overlay.ShowLabels = HudShowLabelsCheck.IsChecked == true;
        _overlay.ShowHeader = HudShowHeaderCheck.IsChecked == true;
        _overlay.AccentBar = HudAccentBarCheck.IsChecked == true;
        _overlay.TextShadow = HudTextShadowCheck.IsChecked == true;
        _overlay.PulseOnChest = HudPulseCheck.IsChecked == true;
        _overlay.DimWhenIdle = HudDimIdleCheck.IsChecked == true;
        _overlay.HideWhenIdle = HudHideIdleCheck.IsChecked == true;
        _overlay.SyncObsStyle = SyncObsCheck.IsChecked == true;

        if (ReferenceEquals(sender, HudHideIdleCheck) || ReferenceEquals(sender, SyncObsCheck))
        {
            _overlay.Normalize();
            ApplyLiveOverlayChanges(persist: true);
        }
        else
        {
            MarkCustomAndCommit();
        }
    }

    private void SaveOverlay() { try { OverlayConfig.Save(_overlay); } catch { } }

    private void WorkspaceTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || OverviewScroll == null || ProbabilitiesGrid == null || HistoryScroll == null || OverlayStudioScroll == null) return;
        OverviewScroll.Visibility = TabOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProbabilitiesGrid.Visibility = TabProbabilities.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlayStudioScroll.Visibility = TabOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryScroll.Visibility = TabHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool mapVisible = TabMap.IsChecked == true;
        if (mapVisible && MapHost.Child == null) MapHost.Child = new IslandPlanner(loadSavedRoutes: !_isVerifyMode);
        MapHost.Visibility = mapVisible ? Visibility.Visible : Visibility.Collapsed;
        ControlsRightPanel.Visibility = mapVisible ? Visibility.Collapsed : Visibility.Visible;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            ApplyResponsiveLayout();
            UpdateLayout();
            if (TabOverview.IsChecked == true) RebuildMatchBarChart();
            else if (TabProbabilities.IsChecked == true) RenderProbabilityWorkspace();
            else if (TabOverlay.IsChecked == true)
            {
                EnsurePresetCardsBuilt();
                RebuildPreviewSceneBackdrop();
                RefreshPreviewHud(allowPulse: false);
                UpdateOverlayCoordsUi();
            }
            else if (TabHistory.IsChecked == true) UpdateDiagnosticsText();
        });
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            if (ScanButton.IsEnabled) _ = RunScanAsync(isAutoStartup: false);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        switch (e.Key)
        {
            case Key.D1: case Key.NumPad1: TabOverview.IsChecked = true; e.Handled = true; break;
            case Key.D2: case Key.NumPad2: TabMap.IsChecked = true; e.Handled = true; break;
            case Key.D3: case Key.NumPad3: TabProbabilities.IsChecked = true; e.Handled = true; break;
            case Key.D4: case Key.NumPad4: TabOverlay.IsChecked = true; e.Handled = true; break;
            case Key.D5: case Key.NumPad5: TabHistory.IsChecked = true; e.Handled = true; break;
            case Key.R:
                if (ScanButton.IsEnabled) _ = RunScanAsync(isAutoStartup: false);
                e.Handled = true;
                break;
            case Key.F:
                if (TabHistory.IsChecked == true)
                {
                    HistorySearchBox.Focus();
                    e.Handled = true;
                }
                else if (TabMap.IsChecked == true && MapHost.Child is IslandPlanner planner)
                {
                    planner.LayerQuery.Focus();
                    e.Handled = true;
                }
                break;
        }
    }

    private void OpenProbabilitiesTab_Click(object sender, MouseButtonEventArgs e) =>
        TabProbabilities.IsChecked = true;

    private void OpenProbabilitiesTab_ButtonClick(object sender, RoutedEventArgs e) =>
        TabProbabilities.IsChecked = true;

    private void OpenOverlayTab_ButtonClick(object sender, RoutedEventArgs e) =>
        TabOverlay.IsChecked = true;

    private void RestoreSelectedPeriod(int index)
    {
        switch (index)
        {
            case 0: F0.IsChecked = true; break;
            case 1: F1.IsChecked = true; break;
            case 3: F3.IsChecked = true; break;
            default: F2.IsChecked = true; break;
        }
    }

    private int CurrentPeriodIndex() =>
        F0.IsChecked == true ? 0 : F1.IsChecked == true ? 1 : F3.IsChecked == true ? 3 : 2;

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || !IsInitialized) return;
        _app.SelectedPeriodIndex = CurrentPeriodIndex();
        if (!_isVerifyMode) AppConfig.Save(_app);
        if (IsLoaded) Render();
    }

    private void ProbTarget_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string kind })
        {
            _selectedProbKind = kind;
            if (IsLoaded) RenderProbabilityWorkspace();
        }
    }

    private void ProbRange_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RenderProbabilityWorkspace();
    }

    private void ProbOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RenderProbabilityWorkspace();
    }

    // ==================== WIN32 DWM ROUNDED CORNERS & GLOBAL HOTKEYS ====================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int borderColor = 0x00382823; // COLORREF BGR for #232838
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        }
        catch { }

        UpdateMaximizeIconAndFrame();

        if (!_isVerifyMode && !AppChannel.IsTest)
        {
            try
            {
                RegisterHotKey(hwnd, HotkeyToggle, MOD_CONTROL | MOD_SHIFT, 0x4F);     // Ctrl+Shift+O
                RegisterHotKey(hwnd, HotkeyLock, MOD_CONTROL | MOD_SHIFT, 0x4C);       // Ctrl+Shift+L
                RegisterHotKey(hwnd, HotkeyStyleCycle, MOD_CONTROL | MOD_SHIFT, 0x4D); // Ctrl+Shift+M
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            }
            catch { }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyToggle) { ToggleInGameFromHotkey(); handled = true; }
            else if (id == HotkeyLock) { ToggleInGameLock(); handled = true; }
            else if (id == HotkeyStyleCycle) { CycleOverlayPreset(); handled = true; }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowBounds();
        if (!_isVerifyMode)
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(h, HotkeyToggle);
                UnregisterHotKey(h, HotkeyLock);
                UnregisterHotKey(h, HotkeyStyleCycle);
            }
            catch { }
        }
        _live?.Stop();
        _inGame?.Close();
        _server?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }

    // ==================== DATA RENDERING ====================

    private DateTime? SelectedSince() => CurrentPeriodIndex() switch
    {
        0 => DateTime.UtcNow.AddDays(-1),
        1 => DateTime.UtcNow.AddDays(-7),
        2 => ChestCounterService.SeasonStartUtc,
        _ => null,
    };

    private string SelectedPeriodLabel() => CurrentPeriodIndex() switch
    {
        0 => "Baús (24 horas)",
        1 => "Baús (7 dias)",
        3 => "Baús (Histórico Total)",
        _ => "Baús na Temporada",
    };

    private (long SeasonYou, long SeasonSquad, int SeasonMatches, double SeasonAvg) GetLiveSeasonTotals()
    {
        var season = _stats.Over(ChestCounterService.SeasonStartUtc);
        int liveAdd = Math.Max(0, _liveCount ?? 0);
        long you = season.YourChests + liveAdd;
        long squad = season.SquadChests + liveAdd;
        double avg = season.Matches > 0 ? season.AvgYou : (_stats.Matches > 0 ? _stats.AvgYou : 25.0);
        return (you, squad, season.Matches, avg);
    }

    private void Render()
    {
        if (!IsInitialized) return;

        var since = SelectedSince();
        var recs = _stats.RecordsSince(since);
        int n = recs.Count;
        int liveAdd = Math.Max(0, _liveCount ?? 0);

        var (seasonYou, seasonSquad, seasonMatches, _) = GetLiveSeasonTotals();
        long seasonEstimate = liveAdd + _stats.Records
            .Where(r => r.Time >= ChestCounterService.SeasonStartUtc && r.Source != "replay").Sum(r => (long)r.You);
        SeasonHeroTotalText.Text = (seasonEstimate > 0 ? "≈ " : "") + seasonYou.ToString("N0");
        SeasonHeroTotalText.ToolTip = $"{seasonYou - seasonEstimate:N0} baús atribuídos a você nos replays · {seasonEstimate:N0} estimados";

        if (_liveCount is { } lc)
        {
            SeasonLiveDeltaBadge.Visibility = Visibility.Visible;
            SeasonLiveDeltaText.Text = $"+{lc} ao vivo";
        }
        else
        {
            SeasonLiveDeltaBadge.Visibility = Visibility.Collapsed;
        }

        SeasonHeroSubText.Text = seasonMatches == 0
            ? (_liveCount != null
                ? "Partida em andamento ao vivo · será consolidada automaticamente ao terminar"
                : ChestCounterService.DemosFolderExists()
                    ? "Nenhuma partida da temporada salva ainda · aguardando partidas"
                    : "Pasta de replays não encontrada · ative a gravação de replays no Fortnite")
            : $"{seasonMatches:N0} {(seasonMatches == 1 ? "partida contabilizada" : "partidas contabilizadas")} · ≥ {seasonSquad:N0} baús visíveis c/ esquadrão";

        // Period metrics
        long periodDone = recs.Sum(r => (long)r.You);
        long periodTotal = periodDone + liveAdd;
        double avg = n > 0 ? (double)periodDone / n : 0;

        PeriodLabelText.Text = SelectedPeriodLabel();
        AvgYouText.Text = avg.ToString("F1");
        long estimated = liveAdd + recs.Where(r => r.Source != "replay").Sum(r => (long)r.You);
        YourTotalText.Text = (estimated > 0 ? "≈ " : "") + periodTotal.ToString("N0");
        MatchesText.Text = _liveCount is > 0
            ? (n == 0 ? "em partida ao vivo" : $"em {n:N0} part. (+{_liveCount} ao vivo)")
            : (n == 1 ? "em 1 partida" : $"em {n:N0} partidas");

        var best = n > 0 ? recs.MaxBy(r => r.You) : null;
        BestText.Text = (best?.You ?? 0).ToString();

        var last10 = recs.Skip(Math.Max(0, n - 10)).ToList();
        if (last10.Count > 0)
        {
            double rAvg = last10.Average(r => r.You);
            string arrow = rAvg >= avg - 0.05 ? "▲" : "▼";
            RecentFormText.Text = $"{arrow} últimas {last10.Count}: {rAvg:F1}/partida";
            KpiAvgCard.ToolTip = $"Média geral no filtro: {avg:F2} baús/partida ({n} partidas)\nRitmo recente (últimas {last10.Count}): {rAvg:F2} baús/partida";
        }
        else
        {
            RecentFormText.Text = "sem partidas no filtro";
            KpiAvgCard.ToolTip = "Nenhuma partida concluída no período selecionado.";
        }

        KpiPeriodCard.ToolTip = $"{SelectedPeriodLabel()}: {periodTotal:N0} baús pessoais · {periodTotal - estimated:N0} por replay e {estimated:N0} estimados\nBaús visíveis com esquadrão no período: ≥ {recs.Sum(r => (long)r.SquadVisibleChests) + liveAdd:N0}";

        LastGameText.Text = n > 0 ? recs[^1].You.ToString() : "—";
        LastMatchText.Text = n > 0
            ? $"{recs[^1].Time.ToLocalTime():dd/MM HH:mm} · {LifetimeStats.FriendlyPlaylist(recs[^1].Playlist, recs[^1].Source)}"
            : "";
        KpiBestCard.ToolTip = best != null
            ? $"Última partida: {recs[^1].You} baús ({recs[^1].Time.ToLocalTime():dd/MM/yyyy HH:mm} · {LifetimeStats.FriendlyPlaylist(recs[^1].Playlist, recs[^1].Source)})\nRecorde no período: {best.You} baús em {best.Time.ToLocalTime():dd/MM/yyyy HH:mm}"
            : "Jogue uma partida para registrar seu recorde.";

        // Hero Token Probability Card
        double soloTokenP = TreasureCatalog.ChanceOfAtLeastOne(137_500, seasonYou);
        double squadTokenP = TreasureCatalog.ChanceOfAtLeastOne(137_500, seasonSquad);
        HeroTokenChanceText.Text = TreasureCatalog.FormatChance(soloTokenP, highPrecision: true);
        HeroTokenSubText.Text = seasonSquad > seasonYou
            ? $"c/ equipe ≥ {TreasureCatalog.FormatChance(squadTokenP, true)} · ver gráficos →"
            : "clique p/ gráficos de probabilidade →";

        FooterSummaryText.Text = $"Temporada: {seasonYou:N0} baús ({seasonMatches} partidas) · Token Pirata: {TreasureCatalog.FormatChance(soloTokenP, true)}";

        // Match chart bars (last 36 matches in period)
        _chartBars = recs.Skip(Math.Max(0, n - 36)).ToList();
        ChartMetaText.Text = _chartBars.Count > 0
            ? $"últimas {_chartBars.Count} partidas · média {_chartBars.Average(r => r.You):F1} baús"
            : "";
        NoDataText.Visibility = _chartBars.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        MatchChartCanvas.Visibility = _chartBars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearMatchBarHover();
        RebuildMatchBarChart();

        ChestKindList.ItemsSource = ChestKinds.All.Select(kind => new
        {
            kind.Name,
            Icon = new Uri("pack://application:,,,/" + kind.Icon),
            kind.Color,
            BuriedMarker = kind == ChestKinds.Buried ? Visibility.Visible : Visibility.Collapsed,
            PosterMarker = kind == ChestKinds.Poster ? Visibility.Visible : Visibility.Collapsed,
            Count = recs.Sum(r => (long)r.CountOf(kind)).ToString("N0"),
            Hint = kind == ChestKinds.Poster ? "Baú enterrado identificado pelo replay como ligado a um cartaz."
                : kind == ChestKinds.Buried ? "Baú de tesouro encontrado ao escavar o ponto marcado."
                : kind == ChestKinds.Relic ? "Baú de Elemental (Sprite). A família inclui as variantes da temporada."
                : $"Baú {kind.Name.ToLowerInvariant()} aberto por você."
        }).ToList();
        ChestKindsPeriodText.Text = CurrentPeriodIndex() switch { 0 => "24 horas", 1 => "7 dias", 3 => "Todo o histórico", _ => "Temporada" };
        long untyped = recs.Sum(r => (long)r.UntypedChests) + liveAdd;
        ChestKindNote.Text = untyped > 0
            ? $"{untyped:N0} sem tipo identificado · inclui estimativas ao vivo e registros sem detalhe."
            : n == 0 ? "Sincronize um replay para ver suas primeiras aberturas." : "Aberturas pessoais confirmadas nos replays do período.";

        RenderSquadAndPrizes(recs);
        RenderMatchHistoryTable(recs);
        _oddsCacheKey = "";
        RenderTreasureHour();
        if (TabProbabilities.IsChecked == true || _isVerifyMode)
            RenderProbabilityWorkspace();
        PushInGameStats();
    }

    private void RenderSquadAndPrizes(List<MatchRecord> recs)
    {
        var last = recs.LastOrDefault(r => r.Mates.Count > 0 || r.SquadStatus != "Solo");
        var squad = new List<Row>();
        if (last != null && last.Mates.Count > 0)
        {
            string matchCtx = $"{last.Time.ToLocalTime():dd/MM/yyyy HH:mm} · {LifetimeStats.FriendlyPlaylist(last.Playlist, last.Source)}";
            squad.Add(new Row("Você", last.You.ToString(), $"Você abriu {last.You} baús nesta partida em grupo\n{matchCtx}"));
            squad.AddRange(last.Mates.Select(m => new Row(
                m.Name,
                "≥ " + m.Chests,
                $"{m.Name}: pelo menos {m.Chests} baús visíveis no alcance do seu replay\n{matchCtx}")));
            SquadTotalText.Text = $"≥ {last.SquadVisibleChests} no total";
        }
        else
        {
            SquadTotalText.Text = "";
            SquadEmptyText.Text = last?.Source == "play-again"
                ? "Partida encerrada. Os baús dos colegas aparecem quando o Fortnite finaliza o replay."
                 : last == null ? "Seus colegas de equipe aparecem aqui após partidas em grupo."
                : $"Equipe sem dados para a partida de {last.Time.ToLocalTime():dd/MM HH:mm}.";
        }

        SquadList.ItemsSource = squad;
        SquadEmptyText.Visibility = squad.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var mates = _stats.TopMates(SelectedSince(), 4)
            .Select(t =>
            {
                double avgMate = t.Matches > 0 ? (double)t.Chests / t.Matches : 0;
                return new Row(
                    t.Name,
                    $"{t.Matches} {(t.Matches == 1 ? "part." : "part.")} · ≥ {t.Chests:N0} baús",
                    $"{t.Name}\nPartidas juntos no período: {t.Matches}\nBaús visíveis abertos: ≥ {t.Chests:N0} (~{avgMate:F1}/partida)");
            })
            .ToList();
        MatesList.ItemsSource = mates;
        MatesEmptyText.Visibility = mates.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var finds = recs.SelectMany(r => r.Prizes.Select(p => (Rec: r, Prize: p))).Reverse().ToList();
        var token = finds.FirstOrDefault(f => Treasures.IsToken(f.Prize.PrizeId));
        TokenText.Text = token.Prize == null
            ? "Ficha da Loja Pirata ainda não encontrada — cada baú soma na sua chance!"
            : $"★ Ficha da Loja Pirata encontrada por {Finder(token.Prize)} · {token.Rec.Time.ToLocalTime():dd/MM, HH:mm}";
        TokenText.Foreground = ResBrush(token.Prize == null ? "Gold" : "Emerald");

        var prizes = finds.Where(f => !Treasures.IsToken(f.Prize.PrizeId)).Take(4)
            .Select(f =>
            {
                string pName = Treasures.Name(f.Prize.PrizeId);
                string who = Finder(f.Prize);
                string when = f.Rec.Time.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                return new Row(pName, $" — {who}, {f.Rec.Time.ToLocalTime():dd/MM HH:mm}",
                    $"{pName}\nObtido por: {who}\nPartida: {when} ({LifetimeStats.FriendlyPlaylist(f.Rec.Playlist, f.Rec.Source)})");
            })
            .ToList();
        PrizeList.ItemsSource = prizes;
        PrizeEmptyText.Visibility = prizes.Count > 0 || token.Prize != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderMatchHistoryTable(List<MatchRecord> recs)
    {
        long sumYou = recs.Sum(r => (long)r.You);
        long sumSq = recs.Sum(r => (long)r.SquadVisibleChests);
        HistorySummaryText.Text = $"{recs.Count:N0} partidas no período · {sumYou:N0} baús abertos por você · ≥ {sumSq:N0} visíveis com esquadrão";

        var goldBrush = ResBrush("Gold");
        var mutedBrush = ResBrush("Muted");
        var emeraldBrush = ResBrush("Emerald");

        var rows = new List<MatchHistoryRow>(recs.Count);
        for (int i = recs.Count - 1; i >= 0; i--)
        {
            var r = recs[i];
            int mateChests = r.Mates.Sum(m => m.Chests);
            string sqStr = r.Mates.Count > 0 ? $"≥ {mateChests} ({r.Mates.Count} col.)" : r.SquadStatus;
            string modeStr = LifetimeStats.FriendlyPlaylist(r.Playlist, r.Source);
            string whenStr = r.Time.ToLocalTime().ToString("dd/MM/yy HH:mm");

            string prizeSummary;
            Brush detailBrush;
            if (r.Prizes.Count > 0)
            {
                var grouped = r.Prizes.GroupBy(p => Treasures.Name(p.PrizeId))
                    .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
                prizeSummary = "★ " + string.Join(", ", grouped);
                detailBrush = r.Prizes.Any(p => !Treasures.IsGizmo(p.PrizeId)) ? emeraldBrush : goldBrush;
            }
            else if (r.Source == "play-again")
            {
                prizeSummary = "✓ Jogar novamente (consolidando…)";
                detailBrush = emeraldBrush;
            }
            else if (r.Source == "ocr-rescue")
            {
                prizeSummary = "✓ Resgatada via OCR";
                detailBrush = goldBrush;
            }
            else
            {
                prizeSummary = "✓ Replay verificado";
                detailBrush = mutedBrush;
            }

            string matesDetail = r.Mates.Count > 0
                ? "\nEsquadrão: " + string.Join(", ", r.Mates.Select(m => $"{m.Name} (≥{m.Chests})"))
                : "\n" + r.SquadStatus;
            if (r.Unattributed is > 0)
                matesDetail += $"\nBaús visíveis sem autoria confirmada: {r.Unattributed} (podem incluir adversários).";
            string prizesDetail = r.Prizes.Count > 0
                ? "\nRecompensas: " + string.Join(", ", r.Prizes.Select(p => $"{Treasures.Name(p.PrizeId)} ({Finder(p)})"))
                : "";

            var kindParts = ChestKinds.All.Where(k => r.CountOf(k) > 0).Select(k => $"{k.Name} {r.CountOf(k)}").ToList();
            if (r.UntypedChests > 0) kindParts.Add($"Sem tipo {r.UntypedChests}");
            string kindSummary = kindParts.Count > 0 ? string.Join("   ·   ", kindParts) : "Nenhum baú pessoal";
            string tip = $"Partida #{i + 1} · {r.Time.ToLocalTime():dd/MM/yyyy HH:mm:ss}\nModo: {modeStr}\nSeus baús abertos: {r.You} (Total visível c/ equipe: ≥ {r.SquadVisibleChests})\n{kindSummary}{matesDetail}{prizesDetail}";
            string searchKey = $"{i + 1} {whenStr} {modeStr} {kindSummary} {prizeSummary} {string.Join(' ', r.Mates.Select(m => m.Name))}".ToLowerInvariant();

            rows.Add(new MatchHistoryRow(
                $"#{i + 1}",
                whenStr,
                modeStr,
                r.You.ToString(),
                sqStr,
                prizeSummary,
                detailBrush,
                tip,
                searchKey,
                kindSummary));
        }

        _allHistoryRows = rows;
        ApplyMatchHistoryFilter();
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        HistorySearchPlaceholder.Visibility = string.IsNullOrEmpty(HistorySearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyMatchHistoryFilter();
    }

    private void ApplyMatchHistoryFilter()
    {
        if (MatchHistoryList == null) return;
        string q = (HistorySearchBox?.Text ?? "").Trim().ToLowerInvariant();
        var filtered = string.IsNullOrEmpty(q)
            ? _allHistoryRows
            : _allHistoryRows.Where(r => r.SearchKey.Contains(q, StringComparison.Ordinal)).ToList();

        MatchHistoryList.ItemsSource = filtered;
        if (HistoryEmptyFilterText != null)
        {
            HistoryEmptyFilterText.Visibility = (_allHistoryRows.Count > 0 && filtered.Count == 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void RenderTreasureHour()
    {
        var now = DateTime.UtcNow;
        var hour = TreasureCatalog.CurrentOrNext(now);
        bool live = hour != null && TreasureCatalog.IsLive(hour, now);

        if (hour == null)
        {
            HourTitleText.Text = "Evento Caça ao Tesouro encerrado";
            HourBoostText.Text = "Todos os seus dados da temporada permanecem salvos.";
            HourCountdownText.Text = "ENCERRADO";
        }
        else
        {
            var left = (live ? hour.EndUtc : hour.StartUtc) - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            HourCountdownText.Text = left.TotalHours >= 24
                ? $"{(int)left.TotalDays}d {left.Hours:00}:{left.Minutes:00}:{left.Seconds:00}"
                : $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
            HourTitleText.Text = live
                ? $"🔥 HORA DO TESOURO AO VIVO ATÉ {hour.EndUtc.ToLocalTime():HH:mm}"
                : $"Próxima Hora do Tesouro · {hour.StartUtc.ToLocalTime():ddd HH:mm}";
            HourBoostText.Text = string.Join("   ·   ", hour.Boosts.Select(b =>
                $"{TreasureCatalog.Odds.First(o => o.Kind == b.Key).ShortLabel} ×{b.Value:0.#}"));
        }

        HourBorder.Background = live ? HexBrush("#2A121D", ResBrush("CardAlt")) : ResBrush("CardAlt");
        HourBorder.BorderBrush = live ? ResBrush("Gem") : Brushes.Transparent;
        HourBorder.BorderThickness = live ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        HourTitleText.Foreground = ResBrush(live ? "Gem" : "Text");
        HourCountdownText.Foreground = ResBrush(live ? "Gold" : "Gold");
        HourBoostText.Foreground = ResBrush("Steel");

        var week = TreasureCatalog.WeekAt(now);
        var (seasonYou, seasonSquad, _, _) = GetLiveSeasonTotals();
        string key = $"{live}|{week?.Number}|{seasonYou}|{seasonSquad}";
        if (key == _oddsCacheKey) return;
        _oddsCacheKey = key;

        WeekText.Text = week != null
            ? $"Chances Acumuladas · Semana {week.Number}"
            : "Chances Acumuladas na Temporada";

        var textBrush = ResBrush("Text");
        var gemBrush = ResBrush("Gem");
        var goldBrush = ResBrush("Gold");

        OddsList.ItemsSource = TreasureCatalog.Odds.Select(o =>
        {
            double boost = live && hour!.Boosts.TryGetValue(o.Kind, out var b) ? b : 1.0;
            string item = week != null && week.Prizes.TryGetValue(o.Kind, out var name) ? name
                : o.Kind == TreasureCatalog.Token ? "Desbloqueia 14 dias grátis na Loja Pirata"
                : o.Kind == TreasureCatalog.Gizmo ? "Localizadores e engenhocas compartilhadas"
                : "Recompensa temática da semana";

            double p = TreasureCatalog.ChanceOfAtLeastOne(o.OneIn, seasonYou);
            string luck = TreasureCatalog.FormatChance(p, highPrecision: o.OneIn >= 5_000);
            var barColor = HexBrush(o.HexColor, goldBrush);
            double clamped = Math.Clamp(p, 0.008, 1.0);

            long halfChests = TreasureCatalog.ChestsForTargetProbability(o.OneIn, 0.50);
            bool shared = o.Kind is TreasureCatalog.Token or TreasureCatalog.Gizmo;
            string sqLine = shared && seasonSquad > seasonYou
                ? $"\nChance efetiva c/ esquadrão (≥{seasonSquad:N0} baús): ≥ {TreasureCatalog.FormatChance(TreasureCatalog.ChanceOfAtLeastOne(o.OneIn, seasonSquad), true)}"
                : "";
            string boostLine = TreasureCatalog.UsualBoosts.TryGetValue(o.Kind, out var ub)
                ? $"\nTaxa na Hora do Tesouro (×{ub:0.#}): 1 em {Math.Round(o.OneIn / ub):N0}"
                : "";
            string tip = $"{o.Label}\nItem atual: {item}\nTaxa base: 1 em {o.OneIn:N0} por baú{boostLine}\nSua chance acumulada ({seasonYou:N0} baús): {TreasureCatalog.FormatChance(p, true)}{sqLine}\nPonto de 50% (cara ou coroa): {halfChests:N0} baús";

            return new OddsQuickRow(
                o.Label,
                item,
                $"1 em {Math.Round(o.OneIn / boost):N0}",
                boost > 1 ? gemBrush : textBrush,
                luck,
                barColor,
                new GridLength(clamped, GridUnitType.Star),
                new GridLength(Math.Max(0.0, 1.0 - clamped), GridUnitType.Star),
                tip);
        }).ToList();
    }

    // ==================== WORKSPACE 2: INTERACTIVE PROBABILITY CHARTS ====================

    private void RenderProbabilityWorkspace()
    {
        if (!IsInitialized || ProbCurrentText == null) return;

        var selected = TreasureCatalog.Odds.FirstOrDefault(o => o.Kind == _selectedProbKind)
                       ?? TreasureCatalog.Odds[0];
        var (seasonYou, seasonSquad, _, seasonAvg) = GetLiveSeasonTotals();
        double realAvg = Math.Max(1.0, seasonAvg);

        bool isSharedWithSquad = selected.Kind is TreasureCatalog.Token or TreasureCatalog.Gizmo;
        double boostMult = TreasureCatalog.UsualBoosts.TryGetValue(selected.Kind, out var bm) ? bm : 1.0;

        double soloProb = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, seasonYou);
        ProbCurrentText.Text = TreasureCatalog.FormatChance(soloProb, highPrecision: true);
        ProbCurrentText.Foreground = HexBrush(selected.HexColor, ResBrush("Gold"));
        ProbCurrentSubText.Text = $"com {seasonYou:N0} baús abertos por você";

        if (isSharedWithSquad && ShowSquadCurveCheck.IsChecked == true && seasonSquad > seasonYou)
        {
            double sqProb = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, seasonSquad);
            ProbSecondaryTitleText.Text = "Chance Efetiva c/ Esquadrão";
            ProbBoostedText.Text = "≥ " + TreasureCatalog.FormatChance(sqProb, highPrecision: true);
            ProbBoostedSubText.Text = $"≥ {seasonSquad:N0} baús visíveis (compartilhado)";
        }
        else if (boostMult > 1.0)
        {
            double boostProb = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn / boostMult, seasonYou);
            ProbSecondaryTitleText.Text = $"Equivalente Hora do Tesouro (×{boostMult:0.#})";
            ProbBoostedText.Text = TreasureCatalog.FormatChance(boostProb, highPrecision: true);
            ProbBoostedSubText.Text = $"taxa turbo: 1 em {Math.Round(selected.OneIn / boostMult):N0} por baú";
        }
        else
        {
            double expected = TreasureCatalog.ExpectedPulls(selected.OneIn, seasonYou);
            ProbSecondaryTitleText.Text = "Quantidade Esperada (E[X])";
            ProbBoostedText.Text = expected.ToString("F2");
            ProbBoostedSubText.Text = $"média estatística em {seasonYou:N0} baús";
        }

        long plus50Chests = seasonYou + (long)Math.Round(50 * realAvg);
        double plus50Prob = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, plus50Chests);
        ProbProjectedText.Text = TreasureCatalog.FormatChance(plus50Prob, highPrecision: true);
        ProbProjectedSubText.Text = $"após ~{plus50Chests:N0} baús ({realAvg:F1}/partida)";

        long halfChests = TreasureCatalog.ChestsForTargetProbability(selected.OneIn, 0.50);
        ProbHalfwayText.Text = halfChests.ToString("N0");
        long remHalf = Math.Max(0, halfChests - seasonYou);
        ProbHalfwaySubText.Text = remHalf == 0
            ? "✓ Você já superou 50% de probabilidade!"
            : $"faltam {remHalf:N0} baús para 50% de chance";

        CurveChartTitleText.Text = $"Curva Acumulada — {selected.Label} (1 em {selected.OneIn:N0})";
        HistoryProbTitleText.Text = $"Sua Evolução Real — {selected.ShortLabel}";

        // Build chronological progression points across the season
        var cumSeries = _stats.CumulativeSeries(ChestCounterService.SeasonStartUtc);
        _seasonProgPoints = cumSeries.Select(pt =>
        {
            long effectiveChests = isSharedWithSquad && ShowSquadCurveCheck.IsChecked == true
                ? pt.CumulativeSquadChests
                : pt.CumulativeYourChests;
            return (pt.MatchNumber, pt.TimeUtc, pt.MatchChests, effectiveChests,
                TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, effectiveChests));
        }).ToList();

        if (_liveCount is > 0)
        {
            long prevCum = _seasonProgPoints.Count > 0 ? _seasonProgPoints[^1].CumChests : 0;
            long liveCum = prevCum + _liveCount.Value;
            _seasonProgPoints.Add((_seasonProgPoints.Count + 1, DateTime.UtcNow, _liveCount.Value, liveCum,
                TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, liveCum)));
        }

        HistoryProbMetaText.Text = _seasonProgPoints.Count > 0
            ? $"{_seasonProgPoints.Count} part. · {TreasureCatalog.FormatChance(_seasonProgPoints[^1].Prob, true)}"
            : "sem partidas";

        // Build 7-tier full comparison matrix
        var now = DateTime.UtcNow;
        var week = TreasureCatalog.WeekAt(now);
        var seasonRecs = _stats.RecordsSince(ChestCounterService.SeasonStartUtc);
        int gizmosPulled = seasonRecs.Sum(r => r.Prizes.Count(p => Treasures.IsGizmo(p.PrizeId)));
        int tokensPulled = seasonRecs.Sum(r => r.Prizes.Count(p => Treasures.IsToken(p.PrizeId)));

        OddsComparisonList.ItemsSource = TreasureCatalog.Odds.Select(o =>
        {
            string itemName = week != null && week.Prizes.TryGetValue(o.Kind, out var wkItem) ? wkItem
                : o.Kind == TreasureCatalog.Token ? "Acesso completo à Loja Pirata"
                : o.Kind == TreasureCatalog.Gizmo ? "Engenhocas compartilhadas c/ equipe"
                : "Cosmético raro da semana";

            double pSolo = TreasureCatalog.ChanceOfAtLeastOne(o.OneIn, seasonYou);
            bool shared = o.Kind is TreasureCatalog.Token or TreasureCatalog.Gizmo;
            double pSquad = TreasureCatalog.ChanceOfAtLeastOne(o.OneIn, seasonSquad);
            string sqCol = shared
                ? "≥ " + TreasureCatalog.FormatChance(pSquad, true)
                : "Individual";

            long p50c = seasonYou + (long)Math.Round(50 * realAvg);
            long p100c = seasonYou + (long)Math.Round(100 * realAvg);
            string p50s = TreasureCatalog.FormatChance(TreasureCatalog.ChanceOfAtLeastOne(o.OneIn, p50c), true);
            string p100s = TreasureCatalog.FormatChance(TreasureCatalog.ChanceOfAtLeastOne(o.OneIn, p100c), true);
            string proj = $"{p50s} / {p100s}";

            double exp = TreasureCatalog.ExpectedPulls(o.OneIn, seasonYou);
            string expStr = o.Kind == TreasureCatalog.Gizmo
                ? $"{exp:F1} esp. ({gizmosPulled} obt.)"
                : o.Kind == TreasureCatalog.Token
                    ? $"{exp:F3} esp. ({tokensPulled} obt.)"
                    : $"{exp:F2} esp.";

            long c50 = TreasureCatalog.ChestsForTargetProbability(o.OneIn, 0.50);
            long c90 = TreasureCatalog.ChestsForTargetProbability(o.OneIn, 0.90);
            string tip = $"{o.Label} — {itemName}\nTaxa individual: 1 em {o.OneIn:N0} por baú\nSua chance atual ({seasonYou:N0} baús): {TreasureCatalog.FormatChance(pSolo, true)}\n" +
                         (shared ? $"Chance c/ esquadrão (≥{seasonSquad:N0} baús): ≥ {TreasureCatalog.FormatChance(pSquad, true)}\n" : "") +
                         $"Projeção em +50 partidas (~{p50c:N0} baús): {p50s}\nProjeção em +100 partidas (~{p100c:N0} baús): {p100s}\nMarcos matemáticos: 50% em {c50:N0} baús · 90% em {c90:N0} baús";

            var brush = HexBrush(o.HexColor, ResBrush("Gold"));
            double fill = Math.Clamp(pSolo, 0.008, 1.0);
            return new OddsMatrixRow(
                o.Label,
                itemName,
                $"1 em {o.OneIn:N0}",
                TreasureCatalog.FormatChance(pSolo, highPrecision: true),
                sqCol,
                proj,
                expStr,
                brush,
                new GridLength(fill, GridUnitType.Star),
                new GridLength(Math.Max(0.0, 1.0 - fill), GridUnitType.Star),
                tip);
        }).ToList();

        RebuildProbabilityCurveChart();
        RebuildSeasonProgressionChart();
    }

    // ==================== VECTOR CHART 1: MATCH BARS (ZERO-FLICKER O(1) HOVER) ====================

    private void MatchChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClearMatchBarHover();
        RebuildMatchBarChart();
    }

    private void MatchChartCanvas_MouseLeave(object sender, MouseEventArgs e) => ClearMatchBarHover();

    private void MatchChartCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_chartBars.Count == 0 || _barVisuals.Count != _chartBars.Count || MatchChartCanvas.ActualWidth <= 20) return;
        var pos = e.GetPosition(MatchChartCanvas);
        double slotW = MatchChartCanvas.ActualWidth / _chartBars.Count;
        int idx = Math.Clamp((int)(pos.X / slotW), 0, _chartBars.Count - 1);
        ShowMatchBarHover(idx, pos);
    }

    private void ClearMatchBarHover()
    {
        if (_hoveredBarIndex >= 0 && _hoveredBarIndex < _barVisuals.Count)
            ApplyBarVisualState(_hoveredBarIndex, hovered: false);
        _hoveredBarIndex = -1;
        if (MatchChartTooltipBorder != null)
            MatchChartTooltipBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowMatchBarHover(int idx, Point mousePos)
    {
        if (idx < 0 || idx >= _chartBars.Count || idx >= _barVisuals.Count) return;

        if (_hoveredBarIndex != idx)
        {
            if (_hoveredBarIndex >= 0 && _hoveredBarIndex < _barVisuals.Count)
                ApplyBarVisualState(_hoveredBarIndex, hovered: false);
            _hoveredBarIndex = idx;
            ApplyBarVisualState(idx, hovered: true);

            var r = _chartBars[idx];
            int mateSum = r.Mates.Sum(m => m.Chests);
            string matePart = r.Mates.Count > 0 ? $" · Equipe: ≥ {mateSum}" : " · " + r.SquadStatus;
            string prizePart = r.Prizes.Count > 0
                ? $"\n★ {string.Join(", ", r.Prizes.Take(2).Select(p => Treasures.Name(p.PrizeId)))}"
                : "";
            MatchChartTooltipText.Text =
                $"{r.You} baús abertos{matePart}\n{r.Time.ToLocalTime():dd/MM/yyyy HH:mm} · {LifetimeStats.FriendlyPlaylist(r.Playlist, r.Source)}{prizePart}";
        }

        MatchChartTooltipBorder.Visibility = Visibility.Visible;
        PositionFloatingTooltip(MatchChartTooltipBorder, MatchChartCanvas,
            (idx + 0.5) * (MatchChartCanvas.ActualWidth / _chartBars.Count),
            Canvas.GetTop(_barVisuals[idx].Rect),
            mousePos.Y);
    }

    private void ApplyBarVisualState(int idx, bool hovered)
    {
        var (rect, lbl, isTop) = _barVisuals[idx];
        rect.Background = hovered ? ResBrush("Gold")
            : isTop ? ResBrush("Gem")
            : HexBrush("#DCE6ECEF", ResBrush("Text"));
        rect.Opacity = hovered ? 1.0 : (isTop ? 0.96 : 0.82);

        if (lbl != null)
        {
            lbl.Foreground = hovered ? ResBrush("Gold") : (isTop ? ResBrush("Gem") : ResBrush("Muted"));
            lbl.FontWeight = hovered || isTop ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private static void PositionFloatingTooltip(Border tip, Canvas canvas, double anchorX, double anchorTopY, double mouseY)
    {
        double cw = canvas.ActualWidth;
        double ch = canvas.ActualHeight;
        if (cw <= 20 || ch <= 20) return;

        // Keep Margin zeroed and position exclusively via TranslateTransform so DesiredSize
        // never feeds the previous offset back into Measure (which caused horizontal oscillation).
        if (tip.Margin != default) tip.Margin = new Thickness(0);
        tip.Measure(new Size(Math.Max(140, cw - 12), double.PositiveInfinity));
        double tw = tip.DesiredSize.Width;
        double th = tip.DesiredSize.Height;

        double left;
        double top;

        // If anchor point is near the top of the chart, place tooltip cleanly to the side of the bar/point so it never covers the label or cursor!
        if (anchorTopY < th + 18)
        {
            left = anchorX + 16 + tw <= cw - 4
                ? anchorX + 16
                : Math.Max(4, anchorX - tw - 16);
            top = Math.Clamp(Math.Min(anchorTopY + 4, mouseY - th * 0.5), 4, Math.Max(4, ch - th - 4));
        }
        else
        {
            left = Math.Clamp(anchorX - tw * 0.5, 4, Math.Max(4, cw - tw - 4));
            top = Math.Clamp(anchorTopY - th - 10, 4, Math.Max(4, ch - th - 4));
        }

        if (tip.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            tip.RenderTransform = tt;
        }
        tt.X = Math.Round(left);
        tt.Y = Math.Round(top);
    }

    private void RebuildMatchBarChart()
    {
        MatchChartCanvas.Children.Clear();
        _barVisuals.Clear();
        _hoveredBarIndex = -1;

        double w = MatchChartCanvas.ActualWidth;
        double h = MatchChartCanvas.ActualHeight;
        if (w <= 30 || h <= 36 || _chartBars.Count == 0) return;

        int maxVal = Math.Max(1, _chartBars.Max(r => r.You));
        double avgVal = _chartBars.Average(r => r.You);
        double topScale = maxVal * 1.20;
        double bottomY = h - 4;

        // Baseline
        MatchChartCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = bottomY, X2 = w, Y2 = bottomY,
            Stroke = ResBrush("Stroke"), StrokeThickness = 1,
            IsHitTestVisible = false,
        });

        // Average dashed reference line
        double avgY = bottomY - (avgVal / topScale) * (bottomY - 8);
        MatchChartCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = avgY, X2 = w, Y2 = avgY,
            Stroke = HexBrush("#663DDC84", ResBrush("Emerald")),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            IsHitTestVisible = false,
        });

        double slotW = w / _chartBars.Count;
        double barW = Math.Clamp(slotW - 5, 4, 28);
        double lastLabelRight = -100;

        for (int i = 0; i < _chartBars.Count; i++)
        {
            var r = _chartBars[i];
            double barH = Math.Max(3, (r.You / topScale) * (bottomY - 8));
            double x = i * slotW + (slotW - barW) * 0.5;
            double y = bottomY - barH;
            bool isTop = r.You == maxVal && maxVal > 0;

            var rect = new Border
            {
                Width = barW,
                Height = barH,
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background = isTop ? ResBrush("Gem") : HexBrush("#DCE6ECEF", ResBrush("Text")),
                Opacity = isTop ? 0.96 : 0.82,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            MatchChartCanvas.Children.Add(rect);

            TextBlock? lbl = null;
            if (slotW >= 20 || isTop || i == _chartBars.Count - 1)
            {
                var candidate = new TextBlock
                {
                    Text = r.You.ToString(),
                    FontSize = 10,
                    Foreground = isTop ? ResBrush("Gem") : ResBrush("Muted"),
                    FontWeight = isTop ? FontWeights.Bold : FontWeights.Normal,
                    IsHitTestVisible = false,
                };
                candidate.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lx = x + (barW - candidate.DesiredSize.Width) * 0.5;
                if (lx > lastLabelRight + 3 || isTop)
                {
                    Canvas.SetLeft(candidate, Math.Clamp(lx, 0, Math.Max(0, w - candidate.DesiredSize.Width)));
                    Canvas.SetTop(candidate, Math.Max(0, y - candidate.DesiredSize.Height - 2));
                    MatchChartCanvas.Children.Add(candidate);
                    lastLabelRight = lx + candidate.DesiredSize.Width;
                    lbl = candidate;
                }
            }

            _barVisuals.Add((rect, lbl, isTop));
        }
    }

    // ==================== VECTOR CHART 2: THEORETICAL PROBABILITY CURVE (O(1) HOVER) ====================

    private void ProbCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildProbabilityCurveChart();

    private void ProbCurveCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_probCrosshairLine != null) _probCrosshairLine.Visibility = Visibility.Collapsed;
        if (_probCrosshairDot != null) _probCrosshairDot.Visibility = Visibility.Collapsed;
        if (ProbCurveTooltipBorder != null) ProbCurveTooltipBorder.Visibility = Visibility.Collapsed;
        ProbCurveHoverText.Text = "Passe o mouse sobre a curva para simular qualquer quantidade de baús e partidas.";
    }

    private void ProbCurveCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (ProbCurveCanvas.ActualWidth <= 80) return;
        UpdateProbCurveHover(e.GetPosition(ProbCurveCanvas).X);
    }

    private void UpdateProbCurveHover(double mx)
    {
        double w = ProbCurveCanvas.ActualWidth;
        double h = ProbCurveCanvas.ActualHeight;
        const double padL = 52, padR = 18, padT = 14, padB = 24;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW <= 20 || plotH <= 20 || mx < padL || mx > padL + plotW)
        {
            ProbCurveCanvas_MouseLeave(this, null!);
            return;
        }

        var selected = TreasureCatalog.Odds.FirstOrDefault(o => o.Kind == _selectedProbKind) ?? TreasureCatalog.Odds[0];
        var (_, _, _, seasonAvg) = GetLiveSeasonTotals();
        double realAvg = Math.Max(1.0, seasonAvg);
        double boostMult = TreasureCatalog.UsualBoosts.TryGetValue(selected.Kind, out var bm) ? bm : 1.0;

        long simChests = (long)Math.Round(((mx - padL) / plotW) * _probChartMaxChests);
        double simBaseP = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, simChests);
        double simBoostP = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn / boostMult, simChests);

        double sx = padL + Math.Clamp(simChests / _probChartMaxChests, 0, 1) * plotW;
        double sy = padT + (1.0 - Math.Clamp(simBaseP / _probChartMaxY, 0, 1)) * plotH;

        if (_probCrosshairLine != null)
        {
            _probCrosshairLine.X1 = _probCrosshairLine.X2 = sx;
            _probCrosshairLine.Y1 = padT;
            _probCrosshairLine.Y2 = padT + plotH;
            _probCrosshairLine.Visibility = Visibility.Visible;
        }
        if (_probCrosshairDot != null)
        {
            Canvas.SetLeft(_probCrosshairDot, sx - 4.5);
            Canvas.SetTop(_probCrosshairDot, sy - 4.5);
            _probCrosshairDot.Visibility = Visibility.Visible;
        }

        long estMatches = (long)Math.Ceiling(simChests / realAvg);
        string boostLine = boostMult > 1.0
            ? $"\nHora do Tesouro (×{boostMult:0.#}): {TreasureCatalog.FormatChance(simBoostP, true)}"
            : "";

        ProbCurveTooltipBorder.BorderBrush = HexBrush(selected.HexColor, ResBrush("Gold"));
        ProbCurveTooltipText.Text =
            $"Simulação: {simChests:N0} baús (~{estMatches:N0} part.)\nChance Base: {TreasureCatalog.FormatChance(simBaseP, true)}{boostLine}";
        ProbCurveTooltipBorder.Visibility = Visibility.Visible;
        PositionFloatingTooltip(ProbCurveTooltipBorder, ProbCurveCanvas, sx, sy, sy);

        ProbCurveHoverText.Text =
            $"Em {simChests:N0} baús (~{estMatches:N0} partidas): Base {TreasureCatalog.FormatChance(simBaseP, true)}" +
            (boostMult > 1.0 ? $" · Turbo {TreasureCatalog.FormatChance(simBoostP, true)}" : "");
    }

    private void RebuildProbabilityCurveChart()
    {
        ProbCurveCanvas.Children.Clear();
        _probCrosshairLine = null;
        _probCrosshairDot = null;
        if (ProbCurveTooltipBorder != null) ProbCurveTooltipBorder.Visibility = Visibility.Collapsed;

        double w = ProbCurveCanvas.ActualWidth;
        double h = ProbCurveCanvas.ActualHeight;
        if (w <= 80 || h <= 60) return;

        var selected = TreasureCatalog.Odds.FirstOrDefault(o => o.Kind == _selectedProbKind) ?? TreasureCatalog.Odds[0];
        var (seasonYou, seasonSquad, _, seasonAvg) = GetLiveSeasonTotals();
        double realAvg = Math.Max(1.0, seasonAvg);
        bool isShared = selected.Kind is TreasureCatalog.Token or TreasureCatalog.Gizmo;
        bool showSquad = isShared && ShowSquadCurveCheck.IsChecked == true && seasonSquad > seasonYou;
        double boostMult = TreasureCatalog.UsualBoosts.TryGetValue(selected.Kind, out var bm) ? bm : 1.0;

        const double padL = 52, padR = 18, padT = 14, padB = 24;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;

        long fullChests = TreasureCatalog.ChestsForTargetProbability(selected.OneIn, 0.95);
        if (ZoomRangeFull.IsChecked == true)
        {
            _probChartMaxChests = Math.Max(fullChests, Math.Max(seasonYou, seasonSquad) * 1.15);
        }
        else
        {
            long refChests = showSquad ? seasonSquad : seasonYou;
            double smartMax = Math.Max(refChests * 2.8, refChests + 85 * realAvg);
            smartMax = Math.Max(smartMax, Math.Min(fullChests, 500));
            _probChartMaxChests = Math.Min(Math.Max(fullChests * 1.05, refChests * 1.2), smartMax);
        }

        double maxBaseP = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, (long)_probChartMaxChests);
        double maxBoostP = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn / boostMult, (long)_probChartMaxChests);
        _probChartMaxY = ZoomRangeFull.IsChecked == true
            ? 1.0
            : Math.Clamp(Math.Max(maxBaseP, maxBoostP) * 1.16, 0.002, 1.0);

        double XOf(double chests) => padL + Math.Clamp(chests / _probChartMaxChests, 0, 1) * plotW;
        double YOf(double prob) => padT + (1.0 - Math.Clamp(prob / _probChartMaxY, 0, 1)) * plotH;

        for (int g = 0; g <= 4; g++)
        {
            double frac = g / 4.0;
            double gy = padT + (1.0 - frac) * plotH;
            ProbCurveCanvas.Children.Add(new Line
            {
                X1 = padL, Y1 = gy, X2 = padL + plotW, Y2 = gy,
                Stroke = ResBrush("Stroke"), StrokeThickness = 1,
                Opacity = g == 0 ? 1.0 : 0.55,
                IsHitTestVisible = false,
            });
            double pVal = frac * _probChartMaxY;
            var yLbl = new TextBlock
            {
                Text = TreasureCatalog.FormatChance(pVal, highPrecision: _probChartMaxY < 0.15),
                FontSize = 10,
                Foreground = ResBrush("Muted"),
                IsHitTestVisible = false,
            };
            yLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(yLbl, Math.Max(0, padL - yLbl.DesiredSize.Width - 6));
            Canvas.SetTop(yLbl, gy - yLbl.DesiredSize.Height / 2);
            ProbCurveCanvas.Children.Add(yLbl);
        }

        for (int xTick = 0; xTick <= 4; xTick++)
        {
            double frac = xTick / 4.0;
            double gx = padL + frac * plotW;
            long cVal = (long)Math.Round(frac * _probChartMaxChests);
            string cText = cVal >= 1_000_000 ? $"{cVal / 1_000_000.0:0.##}M"
                : cVal >= 10_000 ? $"{cVal / 1_000.0:0.#}k"
                : cVal.ToString("N0");
            var xLbl = new TextBlock { Text = cText + " baús", FontSize = 9.5, Foreground = ResBrush("Muted"), IsHitTestVisible = false };
            xLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(xLbl, Math.Clamp(gx - xLbl.DesiredSize.Width / 2, padL, w - xLbl.DesiredSize.Width));
            Canvas.SetTop(xLbl, padT + plotH + 5);
            ProbCurveCanvas.Children.Add(xLbl);
        }

        const int steps = 120;
        var accentColor = (Color)ColorConverter.ConvertFromString(selected.HexColor);
        var accentBrush = new SolidColorBrush(accentColor);
        accentBrush.Freeze();

        var areaGeo = new StreamGeometry();
        var lineGeo = new StreamGeometry();
        using (var actx = areaGeo.Open())
        using (var lctx = lineGeo.Open())
        {
            actx.BeginFigure(new Point(padL, padT + plotH), true, true);
            for (int s = 0; s <= steps; s++)
            {
                double c = (_probChartMaxChests * s) / steps;
                double p = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, (long)Math.Round(c));
                var pt = new Point(XOf(c), YOf(p));
                actx.LineTo(pt, true, false);
                if (s == 0) lctx.BeginFigure(pt, false, false);
                else lctx.LineTo(pt, true, false);
            }
            actx.LineTo(new Point(padL + plotW, padT + plotH), true, false);
        }
        areaGeo.Freeze();
        lineGeo.Freeze();

        var fillGrad = new LinearGradientBrush(
            Color.FromArgb(72, accentColor.R, accentColor.G, accentColor.B),
            Color.FromArgb(4, accentColor.R, accentColor.G, accentColor.B),
            new Point(0, 0), new Point(0, 1));
        fillGrad.Freeze();

        ProbCurveCanvas.Children.Add(new System.Windows.Shapes.Path { Data = areaGeo, Fill = fillGrad, IsHitTestVisible = false });
        ProbCurveCanvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = lineGeo, Stroke = accentBrush, StrokeThickness = 2.3, IsHitTestVisible = false
        });

        if (boostMult > 1.0)
        {
            var boostGeo = new StreamGeometry();
            using (var bctx = boostGeo.Open())
            {
                for (int s = 0; s <= steps; s++)
                {
                    double c = (_probChartMaxChests * s) / steps;
                    double p = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn / boostMult, (long)Math.Round(c));
                    var pt = new Point(XOf(c), YOf(p));
                    if (s == 0) bctx.BeginFigure(pt, false, false);
                    else bctx.LineTo(pt, true, false);
                }
            }
            boostGeo.Freeze();
            ProbCurveCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = boostGeo,
                Stroke = ResBrush("Gem"),
                StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Opacity = 0.85,
                IsHitTestVisible = false,
            });
        }

        Rect? youBadgeRect = null;
        if (seasonYou <= _probChartMaxChests)
        {
            double youP = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, seasonYou);
            youBadgeRect = DrawMarkerPin(ProbCurveCanvas, XOf(seasonYou), YOf(youP), padT + plotH,
                accentBrush, $"VOCÊ · {seasonYou:N0} ({TreasureCatalog.FormatChance(youP, true)})", placeAbove: true, avoidRect: null);
        }

        if (showSquad && seasonSquad <= _probChartMaxChests)
        {
            double sqP = TreasureCatalog.ChanceOfAtLeastOne(selected.OneIn, seasonSquad);
            DrawMarkerPin(ProbCurveCanvas, XOf(seasonSquad), YOf(sqP), padT + plotH,
                ResBrush("Cyan"), $"Equipe ≥ {TreasureCatalog.FormatChance(sqP, true)}", placeAbove: false, avoidRect: youBadgeRect);
        }

        // Dedicated O(1) reusable hover crosshair elements (hidden until MouseMove)
        _probCrosshairLine = new Line
        {
            Stroke = ResBrush("Steel"), StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 }, Opacity = 0.75,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        _probCrosshairDot = new Ellipse
        {
            Width = 9, Height = 9, Fill = ResBrush("Text"), Stroke = accentBrush, StrokeThickness = 2,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        ProbCurveCanvas.Children.Add(_probCrosshairLine);
        ProbCurveCanvas.Children.Add(_probCrosshairDot);
    }

    private Rect DrawMarkerPin(Canvas canvas, double x, double y, double bottomY, Brush color, string text, bool placeAbove, Rect? avoidRect)
    {
        canvas.Children.Add(new Line
        {
            X1 = x, Y1 = y, X2 = x, Y2 = bottomY,
            Stroke = color, StrokeThickness = 1.2, Opacity = 0.55,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            IsHitTestVisible = false,
        });

        var halo = new Ellipse { Width = 16, Height = 16, Fill = color, Opacity = 0.24, IsHitTestVisible = false };
        Canvas.SetLeft(halo, x - 8);
        Canvas.SetTop(halo, y - 8);
        canvas.Children.Add(halo);

        var dot = new Ellipse { Width = 8.5, Height = 8.5, Fill = color, Stroke = ResBrush("Bg"), StrokeThickness = 1.5, IsHitTestVisible = false };
        Canvas.SetLeft(dot, x - 4.25);
        Canvas.SetTop(dot, y - 4.25);
        canvas.Children.Add(dot);

        var badge = new Border
        {
            Background = HexBrush("#EA10131C", ResBrush("Card")),
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text, Foreground = color, FontSize = 10, FontWeight = FontWeights.Bold, IsHitTestVisible = false }
        };
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = badge.DesiredSize.Width;
        double bh = badge.DesiredSize.Height;

        double bx = Math.Clamp(x - bw / 2, 54, Math.Max(54, canvas.ActualWidth - bw - 6));
        double by = placeAbove
            ? Math.Max(4, y - bh - 10)
            : Math.Min(bottomY - bh - 4, y + 10);

        var rect = new Rect(bx, by, bw, bh);
        if (avoidRect is { } av && rect.IntersectsWith(av))
        {
            double tryAbove = av.Top - bh - 4;
            double tryRight = av.Right + 6;
            if (tryAbove >= 4) by = tryAbove;
            else if (tryRight + bw <= canvas.ActualWidth - 4) bx = tryRight;
            rect = new Rect(bx, by, bw, bh);
        }

        Canvas.SetLeft(badge, bx);
        Canvas.SetTop(badge, by);
        canvas.Children.Add(badge);
        return rect;
    }

    // ==================== VECTOR CHART 3: REAL SEASON PROGRESSION (O(1) HOVER) ====================

    private void SeasonProbCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildSeasonProgressionChart();

    private void SeasonProbCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_seasonCrosshairLine != null) _seasonCrosshairLine.Visibility = Visibility.Collapsed;
        if (_seasonCrosshairDot != null) _seasonCrosshairDot.Visibility = Visibility.Collapsed;
        if (SeasonProbTooltipBorder != null) SeasonProbTooltipBorder.Visibility = Visibility.Collapsed;
        SeasonProbHoverText.Text = "Mostra como sua chance acumulada cresceu da 1ª partida até agora.";
    }

    private void SeasonProbCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (SeasonProbCanvas.ActualWidth <= 80 || _seasonProgPoints.Count == 0) return;
        UpdateSeasonProbHover(e.GetPosition(SeasonProbCanvas).X);
    }

    private void UpdateSeasonProbHover(double mx)
    {
        int count = _seasonProgPoints.Count;
        if (count == 0) return;

        double w = SeasonProbCanvas.ActualWidth;
        double h = SeasonProbCanvas.ActualHeight;
        const double padL = 52, padR = 16, padT = 14, padB = 24;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW <= 20 || plotH <= 20 || mx < padL || mx > padL + plotW)
        {
            SeasonProbCanvas_MouseLeave(this, null!);
            return;
        }

        var selected = TreasureCatalog.Odds.FirstOrDefault(o => o.Kind == _selectedProbKind) ?? TreasureCatalog.Odds[0];
        double maxP = Math.Min(1.0, Math.Max(0.0005, _seasonProgPoints[^1].Prob * 1.15));

        int idx = count == 1 ? 0 : Math.Clamp((int)Math.Round(((mx - padL) / plotW) * (count - 1)), 0, count - 1);
        var pInfo = _seasonProgPoints[idx];
        double hx = count == 1 ? padL + plotW / 2 : padL + (idx / (double)(count - 1)) * plotW;
        double hy = padT + (1.0 - Math.Clamp(pInfo.Prob / maxP, 0, 1)) * plotH;

        if (_seasonCrosshairLine != null)
        {
            _seasonCrosshairLine.X1 = _seasonCrosshairLine.X2 = hx;
            _seasonCrosshairLine.Y1 = padT;
            _seasonCrosshairLine.Y2 = padT + plotH;
            _seasonCrosshairLine.Visibility = Visibility.Visible;
        }
        if (_seasonCrosshairDot != null)
        {
            Canvas.SetLeft(_seasonCrosshairDot, hx - 4.5);
            Canvas.SetTop(_seasonCrosshairDot, hy - 4.5);
            _seasonCrosshairDot.Visibility = Visibility.Visible;
        }

        SeasonProbTooltipBorder.BorderBrush = HexBrush(selected.HexColor, ResBrush("Gold"));
        SeasonProbTooltipText.Text =
            $"Partida #{pInfo.MatchNum} · {pInfo.TimeUtc.ToLocalTime():dd/MM/yy HH:mm}\n+{pInfo.MatchYou} baús → Total: {pInfo.CumChests:N0} ({TreasureCatalog.FormatChance(pInfo.Prob, true)})";
        SeasonProbTooltipBorder.Visibility = Visibility.Visible;
        PositionFloatingTooltip(SeasonProbTooltipBorder, SeasonProbCanvas, hx, hy, hy);

        SeasonProbHoverText.Text =
            $"Partida #{pInfo.MatchNum} ({pInfo.TimeUtc.ToLocalTime():dd/MM HH:mm}): +{pInfo.MatchYou} baús → {pInfo.CumChests:N0} acumulados ({TreasureCatalog.FormatChance(pInfo.Prob, true)})";
    }

    private void RebuildSeasonProgressionChart()
    {
        SeasonProbCanvas.Children.Clear();
        _seasonCrosshairLine = null;
        _seasonCrosshairDot = null;
        if (SeasonProbTooltipBorder != null) SeasonProbTooltipBorder.Visibility = Visibility.Collapsed;

        double w = SeasonProbCanvas.ActualWidth;
        double h = SeasonProbCanvas.ActualHeight;
        if (w <= 80 || h <= 60) return;

        if (_seasonProgPoints.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "Sincronize os replays para visualizar sua curva real da temporada.",
                Foreground = ResBrush("Muted"), FontSize = 12, IsHitTestVisible = false,
            };
            Canvas.SetLeft(empty, 16);
            Canvas.SetTop(empty, h / 2 - 10);
            SeasonProbCanvas.Children.Add(empty);
            return;
        }

        var selected = TreasureCatalog.Odds.FirstOrDefault(o => o.Kind == _selectedProbKind) ?? TreasureCatalog.Odds[0];
        var accentColor = (Color)ColorConverter.ConvertFromString(selected.HexColor);
        var accentBrush = new SolidColorBrush(accentColor);
        accentBrush.Freeze();

        const double padL = 52, padR = 16, padT = 14, padB = 24;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;

        double maxP = Math.Min(1.0, Math.Max(0.0005, _seasonProgPoints[^1].Prob * 1.15));
        int count = _seasonProgPoints.Count;

        double XAt(int i) => count == 1 ? padL + plotW / 2 : padL + (i / (double)(count - 1)) * plotW;
        double YAt(double p) => padT + (1.0 - Math.Clamp(p / maxP, 0, 1)) * plotH;

        for (int g = 0; g <= 3; g++)
        {
            double frac = g / 3.0;
            double gy = padT + (1.0 - frac) * plotH;
            SeasonProbCanvas.Children.Add(new Line
            {
                X1 = padL, Y1 = gy, X2 = padL + plotW, Y2 = gy,
                Stroke = ResBrush("Stroke"), StrokeThickness = 1, Opacity = g == 0 ? 1.0 : 0.55,
                IsHitTestVisible = false,
            });
            var yLbl = new TextBlock
            {
                Text = TreasureCatalog.FormatChance(frac * maxP, highPrecision: maxP < 0.15),
                FontSize = 10, Foreground = ResBrush("Muted"), IsHitTestVisible = false,
            };
            yLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(yLbl, Math.Max(0, padL - yLbl.DesiredSize.Width - 6));
            Canvas.SetTop(yLbl, gy - yLbl.DesiredSize.Height / 2);
            SeasonProbCanvas.Children.Add(yLbl);
        }

        var firstLbl = new TextBlock { Text = "Partida #1", FontSize = 9.5, Foreground = ResBrush("Muted"), IsHitTestVisible = false };
        Canvas.SetLeft(firstLbl, padL);
        Canvas.SetTop(firstLbl, padT + plotH + 5);
        SeasonProbCanvas.Children.Add(firstLbl);

        var lastLbl = new TextBlock { Text = $"Partida #{count}", FontSize = 9.5, Foreground = ResBrush("Muted"), IsHitTestVisible = false };
        lastLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(lastLbl, Math.Max(padL + 60, padL + plotW - lastLbl.DesiredSize.Width));
        Canvas.SetTop(lastLbl, padT + plotH + 5);
        SeasonProbCanvas.Children.Add(lastLbl);

        var areaGeo = new StreamGeometry();
        var lineGeo = new StreamGeometry();
        using (var actx = areaGeo.Open())
        using (var lctx = lineGeo.Open())
        {
            actx.BeginFigure(new Point(XAt(0), padT + plotH), true, true);
            for (int i = 0; i < count; i++)
            {
                var pt = new Point(XAt(i), YAt(_seasonProgPoints[i].Prob));
                actx.LineTo(pt, true, false);
                if (i == 0) lctx.BeginFigure(pt, false, false);
                else lctx.LineTo(pt, true, false);
            }
            actx.LineTo(new Point(XAt(count - 1), padT + plotH), true, false);
        }
        areaGeo.Freeze();
        lineGeo.Freeze();

        var fillGrad = new LinearGradientBrush(
            Color.FromArgb(68, accentColor.R, accentColor.G, accentColor.B),
            Color.FromArgb(4, accentColor.R, accentColor.G, accentColor.B),
            new Point(0, 0), new Point(0, 1));
        fillGrad.Freeze();

        SeasonProbCanvas.Children.Add(new System.Windows.Shapes.Path { Data = areaGeo, Fill = fillGrad, IsHitTestVisible = false });
        SeasonProbCanvas.Children.Add(new System.Windows.Shapes.Path { Data = lineGeo, Stroke = accentBrush, StrokeThickness = 2.3, IsHitTestVisible = false });

        var endPt = new Point(XAt(count - 1), YAt(_seasonProgPoints[^1].Prob));
        var endDot = new Ellipse { Width = 8.5, Height = 8.5, Fill = accentBrush, Stroke = ResBrush("Bg"), StrokeThickness = 1.5, IsHitTestVisible = false };
        Canvas.SetLeft(endDot, endPt.X - 4.25);
        Canvas.SetTop(endDot, endPt.Y - 4.25);
        SeasonProbCanvas.Children.Add(endDot);

        _seasonCrosshairLine = new Line
        {
            Stroke = ResBrush("Steel"), StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 }, Opacity = 0.75,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        _seasonCrosshairDot = new Ellipse
        {
            Width = 9, Height = 9, Fill = ResBrush("Text"), Stroke = accentBrush, StrokeThickness = 2,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        SeasonProbCanvas.Children.Add(_seasonCrosshairLine);
        SeasonProbCanvas.Children.Add(_seasonCrosshairDot);
    }

    // ==================== ACTIONS, DIAGNOSTICS & SYNC ====================

    private static string Finder(PrizeFind p)
    {
        if (Treasures.IsGizmo(p.PrizeId))
            return p.By switch { "" => "seu esquadrão", "you" => "você / esquadrão", _ => $"baú de {p.By}" };
        return p.Mine ? "você"
            : p.Team ? (string.IsNullOrEmpty(p.By) ? "seu esquadrão" : $"{p.By} (esquadrão)")
            : string.IsNullOrEmpty(p.By) ? "jogador próximo" : p.By;
    }

    private void UpdateDiagnosticsText()
    {
        if (DiagOcrText == null) return;
        DiagOcrText.Text = _live?.Available == true
            ? $"✓ OCR do Windows ativo ({_live.LanguageTag}) · Pré-filtro rápido e captura multi-escala ligados"
            : _isVerifyMode
                ? "✓ Motor óptico pronto para sessão normal"
                : "⚠ OCR de idioma ausente no Windows · Ative Português (Brasil) nas Opções de Idioma";

        DiagDemosText.Text = _isVerifyMode ? "Demonstração com partidas fictícias"
            : ChestCounterService.DemosFolderExists()
            ? $"✓ Pasta de replays ativa: {ChestCounterService.FindReplays().Count} arquivos .replay detectados"
            : "⚠ Pasta de replays não encontrada (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos)";

        DiagStorageText.Text =
            $"✓ Histórico salvo: {_stats.Matches} partidas · {_stats.YourChests:N0} baús · {_stats.ScannedFiles.Count} replays indexados";
    }

    private void CopyObsUrl_Click(object sender, MouseButtonEventArgs e) => CopyObsUrlToClipboard();
    private void CopyObsUrlButton_Click(object sender, RoutedEventArgs e) => CopyObsUrlToClipboard();

    private void CopyObsUrlToClipboard()
    {
        try
        {
            Clipboard.SetText(AppChannel.OverlayUrl);
            StatusText.Text = $"✓ Endereço {AppChannel.OverlayUrl} copiado para a área de transferência!";
        }
        catch { }
    }

    private void OpenObsBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppChannel.OverlayUrl) { UseShellExecute = true }); }
        catch { }
    }

    private void OpenDemosFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(ChestCounterService.DefaultDemosPath))
                Process.Start(new ProcessStartInfo(ChestCounterService.DefaultDemosPath) { UseShellExecute = true });
            else
                StatusText.Text = "A pasta de replays do Fortnite ainda não existe neste computador.";
        }
        catch { }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(ChestCounterService.StatsPath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    private static string CsvField(string s)
    {
        var value = s.AsSpan().TrimStart();
        if (!value.IsEmpty && value[0] is '=' or '+' or '-' or '@') s = "'" + s;
        if (s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar Histórico de Baús",
                FileName = $"baus-temporada-{DateTime.Now:yyyy-MM-dd}.csv",
                Filter = "Planilha CSV (*.csv)|*.csv",
            };
            if (dlg.ShowDialog(this) != true) return;

            var recs = _stats.RecordsSince(null);
            var sb = new StringBuilder();
            sb.AppendLine("Partida;DataHoraLocal;Modo;SeusBaus;BausAcumulados;BausEsquadraoVisiveis;ChanceTokenPirata;Tesouros;Origem;MatchId;ColegasVisiveis;BausSemAutorNoReplay;"
                + string.Join(";", ChestKinds.All.Select(k => k.Name)) + ";SemTipo");
            long cum = 0;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                cum += r.You;
                double tokenP = TreasureCatalog.ChanceOfAtLeastOne(137_500, cum);
                string prizes = string.Join(" | ", r.Prizes.Select(p => Treasures.Name(p.PrizeId)));
                sb.AppendLine(string.Join(";",
                    i + 1,
                    r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    CsvField(LifetimeStats.FriendlyPlaylist(r.Playlist, r.Source)),
                    r.You,
                    cum,
                    r.SquadVisibleChests,
                    TreasureCatalog.FormatChance(tokenP, true),
                    CsvField(prizes),
                    CsvField(r.Source),
                    CsvField(r.MatchId),
                    CsvField(string.Join(" | ", r.Mates.Select(m => $"{m.Name} ({m.Chests})"))),
                    r.Unattributed,
                    string.Join(";", ChestKinds.All.Select(k => r.CountOf(k))),
                    r.UntypedChests));
            }
            // UTF-8 with BOM ensures Microsoft Excel (pt-BR) renders accents (Baús, Esquadrão) properly!
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = $"✓ Relatório exportado ({recs.Count} partidas, {cum:N0} baús): {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao exportar CSV: {ex.Message}";
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Apagar todas as partidas salvas no cache e recomeçar?\n\nOs arquivos .replay que ainda estão na pasta do Fortnite poderão ser sincronizados novamente.",
            "Zerar Histórico", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        _stats = new LifetimeStats { Version = LifetimeStats.CurrentVersion };
        ChestCounterService.Save(_stats);
        _oddsCacheKey = "";
        Render();
        UpdateDiagnosticsText();
        StatusText.Text = "Histórico zerado. Clique em Sincronizar para importar os replays da pasta.";
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await RunScanAsync(isAutoStartup: false);

    private async Task RunScanAsync(bool isAutoStartup)
    {
        if (_scanning) return;

        var files = ChestCounterService.FindReplays();
        if (files.Count == 0)
        {
            if (!isAutoStartup)
                StatusText.Text = "Nenhum arquivo .replay encontrado. Ative a gravação de replays no Fortnite.";
            return;
        }

        _scanning = true;
        ResetButton.IsEnabled = ScanButton.IsEnabled = false;
        ScanButton.Content = "Sincronizando…";
        ScanProgressBar.Value = 0;
        ScanProgressBar.Visibility = Visibility.Visible;
        SyncBadgeText.Text = $"Sincronizando 0/{files.Count} replays…";
        StatusText.Text = isAutoStartup
            ? $"Contabilizando replays da temporada (0/{files.Count})…"
            : $"Lendo replays em paralelo (0/{files.Count})…";

        var sw = Stopwatch.StartNew();
        try
        {
            var (run, stats) = await Task.Run(() => ChestCounterService.Scan(files, (done, total, _, _) =>
            {
                if (done % 4 == 0 || done == total)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!_scanning) return;
                        ScanProgressBar.Value = total > 0 ? (done * 100.0) / total : 100;
                        StatusText.Text = $"Verificando replays ({done}/{total})…";
                        SyncBadgeText.Text = $"Sincronizando {done}/{total}…";
                    });
                }
            }));

            sw.Stop();
            _stats = stats;
            _live?.NotifyExternalScan(stats);
            _oddsCacheKey = "";
            Render();
            UpdateDiagnosticsText();

            var season = _stats.Over(ChestCounterService.SeasonStartUtc);
            SyncBadgeText.Text = "✓ Contagem automática da temporada";

            var extras = new List<string>();
            if (run.NewMatches > 0) extras.Add($"+{run.NewMatches} novas");
            if (run.ActiveRecording > 0) extras.Add($"{run.ActiveRecording} em andamento");
            if (run.SkippedMode > 0) extras.Add($"{run.SkippedMode} outros modos");
            string extraStr = extras.Count > 0 ? $" ({string.Join(" · ", extras)})" : "";

            StatusText.Text =
                $"✓ Sincronizado em {sw.Elapsed.TotalSeconds:F1}s · {season.YourChests:N0} baús em {season.Matches} partidas{extraStr}";
        }
        catch (Exception ex)
        {
            SyncBadgeText.Text = "⚠ Aviso na leitura";
            StatusText.Text = $"Falha ao sincronizar replays: {ex.Message}";
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            ScanButton.Content = "Sincronizar";
            ResetButton.IsEnabled = ScanButton.IsEnabled = true;
            _scanning = false;
        }
    }

    private const int WM_HOTKEY = 0x0312;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int HotkeyToggle = 9001;
    private const int HotkeyLock = 9002;
    private const int HotkeyStyleCycle = 9003;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
}
