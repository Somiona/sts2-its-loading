using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ItsLoading;
using Xunit;

#nullable enable

// 主题包发现层回归(纯逻辑,真实临时目录):
//   扫描 —— themes/<id>/theme.json 目录成条;非法 id / 无 theme.json 跳过
//   合并 —— 内置先注册故 id 冲突时赢;包之间先到先得
//   缓存 —— 序列化/解析往返;解析剔除失效目录与非法条目
public sealed class ThemePacksTests : IDisposable
{
    private readonly string _root;

    public ThemePacksTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "itsloading-pack-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeThemes(string name, params string[] ids)
    {
        string dir = Path.Combine(_root, name, "themes");
        foreach (string id in ids)
        {
            string t = Path.Combine(dir, id);
            Directory.CreateDirectory(t);
            File.WriteAllText(Path.Combine(t, "theme.json"), "{\"format\": 1}");
        }
        return dir;
    }

    [Fact]
    public void Merge_builtin_wins_over_pack_and_packs_first_come()
    {
        string builtin = MakeThemes("builtin", "classic", "shared");
        string packA = MakeThemes("packA", "shared", "neon");
        string packB = MakeThemes("packB", "shared", "zeta");
        var themes = ThemePacks.Merge(new[]
        {
            ("ItsLoading", builtin),
            ("PackA", packA),
            ("PackB", packB),
        });
        Assert.Equal(new[] { "classic", "neon", "shared", "zeta" },
            themes.Select(t => t.Id).ToArray());
        Assert.Equal("ItsLoading", themes.First(t => t.Id == "shared").ModId); // 内置赢
        Assert.Equal("PackA", themes.First(t => t.Id == "neon").ModId);
    }

    [Fact]
    public void Merge_skips_invalid_ids_and_dirless()
    {
        string dir = Path.Combine(_root, "weird", "themes");
        Directory.CreateDirectory(Path.Combine(dir, "UPPER"));          // 非法:大写
        Directory.CreateDirectory(Path.Combine(dir, "ok"));
        File.WriteAllText(Path.Combine(dir, "ok", "theme.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "nojson"));          // 无 theme.json
        var themes = ThemePacks.Merge(new[] { ("m", dir) });
        Assert.Equal(new[] { "ok" }, themes.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void Cache_roundtrip_and_parse_drops_stale_entries()
    {
        string keep = Path.Combine(_root, "keep");
        string gone = Path.Combine(_root, "gone"); // 不创建 = 已退订
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "theme.json"), "{}");
        string json = ThemePacks.SerializeCache(new List<ThemePacks.ThemeEntry>
        {
            new("keep", keep, "pack"),
            new("gone", gone, "pack"),
        });
        var parsed = ThemePacks.ParseCache(json, p => File.Exists(p));
        Assert.Equal(new[] { "keep" }, parsed.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void ParseCache_rejects_relative_and_garbage()
    {
        string json = "{\"a\": \"relative/path\", \"b\": \"/abs\", \"bad\": 5}";
        var parsed = ThemePacks.ParseCache(json, _ => true); // b 合法路径形,其余剔
        Assert.Equal(new[] { "b" }, parsed.Select(t => t.Id).ToArray());
        Assert.Empty(ThemePacks.ParseCache("not json", _ => true));
    }
}
