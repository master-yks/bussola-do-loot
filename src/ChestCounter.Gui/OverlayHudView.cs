using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChestCounter;

namespace ChestCounter.Gui;

public readonly record struct OverlayStatsSnapshot(
    double Avg,
    int? Last,
    int Best,
    long SeasonTotal,
    string TokenChance,
    string TreasureHourText,
    bool TreasureHourLive);

/// <summary>
/// Shared, pixel-accurate HUD renderer used both by the floating in-game overlay window
/// and by the interactive Live Preview Stage inside the app customizer.
/// </summary>
public sealed class OverlayHudView : Grid
{
    private static readonly FontFamily FontFortnite = new("Bahnschrift Condensed, Bahnschrift, Segoe UI");
    private static readonly FontFamily FontModern = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily FontMono = new("Cascadia Mono, Consolas, Segoe UI");

    private static readonly SolidColorBrush LiveDotBrush = Freeze(new(Color.FromRgb(0x3D, 0xDC, 0x84)));
    private static readonly SolidColorBrush IdleDotBrush = Freeze(new(Color.FromRgb(0xFF, 0x4D, 0x73)));
    private static readonly SolidColorBrush DarkRingBrush = Freeze(new(Color.FromArgb(0xE8, 0x06, 0x08, 0x0E)));
    private static readonly SolidColorBrush StrokeDarkBrush = Freeze(new(Color.FromArgb(0xEE, 0x04, 0x06, 0x0B)));
    private static readonly SolidColorBrush DividerBrush = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush GemHotBrush = Freeze(new(Color.FromRgb(0xFF, 0x4D, 0x73)));
    private static BitmapImage? SharedChestImage;

    private readonly ScaleTransform _layoutScale = new(1.0, 1.0);
    private readonly ScaleTransform _pulseScale = new(1.0, 1.0);
    private readonly Grid _backdropHost;
    private readonly Border _backdropPlate;
    private readonly Border _accentSlash;
    private readonly Grid _contentHost;
    private readonly Border _pulseBadge;
    private readonly OutlinedTextBlock _pulseBadgeText;
    private readonly Border _toastBadge;
    private readonly TextBlock _toastBadgeText;
    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _toastTimer;

    private OverlaySettings _settings = OverlayPresets.All[0].Create();
    private OverlayStatsSnapshot _stats = new(0, null, 0, 0, "0,000%", "", false);
    private int? _liveCount;
    private bool _locked;
    private bool _toastActive;

    public bool IsPreview { get; set; }

    public OverlayHudView()
    {
        Margin = new Thickness(8);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        LayoutTransform = _layoutScale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _pulseScale;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _backdropPlate = new Border
        {
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };

        _accentSlash = new Border
        {
            Width = 3.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 5, 0, 5),
            IsHitTestVisible = false,
        };

