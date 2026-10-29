using AxisRecognizer;

namespace AxisRecognizer.Tests;

public class SequenceTypingTests
{
    /// <summary>Types each piece at the end of the text, the way keystrokes and IME commits arrive.</summary>
    private static string Type(params string[] pieces)
    {
        var text = string.Empty;
        foreach (var piece in pieces)
        {
            text += SequenceTyping.Filter(text, text.Length, 0, piece);
        }

        return text;
    }

    [Fact]
    public void R47_example_index_space_doubled_space_and_unknown_symbol()
    {
        Assert.Equal("3 昔涟 战技", Type("3", "昔", "涟", " ", " ", "|", "战技"));
    }

    [Theory]
    [InlineData("3-1 长夜月 终结技", "3", "-", "1", "长夜月", " ", "终结技")]
    [InlineData("12 风堇", "1", "2", "风堇")]
    [InlineData("２ 昔涟", "２", "昔涟")]
    [InlineData("0-1 U0003", "0", "-", "1", "U0003")]
    public void R47_space_goes_after_the_whole_index(string expected, params string[] pieces)
    {
        Assert.Equal(expected, Type(pieces));
    }

    [Fact]
    public void R47_no_space_inside_an_unfinished_index_or_away_from_line_start()
    {
        Assert.Equal("3-", Type("3", "-"));
        Assert.Equal("2 昔涟 敌2", Type("2", "昔涟", " ", "敌", "2"));
        Assert.Equal("== 换波 ==", Type("=", "=", " ", "换波", " ", "=", "="));
    }

    [Fact]
    public void R47_index_space_on_every_line()
    {
        Assert.Equal("1 长夜月\r\n2 昔涟", Type("1", "长夜月", "\r\n", "2", "昔涟"));
    }

    [Fact]
    public void R47_symbols_of_the_format_stay_others_are_dropped()
    {
        // R20, R22: condition symbols in both widths.
        Assert.Equal("2 昔涟 敌(-1) @ 轮次=1 行动值=120~160", Type("2", "昔涟", " ", "敌(-1)", " ", "@", " ", "轮次=1", " ", "行动值=120~160"));
        Assert.Equal("2 昔涟 敌（－１） ＠ 行动值＝１２０～１６０", Type("2", "昔涟", " ", "敌（－１）", " ", "＠", " ", "行动值＝１２０～１６０"));
        // R15 notes.
        Assert.Equal("5 阿哈时刻（含阿哈时刻附加行动：爻光、真珠）", Type("5", "阿哈时刻（含阿哈时刻附加行动：爻光、真珠）"));
        // R17: no arrows; other stray symbols are dropped too.
        Assert.Equal("2 昔涟 敌2", Type("2", "昔涟", " ", "→", "敌2", ";", "|"));
        Assert.Equal("1 丹恒·饮月", Type("1", "丹恒·饮月"));
    }

    [Fact]
    public void R47_comments_are_not_filtered()
    {
        Assert.Equal("1 长夜月 # 看不清  →|", Type("1", "长夜月", " ", "#", " ", "看不清", " ", " ", "→|"));
    }

    [Fact]
    public void R47_space_next_to_an_existing_space_is_not_added()
    {
        // Caret right before a space.
        Assert.Equal(string.Empty, SequenceTyping.Filter("3 昔涟", 1, 0, " "));
        Assert.Equal(string.Empty, SequenceTyping.Filter("3 昔涟", 2, 0, "　"));
        // Replacing a selection: the neighbours of the selection count.
        Assert.Equal(" ", SequenceTyping.Filter("3昔涟战技", 3, 0, " "));
        Assert.Equal(string.Empty, SequenceTyping.Filter("3 昔涟 战技", 3, 1, " "));
    }
}
