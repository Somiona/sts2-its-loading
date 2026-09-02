using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace ItsLoading;

/// <summary>
/// theme.json 的 C# 侧加载器(原生冻结呈现面 MacLayerSurface 消费;未来也是
/// 外部主题包的运行时校验入口)。与 gd 侧 render/interpreter.gd、构建门禁
/// tools/check_themes.py 共享同一词汇表 v1 —— 三方向闭环:
/// gd 渲染 / CALayer 渲染 / 构建期 lint 各自独立校验同一份声明。
///
/// 失败策略与 gd 侧对齐:逐元素(未知类型/坏值/引用缺失 → 跳过该元素,经
/// warn 回调上报);整体(format ≠ 1 / 无元素存活 / theme.json 不可读)→
/// Load 返回 null,调用方走回退链。纯 BCL:不触 Godot 类型,离线可单测。
/// </summary>
internal sealed record ThemeDef
{
    public const int CurrentFormat = 1;

    public ThemeSpaceDef Space { get; init; } = new();
    public IReadOnlyList<ThemeElementDef> Elements { get; init; } = Array.Empty<ThemeElementDef>();

    /// <summary>统一反序列化选项:词汇表的 snake_case 键 ↔ PascalCase 属性。
    /// (STJ 默认大小写敏感 —— 不加策略,所有属性都静默落默认值。)</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 从主题目录(含 theme.json)装载;themeDir 也用于后续素材解析(调用方持有)。
    /// 任何整体性失败返回 null;元素级问题跳过并经 warn 上报。
    /// </summary>
    public static ThemeDef? Load(string themeDir, Action<string>? warn = null)
    {
        warn ??= _ => { };
        string path = Path.Combine(themeDir, "theme.json");
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            warn($"[theme] {path} 不可读({e.Message})— 主题不可用");
            return null;
        }
        return Parse(json, warn);
    }

    /// <summary>解析 + 校验(与 Load 同语义;独立出来供测试与内联 JSON 用)。</summary>
    public static ThemeDef? Parse(string json, Action<string>? warn = null)
    {
        warn ??= _ => { };
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            warn($"[theme] JSON 解析失败({e.Message})— 主题不可用");
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warn("[theme] 顶层不是 JSON 对象 — 主题不可用");
                return null;
            }
            if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.Number
                || fmt.GetInt32() != CurrentFormat)
            {
                warn($"[theme] format≠{CurrentFormat} — 主题不可用");
                return null;
            }

            var space = ThemeSpaceDef.Screen;
            if (root.TryGetProperty("space", out var sp) && sp.ValueKind == JsonValueKind.Object)
            {
                space = JsonSerializer.Deserialize<ThemeSpaceDef>(sp.GetRawText(), Json)
                    ?? ThemeSpaceDef.Screen;
                if (space.IsDesign && (space.W <= 0 || space.H <= 0))
                {
                    warn("[theme] design 空间的 w/h 必须为正 — 主题不可用");
                    return null;
                }
            }

            var elements = new List<ThemeElementDef>();
            if (!root.TryGetProperty("elements", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                warn("[theme] elements 缺失或非数组 — 主题不可用");
                return null;
            }
            foreach (var el in arr.EnumerateArray())
            {
                // 逐元素反序列化:单元素失败只跳过自己(与 gd 侧一致)
                ThemeElementDef? def = null;
                try
                {
                    def = JsonSerializer.Deserialize<ThemeElementDef>(el.GetRawText(), Json);
                }
                catch (JsonException e)
                {
                    warn($"[theme] 元素解析失败,跳过:{e.Message}");
                }
                if (def != null) elements.Add(def);
            }
            var kept = Validate(elements, warn);
            if (kept.Count == 0)
            {
                warn("[theme] 无元素存活 — 主题不可用");
                return null;
            }
            return new ThemeDef { Space = space, Elements = kept };
        }
    }

    // ---- 结构校验 + 过滤(ids/父引用/成员引用/bind 域;数值类型错已在元素反序列化时跳过)。
    // 引用断裂/bind 非法的元素剔除自己,主题继续 —— 与 gd 侧逐元素失败策略同语义。----

    private static List<ThemeElementDef> Validate(List<ThemeElementDef> elements, Action<string> warn)
    {
        var kept = new List<ThemeElementDef>();
        var seen = new HashSet<string>();
        var strips = new HashSet<string>();
        var rows = new HashSet<string>();
        var dotSets = new HashSet<string>();
        foreach (var e in elements)
        {
            if (string.IsNullOrEmpty(e.Id))
            {
                warn($"[theme] 元素 {e.GetType().Name} 缺 id — 跳过");
                continue;
            }
            if (!seen.Add(e.Id))
            {
                warn($"[theme] 重复 id '{e.Id}' — 跳过后者");
                continue;
            }
            if (e.Parent != null && !strips.Contains(e.Parent))
            {
                warn($"[theme] {e.Id}: parent '{e.Parent}' 未先出现(容器须在子元素之前)— 跳过");
                continue;
            }
            switch (e)
            {
                case StripElement:
                    strips.Add(e.Id);
                    break;
                case LabelElement l when l.Bind is not (ThemeBind.Step or ThemeBind.Detail):
                    warn($"[theme] {e.Id}: label 只能绑 step/detail — 跳过");
                    continue;
                case BarElementDef b when b.Bind is not (ThemeBind.Overall or ThemeBind.Local):
                    warn($"[theme] {e.Id}: bar 只能绑 overall/local — 跳过");
                    continue;
                case BarElementDef b2 when b2.Indeterminate != null && b2.Bind != ThemeBind.Local:
                    warn($"[theme] {e.Id}: indeterminate 只属于 local 条 — 跳过");
                    continue;
                case IconRowElement r when r.Enlarge != null && r.Enlarge.Factor <= 0:
                    warn($"[theme] {e.Id}: enlarge.factor 必须为正 — 跳过");
                    continue;
                case DotsElement d when !rows.Contains(d.Of):
                    warn($"[theme] {e.Id}: dots.of '{d.Of}' 未先出现(须是先前的 icon_row)— 跳过");
                    continue;
                case MaskTrackElement m when !m.Members.Any(x => rows.Contains(x) || dotSets.Contains(x)):
                    warn($"[theme] {e.Id}: 没有先于本元素出现的可用成员 — 跳过");
                    continue;
            }
            switch (e)
            {
                case IconRowElement:
                    rows.Add(e.Id);
                    break;
                case DotsElement:
                    dotSets.Add(e.Id);
                    break;
            }
            kept.Add(e);
        }
        return kept;
    }
}

