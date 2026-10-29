# 任务线 1：头像识别（全局单位编号）

先读 CLAUDE.md、docs/sequence-rules.md（规则编号 R…）、docs/score-retry-check.md、docs/action-axis-knowledge-base.md。用中文和用户交流。

## 目标
- 同一单位的状态变体（加 buff、被控、半透明、带角标等）合并成**一个**全局单位编号（U0001…），不再拆成多个条目。
- 屏幕文字（如“这里”）和轴底的“剩余行动值”条（“⌛轮次 | 行动值”）**永远不算单位**：识别时直接拒绝，不进知识库，不出现在序列里。
- 临时编号（新01…）仍按原规则：用户核对后才升格为正式 U 编号。单位名称是用户的备注，不要自己改名。

## 验收
1. `dotnet test` 全部通过，新增测试覆盖：变体合并（同一单位两种外观 → 同一编号）、“这里”文字格被拒绝、轴底行动值条被拒绝。
2. `axis kb eval`（或现有的知识库评估命令）准确率不低于改动前；改动前后的数字写进 docs/tuning-log.md。
3. 在 3 个样本视频上跑 `pwsh scripts/check-samples.ps1`（主目录运行，需要 expected.txt 已由用户确认）：“主”和“插入”的召回率不低于改动前，且输出里不再出现由文字或行动值条产生的单位。
4. 合并变体前，把要合并的条目列给用户确认；用户没点头不改知识库。

## 样本视频（axis-data/recordings，只在主目录；用环境变量 AXIS_DATA 指过去）
- 20261027-181522_BV1EM8Q69EqZ_0-3min（铁墓）
- 20261027-181522_BV1pCaX6iEsU_0-3min（亚婆离）
- 20261027-181522_BV1vAgy6LE9G_0-3min（绘世）
- **永远不要打开或运行任何 BV18nVd6VEiB 的录像**：它只用于盲测。

## 可以改
- src/AxisRecognizer.Core/Recognizer.cs、AxisDetector.cs、AvatarFeature.cs、CellLocator.cs、NewUnits.cs、KnowledgeBase.cs、KbModels.cs、KbLabeler.cs
- tests/AxisRecognizer.Tests/RecognitionTests.cs、NewUnitTests.cs，以及新的测试文件
- axis-data/kb（只能通过 axis kb 命令改，每次改动写进 kb/changelog.md）

## 不许改
- EventAnalyzer.cs、BattleSequence.cs、ScreenCues*.cs（归任务线 2、3）
- src/AxisTool/Program.cs、src/GameLauncher/**（合并时统一处理；需要新命令就在交付说明里写清楚）
- samples/*/expected.txt、docs/sequence-rules.md 的已确认规则（新规则先问用户，确认后先写进规则再写代码）

## 交付
在自己的分支上分步提交（用户说可以才提交），最后写一段说明：改了什么、测试结果、知识库改动清单、需要合并时补的 Program.cs 改动。
