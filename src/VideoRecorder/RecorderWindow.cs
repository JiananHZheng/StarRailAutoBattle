using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace VideoRecorder;

/// <summary>
/// Borderless window that hosts the recorder's own browser view. It is placed just left of all screens, so it never
/// covers anything or takes the focus, and has no taskbar button; it still counts as shown, so the video keeps
/// being drawn (minimizing or hiding it would stop that).
/// </summary>
internal sealed class RecorderWindow : Window
{
    private bool _closingByCode;

    public RecorderWindow()
    {
        Title = "StarRailAutoBattle 录制中";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Top = SystemParameters.VirtualScreenTop;
        Content = View;
        Closed += (_, _) => UserClosed = !_closingByCode;
        PlaceOffScreen();
    }

    public WebView2 View { get; } = new();

    public bool UserClosed { get; private set; }

    public DpiScale Dpi => VisualTreeHelper.GetDpi(this);

    /// <summary>Client size in physical pixels.</summary>
    public (int Width, int Height) ClientPixels =>
        ((int)Math.Round(ActualWidth * Dpi.DpiScaleX), (int)Math.Round(ActualHeight * Dpi.DpiScaleY));

    /// <summary>Largest client size in physical pixels: the size of the primary screen.</summary>
    public (int Width, int Height) MaxClientPixels =>
        ((int)Math.Round(SystemParameters.PrimaryScreenWidth * Dpi.DpiScaleX), (int)Math.Round(SystemParameters.PrimaryScreenHeight * Dpi.DpiScaleY));

    public void SetClientPixels(int width, int height)
    {
        var (maxW, maxH) = MaxClientPixels;
        Width = Math.Min(width, maxW) / Dpi.DpiScaleX;
        Height = Math.Min(height, maxH) / Dpi.DpiScaleY;
        PlaceOffScreen();
    }

    public void CloseByCode()
    {
        _closingByCode = true;
        Close();
    }

    private void PlaceOffScreen() =>
        Left = SystemParameters.VirtualScreenLeft - (double.IsNaN(Width) ? 2000 : Width) - 100;
}