// ---------------------------------------------------------------- 值类型

/// <summary>坐标空间:screen(视口像素)或 design(等比缩放居中的设计矩形)。</summary>
internal sealed record ThemeSpaceDef
{
    public static readonly ThemeSpaceDef Screen = new() { Kind = "screen" };

    public string Kind { get; init; } = "screen";
    public double W { get; init; }
    public double H { get; init; }

    public bool IsDesign => Kind == "design";
}

/// <summary>枚举字符串读取(词汇表键全小写;STJ 内建字符串枚举大小写敏感)。</summary>
internal sealed class LowercaseEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type objectType, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse(reader.GetString(), ignoreCase: true, out T value))
            return value;
        throw new JsonException($"非法 {typeof(T).Name} 值");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}

[JsonConverter(typeof(LowercaseEnumConverter<ThemeBind>))]
internal enum ThemeBind { Overall, Local, Step, Detail, Log, Stage }

[JsonConverter(typeof(LowercaseEnumConverter<IndeterminateMode>))]
internal enum IndeterminateMode { Pulse, Slide }

/// <summary>长度:数值像素,或 "fill" = 所在空间宽度 − 2x。</summary>
[JsonConverter(typeof(ThemeLengthConverter))]
internal readonly record struct ThemeLength(bool IsFill, double Value)
{
    public static readonly ThemeLength Fill = new(true, 0);
}

internal class ThemeLengthConverter : JsonConverter<ThemeLength>
{
    public override ThemeLength Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => new ThemeLength(false, reader.GetDouble()),
            JsonTokenType.String when reader.GetString() == "fill" => ThemeLength.Fill,
            _ => throw new JsonException("长度须为数字或 \"fill\""),
        };

    public override void Write(Utf8JsonWriter writer, ThemeLength v, JsonSerializerOptions o)
    {
        if (v.IsFill) writer.WriteStringValue("fill");
        else writer.WriteNumberValue(v.Value);
    }
}

