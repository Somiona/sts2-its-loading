using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ItsLoading;

/// <summary>
/// 自有 UI 文案本地化。进度条阶段游戏的 LocManager 尚未初始化,
/// 因此直接读 mod 目录 localization/<语言>/strings.json(随 mod 分发的 loose 文件)。
/// 查找链:目标语言 → eng → 内置中文默认(硬编码在 T() 调用处,保证永不空白)。
/// </summary>
internal static class I18n
{
    private static Dictionary<string, string> _table = new();

    /// <summary>在 mod 初始化最前调用(此时 SettingsSave 已加载)。</summary>
    public static void Init()
    {
        string lang = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.SettingsSave?.Language ?? "eng";
        string dir = Path.Combine(
            Path.GetDirectoryName(typeof(I18n).Assembly.Location) ?? ".",
            "localization", lang);
        string path = Path.Combine(dir, "strings.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(
                Path.GetDirectoryName(typeof(I18n).Assembly.Location) ?? ".",
                "localization", "eng", "strings.json");
        }
        try
        {
            if (File.Exists(path))
            {
                _table = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path)) ?? new();
            }
        }
        catch
        {
            _table = new();
        }
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
