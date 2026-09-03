using System;
using System.IO;
using System.Linq;
using ItsLoading;
using Xunit;

#nullable enable

// theme.json 的 C# 加载器回归(纯 BCL):
//   真实素材 —— 三个随 mod 发行 的 theme.json 逐个装载,零警告、
//               元素类型/数量与声明一致(与 gd 侧 interpreter、构建门禁
//               check_themes.py 三方向闭环的 C# 侧)
//   语义 —— format 门禁 / 逐元素失败策略(未知类型、坏颜色)/
//           引用断裂剔除(dup id、dots.of、mask 成员、parent)/
//           值解析("fill" 长度、#RRGGBBAA、{"loc"} 文本、bind 枚举)

public sealed class ThemeDefTests
{
    // 测试程序集在 <repo>/tests/.../bin/Release/net10.0 —— 向上走仓库根
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ItsLoading", "themes")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到仓库根(src/ItsLoading/themes)");
        return dir.FullName;
    }

    [Fact]
    public void Shipped_classic_loads_with_expected_shape()
    {
        var warns = new System.Collections.Generic.List<string>();
        var def = ThemeDef.Load(Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "classic"), warns.Add);
        Assert.NotNull(def);
        Assert.Empty(warns);
        Assert.False(def!.Space.IsDesign);
        Assert.Equal(7, def.Elements.Count);
        var strip = Assert.IsType<StripElement>(def.Elements[1]);
        Assert.Equal(76, strip.H);
        var step = def.Elements.OfType<LabelElement>().Single(l => l.Id == "step");
        Assert.Equal(ThemeBind.Step, step.Bind);
        Assert.Equal("bar.starting", step.Text.Loc);
        var local = def.Elements.OfType<BarSolidElement>().Single(b => b.Id == "local");
        Assert.Equal(ThemeBind.Local, local.Bind);
        Assert.True(local.W.IsFill);
        Assert.NotNull(local.Indeterminate);
        Assert.Equal(IndeterminateMode.Pulse, local.Indeterminate!.Mode);
        Assert.Equal(60, local.Indeterminate.MinW);
        // 颜色往返:#ffffff26 → 255/255/255/38;detail 标签 #9ea3b3ff → 158/163/179
        Assert.Equal(255, Math.Round(local.Track.R * 255));
        Assert.Equal(0x26, (int)Math.Round(local.Track.A * 255));
        var detail = def.Elements.OfType<LabelElement>().Single(l => l.Id == "detail");
        Assert.Equal(158, Math.Round(detail.Color.R * 255));
        Assert.Equal(179, Math.Round(detail.Color.B * 255));
    }

    [Fact]
    public void Shipped_minespire_loads_with_expected_shape()
    {
        var warns = new System.Collections.Generic.List<string>();
        var def = ThemeDef.Load(Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "minespire"), warns.Add);
        Assert.NotNull(def);
        Assert.Empty(warns);
        Assert.True(def!.Space.IsDesign);
        Assert.Equal(854, def.Space.W);
        Assert.Equal(9, def.Elements.Count);
        var logo = def.Elements.OfType<LogoElement>().Single();
        Assert.Equal("mc_style_sts2_logo.png", logo.Src);
        Assert.True(logo.Nearest);
        var overall = def.Elements.OfType<BarOutlineElement>().Single(b => b.Id == "overall");
        Assert.Equal(2, overall.BorderW);
        Assert.Equal(4, overall.Inset);
        Assert.Equal(ThemeBind.Overall, overall.Bind);
        var local = def.Elements.OfType<BarOutlineElement>().Single(b => b.Id == "local");
        Assert.Equal(IndeterminateMode.Slide, local.Indeterminate!.Mode);
        var fox = def.Elements.OfType<SpriteElement>().Single();
        Assert.Equal(28, fox.Frames);
        Assert.Equal(151, fox.FrameW);
        Assert.Equal(8, fox.Fps);
        Assert.Equal(1, fox.Activity!.FramesPerUpdate);
        var version = def.Elements.OfType<VersionLabelElement>().Single();
        Assert.Equal("It's Loading v", version.Prefix);
        Assert.Equal(2, version.Align);
    }

    [Fact]
    public void Shipped_gachathespire_loads_with_expected_shape()
    {
        var warns = new System.Collections.Generic.List<string>();
        var def = ThemeDef.Load(Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "gachathespire"), warns.Add);
        Assert.NotNull(def);
        Assert.Empty(warns);
        Assert.Equal(8, def.Elements.Count);
        var row2 = def.Elements.OfType<IconRowElement>().Single(r => r.Id == "row2");
        Assert.Equal(7, row2.Count);
        Assert.Equal(37.4, row2.Size, 3);
        Assert.Equal(8, row2.IndexBase);
        Assert.Equal("gachathespire_%d.png", row2.Pattern);
        var row1 = def.Elements.OfType<IconRowElement>().Single(r => r.Id == "row1");
        Assert.Equal("bottom", row1.Pivot);
        Assert.Equal(330, row1.Bottom);
        Assert.Equal(1.2, row1.Enlarge!.Factor);
        var dots = def.Elements.OfType<DotsElement>().Single();
        Assert.Equal("row2", dots.Of);
        var mask = def.Elements.OfType<MaskTrackElement>().Single();
        Assert.Equal(new[] { "row2", "dots" }, mask.Members);
        Assert.Equal(0.5, mask.Tint.R, 2); // #808080 → 128/255
        var log = def.Elements.OfType<LogRowsElement>().Single();
        Assert.Equal(5, log.PerLine);
        Assert.Equal(" | ", log.Sep);
    }

    [Fact]
    public void Format_gate_rejects_wrong_and_missing_version()
    {
        Assert.Null(ThemeDef.Parse("""{"elements": []}"""));
        Assert.Null(ThemeDef.Parse("""{"format": 2, "elements": []}"""));
        Assert.Null(ThemeDef.Parse("not json"));
        Assert.Null(ThemeDef.Parse("""[1, 2]"""));
        Assert.Null(ThemeDef.Parse("""{"format": 1}""")); // elements 缺失
    }

    [Fact]
    public void Unknown_element_type_is_skipped_not_fatal()
    {
        var warns = new System.Collections.Generic.List<string>();
        var def = ThemeDef.Parse("""
            {"format": 1, "elements": [
              {"id": "bg", "type": "bg", "color": "#ffffff"},
              {"id": "weird", "type": "hologram", "x": 1},
              {"id": "bad", "type": "label", "bind": "step", "text": "x", "x": 0, "y": 0, "font": 12, "color": "red"}
            ]}
            """, warns.Add);
        Assert.NotNull(def);
        Assert.Single(def!.Elements);
        Assert.IsType<BgElement>(def.Elements[0]);
        Assert.Equal(2, warns.Count);
    }

    [Fact]
    public void Broken_references_drop_only_the_offending_element()
    {
        var warns = new System.Collections.Generic.List<string>();
        var def = ThemeDef.Parse("""
            {"format": 1, "elements": [
              {"id": "row", "type": "icon_row", "count": 3, "size": 10, "gap": 2, "cx": 100, "cy": 50, "src": "x.png"},
              {"id": "d1", "type": "dots", "of": "row", "scale": 0.2, "color": "#808080", "cy": 50},
              {"id": "d2", "type": "dots", "of": "ghost", "scale": 0.2, "color": "#808080", "cy": 50},
              {"id": "row", "type": "strip", "h": 10},
              {"id": "m", "type": "mask_track", "members": ["ghost2"], "tint": "#808080", "bind": "local",
               "indeterminate": {"mode": "slide", "cycle_s": 3}}
            ]}
            """, warns.Add);
        Assert.NotNull(def);
        // d2(of 指向不存在)、重复 id 的 strip、m(成员全断裂)被剔除;row/d1 存活
        Assert.Equal(2, def!.Elements.Count);
        Assert.Contains(def.Elements, e => e.Id == "row");
        Assert.Contains(def.Elements, e => e.Id == "d1");
        Assert.Equal(3, warns.Count);
    }

    [Fact]
    public void Value_forms_parse()
    {
        var def = ThemeDef.Parse("""
            {"format": 1, "space": {"kind": "design", "w": 854, "h": 480}, "elements": [
              {"id": "t", "type": "label", "bind": "detail", "text": "engine boot",
               "x": 1.5, "y": -174, "font": 14, "color": "#9ea3b3"},
              {"id": "b", "type": "bar_solid", "bind": "local", "x": 24, "y": 66,
               "w": "fill", "h": 5, "track": "#ffffff26", "fill": "#33d9e6ff",
               "indeterminate": {"mode": "slide", "cycle_s": 3}}
            ]}
            """, _ => { });
        Assert.NotNull(def);
        var t = Assert.IsType<LabelElement>(def!.Elements[0]);
        Assert.Equal("engine boot", t.Text.Literal);
        Assert.Null(t.Text.Loc);
        Assert.Null(t.W);
        Assert.Equal(-174, t.Y);
        var b = Assert.IsType<BarSolidElement>(def.Elements[1]);
        Assert.True(b.W.IsFill);
        Assert.Equal(0x33, (int)Math.Round(b.Fill.R * 255));
        Assert.Equal(0xd9, (int)Math.Round(b.Fill.G * 255));
        Assert.Equal(0xe6, (int)Math.Round(b.Fill.B * 255));
        Assert.Equal(1.0, b.Fill.A, 3);
        // {"loc"} 文本解析
        var def2 = ThemeDef.Parse("""
            {"format": 1, "elements": [
              {"id": "s", "type": "label", "bind": "step", "text": {"loc": "bar.starting"},
               "x": 0, "y": 0, "font": 20, "color": "#ffffffff"}
            ]}
            """, _ => { });
        Assert.Equal("bar.starting", Assert.IsType<LabelElement>(def2!.Elements[0]).Text.Loc);
        Assert.Equal("LOADING", Assert.IsType<LabelElement>(def2.Elements[0]).Text.Resolve(_ => "LOADING"));
    }

    [Fact]
    public void Load_returns_null_for_missing_directory()
    {
        var warns = new System.Collections.Generic.List<string>();
        Assert.Null(ThemeDef.Load(Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "no-such-theme"), warns.Add));
        Assert.Single(warns);
    }

    [Fact]
    public void Invalid_component_invariants_are_rejected_before_rendering()
    {
        var warnings = new System.Collections.Generic.List<string>();
        var def = ThemeDef.Parse("""
            {"format": 1, "elements": [
              {"id":"bg", "type":"bg", "color":"#000000ff"},
              {"id":"row", "type":"icon_row", "count":0, "size":32, "gap":0,
               "cx":100, "cy":100, "pattern":"icon_%d.png", "index_base":1},
              {"id":"log", "type":"log_rows", "bind":"log", "x":0, "y":0,
               "w":100, "lines":3, "per_line":0, "line_h":12, "font":10,
               "color":"#ffffffff", "align":9},
              {"id":"sprite", "type":"sprite", "src":"x.png", "x":0, "y":0,
               "w":10, "h":10, "frame_w":10, "frame_h":10, "frames":2, "fps":1,
               "activity":{"frames_per_update":0}}
            ]}
            """, warnings.Add);

        Assert.NotNull(def);
        Assert.Single(def!.Elements);
        Assert.IsType<BgElement>(def.Elements[0]);
        Assert.Equal(3, warnings.Count);
    }

    // ---- meta 自述块(画廊卡片;与元素校验解耦,任何损坏 → null 回退 id)----

    [Fact]
    public void ReadMeta_reads_shipped_meta()
    {
        var meta = ThemeDef.ReadMeta(Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "classic"));
        Assert.NotNull(meta);
        Assert.Equal("Classic", meta!.Name);
        Assert.Equal("Somiona", meta.Author);
    }

    [Fact]
    public void ReadMeta_falls_back_gracefully()
    {
        string root = Path.Combine(Path.GetTempPath(), "itsloading-meta-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Theme(string json)
            {
                string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "theme.json"), json);
                return dir;
            }

            // 无 meta / 坏 JSON / meta 非对象 → null
            Assert.Null(ThemeDef.ReadMeta(Theme(
                """{"format":1, "elements":[{"id":"s","type":"label","text":"x","x":0,"y":0,"font":9,"color":"#ffffffff"}]}""")));
            Assert.Null(ThemeDef.ReadMeta(Theme("not json")));
            Assert.Null(ThemeDef.ReadMeta(Theme("""{"format":1, "meta": "oops", "elements":[]}""")));
            // 部分 meta:缺 name 只留 author;name 空白 = 无有效名
            var part = ThemeDef.ReadMeta(Theme(
                """{"format":1, "meta": {"author": " Someone "}, "elements":[]}"""));
            Assert.NotNull(part);
            Assert.Null(part!.Name);
            Assert.Equal("Someone", part.Author);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
