using System.Collections.Generic;
using System.IO;
using System.Text.Json;
#nullable enable

namespace ItsLoading;

/// <summary>
/// 自有 UI 文案本地化。进度条阶段游戏的 LocManager 尚未初始化,
/// 因此直接读 mod 目录 localization/<语言>/strings.json(随 mod 分发的 loose 文件;
/// gd splash 读同一张表,见 BootSplash.cs)。
/// 逐键回退链:目标语言 → eng → 键本身(T 内,保证永不空白)——部分翻译也能用。
/// </summary>
internal static class I18n
{
    private static Dictionary<string, string> _table = new();

    /// <summary>在 mod 初始化最前调用(此时 SettingsSave 已加载)。幂等,可重复调用。</summary>
    public static void Init()
    {
        string lang = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.SettingsSave?.Language ?? "eng";
        string modDir = Path.GetDirectoryName(typeof(I18n).Assembly.Location) ?? ".";
        // eng 表打底、目标语言覆盖:缺键逐键落到 eng,而不是整文件回退
        var table = ReadTable(Path.Combine(modDir, "localization", "eng", "strings.json"));
        if (lang != "eng")
        {
            foreach (var (k, v) in ReadTable(Path.Combine(modDir, "localization", lang, "strings.json")))
                table[k] = v;
        }
        _table = table;
    }

    private static Dictionary<string, string> ReadTable(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path)) ?? new();
            }
        }
        catch
        {
            // 解析失败按缺文件处理:该层为空,回退链继续
        }
        return new();
    }

    /// <summary>取文案;支持 {token} 占位替换(args 为 token→值)。</summary>
    public static string T(string key, Dictionary<string, string>? args = null)
    {
        string s = _table.TryGetValue(key, out var v) ? v : key;
        if (args != null)
        {
            foreach (var (k, val) in args) s = s.Replace("{" + k + "}", val);
        }
        return s;
    }
}