/// <summary>#RRGGBB / #RRGGBBAA → 0..1 分量。</summary>
[JsonConverter(typeof(ThemeColorConverter))]
internal readonly record struct ThemeColor(double R, double G, double B, double A)
{
    public static ThemeColor Parse(string? s)
    {
        if (s == null || s.Length is not (7 or 9) || s[0] != '#')
            throw new FormatException($"颜色须为 #RRGGBB(AA):{s}");
        bool hasAlpha = s.Length == 9;
        return new ThemeColor(
            ParseByte(s, 1) / 255.0, ParseByte(s, 3) / 255.0, ParseByte(s, 5) / 255.0,
            (hasAlpha ? ParseByte(s, 7) : 255) / 255.0);

        static double ParseByte(string s, int at) =>
            byte.Parse(s.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

internal class ThemeColorConverter : JsonConverter<ThemeColor>
{
    public override ThemeColor Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("颜色须为 #RRGGBB(AA) 字符串");
        try
        {
            return ThemeColor.Parse(reader.GetString());
        }
        catch (FormatException e)
        {
            // 包成 JsonException:STJ 不包裹转换器异常,裸 FormatException 会
            // 击穿逐元素的 catch(失败策略要求单元素失败只跳过自己)
            throw new JsonException(e.Message);
        }
    }

    public override void Write(Utf8JsonWriter writer, ThemeColor v, JsonSerializerOptions o)
        => throw new NotSupportedException();
}

/// <summary>文本源:字面量字符串,或 {"loc": 键}(运行时经 I18n 表解析)。</summary>
[JsonConverter(typeof(TextSourceConverter))]
internal readonly record struct TextSource(string Literal, string? Loc)
{
    public string Resolve(Func<string, string> lookup)
        => Loc != null ? lookup(Loc) : Literal;
}

internal class TextSourceConverter : JsonConverter<TextSource>
{
    public override TextSource Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new TextSource(reader.GetString() ?? "", null);
            case JsonTokenType.StartObject:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.TryGetProperty("loc", out var loc)
                    && loc.ValueKind == JsonValueKind.String)
                    return new TextSource("", loc.GetString());
                throw new JsonException("文本对象只支持 {\"loc\": 键}");
            }
            default:
                throw new JsonException("text 须为字符串或 {\"loc\": 键}");
        }
    }

    public override void Write(Utf8JsonWriter writer, TextSource v, JsonSerializerOptions o)
        => throw new NotSupportedException();
}

internal sealed record IndeterminateDef
{
    public IndeterminateMode Mode { get; init; } = IndeterminateMode.Slide;
    public double MinW { get; init; } = 60;
    public double Travel { get; init; } = 160;
    public double CycleS { get; init; } = 3;
}

internal sealed record EnlargeDef
{
    public double Factor { get; init; } = 1.2;
}

// ---------------------------------------------------------------- 元素词汇表 v1
// type 鉴别子自定转换器:属性顺序无关(STJ 内建多态要求 type 是首属性,而
// 主题惯例 id 在前);未知类型即抛(→ 调用方按元素跳过)= 运行时封闭词汇表

[JsonConverter(typeof(ThemeElementConverter))]
internal abstract record ThemeElementDef
{
    public string Id { get; init; } = "";
    /// <summary>父容器(目前仅 strip);null = 挂主题根。</summary>
    public string? Parent { get; init; }
}

internal sealed class ThemeElementConverter : JsonConverter<ThemeElementDef>
{
    private static readonly Dictionary<string, Type> Vocabulary = new()
    {
        ["bg"] = typeof(BgElement),
        ["logo"] = typeof(LogoElement),
        ["strip"] = typeof(StripElement),
        ["label"] = typeof(LabelElement),
        ["version_label"] = typeof(VersionLabelElement),
        ["bar_solid"] = typeof(BarSolidElement),
        ["bar_outline"] = typeof(BarOutlineElement),
        ["icon_row"] = typeof(IconRowElement),
        ["dots"] = typeof(DotsElement),
        ["mask_track"] = typeof(MaskTrackElement),
        ["sprite"] = typeof(SpriteElement),
        ["log_column"] = typeof(LogColumnElement),
        ["log_rows"] = typeof(LogRowsElement),
    };

