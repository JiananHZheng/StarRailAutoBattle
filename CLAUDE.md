# CLAUDE.md

## Environment facts (do not re-assume)
- The game (Star Rail) runs as a native Windows client. It does NOT run in Edge or any browser, so never route game capture through the browser/WebView capture path.
- Only Bilibili *video* recording uses the launcher's built-in browser. It must be logged in to get 1080P (not logged in = 360P/480P).
- Bilibili direct API/CLI downloads get blocked (HTTP 412 risk control, truncated downloads). Capture the stream through the page instead.
- Shell is PowerShell on Windows. Many files use CRLF line endings, so use the Edit tool rather than sed/regex shell edits.
- The .NET 10 SDK is required. If `dotnet` is missing, stop and tell the user. Do not work around it.

## Domain Rules
Append new rules here whenever the user confirms one.

### 凹分 sequence recognition rules (confirmed by user)
- Battle start = black screen followed by a white line. It is NOT a UI element appearing.
- Ultimate start signal = the team-bar avatar lighting up. It is NOT the insert card.
- Extra actions (追加/额外行动) are NOT ultimates. Never label them as ultimates.
- Skill detection must not rely on skill-point changes, because some skills don't change skill points.
- Target defaults to the boss when the target ring cannot be detected.
- Do not invent new syntax (e.g. arrow target syntax). Use only the sequence syntax the user defined.
- When a rule is ambiguous, ask before implementing. Then record the answer in docs/sequence-rules.md.
