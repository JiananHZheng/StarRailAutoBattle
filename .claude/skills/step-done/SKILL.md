---
name: step-done
description: Finish a build step of the sequence check system
---
1. Run all tests and report the results.
2. Run the analyzer on the reference recording(s) in axis-data/recordings, and compare against the expected sequences in axis-data/sequences. Show detected vs expected sequence and the output resolution.
   - axis-data/ is gitignored, so it exists only in the main checkout (D:\下载\StarRailAutoBattle\axis-data), not in worktrees.
   - Never use any recording of BV18nVd6VEiB (folder name contains it): it is blind-test only and must never be viewed.
3. List temp/test files that are not part of the repo (`git status`), with sizes. Delete them once the user says yes.
4. Append any newly confirmed rules to docs/sequence-rules.md.
5. Summarize what changed and what is still open. Ask the user to confirm before committing.