        _backdropHost = new Grid
        {
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _backdropHost.Children.Add(_backdropPlate);
        _backdropHost.Children.Add(_accentSlash);

        _contentHost = new Grid
        {
            IsHitTestVisible = false,
        };

        _pulseBadgeText = new OutlinedTextBlock
        {
            Text = "+1",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Stroke = StrokeDarkBrush,
            StrokeThickness = 1.6,
        };
        _pulseBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -7, -6, 0),
            Padding = new Thickness(5, 1, 5, 1),
            CornerRadius = new CornerRadius(7),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _pulseBadgeText,
        };

        _toastBadgeText = new TextBlock
        {
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
        };
        _toastBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, 0, 0),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            CornerRadius = new CornerRadius(5),
            Background = Freeze(new SolidColorBrush(Color.FromArgb(0xE8, 0x0E, 0x13, 0x1F))),
            BorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _toastBadgeText,
        };

        Children.Add(_backdropHost);
        Children.Add(_contentHost);
        Children.Add(_pulseBadge);
        Children.Add(_toastBadge);

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1550) };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseTimer.Stop();
            _pulseBadge.Visibility = Visibility.Collapsed;
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1650) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toastActive = false;
            _toastBadge.Visibility = Visibility.Collapsed;
            UpdateEffectiveOpacity();
        };

        RebuildVisuals();
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private static BitmapImage? GetChestImage()
    {
        if (SharedChestImage != null) return SharedChestImage;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/chest.png", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            SharedChestImage = bmp;
        }
        catch { }
        return SharedChestImage;
    }

    public void ApplySettings(OverlaySettings settings)
    {
        _settings = settings.Clone();
        _settings.Normalize();
        RebuildVisuals();
    }

    public void SetLive(int? n, bool allowPulse = true)
    {
        int? prev = _liveCount;
        _liveCount = n;
        RebuildVisuals();

        if (allowPulse && _settings.PulseOnChest && prev.HasValue && n.HasValue && n.Value > prev.Value)
            TriggerSimulatedPulse(n.Value - prev.Value);
    }

    public void SetStats(OverlayStatsSnapshot snapshot)
    {
        _stats = snapshot;
        RebuildVisuals();
    }

    public void SetLockedState(bool locked, bool showTransientToast = false)
    {
        _locked = locked;
        if (showTransientToast && !IsPreview)
        {
            _toastActive = true;
            _toastBadgeText.Text = locked ? "🔒 CLIQUES NO JOGO" : "🔓 MODO MÓVEL";
            _toastBadgeText.Foreground = locked ? LiveDotBrush : Freeze(new(ParseColor(_settings.Accent, Color.FromRgb(0xF5, 0xB9, 0x42))));
            _toastBadge.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        RebuildVisuals();
    }

    public void TriggerSimulatedPulse(int delta = 1)
    {
        if (!_settings.PulseOnChest && !IsPreview) return;

        var accent = ParseColor(_settings.Accent, Color.FromRgb(0xF5, 0xB9, 0x42));
        _pulseBadgeText.Text = $"+{Math.Max(1, delta)}";
        _pulseBadgeText.Fill = Freeze(new(accent));
        _pulseBadge.Background = Freeze(new(Color.FromArgb(0xB8, 0x09, 0x0C, 0x14)));
        _pulseBadge.BorderBrush = Freeze(new(Color.FromArgb(0x99, accent.R, accent.G, accent.B)));
        _pulseBadge.BorderThickness = new Thickness(1);
        _pulseBadge.Visibility = Visibility.Visible;
        _pulseTimer.Stop();
        _pulseTimer.Start();

        try
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(220),
            };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.09, KeyTime.FromPercent(0.42))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });
            _pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            _pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        catch { }
    }

    private void UpdateEffectiveOpacity()
    {
        bool active = _liveCount != null;

        if (_settings.HideWhenIdle && !active && !_toastActive)
        {
            if (IsPreview)
            {
                Opacity = Math.Max(0.38, _settings.HudOpacity * 0.45);
                return;
            }
            if (_locked)
            {
                Opacity = 0.0;
                return;
            }
        }

        double baseOp = _settings.HudOpacity;
        if (!active && _settings.DimWhenIdle)
            baseOp *= 0.78;

        Opacity = Math.Clamp(baseOp, 0.20, 1.0);
    }

    private void RebuildVisuals()
    {
        _layoutScale.ScaleX = _settings.Scale;
        _layoutScale.ScaleY = _settings.Scale;

        var accentColor = ParseColor(_settings.Accent, Color.FromRgb(0xF5, 0xB9, 0x42));
        var secondaryColor = ParseColor(_settings.SecondaryAccent, Color.FromRgb(0x4C, 0xC9, 0xF0));
        var bgColor = ParseColor(_settings.BackgroundHex, Color.FromRgb(0x0A, 0x0F, 0x1A));
        var textColor = ParseColor(_settings.TextHex, Color.FromRgb(0xF8, 0xFA, 0xFC));

        var accentBrush = Freeze(new(accentColor));
        var secondaryBrush = Freeze(new(secondaryColor));
        var textBrush = Freeze(new(textColor));
        var labelColor = Blend(textColor, Color.FromRgb(0x94, 0xA3, 0xB8), 0.52);
        var labelBrush = Freeze(new(labelColor));

        var numFont = _settings.FontStyle switch
        {
            "modern" => FontModern,
            "mono" => FontMono,
            _ => FontFortnite,
        };

        ConfigureBackdrop(bgColor, accentColor, accentBrush);

        _contentHost.Effect = _settings.TextShadow
            ? new DropShadowEffect
            {
                BlurRadius = _settings.Chrome == "none" || _settings.BgOpacity < 0.18 ? 6.0 : 4.5,
                ShadowDepth = 1.3,
                Direction = 290,
                Opacity = _settings.Chrome == "none" || _settings.BgOpacity < 0.18 ? 0.88 : 0.72,
                Color = Colors.Black,
            }
            : null;

        _contentHost.Children.Clear();

        var metrics = BuildSecondaryMetrics(textBrush, secondaryBrush, accentBrush);

        UIElement body = _settings.Layout switch
        {
            "pill" => BuildPillLayout(numFont, accentBrush, textBrush, labelBrush, metrics),
            "micro" => BuildMicroLayout(numFont, accentBrush, textBrush, labelBrush, metrics),
            "vertical" => BuildVerticalLayout(numFont, accentBrush, textBrush, labelBrush, metrics),
            "card" => BuildCardLayout(numFont, accentBrush, secondaryBrush, textBrush, labelBrush, metrics),
            _ => BuildTacticalLayout(numFont, accentBrush, textBrush, labelBrush, metrics),
        };

        _contentHost.Children.Add(body);
        UpdateEffectiveOpacity();
    }

    private void ConfigureBackdrop(Color bgColor, Color accentColor, SolidColorBrush accentBrush)
    {
        bool noChrome = _settings.Chrome == "none" || (_settings.BgOpacity <= 0.01 && _settings.BorderStrength <= 0.01);
        if (noChrome)
        {
            _backdropHost.Visibility = Visibility.Collapsed;
            return;
        }

        _backdropHost.Visibility = Visibility.Visible;

        bool isAngled = _settings.Chrome == "angled" && _settings.Layout is not ("card" or "vertical");
        _backdropHost.RenderTransform = isAngled ? new SkewTransform(-8.5, 0) : Transform.Identity;

        double bgAlpha = _settings.BgOpacity;
        if (bgAlpha > 0.005)
        {
            var topTint = Blend(bgColor, accentColor, _settings.Chrome is "angled" or "glass" ? 0.10 : 0.04);
            byte aTop = (byte)Math.Clamp(Math.Round(bgAlpha * 255), 0, 255);
            byte aBot = (byte)Math.Clamp(Math.Round(Math.Min(1.0, bgAlpha * 1.12) * 255), 0, 255);

            var grad = new LinearGradientBrush(
                Color.FromArgb(aTop, topTint.R, topTint.G, topTint.B),
                Color.FromArgb(aBot, bgColor.R, bgColor.G, bgColor.B),
                new Point(0, 0), new Point(1, 1));
            grad.Freeze();
            _backdropPlate.Background = grad;
        }
        else
        {
            _backdropPlate.Background = Brushes.Transparent;
        }

        double bStr = _settings.BorderStrength;
        if (bStr > 0.02)
        {
            byte aEdge = (byte)Math.Clamp(Math.Round(bStr * 215), 0, 255);
            byte aSoft = (byte)Math.Clamp(Math.Round(bStr * 85), 0, 255);
            var borderTint = Blend(Colors.White, accentColor, _settings.Chrome is "angled" or "capsule" ? 0.55 : 0.35);
            var bGrad = new LinearGradientBrush(
                Color.FromArgb(aEdge, borderTint.R, borderTint.G, borderTint.B),
                Color.FromArgb(aSoft, 0xFF, 0xFF, 0xFF),
                new Point(0, 0), new Point(1, 1));
            bGrad.Freeze();
            _backdropPlate.BorderBrush = bGrad;
            _backdropPlate.BorderThickness = new Thickness(1);
        }
        else
        {
            _backdropPlate.BorderBrush = null;
            _backdropPlate.BorderThickness = new Thickness(0);
        }

        _backdropPlate.CornerRadius = _settings.Chrome switch
        {
            "capsule" => new CornerRadius(_settings.Layout is "card" or "vertical" ? 16 : 22),
            "sharp" => new CornerRadius(3),
            "angled" => new CornerRadius(6),
            _ => new CornerRadius(10),
        };

        _backdropPlate.Effect = bgAlpha >= 0.14
            ? new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = Math.Clamp(bgAlpha * 0.65, 0.14, 0.48),
                Color = Colors.Black,
            }
            : null;

        bool showSlash = _settings.AccentBar && _settings.Chrome != "capsule";
        _accentSlash.Visibility = showSlash ? Visibility.Visible : Visibility.Collapsed;
        if (showSlash)
        {
            _accentSlash.Background = accentBrush;
            _accentSlash.Margin = _settings.Chrome == "sharp"
                ? new Thickness(0, 0, 0, 0)
                : new Thickness(0.5, 6, 0, 6);
            _accentSlash.CornerRadius = _settings.Chrome == "sharp"
                ? new CornerRadius(2, 0, 0, 2)
                : new CornerRadius(2);
        }
    }

    private List<(string ShortTag, string FullTag, string Value, Brush ValueBrush)> BuildSecondaryMetrics(
        Brush textBrush, Brush secondaryBrush, Brush accentBrush)
    {
        var list = new List<(string, string, string, Brush)>(6);
        if (_settings.ShowSeason)
            list.Add(("TEMP", "Temporada", _stats.SeasonTotal.ToString("N0"), textBrush));
        if (_settings.ShowToken)
            list.Add(("TOKEN", "Token Pirata", _stats.TokenChance, secondaryBrush));
        if (_settings.ShowAvg)
            list.Add(("MÉD", "Média", _stats.Avg.ToString("F1"), textBrush));
        if (_settings.ShowLast)
            list.Add(("ÚLT", "Última", _stats.Last?.ToString() ?? "—", textBrush));
        if (_settings.ShowBest)
            list.Add(("MÁX", "Recorde", _stats.Best.ToString(), accentBrush));
        if (_settings.ShowTreasureHour && !string.IsNullOrWhiteSpace(_stats.TreasureHourText))
            list.Add(("HORA", "Hora Tesouro", _stats.TreasureHourText, _stats.TreasureHourLive ? GemHotBrush : secondaryBrush));
        return list;
    }

    private Thickness GetAdaptivePadding(double hPad, double vPad)
    {
        if (_settings.Chrome == "none" || (_settings.BgOpacity <= 0.01 && _settings.BorderStrength <= 0.01))
            return new Thickness(3, 1, 3, 1);

        bool isAngled = _settings.Chrome == "angled" && _settings.Layout is not ("card" or "vertical");
        double leftExtra = (_settings.AccentBar && _settings.Chrome != "capsule" ? 3.5 : 0) + (isAngled ? 4 : 0);
        double rightExtra = isAngled ? 4 : 0;
        return new Thickness(hPad + leftExtra, vPad, hPad + rightExtra, vPad);
    }

    private UIElement BuildIconWithStatusDot(double iconSize)
    {
        bool active = _liveCount != null;
        var host = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var src = GetChestImage();
        if (src != null)
        {
            var img = new Image
            {
                Source = src,
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            host.Children.Add(img);
        }

        var dot = new Border
        {
            Width = Math.Max(6, iconSize * 0.28),
            Height = Math.Max(6, iconSize * 0.28),
            CornerRadius = new CornerRadius(6),
            Background = active ? LiveDotBrush : IdleDotBrush,
            BorderBrush = DarkRingBrush,
            BorderThickness = new Thickness(1.2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -1, -1),
        };
        host.Children.Add(dot);
        return host;
    }

    private UIElement BuildSimpleStatusDot(double size = 7)
    {
        bool active = _liveCount != null;
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size),
            Background = active ? LiveDotBrush : IdleDotBrush,
            BorderBrush = DarkRingBrush,
            BorderThickness = new Thickness(1.1),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private OutlinedTextBlock MakeText(string text, FontFamily font, double size, FontWeight weight, Brush fill, double strokeFactor = 1.0)
    {
        return new OutlinedTextBlock
        {
            Text = text,
            FontFamily = font,
            FontSize = size,
            FontWeight = weight,
            Fill = fill,
            Stroke = StrokeDarkBrush,
            StrokeThickness = _settings.TextStroke * strokeFactor,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private UIElement BuildOptionalHeader(Brush textBrush, Brush labelBrush)
    {
        bool active = _liveCount != null;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(BuildSimpleStatusDot(6.5));
        var title = MakeText(_settings.Title, FontModern, 10.2, FontWeights.SemiBold, textBrush, 0.72);
        title.MaxWidth = 136;
        title.Margin = new Thickness(5, 0, 8, 0);
        left.Children.Add(title);
        grid.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_locked && !IsPreview)
        {
            var lockTxt = MakeText("FIXO", FontModern, 7.8, FontWeights.Bold, LiveDotBrush, 0.65);
            lockTxt.Margin = new Thickness(0, 0, 5, 0);
            right.Children.Add(lockTxt);
        }
        var status = MakeText(active ? "AO VIVO" : "ESPERA", FontModern, 8.2, FontWeights.Bold, active ? LiveDotBrush : labelBrush, 0.65);
        right.Children.Add(status);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    // ==================== 1. TACTICAL LAYOUT (FORTNITE NATIVE DEFAULT) ====================
    private UIElement BuildTacticalLayout(
        FontFamily numFont, Brush accentBrush, Brush textBrush, Brush labelBrush,
        List<(string ShortTag, string FullTag, string Value, Brush ValueBrush)> metrics)
    {
        var outer = new StackPanel { Margin = GetAdaptivePadding(11, 5.5) };
        if (_settings.ShowHeader)
            outer.Children.Add(BuildOptionalHeader(textBrush, labelBrush));

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        bool hasLeft = _settings.ShowIcon || _settings.ShowLive;
        if (hasLeft)
        {
            if (_settings.ShowIcon)
                row.Children.Add(BuildIconWithStatusDot(25));
            else if (!_settings.ShowHeader)
                row.Children.Add(BuildSimpleStatusDot(7.5));

            if (_settings.ShowLive)
            {
                var liveStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(_settings.ShowIcon || !_settings.ShowHeader ? 6.5 : 0, -1, 0, -1),
                };
                if (_settings.ShowLabels)
                {
                    var tag = MakeText("PARTIDA", FontModern, 7.6, FontWeights.Bold, labelBrush, 0.62);
                    tag.Margin = new Thickness(0, 0, 0, -3);
                    liveStack.Children.Add(tag);
                }
                var liveVal = MakeText(_liveCount?.ToString() ?? "—", numFont, 27, FontWeights.Bold, accentBrush, 1.0);
                liveStack.Children.Add(liveVal);
                row.Children.Add(liveStack);
            }
        }

        if (hasLeft && metrics.Count > 0)
        {
            var sep = new Border
            {
                Width = 1.2,
                Height = metrics.Count > 1 ? 26 : 18,
                Background = DividerBrush,
                Margin = new Thickness(9, 1, 9, 1),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _settings.Chrome == "angled" ? new SkewTransform(-8.5, 0) : Transform.Identity,
            };
            row.Children.Add(sep);
        }

        if (metrics.Count > 0)
        {
            int cols = metrics.Count <= 2 ? 1 : (metrics.Count + 1) / 2;
            int rows = metrics.Count == 1 ? 1 : 2;
            var mGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            for (int c = 0; c < cols; c++)
                mGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < rows; r++)
                mGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < metrics.Count; i++)
            {
                int r = i % rows;
                int c = i / rows;
                var m = metrics[i];

                var cell = new Grid
                {
                    Margin = new Thickness(c > 0 ? 10 : 0, r > 0 ? 1 : 0, 0, 0),
                };
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                if (_settings.ShowLabels)
                {
                    var k = MakeText(m.ShortTag, FontModern, 8.2, FontWeights.Bold, labelBrush, 0.65);
                    k.Margin = new Thickness(0, 0, 6, 0);
                    cell.Children.Add(k);
                }

                var v = MakeText(m.Value, numFont, metrics.Count == 1 ? 17 : 13.5, FontWeights.Bold, m.ValueBrush, 0.85);
                v.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(v, 1);
                cell.Children.Add(v);

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                mGrid.Children.Add(cell);
            }
            row.Children.Add(mGrid);
        }

        outer.Children.Add(row);
        return outer;
    }

    // ==================== 2. PILL LAYOUT (SINGLE-LINE MINIMALIST BAR) ====================
    private UIElement BuildPillLayout(
        FontFamily numFont, Brush accentBrush, Brush textBrush, Brush labelBrush,
        List<(string ShortTag, string FullTag, string Value, Brush ValueBrush)> metrics)
    {
        var outer = new StackPanel { Margin = GetAdaptivePadding(11, 4.5) };
        if (_settings.ShowHeader)
            outer.Children.Add(BuildOptionalHeader(textBrush, labelBrush));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (_settings.ShowIcon)
            bar.Children.Add(BuildIconWithStatusDot(20));
        else if (!_settings.ShowHeader)
            bar.Children.Add(BuildSimpleStatusDot(6.5));

        bool needSep = false;
        if (_settings.ShowLive)
        {
            var liveVal = MakeText(_liveCount?.ToString() ?? "—", numFont, 21.5, FontWeights.Bold, accentBrush, 1.0);
            liveVal.Margin = new Thickness(_settings.ShowIcon || !_settings.ShowHeader ? 5.5 : 0, -1, 0, -1);
            bar.Children.Add(liveVal);
            needSep = true;
        }
        else if (_settings.ShowIcon)
        {
            needSep = true;
        }

        foreach (var m in metrics)
        {
            if (needSep)
            {
                var dot = new Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Fill = DividerBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(7.5, 0, 7.5, 0),
                };
                bar.Children.Add(dot);
            }

            if (_settings.ShowLabels)
            {
                var k = MakeText(m.ShortTag, FontModern, 8.5, FontWeights.SemiBold, labelBrush, 0.65);
                k.Margin = new Thickness(0, 0, 3.5, 0);
                bar.Children.Add(k);
            }

            var v = MakeText(m.Value, numFont, 15.0, FontWeights.Bold, m.ValueBrush, 0.85);
            bar.Children.Add(v);
            needSep = true;
        }

        outer.Children.Add(bar);
        return outer;
    }

    // ==================== 3. MICRO LAYOUT (ESPORTS ULTRA-COMPACT) ====================
    private UIElement BuildMicroLayout(
        FontFamily numFont, Brush accentBrush, Brush textBrush, Brush labelBrush,
        List<(string ShortTag, string FullTag, string Value, Brush ValueBrush)> metrics)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = GetAdaptivePadding(8, 3),
        };

        if (_settings.ShowIcon)
            bar.Children.Add(BuildIconWithStatusDot(16.5));
        else
            bar.Children.Add(BuildSimpleStatusDot(6));

        if (_settings.ShowLive)
        {
            var liveVal = MakeText(_liveCount?.ToString() ?? "—", numFont, 19.5, FontWeights.Bold, accentBrush, 1.0);
            liveVal.Margin = new Thickness(5, -1, 0, -1);
            bar.Children.Add(liveVal);
        }

        int shown = 0;
        foreach (var m in metrics)
        {
            if (shown >= 2) break;
            var slash = MakeText("·", FontModern, 11, FontWeights.Bold, labelBrush, 0.6);
            slash.Margin = new Thickness(4.5, 0, 4.5, 0);
            bar.Children.Add(slash);

            if (_settings.ShowLabels)
            {
                var k = MakeText(m.ShortTag, FontModern, 7.8, FontWeights.SemiBold, labelBrush, 0.6);
                k.Margin = new Thickness(0, 0, 2.5, 0);
                bar.Children.Add(k);
            }

            var v = MakeText(m.Value, numFont, 13.0, FontWeights.SemiBold, m.ValueBrush, 0.8);
            bar.Children.Add(v);
            shown++;
        }

        return bar;
    }

    // ==================== 4. VERTICAL LAYOUT (SIDE EDGE STACK) ====================
    private UIElement BuildVerticalLayout(
        FontFamily numFont, Brush accentBrush, Brush textBrush, Brush labelBrush,
        List<(string ShortTag, string FullTag, string Value, Brush ValueBrush)> metrics)
    {
        var stack = new StackPanel
        {
            MinWidth = 98,
            Margin = GetAdaptivePadding(10, 7),
        };

        if (_settings.ShowHeader)
            stack.Children.Add(BuildOptionalHeader(textBrush, labelBrush));

        if (_settings.ShowIcon || _settings.ShowLive)
        {
            var top = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (_settings.ShowIcon)
                top.Children.Add(BuildIconWithStatusDot(22));
            else if (!_settings.ShowHeader)
                top.Children.Add(BuildSimpleStatusDot(6.5));

            if (_settings.ShowLive)
            {
                var liveCol = new StackPanel { Margin = new Thickness(_settings.ShowIcon ? 6 : 0, -1, 0, -1) };
                if (_settings.ShowLabels)
                {
                    var lbl = MakeText("PARTIDA", FontModern, 7.5, FontWeights.Bold, labelBrush, 0.6);
                    lbl.Margin = new Thickness(0, 0, 0, -3);
                    liveCol.Children.Add(lbl);
                }
                liveCol.Children.Add(MakeText(_liveCount?.ToString() ?? "—", numFont, 26, FontWeights.Bold, accentBrush, 1.0));
                top.Children.Add(liveCol);
            }
            stack.Children.Add(top);
        }

        if (metrics.Count > 0 && (_settings.ShowIcon || _settings.ShowLive))
        {
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = DividerBrush,
                Margin = new Thickness(0, 4, 0, 3),
            });
        }

        foreach (var m in metrics)
        {
            var r = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (_settings.ShowLabels)
            {
                var k = MakeText(m.ShortTag, FontModern, 8.2, FontWeights.SemiBold, labelBrush, 0.65);
                k.Margin = new Thickness(0, 0, 8, 0);
                r.Children.Add(k);
            }
            var v = MakeText(m.Value, numFont, 13.5, FontWeights.Bold, m.ValueBrush, 0.82);
            Grid.SetColumn(v, 1);
            r.Children.Add(v);
            stack.Children.Add(r);
        }

        return stack;
    }

    // ==================== 5. CARD LAYOUT (STREAMER / COMPLETE GLASS PANEL) ====================
    private UIElement BuildCardLayout(
        FontFamily numFont, Brush accentBrush, Brush secondaryBrush, Brush textBrush, Brush labelBrush,
        List<(string ShortTag, string FullTag, string Value, Brush ValueBrush)> metrics)
    {
        var card = new StackPanel
        {
            MinWidth = 228,
            Margin = GetAdaptivePadding(12, 8.5),
        };

        if (_settings.ShowHeader)
            card.Children.Add(BuildOptionalHeader(textBrush, labelBrush));

        var hero = new Grid { Margin = new Thickness(0, _settings.ShowHeader ? 1 : 0, 0, 0) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftHero = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_settings.ShowIcon)
        {
            var icon = BuildIconWithStatusDot(28);
            if (icon is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 7, 0);
            leftHero.Children.Add(icon);
        }

        if (_settings.ShowLive)
        {
            var ls = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (_settings.ShowLabels)
            {
                var lLbl = MakeText("NESTA PARTIDA", FontModern, 7.8, FontWeights.Bold, labelBrush, 0.65);
                lLbl.Margin = new Thickness(0, 0, 0, -3);
                ls.Children.Add(lLbl);
            }
            ls.Children.Add(MakeText(_liveCount?.ToString() ?? "—", numFont, 33, FontWeights.Bold, accentBrush, 1.0));
            leftHero.Children.Add(ls);
        }
        hero.Children.Add(leftHero);

        var remaining = new List<(string ShortTag, string FullTag, string Value, Brush ValueBrush)>(metrics);
        if (_settings.ShowSeason)
        {
            remaining.RemoveAll(x => x.ShortTag == "TEMP");
            var rs = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            if (_settings.ShowLabels)
            {
                var sLbl = MakeText("TEMPORADA", FontModern, 7.8, FontWeights.Bold, labelBrush, 0.65);
                sLbl.HorizontalAlignment = HorizontalAlignment.Right;
                sLbl.Margin = new Thickness(0, 0, 0, -2);
                rs.Children.Add(sLbl);
            }
            var sVal = MakeText(_stats.SeasonTotal.ToString("N0"), numFont, 19.5, FontWeights.Bold, textBrush, 0.85);
            sVal.HorizontalAlignment = HorizontalAlignment.Right;
            rs.Children.Add(sVal);
            Grid.SetColumn(rs, 1);
            hero.Children.Add(rs);
        }

        card.Children.Add(hero);

        if (_settings.ShowTreasureHour && !string.IsNullOrWhiteSpace(_stats.TreasureHourText))
        {
            remaining.RemoveAll(x => x.ShortTag == "HORA");
            var thBorder = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2.5, 7, 2.5),
                Margin = new Thickness(0, 5, 0, 0),
                Background = Freeze(new(Color.FromArgb(0x38, 0xFF, 0x3D, 0x6B))),
                BorderBrush = Freeze(new(Color.FromArgb(0x55, 0xFF, 0x3D, 0x6B))),
                BorderThickness = new Thickness(1),
            };
            var thRow = new DockPanel();
            var thVal = MakeText(_stats.TreasureHourText, numFont, 12.0, FontWeights.Bold,
                _stats.TreasureHourLive ? GemHotBrush : secondaryBrush, 0.75);
            DockPanel.SetDock(thVal, Dock.Right);
            thRow.Children.Add(thVal);
            thRow.Children.Add(MakeText(_stats.TreasureHourLive ? "Hora do Tesouro AO VIVO" : "Próxima Hora do Tesouro",
                FontModern, 9.0, FontWeights.SemiBold, textBrush, 0.65));
            thBorder.Child = thRow;
            card.Children.Add(thBorder);
        }

        if (remaining.Count > 0)
        {
            card.Children.Add(new Border
            {
                Height = 1,
                Background = DividerBrush,
                Margin = new Thickness(0, 5, 0, 4),
            });

            var strip = new Grid();
            for (int i = 0; i < remaining.Count; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = i < remaining.Count - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
                });
                var m = remaining[i];
                var item = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(i > 0 ? 7 : 0, 0, 0, 0),
                    HorizontalAlignment = i == 0 ? HorizontalAlignment.Left
                        : i == remaining.Count - 1 ? HorizontalAlignment.Right
                        : HorizontalAlignment.Center,
                };
                if (_settings.ShowLabels)
                {
                    var k = MakeText(m.ShortTag, FontModern, 8.0, FontWeights.SemiBold, labelBrush, 0.65);
                    k.Margin = new Thickness(0, 0, 3, 0);
                    item.Children.Add(k);
                }
                item.Children.Add(MakeText(m.Value, numFont, 13.0, FontWeights.Bold, m.ValueBrush, 0.8));
                Grid.SetColumn(item, i);
                strip.Children.Add(item);
            }
            card.Children.Add(strip);
        }

        return card;
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            if (ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                return c;
        }
        catch { }
        return fallback;
    }

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
