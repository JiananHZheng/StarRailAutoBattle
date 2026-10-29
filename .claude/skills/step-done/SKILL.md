---
name: step-done
description: Finish a build step of the sequence check system
---
1. Run all tests and report the results.
2. Run `pwsh scripts/check-samples.ps1` from the main checkout. It runs the analyzer on each sample in samples/ (recordings in axis-data/recordings) and prints per-action mismatches against the user-confirmed expected.txt. Show those mismatches and the recordings' resolution.
   - axis-data/ is gitignored, so it exists only in the main checkout (D:\下载\StarRailAutoBattle\axis-data), not in worktrees.
   - Never use any recording of BV18nVd6VEiB (folder name contains it): it is blind-test only and must never be viewed.
3. List temp/test files that are not part of the repo (`git status`), with sizes. Delete them once the user says yes.
4. Append any newly confirmed rules to docs/sequence-rules.md as numbered rules with a one-line example.
5. Summarize what changed and what is still open. Ask the user to confirm before committing.
