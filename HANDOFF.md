# Handoff — 2026-10-29

**Current step:** sample regression set for the 凹分 sequence analyzer (step 5 support). Worktree branch `claude/insights-072e67`; step-5 code itself is uncommitted in the main checkout.

**What works**
- CLAUDE.md (environment, 凹分 rules, testing), Stop hook, `/step-done` skill: committed and on main (e0925e5).
- `scripts/check-samples.ps1`: runs `axis sequence` per sample, prints missing/extra/different/skill-different rows and precision/recall per type (主/插入/分段/普攻/战技/终结技); tested on made-up sequences only.
- Drafts generated: `samples/*/draft.txt` (铁墓 49, 亚婆离 56, 绘世 60 actions; each has 2 `未知` lines); gitignored.
- `samples/` has 3 samples (铁墓, 亚婆离, 绘世, each 0:00–3:00) with sample.json; teams from video descriptions, 亚婆离 from the team bar at 0:25.

**Known bugs / open issues**
- 铁墓 draft (`axis-data/cue-study/铁墓_0-3分钟_生成序列.txt`): av-bar reading stuck at `轮次=2 行动值=282` for most rows. Repro (main checkout): `axis sequence axis-data/recordings/20261027-181522_BV1EM8Q69EqZ_0-3min/recording.avi --team 昔涟,长夜月,风堇,记忆主`.
- Insert-card units often unrecognized → `未知 终结技` (same repro, e.g. 13-9 at 1:38.4).
- Target reticle unreadable; targets omitted (= boss).
- 亚婆离 team `风堇,长夜月,记忆主,昔涟` is read from portraits, not confirmed.
- No `expected.txt` yet, so the check script has never run on a video.

**Next 3 actions**
1. Confirm the 亚婆离 team; check each `samples/*/draft.txt` and save the corrected copy as `expected.txt`.
2. Commit samples/ + script + CLAUDE.md/skill/.gitignore edits here, fast-forward main, run `pwsh scripts/check-samples.ps1` from the main checkout.
3. Fix the largest mismatch class (start with the stuck av reading), re-run the check after each detector change.

**Uncommitted / temp files**
- Worktree: `CLAUDE.md`, `.gitignore`, `.claude/skills/step-done/SKILL.md` (modified); `samples/`, `scripts/check-samples.ps1`, `docs/sequence-rules.md`, `docs/tasks/`, `HANDOFF.md` (new).
- Main checkout: 16 uncommitted step-5 files (yours). Build output `src/AxisTool/bin/Release` (gitignored). Scratch frames already deleted.
