using AxisRecognizer;

namespace AxisRecognizer.Tests;

public class ExpectedSequenceTests
{
    private static List<string> Errors(ExpectedSequence sequence) =>
        sequence.Issues.Where(i => i.Level == IssueLevel.Error).Select(i => i.ToString()).ToList();

    [Fact]
    public void Full_example_parses_steps_segments_skills_targets_and_checks()
    {
        var sequence = ExpectedSequence.Parse("""
            # 第1波
            1 长夜月
            2 昔涟 战技 敌2      @ 轮次=1 行动值=120~160
            2-1 长夜月 终结技
            2-2 风堇 终结技 我3
            3 记忆主 普攻 敌(-1)
            == 换波 ==
            # 第2波
            1 昔涟 敌(0)        @ 轮次=2 行动值=200~260
            == 转阶段 ==  @ 行动值=50~100
            1 长夜月 敌(+1)
            """);

        Assert.True(sequence.IsValid, string.Join("\n", sequence.Issues));
        Assert.Empty(sequence.Issues);
        Assert.Equal(["1", "2", "2-1", "2-2", "3", "换波", "1", "转阶段", "1"], sequence.Steps.Select(s => s.Index));
        Assert.Equal([1, 1, 1, 1, 1, 1, 2, 2, 3], sequence.Steps.Select(s => s.Segment));
        Assert.Equal(5, sequence.Actions.Count(s => s.Segment == 1));

        var second = sequence.Steps[1];
        Assert.Equal(StepKind.Main, second.Kind);
        Assert.Equal("昔涟", second.UnitText);
        Assert.Equal(SkillKind.Skill, second.Skill);
        Assert.Equal(new TargetRef(true, false, 2), second.Target);
        Assert.Equal(new StepCheck(new ValueRange(1, 1), new ValueRange(120, 160)), second.Check);
        Assert.Equal(StepKind.Extra, sequence.Steps[2].Kind);
        Assert.Equal(new TargetRef(false, false, 3), sequence.Steps[3].Target);
        Assert.Equal(new TargetRef(true, true, -1), sequence.Steps[4].Target);
        Assert.Equal(new TargetRef(true, true, 0), sequence.Steps[6].Target);
        Assert.Equal(StepKind.PhaseChange, sequence.Steps[7].Kind);
        Assert.Equal(new StepCheck(null, new ValueRange(50, 100)), sequence.Steps[7].Check);
        Assert.Equal(new TargetRef(true, true, 1), sequence.Steps[8].Target);
    }

    [Fact]
    public void Full_width_punctuation_and_inline_comments_are_accepted()
    {
        var sequence = ExpectedSequence.Parse("1 昔涟 敌（－１）　＠ 行动值＝１２０～１６０ # 备注");
        Assert.True(sequence.IsValid, string.Join("\n", sequence.Issues));
        Assert.Equal(new TargetRef(true, true, -1), sequence.Steps[0].Target);
        Assert.Equal(new ValueRange(120, 160), sequence.Steps[0].Check!.ActionValue);
    }

    [Fact]
    public void Older_arrow_targets_still_parse_and_are_written_without_the_arrow()
    {
        var sequence = ExpectedSequence.Parse("1 昔涟 战技 →敌2\n2 长夜月 ->敌(-1)\n3 风堇 →我1");
        Assert.True(sequence.IsValid, string.Join("\n", sequence.Issues));
        Assert.Equal(["1 昔涟 战技 敌2", "2 长夜月 敌(-1)", "3 风堇 我1"], sequence.Steps.Select(s => s.ToText()));
    }

    [Fact]
    public void Skill_and_target_can_be_left_out_for_the_default()
    {
        var sequence = ExpectedSequence.Parse("1 昔涟\n2 长夜月 敌3\n3 风堇 战技");
        Assert.True(sequence.IsValid, string.Join("\n", sequence.Issues));
        Assert.Equal([(null, null), (null, new TargetRef(true, false, 3)), ((SkillKind?)SkillKind.Skill, (TargetRef?)null)],
            sequence.Steps.Select(s => (s.Skill, s.Target)));
    }

