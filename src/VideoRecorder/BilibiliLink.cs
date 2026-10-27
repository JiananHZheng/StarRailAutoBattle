using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace VideoRecorder;

/// <summary>A Bilibili video page. <see cref="Url"/> is normalized and carries no share/tracking parameters.</summary>
public sealed record BilibiliLink(Uri Url, string VideoId, int? Part)
{
    public bool IsShortLink => Url.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
}

public static partial class BilibiliLinks
{
    private static readonly string[] VideoHosts = ["bilibili.com", "www.bilibili.com", "m.bilibili.com"];

    [GeneratedRegex(@"https?://[^\s<>""'，。、；！？【】（）《》]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInText();

    [GeneratedRegex(@"^/video/(BV[0-9A-Za-z]{10}|av\d+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoPath();

    [GeneratedRegex(@"^(BV[0-9A-Za-z]{10})$")]
    private static partial Regex BareBv();

    [GeneratedRegex(@"^/[0-9A-Za-z]{4,16}/?$")]
    private static partial Regex ShortPath();

    /// <summary>Accepts a link, the share text the Bilibili app copies ("【标题】 https://…"), or a bare BV id.</summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out BilibiliLink? link, [NotNullWhen(false)] out string? error)
    {
        link = null;
        error = null;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "请填写 B 站视频链接。";
            return false;
        }

        if (text.Length > 4096)
        {
            error = "链接过长，请检查后重新粘贴。";
            return false;
        }

        if (BareBv().Match(text) is { Success: true } bare)
        {
            link = new BilibiliLink(new Uri($"https://www.bilibili.com/video/{bare.Groups[1].Value}/"), bare.Groups[1].Value, null);
            return true;
        }

        var found = UrlInText().Match(text);
        if (!found.Success || !Uri.TryCreate(found.Value, UriKind.Absolute, out var uri))
        {
            error = "没有找到网址。请粘贴 B 站视频链接，例如 https://www.bilibili.com/video/BV…";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "网址中不能包含用户名或密码。";
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "b23.tv")
        {
            if (!ShortPath().IsMatch(uri.AbsolutePath))
            {
                error = "这个 b23.tv 短链接格式不对，请重新复制分享链接。";
                return false;
            }

            var code = uri.AbsolutePath.Trim('/');
            link = new BilibiliLink(new Uri($"https://b23.tv/{code}"), "b23_" + code, null);
            return true;
        }

        if (!VideoHosts.Contains(host))
        {
            error = "目前只支持 B 站视频链接（bilibili.com 或 b23.tv）。其他网站的视频请录好后用“自录视频文件”导入。";
            return false;
        }

        var match = VideoPath().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            error = "这不是 B 站视频播放页链接。请打开具体视频后复制链接（形如 https://www.bilibili.com/video/BV…）。";
            return false;
        }

        var id = match.Groups[1].Value;
        id = id.StartsWith("av", StringComparison.OrdinalIgnoreCase) ? "av" + id[2..] : id;
        var part = PartOf(uri.Query);
        var normalized = $"https://www.bilibili.com/video/{id}/" + (part is > 1 ? $"?p={part}" : string.Empty);
        link = new BilibiliLink(new Uri(normalized), id, part is > 1 ? part : null);
        return true;
    }

    /// <summary>True for pages the recorder window may navigate to (the video page itself, after short-link redirects).</summary>
    public static bool IsAllowedNavigation(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && (uri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>The BV/av id of a resolved video page URL, or null.</summary>
    public static string? VideoIdOf(Uri uri)
    {
        if (!VideoHosts.Contains(uri.Host.ToLowerInvariant()))
        {
            return null;
        }

        var match = VideoPath().Match(uri.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int? PartOf(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "p" && int.TryParse(kv[1], out var p) && p is > 0 and < 10000)
            {
                return p;
            }
        }

        return null;
    }
}
