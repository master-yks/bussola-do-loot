using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ChestCounter;

namespace ChestCounter.Gui;

// Minimalist, transparent, customizable always-on-top HUD over Fortnite (borderless windowed).
// Supports full OS-level click-through (WS_EX_TRANSPARENT + HTTRANSPARENT) when locked.
public partial class InGameOverlay : Window
{
    private static readonly SolidColorBrush HoverGuideBg = Freeze(new(Color.FromArgb(0x18, 0xF5, 0xB9, 0x42)));
    private static readonly SolidColorBrush HoverGuideBorder = Freeze(new(Color.FromArgb(0x66, 0xF5, 0xB9, 0x42)));
    private static readonly SolidColorBrush HitCatchBg = Freeze(new(Color.FromArgb(0x01, 0x00, 0x00, 0x00)));

    public bool Locked { get; private set; }
    public event Action? Moved;

    public InGameOverlay()
    {
        InitializeComponent();
        SnapToAnchor("top_left");
    }

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public void SnapTopLeft() => SnapToAnchor("top_left");

    public void SnapToAnchor(string anchor)
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        double w = ActualWidth > 24 ? ActualWidth : 200;
        double h = ActualHeight > 20 ? ActualHeight : 54;

        switch (anchor)
        {
            case "top_center":
                Left = Math.Round(area.Left + (area.Width - w) * 0.5);
                Top = Math.Round(area.Top + 6);
                break;
            case "beside_minimap":
                // Sits neatly to the left of Fortnite's top-right minimap
                double minimapReserve = Math.Round(area.Width * 0.182);
                Left = Math.Round(area.Right - minimapReserve - w - 10);
                Top = Math.Round(area.Top + 10);
                break;
            case "below_minimap":
                // Sits right beneath Fortnite's minimap & player count strip
                Left = Math.Round(area.Right - w - 10);
                Top = Math.Round(area.Top + area.Height * 0.295);
                break;
            case "bottom_left":
                Left = Math.Round(area.Left + 10);
                Top = Math.Round(area.Bottom - h - 10);
                break;
            case "bottom_right":
                Left = Math.Round(area.Right - w - 10);
                Top = Math.Round(area.Bottom - h - 10);
                break;
            default:
                Left = Math.Round(area.Left + 8);
                Top = Math.Round(area.Top + 8);
                break;
        }

        EnsureOnScreen();
    }

    public void EnsureOnScreen()
    {
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;

        double w = ActualWidth > 24 ? ActualWidth : 190;
        double h = ActualHeight > 20 ? ActualHeight : 52;

        if (double.IsNaN(Left) || double.IsNaN(Top)
            || Left + w < vLeft + 24 || Left > vRight - 24
            || Top + h < vTop + 24 || Top > vBottom - 24)
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Round(area.Left + 8);
            Top = Math.Round(area.Top + 8);
            return;
        }

        // Keep fully visible inside virtual desktop if resizing pushed an edge slightly past screen bounds
        if (Left + w > vRight - 2) Left = Math.Round(vRight - w - 4);
        if (Top + h > vBottom - 2) Top = Math.Round(vBottom - h - 4);
        if (Left < vLeft - 6) Left = Math.Round(vLeft + 2);
        if (Top < vTop - 6) Top = Math.Round(vTop + 2);
    }

    public void ApplySettings(OverlaySettings settings, string? activeAnchor = null)
    {
        HudView.ApplySettings(settings);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!string.IsNullOrEmpty(activeAnchor) && activeAnchor != "free")
                SnapToAnchor(activeAnchor);
            else
                EnsureOnScreen();
        });
    }

    public void SetLive(int? n, bool allowPulse = true) =>
        HudView.SetLive(n, allowPulse);

    public void SetStats(OverlayStatsSnapshot snapshot) =>
        HudView.SetStats(snapshot);

    private void DragSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Locked) return;
        DragSurface.Background = HoverGuideBg;
        DragSurface.BorderBrush = HoverGuideBorder;
        DragSurface.BorderThickness = new Thickness(1);
    }

    private void DragSurface_MouseLeave(object sender, MouseEventArgs e) =>
        ClearHoverGuide();

    private void ClearHoverGuide()
    {
        DragSurface.Background = Locked ? Brushes.Transparent : HitCatchBg;
        DragSurface.BorderBrush = null;
        DragSurface.BorderThickness = new Thickness(0);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (Locked || e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
            EnsureOnScreen();
            Moved?.Invoke();
        }
        catch { }
    }

    public void SetLocked(bool locked, bool showToast = false)
    {
        Locked = locked;
        HudView.SetLockedState(locked, showTransientToast: showToast);
        DragSurface.IsHitTestVisible = !locked;
        DragSurface.Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
        ClearHoverGuide();
        ApplyWindowStyles();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        ApplyWindowStyles();
        EnsureOnScreen();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && Locked)
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex = Locked ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_NCHITTEST = 0x0084;
    private const int MA_NOACTIVATE = 3;
    private const int HTTRANSPARENT = -1;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
