using System.IO;
using Microsoft.Web.WebView2.Core;

namespace VideoRecorder;

/// <summary>
/// The recorder's own browser (WebView2, separate from the user's Edge). The recording window, the login window and
/// the sign-in check all use the same profile folder and the same options, so they can run one after another.
/// </summary>
internal static class RecorderBrowser
{
    public static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        // The recording window sits off-screen; without these flags the browser would treat it as hidden and stop
        // drawing the video.
        var options = new CoreWebView2EnvironmentOptions(
            "--autoplay-policy=no-user-gesture-required --mute-audio --disable-features=CalculateNativeWinOcclusion " +
            "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling");
        return await CoreWebView2Environment.CreateAsync(null, dataFolder, options);
    }

    /// <summary>
    /// No dev tools, downloads, pop-ups, permission prompts or saved passwords; top-level navigation stays on Bilibili.
    /// <paramref name="onBlocked"/> receives the host of a page that was not allowed.
    /// </summary>
    public static void Harden(CoreWebView2 core, Action<string>? onBlocked = null)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        core.IsMuted = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !BilibiliLinks.IsAllowedNavigation(uri))
            {
                e.Cancel = true;
                onBlocked?.Invoke(uri?.Host ?? "?");
            }
        };
    }
}
