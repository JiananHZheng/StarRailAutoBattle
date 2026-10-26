namespace GameLauncher.Core;

public enum UrlCheckState
{
    Empty,
    Valid,
    Invalid,
}

public sealed record UrlCheck(UrlCheckState State, Uri? Url, string? Message);

public static class UrlValidator
{
    public const int MaxLength = 2048;

    public static UrlCheck Validate(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new UrlCheck(UrlCheckState.Empty, null, null);
        }

        if (text.Length > MaxLength || text.Any(char.IsControl) || text.Any(char.IsWhiteSpace))
        {
            return Invalid("网址包含空格、换行或过长，请检查后重新粘贴。");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            return Invalid("请填写完整网址，以 http:// 或 https:// 开头，例如 https://www.bilibili.com/video/…");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return Invalid("网址格式无效，请检查后重试。");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return Invalid("只支持 http:// 或 https:// 开头的网页地址。");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return Invalid("网址缺少域名，请检查后重试。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid("网址中不能包含用户名或密码。");
        }

        return new UrlCheck(UrlCheckState.Valid, uri, null);
    }

    private static UrlCheck Invalid(string message) => new(UrlCheckState.Invalid, null, message);
}