    [Fact]
    public void Steps_round_trip_through_text()
    {
        const string text = """
            1 长夜月
            2 昔涟 战技 敌2  @ 轮次=1 行动值=120~160
            2-1 长夜月 终结技
            == 换波 ==
            0-1 风堇 终结技 敌(+1)
            1 昔涟
            == 转阶段 ==  @ 行动值=50~100
            1 记忆主
            """;
        var sequence = ExpectedSequence.Parse(text);
        Assert.True(sequence.IsValid, string.Join("\n", sequence.Issues));
        var again = ExpectedSequence.Parse(ExpectedSequence.Format(sequence.Steps));
        Assert.Equal(sequence.Steps.Select(s => s with { Line = 0 }), again.Steps.Select(s => s with { Line = 0 }));
    }

    [Theory]
    [InlineData("1 A\n3 B", "第 2 行错误：序号应为 2。")]
    [InlineData("1 A\n2 B\n1-1 C", "第 3 行错误：额外行动要挂在本段当前的主轴行动 2 下，应写成 2-1。")]
    [InlineData("1 A\n1-2 B", "第 2 行错误：序号应为 1-1。")]
    [InlineData("1 A\n== 换波 ==\n2 B", "第 3 行错误：换段后序号从 1 重新开始，这里应为 1。")]
    [InlineData("A 1", "第 1 行错误：行首应为序号（如 3 或 3-1）：A")]
    [InlineData("1", "第 1 行错误：序号后面要写单位（名称或 U 编号）。")]
    [InlineData("1 A 大招", "第 1 行错误：看不懂“大招”：单位后面只能写招式（普攻、战技、终结技）和目标（如 敌2、敌(-1)、我1）。")]
    [InlineData("1 A 敌0", "第 1 行错误：从左数的目标从 1 开始：敌0")]
    [InlineData("1 A 我(1)", "第 1 行错误：以 boss 为基准的写法只用于敌方：我(1)")]
    [InlineData("1 A 敌x", "第 1 行错误：目标写法不对：敌x（应为 敌2、敌(0)、敌(-1)、我1）")]
    [InlineData("1 A @ 行动值=160~120", "第 1 行错误：区间下限大于上限：行动值=160~120")]
    [InlineData("1 A @ 速度=100", "第 1 行错误：检定条件写法不对：速度=100（应为 轮次=1 或 行动值=120~160）")]
    [InlineData("1 A @", "第 1 行错误：@ 后面没有写检定条件。")]
    [InlineData("1 A @ 轮次=1 轮次=2", "第 1 行错误：轮次写了两次。")]
    [InlineData("1 A\n== 换关 ==", "第 2 行错误：特殊行动应写成 == 换波 == 或 == 转阶段 ==。")]
    [InlineData("# 只有注释", "第 1 行错误：序列里没有任何行动。")]
    public void Format_errors_name_the_line_and_the_fix(string text, string expected)
    {
        var sequence = ExpectedSequence.Parse(text);
        Assert.False(sequence.IsValid);
        Assert.Contains(expected, Errors(sequence));
    }

    [Theory]
    [InlineData("1 A 终结技", "终结技一般写成额外行动（n-i），这里写成了主轴行动。")]
    [InlineData("1 A\n1-1 B 战技", "额外行动一般是终结技，这里写成了普攻或战技。")]
    [InlineData("== 换波 ==\n1 A", "战斗开始后还没有行动就换段了。")]
    [InlineData("1 A\n== 换波 ==", "最后一段没有任何行动。")]
    public void Unusual_but_allowed_lines_are_warnings(string text, string expected)
    {
        var sequence = ExpectedSequence.Parse(text);
        Assert.True(sequence.IsValid, string.Join("\n", sequence.Issues));
        Assert.Contains(sequence.Issues, i => i.Level == IssueLevel.Warning && i.Message == expected);
    }

