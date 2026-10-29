# 任务线 3：终结技 / 额外行动分类

先读 CLAUDE.md、docs/sequence-rules.md（规则编号 R…）、docs/score-retry-check.md。用中文和用户交流。

## 已确认的规则（不要重新假设）
- 终结技的开始 = 队伍栏头像亮起，**不是**插入卡出现。按键 = 释放（队伍栏终结技按钮从亮变灭）。（R29）
- 插入卡不可靠：不能用来判断启动，也不能用来给终结技计数（R45）。现在“每张新插入卡记一个额外行动”的逻辑要去掉。
- 额外行动（追加 / 额外行动）**不全是**终结技：只有队伍栏看到启动/释放的才写“终结技”，只有星标的额外行动不写招式。（R14）
- 昔涟的心形图标也按亮/灭读（R46）；它的样子（Q5a）和 Q6（中途按下、不进当前格的终结技挂在哪）没回答前，不许自己定规则，先问用户。
- 队伍栏位置 → 单位只来自用户给的队伍（--team），不学习、不猜。
- 连续多个终结技挂在同一个被打断的主行动下：n-1、n-2…
- 拿不准写“待定”，不判失败。

## 目标
降低三份样本里插入行动的错误：漏掉的终结技、把非终结技的额外行动写成终结技、因插入卡多记或错记的终结技。

## 验收
1. 3 个样本的 expected.txt 已由用户确认（这是前提，没有就先停下来请用户核对 samples/*/draft.txt）。
2. 在主目录跑 `pwsh scripts/check-samples.ps1`：“插入”的精确率和召回率都 ≥ 90%，“终结技”召回率 ≥ 90%；“主”的召回率不低于改动前。每次改检测器后都重跑，数字写进 docs/tuning-log.md。
3. `dotnet test` 全部通过；EventAnalyzerTests 新增用例覆盖：头像亮起后才按键、星标额外行动不写终结技、两个终结技连按。
4. 不改 expected.txt 去迁就输出。

## 样本视频（axis-data/recordings，只在主目录；用环境变量 AXIS_DATA 指过去）
- samples/ 下的 3 个样本：铁墓、亚婆离、绘世（各 0:00–3:00）
- 补充：20261028-cue_* 各段（3:00–6:00 等），只作为观察素材，不当验收
- **永远不要打开或运行任何 BV18nVd6VEiB 的录像**：它只用于盲测。

## 可以改
- src/AxisRecognizer.Core/EventAnalyzer.cs、BattleSequence.cs、ScreenCues.Team.cs、ScreenCues.Insert.cs
- tests/AxisRecognizer.Tests/EventAnalyzerTests.cs，以及新的测试文件

## 不许改
- Recognizer.cs、AxisDetector.cs、知识库（归任务线 1）
- ScreenCues.cs 里的目标圈部分、ScreenCues.Target.cs（归任务线 2）
- src/AxisTool/Program.cs、src/GameLauncher/**、samples/*/expected.txt

## 交付
分步提交（用户说可以才提交），每轮改动和数字记在 docs/tuning-log.md，最后总结每条规则改动和原因。
