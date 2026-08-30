using System.Collections.Generic;
using ItsLoading;
using Xunit;

// Slaytheshin 日志窗口契约(纯函数;gd 模板 _ss_log_render 同款算法):
//   每行 5 条、窗口 3 行、超出整行淘汰(最旧一行消失,其余整体上移,最新永远在底行)

public sealed class SlaytheshinLogTests
{
    private static List<string> Feed(int count)
    {
        var entries = new List<string>();
        for (int i = 1; i <= count; i++) SlaytheshinLog.Append(entries, $"e{i}");
        return entries;
    }

    [Fact]
    public void Fewer_than_a_line_renders_single_partial_line()
    {
        string[] lines = SlaytheshinLog.Render(Feed(3));
        Assert.Equal("e1 | e2 | e3", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("", lines[2]);
    }

    [Fact]
    public void Exactly_five_entries_fill_one_line()
    {
        string[] lines = SlaytheshinLog.Render(Feed(5));
        Assert.Equal("e1 | e2 | e3 | e4 | e5", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("", lines[2]);
    }

    [Fact]
    public void Fifteen_entries_fill_three_lines()
    {
        string[] lines = SlaytheshinLog.Render(Feed(15));
        Assert.Equal("e1 | e2 | e3 | e4 | e5", lines[0]);
        Assert.Equal("e6 | e7 | e8 | e9 | e10", lines[1]);
        Assert.Equal("e11 | e12 | e13 | e14 | e15", lines[2]);
    }

    [Fact]
    public void Sixteenth_entry_evicts_oldest_whole_line()
    {
        List<string> entries = Feed(16);
        Assert.Equal(11, entries.Count);                    // 15 上限 +1 → 整行淘汰 5
        string[] lines = SlaytheshinLog.Render(entries);
        Assert.Equal("e6 | e7 | e8 | e9 | e10", lines[0]);  // 行1 整行消失,行2 上移
        Assert.Equal("e11 | e12 | e13 | e14 | e15", lines[1]);
        Assert.Equal("e16", lines[2]);                      // 新条目从行3 重新打起
    }

    [Fact]
    public void Window_never_exceeds_cap_and_newest_stays_bottom()
    {
        List<string> entries = Feed(50);
        Assert.Equal(SlaytheshinBar.LogLines * SlaytheshinBar.LogPerLine, entries.Count);
        string[] lines = SlaytheshinLog.Render(entries);
        Assert.Equal("e36 | e37 | e38 | e39 | e40", lines[0]);
        Assert.Equal("e41 | e42 | e43 | e44 | e45", lines[1]);
        Assert.Equal("e46 | e47 | e48 | e49 | e50", lines[2]);
    }
}