    [Theory]
    [InlineData("王虫 0T 第一版", null)]
    [InlineData("", "请填写序列名称。")]
    [InlineData(" 前空格", "名称前后不能有空格。")]
    [InlineData("a/b", "名称不能包含 \\ / : * ? \" < > | 等字符。")]
    [InlineData("末尾点.", "名称不能以点开头或结尾。")]
    [InlineData("con", "这个名称是 Windows 的保留名，请换一个。")]
    public void Sequence_names_are_checked_before_saving(string name, string? expected)
    {
        Assert.Equal(expected, SequenceFiles.NameError(name));
    }

    [Fact]
    public void Sequences_are_saved_listed_newest_first_and_read_back()
    {
        using var dir = new TempDir("序列文件");
        Assert.Empty(SequenceFiles.List(dir.Path));

        var first = SequenceFiles.Write(dir.Path, "第一份", "1 昔涟\r\n");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-5));
        SequenceFiles.Write(dir.Path, "第二份", "1 长夜月\r\n");

        Assert.Equal(["第二份", "第一份"], SequenceFiles.List(dir.Path));
        Assert.Equal("1 昔涟\r\n", SequenceFiles.Read(dir.Path, "第一份"));
        Assert.True(SequenceFiles.Exists(dir.Path, "第一份"));
        Assert.False(SequenceFiles.Exists(dir.Path, "a/b"));

        SequenceFiles.Write(dir.Path, "第一份", "1 风堇\r\n");
        Assert.Equal("1 风堇\r\n", SequenceFiles.Read(dir.Path, "第一份"));
        Assert.Equal(2, Directory.GetFiles(SequenceFiles.Folder(dir.Path)).Length);
        Assert.Throws<ArgumentException>(() => SequenceFiles.Write(dir.Path, "a/b", "1 A"));
    }

    [Fact]
    public void Units_are_looked_up_in_the_knowledge_base()
    {
        using var dir = new TempDir("序列");
        var kb = KnowledgeBase.Create(Path.Combine(dir.Path, "kb"));
        var xilian = kb.AddUnit(UnitKind.Character, null, "昔涟", "confirmed", "test", null);
        var old = kb.AddUnit(UnitKind.Character, null, "旧头像", "confirmed", "test", null);
        kb.Merge(old.Id, xilian.Id);
        kb.AddUnit(UnitKind.Character, null, "同名", "confirmed", "test", null);
        kb.AddUnit(UnitKind.CharacterSummon, null, "同名", "confirmed", "test", null);
        var boss = kb.AddUnit(UnitKind.Monster, null, "首领", "confirmed", "test", null);
        var marker = kb.AddUnit(UnitKind.Marker, null, "提醒", "confirmed", "test", null);

        var sequence = ExpectedSequence.Parse($"""
            1 昔涟
            2 {old.Id}
            3 同名
            4 首领
            5 {marker.Id}
            6 不存在
            """, ExpectedSequence.KbLookup(kb));

        Assert.Equal(xilian.Id, sequence.Steps[0].UnitId);
        Assert.Equal(xilian.Id, sequence.Steps[1].UnitId);
        var errors = Errors(sequence);
        Assert.Equal(4, errors.Count);
        Assert.StartsWith("第 3 行错误：“同名”对应多个单位", errors[0]);
        Assert.Equal($"第 4 行错误：{boss.Id}（首领）是敌方单位，敌方行动不写进序列。", errors[1]);
        Assert.Equal($"第 5 行错误：{marker.Id}（提醒）不是行动单位，不写进序列。", errors[2]);
        Assert.Equal("第 6 行错误：知识库里没有叫“不存在”的单位，请写名称或 U 编号。", errors[3]);
    }
}
