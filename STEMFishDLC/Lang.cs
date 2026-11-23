using System;
using System.IO;
using BepInEx;

namespace STEMFishDLC;

// ============================================================================
//  Lang - 语言选择 / 文本文件路径
//  ---------------------------------------------------------------------------
//  cfg [General] Language = auto / zh-CN / en-US
//    auto  → 读游戏自己的 Locale.currentLangName
//            （zh / Chinese / 中文 → zh-CN，其余 → en-US）
//
//  所有外置文本都走同一套命名：
//    items.zh-CN.json / items.en-US.json
//    voice.zh-CN.json / voice.en-US.json
//    lore.zh-CN.json  / lore.en-US.json
//    ui.zh-CN.json    / ui.en-US.json
//
//  英文文件缺失时回退中文，保证不会因为少一个 json 就白屏。
// ============================================================================
public static class Lang
{
    public static string Code = "zh-CN";
    public static bool IsEnglish => Code != null && Code.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static void Init(Plugin plugin)
    {
        try
        {
            string mode = plugin.Config.Bind("General", "Language", "auto",
                "文本语言 / Text language：auto（跟随游戏 / follow the game）/ zh-CN / en-US").Value;

            if (string.IsNullOrEmpty(mode) || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                Code = Detect();
            else if (mode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                Code = "en-US";
            else
                Code = "zh-CN";
        }
        catch
        {
            Code = "zh-CN";
        }

        try
        {
            Plugin.Log.LogInfo($"[STEMFishDLC] 文本语言: {Code}（游戏语言 {SafeLangName()}）");
        }
        catch { }
    }

    private static string SafeLangName()
    {
        try
        {
            if (Locale.currentLang == null) Locale.LoadLanguage();
            return Locale.currentLangName ?? "?";
        }
        catch { return "?"; }
    }

    private static string Detect()
    {
        try
        {
            string n = SafeLangName().ToLowerInvariant();
            if (string.IsNullOrEmpty(n) || n == "?") return "zh-CN";   // 读不到就保持中文默认
            if (n.Contains("zh") || n.Contains("chinese") || n.Contains("中文") ||
                n.Contains("简体") || n.Contains("繁體") || n.Contains("繁体"))
                return "zh-CN";
            return "en-US";
        }
        catch { return "zh-CN"; }
    }

    // 优先当前语言；文件不存在就回退中文
    public static string File(string baseName)
    {
        try
        {
            string dir = Path.Combine(Paths.PluginPath, "STEMFishDLC");
            string cur = Path.Combine(dir, baseName + "." + Code + ".json");
            if (System.IO.File.Exists(cur)) return cur;
            string zh = Path.Combine(dir, baseName + ".zh-CN.json");
            if (System.IO.File.Exists(zh)) return zh;
            return cur;
        }
        catch { return baseName + "." + Code + ".json"; }
    }

    // 短小的代码内回退（没有 json 时用）
    public static string T(string zh, string en) => IsEnglish ? en : zh;
}
