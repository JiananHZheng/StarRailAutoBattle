# 任务线 2：目标圈识别

先读 CLAUDE.md、docs/sequence-rules.md（规则编号 R…）、docs/score-retry-check.md（尤其“画面线索”和目标写法）。用中文和用户交流。

## 背景
选目标时敌人身上有红色圆圈和中心点，主目标的圈更大。之前试过 3 种方法都分不开（CueReader.TargetReticles，ScreenCues.cs 约 693 行起），所以现在的规则是：读不到目标就默认 boss（敌(0)），视频生成的序列不写目标。本任务要把目标读出来，替换这个默认。

## 目标
- 在选目标状态的画面上读出主目标：敌人从左到右编号 敌1、敌2…（换波或死亡后重新编号），同时能换算成 boss 相对写法 敌(0)/敌(-1)/敌(+1)。
- 拿不准时输出“待定”，**不要**静默回退到 boss；只有确实读不到时才保留默认，并在输出里注明。
- 目标不参与通过/失败判定（规则如此），只用于给操作代码填目标。

## 验收
1. 先做标注集：从下面的视频截取选目标状态的帧（每个视频至少 20 帧，覆盖单体、群攻、boss 两侧有小怪的情况），由**用户确认**每帧的正确目标后存为测试数据。没有用户确认的标注不能当验收依据。
2. 在标注集上主目标准确率 ≥ 95%，且错误全部是“待定”而不是错的敌人编号。
3. `dotnet test` 全部通过，新增测试用标注帧验证读数。
4. 每帧判断耗时不超过 30 ms（Release），因为整体决策要在 0.5 秒内。

## 样本视频（axis-data/recordings，只在主目录；用环境变量 AXIS_DATA 指过去）
- 20261027-181522_BV1EM8Q69EqZ_0-3min（铁墓）、20261028-cue_BV1EM8Q69EqZ_0300-0600
- 20261027-181522_BV1pCaX6iEsU_0-3min（亚婆离：小怪多，适合测主目标）
- 20261027-181522_BV1vAgy6LE9G_0-3min（绘世）
- **永远不要打开或运行任何 BV18nVd6VEiB 的录像**：它只用于盲测。

## 可以改
- 新文件 src/AxisRecognizer.Core/ScreenCues.Target.cs（partial class CueReader），把 TargetReticles 相关逻辑搬过来时只删 ScreenCues.cs 里目标圈那一段
- 新测试文件 tests/AxisRecognizer.Tests/TargetCueTests.cs 和标注数据
- docs/sequence-rules.md 的 R18、R19（改前先问用户，确认后先写进规则再写代码）

## 不许改
- EventAnalyzer.cs、BattleSequence.cs（接入序列输出由合并时统一做；在交付说明里写清楚接口）
- Recognizer.cs、AxisDetector.cs、知识库（归任务线 1）；ScreenCues.Team.cs、ScreenCues.Insert.cs（归任务线 3）
- src/AxisTool/Program.cs、src/GameLauncher/**、samples/*/expected.txt

## 交付
一个公开方法（例如 `CueReader.ReadTarget(Mat frame, Rect content) -> TargetReading`），准确率报告写进 docs/tuning-log.md，说明合并时 EventAnalyzer/BattleSequence 需要怎么接。用户说可以才提交。
