using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxisRecognizer;
using OpenCvSharp;
using VideoRecorder;

namespace AxisTool;

internal static class Program
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "holdout", "no-refine", "sequence", "all", "copy-video", "confirmed",
    };

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv"];

    private static readonly UTF8Encoding Utf8 = new(false);

    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var args = new Args(argv, Flags);
            var dataRoot = Path.GetFullPath(args.Get("data") ?? Environment.GetEnvironmentVariable("AXIS_DATA") ?? DefaultDataRoot());
            var words = args.Positional;
            if (words.Count == 0)
            {
                PrintHelp();
                return 0;
            }

            var command = words.Count >= 2 ? $"{words[0]} {words[1]}" : words[0];
            switch (words[0])
            {
                case "kb" when words.Count >= 3 && words[1] == "unit":
                    return KbUnit(words[2], args, dataRoot);
                case "kb":
                    return Kb(words.ElementAtOrDefault(1) ?? "", args, dataRoot);
                case "layout":
                    return Layout(words.ElementAtOrDefault(1) ?? "", args, dataRoot);
                case "recognize":
                    return Recognize(args, dataRoot);
                case "test":
                    return Test(words.ElementAtOrDefault(1) ?? "", args, dataRoot);
                case "video":
                    return Video(words.ElementAtOrDefault(1) ?? "", args);
                case "record":
                    return Record(args, dataRoot);
                case "help":
                    PrintHelp();
                    return 0;
                default:
                    throw new UsageException($"未知命令：{command}");
            }
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("用法错误：" + ex.Message);
            Console.Error.WriteLine("运行 axis help 查看命令。");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 1;
        }
    }

    private static string DefaultDataRoot() => AxisData.DefaultRoot(Environment.CurrentDirectory);

    private static string KbRoot(string dataRoot) => Path.Combine(dataRoot, "kb");

    private static void PrintHelp()
    {
        Console.WriteLine("""
            axis —— 星铁行动轴头像知识库与识别工具（数据目录默认 <仓库>/axis-data，可用 --data 或环境变量 AXIS_DATA 指定）

            知识库
              axis kb init
              axis kb stats
              axis kb unit add [--kind unclassified|character|character-summon|monster|monster-summon|marker|attachment] [--name 名称]
                               [--name-status confirmed|candidate] [--name-source 来源] [--notes 备注]
                                    （编号自动为下一个 U0001…；类型和名称只是注释）
              axis kb unit list
              axis kb unit rename --id 编号 [--name 名称 --name-status ... --name-source ...] [--kind 新类型] [--notes 备注] [--insert true|false]
                                    （改名或改类型，编号保持不变；旧编号作为别名仍可使用；marker = 非单位提醒条目，识别后不列入结果）
              axis kb settings [--weight-gray x --weight-edge x --weight-hist x --feature-size n --accept x --margin x --candidate x --group-join x] --reason 依据
                                    （不带参数时显示当前识别参数；修改会记入 changelog，知识库版本 +1）
              axis kb relabel --sample A000123 --unit U0002 --reason 依据       把归错单位的样本改归正确单位
              axis kb jpeg-frames                                  把以 PNG 保存的视频帧截图转存为 JPEG（新存入的视频帧本来就保持 JPEG）
              axis kb migrate-ids                                 把旧编号（CH-…、M0001…）改为全局编号 U0001…，旧编号保留为别名
              axis kb promote <测试目录> [--answers <答案.txt>] [--rest provisional]
                                    （把核对过的临时编号（新01…）转为全局编号，示例帧存为样本；--rest provisional 把其余未核对的
                                      临时编号存为“临时”单位，结果里显示“（临时）”，用 rename --name … --name-status confirmed 或
                                      --provisional false 取消临时标记）
                                    （答案每行：新01 = new [名称] / 新01 = U0012 / 新01 = 新02 / 新01 = skip / 新01 = marker [名称]（非单位条目））
              axis kb unit merge --from 编号 --into 编号 [--confirmed]   （仅在用户确认是同一单位后使用；--confirmed 同时把临时单位的样本记为已确认）
              axis kb add-image <图片> --source-type user-screenshot|user-video|local-capture|web --origin 说明
                               [--url 链接] [--license 许可] [--game-version 3.x] [--holdout] [--notes 备注]
              axis kb add-link --url 链接 [--title 标题] [--uploader UP主] [--notice 页面转载声明] [--notes 备注]
                                    （许可无法确认时只记录链接，不保存图像）
              axis kb label <截图编号> --slot current=单位 --slot main2=单位 ... [--rect 格名=x,y,w,h]
                               [--label-source user|claude-assisted] [--status confirmed|unconfirmed]
              axis kb eval [--all]                                  用留出截图（或全部）检验

            布局（相对坐标，按游戏画面高度归一）
              axis layout calibrate --image <图片> --id 布局名 --aspect 1.70-1.80 --current x,y,w,h --main x,y,w,h --search-bottom y [--slot insert1=x,y,w,h ...]
                                    （检测模式：当前格与主轴格按边框自动查找；坐标为该图片的像素，保存时换算为相对坐标）
              axis layout calibrate --image <图片> --id 布局名 --aspect ... --slot current=x,y,w,h --slot main2=... （固定格模式）
              axis layout preview --image <图片> [--out 输出.png]

            识别与测试
              axis recognize <图片>                                 开发用，直接打印结果
              axis test run --name 名称 <图片或视频...> [--sequence] [--every 秒，视频默认 1/15] [--start 秒] [--end 秒]
              axis test correct <测试目录> --answers <答案.txt>
              axis test rerun <测试目录>                            用当前知识库重跑同一段，另存为修正后结果（原始结果不变）

            视频
              axis record <B站链接> [--start 1:14] [--end 2:30] [--fps 15] [--out 目录]
                                    （开发用：在屏幕外的录制窗口里播放并按每秒 15 帧录下这一段，保存为 recording.avi + recording_info.txt；
                                      使用启动器里登录过的录制器浏览器；
                                      默认保存到 <数据目录>/recordings/）
              axis video probe <视频>
              axis video frames <视频> --out <目录> [--every 1.0] [--start 秒] [--end 秒]
            """);
    }

    // ---------------- kb ----------------

    private static int Kb(string sub, Args args, string dataRoot)
    {
        var root = KbRoot(dataRoot);
        switch (sub)
        {
            case "init":
            {
                var kb = KnowledgeBase.Create(root);
                Console.WriteLine($"已创建知识库 v{kb.Version}：{root}");
                return 0;
            }

            case "stats":
            {
                var kb = KnowledgeBase.Load(root);
                Console.WriteLine($"知识库 v{kb.Version}（内容校验 {kb.ContentHash()}）");
                Console.WriteLine($"单位 {kb.Units.Count(u => u.MergedInto is null)} 个，截图 {kb.Screenshots.Count} 张（留出 {kb.Screenshots.Count(s => s.Split == "holdout")}），样本 {kb.Samples.Count} 个，布局 {kb.Layouts.Count} 个");
                foreach (var group in kb.Samples.GroupBy(s => s.Status))
                {
                    Console.WriteLine($"  样本状态 {group.Key}：{group.Count()}");
                }

                Console.WriteLine($"磁盘占用：{kb.DiskBytes() / 1024.0 / 1024.0:0.0} MB（上限 2048 MB）");
                return 0;
            }

            case "add-image":
            {
                var kb = KnowledgeBase.Load(root);
                var file = args.Positional.ElementAtOrDefault(2) ?? throw new UsageException("缺少图片路径。");
                var source = new SourceInfo
                {
                    Type = args.Require("source-type"),
                    Origin = args.Require("origin"),
                    Url = args.Get("url"),
                    License = args.Get("license"),
                };
                if (source.Type == "web" && string.IsNullOrWhiteSpace(source.License))
                {
                    throw new UsageException("网络来源的图片必须写明 --license（已核实允许本地保存的许可）；不能确认时只记录链接，不要保存图片。");
                }

                var shot = kb.AddScreenshot(file, source, args.Get("game-version"), args.Flag("holdout") ? "holdout" : "train", args.Get("notes"), out var present);
                if (present)
                {
                    Console.WriteLine($"该图片已在知识库中：{shot.Id}");
                    return 0;
                }

                kb.Commit($"添加截图 {shot.Id}", [$"{shot.Id}：{Path.GetFileName(file)}，{shot.Width}×{shot.Height}，来源 {source.Type}（{source.Origin}），用途 {shot.Split}"]);
                Console.WriteLine($"已添加 {shot.Id}（{shot.Split}），知识库 v{kb.Version}");
                return 0;
            }

            case "add-link":
            {
                var kb = KnowledgeBase.Load(root);
                var link = new WebLinkRecord
                {
                    Url = args.Require("url"),
                    Title = args.Get("title"),
                    Uploader = args.Get("uploader"),
                    Notice = args.Get("notice"),
                    Notes = args.Get("notes"),
                };
                kb.WebLinks.Add(link);
                kb.Commit("记录网络来源（仅链接）", [$"{link.Title ?? link.Url}：{link.Url}，UP主 {link.Uploader ?? "未注明"}，页面声明“{link.Notice ?? "无"}”。未保存其图像。{link.Notes}"]);
                Console.WriteLine($"已记录链接，知识库 v{kb.Version}");
                return 0;
            }

            case "label":
                return KbLabel(args, root);

            case "eval":
                return KbEval(args, root);

            case "migrate-ids":
            {
                var kb = KnowledgeBase.Load(root);
                var renames = kb.MigrateToGlobalIds();
                if (renames.Count == 0)
                {
                    Console.WriteLine("所有单位已经是全局编号，无需迁移。");
                    return 0;
                }

                kb.Commit("改用全局编号 U0001…", renames.Select(r => $"{r.Old} → {r.New}（旧编号保留为别名，样本文件移到 samples/{r.New}/）"));
                foreach (var (old, @new) in renames)
                {
                    Console.WriteLine($"{old} → {@new}");
                }

                Console.WriteLine($"已迁移 {renames.Count} 个单位，知识库 v{kb.Version}");
                return 0;
            }

            case "promote":
                return KbPromote(args, root);

            case "relabel":
            {
                var kb = KnowledgeBase.Load(root);
                var sampleId = args.Require("sample");
                var old = kb.Samples.FirstOrDefault(s => s.Id.Equals(sampleId, StringComparison.OrdinalIgnoreCase))?.UnitId ?? "?";
                var sample = kb.RelabelSample(sampleId, args.Require("unit"));
                kb.AppendCorrection($"样本 {sample.Id}\t{old} → {sample.UnitId}\t{args.Require("reason")}");
                kb.Commit($"样本改归 {sample.Id}", [$"{sample.Id}：{old} → {sample.UnitId}；依据：{args.Require("reason")}"]);
                Console.WriteLine($"{sample.Id}：{old} → {sample.UnitId}，知识库 v{kb.Version}");
                return 0;
            }

            case "jpeg-frames":
            {
                var kb = KnowledgeBase.Load(root);
                var (files, before, after) = kb.ConvertVideoFramesToJpeg();
                if (files == 0)
                {
                    Console.WriteLine("没有需要转换的视频帧截图。");
                    return 0;
                }

                var summary = $"{files} 张视频帧截图由 PNG 转存为 JPEG（质量 95）：{before / 1048576.0:0.0} MB → {after / 1048576.0:0.0} MB。样本（头像裁剪）不变。";
                kb.Commit("视频帧截图改用 JPEG", [summary, "依据：用户要求节省存储（2026-10-27）；这些帧本身来自有损视频。"]);
                Console.WriteLine(summary);
                Console.WriteLine($"知识库 v{kb.Version}");
                return 0;
            }

            case "settings":
            {
                var kb = KnowledgeBase.Load(root);
                var s = kb.Settings;
                var changes = new List<string>();
                void Set(string option, string label, Func<double> get, Action<double> set)
                {
                    if (args.Get(option) is { } text)
                    {
                        var value = double.Parse(text, CultureInfo.InvariantCulture);
                        changes.Add(FormattableString.Invariant($"{label} {get():0.###} → {value:0.###}"));
                        set(value);
                    }
                }

                Set("weight-gray", "灰度权重", () => s.WeightGray, v => s.WeightGray = v);
                Set("weight-edge", "边缘权重", () => s.WeightEdge, v => s.WeightEdge = v);
                Set("weight-hist", "颜色直方图权重", () => s.WeightHist, v => s.WeightHist = v);
                Set("feature-size", "特征尺寸", () => s.FeatureSize, v => s.FeatureSize = (int)v);
                Set("accept", "确定阈值", () => s.AcceptScore, v => s.AcceptScore = v);
                Set("margin", "领先差", () => s.AcceptMargin, v => s.AcceptMargin = v);
                Set("candidate", "候选下限", () => s.CandidateScore, v => s.CandidateScore = v);
                Set("group-join", "新头像分组阈值", () => s.GroupJoinScore, v => s.GroupJoinScore = v);
                if (changes.Count == 0)
                {
                    Console.WriteLine(JsonSerializer.Serialize(s, KnowledgeBase.JsonOptions));
                    return 0;
                }

                var reason = args.Require("reason");
                kb.Commit("调整识别参数", changes.Append("依据：" + reason));
                Console.WriteLine(string.Join(Environment.NewLine, changes));
                Console.WriteLine($"知识库 v{kb.Version}");
                return 0;
            }

            default:
                throw new UsageException($"未知 kb 子命令：{sub}");
        }
    }

    private static int KbUnit(string sub, Args args, string dataRoot)
    {
        var kb = KnowledgeBase.Load(KbRoot(dataRoot));
        switch (sub)
        {
            case "add":
            {
                var kind = ParseKind(args.Get("kind") ?? "unclassified");
                var unit = kb.AddUnit(kind, args.Get("id"), args.Get("name"), args.Get("name-status") ?? "candidate", args.Get("name-source"), args.Get("notes"));
                kb.Commit($"登记单位 {unit.Id}", [$"{unit.Id}：{KnowledgeBase.KindText(kind)}，名称 {unit.Name ?? "（无）"}（{unit.NameStatus}{(unit.NameSource is null ? "" : "，来源 " + unit.NameSource)}）"]);
                Console.WriteLine($"已登记 {unit.Id}，知识库 v{kb.Version}");
                return 0;
            }

            case "list":
                Console.WriteLine(File.ReadAllText(Path.Combine(kb.Root, "units.txt")));
                return 0;

            case "rename":
            {
                var unit = kb.FindUnit(args.Require("id")) ?? throw new KeyNotFoundException(args.Require("id"));
                var changes = new List<string>();
                if (args.Get("name") is { } name)
                {
                    var old = unit.Name;
                    unit.Name = name;
                    unit.NameStatus = args.Get("name-status") ?? "candidate";
                    unit.NameSource = args.Get("name-source") ?? unit.NameSource;
                    changes.Add($"名称 {old ?? "（无）"} → {unit.Name}（{unit.NameStatus}，来源 {unit.NameSource ?? "未注明"}）");
                }

                if (args.Get("kind") is { } kindText)
                {
                    var oldKind = unit.Kind;
                    unit.Kind = ParseKind(kindText);
                    changes.Add($"类型 {KnowledgeBase.KindText(oldKind)} → {KnowledgeBase.KindText(unit.Kind)}（编号不变）");
                }

                if (args.Get("notes") is { } notes)
                {
                    unit.Notes = notes;
                    changes.Add($"备注 → {notes}");
                }

                if (args.Get("insert") is { } insertText)
                {
                    unit.ActsAsInsert = bool.TryParse(insertText, out var insert) ? insert : throw new UsageException("--insert 只能是 true 或 false。");
                    changes.Add(insert ? "行动视为插入回合（无论在哪一格）" : "行动按所在位置判断是否为插入回合");
                }

                // A confirmed name, or --provisional false, means the user has checked this 临时 unit.
                if (unit.Provisional && (args.Get("provisional") == "false" || (args.Get("name") is not null && unit.NameStatus == "confirmed")))
                {
                    kb.Confirm(unit);
                    changes.Add("用户核对，取消临时标记");
                }

                if (changes.Count == 0)
                {
                    throw new UsageException("至少需要 --name、--kind、--notes、--insert 或 --provisional false 之一。");
                }

                unit.History.AddRange(changes.Select(c => $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} {c}"));
                kb.Commit($"修改单位 {unit.Id}", changes.Select(c => $"{unit.Id}：{c}"));
                Console.WriteLine($"已更新 {unit.Id}，知识库 v{kb.Version}");
                return 0;
            }

            case "merge":
            {
                var from = args.Require("from");
                var into = args.Require("into");
                // --confirmed: the user checked a 临时 unit; its samples count as confirmed from now on.
                if (args.Flag("confirmed") && kb.FindUnit(from) is { Provisional: true } provisional)
                {
                    kb.Confirm(provisional);
                }

                var moved = kb.Merge(from, into);
                kb.Commit($"合并 {from} → {into}", [$"用户确认 {from} 与 {into} 为同一单位；移动样本 {moved} 个；{from} 保留为别名。"]);
                Console.WriteLine($"已合并，移动样本 {moved} 个，知识库 v{kb.Version}");
                return 0;
            }

            default:
                throw new UsageException($"未知 kb unit 子命令：{sub}");
        }
    }

    /// <summary>
    /// Turns a checked run's temporary ids (新01…) into global units: new ones get the next U id (name as a note),
    /// the others extend a known unit. Each temporary id's saved example frames become samples. The raw result and
    /// new_units listing of the run are not changed; the mapping is written next to them.
    /// </summary>
    private static int KbPromote(Args args, string root)
    {
        var runDir = Path.GetFullPath(args.Positional.ElementAtOrDefault(2) ?? throw new UsageException("缺少测试目录。"));
        var newUnitsDir = Path.Combine(runDir, NewUnitFiles.Folder);
        var tablePath = Path.Combine(newUnitsDir, NewUnitFiles.TableFile);
        if (!File.Exists(tablePath))
        {
            throw new InvalidOperationException("这个测试目录没有新单位清单（new_units/new_units.tsv）。");
        }

        var rows = NewUnitFiles.ParseTable(File.ReadAllText(tablePath)).ToDictionary(r => r.TempId);
        var restProvisional = args.Get("rest") switch
        {
            null => false,
            "provisional" => true,
            var other => throw new UsageException($"--rest 只能是 provisional：{other}"),
        };
        var decisions = args.Get("answers") is { } answersPath
            ? NewUnitPromotion.Parse(File.ReadAllText(answersPath))
            : restProvisional ? [] : throw new UsageException("需要 --answers，或用 --rest provisional 把全部临时编号存为临时单位。");
        foreach (var id in decisions.Keys.Where(id => !rows.ContainsKey(id)))
        {
            throw new FormatException($"{id} 不在这次运行的新单位清单里。");
        }

        var plan = NewUnitPromotion.Plan(decisions);
        var kb = KnowledgeBase.Load(root);
        var runName = Path.GetFileName(runDir);
        var details = new List<string>();
        var mapping = new StringBuilder($"# {runName} 临时编号转正（知识库 v{kb.Version + 1}）\n");
        foreach (var target in plan)
        {
            var unit = target.ExistingUnit is { } existing
                ? kb.ResolveUnit(existing)
                : kb.AddUnit(target.Kind, null, target.NewName, "confirmed", $"用户（{runName}）",
                    $"由 {runName} 的 {string.Join("、", target.TempIds)} 转正" + (target.Kind == UnitKind.Marker ? "；非单位条目，识别后不列入结果" : string.Empty));
            foreach (var tempId in target.TempIds)
            {
                foreach (var example in rows[tempId].Examples)
                {
                    var origin = FormattableString.Invariant($"{runName}@{example.Time:0.000}");
                    var shot = kb.AddScreenshot(Path.Combine(newUnitsDir, example.FrameFile), new SourceInfo { Type = "user-video", Origin = origin }, null, "train", $"{runName} 新单位示例帧", out _);
                    using var frame = kb.ReadScreenshot(shot);
                    details.AddRange(KbLabeler.LabelFrame(kb, shot, frame, new Dictionary<string, string> { [example.Slot] = unit.Id },
                        new Dictionary<string, Rect> { [example.Slot] = example.Rect }, "user", "confirmed", $"{runName} {tempId}"));
                }

                mapping.AppendLine($"{tempId} → {ResultText.UnitLabel(kb, unit.Id)}{(target.ExistingUnit is null ? "（新建）" : "")}");
                kb.AppendCorrection($"{runName}\t{tempId} → {unit.Id}\t用户核对");
            }
        }

        if (restProvisional)
        {
            // Temporary ids promoted by an earlier call (recorded in promoted_kbv*.txt) are already in the KB.
            var earlier = Directory.EnumerateFiles(runDir, "promoted_kbv*.txt")
                .SelectMany(File.ReadLines)
                .Select(l => l.Split(" → ", 2)[0].Trim())
                .Where(NewUnitClusterer.IsTempId)
                .ToHashSet();
            foreach (var tempId in rows.Keys.Where(id => !decisions.ContainsKey(id) && !earlier.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList())
            {
                var (line, added) = AddProvisional(kb, runName, newUnitsDir, tempId, rows[tempId]);
                mapping.AppendLine(line);
                details.AddRange(added);
                decisions[tempId] = new NewUnitPromotion.Decision(NewUnitPromotion.Action.New, null, null);
            }
        }

        var skipped = decisions.Where(d => d.Value.Action == NewUnitPromotion.Action.Skip).Select(d => d.Key).ToList();
        var promotedBefore = Directory.EnumerateFiles(runDir, "promoted_kbv*.txt").SelectMany(File.ReadLines)
            .Select(l => l.Split(" → ", 2)[0].Trim()).Where(NewUnitClusterer.IsTempId).ToHashSet();
        var untouched = rows.Keys.Where(id => !decisions.ContainsKey(id) && !promotedBefore.Contains(id)).ToList();
        if (skipped.Count > 0)
        {
            mapping.AppendLine($"不是单位（skip）：{string.Join("、", skipped)}");
        }

        if (untouched.Count > 0)
        {
            mapping.AppendLine($"未处理（答案里没写）：{string.Join("、", untouched)}");
        }

        kb.Commit($"转正新单位（{runName}）", mapping.ToString().Split('\n').Skip(1).Where(l => l.Length > 0).Concat(details));
        var mappingPath = Path.Combine(runDir, $"promoted_kbv{kb.Version}_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(mappingPath, mapping.ToString(), Utf8);
        Console.Write(mapping);
        Console.WriteLine($"知识库 v{kb.Version}；对照表：{mappingPath}");
        return 0;
    }

    /// <summary>
    /// Stores one unchecked temporary id as a 临时 unit so the user can browse it in the KB. Its examples are first
    /// recognized with the current KB: when most of them match an existing 临时 unit, they join it; a match with a
    /// confirmed unit is only noted (疑似), so unchecked samples never go into a confirmed unit.
    /// </summary>
    private static (string Line, List<string> Details) AddProvisional(KnowledgeBase kb, string runName, string newUnitsDir, string tempId, NewUnitFiles.Row row)
    {
        var recognizer = new Recognizer(kb);
        var votes = new List<string>();
        foreach (var example in row.Examples)
        {
            using var frame = ImageIO.Read(Path.Combine(newUnitsDir, example.FrameFile));
            if (recognizer.Recognize(frame).Slots.FirstOrDefault(s => s.Slot == example.Slot)?.UnitId is { } id && id != KnowledgeBase.EmptyUnitId)
            {
                votes.Add(id);
            }
        }

        var top = votes.GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault();
        var matched = top is not null && top.Count() * 2 > row.Examples.Count ? kb.ResolveUnit(top.Key) : null;
        UnitRecord unit;
        string line;
        if (matched is { Provisional: true })
        {
            unit = matched;
            line = $"{tempId} → {ResultText.UnitLabel(kb, unit.Id)}（工具判断与该临时单位相同，已归入）";
        }
        else
        {
            var suspect = matched is null ? string.Empty : $"；疑似 {ResultText.UnitLabel(kb, matched.Id)}（工具判断，未核对）";
            unit = kb.AddUnit(UnitKind.Unclassified, null, null, "none", null, $"临时：来自 {runName} 的 {tempId}，未经用户核对{suspect}");
            unit.Provisional = true;
            line = $"{tempId} → {unit.Id}（临时，新建{suspect}）";
        }

        var details = new List<string>();
        foreach (var example in row.Examples)
        {
            var origin = FormattableString.Invariant($"{runName}@{example.Time:0.000}");
            var shot = kb.AddScreenshot(Path.Combine(newUnitsDir, example.FrameFile), new SourceInfo { Type = "user-video", Origin = origin }, null, "train", $"{runName} 新单位示例帧", out _);
            using var frame = kb.ReadScreenshot(shot);
            details.AddRange(KbLabeler.LabelFrame(kb, shot, frame, new Dictionary<string, string> { [example.Slot] = unit.Id },
                new Dictionary<string, Rect> { [example.Slot] = example.Rect }, "tool", "unconfirmed", $"{runName} {tempId}（临时）"));
        }

        return (line, details);
    }

    private static UnitKind ParseKind(string text) => text.ToLowerInvariant() switch
    {
        "unclassified" => UnitKind.Unclassified,
        "character" => UnitKind.Character,
        "character-summon" => UnitKind.CharacterSummon,
        "monster" => UnitKind.Monster,
        "monster-summon" => UnitKind.MonsterSummon,
        "marker" => UnitKind.Marker,
        "attachment" => UnitKind.Attachment,
        _ => throw new UsageException($"--kind 只能是 unclassified（未分类）、character、character-summon、monster、monster-summon、marker（非单位提醒条目）、attachment（附属格，并入上一条目）：{text}"),
    };

    private static int KbLabel(Args args, string root)
    {
        var kb = KnowledgeBase.Load(root);
        var shot = kb.GetScreenshot(args.Positional.ElementAtOrDefault(2) ?? throw new UsageException("缺少截图编号。"));
        var labels = ParseAssignments(args.All("slot"));
        if (labels.Count == 0)
        {
            throw new UsageException("至少需要一个 --slot 格名=单位编号。");
        }

        var overrides = ParseAssignments(args.All("rect")).ToDictionary(kv => kv.Key, kv => Geometry.Parse(kv.Value), StringComparer.OrdinalIgnoreCase);
        var labelSource = args.Get("label-source") ?? "claude-assisted";
        var status = args.Get("status") ?? (labelSource == "user" ? "confirmed" : "unconfirmed");
        using var frame = kb.ReadScreenshot(shot);
        var details = KbLabeler.LabelFrame(kb, shot, frame, labels, overrides, labelSource, status, null);
        kb.Commit($"标注截图 {shot.Id}", details);
        foreach (var line in details)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"知识库 v{kb.Version}");
        return 0;
    }

    private static int KbEval(Args args, string root)
    {
        var kb = KnowledgeBase.Load(root);
        var shots = kb.Screenshots.Where(s => s.Truth.Count > 0 && (args.Flag("all") || s.Split == "holdout")).ToList();
        if (shots.Count == 0)
        {
            Console.WriteLine("没有带正确答案的留出截图（可用 --all 检验全部已标注截图，但训练截图的成绩会偏高）。");
            return 0;
        }

        var recognizer = new Recognizer(kb);
        int total = 0, correct = 0, unknown = 0, wrong = 0;
        foreach (var shot in shots)
        {
            using var frame = kb.ReadScreenshot(shot);
            var result = recognizer.Recognize(frame);
            foreach (var (slotName, truth) in shot.Truth)
            {
                total++;
                var slot = result.Slots.FirstOrDefault(s => s.Slot.Equals(slotName, StringComparison.OrdinalIgnoreCase));
                var predicted = slot is null ? null : slot.IsEmpty ? KnowledgeBase.EmptyUnitId : slot.UnitId;
                if (predicted == truth)
                {
                    correct++;
                }
                else if (predicted is null)
                {
                    unknown++;
                    Console.WriteLine($"  {shot.Id}/{slotName}：未识别（正确 {truth}）{ResultText.CandidatesText(kb, slot?.Candidates ?? [])}");
                }
                else
                {
                    wrong++;
                    Console.WriteLine($"  {shot.Id}/{slotName}：识别为 {predicted}，正确 {truth}");
                }
            }
        }

        Console.WriteLine($"检验 {shots.Count} 张截图、{total} 格：正确 {correct}，未知 {unknown}，错误 {wrong}（知识库 v{kb.Version}）");
        return 0;
    }

    private static Dictionary<string, string> ParseAssignments(IEnumerable<string> items)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var eq = item.IndexOf('=');
            if (eq <= 0)
            {
                throw new UsageException($"应为 格名=值：{item}");
            }

            var name = item[..eq].Trim();
            SlotNames.Parse(name);
            map[name] = item[(eq + 1)..].Trim();
        }

        return map;
    }

    // ---------------- layout ----------------

    private static int Layout(string sub, Args args, string dataRoot)
    {
        var kb = KnowledgeBase.Load(KbRoot(dataRoot));
        var imagePath = args.Require("image");
        using var frame = ImageIO.Read(imagePath);
        var content = Geometry.DetectContent(frame);
        var aspect = content.Width / (double)content.Height;

        switch (sub)
        {
            case "calibrate":
            {
                var range = args.Require("aspect").Split('-', StringSplitOptions.TrimEntries);
                if (range.Length != 2)
                {
                    throw new UsageException("--aspect 格式为 最小-最大，例如 1.70-1.80。");
                }

                var slots = ParseAssignments(args.All("slot"))
                    .Select(kv =>
                    {
                        var (kind, order) = SlotNames.Parse(kv.Key);
                        return new SlotDef { Name = kv.Key.ToLowerInvariant(), Kind = kind, Order = order, Rect = RelRect.FromPixels(Geometry.Parse(kv.Value), content) };
                    })
                    .OrderBy(s => s.Kind).ThenBy(s => s.Order)
                    .ToList();
                AxisColumn? axisColumn = null;
                if (args.Get("current") is { } currentText)
                {
                    var main = Geometry.Parse(args.Require("main"));
                    axisColumn = new AxisColumn
                    {
                        CurrentCell = RelRect.FromPixels(Geometry.Parse(currentText), content),
                        MainCell = RelRect.FromPixels(main, content),
                        SearchBottom = (double.Parse(args.Require("search-bottom"), CultureInfo.InvariantCulture) - content.Y) / content.Height,
                    };
                    slots.RemoveAll(s => s.Kind != SlotKind.Insert);
                }
                else if (!slots.Any(s => s.Kind == SlotKind.Current))
                {
                    throw new UsageException("布局必须包含 current 格（固定格模式），或用 --current/--main/--search-bottom 定义行动轴列（检测模式）。");
                }

                var layout = new LayoutProfile
                {
                    Id = args.Require("id"),
                    AspectMin = double.Parse(range[0], CultureInfo.InvariantCulture),
                    AspectMax = double.Parse(range[1], CultureInfo.InvariantCulture),
                    Slots = slots,
                    Axis = axisColumn,
                    CalibratedFrom = [$"{Path.GetFileName(imagePath)}（{frame.Width}×{frame.Height}，画面区域 {content.Width}×{content.Height}，宽高比 {aspect:0.000}）"],
                    Notes = args.Get("notes"),
                };
                kb.UpsertLayout(layout);
                kb.Commit($"标定布局 {layout.Id}", [
                    $"{layout.Id}：宽高比 {layout.AspectMin}–{layout.AspectMax}，依据 {layout.CalibratedFrom[0]}",
                    axisColumn is null
                        ? "  固定格模式"
                        : $"  检测模式：当前格 {axisColumn.CurrentCell}，主轴格 {axisColumn.MainCell}，搜索至 {axisColumn.SearchBottom:0.0000}，边框阈值 {axisColumn.MaxFrameScore}",
                    .. slots.Select(s => $"  固定格 {s.Name} = {s.Rect}"),
                ]);
                Console.WriteLine($"已保存布局 {layout.Id}（{(axisColumn is null ? "固定格" : "检测模式")}，固定格 {slots.Count} 个），知识库 v{kb.Version}");
                return 0;
            }

            case "preview":
            {
                var recognizer = new Recognizer(kb);
                var result = recognizer.Recognize(frame);
                if (result.Problem is not null)
                {
                    Console.WriteLine(result.Problem);
                    return 1;
                }

                using var canvas = frame.Clone();
                Cv2.Rectangle(canvas, result.Content, Scalar.Cyan, 1);
                foreach (var slot in result.Slots)
                {
                    var color = slot.IsEmpty ? Scalar.Gray : slot.UnitId is null ? Scalar.Orange : Scalar.LimeGreen;
                    Cv2.Rectangle(canvas, slot.PixelRect, color, 2);
                    var tag = $"{slot.Slot}:{(slot.IsEmpty ? "empty" : slot.UnitId is null ? "?" : AsciiTag(slot.UnitId))} {slot.Score:0.00}";
                    Cv2.PutText(canvas, tag, new Point(slot.PixelRect.X, Math.Max(12, slot.PixelRect.Y - 4)), HersheyFonts.HersheySimplex, 0.45, color, 1);
                }

                var outPath = args.Get("out") ?? Path.Combine(dataRoot, "preview", Path.GetFileNameWithoutExtension(imagePath) + "_preview.png");
                ImageIO.WritePng(outPath, canvas);
                Console.WriteLine(ResultText.Frame(kb, result, Path.GetFileName(imagePath)));
                Console.WriteLine($"预览图：{outPath}");
                return 0;
            }

            default:
                throw new UsageException($"未知 layout 子命令：{sub}");
        }
    }

    private static string AsciiTag(string unitId) =>
        unitId.All(c => c < 128) ? unitId : "U" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unitId)))[..4];

    // ---------------- recognize ----------------

    private static int Recognize(Args args, string dataRoot)
    {
        var kb = KnowledgeBase.Load(KbRoot(dataRoot));
        var file = args.Positional.ElementAtOrDefault(1) ?? throw new UsageException("缺少图片路径。");
        var recognizer = new Recognizer(kb);
        using var frame = ImageIO.Read(file);
        Console.WriteLine(ResultText.Frame(kb, recognizer.Recognize(frame), Path.GetFileName(file)));
        return 0;
    }

    // ---------------- test ----------------

    private static int Test(string sub, Args args, string dataRoot) => sub switch
    {
        "run" => TestRun(args, dataRoot),
        "correct" => TestCorrect(args, dataRoot),
        "rerun" => TestRerun(args, dataRoot),
        _ => throw new UsageException($"未知 test 子命令：{sub}"),
    };

    /// <summary>
    /// Blind test: freezes the KB/tool version, runs the tool once and writes raw_result.txt read-only.
    /// Nothing here involves a model; the output is exactly what the tool produced.
    /// </summary>
    private static int TestRun(Args args, string dataRoot)
    {
        var kb = KnowledgeBase.Load(KbRoot(dataRoot));
        var inputs = args.Positional.Skip(2).Select(Path.GetFullPath).ToList();
        if (inputs.Count == 0)
        {
            throw new UsageException("至少提供一个图片或视频。");
        }

        foreach (var input in inputs.Where(i => !File.Exists(i)))
        {
            throw new FileNotFoundException($"找不到输入文件：{input}");
        }

        var name = SafeRunName(args.Require("name"));
        var runDir = Path.Combine(dataRoot, "runs", $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}");
        Directory.CreateDirectory(Path.Combine(runDir, "input"));
        var every = args.Double("every", VideoRecognition.DefaultEvery);

        var meta = new StringBuilder();
        meta.AppendLine($"测试名称：{name}");
        meta.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        meta.AppendLine($"知识库：v{kb.Version}，内容校验 {kb.ContentHash()}，样本 {kb.Samples.Count}");
        meta.AppendLine($"识别工具：AxisRecognizer {ResultText.ToolVersion}");
        meta.AppendLine($"命令参数：{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        meta.AppendLine("输入：");
        foreach (var input in inputs)
        {
            var isVideo = VideoExtensions.Contains(Path.GetExtension(input).ToLowerInvariant());
            meta.AppendLine($"  {Path.GetFileName(input)}  sha256={Sha256(input)}  {new FileInfo(input).Length} 字节{(isVideo && !args.Flag("copy-video") ? "（视频未复制，路径：" + input + "）" : "")}");
            if (!isVideo || args.Flag("copy-video"))
            {
                File.Copy(input, Path.Combine(runDir, "input", Path.GetFileName(input)), overwrite: false);
            }
        }

        File.WriteAllText(Path.Combine(runDir, "meta.txt"), meta.ToString(), Utf8);

        var (text, detail) = RunRecognition(kb, inputs, args.Flag("sequence"), every, args.Get("start"), args.Get("end"), "行动轴识别结果（原始输出：工具首次生成，不得修改）", runDir);
        var rawPath = Path.Combine(runDir, "raw_result.txt");
        File.WriteAllText(rawPath, text, Utf8);
        File.WriteAllText(Path.Combine(runDir, "raw_detail.tsv"), detail, Utf8);
        File.SetAttributes(rawPath, FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(runDir, "raw_detail.tsv"), FileAttributes.ReadOnly);

        Console.WriteLine(text);
        Console.WriteLine($"原始结果（只读）：{rawPath}");
        return 0;
    }

    /// <param name="runDir">For a first (raw) run of a single video: where new avatars' examples are saved for axis kb promote.</param>
    private static (string Text, string Detail) RunRecognition(
        KnowledgeBase kb, IReadOnlyList<string> inputs, bool sequence, double every, string? start, string? end, string title, string? runDir = null)
    {
        var recognizer = new Recognizer(kb);
        var text = new StringBuilder();
        var detail = new StringBuilder(VideoRecognition.DetailHeader);
        var videos = inputs.Where(i => VideoExtensions.Contains(Path.GetExtension(i).ToLowerInvariant())).ToList();
        var images = inputs.Where(i => ImageExtensions.Contains(Path.GetExtension(i).ToLowerInvariant())).ToList();
        foreach (var other in inputs.Except(videos).Except(images))
        {
            text.AppendLine($"未完成：不支持的输入类型 {Path.GetFileName(other)}");
        }

        var kind = videos.Count > 0 ? "视频" : sequence ? "连续截图" : "孤立截图";
        text.Insert(0, ResultText.Header(kb, title, kind) + "\n");

        void AddDetail(string key, FrameResult r) => detail.Append(ResultText.DetailRows(key, r));

        if (sequence && images.Count > 0)
        {
            var frames = new List<(double, FrameResult)>();
            for (var i = 0; i < images.Count; i++)
            {
                using var mat = ImageIO.Read(images[i]);
                var r = recognizer.Recognize(mat);
                AddDetail(Path.GetFileName(images[i]), r);
                frames.Add((i + 1, r));
            }

            var analyzer = new SequenceAnalyzer(kb, stableFrames: 1) { TimeIsIndex = true };
            text.AppendLine(analyzer.ToText(analyzer.Analyze(frames), $"{images.Count} 张连续截图（按给定顺序）", "逐张识别"));
        }
        else
        {
            foreach (var image in images)
            {
                try
                {
                    using var mat = ImageIO.Read(image);
                    var r = recognizer.Recognize(mat);
                    AddDetail(Path.GetFileName(image), r);
                    text.AppendLine(ResultText.Frame(kb, r, Path.GetFileName(image)));
                }
                catch (InvalidDataException ex)
                {
                    text.AppendLine($"## {Path.GetFileName(image)}\n未完成：{ex.Message}\n");
                }
            }
        }

        foreach (var video in videos)
        {
            var startSec = start is null ? 0 : double.Parse(start, CultureInfo.InvariantCulture);
            double? endSec = end is null ? null : double.Parse(end, CultureInfo.InvariantCulture);
            try
            {
                var result = VideoRecognition.Run(kb, recognizer, video, every, startSec, endSec, Path.GetFileName(video),
                    runDirectory: videos.Count == 1 ? runDir : null, timeOffset: RecordingOffset(video));
                detail.Append(result.Detail);
                text.Append(result.Text);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
            {
                text.AppendLine($"## {Path.GetFileName(video)}\n未完成：视频无法读取（{ex.Message}）。本次测试未完成，请提供其他格式的视频或截图。\n");
            }
        }

        return (text.ToString(), detail.ToString());
    }

    /// <summary>
    /// Applies the user's corrections: adds the original frames, cropped avatars and correct labels to the KB,
    /// logs each misrecognition, then re-runs and saves a separate corrected result. The raw result is untouched.
    /// </summary>
    private static int TestCorrect(Args args, string dataRoot)
    {
        var runDir = Path.GetFullPath(args.Positional.ElementAtOrDefault(2) ?? throw new UsageException("缺少测试目录。"));
        if (!File.Exists(Path.Combine(runDir, "raw_result.txt")))
        {
            throw new InvalidOperationException("这不是测试目录（没有 raw_result.txt）。");
        }

        var answersPath = args.Require("answers");
        var sections = AnswersFile.Parse(File.ReadAllText(answersPath));
        var kb = KnowledgeBase.Load(KbRoot(dataRoot));
        var versionBefore = kb.Version;
        var testedKb = File.ReadLines(Path.Combine(runDir, "meta.txt"))
            .FirstOrDefault(l => l.StartsWith("知识库：", StringComparison.Ordinal))?["知识库：".Length..].Split('，')[0] ?? "未知";
        var rawDetail = ReadDetail(Path.Combine(runDir, "raw_detail.tsv"));
        var inputs = ReadInputs(runDir);
        var log = new List<string> { $"依据：{Path.GetFileName(runDir)} 的用户纠正（{Path.GetFileName(answersPath)}）" };

        foreach (var section in sections)
        {
            var (fileName, seconds) = AnswersFile.SplitFrameKey(section.FrameKey);
            var source = inputs.FirstOrDefault(i => Path.GetFileName(i).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"测试目录里没有输入 {fileName}");
            string imagePath;
            string origin;
            if (seconds is null)
            {
                imagePath = source;
                origin = fileName;
            }
            else
            {
                // Video frames are kept as JPEG (they come from lossy video).
                imagePath = Path.Combine(runDir, "frames", $"{Path.GetFileNameWithoutExtension(fileName)}_{seconds.Value.ToString("0.000", CultureInfo.InvariantCulture)}.jpg");
                var frameAt = VideoFrames.Sample(source, 1000, seconds.Value, seconds.Value + 1).FirstOrDefault();
                if (frameAt.Frame is null)
                {
                    throw new InvalidDataException($"视频 {fileName} 在 {seconds} 秒处取不到帧。");
                }

                using (frameAt.Frame)
                {
                    ImageIO.WriteJpeg(imagePath, frameAt.Frame);
                }

                origin = $"{fileName}@{seconds.Value.ToString("0.000", CultureInfo.InvariantCulture)}";
            }

            var shot = kb.AddScreenshot(imagePath, new SourceInfo { Type = seconds is null ? "user-screenshot" : "user-video", Origin = origin, License = "用户提供，本地知识库使用" }, null, "train", $"来自测试 {Path.GetFileName(runDir)}", out _);
            using var frame = kb.ReadScreenshot(shot);

            // Use the exact crops the tool used in the blind test when available.
            var rects = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
            var frameKey = rawDetail.Keys.FirstOrDefault(k => k.Equals(section.FrameKey, StringComparison.OrdinalIgnoreCase));
            if (frameKey is not null)
            {
                foreach (var row in rawDetail[frameKey])
                {
                    rects[row.Slot] = row.Rect;
                    if (section.Slots.TryGetValue(row.Slot, out var truth))
                    {
                        var predicted = row.Empty ? KnowledgeBase.EmptyUnitId : row.Unit.Length == 0 ? "未知" : row.Unit;
                        if (!predicted.Equals(kb.ResolveUnit(truth).Id, StringComparison.OrdinalIgnoreCase))
                        {
                            kb.AppendCorrection($"{Path.GetFileName(runDir)}\t{section.FrameKey}\t{row.Slot}\t原始={predicted}\t正确={kb.ResolveUnit(truth).Id}\t候选={row.Candidates}\t盲测时知识库={testedKb}\t纠正时知识库=v{versionBefore}");
                            log.Add($"误识别：{section.FrameKey}/{row.Slot} 原始 {predicted} → 正确 {kb.ResolveUnit(truth).Id}");
                        }
                    }
                }
            }

            log.AddRange(KbLabeler.LabelFrame(kb, shot, frame, section.Slots, rects, "user", "confirmed", $"测试 {Path.GetFileName(runDir)} 纠正"));
        }

        kb.Commit($"按测试纠正更新：{Path.GetFileName(runDir)}", log);
        var correctedPath = WriteCorrected(kb, runDir, inputs);
        Console.WriteLine($"知识库 v{versionBefore} → v{kb.Version}；修正后结果：{correctedPath}");
        return 0;
    }

    /// <summary>Re-runs a test with the current KB (e.g. after axis kb promote) and saves it as a separate corrected result.</summary>
    private static int TestRerun(Args args, string dataRoot)
    {
        var runDir = Path.GetFullPath(args.Positional.ElementAtOrDefault(2) ?? throw new UsageException("缺少测试目录。"));
        if (!File.Exists(Path.Combine(runDir, "raw_result.txt")))
        {
            throw new InvalidOperationException("这不是测试目录（没有 raw_result.txt）。");
        }

        var kb = KnowledgeBase.Load(KbRoot(dataRoot));
        var correctedPath = WriteCorrected(kb, runDir, ReadInputs(runDir));
        Console.WriteLine($"知识库 v{kb.Version}；修正后结果：{correctedPath}");
        return 0;
    }

    /// <summary>Runs the test's inputs again over the same stretch and writes corrected_kbv{N}_{time}.txt; the raw result is untouched.</summary>
    private static string WriteCorrected(KnowledgeBase kb, string runDir, IReadOnlyList<string> inputs)
    {
        var meta = File.ReadAllLines(Path.Combine(runDir, "meta.txt"));
        var sequence = meta.Any(l => l.Contains("--sequence", StringComparison.Ordinal));
        var (text, _) = RunRecognition(kb, inputs, sequence, ParseEvery(meta), MetaOption(meta, "start"), MetaOption(meta, "end"),
            $"行动轴识别结果（修正后结果，知识库 v{kb.Version}；原始结果见 raw_result.txt）");
        var correctedPath = Path.Combine(runDir, $"corrected_kbv{kb.Version}_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(correctedPath, text, Utf8);
        Console.WriteLine(text);
        return correctedPath;
    }

    /// <summary>
    /// For a recording made by the recorder: the video time of the file's time 0, read from the recording_info.txt next to
    /// it ("文件时间 0 对应视频 01:09"); 0 for other videos.
    /// </summary>
    private static double RecordingOffset(string video)
    {
        var info = Path.Combine(Path.GetDirectoryName(video)!, BilibiliRecorder.InfoFileName);
        if (!File.Exists(info))
        {
            return 0;
        }

        const string Marker = "文件时间 0 对应视频 ";
        var line = File.ReadLines(info).FirstOrDefault(l => l.Contains(Marker, StringComparison.Ordinal));
        return line is not null && TimeText.TryParse(line[(line.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..].Trim(), out var seconds, out _)
            ? seconds ?? 0
            : 0;
    }

    private static double ParseEvery(IEnumerable<string> metaLines) =>
        MetaOption(metaLines, "every") is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var every)
            ? every
            : VideoRecognition.DefaultEvery;

    /// <summary>A --name value from the command line recorded in meta.txt, or null.</summary>
    private static string? MetaOption(IEnumerable<string> metaLines, string name)
    {
        foreach (var line in metaLines.Where(l => l.StartsWith("命令参数：", StringComparison.Ordinal)))
        {
            var parts = line["命令参数：".Length..].Split(' ');
            var index = Array.IndexOf(parts, "--" + name);
            if (index >= 0 && index + 1 < parts.Length)
            {
                return parts[index + 1];
            }
        }

        return null;
    }

    private sealed record DetailRow(string Slot, Rect Rect, bool Empty, string Unit, string Candidates);

    private static Dictionary<string, List<DetailRow>> ReadDetail(string path)
    {
        var map = new Dictionary<string, List<DetailRow>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 10)
            {
                continue;
            }

            var row = new DetailRow(f[1], new Rect(int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]), int.Parse(f[5])), bool.Parse(f[6]), f[7], f[9]);
            if (!map.TryGetValue(f[0], out var list))
            {
                map[f[0]] = list = [];
            }

            list.Add(row);
        }

        return map;
    }

    /// <summary>Inputs of a run: copied files in input/, plus videos referenced by path in meta.txt.</summary>
    private static List<string> ReadInputs(string runDir)
    {
        var inputs = Directory.GetFiles(Path.Combine(runDir, "input")).OrderBy(f => f, StringComparer.Ordinal).ToList();
        const string marker = "（视频未复制，路径：";
        foreach (var line in File.ReadLines(Path.Combine(runDir, "meta.txt")))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                inputs.Add(line[(at + marker.Length)..].TrimEnd('）'));
            }
        }

        return inputs;
    }

    private static string SafeRunName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ---------------- video ----------------

    private static int Video(string sub, Args args)
    {
        var video = args.Positional.ElementAtOrDefault(2) ?? throw new UsageException("缺少视频路径。");
        switch (sub)
        {
            case "probe":
            {
                var (fps, seconds, w, h) = VideoFrames.Probe(video);
                Console.WriteLine($"{Path.GetFileName(video)}：{w}×{h}，{fps:0.##} fps，约 {seconds:0.0} 秒");
                return 0;
            }

            case "frames":
            {
                var outDir = args.Require("out");
                var count = 0;
                double? end = args.Get("end") is { } e ? double.Parse(e, CultureInfo.InvariantCulture) : null;
                foreach (var (time, frame) in VideoFrames.Sample(video, args.Double("every", 1.0), args.Double("start", 0), end))
                {
                    using (frame)
                    {
                        ImageIO.WritePng(Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(video)}_t{time.ToString("0000.00", CultureInfo.InvariantCulture)}.png"), frame);
                        count++;
                    }
                }

                Console.WriteLine($"已导出 {count} 帧到 {outDir}");
                return 0;
            }

            default:
                throw new UsageException($"未知 video 子命令：{sub}");
        }
    }

    // ---------------- record ----------------

    /// <summary>Developer-side recording of a Bilibili clip (the launcher's video page uses the same recorder).</summary>
    private static int Record(Args args, string dataRoot)
    {
        if (!BilibiliLinks.TryParse(args.Positional.ElementAtOrDefault(1), out var link, out var linkError))
        {
            throw new UsageException(linkError!);
        }

        if (!TimeText.TryParse(args.Get("start"), out var start, out var startError))
        {
            throw new UsageException("--start " + startError);
        }

        if (!TimeText.TryParse(args.Get("end"), out var end, out var endError))
        {
            throw new UsageException("--end " + endError);
        }

        var outDir = Path.GetFullPath(args.Get("out") ?? Path.Combine(dataRoot, "recordings", $"{DateTime.Now:yyyyMMdd-HHmmss}_{SafeRunName(link.VideoId)}"));
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        {
            throw new InvalidOperationException($"输出目录不是空的：{outDir}");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var lastReport = DateTime.MinValue;
        var progress = new Progress<RecordingProgress>(p =>
        {
            if (p.Stage != RecordingStage.Recording || DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
            {
                lastReport = DateTime.UtcNow;
                Console.WriteLine(p.Stage == RecordingStage.Recording
                    ? $"  录制中 {TimeText.Format(p.MediaTime)} / {TimeText.Format(p.End)}"
                    : $"  {p.Stage}");
            }
        });

        var request = new RecordingRequest(link, start ?? 0, end, outDir, RecorderPaths.DefaultBrowserDataFolder)
        {
            OutputFps = args.Double("fps", 15),
        };
        var result = BilibiliRecorder.RecordOnNewThread(request, progress, cts.Token);
        Console.WriteLine(result.Message);
        if (!result.Ok)
        {
            return 1;
        }

        Console.WriteLine($"视频：{result.VideoPath}");
        Console.WriteLine($"说明：{result.InfoPath}");
        return 0;
    }
}
