using ItsLoading;
using Xunit;

// Slaytheshin 第二排填充段契约(纯函数;gd 模板 _ss_sync_fill 同款公式):
//   确定进度 = 段 [0, local](右缘即填充前沿,轨分数 0..1);
//   不定进度 = 宽 1/4 的滑段循环扫,头部行程 0 → 0.75(右缘恰好可达轨满宽)。

public sealed class SlaytheshinFillTests
{
    private static FillSegment Seg(float local, bool indeterminate, float t = 0f, float cycle = 3f) =>
        SlaytheshinFill.Segment(local, indeterminate, t, cycle);

    // ---- 确定进度 ----

    [Fact]
    public void Determinate_starts_at_track_left_and_ends_at_local()
    {
        FillSegment s = Seg(0.4f, indeterminate: false);
        Assert.Equal(0f, s.A);
        Assert.Equal(0.4f, s.B);
    }

    [Fact]
    public void Determinate_zero_is_empty_segment()
    {
        FillSegment s = Seg(0f, indeterminate: false);
        Assert.True(s.A >= s.B); // 空段 = 不可见(shader seg_b ≤ seg_a)
    }

    [Theory]
    [InlineData(-0.5f, 0f)]  // 越界负值钳到 0(阶段切换瞬间可能出现)
    [InlineData(1.5f, 1f)]   // 越界正值钳到 1(满轨)
    public void Determinate_clamps_local_into_track(float local, float expected)
    {
        Assert.Equal(expected, Seg(local, indeterminate: false).B);
    }

    // ---- 不定进度 ----

    [Fact]
    public void Indeterminate_is_quarter_width_slider()
    {
        FillSegment s = Seg(0f, indeterminate: true, t: 0.9f);
        Assert.Equal(SlaytheshinFill.SweepWidth, s.B - s.A, 5);
    }

    [Fact]
    public void Indeterminate_head_starts_at_zero_and_travels_three_quarters()
    {
        Assert.Equal(0f, Seg(0f, true, t: 0f).A);
        Assert.Equal(SlaytheshinFill.SweepTravel,
            Seg(0f, true, t: SlaytheshinBar.IndeterminateCycleSeconds - 0.01f).A, 2);
    }

    [Fact]
    public void Indeterminate_right_edge_never_exceeds_track()
    {
        // 头部 0.75 × 1/4 宽 → 右缘峰值恰好 1.0,滑段不出轨
        Assert.Equal(1f, Seg(0f, true, t: SlaytheshinBar.IndeterminateCycleSeconds - 0.01f).B, 2);
    }

    [Fact]
    public void Indeterminate_wraps_around_cycle()
    {
        FillSegment a = Seg(0f, true, t: 0f);
        FillSegment b = Seg(0f, true, t: SlaytheshinBar.IndeterminateCycleSeconds);
        Assert.Equal(a.A, b.A, 5);
        Assert.Equal(a.B, b.B, 5);
    }
}
