# CLAUDE.md

## Environment facts (do not re-assume)
- The game (Star Rail) runs as a native Windows client. It does NOT run in Edge or any browser, so never route game capture through the browser/WebView capture path.
- Only Bilibili *video* recording uses the launcher's built-in browser. It must be logged in to get 1080P (not logged in = 360P/480P).
- Bilibili direct API/CLI downloads get blocked (HTTP 412 risk control, truncated downloads). Capture the stream through the page instead.
- Shell is PowerShell on Windows. Many files use CRLF line endings, so use the Edit tool rather than sed/regex shell edits.
- The .NET 10 SDK is required. If `dotnet` is missing, stop and tell the user. Do not work around it.
