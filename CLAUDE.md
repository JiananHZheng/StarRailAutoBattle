# CLAUDE.md

## Environment facts (do not re-assume)
- The game (Star Rail) runs as a native Windows client. It does NOT run in Edge or any browser, so never route game capture through the browser/WebView capture path.
- Only Bilibili *video* recording uses the launcher's built-in browser. It must be logged in to get 1080P (not logged in = 360P/480P).
- Bilibili direct API/CLI downloads get blocked (HTTP 412 risk control, truncated downloads). Capture the stream through the page instead.
- Shell is PowerShell on Windows. Many files use CRLF line endings, so use the Edit tool rather than sed/regex shell edits.
- The .NET 10 SDK is required. If `dotnet` is missing, stop and tell the user. Do not work around it.

## Domain Rules
docs/sequence-rules.md is the single numbered spec (R1…) for recording, recognition and the 凹分 check. Read it before any work on those. Code and tests cite rule numbers (e.g. `// R23`). Its "待确认" items (Q1…) are NOT rules; never implement them as if they were.

### Before any implementation step
1. List every assumption you are making. Mark each one with the rule that supports it (R…) or `UNCONFIRMED`.
2. Stop and ask the user about every `UNCONFIRMED` assumption. Do not write code until they answer.
3. After the user answers, update docs/sequence-rules.md first (new numbered rule + one-line example). Then write the code and the tests, citing the rule numbers.

## Known pitfalls
These are wrong assumptions that were already made and corrected. Don't make them again.
- The game is not in Edge or any browser. Game capture never goes through WebView (R1). Only Bilibili recording uses the launcher's own WebView2 (R2).
- Not logged in = 360P. Bilibili recording must be logged in, and the output checked at 1920×1080 (R3, R5). A crop bug once produced 676×61 recordings that were reported as success.
- Never call Bilibili's API or a CLI downloader from outside the page. They return 412 or get cut off at ~515 KB (R4).
- Battle start is the black screen then a white line, not a UI element appearing (R23).
- An ultimate starts when the team-bar avatar lights up, not on the insert card (R29). The insert card is unreliable. Never use it to detect or count ultimates (R45).
- Not every extra action is an ultimate. Only an extra with a team-bar start is 终结技. A star-only extra gets no skill (R14).
- Skill points don't identify the skill. Read the enlarged selection button (R31).
- No arrow target syntax and no invented syntax (R17). An omitted target means the boss (R19). Don't add a silent fallback anywhere else.
- Team slot → unit comes only from the user's team. Never learn it or guess it (R30).
- Status variants are one unit, and '这里' and the action-value bar are not units (R38, R40). 187 green tests once hid both bugs.
- Never view BV18nVd6VEiB (R6).
- Edits to the main checkout are blocked from worktree sessions. Step work that is uncommitted on main is invisible to new worktrees.

## Testing

### Recognition & testing hygiene
- Units use global unit IDs. Status variants of the same avatar must merge into ONE unit entry.
- On-screen text (e.g. '这里') and the remaining action bar are never units.
- After any recording/crop change, print and check the output resolution (expect 1920x1080 for 1080P) before reporting success.
- 'Tests pass' is not enough. Also run on at least one real recording and report detected vs expected results.
- After every detector change, run `pwsh scripts/check-samples.ps1` from the main checkout (it needs axis-data/) and report its per-action mismatches. Samples live in samples/<name>/ (sample.json + user-confirmed expected.txt); never edit expected.txt to make a check pass.
- Delete temporary test scripts, scratch outputs and old test files before committing. Run `git status` and list anything untracked.
