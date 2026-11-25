using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ChestCounter.Gui;

/// <summary>
/// Renders game-HUD text with a crisp outer contour (paint-order: stroke behind fill)
/// so numbers remain 100% legible even over bright Fortnite snow or sky on a 0% transparent backdrop.
/// </summary>
public sealed class OutlinedTextBlock : FrameworkElement
{
    private static readonly Brush DefaultFillBrush = CreateFrozenBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly Brush DefaultStrokeBrush = CreateFrozenBrush(Color.FromArgb(0xEC, 0x05, 0x07, 0x0D));
    private static readonly FontFamily DefaultHudFont = new("Bahnschrift Condensed, Bahnschrift, Segoe UI");

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(DefaultFillBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(DefaultStrokeBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(DefaultHudFont, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontStyleProperty = DependencyProperty.Register(
        nameof(FontStyle), typeof(FontStyle), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontStyles.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => (FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public OutlinedTextBlock()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    private static SolidColorBrush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private FormattedText BuildFormattedText(double maxWidth)
    {
        string text = Text ?? string.Empty;
        double ppd = 1.25;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.PixelsPerDip > 0) ppd = dpi.PixelsPerDip;
        }
        catch { }

        bool hasExplicitCap = double.IsFinite(maxWidth) && maxWidth > 4;
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily ?? DefaultHudFont, FontStyle, FontWeight, FontStretches.Normal),
            Math.Max(1.0, FontSize),
            Fill ?? DefaultFillBrush,
            ppd)
        {
            Trimming = hasExplicitCap ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            MaxLineCount = 1,
        };

        if (hasExplicitCap)
            ft.MaxTextWidth = maxWidth;

        return ft;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text))
            return new Size(0, 0);

        double st = Math.Max(0, StrokeThickness);
        double padX = st > 0.05 ? Math.Ceiling(st + 1.0) : 0;
        double padY = st > 0.05 ? Math.Ceiling(st * 0.6) : 0;

        double capW = double.IsFinite(MaxWidth)
            ? Math.Max(4, MaxWidth - padX * 2)
            : double.PositiveInfinity;

        var ft = BuildFormattedText(capW);
        return new Size(
            Math.Ceiling(ft.WidthIncludingTrailingWhitespace + padX * 2 + 1.0),
            Math.Ceiling(ft.Height + padY));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text))
            return;

        double st = Math.Max(0, StrokeThickness);
        double padX = st > 0.05 ? Math.Ceiling(st + 1.0) : 0;
        double padY = st > 0.05 ? Math.Ceiling(st * 0.6) : 0;

        double capW = double.IsFinite(MaxWidth)
            ? Math.Max(4, MaxWidth - padX * 2)
            : double.PositiveInfinity;
        var ft = BuildFormattedText(capW);
        var origin = new Point(padX, padY * 0.5);

        if (st > 0.05 && Stroke != null)
        {
            var geo = ft.BuildGeometry(origin);
            var pen = new Pen(Stroke, st * 2.0)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        dc.DrawText(ft, origin);
    }
}
