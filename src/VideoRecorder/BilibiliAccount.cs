using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VideoRecorder;

/// <summary>
/// Login state of the recorder's own browser profile. The user logs in on Bilibili's page in <see cref="ShowLoginWindow"/>;
/// the account and password are handled by that page alone. This class only checks whether Bilibili's session cookies
/// exist (names and expiry, never their values) and can delete them again. It never touches the user's Edge profile.
/// </summary>
public static class BilibiliAccount
{
    private const string SiteUrl = "https://www.bilibili.com";
    private const string LoginUrl = "https://passport.bilibili.com/login";

    /// <summary>Signed in when Bilibili's session cookie (SESSDATA) and user cookie (DedeUserID) are present and unexpired.</summary>
    public static bool IsSignedIn(IEnumerable<(string Name, DateTime Expires, bool IsSession)> cookies, DateTime nowUtc)
    {
        var live = cookies.Where(c => c.IsSession || c.Expires.ToUniversalTime() > nowUtc).Select(c => c.Name).ToHashSet();
        return live.Contains("SESSDATA") && live.Contains("DedeUserID");
    }

    /// <summary>Checks the profile without showing anything (a hidden browser view under <paramref name="parentWindow"/>).</summary>
    public static async Task<bool> IsSignedInAsync(string dataFolder, IntPtr parentWindow)
    {
        var environment = await RecorderBrowser.CreateEnvironmentAsync(dataFolder);
        var controller = await environment.CreateCoreWebView2ControllerAsync(parentWindow);
        try
        {
            controller.IsVisible = false;
            return await IsSignedInAsync(controller.CoreWebView2);
        }
        finally
        {
            controller.Close();
        }
    }

    /// <summary>Deletes every cookie of the recorder's own profile, which logs it out of Bilibili.</summary>
    public static async Task SignOutAsync(string dataFolder, IntPtr parentWindow)
    {
        var environment = await RecorderBrowser.CreateEnvironmentAsync(dataFolder);
        var controller = await environment.CreateCoreWebView2ControllerAsync(parentWindow);
        try
        {
            controller.IsVisible = false;
            controller.CoreWebView2.CookieManager.DeleteAllCookies();
        }
        finally
        {
            controller.Close();
        }
    }

    /// <summary>Shows Bilibili's login page in the recorder's browser. Returns true when the profile is logged in afterwards.</summary>
    public static bool ShowLoginWindow(string dataFolder, Window owner)
    {
        var window = new LoginWindow(dataFolder) { Owner = owner };
        window.ShowDialog();
        return window.SignedIn;
    }

    internal static async Task<bool> IsSignedInAsync(CoreWebView2 core)
    {
        var cookies = await core.CookieManager.GetCookiesAsync(SiteUrl);
        return IsSignedIn(cookies.Select(c => (c.Name, c.Expires, c.IsSession)), DateTime.UtcNow);
    }

    private sealed class LoginWindow : Window
    {
        private readonly string _dataFolder;
        private readonly WebView2 _view = new();
        private readonly TextBlock _banner = new()
        {
            Text = "请在下方 B 站页面登录（推荐用 B 站 App 扫码）。账号和密码只由 B 站页面处理，本程序不读取、不保存。登录成功后此窗口会自动关闭。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(16, 10, 16, 10),
        };

        private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };

        public LoginWindow(string dataFolder)
        {
            _dataFolder = dataFolder;
            Title = "登录 B 站（仅用于录制视频）";
            Width = 1000;
            Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var layout = new DockPanel();
            var bar = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA)), Child = _banner };
            DockPanel.SetDock(bar, Dock.Top);
            layout.Children.Add(bar);
            layout.Children.Add(_view);
            Content = layout;
            Loaded += async (_, _) => await StartAsync();
            Closed += (_, _) => _poll.Stop();
            _poll.Tick += async (_, _) => await CheckAsync();
        }

        public bool SignedIn { get; private set; }

        private async Task StartAsync()
        {
            try
            {
                var environment = await RecorderBrowser.CreateEnvironmentAsync(_dataFolder);
                await _view.EnsureCoreWebView2Async(environment);
                RecorderBrowser.Harden(_view.CoreWebView2, host =>
                    _banner.Text = $"已阻止跳转到 {host}。这里只支持在 B 站页面上登录（扫码、短信或密码），不支持第三方账号登录。");
                if (await IsSignedInAsync(_view.CoreWebView2))
                {
                    Finish("已经登录 B 站，可以关闭此窗口。");
                    return;
                }

                _view.CoreWebView2.Navigate(LoginUrl);
                _poll.Start();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                _banner.Text = RecordingMessages.For(RecordingFailure.RuntimeMissing);
            }
        }

        private async Task CheckAsync()
        {
            if (_view.CoreWebView2 is null || SignedIn)
            {
                return;
            }

            if (await IsSignedInAsync(_view.CoreWebView2))
            {
                Finish("登录成功，正在关闭…");
            }
        }

        private async void Finish(string message)
        {
            SignedIn = true;
            _poll.Stop();
            _banner.Text = message;
            await Task.Delay(1200);
            if (IsLoaded)
            {
                Close();
            }
        }
    }
}