    public override ThemeElementDef? Read(ref Utf8JsonReader reader, Type objectType,
        JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("type", out var ty)
            || ty.ValueKind != JsonValueKind.String)
            throw new JsonException("元素缺 type");
        string name = ty.GetString()!;
        if (!Vocabulary.TryGetValue(name, out Type? concrete))
            throw new JsonException($"未知元素类型 '{name}'");
        return (ThemeElementDef?)JsonSerializer.Deserialize(doc.RootElement.GetRawText(), concrete, options);
    }

    public override void Write(Utf8JsonWriter writer, ThemeElementDef value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}

internal sealed record BgElement : ThemeElementDef
{
    public ThemeColor Color { get; init; }
}

internal sealed record LogoElement : ThemeElementDef
{
    public string Src { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; } = 100;
    public string FallbackText { get; init; } = "";
    public double FallbackFont { get; init; } = 28;
    public ThemeColor FallbackColor { get; init; }
    public bool Nearest { get; init; } = true;
}

internal sealed record StripElement : ThemeElementDef
{
    public double H { get; init; } = 76;
}

internal sealed record LabelElement : ThemeElementDef
{
    public TextSource Text { get; init; }
    public ThemeBind Bind { get; init; } = ThemeBind.Step;
    public double X { get; init; }
    public double Y { get; init; }
    public double? W { get; init; }
    public double? H { get; init; }
    public double Font { get; init; } = 14;
    public ThemeColor Color { get; init; }
    public int? Align { get; init; }
    public int? Overrun { get; init; }
}

internal sealed record VersionLabelElement : ThemeElementDef
{
    public string Prefix { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double? W { get; init; }
    public double? H { get; init; }
    public double Font { get; init; } = 12;
    public ThemeColor Color { get; init; }
    public int? Align { get; init; }
    public int? Overrun { get; init; }
}

internal abstract record BarElementDef : ThemeElementDef
{
    public double X { get; init; }
    public double Y { get; init; }
    public ThemeLength W { get; init; }
    public double H { get; init; } = 5;
    public ThemeBind Bind { get; init; }
    public IndeterminateDef? Indeterminate { get; init; }
    public ThemeColor Fill { get; init; }
}

internal sealed record BarSolidElement : BarElementDef
{
    public ThemeColor Track { get; init; }
}

internal sealed record BarOutlineElement : BarElementDef
{
    public double BorderW { get; init; } = 2;
    public double Inset { get; init; } = 4;
    public ThemeColor Border { get; init; }
}

internal sealed record IconRowElement : ThemeElementDef
{
    public int Count { get; init; } = 1;
    public double Size { get; init; } = 32;
    public double Gap { get; init; }
    public double Cx { get; init; }
    public double? Cy { get; init; }
    public double? Bottom { get; init; }
    public string? Pivot { get; init; }
    public string? Src { get; init; }
    public string? Pattern { get; init; }
    public int IndexBase { get; init; } = 1;
    public bool Nearest { get; init; } = true;
    public ThemeColor? Placeholder { get; init; }
    public EnlargeDef? Enlarge { get; init; }
}

internal sealed record DotsElement : ThemeElementDef
{
    public string Of { get; init; } = "";
    public double Scale { get; init; } = 0.2;
    public ThemeColor Color { get; init; }
    public double Cy { get; init; }
}

internal sealed record MaskTrackElement : ThemeElementDef
{
    public List<string> Members { get; init; } = new();
    public ThemeColor Tint { get; init; }
    public ThemeBind Bind { get; init; } = ThemeBind.Local;
    public IndeterminateDef Indeterminate { get; init; } = new();
}

internal sealed record SpriteElement : ThemeElementDef
{
    public string Src { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public double FrameW { get; init; }
    public double FrameH { get; init; }
    public int Frames { get; init; } = 1;
    public double Fps { get; init; } = 12;
    public bool Nearest { get; init; } = true;
}

internal abstract record LogElementDef : ThemeElementDef
{
    public double X { get; init; }
    public double Y { get; init; }
    public int Lines { get; init; } = 10;
    public double LineH { get; init; } = 17;
    public double Font { get; init; } = 12;
    public ThemeColor Color { get; init; }
    public ThemeBind Bind { get; init; } = ThemeBind.Log;
    public int? Align { get; init; }
    public int? Overrun { get; init; }
}

internal sealed record LogColumnElement : LogElementDef;

internal sealed record LogRowsElement : LogElementDef
{
    public double W { get; init; }
    public int PerLine { get; init; } = 5;
    public string Sep { get; init; } = " | ";
}
